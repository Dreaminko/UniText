#!/usr/bin/env python3
"""UniText local OSC sender, OSCQuery detector, dry-run CRT verifier, and vector runner.

Usage examples:
  python tools/osc-tester/send_demo.py --verify-vectors
  python tools/osc-tester/send_demo.py --dry-run --tier balanced --demo long --rows 8 --row-px 256
  python tools/osc-tester/send_demo.py --dry-run --tier lite --demo scroll
  python tools/osc-tester/send_demo.py --oscquery-url http://127.0.0.1:9001/avatar/parameters --tier auto --demo long --loop
  python tools/osc-tester/send_demo.py --tier standard --interval 150 --loop
"""

from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.request
from collections import Counter
from pathlib import Path
from typing import Dict, List, Optional, Sequence, Set, Tuple

from protocol import (
    COLS,
    COMPACT36_BYTES,
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
    MAX_ROW_PX,
    MAX_ROWS,
    MIN_ROW_PX,
    MIN_ROWS,
    PAGE_ROWS,
    ROW_PX,
    ROWS,
    TIER_PAYLOAD_BITS,
    TIERS,
    WIDE_COLS,
    CRTBufferSimulator,
    Packet,
    VirtualScreen,
    encode_geometry_packet,
    encode_screen_packets,
    resolve_live_viewport,
    verify_test_vectors,
    wrap_text,
)

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_VECTORS = REPO_ROOT / "spec" / "test-vectors.json"
OPCODE_DISPLAY_ORDER = ("LUT12", "PAGE", "ASCII", "UTF16", "VIEW", "GEO", "CLEAR", "NOP", "RSVD")


def classify_packet(ctrl: int) -> str:
    c = ctrl & 0xFF
    if c == CTRL_NOP:
        return "NOP"
    if CTRL_UTF16_MIN <= c <= CTRL_UTF16_MAX:
        return "UTF16"
    if CTRL_ASCII_MIN <= c <= CTRL_ASCII_MAX:
        return "ASCII"
    if (CTRL_LUT12_MIN <= c <= CTRL_LUT12_MAX) or c == CTRL_LUT12_LAST:
        return "LUT12"
    if c in (CTRL_CLEAR_ALL, CTRL_CLEAR_ROW):
        return "CLEAR"
    if c == CTRL_SET_VIEW:
        return "VIEW"
    if c == CTRL_SET_GEOMETRY:
        return "GEO"
    if CTRL_PAGE_MIN <= c <= CTRL_PAGE_MAX:
        return "PAGE"
    return "RSVD"


def format_packet_stats(packets: Sequence[Packet], interval_ms: int) -> str:
    counts = Counter(classify_packet(p.ctrl) for p in packets)
    parts = [f"{op}:{counts[op]}" for op in OPCODE_DISPLAY_ORDER if counts[op] > 0]
    breakdown = ", ".join(parts) if parts else "none"
    est_s = (len(packets) * max(0, interval_ms)) / 1000.0
    return f"{len(packets)} pkts (~{est_s:.2f}s @ {interval_ms}ms) [{breakdown}]"


def extract_oscquery_param_names(tree: dict) -> Set[str]:
    """Recursively collect UniText parameter names from a VRChat OSCQuery JSON subtree."""
    names: Set[str] = set()

    def walk(node: object, key_name: Optional[str] = None) -> None:
        if not isinstance(node, dict):
            return
        if key_name and key_name.startswith("UniText_"):
            names.add(key_name)
        full_path = node.get("FULL_PATH")
        if isinstance(full_path, str):
            leaf = full_path.rstrip("/").rsplit("/", 1)[-1]
            if leaf.startswith("UniText_"):
                names.add(leaf)
        contents = node.get("CONTENTS")
        if isinstance(contents, dict):
            for child_key, child_val in contents.items():
                walk(child_val, str(child_key))

    walk(tree)
    return names


def detect_tier_from_oscquery_tree(tree: dict) -> Optional[str]:
    """Detect bandwidth tier from VRChat OSCQuery /avatar/parameters JSON."""
    names = extract_oscquery_param_names(tree)
    if "UniText_D15" in names:
        return "turbo"
    if "UniText_D7" in names:
        return "standard"
    if "UniText_D5" in names:
        return "balanced"
    if "UniText_D3" in names:
        return "compact"
    if "UniText_D2" in names:
        return "lite"
    return None


def detect_viewport_from_oscquery_tree(tree: dict) -> Tuple[int, int, int, bool]:
    """Extract live (row_cells, rows, row_px, has_row_page) from OSCQuery parameters."""
    values: Dict[str, float] = {}

    def walk(node: object, key_name: Optional[str] = None) -> None:
        if not isinstance(node, dict):
            return
        leaf = key_name
        full_path = node.get("FULL_PATH")
        if isinstance(full_path, str):
            leaf = full_path.rstrip("/").rsplit("/", 1)[-1]
        if leaf in ("UniText_Rows", "UniText_RowPx8", "UniText_Width", "UniText_Height") and "VALUE" in node:
            raw = node["VALUE"]
            if isinstance(raw, list) and raw:
                raw = raw[0]
            if isinstance(raw, (int, float)):
                values[leaf] = float(raw)
        contents = node.get("CONTENTS")
        if isinstance(contents, dict):
            for child_key, child_val in contents.items():
                walk(child_val, str(child_key))

    walk(tree)
    names = extract_oscquery_param_names(tree)
    row_cells = WIDE_COLS if "UniText_Bank" in names else COLS
    base_rows = int(round(values.get("UniText_Rows", float(ROWS))))
    base_row_px = int(round(values.get("UniText_RowPx8", float(ROW_PX // 8)))) * 8
    rows, row_px = resolve_live_viewport(
        base_rows,
        base_row_px,
        values.get("UniText_Width", 0.0),
        values.get("UniText_Height", 0.0),
        wide_rows=(row_cells == WIDE_COLS),
    )
    has_row_page = "UniText_RowPage" in names
    return row_cells, rows, row_px, has_row_page


def query_oscquery(url: str, timeout: float = 2.0) -> dict:
    req = urllib.request.Request(url, headers={"Accept": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return json.loads(resp.read().decode("utf-8"))


def build_multilingual_demo_screen(cols: int = WIDE_COLS, rows: int = ROWS) -> VirtualScreen:
    s = VirtualScreen(cols, rows)
    samples = [
        ("Speaker: Alice [Live OSC]", 1, 1),
        ("大家好，这里是 UniText 多语言字幕测试！", 0, 2),
        ("Hello everyone, proportional Latin packs twice as much!", 2, 2),
        ("こんにちは、リアルタイム字幕です", 3, 0),
        ("안녕하세요! 다국어 자막 테스트", 4, 0),
        ("PCVR + Desktop Shared Subtitles Ready", 7, 0),
    ]
    for r, (text, color, flags) in enumerate(samples[:rows]):
        s.set_row_text(r, text)
        s.set_row_style(r, color=color, flags=flags)
    s.set_status(2)
    return s


def build_stress_screens(cols: int = WIDE_COLS, rows: int = ROWS) -> List[VirtualScreen]:
    screens: List[VirtualScreen] = []
    samples = [
        (
            "Frame 1/3: Speech -> ZH/EN/JA",
            "刚才那句话翻译得非常准确！",
            "That translation was super crisp!",
            "さっきの翻訳はとても正確でした！",
            "방금 번역이 정말 정확했어요!",
            "OSC Pacing Stress Test",
            1,
        ),
        (
            "Frame 2/3: Mixed CJK + ASCII",
            "1234567890 ABCDEFGHIJKLMNOPQRSTUVWXYZ",
            "中文混排 English 100% OK 测试中",
            "日本語カタカナひらがな漢字混在",
            "한국어 가나다라마바사 12345678",
            "Tier Bandwidth Comparison",
            2,
        ),
        (
            "Frame 3/3: Rapid Update Check",
            "World Drop 固定模式走远也清晰！",
            "Stand near or walk across room.",
            "ワールド固定でもくっきり読める！",
            "월드 고정 모드에서도 선명해요!",
            "UniText Ready",
            0,
        ),
    ]
    for r0, r1, r2, r3, r4, r5, status in samples:
        s = VirtualScreen(cols, rows)
        lines = [(r0, 1, 1), (r1, 0, 2), (r2, 2, 2), (r3, 3, 0), (r4, 4, 0), (r5, 14, 0)]
        for r, (text, color, flags) in enumerate(lines[:rows]):
            s.set_row_text(r, text)
            s.set_row_style(r, color=color, flags=flags)
        s.set_status(status)
        screens.append(s)
    return screens


SCROLL_DEMO_LINES = [
    ("Alice: 大家好，测试 Ring-Buffer 滚屏！", 0, 2),
    ("Bob: こんにちは！Mixed 日本語 & English wrap!", 2, 0),
    ("Alice: 33b Lite 档也能流畅滚动双语字幕！", 3, 2),
    ("Bob: Proportional Latin saves nearly half the row!", 1, 1),
    ("Alice: 12-bit LUT 压缩节省大量参数位数。", 4, 0),
    ("Bob: 안녕하세요! 한국어도 지원합니다.", 5, 0),
    ("Alice: 无需重传上方历史行的文字内容！", 7, 2),
    ("Bob: Ring-Buffer zero-copy scroll OK!", 2, 2),
]

LONG_DEMO_PARAGRAPHS = [
    (
        "Alice: 今天的 VRChat 实时字幕视窗支持任意调整宽高与显示行数，即使遇到特别长的双语说明，"
        "也能通过增加显示行数与自动滚屏把整句话完整呈现出来，不会在中途被意外截断。",
        0,
        2,
    ),
    (
        "Alice: Resizable viewports and 4-to-12 configurable rows mean long bilingual sentences wrap "
        "to the exact row pixel budget and scroll through any overflow seamlessly.",
        2,
        2,
    ),
    ("Bob: Narrow or tall boards automatically keep every word readable!", 3, 0),
]


class _BaseTransport:
    def set_visible(self, visible: bool) -> None:
        pass

    def send_packet(self, packet: Packet) -> None:
        raise NotImplementedError

    def sleep(self, seconds: float) -> None:
        pass

    def verify_screen(self, screen: VirtualScreen, label: str) -> None:
        pass


class UdpOscTransport(_BaseTransport):
    def __init__(
        self,
        client,
        tier: str,
        mode: str,
        single_nop_gap_s: float = 0.025,
        send_bank: bool = False,
        send_row_page: bool = False,
    ) -> None:
        self.client = client
        self.tier = tier
        self.mode = mode
        self.single_nop_gap_s = max(0.0, single_nop_gap_s)
        self.send_bank = send_bank
        self.send_row_page = send_row_page
        self._last_ctrl = CTRL_NOP

    def set_visible(self, visible: bool) -> None:
        self.client.send_message("/avatar/parameters/UniText_Visible", bool(visible))

    def send_packet(self, packet: Packet) -> None:
        ctrl = packet.ctrl & 0xFF
        if (
            self.mode == "single"
            and ctrl != CTRL_NOP
            and self._last_ctrl != CTRL_NOP
            and self.single_nop_gap_s > 0.0
        ):
            self.client.send_message("/avatar/parameters/UniText_Ctrl", int(CTRL_NOP))
            time.sleep(self.single_nop_gap_s)
        send_packet(
            self.client,
            packet,
            self.tier,
            self.mode,
            send_bank=self.send_bank,
            send_row_page=self.send_row_page,
        )
        self._last_ctrl = ctrl

    def sleep(self, seconds: float) -> None:
        time.sleep(seconds)


class DryRunTransport(_BaseTransport):
    def __init__(self, tier: str, rows: int = ROWS, row_px: int = ROW_PX) -> None:
        self.tier = tier
        self.rows = rows
        self.row_px = row_px
        self.sim = CRTBufferSimulator(rows=rows)
        self.total_packets = 0
        self._geometry_sent = False

    def send_packet(self, packet: Packet) -> None:
        self.sim.apply_packet(packet, tier=self.tier, frames=15)
        if (packet.ctrl & 0xFF) == CTRL_SET_GEOMETRY:
            self._geometry_sent = True
        self.total_packets += 1

    def verify_screen(self, screen: VirtualScreen, label: str) -> None:
        if self.sim.read_top_row() != screen.top_row:
            raise AssertionError(
                f"[DRY-RUN FAIL] {label}: topRow mismatch ({self.sim.read_top_row()} != {screen.top_row})"
            )
        if self.sim.read_visual_cells(screen.cols, screen.rows) != screen.read_visual_cells():
            raise AssertionError(f"[DRY-RUN FAIL] {label}: visual cells mismatch!")
        if self.sim.read_visual_row_styles() != screen.read_visual_row_styles():
            raise AssertionError(f"[DRY-RUN FAIL] {label}: visual row styles mismatch!")
        if self.sim.read_status() != screen.status:
            raise AssertionError(
                f"[DRY-RUN FAIL] {label}: status mismatch ({self.sim.read_status()} != {screen.status})"
            )
        if self._geometry_sent and self.sim.read_geometry() != (self.rows, self.row_px):
            raise AssertionError(
                f"[DRY-RUN FAIL] {label}: geometry mismatch ({self.sim.read_geometry()} != {(self.rows, self.row_px)})"
            )
        row_px = self.sim.read_visual_row_px()
        if row_px != [screen.visual_row_px(r) for r in range(screen.rows)] or max(row_px) > self.row_px:
            raise AssertionError(f"[DRY-RUN FAIL] {label}: layout mismatch/overflow {row_px} (limit {self.row_px})")
        print(
            f"  [DRY-RUN OK] {label} | topRow={self.sim.read_top_row()} "
            f"| status={self.sim.read_status()} | total_sim_pkts={self.total_packets} | row_px={row_px}"
        )


def _send_clear_all(transport: _BaseTransport, tier: str, interval_s: float) -> None:
    transport.send_packet(Packet(CTRL_CLEAR_ALL, (0,) * TIERS[tier]))
    transport.sleep(interval_s)


def _send_geometry(transport: _BaseTransport, tier: str, rows: int, row_px: int, interval_s: float) -> None:
    transport.send_packet(encode_geometry_packet(TIERS[tier], rows, row_px))
    transport.sleep(interval_s)


def _send_nop(transport: _BaseTransport, tier: str) -> None:
    transport.send_packet(Packet(CTRL_NOP, (0,) * TIERS[tier]))


def run_scroll_demo(
    transport: _BaseTransport,
    args: argparse.Namespace,
    interval_s: float,
    clear_init: bool,
    cols: int,
    rows: int,
    row_px: int,
) -> int:
    if clear_init:
        _send_clear_all(transport, args.tier, interval_s)
    _send_geometry(transport, args.tier, rows, row_px, interval_s)
    _send_geometry(transport, args.tier, rows, row_px, interval_s)

    screen = VirtualScreen(cols, rows)
    initial = [
        ("UniText Ring-Buffer Demo", 7, 0),
        ("开启硬件滚屏后无需重传历史行", 1, 1),
        ("12-bit LUT 汉字吞吐翻倍", 3, 0),
        ("日文假名走 0x30 单字节页通道", 2, 0),
        ("おはようございます！", 4, 0),
        ("Ready for live scrolling...", 0, 2),
    ]
    for r, (text, color, flags) in enumerate(initial[:rows]):
        screen.set_row_text(r, text)
        screen.set_row_style(r, color=color, flags=flags)
    screen.set_status(1)

    init_pkts = encode_screen_packets(screen, tier=args.tier, dirty_only=False)
    print(f"[Init] {format_packet_stats(init_pkts, args.interval)}")
    for pkt in init_pkts:
        transport.send_packet(pkt)
        transport.sleep(interval_s)
    screen.mark_clean()
    _send_nop(transport, args.tier)
    transport.verify_screen(screen, "scroll/init")

    try:
        step = 0
        while True:
            transport.sleep(1.0)
            text, color, flags = SCROLL_DEMO_LINES[step % len(SCROLL_DEMO_LINES)]
            screen.scroll_up(1)
            screen.set_row_text(rows - 1, text)
            screen.set_row_style(rows - 1, color=color, flags=flags)
            screen.set_status(2 if (step % 2 == 0) else 1)
            pkts = encode_screen_packets(screen, tier=args.tier, dirty_only=True)
            print(
                f"[Scroll {step + 1}] topRow={screen.top_row} | "
                f"{format_packet_stats(pkts, args.interval)} -> {text}"
            )
            for pkt in pkts:
                transport.send_packet(pkt)
                transport.sleep(interval_s)
            screen.mark_clean()
            _send_nop(transport, args.tier)
            transport.verify_screen(screen, f"scroll/step-{step + 1}")
            if not args.loop:
                break
            step += 1
    except KeyboardInterrupt:
        print("\nStopped by user.")

    return 0


def run_long_demo(
    transport: _BaseTransport,
    args: argparse.Namespace,
    interval_s: float,
    clear_init: bool,
    cols: int,
    rows: int,
    row_px: int,
) -> int:
    if clear_init:
        _send_clear_all(transport, args.tier, interval_s)
    _send_geometry(transport, args.tier, rows, row_px, interval_s)

    wrapped_entries: List[Tuple[str, int, int]] = []
    for text, color, flags in LONG_DEMO_PARAGRAPHS:
        for line in wrap_text(text, max_px=row_px, max_cells=cols):
            wrapped_entries.append((line, color, flags))

    screen = VirtualScreen(cols=cols, rows=rows)
    initial_count = min(rows, len(wrapped_entries))
    for r in range(initial_count):
        line, color, flags = wrapped_entries[r]
        screen.set_row_text(r, line)
        screen.set_row_style(r, color=color, flags=flags)
    screen.set_status(2)

    init_pkts = encode_screen_packets(screen, tier=args.tier, dirty_only=False)
    print(f"[Viewport {rows}r×{row_px}px Init] {format_packet_stats(init_pkts, args.interval)}")
    for pkt in init_pkts:
        transport.send_packet(pkt)
        transport.sleep(interval_s)
    screen.mark_clean()
    _send_nop(transport, args.tier)
    transport.verify_screen(screen, "long/init")

    overflow_entries = wrapped_entries[initial_count:] or wrapped_entries

    try:
        cycle = 0
        while True:
            for step, (line, color, flags) in enumerate(overflow_entries):
                transport.sleep(0.8)
                screen.scroll_up(1)
                screen.set_row_text(rows - 1, line)
                screen.set_row_style(rows - 1, color=color, flags=flags)
                screen.set_status(1 if (step % 2 == 0) else 2)

                pkts = encode_screen_packets(screen, tier=args.tier, dirty_only=True)
                print(
                    f"[Long Scroll {step + 1}/{len(overflow_entries)}] topRow={screen.top_row} | "
                    f"{format_packet_stats(pkts, args.interval)} -> {line}"
                )
                for pkt in pkts:
                    transport.send_packet(pkt)
                    transport.sleep(interval_s)
                screen.mark_clean()
                _send_nop(transport, args.tier)
                transport.verify_screen(screen, f"long/scroll-{cycle}-{step + 1}")

            if not args.loop:
                break
            cycle += 1
    except KeyboardInterrupt:
        print("\nStopped by user.")

    return 0


def send_packet(
    client,
    packet: Packet,
    tier: str,
    mode: str,
    send_bank: bool = False,
    send_row_page: bool = False,
) -> None:
    from pythonosc.osc_bundle_builder import IMMEDIATELY, OscBundleBuilder
    from pythonosc.osc_message_builder import OscMessageBuilder

    n_bytes = TIERS[tier]
    data = packet.padded_data(n_bytes)
    int_bytes = 4 if n_bytes == COMPACT36_BYTES else n_bytes

    if mode == "bundle":
        bundle = OscBundleBuilder(IMMEDIATELY)
        for idx in range(int_bytes):
            msg = OscMessageBuilder(f"/avatar/parameters/UniText_D{idx}")
            msg.add_arg(int(data[idx]), arg_type="i")
            bundle.add_content(msg.build())
        if n_bytes == COMPACT36_BYTES:
            d4 = data[4]
            for bit in range(4, 8):
                bit_msg = OscMessageBuilder(f"/avatar/parameters/UniText_D4b{bit}")
                bit_msg.add_arg(bool((d4 >> bit) & 1))
                bundle.add_content(bit_msg.build())
        if send_bank:
            bank_msg = OscMessageBuilder("/avatar/parameters/UniText_Bank")
            bank_msg.add_arg(bool(packet.bank))
            bundle.add_content(bank_msg.build())
        if send_row_page:
            rp_msg = OscMessageBuilder("/avatar/parameters/UniText_RowPage")
            rp_msg.add_arg(bool(packet.row_page))
            bundle.add_content(rp_msg.build())
        ctrl_msg = OscMessageBuilder("/avatar/parameters/UniText_Ctrl")
        ctrl_msg.add_arg(int(packet.ctrl), arg_type="i")
        bundle.add_content(ctrl_msg.build())
        client.send(bundle.build())
    elif mode == "single":
        for idx in range(int_bytes):
            client.send_message(f"/avatar/parameters/UniText_D{idx}", int(data[idx]))
        if n_bytes == COMPACT36_BYTES:
            d4 = data[4]
            for bit in range(4, 8):
                client.send_message(f"/avatar/parameters/UniText_D4b{bit}", bool((d4 >> bit) & 1))
        if send_bank:
            client.send_message("/avatar/parameters/UniText_Bank", bool(packet.bank))
        if send_row_page:
            client.send_message("/avatar/parameters/UniText_RowPage", bool(packet.row_page))
        client.send_message("/avatar/parameters/UniText_Ctrl", int(packet.ctrl))
    else:
        raise ValueError(f"Unsupported mode: {mode}")


def _resolve_options(args: argparse.Namespace) -> int:
    if args.tier == "auto" and not args.oscquery_url:
        print("ERROR: --tier auto requires --oscquery-url.", file=sys.stderr)
        return 1

    detected_cols: Optional[int] = None
    detected_rows: Optional[int] = None
    detected_row_px: Optional[int] = None
    if args.oscquery_url:
        try:
            tree = query_oscquery(args.oscquery_url)
        except Exception as exc:
            print(f"ERROR: Failed querying OSCQuery at {args.oscquery_url}: {exc}", file=sys.stderr)
            return 1
        detected_tier = detect_tier_from_oscquery_tree(tree)
        detected_cols, detected_rows, detected_row_px, _ = detect_viewport_from_oscquery_tree(tree)
        print(
            f"OSCQuery detected: tier={detected_tier}, "
            f"row_cells={detected_cols}, rows={detected_rows}, row_px={detected_row_px}"
        )
        if args.tier == "auto":
            if detected_tier is None:
                print("ERROR: UniText prop not detected in OSCQuery parameters.", file=sys.stderr)
                return 1
            args.tier = detected_tier

    if args.row_cells == "auto":
        resolved_cols = detected_cols if detected_cols is not None else WIDE_COLS
    else:
        resolved_cols = int(args.row_cells)
    args.row_cells = str(resolved_cols)

    if args.rows == "auto":
        resolved_rows = detected_rows if detected_rows is not None else (8 if args.demo == "long" else ROWS)
    else:
        resolved_rows = int(args.rows)
    if not (MIN_ROWS <= resolved_rows <= MAX_ROWS):
        print(f"ERROR: --rows must be between {MIN_ROWS} and {MAX_ROWS}.", file=sys.stderr)
        return 1
    args.rows = str(resolved_rows)

    if args.row_px == "auto":
        resolved_row_px = detected_row_px if detected_row_px is not None else ROW_PX
    else:
        resolved_row_px = int(args.row_px)
    if not (MIN_ROW_PX <= resolved_row_px <= MAX_ROW_PX):
        print(f"ERROR: --row-px must be between {MIN_ROW_PX} and {MAX_ROW_PX}.", file=sys.stderr)
        return 1
    args.row_px = str(resolved_row_px)

    return 0


def run_osc_sender(args: argparse.Namespace) -> int:
    rc = _resolve_options(args)
    if rc != 0:
        return rc

    row_cells = int(args.row_cells)
    rows = int(args.rows)
    row_px = int(args.row_px)
    send_bank = row_cells == WIDE_COLS
    send_row_page = rows > PAGE_ROWS
    interval_s = max(0.01, args.interval / 1000.0)
    clear_init = not args.no_clear_init

    if args.dry_run:
        transport: _BaseTransport = DryRunTransport(args.tier, rows=rows, row_px=row_px)
        target_desc = "DRY-RUN (CRTBufferSimulator, 15-frame idempotency)"
    else:
        try:
            from pythonosc.udp_client import SimpleUDPClient
        except ImportError:
            print(
                "ERROR: python-osc is required for live UDP OSC sending (or pass --dry-run). "
                "Install with: pip install -r tools/osc-tester/requirements.txt",
                file=sys.stderr,
            )
            return 1
        client = SimpleUDPClient(args.host, args.port)
        transport = UdpOscTransport(
            client,
            args.tier,
            args.mode,
            single_nop_gap_s=max(0, args.single_nop_gap) / 1000.0,
            send_bank=send_bank,
            send_row_page=send_row_page,
        )
        target_desc = f"{args.host}:{args.port} | mode={args.mode}"

    print(
        f"UniText OSC -> {target_desc} | tier={args.tier} "
        f"({TIER_PAYLOAD_BITS[args.tier]}b) | viewport={row_cells}c×{rows}r@{row_px}px | interval={args.interval}ms"
    )

    if args.visible is not None:
        transport.set_visible(bool(args.visible))

    if args.demo == "clear":
        _send_clear_all(transport, args.tier, interval_s)
        _send_nop(transport, args.tier)
        transport.verify_screen(VirtualScreen(row_cells, rows), "clear")
        print("Sent CMD_CLEAR_ALL.")
        return 0

    if args.demo == "scroll":
        return run_scroll_demo(transport, args, interval_s, clear_init, cols=row_cells, rows=rows, row_px=row_px)

    if args.demo == "long":
        return run_long_demo(transport, args, interval_s, clear_init, cols=row_cells, rows=rows, row_px=row_px)

    if args.demo == "multilingual":
        screens = [build_multilingual_demo_screen(row_cells, rows)]
    elif args.demo == "stress":
        screens = build_stress_screens(row_cells, rows)
    else:
        raise ValueError(f"Unknown demo mode: {args.demo}")

    try:
        iteration = 0
        while True:
            screen = screens[iteration % len(screens)]
            pkts = encode_screen_packets(screen, tier=args.tier, dirty_only=False)
            if (iteration == 0 and clear_init) or (iteration > 0 and args.demo == "stress"):
                _send_clear_all(transport, args.tier, interval_s)
            if iteration == 0:
                _send_geometry(transport, args.tier, rows, row_px, interval_s)
            print(f"[Pass {iteration + 1}] {format_packet_stats(pkts, args.interval)}")
            for pkt in pkts:
                transport.send_packet(pkt)
                transport.sleep(interval_s)

            _send_nop(transport, args.tier)
            transport.verify_screen(screen, f"{args.demo}/pass-{iteration + 1}")

            if not args.loop:
                break
            iteration += 1
            transport.sleep(1.5 if args.demo == "stress" else 0.5)
    except KeyboardInterrupt:
        print("\nStopped by user.")

    return 0


def parse_args(argv: Optional[Sequence[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="UniText OSC tester, dry-run verifier, and vector runner.")
    parser.add_argument(
        "--verify-vectors",
        nargs="?",
        const=str(DEFAULT_VECTORS),
        default=None,
        help="Verify test vectors JSON (defaults to spec/test-vectors.json) and exit.",
    )
    parser.add_argument("--host", default="127.0.0.1", help="Target OSC host (default: 127.0.0.1).")
    parser.add_argument("--port", type=int, default=9000, help="Target VRChat OSC port (default: 9000).")
    parser.add_argument(
        "--oscquery-url",
        default=None,
        help="Optional VRChat OSCQuery URL (e.g. http://127.0.0.1:9001/avatar/parameters) for auto-detection.",
    )
    parser.add_argument(
        "--row-cells",
        choices=("32", "64", "auto"),
        default="auto",
        help="Row cell width (64 = Wide Rows with UniText_Bank, 32 = narrow fallback, default: auto).",
    )
    parser.add_argument("--rows", default="auto", help="Visible row count (4..12, default: auto).")
    parser.add_argument("--row-px", default="auto", help="Logical row pixel width (192..640, default: auto).")
    parser.add_argument(
        "--tier",
        choices=("lite", "compact", "balanced", "standard", "turbo", "auto"),
        default="standard",
        help="Bandwidth tier (default: standard; 'auto' requires --oscquery-url).",
    )
    parser.add_argument(
        "--interval",
        type=int,
        default=150,
        help="Milliseconds between OSC packets (recommended: 100/150/200, default: 150).",
    )
    parser.add_argument(
        "--mode",
        choices=("bundle", "single"),
        default="bundle",
        help="Send an atomic OSC Bundle or individual OSC messages (D* then Ctrl) (default: bundle).",
    )
    parser.add_argument(
        "--single-nop-gap",
        type=int,
        default=25,
        help="Milliseconds to hold Ctrl=0 (NOP) before updating D* in single mode (0 disables, default: 25).",
    )
    parser.add_argument(
        "--demo",
        choices=("multilingual", "stress", "scroll", "long", "clear"),
        default="multilingual",
        help="Demo payload preset (default: multilingual).",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Run locally through CRTBufferSimulator (15-frame idempotency + visual assertion) instead of UDP.",
    )
    parser.add_argument("--no-clear-init", action="store_true", help="Skip initial CMD_CLEAR_ALL.")
    parser.add_argument("--visible", dest="visible", action="store_true", default=None, help="Set UniText_Visible = True.")
    parser.add_argument("--no-visible", dest="visible", action="store_false", help="Set UniText_Visible = False.")
    parser.add_argument("--loop", action="store_true", help="Continuously cycle / re-transmit for Late-Joiner testing.")
    return parser.parse_args(argv)


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = parse_args(argv)
    if args.verify_vectors is not None:
        vec_path = DEFAULT_VECTORS if args.verify_vectors.strip().lower() in ("", "all") else Path(args.verify_vectors)
        print(f"Verifying test vectors in {vec_path} ...")
        count = verify_test_vectors(vec_path)
        print(f"All {count} test vectors PASSED.")
        return 0
    return run_osc_sender(args)


if __name__ == "__main__":
    raise SystemExit(main())
