// Fall Rush toon shader (G2.1, doc 17 §3.2). Hand-written URP Forward HLSL (not Shader Graph: the project is built
// headless in batch mode, a text shader is reviewable and has exactly the variants we need, and it carries the
// inverted-hull outline as a second pass, which Shader Graph cannot do in URP without a renderer feature).
//
//  * 3-band cel ramp on half-Lambert from the one directional "sun" (no realtime shadows), shade colour tint
//  * rim light (sun side) + G4.6 view rim on hazards / Rushlings (_ViewRim, gated by _RimAll or uv2.y = 4; off on
//    up-facing faces), palette tint (_BaseColor x vertex colour), fake AO from vertex colour (code-built meshes bake it)
//  * gradient ambient (globals _FR_AmbSky / _FR_AmbGround set by the client; falls back to the SH ambient probe)
//  * flash / glow mask (vertex alpha) for telegraphs: lerp to _FlashColor.rgb by _FlashColor.a (per renderer, MPB)
//  * travelling chevron light for conveyor belts (uv2.x = chevron index, _Scroll = wave position, MPB)
//  * Rushling extras (uv2.y flags): 1 = LED screen face (atlas _FaceMap, offset _FaceST, emissive, unlit),
//    2 = wind-up key vertices (spun about the key shaft by _KeyAngle in object space), 3 = no outline hull
//  * _Unlit blends lighting out (clouds, effects, the silhouette test); _Emission adds glow; alpha from _BaseColor.a
//    when the material is set up as transparent (_SrcBlend/_DstBlend/_ZWrite, queue 3000)
//  * Outline pass (LightMode SRPDefaultUnlit): only enabled on Rushling materials (Material.SetShaderPassEnabled)
//  * SRP Batcher compatible (one UnityPerMaterial CBUFFER) and GPU-instancing compatible.
//  * G4.8 looks (global _FR_Look: 0 soft, 1 candy). Map meshes carry two colours: COLOR = soft albedo, uv3 = (candy albedo,
//    2 + pattern + 8 x role); meshes without uv3 (w < 1.5) use COLOR in both looks. Soft only: a tone-on-tone world-space
//    pattern on walkable tops (role 1; stripes / dots / checks / chevrons, +-6 % value, fwidth anti-aliased), a wider foam ramp
//    (half the cel bands, half a smooth half-Lambert), a broad soft gloss on rims / hazards / props (roles 3 / 4 / 5), a
//    Fresnel sky tint on edges (never on tops) and the URP main-light shadows (one cascade, soft; ShadowCaster pass below).
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
        _ViewRim("View rim strength (any side of the sun)", Range(0,1)) = 0
        _RimAll("View rim on the whole material (else uv2.y = 4 parts only)", Range(0,1)) = 0
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
            half _ViewRim, _RimAll;
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
        half _FR_Look;        // G4.8 global: 0 soft (default), 1 candy
        half4 _FR_SkyHorizon; // G4.8 global: the sky horizon (Fresnel tint)

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
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                float2 uv2 : TEXCOORD1;
                float4 uv3 : TEXCOORD3;
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
                float4 dual : TEXCOORD6;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // G4.8 tone-on-tone patterns (world xz, metres): 1 soft diagonal stripes, 2 polka dots, 3 checks, 4 chevrons.
            half Pattern(float2 p, half id)
            {
                if (id < 0.5h)
                    return 0.5h;
                float v;
                if (id < 1.5h)
                {
                    v = frac((p.x + p.y) / 2.2) * 2.0 - 1.0;   // wide soft stripes
                    float w = fwidth(v) * 1.5 + 0.02;
                    return smoothstep(0.5 - w, 0.5 + w, abs(v));
                }
                if (id < 2.5h)
                {
                    float2 c = p / 1.7;
                    c.x += floor(c.y) * 0.5;                    // staggered rows
                    float d = length(frac(c) - 0.5) * 1.7;
                    float w = fwidth(d) * 1.5 + 0.01;
                    return 1.0 - smoothstep(0.36 - w, 0.36 + w, d);
                }
                if (id < 3.5h)
                {
                    float2 c = frac(p / 3.0) - 0.5;              // 1.5 m checks
                    float q = c.x * c.y;
                    float w = fwidth(q) * 1.5 + 0.001;
                    return smoothstep(-w, w, q);
                }
                v = frac((abs(frac(p.x / 2.4) - 0.5) * 2.4 + p.y) / 1.8) * 2.0 - 1.0; // chevrons
                float w4 = fwidth(v) * 1.5 + 0.02;
                return smoothstep(0.5 - w4, 0.5 + w4, abs(v));
            }

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
                o.dual = v.uv3;
                o.fog = ComputeFogFactor(pos.positionCS.z);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv);
                // G4.8: the look's vertex colour (soft COLOR / candy uv3) and the soft roles.
                half soft = 1.0h - saturate(_FR_Look);
                half3 vcol = i.color.rgb;
                half role = 0.0h, pat = 0.0h;
                if (i.dual.w > 1.5)
                {
                    vcol = lerp(i.color.rgb, (half3)i.dual.rgb, saturate(_FR_Look));
                    float k = i.dual.w - 2.0;
                    role = floor(k / 8.0 + 0.01);
                    pat = k - role * 8.0;
                }
                half3 albedo = tex.rgb * _BaseColor.rgb * vcol;
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
                // G4.8 soft: tone-on-tone pattern on walkable tops (role 1), +-5 % value.
                if (soft > 0.5h && abs(role - 1.0h) < 0.5h && n.y > 0.7)
                    albedo *= 1.0h + (Pattern(i.positionWS.xz, pat) - 0.5h) * 0.1h;
            #if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE)
                Light sun = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
            #else
                Light sun = GetMainLight();
            #endif
                half ndl = dot(n, sun.direction) * 0.5h + 0.5h;               // half-Lambert
                half bs = lerp(_BandSoft, 0.1h, soft);                         // soft: wider, softer bands
                half bandA = smoothstep(_BandA - bs, _BandA + bs, ndl);
                half bandB = smoothstep(_BandB - bs, _BandB + bs, ndl);
                half ramp = lerp(0.0h, _MidLevel, bandA) + (1.0h - _MidLevel) * bandB; // 0 / mid / 1
                // G4.8 soft foam ramp: half the cel bands, half a smooth half-Lambert (a lit top, ndl 0.87, stays at 1).
                ramp = lerp(ramp, 0.5h * ramp + 0.5h * saturate((ndl - 0.25h) / 0.55h), soft);
                half3 shade = lerp(_ShadeColor.rgb, half3(1, 1, 1), ramp);
                // Real shadows (soft look only: the candy light casts none): the sun term drops to a quarter in full shadow,
                // so a cast shadow reads as a soft lavender patch (shadow strength 0.6 on the light keeps it from going dark).
                shade *= lerp(1.0h, lerp(0.25h, 1.0h, sun.shadowAttenuation), soft);

                half3 amb;
                if (_FR_AmbSky.a > 0.0h)
                    amb = lerp(_FR_AmbGround.rgb, _FR_AmbSky.rgb, n.y * 0.5h + 0.5h);
                else
                    amb = SampleSH(n);

                half3 lit = albedo * (shade * sun.color * 0.58h + amb * 0.34h);

                float3 viewDir = normalize(GetWorldSpaceViewDir(i.positionWS));
                half fres = pow(1.0h - saturate(dot(n, viewDir)), _RimPower);
                // G4.6: no rim on up-facing faces (walkable tops stay their colour at grazing angles instead of washing white).
                half notTop = 1.0h - smoothstep(0.55h, 0.85h, n.y);
                half rim = fres * _RimStrength * smoothstep(0.2h, 0.6h, ndl) * notTop; // sun rim: the lit side only
                // G4.6 view rim (hazards and Rushlings): independent of the sun, so a hazard's silhouette reads in shade too.
                // Gated per material (_RimAll: Rushlings, stripe posts) or per vertex (uv2.y = 4: merged hazard parts).
                half gate = max(_RimAll, step(abs(i.uv2.y - 4.0), 0.5h));
                rim = max(rim, fres * _ViewRim * gate * notTop);
                lit += _RimColor.rgb * rim;
                if (_Gloss > 0.0h)
                {
                    float3 h = normalize(sun.direction + viewDir);
                    half spec = smoothstep(0.92h, 0.96h, dot(n, h));
                    lit += spec * _Gloss * sun.color;
                }

                // G4.8 soft: broad foam / vinyl gloss on rims (3), hazards (4), props (5); a sky Fresnel tint on edges.
                if (soft > 0.5h)
                {
                    half g = role > 2.5h ? (role > 4.5h ? 0.22h : (role > 3.5h ? 0.4h : 0.32h)) : 0.0h;
                    if (g > 0.0h)
                    {
                        float3 hh = normalize(sun.direction + viewDir);
                        half sp = pow(saturate(dot(n, hh)), 28.0h);
                        lit += sp * g * sun.color * (0.35h + 0.65h * sun.shadowAttenuation) * 1.6h;
                    }
                    lit += _FR_SkyHorizon.rgb * (fres * fres * 0.22h * notTop) * step(0.5h, i.dual.w);
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

        // G4.8: shadow caster (the soft look's sun shadows; the candy look's light casts none).
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex vertShadow
            #pragma fragment fragShadow
            #pragma multi_compile_instancing

            float3 _LightDirection;

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
            };

            V vertShadow(A v)
            {
                V o = (V)0;
                UNITY_SETUP_INSTANCE_ID(v);
                float3 p = Lid(SpinKey(v.positionOS.xyz, v.uv2.y), v.uv2.x * _RushlingMesh);
                float3 n = LidN(SpinKeyN(v.normalOS, v.uv2.y), v.uv2.x * _RushlingMesh);
                float3 ws = TransformObjectToWorld(p);
                float3 nws = TransformObjectToWorldNormal(n);
                float4 cs = TransformWorldToHClip(ApplyShadowBias(ws, nws, _LightDirection));
            #if UNITY_REVERSED_Z
                cs.z = min(cs.z, UNITY_NEAR_CLIP_VALUE);
            #else
                cs.z = max(cs.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                o.positionCS = cs;
                return o;
            }

            half4 fragShadow(V i) : SV_Target { return 0; }
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
