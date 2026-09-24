using System.Collections.Generic;
using UnityEngine;

namespace VolumetricFog
{
    /// <summary>
    /// Opt-in marker for a point or spot light that should scatter into the fog.
    /// Lights are not picked up automatically: marching every light in the scene is what makes
    /// volumetrics unaffordable on a Quest, so each light pays its own way here.
    /// Shadows are not sampled for these lights, only the cone/range attenuation.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(Light))]
    [AddComponentMenu("Rendering/Volumetric Fog Light")]
    public sealed class VolumetricFogLight : MonoBehaviour
    {
        /// <summary>Hard cap that must match MAX_VOLUMETRIC_LIGHTS in VolumetricFogCommon.hlsl.</summary>
        public const int MaxLights = 8;

        static readonly List<VolumetricFogLight> s_Active = new List<VolumetricFogLight>(MaxLights);

        public static IReadOnlyList<VolumetricFogLight> Active => s_Active;

        [Tooltip("Multiplies this light's scattering contribution, on top of the light's own intensity.")]
        [Min(0f)] public float intensityMultiplier = 1.0f;

        [Tooltip("Higher priority lights win the limited light slots when more than eight are visible.")]
        public int priority;

        Light m_Light;

        /// <summary>The Light this component drives. Cached, because the raymarch reads it every frame.</summary>
        public Light Light
        {
            get
            {
                if (m_Light == null)
                    m_Light = GetComponent<Light>();
                return m_Light;
            }
        }

        void OnEnable()
        {
            if (!s_Active.Contains(this))
                s_Active.Add(this);
        }

        void OnDisable()
        {
            s_Active.Remove(this);
        }
    }
}
