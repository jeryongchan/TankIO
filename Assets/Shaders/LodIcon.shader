Shader "Custom/LodIcon"
{
    Properties
    {
        _BaseMap ("Icon", 2D) = "white" {}
        _MaskTex ("Team Color Mask", 2D) = "black" {} // unassigned mask tints nothing
        _BaseColor ("Team Color", Color) = (1, 1, 1, 1)
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5
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
            Name "Unlit"
            Tags { "LightMode"="UniversalForward" }

            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // LodIcon.Tint sets _BaseColor per icon via property block - one draw call each without this
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float _Cutoff;
            CBUFFER_END

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _BaseColor)
            UNITY_INSTANCING_BUFFER_END(Props)

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_MaskTex);
            SAMPLER(sampler_MaskTex);

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);

                half4 art = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv);
                clip(art.a - _Cutoff);

                half mask = SAMPLE_TEXTURE2D(_MaskTex, sampler_MaskTex, IN.uv).r;
                half4 teamColor = UNITY_ACCESS_INSTANCED_PROP(Props, _BaseColor);

                // multiplied, not replaced, so the art keeps its shading
                half3 col = lerp(art.rgb, art.rgb * teamColor.rgb, mask);
                return half4(col, 1);
            }
            ENDHLSL
        }
    }
}
