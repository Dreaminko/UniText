#!/usr/bin/env python3
"""UniText unified OSC protocol encoder, 64x32 CRT simulator, and test-vector verifier."""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Optional, Sequence, Set, Tuple

COLS = 32               # Columns per addressable bank plane
WIDE_COLS = 64          # Columns per physical row when UniText_Bank is synced (2 banks x 32)
PAGE_ROWS = 6           # Rows per addressable row-page plane
MIN_ROWS = 4
ROWS = 6
MAX_ROWS = 12
TOTAL_CELLS = COLS * PAGE_ROWS  # 192 cells per (bank, row_page) plane

ROW_PX = 384            # Default row width in glyph pixels (32 full-width glyphs x 12 px)
MIN_ROW_PX = 192
MAX_ROW_PX = 640
ROW_HEIGHT_PX = 16
MAX_ADVANCE = 12
CRT_W = 64
CRT_H = 32
META_ROW = 12
LAYOUT_ROW0 = 16

# Packet data length (D0..D{N-1}) per tier. Compact36 carries D0..D3 plus the HIGH NIBBLE of D4
# (synced as Bools UniText_D4b4..UniText_D4b7), i.e. 36 payload bits; its D4 low nibble is always 0.
TIERS: Dict[str, int] = {
    "lite": 3,
    "compact": 5,
    "balanced": 6,
    "standard": 8,
    "turbo": 16,
}
COMPACT36_BYTES = 5
TIER_PAYLOAD_BITS: Dict[str, int] = {"lite": 24, "compact": 36, "balanced": 48, "standard": 64, "turbo": 128}
PROTOCOL_VERSION = 2


def full_bytes(n_bytes: int) -> int:
    """Whole payload bytes usable by byte-oriented opcodes (WRITE_ASCII / WRITE_PAGE)."""
    return 4 if n_bytes == COMPACT36_BYTES else n_bytes


def mask_payload(data: Sequence[int], n_bytes: int) -> Tuple[int, ...]:
    """Apply the synced-bit mask of a tier (Compact36 only syncs the high nibble of D4)."""
    vals = [int(v) & 0xFF for v in data]
    if n_bytes == COMPACT36_BYTES and len(vals) > 4:
        vals[4] &= 0xF0
    return tuple(vals)

CTRL_NOP = 0
CTRL_UTF16_MIN = 1
CTRL_UTF16_MAX = 96
CTRL_ASCII_MIN = 97
CTRL_ASCII_MAX = 144
CTRL_LUT12_MIN = 145
CTRL_LUT12_MAX = 239
CTRL_CLEAR_ALL = 240
CTRL_CLEAR_ROW = 241
CTRL_LUT12_LAST = 243
CTRL_PAGE_MIN = 244
CTRL_PAGE_MAX = 249
CTRL_SET_VIEW = 250
CTRL_SET_GEOMETRY = 251
GEOMETRY_PX = 29          # Metadata pixel (29, 12) holding the live viewport geometry
MIN_ROW_PX8 = MIN_ROW_PX // 8
MAX_ROW_PX8 = MAX_ROW_PX // 8

PAGE_SEL_TO_HI: Dict[int, int] = {
    0: 0x30,
    1: 0xFF,
    2: 0x04,
    3: 0x20,
    4: 0x00,
}
PAGE_HI_TO_SEL: Dict[int, int] = {hi: sel for sel, hi in PAGE_SEL_TO_HI.items()}

REPO_ROOT = Path(__file__).resolve().parents[2]
LUT12_SPEC_PATH = REPO_ROOT / "spec" / "lut12.json"
ADVANCE_SPEC_PATH = REPO_ROOT / "spec" / "glyph-advance.json"
_LUT12_CACHE: Optional[Tuple[List[int], Dict[int, int]]] = None
_ADVANCE_CACHE: Optional[List[int]] = None


def load_lut12(path: Path = LUT12_SPEC_PATH) -> Tuple[List[int], Dict[int, int]]:
    global _LUT12_CACHE
    if path == LUT12_SPEC_PATH and _LUT12_CACHE is not None:
        return _LUT12_CACHE
    doc = json.loads(path.read_text(encoding="utf-8"))
    idx_to_cp = [int(cp) for cp in doc["codepoints"]]
    if len(idx_to_cp) != 4096:
        raise ValueError(f"Expected 4096 LUT12 codepoints in {path}, got {len(idx_to_cp)}")
    cp_to_idx = {cp: idx for idx, cp in enumerate(idx_to_cp)}
    if path == LUT12_SPEC_PATH:
        _LUT12_CACHE = (idx_to_cp, cp_to_idx)
    return idx_to_cp, cp_to_idx


def load_glyph_advances(path: Path = ADVANCE_SPEC_PATH) -> List[int]:
    """Expand spec/glyph-advance.json run-length ranges into a 65,536-entry advance list."""
    global _ADVANCE_CACHE
    if path == ADVANCE_SPEC_PATH and _ADVANCE_CACHE is not None:
        return _ADVANCE_CACHE
    doc = json.loads(path.read_text(encoding="utf-8"))
    advances = [-1] * 65536
    for start, end, adv in doc["ranges"]:
        for cp in range(int(start), int(end) + 1):
            advances[cp] = int(adv)
    if any(a < 0 for a in advances):
        raise ValueError(f"{path} does not cover every BMP codepoint")
    if any(a > MAX_ADVANCE for a in advances) or advances[0] != 0:
        raise ValueError(f"{path} violates advance bounds (0..{MAX_ADVANCE}, U+0000 == 0)")
    if path == ADVANCE_SPEC_PATH:
        _ADVANCE_CACHE = advances
    return advances


def glyph_advance(cp: int) -> int:
    """Proportional advance (glyph pixels) of a BMP codepoint."""
    return load_glyph_advances()[cp & 0xFFFF]


def text_advance(text: str) -> int:
    return sum(glyph_advance(normalize_bmp_codepoint(ch)) for ch in text)


def _is_break_space(cp: int) -> bool:
    return cp in (0x0020, 0x3000)


def _is_wide_break_opportunity(cp: int) -> bool:
    return (
        0x2E80 <= cp <= 0x9FFF
        or 0xAC00 <= cp <= 0xD7AF
        or 0xF900 <= cp <= 0xFAFF
        or 0xFF00 <= cp <= 0xFFEF
    )


def normalize_bmp_codepoint(ch: str) -> int:
    cp = ord(ch)
    return 0xFFFD if cp > 0xFFFF else cp


def resolve_live_viewport(
    base_rows: int,
    base_row_px: int,
    width_param: float = 0.0,
    height_param: float = 0.0,
    wide_rows: bool = True,
) -> Tuple[int, int]:
    """Compute live (rows, row_px) from base config + Resize (W/H) parameters in [-1, +1]."""
    b_rows = max(MIN_ROWS, min(MAX_ROWS, int(base_rows)))
    max_allowed = MAX_ROW_PX if wide_rows else ROW_PX
    b_px = max(MIN_ROW_PX, min(max_allowed, int(round(base_row_px / 8.0)) * 8))

    px_min = max(MIN_ROW_PX, min(b_px, int(round((b_px * 0.5) / 8.0)) * 8))
    px_max = max(b_px, min(max_allowed, int(round((b_px * 1.5) / 8.0)) * 8))

    w = max(-1.0, min(1.0, float(width_param)))
    raw_px = (b_px + w * (b_px - px_min)) if w < 0.0 else (b_px + w * (px_max - b_px))
    live_px = max(MIN_ROW_PX, min(max_allowed, int(round(raw_px / 8.0)) * 8))

    h = max(-1.0, min(1.0, float(height_param)))
    raw_rows = (b_rows + h * (b_rows - MIN_ROWS)) if h < 0.0 else (b_rows + h * (MAX_ROWS - b_rows))
    live_rows = max(MIN_ROWS, min(MAX_ROWS, int(round(raw_rows))))

    return live_rows, live_px


def wrap_text(text: str, max_px: int = ROW_PX, max_cells: int = WIDE_COLS) -> List[str]:
    """Greedy proportional line breaking bounded by both `max_px` and `max_cells`."""
    cps = [normalize_bmp_codepoint(ch) for ch in text.replace("\r", "").replace("\n", " ")]
    lines: List[str] = []
    i = 0
    n = len(cps)
    while i < n:
        while i < n and _is_break_space(cps[i]) and lines:
            i += 1
        if i >= n:
            break
        width = 0
        j = i
        last_break = -1
        while j < n and (j - i) < max_cells:
            adv = glyph_advance(cps[j])
            if width + adv > max_px:
                break
            width += adv
            if _is_break_space(cps[j]):
                last_break = j
            elif _is_wide_break_opportunity(cps[j]) or (j + 1 < n and _is_wide_break_opportunity(cps[j + 1])):
                last_break = j
            j += 1
        if j >= n:
            end = n
        elif _is_break_space(cps[j]):
            end = j
        elif last_break >= i:
            end = last_break + 1
        else:
            end = max(j, i + 1)
        lines.append("".join(chr(cp) for cp in cps[i:end]).rstrip(" "))
        i = end
    return lines


@dataclass(frozen=True)
class Packet:
    ctrl: int
    data: Tuple[int, ...]
    bank: int = 0
    row_page: int = 0

    def padded_data(self, n_bytes: int) -> Tuple[int, ...]:
        vals = list(self.data[:n_bytes])
        while len(vals) < n_bytes:
            vals.append(0)
        return tuple(v & 0xFF for v in vals)


class VirtualScreen:
    """Logical screen (4..12 rows, 32 or 64 cols) with Ring-Buffer topRow, row styles, status, and dirty tracking."""

    def __init__(self, cols: int = WIDE_COLS, rows: int = ROWS) -> None:
        if cols not in (COLS, WIDE_COLS):
            raise ValueError(f"cols must be {COLS} or {WIDE_COLS}, got {cols}")
        if not (MIN_ROWS <= rows <= MAX_ROWS):
            raise ValueError(f"rows must be {MIN_ROWS}..{MAX_ROWS}, got {rows}")
        self.cols: int = cols
        self.rows: int = rows
        self.total_cells: int = cols * rows
        self.cells: List[int] = [0] * self.total_cells
        self.row_styles: List[Tuple[int, int]] = [(0, 0) for _ in range(rows)]
        self.status: int = 0
        self.top_row: int = 0
        self.dirty_cells: List[bool] = [False] * self.total_cells
        self.dirty_styles: List[bool] = [False] * rows
        self.dirty_status: bool = False
        self.dirty_top_row: bool = False
        self.recycled_phys_rows: Set[int] = set()

    def visual_to_physical(self, row: int) -> int:
        if not (0 <= row < self.rows):
            raise ValueError(f"Row {row} out of range 0..{self.rows - 1}")
        return (row + self.top_row) % self.rows

    def clone(self) -> "VirtualScreen":
        copy = VirtualScreen(self.cols, self.rows)
        copy.cells = list(self.cells)
        copy.row_styles = list(self.row_styles)
        copy.status = self.status
        copy.top_row = self.top_row
        copy.dirty_cells = list(self.dirty_cells)
        copy.dirty_styles = list(self.dirty_styles)
        copy.dirty_status = self.dirty_status
        copy.dirty_top_row = self.dirty_top_row
        copy.recycled_phys_rows = set(self.recycled_phys_rows)
        return copy

    def clear_all(self) -> None:
        for i in range(self.total_cells):
            if self.cells[i] != 0:
                self.cells[i] = 0
                self.dirty_cells[i] = True
        for r in range(self.rows):
            if self.row_styles[r] != (0, 0):
                self.row_styles[r] = (0, 0)
                self.dirty_styles[r] = True
        if self.status != 0:
            self.status = 0
            self.dirty_status = True
        if self.top_row != 0:
            self.top_row = 0
            self.dirty_top_row = True
        self.recycled_phys_rows.clear()

    def scroll_up(self, lines: int = 1) -> None:
        if lines <= 0:
            return
        cols = self.cols
        rows = self.rows
        old_top = self.top_row
        for step in range(min(lines, rows)):
            phys = (old_top + step) % rows
            base = phys * cols
            had_content = any(self.cells[base + c] != 0 or self.dirty_cells[base + c] for c in range(cols))
            for c in range(cols):
                self.cells[base + c] = 0
                self.dirty_cells[base + c] = False
            if self.row_styles[phys] != (0, 0):
                self.row_styles[phys] = (0, 0)
                self.dirty_styles[phys] = True
            if had_content:
                self.recycled_phys_rows.add(phys)
        new_top = (old_top + lines) % rows
        if new_top != old_top:
            self.top_row = new_top
            self.dirty_top_row = True

    def clear_row(self, row: int) -> None:
        phys = self.visual_to_physical(row)
        base = phys * self.cols
        for c in range(self.cols):
            idx = base + c
            if self.cells[idx] != 0:
                self.cells[idx] = 0
                self.dirty_cells[idx] = True

    def set_cell(self, row: int, col: int, cp: int) -> None:
        if not (0 <= col < self.cols):
            raise ValueError(f"Coordinate ({row}, {col}) out of range")
        phys = self.visual_to_physical(row)
        cp = cp & 0xFFFF
        idx = phys * self.cols + col
        if self.cells[idx] != cp:
            self.cells[idx] = cp
            self.dirty_cells[idx] = True

    def set_row_text(self, row: int, text: str) -> None:
        phys = self.visual_to_physical(row)
        cps = [normalize_bmp_codepoint(ch) for ch in text[: self.cols]]
        while len(cps) < self.cols:
            cps.append(0)
        base = phys * self.cols
        for c, cp in enumerate(cps):
            idx = base + c
            if self.cells[idx] != cp:
                self.cells[idx] = cp
                self.dirty_cells[idx] = True

    def set_row_style(self, row: int, color: int, flags: int) -> None:
        phys = self.visual_to_physical(row)
        style = (color & 0x0F, flags & 0x3F)
        if self.row_styles[phys] != style:
            self.row_styles[phys] = style
            self.dirty_styles[phys] = True

    def set_status(self, status: int) -> None:
        s = status & 0x0F
        if self.status != s:
            self.status = s
            self.dirty_status = True

    def read_visual_cells(self) -> List[int]:
        out: List[int] = []
        for r in range(self.rows):
            phys = self.visual_to_physical(r)
            base = phys * self.cols
            out.extend(self.cells[base : base + self.cols])
        return out

    def read_visual_row_styles(self) -> List[Tuple[int, int]]:
        return [self.row_styles[self.visual_to_physical(r)] for r in range(self.rows)]

    def visual_row_px(self, row: int) -> int:
        base = self.visual_to_physical(row) * self.cols
        return sum(glyph_advance(cp) for cp in self.cells[base : base + self.cols])

    def mark_clean(self) -> None:
        self.dirty_cells = [False] * self.total_cells
        self.dirty_styles = [False] * self.rows
        self.dirty_status = False
        self.dirty_top_row = False
        self.recycled_phys_rows.clear()


def compute_row_layout(row_cells: Sequence[int]) -> List[Tuple[int, int]]:
    """Reference proportional layout: per-cell (xStart, advance) matching BufferCRT.shader."""
    out: List[Tuple[int, int]] = []
    x = 0
    for cp in row_cells:
        adv = glyph_advance(cp) if cp != 0 else 0
        out.append((x, adv))
        x += adv
    return out


class CRTBufferSimulator:
    """Bit-exact Python replica of the 64x32 BufferCRT.shader (+ Display.shader lookups)."""

    def __init__(self, rows: int = ROWS) -> None:
        if not (MIN_ROWS <= rows <= MAX_ROWS):
            raise ValueError(f"rows must be {MIN_ROWS}..{MAX_ROWS}, got {rows}")
        self.rows = rows
        self.width = CRT_W
        self.height = CRT_H
        self.cols = WIDE_COLS
        self._reset_buffer()

    def _reset_buffer(self) -> None:
        self.buffer: List[List[List[int]]] = [
            [[0, 0, 0, 0] for _ in range(CRT_W)] for _ in range(CRT_H)
        ]

    def load_from_screen(self, screen: VirtualScreen) -> None:
        if screen.rows > self.rows:
            raise ValueError(f"Screen has {screen.rows} rows but simulator holds only {self.rows}")
        self._reset_buffer()
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
        self._update_layout()

    def apply_packet(self, packet: Packet, tier: str, frames: int = 15) -> None:
        """Apply a packet across `frames` consecutive CRT updates to verify idempotency."""
        n_bytes = TIERS[tier]
        data = mask_payload(packet.padded_data(n_bytes), n_bytes)
        ctrl = packet.ctrl & 0xFF
        bank = packet.bank & 1
        row_page = packet.row_page & 1
        snapshot = None

        for frame_idx in range(max(1, frames)):
            self._step(ctrl, data, n_bytes, bank, row_page)
            if frame_idx == 0:
                snapshot = [[list(px) for px in row] for row in self.buffer]
            elif self.buffer != snapshot:
                raise AssertionError(
                    f"Non-idempotent packet detected! ctrl={ctrl}, bank={bank}, row_page={row_page}, data={data} changed on frame {frame_idx}"
                )

    def _cell_xy(self, cell: int, bank: int, row_page: int) -> Tuple[int, int]:
        return (cell // COLS) + PAGE_ROWS * row_page, (cell % COLS) + COLS * bank

    def _update_layout(self) -> None:
        for phys in range(MAX_ROWS):
            row_cps = []
            for col in range(WIDE_COLS):
                r, g, _, a = self.buffer[phys][col]
                row_cps.append(((r << 8) | g) if a != 0 else 0)
            for col, (x, adv) in enumerate(compute_row_layout(row_cps)):
                self.buffer[LAYOUT_ROW0 + phys][col] = [(x >> 8) & 0xFF, x & 0xFF, adv & 0xFF, 255]

    def _step(self, ctrl: int, data: Sequence[int], n_bytes: int, bank: int = 0, row_page: int = 0) -> None:
        if ctrl == CTRL_NOP:
            return
        self._apply(ctrl, data, n_bytes, bank, row_page)
        if ctrl == CTRL_SET_VIEW or ctrl == 242 or ctrl >= 251:
            return
        self._update_layout()

    def _apply(self, ctrl: int, data: Sequence[int], n_bytes: int, bank: int, row_page: int) -> None:
        if ctrl == CTRL_NOP:
            return

        if ctrl == CTRL_CLEAR_ALL:
            geometry = list(self.buffer[META_ROW][GEOMETRY_PX])
            self._reset_buffer()
            self.buffer[META_ROW][GEOMETRY_PX] = geometry  # live viewport geometry survives CLEAR_ALL
            return

        if ctrl == CTRL_CLEAR_ROW:
            mask = (data[0] & 0x3F) | ((data[1] & 0x3F) << 6)
            for row in range(MAX_ROWS):
                if (mask >> row) & 1:
                    for col in range(WIDE_COLS):
                        self.buffer[row][col] = [0, 0, 0, 0]
            return

        if ctrl == CTRL_SET_VIEW:
            new_top = data[0] & 0x0F
            new_status = (data[0] >> 4) & 0x0F
            target_row = (data[1] >> 4) & 0x0F
            color = data[1] & 0x0F
            flags = data[2] & 0x3F
            upd_status = (data[2] >> 7) & 1
            if target_row < MAX_ROWS:
                self.buffer[META_ROW][target_row] = [color, flags, 0, 255]
            if new_top < MAX_ROWS:
                self.buffer[META_ROW][30] = [new_top, 0, 0, 255]
            if upd_status:
                self.buffer[META_ROW][31] = [new_status, 0, 0, 255]
            return

        if ctrl == CTRL_SET_GEOMETRY:
            geo_rows = data[0] & 0xFF
            geo_px8 = data[1] & 0xFF
            if MIN_ROWS <= geo_rows <= MAX_ROWS and MIN_ROW_PX8 <= geo_px8 <= MAX_ROW_PX8:
                self.buffer[META_ROW][GEOMETRY_PX] = [geo_rows, geo_px8, 0, 255]
            return

        if CTRL_UTF16_MIN <= ctrl <= CTRL_UTF16_MAX:
            odd_offset = (data[2] & 1) if n_bytes == 3 else 0
            start = (ctrl - CTRL_UTF16_MIN) * 2 + odd_offset
            count = n_bytes // 2
            for i in range(count):
                cell = start + i
                if cell >= TOTAL_CELLS:
                    continue
                hi = data[2 * i] & 0xFF
                lo = data[2 * i + 1] & 0xFF
                cp = (hi << 8) | lo
                row, x = self._cell_xy(cell, bank, row_page)
                self.buffer[row][x] = [hi, lo, 0, 255 if cp != 0 else 0]
            return

        if CTRL_ASCII_MIN <= ctrl <= CTRL_ASCII_MAX:
            start = (ctrl - CTRL_ASCII_MIN) * 4
            for i in range(full_bytes(n_bytes)):
                cell = start + i
                if cell >= TOTAL_CELLS:
                    continue
                lo = data[i] & 0xFF
                row, x = self._cell_xy(cell, bank, row_page)
                self.buffer[row][x] = [0, lo, 0, 255 if lo != 0 else 0]
            return

        if (CTRL_LUT12_MIN <= ctrl <= CTRL_LUT12_MAX) or ctrl == CTRL_LUT12_LAST:
            slot = 95 if ctrl == CTRL_LUT12_LAST else (ctrl - CTRL_LUT12_MIN)
            lut_idx_to_cp, _ = load_lut12()
            if n_bytes == COMPACT36_BYTES:
                if slot < 66:
                    target_row = (slot // 11) + PAGE_ROWS * row_page
                    start_col = (slot % 11) * 3
                    for i in range(min(3, COLS - start_col)):
                        base = (i >> 1) * 3
                        if (i & 1) == 0:
                            lut_idx = (data[base] << 4) | (data[base + 1] >> 4)
                        else:
                            lut_idx = ((data[base + 1] & 0x0F) << 8) | data[base + 2]
                        cp = lut_idx_to_cp[lut_idx & 0x0FFF]
                        self.buffer[target_row][start_col + i + COLS * bank] = [
                            (cp >> 8) & 0xFF,
                            cp & 0xFF,
                            0,
                            255 if cp != 0 else 0,
                        ]
                return
            odd_offset = (data[7] & 1) if n_bytes == 8 else 0
            start = slot * 2 + odd_offset
            count = (n_bytes * 2) // 3
            for i in range(count):
                cell = start + i
                if cell >= TOTAL_CELLS:
                    continue
                base = (i >> 1) * 3
                if (i & 1) == 0:
                    lut_idx = (data[base] << 4) | (data[base + 1] >> 4)
                else:
                    lut_idx = ((data[base + 1] & 0x0F) << 8) | data[base + 2]
                cp = lut_idx_to_cp[lut_idx & 0x0FFF]
                row, x = self._cell_xy(cell, bank, row_page)
                self.buffer[row][x] = [(cp >> 8) & 0xFF, cp & 0xFF, 0, 255 if cp != 0 else 0]
            return

        if CTRL_PAGE_MIN <= ctrl <= CTRL_PAGE_MAX:
            target_row = (ctrl - CTRL_PAGE_MIN) + PAGE_ROWS * row_page
            start_col = data[0] & 0x1F
            page_sel = (data[0] >> 5) & 0x07
            count = max(0, full_bytes(n_bytes) - 1)
            if page_sel in PAGE_SEL_TO_HI:
                page_hi = PAGE_SEL_TO_HI[page_sel]
                for i in range(count):
                    col = start_col + i
                    if col >= COLS:
                        continue
                    x = col + COLS * bank
                    lo = data[i + 1] & 0xFF
                    self.buffer[target_row][x] = [page_hi, lo, 0, 255] if lo != 0 else [0, 0, 0, 0]
            return

        # Reserved Ctrl values (242, 252..255) -> NOP

    def read_geometry(self) -> Optional[Tuple[int, int]]:
        """Live viewport (rows, row_px) written by CMD_SET_GEOMETRY, or None before the first one."""
        r, g, _, a = self.buffer[META_ROW][GEOMETRY_PX]
        if a == 0 or not (MIN_ROWS <= r <= MAX_ROWS) or not (MIN_ROW_PX8 <= g <= MAX_ROW_PX8):
            return None
        return r, g * 8

    def local_clear(self) -> None:
        """Replicates UniText_LocalClear: wipes everything except the geometry pixel."""
        geometry = list(self.buffer[META_ROW][GEOMETRY_PX])
        self._reset_buffer()
        self.buffer[META_ROW][GEOMETRY_PX] = geometry

    def read_top_row(self) -> int:
        top = self.buffer[META_ROW][30][0]
        return top if top < self.rows else 0

    def read_visual_cells(self, cols: Optional[int] = None, rows: Optional[int] = None) -> List[int]:
        cols = self.cols if cols is None else cols
        rows = self.rows if rows is None else rows
        top = self.read_top_row()
        out: List[int] = []
        for vr in range(rows):
            row = (vr + top) % rows
            for col in range(cols):
                r, g, _, a = self.buffer[row][col]
                out.append(((r << 8) | g) if a != 0 else 0)
        return out

    def read_layout(self, phys_row: int) -> List[Tuple[int, int]]:
        return [((px[0] << 8) | px[1], px[2]) for px in self.buffer[LAYOUT_ROW0 + phys_row]]

    def read_visual_row_px(self) -> List[int]:
        top = self.read_top_row()
        totals: List[int] = []
        for vr in range(self.rows):
            x, adv = self.read_layout((vr + top) % self.rows)[-1]
            totals.append(x + adv)
        return totals

    def glyph_at(self, visual_row: int, pen_x: int) -> Tuple[int, int]:
        """Replicates Display.shader's 6-step binary search: returns (codepoint, gx) at pixel X."""
        phys = (visual_row + self.read_top_row()) % self.rows
        layout = self.read_layout(phys)
        lo, hi = 0, WIDE_COLS - 1
        x, adv = 0, 0
        for _ in range(6):
            mid = (lo + hi + 1) >> 1
            mid_x, mid_adv = layout[mid]
            if mid_x <= pen_x:
                lo = mid
                x, adv = mid_x, mid_adv
            else:
                hi = mid - 1
                if mid == 1:
                    adv = mid_x
        if not (x <= pen_x < x + adv):
            return 0, 0
        r, g, _, a = self.buffer[phys][lo]
        cp = ((r << 8) | g) if a != 0 else 0
        return cp, pen_x - x

    def read_visual_row_styles(self) -> List[Tuple[int, int]]:
        top = self.read_top_row()
        return [
            (self.buffer[META_ROW][(vr + top) % self.rows][0] & 0x0F, self.buffer[META_ROW][(vr + top) % self.rows][1])
            for vr in range(self.rows)
        ]

    def read_status(self) -> int:
        return self.buffer[META_ROW][31][0]


def _encode_set_view_packet(
    n_bytes: int,
    top_row: int,
    style_entry: Optional[Tuple[int, int, int]],
    status: Optional[int],
) -> Packet:
    upd_status = 1 if status is not None else 0
    status_val = (status & 0x0F) if status is not None else 0
    d0 = ((status_val & 0x0F) << 4) | (top_row & 0x0F)

    if style_entry is not None:
        row, color, flags = style_entry
        d1 = ((row & 0x0F) << 4) | (color & 0x0F)
        d2 = (upd_status << 7) | (flags & 0x3F)
    else:
        d1 = 0xF0
        d2 = upd_status << 7

    return Packet(CTRL_SET_VIEW, (d0, d1, d2) + (0,) * (n_bytes - 3))


def encode_geometry_packet(n_bytes: int, rows: int, row_px: int) -> Packet:
    """CMD_SET_GEOMETRY (Ctrl 251): broadcast live (rows, row_px) to every viewer (3 bytes, fits Lite)."""
    r = max(MIN_ROWS, min(MAX_ROWS, int(rows)))
    px8 = max(MIN_ROW_PX8, min(MAX_ROW_PX8, int(round(int(row_px) / 8.0))))
    return Packet(CTRL_SET_GEOMETRY, (r, px8) + (0,) * (n_bytes - 2))


def _encode_plane_cells(
    plane_cells: Sequence[int],
    need_write: List[bool],
    n_bytes: int,
    cp_to_idx: Dict[int, int],
    bank: int,
    row_page: int,
) -> List[Packet]:
    """Greedy optimal encoder over one 32x6 bank/row-page plane (cell index space 0..191)."""
    packets: List[Packet] = []
    lut_count = (n_bytes * 2) // 3
    ascii_count = full_bytes(n_bytes)
    page_count = max(0, ascii_count - 1)
    utf16_count = max(1, n_bytes // 2)

    cell = 0
    while cell < TOTAL_CELLS:
        if not need_write[cell]:
            cell += 1
            continue

        # Candidates: (covered_needed_count, priority, covered_cells_list, Packet)
        # Priority tie-break: WRITE_ASCII(4) > WRITE_PAGE(3) > WRITE_UTF16(2) > WRITE_LUT12(1)
        candidates: List[Tuple[int, int, List[int], Packet]] = []

        # Candidate A: WRITE_ASCII
        ascii_start = (cell // 4) * 4
        ascii_end = min(TOTAL_CELLS, ascii_start + ascii_count)
        if ascii_start <= cell < ascii_end:
            ascii_slice = plane_cells[ascii_start:ascii_end]
            if all(cp <= 0xFF for cp in ascii_slice):
                cov = list(range(ascii_start, ascii_end))
                score = sum(1 for k in cov if need_write[k])
                data = list(ascii_slice) + [0] * (n_bytes - len(ascii_slice))
                candidates.append((score, 4, cov, Packet(CTRL_ASCII_MIN + (ascii_start // 4), tuple(data), bank, row_page)))

        # Candidate B: WRITE_PAGE
        if page_count > 0:
            phys_row = cell // COLS
            col = cell % COLS
            page_hi = (plane_cells[cell] >> 8) & 0xFF
            if page_hi in PAGE_HI_TO_SEL:
                page_sel = PAGE_HI_TO_SEL[page_hi]
                row_base = phys_row * COLS
                end_col = min(COLS, col + page_count)
                valid_page = True
                lo_bytes: List[int] = []
                cov_page: List[int] = []
                for c_col in range(col, end_col):
                    idx = row_base + c_col
                    c_cp = plane_cells[idx]
                    if c_cp == 0:
                        lo_bytes.append(0)
                    elif ((c_cp >> 8) & 0xFF) == page_hi and (c_cp & 0xFF) != 0:
                        lo_bytes.append(c_cp & 0xFF)
                    else:
                        valid_page = False
                        break
                    cov_page.append(idx)
                if valid_page and cov_page:
                    score = sum(1 for k in cov_page if need_write[k])
                    d0 = (page_sel << 5) | (col & 0x1F)
                    data = [d0] + lo_bytes + [0] * (n_bytes - 1 - len(lo_bytes))
                    candidates.append((score, 3, cov_page, Packet(CTRL_PAGE_MIN + phys_row, tuple(data), bank, row_page)))

        # Candidate C: WRITE_LUT12
        if lut_count > 0:
            if n_bytes == COMPACT36_BYTES:
                page_row = cell // COLS
                chunk = (cell % COLS) // 3
                start_col = chunk * 3
                end_col = min(COLS, start_col + 3)
                l_start = page_row * COLS + start_col
                l_end = page_row * COLS + end_col
                cov_lut = list(range(l_start, l_end))
                if all(plane_cells[k] in cp_to_idx for k in cov_lut):
                    indices = [cp_to_idx[plane_cells[k]] for k in cov_lut]
                    while len(indices) < 3:
                        indices.append(0)
                    a, b, c = indices[0], indices[1], indices[2]
                    data = [
                        (a >> 4) & 0xFF,
                        ((a & 0x0F) << 4) | ((b >> 8) & 0x0F),
                        b & 0xFF,
                        (c >> 4) & 0xFF,
                        (c & 0x0F) << 4,
                    ]
                    slot = page_row * 11 + chunk
                    ctrl = CTRL_LUT12_MIN + slot
                    score = sum(1 for k in cov_lut if need_write[k])
                    candidates.append((score, 1, cov_lut, Packet(ctrl, tuple(data), bank, row_page)))
            else:
                lut_starts = [cell - (cell % 2)]
                if n_bytes == 8 and (cell % 2) == 1:
                    lut_starts.append(cell)
                for l_start in lut_starts:
                    l_end = min(TOTAL_CELLS, l_start + lut_count)
                    if not (l_start <= cell < l_end):
                        continue
                    cov_lut = list(range(l_start, l_end))
                    if all(plane_cells[k] in cp_to_idx for k in cov_lut):
                        indices = [
                            cp_to_idx[plane_cells[l_start + i]] if (l_start + i) < TOTAL_CELLS else 0
                            for i in range(lut_count)
                        ]
                        data = [0] * n_bytes
                        for p_i in range(0, len(indices), 2):
                            a = indices[p_i]
                            b_off = (p_i // 2) * 3
                            if p_i + 1 < len(indices):
                                b = indices[p_i + 1]
                                data[b_off] = (a >> 4) & 0xFF
                                data[b_off + 1] = ((a & 0x0F) << 4) | ((b >> 8) & 0x0F)
                                data[b_off + 2] = b & 0xFF
                            else:
                                data[b_off] = (a >> 4) & 0xFF
                                data[b_off + 1] = ((a & 0x0F) << 4) | ((l_start & 1) if n_bytes == 8 else 0)
                        slot = l_start // 2
                        ctrl = CTRL_LUT12_LAST if slot == 95 else (CTRL_LUT12_MIN + slot)
                        score = sum(1 for k in cov_lut if need_write[k])
                        candidates.append((score, 1, cov_lut, Packet(ctrl, tuple(data), bank, row_page)))

        # Candidate D: WRITE_UTF16
        if n_bytes == 3:
            u_start = cell
            cp = plane_cells[u_start]
            u_data = ((cp >> 8) & 0xFF, cp & 0xFF, u_start & 1)
            cov_utf = [u_start]
            score = 1 if need_write[u_start] else 0
            candidates.append((score, 2, cov_utf, Packet(CTRL_UTF16_MIN + (u_start // 2), u_data, bank, row_page)))
        else:
            u_start = cell - (cell % 2)
            u_end = min(TOTAL_CELLS, u_start + utf16_count)
            cov_utf = list(range(u_start, u_end))
            u_bytes: List[int] = []
            for i in range(utf16_count):
                idx = u_start + i
                cp = plane_cells[idx] if idx < TOTAL_CELLS else 0
                u_bytes.append((cp >> 8) & 0xFF)
                u_bytes.append(cp & 0xFF)
            score = sum(1 for k in cov_utf if need_write[k])
            candidates.append((score, 2, cov_utf, Packet(CTRL_UTF16_MIN + (u_start // 2), tuple(u_bytes), bank, row_page)))

        _, _, best_cov, best_pkt = max(candidates, key=lambda item: (item[0], item[1]))
        packets.append(best_pkt)
        for k in best_cov:
            need_write[k] = False

    return packets


def encode_screen_packets(
    screen: VirtualScreen,
    tier: str,
    dirty_only: bool = False,
    clear_blank_rows: bool = True,
) -> List[Packet]:
    """Encode VirtualScreen (or dirty slice) into deterministic UniText OSC packets."""
    n_bytes = TIERS[tier]
    packets: List[Packet] = []
    _, cp_to_idx = load_lut12()
    cols = screen.cols
    rows = screen.rows

    need_top_row = screen.dirty_top_row if dirty_only else (screen.top_row != 0)
    dirty_style_rows = [
        r for r in range(rows)
        if (screen.dirty_styles[r] if dirty_only else (screen.row_styles[r] != (0, 0)))
    ]
    need_status = screen.dirty_status if dirty_only else (screen.status != 0)

    if need_top_row:
        first_style = None
        if dirty_style_rows:
            r0 = dirty_style_rows.pop(0)
            c0, f0 = screen.row_styles[r0]
            first_style = (r0, c0, f0)
        status_arg = screen.status if need_status else None
        need_status = False
        packets.append(_encode_set_view_packet(n_bytes, screen.top_row, first_style, status_arg))

    clear_mask = 0
    cleared_blank_rows: Set[int] = set()
    recycled_rows: Set[int] = set()
    if clear_blank_rows:
        for phys_row in range(rows):
            base = phys_row * cols
            row_cells = screen.cells[base : base + cols]
            row_dirty = screen.dirty_cells[base : base + cols]
            is_blank = all(cp == 0 for cp in row_cells)
            if dirty_only:
                if is_blank and (any(row_dirty) or phys_row in screen.recycled_phys_rows):
                    clear_mask |= 1 << phys_row
                    cleared_blank_rows.add(phys_row)
                elif phys_row in screen.recycled_phys_rows:
                    clear_mask |= 1 << phys_row
                    recycled_rows.add(phys_row)
        if clear_mask != 0:
            packets.append(Packet(CTRL_CLEAR_ROW, (clear_mask & 0x3F, (clear_mask >> 6) & 0x3F) + (0,) * (n_bytes - 2)))

    need_write = [False] * screen.total_cells
    for c in range(screen.total_cells):
        phys_row = c // cols
        if phys_row in cleared_blank_rows:
            continue
        if phys_row in recycled_rows:
            need_write[c] = screen.cells[c] != 0
        elif dirty_only:
            need_write[c] = screen.dirty_cells[c]
        else:
            need_write[c] = screen.cells[c] != 0

    num_row_pages = 2 if rows > PAGE_ROWS else 1
    for row_page in range(num_row_pages):
        page_rows = min(PAGE_ROWS, rows - row_page * PAGE_ROWS)
        for bank in range(cols // COLS):
            plane_cells = [0] * TOTAL_CELLS
            plane_need = [False] * TOTAL_CELLS
            for pr in range(page_rows):
                r = row_page * PAGE_ROWS + pr
                for c in range(COLS):
                    src = r * cols + bank * COLS + c
                    plane_cells[pr * COLS + c] = screen.cells[src]
                    plane_need[pr * COLS + c] = need_write[src]
            packets.extend(_encode_plane_cells(plane_cells, plane_need, n_bytes, cp_to_idx, bank, row_page))

    for idx, r in enumerate(dirty_style_rows):
        color, flags = screen.row_styles[r]
        status_arg = screen.status if (need_status and idx == 0) else None
        if status_arg is not None:
            need_status = False
        packets.append(_encode_set_view_packet(n_bytes, screen.top_row, (r, color, flags), status_arg))

    if need_status:
        packets.append(_encode_set_view_packet(n_bytes, screen.top_row, None, screen.status))

    return packets


def _build_screen_from_state(state: dict, cols: int = WIDE_COLS, rows: int = ROWS) -> VirtualScreen:
    screen = VirtualScreen(cols, rows)
    screen.top_row = int(state.get("top_row", 0)) % rows
    for row_str, text in state.get("rows", {}).items():
        screen.set_row_text(int(row_str), text)
    for sparse in state.get("sparse_cells", []):
        screen.set_cell(int(sparse["row"]), int(sparse["col"]), int(sparse["cp"]))
    for style_entry in state.get("row_styles", []):
        screen.set_row_style(
            int(style_entry["row"]),
            int(style_entry["color"]),
            int(style_entry["flags"]),
        )
    if "status" in state:
        screen.set_status(int(state["status"]))
    return screen


def verify_test_vectors(vectors_path: Path) -> int:
    doc = json.loads(vectors_path.read_text(encoding="utf-8"))
    vectors = doc.get("vectors", [])
    if not vectors:
        raise AssertionError(f"No vectors found in {vectors_path}")

    for vec in vectors:
        vid = vec["id"]
        tier = vec["tier"]
        expected_n = TIERS[tier]
        if vec["payload_bytes"] != expected_n:
            raise AssertionError(f"{vid}: payload_bytes {vec['payload_bytes']} != tier {expected_n}")

        cols = int(vec.get("row_cells", WIDE_COLS))
        rows = int(vec.get("rows", ROWS))
        row_px = int(vec.get("row_px", ROW_PX))
        init_screen = _build_screen_from_state(vec.get("initial_state", {}), cols, rows)
        init_screen.mark_clean()
        expected_screen = _build_screen_from_state(vec["expected_state"], cols, rows)

        sim = CRTBufferSimulator(rows=rows)
        sim.load_from_screen(init_screen)
        for pkt_raw in vec["packets"]:
            data = tuple(int(x) for x in pkt_raw["data"])
            if len(data) != expected_n:
                raise AssertionError(f"{vid}: packet data length {len(data)} != {expected_n}")
            bank = int(pkt_raw.get("bank", 0))
            row_page = int(pkt_raw.get("row_page", 0))
            sim.apply_packet(Packet(int(pkt_raw["ctrl"]), data, bank, row_page), tier=tier, frames=15)

        if sim.read_top_row() != expected_screen.top_row:
            raise AssertionError(f"{vid}: CRT top_row mismatch ({sim.read_top_row()} != {expected_screen.top_row})")
        if sim.read_visual_cells(cols, rows) != expected_screen.read_visual_cells():
            raise AssertionError(f"{vid}: CRT cell mismatch!")
        if sim.cols > cols and any(sim.read_visual_cells(sim.cols, rows)[r * sim.cols + c] for r in range(rows) for c in range(cols, sim.cols)):
            raise AssertionError(f"{vid}: narrow vector wrote cells beyond column {cols}")
        if sim.read_visual_row_styles() != expected_screen.read_visual_row_styles():
            raise AssertionError(f"{vid}: CRT row_styles mismatch!")
        if sim.read_status() != expected_screen.status:
            raise AssertionError(f"{vid}: CRT status mismatch ({sim.read_status()} != {expected_screen.status})")
        if "expected_geometry" in vec:
            exp_geo = vec["expected_geometry"]
            exp_geo = None if exp_geo is None else (int(exp_geo[0]), int(exp_geo[1]))
            if sim.read_geometry() != exp_geo:
                raise AssertionError(f"{vid}: CRT geometry mismatch ({sim.read_geometry()} != {exp_geo})")

        got_px = sim.read_visual_row_px()
        exp_px = [expected_screen.visual_row_px(r) for r in range(rows)]
        if got_px != exp_px:
            raise AssertionError(f"{vid}: CRT layout row widths mismatch! got={got_px}, expected={exp_px}")
        if "expected_row_px" in vec and [int(x) for x in vec["expected_row_px"]] != got_px:
            raise AssertionError(f"{vid}: expected_row_px mismatch! got={got_px}, expected={vec['expected_row_px']}")
        if max(got_px) > row_px:
            raise AssertionError(f"{vid}: visual row px {got_px} exceeds {row_px}")
        for vr_str, exp_x in vec.get("expected_layout", {}).items():
            phys = (int(vr_str) + sim.read_top_row()) % rows
            got_x = [x for x, _ in sim.read_layout(phys)[: len(exp_x)]]
            if got_x != [int(x) for x in exp_x]:
                raise AssertionError(f"{vid}: expected_layout row {vr_str} mismatch! got={got_x}, expected={exp_x}")

        if vec.get("encoder_check", False):
            working = init_screen.clone()
            scroll_up_lines = int(vec.get("scroll_up", 0))
            if scroll_up_lines > 0:
                working.scroll_up(scroll_up_lines)

            exp_visual_cells = expected_screen.read_visual_cells()
            exp_visual_styles = expected_screen.read_visual_row_styles()
            for r in range(rows):
                for c in range(cols):
                    working.set_cell(r, c, exp_visual_cells[r * cols + c])
                color, flags = exp_visual_styles[r]
                working.set_row_style(r, color, flags)
            working.set_status(expected_screen.status)

            encoded_pkts = encode_screen_packets(working, tier=tier, dirty_only=True)
            max_pkts = vec.get("max_encoder_packets")
            if max_pkts is not None and len(encoded_pkts) > int(max_pkts):
                raise AssertionError(
                    f"{vid}: encoder emitted {len(encoded_pkts)} packets, exceeding max_encoder_packets={max_pkts}"
                )

            enc_sim = CRTBufferSimulator(rows=rows)
            enc_sim.load_from_screen(init_screen)
            for pkt in encoded_pkts:
                enc_sim.apply_packet(pkt, tier=tier, frames=15)

            if enc_sim.read_top_row() != expected_screen.top_row:
                raise AssertionError(f"{vid}: encoder round-trip top_row mismatch!")
            if enc_sim.read_visual_cells(cols, rows) != exp_visual_cells:
                raise AssertionError(f"{vid}: encoder round-trip cell mismatch!")
            if enc_sim.read_visual_row_styles() != exp_visual_styles:
                raise AssertionError(f"{vid}: encoder round-trip style mismatch!")
            if enc_sim.read_status() != expected_screen.status:
                raise AssertionError(f"{vid}: encoder round-trip status mismatch!")

        print(f"  [PASS] {vid} ({tier}, {len(vec['packets'])} packets)")

    return len(vectors)
