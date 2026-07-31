// the sonar ping under a move order's goal, on the flat quad CommandLines pools next to each line.
// rings march outward from the centre by _Time, so nothing per-frame comes from C#.
Shader "TankIO/CommandRipple"
{
    Properties
    {
        _Color("Colour", Color) = (0, 1, 0.66, 0.9)
        _RingCount("Rings", Range(1, 6)) = 2
        _Speed("Ring speed (rings/sec)", Range(0, 5)) = 1.2
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

            Blend SrcAlpha OneMinusSrcAlpha
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
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            CBUFFER_START(UnityPerMaterial)
            float4 _Color;
            float _RingCount;
            float _Speed;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float r = length(IN.uv - 0.5) * 2.0; // 0 at centre, 1 at the quad's inscribed circle

                // phase grows with radius, so constant time slices are rings; time pushes them outward
                float wave = frac(r * _RingCount - _Time.y * _Speed);
                half ring = smoothstep(0.0, 0.15, wave) * (1.0 - smoothstep(0.5, 1.0, wave));

                half fade = saturate(1.0 - r); // die off toward the rim, and clip the quad's corners
                return half4(_Color.rgb, _Color.a * ring * fade);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
