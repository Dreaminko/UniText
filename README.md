<div align="center">

# UniText

**English** · [简体中文](README.zh-CN.md) · [日本語](README.ja.md)

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![VPM Package](https://img.shields.io/badge/VPM-dev.dreaminko.unitext-brightgreen.svg)](https://vpm.dreamo.ink/)
![Unity 2022.3.22f1](https://img.shields.io/badge/Unity-2022.3.22f1-lightgrey.svg)
![Platform: PCVR / PC Desktop](https://img.shields.io/badge/Platform-PCVR%20%2F%20PC%20Desktop-purple.svg)

</div>

UniText enables VRChat PC avatars to display **real-time, shared, multilingual (Chinese / Japanese / Korean / English / Unicode BMP) subtitles and translations** visible to every PC player in the instance—breaking past the native VRChat Chatbox limits.

---

## Key Features

- **Full Unicode BMP & Proportional Layout**: Configurable `192–640 px` row width (default `384 px`) × `4–12` visible rows (default `6`). Supports up to `64` cells/row via `UniText_Bank` and `7–12` rows via optional `+1 bit` `UniText_RowPage`.
- **Ultra-Low VRAM Footprint (`~712 KB`)**: Block-indexed packed font atlas (`680 KB`) + unified auxiliary table (`16 KB`) + CRT/ParamBridge (`16 KB`).
- **High-Throughput Encoding & Zero-Copy Scroll**: 12-bit common-character LUT (`4,096` glyphs) + 1-byte page compression (`0x30` Kana/CJK punctuation, etc.) + Ring-Buffer hardware scrolling.
- **Rich Presentation & Live Controls**: 16-color per-row palette, dim/highlight row styles, status indicator, SPS-I stereo Billboard & World Drop (`VRCParentConstraint`), plus live `Width` / `Height` reflow (`0` synced axis bits via `CMD_SET_GEOMETRY`).
- **Non-Destructive NDMF + Modular Avatar Workflow**: Zero edits to your base Avatar Animator Controller, Expression Parameters, or Expressions Menu assets on disk.

---

## Requirements

| Dependency | Version | Notes |
| :--- | :--- | :--- |
| **Unity** | `2022.3.22f1` | VRChat SDK3 supported Unity editor |
| **VRChat SDK - Avatars** | `>= 3.7.0` | `com.vrchat.avatars` (auto-resolved via VPM) |
| **Modular Avatar** | `>= 1.10.0` | `nadena.dev.modular-avatar` (auto-resolved via VPM) |
| **NDMF** | `>= 1.5.0` | `nadena.dev.ndmf` (auto-resolved via VPM) |

---

## Quick Installation (VCC / VPM)

1. Click **[Add Dreaminko's VPM Repository to VCC](vcc://vpm/addRepo?url=https://vpm.dreamo.ink/index.json)** (or visit [Dreaminko's VPM Repository](https://vpm.dreamo.ink/)), or manually add the VPM Repository URL in VCC (`Settings → Packages → Add Repository`):
   ```text
   https://vpm.dreamo.ink/index.json
   ```
2. In your Avatar project in VCC, add **`UniText`** (`dev.dreaminko.unitext`) from **Dreaminko's VPM Repository**. VCC will automatically install `com.vrchat.avatars`, `nadena.dev.modular-avatar`, and `nadena.dev.ndmf`.
3. Open Unity (`2022.3.22f1`), click **`UniText → Install to Avatar`** (or **`Tools → UniText → Install to Avatar`**) in the top menu bar, drag your **Avatar** from the Scene Hierarchy into the window, choose your tier and placement, and click **Install** (or manually drag one of the prefabs from `Packages/dev.dreaminko.unitext/Prefabs/` onto your Avatar root). Upload or enter Play Mode to preview!

---

## Non-Destructive Modular Avatar Import (`UniTextProp`)

UniText prefabs never modify your base Avatar Animator Controller, Expression Parameters, or Expressions Menu assets on disk. At avatar build / Play Mode time, the built-in NDMF plugin (`UniTextNdmfPlugin`) runs right before Modular Avatar and expands each `UniTextProp` cleanly:

- **Automatic Bone Attachment (`MA Bone Proxy`)**: Attaches `SideFollowAnchor` to the configured humanoid bone (defaults to `Chest`, with optional auto-offset alignment) and binds `BoardPivot` via `VRCParentConstraint` for seamless Side Follow / World Drop.
- **Direct BlendTree FX Merge (`MA Merge Animator`)**: Generates the tier's Direct BlendTree FX layer in memory and merges it using `MergeAnimatorPathMode.Relative` and `matchAvatarWriteDefaults = true`.
- **Automatic Parameters (`MA Parameters`)**: Registers `UniText_Visible`, `UniText_Ctrl`, `UniText_D0..D{N-1}`, optional synced `UniText_WorldDrop`, `UniText_Bank` (Wide Rows), and `UniText_RowPage` (`> 6` rows), plus local-only UI controls (`Width`, `Height`, `Scale`, `Opacity`) and viewport geometry markers (`UniText_Rows`, `UniText_RowPx8`).
- **Editable Hierarchy Submenu (`MA Menu Installer` + `MA Menu Item`)**: Generates an editable `"UniText"` submenu hierarchy under your prop (`MA Menu Installer` + `MA Menu Item` nodes) so you can rename entries, assign icons, reorder items, tweak defaults, or reparent controls into other avatar menus directly in the Hierarchy, with automatic build-time fallback if left ungenerated.
- **Automatic Quest/Android Stripping**: When `stripOnAndroid` is enabled (default), UniText automatically removes itself on Android builds without touching your PC prefab setup.

### In-Game Expressions Menu

Inside VRChat's Expressions Menu under **`UniText`**, you get:

- **Show Board**: Toggle subtitle board visibility (`UniText_Visible`)
- **World Drop**: Freeze the board in world space or follow your avatar (`UniText_WorldDrop`)
- **Billboard**: Toggle stereo-safe camera-facing mode (`UniText_Billboard`)
- **Clear Screen**: Clear the local subtitle buffer (`UniText_LocalClear`)
- **Appearance** *(Submenu)*:
  - **Scale**: Adjust physical board size (`UniText_Scale`)
  - **Opacity**: Adjust board background/text opacity (`UniText_Opacity`)
  - **Width** & **Height**: Live-reflow row width and visible row count (`UniText_Width`, `UniText_Height`)

---

## Parameter Budget Tiers

| Tier | Prefab | Synced Bits | Payload | LUT12 CJK / Pkt | Page (`0x30`) / Pkt | UTF-16 / Pkt | ASCII / Pkt | Best For |
| :--- | :--- | :---: | :---: | :---: | :---: | :---: | :---: | :--- |
| **Lite** | `Board_Lite_33b.prefab` | **33 bits** | 3B (`D0..D2`) | 2 | 2 | 1 | 3 | Ultra-tight budgets (fast Ring-Buffer scroll) |
| **Compact** | `Board_Compact_45b.prefab` | **45 bits** | 36b (`D0..D3` + `D4b4..D4b7`) | 3 | 3 | 2 | 4 | Tight parameter budgets (+50% CJK speed) |
| **Balanced** | `Board_Balanced_57b.prefab` | **57 bits** | 6B (`D0..D5`) | 4 | 5 | 3 | 6 | Balanced cost & CJK speed (16 bits less than Standard) |
| **Standard** *(Default)* | `Board_Standard_73b.prefab` | **73 bits** | 8B (`D0..D7`) | 5 | 7 | 4 | 8 | Most avatars (recommended) |
| **Turbo** | `Board_Turbo_137b.prefab` | **137 bits** | 16B (`D0..D15`) | 10 | 15 | 8 | 16 | Lowest subtitle latency |

### Optional Synced Bits & VRAM Breakdown

- **Optional World Drop Sync (`+1 bit`)**: Syncing `UniText_WorldDrop` uses `+1 bit` (on by default so remote players see the board stay dropped in world space; can be switched to local-only in `UniTextProp` or `ModularAvatarParameters` if desired).
- **Optional Wide Rows (`+1 bit`)**: Syncing `UniText_Bank` (`syncWideRows`, on by default) uses `+1 bit` so each row holds up to `64` proportional cells (`~2×` more English per row). Disabling it keeps `32` cells per row while preserving proportional rendering.
- **Optional Extended Rows (`+1 bit` when `> 6` rows or live resize enabled)**: Unlocks `7..12` rows via `UniText_RowPage` (`+1 bit`).
- **Optional Live Resize & Reflow (`+1 bit`, on by default)**: `UniText_Width` and `UniText_Height` stay local-only (`0` synced bits); [VRCS](https://github.com/Dreaminko/VRCS) broadcasts the reflowed `(rows, row_px)` to local and remote players via `CMD_SET_GEOMETRY`, so enabling `Allow In-Game Resize` only costs `+1 bit` for `UniText_RowPage` (disable to save `1` bit).
- **Local-only UI Controls & Capability Markers (`0` synced bits)**: `UniText_ProtoVer`, `UniText_Rows`, `UniText_RowPx8`, `UniText_Width`, `UniText_Height`, `UniText_Billboard`, `UniText_Scale`, `UniText_Opacity`, `UniText_LocalClear`.
- **VRAM Footprint**: Packed block-indexed atlas (`FontAtlas_BMP_Packed.png`, `680 KB`) + unified aux table (`UniTextAux_64x64.png`, `16 KB`) + CRT/ParamBridge (`16 KB`) ≈ **`712 KB`** total.
- **Parameter Budget Tip**: Check your avatar's remaining parameter budget in Unity via **`Tools → UniText → Check Avatar Parameter Budget`**. If your avatar's own clothing/slider toggles take too many bits, use **VRCFury Parameter Compressor** on your avatar's static menu parameters (while keeping UniText's OSC data parameters uncompressed).

---

## Android / Quest Notes

VRChat Quest/Android avatars do not support `CustomRenderTexture` updates or multi-pass custom CRT shaders. By default (`stripOnAndroid = true` on `UniTextProp`), UniText's NDMF plugin automatically strips the prop non-destructively during Quest/Android builds so cross-platform avatars build cleanly with zero broken shaders.

---

## Developer Quick Start

### 1. Run Automated Verification
```bash
python tools/test_all.py
```

### 2. Verify Font Atlas & Regenerate LUT12 Codebook
```bash
python tools/font-atlas/verify_atlas.py
python tools/font-atlas/generate_lut12.py
```

### 3. Send Test Subtitles to Local VRChat / Unity (`127.0.0.1:9000`)
```bash
pip install -r tools/osc-tester/requirements.txt
python tools/osc-tester/send_demo.py --verify-vectors
python tools/osc-tester/send_demo.py --tier balanced --interval 150 --mode bundle --demo long --loop
python tools/osc-tester/send_demo.py --tier lite --interval 150 --demo scroll --loop
python tools/osc-tester/send_demo.py --oscquery-url http://127.0.0.1:9001/avatar/parameters --tier auto --demo long
```

### 4. Verify Package Assets / Export `.unitypackage` in Unity
Open `unity/` in **Unity 2022.3.22f1** (with `com.vrchat.avatars`, `nadena.dev.modular-avatar`, and `nadena.dev.ndmf` resolved via VCC/VPM):
- Prefabs under `Packages/dev.dreaminko.unitext/Prefabs/` are **immediately usable via NDMF + Modular Avatar** without any pre-bake step.
- Maintainers can verify/refresh package asset bindings or export an offline `.unitypackage` via:
  - **`Tools → UniText → Verify & Refresh Package Assets`**
  - **`Tools → UniText → Export .unitypackage`**

---

## Documentation & Licenses

- [Protocol Specification (`v2`)](docs/PROTOCOL.md) — CRT memory layout, OSC parameters, command opcodes, and sender rules
- [Custom Skin & Prop Authoring Guide](docs/SKIN_GUIDE.md) — `UniTextCommon.cginc` API and custom shader authoring
- [Protocol Test Vectors](spec/test-vectors.json) — Reference verification vectors for sender implementations
- [Third-Party Licenses](THIRD_PARTY_LICENSES.md) — Bundled [Fusion Pixel Font](https://github.com/TakWolf/fusion-pixel-font) (`SIL OFL-1.1`), [Google Material Symbols](https://github.com/google/material-design-icons) (`Apache-2.0`), and tool attributions
- [MIT License](LICENSE)
