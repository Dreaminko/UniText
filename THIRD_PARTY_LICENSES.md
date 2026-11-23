# Third-Party Licenses & Attributions

UniText code, shaders, OSC protocol specification, and documentation are licensed under the **MIT License** (see [LICENSE](LICENSE)).

---

## Bundled Font Atlas Assets

### Fusion Pixel Font (缝合像素字体)
- **Source**: [TakWolf/fusion-pixel-font](https://github.com/TakWolf/fusion-pixel-font) (version `2026.11.10`, `12px-proportional` TTF variants: `fusion-pixel-12px-proportional-zh_hans.ttf`, `fusion-pixel-12px-proportional-zh_hant.ttf`, `fusion-pixel-12px-proportional-ja.ttf`, `fusion-pixel-12px-proportional-ko.ttf`, `fusion-pixel-12px-proportional-latin.ttf`, `fusion-pixel-12px-proportional-zh_hk.ttf`, `fusion-pixel-12px-proportional-zh_tw.ttf`)
- **License**: **SIL Open Font License 1.1 (OFL-1.1)**
- **Usage**: Used by `tools/font-atlas/generate_atlas.py` to bake the bit-packed Unicode BMP glyph atlas (`unity/Packages/dev.dreaminko.unitext/Textures/FontAtlas_BMP_16x16.png`) and the proportional advance width table (`unity/Packages/dev.dreaminko.unitext/Textures/GlyphAdvance_128x128.png` and `spec/glyph-advance.json`).
- **Notes**: The font atlas contains 1-bit pixel-exact 12px glyph bitmaps generated from SIL OFL 1.1 open-source Fusion Pixel Font covering Latin, Greek, Cyrillic, Simplified/Traditional Chinese, Japanese Kana/Kanji, Korean Hangul, and punctuation/symbols. No proprietary or GPL-licensed system fonts are bundled.

```text
SIL OPEN FONT LICENSE Version 1.1 - 26 February 2007

PREAMBLE
The goals of the Open Font License (OFL) are to stimulate worldwide
development of collaborative font projects, to support the font creation
efforts of academic and linguistic communities, and to provide a free and
open framework in which fonts may be shared and improved in partnership
with others.

The OFL allows the licensed fonts to be used, studied, modified and
redistributed freely as long as they are not sold by themselves. The
fonts, including any derivative works, can be bundled, embedded,
redistributed and/or sold with any software provided that any reserved
names are not used by derivative works. The fonts and derivatives,
however, cannot be released under any other type of license. The
requirement for fonts to remain under this license does not apply
to any document created using the fonts or their derivatives.
```

---

## Unity Package Runtime / Editor Dependencies (External, Declared via VPM)

These packages are not redistributed inside UniText; VCC/VPM installs them automatically from their official repositories:

1. **VRChat SDK3 - Avatars (`com.vrchat.avatars`)**
   - Vendor: VRChat Inc.
   - Reference: https://creators.vrchat.com/sdk/
2. **Modular Avatar (`nadena.dev.modular-avatar`)**
   - Author: bd_ (Nadena)
   - License: MIT License
   - Reference: https://modular-avatar.nadena.dev/

---

## Python Development & Verification Tools (`tools/`)

- **Pillow** — HPND License
- **fontTools** — MIT License
- **NumPy** — BSD-3-Clause License
- **python-osc** — Unlicense
