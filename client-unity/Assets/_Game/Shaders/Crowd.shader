// G4.10 backdrop crowd and waving flags (soft look, far stadium). One merged mesh, vertex colours, animated entirely in
// the vertex shader (0 CPU per frame): uv1 = (phase 0..1, weight 0 at the base → 1 at the top), uv2.x = kind
// (0 = a capsule spectator: hops and sways; 1 = a banner / flag: waves along its normal). A cheap lit term from the main
// light half-Lambert, partly unlit like the backdrop, and the scene fog (the horizon haze). Opaque, no shadows.
Shader "FallRush/Crowd"
{
    Properties
    {
        _Unlit("Unlit amount", Range(0, 1)) = 0.45
        _Hop("Hop height (m)", Float) = 0.9
        _Sway("Sway (m at the top)", Float) = 0.35
        _Rate("Hop rate (rad/s)", Float) = 5.5
        _Wave("Flag wave (m at the tip)", Float) = 0.9
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        Pass
        {
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            CBUFFER_START(UnityPerMaterial)
                half _Unlit;
                float _Hop, _Sway, _Rate, _Wave;
            CBUFFER_END
            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; half4 color : COLOR; float2 uv1 : TEXCOORD1; float2 uv2 : TEXCOORD2; };
            struct V { float4 positionCS : SV_POSITION; half4 color : COLOR; half3 normalWS : TEXCOORD0; half fog : TEXCOORD1; };
            V vert(A v)
            {
                V o;
                float3 p = TransformObjectToWorld(v.positionOS.xyz);
                float3 n = TransformObjectToWorldNormal(v.normalOS);
                float ph = v.uv1.x * 6.2831853;
                float w = v.uv1.y;
                float t = _Time.y;
                if (v.uv2.x < 0.5)
                {
                    // Spectator: a bouncy hop (each its own phase and a slightly different rate) and a sway from the base.
                    float rate = _Rate * (0.8 + 0.4 * frac(v.uv1.x * 7.31));
                    float hop = sin(t * rate + ph);
                    p.y += _Hop * hop * hop * step(0.0, hop);
                    float s = sin(t * rate * 0.5 + ph * 1.7);
                    p.xz += float2(cos(ph * 3.1), sin(ph * 3.1)) * (_Sway * w * s);
                }
                else
                {
                    // Banner / flag: a travelling wave along its normal, stronger toward the free end (weight).
                    p += n * (_Wave * w * sin(t * 3.2 + ph + (p.x + p.z) * 0.15));
                }

                o.positionCS = TransformWorldToHClip(p);
                o.normalWS = n;
                o.color = v.color;
                o.fog = ComputeFogFactor(o.positionCS.z);
                return o;
            }
            half4 frag(V i, bool front : SV_IsFrontFace) : SV_Target
            {
                Light sun = GetMainLight();
                half3 n = normalize(i.normalWS) * (front ? 1.0h : -1.0h);
                half ndl = dot(n, sun.direction) * 0.5h + 0.5h;
                half3 lit = i.color.rgb * lerp(0.62h + 0.38h * ndl, 1.0h, _Unlit);
                return half4(MixFog(lit, i.fog), 1.0h);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
