// The battle royale storm wall: purple, translucent, with slow swirling bands, denser where you look
// along it, fading out towards the top.
Shader "Island/StormWall"
{
    Properties
    {
        _Color ("Color", Color) = (0.48, 0.16, 0.78, 0.42)
        _Glow ("Band colour", Color) = (0.95, 0.55, 1.0, 1)
    }
    SubShader
    {
        Tags { "Queue"="Transparent+20" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Cull Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color, _Glow;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 wpos : TEXCOORD0;
                float2 uv : TEXCOORD1;
                float3 n : TEXCOORD2;
            };

            v2f vert (appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.uv = v.texcoord.xy;
                o.n = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float t = _Time.y;
                float b1 = sin(i.uv.x * 180.0 + i.wpos.y * 0.09 + t * 1.3) * 0.5 + 0.5;
                float b2 = sin(i.uv.x * 61.0 - i.wpos.y * 0.05 - t * 0.6) * 0.5 + 0.5;
                float b3 = sin(i.wpos.y * 0.21 + t * 2.1 + i.uv.x * 23.0) * 0.5 + 0.5;
                float band = b1 * 0.45 + b2 * 0.35 + b3 * 0.2;
                float h = saturate((i.wpos.y + 10.0) / 300.0);
                float3 view = normalize(_WorldSpaceCameraPos - i.wpos);
                float graze = 1.0 - abs(dot(normalize(i.n), view));
                float a = _Color.a * (0.55 + 0.45 * band) * (1.0 - h * 0.7) + graze * graze * 0.35;
                float3 col = lerp(_Color.rgb, _Glow.rgb, band * 0.45 + graze * 0.25);
                return fixed4(col, saturate(a));
            }
            ENDCG
        }
    }
}
