#ifndef VOLUMETRIC_FOG_COMMON_INCLUDED
#define VOLUMETRIC_FOG_COMMON_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

// Must match VolumetricFogLight.MaxLights.
#define MAX_VOLUMETRIC_LIGHTS 8

CBUFFER_START(VolumetricFogParams)
    float4 _FogParams0;        // x: density, y: anisotropy, z: maxDistance, w: stepCount
    float4 _FogParams1;        // x: baseHeight, y: heightFalloff, z: mainLightContribution, w: additionalLightContribution
    float4 _FogParams2;        // x: noiseScale (1/metres), y: noiseIntensity, z: noiseEnabled, w: frameIndex
    float4 _FogParams3;        // x: surfaceClarity, y: 1/clarityDistance, z: highlightLimit
    float4 _FogTint;           // rgb: tint, w: scene darkening (0 = additive beams only)
    float4 _FogAmbient;        // rgb: ambient in-scatter
    float4 _FogWindOffset;     // xyz: accumulated wind offset in metres
    float4 _FogTexelSize;      // xy: 1/size of the fog buffer, zw: size
    int    _FogLightCount;
    float4 _FogLightPosRange[MAX_VOLUMETRIC_LIGHTS];   // xyz: position, w: 1/(range*range)
    float4 _FogLightColor[MAX_VOLUMETRIC_LIGHTS];      // rgb: colour*intensity
    float4 _FogLightSpotDir[MAX_VOLUMETRIC_LIGHTS];    // xyz: direction, w: 1 for spot, 0 for point
    float4 _FogLightSpotAngle[MAX_VOLUMETRIC_LIGHTS];  // x: cos(outer/2) scale, y: offset  (URP spot attenuation form)
CBUFFER_END

#define FOG_DENSITY            _FogParams0.x
#define FOG_ANISOTROPY         _FogParams0.y
#define FOG_MAX_DISTANCE       _FogParams0.z
#define FOG_STEP_COUNT         _FogParams0.w
#define FOG_BASE_HEIGHT        _FogParams1.x
#define FOG_HEIGHT_FALLOFF     _FogParams1.y
#define FOG_MAIN_LIGHT_SCALE   _FogParams1.z
#define FOG_ADD_LIGHT_SCALE    _FogParams1.w
#define FOG_NOISE_FREQ         _FogParams2.x
#define FOG_NOISE_INTENSITY    _FogParams2.y
#define FOG_NOISE_ENABLED      _FogParams2.z
#define FOG_FRAME_INDEX        _FogParams2.w
#define FOG_SURFACE_CLARITY    _FogParams3.x
#define FOG_INV_CLARITY_DIST   _FogParams3.y
#define FOG_HIGHLIGHT_LIMIT    _FogParams3.z

// ---------------------------------------------------------------------------------------------
// Noise
// ---------------------------------------------------------------------------------------------

// Interleaved gradient noise (Jimenez 2014) — temporally offset so TAA-less headsets still
// average the dither out across frames without any texture asset.
float InterleavedGradientNoise(float2 pixel, float frame)
{
    pixel += frame * float2(47.0, 17.0) * 0.695;
    const float3 magic = float3(0.06711056, 0.00583715, 52.9829189);
    return frac(magic.z * frac(dot(pixel, magic.xy)));
}

float Hash13(float3 p)
{
    p = frac(p * 0.1031);
    p += dot(p, p.zyx + 31.32);
    return frac((p.x + p.y) * p.z);
}

// Trilinear value noise in [0,1]; one octave is enough to break up a uniform medium.
float ValueNoise3D(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);

    float n000 = Hash13(i + float3(0, 0, 0));
    float n100 = Hash13(i + float3(1, 0, 0));
    float n010 = Hash13(i + float3(0, 1, 0));
    float n110 = Hash13(i + float3(1, 1, 0));
    float n001 = Hash13(i + float3(0, 0, 1));
    float n101 = Hash13(i + float3(1, 0, 1));
    float n011 = Hash13(i + float3(0, 1, 1));
    float n111 = Hash13(i + float3(1, 1, 1));

    float nx00 = lerp(n000, n100, f.x);
    float nx10 = lerp(n010, n110, f.x);
    float nx01 = lerp(n001, n101, f.x);
    float nx11 = lerp(n011, n111, f.x);
    float nxy0 = lerp(nx00, nx10, f.y);
    float nxy1 = lerp(nx01, nx11, f.y);
    return lerp(nxy0, nxy1, f.z);
}

// ---------------------------------------------------------------------------------------------
// Medium
// ---------------------------------------------------------------------------------------------

float FogDensityAt(float3 positionWS)
{
    float heightTerm = exp(-max(positionWS.y - FOG_BASE_HEIGHT, 0.0) * FOG_HEIGHT_FALLOFF);
    float density = FOG_DENSITY * heightTerm;

    if (FOG_NOISE_ENABLED > 0.5)
    {
        float n = ValueNoise3D((positionWS + _FogWindOffset.xyz) * FOG_NOISE_FREQ);
        density *= lerp(1.0, n, FOG_NOISE_INTENSITY);
    }
    return density;
}

float HenyeyGreenstein(float cosTheta, float g)
{
    float g2 = g * g;
    float denom = 1.0 + g2 - 2.0 * g * cosTheta;
    return (1.0 - g2) / (4.0 * PI * denom * sqrt(max(denom, 1e-4)));
}

// ---------------------------------------------------------------------------------------------
// Lights
// ---------------------------------------------------------------------------------------------

float SampleMainLightShadow(float3 positionWS)
{
#if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE)
    float4 shadowCoord = TransformWorldToShadowCoord(positionWS);
    return MainLightRealtimeShadow(shadowCoord);
#else
    return 1.0;
#endif
}

float3 MainLightInScatter(float3 positionWS, float3 viewDir)
{
    Light mainLight = GetMainLight();
    float shadow = SampleMainLightShadow(positionWS);
    float phase = HenyeyGreenstein(dot(viewDir, mainLight.direction), FOG_ANISOTROPY);
    return mainLight.color * (shadow * phase * FOG_MAIN_LIGHT_SCALE);
}

float3 AdditionalLightsInScatter(float3 positionWS, float3 viewDir)
{
    float3 result = 0.0;
    [loop]
    for (int i = 0; i < _FogLightCount; ++i)
    {
        float3 toLight = _FogLightPosRange[i].xyz - positionWS;
        float distSq = max(dot(toLight, toLight), 1e-4);
        float3 lightDir = toLight * rsqrt(distSq);

        // Same smooth range window URP uses so the fog falls off where the surfaces do.
        float rangeAtten = saturate(1.0 - distSq * _FogLightPosRange[i].w);
        rangeAtten *= rangeAtten;
        float atten = rangeAtten / distSq;

        float spotFactor = saturate(dot(_FogLightSpotDir[i].xyz, lightDir) * _FogLightSpotAngle[i].x + _FogLightSpotAngle[i].y);
        spotFactor *= spotFactor;
        atten *= lerp(1.0, spotFactor, _FogLightSpotDir[i].w);

        float phase = HenyeyGreenstein(dot(viewDir, lightDir), FOG_ANISOTROPY);
        result += _FogLightColor[i].rgb * (atten * phase);
    }
    return result * FOG_ADD_LIGHT_SCALE;
}

// ---------------------------------------------------------------------------------------------
// Depth helpers
// ---------------------------------------------------------------------------------------------

float LinearEyeDepthFromRaw(float rawDepth)
{
    #if UNITY_REVERSED_Z
        float d = rawDepth;
    #else
        float d = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, rawDepth);
    #endif
    return LinearEyeDepth(d, _ZBufferParams);
}

bool IsSkyDepth(float rawDepth)
{
    #if UNITY_REVERSED_Z
        return rawDepth <= 1e-6;
    #else
        return rawDepth >= 1.0 - 1e-6;
    #endif
}

#endif // VOLUMETRIC_FOG_COMMON_INCLUDED
