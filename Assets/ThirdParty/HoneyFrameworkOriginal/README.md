# HoneyFramework original terrain art

This folder contains the terrain stamps and foreground artwork copied from the
project-local HoneyFramework reference at:

`D:/unity game hub/WW2_map_project/Assets/URP3D_HoneyFramework/HoneyFramework`

The complete source project is also available inside this repository as the
`References/WW2_map_project` Git submodule. Use its
`Assets/URP3D_HoneyFramework/HoneyFramework` directory when checking the
original implementation rather than inferring behavior from the copied art.

The original image files are retained as authored. Import metadata disables
sRGB sampling for numeric height/mixer masks and CPU readback for foreground
art, which is sampled only by the GPU.

## Runtime integration

- Dirt, plains, marsh, hill, mountain, river, and sea use HF's original
  diffuse, height, and mixer stamps. Sea uses HF's Sand1_d / Water_h / Water_m
  triplet.
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
- Land and seabed are one continuous HF-reconstructed surface. A horizontal
  water plane intersects that surface, matching HF's coast construction and
  replacing Catlike's straight shared-edge shore and cliff strips.
- Exact HF mode keeps the legacy Catlike mesh for collision and cell data but
  disables its renderer. Drawing it together with the displaced HF surface
  creates camera-dependent depth intersections that appear as tan polygons.
- The water plane keeps this project's existing deep/shallow palette, animated
  waves, transparency, and foam. Its narrow shore contour is derived from the
  same reconstructed height used by the opaque terrain, so art and geometry do
  not drift apart.
- The transparent water plane does not receive terrain shadows, and submerged
  HF floor pixels ignore shadow-map attenuation. This prevents the same land
  shadow from being composited twice as a dark ring around the coast; dry land
  and mountains still use normal URP lighting and realtime shadows.
- One spare logical-shape bit marks cells that touch the opposite water state.
  Only those coast patches keep a stable tessellation floor at distance; deep
  sea and inland terrain retain the normal camera-distance LOD. Strong Sea
  ownership also receives a small below-water safety margin so coarse triangles
  cannot expose Sand1_d as temporary camera-dependent islands.
- Seamlessly repositioned chunk columns resolve their shifted X coordinate back
  into the logical map range before constructing a linear cell index. Without
  this step, a +/- map-width X shift carries into the Z row and makes the water
  mask read the previous or next row while the HF terrain reads the correct one.

The pre-HF material textures remain available under `Assets/Materials/Terrain`
and `Assets/ThirdParty/HoneyFrameworkTerrain`; none were deleted or overwritten.
