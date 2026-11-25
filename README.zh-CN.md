<div align="center">

<img src="Assets/Banner.png" alt="UniText" width="480" />

[English](README.md) · **简体中文** · [日本語](README.ja.md)

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![VPM Package](https://img.shields.io/badge/VPM-dev.dreaminko.unitext-brightgreen.svg)](https://vpm.dreamo.ink/)
![Unity 2022.3.22f1](https://img.shields.io/badge/Unity-2022.3.22f1-lightgrey.svg)
![Platform: PCVR / PC Desktop](https://img.shields.io/badge/Platform-PCVR%20%2F%20PC%20Desktop-purple.svg)

</div>

UniText 让 VRChat PC 端 Avatar 能够实时显示**所有人可见的共享多语言字幕与翻译（中文 / 日文 / 韩文 / 英文 / Unicode BMP）**，彻底突破 VRChat 原生聊天框（Chatbox）的限制。

---

## 核心特性

- **完整 Unicode BMP 与比例排版**：支持 `192–640 px` 可配置行宽（默认 `384 px`）× `4–12` 可配置可见行数（默认 `6` 行）；通过 `UniText_Bank` 支持单行最多 `64` 个字符单元，通过可选 `+1 bit` 的 `UniText_RowPage` 支持 `7–12` 行显示。
- **超低显存占用（约 `712 KB`）**：块索引压缩字库图集（`680 KB`）+ 统一辅助查找表（`16 KB`）+ CRT / ParamBridge（`16 KB`）。
- **高吞吐编码与零拷贝硬件滚动**：12-bit 常用字 LUT 码本（`4,096` 字形）+ 1 字节页压缩（`0x30` 假名/CJK 标点等）+ 环形缓冲区（Ring-Buffer）硬件滚动。
- **丰富的显示与交互能力**：每行 16 色调色板、暗淡/高亮行样式、全局状态指示器、SPS-I 双眼立体看板（Billboard）与世界固定放置（World Drop / `VRCParentConstraint`），以及局内实时调整宽高重排（通过 `CMD_SET_GEOMETRY` 同步，轴参数本身占用 `0` 同步 bit）。
- **NDMF + Modular Avatar 非破坏性工作流**：不会修改磁盘上原有的 Avatar Animator Controller、Expression Parameters 或 Expressions Menu 资源。

---

## 环境要求

| 依赖项 | 版本要求 | 说明 |
| :--- | :--- | :--- |
| **Unity** | `2022.3.22f1` | VRChat SDK3 支持的 Unity 版本 |
| **VRChat SDK - Avatars** | `>= 3.7.0` | `com.vrchat.avatars`（通过 VPM 自动安装） |
| **Modular Avatar** | `>= 1.10.0` | `nadena.dev.modular-avatar`（通过 VPM 自动安装） |
| **NDMF** | `>= 1.5.0` | `nadena.dev.ndmf`（通过 VPM 自动安装） |

---

## 快速安装（VCC / VPM）

1. 点击 **[将 Dreaminko 的 VPM 仓库添加到 VCC](vcc://vpm/addRepo?url=https://vpm.dreamo.ink/index.json)**（或访问 [Dreaminko VPM 仓库主页](https://vpm.dreamo.ink/)），也可以在 VCC 中手动添加仓库地址（`Settings → Packages → Add Repository`）：
   ```text
   https://vpm.dreamo.ink/index.json
   ```
2. 在 VCC 中打开你的 Avatar 项目，从 **Dreaminko's VPM Repository** 添加 **`UniText`**（`dev.dreaminko.unitext`）。VCC 会自动安装 `com.vrchat.avatars`、`nadena.dev.modular-avatar` 和 `nadena.dev.ndmf`。
3. 打开 Unity（`2022.3.22f1`），在顶部菜单栏点击 **`UniText → Install to Avatar`**（或 **`Tools → UniText → Install to Avatar`**），将场景层级（Hierarchy）中的 **Avatar** 拖入窗口，选择档位与放置位置后点击 **Install**（也可手动将 `Packages/dev.dreaminko.unitext/Prefabs/` 中的 Prefab 拖到 Avatar 根节点下）。直接上传或进入 Play Mode 即可预览！

---

## Modular Avatar 非破坏性装配（`UniTextProp`）

UniText 的 Prefab 绝不会直接改写你磁盘上的基础 Animator Controller、Expression Parameters 或 Expressions Menu 资产。在构建 Avatar 或进入 Play Mode 时，内置的 NDMF 插件（`UniTextNdmfPlugin`）会在 Modular Avatar 之前运行，自动将每个 `UniTextProp` 干净地展开：

- **自动骨骼绑定（`MA Bone Proxy`）**：将 `SideFollowAnchor` 挂载到配置的人形骨骼（默认 `Chest`，支持自动对齐偏移），并通过 `VRCParentConstraint` 绑定 `BoardPivot`，实现平滑的侧身跟随与世界放置切换。
- **Direct BlendTree FX 合并（`MA Merge Animator`）**：在内存中生成对应档位的 Direct BlendTree FX 图层，并以 `MergeAnimatorPathMode.Relative` 和 `matchAvatarWriteDefaults = true` 进行合并。
- **自动参数注册（`MA Parameters`）**：注册 `UniText_Visible`、`UniText_Ctrl`、`UniText_D0..D{N-1}`，以及可选同步参数 `UniText_WorldDrop`、`UniText_Bank`（宽行）和 `UniText_RowPage`（`> 6` 行），同时附加仅限本地的 UI 控制参数（`Width`、`Height`、`Scale`、`Opacity`）与视口几何标记（`UniText_Rows`、`UniText_RowPx8`）。
- **可编辑层级菜单（`MA Menu Installer` + `MA Menu Item`）**：在组件下直接生成可编辑的 `"UniText"` 子菜单层级（`MA Menu Installer` + `MA Menu Item`），方便直接在 Hierarchy 中改名、配置图标、调整顺序、设置默认值或拖拽移动到其他菜单；若未提前生成，构建时也会自动兜底生成。
- **Quest / Android 自动剥离**：默认开启 `stripOnAndroid` 时，构建 Android 平台 Avatar 会自动移除 UniText 组件与节点，同时完全保留 PC 端配置。

### 游戏内表情菜单控制

在 VRChat 的 Expressions Menu 中打开 **`UniText`** 子菜单即可使用：

- **Show Board**：开关字幕板显示（`UniText_Visible`）
- **World Drop**：在空间中固定字幕板，或恢复跟随角色（`UniText_WorldDrop`）
- **Billboard**：切换双眼立体安全的朝向摄像机模式（`UniText_Billboard`）
- **Clear Screen**：清空本地字幕缓冲区（`UniText_LocalClear`）
- **Appearance** *（子菜单）*：
  - **Scale**：调整字幕板物理大小（`UniText_Scale`）
  - **Opacity**：调整背景与文字不透明度（`UniText_Opacity`）
  - **Width** 与 **Height**：实时调整行宽与可见行数并自动重排（`UniText_Width`、`UniText_Height`）

---

## 参数预算档位

| 档位 | Prefab | 同步参数位数 | 载荷容量 | LUT12 中日韩/包 | 页编码 (`0x30`)/包 | UTF-16/包 | ASCII/包 | 适用场景 |
| :--- | :--- | :---: | :---: | :---: | :---: | :---: | :---: | :--- |
| **Lite** | `Board_Lite_33b.prefab` | **33 bits** | 3B (`D0..D2`) | 2 | 2 | 1 | 3 | 极度紧张的参数预算（支持高速环形滚动） |
| **Compact** | `Board_Compact_45b.prefab` | **45 bits** | 36b (`D0..D3` + `D4b4..D4b7`) | 3 | 3 | 2 | 4 | 较紧张的参数预算（CJK 传输速度 +50%） |
| **Balanced** | `Board_Balanced_57b.prefab` | **57 bits** | 6B (`D0..D5`) | 4 | 5 | 3 | 6 | 兼顾参数占用与 CJK 速度（比 Standard 省 16 bits） |
| **Standard** *(默认)* | `Board_Standard_73b.prefab` | **73 bits** | 8B (`D0..D7`) | 5 | 7 | 4 | 8 | 大多数 Avatar（推荐） |
| **Turbo** | `Board_Turbo_137b.prefab` | **137 bits** | 16B (`D0..D15`) | 10 | 15 | 8 | 16 | 追求最低字幕延迟 |

### 可选同步位与显存明细

- **可选世界放置同步（`+1 bit`）**：同步 `UniText_WorldDrop` 占用 `+1 bit`（默认开启，使远端玩家也能看到字幕板固定在空间中；如不需要可在 `UniTextProp` 或 `ModularAvatarParameters` 中改为仅本地）。
- **可选宽行模式（`+1 bit`）**：同步 `UniText_Bank`（`syncWideRows`，默认开启）占用 `+1 bit`，使单行最多可容纳 `64` 个比例字符单元（英文容量约翻倍）。关闭后保留每行 `32` 单元与比例排版。
- **可选扩展行数（当 `> 6` 行或开启实时缩放时 `+1 bit`）**：通过 `UniText_RowPage`（`+1 bit`）解锁 `7..12` 行显示。
- **可选实时宽高重排（`+1 bit`，默认开启）**：`UniText_Width` 和 `UniText_Height` 仅限本地（`0` 同步位）；[VRCS](https://github.com/Dreaminko/VRCS) 会通过 `CMD_SET_GEOMETRY` 向本地与远端玩家广播重排后的 `(rows, row_px)`，因此开启 `Allow In-Game Resize` 仅额外占用 `UniText_RowPage` 的 `+1 bit`（关闭可节省 `1` bit）。
- **仅本地 UI 控制与能力标记（`0` 同步位）**：`UniText_ProtoVer`、`UniText_Rows`、`UniText_RowPx8`、`UniText_Width`、`UniText_Height`、`UniText_Billboard`、`UniText_Scale`、`UniText_Opacity`、`UniText_LocalClear`。
- **显存占用**：块索引压缩字库图集（`FontAtlas_BMP_Packed.png`，`680 KB`）+ 统一辅助表（`UniTextAux_64x64.png`，`16 KB`）+ CRT / ParamBridge（`16 KB`）≈ **`712 KB`**。
- **参数预算检查建议**：可在 Unity 中点击 **`Tools → UniText → Check Avatar Parameter Budget`** 查看 Avatar 剩余参数空间。若角色自身的换装或滑块参数占用过多位数，建议仅对角色自身的静态菜单参数使用 **VRCFury Parameter Compressor**（切勿压缩 UniText 的实时 OSC 数据参数）。

---

## Android / Quest 说明

VRChat Quest / Android 端不支持 `CustomRenderTexture` 更新以及多 Pass 自定义 CRT 着色器。在 `UniTextProp` 默认开启 `stripOnAndroid = true` 的情况下，UniText 的 NDMF 插件会在构建 Quest / Android 版本时自动以非破坏方式剥离挂件，确保跨平台 Avatar 能够干净打包且不会出现粉红材质或报错着色器。

---

## 开发者快速上手

### 1. 运行自动化测试套件
```bash
python tools/test_all.py
```

### 2. 校验字库图集与重新生成 LUT12 码本
```bash
python tools/font-atlas/verify_atlas.py
python tools/font-atlas/generate_lut12.py
```

### 3. 向本地 VRChat / Unity (`127.0.0.1:9000`) 发送测试字幕
```bash
pip install -r tools/osc-tester/requirements.txt
python tools/osc-tester/send_demo.py --verify-vectors
python tools/osc-tester/send_demo.py --tier balanced --interval 150 --mode bundle --demo long --loop
python tools/osc-tester/send_demo.py --tier lite --interval 150 --demo scroll --loop
python tools/osc-tester/send_demo.py --oscquery-url http://127.0.0.1:9001/avatar/parameters --tier auto --demo long
```

### 4. 在 Unity 中校验资源 / 导出 `.unitypackage`
使用 **Unity 2022.3.22f1** 打开 `unity/` 工程（确保已通过 VCC/VPM 解析 `com.vrchat.avatars`、`nadena.dev.modular-avatar` 与 `nadena.dev.ndmf`）：
- `Packages/dev.dreaminko.unitext/Prefabs/` 下的 Prefab 可通过 **NDMF + Modular Avatar** 直接使用，无需任何预烘焙步骤。
- 维护者可通过以下菜单校验刷新资源绑定或导出离线 `.unitypackage`：
  - **`Tools → UniText → Verify & Refresh Package Assets`**
  - **`Tools → UniText → Export .unitypackage`**

---

## 相关文档与开源协议

- [协议规范 (`v2`)](docs/PROTOCOL.md) — CRT 内存布局、OSC 参数定义、指令集与发送端规范
- [自定义外观与挂件制作指南](docs/SKIN_GUIDE.md) — `UniTextCommon.cginc` 接口说明与自定义着色器开发
- [协议测试向量](spec/test-vectors.json) — 供发送端实现进行校验的标准测试向量
- [第三方许可声明](THIRD_PARTY_LICENSES.md) — 内置 [Fusion Pixel Font（缝合像素字体）](https://github.com/TakWolf/fusion-pixel-font)（`SIL OFL-1.1`）、[Google Material Symbols](https://github.com/google/material-design-icons)（`Apache-2.0`）与依赖说明
- [MIT License](LICENSE)
