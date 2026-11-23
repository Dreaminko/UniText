Shader "UniText/BufferCRT"
{
    Properties
    {
        [KeywordEnum(Standard, Compact, Turbo, Lite, Balanced)] _Tier ("Bandwidth Tier", Float) = 0
        [Toggle] _UseParamTex ("Use ParamBridge Texture", Float) = 1
        _ParamTex ("ParamBridge (8x1)", 2D) = "black" {}
        _AuxTex ("Aux Lookup (Block Table + Advances + LUT12, 64x64)", 2D) = "black" {}

        _Ctrl ("Ctrl", Float) = 0
        _D0   ("D0",   Float) = 0
        _D1   ("D1",   Float) = 0
        _D2   ("D2",   Float) = 0
        _D3   ("D3",   Float) = 0
        _D4   ("D4",   Float) = 0
        _D5   ("D5",   Float) = 0
        _D6   ("D6",   Float) = 0
        _D7   ("D7",   Float) = 0
        _D8   ("D8",   Float) = 0
        _D9   ("D9",   Float) = 0
        _D10  ("D10",  Float) = 0
        _D11  ("D11",  Float) = 0
        _D12  ("D12",  Float) = 0
        _D13  ("D13",  Float) = 0
        _D14  ("D14",  Float) = 0
        _D15  ("D15",  Float) = 0
        _LocalClear ("Local Clear", Float) = 0
        _Bank ("Column Bank", Float) = 0
        _RowPage ("Row Page", Float) = 0
    }
    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always
        ColorMask RGBA
        Lighting Off
        Blend One Zero

        Pass
        {
            Name "Update"
            CGPROGRAM
            #pragma target 4.0
            #pragma shader_feature_local _TIER_STANDARD _TIER_COMPACT _TIER_TURBO _TIER_LITE _TIER_BALANCED
            #include "UnityCustomRenderTexture.cginc"
            #include "UniTextCommon.cginc"
            #pragma vertex CustomRenderTextureVertexShader
            #pragma fragment frag

            float _Tier;
            float _UseParamTex;
            sampler2D _ParamTex;
            sampler2D _AuxTex;

            float _Ctrl;
            float _D0, _D1, _D2, _D3, _D4, _D5, _D6, _D7;
            float _D8, _D9, _D10, _D11, _D12, _D13, _D14, _D15;
            float _LocalClear;
            float _Bank;
            float _RowPage;

            struct UniTextPacket
            {
                uint ctrl;
                uint n;
                uint bank;
                uint rowPage;
                uint d[16];
            };

            // Payload size in bytes of D0..D{N-1}. Compact36 reports 5: D0..D3 plus the high nibble of D4
            // (driven by the UniText_D4b4..UniText_D4b7 synced Bools), i.e. 36 payload bits.
            inline uint UniTextGetPayloadBytes()
            {
            #if defined(_TIER_LITE)
                return 3u;
            #elif defined(_TIER_COMPACT)
                return 5u;
            #elif defined(_TIER_BALANCED)
                return 6u;
            #elif defined(_TIER_TURBO)
                return 16u;
            #else
                // Fallback to _Tier property (0=Standard/8B, 1=Compact36/4.5B, 2=Turbo/16B, 3=Lite/3B, 4=Balanced/6B) even if shader keywords are unset
                if (_Tier > 3.5) return 6u;
                if (_Tier > 2.5) return 3u;
                if (_Tier > 1.5) return 16u;
                if (_Tier > 0.5) return 5u;
                return 8u;
            #endif
            }

            // Whole bytes usable by byte-oriented opcodes (WRITE_ASCII / WRITE_PAGE): Compact36's D4 nibble is LUT12-only.
            inline uint UniTextFullBytes(uint payloadBytes)
            {
                return (payloadBytes == 5u) ? 4u : payloadBytes;
            }

            inline float4 UniTextSampleSelf(uint px, uint py)
            {
                float2 uv = float2((px + 0.5) / UNITEXT_CRT_W, (py + 0.5) / UNITEXT_CRT_H);
                return tex2Dlod(_SelfTexture2D, float4(uv, 0.0, 0.0));
            }

            inline uint UniTextPixelCodepoint(float4 cellPx)
            {
                if (UniTextToByte(cellPx.a * 255.0) == 0u)
                {
                    return 0u;
                }
                return (UniTextToByte(cellPx.r * 255.0) << 8) | UniTextToByte(cellPx.g * 255.0);
            }

            inline bool UniTextPacketEqual(float4 a, float4 b)
            {
                return all(abs(a - b) <= (0.5 / 255.0));
            }

            inline bool UniTextCellSpanBounds(uint startCell, uint count, uint pageRow, uint bankBase, out uint minCol, out uint maxCol)
            {
                minCol = 0u;
                maxCol = 0u;
                if (count == 0u || startCell >= 192u)
                {
                    return false;
                }
                uint endCell = min(startCell + count, 192u);
                uint rowCellStart = pageRow * UNITEXT_BANK_COLS;
                uint rowCellEnd = rowCellStart + UNITEXT_BANK_COLS;
                uint loCell = max(startCell, rowCellStart);
                uint hiCell = min(endCell, rowCellEnd);
                if (loCell >= hiCell)
                {
                    return false;
                }
                minCol = bankBase + (loCell - rowCellStart);
                maxCol = bankBase + (hiCell - rowCellStart - 1u);
                return true;
            }

            // Exact [minCol, maxCol] column range within physical row physRow that write packet pkt can modify.
            inline bool UniTextPacketRowBounds(UniTextPacket pkt, uint physRow, out uint minCol, out uint maxCol)
            {
                minCol = 0u;
                maxCol = 0u;
                if (physRow >= UNITEXT_MAX_ROWS)
                {
                    return false;
                }
                uint physPage = (physRow >= UNITEXT_PAGE_ROWS) ? 1u : 0u;
                if (physPage != pkt.rowPage)
                {
                    return false;
                }
                uint pageRow = physRow - physPage * UNITEXT_PAGE_ROWS;
                uint bankBase = pkt.bank * UNITEXT_BANK_COLS;
                uint ctrl = pkt.ctrl;
                uint payloadBytes = pkt.n;

                if (ctrl >= 1u && ctrl <= 96u)
                {
                    uint oddOffset = (payloadBytes == 3u) ? (pkt.d[2] & 1u) : 0u;
                    uint startCell = (ctrl - 1u) * 2u + oddOffset;
                    uint count = payloadBytes / 2u;
                    return UniTextCellSpanBounds(startCell, count, pageRow, bankBase, minCol, maxCol);
                }

                if (ctrl >= 97u && ctrl <= 144u)
                {
                    uint startCell = (ctrl - 97u) * 4u;
                    uint count = UniTextFullBytes(payloadBytes);
                    return UniTextCellSpanBounds(startCell, count, pageRow, bankBase, minCol, maxCol);
                }

                if ((ctrl >= 145u && ctrl <= 239u) || ctrl == 243u)
                {
                    uint slot = (ctrl == 243u) ? 95u : (ctrl - 145u);
                    if (payloadBytes == 5u)
                    {
                        if (slot < 66u)
                        {
                            uint targetRow = slot / 11u;
                            if (pageRow == targetRow)
                            {
                                uint startCol = (slot - targetRow * 11u) * 3u;
                                uint endCol = min(startCol + 3u, UNITEXT_BANK_COLS);
                                if (startCol < endCol)
                                {
                                    minCol = bankBase + startCol;
                                    maxCol = bankBase + endCol - 1u;
                                    return true;
                                }
                            }
                        }
                        return false;
                    }
                    uint oddOffset = (payloadBytes == 8u) ? (pkt.d[7] & 1u) : 0u;
                    uint startCell = slot * 2u + oddOffset;
                    uint count = (payloadBytes * 2u) / 3u;
                    return UniTextCellSpanBounds(startCell, count, pageRow, bankBase, minCol, maxCol);
                }

                if (ctrl >= 244u && ctrl <= 249u)
                {
                    uint targetRow = ctrl - 244u;
                    uint pageSel = (pkt.d[0] >> 5) & 0x07u;
                    uint fullBytes = UniTextFullBytes(payloadBytes);
                    uint count = (fullBytes > 1u) ? (fullBytes - 1u) : 0u;
                    if (pageRow == targetRow && pageSel <= 4u && count > 0u)
                    {
                        uint startCol = pkt.d[0] & 0x1Fu;
                        uint endCol = min(startCol + count, UNITEXT_BANK_COLS);
                        if (startCol < endCol)
                        {
                            minCol = bankBase + startCol;
                            maxCol = bankBase + endCol - 1u;
                            return true;
                        }
                    }
                    return false;
                }

                return false;
            }

            // Applies one idempotent packet to a text (py 0..11), metadata (py 12) or reserved (py 13..15) pixel.
            // Cell addressing (cell = row * 32 + col) targets the 32-column half selected by
            // UniText_Bank (0 = cols 0..31, 1 = cols 32..63) and 6-row page selected by UniText_RowPage (0 = rows 0..5, 1 = rows 6..11).
            float4 UniTextApplyPacket(uint px, uint py, float4 prev, UniTextPacket pkt)
            {
                uint ctrl = pkt.ctrl;
                uint payloadBytes = pkt.n;
                bool inBank = (px >> 5) == pkt.bank;
                uint bankCol = px & 31u;

                // Ctrl == 0: Power-on safe NOP
                if (ctrl == 0u)
                {
                    return prev;
                }

                // Ctrl == 240: CMD_CLEAR_ALL (keeps the live viewport geometry pixel (29, 12))
                if (ctrl == 240u)
                {
                    if (py == UNITEXT_META_ROW && px == UNITEXT_GEOMETRY_PX)
                    {
                        return prev;
                    }
                    return float4(0.0, 0.0, 0.0, 0.0);
                }

                // Ctrl == 241: CMD_CLEAR_ROW (12-bit mask: D0 bits 0..5 -> rows 0..5, D1 bits 0..5 -> rows 6..11) clears whole 64-cell rows and resets cached rowMaxPx
                if (ctrl == 241u)
                {
                    uint mask = (pkt.d[0] & 0x3Fu) | ((pkt.d[1] & 0x3Fu) << 6);
                    if (py < UNITEXT_MAX_ROWS && ((mask >> py) & 1u) != 0u)
                    {
                        return float4(0.0, 0.0, 0.0, 0.0);
                    }
                    if (py == UNITEXT_META_ROW && px < UNITEXT_MAX_ROWS && ((mask >> px) & 1u) != 0u)
                    {
                        return float4(prev.r, prev.g, 0.0, prev.a);
                    }
                    return prev;
                }

                // Ctrl == 250: CMD_SET_VIEW (atomic topRow 0..11 + optional rowStyle 0..11 + optional status in 3 bytes)
                if (ctrl == 250u)
                {
                    if (py == UNITEXT_META_ROW)
                    {
                        uint newTopRow = pkt.d[0] & 0x0Fu;
                        uint newStatus = (pkt.d[0] >> 4) & 0x0Fu;
                        uint targetRow = (pkt.d[1] >> 4) & 0x0Fu;
                        uint colorIdx = pkt.d[1] & 0x0Fu;
                        uint flags = pkt.d[2] & 0x3Fu;
                        uint updateStatus = (pkt.d[2] >> 7) & 1u;

                        if (px < UNITEXT_MAX_ROWS && targetRow < UNITEXT_MAX_ROWS && px == targetRow)
                        {
                            return float4(colorIdx / 255.0, flags / 255.0, prev.b, 1.0);
                        }
                        if (px == 30u && newTopRow < UNITEXT_MAX_ROWS)
                        {
                            return float4(newTopRow / 255.0, 0.0, 0.0, 1.0);
                        }
                        if (px == 31u && updateStatus != 0u)
                        {
                            return float4(newStatus / 255.0, 0.0, 0.0, 1.0);
                        }
                    }
                    return prev;
                }

                // Ctrl == 251: CMD_SET_GEOMETRY (live viewport rows = D0 in 4..12, row_px = D1 * 8 with D1 in 24..80)
                if (ctrl == 251u)
                {
                    if (py == UNITEXT_META_ROW && px == UNITEXT_GEOMETRY_PX)
                    {
                        uint geoRows = pkt.d[0];
                        uint geoPx8 = pkt.d[1];
                        if (geoRows >= 4u && geoRows <= UNITEXT_MAX_ROWS && geoPx8 >= 24u && geoPx8 <= 80u)
                        {
                            return float4(geoRows / 255.0, geoPx8 / 255.0, 0.0, 1.0);
                        }
                    }
                    return prev;
                }

                // All remaining write opcodes only touch text cells inside the selected bank & rowPage
                if (py >= UNITEXT_MAX_ROWS || !inBank)
                {
                    return prev;
                }
                uint rowPage = (py >= UNITEXT_PAGE_ROWS) ? 1u : 0u;
                if (rowPage != pkt.rowPage)
                {
                    return prev;
                }
                uint pageRow = py - rowPage * UNITEXT_PAGE_ROWS;
                uint cell = pageRow * 32u + bankCol;

                // Ctrl == 1..96: WRITE_UTF16 (big-endian BMP codepoints; D2 & 1 selects odd cell on Lite 3B)
                if (ctrl >= 1u && ctrl <= 96u)
                {
                    uint oddOffset = (payloadBytes == 3u) ? (pkt.d[2] & 1u) : 0u;
                    uint startCell = (ctrl - 1u) * 2u + oddOffset;
                    uint count = payloadBytes / 2u;
                    if (cell >= startCell && cell < startCell + count && cell < 192u)
                    {
                        uint idx = cell - startCell;
                        uint hi = pkt.d[idx * 2u];
                        uint lo = pkt.d[idx * 2u + 1u];
                        uint cp = (hi << 8) | lo;
                        return float4(hi / 255.0, lo / 255.0, 0.0, (cp != 0u) ? 1.0 : 0.0);
                    }
                    return prev;
                }

                // Ctrl == 97..144: WRITE_ASCII (1-byte packed writes)
                if (ctrl >= 97u && ctrl <= 144u)
                {
                    uint startCell = (ctrl - 97u) * 4u;
                    uint count = UniTextFullBytes(payloadBytes);
                    if (cell >= startCell && cell < startCell + count && cell < 192u)
                    {
                        uint idx = cell - startCell;
                        uint lo = pkt.d[idx];
                        return float4(0.0, lo / 255.0, 0.0, (lo != 0u) ? 1.0 : 0.0);
                    }
                    return prev;
                }

                // Ctrl == 145..239, 243: WRITE_LUT12 (12-bit codebook writes via the _AuxTex LUT12 region)
                if ((ctrl >= 145u && ctrl <= 239u) || ctrl == 243u)
                {
                    uint slot = (ctrl == 243u) ? 95u : (ctrl - 145u);
                    bool matched = false;
                    uint idx = 0u;
                    if (payloadBytes == 5u)
                    {
                        // Compact36 (3 cells/pkt): slots 0..65 cover 6 rows x 11 3-col chunks (cols 0..31 within row).
                        if (slot < 66u)
                        {
                            uint targetRow = slot / 11u;
                            uint startCol = (slot - targetRow * 11u) * 3u;
                            if (pageRow == targetRow && bankCol >= startCol && bankCol < startCol + 3u)
                            {
                                matched = true;
                                idx = bankCol - startCol;
                            }
                        }
                    }
                    else
                    {
                        uint oddOffset = (payloadBytes == 8u) ? (pkt.d[7] & 1u) : 0u;
                        uint startCell = slot * 2u + oddOffset;
                        uint count = (payloadBytes * 2u) / 3u;
                        if (cell >= startCell && cell < startCell + count && cell < 192u)
                        {
                            matched = true;
                            idx = cell - startCell;
                        }
                    }
                    if (matched)
                    {
                        uint baseByte = (idx >> 1) * 3u;
                        uint lutIdx = ((idx & 1u) == 0u)
                            ? ((pkt.d[baseByte] << 4) | (pkt.d[baseByte + 1u] >> 4))
                            : (((pkt.d[baseByte + 1u] & 0x0Fu) << 8) | pkt.d[baseByte + 2u]);
                        uint hi = 0u;
                        uint lo = 0u;
                        UniTextLookupLut12(_AuxTex, lutIdx, hi, lo);
                        uint cp = (hi << 8) | lo;
                        return float4(hi / 255.0, lo / 255.0, 0.0, (cp != 0u) ? 1.0 : 0.0);
                    }
                    return prev;
                }

                // Ctrl == 244..249: WRITE_PAGE (single high-byte page write per physical row within active rowPage)
                if (ctrl >= 244u && ctrl <= 249u)
                {
                    uint targetRow = ctrl - 244u;
                    if (pageRow == targetRow)
                    {
                        uint startCol = pkt.d[0] & 0x1Fu;
                        uint pageSel = (pkt.d[0] >> 5) & 0x07u;
                        uint fullBytes = UniTextFullBytes(payloadBytes);
                        uint count = (fullBytes > 1u) ? (fullBytes - 1u) : 0u;
                        if (pageSel <= 4u && bankCol >= startCol && bankCol < startCol + count)
                        {
                            uint pageHi = 0x00u;
                            if (pageSel == 0u) pageHi = 0x30u;
                            else if (pageSel == 1u) pageHi = 0xFFu;
                            else if (pageSel == 2u) pageHi = 0x04u;
                            else if (pageSel == 3u) pageHi = 0x20u;
                            else pageHi = 0x00u;

                            uint lo = pkt.d[(bankCol - startCol) + 1u];
                            if (lo == 0u)
                            {
                                return float4(0.0, 0.0, 0.0, 0.0);
                            }
                            return float4(pageHi / 255.0, lo / 255.0, 0.0, 1.0);
                        }
                    }
                    return prev;
                }

                // Reserved Ctrl values (242, 252..255) -> NOP
                return prev;
            }

            inline int UniTextComputeSpanDelta(uint physRow, uint minCol, uint maxCol, UniTextPacket pkt)
            {
                int delta = 0;
                [loop]
                for (uint k = minCol; k <= maxCol; k++)
                {
                    if (k >= UNITEXT_COLS)
                    {
                        break;
                    }
                    float4 oldCell = UniTextSampleSelf(k, physRow);
                    float4 newCell = UniTextApplyPacket(k, physRow, oldCell, pkt);
                    uint oldCp = UniTextPixelCodepoint(oldCell);
                    uint newCp = UniTextPixelCodepoint(newCell);
                    if (newCp != oldCp)
                    {
                        float4 oldLay = UniTextSampleSelf(k, UNITEXT_LAYOUT_ROW0 + physRow);
                        uint oldAdv = UniTextToByte(oldLay.b * 255.0);
                        uint newAdv = UniTextSampleAdvance(_AuxTex, newCp);
                        delta += (int)newAdv - (int)oldAdv;
                    }
                }
                return delta;
            }

            float4 frag(v2f_customrendertexture IN) : SV_Target
            {
                uint px = (uint)clamp(floor(IN.globalTexcoord.x * UNITEXT_CRT_W), 0.0, UNITEXT_CRT_W - 1.0);
                uint py = (uint)clamp(floor(IN.globalTexcoord.y * UNITEXT_CRT_H), 0.0, UNITEXT_CRT_H - 1.0);

                UniTextPacket pkt;
                pkt.n = UniTextGetPayloadBytes();
                uint localClear = 0u;

                if (_UseParamTex > 0.5)
                {
                    float4 p0 = tex2Dlod(_ParamTex, float4(0.5 / 8.0, 0.5, 0.0, 0.0));
                    float4 p1 = tex2Dlod(_ParamTex, float4(1.5 / 8.0, 0.5, 0.0, 0.0));
                    float4 p2 = tex2Dlod(_ParamTex, float4(2.5 / 8.0, 0.5, 0.0, 0.0));
                    float4 p3 = tex2Dlod(_ParamTex, float4(3.5 / 8.0, 0.5, 0.0, 0.0));
                    float4 p4 = tex2Dlod(_ParamTex, float4(4.5 / 8.0, 0.5, 0.0, 0.0));

                    pkt.ctrl = UniTextToByte(p0.r * 255.0);
                    pkt.d[0]  = UniTextToByte(p0.g * 255.0);
                    pkt.d[1]  = UniTextToByte(p0.b * 255.0);
                    pkt.d[2]  = UniTextToByte(p0.a * 255.0);
                    pkt.d[3]  = UniTextToByte(p1.r * 255.0);
                    pkt.d[4]  = UniTextToByte(p1.g * 255.0);
                    pkt.d[5]  = UniTextToByte(p1.b * 255.0);
                    pkt.d[6]  = UniTextToByte(p1.a * 255.0);
                    pkt.d[7]  = UniTextToByte(p2.r * 255.0);
                    pkt.d[8]  = UniTextToByte(p2.g * 255.0);
                    pkt.d[9]  = UniTextToByte(p2.b * 255.0);
                    pkt.d[10] = UniTextToByte(p2.a * 255.0);
                    pkt.d[11] = UniTextToByte(p3.r * 255.0);
                    pkt.d[12] = UniTextToByte(p3.g * 255.0);
                    pkt.d[13] = UniTextToByte(p3.b * 255.0);
                    pkt.d[14] = UniTextToByte(p3.a * 255.0);
                    pkt.d[15] = UniTextToByte(p4.r * 255.0);
                    localClear = UniTextToByte(p4.g * 255.0);
                    pkt.bank = UniTextToByte(p4.b * 255.0) & 1u;
                    pkt.rowPage = UniTextToByte(p4.a * 255.0) & 1u;
                }
                else
                {
                    pkt.ctrl = UniTextToByte(_Ctrl);
                    pkt.d[0]  = UniTextToByte(_D0);
                    pkt.d[1]  = UniTextToByte(_D1);
                    pkt.d[2]  = UniTextToByte(_D2);
                    pkt.d[3]  = UniTextToByte(_D3);
                    pkt.d[4]  = UniTextToByte(_D4);
                    pkt.d[5]  = UniTextToByte(_D5);
                    pkt.d[6]  = UniTextToByte(_D6);
                    pkt.d[7]  = UniTextToByte(_D7);
                    pkt.d[8]  = UniTextToByte(_D8);
                    pkt.d[9]  = UniTextToByte(_D9);
                    pkt.d[10] = UniTextToByte(_D10);
                    pkt.d[11] = UniTextToByte(_D11);
                    pkt.d[12] = UniTextToByte(_D12);
                    pkt.d[13] = UniTextToByte(_D13);
                    pkt.d[14] = UniTextToByte(_D14);
                    pkt.d[15] = UniTextToByte(_D15);
                    localClear = (_LocalClear > 0.5) ? 255u : 0u;
                    pkt.bank = (_Bank > 0.5) ? 1u : 0u;
                    pkt.rowPage = (_RowPage > 0.5) ? 1u : 0u;
                }

                if (pkt.n == 5u)
                {
                    // Compact36: only the high nibble of D4 is synced (UniText_D4b4..D4b7)
                    pkt.d[4] &= 0xF0u;
                }

                if (localClear > 127u)
                {
                    // Local clear wipes text/styles/dedup-cache/layout while keeping live viewport geometry pixel
                    if (py == UNITEXT_META_ROW && px == UNITEXT_GEOMETRY_PX)
                    {
                        return UniTextSampleSelf(px, py);
                    }
                    return float4(0.0, 0.0, 0.0, 0.0);
                }

                // 5-pixel packet fingerprint in reserved row 13: skip redundant consecutive frames (at ~98% idle savings)
                float4 curP0 = float4(pkt.ctrl / 255.0, pkt.d[0] / 255.0, pkt.d[1] / 255.0, pkt.d[2] / 255.0);
                float4 curP1 = float4(pkt.d[3] / 255.0, pkt.d[4] / 255.0, pkt.d[5] / 255.0, pkt.d[6] / 255.0);
                float4 curP2 = float4(pkt.d[7] / 255.0, pkt.d[8] / 255.0, pkt.d[9] / 255.0, pkt.d[10] / 255.0);
                float4 curP3 = float4(pkt.d[11] / 255.0, pkt.d[12] / 255.0, pkt.d[13] / 255.0, pkt.d[14] / 255.0);
                float4 curP4 = float4(pkt.d[15] / 255.0, 0.0, (float)pkt.bank, (float)pkt.rowPage);

                bool samePacket =
                    UniTextPacketEqual(UniTextSampleSelf(0u, UNITEXT_CACHE_ROW), curP0) &&
                    UniTextPacketEqual(UniTextSampleSelf(1u, UNITEXT_CACHE_ROW), curP1) &&
                    UniTextPacketEqual(UniTextSampleSelf(2u, UNITEXT_CACHE_ROW), curP2) &&
                    UniTextPacketEqual(UniTextSampleSelf(3u, UNITEXT_CACHE_ROW), curP3) &&
                    UniTextPacketEqual(UniTextSampleSelf(4u, UNITEXT_CACHE_ROW), curP4);

                if (samePacket)
                {
                    return UniTextSampleSelf(px, py);
                }

                // Reserved row 13: store the active packet fingerprint at (0..4, 13)
                if (py == UNITEXT_CACHE_ROW)
                {
                    if (px == 0u) return curP0;
                    if (px == 1u) return curP1;
                    if (px == 2u) return curP2;
                    if (px == 3u) return curP3;
                    if (px == 4u) return curP4;
                    return float4(0.0, 0.0, 0.0, 0.0);
                }

                // Rows 0..15: text cells (0..11), metadata (12), reserved (14..15)
                if (py < UNITEXT_LAYOUT_ROW0)
                {
                    float4 prev = UniTextSampleSelf(px, py);
                    if (py == UNITEXT_META_ROW && px < UNITEXT_MAX_ROWS)
                    {
                        uint minCol = 0u;
                        uint maxCol = 0u;
                        if (UniTextPacketRowBounds(pkt, px, minCol, maxCol))
                        {
                            int delta = UniTextComputeSpanDelta(px, minCol, maxCol, pkt);
                            float4 lastLay = UniTextSampleSelf(UNITEXT_COLS - 1u, UNITEXT_LAYOUT_ROW0 + px);
                            uint oldTotal = ((UniTextToByte(lastLay.r * 255.0) << 8) | UniTextToByte(lastLay.g * 255.0)) + UniTextToByte(lastLay.b * 255.0);
                            uint newTotal = (uint)max(0, (int)oldTotal + delta);
                            uint rowMaxQ = min(255u, (newTotal + 3u) >> 2);
                            return float4(prev.r, prev.g, rowMaxQ / 255.0, prev.a);
                        }
                    }
                    return UniTextApplyPacket(px, py, prev, pkt);
                }

                // Rows 16..27: incremental O(packet-width) proportional layout for physical row (py - 16)
                if (py < UNITEXT_LAYOUT_ROW0 + UNITEXT_MAX_ROWS)
                {
                    uint physRow = py - UNITEXT_LAYOUT_ROW0;
                    if (pkt.ctrl == 0u || pkt.ctrl == 242u || pkt.ctrl >= 250u)
                    {
                        return UniTextSampleSelf(px, py);
                    }
                    if (pkt.ctrl == 240u)
                    {
                        return float4(0.0, 0.0, 0.0, 1.0);
                    }
                    if (pkt.ctrl == 241u)
                    {
                        uint mask = (pkt.d[0] & 0x3Fu) | ((pkt.d[1] & 0x3Fu) << 6);
                        return (((mask >> physRow) & 1u) != 0u)
                            ? float4(0.0, 0.0, 0.0, 1.0)
                            : UniTextSampleSelf(px, py);
                    }

                    uint minCol = 0u;
                    uint maxCol = 0u;
                    if (!UniTextPacketRowBounds(pkt, physRow, minCol, maxCol))
                    {
                        return UniTextSampleSelf(px, py);
                    }

                    float4 prevLay = UniTextSampleSelf(px, py);
                    if (px < minCol)
                    {
                        return prevLay;
                    }

                    if (px <= maxCol)
                    {
                        float4 baseLay = UniTextSampleSelf(minCol, py);
                        uint xStart = (UniTextToByte(baseLay.r * 255.0) << 8) | UniTextToByte(baseLay.g * 255.0);
                        [loop]
                        for (uint k = minCol; k < px; k++)
                        {
                            float4 cellK = UniTextApplyPacket(k, physRow, UniTextSampleSelf(k, physRow), pkt);
                            xStart += UniTextSampleAdvance(_AuxTex, UniTextPixelCodepoint(cellK));
                        }
                        float4 ownCell = UniTextApplyPacket(px, physRow, UniTextSampleSelf(px, physRow), pkt);
                        uint advance = UniTextSampleAdvance(_AuxTex, UniTextPixelCodepoint(ownCell));
                        return float4((xStart >> 8) / 255.0, (xStart & 0xFFu) / 255.0, advance / 255.0, 1.0);
                    }

                    int delta = UniTextComputeSpanDelta(physRow, minCol, maxCol, pkt);
                    if (delta == 0)
                    {
                        return prevLay;
                    }
                    int oldX = (int)((UniTextToByte(prevLay.r * 255.0) << 8) | UniTextToByte(prevLay.g * 255.0));
                    uint newX = (uint)max(0, oldX + delta);
                    return float4((newX >> 8) / 255.0, (newX & 0xFFu) / 255.0, prevLay.b, 1.0);
                }

                // Rows 28..31: reserved
                return float4(0.0, 0.0, 0.0, 0.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
