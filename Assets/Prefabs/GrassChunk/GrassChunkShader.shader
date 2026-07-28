// Shader "Custom/GrassShader"
// {
//     Properties
//     {
//         _MainTex ("Texture", 2D) = "white" {}
//     }
//     SubShader
//     {
//         Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
//         LOD 100

//         Pass
//         {
//             HLSLPROGRAM
//             #pragma vertex vert
//             #pragma fragment frag
//             #pragma multi_compile_fog

//             #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

//             struct Attributes
//             {
//                 float4 positionOS : POSITION;
//                 float2 uv : TEXCOORD0;
//             };

//             struct Varyings
//             {
//                 float4 positionHCS : SV_POSITION;
//                 float2 uv : TEXCOORD0;
//             };

//             // ALL properties must live inside this CBUFFER for SRP Batcher
//             CBUFFER_START(UnityPerMaterial)
//                 float4 _MainTex_ST;
//             CBUFFER_END

//             TEXTURE2D(_MainTex);
//             SAMPLER(sampler_MainTex);

//             Varyings vert(Attributes IN)
//             {
//                 Varyings OUT;
//                 OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
//                 OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);
//                 return OUT;
//             }

//             half4 frag(Varyings IN) : SV_Target
//             {
//                 half4 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
//                 clip(col.a - 0.5);
//                 return col;
//             }
//             ENDHLSL
//         }
//     }
// }
Shader "Custom/GrassShader"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}

        _BottomColor ("Bottom Color", Color) = (0.25, 0.45, 0.12, 1)
        _TopColor ("Top Color", Color) = (0.65, 0.95, 0.35, 1)
        _Gradient ("Gradient Height", Range(0.01, 1)) = 1.0

        _WindStrength ("Wind Strength", Range(0, 1)) = 0.15
        _WindSpeed ("Wind Speed", Range(0, 10)) = 2
        _WindFrequency ("Wind Frequency", Range(0, 10)) = 3
    }

    SubShader
    {
        Tags
        {
            "RenderType"="TransparentCutout"
            "Queue"="AlphaTest"
            "RenderPipeline"="UniversalPipeline"
        }

        LOD 100

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fog
            // RenderMeshInstanced feeds unity_ObjectToWorld per instance; without this
            // variant every instance in a batch renders with the same matrix
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float fogCoord : TEXCOORD2;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;

                float4 _BottomColor;
                float4 _TopColor;
                float _Gradient;

                float _WindStrength;
                float _WindSpeed;
                float _WindFrequency;
            CBUFFER_END

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                // selects this instance's matrices before any ObjectToWorld transform below
                UNITY_SETUP_INSTANCE_ID(IN);

                float3 pos = IN.positionOS.xyz;

                float heightMask = saturate(pos.y);

                float wind = sin(
                    (_Time.y * _WindSpeed) +
                    (pos.x * _WindFrequency)
                );

                pos.x += wind * _WindStrength * heightMask;

                OUT.positionHCS = TransformObjectToHClip(pos);
                OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);

                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                OUT.fogCoord = ComputeFogFactor(OUT.positionHCS.z);

                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half4 texCol = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);

                clip(texCol.a - 0.5);

                float gradientMask = smoothstep(0.0, _Gradient, IN.uv.y);
                half4 gradientColor = lerp(_BottomColor, _TopColor, gradientMask);

                half4 col = texCol * gradientColor;

                float3 normalWS = normalize(IN.normalWS);

                Light mainLight = GetMainLight();

                float NdotL = saturate(dot(normalWS, mainLight.direction));

                // Direct light
                float3 directLight = mainLight.color * NdotL;

                // Ambient light from scene/environment
                float3 ambientLight = SampleSH(normalWS);

                col.rgb *= directLight + ambientLight;

                col.rgb = MixFog(col.rgb, IN.fogCoord);

                return col;
            }

            ENDHLSL
        }
    }
}