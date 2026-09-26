# BetterBannerlordNavmeshing

A standalone navmesh generator for Mount & Blade II: Bannerlord scenes - built to replace the in-editor
navmesh generator for large and complex scenes, independent of any terrain-generation tooling.

## Current state

First milestone: run the navmesh generator on a **regular scene that already has terrain**, reading the
heights from the scene itself (editor first; in-game hotkey as an experimental secondary path).

| File | Role |
|---|---|
| `TerrainNavMeshGenerator.cs` | Error-bounded adaptive mesher + NMG8 prefab writer/importer (imported from `Procedural_Bannerlord_Scenes`, Build 3; namespace renamed only). Input `(heights, width, height, spacing)`; output `NavMeshPrefabs\<name>.bin` + `Scene.ImportNavigationMeshPrefab` (static faces). |
| `SceneTerrainSampler.cs` | Reads a scene's **live in-memory** terrain into a flat height grid (bulk per-node API, with point-sampling fallback + validation). |
| `SceneNavMeshExporter.cs` | **Editor (primary).** `ScriptComponentBehavior` with an `Export NavMesh` button; also hosts the shared pipeline `RunPipeline(...)`. |
| `SceneNavMeshMissionLogic.cs` | **EXPERIMENTAL in-game path (secondary).** `MissionLogic`; hotkey `~`, deferred to the next tick. Singleplayer only. |

## Build & install

Prerequisites: the Bannerlord **modding kit** (so `bin\Win64_Shipping_wEditor` exists) and the .NET SDK.

From the repo root:

```powershell
.\build.ps1
```

`build.ps1` builds the editor (`Win64_Shipping_wEditor`) and client (`Win64_Shipping_Client`) variants in
Release and deploys them, then copies this repo's `SubModule.xml` into the module:

```
<Bannerlord>\Modules\BetterBannerlordNavmeshing\
  SubModule.xml
  bin\Win64_Shipping_wEditor\BetterBannerlordNavmeshing.dll
  bin\Win64_Shipping_Client\BetterBannerlordNavmeshing.dll
```

Override the game path if needed: `.\build.ps1 -GameDir 'D:\...\Mount & Blade II Bannerlord'`.

The module must be **enabled** in the launcher. The editor loads the `Win64_Shipping_wEditor` assembly; the
campaign/battle runtime loads `Win64_Shipping_Client`.

Usage (see Quick start): (1) **editor** - add the `Scene Nav Mesh Exporter` component to an entity in a scene
that has terrain and click **Export NavMesh**; (2) **in-game (experimental)** - press `~` during a `Continuing`
singleplayer mission.

## Quick start

### Editor (primary)

1. Compile all four `.cs` files into your module.
2. In a scene that has terrain, add the **Scene Nav Mesh Exporter** script component to any entity.
3. Adjust the settings if you like (table below) and click **Export NavMesh**.
4. Read the `NavMesh: ...` lines in the editor log. On success the prefab is written to
   `<module>\NavMeshPrefabs\<scene>_navmesh.bin` and its faces are imported into the scene.

The read reflects the **live in-memory terrain** (including unsaved/uncommitted terrain edits): the bulk
per-node grid is cross-checked against `GetTerrainHeight` at sample vertices (see log lines below), so the
validation lines are the proof that the mesh matches what is currently in the editor.

### In-game (experimental, secondary)

`SceneNavMeshMissionLogic` is a `MissionLogic`: register it on a mission (e.g. from a mission-behaviour
registration hook) and press `~` while a mission is `Continuing`. Work is queued on the key press and run on
the **next** tick, so the input call never blocks. It runs the same pipeline on `Mission.Current.Scene`.

> Singleplayer only for now. In vanilla, imports/navmesh changes on a mission are server-authoritative
> (`MissionObject.OnInit` guards on `GameNetwork.IsClientOrReplay`); that guard is **not** added here yet.

## Scene terrain API (verified 1.4.8 editor assemblies)

- `bool scene.HasTerrainHeightmap` - a scene has terrain.
- `void scene.GetTerrainData(out Vec2i nodeDimension, out float nodeSize, out int layerCount, out int layerVersion)`
  → extent `xMeters = nodeSize * nodeDimension.Item1`, `yMeters = nodeSize * nodeDimension.Item2`.
- `void scene.GetTerrainNodeData(int xIndex, int yIndex, out int vertexCountAlongAxis, out float quadLength, out float minHeight, out float maxHeight)`
  - one terrain node's per-axis vertex count, vertex spacing and height range.
- `float[] scene.GetTerrainHeightData(int nodeXIndex, int nodeYIndex)` - that node's full vertex grid in one native call.
- `float scene.GetTerrainHeight(Vec2 position, bool checkHoles = true)` - point sample (fallback + validation oracle).
- `bool scene.GetTerrainMinMaxHeight(out float min, out float max)` - global height range.

Stitching matches the terrain-writing API: for node `(nx, ny)` and vertex `(ix, iy)`,
`stitchedX = nx*(verts-1)+ix`, `stitchedY = ny*(verts-1)+iy`, `index = stitchedX*globalHeight + stitchedY`,
world `(x, y) = (stitchedX*quadLen, stitchedY*quadLen)`.

The node array's in-memory **index order is discovered empirically** (`node[ix*verts+iy]` vs
`node[iy*verts+ix]`) by comparing both against `GetTerrainHeight`; the chosen order is logged.

## Expected log lines (editor / rgl log)

Terrain read + validation (one atomic line each):

```
NavMesh: run: build=Build 3 (adaptive navmesh) trigger=editor-button scene=<scene>
NavMesh: settings: stride=1 maxSlope=65deg maxError=0.25m coarsest=16m cullBelowHeight=False minHeight=0 heightOffset=0.5m prefabName='' exportFolder='' clearExisting=True importIntoScene=True writeObj=False
NavMesh: terrain: nodes=7x7 nodeSize=512 layers=1 layerVersion=1 extent=3584x3584m
NavMesh: node(0,0): verts=129 quadLen=4 expectedQuad=4 minHeight=-10.02 maxHeight=99.87
NavMesh: grid: 897x897 spacing=4m
NavMesh: layout node(0,0): 9 samples, ix-major (node[ix*verts+iy]) 9/9, iy-major (node[iy*verts+ix]) 0/9 -> using ix-major
NavMesh: bulk validation: nodes=49 arraysOk=yes minMaxMismatchNodes=0 pointCrossCheck=441/441 (tol 0.25m, mismatchedNodes=0)
NavMesh: global min/max: grid=[-10.02..99.87] scene=[-10.02..99.87] deltaMin=0 deltaMax=0
NavMesh: grid ready: 897x897 spacing=4m fallback=False liveTerrainValidation=bulk+cross-checked
```

What the validation lines tell you:

- `layout ... -> using ix-major|iy-major` - which index order matched `GetTerrainHeight`. `NEITHER order
  matches` means the bulk path is rejected and the fallback runs (logged loudly).
- `bulk validation: ... pointCrossCheck=N/N` - how many sampled vertices matched the point oracle within
  0.25 m, and how many nodes had any mismatch (`mismatchedNodes=0` is the good case).
- `minMaxMismatchNodes` - nodes whose array min/max disagreed with the reported `GetTerrainNodeData` range
  (each mismatch is also logged individually).
- `global min/max: ... deltaMin/deltaMax` - whole-grid range vs `GetTerrainMinMaxHeight`.
- `fallback: point-sampling ...` - used when the bulk read failed; `fallback=False` in `grid ready` is the
  good case.

Then the mesher/import lines (same as Build 3):

```
NavMesh: build done: samples=... V=... E=... F=... culled(slope=...,height=...) z=[...] binBytes=... elapsedMs=...
NavMesh: prefab name: <scene>_navmesh (scene-derived default)
NavMesh: folder: module containing the scene: ...\Modules\<Mod>
NavMesh: folder resolved: ...\Modules\<Mod>\NavMeshPrefabs
NavMesh: writing bin: ...\<scene>_navmesh.bin
NavMesh: existing navmesh faces: N
NavMesh: importing prefab '<scene>_navmesh' (shift 0)...
NavMesh: import call returned.
NavMesh: faces after import: N
NavMesh: <F> faces, <V> vertices, <E> edges (culled: <S> slope, <H> height)
NavMesh: bin written: ...\<scene>_navmesh.bin
NavMesh: faces imported into scene as '<scene>_navmesh' (static).
NavMesh: done: bin=<n> bytes, V=<v> E=<e> F=<f>
```

`faces after import` is the ground truth for whether the import added faces. Failures log `ERROR: <type>:
<message>` plus a flattened `ERROR stack:` line; the pipeline never throws into the caller.

## Settings (Scene NavMesh Exporter fields)

| Field | Default | Notes |
|---|---|---|
| `ExportNavMesh` | button | Runs the export on the scene's live terrain. |
| `NavMeshSampleStride` | `1` | **Finest** sampling: 1 = every terrain vertex. Refinement decides the coarser cells. |
| `NavMeshMaxErrorMeters` | `0.25` | Fidelity guarantee: mesh surface stays within this of the ground. |
| `NavMeshCoarsestSpacing` | `16` | Largest cell size (m). Smaller = tighter bound, more faces. |
| `NavMeshMaxSlopeDegrees` | `65` | Faces steeper than this are dropped. Lower it (~30) to actually trim steep ground. |
| `NavMeshCullBelowHeight` / `NavMeshMinHeight` | `false` / `0` | Optional floor/water culling (true heights; the offset does not shift the floor). |
| `NavMeshHeightOffsetMeters` | `0.5` | Uniform lift applied to the emitted mesh. 0 = flush with the terrain. |
| `NavMeshPrefabName` | `""` | Defaults to `<scene name>_navmesh`. |
| `NavMeshExportFolder` | `""` | Override for the output folder (must be a loaded module's `NavMeshPrefabs` for the import to find it). |
| `NavMeshClearExistingFaces` | `true` | Clears existing faces before import (only when faces exist). |
| `NavMeshImportIntoScene` | `true` | Set false to only write the file. |
| `NavMeshWriteObj` | `false` | Also writes a `.obj` next to the bin (includes the offset). |

Mesher cost (default 3584x3584 m terrain, stride 1, coarsest 16, slope 65, Release, seed 12345): at
`MaxErrorMeters = 0.25` ≈ 1.60 M faces / 136 MB / ~2.1 s (mesher ~150 ms, serialization dominates); at
`2.0` ≈ 411 k faces / 35 MB / ~0.5 s; at `4.0` ≈ 121 k faces / 10 MB / ~0.3 s.

## First-run questions this milestone must answer

1. **Index order**: which of `node[ix*verts+iy]` / `node[iy*verts+ix]` the engine's `GetTerrainHeightData`
   returns (`layout ... -> using ...`). It is discovered per run.
2. **Bulk vs fallback**: does the bulk path validate (`fallback=False`) on real scenes, or does the
   point-sampling fallback kick in (`fallback=True`)?
3. **Live-terrain fidelity**: do `minMaxMismatchNodes=0` and `pointCrossCheck=N/N` with small
   `deltaMin/deltaMax` - i.e. does the bulk grid match the in-editor (unsaved) surface?
4. **Bulk read performance**: wall time of `SceneTerrainSampler.Read` on a full-size scene (log timings to
   be added if it is slow).
5. Editor import visibility: whether the freshly written prefab imports in the same session (may need a
   second click / manual *Import faces from prefabs*).

## Roadmap

1. Read terrain heights from any scene (this milestone).
2. Editor entry point (script button) + in-game hotkey; write + import into the module's `NavMeshPrefabs`.
3. Blockers/props (buildings, rocks, walls) and multi-level geometry.
4. Full replacement of the stock generator on large scenes.
