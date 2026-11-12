// Build preview: a see-through blue (or red) copy of the piece with brighter edges.
Shader "Island/Ghost"
{
    Properties
    {
        _Color ("Color", Color) = (0.35, 0.7, 1, 0.38)
    }
    SubShader
    {
        Tags { "Queue"="Transparent+5" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Cull Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 n : TEXCOORD0;
                float3 v : TEXCOORD1;
            };

            v2f vert (appdata_base i)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(i.vertex);
                o.n = UnityObjectToWorldNormal(i.normal);
                o.v = _WorldSpaceCameraPos - mul(unity_ObjectToWorld, i.vertex).xyz;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float rim = 1.0 - abs(dot(normalize(i.n), normalize(i.v)));
                float a = saturate(_Color.a + rim * rim * 0.45);
                return fixed4(_Color.rgb * (1.0 + rim * 0.6), a);
            }
            ENDCG
        }
    }
}
