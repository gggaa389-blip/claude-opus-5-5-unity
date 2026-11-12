// Opaque PBR shader for the test 1 assets: glTF baseColorFactor x COLOR_0 vertex colours.
// Optional wind: _Wind is the sway amplitude in metres; _WindHeight 1 scales it by the local vertex
// height (instanced grass tufts), 0 sways the whole surface (leaf canopies, which are static-batched
// into world space); _WindAlpha 1 additionally weights it by the vertex alpha (crop rows: 0 at the
// root, 1 at the tip). Time comes from the global _IslandTime.
Shader "Island/VertexColorLit"
{
    Properties
    {
        _Color ("Base Color", Color) = (1,1,1,1)
        _Metallic ("Metallic", Range(0,1)) = 0
        _Glossiness ("Smoothness", Range(0,1)) = 0.3
        [HDR] _EmissionColor ("Emission", Color) = (0,0,0,1)
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
        _Wind ("Wind sway (m)", Float) = 0
        _WindHeight ("Wind scales with local height", Range(0,1)) = 0
        _WindAlpha ("Wind scales with vertex alpha", Range(0,1)) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 200
        Cull [_Cull]

        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows addshadow vertex:vert
        #pragma multi_compile_instancing
        #pragma target 3.5

        struct Input { float4 color : COLOR; };
        fixed4 _Color;
        half _Metallic;
        half _Glossiness;
        half4 _EmissionColor;
        float _Wind;
        float _WindHeight;
        float _WindAlpha;
        float _IslandTime;

        void vert (inout appdata_full v)
        {
            if (_Wind > 0)
            {
                float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                float phase = _IslandTime * 1.6 + wp.x * 0.21 + wp.z * 0.17;
                float gust = 0.6 + 0.4 * sin(_IslandTime * 0.37 + wp.x * 0.013 + wp.z * 0.011);
                float2 sway = float2(sin(phase), sin(phase * 0.83 + 1.7) * 0.6) * _Wind * gust;
                float w = lerp(1.0, saturate(v.vertex.y * 1.8), _WindHeight) * lerp(1.0, v.color.a, _WindAlpha);
                float3 worldOffset = float3(sway.x, 0, sway.y) * w;
                v.vertex.xyz += mul((float3x3)unity_WorldToObject, worldOffset);
            }
        }

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            fixed3 c = _Color.rgb * IN.color.rgb;
            o.Albedo = c;
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
            o.Emission = _EmissionColor.rgb;
            o.Alpha = 1;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
