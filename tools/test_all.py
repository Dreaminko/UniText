#!/usr/bin/env python3
"""End-to-end automated validation suite for UniText."""

from __future__ import annotations

import hashlib
import json
import re
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path
from typing import Dict, List, Optional, Sequence, Set, Tuple

import numpy as np
from PIL import Image

REPO_ROOT = Path(__file__).resolve().parents[1]
PKG_DIR = REPO_ROOT / "unity" / "Packages" / "dev.dreaminko.unitext"
VECTORS_PATH = REPO_ROOT / "spec" / "test-vectors.json"
LUT12_PATH = REPO_ROOT / "spec" / "lut12.json"
ADVANCE_JSON_PATH = REPO_ROOT / "spec" / "glyph-advance.json"
ATLAS_PATH = PKG_DIR / "Textures" / "FontAtlas_BMP_Packed.png"
AUX_PNG_PATH = PKG_DIR / "Textures" / "UniTextAux_64x64.png"

sys.path.insert(0, str(REPO_ROOT / "tools" / "osc-tester"))
sys.path.insert(0, str(REPO_ROOT / "tools" / "font-atlas"))

from generate_lut12 import build_lut12_codepoints  # noqa: E402
import atlas_format as af  # noqa: E402
from protocol import (  # noqa: E402
    COLS,
    COMPACT36_BYTES,
    CRT_H,
    CRT_W,
    CTRL_ASCII_MAX,
    CTRL_ASCII_MIN,
    CTRL_CLEAR_ALL,
    CTRL_CLEAR_ROW,
    CTRL_LUT12_LAST,
    CTRL_LUT12_MAX,
    CTRL_LUT12_MIN,
    CTRL_NOP,
    CTRL_PAGE_MAX,
    CTRL_PAGE_MIN,
    CTRL_SET_GEOMETRY,
    CTRL_SET_VIEW,
    CTRL_UTF16_MAX,
    CTRL_UTF16_MIN,
    GEOMETRY_PX,
    LAYOUT_ROW0,
    MAX_ROW_PX8,
    MAX_ROWS,
    META_ROW,
    MIN_ROW_PX8,
    MIN_ROWS,
    PAGE_ROWS,
    PAGE_SEL_TO_HI,
    ROW_PX,
    TIERS,
    TOTAL_CELLS,
    WIDE_COLS,
    CRTBufferSimulator,
    Packet,
    VirtualScreen,
    encode_geometry_packet,
    encode_screen_packets,
    full_bytes,
    load_lut12,
    mask_payload,
    resolve_live_viewport,
    text_advance,
    verify_test_vectors,
    wrap_text,
)
from send_demo import (  # noqa: E402
    detect_tier_from_oscquery_tree,
    detect_viewport_from_oscquery_tree,
)
from verify_atlas import (  # noqa: E402
    load_rgba,
    sample_advance_hlsl,
    sample_glyph_hlsl,
    verify_atlas,
    verify_glyph_advance,
    verify_lut12,
)


def test_vectors() -> None:
    print("[1/6] Verifying spec/test-vectors.json (encoder + 15-frame CRT idempotency)...")
    count = verify_test_vectors(VECTORS_PATH)
    assert count >= 11, f"Expected >= 11 test vectors, got {count}"


def test_font_atlas_and_crt_to_display() -> None:
    print("[2/6] Verifying FontAtlas, UniTextAux, Lut12, and 5-tier CRT-to-Display pipelines...")
    verify_atlas(ATLAS_PATH, AUX_PNG_PATH, print_samples=False, preview_png=None)
    atlas_rgba = load_rgba(ATLAS_PATH)
    aux_rgba = load_rgba(AUX_PNG_PATH)
    assert atlas_rgba.nbytes <= 700 * 1024, f"Packed atlas VRAM {atlas_rgba.nbytes} exceeds 700 KB"
    assert aux_rgba.nbytes == 16 * 1024, f"Aux texture VRAM {aux_rgba.nbytes} != 16 KB"

    verify_glyph_advance(aux_rgba, ADVANCE_JSON_PATH)

    lut_doc = json.loads(LUT12_PATH.read_text(encoding="utf-8"))
    assert lut_doc["codepoints"] == build_lut12_codepoints(), "spec/lut12.json diverged from generate_lut12.py!"
    verify_lut12(atlas_rgba, aux_rgba, LUT12_PATH)

    def _assert_display_layout(sim: CRTBufferSimulator, rows_text: Sequence[str], label: str) -> None:
        got_px = sim.read_visual_row_px()
        exp_px = [text_advance(txt) for txt in rows_text]
        assert got_px == exp_px, f"{label}: row_px mismatch {got_px} != {exp_px}"
        for vr, expected_txt in enumerate(rows_text):
            expected_pixels: List[tuple[int, int]] = []
            for ch in expected_txt:
                cp = ord(ch)
                adv = sample_advance_hlsl(aux_rgba, cp)
                if ch != " ":
                    assert sample_glyph_hlsl(atlas_rgba, aux_rgba, cp).sum() > 0, f"{label}: blank glyph U+{cp:04X}"
                for gx in range(adv):
                    expected_pixels.append((cp, gx))
            while len(expected_pixels) < ROW_PX:
                expected_pixels.append((0, 0))
            for pen_x in range(ROW_PX):
                got_pix = sim.glyph_at(vr, pen_x)
                assert got_pix == expected_pixels[pen_x], f"{label} vr={vr} pen_x={pen_x}: {got_pix} != {expected_pixels[pen_x]}"

    # Wide 64-col 6-row proportional wraparound across all 5 tiers
    initial = [
        "iW Proportional Latin packs ~60+ English chars per subtitle row!",
        "中文 32 全角：实时翻译字幕测试完成，保持完整容量与清晰像素！",
        "Alice: 今天的 VRChat meetup 我们讨论 proportional fonts",
        "日本語テスト：こんにちは世界！Mixed English & カタカナ OK",
        "한국어 테스트: 안녕하세요! Proportional Latin saves space.",
        "Status: Translating [64x32 CRT | 384px row | 1024x512 Atlas]",
    ]
    scroll_lines = [
        "Scroll 1: Proportional English subtitles wrap cleanly at spaces!",
        "Scroll 2: こんにちは！Mixed 日本語 and English on Wide Rows!",
        "Scroll 3: 中文仍保持每行 32 个全角汉字，混排 English 更紧凑。",
        "Scroll 4: Korean 한국어 자막 + wide proportional Latin OK!",
        "Scroll 5: Even 33b Lite scrolls 60+ English chars cleanly here!",
        "Scroll 6: Wraparound back to topRow=0 on 64x32 BufferCRT OK!",
    ]
    for line in initial + scroll_lines:
        assert len(line) <= WIDE_COLS and text_advance(line) <= ROW_PX

    for tier in ("lite", "compact", "balanced", "standard", "turbo"):
        scr = VirtualScreen(WIDE_COLS, 6)
        for r, txt in enumerate(initial):
            scr.set_row_text(r, txt)
            scr.set_row_style(r, color=(r + 1) % 16, flags=r % 3)
        scr.set_status(2)

        sim = CRTBufferSimulator(rows=6)
        for pkt in encode_screen_packets(scr, tier=tier, dirty_only=False):
            sim.apply_packet(pkt, tier=tier, frames=15)
        scr.mark_clean()
        assert sim.read_top_row() == 0
        assert sim.read_visual_cells() == scr.read_visual_cells()
        _assert_display_layout(sim, initial, f"{tier}/init")

        expected_rows = list(initial)
        for step, new_line in enumerate(scroll_lines):
            scr.scroll_up(1)
            scr.set_row_text(5, new_line)
            scr.set_row_style(5, color=(step + 3) % 16, flags=step % 3)
            scr.set_status(1 if (step % 2 == 0) else 2)
            for pkt in encode_screen_packets(scr, tier=tier, dirty_only=True):
                sim.apply_packet(pkt, tier=tier, frames=15)
            scr.mark_clean()
            assert sim.read_top_row() == (step + 1) % 6
            assert sim.read_visual_cells() == scr.read_visual_cells()
            assert sim.read_visual_row_styles() == scr.read_visual_row_styles()
            assert sim.read_status() == scr.status
            expected_rows = expected_rows[1:] + [new_line]
            _assert_display_layout(sim, expected_rows, f"{tier}/step-{step}")
        assert sim.read_top_row() == 0

    # Narrow 32-cell board isolation
    narrow = VirtualScreen(COLS, 6)
    narrow_rows = ["Narrow 32-cell proportional", "中文短行测试", "", "", "", ""]
    for r, txt in enumerate(narrow_rows):
        if txt:
            narrow.set_row_text(r, txt)
    narrow_pkts = encode_screen_packets(narrow, tier="balanced", dirty_only=False)
    assert all(p.bank == 0 and p.row_page == 0 for p in narrow_pkts)
    narrow_sim = CRTBufferSimulator(rows=6)
    for pkt in narrow_pkts:
        narrow_sim.apply_packet(pkt, tier="balanced", frames=15)
    assert narrow_sim.read_visual_cells(COLS, 6) == narrow.read_visual_cells()
    full_narrow = narrow_sim.read_visual_cells(WIDE_COLS, 6)
    assert all(full_narrow[r * 64 + c] == 0 for r in range(6) for c in range(32, 64))

    # Extended 8-row & 12-row resizable viewport wraparound
    long_text = (
        "UniText 视窗宽高与显示行数可自由调整：这是一段较长的中英混排字幕，用来验证 8 行和 12 行模式下的自动换行与 Ring-Buffer 滚动。"
        "Even long bilingual sentences stay readable without truncation across resizable viewports!"
    )
    for test_rows, test_row_px, tier in ((8, 256, "lite"), (12, 384, "balanced")):
        wrapped = wrap_text(long_text, max_px=test_row_px, max_cells=WIDE_COLS)
        assert len(wrapped) >= 3
        scr_ext = VirtualScreen(WIDE_COLS, test_rows)
        for r in range(test_rows):
            scr_ext.set_row_text(r, wrapped[r % len(wrapped)])
            scr_ext.set_row_style(r, color=(r + 1) % 16, flags=r % 3)
        scr_ext.set_status(2)

        sim_ext = CRTBufferSimulator(rows=test_rows)
        for pkt in encode_screen_packets(scr_ext, tier=tier, dirty_only=False):
            sim_ext.apply_packet(pkt, tier=tier, frames=15)
        scr_ext.mark_clean()
        assert sim_ext.read_visual_cells() == scr_ext.read_visual_cells()
        assert max(sim_ext.read_visual_row_px()) <= test_row_px

        for step in range(test_rows):
            scr_ext.scroll_up(1)
            scr_ext.set_row_text(test_rows - 1, wrapped[(test_rows + step) % len(wrapped)])
            scr_ext.set_row_style(test_rows - 1, color=(step + 4) % 16, flags=step % 3)
            scr_ext.set_status(1 if (step % 2 == 0) else 2)
            for pkt in encode_screen_packets(scr_ext, tier=tier, dirty_only=True):
                sim_ext.apply_packet(pkt, tier=tier, frames=15)
            scr_ext.mark_clean()
            assert sim_ext.read_top_row() == (step + 1) % test_rows
            assert sim_ext.read_visual_cells() == scr_ext.read_visual_cells()
            assert sim_ext.read_visual_row_styles() == scr_ext.read_visual_row_styles()
            assert sim_ext.read_status() == scr_ext.status
        assert sim_ext.read_top_row() == 0

    # Compact36: 30-char Chinese row in 10 pkts, 32-char in 11 pkts, chunk 10 does not bleed into row 1
    zh30 = "实时翻译字幕测试：你好世界，我们在这里讨论比例字体与压缩参数"
    zh32 = zh30 + "配置"
    scr30 = VirtualScreen(WIDE_COLS, 6)
    scr30.set_row_text(0, zh30)
    pkts30 = encode_screen_packets(scr30, tier="compact", dirty_only=False)
    assert len(pkts30) == 10, f"Compact36 expected 10 pkts for 30 CJK chars, got {len(pkts30)}"
    scr32 = VirtualScreen(WIDE_COLS, 6)
    scr32.set_row_text(0, zh32)
    pkts32 = encode_screen_packets(scr32, tier="compact", dirty_only=False)
    assert len(pkts32) == 11, f"Compact36 expected 11 pkts for 32 CJK chars, got {len(pkts32)}"
    sim_c36 = CRTBufferSimulator(rows=6)
    for pkt in pkts30:
        sim_c36.apply_packet(pkt, tier="compact", frames=15)
    # Force chunk 10 LUT12 packet on row 0 (cols 30..31) with a non-zero 3rd LUT index to verify row 1 isolation
    _, cp_to_idx = (None, {cp: idx for idx, cp in enumerate(lut_doc["codepoints"])})
    idx_a, idx_b, idx_c = cp_to_idx[ord(zh32[30])], cp_to_idx[ord(zh32[31])], 42
    c10_pkt = Packet(
        CTRL_LUT12_MIN + 10,
        ((idx_a >> 4) & 0xFF, ((idx_a & 0x0F) << 4) | ((idx_b >> 8) & 0x0F), idx_b & 0xFF, (idx_c >> 4) & 0xFF, (idx_c & 0x0F) << 4),
        0,
        0,
    )
    sim_c36.apply_packet(c10_pkt, tier="compact", frames=15)
    assert sim_c36.read_visual_cells() == scr32.read_visual_cells(), "Compact36 chunk-10 bled into row 1 or mismatched!"

    # CMD_SET_GEOMETRY: sets geometry, survives CMD_CLEAR_ALL and local_clear(), rejects out-of-range
    geo_sim = CRTBufferSimulator(rows=12)
    assert geo_sim.read_geometry() is None
    geo_sim.apply_packet(encode_geometry_packet(3, 9, 448), tier="lite", frames=15)
    assert geo_sim.read_geometry() == (9, 448)
    geo_sim.apply_packet(Packet(CTRL_CLEAR_ALL, (0, 0, 0)), tier="lite", frames=15)
    assert geo_sim.read_geometry() == (9, 448)
    geo_sim.local_clear()
    assert geo_sim.read_geometry() == (9, 448)
    geo_sim.apply_packet(Packet(CTRL_SET_GEOMETRY, (3, 48, 0)), tier="lite", frames=15)
    geo_sim.apply_packet(Packet(CTRL_SET_GEOMETRY, (13, 48, 0)), tier="lite", frames=15)
    geo_sim.apply_packet(Packet(CTRL_SET_GEOMETRY, (6, 23, 0)), tier="lite", frames=15)
    geo_sim.apply_packet(Packet(CTRL_SET_GEOMETRY, (6, 81, 0)), tier="lite", frames=15)
    assert geo_sim.read_geometry() == (9, 448), f"Out-of-range CMD_SET_GEOMETRY mutated state: {geo_sim.read_geometry()}"

    _verify_optimized_pipeline(atlas_rgba, aux_rgba)


CACHE_ROW = 13


class _OptimizedCRTSimulator:
    """Bit-exact Python mirror of the optimized BufferCRT + Display shader logic."""

    def __init__(self, aux_rgba: np.ndarray, rows: int = 6) -> None:
        self.aux_rgba = aux_rgba
        self.rows = rows
        self.dedup_skips = 0
        self.active_frames = 0
        self.early_out_skips = 0
        self.binary_searches = 0
        self.reset()

    def reset(self) -> None:
        self.buffer: List[List[List[int]]] = [
            [[0, 0, 0, 0] for _ in range(CRT_W)] for _ in range(CRT_H)
        ]

    def load_from_screen(self, screen: VirtualScreen) -> None:
        self.reset()
        for row in range(screen.rows):
            for col in range(screen.cols):
                cp = screen.cells[row * screen.cols + col]
                if cp != 0:
                    self.buffer[row][col] = [(cp >> 8) & 0xFF, cp & 0xFF, 0, 255]
        for row in range(screen.rows):
            color, flags = screen.row_styles[row]
            self.buffer[META_ROW][row] = [color & 0x0F, flags & 0x3F, 0, 255]
        if screen.top_row != 0:
            self.buffer[META_ROW][30] = [screen.top_row % self.rows, 0, 0, 255]
        self.buffer[META_ROW][31] = [screen.status & 0x0F, 0, 0, 255]
        for phys in range(MAX_ROWS):
            x_acc = 0
            for col in range(WIDE_COLS):
                r, g, _, a = self.buffer[phys][col]
                cp = ((r << 8) | g) if a != 0 else 0
                adv = sample_advance_hlsl(self.aux_rgba, cp)
                self.buffer[LAYOUT_ROW0 + phys][col] = [(x_acc >> 8) & 0xFF, x_acc & 0xFF, adv & 0xFF, 255]
                x_acc += adv
            self.buffer[META_ROW][phys][2] = min(255, (x_acc + 3) >> 2)

    @staticmethod
    def _cell_span_bounds(start_cell: int, count: int, page_row: int, bank_base: int) -> Optional[Tuple[int, int]]:
        if count == 0 or start_cell >= TOTAL_CELLS:
            return None
        end_cell = min(start_cell + count, TOTAL_CELLS)
        row_start = page_row * COLS
        row_end = row_start + COLS
        lo_cell = max(start_cell, row_start)
        hi_cell = min(end_cell, row_end)
        if lo_cell >= hi_cell:
            return None
        return bank_base + (lo_cell - row_start), bank_base + (hi_cell - row_start - 1)

    @classmethod
    def packet_row_bounds(
        cls,
        ctrl: int,
        data: Sequence[int],
        n_bytes: int,
        bank: int,
        row_page: int,
        phys_row: int,
    ) -> Optional[Tuple[int, int]]:
        if phys_row >= MAX_ROWS:
            return None
        phys_page = 1 if phys_row >= PAGE_ROWS else 0
        if phys_page != row_page:
            return None
        page_row = phys_row - phys_page * PAGE_ROWS
        bank_base = bank * COLS

        if CTRL_UTF16_MIN <= ctrl <= CTRL_UTF16_MAX:
            odd_offset = (data[2] & 1) if n_bytes == 3 else 0
            start_cell = (ctrl - CTRL_UTF16_MIN) * 2 + odd_offset
            count = n_bytes // 2
            return cls._cell_span_bounds(start_cell, count, page_row, bank_base)

        if CTRL_ASCII_MIN <= ctrl <= CTRL_ASCII_MAX:
            start_cell = (ctrl - CTRL_ASCII_MIN) * 4
            count = full_bytes(n_bytes)
            return cls._cell_span_bounds(start_cell, count, page_row, bank_base)

        if (CTRL_LUT12_MIN <= ctrl <= CTRL_LUT12_MAX) or ctrl == CTRL_LUT12_LAST:
            slot = 95 if ctrl == CTRL_LUT12_LAST else (ctrl - CTRL_LUT12_MIN)
            if n_bytes == COMPACT36_BYTES:
                if slot < 66:
                    target_row = slot // 11
                    if page_row == target_row:
                        start_col = (slot - target_row * 11) * 3
                        end_col = min(start_col + 3, COLS)
                        if start_col < end_col:
                            return bank_base + start_col, bank_base + end_col - 1
                return None
            odd_offset = (data[7] & 1) if n_bytes == 8 else 0
            start_cell = slot * 2 + odd_offset
            count = (n_bytes * 2) // 3
            return cls._cell_span_bounds(start_cell, count, page_row, bank_base)

        if CTRL_PAGE_MIN <= ctrl <= CTRL_PAGE_MAX:
            target_row = ctrl - CTRL_PAGE_MIN
            page_sel = (data[0] >> 5) & 0x07
            fb = full_bytes(n_bytes)
            count = (fb - 1) if fb > 1 else 0
            if page_row == target_row and page_sel <= 4 and count > 0:
                start_col = data[0] & 0x1F
                end_col = min(start_col + count, COLS)
                if start_col < end_col:
                    return bank_base + start_col, bank_base + end_col - 1
            return None

        return None

    def _apply_packet_pixel(
        self,
        px: int,
        py: int,
        prev: List[int],
        ctrl: int,
        data: Sequence[int],
        n_bytes: int,
        bank: int,
        row_page: int,
    ) -> List[int]:
        if ctrl == CTRL_NOP:
            return list(prev)

        if ctrl == CTRL_CLEAR_ALL:
            if py == META_ROW and px == GEOMETRY_PX:
                return list(prev)
            return [0, 0, 0, 0]

        if ctrl == CTRL_CLEAR_ROW:
            mask = (data[0] & 0x3F) | ((data[1] & 0x3F) << 6)
            if py < MAX_ROWS and ((mask >> py) & 1):
                return [0, 0, 0, 0]
            if py == META_ROW and px < MAX_ROWS and ((mask >> px) & 1):
                return [prev[0], prev[1], 0, prev[3]]
            return list(prev)

        if ctrl == CTRL_SET_VIEW:
            if py == META_ROW:
                new_top = data[0] & 0x0F
                new_status = (data[0] >> 4) & 0x0F
                target_row = (data[1] >> 4) & 0x0F
                color = data[1] & 0x0F
                flags = data[2] & 0x3F
                upd_status = (data[2] >> 7) & 1
                if px < MAX_ROWS and target_row < MAX_ROWS and px == target_row:
                    return [color, flags, prev[2], 255]
                if px == 30 and new_top < MAX_ROWS:
                    return [new_top, 0, 0, 255]
                if px == 31 and upd_status:
                    return [new_status, 0, 0, 255]
            return list(prev)

        if ctrl == CTRL_SET_GEOMETRY:
            if py == META_ROW and px == GEOMETRY_PX:
                geo_rows = data[0] & 0xFF
                geo_px8 = data[1] & 0xFF
                if MIN_ROWS <= geo_rows <= MAX_ROWS and MIN_ROW_PX8 <= geo_px8 <= MAX_ROW_PX8:
                    return [geo_rows, geo_px8, 0, 255]
            return list(prev)

        if py >= MAX_ROWS or (px >> 5) != bank:
            return list(prev)
        rp = 1 if py >= PAGE_ROWS else 0
        if rp != row_page:
            return list(prev)
        page_row = py - rp * PAGE_ROWS
        bank_col = px & 31
        cell = page_row * COLS + bank_col

        if CTRL_UTF16_MIN <= ctrl <= CTRL_UTF16_MAX:
            odd_offset = (data[2] & 1) if n_bytes == 3 else 0
            start_cell = (ctrl - CTRL_UTF16_MIN) * 2 + odd_offset
            count = n_bytes // 2
            if start_cell <= cell < start_cell + count and cell < TOTAL_CELLS:
                idx = cell - start_cell
                hi = data[idx * 2] & 0xFF
                lo = data[idx * 2 + 1] & 0xFF
                cp = (hi << 8) | lo
                return [hi, lo, 0, 255 if cp != 0 else 0]
            return list(prev)

        if CTRL_ASCII_MIN <= ctrl <= CTRL_ASCII_MAX:
            start_cell = (ctrl - CTRL_ASCII_MIN) * 4
            count = full_bytes(n_bytes)
            if start_cell <= cell < start_cell + count and cell < TOTAL_CELLS:
                lo = data[cell - start_cell] & 0xFF
                return [0, lo, 0, 255 if lo != 0 else 0]
            return list(prev)

        if (CTRL_LUT12_MIN <= ctrl <= CTRL_LUT12_MAX) or ctrl == CTRL_LUT12_LAST:
            slot = 95 if ctrl == CTRL_LUT12_LAST else (ctrl - CTRL_LUT12_MIN)
            matched = False
            idx = 0
            if n_bytes == COMPACT36_BYTES:
                if slot < 66:
                    target_row = slot // 11
                    start_col = (slot - target_row * 11) * 3
                    if page_row == target_row and start_col <= bank_col < start_col + 3:
                        matched = True
                        idx = bank_col - start_col
            else:
                odd_offset = (data[7] & 1) if n_bytes == 8 else 0
                start_cell = slot * 2 + odd_offset
                count = (n_bytes * 2) // 3
                if start_cell <= cell < start_cell + count and cell < TOTAL_CELLS:
                    matched = True
                    idx = cell - start_cell
            if matched:
                base_b = (idx >> 1) * 3
                if (idx & 1) == 0:
                    lut_idx = (data[base_b] << 4) | (data[base_b + 1] >> 4)
                else:
                    lut_idx = ((data[base_b + 1] & 0x0F) << 8) | data[base_b + 2]
                cp = af.decode_lut12(self.aux_rgba, lut_idx)
                return [(cp >> 8) & 0xFF, cp & 0xFF, 0, 255 if cp != 0 else 0]
            return list(prev)

        if CTRL_PAGE_MIN <= ctrl <= CTRL_PAGE_MAX:
            target_row = ctrl - CTRL_PAGE_MIN
            if page_row == target_row:
                start_col = data[0] & 0x1F
                page_sel = (data[0] >> 5) & 0x07
                fb = full_bytes(n_bytes)
                count = (fb - 1) if fb > 1 else 0
                if page_sel <= 4 and start_col <= bank_col < start_col + count:
                    page_hi = PAGE_SEL_TO_HI[page_sel]
                    lo = data[(bank_col - start_col) + 1] & 0xFF
                    if lo == 0:
                        return [0, 0, 0, 0]
                    return [page_hi, lo, 0, 255]
            return list(prev)

        return list(prev)

    @staticmethod
    def _cell_cp(cell_px: Sequence[int]) -> int:
        if cell_px[3] == 0:
            return 0
        return (cell_px[0] << 8) | cell_px[1]

    def _compute_span_delta(
        self,
        phys_row: int,
        min_col: int,
        max_col: int,
        ctrl: int,
        data: Sequence[int],
        n_bytes: int,
        bank: int,
        row_page: int,
    ) -> int:
        delta = 0
        for k in range(min_col, min(max_col + 1, WIDE_COLS)):
            old_cell = self.buffer[phys_row][k]
            new_cell = self._apply_packet_pixel(k, phys_row, old_cell, ctrl, data, n_bytes, bank, row_page)
            old_cp = self._cell_cp(old_cell)
            new_cp = self._cell_cp(new_cell)
            if new_cp != old_cp:
                old_adv = self.buffer[LAYOUT_ROW0 + phys_row][k][2]
                new_adv = sample_advance_hlsl(self.aux_rgba, new_cp)
                delta += int(new_adv) - int(old_adv)
        return delta

    def step(
        self,
        ctrl: int,
        data: Sequence[int],
        n_bytes: int,
        bank: int = 0,
        row_page: int = 0,
        local_clear: int = 0,
    ) -> bool:
        ctrl &= 0xFF
        bank &= 1
        row_page &= 1
        d = [int(x) & 0xFF for x in data] + [0] * (16 - len(data))
        if n_bytes == COMPACT36_BYTES:
            d[4] &= 0xF0

        if local_clear > 127:
            geometry = list(self.buffer[META_ROW][GEOMETRY_PX])
            self.reset()
            self.buffer[META_ROW][GEOMETRY_PX] = geometry
            self.active_frames += 1
            return True

        fp = [
            [ctrl, d[0], d[1], d[2]],
            [d[3], d[4], d[5], d[6]],
            [d[7], d[8], d[9], d[10]],
            [d[11], d[12], d[13], d[14]],
            [d[15], 0, bank, row_page],
        ]
        if self.buffer[CACHE_ROW][0:5] == fp:
            self.dedup_skips += 1
            return False

        self.active_frames += 1
        next_buf: List[List[List[int]]] = [
            [[0, 0, 0, 0] for _ in range(CRT_W)] for _ in range(CRT_H)
        ]
        for py in range(CRT_H):
            for px in range(CRT_W):
                if py == CACHE_ROW:
                    next_buf[py][px] = list(fp[px]) if px < 5 else [0, 0, 0, 0]
                elif py < LAYOUT_ROW0:
                    prev = self.buffer[py][px]
                    if py == META_ROW and px < MAX_ROWS:
                        bounds = self.packet_row_bounds(ctrl, d, n_bytes, bank, row_page, px)
                        if bounds is not None:
                            min_col, max_col = bounds
                            delta = self._compute_span_delta(px, min_col, max_col, ctrl, d, n_bytes, bank, row_page)
                            last_lay = self.buffer[LAYOUT_ROW0 + px][WIDE_COLS - 1]
                            old_total = ((last_lay[0] << 8) | last_lay[1]) + last_lay[2]
                            new_total = max(0, old_total + delta)
                            row_max_q = min(255, (new_total + 3) >> 2)
                            next_buf[py][px] = [prev[0], prev[1], row_max_q, prev[3]]
                            continue
                    next_buf[py][px] = self._apply_packet_pixel(px, py, prev, ctrl, d, n_bytes, bank, row_page)
                elif py < LAYOUT_ROW0 + MAX_ROWS:
                    phys = py - LAYOUT_ROW0
                    if ctrl == 0 or ctrl == 242 or ctrl >= 250:
                        next_buf[py][px] = list(self.buffer[py][px])
                    elif ctrl == CTRL_CLEAR_ALL:
                        next_buf[py][px] = [0, 0, 0, 255]
                    elif ctrl == CTRL_CLEAR_ROW:
                        mask = (d[0] & 0x3F) | ((d[1] & 0x3F) << 6)
                        next_buf[py][px] = [0, 0, 0, 255] if ((mask >> phys) & 1) else list(self.buffer[py][px])
                    else:
                        bounds = self.packet_row_bounds(ctrl, d, n_bytes, bank, row_page, phys)
                        prev_lay = self.buffer[py][px]
                        if bounds is None or px < bounds[0]:
                            next_buf[py][px] = list(prev_lay)
                        elif px <= bounds[1]:
                            min_col = bounds[0]
                            base_lay = self.buffer[py][min_col]
                            x_start = (base_lay[0] << 8) | base_lay[1]
                            for k in range(min_col, px):
                                cell_k = self._apply_packet_pixel(k, phys, self.buffer[phys][k], ctrl, d, n_bytes, bank, row_page)
                                x_start += sample_advance_hlsl(self.aux_rgba, self._cell_cp(cell_k))
                            own_cell = self._apply_packet_pixel(px, phys, self.buffer[phys][px], ctrl, d, n_bytes, bank, row_page)
                            own_adv = sample_advance_hlsl(self.aux_rgba, self._cell_cp(own_cell))
                            next_buf[py][px] = [(x_start >> 8) & 0xFF, x_start & 0xFF, own_adv & 0xFF, 255]
                        else:
                            min_col, max_col = bounds
                            delta = self._compute_span_delta(phys, min_col, max_col, ctrl, d, n_bytes, bank, row_page)
                            if delta == 0:
                                next_buf[py][px] = list(prev_lay)
                            else:
                                old_x = (prev_lay[0] << 8) | prev_lay[1]
                                new_x = max(0, old_x + delta)
                                next_buf[py][px] = [(new_x >> 8) & 0xFF, new_x & 0xFF, prev_lay[2], 255]
                else:
                    next_buf[py][px] = [0, 0, 0, 0]
        self.buffer = next_buf
        return True

    def apply_packet(self, packet: Packet, tier: str, frames: int = 15, force_raw_data: Optional[Sequence[int]] = None) -> None:
        n_bytes = TIERS[tier]
        raw = tuple(force_raw_data) if force_raw_data is not None else mask_payload(packet.padded_data(n_bytes), n_bytes)
        ctrl = packet.ctrl & 0xFF
        bank = packet.bank & 1
        row_page = packet.row_page & 1
        skips_before = self.dedup_skips
        snapshot = None
        for idx in range(max(1, frames)):
            ran = self.step(ctrl, raw, n_bytes, bank, row_page)
            if idx == 0:
                snapshot = [[list(px) for px in r] for r in self.buffer]
            else:
                assert not ran, f"Repeat frame {idx} for ctrl={ctrl} failed samePacket dedup"
                assert self.buffer == snapshot
        if frames > 1:
            assert self.dedup_skips >= skips_before + (frames - 1)

    def read_top_row(self) -> int:
        top = self.buffer[META_ROW][30][0]
        return top if top < self.rows else 0

    def read_geometry(self) -> Optional[Tuple[int, int]]:
        r, g, _, a = self.buffer[META_ROW][GEOMETRY_PX]
        if a == 0 or not (MIN_ROWS <= r <= MAX_ROWS) or not (MIN_ROW_PX8 <= g <= MAX_ROW_PX8):
            return None
        return r, g * 8

    def display_glyph_at(self, visual_row: int, pen_x: int) -> Tuple[int, int]:
        phys = (visual_row + self.read_top_row()) % self.rows
        row_max_px = self.buffer[META_ROW][phys][2] * 4
        if pen_x >= row_max_px:
            self.early_out_skips += 1
            return 0, 0
        self.binary_searches += 1
        layout = self.buffer[LAYOUT_ROW0 + phys]
        lo, hi = 0, WIDE_COLS - 1
        x, adv = 0, 0
        for _ in range(6):
            mid = (lo + hi + 1) >> 1
            m = layout[mid]
            mid_x, mid_adv = (m[0] << 8) | m[1], m[2]
            if mid_x <= pen_x:
                lo = mid
                x, adv = mid_x, mid_adv
            else:
                hi = mid - 1
                if mid == 1:
                    adv = mid_x
        if not (x <= pen_x < x + adv):
            return 0, 0
        cp = self._cell_cp(self.buffer[phys][lo])
        return cp, pen_x - x


def _sample_tofu_bit(gx: int, gy: int) -> int:
    h_edge = (gy == 3 or gy == 13) and 1 <= gx <= 10
    v_edge = (gx == 1 or gx == 10) and 3 <= gy <= 13
    return 1 if (h_edge or v_edge) else 0


def _glyph_bit_index(ref: Tuple[int, int], gx: int, gy: int) -> Optional[int]:
    cls, bit_base = ref
    if gx < 0 or gy < 0 or gx >= 16 or gy >= 16:
        return None
    if cls == af.CLASS_SMALL:
        if gx >= af.SMALL_COLS or gy < af.SMALL_ROW0 or gy > 13:
            return None
        return bit_base + (gy - af.SMALL_ROW0) * af.SMALL_COLS + gx
    if cls == af.CLASS_LARGE:
        if gx >= af.LARGE_COLS:
            return None
        return bit_base + gy * af.LARGE_COLS + gx
    return None


def _sample_glyph_and_shadow_hlsl(
    atlas: np.ndarray,
    aux: np.ndarray,
    cp: int,
    gx: int,
    gy: int,
    sample_shadow: bool = True,
) -> Tuple[int, int, bool, bool]:
    """Mirror UniTextSampleGlyphAndShadow; returns (glyph, shadow, pruned_shadow, reused_texel)."""
    if (cp & 0xFFFF) == 0:
        return 0, 0, False, False
    ref = af.glyph_ref(aux, cp)
    cls, _ = ref
    if cls == af.CLASS_ABSENT:
        if gx < 16 and gy < 16:
            glyph = _sample_tofu_bit(gx, gy)
            shadow = 0
            pruned = False
            if sample_shadow and gx > 0 and gy > 0:
                if glyph == 0:
                    shadow = _sample_tofu_bit(gx - 1, gy - 1)
                else:
                    pruned = True
            return glyph, shadow, pruned, False
        return 0, 0, False, False

    height = atlas.shape[0]
    fg_bit = _glyph_bit_index(ref, gx, gy)
    fg_texel: Optional[int] = None
    fg_px: Optional[np.ndarray] = None
    glyph = 0
    if fg_bit is not None:
        fg_texel = fg_bit >> 5
        fg_px = atlas[fg_texel >> 10, fg_texel & 1023]
        glyph = (int(fg_px[(fg_bit >> 3) & 3]) >> (7 - (fg_bit & 7))) & 1

    shadow = 0
    pruned = False
    reused = False
    if sample_shadow and gx > 0 and gy > 0:
        if glyph != 0:
            pruned = True
        else:
            sh_bit = _glyph_bit_index(ref, gx - 1, gy - 1)
            if sh_bit is not None:
                sh_texel = sh_bit >> 5
                if fg_texel is not None and sh_texel == fg_texel and fg_px is not None:
                    sh_px = fg_px
                    reused = True
                else:
                    sh_px = atlas[sh_texel >> 10, sh_texel & 1023]
                shadow = (int(sh_px[(sh_bit >> 3) & 3]) >> (7 - (sh_bit & 7))) & 1
    return glyph, shadow, pruned, reused


def _assert_crt_equivalence(opt: _OptimizedCRTSimulator, base: CRTBufferSimulator, rows: int, label: str) -> None:
    assert opt.read_top_row() == base.read_top_row(), f"{label}: topRow mismatch"
    assert opt.buffer[META_ROW][31][0] == base.read_status(), f"{label}: status mismatch"
    assert opt.read_geometry() == base.read_geometry(), f"{label}: geometry mismatch"

    for phys in range(MAX_ROWS):
        base_meta = base.buffer[META_ROW][phys]
        opt_meta = opt.buffer[META_ROW][phys]
        assert (opt_meta[0] & 0x0F) == (base_meta[0] & 0x0F), f"{label} phys={phys}: color mismatch"
        assert opt_meta[1] == base_meta[1], f"{label} phys={phys}: flags mismatch"

        base_lay = base.read_layout(phys)
        opt_lay = [((p[0] << 8) | p[1], p[2]) for p in opt.buffer[LAYOUT_ROW0 + phys]]
        assert opt_lay == base_lay, f"{label} phys={phys}: layout mismatch"

        last_x, last_adv = base_lay[-1]
        total_px = last_x + last_adv
        exp_q = min(255, (total_px + 3) >> 2)
        assert opt_meta[2] == exp_q, f"{label} phys={phys}: cached rowMaxQ {opt_meta[2]} != {exp_q}"
        assert (opt_meta[2] * 4) >= total_px and ((total_px == 0) == (opt_meta[2] == 0))

        for col in range(WIDE_COLS):
            bc = base.buffer[phys][col]
            oc = opt.buffer[phys][col]
            bcp = ((bc[0] << 8) | bc[1]) if bc[3] != 0 else 0
            ocp = ((oc[0] << 8) | oc[1]) if oc[3] != 0 else 0
            assert ocp == bcp, f"{label} phys={phys} col={col}: cell mismatch {ocp} != {bcp}"

    for vr in range(rows):
        for pen_x in range(ROW_PX):
            assert opt.display_glyph_at(vr, pen_x) == base.glyph_at(vr, pen_x), (
                f"{label} vr={vr} pen_x={pen_x}: Display lookup mismatch"
            )


def _verify_optimized_pipeline(atlas_rgba: np.ndarray, aux_rgba: np.ndarray) -> None:
    # 1. Cross-check all test vectors against CRTBufferSimulator
    vec_doc = json.loads(VECTORS_PATH.read_text(encoding="utf-8"))
    for vec in vec_doc["vectors"]:
        rows = int(vec.get("rows", 6))
        cols = int(vec.get("row_cells", WIDE_COLS))
        tier = vec["tier"]
        init_raw = vec.get("initial_state", {})
        init_scr = VirtualScreen(cols, rows)
        init_scr.top_row = int(init_raw.get("top_row", 0)) % rows
        for r_str, txt in init_raw.get("rows", {}).items():
            init_scr.set_row_text(int(r_str), txt)
        for sp in init_raw.get("sparse_cells", []):
            init_scr.set_cell(int(sp["row"]), int(sp["col"]), int(sp["cp"]))
        for st in init_raw.get("row_styles", []):
            init_scr.set_row_style(int(st["row"]), int(st["color"]), int(st["flags"]))
        if "status" in init_raw:
            init_scr.set_status(int(init_raw["status"]))
        init_scr.mark_clean()

        base = CRTBufferSimulator(rows=rows)
        base.load_from_screen(init_scr)
        opt = _OptimizedCRTSimulator(aux_rgba, rows=rows)
        opt.load_from_screen(init_scr)

        for p_idx, p_raw in enumerate(vec["packets"]):
            pkt = Packet(
                int(p_raw["ctrl"]),
                tuple(int(x) for x in p_raw["data"]),
                int(p_raw.get("bank", 0)),
                int(p_raw.get("row_page", 0)),
            )
            base.apply_packet(pkt, tier=tier, frames=15)
            opt.apply_packet(pkt, tier=tier, frames=15)
            _assert_crt_equivalence(opt, base, rows, f"vec:{vec['id']}/pkt-{p_idx}")
        assert opt.early_out_skips > 0

    # 1b. Cross-check 5-tier 6-row wraparound + 6-step scroll and 8/12-row resize sequences
    scroll_lines = [
        "iW Proportional Latin packs ~60+ English chars per subtitle row!",
        "中文 32 全角：实时翻译字幕测试完成，保持完整容量与清晰像素！",
        "Alice: 今天的 VRChat meetup 我们讨论 proportional fonts",
        "日本語テスト：こんにちは世界！Mixed English & カタカナ OK",
        "한국어 테스트: 안녕하세요! Proportional Latin saves space.",
        "Status: Translating [64x32 CRT | 384px row | 1024x170 Atlas]",
    ]
    for tier in ("lite", "compact", "balanced", "standard", "turbo"):
        scr = VirtualScreen(WIDE_COLS, 6)
        base = CRTBufferSimulator(rows=6)
        opt = _OptimizedCRTSimulator(aux_rgba, rows=6)
        for r, txt in enumerate(scroll_lines):
            scr.set_row_text(r, txt)
            scr.set_row_style(r, color=(r + 2) % 16, flags=r % 3)
        for pkt in encode_screen_packets(scr, tier=tier, dirty_only=False):
            base.apply_packet(pkt, tier=tier, frames=3)
            opt.apply_packet(pkt, tier=tier, frames=3)
        scr.mark_clean()
        _assert_crt_equivalence(opt, base, 6, f"opt-5tier:{tier}/init")
        for step in range(6):
            scr.scroll_up(1)
            scr.set_row_text(5, scroll_lines[step])
            scr.set_row_style(5, color=(step + 5) % 16, flags=step % 3)
            scr.set_status(1 if (step % 2 == 0) else 2)
            for pkt in encode_screen_packets(scr, tier=tier, dirty_only=True):
                base.apply_packet(pkt, tier=tier, frames=3)
                opt.apply_packet(pkt, tier=tier, frames=3)
            scr.mark_clean()
            _assert_crt_equivalence(opt, base, 6, f"opt-5tier:{tier}/scroll-{step}")

    for test_rows, test_row_px, tier in ((8, 256, "lite"), (12, 384, "balanced")):
        scr = VirtualScreen(WIDE_COLS, test_rows)
        base = CRTBufferSimulator(rows=test_rows)
        opt = _OptimizedCRTSimulator(aux_rgba, rows=test_rows)
        for r in range(test_rows):
            scr.set_row_text(r, wrap_text(scroll_lines[r % len(scroll_lines)], max_px=test_row_px, max_cells=WIDE_COLS)[0])
            scr.set_row_style(r, color=(r + 1) % 16, flags=r % 3)
        for pkt in encode_screen_packets(scr, tier=tier, dirty_only=False):
            base.apply_packet(pkt, tier=tier, frames=2)
            opt.apply_packet(pkt, tier=tier, frames=2)
        scr.mark_clean()
        _assert_crt_equivalence(opt, base, test_rows, f"opt-ext:{test_rows}r/init")
        for step in range(3):
            scr.scroll_up(1)
            scr.set_row_text(test_rows - 1, f"Ext row step {step}: {test_rows}r OK!")
            for pkt in encode_screen_packets(scr, tier=tier, dirty_only=True):
                base.apply_packet(pkt, tier=tier, frames=2)
                opt.apply_packet(pkt, tier=tier, frames=2)
            scr.mark_clean()
            _assert_crt_equivalence(opt, base, test_rows, f"opt-ext:{test_rows}r/scroll-{step}")

    # 2. Cross-check edge cases: negative delta overwrite, CMD_SET_VIEW style preservation, CLEAR_ROW, CLEAR_ALL, LocalClear, Compact36 D4 noise
    edge_base = CRTBufferSimulator(rows=6)
    edge_opt = _OptimizedCRTSimulator(aux_rgba, rows=6)
    scr_long = VirtualScreen(WIDE_COLS, 6)
    scr_long.set_row_text(0, "Long initial row: 0123456789 ABCDEFGHIJKLMNOPQRSTUVWXYZ")
    scr_long.set_row_text(1, "Second row stays intact!")
    scr_long.set_row_style(0, color=5, flags=2)
    for pkt in encode_screen_packets(scr_long, tier="standard", dirty_only=False):
        edge_base.apply_packet(pkt, tier="standard", frames=4)
        edge_opt.apply_packet(pkt, tier="standard", frames=4)
    scr_long.mark_clean()
    _assert_crt_equivalence(edge_opt, edge_base, 6, "edge/long-init")

    # CMD_SET_VIEW style-only update on non-empty row must preserve rowMaxQ
    scr_long.set_row_style(0, color=9, flags=3)
    for pkt in encode_screen_packets(scr_long, tier="standard", dirty_only=True):
        edge_base.apply_packet(pkt, tier="standard", frames=4)
        edge_opt.apply_packet(pkt, tier="standard", frames=4)
    scr_long.mark_clean()
    _assert_crt_equivalence(edge_opt, edge_base, 6, "edge/style-preserve-rowmax")

    # Negative-delta overwrite of col 0..7 on row 0 ("Long ini" -> "Hi!")
    neg_pkt = Packet(CTRL_ASCII_MIN + 0, (ord("H"), ord("i"), ord("!"), 0, 0, 0, 0, 0), 0, 0)
    edge_base.apply_packet(neg_pkt, tier="standard", frames=4)
    edge_opt.apply_packet(neg_pkt, tier="standard", frames=4)
    _assert_crt_equivalence(edge_opt, edge_base, 6, "edge/negative-delta")

    # CMD_CLEAR_ROW on row 0 resets (0, 12).b and layout row 16 while preserving row 1
    clr_row_pkt = Packet(CTRL_CLEAR_ROW, (0x01, 0, 0, 0, 0, 0, 0, 0), 0, 0)
    edge_base.apply_packet(clr_row_pkt, tier="standard", frames=4)
    edge_opt.apply_packet(clr_row_pkt, tier="standard", frames=4)
    assert edge_opt.buffer[META_ROW][0][2] == 0
    _assert_crt_equivalence(edge_opt, edge_base, 6, "edge/clear-row-0")

    # CMD_CLEAR_ALL followed by resending neg_pkt must NOT hit stale dedup
    clr_all = Packet(CTRL_CLEAR_ALL, (0, 0, 0, 0, 0, 0, 0, 0), 0, 0)
    edge_base.apply_packet(clr_all, tier="standard", frames=3)
    edge_opt.apply_packet(clr_all, tier="standard", frames=3)
    edge_base.apply_packet(neg_pkt, tier="standard", frames=3)
    edge_opt.apply_packet(neg_pkt, tier="standard", frames=3)
    _assert_crt_equivalence(edge_opt, edge_base, 6, "edge/clear-all-resend")

    # LocalClear preserves CMD_SET_GEOMETRY and wipes row-13 cache so re-presenting neg_pkt applies again
    geo_pkt = encode_geometry_packet(TIERS["standard"], 8, 320)
    edge_base.apply_packet(geo_pkt, tier="standard", frames=3)
    edge_opt.apply_packet(geo_pkt, tier="standard", frames=3)
    edge_base.local_clear()
    edge_opt.step(0, [0] * 16, TIERS["standard"], local_clear=255)
    _assert_crt_equivalence(edge_opt, edge_base, 6, "edge/local-clear")
    assert edge_opt.buffer[CACHE_ROW][0:5] == [[0, 0, 0, 0] for _ in range(5)]
    edge_base.apply_packet(neg_pkt, tier="standard", frames=3)
    edge_opt.apply_packet(neg_pkt, tier="standard", frames=3)
    _assert_crt_equivalence(edge_opt, edge_base, 6, "edge/post-local-clear-reapply")

    # Compact36 low-nibble noise on D4 must still hit samePacket dedup
    c36_opt = _OptimizedCRTSimulator(aux_rgba, rows=6)
    c36_base = CRTBufferSimulator(rows=6)
    c36_pkt = Packet(CTRL_LUT12_MIN + 0, (0x12, 0x34, 0x56, 0x78, 0x90), 0, 0)
    c36_base.apply_packet(c36_pkt, tier="compact", frames=2)
    assert c36_opt.step(c36_pkt.ctrl, c36_pkt.data, TIERS["compact"], 0, 0) is True
    assert c36_opt.step(c36_pkt.ctrl, (0x12, 0x34, 0x56, 0x78, 0x9F), TIERS["compact"], 0, 0) is False
    _assert_crt_equivalence(c36_opt, c36_base, 6, "edge/compact36-d4-noise-dedup")

    # 3. Verify UniTextSampleGlyphAndShadow against brute-force glyph + (gx-1, gy-1) shadow sampling across all 3 block classes
    flat_aux = aux_rgba.reshape(-1, 4)
    sample_cps = [ord("A"), ord("i"), ord("g"), ord("中"), ord("あ"), ord("한")]
    for target_cls in (af.CLASS_ABSENT, af.CLASS_SMALL, af.CLASS_LARGE):
        found = next(
            (
                b * 64 + 1
                for b in range(af.NUM_BLOCKS)
                if (((int(flat_aux[b][2]) >> 2) & 3) == target_cls) and (b * 64 + 1) != 0
            ),
            None,
        )
        assert found is not None, f"Could not find block class {target_cls} in aux"
        if found not in sample_cps:
            sample_cps.append(found)

    prune_hits = 0
    reuse_hits = 0
    for cp in sample_cps:
        glyph_grid = sample_glyph_hlsl(atlas_rgba, aux_rgba, cp)
        for gy in range(16):
            for gx in range(16):
                opt_g, opt_s, pruned, reused = _sample_glyph_and_shadow_hlsl(
                    atlas_rgba, aux_rgba, cp, gx, gy, sample_shadow=True
                )
                prune_hits += int(pruned)
                reuse_hits += int(reused)
                ref_g = int(glyph_grid[gy, gx])
                ref_s = int(glyph_grid[gy - 1, gx - 1]) if (gx > 0 and gy > 0 and ref_g == 0) else 0
                assert opt_g == ref_g and opt_s == ref_s, (
                    f"GlyphAndShadow mismatch cp=U+{cp:04X} ({gx},{gy}): ({opt_g},{opt_s}) != ({ref_g},{ref_s})"
                )
    assert prune_hits > 0 and reuse_hits > 0, (
        f"Expected both shadow-prune and same-texel reuse hits, got prune={prune_hits}, reuse={reuse_hits}"
    )


def test_osc_tester_cli() -> None:
    print("[3/6] Verifying OSCQuery auto-detection and send_demo.py CLI / dry-run modes...")

    def make_tree(param_names: Sequence[str], nested_root: bool = False) -> dict:
        params_contents = {
            name: {"FULL_PATH": f"/avatar/parameters/{name}"} for name in param_names
        }
        if nested_root:
            return {
                "FULL_PATH": "/",
                "CONTENTS": {
                    "avatar": {
                        "FULL_PATH": "/avatar",
                        "CONTENTS": {
                            "parameters": {
                                "FULL_PATH": "/avatar/parameters",
                                "CONTENTS": params_contents,
                            }
                        },
                    }
                },
            }
        return {"FULL_PATH": "/avatar/parameters", "CONTENTS": params_contents}

    cases = [
        (["UniText_Ctrl", "UniText_D0", "UniText_D2", "UniText_D3", "UniText_D5", "UniText_D7", "UniText_D15", "UniText_Bank", "UniText_RowPage"], False, "turbo", 64),
        (["UniText_Ctrl", "UniText_D0", "UniText_D7", "UniText_Bank"], False, "standard", 64),
        (["UniText_Ctrl", "UniText_D0", "UniText_D5"], False, "balanced", 32),
        (["UniText_Ctrl", "UniText_D0", "UniText_D3", "UniText_D4b4", "UniText_D4b5", "UniText_D4b6", "UniText_D4b7", "UniText_ProtoVer"], True, "compact", 32),
        (["UniText_Ctrl", "UniText_D0", "UniText_D2", "UniText_Bank"], False, "lite", 64),
        (["VRCEmote", "FaceTracking_JawOpen"], True, None, 32),
    ]
    for param_list, nested, expected_tier, expected_cols in cases:
        tree = make_tree(param_list, nested_root=nested)
        assert detect_tier_from_oscquery_tree(tree) == expected_tier
        assert detect_viewport_from_oscquery_tree(tree)[0] == expected_cols

    vp_tree = {
        "FULL_PATH": "/avatar/parameters",
        "CONTENTS": {
            "UniText_ProtoVer": {"FULL_PATH": "/avatar/parameters/UniText_ProtoVer", "VALUE": [2]},
            "UniText_Bank": {"FULL_PATH": "/avatar/parameters/UniText_Bank", "VALUE": [False]},
            "UniText_Rows": {"FULL_PATH": "/avatar/parameters/UniText_Rows", "VALUE": [8]},
            "UniText_RowPx8": {"FULL_PATH": "/avatar/parameters/UniText_RowPx8", "VALUE": [32]},
            "UniText_RowPage": {"FULL_PATH": "/avatar/parameters/UniText_RowPage", "VALUE": [False]},
        },
    }
    assert detect_viewport_from_oscquery_tree(vp_tree) == (64, 8, 256, True)

    # Live Resize (W/H) viewport mapping across (-1, 0, +1) corners + 8-px rounding + narrow 384px cap
    assert resolve_live_viewport(6, 384, 0.0, 0.0, wide_rows=True) == (6, 384)
    assert resolve_live_viewport(6, 384, -1.0, -1.0, wide_rows=True) == (4, 192)
    assert resolve_live_viewport(6, 384, 1.0, 1.0, wide_rows=True) == (12, 576)
    assert resolve_live_viewport(6, 384, 1.0, 1.0, wide_rows=False) == (12, 384)
    assert resolve_live_viewport(6, 384, 0.1, 0.0, wide_rows=True)[1] % 8 == 0
    vp_live = {
        "FULL_PATH": "/avatar/parameters",
        "CONTENTS": {
            "UniText_Bank": {"FULL_PATH": "/avatar/parameters/UniText_Bank", "VALUE": [False]},
            "UniText_Rows": {"FULL_PATH": "/avatar/parameters/UniText_Rows", "VALUE": [6]},
            "UniText_RowPx8": {"FULL_PATH": "/avatar/parameters/UniText_RowPx8", "VALUE": [48]},
            "UniText_Width": {"FULL_PATH": "/avatar/parameters/UniText_Width", "VALUE": [1.0]},
            "UniText_Height": {"FULL_PATH": "/avatar/parameters/UniText_Height", "VALUE": [1.0]},
            "UniText_RowPage": {"FULL_PATH": "/avatar/parameters/UniText_RowPage", "VALUE": [False]},
        },
    }
    assert detect_viewport_from_oscquery_tree(vp_live) == (64, 12, 576, True)

    send_demo = str(REPO_ROOT / "tools" / "osc-tester" / "send_demo.py")
    subprocess.run([sys.executable, send_demo, "--verify-vectors"], check=True, stdout=subprocess.DEVNULL)

    for dry_args in (
        ["--dry-run", "--demo", "long", "--tier", "balanced", "--rows", "8", "--row-px", "256"],
        ["--dry-run", "--demo", "long", "--tier", "compact"],
        ["--dry-run", "--demo", "scroll", "--tier", "lite"],
        ["--dry-run", "--demo", "multilingual", "--tier", "standard", "--row-cells", "32"],
        ["--dry-run", "--demo", "stress", "--tier", "turbo"],
    ):
        subprocess.run([sys.executable, send_demo, *dry_args], check=True, stdout=subprocess.DEVNULL)

    bad = subprocess.run(
        [sys.executable, send_demo, "--dry-run", "--rows", "2"],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    assert bad.returncode != 0, "Expected --rows 2 (below min 4) to fail cleanly"


def _check_balanced_braces(text: str, filename: str) -> None:
    depth = 0
    for ch in text:
        if ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
            assert depth >= 0, f"{filename}: unexpected closing brace '}}'"
    assert depth == 0, f"{filename}: unbalanced braces (depth={depth})"


def test_shaders_static() -> None:
    print("[4/6] Running static shader integrity checks...")
    shaders_dir = PKG_DIR / "Shaders"
    common = (shaders_dir / "UniTextCommon.cginc").read_text(encoding="utf-8")
    param_enc = (shaders_dir / "ParamEncoder.shader").read_text(encoding="utf-8")
    buffer_crt = (shaders_dir / "BufferCRT.shader").read_text(encoding="utf-8")
    display = (shaders_dir / "Display.shader").read_text(encoding="utf-8")

    for name, src in [
        ("ParamEncoder.shader", param_enc),
        ("BufferCRT.shader", buffer_crt),
        ("Display.shader", display),
        ("UniTextCommon.cginc", common),
    ]:
        raw = (shaders_dir / name).read_bytes()
        assert not raw.startswith(b"\xef\xbb\xbf"), f"{name} must not contain a UTF-8 BOM"
        _check_balanced_braces(src, name)
        if name.endswith(".shader"):
            assert src.count("CGPROGRAM") == 1 and src.count("ENDCG") == 1, f"{name}: CGPROGRAM/ENDCG mismatch"

    for sym in [
        "UniTextToByte",
        "UniTextGetPaletteColor",
        "UniTextSampleCell",
        "UniTextSampleRowStyle",
        "out uint rowMaxPx",
        "UniTextSampleStatus",
        "UniTextSampleTopRow",
        "UniTextSampleLayout",
        "UniTextSampleGeometry",
        "UniTextAuxFetch",
        "UniTextSampleAdvance",
        "UniTextLookupLut12",
        "UniTextGetGlyphRef",
        "UniTextSampleTofuBit",
        "UniTextGlyphBitIndex",
        "UniTextFetchAtlasTexel",
        "UniTextExtractAtlasBit",
        "UniTextSampleGlyphBit",
        "UniTextSampleGlyphAndShadow",
        "UNITEXT_CRT_W",
        "UNITEXT_CRT_H",
        "UNITEXT_MAX_ROWS",
        "UNITEXT_CACHE_ROW",
        "UNITEXT_GEOMETRY_PX",
        "UNITEXT_SMALL_BITS",
        "UNITEXT_LARGE_BITS",
    ]:
        assert sym in common, f"UniTextCommon.cginc missing {sym}"
    assert "UNITEXT_ROWS" not in common, "UniTextCommon.cginc still contains legacy UNITEXT_ROWS"

    assert "_ScreenParams.x - 8.0" in param_enc and "_ScreenParams.y - 1.0" in param_enc
    for tok in ("_Bank", "_RowPage", "_D4b4", "_D4b5", "_D4b6", "_D4b7"):
        assert tok in param_enc, f"ParamEncoder.shader missing {tok}"

    for req in [
        "Cull Off",
        "ZWrite Off",
        "ZTest Always",
        "float _Tier",
        "_TIER_LITE",
        "_TIER_COMPACT",
        "_TIER_BALANCED",
        "_TIER_STANDARD",
        "_TIER_TURBO",
        "_AuxTex",
        "_Bank",
        "_RowPage",
        "UniTextSampleAdvance",
        "UniTextFullBytes",
        "UniTextPacketEqual",
        "UniTextCellSpanBounds",
        "UniTextPacketRowBounds",
        "UniTextComputeSpanDelta",
        "UNITEXT_CACHE_ROW",
        "samePacket",
        "rowMaxQ",
        "prev.b",
        "slot / 11u",
        "UNITEXT_LAYOUT_ROW0",
        "ctrl == 0u",
        "py < UNITEXT_MAX_ROWS",
        "cell < 192u",
        "ctrl == 240u",
        "ctrl == 241u",
        "ctrl == 243u",
        "ctrl >= 244u && ctrl <= 249u",
        "ctrl == 250u",
        "ctrl == 251u",
        "_SelfTexture2D",
    ]:
        assert req in buffer_crt, f"BufferCRT.shader missing {req}"
    assert "for (uint k = 0u; k < UNITEXT_COLS; k++)" not in buffer_crt, (
        "BufferCRT.shader still contains O(64) layout loop"
    )
    assert "_LutTex" not in buffer_crt and "_AdvTex" not in buffer_crt, (
        "BufferCRT.shader still references legacy _LutTex / _AdvTex"
    )
    assert "if (ctrl == 242u)" not in buffer_crt and "if (ctrl == 252u)" not in buffer_crt, (
        "BufferCRT.shader still contains reserved ctrl 242 / 252 branches"
    )

    for disp_req in [
        "nointerpolation float4 geo",
        "UniTextSampleTopRow",
        "UniTextSampleStatus",
        "UniTextSampleRowStyle",
        "penX < rowMaxPx",
        "UniTextSampleLayout",
        "UniTextSampleGeometry",
        "UniTextGetGlyphRef",
        "UniTextSampleGlyphAndShadow",
        "_AuxTex",
        "_LiveGeometry",
        "_FontAtlas_TexelSize",
        "panelAspect",
        "_DisplayRows",
        "_RowPx",
        "physRow",
    ]:
        assert disp_req in display, f"Display.shader missing '{disp_req}'"

    vert_pos = display.index("v2f vert(appdata v)")
    frag_pos = display.index("fixed4 frag(v2f i)")
    vert_body = display[vert_pos:frag_pos]
    frag_body = display[frag_pos:]
    assert "UniTextSampleTopRow(" in vert_body and "UniTextSampleStatus(" in vert_body, (
        "Display.shader must cache topRow and status in vert"
    )
    assert "UniTextSampleTopRow(" not in frag_body and "UniTextSampleStatus(" not in frag_body, (
        "Display.shader must not re-sample topRow or status inside frag"
    )

    for macro in [
        "UNITY_VERTEX_INPUT_INSTANCE_ID",
        "UNITY_VERTEX_OUTPUT_STEREO",
        "UNITY_SETUP_INSTANCE_ID",
        "UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO",
        "UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX",
        "USING_STEREO_MATRICES",
    ]:
        assert macro in display, f"Display.shader missing VR SPS-I macro {macro}"


def test_unity_package_integrity() -> None:
    print("[5/6] Verifying Unity VPM package manifests, .meta GUIDs, CRT settings, NDMF plugin, and prefabs...")
    manifest = json.loads((PKG_DIR / "package.json").read_text(encoding="utf-8"))
    assert manifest["name"] == "dev.dreaminko.unitext"
    assert re.match(r"^\d+\.\d+\.\d+", manifest["version"])
    deps = manifest.get("vpmDependencies", {})
    assert "com.vrchat.avatars" in deps and "nadena.dev.modular-avatar" in deps and "nadena.dev.ndmf" in deps

    guid_map: Dict[str, Path] = {}
    for item in sorted(PKG_DIR.rglob("*")):
        if item.name.endswith(".meta"):
            continue
        meta_path = Path(f"{item}.meta")
        assert meta_path.is_file(), f"Missing .meta for {item.relative_to(PKG_DIR)}"
        meta_txt = meta_path.read_text(encoding="utf-8")
        m = re.search(r"^guid:\s*([0-9a-f]{32})\s*$", meta_txt, flags=re.MULTILINE)
        assert m, f"Invalid or missing GUID in {meta_path.relative_to(PKG_DIR)}"
        guid = m.group(1)
        assert guid not in guid_map, f"Duplicate GUID {guid}: {meta_path} vs {guid_map[guid]}"
        guid_map[guid] = item

    for tex_meta_name in ("FontAtlas_BMP_Packed.png.meta", "UniTextAux_64x64.png.meta"):
        tex_meta = (PKG_DIR / "Textures" / tex_meta_name).read_text(encoding="utf-8")
        for expected_line in [
            "sRGBTexture: 0",
            "enableMipMap: 0",
            "alphaIsTransparency: 0",
            "nPOTScale: 0",
            "filterMode: 0",
            "textureCompression: 0",
            "textureFormat: 4",
        ]:
            assert expected_line in tex_meta, f"{tex_meta_name} missing '{expected_line}'"

    known_guids: Set[str] = set(guid_map.keys())
    for tier in ("Lite", "Compact", "Balanced", "Standard", "Turbo"):
        crt_txt = (PKG_DIR / "Textures" / f"Buffer_64x32_{tier}.asset").read_text(encoding="utf-8")
        for expected in [
            "m_Width: 64",
            "m_Height: 32",
            "m_ColorFormat: 8",
            "m_DoubleBuffered: 1",
            "m_SRGB: 0",
            "m_ShaderPass: 0",
            "m_InitSource: 0",
            "m_InitColor: {r: 0, g: 0, b: 0, a: 0}",
            "m_InitializationMode: 0",
            "m_UpdateMode: 1",
            "m_UpdateZones: []",
        ]:
            assert expected in crt_txt, f"Buffer_64x32_{tier}.asset missing '{expected}'"
        for ref_guid in re.findall(r"guid:\s*([0-9a-f]{32})", crt_txt):
            assert ref_guid in known_guids, f"Broken GUID {ref_guid} in Buffer_64x32_{tier}.asset"

        rt_txt = (PKG_DIR / "Textures" / f"ParamBridge_8x1_{tier}.renderTexture").read_text(encoding="utf-8")
        for expected in ["m_Width: 8", "m_Height: 1", "m_ColorFormat: 8", "m_SRGB: 0"]:
            assert expected in rt_txt, f"ParamBridge_8x1_{tier}.renderTexture missing '{expected}'"

    builder_src = (PKG_DIR / "Editor" / "UniTextAssetBuilder.cs").read_text(encoding="utf-8")
    assert 'shaderPass = "' not in builder_src
    for tok in (
        "ConfigureAuxImporter",
        "_TIER_LITE",
        "_TIER_BALANCED",
        "UniText_Bank",
        "UniText_RowPage",
        "UniText_D4b",
        "FontAtlas_BMP_Packed.png",
        "UniTextAux_64x64.png",
        "_LiveGeometry",
        "Board_Compact_45b.prefab",
        "Buffer_64x32_",
        "ParamBridgeLayer = 9",
        "ParamCameraCullingMask = (1 << 9) | (1 << 10)",
        "CreateVisibilityClip",
        "RenderingPath.Forward",
        "DepthTextureMode.None",
        "allowDynamicResolution = false",
        "encoderQuad.layer = ParamBridgeLayer",
    ):
        assert tok in builder_src, f"UniTextAssetBuilder.cs missing '{tok}'"
    for legacy_tok in ("UniText_V11", "UniText_V12", "UniText_V13", "UniText_ParamCamera", "BuildConstantLayer"):
        assert legacy_tok not in builder_src, f"UniTextAssetBuilder.cs still contains legacy '{legacy_tok}'"

    prop_src = (PKG_DIR / "Runtime" / "UniTextProp.cs").read_text(encoding="utf-8")
    for prop_tok in (
        "Lite = 3",
        "Balanced = 4",
        "ProtocolVersion = 2",
        "HasD4Nibble",
        "PayloadBits",
        "GetBaseSyncedBits",
        "syncWideRows",
        "viewportWidth",
        "displayRows",
        "NeedsRowPage",
        "ComputeRuntimeBounds",
    ):
        assert prop_tok in prop_src, f"UniTextProp.cs missing '{prop_tok}'"

    checker_src = (PKG_DIR / "Editor" / "UniTextParameterChecker.cs").read_text(encoding="utf-8")
    for chk_tok in (
        "GetPayloadIntBytes",
        "GetPayloadBits",
        "TierHasD4Nibble",
        "UniText_Bank",
        "UniText_RowPage",
        "HasD4Nibble",
        "UniText_D4b",
        "45b",
        "syncWideRows",
    ):
        assert chk_tok in checker_src, f"UniTextParameterChecker.cs missing '{chk_tok}'"

    tier_keywords = {
        "UniText_BufferCRT_Lite.mat": "_TIER_LITE",
        "UniText_BufferCRT_Compact.mat": "_TIER_COMPACT",
        "UniText_BufferCRT_Balanced.mat": "_TIER_BALANCED",
        "UniText_BufferCRT_Standard.mat": "_TIER_STANDARD",
        "UniText_BufferCRT_Turbo.mat": "_TIER_TURBO",
    }
    for mat_path in sorted((PKG_DIR / "Materials").glob("*.mat")):
        mat_txt = mat_path.read_text(encoding="utf-8")
        assert "m_ValidKeywords:" in mat_txt
        if mat_path.name in tier_keywords:
            assert f"- {tier_keywords[mat_path.name]}" in mat_txt
            assert "- _Bank: 0" in mat_txt and "- _RowPage: 0" in mat_txt
            assert "_AuxTex:" in mat_txt and "_LutTex:" not in mat_txt and "_AdvTex:" not in mat_txt
        if mat_path.name.startswith("UniText_ParamEncoder_"):
            assert "- _Bank: 0" in mat_txt and "- _RowPage: 0" in mat_txt
            for b in range(4, 8):
                assert f"- _D4b{b}: 0" in mat_txt, f"{mat_path.name} missing _D4b{b}"
        if mat_path.name.startswith("UniText_Display_"):
            assert "- _DisplayRows: 6" in mat_txt and "- _RowPx: 384" in mat_txt
            assert "_AuxTex:" in mat_txt and "- _LiveGeometry: 1" in mat_txt
        for ref_guid in re.findall(r"guid:\s*([0-9a-f]{32})", mat_txt):
            assert ref_guid in known_guids, f"Broken GUID {ref_guid} in {mat_path.name}"

    prop_meta = (PKG_DIR / "Runtime" / "UniTextProp.cs.meta").read_text(encoding="utf-8")
    prop_guid = re.search(r"^guid:\s*([0-9a-f]{32})\s*$", prop_meta, flags=re.MULTILINE).group(1)

    ndmf_src = (PKG_DIR / "Editor" / "UniTextNdmfPlugin.cs").read_text(encoding="utf-8")
    for req_token in [
        "ExportsPlugin",
        'BeforePlugin("nadena.dev.modular-avatar")',
        "ModularAvatarMergeAnimator",
        "ModularAvatarParameters",
        "ModularAvatarMenuInstaller",
        "ModularAvatarBoneProxy",
        "BuildTarget.Android",
        "UniText_ProtoVer",
        "ProtocolVersion",
        "UniText_D4b",
        "_LiveGeometry",
        "UniText_Bank",
        "UniText_RowPage",
        "UniText_Rows",
        "UniText_RowPx8",
        "Buffer_64x32_",
        "UniTextAssetBuilder.ParamCameraCullingMask",
        "RenderingPath.Forward",
        "DepthTextureMode.None",
        "allowDynamicResolution = false",
        "encoderQuadTf.gameObject.layer = UniTextAssetBuilder.ParamBridgeLayer",
    ]:
        assert req_token in ndmf_src, f"UniTextNdmfPlugin.cs missing '{req_token}'"
    for legacy_tok in ("UniText_V11", "UniText_V12", "UniText_V13"):
        assert legacy_tok not in ndmf_src, f"UniTextNdmfPlugin.cs still contains legacy '{legacy_tok}'"

    installer_src = (PKG_DIR / "Editor" / "UniTextInstallerWindow.cs").read_text(encoding="utf-8")
    _check_balanced_braces(installer_src, "UniTextInstallerWindow.cs")
    for req_token in ("UniTextInstallerWindow", "Board_Lite_33b.prefab", "Board_Compact_45b.prefab", "_displayRows", "DrawViewportSection"):
        assert req_token in installer_src, f"UniTextInstallerWindow.cs missing '{req_token}'"

    builtin_guid = "0000000000000000e000000000000000"
    for prefab_name, expected_tier in [
        ("Board_Lite_33b.prefab", "3"),
        ("Board_Compact_45b.prefab", "0"),
        ("Board_Balanced_57b.prefab", "4"),
        ("Board_Standard_73b.prefab", "1"),
        ("Board_Turbo_137b.prefab", "2"),
    ]:
        prefab_path = PKG_DIR / "Prefabs" / prefab_name
        assert prefab_path.is_file() and (PKG_DIR / "Prefabs" / f"{prefab_name}.meta").is_file()
        prefab_txt = prefab_path.read_text(encoding="utf-8")
        assert prop_guid in prefab_txt
        assert f"tier: {expected_tier}" in prefab_txt
        assert "syncWideRows: 1" in prefab_txt
        assert "viewportWidth: 0.84" in prefab_txt and "displayRows: 6" in prefab_txt
        assert "m_Bits: 1536" in prefab_txt, f"{prefab_name}: ParamCamera cullingMask must be 1536 (Player | PlayerLocal)"
        assert "m_RenderingPath: 1" in prefab_txt, f"{prefab_name}: ParamCamera m_RenderingPath must be 1 (Forward)"
        assert re.search(r"m_Layer:\s*9\r?\n\s*m_Name:\s*EncoderQuad", prefab_txt), (
            f"{prefab_name}: EncoderQuad must be on layer 9 (Player)"
        )
        for ref_guid in re.findall(r"guid:\s*([0-9a-f]{32})", prefab_txt):
            if ref_guid != builtin_guid:
                assert ref_guid in known_guids, f"Broken GUID {ref_guid} in {prefab_name}"


def test_vpm_packaging_dry_run() -> None:
    print("[6/6] Testing VPM pack + listing generation...")
    script = REPO_ROOT / "tools" / "vpm" / "build_listing.py"
    with tempfile.TemporaryDirectory() as tmp:
        tmp_path = Path(tmp)
        dist_dir = tmp_path / "dist"
        site_dir = tmp_path / "_site"

        subprocess.run([sys.executable, str(script), "pack", "--out-dir", str(dist_dir)], check=True)
        subprocess.run(
            [
                sys.executable,
                str(script),
                "listing",
                "--offline",
                "--out-dir",
                str(site_dir),
                "--local-package-json",
                str(dist_dir / "package.json"),
            ],
            check=True,
        )

        manifest = json.loads((PKG_DIR / "package.json").read_text(encoding="utf-8"))
        version = manifest["version"]
        zip_path = dist_dir / f"dev.dreaminko.unitext-{version}.zip"
        assert zip_path.is_file()
        pkg_meta = json.loads((dist_dir / "package.json").read_text(encoding="utf-8"))
        assert pkg_meta["zipSHA256"] == hashlib.sha256(zip_path.read_bytes()).hexdigest()

        with zipfile.ZipFile(zip_path) as zf:
            names = set(zf.namelist())
            for required in (
                "package.json",
                "Runtime/UniTextProp.cs",
                "Editor/UniTextNdmfPlugin.cs",
                "Editor/UniTextInstallerWindow.cs",
                "Shaders/BufferCRT.shader",
                "Textures/FontAtlas_BMP_Packed.png",
                "Textures/UniTextAux_64x64.png",
                "Textures/Buffer_64x32_Standard.asset",
                "Prefabs/Board_Compact_45b.prefab",
                "Prefabs/Board_Standard_73b.prefab",
            ):
                assert required in names, f"Missing '{required}' inside VPM zip"

        listing = json.loads((site_dir / "index.json").read_text(encoding="utf-8"))
        assert listing["id"] == "dev.dreaminko.unitext.vpm"
        assert version in listing["packages"]["dev.dreaminko.unitext"]["versions"]


def main() -> int:
    test_vectors()
    test_font_atlas_and_crt_to_display()
    test_osc_tester_cli()
    test_shaders_static()
    test_unity_package_integrity()
    test_vpm_packaging_dry_run()
    print("\nALL UNITEXT VALIDATION SUITES PASSED.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
