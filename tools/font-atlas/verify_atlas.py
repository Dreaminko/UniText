#!/usr/bin/env python3
"""UniText v2 packed font atlas verifier.

Decodes FontAtlas_BMP_Packed.png + UniTextAux_64x64.png using the exact HLSL UV and bit-unpacking
formulas from docs/PROTOCOL.md section 5 (bit-exact replicas live in atlas_format.py), prints
multilingual glyph ASCII art, and verifies bit-order, orientation (no Y-flip or bit-reversal),
blank whitespace cells, the proportional advance table and the LUT12 codebook region.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import List, Optional, Sequence

import numpy as np
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
import atlas_format as af  # noqa: E402

REPO_ROOT = Path(__file__).resolve().parents[2]
TEXTURES_DIR = REPO_ROOT / "unity" / "Packages" / "dev.dreaminko.unitext" / "Textures"
DEFAULT_ATLAS = TEXTURES_DIR / "FontAtlas_BMP_Packed.png"
DEFAULT_AUX = TEXTURES_DIR / "UniTextAux_64x64.png"
DEFAULT_LUT12_JSON = REPO_ROOT / "spec" / "lut12.json"
DEFAULT_ADVANCE_JSON = REPO_ROOT / "spec" / "glyph-advance.json"

# Packed atlas VRAM budget (bytes). The v1 1024x512 layout used 2 MiB.
MAX_ATLAS_BYTES = 768 * 1024

SAMPLE_CHARS = [
    ("EN", "ATLFg0?"),
    ("ZH-Hans", "中文字幕"),
    ("ZH-Hant", "繁體字幕"),
    ("JA", "あカ語。"),
    ("KO", "한글안녕"),
]


def load_rgba(path: Path) -> np.ndarray:
    if not path.is_file():
        raise FileNotFoundError(f"Texture not found: {path}")
    img = Image.open(path)
    if img.mode != "RGBA":
        raise AssertionError(f"Invalid color mode {img.mode} in {path.name}, expected RGBA")
    return np.asarray(img, dtype=np.uint8)


def sample_glyph_hlsl(atlas: np.ndarray, aux: np.ndarray, cp: int) -> np.ndarray:
    """Decode a 16x16 glyph exactly like UniTextGetGlyphRef + UniTextSampleGlyphBit."""
    return af.decode_glyph(atlas, aux, cp)


def sample_advance_hlsl(aux: np.ndarray, cp: int) -> int:
    """Decode proportional glyph advance (0..12 px) exactly like UniTextSampleAdvance."""
    return af.decode_advance(aux, cp)


def is_tofu_box(glyph: np.ndarray) -> bool:
    return af.is_tofu(glyph)


def format_glyph_ascii(glyph: np.ndarray, on: str = "##", off: str = "..") -> str:
    return "\n".join("".join(on if px else off for px in row) for row in glyph)


def verify_atlas(
    atlas_path: Path = DEFAULT_ATLAS,
    aux_path: Path = DEFAULT_AUX,
    print_samples: bool = True,
    preview_png: Optional[Path] = None,
) -> None:
    atlas = load_rgba(atlas_path)
    aux = load_rgba(aux_path)
    if atlas.shape[1] != af.ATLAS_WIDTH:
        raise AssertionError(f"Invalid atlas width {atlas.shape[1]}, expected {af.ATLAS_WIDTH}")
    if atlas.nbytes > MAX_ATLAS_BYTES:
        raise AssertionError(f"Packed atlas uses {atlas.nbytes} bytes, exceeding budget {MAX_ATLAS_BYTES}")
    if aux.shape != (af.AUX_SIZE, af.AUX_SIZE, 4):
        raise AssertionError(f"Invalid aux dimensions {aux.shape}, expected (64, 64, 4)")

    # 0. Block table integrity: every stored block must lie inside the atlas bitstream
    flat_aux = aux.reshape(-1, 4)
    total_bits = atlas.shape[0] * atlas.shape[1] * 32
    mixed_seen = set()
    for block in range(af.NUM_BLOCKS):
        e = flat_aux[af.AUX_BLOCK_BASE + block]
        cls = (int(e[2]) >> 2) & 3
        if cls == 3 or (int(e[2]) >> 4) != 0:
            raise AssertionError(f"Block {block}: invalid class/reserved bits in entry {e.tolist()}")
        base_bit = ((int(e[0]) | (int(e[1]) << 8) | ((int(e[2]) & 3) << 16)) << 1) * 32
        size = {af.CLASS_ABSENT: 0, af.CLASS_SMALL: af.SMALL_BITS, af.CLASS_LARGE: af.LARGE_BITS}[cls] * af.BLOCK_SIZE
        if base_bit + size > total_bits:
            raise AssertionError(f"Block {block} overruns the atlas bitstream")
        a = int(e[3])
        if a & af.ADV_MIXED_FLAG:
            mixed_seen.add(a & 0x7F)
        elif a > af.MAX_ADVANCE:
            raise AssertionError(f"Block {block}: uniform advance {a} > {af.MAX_ADVANCE}")
    if mixed_seen and max(mixed_seen) >= af.MAX_MIXED_ADV_BLOCKS:
        raise AssertionError("Mixed advance index out of range")

    # 1. Verify blank codepoints are zeroed
    for blank_cp in (0x0000, 0x0020, 0x3000):
        g = sample_glyph_hlsl(atlas, aux, blank_cp)
        if g.sum() != 0:
            raise AssertionError(f"Codepoint U+{blank_cp:04X} should be completely blank, got sum={g.sum()}")

    # 2. Verify sample glyphs are present and not tofu
    for group, chars in SAMPLE_CHARS:
        for ch in chars:
            cp = ord(ch)
            g = sample_glyph_hlsl(atlas, aux, cp)
            if g.sum() == 0:
                raise AssertionError(f"[{group}] Glyph '{ch}' (U+{cp:04X}) is unexpectedly empty!")
            if is_tofu_box(g):
                raise AssertionError(f"[{group}] Glyph '{ch}' (U+{cp:04X}) fell back to tofu!")

    # Uncovered codepoints (e.g. surrogates / PUA gaps) render the procedural tofu box
    if not is_tofu_box(sample_glyph_hlsl(atlas, aux, 0xD800)):
        raise AssertionError("Absent block U+D800 should decode to the procedural tofu box")

    # 3. Orientation & bit-order sanity checks (detects Y-flip or LSB/MSB flip!)
    g_T = sample_glyph_hlsl(atlas, aux, ord("T"))
    if g_T[:8, :].sum() <= g_T[8:, :].sum():
        raise AssertionError("Orientation check failed on 'T': top half should have more pixels than bottom half (possible Y-flip).")

    g_L = sample_glyph_hlsl(atlas, aux, ord("L"))
    if g_L[8:, :].sum() <= g_L[:8, :].sum():
        raise AssertionError("Orientation check failed on 'L': bottom half should have more pixels than top half (possible Y-flip).")

    g_F = sample_glyph_hlsl(atlas, aux, ord("F"))
    if g_F[:, :6].sum() <= g_F[:, 6:].sum():
        raise AssertionError("Bit-order check failed on 'F': left half should have more pixels than right half (possible bit-reversal).")

    # 4. Fast full decode must agree with the HLSL slow path on a spread of codepoints
    fast = af.decode_all_glyphs_fast(atlas, aux)
    for cp in list(range(0x20, 0x80)) + [0x00C5, 0x3042, 0x4E2D, 0x9F8D, 0xAC00, 0xD55C, 0xE000, 0xFF01, 0xFFFD]:
        if not np.array_equal(fast[cp], sample_glyph_hlsl(atlas, aux, cp)):
            raise AssertionError(f"Fast/HLSL glyph decode mismatch at U+{cp:04X}")

    if print_samples:
        for group, chars in SAMPLE_CHARS:
            print(f"\n=== {group} ===")
            for ch in chars[:2]:
                g = sample_glyph_hlsl(atlas, aux, ord(ch))
                print(f"'{ch}' (U+{ord(ch):04X}):")
                print(format_glyph_ascii(g))

    if preview_png is not None:
        render_preview_board(atlas, aux, preview_png)


def verify_lut12(atlas: np.ndarray, aux: np.ndarray, lut12_json: Path) -> None:
    """Verify the aux LUT12 region matches spec/lut12.json entry-for-entry via HLSL UV math."""
    if not lut12_json.is_file():
        raise FileNotFoundError(f"LUT12 spec JSON not found: {lut12_json}")

    lut_doc = json.loads(lut12_json.read_text(encoding="utf-8"))
    lut_cps = [int(cp) for cp in lut_doc["codepoints"]]
    if len(lut_cps) != 4096:
        raise AssertionError(f"Expected 4096 LUT12 entries, got {len(lut_cps)}")
    if lut_cps[0] != 0:
        raise AssertionError(f"LUT12 entry 0 must be 0x0000, got U+{lut_cps[0]:04X}")
    if len(set(lut_cps)) != 4096:
        raise AssertionError("LUT12 codepoints must be unique!")
    if af.lut12_sha256(lut_cps) != lut_doc["sha256"]:
        raise AssertionError("spec/lut12.json sha256 does not match its codepoints")

    glyphs = af.decode_all_glyphs_fast(atlas, aux)
    for idx, expected_cp in enumerate(lut_cps):
        decoded_cp = af.decode_lut12(aux, idx)
        if decoded_cp != expected_cp:
            raise AssertionError(f"LUT12 mismatch at idx={idx}: {decoded_cp} != {expected_cp}")
        if expected_cp not in (0, 0x0020):
            glyph = glyphs[expected_cp]
            if glyph.sum() == 0:
                raise AssertionError(f"LUT12 entry {idx} (U+{expected_cp:04X}) is blank in FontAtlas!")
            if is_tofu_box(glyph):
                raise AssertionError(f"LUT12 entry {idx} (U+{expected_cp:04X}) fell back to tofu!")


def load_advance_spec(advance_json: Path) -> List[int]:
    doc = json.loads(advance_json.read_text(encoding="utf-8"))
    if doc.get("schema_version") != "1.2.0":
        raise AssertionError(f"Unexpected glyph-advance schema_version: {doc.get('schema_version')}")
    if doc.get("row_px") != 384 or doc.get("max_cells_per_row") != 64 or doc.get("max_advance") != 12:
        raise AssertionError(f"Unexpected glyph-advance geometry metadata in {advance_json}")

    advances = [-1] * 65536
    for start, end, adv in doc["ranges"]:
        s_cp, e_cp, adv_val = int(start), int(end), int(adv)
        if not (0 <= s_cp <= e_cp <= 65535) or not (0 <= adv_val <= 12):
            raise AssertionError(f"Invalid range entry [{start}, {end}, {adv}] in {advance_json}")
        for cp in range(s_cp, e_cp + 1):
            advances[cp] = adv_val
    if any(a < 0 for a in advances):
        raise AssertionError(f"{advance_json} does not cover all 65,536 BMP codepoints")
    if advances[0] != 0:
        raise AssertionError(f"U+0000 advance must be 0, got {advances[0]}")
    import hashlib

    if hashlib.sha256(bytes(advances)).hexdigest() != doc["sha256"]:
        raise AssertionError("glyph-advance.json sha256 does not match its ranges")
    return advances


def verify_glyph_advance(aux: np.ndarray, advance_json: Path) -> List[int]:
    """Verify the aux block-table advances match spec/glyph-advance.json for all 65,536 codepoints."""
    if not advance_json.is_file():
        raise FileNotFoundError(f"Glyph advance spec JSON not found: {advance_json}")
    advances = load_advance_spec(advance_json)

    if af.decode_all_advances_fast(aux).tolist() != advances:
        raise AssertionError("UniTextAux advance table does not match expanded spec/glyph-advance.json!")

    spot_cps = (
        list(range(0x0000, 0x0100))
        + list(range(0x3000, 0x3100))
        + [0x041F, 0x2014, 0x4E00, 0x4E2D, 0x9FFF, 0xAC00, 0xD55C, 0xFF01, 0xFFFD, 0xFFFF]
    )
    for cp in spot_cps:
        if sample_advance_hlsl(aux, cp) != advances[cp]:
            raise AssertionError(f"HLSL advance sample mismatch at U+{cp:04X}")

    adv_i = sample_advance_hlsl(aux, ord("i"))
    adv_W = sample_advance_hlsl(aux, ord("W"))
    adv_zh = sample_advance_hlsl(aux, ord("中"))
    adv_sp = sample_advance_hlsl(aux, ord(" "))
    adv_ja = sample_advance_hlsl(aux, ord("あ"))
    adv_ko = sample_advance_hlsl(aux, ord("한"))
    if not (adv_i < adv_W < adv_zh == 12):
        raise AssertionError(f"Proportional width invariant failed: i={adv_i}, W={adv_W}, 中={adv_zh}")
    if adv_sp != 6 or adv_ja != 12 or adv_ko != 12:
        raise AssertionError(f"Unexpected space/kana/hangul advance: ' '={adv_sp}, あ={adv_ja}, 한={adv_ko}")
    return advances


def render_preview_board(atlas: np.ndarray, aux: np.ndarray, output_path: Path) -> None:
    scale = 2
    pad = 16
    palette = [
        (203, 213, 225),
        (255, 255, 255),
        (74, 222, 128),
        (251, 191, 36),
        (56, 189, 248),
        (45, 212, 191),
    ]
    lines = [
        "Speaker: Alice [ZH/JA/KO/EN] - Proportional v2 Live Subtitles!",
        "English: The quick brown fox jumps over the lazy dog cleanly!",
        "中文字幕测试：你好，世界！比例英文 + 全角汉字混排演示。",
        "日本語テスト：こんにちは世界！Mixed English & カタカナ OK",
        "한국어 테스트: 안녕하세요! Proportional Latin saves space.",
        "Status: Translating [64x32 CRT | 384px row | packed atlas]",
    ]
    row_h = 16 * scale
    row_w = 384 * scale
    w = row_w + pad * 2
    h = 6 * row_h + pad * 2
    board = Image.new("RGB", (w, h), (18, 22, 32))
    arr = np.asarray(board, dtype=np.uint8).copy()

    for r, line in enumerate(lines):
        color = palette[r % len(palette)]
        pen_x = 0
        y0 = pad + r * row_h
        for ch in line[:64]:
            cp = ord(ch)
            adv = sample_advance_hlsl(aux, cp)
            if pen_x + adv > 384:
                break
            g = sample_glyph_hlsl(atlas, aux, cp)
            draw_w = min(16, 384 - pen_x)
            g_crop = g[:, :draw_w]
            g_scaled = np.repeat(np.repeat(g_crop, scale, axis=0), scale, axis=1)
            x0 = pad + pen_x * scale
            mask = g_scaled > 0
            arr[y0 : y0 + row_h, x0 : x0 + draw_w * scale][mask] = color
            pen_x += adv

    output_path.parent.mkdir(parents=True, exist_ok=True)
    Image.fromarray(arr).save(output_path)
    print(f"Wrote preview board image to {output_path}")


def parse_args(argv: Optional[Sequence[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Verify UniText v2 packed font atlas + aux lookup texture.")
    parser.add_argument("--atlas", type=Path, default=DEFAULT_ATLAS, help=f"Packed atlas PNG (default: {DEFAULT_ATLAS}).")
    parser.add_argument("--aux", type=Path, default=DEFAULT_AUX, help=f"Aux lookup PNG (default: {DEFAULT_AUX}).")
    parser.add_argument("--quiet", action="store_true", help="Suppress ASCII glyph sample output.")
    parser.add_argument("--preview-png", type=Path, default=None, help="Optional rendered multilingual preview PNG.")
    parser.add_argument("--lut12-json", type=Path, default=DEFAULT_LUT12_JSON)
    parser.add_argument("--skip-lut12", action="store_true", help="Skip LUT12 codebook verification.")
    parser.add_argument("--advance-json", type=Path, default=DEFAULT_ADVANCE_JSON)
    parser.add_argument("--skip-advance", action="store_true", help="Skip glyph advance verification.")
    return parser.parse_args(argv)


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = parse_args(argv)
    verify_atlas(args.atlas, args.aux, print_samples=not args.quiet, preview_png=args.preview_png)
    atlas = load_rgba(args.atlas)
    aux = load_rgba(args.aux)
    print(f"Packed atlas {atlas.shape[1]}x{atlas.shape[0]} ({atlas.nbytes // 1024} KB VRAM) verification PASSED.")
    if not args.skip_advance:
        verify_glyph_advance(aux, args.advance_json)
        print("Glyph advance table (aux block table) verification PASSED.")
    if not args.skip_lut12:
        verify_lut12(atlas, aux, args.lut12_json)
        print("LUT12 codebook (aux region) verification PASSED.")
    print("\nFont atlas verification PASSED.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
