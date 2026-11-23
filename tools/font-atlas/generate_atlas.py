#!/usr/bin/env python3
"""UniText offline Unicode BMP packed font atlas, aux lookup texture & glyph advance generator.

Renders all 65,536 Unicode BMP codepoints (U+0000..U+FFFF) as 16x16 1-bit glyphs and packs
them into the v2 block-indexed bitstream atlas (`FontAtlas_BMP_Packed.png`, 1024 x H RGBA32)
plus the 64x64 aux texture (`UniTextAux_64x64.png`: block table + proportional advances +
LUT12 codebook) according to docs/PROTOCOL.md section 5 (see atlas_format.py).

Proportional layout: glyphs are baked **left-aligned at the pen origin**
(baseline row 13) and every codepoint gets a proportional advance width
(0..12 px) written to both the canonical sender spec
(`spec/glyph-advance.json`) and the aux texture.

Default font stack: SIL OFL 1.1 Fusion Pixel Font (12px Proportional).
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import sys
import urllib.error
import urllib.request
import zipfile
from pathlib import Path
from typing import Dict, List, Optional, Sequence, Tuple

import numpy as np
from fontTools.ttLib import TTFont
from PIL import Image, ImageDraw, ImageFont

sys.path.insert(0, str(Path(__file__).resolve().parent))
import atlas_format  # noqa: E402

REPO_ROOT = Path(__file__).resolve().parents[2]
TEXTURES_DIR = REPO_ROOT / "unity" / "Packages" / "dev.dreaminko.unitext" / "Textures"
DEFAULT_OUTPUT = TEXTURES_DIR / "FontAtlas_BMP_Packed.png"
DEFAULT_AUX_PNG = TEXTURES_DIR / "UniTextAux_64x64.png"
DEFAULT_ADVANCE_JSON = REPO_ROOT / "spec" / "glyph-advance.json"
DEFAULT_LUT12_JSON = REPO_ROOT / "spec" / "lut12.json"
DEFAULT_CACHE_DIR = REPO_ROOT / ".cache" / "fonts" / "fusion-pixel-proportional"

FUSION_PIXEL_REPO = "TakWolf/fusion-pixel-font"
FUSION_PIXEL_VERSION = "2026.11.10"
FUSION_PIXEL_VARIANT = "12px-proportional"

# v1.2 proportional layout constants (mirrored in docs/PROTOCOL.md section 1.2 and protocol.py)
ROW_PX = 384            # Layout width of one subtitle row in glyph pixels (= 32 full-width glyphs)
ROW_HEIGHT_PX = 16      # Glyph cell / line height in glyph pixels
MAX_CELLS_PER_ROW = 64  # Cells per physical row when UniText_Bank is synced
NARROW_CELLS_PER_ROW = 32
MAX_ADVANCE = 12        # Full-width advance; 32 * 12 == ROW_PX keeps every v1.1 row inside the panel
DEFAULT_BASELINE_Y = 13 # Fusion Pixel 12px proportional: ascent 13 + descent 3 == 16-row line box
TOFU_ADVANCE = 12

WHITESPACE_OR_CONTROL = (
    set(range(0x0000, 0x0021))
    | set(range(0x007F, 0x00A1))
    | set(range(0x2000, 0x2010))
    | {0x2028, 0x2029, 0x202F, 0x205F, 0x3000, 0xFEFF}
)

# Codepoints that never advance the pen (controls, zero-width format characters, surrogates, nonchars)
ZERO_WIDTH = (
    set(range(0x0000, 0x0020))
    | set(range(0x007F, 0x00A0))
    | {0x00AD}
    | set(range(0x200B, 0x2010))
    | set(range(0x2028, 0x202F))
    | set(range(0x2060, 0x2070))
    | set(range(0xD800, 0xE000))
    | set(range(0xFE00, 0xFE10))
    | {0xFEFF, 0xFFFE, 0xFFFF}
)


def is_blank_codepoint(cp: int) -> bool:
    return cp in WHITESPACE_OR_CONTROL or (0xD800 <= cp <= 0xDFFF)


def make_tofu_glyph() -> np.ndarray:
    """Create a clean left-aligned hollow box fallback glyph (advance 12) for uncovered codepoints."""
    return atlas_format.make_tofu_glyph()


def sort_fusion_pixel_fonts(font_paths: Sequence[Path]) -> List[Path]:
    priority = ("zh_hans", "zh_hant", "ja", "ko", "latin")

    def key_fn(p: Path) -> Tuple[int, str]:
        low = p.name.lower()
        for idx, lang in enumerate(priority):
            if lang in low:
                return (idx, low)
        return (len(priority), low)

    return sorted(font_paths, key=key_fn)


def _find_latest_fusion_pixel_zip_url() -> str:
    api_url = f"https://api.github.com/repos/{FUSION_PIXEL_REPO}/releases/latest"
    req = urllib.request.Request(
        api_url,
        headers={
            "User-Agent": "UniText-FontAtlas-Generator/1.0",
            "Accept": "application/vnd.github+json",
        },
    )
    with urllib.request.urlopen(req, timeout=60) as resp:
        release = json.loads(resp.read().decode("utf-8"))

    target_prefix = f"fusion-pixel-font-{FUSION_PIXEL_VARIANT}-ttf"
    for asset in release.get("assets", []):
        name = str(asset.get("name", ""))
        if target_prefix in name.lower() and name.lower().endswith(".zip"):
            return str(asset["browser_download_url"])
    raise RuntimeError(f"Could not find {target_prefix}*.zip in latest release of {FUSION_PIXEL_REPO}")


def download_fusion_pixel(
    cache_dir: Path,
    version: str = FUSION_PIXEL_VERSION,
) -> List[Path]:
    """Download SIL OFL 1.1 Fusion Pixel Font (12px Proportional TTF) into cache_dir."""
    cache_dir.mkdir(parents=True, exist_ok=True)
    cached = [
        p
        for p in cache_dir.glob("*.ttf")
        if p.is_file() and p.stat().st_size > 10_000
    ]
    if cached and any(p.stat().st_size > 100_000 for p in cached):
        sorted_cached = sort_fusion_pixel_fonts(cached)
        print(f"Using {len(sorted_cached)} cached Fusion Pixel font(s) in {cache_dir}")
        return sorted_cached

    ver_clean = version.lstrip("v")
    candidate_urls = [
        f"https://github.com/{FUSION_PIXEL_REPO}/releases/download/{version}/fusion-pixel-font-{FUSION_PIXEL_VARIANT}-ttf-v{ver_clean}.zip",
        f"https://github.com/{FUSION_PIXEL_REPO}/releases/download/{version}/fusion-pixel-font-{FUSION_PIXEL_VARIANT}-ttf-{ver_clean}.zip",
    ]

    data: Optional[bytes] = None
    used_url = ""
    for url in candidate_urls:
        try:
            print(f"Downloading Fusion Pixel Font from {url} ...")
            req = urllib.request.Request(
                url, headers={"User-Agent": "UniText-FontAtlas-Generator/1.0"}
            )
            with urllib.request.urlopen(req, timeout=180) as resp:
                data = resp.read()
            used_url = url
            break
        except urllib.error.HTTPError as exc:
            if exc.code == 404:
                print(f"Asset not found at {url} (404), trying fallback...")
                continue
            raise

    if data is None:
        latest_url = _find_latest_fusion_pixel_zip_url()
        print(f"Downloading Fusion Pixel Font from {latest_url} ...")
        req = urllib.request.Request(
            latest_url, headers={"User-Agent": "UniText-FontAtlas-Generator/1.0"}
        )
        with urllib.request.urlopen(req, timeout=180) as resp:
            data = resp.read()
        used_url = latest_url

    if len(data) < 100_000:
        raise RuntimeError(
            f"Downloaded archive from {used_url} looks unexpectedly small ({len(data)} bytes)"
        )

    extracted_fonts: List[Path] = []
    with zipfile.ZipFile(io.BytesIO(data)) as zf:
        for info in zf.infolist():
            if info.is_dir():
                continue
            filename = Path(info.filename).name
            if not filename:
                continue
            low = filename.lower()
            if low.endswith(".ttf") or filename.upper().startswith(("OFL", "LICENSE")):
                dest = cache_dir / filename
                dest.write_bytes(zf.read(info))
                if low.endswith(".ttf"):
                    extracted_fonts.append(dest)
                    print(f"Extracted {dest} ({dest.stat().st_size} bytes)")
                else:
                    print(f"Extracted license file {dest}")

    if not extracted_fonts:
        raise RuntimeError(f"No .ttf files found in {used_url}")

    return sort_fusion_pixel_fonts(extracted_fonts)


def load_bmp_cmap(font_path: Path) -> set[int]:
    with TTFont(font_path, fontNumber=0) as tt:
        cmap = tt.getBestCmap() or {}
        return {cp for cp in cmap.keys() if 0 <= cp <= 0xFFFF}


def glyph_advance_px(font: ImageFont.FreeTypeFont, ch: str) -> int:
    """Proportional pen advance in glyph pixels, clamped to the v1.2 range 0..MAX_ADVANCE."""
    try:
        adv = int(round(font.getlength(ch)))
    except Exception:
        adv = MAX_ADVANCE
    return max(0, min(MAX_ADVANCE, adv))


def render_glyph_16x16(
    font: ImageFont.FreeTypeFont,
    ch: str,
    baseline_y: int = DEFAULT_BASELINE_Y,
    threshold: int = 128,
) -> np.ndarray:
    """Render a pixel glyph left-aligned at the pen origin into a 16x16 0/1 numpy uint8 array.

    v1.2 proportional layout places glyph column 0 exactly at the pen position, so the
    renderer keeps the font's own side bearings and only nudges ink that would fall
    outside the 16x16 cell.
    """
    try:
        bbox = font.getbbox(ch, mode="L", anchor="ls")
    except Exception:
        bbox = font.getbbox(ch, mode="L")

    if bbox is None:
        return np.zeros((16, 16), dtype=np.uint8)

    left, top, right, bottom = bbox
    gw = right - left
    gh = bottom - top
    if gw <= 0 or gh <= 0:
        return np.zeros((16, 16), dtype=np.uint8)

    canvas = Image.new("L", (32, 32), 0)
    draw = ImageDraw.Draw(canvas)

    draw_x = 0
    if draw_x + left < 0:
        draw_x -= draw_x + left
    if draw_x + right > 16:
        draw_x -= (draw_x + right) - 16
        draw_x = max(draw_x, -left)

    draw_y = baseline_y
    if draw_y + top < 0:
        draw_y -= draw_y + top
    if draw_y + bottom > 16:
        draw_y -= (draw_y + bottom) - 16

    try:
        draw.text((draw_x, draw_y), ch, fill=255, font=font, anchor="ls")
    except Exception:
        draw.text((draw_x, 1), ch, fill=255, font=font)

    cropped = canvas.crop((0, 0, 16, 16))
    arr = np.asarray(cropped, dtype=np.uint8)

    bits = (arr >= threshold).astype(np.uint8)
    if bits.sum() == 0:
        bits = (arr >= 64).astype(np.uint8)
    return bits


def load_lut12_codepoints(path: Path) -> List[int]:
    doc = json.loads(path.read_text(encoding="utf-8"))
    cps = [int(cp) for cp in doc["codepoints"]]
    if len(cps) != 4096:
        raise ValueError(f"{path} must contain 4096 LUT12 codepoints (run generate_lut12.py first)")
    return cps


def advances_to_ranges(advances: Sequence[int]) -> List[List[int]]:
    """Run-length encode 65,536 advances into inclusive [start, end, advance] ranges."""
    ranges: List[List[int]] = []
    start = 0
    for cp in range(1, 65537):
        if cp == 65536 or int(advances[cp]) != int(advances[start]):
            ranges.append([start, cp - 1, int(advances[start])])
            start = cp
    return ranges


def advances_sha256(advances: Sequence[int]) -> str:
    return hashlib.sha256(bytes(int(a) for a in advances)).hexdigest()


def build_advance_spec(advances: Sequence[int]) -> dict:
    return {
        "schema_version": "1.2.0",
        "description": (
            "UniText v1.2 proportional glyph advance table (glyph pixels) for every Unicode BMP codepoint. "
            "Senders MUST use this exact table to wrap rows; BufferCRT.shader reads the identical "
            "advances from the UniTextAux_64x64.png block table to place glyphs."
        ),
        "font": f"Fusion Pixel Font {FUSION_PIXEL_VARIANT} {FUSION_PIXEL_VERSION} (SIL OFL 1.1)",
        "row_px": ROW_PX,
        "row_height_px": ROW_HEIGHT_PX,
        "max_cells_per_row": MAX_CELLS_PER_ROW,
        "narrow_cells_per_row": NARROW_CELLS_PER_ROW,
        "max_advance": MAX_ADVANCE,
        "sha256": advances_sha256(advances),
        "ranges": advances_to_ranges(advances),
    }


def write_advance_spec(advances: Sequence[int], json_path: Path) -> None:
    doc = build_advance_spec(advances)
    json_path.parent.mkdir(parents=True, exist_ok=True)
    header = {k: v for k, v in doc.items() if k != "ranges"}
    lines = json.dumps(header, indent=2, ensure_ascii=False).rstrip("}").rstrip()
    range_lines = ",\n".join(f"    [{s}, {e}, {a}]" for s, e, a in doc["ranges"])
    json_path.write_text(
        f"{lines},\n  \"ranges\": [\n{range_lines}\n  ]\n}}\n", encoding="utf-8", newline="\n"
    )
    json.loads(json_path.read_text(encoding="utf-8"))  # self-check
    print(f"Wrote glyph advance spec ({len(doc['ranges'])} ranges) to {json_path}")


def compute_coverage_report(covered: set[int]) -> Dict[str, Dict[str, int | float]]:
    ranges = {
        "ASCII Printable (U+0021..007E)": range(0x0021, 0x007F),
        "Hiragana (U+3040..309F)": range(0x3040, 0x30A0),
        "Katakana (U+30A0..30FF)": range(0x30A0, 0x3100),
        "CJK Symbols & Punct (U+3001..303F)": range(0x3001, 0x3040),
        "CJK Unified Ideographs (U+4E00..9FFF)": range(0x4E00, 0xA000),
        "Hangul Syllables (U+AC00..D7AF)": range(0xAC00, 0xD7B0),
        "Halfwidth & Fullwidth (U+FF00..FFEF)": range(0xFF00, 0xFFF0),
        "Total BMP (U+0000..FFFF)": range(0x0000, 0x10000),
    }
    report: Dict[str, Dict[str, int | float]] = {}
    for label, rng in ranges.items():
        total = len(rng)
        hits = sum(1 for cp in rng if cp in covered)
        report[label] = {
            "covered": hits,
            "total": total,
            "percent": round((hits / total) * 100.0, 2) if total else 0.0,
        }
    return report


def generate_atlas(
    font_paths: Sequence[Path],
    output_path: Path,
    pixel_size: int = 12,
    baseline_y: int = DEFAULT_BASELINE_Y,
    threshold: int = 128,
    report_path: Optional[Path] = None,
    aux_png_path: Optional[Path] = DEFAULT_AUX_PNG,
    advance_json_path: Optional[Path] = DEFAULT_ADVANCE_JSON,
    lut12_json_path: Path = DEFAULT_LUT12_JSON,
) -> Dict[str, Dict[str, int | float]]:
    if not font_paths:
        raise ValueError("At least one font path must be provided.")

    loaded_fonts: List[Tuple[Path, ImageFont.FreeTypeFont, set[int]]] = []
    for fp in font_paths:
        if not fp.is_file():
            raise FileNotFoundError(f"Font file not found: {fp}")
        cmap = load_bmp_cmap(fp)
        pil_font = ImageFont.truetype(str(fp), size=pixel_size)
        loaded_fonts.append((fp, pil_font, cmap))
        print(f"Loaded {fp.name}: {len(cmap)} BMP glyphs")

    owner: Dict[int, ImageFont.FreeTypeFont] = {}
    advance_owner: Dict[int, ImageFont.FreeTypeFont] = {}
    for _, pil_font, cmap in loaded_fonts:
        for cp in cmap:
            if cp not in advance_owner:
                advance_owner[cp] = pil_font
            if cp not in owner and not is_blank_codepoint(cp):
                owner[cp] = pil_font

    glyphs = np.zeros((65536, 16, 16), dtype=np.uint8)
    advances = np.zeros((65536,), dtype=np.uint8)
    tofu = make_tofu_glyph()

    covered_nonblank = set(owner.keys())
    for cp in range(65536):
        ch = chr(cp)
        if cp in ZERO_WIDTH:
            advances[cp] = 0
        elif cp in advance_owner:
            advances[cp] = glyph_advance_px(advance_owner[cp], ch)
        elif is_blank_codepoint(cp):
            advances[cp] = MAX_ADVANCE if cp == 0x3000 else MAX_ADVANCE // 2
        else:
            advances[cp] = TOFU_ADVANCE

        if is_blank_codepoint(cp):
            continue
        pil_font = owner.get(cp)
        if pil_font is None:
            glyphs[cp] = tofu
            continue
        arr = render_glyph_16x16(
            pil_font,
            ch,
            baseline_y=baseline_y,
            threshold=threshold,
        )
        if arr.sum() == 0:
            glyphs[cp] = tofu
        else:
            glyphs[cp] = arr

    advances[0] = 0
    lut_cps = load_lut12_codepoints(lut12_json_path)
    atlas_rgba, aux_rgba, pack_stats = atlas_format.pack_atlas(glyphs, advances, lut_cps)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    img = Image.fromarray(atlas_rgba)
    img.save(output_path, format="PNG", compress_level=9)
    print(
        f"Wrote packed font atlas ({img.size[0]}x{img.size[1]} RGBA, {pack_stats['atlas_bytes'] // 1024} KB VRAM; "
        f"blocks small={pack_stats['small']} large={pack_stats['large']} absent={pack_stats['absent']}, "
        f"clipped glyphs={pack_stats['clipped_glyphs']}) to {output_path}"
    )

    if aux_png_path is not None:
        aux_png_path.parent.mkdir(parents=True, exist_ok=True)
        Image.fromarray(aux_rgba).save(aux_png_path, format="PNG", compress_level=9)
        print(f"Wrote aux lookup texture (64x64 RGBA, {pack_stats['mixed_advance']} mixed-advance blocks) to {aux_png_path}")
    if advance_json_path is not None:
        write_advance_spec(advances.tolist(), advance_json_path)

    report = compute_coverage_report(covered_nonblank | (WHITESPACE_OR_CONTROL & set(range(0x10000))))
    print("\n--- BMP Coverage Report ---")
    for label, stats in report.items():
        print(f"  {label:<40s}: {stats['covered']:>5}/{stats['total']:<5} ({stats['percent']:>6.2f}%)")

    if report_path is not None:
        report_path.parent.mkdir(parents=True, exist_ok=True)
        report_path.write_text(json.dumps(report, indent=2), encoding="utf-8")
        print(f"Wrote coverage report to {report_path}")

    return report


def parse_args(argv: Optional[Sequence[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Generate UniText packed Unicode BMP font atlas + aux lookup texture.")
    parser.add_argument(
        "--fonts",
        nargs="+",
        type=Path,
        default=None,
        help="Ordered list of TTF/OTF fonts (first font has highest priority).",
    )
    parser.add_argument(
        "--download-fusion-pixel",
        action="store_true",
        help="Download SIL OFL 1.1 Fusion Pixel Font (12px Proportional TTF) into cache dir.",
    )
    parser.add_argument(
        "--fusion-pixel-version",
        type=str,
        default=FUSION_PIXEL_VERSION,
        help=f"Fusion Pixel Font release tag to download (default: {FUSION_PIXEL_VERSION}).",
    )
    parser.add_argument(
        "--download-only",
        action="store_true",
        help="Only download/cache fonts without generating the PNG.",
    )
    parser.add_argument(
        "--cache-dir",
        type=Path,
        default=DEFAULT_CACHE_DIR,
        help="Directory for cached downloaded fonts.",
    )
    parser.add_argument(
        "--pixel-size",
        type=int,
        default=12,
        help="Font rendering pixel size inside 16x16 cell (default: 12).",
    )
    parser.add_argument(
        "--baseline-y",
        type=int,
        default=DEFAULT_BASELINE_Y,
        help=f"Baseline Y offset inside 16x16 cell (default: {DEFAULT_BASELINE_Y}).",
    )
    parser.add_argument(
        "--threshold",
        type=int,
        default=128,
        help="Grayscale alpha binarization threshold 1..255 (default: 128).",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=DEFAULT_OUTPUT,
        help=f"Output PNG path (default: {DEFAULT_OUTPUT}).",
    )
    parser.add_argument(
        "--aux-png",
        type=Path,
        default=DEFAULT_AUX_PNG,
        help=f"Output 64x64 aux lookup texture (block table + advances + LUT12) (default: {DEFAULT_AUX_PNG}).",
    )
    parser.add_argument(
        "--lut12-json",
        type=Path,
        default=DEFAULT_LUT12_JSON,
        help=f"Canonical LUT12 codebook packed into the aux texture (default: {DEFAULT_LUT12_JSON}).",
    )
    parser.add_argument(
        "--advance-json",
        type=Path,
        default=DEFAULT_ADVANCE_JSON,
        help=f"Output canonical glyph advance spec JSON (default: {DEFAULT_ADVANCE_JSON}).",
    )
    parser.add_argument(
        "--report",
        type=Path,
        default=None,
        help="Optional path to save coverage report JSON.",
    )
    return parser.parse_args(argv)


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = parse_args(argv)
    font_paths: List[Path] = []
    if args.fonts:
        font_paths.extend(args.fonts)
    if args.download_fusion_pixel:
        font_paths.extend(download_fusion_pixel(args.cache_dir, version=args.fusion_pixel_version))

    if not font_paths and args.cache_dir.is_dir():
        cached = sorted(
            list(args.cache_dir.rglob("*.ttf")) + list(args.cache_dir.rglob("*.otf"))
        )
        font_paths = sort_fusion_pixel_fonts(cached)

    if args.download_only:
        if not font_paths:
            print("ERROR: No fonts downloaded or found.", file=sys.stderr)
            return 1
        print(f"Downloaded/cached {len(font_paths)} Fusion Pixel font files.")
        return 0

    if not font_paths:
        print(
            "ERROR: No fonts provided and none cached. Pass --download-fusion-pixel or --fonts <paths>.",
            file=sys.stderr,
        )
        return 1

    generate_atlas(
        font_paths=font_paths,
        output_path=args.output,
        pixel_size=args.pixel_size,
        baseline_y=args.baseline_y,
        threshold=args.threshold,
        report_path=args.report,
        aux_png_path=args.aux_png,
        advance_json_path=args.advance_json,
        lut12_json_path=args.lut12_json,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
