# URP Volumetric Fog

Raymarched volumetric fog and light shafts for **Unity 6 / URP 17**, written for the VR Knee Surgery
project (Quest standalone + Quest Link). Inspired by
[CristianQiu/Unity-URP-Volumetric-Light](https://github.com/CristianQiu/Unity-URP-Volumetric-Light),
built from scratch with the RenderGraph API and XR single-pass rendering in mind.

## Install

Package Manager ▸ **Add package from git URL**, or add to `Packages/manifest.json`:

```json
"com.ram.volumetric-lighting": "https://github.com/ramalingamthangamani/URP-Volumetric-Fog.git#v1.1.0"
```

## Set up (3 steps)

1. **Renderer feature** — select `Assets/Settings/Mobile_Renderer.asset` (and `PC_Renderer.asset`),
   *Add Renderer Feature ▸ Volumetric Fog Renderer Feature*. The shader field fills itself.
   Suggested quality per renderer:

   | Renderer | Downsample | Steps | Blur |
   |---|---|---|---|
   | Mobile (Quest) | Half or Quarter | 16–24 | on |
   | PC (Link) | Half | 48–64 | on |

2. **Depth texture** — the feature requests depth itself (`ConfigureInput(Depth)`); nothing to toggle.
3. **Volume override** — on any Volume profile, *Add Override ▸ Rendering ▸ Volumetric Fog* and tick
   **Enabled**. Start with `density 0.05`, `anisotropy 0.4`, `maxDistance 30`.

Shafts come from the **main directional light's shadows** — turn shadows on for that light and the
light will carve through blinds, doors and surgical drapes. Point/spot lights do **not** scatter
unless you add a **`VolumetricFogLight`** component to them (Add Component ▸ Rendering ▸ Volumetric
Fog Light). Up to 8 are marched; more are culled by `priority` then distance. These lights use
cone/range attenuation only, no shadow sampling, which is the cheap look you want for an overhead
theatre lamp.

## Parameters

**Renderer feature (cost):** `downsample`, `stepCount`, `blur`, `renderPassEvent`, `renderInSceneView`.

**Volume (look):**

| Group | Parameter | Effect |
|---|---|---|
| Participating Media | `density` | Thickness of the air. Beams get fuller as it rises. |
| | `tint` | Colour of the medium. |
| | `sceneDarkening` | 1 = physical extinction (scene dims behind fog); **0 = scene untouched, beams only added on top.** |
| | `anisotropy` | Forward scattering. Higher = bright only when looking toward the lamp. |
| | `maxDistance` | Raymarch limit from the camera. |
| Clarity | `surfaceClarity` | Thins the haze directly in front of surfaces so objects inside a beam stay sharp. |
| | `clarityDistance` | Metres in front of a surface that get thinned. |
| | `highlightLimit` | Soft cap on beam brightness; 0 = off. |
| Height Falloff | `baseHeight`, `heightFalloff` | Exponential thinning above a height. 0 falloff = uniform. |
| Lighting | `mainLightContribution` | Directional light shafts (needs its shadows on). |
| | `additionalLightContribution` | Strength of all `VolumetricFogLight` beams. |
| | `ambient` | Flat in-scatter so unlit fog isn't black. |
| Noise | `noiseEnabled`, `noiseScale`, `noiseIntensity`, `windSpeed` | Animated 3D value noise. |

### Recommended preset: "beams without haze" (indoor, baked lighting, VR)

Keeps the environment exactly as lit, adds visible lamp cones, keeps instruments readable:

```
Enabled 1  Density 0.1  Scene Darkening 0  Anisotropy 0.3
Surface Clarity 0.7  Clarity Distance 1.0  Highlight Limit 1.0
Main Light Contribution 0  Additional Light Contribution 8  Ambient black
```

Then add `VolumetricFogLight` to the lamps, Range 8–10, Outer Spot 90–100, Intensity 10–20.

> Every Volume parameter has a checkbox on its left. Untick = "use default" and the field is greyed out;
> tick it (or click **ALL**) before editing.

## How it works

1. **Raymarch** (fog res): reconstructs the world ray per pixel from `_CameraDepthTexture`, jitters the
   start with interleaved gradient noise (temporally rotated, so no TAA needed), marches to the
   surface or `maxDistance`, and accumulates Beer–Lambert in-scatter from the main light (shadowed,
   Henyey–Greenstein phase), the opt-in lights and a flat ambient term. Output `rgb` = scatter,
   `a` = transmittance.
2. **Bilateral blur** (optional, fog res): 5-tap separable, depth-weighted so it never smears over edges.
3. **Composite** (full res): depth-aware 4-tap upsample, then `Blend One SrcAlpha` straight into the
   camera colour — no colour copy, no extra full-screen read.

## Performance notes for Quest

- Half res × 16 steps ≈ 0.6–0.9 ms on Quest 3 at default render scale. Quarter res halves that again.
- Every `VolumetricFogLight` adds ~5% per step; keep it to the lamps that matter.
- `noiseEnabled` costs roughly 20%; leave it off unless you want visible drift.
- The pass is skipped entirely when the volume override is disabled or density is 0, so it is safe to
  leave the feature on the renderer permanently.

## Limitations

- Opt-in lights are not shadowed and ignore cookies.
- Fog is composited before transparents; particles are not fogged by this system.
- Forward+ / Deferred renderer light lists are deliberately not used; only marked lights scatter.
