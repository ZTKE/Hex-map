# Project context

This Unity project is developed together with two companion projects. Treat them as part of the available technical context; the user should not need to mention them again.

## Companion projects

- `D:\unity game hub\WW2_map_project`
  - Reference/upstream project for the WW2 map and HoneyFramework content.
  - Consult it for original behavior, terrain resources, materials, shaders, rendering, and HoneyFramework-derived implementation details.
- `D:\ww2_new2`
  - The game project that integrates this project's Hex Map code and assets under `Assets\HexMapPackage` and calls `ZTKE.HexMap.Runtime` from game code.
  - It has its own `AGENTS.md`; follow those instructions whenever inspecting or changing that project.

## How to use this context

- Proactively inspect the relevant companion-project files when a task involves maps, hex cells or grids, terrain, coasts or water, rendering, shaders or materials, map/world/country/city data, editor/baker/importer tooling, HoneyFramework integration, public Hex Map APIs, regressions, or migration/synchronization of shared code or assets.
- For changes to public runtime/editor behavior or shared assets in this repository, search `D:\ww2_new2` for consumers and its integrated counterpart so compatibility and real game usage are considered.
- When expected behavior or provenance is unclear, compare with `D:\unity game hub\WW2_map_project`; do not assume either companion copy is automatically authoritative.
- Locate counterparts by name and content rather than assuming identical relative paths, because the projects can be reorganized independently. Exclude generated Unity directories such as `Library`, `Temp`, and `Logs` from broad searches.
- Companion projects are context and references by default. Do not modify files outside `D:\hex-map` unless the user explicitly asks for a cross-project change.
- The companion worktrees may contain unrelated work in progress. Never reset, clean, revert, or overwrite their existing changes.
- If a companion path is unavailable, continue with the accessible context and mention the limitation when it affects confidence or verification.

## HexMap package synchronization

- This is the original standalone Hex Map project; `D:\ww2_new2\Assets\HexMapPackage` is the integrated game-project copy and may contain later shared-package work.
- The shared package boundary is `Assets\HexMapPackage` in both projects, including `.meta` files. Compare and synchronize that boundary by relative path and content hash; never copy generated Unity folders.
- Keep this project's `Packages`, `ProjectSettings`, build scenes, and other project-shell files independent unless the user explicitly requests a specific setting or dependency migration.
- After the 2026-08-23 synchronization, the two `Assets\HexMapPackage` trees had identical file content. Re-check both dirty worktrees before any later synchronization and back up overwritten untracked files.

## 2026-09-19 map migration from ww2_new2

- Active standalone flat scene: `Assets/HexMapPackage/Scenes/Hex Map Scene.unity`.
- Active standalone sphere scene: `Assets/StandaloneMaps/Scenes/Spherical Map.unity`.
- `Assets/StandaloneMaps/README.md` documents entry menus, source boundaries, data independence, and validation.
- Current flat geography has 882 cities (1100 x 469). Native sphere has 590,492 cells, 882 cities, and 984 regions. Do not restore the old 623-city data.
- Keep `Assets/StreamingAssets/SphericalMap` files together. Sphere loads native data directly, without recreating the flat map or importing cities on every startup.
- Shared terrain rendering assets remain in `Assets/HexMapPackage`; sphere runtime and topology also require `Assets/MapProjectionIntegration/SphericalTerrainPreview`, `Assets/IcoSphere`, referenced satellite images/font, and the standalone adapter.
- Backup and verification: `Artifacts/MapMigration20260919`. Existing target work was backed up before replacement; do not reset its uncommitted directory reorganization.
- While waiting for Unity compilation/import/scene loading, inspect progress no more often than once every five minutes; prefer completion signals and independent work.
