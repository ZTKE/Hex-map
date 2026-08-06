# Runtime code layout

The folders below describe responsibilities only. The project intentionally
uses one Unity runtime assembly for now, so moving a type between these folders
does not change serialization or assembly boundaries.

- `Core`: dependency-light hex math, coordinates, flags, value types, and
  reusable helpers.
- `Map`: map data orchestration, chunk lifecycle, generation, path search, and
  camera movement.
- `Terrain`: shared mesh and shader-data infrastructure plus terrain style
  configuration.
- `Terrain/HF`: the HoneyFramework surface implementation: rendered relief,
  coastline, foreground, CPU height sampling, and collision.
- `Gameplay`: units and placed map features.
- `UI`: map editor and save/load presentation code.

## HexGridChunk files

`HexGridChunk` remains one Unity component, split into partial files by concern:

- `HexGridChunk.cs`: serialized fields, lifecycle, surface-mode selection, and
  per-cell dispatch.
- `HexGridChunk.Water.cs`: HF ocean coverage and legacy water/shore/estuary
  triangulation.
- `HexGridChunk.RoadsAndRivers.cs`: shared road and river overlay topology.
- `HexGridChunk.LegacyTopology.cs`: Catlike connection, terrace, cliff, and
  corner triangulation retained as the compatibility/topology source.

## Surface ownership rule

`HexSurfaceMode` selects exactly one visible and interactive terrain authority.
In `HFOriginal`, rendering, collision, roads, rivers, objects, units, and edit
UI all sample the HF surface. Catlike terrain triangulation may still provide
logical topology, but its terrain renderer and collider must remain disabled.

Do not introduce a silent fallback from `HFOriginal` to Catlike when an HF
texture is missing. Report the invalid style and keep the ownership decision
explicit.
