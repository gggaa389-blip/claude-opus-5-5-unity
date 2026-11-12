// Procedural skybox: zenith / horizon gradient, sun disk with glow, and drifting fbm clouds projected
// on a flat cloud layer. Sun direction comes from the scene's main directional light.
Shader "Island/SkyClouds"
{
    Properties
    {
        _ZenithColor ("Zenith", Color) = (0.24, 0.47, 0.86, 1)
        _HorizonColor ("Horizon", Color) = (0.70, 0.84, 0.95, 1)
        _GroundColor ("Below horizon", Color) = (0.42, 0.55, 0.66, 1)
        _SunColor ("Sun", Color) = (1, 0.95, 0.82, 1)
        _SunSize ("Sun size", Range(0.0005, 0.02)) = 0.0025
        _CloudColor ("Cloud lit colour", Color) = (1, 1, 1, 1)
        _CloudShade ("Cloud shade colour", Color) = (0.68, 0.74, 0.84, 1)
        _CloudCover ("Cloud cover", Range(0, 1)) = 0.48
        _CloudScale ("Cloud scale", Float) = 1.6
        _CloudSpeed ("Cloud drift", Float) = 0.004
        _Exposure ("Exposure", Float) = 1.0
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            half4 _ZenithColor, _HorizonColor, _GroundColor, _SunColor, _CloudColor, _CloudShade;
            half _SunSize, _CloudCover, _CloudScale, _CloudSpeed, _Exposure;
            float _IslandTime;

            struct v2f { float4 pos : SV_POSITION; float3 dir : TEXCOORD0; };

            v2f vert (appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.dir = v.vertex.xyz;
                return o;
            }

            float hash (float2 p) { p = frac(p * float2(123.34, 456.21)); p += dot(p, p + 45.32); return frac(p.x * p.y); }
            float vnoise (float2 p)
            {
                float2 i = floor(p), f = frac(p);
                f = f * f * (3 - 2 * f);
                return lerp(lerp(hash(i), hash(i + float2(1, 0)), f.x), lerp(hash(i + float2(0, 1)), hash(i + float2(1, 1)), f.x), f.y);
            }
            float fbm (float2 p)
            {
                float s = 0, a = 0.5;
                for (int k = 0; k < 6; k++) { s += a * vnoise(p); p = p * 2.03 + float2(17.1, -9.3); a *= 0.5; }
                return s;
            }

            half4 frag (v2f i) : SV_Target
            {
                float3 d = normalize(i.dir);
                float3 sunDir = normalize(_WorldSpaceLightPos0.xyz);
                float h = d.y;
                half3 col = lerp(_HorizonColor.rgb, _ZenithColor.rgb, pow(saturate(h), 0.45));
                col = lerp(col, _GroundColor.rgb, saturate(-h * 6));

                float sd = dot(d, sunDir);
                half3 glow = _SunColor.rgb * (pow(saturate(sd), 64) * 0.55 + pow(saturate(sd), 8) * 0.18);
                half sunDisk = smoothstep(1 - _SunSize, 1 - _SunSize * 0.6, sd);

                // clouds on a flat layer: uv = horizontal position where the view ray meets the layer
                half cloudA = 0;
                half3 cloudCol = 0;
                if (h > 0.0)
                {
                    float2 uv = d.xz / (h + 0.12) * _CloudScale + _IslandTime * _CloudSpeed * float2(1, 0.35);
                    float n = fbm(uv);
                    float n2 = fbm(uv * 2.7 + 3.1);
                    float cover = 1 - _CloudCover;
                    cloudA = smoothstep(cover, cover + 0.22, n * 0.8 + n2 * 0.3);
                    cloudA *= smoothstep(0.0, 0.18, h);                     // thin out toward the horizon
                    float lit = saturate(0.55 + 0.45 * dot(normalize(float3(d.x, 0.4, d.z)), sunDir));
                    float thick = saturate((n * 0.8 + n2 * 0.3 - cover) * 2.5);
                    cloudCol = lerp(_CloudColor.rgb, _CloudShade.rgb, thick * (1 - lit * 0.6));
                    cloudCol += _SunColor.rgb * pow(saturate(sd), 12) * 0.35;  // silver lining toward the sun
                }

                col += glow;
                col = lerp(col, cloudCol, cloudA * 0.92);
                col += _SunColor.rgb * sunDisk * (1 - cloudA) * 4;
                return half4(col * _Exposure, 1);
            }
            ENDCG
        }
    }
    FallBack Off
}
