using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using UnityEngine.Experimental.Rendering;

namespace VolumetricFog
{
    /// <summary>
    /// Raymarched volumetric fog for URP 17. Add this feature to the renderer, then drive the look
    /// from a <see cref="VolumetricFogVolumeComponent"/> override on a Volume. Quality knobs live
    /// here because they change GPU cost and should be set per renderer (Mobile vs PC).
    /// RenderGraph only; XR single-pass instanced is handled by the URP blit helpers.
    /// </summary>
    [DisallowMultipleRendererFeature("Volumetric Fog")]
    [Tooltip("Raymarched volumetric fog with directional light shadows and opt-in point/spot lights.")]
    public sealed class VolumetricFogRendererFeature : ScriptableRendererFeature
    {
        public enum Downsample { Full = 1, Half = 2, Quarter = 4 }

        [Serializable]
        public sealed class Settings
        {
            [Tooltip("Where the fog is injected. Before transparents is right for almost everything.")]
            public RenderPassEvent renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;

            [Tooltip("Resolution divider for the raymarch buffer. Half is the sweet spot; Quarter for Quest if the frame is tight.")]
            public Downsample downsample = Downsample.Half;

            [Tooltip("Raymarch steps per pixel. 16 to 24 for Quest, 48 to 64 for PC.")]
            [Range(4, 128)] public int stepCount = 24;

            [Tooltip("Runs a depth-aware 5-tap blur at fog resolution before compositing. Hides the dither for very few extra passes.")]
            public bool blur = true;

            [Tooltip("Skip the effect in the scene view. Turn on when tuning shafts by eye.")]
            public bool renderInSceneView = true;

            [Tooltip("Shader used by the passes. Filled in automatically; only override when you ship a modified copy.")]
            public Shader shader;
        }

        const string k_ShaderName = "Hidden/VolumetricFog/VolumetricFog";

        public Settings settings = new Settings();

        Material m_Material;
        VolumetricFogPass m_Pass;

        public override void Create()
        {
#if UNITY_EDITOR
            if (settings.shader == null)
                settings.shader = Shader.Find(k_ShaderName);
#endif
            m_Pass = new VolumetricFogPass(name);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            var cameraType = renderingData.cameraData.cameraType;
            if (cameraType == CameraType.Preview || cameraType == CameraType.Reflection)
                return;
            if (cameraType == CameraType.SceneView && !settings.renderInSceneView)
                return;

            var volume = VolumeManager.instance.stack.GetComponent<VolumetricFogVolumeComponent>();
            if (volume == null || !volume.IsActive())
                return;

            if (!EnsureMaterial())
                return;

            m_Pass.Setup(settings, m_Material, volume);
            m_Pass.renderPassEvent = settings.renderPassEvent;
            // We blend into the camera colour, so URP must not render straight to the back buffer.
            m_Pass.requiresIntermediateTexture = true;
            m_Pass.ConfigureInput(ScriptableRenderPassInput.Depth);
            renderer.EnqueuePass(m_Pass);
        }

        bool EnsureMaterial()
        {
            if (settings.shader == null)
            {
                settings.shader = Shader.Find(k_ShaderName);
                if (settings.shader == null)
                {
                    Debug.LogWarning($"[VolumetricFog] Shader '{k_ShaderName}' not found. Assign it on the renderer feature.");
                    return false;
                }
            }

            if (m_Material == null || m_Material.shader != settings.shader)
            {
                CoreUtils.Destroy(m_Material);
                m_Material = CoreUtils.CreateEngineMaterial(settings.shader);
            }
            return m_Material != null;
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(m_Material);
            m_Material = null;
        }

        // -----------------------------------------------------------------------------------------

        sealed class VolumetricFogPass : ScriptableRenderPass
        {
            const int k_PassRaymarch = 0;
            const int k_PassBlurH = 1;
            const int k_PassBlurV = 2;
            const int k_PassComposite = 3;

            static class ShaderIDs
            {
                public static readonly int FogParams0 = Shader.PropertyToID("_FogParams0");
                public static readonly int FogParams1 = Shader.PropertyToID("_FogParams1");
                public static readonly int FogParams2 = Shader.PropertyToID("_FogParams2");
                public static readonly int FogParams3 = Shader.PropertyToID("_FogParams3");
                public static readonly int FogTint = Shader.PropertyToID("_FogTint");
                public static readonly int FogAmbient = Shader.PropertyToID("_FogAmbient");
                public static readonly int FogWindOffset = Shader.PropertyToID("_FogWindOffset");
                public static readonly int FogTexelSize = Shader.PropertyToID("_FogTexelSize");
                public static readonly int FogLightCount = Shader.PropertyToID("_FogLightCount");
                public static readonly int FogLightPosRange = Shader.PropertyToID("_FogLightPosRange");
                public static readonly int FogLightColor = Shader.PropertyToID("_FogLightColor");
                public static readonly int FogLightSpotDir = Shader.PropertyToID("_FogLightSpotDir");
                public static readonly int FogLightSpotAngle = Shader.PropertyToID("_FogLightSpotAngle");
            }

            class PassData
            {
                public Material material;
                public int passIndex;
                public TextureHandle source;
            }

            readonly ProfilingSampler m_Sampler;
            Settings m_Settings;
            Material m_Material;
            VolumetricFogVolumeComponent m_Volume;

            // Wind accumulates so changing speed at runtime never snaps the noise field.
            Vector3 m_WindOffset;
            float m_LastTime;
            int m_FrameIndex;

            readonly Vector4[] m_LightPosRange = new Vector4[VolumetricFogLight.MaxLights];
            readonly Vector4[] m_LightColor = new Vector4[VolumetricFogLight.MaxLights];
            readonly Vector4[] m_LightSpotDir = new Vector4[VolumetricFogLight.MaxLights];
            readonly Vector4[] m_LightSpotAngle = new Vector4[VolumetricFogLight.MaxLights];
            readonly VolumetricFogLight[] m_SortedLights = new VolumetricFogLight[64];

            public VolumetricFogPass(string featureName)
            {
                m_Sampler = new ProfilingSampler(featureName);
                profilingSampler = m_Sampler;
            }

            public void Setup(Settings settings, Material material, VolumetricFogVolumeComponent volume)
            {
                m_Settings = settings;
                m_Material = material;
                m_Volume = volume;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resourceData = frameData.Get<UniversalResourceData>();
                var cameraData = frameData.Get<UniversalCameraData>();

                if (resourceData.isActiveTargetBackBuffer)
                {
                    Debug.LogWarning("[VolumetricFog] Active target is the back buffer; pass skipped. Enable an intermediate texture on the renderer.");
                    return;
                }

                UpdateMaterialParams(cameraData);

                var desc = cameraData.cameraTargetDescriptor;
                int div = (int)m_Settings.downsample;
                desc.width = Mathf.Max(1, desc.width / div);
                desc.height = Mathf.Max(1, desc.height / div);
                desc.msaaSamples = 1;
                desc.depthBufferBits = 0;
                desc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                desc.useMipMap = false;

                m_Material.SetVector(ShaderIDs.FogTexelSize,
                    new Vector4(1f / desc.width, 1f / desc.height, desc.width, desc.height));

                TextureHandle fogA = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "_VolumetricFogA", false, FilterMode.Bilinear);
                TextureHandle depth = resourceData.cameraDepthTexture;

                // Raymarch reads only depth; the blit source is just there to satisfy the helper.
                AddBlitPass(renderGraph, "Volumetric Fog Raymarch", depth, fogA, k_PassRaymarch);

                TextureHandle fogFinal = fogA;
                if (m_Settings.blur)
                {
                    TextureHandle fogB = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "_VolumetricFogB", false, FilterMode.Bilinear);
                    AddBlitPass(renderGraph, "Volumetric Fog Blur H", fogA, fogB, k_PassBlurH);
                    AddBlitPass(renderGraph, "Volumetric Fog Blur V", fogB, fogA, k_PassBlurV);
                    fogFinal = fogA;
                }

                AddBlitPass(renderGraph, "Volumetric Fog Composite", fogFinal, resourceData.activeColorTexture, k_PassComposite);
            }

            void AddBlitPass(RenderGraph renderGraph, string passName, TextureHandle source, TextureHandle destination, int passIndex)
            {
                using (var builder = renderGraph.AddRasterRenderPass<PassData>(passName, out var passData, m_Sampler))
                {
                    passData.material = m_Material;
                    passData.passIndex = passIndex;
                    passData.source = source;

                    builder.UseTexture(source, AccessFlags.Read);
                    builder.SetRenderAttachment(destination, 0, AccessFlags.ReadWrite);
                    // Every pass reads _CameraDepthTexture and the main shadow map via URP's globals.
                    builder.UseAllGlobalTextures(true);
                    builder.AllowPassCulling(false);

                    builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
                    {
                        Blitter.BlitTexture(ctx.cmd, data.source, new Vector4(1f, 1f, 0f, 0f), data.material, data.passIndex);
                    });
                }
            }

            void UpdateMaterialParams(UniversalCameraData cameraData)
            {
                var v = m_Volume;

                float now = Time.realtimeSinceStartup;
                float dt = m_LastTime > 0f ? Mathf.Clamp(now - m_LastTime, 0f, 0.1f) : 0f;
                m_LastTime = now;
                m_WindOffset += v.windSpeed.value * dt;
                m_FrameIndex = (m_FrameIndex + 1) & 63;

                m_Material.SetVector(ShaderIDs.FogParams0, new Vector4(
                    v.density.value, v.anisotropy.value, v.maxDistance.value, m_Settings.stepCount));
                m_Material.SetVector(ShaderIDs.FogParams1, new Vector4(
                    v.baseHeight.value, v.heightFalloff.value, v.mainLightContribution.value, v.additionalLightContribution.value));
                m_Material.SetVector(ShaderIDs.FogParams2, new Vector4(
                    1f / Mathf.Max(v.noiseScale.value, 0.01f), v.noiseIntensity.value, v.noiseEnabled.value ? 1f : 0f, m_FrameIndex));
                m_Material.SetVector(ShaderIDs.FogParams3, new Vector4(
                    v.surfaceClarity.value, 1f / Mathf.Max(v.clarityDistance.value, 0.05f), v.highlightLimit.value, 0f));
                m_Material.SetVector(ShaderIDs.FogTint, new Vector4(v.tint.value.r, v.tint.value.g, v.tint.value.b, v.sceneDarkening.value));
                m_Material.SetVector(ShaderIDs.FogAmbient, v.ambient.value);
                m_Material.SetVector(ShaderIDs.FogWindOffset, m_WindOffset);

                UploadLights(cameraData.camera);
            }

            void UploadLights(Camera camera)
            {
                var active = VolumetricFogLight.Active;
                int candidates = 0;
                for (int i = 0; i < active.Count && candidates < m_SortedLights.Length; i++)
                {
                    var fl = active[i];
                    var l = fl != null ? fl.Light : null;
                    if (l == null || !l.enabled || !fl.isActiveAndEnabled || l.intensity <= 0f)
                        continue;
                    if (l.type != LightType.Point && l.type != LightType.Spot)
                        continue;
                    if ((camera.cullingMask & (1 << l.gameObject.layer)) == 0)
                        continue;
                    m_SortedLights[candidates++] = fl;
                }

                // Priority first, then nearest to the camera, so a crowded scene degrades predictably.
                Vector3 camPos = camera.transform.position;
                Array.Sort(m_SortedLights, 0, candidates, Comparer<VolumetricFogLight>.Create((a, b) =>
                {
                    int p = b.priority.CompareTo(a.priority);
                    if (p != 0) return p;
                    float da = (a.transform.position - camPos).sqrMagnitude;
                    float db = (b.transform.position - camPos).sqrMagnitude;
                    return da.CompareTo(db);
                }));

                int count = Mathf.Min(candidates, VolumetricFogLight.MaxLights);
                for (int i = 0; i < count; i++)
                {
                    var fl = m_SortedLights[i];
                    var l = fl.Light;
                    var t = l.transform;

                    float range = Mathf.Max(l.range, 0.01f);
                    m_LightPosRange[i] = new Vector4(t.position.x, t.position.y, t.position.z, 1f / (range * range));

                    Color c = l.color.linear * (l.intensity * fl.intensityMultiplier);
                    m_LightColor[i] = new Vector4(c.r, c.g, c.b, 0f);

                    if (l.type == LightType.Spot)
                    {
                        // Same angle attenuation URP uses: smoothstep between inner and outer cone.
                        float cosOuter = Mathf.Cos(Mathf.Deg2Rad * l.spotAngle * 0.5f);
                        float cosInner = Mathf.Cos(Mathf.Deg2Rad * l.innerSpotAngle * 0.5f);
                        float invRange = 1f / Mathf.Max(cosInner - cosOuter, 1e-4f);
                        Vector3 dir = -t.forward;
                        m_LightSpotDir[i] = new Vector4(dir.x, dir.y, dir.z, 1f);
                        m_LightSpotAngle[i] = new Vector4(invRange, -cosOuter * invRange, 0f, 0f);
                    }
                    else
                    {
                        m_LightSpotDir[i] = new Vector4(0f, 1f, 0f, 0f);
                        m_LightSpotAngle[i] = new Vector4(0f, 1f, 0f, 0f);
                    }
                }
                for (int i = 0; i < candidates; i++) m_SortedLights[i] = null;

                m_Material.SetInteger(ShaderIDs.FogLightCount, count);
                if (count > 0)
                {
                    m_Material.SetVectorArray(ShaderIDs.FogLightPosRange, m_LightPosRange);
                    m_Material.SetVectorArray(ShaderIDs.FogLightColor, m_LightColor);
                    m_Material.SetVectorArray(ShaderIDs.FogLightSpotDir, m_LightSpotDir);
                    m_Material.SetVectorArray(ShaderIDs.FogLightSpotAngle, m_LightSpotAngle);
                }
            }
        }
    }
}
