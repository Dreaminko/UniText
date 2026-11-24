# UniText Protocol Specification (`v2`)

This document is the **single source of truth** for the UniText Avatar 3.0 OSC subtitle display protocol, `64 × 32` CRT memory layout, block-indexed packed Unicode BMP font atlas (`FontAtlas_BMP_Packed.png`), unified `64 × 64` auxiliary lookup texture (`UniTextAux_64x64.png`), proportional glyph advance table ([`spec/glyph-advance.json`](../spec/glyph-advance.json)), 12-bit LUT codebook ([`spec/lut12.json`](../spec/lut12.json)), Ring-Buffer hardware scroll semantics, `CMD_SET_GEOMETRY` resizable viewport geometry, and sender scheduling behavior. Both **VRCS** and third-party senders must conform to this specification and pass [`spec/test-vectors.json`](../spec/test-vectors.json).

---

## 1. Logical Screen Grid & Ring-Buffer Mapping

### 1.1 Row & Cell Capacity (`32` / `64` Columns × `4..12` Rows)

- **Columns per Row**:
  - **Narrow (`UniText_Bank` absent)**: `32` columns per row.
  - **Wide Rows (`UniText_Bank` present, `+1` synced bit)**: `64` columns per row (`2` banks × `32` cols).
- **Visible Rows (`R ∈ [4, 12]`, default `6`)**:
  - Minimum guaranteed display height is **`4`** rows (`MIN_DISPLAY_ROWS = 4`) to prevent long-sentence truncation.
  - Up to `6` rows require **`0`** row-page bits. Configurations with `7..12` rows sync **`UniText_RowPage`** (`+1` bit) to address `2` row pages × `6` rows = up to `12` rows × `64` cols = **`768` cells**.
- **Visual vs. Physical Row Mapping (Ring-Buffer Scroll)**:
  - Given configured active row count `R ∈ [4, 12]` (reported by `UniText_Rows`, default `6`), the CRT stores `topRow ∈ [0, R - 1]` at metadata pixel `(30, 12)` (power-on default `0`).
  - Given a visual row `visual_row ∈ [0, R - 1]` (`0` = top visual line, `R - 1` = bottom visual line):
    ```text
    phys_row = (visual_row + topRow) % R
    row_page = phys_row / 6              (0 for phys_row 0..5, 1 for phys_row 6..11)
    page_row = phys_row % 6              (0..5 within the selected row page)
    bank     = col >> 5                  (0 for cols 0..31, 1 for cols 32..63)
    bank_col = col & 31                  (0..31 within the selected bank plane)
    cell     = page_row * 32 + bank_col  (0..191 within the selected bank + rowPage plane)
    ```
  - When `topRow == 0`, `phys_row == visual_row`.
- **Cell Content**: 16-bit Unicode Basic Multilingual Plane (BMP) codepoint `cp ∈ [0x0000, 0xFFFF]`.
  - `0x0000` represents an **empty / transparent cell**.
  - Senders must map non-BMP characters (`cp > 0xFFFF`) to an in-BMP replacement (e.g., `U+FFFD` or space `U+0020`) before encoding.

### 1.2 Proportional Layout, Row Width & Long-Sentence Overflow

- **Row Pixel Budget (`UniText_RowPx8`)**: Default base row width is **`384` glyph pixels** (`32` full-width `12 px` CJK glyphs). `UniText_RowPx8` (`Int`, local-only) exposes the base width `base_row_px = UniText_RowPx8 * 8 ∈ [192, 640]`. When `UniText_Width` / `UniText_Height` (`Float ∈ [-1, +1]`) are present, live `(rows, row_px)` scale smoothly around the base config (see Section 2.2).
- **Proportional Advances (`0..12 px`)**:
  - Latin / ASCII / punctuation glyphs use their proportional advance from [`spec/glyph-advance.json`](../spec/glyph-advance.json) (e.g., `'i' = 4 px`, `' ' = 6 px`, `'a' = 6 px`, `'W' = 10 px`), fitting `~55–64` English chars in `384 px`.
  - Full-width CJK ideographs, Kana, and Hangul remain `12 px`.
  - `BufferCRT.shader` reads the identical advances from `UniTextAux_64x64.png` and writes cumulative `(xStart, advance)` into CRT layout rows `16..27`. `Display.shader` binary-searches the layout row (`6` iterations over `64` cells) to sample each glyph left-aligned at `xStart`.
- **Sender Greedy Line Wrapping & Overflow Guarantee**:
  - A row must satisfy **both** `sum(advance(cp)) <= row_px` **and** `len(chars) <= C` (`64` when `UniText_Bank` is present, else `32`).
  - Wrap Latin text at spaces (hard-splitting overlong tokens) and allow CJK breaks between any two wide characters, dropping the trailing space at a wrap point.
  - **Long-Sentence Overflow**: When wrapped lines exceed `R` visible rows, senders MUST scroll or page through remaining lines rather than truncating trailing text.

---

## 2. OSC Parameters & OSCQuery Capability Discovery

All data channels **MUST** be declared as **`Int`** (`0..255`) in VRChat `VRCExpressionParameters`.
**NEVER** use synced `Float` parameters for byte payloads—VRChat quantizes synced floats to ~7-bit signed precision, causing ±1 bit flips that corrupt CJK codepoints.

### 2.1 Avatar Parameters

| Parameter Name | OSC Path | VRChat Sync Type | Synced Bits | Lite (`33b`+) | Compact (`45b`+) | Balanced (`57b`+) | Standard (`73b`+, Default) | Turbo (`137b`+) | Saved | Default | Purpose |
| :--- | :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :--- |
| `UniText_Visible` | `/avatar/parameters/UniText_Visible` | `Bool` | 1 | Required | Required | Required | Required | Required | `true` | `false` | Master prop visibility toggle |
| `UniText_Ctrl` | `/avatar/parameters/UniText_Ctrl` | `Int` | 8 | Required | Required | Required | Required | Required | `false` | `0` | Address / command byte (`0..255`) |
| `UniText_D0` .. `UniText_D2` | `/avatar/parameters/UniText_D0..D2` | `Int` × 3 | 24 | Required | Required | Required | Required | Required | `false` | `0` | Payload bytes `0..2` |
| `UniText_D3` | `/avatar/parameters/UniText_D3` | `Int` | 8 | — | Required | Required | Required | Required | `false` | `0` | Payload byte `3` |
| `UniText_D4b4` .. `UniText_D4b7` | `/avatar/parameters/UniText_D4b4..D4b7` | `Bool` × 4 | 4 | — | Required | — | — | — | `false` | `false` | Compact36 high nibble (`bits 4..7`) of payload byte `4` |
| `UniText_D4` .. `UniText_D5` | `/avatar/parameters/UniText_D4..D5` | `Int` × 2 | 16 | — | — | Required | Required | Required | `false` | `0` | Payload bytes `4..5` |
| `UniText_D6` .. `UniText_D7` | `/avatar/parameters/UniText_D6..D7` | `Int` × 2 | 16 | — | — | — | Required | Required | `false` | `0` | Payload bytes `6..7` |
| `UniText_D8` .. `UniText_D15` | `/avatar/parameters/UniText_D8..D15` | `Int` × 8 | 64 | — | — | — | — | Required | `false` | `0` | Payload bytes `8..15` |
| `UniText_Bank` | `/avatar/parameters/UniText_Bank` | `Bool` | 1 | Optional | Optional | Optional | Optional | Optional | `false` | `false` | Wide Rows bank selector (`false` = cols `0..31`, `true` = cols `32..63`) |
| `UniText_RowPage` | `/avatar/parameters/UniText_RowPage` | `Bool` | 1 | Optional | Optional | Optional | Optional | Optional | `false` | `false` | Extended Rows page selector (`false` = rows `0..5`, `true` = rows `6..11`; registered when `displayRows > 6` or live resize enabled) |
| `UniText_Width` | `/avatar/parameters/UniText_Width` | `Float` (local-only) | **0** | Optional | Optional | Optional | Optional | Optional | `true` | `0.0` | In-game `Resize (W)` axis (`-1.0 .. +1.0`; VRCS reads locally and syncs result via `CMD_SET_GEOMETRY`) |
| `UniText_Height` | `/avatar/parameters/UniText_Height` | `Float` (local-only) | **0** | Optional | Optional | Optional | Optional | Optional | `true` | `0.0` | In-game `Resize (H)` axis (`-1.0 .. +1.0`; VRCS reads locally and syncs result via `CMD_SET_GEOMETRY`) |
| `UniText_ProtoVer` | `/avatar/parameters/UniText_ProtoVer` | `Int` (local-only) | **0** | Present | Present | Present | Present | Present | `false` | `2` | Protocol version marker (`2`) |
| `UniText_Rows` | `/avatar/parameters/UniText_Rows` | `Int` (local-only) | **0** | Present | Present | Present | Present | Present | `false` | `6` | Configured base visible row count `base_rows ∈ [4, 12]` |
| `UniText_RowPx8` | `/avatar/parameters/UniText_RowPx8` | `Int` (local-only) | **0** | Present | Present | Present | Present | Present | `false` | `48` | Base logical row width in 8 px units (`base_row_px = VALUE * 8`, `192..640 px`) |

Local / UI prop-control parameters (`0` synced bits unless `syncWorldDrop` is enabled):
- `UniText_WorldDrop` (`Bool`, `1` synced bit when `syncWorldDrop = true`): `false = Side Follow`, `true = World Drop (FreezeToWorld)`.
- `UniText_Billboard`, `UniText_LocalClear` (`Bool`, local-only), `UniText_Scale`, `UniText_Opacity` (`Float`, local-only).

### 2.2 OSCQuery Auto-Detection & Live Resize Reflow

Senders query VRChat OSCQuery at `/avatar/parameters`:
1. **Tier**: check payload parameters in descending order (`UniText_D15` → Turbo `128b`, `UniText_D7` → Standard `64b`, `UniText_D5` → Balanced `48b`, `UniText_D3` → Compact36 `36b`, `UniText_D2` → Lite `24b`).
2. **Base & Live Viewport Geometry**:
   - `cols = 64` if `UniText_Bank` exists, else `32`.
   - `base_rows = clamp(UniText_Rows.VALUE, 4, 12)` (default `6`).
   - `base_row_px = clamp(UniText_RowPx8.VALUE * 8, 192, cols == 64 ? 640 : 384)` (default `384`).
   - Read `w = clamp(UniText_Width, -1.0, 1.0)` and `h = clamp(UniText_Height, -1.0, 1.0)` (default `0.0`), and listen to incoming VRChat OSC updates on `/avatar/parameters/UniText_Width` and `/avatar/parameters/UniText_Height`:
     ```text
     row_px_min  = clamp(round_to_8(base_row_px * 0.5), 192, base_row_px)
     row_px_max  = clamp(round_to_8(base_row_px * 1.5), base_row_px, cols == 64 ? 640 : 384)
     live_row_px = clamp(round_to_8(w < 0 ? base_row_px + w * (base_row_px - row_px_min) : base_row_px + w * (row_px_max - base_row_px)), 192, cols == 64 ? 640 : 384)
     live_rows   = clamp(round(h < 0 ? base_rows + h * (base_rows - 4) : base_rows + h * (12 - base_rows)), 4, 12)
     ```
   - When `live_rows` or `live_row_px` changes (recommended debounce ~`100 ms`), re-wrap active subtitle history to `(live_rows, live_row_px)`, emit `CMD_SET_GEOMETRY` (`Ctrl = 251`) to update all remote viewers, and refresh the buffer (`CMD_CLEAR_ALL` on row-count change or differential update).
   - If `live_rows > 6` (and `UniText_RowPage` exists), stamp packets with `UniText_RowPage` (`false` for rows `0..5`, `true` for rows `6..11`).

---

## 3. Idempotent `Ctrl` Command Set & Addressing

Because `BufferCRT.shader` runs every render frame (60–90+ FPS) while a set of OSC parameters persists for 120–200 ms (~10–18 frames), **every command MUST be 100% idempotent and absolute-addressed**. Relative commands (such as relative `SCROLL_UP`) are prohibited; Ring-Buffer scrolling works by updating the absolute `topRow ∈ [0, R - 1]` index via `CMD_SET_VIEW`.

All text write opcodes (`WRITE_UTF16`, `WRITE_ASCII`, `WRITE_LUT12`, `WRITE_PAGE`) operate on the `32 × 6` (`192`-cell) plane selected by `(UniText_Bank, UniText_RowPage)` and leave all other planes untouched. Global / row-level commands (`CMD_CLEAR_ALL`, `CMD_CLEAR_ROW`, `CMD_SET_VIEW`) act across the entire buffer / full `64`-cell rows regardless of `Bank` or `RowPage`.

Let `N ∈ {3, 5, 6, 8, 16}` be the packet payload byte span (`D0 .. D{N-1}`). On `Compact36` (`N = 5`), `D0..D3` are `Int` (`0..255`), `D4` bits `4..7` come from `UniText_D4b4..D4b7` (`Bool`), and `D4` bits `0..3` are masked to `0`. Let `fullBytes = (N == 5) ? 4 : N` be the whole-byte count usable by byte-oriented opcodes.

| `Ctrl` Value | Instruction | Semantics & Boundary Guards |
| :---: | :--- | :--- |
| `0` | `NOP` | **Power-on safe default.** Preserves previous CRT buffer (`_SelfTexture2D`). |
| `1 ..= 96` | `WRITE_UTF16` | `oddOffset = (N == 3) ? (D2 & 1) : 0`. `start_cell = (Ctrl - 1) * 2 + oddOffset` in the selected `(bank, rowPage)` plane. Writes `count = floor(N / 2)` cells (`1` on `N=3`, `2` on `5/4`, `3` on `6`, `4` on `8`, `8` on `16`): for `i ∈ [0, count)`: `cell = start_cell + i`; **if `cell < 192`**, writes `cp = (D[2i] << 8) \| D[2i + 1]` at `(bank * 32 + (cell % 32), rowPage * 6 + (cell / 32))`. |
| `97 ..= 144` | `WRITE_ASCII` | `start_cell = (Ctrl - 97) * 4` (`0..188`) in the selected `(bank, rowPage)` plane. Writes `count = fullBytes` cells (`3` on `N=3`, `4` on `5`, `6` on `6`, `8` on `8`, `16` on `16`): for `i ∈ [0, count)`: `cell = start_cell + i`; **if `cell < 192`**, `cp = D[i]` (zero high byte). |
| `145 ..= 239`, `243` | `WRITE_LUT12` | `slot = (Ctrl == 243) ? 95 : (Ctrl - 145)` (`0..95`). **On `Compact36` (`N = 5`)**: if `slot < 66`, `targetRow = slot / 11` (`0..5`), `startCol = (slot % 11) * 3` (`0, 3, ..., 30`), unpacks `3` 12-bit indices (clamped at `bankCol < 32`) from `D0..D3` + `D4 & 0xF0` (`slot >= 66` is ignored). **On `N ∈ {3, 6, 8, 16}`**: `oddOffset = (N == 8) ? (D7 & 1) : 0`, `start_cell = slot * 2 + oddOffset`, unpacks `count = floor(2N / 3)` indices (`2` on `N=3`, `4` on `6`, `5` on `8`, `10` on `16`), **if `cell < 192`**. Decodes via `UniTextAux_64x64.png` LUT12 region. |
| `240` | `CMD_CLEAR_ALL` | Clears all `64 × 12` text cells (`0x0000`), resets all 12 row styles (`color=0, flags=0`), resets `topRow = 0`, clears global status (`0`), and resets layout rows (`16..27` recompute to `0`), while **preserving live viewport geometry at `(29, 12)`**. |
| `241` | `CMD_CLEAR_ROW` | `mask = (D0 & 0x3F) \| ((D1 & 0x3F) << 6)`. Clears all `64` text cells (both banks) in every **physical** row `py ∈ [0, 11]` where `(mask >> py) & 1 == 1`. Preserves metadata row `12`, `topRow`, and status. |
| `244 ..= 249` | `WRITE_PAGE` | Targets physical row `py = (Ctrl - 244) + rowPage * 6` in the selected `(bank, rowPage)` plane. `startCol = D0 & 0x1F` (`0..31`), `pageSel = (D0 >> 5) & 0x07` (`0→0x30` Kana/CJK Punct, `1→0xFF` Fullwidth, `2→0x04` Cyrillic, `3→0x20` General Punct, `4→0x00` Latin-1). Writes `count = fullBytes - 1` cells (`2` on `N=3`, `3` on `5`, `5` on `6`, `7` on `8`, `15` on `16`) starting at `startCol` (**clamped at `bankCol < 32`**): `lo = D[1 + i]`, `cp = (lo == 0) ? 0 : (pageHi << 8) \| lo`. |
| `250` | `CMD_SET_VIEW` | Atomic 3-byte view/style/status update on metadata row `12` (compatible with all tiers including `Lite 33b`): `newTopRow = D0 & 0x0F` (`< 12` sets `(30, 12)`), `newStatus = (D0 >> 4) & 0x0F`, `targetRow = (D1 >> 4) & 0x0F` (`< 12` sets `(targetRow, 12)` to `color = D1 & 0x0F`, `flags = D2 & 0x3F`), `updateStatus = (D2 >> 7) & 1` (`1` sets `(31, 12)` to `newStatus`). Pass `15` (`0xF`) in `targetRow` / `newTopRow` to leave them unchanged. |
| `251` | `CMD_SET_GEOMETRY` | Live viewport geometry update on metadata pixel `(29, 12)` (compatible with all tiers): `D0 = rows ∈ [4, 12]`, `D1 = row_px / 8 ∈ [24, 80]` (`192..640 px`). Out-of-range values are ignored (`NOP`). Survives `CMD_CLEAR_ALL` and `UniText_LocalClear`. |
| `242`, `252 ..= 255` | Reserved | Ignored (`NOP`). |

### 3.1 `CMD_SET_VIEW` Bitfields & Standard 16-Color Palette

- **Row Style `flags` (`bits 0..5`)**:
  - `bit 0` (`0x01`): **Dim / Original Speech** (`1` = attenuated opacity, e.g., source transcript)
  - `bit 1` (`0x02`): **Highlight / Translated** (`1` = subtle highlight band + brightened text)
  - `bits 2..5`: Reserved (`0`)
- **Global Status Indicator (`0..15`)**:
  - `0`: `Idle` (clean neutral border, no active accent)
  - `1`: `Listening` (subtle cyan/teal top-border breathing accent)
  - `2`: `Translating` (subtle amber/gold top-border breathing accent)
  - `3..15`: Reserved
- **`color` (`0..15`) Built-in 16-Color Palette**:

| Index | Name | Hex | RGB (0–255) | Typical Role |
| :---: | :--- | :---: | :---: | :--- |
| `0` | Pure White | `#FFFFFF` | `(255, 255, 255)` | Default primary text |
| `1` | Soft Silver | `#CBD5E1` | `(203, 213, 225)` | Secondary / Original speech |
| `2` | Sky Cyan | `#38BDF8` | `(56, 189, 248)` | English / Primary translation |
| `3` | Mint Green | `#4ADE80` | `(74, 222, 128)` | Confirmed / Japanese |
| `4` | Warm Amber | `#FBBF24` | `(251, 191, 36)` | Highlight / Korean |
| `5` | Coral Rose | `#FB7185` | `(251, 113, 133)` | Speaker B / Alert |
| `6` | Lavender | `#C084FC` | `(192, 132, 252)` | Speaker C / Note |
| `7` | Aqua Teal | `#2DD4BF` | `(45, 212, 191)` | System / Status |
| `8` | Soft Peach | `#FDBA74` | `(253, 186, 116)` | Warm accent |
| `9` | Periwinkle | `#818CF8` | `(129, 140, 248)` | Cool accent |
| `10` | Lime | `#A3E635` | `(163, 230, 53)` | Accent |
| `11` | Pink | `#F472B6` | `(244, 114, 182)` | Accent |
| `12` | Gold | `#FACC15` | `(250, 204, 21)` | Accent |
| `13` | Ice Blue | `#7DD3FC` | `(125, 211, 252)` | Accent |
| `14` | Muted Slate | `#94A3B8` | `(148, 163, 184)` | Dimmed metadata |
| `15` | Bright Cream | `#FEF3C7` | `(254, 243, 199)` | High-contrast warm text |

---

## 4. `64 × 32` CRT Memory Layout

- **Unity Asset**: `CustomRenderTexture` (`Width = 64`, `Height = 32`, named `Buffer_64x32_<Tier>.asset`)
- **Format**: `ARGB32` / `R8G8B8A8_UNorm` (**Linear — sRGB MUST be OFF**), `Filter = Point`, `Wrap = Clamp`, `Mipmap = OFF`, `Double Buffered = ON`, `Update Mode = Realtime`.
- **UV / Pixel Coordinate Convention**:
  - `px = floor(uv.x * 64.0)` (`0..63`, left-to-right)
  - `py = floor(uv.y * 32.0)` (`0..31`, in Unity's native texture `uv.y ∈ [0, 1]` space)
  - Both `BufferCRT.shader` (reading `_SelfTexture2D` at `IN.globalTexcoord.xy`) and `Display.shader` (sampling `_BufferTex` via `tex2Dlod`) operate in identical native texture UV space, eliminating platform-specific render-target Y-inversion bugs across D3D11/OpenGL/Vulkan.
  - Pixel payload encoding:
    - **`py ∈ [0, 11]` (768 physical character cells: `rowPage 0` = rows `0..5`, `rowPage 1` = rows `6..11`; `bank 0` = cols `0..31`, `bank 1` = cols `32..63`)**:
      - `R = ((cp >> 8) & 0xFF) / 255.0`
      - `G = (cp & 0xFF) / 255.0`
      - `B = 0.0` (reserved)
      - `A = (cp != 0) ? 1.0 : 0.0`
    - **`py = 12` (Metadata row)**:
      - `(phys_row, 12)` for `phys_row ∈ [0, 11]`: `R = (color & 0x0F) / 255.0`, `G = (flags & 0x3F) / 255.0`, `B = min(255, ceil(rowTotalPx / 4)) / 255.0` (cached row-width upper bound `rowMaxPx = B * 4`, `0` on empty/cleared rows; preserved by `CMD_SET_VIEW` and reset to `0` by `CMD_CLEAR_ROW`), `A = 1.0`
      - `(29, 12)`: `R = rows / 255.0` (`4..12`), `G = (row_px / 8) / 255.0` (`24..80`), `B = 0.0`, `A = 1.0` (valid when `A > 0` and in-range; survives `CMD_CLEAR_ALL` and `UniText_LocalClear`)
      - `(30, 12)`: `R = topRow / 255.0` (`topRow ∈ [0, 11]`, default `0`), `G = 0.0`, `B = 0.0`, `A = 1.0` (or `0.0` on initial clear)
      - `(31, 12)`: `R = status / 255.0`, `G = 0.0`, `B = 0.0`, `A = 1.0`
    - **`py = 13` (Packet Fingerprint Cache)** & **`py ∈ [14, 15]` (Reserved)**:
      - `(0..4, 13)` store the normalized 5-texel fingerprint of the last applied packet (`(Ctrl, D0..D2)`, `(D3..D6)` with `D4 & 0xF0` on Compact36, `(D7..D10)`, `(D11..D14)`, `(D15, 0, bank, rowPage)`) so identical consecutive frames skip text/meta/layout recomputation; `LocalClear` wipes row `13`. `(5..63, 13)` and `py ∈ [14, 15]` are reserved `(0, 0, 0, 0)`.
    - **`py ∈ [16, 27]` (Proportional Layout rows, `phys_row = py - 16`)**:
      - Updated incrementally in `O(packet-width)` when cells change (`px < minCol` copied, `px ∈ [minCol, maxCol]` prefix-summed from `xStart[minCol]`, `px > maxCol` shifted by signed span `delta`), keeping glyph positions synchronized within the same frame:
      - `xStart = sum_{k=0}^{px-1} advance(cell(k, phys_row))` (`0..768`)
      - `advance = advance(cell(px, phys_row))` (`0..12`, `0` for `cp == 0`)
      - `R = ((xStart >> 8) & 0xFF) / 255.0`
      - `G = (xStart & 0xFF) / 255.0`
      - `B = advance / 255.0`
      - `A = 1.0` (or `0.0` on `CMD_CLEAR_ALL` / `LocalClear`)
    - **`py ∈ [28, 31]`**: Reserved (`(0, 0, 0, 0)`).

---

## 5. Packed Font Atlas & Unified `64 × 64` Aux Lookup Texture

### 5.1 Block-Indexed Packed Unicode BMP Font Atlas (`1024 × 170`)

- **File**: `FontAtlas_BMP_Packed.png` (`680 KB` uncompressed RGBA32 VRAM; `67%` smaller than v1's `2.0 MB`)
- **Source Font**: SIL OFL 1.1 **Fusion Pixel Font (`12px-proportional`)**, rendered **left-aligned at the pen origin** with baseline row **`13`** (leaving row `0` clear and rows `14..15` for descenders).
- **Unity Import Settings (Mandatory)**:
  - `Texture Type = Default`, **`sRGB (Color Texture) = OFF`**, `Alpha Is Transparency = OFF`
  - `Non-Power of 2 = None`, `Generate Mip Maps = OFF`
  - `Filter Mode = Point (no filter)`, `Wrap Mode = Clamp`
  - `Compression = None` (`RGBA 32 bit`)
- **Block-Indexed Bitstream Layout** (`tools/font-atlas/atlas_format.py`):
  - The `65,536` BMP codepoints are grouped into `1,024` blocks of `64` codepoints (`block = cp >> 6`, `local = cp & 63`).
  - Each block is assigned one of three storage classes in `UniTextAux_64x64.png`:
    - **`0` (Absent — `325` blocks)**: No real glyphs (empty / tofu-only). Stored with `0` atlas bits; `Display.shader` draws the hollow tofu box procedurally (`rows 3, 13` cols `1..10`, `cols 1, 10` rows `3..13`).
    - **`1` (Small `11 × 11` — `665` blocks)**: Stores glyph rows `3..13`, cols `0..10` (`121` bits/glyph, `7,744` bits = `242` texels/block).
    - **`2` (Large `12 × 16` — `34` blocks)**: Stores glyph rows `0..15`, cols `0..11` (`192` bits/glyph, `12,288` bits = `384` texels/block). Glyphs outside cols `0..11` are clipped (`11` rare glyphs).
  - Active blocks are concatenated into a flat MSB-first bitstream (`baseTexel` aligned to a `2`-texel boundary). For bit index `bitIdx = baseTexel * 32 + local * bitsPerGlyph + cellBit`:
    ```text
    texel = bitIdx >> 5                 // 0..174079 (texels in 1024 x 170)
    tex_x = texel & 1023                // 0..1023
    tex_y = texel >> 10                 // 0..169 (0 = top row of PNG)
    byte  = (bitIdx >> 3) & 3           // 0=R, 1=G, 2=B, 3=A
    bit   = 7 - (bitIdx & 7)            // MSB-first within channel byte
    ```

### 5.2 Unified `64 × 64` Aux Lookup Texture (`UniTextAux_64x64.png`, `16 KB` VRAM)

Replaces both v1's `Lut12_64x64.png` and `GlyphAdvance_128x128.png` with one `64 × 64` RGBA32 texture (`4,096` texels, identical mandatory import settings):
- **Texels `0..1023` — Block Table** (1 texel per `64`-codepoint block):
  - `R | (G << 8) | ((B & 0x03) << 16)` = `baseTexel >> 1` (18-bit half-texel offset)
  - `(B >> 2) & 0x03` = block class (`0 = Absent`, `1 = Small 11×11`, `2 = Large 12×16`)
  - `A` = uniform glyph advance (`0..12`) when bit 7 is `0`, or `0x80 | mixedIdx` (`0..127`) indexing into the mixed-advance region when bit 7 is `1`.
- **Texels `1024..2047` — Mixed-Advance Nibble Table** (`8` texels per mixed block, `35` blocks used):
  - `64 × 4-bit` advances (`0..12`), packed big-endian (even `local` in high nibble, odd `local` in low nibble).
- **Texels `2048..4095` — 12-Bit Common-Character Codebook (`LUT12`, `4,096` entries)**:
  - Canonical entries from [`spec/lut12.json`](../spec/lut12.json): `0 = U+0000`, ASCII printable (`95`), Hiragana (`86`), Katakana (`90`), CJK/General/Fullwidth punctuation (`51`), all `3,755` GB2312 Level-1 Hanzi, and high-frequency Traditional/Kanji padding (`18`).
  - Packed `2` big-endian UTF-16 entries per texel: even index in `(R, G)`, odd index in `(B, A)`.

### 5.3 `WRITE_LUT12` Bit-Packing & Canonical Advance Table

- **`WRITE_LUT12` Nibble Stream**: each pair of 12-bit indices `(A, B)` packs into 3 bytes:
  ```text
  byte[0] = (A >> 4) & 0xFF
  byte[1] = ((A & 0x0F) << 4) | ((B >> 8) & 0x0F)
  byte[2] = B & 0xFF
  ```
  - On `Compact36` (`N = 5`, `3` cells/pkt, `slot = pageRow * 11 + chunk < 66`): index `2` occupies `D3` and `D4` high nibble (`UniText_D4b4..D4b7`); `D4` low nibble is masked to `0`.
  - On `Standard` (`N = 8`, `5` cells/pkt): indices `0..3` occupy bytes `0..5`, index `4` occupies `D6` and `D7` high nibble, while **`D7 & 1`** encodes `oddOffset ∈ {0, 1}`.
- **Canonical Advance Spec ([`spec/glyph-advance.json`](../spec/glyph-advance.json))**: senders use this JSON run-length table (`0..12 px` per BMP codepoint) for proportional line wrapping; shaders read the identical advances from `UniTextAux_64x64.png`.

---

## 6. Animator Direct BlendTree & `ParamBridge` Architecture

1. In `VRCExpressionParameters`, `UniText_Ctrl` and `UniText_D0..D{N-1}` are **`Int` (`0..255`)**, and `UniText_D4b4..D4b7` (Compact36), `UniText_Bank`, and `UniText_RowPage` (when enabled) are **`Bool`**. `UniText_Width` and `UniText_Height` are local-only `Float`s (`0` synced bits).
2. In the FX `AnimatorController`, the matching Direct BlendTree parameters are **`Float`** (VRChat losslessly maps synced `Int` `0..255` to `0.0..255.0` and synced `Bool` to `0.0 / 1.0`).
3. A single **Direct BlendTree** layer (generated non-destructively in memory by `UniTextNdmfPlugin` and merged via `ModularAvatarMergeAnimator`) drives `EncoderQuad` uniforms (`_Ctrl`, `_D0.._D{N-1}`, `_D4b4.._D4b7`, `_LocalClear`, `_Bank`, `_RowPage`), `DisplayQuad` (`_Billboard`, `_Opacity`), and `BoardPivot` (`m_LocalScale`). Live `Resize (W/H)` quad scaling and reflow are handled directly in `Display.shader` from `CMD_SET_GEOMETRY` at `(29, 12)` rather than via Animator curves.
4. **Why `ParamBridge` (`8 × 1` Camera → RenderTexture) exists**:
   In Unity, animating `material._Param` on a `MeshRenderer` writes to that renderer's `MaterialPropertyBlock` rather than the underlying shared `.mat` asset assigned to a `CustomRenderTexture`. To pass those exact Animator-driven bytes losslessly into `BufferCRT.shader`, `EncoderQuad` renders the animated material uniforms into `ParamBridge_8x1_<Tier>.renderTexture` (`R8G8B8A8_UNorm`, Linear, Point), and `BufferCRT.shader` reads `_ParamTex` (`tex2Dlod`) every frame:
   - `x = 0`: `(Ctrl, D0, D1, D2)`
   - `x = 1`: `(D3, D4, D5, D6)` (`ParamEncoder.shader` ORs `_D4b4.._D4b7` into `D4` bits `4..7`)
   - `x = 2`: `(D7, D8, D9, D10)`
   - `x = 3`: `(D11, D12, D13, D14)`
   - `x = 4`: `(D15, LocalClear, Bank, RowPage)`
   - `ParamEncoder.shader` clips its vertices outside `_ScreenParams = 8 × 1`, so it is completely invisible to player, VR, and mirror cameras; additionally `EncoderQuad` sits on layer `9` (`Player`) with `ParamCamera` using Forward rendering, culling mask `(1 << 9) | (1 << 10)` (`1536`, `Player | PlayerLocal`), and `Camera.enabled` tied to `UniText_Visible` so hidden boards incur zero camera rendering cost.

---

## 7. Sender Scheduling & VRChat Network Behavior

- **Packet Pacing**: Default **`150 ms`** between parameter updates (`120–200 ms` supported). VRChat Avatar sync serializes parameters at ~5–10 Hz.
- **Proportional Word Wrapping & Plane Splitting**:
  - Wrap text using [`spec/glyph-advance.json`](../spec/glyph-advance.json) within `max_px = row_px` and `max_cells = cols` (`64` when `UniText_Bank` is present, else `32`).
  - Maintain a `VirtualScreen` (`cols × rows`) and encode each `32 × 6` `(bank, rowPage)` plane separately, stamping every emitted packet with its `UniText_Bank` (and `UniText_RowPage` when `rows > 6`).
  - Emit `CMD_SET_GEOMETRY` (`Ctrl = 251`) on avatar connect, resize reflow, and late-joiner idle refresh when `UniText_ProtoVer >= 2` or live resize is enabled.
- **Ring-Buffer Scrolling & Dirty-First Scheduling**:
  - When a new line causes scrolling (`scroll_up(1)`):
    1. Emit `CMD_SET_VIEW` (`Ctrl=250`) to advance `topRow = (topRow + 1) % R` (piggybacking the new bottom row's style & status).
    2. Emit `CMD_CLEAR_ROW` (`Ctrl=241`) for recycled physical rows (clears both banks in one packet).
    3. Emit only the new bottom row's non-empty characters—never retransmit untouched history lines.
  - When wrapped lines exceed `R` visible rows, scroll or page through remaining lines rather than truncating.
- **Late-Joiner Recovery**: When idle, cycle through occupied non-empty chunks, active row styles, `CMD_SET_GEOMETRY`, and `CMD_SET_VIEW` (`topRow` + status) at steady-state cadence so players joining late or un-hiding the avatar recover the complete subtitle state within **3–5 seconds**.
- **`OscBundle` (Recommended) vs Single-Packet Ordering**:
  - **Prefer `OscBundle` (`IMMEDIATELY`)**: Wrapping `UniText_D0..D{N-1}` (plus `UniText_D4b4..D4b7` on Compact36), `UniText_Bank`, `UniText_RowPage`, and `UniText_Ctrl` inside a single `OscBundle` updates all parameters atomically within the same frame.
  - **Fallback `single` UDP Mode Guard**: Transition `UniText_Ctrl` back to `0` (`NOP`) and wait briefly (e.g., `25 ms`) before updating `D*` / `Bank` / `RowPage` for the next packet.

---

## 8. Live VRChat Validation Matrix

> Run `tools/osc-tester/send_demo.py` with local + remote PC VRChat clients before tagging releases.

| Test Item | Mode / Interval | Expected Result |
| :--- | :--- | :--- |
| `OscBundle` support | `bundle` @ `150ms` | Multilingual rows, row styles, and status border accent render cleanly with zero tearing |
| Single-packet tearing guard | `single` @ `150ms` (`Ctrl=0` gap) | No cross-frame partial `D*` overwrites |
| Ring-Buffer scroll (`lite` / `33b`) | `scroll` / `long` @ `150ms` | 1-line scroll updates bottom row with zero upper-row retransmit |
| Resizable viewport (`rows` / `rowPx`) | `long` @ `150ms` | `4..12` rows and `192..512 px` wrap cleanly without SDF/glyph distortion |
| Late-Joiner recovery | Steady-state round-robin | Full screen recovery (incl. `topRow`) within 3–5s |
| VR SPS-I stereo & World Drop | VR HMD + distance check | Zero eye offset, readable at 2–5m |
