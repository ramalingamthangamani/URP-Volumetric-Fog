Shader "Hidden/VolumetricFog/VolumetricFog"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off

        HLSLINCLUDE
        #pragma target 3.5
        #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
        #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
        #pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON

        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
        #include "VolumetricFogCommon.hlsl"

        // Depth-aware separable blur, run at fog resolution.
        float4 BilateralBlur(Varyings input, float2 dir)
        {
            float2 uv = input.texcoord;
            float centerDepth = LinearEyeDepthFromRaw(SampleSceneDepth(uv));
            float4 center = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv, 0);

            const float kernel[3] = { 0.375, 0.25, 0.0625 };  // 1-4-6-4-1 halved
            float4 sum = center * kernel[0];
            float weightSum = kernel[0];

            [unroll]
            for (int k = 1; k <= 2; ++k)
            {
                float2 offset = dir * _FogTexelSize.xy * k;

                [unroll]
                for (int s = -1; s <= 1; s += 2)
                {
                    float2 sampleUV = uv + offset * s;
                    float sampleDepth = LinearEyeDepthFromRaw(SampleSceneDepth(sampleUV));
                    float depthWeight = saturate(1.0 - abs(sampleDepth - centerDepth) / max(centerDepth * 0.1, 0.05));
                    float w = kernel[k] * depthWeight;
                    sum += SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, sampleUV, 0) * w;
                    weightSum += w;
                }
            }
            return sum / max(weightSum, 1e-4);
        }
        ENDHLSL

        // -------------------------------------------------------------------------------------
        // Pass 0: raymarch at reduced resolution. rgb = in-scattered light, a = transmittance.
        // -------------------------------------------------------------------------------------
        Pass
        {
            Name "Volumetric Fog Raymarch"
            Blend Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragRaymarch

            float4 FragRaymarch(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;

                float rawDepth = SampleSceneDepth(uv);
                float sceneEye = LinearEyeDepthFromRaw(rawDepth);

                // Reconstruct the ray through this pixel. The far-plane point gives the direction even for sky.
                float3 farWS = ComputeWorldSpacePosition(uv, UNITY_RAW_FAR_CLIP_VALUE, UNITY_MATRIX_I_VP);
                float3 camWS = GetCurrentViewPosition(); // per-eye in XR
                float3 rayDir = farWS - camWS;
                float rayLen = length(rayDir);
                rayDir /= rayLen;

                // Convert the eye depth of the surface into a distance along this off-axis ray.
                float cosToForward = abs(dot(rayDir, -UNITY_MATRIX_V[2].xyz));
                float surfaceDist = IsSkyDepth(rawDepth) ? FOG_MAX_DISTANCE : sceneEye / max(cosToForward, 1e-3);
                float marchEnd = min(surfaceDist, FOG_MAX_DISTANCE);

                int steps = (int)FOG_STEP_COUNT;
                float stepLen = marchEnd / steps;
                float jitter = InterleavedGradientNoise(uv * _FogTexelSize.zw, FOG_FRAME_INDEX);

                float3 viewDir = -rayDir;
                float3 scatter = 0.0;
                float transmittance = 1.0;
                float t = (jitter + 0.5) * stepLen;

                [loop]
                for (int i = 0; i < steps; ++i)
                {
                    float3 p = camWS + rayDir * t;
                    float sigma = FogDensityAt(p);

                    if (sigma > 1e-5)
                    {
                        float3 light = MainLightInScatter(p, viewDir)
                                     + AdditionalLightsInScatter(p, viewDir)
                                     + _FogAmbient.rgb;

                        // Thin the haze that hangs right in front of the hit surface so objects stay readable.
                        // Rays that end on the sky or at max distance are left untouched.
                        float clarity = 1.0;
                        if (!IsSkyDepth(rawDepth))
                        {
                            float toSurface = saturate((surfaceDist - t) * FOG_INV_CLARITY_DIST);
                            clarity = lerp(1.0, toSurface, FOG_SURFACE_CLARITY);
                        }

                        // Analytic integration of the in-scatter over the step (Beer-Lambert).
                        float stepT = exp(-sigma * stepLen);
                        float3 integ = light * _FogTint.rgb * (1.0 - stepT) * clarity;
                        scatter += transmittance * integ;
                        transmittance *= stepT;

                        if (transmittance < 0.005)
                            break;
                    }
                    t += stepLen;
                }

                // Soft ceiling so a bright lamp reads as a beam, not a white smear.
                if (FOG_HIGHLIGHT_LIMIT > 0.0)
                    scatter = scatter / (1.0 + scatter / FOG_HIGHLIGHT_LIMIT);

                return float4(scatter, transmittance);
            }
            ENDHLSL
        }

        // -------------------------------------------------------------------------------------
        // Pass 1 / 2: bilateral blur, horizontal then vertical.
        // -------------------------------------------------------------------------------------
        Pass
        {
            Name "Volumetric Fog Blur Horizontal"
            Blend Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragBlurH
            float4 FragBlurH(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return BilateralBlur(input, float2(1, 0));
            }
            ENDHLSL
        }

        Pass
        {
            Name "Volumetric Fog Blur Vertical"
            Blend Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragBlurV
            float4 FragBlurV(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return BilateralBlur(input, float2(0, 1));
            }
            ENDHLSL
        }

        // -------------------------------------------------------------------------------------
        // Pass 3: composite into the camera colour. Blend One SrcAlpha gives
        // colour * transmittance + scatter without ever reading the camera target.
        // -------------------------------------------------------------------------------------
        Pass
        {
            Name "Volumetric Fog Composite"
            Blend One SrcAlpha

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragComposite

            float4 FragComposite(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;

                // Depth-aware upsample: 4 low-res taps weighted by how well their depth matches the
                // full-res pixel. This keeps fog from bleeding across geometry edges.
                float fullDepth = LinearEyeDepthFromRaw(SampleSceneDepth(uv));

                float2 lowTexel = _FogTexelSize.xy;
                float2 lowUV = uv * _FogTexelSize.zw - 0.5;
                float2 base = (floor(lowUV) + 0.5) * lowTexel;
                float2 f = frac(lowUV);

                float2 offsets[4] = { float2(0, 0), float2(1, 0), float2(0, 1), float2(1, 1) };
                float bilinear[4] = { (1 - f.x) * (1 - f.y), f.x * (1 - f.y), (1 - f.x) * f.y, f.x * f.y };

                float4 sum = 0.0;
                float weightSum = 0.0;
                float4 fallback = 0.0;
                float bestWeight = -1.0;

                [unroll]
                for (int i = 0; i < 4; ++i)
                {
                    float2 sUV = base + offsets[i] * lowTexel;
                    float lowDepth = LinearEyeDepthFromRaw(SampleSceneDepth(sUV));
                    float depthWeight = saturate(1.0 - abs(lowDepth - fullDepth) / max(fullDepth * 0.08, 0.05));
                    float4 fog = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_PointClamp, sUV, 0);
                    float w = bilinear[i] * depthWeight;
                    sum += fog * w;
                    weightSum += w;
                    if (depthWeight > bestWeight) { bestWeight = depthWeight; fallback = fog; }
                }

                float4 result = weightSum > 1e-3 ? sum / weightSum : fallback;
                // Scene darkening = 1 keeps physical extinction; 0 leaves the scene as-is and adds beams only.
                float transmittance = lerp(1.0, saturate(result.a), _FogTint.w);
                return float4(result.rgb, transmittance);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
