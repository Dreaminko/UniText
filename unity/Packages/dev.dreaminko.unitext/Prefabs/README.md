# UniText Non-Destructive Modular Avatar Prefabs

Open **`UniText -> Install to Avatar`** (or **`Tools -> UniText -> Install to Avatar`**) from the Unity top menu bar, drag your **Avatar** from the Hierarchy into the installer window, and click **Install**—or manually drag any of these ready-to-use prefabs directly onto your **VRChat Avatar root** in Unity `2022.3.22f1`:
- `Board_Lite_33b.prefab` (33 base synced bits + optional 1b WorldDrop + optional 1b WideRows `UniText_Bank`; 3 payload bytes, 2 LUT12 CJK/pkt + Ring-Buffer scroll)
- `Board_Compact_45b.prefab` (45 base synced bits + optional 1b WorldDrop + optional 1b WideRows `UniText_Bank`; 36 payload bits = 4 payload bytes + 4 `UniText_D4b4..D4b7` Bools, 3 LUT12 CJK/pkt)
- `Board_Balanced_57b.prefab` (57 base synced bits + optional 1b WorldDrop + optional 1b WideRows `UniText_Bank`; 6 payload bytes, 4 LUT12 CJK/pkt)
- `Board_Standard_73b.prefab` (73 base synced bits + optional 1b WorldDrop + optional 1b WideRows `UniText_Bank`, default; 8 payload bytes)
- `Board_Turbo_137b.prefab` (137 base synced bits + optional 1b WorldDrop + optional 1b WideRows `UniText_Bank`; 16 payload bytes)

Each prefab carries a `UniTextProp` component configured for **NDMF + Modular Avatar non-destructive build expansion**:
- Automatically attaches `SideFollowAnchor` via `ModularAvatarBoneProxy` (defaults to `Chest`)
- Automatically configures `VRCParentConstraint` (`Side Follow` / `World Drop`)
- Generates the Direct BlendTree FX `AnimatorController` in memory and attaches `ModularAvatarMergeAnimator`
- Attaches `ModularAvatarParameters` (local-only `UniText_ProtoVer` / `UniText_Rows` / `UniText_RowPx8` capability markers, optional `UniText_Bank` Wide Rows bit for 64 proportional cells/row, optional `UniText_RowPage`, local-only `UniText_Width` / `UniText_Height` resize axes, and tier payload channels) and manages an editable `"UniText"` submenu hierarchy via `ModularAvatarMenuInstaller` + `ModularAvatarMenuItem` (generated in Scene Hierarchy by the Installer / Inspector, with automatic build-time fallback)
- Automatically strips itself non-destructively when building for Quest/Android (`stripOnAndroid = true`)

Maintainers can refresh these prefabs at any time via `Tools -> UniText -> Verify & Refresh Package Assets`.
