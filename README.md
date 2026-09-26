# BetterBannerlordNavmeshing

A standalone navmesh generator for Mount & Blade II: Bannerlord scenes - built to replace the in-editor
navmesh generator for large and complex scenes, independent of any terrain-generation tooling.

## Current state

- `TerrainNavMeshGenerator.cs` - error-bounded adaptive mesher + NMG8 prefab writer/importer (imported from
  `Procedural_Bannerlord_Scenes`, Build 3). Input is `(heights, width, height, spacing)`; output is a
  `NavMeshPrefabs\<name>.bin` plus a `Scene.ImportNavigationMeshPrefab` call (static faces, no attachment).

## Roadmap

1. Read terrain heights from any scene (no beta APIs needed):
   `HasTerrainHeightmap` + `GetTerrainData` + `GetTerrainNodeData` + `GetTerrainHeightData`,
   with point-sampling via `GetTerrainHeight` as fallback and validation oracle.
2. Editor entry point (script component button) + runtime entry point (mission logic hotkey);
   write + import into the module's `NavMeshPrefabs`.
3. Blockers/props, multi-level geometry, and full replacement of the stock generator on large scenes.