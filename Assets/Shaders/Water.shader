// Ocean / lake / river surface. Colour and opacity come from vertex colours (depth tint baked by the
// level builder); two scrolling procedural normal maps give the ripples. _IslandTime is a global set
// by WaterClock (play mode) or by the capture script (offline frames).
Shader "Island/Water"
{
    Properties
    {
        _Tint ("Tint", Color) = (1,1,1,1)
        _NormalTex ("Ripple normal map", 2D) = "bump" {}
        _NormalStrength ("Ripple strength", Range(0,2)) = 0.55
        _Tiling ("Ripple size (m)", Float) = 16
        _Speed ("Scroll speeds", Vector) = (0.020, 0.012, -0.016, 0.021)
        _Glossiness ("Smoothness", Range(0,1)) = 0.9
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent-50" "IgnoreProjector"="True" }
        LOD 200
        ZWrite Off

        CGPROGRAM
        #pragma surface surf Standard alpha:premul
        #pragma target 3.5

        sampler2D _NormalTex;
        half4 _Tint;
        half _NormalStrength;
        float _Tiling;
        float4 _Speed;
        half _Glossiness;
        float _IslandTime;

        struct Input
        {
            float4 color : COLOR;
            float3 worldPos;
        };

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            float2 uv = IN.worldPos.xz / _Tiling;
            float t = _IslandTime;
            half3 n1 = UnpackNormal(tex2D(_NormalTex, uv + t * _Speed.xy));
            half3 n2 = UnpackNormal(tex2D(_NormalTex, uv * 1.93 + float2(0.37, 0.71) + t * _Speed.zw));
            o.Normal = normalize(half3((n1.xy + n2.xy) * _NormalStrength, 1));
            o.Albedo = IN.color.rgb * _Tint.rgb;
            o.Alpha = saturate(IN.color.a * _Tint.a);
            o.Smoothness = _Glossiness;
            o.Metallic = 0;
        }
        ENDCG
    }
    FallBack Off
}
