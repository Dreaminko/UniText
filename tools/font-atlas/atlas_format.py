#!/usr/bin/env python3
"""UniText v2 packed font atlas + auxiliary lookup texture format (docs/PROTOCOL.md section 5).

Shared by generate_atlas.py, generate_lut12.py, verify_atlas.py and tools/test_all.py so the
encoder, verifier and the bit-exact HLSL replicas below never drift apart.

FontAtlas_BMP_Packed.png (1024 x H, RGBA32, linear, point)
    A flat MSB-first bitstream. Bit `b` lives in texel `b >> 5`, byte `(b >> 3) & 3`
    (0=R, 1=G, 2=B, 3=A), bit `7 - (b & 7)`. Texel `t` sits at (t & 1023, t >> 10), PNG row 0 = top.
    Codepoints are grouped into 1,024 blocks of 64. Only blocks that contain at least one real
    glyph are stored, in one of two cell classes:
      * SMALL (class 1): 11 x 11 cell (glyph rows 3..13, cols 0..10) = 121 bits / glyph
      * LARGE (class 2): 12 x 16 cell (glyph rows 0..15, cols 0..11) = 192 bits / glyph
    Blocks without real glyphs (class 0) are drawn procedurally as the hollow tofu box.

UniTextAux_64x64.png (64 x 64, RGBA32, linear, point) - 4,096 texels
    texels    0..1023 : block table, one texel per 64-codepoint block
                        R | G << 8 | (B & 3) << 16 = base texel / 2, (B >> 2) & 3 = class,
                        A = uniform advance (0..12) or 0x80 | mixed-advance index (0..127)
    texels 1024..2047 : mixed-advance table, 8 texels (64 x 4-bit, high nibble first) per block
    texels 2048..4095 : LUT12 codebook, 2 entries per texel (even idx -> R,G; odd idx -> B,A)
"""

from __future__ import annotations

import hashlib
from typing import Dict, List, Sequence, Tuple

import numpy as np

GLYPH_PX = 16
NUM_CODEPOINTS = 65536
BLOCK_SIZE = 64
NUM_BLOCKS = NUM_CODEPOINTS // BLOCK_SIZE
ATLAS_WIDTH = 1024

CLASS_ABSENT = 0
CLASS_SMALL = 1
CLASS_LARGE = 2

SMALL_ROW0 = 3
SMALL_ROWS = 11
SMALL_COLS = 11
SMALL_BITS = SMALL_ROWS * SMALL_COLS  # 121
LARGE_ROWS = 16
LARGE_COLS = 12
LARGE_BITS = LARGE_ROWS * LARGE_COLS  # 192

AUX_SIZE = 64
AUX_BLOCK_BASE = 0
AUX_ADV_BASE = 1024
AUX_LUT_BASE = 2048
MAX_MIXED_ADV_BLOCKS = 128
ADV_MIXED_FLAG = 0x80
MAX_ADVANCE = 12


def make_tofu_glyph() -> np.ndarray:
    """Hollow left-aligned box drawn for codepoints without a real glyph (advance 12)."""
    g = np.zeros((GLYPH_PX, GLYPH_PX), dtype=np.uint8)
    g[3, 1:11] = 1
    g[13, 1:11] = 1
    g[3:14, 1] = 1
    g[3:14, 10] = 1
    return g


TOFU = make_tofu_glyph()
_SMALL_MASK = np.zeros((GLYPH_PX, GLYPH_PX), dtype=bool)
_SMALL_MASK[SMALL_ROW0 : SMALL_ROW0 + SMALL_ROWS, 0:SMALL_COLS] = True


def is_tofu(glyph: np.ndarray) -> bool:
    return bool(np.array_equal(glyph, TOFU))


def _block_class(glyphs: np.ndarray, advances: np.ndarray, block: int) -> int:
    lo = block * BLOCK_SIZE
    blk = glyphs[lo : lo + BLOCK_SIZE]
    adv = advances[lo : lo + BLOCK_SIZE]
    flat = blk.reshape(BLOCK_SIZE, -1)
    is_tofu_cp = (flat == TOFU.reshape(1, -1)).all(axis=1)
    # Absent when every visible codepoint is exactly the procedural tofu box.
    if bool(((adv == 0) | is_tofu_cp).all()):
        return CLASS_ABSENT
    if not bool(blk[:, ~_SMALL_MASK].any()):
        return CLASS_SMALL
    return CLASS_LARGE


def lut12_sha256(codepoints: Sequence[int]) -> str:
    """sha256 over big-endian 16-bit codepoints in index order (unchanged from protocol v1)."""
    return hashlib.sha256(b"".join(int(cp).to_bytes(2, "big") for cp in codepoints)).hexdigest()


def write_lut_region(aux_flat: np.ndarray, lut_codepoints: Sequence[int]) -> None:
    """Write the 4,096-entry LUT12 codebook into texels 2048..4095 of a (4096, 4) aux array."""
    if len(lut_codepoints) != 4096:
        raise ValueError(f"Expected 4096 LUT12 codepoints, got {len(lut_codepoints)}")
    cps = np.asarray(lut_codepoints, dtype=np.uint32)
    hi = ((cps >> 8) & 0xFF).astype(np.uint8)
    lo = (cps & 0xFF).astype(np.uint8)
    region = aux_flat[AUX_LUT_BASE : AUX_LUT_BASE + 2048]
    region[:, 0] = hi[0::2]
    region[:, 1] = lo[0::2]
    region[:, 2] = hi[1::2]
    region[:, 3] = lo[1::2]


def pack_atlas(
    glyphs: np.ndarray,
    advances: np.ndarray,
    lut_codepoints: Sequence[int],
) -> Tuple[np.ndarray, np.ndarray, Dict[str, int]]:
    """Pack (65536, 16, 16) {0,1} glyphs + (65536,) advances into (atlas_rgba, aux_rgba, stats)."""
    if glyphs.shape != (NUM_CODEPOINTS, GLYPH_PX, GLYPH_PX):
        raise ValueError(f"Expected glyph shape (65536, 16, 16), got {glyphs.shape}")
    advances = np.asarray(advances, dtype=np.uint8)
    if advances.shape != (NUM_CODEPOINTS,) or int(advances.max()) > MAX_ADVANCE:
        raise ValueError("advances must be 65,536 values in 0..12")
    glyphs = (glyphs != 0).astype(np.uint8)

    aux = np.zeros((AUX_SIZE * AUX_SIZE, 4), dtype=np.uint8)
    bit_chunks: List[np.ndarray] = []
    base_texel = 0
    mixed_idx = 0
    stats = {"absent": 0, "small": 0, "large": 0, "mixed_advance": 0, "clipped_glyphs": 0}

    for block in range(NUM_BLOCKS):
        lo = block * BLOCK_SIZE
        cls = _block_class(glyphs, advances, block)
        blk = glyphs[lo : lo + BLOCK_SIZE]
        if cls == CLASS_SMALL:
            bits = blk[:, SMALL_ROW0 : SMALL_ROW0 + SMALL_ROWS, 0:SMALL_COLS].reshape(-1)
            stats["small"] += 1
        elif cls == CLASS_LARGE:
            bits = blk[:, :, 0:LARGE_COLS].reshape(-1)
            stats["clipped_glyphs"] += int(blk[:, :, LARGE_COLS:].reshape(BLOCK_SIZE, -1).any(axis=1).sum())
            stats["large"] += 1
        else:
            bits = None
            stats["absent"] += 1

        entry_base = 0
        if bits is not None:
            if bits.size % 64 != 0:
                raise AssertionError("block bit size must be a multiple of 2 texels")
            entry_base = base_texel
            bit_chunks.append(bits)
            base_texel += bits.size // 32
        if entry_base & 1 or (entry_base >> 1) >= (1 << 18):
            raise AssertionError(f"block base texel {entry_base} not encodable")

        blk_adv = advances[lo : lo + BLOCK_SIZE]
        if bool((blk_adv == blk_adv[0]).all()):
            adv_byte = int(blk_adv[0])
        else:
            if mixed_idx >= MAX_MIXED_ADV_BLOCKS:
                raise AssertionError("too many mixed-advance blocks for the aux texture")
            nibbles = blk_adv.astype(np.uint8)
            packed = (nibbles[0::2] << 4) | nibbles[1::2]  # 32 bytes
            t0 = AUX_ADV_BASE + mixed_idx * 8
            aux[t0 : t0 + 8] = packed.reshape(8, 4)
            adv_byte = ADV_MIXED_FLAG | mixed_idx
            mixed_idx += 1
            stats["mixed_advance"] += 1

        half = entry_base >> 1
        aux[AUX_BLOCK_BASE + block] = [half & 0xFF, (half >> 8) & 0xFF, ((half >> 16) & 0x03) | (cls << 2), adv_byte]

    write_lut_region(aux, lut_codepoints)

    all_bits = np.concatenate(bit_chunks) if bit_chunks else np.zeros(0, dtype=np.uint8)
    total_texels = all_bits.size // 32
    height = max(1, -(-total_texels // ATLAS_WIDTH))
    padded = np.zeros(height * ATLAS_WIDTH * 32, dtype=np.uint8)
    padded[: all_bits.size] = all_bits
    atlas = np.packbits(padded, bitorder="big").reshape(height, ATLAS_WIDTH, 4)
    stats["atlas_height"] = height
    stats["atlas_bytes"] = int(atlas.nbytes)
    return atlas, aux.reshape(AUX_SIZE, AUX_SIZE, 4), stats


# ---------------------------------------------------------------------------
# Bit-exact replicas of UniTextCommon.cginc lookups (tex2Dlod UV math included)
# ---------------------------------------------------------------------------

def _aux_fetch(aux: np.ndarray, texel: int) -> np.ndarray:
    tx = texel & 63
    ty = texel >> 6
    u = (tx + 0.5) / 64.0
    v = 1.0 - (ty + 0.5) / 64.0
    return aux[int((1.0 - v) * 64.0), int(u * 64.0)]


def glyph_ref(aux: np.ndarray, cp: int) -> Tuple[int, int]:
    """Return (class, bit_base) exactly like UniTextGetGlyphRef."""
    e = _aux_fetch(aux, (cp & 0xFFFF) >> 6)
    cls = (int(e[2]) >> 2) & 3
    base_texel = (int(e[0]) | (int(e[1]) << 8) | ((int(e[2]) & 3) << 16)) << 1
    local = cp & 63
    bits_per = SMALL_BITS if cls == CLASS_SMALL else LARGE_BITS
    return cls, base_texel * 32 + local * bits_per


def sample_glyph_bit(atlas: np.ndarray, ref: Tuple[int, int], gx: int, gy: int) -> int:
    cls, bit_base = ref
    if gx >= 16 or gy >= 16 or gx < 0 or gy < 0:
        return 0
    if cls == CLASS_ABSENT:
        return int(((gy == 3 or gy == 13) and 1 <= gx <= 10) or ((gx == 1 or gx == 10) and 3 <= gy <= 13))
    if cls == CLASS_SMALL:
        if gx >= SMALL_COLS or gy < SMALL_ROW0 or gy >= SMALL_ROW0 + SMALL_ROWS:
            return 0
        b = bit_base + (gy - SMALL_ROW0) * SMALL_COLS + gx
    elif cls == CLASS_LARGE:
        if gx >= LARGE_COLS:
            return 0
        b = bit_base + gy * LARGE_COLS + gx
    else:
        return 0
    texel = b >> 5
    height = atlas.shape[0]
    tx = texel & 1023
    ty = texel >> 10
    u = (tx + 0.5) / 1024.0
    v = 1.0 - (ty + 0.5) / height
    px = atlas[int((1.0 - v) * height), int(u * 1024.0)]
    byte = int(px[(b >> 3) & 3])
    return (byte >> (7 - (b & 7))) & 1


def decode_glyph(atlas: np.ndarray, aux: np.ndarray, cp: int) -> np.ndarray:
    """Decode a full 16x16 glyph (cp == 0 is always blank, as in Display.shader)."""
    out = np.zeros((GLYPH_PX, GLYPH_PX), dtype=np.uint8)
    if (cp & 0xFFFF) == 0:
        return out
    ref = glyph_ref(aux, cp)
    for gy in range(GLYPH_PX):
        for gx in range(GLYPH_PX):
            out[gy, gx] = sample_glyph_bit(atlas, ref, gx, gy)
    return out


def decode_advance(aux: np.ndarray, cp: int) -> int:
    cp &= 0xFFFF
    if cp == 0:
        return 0
    a = int(_aux_fetch(aux, cp >> 6)[3])
    if (a & ADV_MIXED_FLAG) == 0:
        return min(a, MAX_ADVANCE)
    local = cp & 63
    p = _aux_fetch(aux, AUX_ADV_BASE + (a & 0x7F) * 8 + (local >> 3))
    byte = int(p[(local >> 1) & 3])
    nib = (byte & 0x0F) if (local & 1) else (byte >> 4)
    return min(nib, MAX_ADVANCE)


def decode_lut12(aux: np.ndarray, idx: int) -> int:
    idx &= 0x0FFF
    if idx == 0:
        return 0
    p = _aux_fetch(aux, AUX_LUT_BASE + (idx >> 1))
    if idx & 1:
        return (int(p[2]) << 8) | int(p[3])
    return (int(p[0]) << 8) | int(p[1])


def decode_all_glyphs_fast(atlas: np.ndarray, aux: np.ndarray) -> np.ndarray:
    """Vectorised full decode (65536, 16, 16) used by verifiers; cp 0 forced blank."""
    bits = np.unpackbits(atlas.reshape(-1), bitorder="big")
    flat_aux = aux.reshape(-1, 4)
    out = np.zeros((NUM_CODEPOINTS, GLYPH_PX, GLYPH_PX), dtype=np.uint8)
    for block in range(NUM_BLOCKS):
        e = flat_aux[AUX_BLOCK_BASE + block]
        cls = (int(e[2]) >> 2) & 3
        base_bit = ((int(e[0]) | (int(e[1]) << 8) | ((int(e[2]) & 3) << 16)) << 1) * 32
        lo = block * BLOCK_SIZE
        if cls == CLASS_ABSENT:
            out[lo : lo + BLOCK_SIZE] = TOFU
        elif cls == CLASS_SMALL:
            n = BLOCK_SIZE * SMALL_BITS
            out[lo : lo + BLOCK_SIZE, SMALL_ROW0 : SMALL_ROW0 + SMALL_ROWS, 0:SMALL_COLS] = (
                bits[base_bit : base_bit + n].reshape(BLOCK_SIZE, SMALL_ROWS, SMALL_COLS)
            )
        elif cls == CLASS_LARGE:
            n = BLOCK_SIZE * LARGE_BITS
            out[lo : lo + BLOCK_SIZE, :, 0:LARGE_COLS] = bits[base_bit : base_bit + n].reshape(
                BLOCK_SIZE, LARGE_ROWS, LARGE_COLS
            )
    out[0] = 0
    return out


def decode_all_advances_fast(aux: np.ndarray) -> np.ndarray:
    flat_aux = aux.reshape(-1, 4)
    out = np.zeros(NUM_CODEPOINTS, dtype=np.uint8)
    for block in range(NUM_BLOCKS):
        a = int(flat_aux[AUX_BLOCK_BASE + block][3])
        lo = block * BLOCK_SIZE
        if (a & ADV_MIXED_FLAG) == 0:
            out[lo : lo + BLOCK_SIZE] = min(a, MAX_ADVANCE)
        else:
            t0 = AUX_ADV_BASE + (a & 0x7F) * 8
            packed = flat_aux[t0 : t0 + 8].reshape(-1)
            nib = np.empty(BLOCK_SIZE, dtype=np.uint8)
            nib[0::2] = packed >> 4
            nib[1::2] = packed & 0x0F
            out[lo : lo + BLOCK_SIZE] = np.minimum(nib, MAX_ADVANCE)
    out[0] = 0
    return out


def expected_visible_glyph(glyph: np.ndarray, cls: int) -> np.ndarray:
    """What the packed atlas is expected to reproduce for a source glyph in a block of `cls`."""
    g = (glyph != 0).astype(np.uint8)
    if cls == CLASS_ABSENT:
        return TOFU.copy()
    if cls == CLASS_SMALL:
        return np.where(_SMALL_MASK, g, 0).astype(np.uint8)
    out = g.copy()
    out[:, LARGE_COLS:] = 0
    return out
