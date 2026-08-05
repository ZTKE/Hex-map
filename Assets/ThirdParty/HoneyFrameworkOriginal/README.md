# HoneyFramework original terrain art

This folder contains the terrain stamps and foreground artwork copied from the
project-local HoneyFramework reference at:

`D:/unity game hub/WW2_map_project/Assets/URP3D_HoneyFramework/HoneyFramework`

The original image files are retained as authored. Import metadata disables
sRGB sampling for numeric height/mixer masks and CPU readback for foreground
art, which is sampled only by the GPU.

## Runtime integration

- Dirt, plains, marsh, hill, mountain, and river use HF's original diffuse,
  height, and mixer stamps.
- Stamps overlap at HF's original 1.6-radius scale. The maximum mixer plus
  missing-strength rule removes hard hex borders.
- Height samples use the mip footprint equivalent to HF Oven's downsample and
  Gaussian pass. HF's fixed-direction offset-height shadow is intentionally
  disabled; URP's main light and shadow map provide the terrain lighting.
- Diffuse stamps are reconstructed per fragment, matching the resolution of
  HF's baked diffuse instead of interpolating colour across tessellation
  triangles.
- Terrain shape is reconstructed from map-wide logical cell textures. No
  per-cell or per-chunk height/diffuse render textures are allocated.
- HF foreground sprites are combined into one billboard mesh per chunk and
  read the same logical height in the vertex shader. Their original 0.495-0.75
  height eligibility, transparent blending, vertex tint gradient, atlas pivot,
  and back-to-front Z ordering are retained.
- Ocean rendering remains the project's existing implementation by design.

The pre-HF material textures remain available under `Assets/Materials/Terrain`
and `Assets/ThirdParty/HoneyFrameworkTerrain`; none were deleted or overwritten.
