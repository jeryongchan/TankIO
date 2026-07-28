// Ground mist: one flat quad floating just above the terrain, fading two drifting noise octaves
// in and out. It is real geometry rather than a screen overlay so the depth buffer sorts it for
// free -- canopies stand up through it, tanks pass under it, and the mist sits between things
// instead of washing over them. Sampled by world position, so it stays put as the camera pans
// and you scroll into and out of banks rather than dragging one with you.
Shader "TankIO/Mist"
{
    Properties
    {
        [NoScaleOffset] _MistNoise("Mist noise (R)", 2D) = "gray" {}
        _MistColor("Mist colour (RGB, A: opacity)", Color) = (0.72, 0.76, 0.82, 0.55)
        _MistTiling("Noise tiling (repeats per world unit)", Range(0.002, 0.1)) = 0.012
        _Coverage("Coverage", Range(0, 1)) = 0.5
        _Softness("Edge softness", Range(0.01, 0.6)) = 0.35
        _DriftSpeed("Drift speed", Range(0, 0.05)) = 0.01
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Mist"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off       // writing depth would hide everything the plane covers
            ZTest LEqual     // geometry standing in front of it still occludes it: the whole point
            Cull Off         // so the plane survives being rotated either way up

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _MistColor;
                float _MistTiling;
                float _Coverage;
                float _Softness;
                float _DriftSpeed;
            CBUFFER_END

            TEXTURE2D(_MistNoise); SAMPLER(sampler_MistNoise);

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 worldXZ     : TEXCOORD0;
            };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs pos = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionHCS = pos.positionCS;
                OUT.worldXZ = pos.positionWS.xz;
                return OUT;
            }

            // unlit on purpose: under one overcast directional light the lighting term is a
            // constant, and paying for the whole PBR chain to arrive at a flat tint would be
            // the most expensive way to get a colour that is already a material property.
            half4 Frag(Varyings IN) : SV_Target
            {
                float2 p = IN.worldXZ * _MistTiling;
                // the second sample is here for motion, not detail: one field alone translates
                // rigidly and reads as a texture being dragged over the map, while two crossing
                // at different speeds thin and thicken in place. 1.7 only has to stop the two
                // ever lining up, which is why it is a constant and not a slider.
                half n1 = SAMPLE_TEXTURE2D(_MistNoise, sampler_MistNoise, p + _Time.y * _DriftSpeed * float2(1.0, 0.35)).r;
                half n2 = SAMPLE_TEXTURE2D(_MistNoise, sampler_MistNoise, p * 1.7 - _Time.y * _DriftSpeed * float2(0.6, -0.2)).r;
                // product, not average: mist needs holes to read as banks instead of a flat
                // sheet, and a product collapses wherever either octave goes dark.
                half n = n1 * n2;
                // the product sits well below the midpoint, so coverage is remapped onto the
                // range it actually occupies rather than the 0..1 a single octave would give.
                float threshold = (1.0 - _Coverage) * 0.6;
                half mist = smoothstep(threshold - _Softness, threshold + _Softness, n);
                return half4(_MistColor.rgb, mist * _MistColor.a);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
