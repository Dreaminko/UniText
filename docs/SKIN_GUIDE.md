# UniText Custom Skin & Prop Authoring Guide

This guide explains how avatar creators and 3D prop artists can build custom visual housings, holograms, floating wrist HUDs, or themed subtitle boards powered by UniText's GPU buffer and font atlas.

---

## 1. Reusable Shader Include (`UniTextCommon.cginc`)

Include `Packages/dev.dreaminko.unitext/Shaders/UniTextCommon.cginc` in your custom `.shader` (`CGPROGRAM` / `HLSLPROGRAM`):

### Available Functions

| Function | Signature | Description |
| :--- | :--- | :--- |
| `UniTextToByte` | `uint UniTextToByte(float v)` | Rounds and clamps a `0..255` shader/animator float to an exact byte. |
| `UniTextGetPaletteColor` | `fixed3 UniTextGetPaletteColor(uint idx)` | Returns the standard RGB color for palette slot `0..15`. |
| `UniTextSampleCell` | `uint UniTextSampleCell(sampler2D bufferTex, uint col, uint row)` | Reads the 16-bit Unicode BMP codepoint at `(col ∈ [0,63], row ∈ [0,11])`. Returns `0` for empty cells. |
| `UniTextSampleRowStyle` | `void UniTextSampleRowStyle(sampler2D bufferTex, uint row, out uint colorIdx, out uint flags)`<br>`void UniTextSampleRowStyle(sampler2D bufferTex, uint row, out uint colorIdx, out uint flags, out uint rowMaxPx)` | Reads row `0..11` color index (`0..15`), flags (`bit 0 = Dim`, `bit 1 = Highlight`), and optional cached pixel-width upper bound `rowMaxPx` (`0` when empty, for `penX < rowMaxPx` early-out). |
| `UniTextSampleStatus` | `uint UniTextSampleStatus(sampler2D bufferTex)` | Reads global status indicator (`0 = Idle`, `1 = Listening`, `2 = Translating`). |
| `UniTextSampleTopRow` | `uint UniTextSampleTopRow(sampler2D bufferTex, uint activeRows)` | Reads Ring-Buffer `topRow ∈ [0, activeRows-1]` from `(30, 12)` so custom displays support zero-copy scrolling (`physRow = (cellRow + topRow) % activeRows`). |
| `UniTextSampleLayout` | `void UniTextSampleLayout(sampler2D bufferTex, uint col, uint physRow, out uint xStart, out uint advance)` | Reads the proportional cell origin `xStart ∈ [0, 768]` and glyph advance `advance ∈ [0, 12]` from layout row `16 + physRow`. |
| `UniTextSampleAdvance` | `uint UniTextSampleAdvance(sampler2D auxTex, uint cp)` | Reads a codepoint's proportional pixel advance (`0..12`) from `UniTextAux_64x64.png`'s block / mixed-advance tables. |
| `UniTextLookupLut12` | `void UniTextLookupLut12(sampler2D auxTex, uint lutIdx, out uint hi, out uint lo)` | Decodes a 12-bit codebook index (`0..4095`) from `UniTextAux_64x64.png` into big-endian BMP `(hi, lo)` bytes. |
| `UniTextSampleGeometry` | `bool UniTextSampleGeometry(sampler2D bufferTex, inout uint rows, inout float rowPx)` | Reads live `(rows, rowPx)` sent via `CMD_SET_GEOMETRY` from CRT metadata pixel `(29, 12)`. |
| `UniTextGetGlyphRef` | `UniTextGlyphRef UniTextGetGlyphRef(sampler2D auxTex, uint cp)` | Resolves a codepoint's block class (`0 = tofu`, `1 = 11×11`, `2 = 12×16`) and bit offset from `UniTextAux_64x64.png`. |
| `UniTextSampleGlyphBit` | `float UniTextSampleGlyphBit(sampler2D atlasTex, float atlasHeight, UniTextGlyphRef g, uint gx, uint gy)` | Samples pixel `(gx ∈ [0,15], gy ∈ [0,15])` (`1.0` lit, `0.0` off) from the packed atlas `FontAtlas_BMP_Packed.png` (pass `_FontAtlas_TexelSize.w` as `atlasHeight`). |
| `UniTextSampleGlyphAndShadow` | `void UniTextSampleGlyphAndShadow(sampler2D atlasTex, float atlasHeight, UniTextGlyphRef g, uint gx, uint gy, bool sampleShadow, out float glyph, out float shadow)` | Samples foreground bit and right-down `(gx - 1, gy - 1)` shadow bit, pruning shadow reads when `glyph >= 0.5` and reusing the foreground texel when both land in the same 32-bit texel. |

---

## 2. Minimal Custom Display Shader Requirements

1. **Texture & Geometry Properties**:
   - `_BufferTex ("CRT Buffer (64x32)", 2D) = "black" {}` → assign `Buffer_64x32_Standard.asset` (or matching tier CRT).
   - `_FontAtlas ("Packed Font Atlas (1024xH)", 2D) = "black" {}` → assign `FontAtlas_BMP_Packed.png` (and declare `float4 _FontAtlas_TexelSize;` so `UniTextSampleGlyphBit` knows `_FontAtlas_TexelSize.w`).
   - `_AuxTex ("Aux Lookup (64x64)", 2D) = "black" {}` → assign `UniTextAux_64x64.png`.
   - `_LiveGeometry ("Follow Sender Geometry", Float) = 1` → call `UniTextSampleGeometry` when enabled so live resize updates propagate.
2. **VR Single Pass Stereo Instanced (SPS-I) Macros** (Mandatory for VR compatibility):
   ```hlsl
   struct appdata {
       float4 vertex : POSITION;
       float2 uv : TEXCOORD0;
       UNITY_VERTEX_INPUT_INSTANCE_ID
   };
   struct v2f {
       float4 pos : SV_POSITION;
       float2 uv : TEXCOORD0;
       UNITY_VERTEX_OUTPUT_STEREO
   };
   ```
   Call `UNITY_SETUP_INSTANCE_ID(v); UNITY_INITIALIZE_OUTPUT(v2f, o); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);` in `vert`, and `UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);` in `frag`.
3. **Stereo Billboard Math**:
   If your custom skin supports camera-facing billboard mode, always compute the camera center position using both eyes under `USING_STEREO_MATRICES`:
   ```hlsl
   #if defined(USING_STEREO_MATRICES)
       float3 camPos = 0.5 * (unity_StereoWorldSpaceCameraPos[0] + unity_StereoWorldSpaceCameraPos[1]);
   #else
       float3 camPos = _WorldSpaceCameraPos;
   #endif
   ```
4. **Preserve UV Orientation, Dynamic Viewport Aspect & Ring-Buffer Row Mapping**:
   Standard Unity Quad UVs run `u: 0 → 1` (left-to-right) and `v: 0 → 1` (bottom-to-top). Convert `v` to top-down row index using `float topV = 1.0 - uv.y;` before computing `cellRow = floor(topV * activeRows)` (`4..12`, default `6`). Compute physical panel aspect in `vert` from `unity_ObjectToWorld` column lengths (`scale.x / scale.y`) (after applying live resize scale from `UniTextSampleGeometry` when `_LiveGeometry > 0.5`) and cache `UniTextSampleTopRow` + `UniTextSampleStatus` in `nointerpolation` vertex outputs so `frag` avoids duplicate buffer reads. Map `uint physRow = (cellRow + topRow) % activeRows;` and call `UniTextSampleRowStyle(..., rowMaxPx)` before binary-searching `UniTextSampleLayout` (skipping the binary search whenever `penX >= rowMaxPx`), and prefer `UniTextSampleGlyphAndShadow` when rendering a 1-pixel shadow.

---

## 3. Hooking Your Mesh into a UniText Prefab

1. Drag a UniText prefab (`Board_Lite_33b`, `Board_Compact_45b`, `Board_Balanced_57b`, `Board_Standard_73b`, or `Board_Turbo_137b`) onto your avatar root.
2. Replace or customize the `DisplayQuad` mesh/material under `BoardPivot` while keeping:
   - `UniTextProp` on the prefab root (non-destructively expands into `ModularAvatarBoneProxy`, `ModularAvatarMergeAnimator`, `ModularAvatarParameters`, `ModularAvatarMenuInstaller`, and `VRCParentConstraint` during NDMF build)
   - `SideFollowAnchor` and `BoardPivot` (keep `BoardPivot` active in Edit Mode for scene preview; NDMF hides it at build time until `UniText_Visible` is toggled on)
   - `ParamBridge` (the `8×1` orthographic camera + `EncoderQuad` that feeds `Buffer_64x32_<Tier>`)
3. If your skin adds extra meshes (frame, bezel, stand), attach them under `BoardPivot` so they automatically follow `UniText_Visible`, `UniText_Scale`, and `UniText_WorldDrop`. Keep total triangle count low and prefer 1–2 material slots to preserve Avatar Performance Rank.
4. Optionally customize `followBone`, `boneOffset`, `viewportWidth`, `viewportHeight`, `displayRows` (`4..12`), `menuName`, `installTargetMenu`, `syncWorldDrop`, or `syncWideRows` directly in the `UniTextProp` Inspector without unpacking or modifying shared package assets.
5. If your avatar's synced parameter budget is tight even at `Lite (33b)`, consider enabling **VRCFury Parameter Compressor** on your avatar's own static menu toggles/sliders (do not compress UniText's real-time OSC parameters `UniText_Ctrl` / `UniText_D*`).

---

## 4. Quest / Android Safe Fallback

`CustomRenderTexture` updates are not supported on VRChat Quest/Android avatars. To ensure Quest safety:
- Keep `stripOnAndroid = true` (default) on `UniTextProp` so `UniTextNdmfPlugin` strips the prop cleanly and non-destructively on Android builds.
- Include `Fallback "Unlit/Transparent"` (or `"Mobile/Diffuse"`) in any custom display shader so unstripped previews never turn pink.
