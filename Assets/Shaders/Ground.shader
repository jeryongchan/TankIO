// Ground surface for the disc map: one quad spanning the grid, with the mask clip carving the
// disc out of it. Albedo tiles by world position, so texel density is independent of grid size.
Shader "TankIO/Ground"
{
    Properties
    {
        // hidden: GroundRenderer generates both at runtime and binds them through a property block
        [HideInInspector] [NoScaleOffset] _GroundMask("Ground mask (R)", 2D) = "white" {}
        [HideInInspector] [NoScaleOffset] _SplatMap("Splat map (R: grass weight)", 2D) = "black" {}
        [NoScaleOffset] _GrassAlbedo("Grass albedo", 2D) = "white" {}
        [NoScaleOffset] _DirtAlbedo("Dirt albedo", 2D) = "gray" {}
        _GrassTiling("Grass tiling (repeats per world unit)", Vector) = (0.25, 0.25, 0, 0)
        // Ground081 is 2048x1024, so x stays half of y and the repeat comes out square (SOURCES.txt)
        _DirtTiling("Dirt tiling (repeats per world unit)", Vector) = (0.125, 0.25, 0, 0)
        _BlendWidth("Grass/dirt blend width", Range(0.01, 0.5)) = 0.05
        _GrassTint("Grass tint (RGB), grass over dirt (A)", Color) = (1, 1, 1, 1)
        [NoScaleOffset] [Normal] _DirtNormal("Dirt normal map", 2D) = "bump" {}
        _DirtRelief("Dirt relief strength", Range(0, 3)) = 1
        [NoScaleOffset] _PuddleNoise("Puddle noise (R)", 2D) = "black" {}
        _PuddleTiling("Puddle tiling (repeats per world unit)", Vector) = (0.02, 0.02, 0, 0)
        _PuddleCoverage("Puddle coverage", Range(0, 1)) = 0.35
        _PuddleEdge("Puddle edge softness", Range(0.01, 0.5)) = 0.1
        _PuddleMurk("Puddle murkiness", Range(0, 1)) = 0.6
        _PuddleSheen("Puddle sheen (RGB, A: strength)", Color) = (0.55, 0.62, 0.70, 0.5)
        _PuddleDetailScale("Puddle edge detail scale", Range(1, 20)) = 7
        _PuddleDetailAmount("Puddle edge detail amount", Range(0, 0.5)) = 0.25
        [NoScaleOffset] _RippleNormal("Ripple normal map", 2D) = "bump" {}
        _RippleTiling("Ripple tiling (repeats per world unit)", Range(0.05, 2)) = 0.4
        _RippleSpeed("Ripple speed", Range(0, 0.2)) = 0.02
        _RippleStrength("Ripple bump strength", Range(0, 2)) = 1
        [NoScaleOffset] _DropRippleField("Drop ripples (R dist, G phase, BA dir)", 2D) = "white" {}
        _DropTiling("Drop ripple tiling (repeats per world unit)", Range(0.005, 0.2)) = 0.05
        _DropRate("Drop ripple rate (rings per second)", Range(0, 3)) = 0.7
        _DropStrength("Drop ripple strength", Range(0, 2)) = 0.6
        [HDR] _GlintColor("Glint colour (RGB, A: intensity)", Color) = (1, 0.98, 0.92, 1)
        _GlintTilt("Glint alignment tilt", Range(0, 1)) = 0.25
        _GlintPower("Glint tightness", Range(4, 256)) = 64
        _PatchTiling("Sheen patch tiling (repeats per world unit)", Range(0.001, 0.1)) = 0.01
        _PatchStrength("Sheen patchiness", Range(0, 1)) = 0.7
        [HDR] _EdgeGlow("Puddle edge glow (RGB, A: strength)", Color) = (0.9, 0.95, 1.0, 0.5)
        _ShoreWobble("Shoreline lap distance (world units)", Range(0, 4)) = 1
        _RimLightDir("Shoreline light direction (screen XY)", Vector) = (1, 0.5, 0, 0)
        _RimDirectional("Shoreline directionality", Range(0, 1)) = 0.7
        _RimSpread("Shoreline flare spread", Range(0.25, 4)) = 1
        // past ~3 the dense areas saturate and stop growing while thin ones keep filling in,
        // which is the only reason the range reaches past 1
        _ReflectionStrength("Reflection strength", Range(0, 6)) = 2
        _ReflectionDistort("Reflection wobble", Range(0, 3)) = 1
        _ReflectionTint("Reflection tint (RGB, A: amount)", Color) = (1, 1, 1, 0)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _GrassTiling;
            float4 _DirtTiling;
            float _BlendWidth;
            float4 _GrassTint;
            float _DirtRelief;
            float4 _PuddleTiling;
            float _PuddleCoverage;
            float _PuddleEdge;
            float _PuddleMurk;
            float4 _PuddleSheen;
            float _PuddleDetailScale;
            float _PuddleDetailAmount;
            float _RippleTiling;
            float _RippleSpeed;
            float _RippleStrength;
            float _DropTiling;
            float _DropRate;
            float _DropStrength;
            float4 _GlintColor;
            float _GlintTilt;
            float _GlintPower;
            float _PatchTiling;
            float _PatchStrength;
            float4 _EdgeGlow;
            float _ShoreWobble;
            float4 _RimLightDir;
            float _RimDirectional;
            float _RimSpread;
            float _ReflectionStrength;
            float _ReflectionDistort;
            float4 _ReflectionTint;
        CBUFFER_END

        TEXTURE2D(_GroundMask);   SAMPLER(sampler_GroundMask);
        TEXTURE2D(_SplatMap);     SAMPLER(sampler_SplatMap);
        TEXTURE2D(_GrassAlbedo);  SAMPLER(sampler_GrassAlbedo);
        TEXTURE2D(_DirtAlbedo);   SAMPLER(sampler_DirtAlbedo);
        TEXTURE2D(_DirtNormal);   SAMPLER(sampler_DirtNormal);
        TEXTURE2D(_PuddleNoise);  SAMPLER(sampler_PuddleNoise);
        TEXTURE2D(_RippleNormal); SAMPLER(sampler_RippleNormal);
        TEXTURE2D(_DropRippleField);  SAMPLER(sampler_DropRippleField);

        // set globally by PlanarReflection, so no Properties entry and outside the cbuffer. the
        // flag is per camera: scene view and previews get 0 rather than a mismatched image.
        TEXTURE2D(_PlanarReflectionTex); SAMPLER(sampler_PlanarReflectionTex);
        float _PlanarReflectionOn;

        // the mask is one texel per tile with no mip chain, so a texel is either ground or void.
        void ClipToGround(float2 uv)
        {
            clip(SAMPLE_TEXTURE2D(_GroundMask, sampler_GroundMask, uv).r - 0.5);
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.0

            // forward+ never fills the per-object light data the classic path multiplies by,
            // so without this keyword the main light contributes zero.
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float3 positionWS  : TEXCOORD1;
                float3 normalWS    : TEXCOORD2;
                float  fogFactor   : TEXCOORD3;
            };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs pos = GetVertexPositionInputs(IN.positionOS.xyz);

                OUT.positionHCS = pos.positionCS;
                OUT.positionWS = pos.positionWS;
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                OUT.uv = IN.uv;
                OUT.fogFactor = ComputeFogFactor(pos.positionCS.z);
                return OUT;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                ClipToGround(IN.uv);

                // splat by quad uv (one texel per tile), albedo by world position: shape and
                // detail stay independent resolutions.
                float weight = SAMPLE_TEXTURE2D(_SplatMap, sampler_SplatMap, IN.uv).r;
                // narrow the band: a raw lerp shows both textures at half strength throughout
                // and reads as fog, not a border.
                weight = smoothstep(0.5 - _BlendWidth, 0.5 + _BlendWidth, weight);

                half3 grass = SAMPLE_TEXTURE2D(_GrassAlbedo, sampler_GrassAlbedo, IN.positionWS.xz * _GrassTiling.xy).rgb * _GrassTint.rgb;
                half3 dirt = SAMPLE_TEXTURE2D(_DirtAlbedo, sampler_DirtAlbedo, IN.positionWS.xz * _DirtTiling.xy).rgb;
                // alpha holds grass back from the albedo only: weight stays untouched, so puddles
                // and relief still read the shape the splat map drew.
                half3 albedo = lerp(dirt, grass, weight * _GrassTint.a);

                // scrolling normals for the water surface. decoded by hand (*2-1, not UnpackNormal)
                // because this one is imported as a plain sRGB-off Default map, unlike _DirtNormal.
                // sampled before the pools: the waterline lookup rides on the slope.
                float2 rippleUV = IN.positionWS.xz * _RippleTiling;
                half2 rn1 = SAMPLE_TEXTURE2D(_RippleNormal, sampler_RippleNormal, rippleUV + float2(_Time.y * _RippleSpeed, 0)).rg * 2.0 - 1.0;
                half2 rn2 = SAMPLE_TEXTURE2D(_RippleNormal, sampler_RippleNormal, rippleUV * 1.7 - float2(0, _Time.y * _RippleSpeed * 0.7)).rg * 2.0 - 1.0;
                half2 slope = (rn1 + rn2) * 0.5;

                // pools are low-frequency noise over a coverage threshold. sampling at a
                // wave-displaced position drags the waterline with the ripples so the shore laps;
                // every edge effect keys off poolPos and inherits it.
                float2 poolPos = IN.positionWS.xz + slope * _ShoreWobble;
                float noise = SAMPLE_TEXTURE2D(_PuddleNoise, sampler_PuddleNoise, poolPos * _PuddleTiling.xy).r;
                // finer octave on the threshold: one smooth noise alone gives rounded blobs
                float detail = SAMPLE_TEXTURE2D(_PuddleNoise, sampler_PuddleNoise, poolPos * _PuddleTiling.xy * _PuddleDetailScale).r;
                float threshold = 1.0 - _PuddleCoverage + (detail - 0.5) * _PuddleDetailAmount;
                float pool = smoothstep(threshold - _PuddleEdge, threshold + _PuddleEdge, noise);
                // kept separate from the pool shape: folded in, a grass border would sweep through
                // the half-covered value the shoreline rim keys on and ring every patch in surf.
                float dryGround = 1.0 - weight;
                float puddle = pool * dryGround;

                // darken the gravel out of the way: losing that detail is what reads as a surface
                // rather than wet ground. toward black, since everything visible in a pool is
                // composited after the PBR call and needs no base to sit on.
                albedo *= 1.0 - _PuddleMurk * puddle;

                // the quad faces straight up, so tangent space maps to world as x->x, y->z.
                half2 wave = slope * (_RippleStrength * puddle);

                // raindrop rings. the field bakes distance to the nearest impact point, its phase,
                // and the outward direction, so one sample animates every ring independently: a
                // ring is where the stored distance has been overtaken by that point's age. the uv
                // creep is too slow to see and only stops impacts repeating on the same spots.
                float2 dropUV = IN.positionWS.xz * _DropTiling + _Time.y * 0.004;
                half4 drop = SAMPLE_TEXTURE2D(_DropRippleField, sampler_DropRippleField, dropUV);
                float age = frac(_Time.y * _DropRate + drop.g);
                half band = 1.0 - saturate(abs(drop.r - age) * 9.0);
                // squared for a tighter crest. the age term also keeps unowned texels (distance 1)
                // from flashing at the wrap.
                half ring = band * band * (1.0 - age);
                // into the wave rather than drawn as a decal, so rings bend the reflection and
                // catch glints like the rest of the surface
                wave += (drop.ba * 2.0 - 1.0) * (ring * _DropStrength * puddle);

                // gravel relief. normal maps survive an ortho camera: they feed diffuse N.L, which
                // never involved the view direction. imported as a NormalMap, so it decodes through
                // UnpackNormal rather than the *2-1 above. tiled with the dirt albedo to land on
                // the right grains, and faded under water, which the ripples shape instead.
                half2 relief = UnpackNormalScale(SAMPLE_TEXTURE2D(_DirtNormal, sampler_DirtNormal, IN.positionWS.xz * _DirtTiling.xy), _DirtRelief).xy;
                relief *= dryGround * (1.0 - puddle);

                InputData inputData = (InputData)0;
                inputData.positionWS = IN.positionWS;
                inputData.normalWS = NormalizeNormalPerPixel(IN.normalWS + float3(wave.x + relief.x, 0, wave.y + relief.y));
                inputData.viewDirectionWS = SafeNormalize(GetWorldSpaceViewDir(IN.positionWS));
                inputData.shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                inputData.fogCoord = IN.fogFactor;
                inputData.bakedGI = SampleSH(inputData.normalWS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                // smoothness stays zero: ortho view plus a directional light fix the halfway
                // vector, so the specular lobe is constant. every highlight is added after PBR.
                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = albedo;
                surfaceData.occlusion = 1.0;
                surfaceData.alpha = 1.0;

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                // ortho leaves the water with no bright heart anywhere, so this drifting field puts
                // the broad glare regions back. glints square it to cluster in the cores.
                float patchN = SAMPLE_TEXTURE2D(_PuddleNoise, sampler_PuddleNoise, IN.positionWS.xz * _PatchTiling + _Time.y * 0.003).r;
                float patch = 1.0 + (patchN * 2.0 - 1.0) * _PatchStrength;

                // overcast-sky bounce, flat: keeps the pools from going black between glints
                color.rgb += _PuddleSheen.rgb * (puddle * _PuddleSheen.a * patch);
                // the mirror camera's texture is screen-aligned, so this fragment's own screen uv is
                // the reflected scene; the wave smears the lookup so reflections wobble. alpha is
                // coverage, the camera having cleared to transparent, so open sky contributes
                // nothing. lerp not add: something blocking the sky has to darken the water, which
                // is most of what passes for a shadow here.
                if (_PlanarReflectionOn > 0.5)
                {
                    float2 screenUV = GetNormalizedScreenSpaceUV(IN.positionHCS);
                    half4 refl = SAMPLE_TEXTURE2D(_PlanarReflectionTex, sampler_PlanarReflectionTex, screenUV + wave * (_ReflectionDistort * 0.05));
                    // alpha runs thin: leaf cards clip to nothing between them and a quarter-res
                    // target averages a canopy down further, so a reflected tree lands at about a
                    // third weight and the water shows through, hence the coverage boost.
                    half coverage = saturate(refl.a * _ReflectionStrength);
                    // lerp, not multiply: the reflected rgb is near-black, and near-zero times any
                    // hue stays near-zero. alpha is how far it drifts, so 1 is a flat colour.
                    half3 reflected = lerp(refl.rgb, _ReflectionTint.rgb, _ReflectionTint.a);
                    color.rgb = lerp(color.rgb, reflected, coverage * puddle);
                }
                // sparkle against a virtual direction, not the sun: with one shared view direction
                // real alignment would hold everywhere or nowhere. tilt picks which slope catches,
                // and the ripples scrolling through it do the twinkling.
                half3 glintDir = normalize(half3(_GlintTilt, 1.0, _GlintTilt));
                half glint = pow(saturate(dot(inputData.normalWS, glintDir)), _GlintPower);
                color.rgb += _GlintColor.rgb * (_GlintColor.a * glint * puddle * patch * patch);
                // shoreline shine: the pool's own fade band is where thin water meets ground, so
                // light it directly. peaks mid-fade, zero on dry ground and in open water.
                half rim = pool * (1.0 - pool) * 4.0;
                // detail modulates rather than gates: a full multiply halved the band and crushed
                // its dark stretches to nothing.
                rim *= rim * lerp(0.4, 1.0, detail) * dryGround;
                // a meniscus flares only where it tilts toward the light, and that one-sided flare
                // is the difference between a lit edge and a traced outline. 
                // the rim has no facing of its own, so screen derivatives of pool recover it: the gradient points across the waterline, 
                // and under a camera that never rotates screen direction is fixed.
                float2 facing = float2(ddx(pool), ddy(pool));
                facing *= rsqrt(dot(facing, facing) + 1e-6);
                float2 rimDir = _RimLightDir.xy * rsqrt(dot(_RimLightDir.xy, _RimLightDir.xy) + 1e-6);
                half lit = saturate(dot(facing, rimDir));
                // spread widens the flare from a hard terminator to a soft wrap around the shore
                rim *= lerp(1.0, pow(lit, _RimSpread), _RimDirectional);
                color.rgb += _EdgeGlow.rgb * (_EdgeGlow.a * rim * patch);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                return color;
            }
            ENDHLSL
        }

        // without this the depth prepass would write the full quad and the void outside the disc
        // would occlude anything drawn behind it.
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #pragma target 3.0

            struct DepthAttributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct DepthVaryings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
            };

            DepthVaryings DepthVert(DepthAttributes IN)
            {
                DepthVaryings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 DepthFrag(DepthVaryings IN) : SV_Target
            {
                ClipToGround(IN.uv);
                return 0;
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
}
