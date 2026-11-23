Shader "UniText/ParamEncoder"
{
    Properties
    {
        _Ctrl ("Ctrl", Float) = 0
        _D0   ("D0",   Float) = 0
        _D1   ("D1",   Float) = 0
        _D2   ("D2",   Float) = 0
        _D3   ("D3",   Float) = 0
        _D4   ("D4",   Float) = 0
        _D4b4 ("D4 Bit 4 (Compact36)", Float) = 0
        _D4b5 ("D4 Bit 5 (Compact36)", Float) = 0
        _D4b6 ("D4 Bit 6 (Compact36)", Float) = 0
        _D4b7 ("D4 Bit 7 (Compact36)", Float) = 0
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
        Tags { "Queue"="Overlay+500" "RenderType"="Opaque" "IgnoreProjector"="True" "DisableBatching"="True" }
        Cull Off
        ZTest Always
        ZWrite Off
        Blend Off
        ColorMask RGBA
        Lighting Off

        Pass
        {
            CGPROGRAM
            #pragma target 4.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UniTextCommon.cginc"

            float _Ctrl;
            float _D0, _D1, _D2, _D3, _D4, _D5, _D6, _D7;
            float _D8, _D9, _D10, _D11, _D12, _D13, _D14, _D15;
            float _D4b4, _D4b5, _D4b6, _D4b7;
            float _LocalClear;
            float _Bank;
            float _RowPage;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                // Only render into the dedicated 8x1 ParamBridge RenderTexture camera.
                // Clip cleanly outside frustum for player, VR HMD, and mirror cameras.
                if (abs(_ScreenParams.x - 8.0) > 0.5 || abs(_ScreenParams.y - 1.0) > 0.5)
                {
                    o.pos = float4(0.0, 0.0, -2.0, 1.0);
                    o.uv = float2(0.0, 0.0);
                    return o;
                }
                o.pos = float4(v.uv.x * 2.0 - 1.0, v.uv.y * 2.0 - 1.0, 0.5, 1.0);
                o.uv = v.uv;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                uint px = (uint)clamp(floor(i.uv.x * 8.0), 0.0, 7.0);

                if (px == 0u)
                {
                    return float4(
                        UniTextToByte(_Ctrl),
                        UniTextToByte(_D0),
                        UniTextToByte(_D1),
                        UniTextToByte(_D2)
                    ) / 255.0;
                }
                if (px == 1u)
                {
                    uint d4 = UniTextToByte(_D4);
                    if (_D4b4 > 0.5) d4 |= 16u;
                    if (_D4b5 > 0.5) d4 |= 32u;
                    if (_D4b6 > 0.5) d4 |= 64u;
                    if (_D4b7 > 0.5) d4 |= 128u;
                    return float4(
                        UniTextToByte(_D3),
                        (float)d4,
                        UniTextToByte(_D5),
                        UniTextToByte(_D6)
                    ) / 255.0;
                }
                if (px == 2u)
                {
                    return float4(
                        UniTextToByte(_D7),
                        UniTextToByte(_D8),
                        UniTextToByte(_D9),
                        UniTextToByte(_D10)
                    ) / 255.0;
                }
                if (px == 3u)
                {
                    return float4(
                        UniTextToByte(_D11),
                        UniTextToByte(_D12),
                        UniTextToByte(_D13),
                        UniTextToByte(_D14)
                    ) / 255.0;
                }
                if (px == 4u)
                {
                    float clearByte = (_LocalClear > 0.5) ? 255.0 : 0.0;
                    // UniText_Bank selects the 32-column half of every 64-cell row
                    float bankByte = (_Bank > 0.5) ? 1.0 : 0.0;
                    // UniText_RowPage selects rows 0..5 (0) or rows 6..11 (1)
                    float rowPageByte = (_RowPage > 0.5) ? 1.0 : 0.0;
                    return float4(
                        UniTextToByte(_D15),
                        clearByte,
                        bankByte,
                        rowPageByte
                    ) / 255.0;
                }

                return float4(0.0, 0.0, 0.0, 0.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
