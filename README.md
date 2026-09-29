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
| `TiledNavMeshBuilder.cs` | **Voxel core (navmesh v2, experimental).** Tiled + parallel Recast pipeline via the vendored DotRecast subset; terrain **+ static asset collision** geometry (one soup). Pure data-in/data-out (no TaleWorlds types). |
| `VoxelNavMeshGenerator.cs` | Adapter: terrain sampler grid (+ optional engine-space asset triangles) -> voxel core -> engine-space mesh, then the shared write/import path. Merges both into one soup; `NavMeshHeightOffsetMeters` lifts the whole merged mesh uniformly. |
| `SceneCollisionGeometry.cs` | **Main-thread asset collector (phase 3).** Enumerates scene entities, classifies by `BodyFlags`/mobility, **preloads the included bodies by name** (engine's own `PhysicsShape` preload queue - `GetTriangle` read-AVs on an unloaded body), then reads `GetBodyShape()` triangles and transforms them by the entity global frame into engine-space world triangles (plain arrays for the background build). Optional per-entity TRACE line for crash triage. |
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
  bin\Win64_Shipping_wEditor\BetterBannerlordNavmeshing.pdb
  bin\Win64_Shipping_Client\BetterBannerlordNavmeshing.dll
  bin\Win64_Shipping_Client\BetterBannerlordNavmeshing.pdb
```

The `.pdb` files are deployed next to the DLLs (both variants) so crash dumps resolve our frames to source
lines. They are debug-only and are not part of any release/zip logic.

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
  ÃƒÂ¢Ã¢â‚¬Â Ã¢â‚¬â„¢ extent `xMeters = nodeSize * nodeDimension.Item1`, `yMeters = nodeSize * nodeDimension.Item2`.
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
the cost is outside this pipeline (startup prefab list, viewport, or save). The v2 async path measures those
separately with the `v2: perf:` lines - see *Async build & performance instrumentation* below.

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
  - **Terrain source check** (`probe terrain column:`) - the stored bulk node value at that column (both index
    orders) vs the hole-aware `GetTerrainHeight(xy, true)` and the hole-blind `GetTerrainHeight(xy, false)`:
    answers "is the terrain our soup is built from the live surface here, or a hole/lid?".
  - **Asset geometry within 2 m** (`assets within 2m:`) - entity box triangle totals, the box Y range, the
    triangle-precise count and Y range inside the 2 m disc, plus the top 5 contributors by name. Bodies are
    preloaded by name first (the `PhysicsShape` queue), so `GetTriangle` is never called on an unloaded body.
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
[..] probe terrain column: node=(3,7) verts=65 quad=1.563m local=(12.5,3.75)m index=(8,2) storedIxMajor=6.58m storedIyMajor=6.58m pointCheckHoles=6.58m pointNoHoles=6.58m agree(ix)=True agree(iy)=True holeAwareDiffersFromStored=False
[..] assets within 2m: entities=7 boxTris=12048 boxZ=[6.2..9.9] nearTris=2140 nearZ=[6.31..6.74]
[..] assets within 2m top: 'paved_terrace_a'=4210 | 'paved_terrace_b'=3855 | 'ruinous_wall_a'=2401 | 'sturgia_dungeon_corridor_c'=1582
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
| `DumpSceneSnapshot` | button | Writes the export's main-thread inputs as a scene snapshot (terrain grid + per-entity collision triangles + a readable header) next to the bin, so the standalone harness can rebuild the exact soup for this scene without the editor. See *Scene snapshot & offline harness*. |
| `NavMeshSnapshotAssertPoints` | `""` | Extra assert points for the snapshot header, format `"x,y;x,y;..."`. The two Birtu probe spots are always included; the harness asserts each point is covered by a face on the largest island. |
| `NavMeshSampleStride` | `2` | Terrain sampling decimation: 1 = every vertex, 2 = every other (the default; halves sample count with no practical fidelity loss). |
| `NavMeshMaxErrorMeters` | `0.25` | Fidelity guarantee: mesh surface stays within this of the ground. |
| `NavMeshCoarsestSpacing` | `16` | Largest cell size (m). Smaller = tighter bound, more faces. |
| `NavMeshMaxSlopeDegrees` | `50` | Faces steeper than this are dropped; the v2 core auto-derives the agent climb so the configured slope is actually traversable. |
| `NavMeshCullBelowHeight` / `NavMeshMinHeight` | `false` / `0` | Optional floor/water culling (true heights; the offset does not shift the floor). |
| `NavMeshHeightOffsetMeters` | `0.5` | Uniform lift applied to the whole emitted mesh (terrain **and** structures), so it does not intersect either. 0 = as flush as the voxel lattice allows. |
| `NavMeshPrefabName` | `""` | Defaults to `<scene name>_navmesh`. |
| `NavMeshExportFolder` | `""` | Override for the output folder (must be a loaded module's `NavMeshPrefabs` for the import to find it). |
| `NavMeshClearExistingFaces` | `true` | Clears existing faces before import (only when faces exist). |
| `NavMeshImportIntoScene` | `true` | Set false to only write the file. |
| `NavMeshWriteObj` | `false` | Also writes a `.obj` next to the bin (includes the offset). |
| `NavMeshUseVoxelCore` | `true` | Run the v2 tiled voxel core (terrain + ingested static asset collision; the default since 2026-09-29). Set false for the v1 adaptive terrain mesher. See *Voxel core (experimental)*. |
| `NavMeshVoxelCellSize` | `0.25` | Voxel cell size (m) for the v2 core. 0.25 is required for 1.0-1.2 m doorways/walkways/stair treads to survive (see *Narrow features & connectivity*); it costs ~4x build time and ~2-4x faces vs 0.5 (0.5 remains fine for open terrain / fast iteration). |
| `NavMeshVoxelAgentClimbMeters` | `0` | Agent max climb (m) for the v2 core. `0` = auto-derive so `NavMeshMaxSlopeDegrees` is actually traversable (Recast needs `walkableClimb >= 2*cs*tan(slope)/ch`). See the Voxel core section. |
| `NavMeshVoxelDetailMesh` | `true` | v2 detail mesh. Adds interior vertices so faces follow the terrain instead of chording under knolls (bounded by `NavMeshMaxErrorMeters`). Off = leaner bin, but faces cut through hills. See the Voxel core section. |
| `NavMeshVoxelDetailSampleDist` | `2` | v2 detail-mesh sampling distance (m). `0` = auto (`max(0.9, cellSize)`). The default cuts the tight/sliver faces Recast's detail edge tessellation makes along walkable boundaries. Lower = more surface detail (more faces). |
| `NavMeshVoxelContourMaxError` | `1.3` | v2 contour simplification error (voxels). `0` = auto (`NavMeshMaxErrorMeters / cellSize`, i.e. 0.5). A simpler walkable boundary removes most remaining boundary slivers on boundary-rich maps, with no change to the surface-error budget. |
| `NavMeshVoxelIncludeAssets` | `true` | v2: ingest static asset collision geometry (interiors, stairs, decks, bridges) into the voxel soup so structures are navigable. Terrain-only when false. See *Assets (structures)*. |
| `NavMeshVoxelIncludeDynamic` | `true` | v2 assets: include movable/dynamic physics props (e.g. Birtu's merlons) at their placed pose. Set false to build only from static geometry. |
| `NavMeshVoxelIncludeFlora` | `false` | v2 assets: include flora collision (trees/bushes). Off by default - flora is usually visual and its many small shapes add faces without being reliably navigable. |
| `NavMeshVoxelAssetTrace` | `false` | Diagnostic: log one `assets: TRACE 'entity' body='...' ...` line per included entity immediately before its native triangle read, so if a crash ever happens the log's last line names the culprit. One diagnostic run only. See *Assets (structures)*. |
| `NavMeshVoxelAgentRadius` | `0.25` | v2 agent radius (m). Erosion = `ceil(radius / NavMeshVoxelCellSize)` cells each side; tile border = `3 + ceil(radius / cs)`. 0.25 keeps erosion at 1 cell even at cs 0.25 (0.35 would erode 2 and close 1.0-1.2 m doorways/walkways/stairs). See *Narrow features & connectivity*. |
| `NavMeshVoxelLedgeFilter` | `true` | v2 Recast ledge filter. On = Recast default (trims spans within `walkableClimb` of a drop, keeping the mesh off cliff edges) but it also eats the border cells of narrow walkways with drops on both sides. Turn off if narrow structures keep losing their edges. |
| `NavMeshVoxelGapFillCells` | `2` | Gap bridging (cells, `0` = off). Before erosion, a column with no walkable span is filled when walkable spans flank it on opposite sides along one axis with an empty run of at most this many cells and a floor within the agent climb. Closes abutting-prefab / paved-terrace seams (a hairline collision gap that erosion would widen into a break). See *Gap bridging*. |
| `NavMeshVoxelStitchMaxGapCells` | `6` | Stitching (cells, `0` = off). Per tile, reconnects two mesh components whose nearest boundary edges are within this many cells and within the agent climb, by adding a small quad that reuses existing vertices. Refused across walls/doors/ceilings. **Measured:** at cs 0.25 a real 1-cell seam is a 1.25 m (5-cell) mesh gap, so the default `6` covers it with margin (`5` is the minimum that works, `4` is one cell short). See *Stitching*. |
| `NavMeshVoxelPruneIsolatedIslands` | `true` | Remove small isolated islands after the merge (sealed-void lids, stray fragments) so they cannot sit where no agent can reach. Never touches the big networks (> cap). See *Island pruning*. |
| `NavMeshVoxelPruneMaxIslandFaces` | `400` | Face cap for the prune (`0` = off). Islands with at most this many faces are removed. Raise it if legit isolated areas (sealed rooms, tower tops) disappear; set `NavMeshVoxelPruneIsolatedIslands=false` to disable. |
| `NavMeshVoxelSteppedProps` | `false` | **Off by default; read *Stairs & ladders* before enabling.** Stairs: a `HasSteps`/`Ladder` prop's own triangles are exempt from Recast's ledge trim (a per-tile marker pass restores exactly the spans the trim nulled; `v2: stepped: markedCells=<n> restored=<n>`), which is what a 51-56 deg flight needs - its treads are horizontal and walkable, the trim was the only thing deleting them. Everything else keeps the ordinary 50.19 deg cap. Ladders: `SteppedPropRamps` bridges only the *unsupported* stretches of the run (holes <= 1.5 m and taller than the agent climb) with true-level end anchors, so no ramp ever hovers over a tread. Measured on Birtu: `stairs_bo` 6/6 improve (+9..+20 pts) and `european_castle_ladder` 6/6 improve; 4 of 6 `wooden_platform_stairs_a` improve and 2 regress; islands 5 -> 6 - so the default stays off for now. |
| `NavMeshVoxelWriteSoupObj` | `false` | Diagnostic: also write `<prefab>_soup_terrain.obj` and `<prefab>_soup_assets.obj` next to the bin (engine space) and log both paths. Inspect a failing spot with geometry provenance in the editor/Blender. |
| `NavMeshVoxelTerrainHoleAudit` | `false` | Terrain-hole audit: compare every sampled terrain vertex against the hole-aware point query and drop the cells of any version that disagrees by > 1 m (a false walkable lid over a carve). Costs one native point query per vertex; see *Terrain holes (false lids)*. |
| `NavMeshAsyncBuild` | `true` | Run the v2 voxel build on a background thread so the editor UI stays alive during long builds. Applies only when `NavMeshUseVoxelCore` is on; the v1 mesher is always synchronous. See *Async build & performance instrumentation*. |
| `NavMeshWriteLogFile` | `true` | Write the per-session log file (see Logs). |
| `NavMeshLogFolder` | `""` | Log folder override; empty = the default Documents path. |

Mesher cost (v1 path, measured on 3584x3584 m terrain at stride 1, coarsest 16, slope 65, Release, seed 12345): at
`MaxErrorMeters = 0.25` ÃƒÂ¢Ã¢â‚¬Â°Ã‹â€  1.60 M faces / 136 MB / ~2.1 s (mesher ~150 ms, serialization dominates); at
`2.0` ÃƒÂ¢Ã¢â‚¬Â°Ã‹â€  411 k faces / 35 MB / ~0.5 s; at `4.0` ÃƒÂ¢Ã¢â‚¬Â°Ã‹â€  121 k faces / 10 MB / ~0.3 s.

## Voxel core (experimental)

`NavMeshUseVoxelCore = true` swaps the v1 adaptive terrain mesher for the **v2 tiled voxel core**
(`TiledNavMeshBuilder`, backed by the vendored DotRecast subset in `ThirdParty/DotRecast`). Everything after
the mesh is built - bin write, OBJ, clear, import, face-count verify - is the *same* code path as v1
(`TerrainNavMeshGenerator.WriteAndImport`), so the prefab format and import behaviour are identical.

- **Terrain + static assets.** It voxelizes one triangle soup built from the terrain heightfield *and*
  (by default) every physics entity's collision geometry, so interiors, stairs, decks and bridges over
  terrain become navigable. See *Assets (structures)* below.
- **Tiled + parallel.** The world is split into 16 m tiles (`NavMeshCoarsestSpacing`) with a Recast border,
  built on a bounded worker pool (`Parallel.For`, `MaxDegreeOfParallelism = #processors`), then merged in
  sorted tile order. Adjacent tiles weld exactly on the shared voxel lattice (no cracks).
- **Settings:** `NavMeshVoxelCellSize` (default 0.25 m), `NavMeshMaxSlopeDegrees`, `NavMeshMaxErrorMeters`
  (0.25 m -> detail-mesh tolerance), `NavMeshCoarsestSpacing` (tile size), `NavMeshHeightOffsetMeters`,
  `NavMeshVoxelAgentClimbMeters` (0 = auto), `NavMeshVoxelDetailMesh` (default true),
  `NavMeshVoxelDetailSampleDist` (default 2 m), `NavMeshVoxelContourMaxError` (default 1.3 vox),
  `NavMeshVoxelAgentRadius` (default 0.25 m), `NavMeshVoxelLedgeFilter` (default true),
  `NavMeshVoxelGapFillCells` (default 2), `NavMeshVoxelStitchMaxGapCells` (default 6),
  `NavMeshVoxelPruneIsolatedIslands` / `NavMeshVoxelPruneMaxIslandFaces` (default true / 400),
  `NavMeshVoxelIncludeAssets` / `NavMeshVoxelIncludeDynamic` / `NavMeshVoxelIncludeFlora` (asset ingestion;
  see *Assets (structures)*), `NavMeshVoxelTerrainHoleAudit` (default false; see *Terrain holes*),
  `NavMeshVoxelWriteSoupObj` (default false; soup dump), `NavMeshVoxelAssetTrace` (default false; crash
  triage). Agent height is fixed at 1.8 m; **radius is configurable** and **climb is auto-derived** so the
  slope setting is actually walkable (see below).

### Async build & performance instrumentation

With `NavMeshUseVoxelCore = true` and `NavMeshAsyncBuild = true` (the default), the Export button runs the
pipeline in two halves:

- **Main thread:** run header + settings, `faces before run`, the live terrain read (`SceneTerrainSampler`),
  the asset collision scan (`SceneCollisionGeometry`, when `NavMeshVoxelIncludeAssets`), and prefab/folder
  resolution.
- **Background thread:** `VoxelNavMeshGenerator.BuildExportMesh` only - the pure-managed build (terrain soup
  -> `TiledNavMeshBuilder.Build` -> NMG8 serialization). It never touches `Scene`/game objects.
- **Main thread (completion):** the **unchanged** `TerrainNavMeshGenerator.WriteAndImport` half - bin write,
  `ClearNavMesh`, native `ImportNavigationMeshPrefab`, face-count verify.

Completion is picked up on the editor tick. The editor calls `OnEditorTick(dt)` for every script component in
the open scene each frame (decompiled 1.4.8 editor: `ManagedScriptHolder.SetScriptComponentHolder` adds the
component to `_toTickForEditor`, and `ManagedScriptHolder.TickComponentsEditor` invokes `OnEditorTick`), so
the component polls a completion flag there. The editor stays interactive for the whole build instead of
blocking for 3-60 s. Pressing Export again while a build is running logs `v2: build already running - ignoring`
and is ignored. A background failure is reported on the main thread and never tears the editor down; the
invariant-gate abort keeps its old behaviour (no bin, no import).

New log lines (async path only; v1 and **Import Only** are unchanged):

```
NavMesh: v2: async build started on a background thread; the editor stays responsive (progress lines follow).
NavMesh: v2: progress tiles=<done>/<total> (<pct>%) elapsedMs=<n>          <- every ~5% of tiles, >=2 s apart
NavMesh: v2: async build finished on the background thread.
NavMesh: v2: perf: ticksDuringBuild=<n> maxTickMs=<n>                      <- editor stayed alive during the build
NavMesh: v2: perf: clickToDoneMs=<n> sampleMs=<n> assetsMs=<n> buildMs=<n> writeMs=<n> importMs=<n>
NavMesh: v2: perf: postImportTickMs min=<a> avg=<b> max=<c> over 30 ticks  <- viewport cost of the new mesh
```

- `ticksDuringBuild` / `maxTickMs` - editor ticks observed while the build ran and the slowest frame; evidence
  the UI stayed responsive. The frame time is the wall-clock gap between `OnEditorTick` calls (not the engine
  `dt`), so a real hitch shows up.
- `clickToDoneMs` - button press to import done; `sampleMs` is the terrain read, `assetsMs` the asset scan,
  `buildMs` the background build, `writeMs` the `.bin` write and `importMs` the native import.
- `postImportTickMs` - the editor frame time sampled over the 30 ticks right after the import; this is the
  first viewport-render measurement of a large imported navmesh (the `timings:` line excludes it).

Determinism is untouched: the progress counter/logging is observational only, and the harness
(`RESULT: DETERMINISM PASS`) is unchanged.

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

### Assets (structures)

With `NavMeshVoxelIncludeAssets = true` (the default) the v2 core ingests **static asset collision geometry**
so buildings, stairs, decks and bridges over terrain become navigable, not just the ground. The geometry is
read on the **main thread** (`SceneCollisionGeometry.Collect`) as engine-space world triangles and handed to
the background build as plain `float[]`/`int[]`, then mixed into the same Recast-space soup as the terrain
before a single tiled voxel build - so terrain and structures voxelize and weld together (a stair connects to
the ground; a deck connects to the banks it spans).

**Include/exclude policy** (classified by `BodyFlags | PhysicsDescBodyFlag` + `GetMobility()`, never by prefab
name):

| Geometry | Default | Why |
|---|---|---|
| Static structure collision (walls, floors, stairs, decks, bridges) | **included** | This is the point of the phase: these become walkable. |
| `HasSteps` stairs | included | Voxelized like any floor; steps <= agent climb are traversable. |
| Dynamic / movable props (`Dynamic`/`Moveable`, dynamic mobility, dynamic rigid body) | included (`NavMeshVoxelIncludeDynamic`) | Captured at their placed pose - e.g. Birtu's merlons are static-in-practice props. Set false for static-only. |
| Flora (`BodyOwnerFlora`) | **excluded** (`NavMeshVoxelIncludeFlora = false`) | Usually visual; many small shapes add faces without being reliably navigable. |
| Barriers (`Barrier` / `Barrier3D`) | excluded | Invisible blocker volumes; walkable faces must not sit on them. |
| Ladders (`Ladder`) | excluded | Not a walkable surface; the engine handles ladders separately. |
| Non-solid-for-agents markers (`Disabled` / `OnlyCollideWithRaycast` / `DontTransferToPhysicsEngine` / `MissileOnly`) | excluded | Flagged as not solid for agents: such a body must not become walkable navmesh. Counted as `unsolid=` in the exclusion log. (None on Birtu today; future-proofing.) |
| Terrain-owner bodies (`BodyOwnerTerrain`) | excluded | The terrain is already meshed from the heightfield; reading it again would double up. |
| Non-physics / empty / no-shape entities | excluded | Nothing to voxelize. |

Per-shape triangle reads are capped at 200k with a `assets: WARNING shape '...' capped at ...` line; entity and
shape access is per-entity `try/catch`, so one bad entity (logged, counted under `exceptions`) never aborts the
collection.

**Body preload (crash hardening).** `PhysicsShape.GetTriangle` read-AVs the whole process when a shape's native
triangle-mesh table is NULL - an engine bug (the native function asserts about the null table, then dereferences
`NULL+0x40` anyway) that hits bodies whose physics has not been loaded, and which is **not catchable** from
managed code even though `TriangleMeshCount()` reports > 0. So `Collect` now runs the engine's own load recipe
(`PreloadHelper.PreloadMeshesAndPhysics`) before it reads any triangle: it collects the unique included body
names (`shape.GetName()`, falling back to the entity name), calls `PhysicsShape.AddPreloadQueueWithName(name,
new Vec3(1f,1f,1f,-1f))` for each, then a single `PhysicsShape.ProcessPreloadQueue()`. If that ever still
crashes, set `NavMeshVoxelAssetTrace = true` for one diagnostic run: a `assets: TRACE 'entity' body='...'`
line is written immediately before each native read, so the log's last line names the culprit. In normal runs
a cheap `assets: progress` line is logged every 250 entities during the read pass.

**Height offset mechanics.** `NavMeshHeightOffsetMeters` (+0.5 m) is a **single uniform lift applied to the
whole merged mesh** - terrain and structures alike. It exists so the navmesh sits just above the collision
surface rather than intersecting it: if the navmesh cut through the terrain/assets, units would lose the mesh
and get confused. `VoxelNavMeshGenerator` therefore mixes the terrain soup and the asset soup untouched, and
the builder's one offset lifts both. `TiledNavMeshBuilder.BuildCombinedSoup` **reverses asset triangle
winding**, because the engine (Z-up) -> Recast (Y-up) axis swap is orientation-reversing, and then **normalises each triangle per
triangle**: any asset triangle whose Recast normal points down has two indices swapped, i.e. collision
data with inconsistent winding is treated as **double-sided for walkability**. Real collision data is
frequently wound inconsistently (on Birtu 249,809 / 591,589 asset triangles, 42%, were downward-wound);
a downward-wound floor is rejected by `MarkWalkableTriangles` (normal.Y < 0 -> NULL area) and would
produce **no navmesh at all** at any cell size, with or without pruning - measured: the corridor
floors at z 20.0-20.4 had no navmesh until this normalisation. The rasterizer merges the two
orientations into the same solid span, so the output mesh is unchanged apart from surfaces that were
previously dropped entirely. The offset is adjustable (`0` = as close to the surface as the voxel
lattice allows). Terrain-only scenes skip the mixer entirely, so their output is byte-identical to
before (harness `RESULT: DETERMINISM PASS`, unchanged hashes).


**Caveats.**
- **Small isolated surfaces are pruned.** Recast discards walkable regions smaller than `RegionMinSize^2`
  cells (default `8` -> 64 cells) *after* agent-radius erosion: ~4 m^2 at the 0.25 m default cell size
  (typical small decks survive) and ~16 m^2 at 0.5 m (where the harness shows an isolated 4x4 m platform
  pruned - it keeps only 25-35 cells after erosion and needs ~64). Surfaces that connect to larger navmesh
  (the usual in-scene case: a deck bridges two banks, stairs reach the ground) are unaffected.
- **Asset fidelity** is `NavMeshMaxErrorMeters`-bounded like the terrain, plus the same uniform
  `NavMeshHeightOffsetMeters` lift and the detail phase's upstream `+ch` nudge; the whole mesh is lifted
  together, so structures do not sit lower than the terrain around them.

### Scene snapshot & offline harness

Iterating on a real map should not need the editor. The **`Dump Scene Snapshot`** button (Scene Nav Mesh
Exporter) runs the same main-thread inputs the export uses - the terrain sampler (full grid) and
`SceneCollisionGeometry` with the current include/dynamic/flora policy - and writes **three files next to the
bin** (same folder resolution as the export). It never throws; the log reports the three paths, the counts and
the elapsed time.

```
NavMesh: snapshot: scene=<name> machine=<name>
NavMesh: snapshot: terrain=<folder>\<prefab>_snapshot_terrain.bin (385x385 spacing=1.563m stride=2 heights=148225)
NavMesh: snapshot: assets=<folder>\<prefab>_snapshot_assets.bin (entities=4336 tris=591589 cappedShapes=0)
NavMesh: snapshot: txt=<folder>\<prefab>_snapshot.txt (largest=20 assertPoints=2)
NavMesh: snapshot: wrote 3 files in 1234ms terrainMs=... assetsMs=...
```

**Formats** (little-endian, version 1):

- `<prefab>_snapshot_terrain.bin` - `int32 version, int32 width, int32 height, float32 spacing, int32 stride`,
  then `width*height` float32 heights exactly as the sampler produced them.
- `<prefab>_snapshot_assets.bin` - `int32 version, int32 entityCount`; per entity: `int32 nameLen` + UTF-8
  name, `int32 flags`, `int32 mobility`, `int32 triCount`, `int32 capped`, then `triCount*3` vertices (9
  float32 each = 3 vertices per triangle, engine Z-up world space), then `triCount*3` int32 indices
  (0-based; trivial, the mesh is stored unindexed).
- `<prefab>_snapshot.txt` - human-readable: scene, date, machine, the settings line, the grid
  (dims/spacing/stride/fallback), asset counts and the exclusion stats, the 20 largest shapes by name, and an
  `assert-points:` section (the two Birtu probe spots plus `NavMeshSnapshotAssertPoints`).

**Offline harness mode** (the standalone harness, `--snapshot`):

```
TiledCoreHarness.exe --snapshot <prefix>                       # <prefix>_snapshot_{terrain,assets}.bin + .txt
TiledCoreHarness.exe --snapshot <terrainBin> <assetsBin> <txt>
```

Optional flags (defaults = the recommended set):

```
--pair "x1,z1|x2,z2"      repeatable. Also accepts x1,z1,level1|x2,z2,level2 (explicit floor level per end).
                          Measures whether the straight path between the two spots is bridged (see below).
--band M  --cs M  --radius M  --climb M  --gapfill N  --stitch N  --prune N  --ledge on|off  --detail on|off  --slope D
```

Assert points are reported with **their own island id** (index + face count + area), and each point's
stacked level is chosen as: a `diag` asset bounding box that contains the point pins the level to that
asset's **top** surface; an explicit `z` wins next; otherwise the highest face. `--pair` samples the
straight segment between the two spots at ~0.25 m steps in that level band and reports
`gapMaxRun` (the longest contiguous off-band run), `coverage`, and `geo`/`euclid`/`ratio` where `geo` is a
Dijkstra path over face adjacency measured through the shared-edge midpoints (a straight, directly
connected pair measures ~1.35-1.45x its euclidean distance). A pair PASSes when `gapMaxRun <= 0.5 m` and
`ratio <= 1.5`; the exit code is 0 only when every pair passes (and there are no pairs).

It loads the snapshot and rebuilds the **exact** soup the editor would: `BuildTerrainSoup` with the header's
spacing/stride, then the assets merged through `BuildCombinedSoup` (engine Z-up -> Recast Y-up, winding
reversal), with the recommended defaults (cs 0.25, radius 0.25, slope 50, detail on, gapFill 2, stitch 6,
prune 400, climb auto, +0.5 m lift). It prints the usual diagnostics (`v2:` settings/params/gapfill/stitch/
prune/weld/invariants/islands), then:

- each **assert point** is checked against the **largest island** (`SNAPSHOT: PASS/FAIL`, exit code 0/1);
  the two Birtu probe spots are always written, so on a different scene delete those two lines from the txt
  (or ignore the exit code) if they are not expected to be covered;
- the island count and the 10 largest islands are listed;
- every asset whose name contains **"diag"** is reported as `tris=<n> facesInBBox=<n> -> present|pruned`
  (its XZ bounding box plus a 1 m Y band), so a renamed problem asset can be tracked by name.

The no-argument batch mode (all the regression tests) is unchanged. The round trip is itself validated:
harness `Test 20` writes a snapshot from a scenario with the real writer, loads it back through the same
helpers, and asserts the soup and the mesh are **identical face for face** to the in-process build.

### Narrow features & connectivity

Doorways, walkways and thin stair treads fail when **erosion + quantization eat the whole feature**:
erosion is `ceil(NavMeshVoxelAgentRadius / NavMeshVoxelCellSize)` cells *per side*, so at `cs = 0.5` and
radius 0.35 (or 0.25) a 1.2 m doorway is 2 cells wide and vanishes, and a 1.0 m walkway (2 cells) is eroded
to nothing. The Recast **ledge filter** compounds it: it removes spans within `walkableClimb` of a drop, so a
narrow walkway with drops on both sides loses its border cells too. The harness connectivity lab
(`Test 13/14`) builds terrain+asset scenes for a 1.2 m doorway, a 1.0 m walkway (drops both sides), a 1.0 m
wide 6x0.4 m staircase, a 2.5x2.2x8 m open-ended tunnel and a courtyard with two 1.2 m mouths, and asserts
face-adjacency connectivity with union-find.

Measured sweep (`P` = connected, order a b c d e; multi-threaded, 24x24 m scenes):

| cs | radius | ledge | a | b | c | d | e | pass | faces | ms |
|---|---|---|---|---|---|---|---|---|---|---|
| 0.5 | 0.35 | on | . | . | . | P | . | 1/5 | 241 | 4 |
| 0.5 | 0.35 | off | . | . | . | P | . | 1/5 | 239 | 3 |
| 0.5 | 0.25 | on | . | . | . | P | . | 1/5 | 241 | 3 |
| 0.5 | 0.1 | on | . | . | . | P | . | 1/5 | 241 | 3 |
| **0.25** | **0.25** | **on** | **P** | **P** | **P** | **P** | **P** | **5/5** | **504** | **11** |
| 0.25 | 0.1 | on | P | P | P | P | P | 5/5 | 504 | 11 |
| 0.25 | 0.35 | on | . | . | . | P | . | 1/5 | 473 | 10 |
| 0.15 | 0.25 | on | P | P | P | P | P | 5/5 | 854 | 50 |
| 0.15 | 0.35 | off | P | P | P | P | P | 5/5 | 874 | 43 |

- At `cs = 0.5` **no radius or ledge setting helps** - erosion is 1 cell for every radius <= 0.5, so the
  1.0-1.2 m features are gone regardless.
- The cheapest all-pass configuration is **`cs = 0.25` with `NavMeshVoxelAgentRadius <= 0.25`** (ledge filter
  either way; keep it on). radius 0.35 at cs 0.25 erodes 2 cells and still fails.
- **`NavMeshVoxelCellSize` defaults to 0.25 since 2026-09-29** (the cheapest all-pass configuration above);
  that is roughly 4x the cells of 0.5, so expect ~4x the build time and ~2-4x the faces (Birtu ~1 s -> ~4 s;
  the async build keeps the editor responsive). Set 0.5 for fast iteration on open terrain - 0.25 with the
  default `NavMeshVoxelAgentRadius = 0.25` is the shipping configuration.

### Gap bridging

A hairline collision gap where two prefab floor pieces abut (a paved terrace, a corridor joint) is usually
sub-cell, but agent-radius erosion widens it into a real break: erosion strips one cell from each side of the
gap and the ledge filter strips one more (it reads an in-bounds *empty* column as a drop). `NavMeshVoxelGapFillCells`
(default 2, `0` = off) closes such seams. Right after rasterization - and **before** the ledge filter, otherwise
that filter would reopen the seam - a column with no walkable span is filled when walkable spans flank it on
**opposite sides along one axis** (N/S, E/W or either diagonal) with an empty run of at most `gapFillCells`
cells and a floor difference within the agent climb; the filled span takes the **lower** neighbour floor. Only
cells inside a tile's own area are filled and only columns inside that tile's padded field are probed, so
exactly one tile decides each world cell (identical across a tile boundary - no new cracks).

Harness `Test 16` (two floors with a gap, union-find connectivity):

| scenario | cap | result |
|---|---|---|
| 1-cell gap | 2 (default) | connected |
| 1-cell gap | 0 (off) | broken |
| 2-cell gap | 2 (default) | connected |
| 2-cell gap | 1 | broken |
| 3-cell gap | 2 (default) | broken (over the cap) |
| 2-cell gap, floors 2 m apart | 2 (default) | broken (delta > climb) |
| 1.5 m ditch with a floor below | 2 (default) | broken (real ditch) |

On all existing harness scenes the fill fires zero times, so face counts are unchanged.

### Stitching

Gap bridging only closes seams the rasterizer stores as empty columns. A seam can also survive as **two
disconnected mesh components** (the collision gap rasterized a cell on one side, the contour inset another).
`NavMeshVoxelStitchMaxGapCells` (cells, `0` = off) runs **per tile, after the tile's mesh is built**: it finds
the tile's separate components, and for each pair whose nearest boundary edges are within the cap and within
`walkableClimb` it adds a two-triangle quad that **reuses existing boundary vertices** (so both weld paths stay
exact). It is refused when the tile's solid spans put geometry inside the gap envelope - a wall, a closed door -
or leave less than `walkableHeight` of headroom (a ceiling). Deterministic and per-tile only, so tile order
cannot change the result. New edges are only added when they are fresh, and the invariant gate stays green
(no duplicate faces, no edge shared by more than two faces, no self-loops).

**Calibration (measured, harness Test 18):** at cs 0.25 / radius 0.25 a real seam (a 1-cell raster gap)
becomes a **1.25 m = 5-cell mesh gap** after erosion and the contour inset. The default `6` therefore covers
the measured case with margin (`5` is the minimum that works; `4` is one cell short and can serve as a
conservative cap if a bridge ever appears where it should not). `0` disables stitching.

Harness `Test 18` (union-find connectivity):

| scenario | cap | result |
|---|---|---|
| 1-cell raster seam, clear air | 5 | connected (2 faces added) |
| same seam | 4 | broken (calibration row: one cell short of the 5-cell gap) |
| same seam | 0 (off) | broken |
| same seam with a wall in the gap | 5 | broken (refused) |
| same seam with a low ceiling | 5 | broken (refused) |
| floors 2 m apart in height | 5 | broken (delta > climb) |

### Island pruning

After the merge and the non-manifold repair, *small isolated islands* are removed: fragments with at most
`NavMeshVoxelPruneMaxIslandFaces` faces (default 400) when `NavMeshVoxelPruneIsolatedIslands` is on. This is
what clears sealed-void lids and stray patches (they cannot be reached by an agent anyway); the big networks
(terrain, castle structure) are never touched because they are far above the cap. `0` (or the bool off)
disables it. The `v2: prune: ...` line reports what was removed and where.

Harness `Test 19`: a big terrain island (> cap) is bit-identical with pruning on/off; a small detached
platform island is pruned with the default and kept when the cap is 0.

### Stairs & ladders - `NavMeshVoxelSteppedProps`, **off by default**

Two mechanisms for props whose body flags include `HasSteps` (`0x800`) or `Ladder` (`0x40`) - Birtu has 84 of
the former and 2 of the latter. **Everything else keeps the ordinary 50 deg cap** (the builder's
`effectiveMaxSlope`).

**1. Stairs: the prop's own treads are exempt from the ledge trim.** Thin stepped collision loses its spans to
the rasterizer (erosion eats a 0.26 m tread), and what survives is then deleted by `FilterLedgeSpans`, whose
rule (a walkable neighbour's floor spread greater than `walkableClimb`) makes any surface steeper than
`atan(climb / (2*cs))` = 50.19 deg a "ledge" - that is why a 51-56 deg flight stays bare even though every
tread is horizontal and walkable. The builder now runs a per-tile **marker pass** between
`FilterLowHangingWalkableObstacles` and `FilterLedgeSpans`: it marks every heightfield cell covered in XZ by
the stepped-prop triangles (and by any synthesized ramp triangle), remembers the spans those cells held as
walkable, and **restores exactly those** afterwards. `FilterWalkableLowHeightSpans` still runs last, so a
ramp cannot become a low ceiling. Ordinary geometry in the same tile is not marked and keeps the 50.19 deg
cap, and a build with no mask is byte-identical to before (neutrality `MATCH`).

**2. Ladders/holes: `SteppedPropRamps.cs` synthesizes a ramp ONLY across unsupported stretches.** The flight's
run is sampled along its direction (station step 0.25 m, finer on short flights), and a station is *supported*
when the prop's own collision has a top surface there. Every maximal **hole** (no geometry) is bridged by a
quad that starts one station past the geometry on each side, so a ramp can never hover above an existing
tread; holes whose rise is inside the agent climb are ignored (the rasterizer already walks over them), holes
wider than 1.5 m break the chain, and the rise/slope/run caps plus a 4-ramp cap bound the rest. The chain's
ends get true-level anchors (the flight's own `zMin`/`zMax`), and the emitted triangles are **forced
walkable** (`RC_WALKABLE_AREA`) regardless of slope.

A claim is no longer "synthesize a ramp over the flight": it is "bridge the holes and let the exempt treads
carry the rest".

Harness `Test 23` with the setting on - all four now connect, and all four controls (setting off) stay broken:

| case | off | on |
|---|---|---|
| a) steep stair with a 1.05 m mid-flight hole | broken | connected (ramp over the hole) |
| b) 62 deg ladder, 5 rungs, floor->floor | broken | connected (4 hole bridges) |
| c) two rungs-ladders sharing a platform | broken | connected (3+3 bridges) |
| d) chained rungs-ladders, apron to base | broken | connected (3+3 bridges) |

**Measured on the recorded Birtu snapshot** (`--ramps on` vs `--ramps off`, shipped defaults):

| | off | on |
|---|---|---|
| `v2: stepped tris:` | - | `forced-walkable=0 ledge-exempt=14474/665317` |
| ramps | - | 0 synthesized (18 props: 12 flat, 2 steep, 4 long - no bridgeable holes on Birtu) |
| `v2: stepped: markedCells=...` | - | `markedCells=38312 restored=21105` |
| mesh | V=32 836 F=50 957 E=84 531 | V=33 408 **F=51 643** E=85 817 |
| islands | 5 | **6** (one extra island) |

Per-instance `--coverage` (the exemption is the only active mechanism on Birtu):

| family | off -> on |
|---|---|
| `stairs_bo` (6) | 39.1->50.4, 45.7->51.5, 51.6->63.9, 55.3->76.1, 59.6->73.4, 59.7->78.1 (all improve) |
| `european_castle_ladder` (6) | 61.9->71.4, 65.9->69.6, 67.1->69.5, 69.0->73.1, 69.4->72.7, 69.8->74.1 (all improve) |
| `wooden_platform_stairs_a` (6) | 30.2->55.8, 35.3->43.5, 53.8->63.2, 58.9->69.1 **improve**; 55.9->40.4, 59.1->58.0 **regress** |

So the setting is a clear win for `stairs_bo` (the V3.11 regressions 45.7->38.3 / 39.1->35.0 are gone, now +9 to
+20 points each) and for `european_castle_ladder`, and improves four of the six wooden flights - but two
`wooden_platform_stairs_a` instances lose coverage (the exempt treads fragment their surface and the
low-height filter drops spans under the coarse plane the rasterizer had built) and one extra island appears.
**The default therefore stays off** until those two residuals are fixed; the exemption and the hole-only ramps
are otherwise measured and safe (invariants PASS, level bands within +/-10 faces, neutrality `MATCH`).

### Terrain holes (false lids)

When a scene carves a hole/void in the terrain (a shaft, a tunnel mouth), the terrain heightfield our soup is
built from can still span it, producing a **false walkable lid** over the void - an agent standing on it falls
through. Decompiled evidence (dnSpy, 1.4.8 assemblies):

- The **only** hole-aware terrain API is `Scene.GetTerrainHeight(Vec2 position, bool checkHoles = true)`
  (interface `IScene.GetTerrainHeight(UIntPtr, Vec2, bool)`; `[EngineMethod("get_terrain_height", ...)]`).
  The shipped `HeightMapExtractor` mod and the game's own spawn/formation snapping all call it with
  `checkHoles: true`.
- The **bulk source this sampler prefers** - `Scene.GetTerrainData` + `Scene.GetTerrainNodeData` +
  `Scene.GetTerrainHeightData` (which calls `IScene.FillTerrainHeightData` into a `float[]` per node) - has
  **no** hole parameter and **no** hole array. A full dump of `Scene`'s terrain methods shows no hole/carve/
  mask/enumeration API at all (only `GetTerrain*`, `SetLandscapeRainMaskData`, `HasTerrainHeightmap`,
  `ContainsTerrain`, terrain raycasts).
- ÃƒÂ¢Ã¢â‚¬Â¡Ã¢â‚¬â„¢ Terrain holes are **not enumerable and not representable in the bulk node arrays**; the only observable
  signal is a per-point disagreement between the stored height and the hole-aware query.

So the fix is: audit the sampled vertices against the hole-aware point query and **drop the terrain cells that
disagree** (`TiledNavMeshBuilder.BuildTerrainSoup(..., bool[] holeMask, ...)` skips any cell whose four
corners touch a hole). This is `NavMeshVoxelTerrainHoleAudit`, **off by default** because it costs one native
point query per terrain vertex (Birtu's grid is 385x385 ~= 148k vertices; Na'ilu's is ~1M). On Birtu the
existing 324-sample cross-check matched the hole-aware query 324/324, so no hole was visible at those probes -
if the tunnel lid persists with the audit on, the `NavMeshVoxelWriteSoupObj` dump tells you whether the lid is
terrain (drop it) or a genuine asset surface. The harness proves the mechanism (`Test 15`: control has a lid;
with the mask the lid is gone and the asset floor below is present and connected).

A second, different lid cause is **real geometry inside a sealed void** - e.g. the tops of `sturgia_dungeon_corridor_c`
modules under a `large_plank_floor_a` ~4 m above, with no agent access. That is not a terrain problem, so the
always-on `v2: islands=...` line is emitted as data: connected components by face adjacency with size, area,
centroid and Y range. A pruning rule can be chosen from it (small area, isolated, within a height band).

### How to read the v2 log lines

Every run also logs the terrain read (see above), then the voxel core:

```
NavMesh: v2: core engaged (tiled voxel). cellSize=0.25m tile=16m
NavMesh: terrain hole audit: verts=<n> holes=<n> (tol 1m, auditMs=<n>)   <- only when NavMeshVoxelTerrainHoleAudit=true
NavMesh: terrain hole audit: <n> stored vertices disagree with GetTerrainHeight(checkHoles=true); their cells are dropped from the terrain soup
NavMesh: assets: preloading <M> unique physics bodies by name
NavMesh: assets: preloaded <M> bodies by name (preloadMs=<n>)
NavMesh: assets: progress entities=<n>/<total> included=<n> tris=<n>      <- every 250 entities (read pass)
NavMesh: assets: TRACE 'entity' body='<shape name>' flags=0x........ mobility=.. dyn=.. meshCount=.. tris=..   <- only when NavMeshVoxelAssetTrace=true
NavMesh: assets: entities=... physics=... included=... excluded=[flora=.. barrier=.. terrain=.. ladder=.. dynamic=.. noShape=.. empty=.. exceptions=.. unsolid=..] tris=... cappedShapes=... scanMs=...
NavMesh: assets: kinds stairs=.. wall=.. floor=.. other=.. (name heuristic; log only)
NavMesh: assets: stepped: ramps=<n> stairs=<n> ladders=<n> holes=<n> noHoles=<n> skipped=<n> (ramps bridge unsupported stretches only; treads are ledge-exempt)   <- only when NavMeshVoxelSteppedProps=true
NavMesh: assets: stepped: skipped detail flat=<n> steep=<n> long=<n> capped=<n>; band=0.5m ...   <- only when something was skipped
NavMesh: assets: largest shapes: '..'=.. '..'=.. ... (top 20 by triangle count)
NavMesh: v2: terrain hole mask applied: cells touching a hole are dropped from the soup   <- only when holes were found
NavMesh: v2: soup terrain=..v/..t + assets=..v/..t -> ..v/..t (merged; lifted uniformly by +0.5m)
NavMesh: v2: soup verts=... tris=... (terrain only) stride=... spacing=...m      <- only when NavMeshVoxelIncludeAssets=false or no assets found
NavMesh: v2: settings cs=...m ch=0.2m tile=16m cellsPerTile=32 tiles=10x10 border=4 hf=40x40 origin=(..) bminY=.. bmaxY=..
NavMesh: v2: agentClimb=0.6m (auto) walkableClimb=3 voxels effectiveMaxSlope=atan(walkableClimb*ch/(2*cs))=50.194deg (setting=50deg)
NavMesh: v2: params slope=50deg radius=0.25m height=1.8m climb=0.6m walkable(r,h,c)=1/9/3 edgeLen=12vox vpp=6 regionMin=8 regionMerge=20 gapFill=2 stitch=6 prune=400 ledgeFilter=True detail=True detSampleDist=2m contourSimpErr=1.3vox
NavMesh: v2: contour maxError=1.3vox (= 0.325m at cs=0.25m; NavMeshVoxelContourMaxError, 0=auto=MaxErrorMeters/cs)
NavMesh: v2: workers=12 (requested 12, tileCount=100)
NavMesh: v2: tris input=... culledByAabb=... rasterized=... (rasterized counts per-tile border duplicates)
NavMesh: v2: forced-walkable tris=<n>/<m> (synthesized stair/ladder ramps; marked walkable regardless of slope)   <- only when ramps exist
NavMesh: soup OBJ written: <folder>\<prefab>_soup_terrain.obj      <- only when NavMeshVoxelWriteSoupObj=true
NavMesh: soup OBJ written: <folder>\<prefab>_soup_assets.obj
NavMesh: v2: tiles=100 built=100 failed=0 workers=12 polys=...
NavMesh: v2: gap fill: filled=<n> emptyColumns=<n> (cap=2 cells, before erosion)   <- only when NavMeshVoxelGapFillCells>0
NavMesh: v2: stitch: added=<n> faces across tiles (cap=<n> cells)                     <- only when NavMeshVoxelStitchMaxGapCells>0
NavMesh: v2: stepped tris: forced-walkable=<n> ledge-exempt=<n>/<m> (ramps walkable regardless of slope; stepped-prop treads kept off the ledge trim)   <- only when NavMeshVoxelSteppedProps=true
NavMesh: v2: stepped: markedCells=<n> restored=<n> (ledge-trim exemption)   <- only when NavMeshVoxelSteppedProps=true and stepped props are present
NavMesh: v2: winding: flipped=<n>/<total> asset triangles (downward-wound)          <- per-triangle asset winding normalisation
NavMesh: v2: merge-stitch: pairs=<n> added=<n> refused=<n> (cap=6 cells)             <- cross-tile stitch over the merged mesh, before the prune
NavMesh: v2: prune: removed <n> islands, <n> faces (<=400 faces each); largest removed=<n> at (x,y,z); kept=<n> islands
NavMesh: v2: weld verts=... -> ... faces=... edges=... degenerate=... (weldMerged=...)
NavMesh: v2: invariants: faces=... edges=... boundary=... maxEdgeMult=.. dupFaces=.. selfLoops=.. minEdges=.. -> PASS
NavMesh: v2: islands=<n> totalFaces=... largest=[0:faces=... area=...m2 centroid=(x,y,z) zr=[min..max] | 1:... ] top10
NavMesh: v2: mesh V=... E=... F=... degenerate=... binBytes=... buildMs=...
NavMesh: v2 timings: tilesMs=... mergeMs=... totalMs=...
NavMesh: v2: tileMs min/avg/max=.../.../...
```

- `v2: tris input=... culledByAabb=...` - input triangles, ones outside every tile, and the number actually
  rasterized (higher than `input` near tile borders because each tile sees its neighbours' geometry).
- `assets: ...` (main thread, before the build) - the collision-geometry scan: entity/physics/included counts,
  the per-reason exclusion counts, total triangles, any capped shapes and the scan time (`scanMs`); plus a
  name-heuristic kind histogram and the twenty largest shapes. `assetsMs` on the perf/`done:` lines is the same
  scan's wall time.
- `v2: soup terrain=... + assets=...` - the merged Recast-space soup size; the uniform lift applied to the
  whole mesh (`NavMeshHeightOffsetMeters`) is stated in the line.
- `terrain hole audit: ...` / `v2: terrain hole mask applied: ...` - only when `NavMeshVoxelTerrainHoleAudit`
  is on: how many sampled vertices disagreed with the hole-aware point query and therefore had their cells
  dropped from the terrain soup.
- `soup OBJ written: ...` - only when `NavMeshVoxelWriteSoupObj` is on: engine-space dumps of the terrain
  and asset soup for geometry provenance.
- `v2: tiles=... built=... failed=...` - failed tiles are also logged individually as `v2: FAILED tile#...`
  with the first exception message; they are never silently dropped.
- `v2: gap fill: filled=... emptyColumns=...` - columns bridged by `NavMeshVoxelGapFillCells` and how many
  empty columns were candidates (the fill fires zero times on open terrain, so face counts are unchanged).
- `v2: stitch: added=... faces across tiles` - connector faces added by the per-tile stitch (0 = no near-miss
  cuts found at that cap).
- `v2: prune: removed ... islands, ... faces` - islands dropped as too small, the largest one's size and
  position, and how many islands were kept.
- `v2: weld verts=... -> ... faces=... degenerate=...` - cross-tile welding, dropped zero-area faces.
- `v2: islands=... totalFaces=... largest=[...]` - always on and cheap: connected components by face adjacency,
  with the ten largest by area (`faces`, `area` m^2, area-weighted `centroid`, and the island's Y range `zr`).
  This is the data used to choose a rule for pruning sealed-void lids.
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
