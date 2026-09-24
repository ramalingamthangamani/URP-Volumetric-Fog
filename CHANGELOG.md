# Changelog

## [1.1.0] - 2026-09-24
- Added **Scene Darkening**: scale the fog's extinction independently of the beams (0 = additive beams only).
- Added **Surface Clarity** / **Clarity Distance**: thin the haze directly in front of surfaces so objects inside a beam stay readable.
- Added **Highlight Limit**: soft ceiling on beam brightness to stop white blow-out.
- Pass now sets `requiresIntermediateTexture`, fixing the effect silently skipping when URP rendered to the back buffer.
- Warn in the Console when the pass is skipped for that reason.

## [1.0.0] - 2026-09-24
- Initial release: RenderGraph raymarched fog for URP 17 / Unity 6.
- Directional light shadows (cascades + soft) drive light shafts.
- Opt-in point/spot lights via `VolumetricFogLight` (max 8, no shadows).
- Half/quarter resolution marching, bilateral blur, depth-aware upsample.
- XR single-pass instanced and multiview safe.
