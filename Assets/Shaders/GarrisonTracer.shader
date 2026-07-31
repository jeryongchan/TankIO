// garrison fire, drawn on the LineRenderer HqController.Garrison builds. LineTextureMode.Tile
// makes uv.x count world units, so the dashes hold one size at any range. uv.y runs across the width.
Shader "TankIO/GarrisonTracer"
{
    Properties
    {
        [HDR] _Color("Colour", Color) = (1, 0.72, 0.3, 1)
        _ScrollSpeed("Scroll speed (dashes/sec)", Range(0, 40)) = 12
        _TailSharpness("Tail sharpness", Range(1, 16)) = 5
        [IntRange] _StrandCount("Strands", Range(1, 8)) = 3
        _StrandWidth("Strand thickness", Range(0.01, 1)) = 0.05
        _SpeedVariance("Speed variance between strands", Range(0, 1)) = 0.4
        _StrandVariance("Brightness variance between strands", Range(0, 1)) = 0.5
        _StartFade("Fade in from the muzzle", Range(0, 0.5)) = 0.12
        _EndFade("Fade out at the impact", Range(0, 0.5)) = 0.12
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Tags { "LightMode" = "UniversalForward" }

            Blend One One
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR; // LineRenderer bakes startColor-to-endColor into this
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };

            CBUFFER_START(UnityPerMaterial)
            float4 _Color;
            float _ScrollSpeed;
            float _TailSharpness;
            float _StrandCount;
            float _StrandWidth;
            float _SpeedVariance;
            float _StrandVariance;
            float _StartFade;
            float _EndFade;
            float _LineLength;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                OUT.color = IN.color;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float strand = IN.uv.y * _StrandCount;
                float index = floor(strand);
                float hash = frac(sin(index * 12.9898) * 43758.5453);

                // _StrandWidth is the fraction of its slot each strand fills. the falloff must end
                // there, not start there, or lowering it widens the strand instead of thinning it
                half profile = 1.0 - smoothstep(0.0, _StrandWidth, abs(frac(strand) * 2.0 - 1.0));

                // per-strand phase and speed, or the strands scroll in lockstep and merge into one
                float speed = _ScrollSpeed * (1.0 + (hash - 0.5) * _SpeedVariance);
                float along = IN.uv.x + hash - _Time.y * speed;
                half dash = pow(frac(along), _TailSharpness); // bright head, fading tail

                half brightness = lerp(1.0 - _StrandVariance, 1.0, hash);

                // 0 at the muzzle, 1 at the impact - the vertex alpha, the only normalized position
                // here since uv.x counts world units
                float alongLine = IN.color.a;
                half ends =
                    smoothstep(0.0, _StartFade, alongLine)
                    * (1.0 - smoothstep(1.0 - _EndFade, 1.0, alongLine));

                return half4(_Color.rgb * (dash * profile * brightness * ends * _Color.a), 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
