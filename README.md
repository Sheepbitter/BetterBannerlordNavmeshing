# BetterBannerlordNavmeshing

A standalone navmesh generator for Mount & Blade II: Bannerlord scenes - built to replace the in-editor
navmesh generator for large and complex scenes, independent of any terrain-generation tooling.

## Current state

First milestone: run the navmesh generator on a **regular scene that already has terrain**, reading the
heights from the scene itself (editor first; in-game hotkey as an experimental secondary path).

| File | Role |
|---|---|
| `TerrainNavMeshGenerator.cs` | Error-bounded adaptive mesher + NMG8 prefab writer/importer (imported from `Procedural_Bannerlord_Scenes`, Build 3; namespace renamed only). Input `(heights, width, height, spacing)`; output `NavMeshPrefabs\<name>.bin` + `Scene.ImportNavigationMeshPrefab` (static faces). |
| `Nmg8Writer.cs` | Shared NMG8 prefab serializer (extracted verbatim from `TerrainNavMeshGenerator`); used by both the v1 adaptive mesher and the v2 voxel core. Output bytes are unchanged for v1. |
| `TiledNavMeshBuilder.cs` | **Voxel core (navmesh v2, experimental).** Tiled + parallel Recast pipeline via the vendored DotRecast subset; terrain-only. Pure data-in/data-out (no TaleWorlds types). |
| `VoxelNavMeshGenerator.cs` | Adapter: terrain sampler grid -> voxel core -> engine-space mesh, then the shared write/import path. |
| `SceneTerrainSampler.cs` | Reads a scene's **live in-memory** terrain into a flat height grid (bulk per-node API, with point-sampling fallback + validation). |
| `SceneNavMeshExporter.cs` | **Editor (primary).** `ScriptComponentBehavior` with an `Export NavMesh` button; also hosts the shared pipeline `RunPipeline(...)`. |
| `SceneNavMeshMissionLogic.cs` | **EXPERIMENTAL in-game path (secondary).** `MissionLogic`; hotkey `~`, deferred to the next tick. Singleplayer only. |
| `NavMeshLog.cs` | Per-session **log file** (+ console mirror). In-editor console output is invisible; this is the real trace. See Logs. |

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

1. Build the module (see **Build & install**) and enable it in the launcher.
2. In a scene that has terrain, add the **Scene Nav Mesh Exporter** script component to any entity.
3. Adjust the settings if you like (table below) and click **Export NavMesh**.
4. Read the session **log file** (see Logs) - in-editor console output is not visible. On success the prefab is written to
   `<module>\NavMeshPrefabs\<scene>_navmesh.bin` and its faces are imported into the scene.

The read reflects the **live in-memory terrain** (including unsaved/uncommitted terrain edits): the bulk
per-node grid is cross-checked against `GetTerrainHeight` at sample vertices (see log lines below), so the
validation lines are the proof that the mesh matches what is currently in the editor.

Buttons: **Export NavMesh** (full pipeline), **Import Only** (imports the already-written prefab - no sampling
or build), **Probe Geometry** (collision-geometry diagnostic, see Probe Geometry).

**Freshly written prefab workflow.** The engine builds its navmesh-prefab list at **startup**, so the first
Export cannot find the prefab it just wrote (the log shows `import warning`). Then either:

1. Export (writes the bin) -> **restart the editor** -> **Import Only** (fast; imports the existing prefab,
   no rebuild), or
2. Export -> restart -> **Export** again (also works - it just rebuilds and rewrites the same bin).

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

## Expected log lines (log file; also mirrored to the console as `NavMesh: ...`)

Terrain read + validation (one atomic line each):

```
[HH:mm:ss.fff] ===== run @ yyyy-MM-dd HH:mm:ss =====
[HH:mm:ss.fff] log file: C:\Users\<you>\Documents\Mount and Blade II Bannerlord\BetterBannerlordNavmeshing\Logs\NavMesh_yyyyMMdd_HHmmss.log
NavMesh: run: build=Build 3 (adaptive navmesh) trigger=editor-button scene=<scene> machine=<host> time=yyyy-MM-dd HH:mm:ss logFile=...\NavMesh_yyyyMMdd_HHmmss.log
NavMesh: settings: stride=1 maxSlope=65deg maxError=0.25m coarsest=16m cullBelowHeight=False minHeight=0 heightOffset=0.5m prefabName='' exportFolder='' clearExisting=True importIntoScene=True writeObj=False writeLogFile=True logFolder=''
NavMesh: faces before run: 0
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
NavMesh: import check: existing=<before> afterImport=<after> clearExisting=<bool> increased=<bool>
NavMesh: import warning: the engine's prefab list is built at startup; if this prefab was just written, restart the editor and press Export again (or use the navmesh panel -> Import faces from prefabs).
NavMesh: timings: buildMs=<n> writeMs=<n> objMs=<n|0> clearMs=<n> importMs=<n> facesAfterMs=<n>
NavMesh: done: totalMs=<n> bin=<n> bytes V=<v> E=<e> F=<f>
```

`import check` compares the face count before the run with the count after the import:
`existing=<before>` is read before anything runs, `afterImport=<after>` after the import call. When clearing is
enabled, a successful import always leaves `afterImport > 0`; the `import warning:` line is emitted whenever the
import appears to have added nothing (the startup prefab-list issue - see Troubleshooting).

`timings:` breaks the run into the native/IO steps, one Stopwatch each, so you can see where the wall time
actually goes (the editor felt slower than `totalMs`, which only covers our code):

- `buildMs` - the adaptive mesher (same value as `elapsedMs` on the `build done:` line).
- `writeMs` - writing the `.bin` to disk.
- `objMs` - writing the `.obj` when `NavMeshWriteObj` is on, else `0`.
- `clearMs` - the `ClearNavMesh()` call, only when it ran, else `0`.
- `importMs` - the `ImportNavigationMeshPrefab()` call (how long the engine spends reading the prefab).
- `facesAfterMs` - the post-import `GetNavMeshFaceCount()` call.

**Not included:** everything the editor does around/after the import call - viewport refresh, scene
save/compile, and any engine-side prefab-list rebuild. If `timings:` is small but the editor still feels slow,
the cost is outside this pipeline (startup prefab list, viewport, or save).

`faces after import` is the ground truth for whether the import added faces. Failures log `ERROR: <type>:
<message>` plus a flattened `ERROR stack:` line; the pipeline never throws into the caller.

## Logs

In-editor `Console.WriteLine` is invisible (it appears in neither the editor console nor `rgl_log`), so every
run writes a full trace to a **session log file**:

```
%USERPROFILE%\Documents\Mount and Blade II Bannerlord\BetterBannerlordNavmeshing\Logs\NavMesh_yyyyMMdd_HHmmss.log
```

- **One file per editor session** (created on the first run; later runs in the same session append with a new
  `===== run @ ... =====` banner plus a `log file:` line).
- **One line per message**, timestamped `[HH:mm:ss.fff]`; CR/LF are flattened to ` | ` so nothing spans lines.
- If the default Documents folder cannot be created it falls back to `%TEMP%`.
- Logging never throws and never interrupts a run, and it is flushed after every line (`AutoFlush`) - safe to
  open or send while the editor is running.

Controls (Scene NavMesh Exporter fields):

| Field | Default | Notes |
|---|---|---|
| `NavMeshWriteLogFile` | `true` | Write the session log file (every line is also mirrored to the console). |
| `NavMeshLogFolder` | `""` | Log folder override. Empty = the default path above. |

The log contains the scene and machine name, timings, all sampler validation lines, the mesher stats and the
import result - safe to attach to a bug report.

## Probe Geometry

Phase 1 diagnostic for navmesh v2 (see `docs/plan-navmesh-v2.md`). The **Probe Geometry** button on the
Scene Nav Mesh Exporter dumps what geometry the scene actually exposes into the run log. It reports:

- **Entity scan & classification** - every entity, classified from `BodyFlags` / `GetMobility()` / prefab / tags
  as terrain / flora / stairs / ladder / barrier / dynamic / static; how many report physics; how many
  `GetBodyShape()` results are usable (`shapesOk`), null, empty, or throw; batched/kinematic counts; total
  collision triangles. One `shape entity='...'` line per entity (capped at 60), then a summary + class histogram.
- **Coordinate space** - for the ~8 largest triangle-count entities (plus one with `HasSteps`), a `coords:` line
  comparing the raw `GetTriangle` AABB against `GetGlobalBoundingBox`, `GetLocalBoundingBox`,
  `GetLocalPhysicsBoundingBox(true)`/`(false)` and the raw AABB run through `GetGlobalFrame`. Whichever box
  matches `triRaw` is the coordinate space of the physics triangles.
- **Column probe** - a top-down march of raycasts at the **attached entity's X/Y** (place the entity at the spot
  of interest, e.g. under a bridge). Logs each hit and the vertical gaps, then compares
  `GetGroundHeightAtPosition` (and its `out normal` form), `GetTerrainHeight`, and the navmesh face group at
  that X/Y - i.e. topmost surface vs ground.
- **Scene levels** - `GetUpgradeLevelCount` / `GetUpgradeLevelNameOfIndex` / `GetUpgradeLevelMaskOfLevelName` /
  `GetUpgradeLevelMask`: one line per declared level plus the combined mask (used to choose face level masks).

Example lines:

```
[..] probe: entities=812 physics=430 shapesOk=398 shapesNull=30 shapesEmpty=2 shapeExceptions=0 batchedKinematic=0 trisTotal=914233 scanMs=142
[..] probe classes: terrain=1 flora=210 stairs=64 ladder=0 barrier=38 dynamic=12 static=487
[..] coords: entity='building_a' prefab='building_a' trisSampled=512 triRaw=[..] globalBox=[..] localBox=[..] physBoxIncChildren=[..] physBoxExcChildren=[..] triRaw*globalFrame=[..]
[..] column: x=12.5 y=-40.25 fromZ=200 toZ=-200
[..] column hit 0: z=14.02 entity='bridge_deck' flags=0x00000000 distance=185.98
[..] column: hits=3 gaps=[0.02,7.4]
[..] ground query: GetGroundHeightAtPosition=6.61 (normal=0,0,1) GetTerrainHeight=6.58 navMeshFaceGroup=none
[..] level 0: name='level_1' mask=0x0000000F
[..] levels: count=1 combinedMask=0x000000FF
[..] probe done: totalMs=820
```

Reading it: `shapesNull`/`shapesEmpty` on batched statics are the "visuals without collision" risk (plan S5);
`triRaw` matching one particular box settles the coordinate-space question; column `gaps` reveal stacked
surfaces (anything < 1.5 m apart is a stacking problem, plan S1.3); the `level` masks feed the emitted face
masks. The attached entity's X/Y **is** the probed column - move the entity to probe a different spot.

## Settings (Scene NavMesh Exporter fields)

| Field | Default | Notes |
|---|---|---|
| `ExportNavMesh` | button | Full pipeline: reads terrain, builds, writes the bin, imports. |
| `ImportNavMeshOnly` | button | Imports the already-written prefab: no sampling/build. Use after a restart (see workflow). |
| `ProbeGeometry` | button | Phase 1 diagnostic: dumps scene collision geometry to the log (see Probe Geometry). |
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
| `NavMeshUseVoxelCore` | `false` | **Experimental:** run the v2 tiled voxel core instead of the v1 adaptive terrain mesher (terrain-only). See *Voxel core (experimental)*. |
| `NavMeshVoxelCellSize` | `0.5` | Voxel cell size (m) for the v2 core. Smaller = finer mesh, more faces. |
| `NavMeshVoxelAgentClimbMeters` | `0` | Agent max climb (m) for the v2 core. `0` = auto-derive so `NavMeshMaxSlopeDegrees` is actually traversable (Recast needs `walkableClimb >= 2*cs*tan(slope)/ch`). See the Voxel core section. |
| `NavMeshVoxelDetailMesh` | `true` | v2 detail mesh. Adds interior vertices so faces follow the terrain instead of chording under knolls (bounded by `NavMeshMaxErrorMeters`). Off = leaner bin, but faces cut through hills. See the Voxel core section. |
| `NavMeshVoxelDetailSampleDist` | `2` | v2 detail-mesh sampling distance (m). `0` = auto (`max(0.9, cellSize)`). The default cuts the tight/sliver faces Recast's detail edge tessellation makes along walkable boundaries. Lower = more surface detail (more faces). |
| `NavMeshVoxelContourMaxError` | `1.3` | v2 contour simplification error (voxels). `0` = auto (`NavMeshMaxErrorMeters / cellSize`, i.e. 0.5). A simpler walkable boundary removes most remaining boundary slivers on boundary-rich maps, with no change to the surface-error budget. |
| `NavMeshWriteLogFile` | `true` | Write the per-session log file (see Logs). |
| `NavMeshLogFolder` | `""` | Log folder override; empty = the default Documents path. |

Mesher cost (default 3584x3584 m terrain, stride 1, coarsest 16, slope 65, Release, seed 12345): at
`MaxErrorMeters = 0.25` ≈ 1.60 M faces / 136 MB / ~2.1 s (mesher ~150 ms, serialization dominates); at
`2.0` ≈ 411 k faces / 35 MB / ~0.5 s; at `4.0` ≈ 121 k faces / 10 MB / ~0.3 s.

## Voxel core (experimental)

`NavMeshUseVoxelCore = true` swaps the v1 adaptive terrain mesher for the **v2 tiled voxel core**
(`TiledNavMeshBuilder`, backed by the vendored DotRecast subset in `ThirdParty/DotRecast`). Everything after
the mesh is built - bin write, OBJ, clear, import, face-count verify - is the *same* code path as v1
(`TerrainNavMeshGenerator.WriteAndImport`), so the prefab format and import behaviour are identical.

- **Terrain-only so far.** It voxelizes the terrain triangle soup; collision triangles (buildings, stairs)
  are a later phase.
- **Tiled + parallel.** The world is split into 16 m tiles (`NavMeshCoarsestSpacing`) with a Recast border,
  built on a bounded worker pool (`Parallel.For`, `MaxDegreeOfParallelism = #processors`), then merged in
  sorted tile order. Adjacent tiles weld exactly on the shared voxel lattice (no cracks).
- **Settings:** `NavMeshVoxelCellSize` (default 0.5 m), `NavMeshMaxSlopeDegrees`, `NavMeshMaxErrorMeters`
  (0.25 m -> detail-mesh tolerance), `NavMeshCoarsestSpacing` (tile size), `NavMeshHeightOffsetMeters`,
  `NavMeshVoxelAgentClimbMeters` (0 = auto), `NavMeshVoxelDetailMesh` (default true),
  `NavMeshVoxelDetailSampleDist` (default 2 m), `NavMeshVoxelContourMaxError` (default 1.3 vox). Agent
  radius/height are fixed at 0.35 / 1.8 m; **climb is auto-derived** so the slope setting is actually
  walkable (see below).

### Detail mesh (on by default)

A Recast **polygon mesh alone has boundary vertices only**, so a face spanning a knoll is a flat chord that
cuts *through* the hill (this was the reported v2 behaviour: the navmesh clipped through hills, and smaller
tiles / coarser cells only reduced it). The **detail phase** adds interior vertices so the emitted triangles
follow the surface, bounded by `NavMeshMaxErrorMeters` (0.25 m). It costs more faces and a larger bin, so it
can be turned off (`NavMeshVoxelDetailMesh = false`) for flat/plateau scenes; on rolling terrain it is what
keeps the mesh on the ground. Detail vertices are seam-welded across tiles/polys deterministically (see
`TiledNavMeshBuilder.MergeDetailTiles`).

**Sliver reduction.** Recast's detail phase tessellates every polygon edge and adds an interior sample grid,
which historically produced a large population of very thin (sliver) faces concentrated along walkable-area
boundaries and terrain breaks. Two knobs cut that population by ~5-30x with no fidelity change:
`NavMeshVoxelDetailSampleDist` (default 2 m) coarsens the edge/interior sampling, and
`NavMeshVoxelContourMaxError` (default 1.3 vox) simplifies the walkable boundary (fewer boundary vertices ->
fewer boundary slivers). On the harness hill scene the tight-face count (min edge < 0.25 m) and slivers
(min angle < 5 deg) fall from 0/31 to 0/1, and on a boundary-rich box scene from 56/244 to 0/31, with the
fidelity budget unchanged. Raise `NavMeshVoxelDetailSampleDist` up to ~2x the smallest polygon extent for
fewer faces; go beyond that and Recast skips interior sampling (the detail mesh degenerates to the fan).

### How to read the v2 log lines

Every run also logs the terrain read (see above), then the voxel core:

```
NavMesh: v2: core engaged (tiled voxel). cellSize=0.5m tile=16m
NavMesh: v2: soup verts=... tris=... stride=... spacing=...m
NavMesh: v2: settings cs=...m ch=0.2m tile=16m cellsPerTile=32 tiles=10x10 border=4 hf=40x40 origin=(..) bminY=.. bmaxY=..
NavMesh: v2: agentClimb=2.2m (auto) walkableClimb=11 voxels effectiveMaxSlope=atan(walkableClimb*ch/(2*cs))=65.556deg (setting=65deg)
NavMesh: v2: params slope=65deg radius=0.35m height=1.8m climb=2.2m walkable(r,h,c)=1/9/11 edgeLen=12vox vpp=6 regionMin=8 regionMerge=20 detail=True detSampleDist=2m contourSimpErr=1.3vox
NavMesh: v2: contour maxError=1.3vox (= 0.65m at cs=0.5m; NavMeshVoxelContourMaxError, 0=auto=MaxErrorMeters/cs)
NavMesh: v2: workers=12 (requested 12, tileCount=100)
NavMesh: v2: tris input=... culledByAabb=... rasterized=... (rasterized counts per-tile border duplicates)
NavMesh: v2: tiles=100 built=100 failed=0 workers=12 polys=...
NavMesh: v2: weld verts=... -> ... faces=... edges=... degenerate=... (weldMerged=...)
NavMesh: v2: mesh V=... E=... F=... degenerate=... binBytes=... buildMs=...
NavMesh: v2 timings: tilesMs=... mergeMs=... totalMs=...
NavMesh: v2: tileMs min/avg/max=.../.../...
```

- `v2: tris input=... culledByAabb=...` - input triangles, ones outside every tile, and the number actually
  rasterized (higher than `input` near tile borders because each tile sees its neighbours' geometry).
- `v2: tiles=... built=... failed=...` - failed tiles are also logged individually as `v2: FAILED tile#...`
  with the first exception message; they are never silently dropped.
- `v2: weld verts=... -> ... faces=... degenerate=...` - cross-tile welding, dropped zero-area faces.
- After that the normal `build done` / import lines appear (the shared path).

### A/B against v1

Toggle `NavMeshUseVoxelCore` and press Export again; both write the same `<scene>_navmesh.bin` and import the
same way. Face counts are **not** directly comparable: v1 emits an adaptive quadtree (large mixed-size
quads/triangles), v2 emits triangulated convex Recast polys at a uniform `NavMeshVoxelCellSize`.

### Agent climb is auto-derived to honour `NavMeshMaxSlopeDegrees`

Recast couples `agentMaxClimb` to slope traversability: its ledge filter rejects a span when the *spread* of
its traversable neighbours' floors exceeds `walkableClimb`, and on a uniform slope that spread is twice the
per-cell rise `cs*tan(slope)/ch`. The setting is therefore only honoured when
`walkableClimb >= 2*cs*tan(slope)/ch`.

`NavMeshVoxelAgentClimbMeters = 0` (the default) derives the smallest whole-voxel climb that satisfies this
(`ceil(2*cs*tan(slope)/ch)*ch`; for 0.5 m cells / 0.2 m height / 65 deg this is **2.2 m**), clamped to a 0.4 m
minimum. The log shows the result and warns when the effective slope is more than 1 deg below the setting:

```
NavMesh: v2: agentClimb=2.2m (auto) walkableClimb=11 voxels effectiveMaxSlope=atan(walkableClimb*ch/(2*cs))=65.556deg (setting=65deg)
```

**Trade-off:** a larger climb also connects single vertical steps (curbs, low walls) up to that height, so the
agent can walk up them. Set `NavMeshVoxelAgentClimbMeters` explicitly to cap that, or reduce
`NavMeshVoxelCellSize` (halving `cs` halves the required climb). A `v2: WARNING ...` line is logged when the
effective walkable slope falls short of `NavMeshMaxSlopeDegrees`.

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

## Troubleshooting

- **First `Export` after writing a new prefab imports nothing** (the log shows the `import warning:` line and
  `import check: ... increased=False`). The engine builds its navmesh-prefab list at **startup**, so a prefab
  written in the current session is not found on the first attempt. **Restart the editor**, then press
  **Import Only** (no rebuild) or **Export** again - verified behaviour: the second attempt after a restart
  imports the whole map in ~2-3 s. Alternatively: navmesh panel -> *Import faces from prefabs*.
- `fallback=True` in `grid ready`: the bulk per-node read failed validation (look for `bulk read failed:` /
  `NEITHER order matches` above it). The point-sampled grid is still correct, just slower.
- **No log file**: check `NavMeshWriteLogFile` is `true`; the `log file:` path is printed at the top of the run
  (and mirrored to the console). If `Documents` is not writable it falls back to `%TEMP%`.
- Bin too heavy / import slow: raise `NavMeshMaxErrorMeters` (0.25 -> 1.0 -> 2.0) or lower the terrain size.

## Roadmap

1. Read terrain heights from any scene (this milestone).
2. Editor entry point (script button) + in-game hotkey; write + import into the module's `NavMeshPrefabs`.
3. Blockers/props (buildings, rocks, walls) and multi-level geometry.
4. Full replacement of the stock generator on large scenes.
