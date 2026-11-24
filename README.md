# UniText

**Native Unicode (CJK / Multilingual) OSC Shared Subtitle Prop & Open Protocol for VRChat Avatar 3.0**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![VPM Package](https://img.shields.io/badge/VPM-dev.dreaminko.unitext-brightgreen.svg)](https://vpm.dreamo.ink/)

UniText enables VRChat PC avatars to display **real-time, shared, multilingual (Chinese / Japanese / Korean / English / Unicode BMP) subtitles and translations** visible to every PC player in the instance—breaking past the native VRChat Chatbox limits (144 chars, 1.5s rate limit, 30s auto-hide, fixed overhead bubble).

- **Flagship Sender**: **VRCS** (or any third-party sender implementing the [Protocol Specification](docs/PROTOCOL.md) and [Test Vectors](spec/test-vectors.json))
- **Target Platform**: VRChat SDK3 Avatars (**PCVR / PC Desktop**; Quest/Android gracefully hides/skips unsupported CRT shaders)
- **Rendering**: Proportional Unicode BMP layout (`192–640 px` row width, default `384 px`, × `4–12` configurable rows, default `6`; up to `64` cells/row via `UniText_Bank`, and `>6` rows via optional `+1 bit` `UniText_RowPage`), Ring-Buffer hardware scroll, block-indexed packed font atlas (`~712 KB` total VRAM, `67%` smaller than v1), 12-bit common-character LUT (`4,096` glyphs) + 1-byte page compression, 16-color per-row palette, dim/highlight row styles, status indicator, SPS-I stereo Billboard & World Drop (`VRCParentConstraint`), plus live `Resize (W/H)` reflow (`0` synced axis bits via `CMD_SET_GEOMETRY`)

---

## Quick Installation (Choose ONE Method)

> **Important**: Choose **either** VCC/VPM **or** `.unitypackage`. **Never install both** into the same Unity project—doing so causes duplicate GUID conflicts.

### Method 1: VCC / VRChat Creator Companion (Recommended)

1. Click **[Add UniText to VCC](vcc://vpm/addRepo?url=https://vpm.dreamo.ink/index.json)** (or visit [https://vpm.dreamo.ink/](https://vpm.dreamo.ink/)), or manually add the VPM Repository URL in VCC (`Settings → Packages → Add Repository`):
   ```text
   https://vpm.dreamo.ink/index.json
   ```
2. In your Avatar project in VCC, add **`UniText`** (`dev.dreaminko.unitext`). VCC will automatically install `com.vrchat.avatars`, `nadena.dev.modular-avatar`, and `nadena.dev.ndmf`.
3. Open Unity (`2022.3.22f1`), click **`UniText → Install to Avatar`** (or **`Tools → UniText → Install to Avatar`**) in the top menu bar, drag your **Avatar** from the Scene Hierarchy into the window, choose your tier, and click **Install** (or manually drag one of the prefabs from `Packages/dev.dreaminko.unitext/Prefabs/` onto your Avatar root). Upload or enter Play Mode to preview!

### Method 2: Offline `.unitypackage` (Fallback)

1. Ensure your Unity `2022.3.22f1` project already has **VRChat SDK - Avatars**, **Modular Avatar** (`>= 1.10.0`), and **NDMF** (`>= 1.5.0`) installed.
2. Download `UniText-<version>.unitypackage` from [GitHub Releases](https://github.com/Dreaminko/UniText/releases).
3. Import it into Unity, open **`UniText → Install to Avatar`** from the top menu bar and drag your Avatar in to install (or manually drag a Prefab from `Packages/dev.dreaminko.unitext/Prefabs/` or `Assets/UniText/Prefabs/` onto your Avatar root), then upload.

---

## Non-Destructive Modular Avatar Import (`UniTextProp`)

UniText prefabs never modify your base Avatar Animator Controller, Expression Parameters, or Expressions Menu assets on disk. At avatar build / Play Mode time, the built-in NDMF plugin (`UniTextNdmfPlugin`) runs right before Modular Avatar and expands each `UniTextProp` cleanly:

- **Automatic Bone Attachment (`MA Bone Proxy`)**: Attaches `SideFollowAnchor` to the configured humanoid bone (defaults to `Chest`, with optional auto-offset alignment) and binds `BoardPivot` via `VRCParentConstraint` for seamless Side Follow / World Drop.
- **Direct BlendTree FX Merge (`MA Merge Animator`)**: Generates the tier's Direct BlendTree FX layer in memory and merges it using `MergeAnimatorPathMode.Relative` and `matchAvatarWriteDefaults = true`.
- **Automatic Parameters (`MA Parameters`)**: Registers `UniText_Visible`, `UniText_Ctrl`, `UniText_D0..D{N-1}`, optional synced `UniText_WorldDrop`, `UniText_Bank` (Wide Rows), and `UniText_RowPage` (`> 6` rows), plus local-only UI controls (`Resize W/H`, `Scale`, `Opacity`) and viewport geometry markers (`UniText_Rows`, `UniText_RowPx8`).
- **Clean Single-Slot Submenu (`MA Menu Installer`)**: Installs a single `"UniText"` submenu entry (`Show Board`, `World Drop`, `Billboard`, `Scale`, `Opacity`, `Clear Screen`, `Resize (W/H)`) without cluttering your root Expressions Menu.
- **Automatic Quest/Android Stripping**: When `stripOnAndroid` is enabled (default), UniText automatically removes itself on Android builds without touching your PC prefab setup.

---

## Parameter Budget Tiers

| Tier | Prefab | Synced Bits | Payload | LUT12 CJK / Pkt | Page (`0x30`) / Pkt | UTF-16 / Pkt | ASCII / Pkt | Best For |
| :--- | :--- | :---: | :---: | :---: | :---: | :---: | :---: | :--- |
| **Lite** | `Board_Lite_33b.prefab` | **33 bits** | 3B (`D0..D2`) | 2 | 2 | 1 | 3 | Ultra-tight budgets (fast Ring-Buffer scroll) |
| **Compact** | `Board_Compact_45b.prefab` | **45 bits** | 36b (`D0..D3` + `D4b4..D4b7`) | 3 | 3 | 2 | 4 | Tight parameter budgets (+50% CJK speed) |
| **Balanced** | `Board_Balanced_57b.prefab` | **57 bits** | 6B (`D0..D5`) | 4 | 5 | 3 | 6 | Matches old 73b CJK speed while saving 16 bits |
| **Standard** *(Default)* | `Board_Standard_73b.prefab` | **73 bits** | 8B (`D0..D7`) | 5 | 7 | 4 | 8 | Most avatars (recommended) |
| **Turbo** | `Board_Turbo_137b.prefab` | **137 bits** | 16B (`D0..D15`) | 10 | 15 | 8 | 16 | Lowest subtitle latency |

- **Optional World Drop Sync (`+1 bit`)**: Syncing `UniText_WorldDrop` uses `+1 bit` (on by default so remote players see the board stay dropped in world space; can be switched to local-only in `UniTextProp` or `ModularAvatarParameters` if desired).
- **Optional Wide Rows (`+1 bit`)**: Syncing `UniText_Bank` (`syncWideRows`, on by default) uses `+1 bit` so each row holds up to `64` proportional cells (`~2×` more English per row). Disabling it keeps `32` cells per row while preserving proportional rendering.
- **Optional Extended Rows (`+1 bit` when `> 6` rows or live resize enabled)**: Unlocks `7..12` rows via `UniText_RowPage` (`+1 bit`).
- **Optional Live Resize & Reflow (`+1 bit`, on by default)**: `UniText_Width` and `UniText_Height` stay local-only (`0` synced bits); VRCS broadcasts the reflowed `(rows, row_px)` to local and remote players via `CMD_SET_GEOMETRY`, so enabling `Allow In-Game Resize` only costs `+1 bit` for `UniText_RowPage` (disable to save `1` bit).
- **Local-only UI Controls & Capability Markers (0 synced bits)**: `UniText_ProtoVer`, `UniText_Rows`, `UniText_RowPx8`, `UniText_Width`, `UniText_Height`, `UniText_Billboard`, `UniText_Scale`, `UniText_Opacity`, `UniText_LocalClear`.
- **VRAM Footprint**: Packed block-indexed atlas (`FontAtlas_BMP_Packed.png`, `680 KB`) + unified aux table (`UniTextAux_64x64.png`, `16 KB`) + CRT/ParamBridge (`16 KB`) ≈ **`712 KB`** total (`~67%` lower than v1's `2.1 MB`).
- Check your avatar's remaining parameter budget in Unity via **`Tools → UniText → Check Avatar Parameter Budget`**. If your avatar's own clothing/slider toggles take too many bits, use **VRCFury Parameter Compressor** on your avatar's static menu parameters (while keeping UniText's OSC data parameters uncompressed).

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

## Android / Quest Notes

VRChat Quest/Android avatars do not support `CustomRenderTexture` updates or multi-pass custom CRT shaders. By default (`stripOnAndroid = true` on `UniTextProp`), UniText's NDMF plugin automatically strips the prop non-destructively during Quest/Android builds so cross-platform avatars build cleanly with zero broken shaders.
