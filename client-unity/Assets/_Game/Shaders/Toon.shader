// Fall Rush toon shader (G2.1, doc 17 §3.2). Hand-written URP Forward HLSL (not Shader Graph: the project is built
// headless in batch mode, a text shader is reviewable and has exactly the variants we need, and it carries the
// inverted-hull outline as a second pass, which Shader Graph cannot do in URP without a renderer feature).
//
//  * 3-band cel ramp on half-Lambert from the one directional "sun" (no realtime shadows), shade colour tint
//  * rim light, palette tint (_BaseColor x vertex colour), fake AO from vertex colour (code-built meshes bake it)
//  * gradient ambient (globals _FR_AmbSky / _FR_AmbGround set by the client; falls back to the SH ambient probe)
//  * flash / glow mask (vertex alpha) for telegraphs: lerp to _FlashColor.rgb by _FlashColor.a (per renderer, MPB)
//  * travelling chevron light for conveyor belts (uv2.x = chevron index, _Scroll = wave position, MPB)
//  * Rushling extras (uv2.y flags): 1 = LED screen face (atlas _FaceMap, offset _FaceST, emissive, unlit),
//    2 = wind-up key vertices (spun about the key shaft by _KeyAngle in object space), 3 = no outline hull
//  * _Unlit blends lighting out (clouds, effects, the silhouette test); _Emission adds glow; alpha from _BaseColor.a
//    when the material is set up as transparent (_SrcBlend/_DstBlend/_ZWrite, queue 3000)
//  * Outline pass (LightMode SRPDefaultUnlit): only enabled on Rushling materials (Material.SetShaderPassEnabled)
//  * SRP Batcher compatible (one UnityPerMaterial CBUFFER) and GPU-instancing compatible.
Shader "FallRush/Toon"
{
    Properties
    {
        _BaseColor("Base colour", Color) = (1,1,1,1)
        _BaseMap("Base map", 2D) = "white" {}
        _ShadeColor("Shade tint", Color) = (0.74, 0.72, 0.92, 1)
        _MidLevel("Mid band level", Range(0,1)) = 0.6
        _BandA("Shade/mid threshold", Range(0,1)) = 0.42
        _BandB("Mid/lit threshold", Range(0,1)) = 0.72
        _BandSoft("Band softness", Range(0.001,0.2)) = 0.03
        _RimColor("Rim colour", Color) = (1,1,1,1)
        _RimStrength("Rim strength", Range(0,1)) = 0.35
        _RimPower("Rim power", Range(0.5,8)) = 3
        _Gloss("Gloss highlight", Range(0,1)) = 0
        _Emission("Emission", Color) = (0,0,0,0)
        _Unlit("Unlit amount", Range(0,1)) = 0
        _Desat("Desaturate", Range(0,1)) = 0
        _FlashColor("Flash colour (a = amount)", Color) = (1,1,1,0)
        _Scroll("Chevron wave", Float) = -1000
        _ChevronCount("Chevron count", Float) = 1
        _ChevronLit("Chevron lit colour", Color) = (0.36, 0.89, 0.71, 1)
        _Stripes("Hazard stripes (0 off, 1 world, 2 object)", Float) = 0
        _StripeColor("Stripe colour", Color) = (1, 0.243, 0.647, 1)
        _StripeWidth("Stripe width (m)", Float) = 0.35
        _FaceMap("Face atlas", 2D) = "black" {}
        _FaceST("Face atlas ST", Vector) = (0.25, 0.25, 0, 0)
        _FaceOn("Face on", Float) = 1
        _FaceTint("Face glyph colour", Color) = (0.36, 0.89, 0.71, 1)
        _KeyAngle("Key angle (rad)", Float) = 0
        _KeyPivot("Key pivot (object)", Vector) = (0, 0.5, -0.32, 0)
        _LidAngle("Lid hinge angle (rad)", Float) = 0
        _RushlingMesh("Rushling mesh (uv2.x = lid)", Float) = 0
        _LidPivot("Lid hinge (object)", Vector) = (0, 0.54, -0.31, 0)
        _OutlineColor("Outline colour", Color) = (0.165, 0.07, 0.31, 1)
        _OutlineWidth("Outline width (m)", Float) = 0.016
        [HideInInspector] _SrcBlend("_SrcBlend", Float) = 1
        [HideInInspector] _DstBlend("_DstBlend", Float) = 0
        [HideInInspector] _ZWrite("_ZWrite", Float) = 1
        [HideInInspector] _Cull("_Cull", Float) = 2
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half4 _ShadeColor;
            half _MidLevel, _BandA, _BandB, _BandSoft;
            half4 _RimColor;
            half _RimStrength, _RimPower, _Gloss;
            half4 _Emission;
            half _Unlit;
            half _Desat;
            half4 _FlashColor;
            float _Scroll, _ChevronCount;
            half4 _ChevronLit;
            half _Stripes;
            half4 _StripeColor;
            float _StripeWidth;
            float4 _FaceST;
            half _FaceOn;
            half4 _FaceTint;
            float _KeyAngle;
            float4 _KeyPivot;
            float _LidAngle;
            half _RushlingMesh;
            float4 _LidPivot;
            half4 _OutlineColor;
            float _OutlineWidth;
        CBUFFER_END

        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        TEXTURE2D(_FaceMap); SAMPLER(sampler_FaceMap);
        half4 _FR_AmbSky;     // global, set by the client (a > 0 = valid)
        half4 _FR_AmbGround;

        // Wind-up key: rotate flagged vertices about the shaft (object −Z axis through _KeyPivot).
        float3 SpinKey(float3 p, float flag)
        {
            if (abs(flag - 2.0) < 0.5)
            {
                float s = sin(_KeyAngle), c = cos(_KeyAngle);
                float3 q = p - _KeyPivot.xyz;
                q.xy = float2(q.x * c - q.y * s, q.x * s + q.y * c);
                p = q + _KeyPivot.xyz;
            }
            return p;
        }

        // Lid: vertices with uv2.x = 1 hinge about the X axis through _LidPivot (the stun pop opens the lid).
        float3 Lid(float3 p, float flag)
        {
            if (flag > 0.5 && flag < 1.5 && _LidAngle != 0.0)
            {
                float s = sin(-_LidAngle), c = cos(-_LidAngle);
                float3 q = p - _LidPivot.xyz;
                q.yz = float2(q.y * c - q.z * s, q.y * s + q.z * c);
                p = q + _LidPivot.xyz;
            }
            return p;
        }

        float3 LidN(float3 n, float flag)
        {
            if (flag > 0.5 && flag < 1.5 && _LidAngle != 0.0)
            {
                float s = sin(-_LidAngle), c = cos(-_LidAngle);
                n.yz = float2(n.y * c - n.z * s, n.y * s + n.z * c);
            }
            return n;
        }

        float3 SpinKeyN(float3 n, float flag)
        {
            if (abs(flag - 2.0) < 0.5)
            {
                float s = sin(_KeyAngle), c = cos(_KeyAngle);
                n.xy = float2(n.x * c - n.y * s, n.x * s + n.y * c);
            }
            return n;
        }
        ENDHLSL

        Pass
        {
            Name "ToonForward"
            Tags { "LightMode" = "UniversalForward" }
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                float2 uv2 : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                half4 color : COLOR;
                float2 uv2 : TEXCOORD3;
                half fog : TEXCOORD4;
                float3 positionOS : TEXCOORD5;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 p = Lid(SpinKey(v.positionOS.xyz, v.uv2.y), v.uv2.x * _RushlingMesh);
                float3 n = LidN(SpinKeyN(v.normalOS, v.uv2.y), v.uv2.x * _RushlingMesh);
                VertexPositionInputs pos = GetVertexPositionInputs(p);
                o.positionCS = pos.positionCS;
                o.positionWS = pos.positionWS;
                o.normalWS = TransformObjectToWorldNormal(n);
                o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
                o.uv2 = v.uv2;
                o.positionOS = p;
                o.color = v.color;
                o.fog = ComputeFogFactor(pos.positionCS.z);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv);
                half3 albedo = tex.rgb * _BaseColor.rgb * i.color.rgb;
                albedo = lerp(albedo, dot(albedo, half3(0.3h, 0.59h, 0.11h)).xxx, _Desat);

                // Travelling chevron light (conveyors): the chevron whose index matches the wave is lit.
                if (_Scroll > -999.0 && i.uv2.x > 0.5 && _RushlingMesh < 0.5)
                {
                    float d = frac((_Scroll - (i.uv2.x - 1.0)) / max(1.0, _ChevronCount));
                    half on = step(d * max(1.0, _ChevronCount), 1.0);
                    albedo = lerp(albedo, _ChevronLit.rgb, on);
                }

                // Hazard stripes (doc 17 §3.1: everything that hurts is striped, so colour is never the only signal).
                if (_Stripes > 0.5)
                {
                    float3 sp = _Stripes > 1.5 ? i.positionOS : i.positionWS;
                    half band = step(0.5, frac((sp.x + sp.y + sp.z) / max(0.05, _StripeWidth * 2.0)));
                    albedo = lerp(albedo, _StripeColor.rgb * i.color.rgb, band);
                }

                // Telegraph flash on the masked part (vertex alpha = 1).
                albedo = lerp(albedo, _FlashColor.rgb, saturate(_FlashColor.a * i.color.a));

                float3 n = normalize(i.normalWS);
                Light sun = GetMainLight();
                half ndl = dot(n, sun.direction) * 0.5h + 0.5h;               // half-Lambert
                half bandA = smoothstep(_BandA - _BandSoft, _BandA + _BandSoft, ndl);
                half bandB = smoothstep(_BandB - _BandSoft, _BandB + _BandSoft, ndl);
                half ramp = lerp(0.0h, _MidLevel, bandA) + (1.0h - _MidLevel) * bandB; // 0 / mid / 1
                half3 shade = lerp(_ShadeColor.rgb, half3(1, 1, 1), ramp);

                half3 amb;
                if (_FR_AmbSky.a > 0.0h)
                    amb = lerp(_FR_AmbGround.rgb, _FR_AmbSky.rgb, n.y * 0.5h + 0.5h);
                else
                    amb = SampleSH(n);

                half3 lit = albedo * (shade * sun.color * 0.58h + amb * 0.34h);

                float3 viewDir = normalize(GetWorldSpaceViewDir(i.positionWS));
                half rim = pow(1.0h - saturate(dot(n, viewDir)), _RimPower) * _RimStrength;
                rim *= smoothstep(0.2h, 0.6h, ndl);                            // rim on the lit side only
                lit += _RimColor.rgb * rim;
                if (_Gloss > 0.0h)
                {
                    float3 h = normalize(sun.direction + viewDir);
                    half spec = smoothstep(0.92h, 0.96h, dot(n, h));
                    lit += spec * _Gloss * sun.color;
                }

                half3 col = lerp(lit, albedo, _Unlit) + _Emission.rgb;

                // LED screen face: emissive glyph atlas (unlit), flagged by uv2.y = 1.
                if (abs(i.uv2.y - 1.0) < 0.5)
                {
                    half4 face = SAMPLE_TEXTURE2D(_FaceMap, sampler_FaceMap, i.uv * _FaceST.xy + _FaceST.zw);
                    half3 screen = half3(0.07, 0.04, 0.16);
                    col = lerp(screen, face.rgb * _FaceTint.rgb * 1.3h, face.a) * _FaceOn + albedo * (1.0h - _FaceOn);
                }

                col = MixFog(col, i.fog);
                return half4(col, _BaseColor.a * tex.a);
            }
            ENDHLSL
        }

        // Inverted-hull outline (Rushlings only; disabled per material elsewhere). Width grows a little with distance
        // so far Rushlings keep a readable outline without getting fat up close.
        Pass
        {
            Name "Outline"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Cull Front
            ZWrite On

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex vertOutline
            #pragma fragment fragOutline
            #pragma multi_compile_instancing
            #pragma multi_compile_fog

            struct A
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv2 : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct V
            {
                float4 positionCS : SV_POSITION;
                half fog : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            V vertOutline(A v)
            {
                V o = (V)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 p = Lid(SpinKey(v.positionOS.xyz, v.uv2.y), v.uv2.x * _RushlingMesh);
                float3 n = LidN(SpinKeyN(v.normalOS, v.uv2.y), v.uv2.x * _RushlingMesh);
                // Small parts (face, goggles, seam, emblem: uv2.y = 1 or 3) get no hull of their own.
                if (abs(v.uv2.y - 1.0) < 0.5 || abs(v.uv2.y - 3.0) < 0.5)
                {
                    o.positionCS = float4(0, 0, -10, 1);
                    return o;
                }
                float3 ws = TransformObjectToWorld(p);
                float3 nws = normalize(TransformObjectToWorldNormal(n));
                float dist = length(GetCameraPositionWS() - ws);
                ws += nws * _OutlineWidth * clamp(dist * 0.06, 1.0, 2.6);
                o.positionCS = TransformWorldToHClip(ws);
                o.fog = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 fragOutline(V i) : SV_Target
            {
                return half4(MixFog(_OutlineColor.rgb, i.fog), 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex vertDepth
            #pragma fragment fragDepth
            #pragma multi_compile_instancing

            struct A
            {
                float4 positionOS : POSITION;
                float2 uv2 : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct V
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            V vertDepth(A v)
            {
                V o = (V)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(Lid(SpinKey(v.positionOS.xyz, v.uv2.y), v.uv2.x * _RushlingMesh));
                return o;
            }

            half fragDepth(V i) : SV_Target { return 0; }
            ENDHLSL
        }
    }

    FallBack Off
}
