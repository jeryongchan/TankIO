// Screen-space rain: a quad parented to the camera, scrolling a tiling streak mask twice at two
// scales. Nothing here is in world space, which is only defensible because the camera never
// rotates -- a fixed screen direction is a fixed world direction, so the streaks keep falling
// "down" the map however far it pans.
Shader "TankIO/Rain"
{
    Properties
    {
        [NoScaleOffset] _RainTex("Streak mask", 2D) = "black" {}
        _RainColor("Rain colour (RGB, A: opacity)", Color) = (0.78, 0.83, 0.9, 0.35)
        _Tiling("Streak tiling (repeats per screen height)", Range(0.5, 8)) = 1.8
        _Slant("Slant", Range(-1, 1)) = 0.35
        _Speed("Fall speed (screen heights per second)", Range(0, 6)) = 1.2
        _LayerScale("Far sheet scale", Range(1, 4)) = 1.9
        _LayerStrength("Far sheet strength", Range(0, 1)) = 0.6
    }

    SubShader
    {
        // past the transparents: rain falls in front of the whole scene, including other overlays
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+100" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Rain"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
            Cull Off // the quad is emitted straight to clip space, so its winding is not worth reasoning about

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _RainColor;
                float _Tiling;
                float _Slant;
                float _Speed;
                float _LayerScale;
                float _LayerStrength;
            CBUFFER_END

            TEXTURE2D(_RainTex); SAMPLER(sampler_RainTex);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
            };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;
                // the uv drives the output, not the vertex position: any unit quad fits, and with
                // no transform in the way, pan and zoom leave the sheet exactly fullscreen.
                OUT.positionHCS = float4(IN.uv * 2.0 - 1.0, UNITY_NEAR_CLIP_VALUE, 1.0);
                // scene geometry gets this flip from its projection matrix when the camera renders
                // into a texture; skipping the matrix means skipping the flip, and the rain falls
                // upward on exactly the platforms where that happens.
                OUT.positionHCS.y *= _ProjectionParams.x;
                OUT.uv = IN.uv;
                return OUT;
            }

            // one sheet of rain. the skew leans the streaks, and the scroll runs along that same
            // lean, so a drop travels down its own length instead of sliding sideways through it.
            half Sheet(float2 p, float tiling)
            {
                float2 t = p * tiling;
                t.x += t.y * _Slant;
                // both sheets advance by the near sheet's tiling, not their own: they cover equal
                // texture distance, but the far sheet spends it on a bigger tile and so drifts
                // slower on screen. that is the parallax between them, for no extra parameter.
                // frac, not the raw product: the mask repeats every unit anyway, so this is the
                // same image with the coordinate kept bounded. it has no screen-space variation,
                // so the wrap cannot open a seam.
                t.y += frac(_Time.y * _Speed * _Tiling);
                // red times alpha, so both kinds of rain png work: white streaks on black, and
                // white streaks masked by transparency. the channel that carries no mask is 1.
                half4 mask = SAMPLE_TEXTURE2D(_RainTex, sampler_RainTex, t);
                return mask.r * mask.a;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                // widen by aspect so a streak keeps its thickness whatever shape the window is
                float2 p = float2(IN.uv.x * (_ScreenParams.x / _ScreenParams.y), IN.uv.y);
                half sheetNear = Sheet(p, _Tiling);
                // the offset stops the second sheet landing on the same texels as the first and
                // drawing the same drops twice, one inside the other.
                half sheetFar = Sheet(p + 0.37, _Tiling * _LayerScale);
                half rain = saturate(sheetNear + sheetFar * _LayerStrength);
                return half4(_RainColor.rgb, rain * _RainColor.a);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
