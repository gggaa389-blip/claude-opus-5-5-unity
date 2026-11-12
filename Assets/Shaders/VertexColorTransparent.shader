// Alpha-blended variant (glass, potion liquid): premultiplied so glass keeps its highlights.
Shader "Island/VertexColorTransparent"
{
    Properties
    {
        _Color ("Base Color", Color) = (1,1,1,0.5)
        _Metallic ("Metallic", Range(0,1)) = 0
        _Glossiness ("Smoothness", Range(0,1)) = 0.8
        [HDR] _EmissionColor ("Emission", Color) = (0,0,0,1)
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "IgnoreProjector"="True" }
        LOD 200
        Cull [_Cull]
        ZWrite Off

        CGPROGRAM
        #pragma surface surf Standard alpha:premul
        #pragma multi_compile_instancing
        #pragma target 3.5

        struct Input { float4 color : COLOR; };
        fixed4 _Color;
        half _Metallic;
        half _Glossiness;
        half4 _EmissionColor;

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 c = _Color * IN.color;
            o.Albedo = c.rgb;
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
            o.Emission = _EmissionColor.rgb;
            o.Alpha = c.a;
        }
        ENDCG
    }
    FallBack "Transparent/Diffuse"
}
