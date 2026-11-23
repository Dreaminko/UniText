#!/usr/bin/env python3
"""UniText canonical 12-bit LUT codebook generator.

Generates:
  - spec/lut12.json (4,096 deterministic BMP codepoints)
  - the LUT12 region (texels 2048..4095) of
    unity/Packages/dev.dreaminko.unitext/Textures/UniTextAux_64x64.png (64x64 RGBA8)
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Dict, List, Optional, Sequence

import numpy as np
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
import atlas_format  # noqa: E402

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_JSON_OUT = REPO_ROOT / "spec" / "lut12.json"
DEFAULT_AUX_PNG = (
    REPO_ROOT
    / "unity"
    / "Packages"
    / "dev.dreaminko.unitext"
    / "Textures"
    / "UniTextAux_64x64.png"
)

EXTRA_TRADITIONAL_AND_KANJI = (
    "這說時來國會學語體個們為無東車實際點開關問話認經動頭從進給後還對讓種樣當與發機過見應"
)


def build_lut12_codepoints() -> List[int]:
    """Build the canonical 4,096-entry UniText v1.1 LUT12 codebook."""
    codepoints: List[int] = []
    seen: set[int] = set()

    def add(cp: int) -> None:
        if not (0 <= cp <= 0xFFFF):
            raise ValueError(f"Codepoint out of BMP range: {cp}")
        if cp not in seen:
            seen.add(cp)
            codepoints.append(cp)

    # Slot 0: empty / transparent cell
    add(0x0000)

    # 1..95: ASCII printable (U+0020..U+007E)
    for cp in range(0x0020, 0x007F):
        add(cp)

    # Hiragana (U+3041..U+3096)
    for cp in range(0x3041, 0x3097):
        add(cp)

    # Katakana (U+30A1..U+30F6, U+30FB..U+30FE)
    for cp in range(0x30A1, 0x30F7):
        add(cp)
    for cp in (0x30FB, 0x30FC, 0x30FD, 0x30FE):
        add(cp)

    # Essential CJK, General, and Fullwidth punctuation
    for cp in (
        0x3001, 0x3002, 0x3008, 0x3009, 0x300A, 0x300B, 0x300C, 0x300D,
        0x300E, 0x300F, 0x3010, 0x3011, 0x3014, 0x3015, 0x301C,
        0x2014, 0x2018, 0x2019, 0x201C, 0x201D, 0x2026, 0x00B7,
    ):
        add(cp)
    for cp in range(0xFF01, 0xFF10):
        add(cp)
    for cp in range(0xFF1A, 0xFF21):
        add(cp)
    for cp in (0xFF3B, 0xFF3D, 0xFF3F, 0xFF5B, 0xFF5D, 0xFF5E, 0xFFE5):
        add(cp)

    # All 3,755 GB2312 Level-1 Hanzi
    for b1 in range(0xB0, 0xD8):
        for b2 in range(0xA1, 0xFF):
            if b1 == 0xD7 and b2 >= 0xFA:
                continue
            ch = bytes([b1, b2]).decode("gb2312")
            add(ord(ch))

    # Pad up to exact 4,096 slots with high-frequency Traditional / Joyo Kanji
    for ch in EXTRA_TRADITIONAL_AND_KANJI:
        if len(codepoints) >= 4096:
            break
        add(ord(ch))

    assert len(codepoints) == 4096, f"Expected 4096 entries, got {len(codepoints)}"
    assert codepoints[0] == 0, "Slot 0 must be 0x0000"
    assert len(set(codepoints)) == 4096, "Codepoints must be unique"
    assert all(1 <= cp <= 0xFFFF for cp in codepoints[1:]), "Non-zero slots must be valid BMP 1..0xFFFF"

    return codepoints


def generate_lut12(json_out: Path, aux_png: Optional[Path]) -> Dict[str, object]:
    codepoints = build_lut12_codepoints()
    digest = atlas_format.lut12_sha256(codepoints)

    doc: Dict[str, object] = {
        "schema_version": "1.1.0",
        "size": 4096,
        "sha256": digest,
        "codepoints": codepoints,
    }

    json_out.parent.mkdir(parents=True, exist_ok=True)
    json_out.write_text(json.dumps(doc, indent=2) + "\n", encoding="utf-8")

    print(f"Generated LUT12 codebook ({len(codepoints)} unique entries, sha256={digest[:16]}...)")
    print(f"  JSON: {json_out}")

    if aux_png is not None:
        if aux_png.is_file():
            aux = np.array(Image.open(aux_png).convert("RGBA"), dtype=np.uint8)
            if aux.shape != (atlas_format.AUX_SIZE, atlas_format.AUX_SIZE, 4):
                raise ValueError(f"Unexpected aux texture shape {aux.shape} in {aux_png}")
        else:
            print("  (aux texture missing; writing LUT region only - run generate_atlas.py to fill the block table)")
            aux = np.zeros((atlas_format.AUX_SIZE, atlas_format.AUX_SIZE, 4), dtype=np.uint8)
        flat = aux.reshape(-1, 4)
        atlas_format.write_lut_region(flat, codepoints)
        aux_png.parent.mkdir(parents=True, exist_ok=True)
        Image.fromarray(flat.reshape(aux.shape)).save(aux_png, format="PNG", compress_level=9)
        print(f"  AUX:  {aux_png} (LUT12 region texels 2048..4095)")
    return doc


def parse_args(argv: Optional[Sequence[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Generate UniText 12-bit LUT codebook (JSON + aux texture region).")
    parser.add_argument("--json-out", type=Path, default=DEFAULT_JSON_OUT)
    parser.add_argument("--aux-png", type=Path, default=DEFAULT_AUX_PNG)
    return parser.parse_args(argv)


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = parse_args(argv)
    generate_lut12(args.json_out, args.aux_png)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
