#ifndef UNITEXT_COMMON_INCLUDED
#define UNITEXT_COMMON_INCLUDED

#include "UnityCG.cginc"

// UniText 64 x 32 CRT geometry (rows 0..11 = 64-cell text rows across 2 row pages, row 12 = metadata, rows 16..27 = layout)
#define UNITEXT_CRT_W 64.0
#define UNITEXT_CRT_H 32.0
#define UNITEXT_COLS 64u
#define UNITEXT_BANK_COLS 32u
#define UNITEXT_PAGE_ROWS 6u
#define UNITEXT_MAX_ROWS 12u
#define UNITEXT_META_ROW 12u
#define UNITEXT_CACHE_ROW 13u
#define UNITEXT_GEOMETRY_PX 29u
#define UNITEXT_LAYOUT_ROW0 16u
#define UNITEXT_ROW_PX 384.0
#define UNITEXT_MAX_ADVANCE 12u

// v2 packed atlas / aux texture layout (docs/PROTOCOL.md section 5, tools/font-atlas/atlas_format.py)
#define UNITEXT_SMALL_BITS 121u
#define UNITEXT_LARGE_BITS 192u
#define UNITEXT_AUX_ADV_BASE 1024u
#define UNITEXT_AUX_LUT_BASE 2048u

inline uint UniTextToByte(float v)
{
    return (uint)clamp(round(v), 0.0, 255.0);
}

inline fixed3 UniTextSRGBToTarget(fixed3 srgb)
{
#ifdef UNITY_COLORSPACE_GAMMA
    return srgb;
#else
    return GammaToLinearSpace(srgb);
#endif
}

inline fixed3 UniTextGetPaletteColor(uint idx)
{
    static const fixed3 kPalette[16] = {
        fixed3(1.0000, 1.0000, 1.0000), // 0: Pure White    #FFFFFF
        fixed3(0.7961, 0.8353, 0.8824), // 1: Soft Silver   #CBD5E1
        fixed3(0.2196, 0.7412, 0.9725), // 2: Sky Cyan      #38BDF8
        fixed3(0.2902, 0.8706, 0.5020), // 3: Mint Green    #4ADE80
        fixed3(0.9843, 0.7490, 0.1412), // 4: Warm Amber    #FBBF24
        fixed3(0.9843, 0.4431, 0.5216), // 5: Coral Rose    #FB7185
        fixed3(0.7529, 0.5176, 0.9882), // 6: Lavender      #C084FC
        fixed3(0.1765, 0.8314, 0.7490), // 7: Aqua Teal     #2DD4BF
        fixed3(0.9922, 0.7294, 0.4549), // 8: Soft Peach    #FDBA74
        fixed3(0.5059, 0.5490, 0.9725), // 9: Periwinkle    #818CF8
        fixed3(0.6392, 0.9020, 0.2078), // 10: Lime         #A3E635
        fixed3(0.9569, 0.4471, 0.7137), // 11: Pink         #F472B6
        fixed3(0.9804, 0.8000, 0.0824), // 12: Gold         #FACC15
        fixed3(0.4902, 0.8275, 0.9882), // 13: Ice Blue     #7DD3FC
        fixed3(0.5804, 0.6392, 0.7216), // 14: Muted Slate  #94A3B8
        fixed3(0.9961, 0.9529, 0.7804)  // 15: Bright Cream #FEF3C7
    };
    return UniTextSRGBToTarget(kPalette[idx & 15u]);
}

inline uint UniTextSampleCell(sampler2D bufferTex, uint col, uint row)
{
    float2 uv = float2((col + 0.5) / UNITEXT_CRT_W, (row + 0.5) / UNITEXT_CRT_H);
    float4 px = tex2Dlod(bufferTex, float4(uv, 0.0, 0.0));
    uint a = UniTextToByte(px.a * 255.0);
    if (a == 0u)
    {
        return 0u;
    }
    uint hi = UniTextToByte(px.r * 255.0);
    uint lo = UniTextToByte(px.g * 255.0);
    return (hi << 8) | lo;
}

inline void UniTextSampleRowStyle(sampler2D bufferTex, uint row, out uint colorIdx, out uint flags)
{
    float2 uv = float2((row + 0.5) / UNITEXT_CRT_W, (UNITEXT_META_ROW + 0.5) / UNITEXT_CRT_H);
    float4 px = tex2Dlod(bufferTex, float4(uv, 0.0, 0.0));
    colorIdx = UniTextToByte(px.r * 255.0) & 0x0Fu;
    flags = UniTextToByte(px.g * 255.0);
}

inline void UniTextSampleRowStyle(sampler2D bufferTex, uint row, out uint colorIdx, out uint flags, out uint rowMaxPx)
{
    float2 uv = float2((row + 0.5) / UNITEXT_CRT_W, (UNITEXT_META_ROW + 0.5) / UNITEXT_CRT_H);
    float4 px = tex2Dlod(bufferTex, float4(uv, 0.0, 0.0));
    colorIdx = UniTextToByte(px.r * 255.0) & 0x0Fu;
    flags = UniTextToByte(px.g * 255.0);
    rowMaxPx = UniTextToByte(px.b * 255.0) * 4u;
}

inline uint UniTextSampleStatus(sampler2D bufferTex)
{
    float2 uv = float2(31.5 / UNITEXT_CRT_W, (UNITEXT_META_ROW + 0.5) / UNITEXT_CRT_H);
    float4 px = tex2Dlod(bufferTex, float4(uv, 0.0, 0.0));
    return UniTextToByte(px.r * 255.0);
}

inline uint UniTextSampleTopRow(sampler2D bufferTex, uint activeRows)
{
    float2 uv = float2(30.5 / UNITEXT_CRT_W, (UNITEXT_META_ROW + 0.5) / UNITEXT_CRT_H);
    float4 px = tex2Dlod(bufferTex, float4(uv, 0.0, 0.0));
    uint topRow = UniTextToByte(px.r * 255.0);
    return (topRow < activeRows) ? topRow : 0u;
}

// Proportional layout row (py = 16 + physRow): R = xStart >> 8, G = xStart & 0xFF, B = advance
inline void UniTextSampleLayout(sampler2D bufferTex, uint col, uint physRow, out uint xStart, out uint advance)
{
    float2 uv = float2((col + 0.5) / UNITEXT_CRT_W, (UNITEXT_LAYOUT_ROW0 + physRow + 0.5) / UNITEXT_CRT_H);
    float4 px = tex2Dlod(bufferTex, float4(uv, 0.0, 0.0));
    xStart = (UniTextToByte(px.r * 255.0) << 8) | UniTextToByte(px.g * 255.0);
    advance = UniTextToByte(px.b * 255.0);
}

// v2 live viewport geometry (CMD_SET_GEOMETRY) at metadata pixel (29, 12): R = rows (4..12), G = rowPx / 8 (24..80), A = valid.
// Returns false (and leaves outputs untouched) until a sender has written a valid geometry.
inline bool UniTextSampleGeometry(sampler2D bufferTex, inout uint rows, inout float rowPx)
{
    float2 uv = float2((UNITEXT_GEOMETRY_PX + 0.5) / UNITEXT_CRT_W, (UNITEXT_META_ROW + 0.5) / UNITEXT_CRT_H);
    float4 px = tex2Dlod(bufferTex, float4(uv, 0.0, 0.0));
    uint r = UniTextToByte(px.r * 255.0);
    uint p8 = UniTextToByte(px.g * 255.0);
    if (UniTextToByte(px.a * 255.0) == 0u || r < 4u || r > UNITEXT_MAX_ROWS || p8 < 24u || p8 > 80u)
    {
        return false;
    }
    rows = r;
    rowPx = (float)(p8 * 8u);
    return true;
}

// ---------------------------------------------------------------------------
// v2 aux lookup texture (UniTextAux_64x64.png, 4,096 texels, PNG row 0 = top):
//   texels    0..1023 : block table (64 codepoints per block)
//   texels 1024..2047 : mixed-advance nibble table (8 texels per mixed block)
//   texels 2048..4095 : LUT12 codebook (2 entries per texel)
// ---------------------------------------------------------------------------
inline uint4 UniTextAuxFetch(sampler2D auxTex, uint texel)
{
    float u = ((float)(texel & 63u) + 0.5) / 64.0;
    float v = 1.0 - ((float)(texel >> 6) + 0.5) / 64.0;
    float4 p = tex2Dlod(auxTex, float4(u, v, 0.0, 0.0));
    return uint4(UniTextToByte(p.r * 255.0), UniTextToByte(p.g * 255.0), UniTextToByte(p.b * 255.0), UniTextToByte(p.a * 255.0));
}

inline uint UniTextSelectByte(uint4 p, uint idx)
{
    return (idx == 0u) ? p.x : ((idx == 1u) ? p.y : ((idx == 2u) ? p.z : p.w));
}

// Proportional glyph advance (0..12 px): uniform per 64-codepoint block, or a 4-bit entry in the mixed table.
inline uint UniTextSampleAdvance(sampler2D auxTex, uint cp)
{
    cp &= 0xFFFFu;
    if (cp == 0u)
    {
        return 0u;
    }
    uint a = UniTextAuxFetch(auxTex, cp >> 6).w;
    if ((a & 0x80u) == 0u)
    {
        return min(a, UNITEXT_MAX_ADVANCE);
    }
    uint local = cp & 63u;
    uint4 p = UniTextAuxFetch(auxTex, UNITEXT_AUX_ADV_BASE + (a & 0x7Fu) * 8u + (local >> 3));
    uint b = UniTextSelectByte(p, (local >> 1) & 3u);
    uint nib = ((local & 1u) != 0u) ? (b & 0x0Fu) : (b >> 4);
    return min(nib, UNITEXT_MAX_ADVANCE);
}

inline void UniTextLookupLut12(sampler2D auxTex, uint lutIdx, out uint hi, out uint lo)
{
    uint idx = lutIdx & 0x0FFFu;
    if (idx == 0u)
    {
        hi = 0u;
        lo = 0u;
        return;
    }
    uint4 p = UniTextAuxFetch(auxTex, UNITEXT_AUX_LUT_BASE + (idx >> 1));
    bool odd = (idx & 1u) != 0u;
    hi = odd ? p.z : p.x;
    lo = odd ? p.w : p.y;
}

// Resolved glyph location inside the packed atlas bitstream.
struct UniTextGlyphRef
{
    uint cls;      // 0 = absent (procedural tofu), 1 = small 11x11 cell, 2 = large 12x16 cell
    uint bitBase;  // first bit of this glyph in the atlas bitstream
};

inline UniTextGlyphRef UniTextGetGlyphRef(sampler2D auxTex, uint cp)
{
    uint4 e = UniTextAuxFetch(auxTex, (cp & 0xFFFFu) >> 6);
    UniTextGlyphRef g;
    g.cls = (e.z >> 2) & 3u;
    uint baseTexel = (e.x | (e.y << 8) | ((e.z & 3u) << 16)) << 1;
    g.bitBase = baseTexel * 32u + (cp & 63u) * ((g.cls == 1u) ? UNITEXT_SMALL_BITS : UNITEXT_LARGE_BITS);
    return g;
}

inline float UniTextSampleTofuBit(uint gx, uint gy)
{
    bool hEdge = (gy == 3u || gy == 13u) && gx >= 1u && gx <= 10u;
    bool vEdge = (gx == 1u || gx == 10u) && gy >= 3u && gy <= 13u;
    return (hEdge || vEdge) ? 1.0 : 0.0;
}

inline bool UniTextGlyphBitIndex(UniTextGlyphRef g, uint gx, uint gy, out uint bitIdx)
{
    bitIdx = 0u;
    if (gx >= 16u || gy >= 16u)
    {
        return false;
    }
    if (g.cls == 1u)
    {
        if (gx >= 11u || gy < 3u || gy > 13u)
        {
            return false;
        }
        bitIdx = g.bitBase + (gy - 3u) * 11u + gx;
        return true;
    }
    if (g.cls == 2u)
    {
        if (gx >= 12u)
        {
            return false;
        }
        bitIdx = g.bitBase + gy * 12u + gx;
        return true;
    }
    return false;
}

inline float4 UniTextFetchAtlasTexel(sampler2D atlasTex, float atlasHeight, uint texel)
{
    float u = ((float)(texel & 1023u) + 0.5) / 1024.0;
    float v = 1.0 - ((float)(texel >> 10) + 0.5) / atlasHeight;
    return tex2Dlod(atlasTex, float4(u, v, 0.0, 0.0));
}

inline float UniTextExtractAtlasBit(float4 p, uint bitIdx)
{
    uint byteIdx = (bitIdx >> 3) & 3u;
    float ch = (byteIdx == 0u) ? p.r : ((byteIdx == 1u) ? p.g : ((byteIdx == 2u) ? p.b : p.a));
    uint byteVal = UniTextToByte(ch * 255.0);
    return (float)((byteVal >> (7u - (bitIdx & 7u))) & 1u);
}

// atlasHeight = _FontAtlas_TexelSize.w (packed atlas is 1024 x H)
inline float UniTextSampleGlyphBit(sampler2D atlasTex, float atlasHeight, UniTextGlyphRef g, uint gx, uint gy)
{
    if (g.cls == 0u)
    {
        return (gx < 16u && gy < 16u) ? UniTextSampleTofuBit(gx, gy) : 0.0;
    }
    uint bitIdx;
    if (!UniTextGlyphBitIndex(g, gx, gy, bitIdx))
    {
        return 0.0;
    }
    float4 p = UniTextFetchAtlasTexel(atlasTex, atlasHeight, bitIdx >> 5);
    return UniTextExtractAtlasBit(p, bitIdx);
}

// Sample glyph foreground bit and (when requested & fg == 0) right-down shadow bit,
// reusing the foreground atlas texel whenever (gx - 1, gy - 1) lands in the same 32-bit texel.
inline void UniTextSampleGlyphAndShadow(
    sampler2D atlasTex,
    float atlasHeight,
    UniTextGlyphRef g,
    uint gx,
    uint gy,
    bool sampleShadow,
    out float glyph,
    out float shadow)
{
    glyph = 0.0;
    shadow = 0.0;

    if (g.cls == 0u)
    {
        if (gx < 16u && gy < 16u)
        {
            glyph = UniTextSampleTofuBit(gx, gy);
            if (sampleShadow && glyph < 0.5 && gx > 0u && gy > 0u)
            {
                shadow = UniTextSampleTofuBit(gx - 1u, gy - 1u);
            }
        }
        return;
    }

    uint fgBit = 0u;
    bool hasFg = UniTextGlyphBitIndex(g, gx, gy, fgBit);
    uint fgTexel = 0u;
    float4 fgPx = float4(0.0, 0.0, 0.0, 0.0);
    if (hasFg)
    {
        fgTexel = fgBit >> 5;
        fgPx = UniTextFetchAtlasTexel(atlasTex, atlasHeight, fgTexel);
        glyph = UniTextExtractAtlasBit(fgPx, fgBit);
    }

    if (sampleShadow && glyph < 0.5 && gx > 0u && gy > 0u)
    {
        uint shBit = 0u;
        if (UniTextGlyphBitIndex(g, gx - 1u, gy - 1u, shBit))
        {
            uint shTexel = shBit >> 5;
            float4 shPx = (hasFg && shTexel == fgTexel)
                ? fgPx
                : UniTextFetchAtlasTexel(atlasTex, atlasHeight, shTexel);
            shadow = UniTextExtractAtlasBit(shPx, shBit);
        }
    }
}

#endif // UNITEXT_COMMON_INCLUDED
