// Fall Rush blob shadow (G2.1): a soft round contact shadow under each Rushling, drawn instanced on a flat quad
// projected to the ground below the capsule (a gameplay aid for judging jumps). The instance's object Y scale carries
// the opacity (the quad is flat, so Y is free): darker when close to the ground.
Shader "FallRush/BlobShadow"
{
    Properties
    {
        _Color("Shadow colour", Color) = (0.17, 0.08, 0.35, 0.55)
    }
    SubShader
    {
        Tags { "Queue" = "Transparent-10" "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Offset -1, -1
        Pass
        {
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
            CBUFFER_END
            struct A { float4 positionOS : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; half alpha : TEXCOORD1; UNITY_VERTEX_OUTPUT_STEREO };
            V vert(A v)
            {
                V o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float4x4 m = GetObjectToWorldMatrix();
                o.alpha = saturate(length(float3(m._m01, m._m11, m._m21)));
                float3 p = v.positionOS.xyz;
                p.y = 0;
                float3 ws = mul(m, float4(p.x, 0, p.z, 1)).xyz;
                o.positionCS = TransformWorldToHClip(ws);
                o.uv = v.uv * 2.0 - 1.0;
                return o;
            }
            half4 frag(V i) : SV_Target
            {
                half r = length(i.uv);
                half a = (1.0h - smoothstep(0.55h, 1.0h, r)) * _Color.a * i.alpha;
                return half4(_Color.rgb, a);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
