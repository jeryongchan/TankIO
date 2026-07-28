// darkens the world toward the map rim, keyed to distance from the grid centre rather than from
// the camera. drawn as one clip-space quad after the opaques so it covers trees and units too:
// the same fade written into Ground.shader would tint only the ground and leave everything
// standing on it fully lit.
Shader "TankIO/MapFog"
{
    Properties
    {
        _FogColor("Fog colour", Color) = (0.04, 0.05, 0.07, 1)
        // fractions of the map radius: inner is where the fade starts, outer where it turns solid.
        // keep outer well under 1 so the ground mask's stepped rim is already hidden before the
        // clip boundary is reached -- the steps are what this is here to bury.
        _FogInner("Fog inner", Range(0, 1.2)) = 0.85
        _FogOuter("Fog outer", Range(0, 1.2)) = 0.95
        _FogMax("Fog max opacity", Range(0, 1)) = 1
    }

    SubShader
    {
        // over the opaques and the weather, under the vignette at Transparent+200. drop this
        // below the rain's queue if rain should stay visible against the fog.
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+150" "RenderPipeline" = "UniversalPipeline" }

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
                float3 nearWS : TEXCOORD0;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _FogColor;
                float _FogInner;
                float _FogOuter;
                float _FogMax;
            CBUFFER_END

            // set globally by MapFogBinder. xy is the grid centre on the world xz plane; the quad
            // is parented to the camera and never sees the grid's own transform.
            float4 _MapCenter;
            float _MapRadius;

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;
                // the quad's own transform is ignored: writing clip space directly means the fill
                // always covers the screen exactly, whatever the mesh is or where it sits
                float4 clipPos = float4(IN.uv * 2.0 - 1.0, UNITY_NEAR_CLIP_VALUE, 1.0);
                // bypassing the projection matrix also bypasses the y-flip Unity applies when a
                // camera renders into an intermediate target, so put it back by hand
                clipPos.y *= _ProjectionParams.x;
                OUT.positionHCS = clipPos;
                // unproject the position actually being rasterised, rather than rebuilding a screen
                // uv in the fragment: the clip position is known exactly here, so nothing depends
                // on which corner the uv origin happens to sit in
                float4 world = mul(UNITY_MATRIX_I_VP, clipPos);
                OUT.nearWS = world.xyz / world.w;
                return OUT;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                // walk from the near plane down to y=0. the camera is orthographic, so this
                // direction is the same for every pixel and the ground point is exact with no
                // depth buffer: a tank fades with the ground it stands on rather than with its
                // own height, which is what stops tall things tearing out of the fade.
                float3 forward = GetViewForwardDir();
                // the camera looks down, so forward.y is negative. clamping keeps it that way and
                // keeps a horizontal scene-view camera from dividing by zero.
                float travel = -IN.nearWS.y / min(forward.y, -1e-4);
                float2 ground = IN.nearWS.xz + forward.xz * travel;

                half fade = smoothstep(_FogInner * _MapRadius, _FogOuter * _MapRadius, length(ground - _MapCenter.xy));
                fade *= _FogMax;
                // the band is a fraction of the screen, so discarding the clear interior skips the
                // blend and its framebuffer traffic for most pixels
                clip(fade - 0.002);
                return half4(_FogColor.rgb, fade);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
