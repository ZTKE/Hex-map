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
- Terrain shape is reconstructed from map-wide logical cell textures. No
  per-cell or per-chunk height/diffuse render textures are allocated.
- HF foreground sprites are combined into one billboard mesh per chunk and
  read the same logical height in the vertex shader.
- Ocean rendering remains the project's existing implementation by design.

The pre-HF material textures remain available under `Assets/Materials/Terrain`
and `Assets/ThirdParty/HoneyFrameworkTerrain`; none were deleted or overwritten.
