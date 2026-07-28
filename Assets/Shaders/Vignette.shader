Shader "TankIO/Vignette"
{
    Properties
    {
        _VignetteColor("Colour", Color) = (0, 0, 0, 1)
        _Intensity("Intensity", Range(0, 1)) = 0.5
        // distance from centre where darkening starts. 1 is the edge midpoints, 1.41 the corners,
        // so anything above 1 leaves the sides clear and shades only the corners.
        _Radius("Radius", Range(0, 1.5)) = 0.75
        _Softness("Softness", Range(0.01, 1)) = 0.5
    }

    SubShader
    {
        // after the weather layers: the vignette darkens the rain and mist too, not just the world
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+200" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _VignetteColor;
                float _Intensity;
                float _Radius;
                float _Softness;
            CBUFFER_END

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;
                // the quad's own transform is ignored: writing clip space directly means the fill always covers the screen exactly, whatever the mesh is or where it sits
                OUT.positionHCS = float4(IN.uv * 2.0 - 1.0, UNITY_NEAR_CLIP_VALUE, 1.0);
                // bypassing the projection matrix also bypasses the y-flip Unity applies when a camera renders into an intermediate target, so put it back by hand
                OUT.positionHCS.y *= _ProjectionParams.x;
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                // no aspect correction: the falloff stays an ellipse matching the screen, which keeps the shading even. a true circle over 16:9 crushes the left and right edges
                float dist = length(IN.uv - 0.5) * 2.0;
                half v = smoothstep(_Radius, _Radius + _Softness, dist);
                return half4(_VignetteColor.rgb, v * _Intensity);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
