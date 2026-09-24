# Sample: Beams Without Haze

Volume profile preset used for indoor VR scenes with baked lighting: the environment keeps its
lighting, lamps carrying `VolumetricFogLight` show visible cones, objects inside the cone stay sharp.

Import via Package Manager ▸ URP Volumetric Fog ▸ Samples ▸ Import, then drop
`BeamsWithoutHaze` onto a Global Volume's Profile field.

Values: Enabled 1, Density 0.1, Scene Darkening 0, Anisotropy 0.3, Surface Clarity 0.7,
Clarity Distance 1.0, Highlight Limit 1.0, Main Light Contribution 0,
Additional Light Contribution 8, Ambient black.

Create the profile asset in Unity (Create ▸ Volume Profile), add the Volumetric Fog override with these
values, and save it into this folder before tagging a release.
