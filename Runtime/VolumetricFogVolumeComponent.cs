using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace VolumetricFog
{
    /// <summary>
    /// Per-scene look controls for the volumetric fog. Drop this override on any URP Volume
    /// profile; the renderer feature owns the quality/cost knobs instead.
    /// </summary>
    [Serializable, VolumeComponentMenu("Rendering/Volumetric Fog")]
    [SupportedOnRenderPipeline(typeof(UniversalRenderPipelineAsset))]
    public sealed class VolumetricFogVolumeComponent : VolumeComponent, IPostProcessComponent
    {
        [Header("Participating Media")]
        [Tooltip("Turns the whole effect on. The pass is skipped entirely when this is off.")]
        public BoolParameter enabled = new BoolParameter(false);

        [Tooltip("Scattering coefficient of the medium, per metre. Small numbers go a long way.")]
        public ClampedFloatParameter density = new ClampedFloatParameter(0.06f, 0.0f, 2.0f);

        [Tooltip("Colour of the medium. Multiplies every scattering event.")]
        public ColorParameter tint = new ColorParameter(Color.white, true, false, true);

        [Tooltip("How much the fog darkens the scene behind it. 1 is physically correct extinction; 0 keeps the scene untouched and only adds the light beams on top.")]
        public ClampedFloatParameter sceneDarkening = new ClampedFloatParameter(1.0f, 0.0f, 1.0f);

        [Tooltip("Henyey-Greenstein anisotropy. Positive scatters forward (haze around lights), negative scatters back.")]
        public ClampedFloatParameter anisotropy = new ClampedFloatParameter(0.35f, -0.95f, 0.95f);

        [Tooltip("How far the raymarch travels before it gives up, in metres.")]
        public MinFloatParameter maxDistance = new MinFloatParameter(48.0f, 1.0f);

        [Header("Clarity")]
        [Tooltip("Thins the haze that sits directly in front of surfaces so objects inside a beam stay readable. 0 = physically uniform, 1 = no haze on surfaces, beam only in open air.")]
        public ClampedFloatParameter surfaceClarity = new ClampedFloatParameter(0.0f, 0.0f, 1.0f);

        [Tooltip("Distance in metres in front of a surface over which the haze is thinned. Larger clears more of the space around objects.")]
        public ClampedFloatParameter clarityDistance = new ClampedFloatParameter(1.0f, 0.05f, 10.0f);

        [Tooltip("Soft ceiling on beam brightness. Lower values stop bright lamps from blowing out to white; 0 disables the limit.")]
        public ClampedFloatParameter highlightLimit = new ClampedFloatParameter(0.0f, 0.0f, 4.0f);

        [Header("Height Falloff")]
        [Tooltip("World height at which the fog reaches full density.")]
        public FloatParameter baseHeight = new FloatParameter(0.0f);

        [Tooltip("Exponential thinning above the base height. 0 gives a uniform, unbounded medium.")]
        public ClampedFloatParameter heightFalloff = new ClampedFloatParameter(0.25f, 0.0f, 2.0f);

        [Header("Lighting")]
        [Tooltip("Scales the directional light's contribution to in-scattering. This is what makes the shafts.")]
        public MinFloatParameter mainLightContribution = new MinFloatParameter(1.0f, 0.0f);

        [Tooltip("Scales the contribution of lights carrying a VolumetricFogLight component.")]
        public MinFloatParameter additionalLightContribution = new MinFloatParameter(1.0f, 0.0f);

        [Tooltip("Flat ambient term added to the medium so unlit fog does not read as black.")]
        public ColorParameter ambient = new ColorParameter(new Color(0.03f, 0.035f, 0.045f, 1f), true, false, true);

        [Header("Noise")]
        [Tooltip("Breaks the medium up with animated 3D value noise. Costs roughly 20% of the pass.")]
        public BoolParameter noiseEnabled = new BoolParameter(false);

        [Tooltip("Size of one noise cell in metres. Larger is smoother.")]
        public MinFloatParameter noiseScale = new MinFloatParameter(8.0f, 0.01f);

        [Tooltip("How much the noise modulates density. 1 lets the medium thin out to nothing.")]
        public ClampedFloatParameter noiseIntensity = new ClampedFloatParameter(0.5f, 0.0f, 1.0f);

        [Tooltip("Metres per second the noise field drifts.")]
        public Vector3Parameter windSpeed = new Vector3Parameter(new Vector3(0.2f, 0.0f, 0.1f));

        /// <summary>Kept for the IPostProcessComponent contract; the feature checks this too.</summary>
        public bool IsActive() => enabled.value && density.value > 0.0f && maxDistance.value > 0.0f;

        public bool IsTileCompatible() => false;
    }
}
