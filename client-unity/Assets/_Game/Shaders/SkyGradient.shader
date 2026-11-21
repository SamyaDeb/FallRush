// Fall Rush sky (G2.1, doc 17 §3.1): a vertical gradient skybox #3FA9F5 (zenith) → #BFE6FF (horizon), a soft warm sun
// glow toward the directional light, and a slightly deeper tone below the horizon (we sit above the clouds).
Shader "FallRush/SkyGradient"
{
    Properties
    {
        _Top("Zenith", Color) = (0.247, 0.663, 0.961, 1)
        _Horizon("Horizon", Color) = (0.749, 0.902, 1.0, 1)
        _Bottom("Below horizon", Color) = (0.62, 0.80, 0.97, 1)
        _SunColor("Sun glow", Color) = (1.0, 0.96, 0.82, 1)
        _SunSize("Sun glow size", Range(1, 64)) = 10
        _SunStrength("Sun glow strength", Range(0, 1)) = 0.35
        _Exponent("Gradient curve", Range(0.2, 4)) = 0.7
    }
    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" "RenderPipeline" = "UniversalPipeline" }
        Cull Off ZWrite Off
        Pass
        {
            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            CBUFFER_START(UnityPerMaterial)
                half4 _Top, _Horizon, _Bottom, _SunColor;
                half _SunSize, _SunStrength, _Exponent;
            CBUFFER_END
            // G4.8 globals (LookState): the look's sky; _FR_SkyHaze = a pale haze band on the horizon line (soft look only:
            // the candy look sets it to its horizon, so the band vanishes).
            half _FR_SkyOn;
            half4 _FR_SkyTop, _FR_SkyHorizon, _FR_SkyHaze;
            struct A { float4 positionOS : POSITION; };
            struct V { float4 positionCS : SV_POSITION; float3 dir : TEXCOORD0; };
            V vert(A v)
            {
                V o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.dir = v.positionOS.xyz;
                return o;
            }
            half4 frag(V i) : SV_Target
            {
                float3 d = normalize(i.dir);
                half up = saturate(d.y);
                half down = saturate(-d.y);
                half on = step(0.5h, _FR_SkyOn);
                half3 top = lerp(_Top.rgb, _FR_SkyTop.rgb, on);
                half3 hor = lerp(_Horizon.rgb, _FR_SkyHorizon.rgb, on);
                half3 bottom = lerp(_Bottom.rgb, hor * half3(0.86h, 0.92h, 0.98h), on);
                half3 c = lerp(hor, top, pow(up, _Exponent));
                c = lerp(c, bottom, saturate(down * 3.0h));
                // Haze: strongest on the horizon line, fading over ~10 degrees up and down.
                half haze = exp(-abs(d.y) * 9.0h) * on;
                c = lerp(c, _FR_SkyHaze.rgb, haze * 0.85h);
                half sun = pow(saturate(dot(d, GetMainLight().direction)), _SunSize);
                c += _SunColor.rgb * sun * _SunStrength;
                return half4(c, 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
