// Ground surface for the disc map. One quad spanning the grid; the mask clip is what carves the
// disc out of it, which is the only thing a stock URP/Lit material cannot do.
// The albedo tiles by world position, so texel density is independent of grid size.
Shader "TankIO/Ground"
{
    Properties
    {
        [NoScaleOffset] _GroundMask("Ground mask (R)", 2D) = "white" {}
        [NoScaleOffset] _SplatMap("Splat map (R: grass weight)", 2D) = "black" {}
        [NoScaleOffset] _GrassAlbedo("Grass albedo", 2D) = "white" {}
        [NoScaleOffset] _DirtAlbedo("Dirt albedo", 2D) = "gray" {}
        _GrassTiling("Grass tiling (repeats per world unit)", Vector) = (0.25, 0.25, 0, 0)
        // Ground081 is 2048x1024: one repeat spans 1/tiling world units, so x stays half of y
        // and the repeat covers a 2:1-wide footprint matching the texture (SOURCES.txt).
        _DirtTiling("Dirt tiling (repeats per world unit)", Vector) = (0.125, 0.25, 0, 0)
        _BlendWidth("Grass/dirt blend width", Range(0.01, 0.5)) = 0.05
        _Smoothness("Smoothness", Range(0, 1)) = 0
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
            float _Smoothness;
        CBUFFER_END

        TEXTURE2D(_GroundMask);   SAMPLER(sampler_GroundMask);
        TEXTURE2D(_SplatMap);     SAMPLER(sampler_SplatMap);
        TEXTURE2D(_GrassAlbedo);  SAMPLER(sampler_GrassAlbedo);
        TEXTURE2D(_DirtAlbedo);   SAMPLER(sampler_DirtAlbedo);

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

                // splat by quad uv (covers the map once, per-tile weights); albedos by world
                // position (tile many times). shape and detail stay independent resolutions.
                float weight = SAMPLE_TEXTURE2D(_SplatMap, sampler_SplatMap, IN.uv).r;
                // narrow the transition band: a raw lerp shows both textures at half strength
                // over the whole gradient and reads as fog, not a border.
                weight = smoothstep(0.5 - _BlendWidth, 0.5 + _BlendWidth, weight);

                half3 grass = SAMPLE_TEXTURE2D(_GrassAlbedo, sampler_GrassAlbedo, IN.positionWS.xz * _GrassTiling.xy).rgb;
                half3 dirt = SAMPLE_TEXTURE2D(_DirtAlbedo, sampler_DirtAlbedo, IN.positionWS.xz * _DirtTiling.xy).rgb;
                half3 albedo = lerp(dirt, grass, weight);

                InputData inputData = (InputData)0;
                inputData.positionWS = IN.positionWS;
                inputData.normalWS = NormalizeNormalPerPixel(IN.normalWS);
                inputData.viewDirectionWS = SafeNormalize(GetWorldSpaceViewDir(IN.positionWS));
                inputData.shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                inputData.fogCoord = IN.fogFactor;
                inputData.bakedGI = SampleSH(inputData.normalWS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = albedo;
                surfaceData.smoothness = _Smoothness;
                surfaceData.normalTS = half3(0, 0, 1);
                surfaceData.occlusion = 1.0;
                surfaceData.alpha = 1.0;

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
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
