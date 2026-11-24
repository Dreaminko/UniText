Shader "UniText/Display"
{
    Properties
    {
        _BufferTex ("CRT Buffer (64x32)", 2D) = "black" {}
        _FontAtlas ("Packed Font Atlas (1024xH)", 2D) = "black" {}
        _AuxTex ("Aux Lookup (Block Table + Advances + LUT12, 64x64)", 2D) = "black" {}
        _BgColor ("Background Color", Color) = (0.05, 0.065, 0.10, 0.86)
        _BorderColor ("Border Color", Color) = (0.56, 0.68, 0.88, 0.42)
        _Opacity ("Master Opacity", Range(0.0, 1.0)) = 1.0
        _CornerRadius ("Corner Radius", Range(0.0, 0.25)) = 0.085
        _Padding ("Inner Padding", Range(0.01, 0.15)) = 0.045
        _ShadowStrength ("Text Shadow Strength", Range(0.0, 1.0)) = 0.68
        _DisplayRows ("Base Display Rows", Range(4, 12)) = 6
        _RowPx ("Base Logical Row Width (px)", Range(192, 640)) = 384
        [Toggle] _LiveGeometry ("Follow Sender Geometry (CMD_SET_GEOMETRY)", Float) = 0
        [Toggle] _KeepGlyphAspect ("Preserve Glyph Aspect", Float) = 1
        [Toggle] _Billboard ("Stereo Billboard", Float) = 0
        _AnchorOffset ("Anchor Offset (XY)", Vector) = (0, 0, 0, 0)
    }
    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "RenderType"="Transparent"
            "IgnoreProjector"="True"
            "VRCFallback"="Hidden"
        }
        Cull Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma target 4.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UniTextCommon.cginc"

            sampler2D _BufferTex;
            sampler2D _FontAtlas;
            float4 _FontAtlas_TexelSize;
            sampler2D _AuxTex;
            fixed4 _BgColor;
            fixed4 _BorderColor;
            float _Opacity;
            float _CornerRadius;
            float _Padding;
            float _ShadowStrength;
            float _DisplayRows;
            float _RowPx;
            float _LiveGeometry;
            float _KeepGlyphAspect;
            float _Billboard;
            float4 _AnchorOffset;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos         : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float  panelAspect : TEXCOORD1;
                nointerpolation float4 geo : TEXCOORD2; // (live rows, live row px, topRow, status)
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // Base geometry comes from the material (baked by NDMF); when live resize is enabled the sender's
            // CMD_SET_GEOMETRY (CRT metadata pixel (29, 12)) overrides it for every viewer, local and remote.
            inline float2 UniTextResolveGeometry()
            {
                uint rows = (uint)clamp(round(_DisplayRows), 4.0, (float)UNITEXT_MAX_ROWS);
                float rowPx = clamp(round(_RowPx), 192.0, 640.0);
                if (_LiveGeometry > 0.5)
                {
                    UniTextSampleGeometry(_BufferTex, rows, rowPx);
                }
                return float2((float)rows, rowPx);
            }

            inline float3 UniTextGetStereoCenterCamPos()
            {
            #if defined(USING_STEREO_MATRICES)
                return 0.5 * (unity_StereoWorldSpaceCameraPos[0] + unity_StereoWorldSpaceCameraPos[1]);
            #else
                return _WorldSpaceCameraPos;
            #endif
            }

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float3 baseScale = float3(
                    length(float3(unity_ObjectToWorld[0][0], unity_ObjectToWorld[1][0], unity_ObjectToWorld[2][0])),
                    length(float3(unity_ObjectToWorld[0][1], unity_ObjectToWorld[1][1], unity_ObjectToWorld[2][1])),
                    length(float3(unity_ObjectToWorld[0][2], unity_ObjectToWorld[1][2], unity_ObjectToWorld[2][2]))
                );

                // Live resize: physical panel size scales linearly with (row_px, rows) around the anchor point.
                float2 geo = UniTextResolveGeometry();
                float baseRows = clamp(round(_DisplayRows), 4.0, (float)UNITEXT_MAX_ROWS);
                float baseRowPx = clamp(round(_RowPx), 192.0, 640.0);
                float2 liveScale = float2(geo.y / baseRowPx, geo.x / baseRows);
                float2 anchor = _AnchorOffset.xy;
                float4 vtx = float4((v.vertex.xy - anchor) * liveScale + anchor, v.vertex.z, 1.0);
                float3 scale = float3(baseScale.xy * liveScale, baseScale.z);
                uint activeRows = (uint)clamp(round(geo.x), 4.0, (float)UNITEXT_MAX_ROWS);
                uint topRow = UniTextSampleTopRow(_BufferTex, activeRows);
                uint status = UniTextSampleStatus(_BufferTex);
                o.geo = float4(geo.x, geo.y, (float)topRow, (float)status);
                o.panelAspect = scale.x / max(scale.y, 1e-4);

                if (_Billboard > 0.5)
                {
                    float2 liveCenterLocal = anchor * (1.0 - liveScale);
                    float3 worldPivot = mul(unity_ObjectToWorld, float4(liveCenterLocal, 0.0, 1.0)).xyz;

                    float3 camPos = UniTextGetStereoCenterCamPos();
                    float3 forward = normalize(camPos - worldPivot);
                    float3 upRef = (abs(forward.y) > 0.995) ? float3(0.0, 0.0, 1.0) : float3(0.0, 1.0, 0.0);
                    float3 right = normalize(cross(forward, upRef));
                    float3 up = cross(right, forward);

                    float3 worldPos = worldPivot
                        + right * (v.vertex.x * scale.x)
                        + up * (v.vertex.y * scale.y)
                        - forward * (v.vertex.z * scale.z);

                    o.pos = UnityWorldToClipPos(worldPos);
                }
                else
                {
                    o.pos = UnityObjectToClipPos(vtx);
                }

                o.uv = v.uv;
                return o;
            }

            inline float RoundedBoxSDF(float2 p, float2 halfSize, float radius)
            {
                float2 q = abs(p) - halfSize + radius;
                return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float2 uv = i.uv;
                float aspect = max(i.panelAspect, 0.25);
                float2 aspectScale = float2(aspect, 1.0);
                float2 p = (uv - 0.5) * aspectScale;
                float shadowPad = 0.018;
                float2 halfSize = max(0.5 * aspectScale - shadowPad, float2(0.05, 0.05));
                float r = clamp(_CornerRadius, 0.001, min(halfSize.x, halfSize.y));
                float dist = RoundedBoxSDF(p, halfSize, r);

                float aa = max(fwidth(dist), 0.0018);
                float panelMask = 1.0 - smoothstep(-aa, aa, dist);
                float shadowDist = RoundedBoxSDF(p + float2(0.0, 0.003), halfSize, r);
                float shadowFalloff = saturate(1.0 - max(shadowDist, 0.0) / 0.014);
                float outerShadow = shadowFalloff * shadowFalloff * 0.34 * _BgColor.a;
                if ((panelMask <= 0.001 && outerShadow <= 0.001) || _Opacity <= 0.001)
                {
                    discard;
                }

                // Frosted dark glass surface with subtle vertical depth and top ambient sheen
                float cardU = saturate((p.x / max(halfSize.x, 1e-4)) * 0.5 + 0.5);
                float cardV = saturate((p.y / max(halfSize.y, 1e-4)) * 0.5 + 0.5);
                fixed3 bgCol = _BgColor.rgb * lerp(0.84, 1.20, cardV);
                float topSheen = 0.045 * pow(cardV, 3.0) * (1.0 - 0.40 * abs(cardU - 0.5));
                bgCol = lerp(bgCol, _BorderColor.rgb, topSheen * _BorderColor.a);
                float bgAlpha = _BgColor.a * panelMask;
                float outShadowVis = outerShadow * (1.0 - panelMask);
                float alpha = bgAlpha + outShadowVis;
                fixed3 col = (alpha > 1e-4) ? (bgCol * bgAlpha / alpha) : fixed3(0.0, 0.0, 0.0);

                // Crisp glass rim border with active-only status breathing accent (no corner dot)
                float borderWidth = 0.0085;
                float borderMask = (1.0 - smoothstep(-aa, aa, abs(dist + borderWidth * 0.5) - borderWidth * 0.5)) * panelMask;
                float rimLight = lerp(0.75, 1.35, pow(cardV, 2.0));
                fixed3 borderRgb = _BorderColor.rgb * rimLight;
                float borderAlpha = saturate(_BorderColor.a * lerp(0.85, 1.15, cardV));

                uint status = (uint)clamp(round(i.geo.w), 0.0, 15.0);
                if (status == 1u || status == 2u)
                {
                    fixed3 statusColor = (status == 1u) ? UniTextGetPaletteColor(7u) : UniTextGetPaletteColor(4u);
                    float pulse = 0.55 + 0.45 * sin(_Time.y * ((status == 1u) ? 4.0 : 6.0));
                    float statusWeight = lerp(0.28, 0.78, pow(cardV, 1.5)) * pulse;
                    borderRgb = lerp(borderRgb, statusColor * 1.15, statusWeight);
                    borderAlpha = max(borderAlpha, 0.55 * statusWeight);
                }

                float borderBlend = borderMask * borderAlpha;
                col = lerp(col, borderRgb, borderBlend);
                alpha = max(alpha, borderBlend);

                uint activeRows = (uint)clamp(round(i.geo.x), 4.0, (float)UNITEXT_MAX_ROWS);
                float rowPx = clamp(round(i.geo.y), 192.0, 640.0);

                // Inner text region: balanced physical padding across aspect ratios (4..12 rows, 192..640 px)
                float physPadY = clamp(_Padding * 1.35 * (6.0 / (float)activeRows), 0.032, 0.105);
                float physPadX = clamp(physPadY * 1.35, 0.045, 0.145);
                float padY = clamp(shadowPad + physPadY, 0.04, 0.14);
                float padX = clamp((shadowPad + physPadX) / aspect, 0.018, 0.10);
                float2 pad = float2(padX, padY);
                float2 gridUV = (uv - pad) / max(1.0 - 2.0 * pad, float2(0.001, 0.001));
                float innerWidth = aspect * (1.0 - 2.0 * padX);
                float innerHeight = 1.0 - 2.0 * padY;
                if (_KeepGlyphAspect > 0.5)
                {
                    float innerAspect = innerWidth / max(innerHeight, 0.001);
                    float targetAspect = rowPx / (16.0 * (float)activeRows);
                    float ratio = innerAspect / max(targetAspect, 0.01);
                    if (ratio > 1.35)
                    {
                        float scaleX = ratio / 1.35;
                        gridUV.x = (gridUV.x - 0.5) * scaleX + 0.5;
                        innerWidth /= scaleX;
                    }
                    else if (ratio < 0.70)
                    {
                        float scaleY = 0.70 / ratio;
                        gridUV.y = (gridUV.y - 0.5) * scaleY + 0.5;
                        innerHeight /= scaleY;
                    }
                }
                float rowPadU = min(physPadX * 0.22, 0.020) / max(innerWidth, 0.001);
                if (gridUV.x >= -rowPadU && gridUV.x < 1.0 + rowPadU && gridUV.y >= 0.0 && gridUV.y < 1.0)
                {
                    // Top-down row index (row 0 = top, row activeRows - 1 = bottom)
                    float topV = 1.0 - gridUV.y;
                    float fRow = topV * (float)activeRows;
                    uint cellRow = (uint)clamp(floor(fRow), 0.0, (float)(activeRows - 1u));
                    uint topRow = (uint)clamp(round(i.geo.z), 0.0, (float)(activeRows - 1u));
                    uint physRow = (cellRow + topRow) % activeRows;

                    uint colorIdx = 0u;
                    uint styleFlags = 0u;
                    uint rowMaxPx = 0u;
                    UniTextSampleRowStyle(_BufferTex, physRow, colorIdx, styleFlags, rowMaxPx);
                    fixed3 rowColor = UniTextGetPaletteColor(colorIdx);

                    bool isDim = (styleFlags & 1u) != 0u;
                    bool isHighlight = (styleFlags & 2u) != 0u;

                    if (isHighlight)
                    {
                        float rowH = innerHeight / (float)activeRows;
                        float rowFullW = innerWidth * (1.0 + 2.0 * rowPadU);
                        float rowLocalX = (gridUV.x - 0.5) * innerWidth;
                        float rowLocalY = (frac(fRow) - 0.5) * rowH;

                        float2 rowHalf = float2(rowFullW * 0.5, rowH * 0.44);
                        float rowR = min(rowH * 0.24, 0.024);
                        float rowDist = RoundedBoxSDF(float2(rowLocalX, rowLocalY), rowHalf, rowR);
                        float rowMask = (1.0 - smoothstep(-aa, aa, rowDist)) * panelMask;
                        float pillFade = lerp(1.0, 0.40, saturate(gridUV.x));
                        col = lerp(col, rowColor * 0.26, rowMask * 0.34 * pillFade);
                    }

                    if (gridUV.x >= 0.0 && gridUV.x < 1.0)
                    {
                        uint penX = (uint)clamp(floor(gridUV.x * rowPx), 0.0, rowPx - 1.0);
                        if (penX < rowMaxPx)
                        {
                            // Binary search the CRT layout row for the last cell whose xStart <= pixel X,
                            // tracking (xStart, advance) inline to avoid a redundant post-loop lookup.
                            uint lo = 0u;
                            uint hi = UNITEXT_COLS - 1u;
                            uint xStart = 0u;
                            uint advance = 0u;
                            [unroll]
                            for (uint iter = 0u; iter < 6u; iter++)
                            {
                                uint mid = (lo + hi + 1u) >> 1;
                                uint midX = 0u;
                                uint midAdv = 0u;
                                UniTextSampleLayout(_BufferTex, mid, physRow, midX, midAdv);
                                if (midX <= penX)
                                {
                                    lo = mid;
                                    xStart = midX;
                                    advance = midAdv;
                                }
                                else
                                {
                                    hi = mid - 1u;
                                    if (mid == 1u)
                                    {
                                        advance = midX;
                                    }
                                }
                            }

                            uint cp = (penX >= xStart && penX < xStart + advance) ? UniTextSampleCell(_BufferTex, lo, physRow) : 0u;
                            if (cp != 0u)
                            {
                                uint gx = penX - xStart;
                                uint gy = (uint)clamp(floor(frac(fRow) * 16.0), 0.0, 15.0);

                                UniTextGlyphRef glyphRef = UniTextGetGlyphRef(_AuxTex, cp);
                                float atlasHeight = max(_FontAtlas_TexelSize.w, 1.0);
                                float glyph = 0.0;
                                float shadow = 0.0;
                                UniTextSampleGlyphAndShadow(
                                    _FontAtlas,
                                    atlasHeight,
                                    glyphRef,
                                    gx,
                                    gy,
                                    _ShadowStrength > 0.001,
                                    glyph,
                                    shadow
                                );

                                // Subtle 1-pixel shadow offset (right-down) for readability against bright scenes
                                if (shadow > 0.5 && glyph < 0.5)
                                {
                                    float shadowAmt = _ShadowStrength * (isDim ? 0.50 : 0.80);
                                    col = lerp(col, fixed3(0.0, 0.0, 0.0), shadowAmt);
                                    alpha = max(alpha, (isDim ? 0.55 : 0.92) * _ShadowStrength);
                                }

                                if (glyph > 0.5)
                                {
                                    float brightness = isDim ? 0.62 : (isHighlight ? 1.12 : 1.0);
                                    float textAlpha = isDim ? 0.80 : 1.0;
                                    col = lerp(col, rowColor * brightness, textAlpha);
                                    alpha = max(alpha, textAlpha);
                                }
                            }
                        }
                    }
                }

                return fixed4(col, saturate(alpha * _Opacity));
            }
            ENDCG
        }
    }
    Fallback "Unlit/Transparent"
}
