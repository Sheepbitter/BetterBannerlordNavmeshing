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
| `NavMeshPrefabIndex.cs` | **Managed mirror of the engine's navmesh-prefab index** (see `NavMeshPrefabIndex.md` for the full disassembly record). Decides whether `Scene.ImportNavigationMeshPrefab` is worth calling at all, and says exactly why not. Rule: a name resolves **only** if a **loaded** module (`Utilities.GetModulesNames()` - the `_MODULES_*A*B*_MODULES_` list, matched by each module's `SubModule.xml` `<Id>`, *not* by folder name) already had `Modules\<M>\NavMeshPrefabs\<name>.bin` when the engine built its index at start-up. Anything else is skipped, which is what keeps the engine's own `No navigation mesh prefab was found with name "<name>"!` out of the log. When the scene's own module is **not** loaded, the same bin is additionally written to this module's `NavMeshPrefabs` (loaded by definition, folder created by `build.ps1`) so the name works from the **next** editor start. Also owns the **scene -> module** resolution (`SceneModuleMatch` / `FindSceneModuleMatches` / `ChooseSceneModule`), because a scene name does not say which module it is in and two module folders can hold `SceneObj\<the same name>`. |
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

**Freshly written prefab workflow.** The engine builds its navmesh-prefab list at **startup**, from the
`*.bin` files that already exist in the `NavMeshPrefabs` folder of every module the editor **loaded**
(launcher -> `_MODULES_*A*B*_MODULES_` -> `Utilities.GetModulesNames()`). Two rules follow, and the
`prefab index:` log block says which one you hit:

* **The name is new** (`absent` in the block): the first Export cannot find the prefab it just wrote. Then
  either: (1) Export (writes the bin) -> **restart the editor** -> **Import Only** (fast; imports the existing
  prefab, no rebuild), or (2) Export -> restart -> **Export** again (also works - it just rebuilds and
  rewrites the same bin).
* **The bin exists but its module is not loaded** (`FOUND-inactive` in the block): the scene lives in a
  module that is not enabled in the launcher, or in a folder that is not a module at all (no
  `SubModule.xml`). The engine never scanned it, so the name can never resolve there - restarting does not
  help. The tool therefore **also writes the same bin into this module's `NavMeshPrefabs`** (kept as a
  copy; the canonical one stays in the scene's module, which is what the game and the dedicated server
  load), logs
  `import SKIPPED ... scene module not active ... wrote a copy to 'BetterBannerlordNavmeshing' - restart the
  editor once, then Import Only`, and stops there. **Restart the editor once**, then **Import Only** works -
  and from then on in-session imports of that name are normal. Enabling the scene's module in the launcher is
  the other fix, and the only one that makes the *game* and the dedicated server find the prefab too.

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
NavMesh: prefab index: folder=...\Modules\<Mod>\NavMeshPrefabs folderExistedBefore=<bool> binExistedBefore=<bool> module=<Mod> moduleId='<id>' moduleActive=<bool|unknown>
NavMesh: prefab index: name='<scene>_navmesh' length=<n> ok
NavMesh: prefab index: loaded modules: TaleWorlds.Engine.Utilities.GetModulesNames() (native get_modules_code, ...)
NavMesh: prefab index:   FOUND          ...\Modules\<Mod>\NavMeshPrefabs\<scene>_navmesh.bin module=<Mod> moduleId='<id>' loaded=True indexed=True  <- this run
NavMesh: prefab index:   FOUND-inactive ...\Modules\<Other>\NavMeshPrefabs\<scene>_navmesh.bin module=<Other> moduleId='<id>' loaded=False indexed=False (module-not-loaded)
NavMesh: prefab index: verdict: 1 of 2 existing copies of '<scene>_navmesh' are in the engine's index (<n> module folder(s) scanned)
NavMesh: prefab index: write folder: ...\Modules\<Mod>\NavMeshPrefabs (module '<Mod>', moduleId '<id>', is loaded in this session, ...)
NavMesh: writing bin: ...\<scene>_navmesh.bin
NavMesh: existing navmesh faces: N
NavMesh: importing prefab '<scene>_navmesh' (shift 0)...
NavMesh: import call returned.
NavMesh: faces after import: N
NavMesh: <F> faces, <V> vertices, <E> edges (culled: <S> slope, <H> height)
```

NavMesh: bin written: ...\<scene>_navmesh.bin
NavMesh: faces imported into scene as '<scene>_navmesh' (static).
NavMesh: import check: existing=<before> afterImport=<after> clearExisting=<bool> increased=<bool>
NavMesh: import warning: no faces appeared. Either this prefab name was not in the engine's index (it is built from the *.bin files that existed, in the modules that were loaded, when the modules loaded; nothing re-runs it - look for a 'FOUND-inactive' or 'FOUND-new' line above), or the imported bin was empty for this scene.
NavMesh: timings: buildMs=<n> writeMs=<n> objMs=<n|0> clearMs=<n> importMs=<n> facesAfterMs=<n>
NavMesh: done: totalMs=<n> bin=<n> bytes V=<v> E=<e> F=<f>
```

### Which module gets the write (`folder:` block)

A scene name does **not** say which module it belongs to - the engine composes the
relative tail `SceneObj/<scene>` and finds the owner itself - so more than one
module folder can hold `SceneObj\<the same name>`. On this install two do:

| folder | contents | dated |
|---|---|---|
| `Modules\MyMaps\SceneObj\CC_20_siege` | the scene that was open and saved (`flowmap.dds`, no `navmesh.bin`) | 2026-10-01 22:15 |
| `Modules\DedicatedCustomServerHelper\SceneObj\CC_20_siege` | a stale shipped copy, **with** a baked `navmesh.bin` | 2023-10 |

`Modules\MyMaps\SceneEditData\CC_20_siege\terrain_ed.bin` exists too, and the
engine derives that path as `<module_path_>SceneEditData/<scene>`, so the open
scene's `module_path_` was `...\Modules\MyMaps\`. Taking the first match in
`Directory.GetDirectories` order picked the other one, because NTFS lists entries
alphabetically and `DedicatedCustomServerHelper` sorts before `MyMaps` - which is
why the previous run left a 21 MB `CC_20_siege_navmesh.bin` in
`Modules\DedicatedCustomServerHelper\NavMeshPrefabs` (a folder whose parent is
not a module at all) and none in `Modules\MyMaps\NavMeshPrefabs`.

`ResolveFolder` therefore resolves in this order, and `NavMeshExportFolder` still
wins over all of it:

1. **`NavMeshExportFolder`** - an explicit override, used verbatim.
2. **`Scene.GetModulePath()`** - the engine's own answer (`IScene.GetModulePath`
   RID 1217, `[EngineMethod("get_module_path", ...)]`, returning
   `rglScene::module_path_`: the module directory the scene was loaded from).
   Accepted only when that directory really has a `SubModule.xml`.
3. **A ranked `SceneObj` search.** Every module folder holding
   `SceneObj\<scene>` is listed and ranked; the highest rank wins:

   | rank | match |
   |---|---|
   | 3 | a module this session **loaded** - the only kind of folder whose bins the engine's index can hold |
   | 2 | loaded but not a module (unreachable while the loaded set is known) |
   | 1 | a real module this session does **not** load - still where a correct install reads it |
   | 0 | not a module at all - nothing written here can ever be indexed |

   Ties keep the first in listing order **and say `AMBIGUOUS`** instead of
   pretending the choice was evidence-based.
4. The module containing this script's own assembly (last resort).

Every match, its rank, the rule and a cross-check against what
`Scene.GetModulePath()` said are printed, so the decision is never invisible:

```
NavMesh: folder: scene 'CC_20_siege' - module from Scene.GetModulePath(): D:\...\Modules\MyMaps
NavMesh: folder:   (the engine's own answer: native get_module_path returns rglScene::module_path_, the module directory the scene was loaded from; it has a SubModule.xml, so it really is a module)
```

```
NavMesh: folder: rejected Scene.GetModulePath() result (not a module): D:\...\Modules\DedicatedCustomServerHelper
NavMesh: folder: scene 'CC_20_siege' is in 2 module folder(s). The engine resolves a scene by name, not by module, so every one of them matches; they are ranked, not taken in folder order:
NavMesh: folder:   ignored D:\...\Modules\DedicatedCustomServerHelper module=DedicatedCustomServerHelper moduleId='' NOT-A-MODULE (no SubModule.xml) loaded=False rank=0 (not a module: a prefab written here can never be indexed)
NavMesh: folder:   CHOSEN  D:\...\Modules\MyMaps module=MyMaps moduleId='My Maps' loaded=True rank=3 (a module this session loaded)  <- this run's write folder
NavMesh: folder:   rule: prefer the module folder the engine loaded in this session - it is the only kind of folder whose NavMeshPrefabs bins the engine's index can hold; winner module 'MyMaps'
NavMesh: folder:   cross-check: Scene.GetModulePath() said 'D:\...\Modules\DedicatedCustomServerHelper', which was REJECTED above (it is not a module folder), so the ranking decided instead.
NavMesh: folder: module containing the scene: D:\...\Modules\MyMaps
```

Evidence and the native record: `NavMeshPrefabIndex.md` §3.6 and §5.7.

The `prefab index:` block is the mirror of the engine's own index (see
`NavMeshPrefabIndex.md`). Read it before anything else when an import misbehaves:

- `loaded=True indexed=True` - the engine can and will resolve this name. If faces still do not appear, the
  bin is empty for this scene.
- `FOUND-inactive` - the bin is on disk but its module is not loaded in this session. The engine never
  scanned it; the tool copies the bin into this module and skips the import (see *Freshly written prefab
  workflow*).
- `FOUND-new` - the bin was written by this session, so it is not in the index yet; restart the editor.
- `FOUND-unchecked` - the loaded-module set could not be read (the engine was not up); nothing is vetoed.
- `moduleActive=unknown` on the `folder=` line, or `loaded modules: UNKNOWN`, means the same thing.

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
| `NavMeshConsoleMessages` | `true` | The log file above stays exactly as verbose as it always was; this additionally mirrors a throttled selection of it to the editor console / the player (see *Console progress*). |

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
| `NavMeshExportFolder` | `""` | Override for the output folder, and the highest-priority rule in the folder resolution (see *Which module gets the write*). Use it to pin the module when the ranked `SceneObj` search reports `AMBIGUOUS`. It must be the `NavMeshPrefabs` of a **loaded** module (has a `SubModule.xml` **and** is enabled in the launcher) for the in-editor import to find it; the `prefab index:` block reports `loaded=` per module. When the resolved folder's module is not loaded, the bin is additionally copied into this module's `NavMeshPrefabs` (see *Freshly written prefab workflow*). |
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
| `NavMeshVoxelCleanup` | `true` | v2 final-mesh sliver cleanup (thin/tiny triangles). Runs after merge/stitch/prune and before the invariant gate: welds, drops degenerates, then **inserts** a midpoint into long thin faces (first, so the later passes clean its children), **removes** the near-collinear vertex of a needle/thin face and retriangulates its hole, slides ratio needles, and finishes with a Delaunay-style flip polish. Everything is bounded by the mesh's own error promise and the topology gate; see *Sliver cleanup*. `false` = the pre-cleanup mesh (A/B). Log: the `cleanup:` line (+ a capped per-needle diagnosis when any survive). |
| `NavMeshVoxelQuadMerge` | `true` | Final **quad merge** (part of the cleanup, after the flip polish). A still-thin face (min angle < 6 deg OR aspect `longest^2/(2*area)` > 8) is combined with a near-coplanar neighbour across a shared edge into ONE emitted 4-vertex face (fold <= 25 deg, four distinct vertices). **The merged quad must be strictly convex in BOTH frames** - in its own plane *and* in the engine's own XZ ground projection (within 175 deg, winding preserved) - because the engine's navmesh validator rejects a concave face ("Concave Navigation Mesh Face Detected" on import), so a reflex-cornered pair is left as two triangles and the emitted `is_concave` is 0 everywhere. **Geometry is unchanged** - the triangle list and every vertex stay put; only the shared interior edge disappears from the emitted perimeter, so there is one fewer face per merge and no geometric risk. Log: `quads=N quadsXZworst=<deg>deg` on the `cleanup:` line. `false` = triangles only (A/B). See *Sliver cleanup*. |
| `NavMeshVoxelGatePlateBlend` | `true` | Rotates a prop's rigid **dead-flat floor slab** onto the surrounding ground so both ends meet the navmesh inside the agent climb (the reported case: `european_castle_wall_gate_l1` on CC_20 projecting a perfectly level plate at y=86.30 across a passage whose terrain falls 87.1 -> 82.8 m, so the far end met faces 1.20 m below it and the plate's tip was a dead end). Classified generically - a maximal edge-connected group of dead-flat up-facing faces on ONE `ch` level, 3..120 faces, <= 8 m a side, standing more than one agent climb (and at most 2.5 m) above the 10th percentile of the mesh around its own footprint. The plane is least-squares fitted to the **built mesh** in a 2 m ring around that footprint (never to the raw soup, and never to our own synthesized ramp cells), the tilt is clamped to `NavMeshMaxSlopeDegrees` **and** to `2*climb/halfSpan`, and the offset is the median residual clamped to `[-climb, 0]`. **Only the plate's own vertices move**: no face is added or removed, no edge is touched, and no plate that already meets its surroundings inside the climb is touched at all. Measured: CC_20 gate worst neighbour mismatch **1.60 -> 0.495 m**, plate 0.0 -> 13.0 deg, dead-end tip 1.20 -> 0.23 m from its neighbour; islands, `selfOverlap` and `--scan OVERLAPpairs` unchanged on CC_20 / Birtu / Basamu, suite byte-identical. Then a second stage, **the seam**, bridges a genuine void next to a blended plate - two triangles reusing the four existing vertices so the engine gets an edge portal - only where the gap is 0.1-1.5 m, the step is inside the climb, the strip is no steeper than `NavMeshMaxSlopeDegrees` and its own footprint is empty; 71 / 80 / 110 voids closed on CC_20 / Birtu / Basamu with the island counts unchanged and the suite byte-identical. `false` = the pre-pass output byte for byte. See *Gate / door plate blend*. |
| `NavMeshVoxelSteppedProps` | `true` | **Read *Stairs & ladders* before changing.** Stairs: a `HasSteps`/`Ladder` prop's own triangles are exempt from Recast's ledge trim (a per-tile marker pass restores exactly the spans the trim nulled; `v2: stepped: markedCells=<n> restored=<n>`), which is what a 51-56 deg flight needs - its treads are horizontal and walkable, the trim was the only thing deleting them. Everything else keeps the ordinary 50.19 deg cap. Ladders: with this on, `Ladder`-flagged bodies are **collected** instead of excluded (counted as `laddersIncluded=<n>`), and `SteppedPropRamps` bridges only the *unsupported* stretches of the run (holes <= 1.5 m and taller than the agent climb) with true-level end anchors, so no ramp ever hovers over a tread. A ladder whose collision is a slanted plank (no holes to bridge at all) instead gets one straight 1.2 m-wide ramp chain from its own bottom to its top (V3.18), and every prop that yields no ramp is named in the log with its reason (V3.18). Measured on Birtu: `stairs_bo` 6/6 improve (+9..+20 pts), `european_castle_ladder` 6/6 improve, 4 of 6 `wooden_platform_stairs_a` improve and the other 2 only *appear* to regress (the coverage window is knife-edged at `surface + 1.10 m`; their real gaps go 20->5 and 48->29, see *Stairs & ladders*); islands stay 5. The level-band criterion (faces per 1 m band within +/-10 of the off build) cannot hold while the feature adds the missing stair/ladder faces - 6 bands gain +12..+45, total +174 faces (+0.34%). |
| `NavMeshVoxelWriteSoupObj` | `false` | Diagnostic: also write `<prefab>_soup_terrain.obj` and `<prefab>_soup_assets.obj` next to the bin (engine space) and log both paths. Inspect a failing spot with geometry provenance in the editor/Blender. |
| `NavMeshVoxelTerrainHoleAudit` | `false` | Terrain-hole audit: compare every sampled terrain vertex against the hole-aware point query and drop the cells of any version that disagrees by > 1 m (a false walkable lid over a carve). Costs one native point query per vertex; see *Terrain holes (false lids)*. |
| `NavMeshAsyncBuild` | `true` | Run the v2 voxel build on a background thread so the editor UI stays alive during long builds. Applies only when `NavMeshUseVoxelCore` is on; the v1 mesher is always synchronous. See *Async build & performance instrumentation*. |
| `NavMeshConsoleMessages` | `true` | Mirror the important run lines (start / stage / progress / finish-or-failure) to the editor console, and in game to the player. The log file is unaffected. See *Console progress*. |
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

### Console progress (`NavMeshConsoleMessages`, on by default)

A v2 build is 3 s - 5 min of work, and until the prefab appears (or an error does) the editor gave no
sign of life at all: the log file was the only witness, and `Console.WriteLine` is invisible there. The
important run lines are therefore mirrored to the **editor console** through `TaleWorlds.Library.Debug.Print`
(the console's own channel; `Console.WriteLine` stays as the fallback), and in game to the player through
`InformationManager.DisplayMessage` - all of it from one helper (`NavMeshVisibleLog`) and gated by
`NavMeshConsoleMessages`. **The verbose log FILE is unchanged**: every line still goes there, in the same
order, with the same text; the console only ever gets a throttled selection of it.

**Async (`NavMeshAsyncBuild = true`, default) - the editor stays responsive:**

```
NavMesh: building 'mp_tdm_Birtu' -> 'mp_tdm_Birtu_navmesh' (background - the editor stays responsive; progress below) [Build 3 (adaptive navmesh)]
NavMesh: stage 1: collecting assets ...
NavMesh: stage 1: collecting assets 45% (+2.6s)
NavMesh: stage 2: building tiles ...
NavMesh: stage 2: building tiles 30% (+2.5s)
NavMesh: stage 2: building tiles 60% (+2.5s)
NavMesh: stage 3: merging/stitching ...
NavMesh: stage 4: pruning ...
NavMesh: stage 5: cleanup ...
NavMesh: stage 6: writing bin ...
NavMesh: stage 7: importing ...
NavMesh: DONE: 'mp_tdm_Birtu_navmesh' V=28213 F=44505 E=73455 in 61.4s -> D:\...\NavMeshPrefabs\mp_tdm_Birtu_navmesh.bin (imported)
```

**Blocking (`NavMeshAsyncBuild = false`, or the v1 mesher - `NavMeshUseVoxelCore = false`):**

```
NavMesh: building 'mp_tdm_Birtu' -> 'mp_tdm_Birtu_navmesh' (blocking - the editor will freeze until it finishes) [Build 3 (adaptive navmesh)]
...
NavMesh: DONE: 'mp_tdm_Birtu_navmesh' V=28213 F=44505 E=73455 in 61.4s -> ...\mp_tdm_Birtu_navmesh.bin (imported)
```

The freeze is therefore **announced**, not mysterious: the difference between the two lines is the only
thing that changes about the architecture. In game the blocking hotkey says
`(blocking - the game will freeze until it finishes)`.

- **Start** - one line: scene, prefab, build tag and mode, before anything is read.
- **Progress** - at most one line per stage per ~2.5 s, only when the percentage moved, and at most 4 per
  stage. The stages are `collecting assets -> building tiles -> merging/stitching -> pruning -> cleanup ->
  writing bin -> importing`; they are derived from the run's own log lines, so no mesher code changed.
- **Finish** - always the last line of the run, in **both** modes: `DONE:` with V/F/E, the elapsed time and
  the written path, or `FAILED:` with the reason (background exception, invariant-gate abort, dispatch or
  write/import error). The async path cannot leave a stale "still running" impression.
- **In game** - the same lines reach the player through `InformationManager`, but hard-throttled: at most one
  message per 20 s and 2 progress messages per run (start and finish always get through). A missing or
  throwing `InformationManager` is a no-op, not an error.
- Off-thread lines (the async worker's progress) are queued and printed from the editor tick
  (`OnEditorTick`), so the console is only ever touched from the thread that owns the UI.

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
| Ladders (`Ladder`) | excluded, **included when `NavMeshVoxelSteppedProps` is on** | Not a walkable surface by themselves and the engine handles ladders separately, so they are dropped by default. With the stepped-props feature on they are collected and the flight is synthesized like a stair's (their rungs are steep-walkable, the gaps between rungs are ramps), because a real ladder's collision is the only thing that carries a navmesh up it. Counted as `laddersIncluded=<n>`. See *Stairs & ladders*. |
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

### Vertical reach: how far can geometry *above* a surface reach?

Asked directly after a mis-diagnosis blamed an "overhang" 21.5 m above a cave spot: **can anything up there
influence the floor down here?** Audited end to end. The answer is **no, and the guarantee is 1.8 m**, not
"most of the time 1.8 m".

Every rule that lets a span influence the span beneath it, and the vertical distance it actually reads:

| rule | call site | reads | bound |
|---|---|---|---|
| clearance | `BuildTile` -> `FilterWalkableLowHeightSpans` | `span.next.smin` only - the *immediately* next span in the same column | `walkableHeight` = **1.8 m** (9 vox) |
| ledge trim | `BuildTile` -> `FilterLedgeSpans` | own column: `span.next.smin`; a neighbour span is admitted only when `min(ceiling,nCeiling) - max(floor,nFloor) >= walkableHeight`, i.e. it must overlap our own clearance band | **1.8 m** (see the two properties below for the one `smin` that is read unbounded) |
| stepped restore | `RestoreSteppedSpans` | `sp.next.smin - sp.smax < walkableHeight`; the near/far split is `up.smax - sp.smax <= walkableClimb` | 1.8 m / 0.6 m |
| connector clearance exemption | `ClearConnectorCeilings` | `up.smin - conn.smax < walkableHeight`, connector cells only | 1.8 m |
| per-tile stitch clearance | `StitchGapClear` | `minY - 1vox .. maxY + walkableHeight` | 1.8 m above the higher end |
| merge-stitch clearance | `GapEnvelopeSolid` | `soupFloor + 0.6*ch .. soupFloor + walkableHeight*ch` | 1.8 m above the lower end |
| gap fill | `FillNarrowGaps` / `TryGapFloor` | the *lowest* walkable span per column; flanks within `GapFillCells`; `abs(dy) <= walkableClimb` | 0.5 m across, **0.6 m** up |
| low-hanging obstacles | `FilterLowHangingWalkableObstacles` | spans within `walkableClimb` | 0.6 m |
| erosion | `ErodeWalkableArea` | horizontal only (`walkableRadius`) | n/a (no vertical term) |

One rule in the whole pipeline *does* read a span's `smin` at unbounded distance, and it is worth naming so
the guarantee is not overclaimed: the ledge trim's "is the neighbour column empty at my level?" test reads
`neighborSpan.smin` - the **bottom of the neighbour's lowest span** - at whatever height that is
(`RcFilters.cs:154`). If the neighbour cell holds nothing within `walkableHeight` of our floor, the trim nulls
our span as a ledge. That is DotRecast's intended "there is a wall beside you" behaviour and it is **capped in
the only way that matters**: the decision fires on `min(ceiling, neighborCeiling) - floor >= walkableHeight`,
so it turns on *how far the neighbour's lowest geometry sits above our floor*, and the effective reach is
exactly `walkableHeight`. Clamping that read to `floor + walkableHeight` was checked against all three cases
(neighbour empty -> `RC_SPAN_MAX_HEIGHT`; neighbour bottom inside the band; neighbour bottom above the band)
and is **behaviour-identical**, so the clamp is left as documentation rather than churn in vendored code.

Two further properties make the ledge trim safe for arbitrarily distant overhangs, and they are worth stating
because they are not obvious from reading it:

- A neighbour span is only *considered* when it vertically overlaps our own clearance band, so a span far
  above is normally skipped outright.
- When it is not skipped (our column has no ceiling, so our `ceiling` is `RC_SPAN_MAX_HEIGHT`), the far-above
  span can only contribute a **positive** `neighborFloorDifference`. `lowestNeighborFloorDifference` is a
  `min()` that is only ever driven **negative** by a span *below* us, and the second delete rule
  (`highest - lowest traversable > walkableClimb`) only reads floors within `walkableClimb`. So an overhang
  cannot null the floor under it no matter how far above it sits.

**Measurement at the disputed spot (CC_20, `x 1113.05, z 965.89`, floor at engine H=98.5):** a
`--vprobe` of a 2.2 x 2.2 m box around it returns 57 asset vertices and the **highest is `rock_set` at
H=97.770**; `rock_cliff_b` tops out at 95.528 and `terrain_green_rock_big_c` at 93.532. There is **no geometry
at all between 98 m and 143.9 m in that column** (`--probe` reports `soupZ=[86.737..143.94]`, and the
143.94 m vertex is 45 m away in XZ, not overhead). The built mesh has exactly **one** face at that XZ,
`h=98.50`, in the main island (226 082 faces) - the spot is covered and connected, not starved. So the
"overhang" never existed there; whatever removed the cave floor is something else, and per the standing
instruction that is parked.

The same log line already prints the bound on every run, so a regression would be visible without re-deriving
it: `v2: params slope=50deg radius=0.25m height=1.8m climb=0.6m walkable(r,h,c)=1/9/3 ...`.

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

**V3.17: the cross-tile (merge-level) clearance probe is obstacle-dense.** The merge-stitch runs after the merge
and re-checks clearance against the *input soup* (a coarse XZ grid of soup triangles); the per-tile pass above
checks the tile's *heightfield spans* instead, which do represent solids. The soup probe only had surface
triangles to work with, and it sampled a single line at up to one cell (0.25 m) spacing, so it missed obstacles
two ways: a thin block could fall between samples or lie beside the line, and - because a solid's interior is
covered by no triangle at all (a box's vertical faces project to a degenerate segment in XZ) - a sample *inside*
the block was seen by nothing. The probe now:

1. samples the stitch line at `StitchProbeSpacingMeters` = **0.1 m** (never coarser),
2. also samples 4 parallel lines **±0.1 / ±0.2 m** perpendicular (`StitchProbeOffsets`), i.e. across the width of
   the stitch quad, where a thin obstacle under the bridge would hide,
3. and for each 0.1 m sub-segment additionally refuses when a triangle that spans the envelope is **crossed** by
   it (`SegSegCross2`, proper interior crossings; mere touches are left to the inclusive point tests).

The envelope rule itself is unchanged (`envLo = soupFloor + 0.6*ch`, `envHi = soupFloor + walkableHeight*ch`), the
pass stays deterministic, and with the stitch off nothing runs.

Measured on the recorded Birtu snapshot (`--ramps on|off`, cap 6):

| | pairs | added | refused | mesh | islands |
|---|---|---|---|---|---|
| before (V3.16) | 745/747 | 158/164 | 666/665 | V=32836/32930 F=50957/51131 | 5 |
| after (V3.17) | 748/750 | **0/0** | 748/750 | V=32405/32499 F=50525/50699 | 5 |

The probe now *finds* the same pairs and refuses all of them, because every seam it proposes on Birtu sits over
terrain that rises more than `0.6*ch` inside the gap - the rule as written refuses that, and the old `added=164`
were pairs that passed only because the sparse samples happened to miss the rising ground. Nothing observable
regressed: `--pair` on the two previously-fixed gap locations
(`268.46,309.348,20.5|274.718,316.501,20.5` and `327.099,285.436,20.5|331.135,286.884,20.5`) still reports
`PASS`, `coverage=100.0%`, `gapMaxRun=0`, `ratio=1.065`/`1.131` - and they pass with the stitch *off* too, so
they never depended on it; and the `--coverage` tables for all 18 `stairs_bo` / `european_castle_ladder` /
`wooden_platform_stairs_a` instances are **bit-identical** before and after in both modes. The 164 faces the pass
used to add were what kept 82 small fragments (268 faces) out of the pruner (ON: `prune: removed 882 islands,
13704 faces` -> `964 islands, 13972 faces`); those fragments are pruned now since the only thing ever joining them
was an unverified stitch. If the merge-stitch is to keep bridging seams on *sloping* ground, the lever is the
envelope's lower bound (`0.6*ch` sits below `walkableClimb`, so a step up to the climb counts as an obstacle) -
that is a rule change, deliberately not made here.


Harness `Test 18` (union-find connectivity):

| scenario | cap | result |
|---|---|---|
| 1-cell raster seam, clear air | 5 | connected (2 faces added) || same seam | 4 | broken (calibration row: one cell short of the 5-cell gap) |
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

With `NavMeshVoxelSteppedProps` on, the staircase exemption can glue several such fragments into one island that
clears the cap, so the pass also prunes **by provenance**: an island whose faces, with the exemption's restored
cells taken out, fall apart into cores no bigger than the cap is a coalition of prunable fragments and goes
whatever its size (see *Stairs & ladders*). Real networks keep a core far above the cap and are untouched.

Harness `Test 19`: a big terrain island (> cap) is bit-identical with pruning on/off; a small detached
platform island is pruned with the default and kept when the cap is 0.

### Sliver cleanup (`NavMeshVoxelCleanup`, on by default)

The detail phase tessellates every polygon edge and adds an interior sample grid; in complex areas (a notched
outline over an open yard is the classic) the result contains long thin **needle** triangles - visually ugly
and, more importantly, expensive to route (every needle is another polygon a path query has to touch). This
was the last quality gap versus the native generator.

The pass runs on the final merged mesh, after the weld/stitch/prune and before the invariant gate. The order
is deliberate: the **insert** pass runs first, so the removal and slide passes remeasure and clean up whatever
it creates.

1. **Weld** vertices within 1 mm and drop degenerate/duplicate faces.
2. **Refine (long), runs FIRST.** A long thin triangle - the user's "a 10 m span under 1 m wide" class, measured
   at 6-9 m on the surviving real-map needles - cannot be folded away or deleted, so it gets an extra vertex on
   its longest edge. The face qualifies when `longest^2 / (2*area) > RefineAspectRatio` (8), `longest >= 4 m`
   (catching the measured 6-9 m class with margin) and its width (`2*area/longest`) is at least 0.12 m (the
   ultra-thin ones belong to removal/slide). `longest^2/(2*area)` is the aspect ratio, `longest/altitude`; the
   min-angle metric does **not** improve under a base split (the base halves, the apex angle is unchanged), the
   aspect ratio does and roughly halves per split, so 8 sweeps converge. The split is **1-2** on an outline edge
   and **2-4** together with the face across an interior edge (found in the exact edge index below). It is the
   one **exactly surface-neutral** surgery: the inserted vertex *is* the midpoint of the split edge, so it lies
   on that edge, every child stays in the plane of the face it replaces, and no tolerance is involved. Two extra
   guards: a split that would itself manufacture a needle child is refused (the child belongs to removal/slide),
   and an interior 2-4 split is only made on a wholly interior face - a 2-4 split beside the outline perturbs
   which boundary vertices the removal pass later folds away and can shrink the walkable coverage.
3. **Decimate (short).** Repeatedly remove a vertex that is incident to a needle, a very thin face or a very
   short edge and retriangulate the hole it leaves (best-quality ear clip in one fixed best-fit plane).
   **Boundary vertices are candidates too** - a boundary needle's apex is usually a small outline wobble; the
   outline *move* is bounded, not forbidden. A vertex that hangs off a single face (a spike tip) is deleted
   with its face when the base edge is interior.
4. **Slide (medium).** The ratio needles that cannot be folded - the canonical case is a 10 m span whose
   "thin point" is 1 m wide - are usually just badly *placed*: sliding the offending vertex inside its local
   surface plane (so the height field is untouched) fattens the whole fan. Each thin vertex tries a small set
   of in-plane offsets (toward the ring's centroid, plus rings of directions out to 2 m) and keeps the one
   that maximises its worst incident angle, subject to the same bounds.
5. **Flip polish.** Delaunay-style edge flips on the remaining near-coplanar quads (strict min-angle gain).
6. **Quad merge (final, `NavMeshVoxelQuadMerge`).** A face that is still thin (min angle < 6 deg OR aspect
   `longest^2/(2*area)` > 8) is combined with a near-coplanar neighbour across one shared edge into ONE
   emitted **4-vertex face**. This is the user's "combine a needle with a nearby triangle to make a quad":
   the pipeline stays triangles-only, and the writer folds each recorded pair into a single n-gon face. The
   NMG8 format stores a per-face vertex count, that many vertex indices, that many boundary edge indices and
   an `is_concave` field, so an n-gon is a first-class face (native navmeshes are stored the same way). The
   scan is deterministic (thin faces in index order; each takes the neighbour that maximises the quad's worst
   interior angle, tie-break smallest perimeter) and **geometry is completely unchanged**: no vertex moves,
   no triangle is added, removed or reordered - merging only deletes the shared interior edge from the emitted
   face perimeters. Same surface, same coverage, zero geometric risk; it is purely a face-count (and routing
   cost) reduction. **The merged quad must be strictly convex** (see below), so the writer's `is_concave`
   comes out `0` for every emitted quad; unpaired faces keep their byte-identical 3-vertex record.

Adjacent component structure is protected twice: component counts must be unchanged, and components of 16
faces or fewer are skipped entirely (a fold could otherwise delete a small island outright).

Guards, all enforced before anything is applied: the surface may follow the replacement at most 0.50 m (the
interior plane backstop; the outline's is 0.35 m) per removal, and - the decisive bound - the surface *height*
at the removed vertex's own spot must stay within 0.15 m of what it was, the fan must be planar enough, no new
face may be thinner than 1.5 deg, the component count must not change, components of 16 faces or fewer are
skipped, and the topology gate - the same thing the export's invariant gate checks - refuses any removal that
would create a duplicate face or an edge with more than two faces.

The quad merge has its own small guard set, all checked per candidate pair before the pair is recorded:
neither face may already be merged; the union's four vertices must be distinct; the two faces may fold by at
most 25 deg, so a step rim or two stacked levels sharing an edge never become one polygon; the resulting quad
must be **strictly convex in its own plane**; and it must be **strictly convex in the engine's own frame,
the XZ ground projection**, with a 5 deg margin. The last two guards are the important ones, and they exist
because **the engine refuses a concave navmesh face**: importing a mesh that contains one raises the editor
dialog

> Concave Navigation Mesh Face Detected
> Concave faces detected on navigation mesh at locations: 1) x=..., z=..., y=... (and so on)

and the map will not load. `is_concave` exists in the NMG8 face record precisely because the engine's
validator knows the flag; setting it honestly is not the fix, because a flagged concave face is a rejected
face either way. The fix is to **never build one**: a pair whose merged walk has a reflex corner in the
engine's frame is simply left as two triangles, which the engine accepts unconditionally. So the emitted
`is_concave` is 0 everywhere, by construction rather than by assertion.

**The engine tests the GROUND projection, not the quad's plane.** This is the whole reason the guard exists in
two frames. The engine's navmesh is a 2D structure - it routes in XZ and triangulates a face from its ground
projection - so whatever concavity test it runs can only see XZ. The plane test alone was **not** enough:
V3.41 emitted five quads on Birtu that are convex in their own best-fit plane (worst angle `179.85` deg,
inside V3.41's own `179.9` ceiling) and **reflex in XZ** (up to `188.13` deg), and the engine's dialog named
exactly those five. That is a measured, one-to-one correspondence: the dialog prints each face's centroid in
the engine's Z-up frame, and all five centroids match the five XZ-reflex quads to **0.00-0.01 m**:

| engine dialog (x, y, z=height) | our centroid (x, y=height, z) | plane worst | XZ worst | fold |
|---|---|---|---|---|
| 513.38, 111.81, 44.70 | 513.38, 44.70, 111.81 | 179.85 deg | **184.76 deg** | 7.3 deg |
| 304.71, 265.83, 39.95 | 304.71, 39.95, 265.83 | 178.51 deg | **181.25 deg** | 14.1 deg |
| 350.44, 327.81, 37.60 | 350.44, 37.60, 327.81 | 163.10 deg | **180.00 deg** | 6.8 deg |
| 264.75, 376.99, 21.15 | 264.75, 21.15, 376.99 | 179.01 deg | **188.13 deg** | 16.6 deg |
| 362.35, 400.80, 21.73 | 362.35, 21.73, 400.80 | 172.53 deg | **180.56 deg** | 23.2 deg |

**The fold cap is a sense guard, not a safety guard, and the measurement says so.** Three of the five faces
the engine actually complained about folded only **6.8 / 7.3 / 14.1 deg** - i.e. V3.41's 25 deg cap never let
them through, so "a bent quad is the XZ risk" is refuted by the engine's own five. The fold allowance was
briefly tightened to 12 deg on that theory; running all three configurations settles it:

| config | Birtu quads | CC_20 quads | Basamu quads | concave / reflex / flat / straight / orientation-bad XZ quads |
|---|---|---|---|---|
| A: fold 25, plane test only (V3.41) | 659 | 1954 | 3196 | 5 reflex on Birtu, 11 CC_20, 35 Basamu |
| B: fold 25 **+ XZ test** | **603** | **1857** | **2955** | **0 / 0 / 0 / 0 / 0** |
| C: fold 12 + XZ test | 476 | 1270 | 1741 | 0 / 0 / 0 / 0 / 0 |

So the XZ test takes **every** metric to zero on its own (config B, which is what ships), and the fold
tightening moves no count at all while costing **127 / 587 / 1214 merges** - 32% of CC_20's and 41% of
Basamu's remaining merges on the two big maps, i.e. a straight routing-cost regression bought with nothing.
The cap therefore stays at **25 deg**, where it does its actual job (keeping a step rim or two stacked levels
out of one polygon) and the XZ test carries the safety. The history of the editor dialog is **100+ faces ->
5 -> 0 by construction**; only the user's re-import can confirm the 0.

The plane test is a probe of all four corners in the quad's own projected plane (the same Newell-normal
+ orthonormal-basis projection the stage already used for the angle/perimeter ranking, so there is one
definition of the quad's plane). In that plane the walk is convex exactly when all four successive cross
products share the sign of the polygon's own signed area; one of the opposite sign is the reflex corner, and
`cr == 0` exactly is a collinear triple. The test is strict (`cr * area2 > 0`) plus an angle ceiling
(`QuadConvexMaxInteriorAngleDegrees = 179.9`): the epsilon exists because with float inputs at map scale
(~1000 m, so ~6e-5 m of coordinate noise) a geometrically straight corner does not land on `cr == 0` but on
noise of either sign, so the sign test alone cannot catch it. 0.1 deg is ~14x that noise while still costing
nothing real - a corner that close to straight is a redundant vertex lying on the segment between its
neighbours (1.7 mm off it on a 1 m edge, versus the cleanup's own 0.05 m tolerance), and handing the engine's
n-gon triangulator a zero-area corner triangle is worse than leaving the pair as two triangles. It is
measured to be worth almost nothing - dropping the ceiling entirely (`179.99999`) buys exactly 1 extra quad
on Birtu and 9 on CC_20 (0.15% / 0.46%) - while with it disabled the worst emitted corner really does reach
`180.0000` deg, i.e. the degenerate corners it exists to catch do occur on real maps. The same test also
subsumes the old bowtie rejection, because a self-intersecting walk always has mixed turn signs.

The **XZ test** repeats the same probe on the ground projection, and it is the one the engine actually
applies. Three conditions: (h1) the XZ projection is non-degenerate (`|area2| > 1e-9`), which also refuses a
vertical quad; (h2) the XZ walk's shoelace sign agrees with the XZ signed area of **both** triangles it
replaces, so a merge can never flip a face's winding in the engine's frame (CC_20 had one such quad); and
(h3) all four XZ interior angles are strictly below `QuadXzMaxInteriorAngleDegrees`. Every XZ corner test is
strict (`cr * area2 > 0`), so a numerically straight corner is refused too - and one of the five measured
engine complaints is *exactly* straight in XZ (`180.0000` deg), so that case is real, not hypothetical.

**Why the XZ margin is 5 deg (`175 deg`) and not the plane test's 0.1 deg.** The plane epsilon is sized
against the noise in our own arithmetic, which we can measure. The engine's epsilon is a black box, so the
margin has to be sized against everything that could plausibly make the engine disagree with us, and one of
the five measured complaints is reflex by `0.0005` deg - i.e. a corner one float rounding away from being
flat, which the engine's own arithmetic could easily put on the other side of the line. Coordinates are
floats at map scale (up to ~2.1 km on CC_20), so one coordinate carries ~2.4e-4 m of rounding; an interior
angle measured over the shortest edge the merge accepts (0.25 m) therefore has ~2*2.4e-4/0.25 = 2e-3 rad =
**0.11 deg of pure noise**. 5 deg is ~45x that worst case, and it is also the level at which such a corner
sits no more than `0.044*L` off the segment between its neighbours - below the cleanup's own 0.05 m tolerance
for a 1 m quad side, so a corner rejected by the margin is a vertex the mesh never promised to keep anyway.
The `cleanup:` log line reports the figure directly (`quadsXZworst=<deg>deg (margin 175deg,
nearStraight=<n>)`), so the headroom is visible on every run: measured worst emitted XZ angle is **174.98 /
174.92 / 174.96 deg** on Birtu / CC_20 / Basamu, i.e. sitting just under the ceiling by construction.

Because the shared edge had exactly two incident faces in the manifold input, deleting it keeps the emitted
mesh manifold. Face indices are stable at this point (`MergeQuads` runs after the flip polish and nothing
compacts `tris` afterwards), so the recorded pair list indexes the final mesh directly. The merge stage is
the **only** producer of 4-vertex faces in the codebase: `quadPairs` originates there and nowhere else, and
the v1 writer path (`WriteFlat`/`BuildEdges`) always emits 3. The toggle `NavMeshVoxelQuadMerge` (default on)
turns the whole stage off instantly - the emitted bin then goes back to triangles only - as an A/B.

Harness `Test 27` owns the assertions: a hand-made concave quad is still refused, a convex patch still
merges, a **real Birtu XZ-reflex patch** captured verbatim from the mesh (the first row of the table above:
plane worst `179.9` deg, fold `7.3` deg, XZ worst `184.76` deg) must be refused - and it can only be
refused by the XZ test, since the fold cap (25 deg) and the plane ceiling both pass it - and in the
real-pipeline case every emitted quad must be XZ-convex within the margin, orientation-matched, with 0
concave and 0 straight corners. The `--xzprobe` harness mode reproduces the table above on demand.

Measured quads emitted (convex in **both** frames, fold <= 25 deg): **Birtu 603, CC_20 1857, Basamu 2955**,
all with **0 concave, 0 reflex, 0 flat, 0 straight and 0 orientation-bad XZ quads** - down from 659 / 1954 /
3196 with V3.41's plane-only guard, of which 5 / 11 / 35 were XZ-reflex. The whole cost is the XZ test:
56 / 97 / 241 quads. Every other metric is unchanged, since the triangle mesh is untouched (see below) -
needles, thin faces and long-thin counts are bit-identical on Birtu
and CC_20.

Refinement's own guarantee is **topological, not just geometric**. Each sweep builds an exact edge -> faces
index (key = the `(a,b)` edge; value = the two face indices packed into one long, face0 in the low 32 bits,
face1 in the high 32 bits, -1 when absent) and updates it on every split - remove the old faces' three edges,
add the new faces' edges - exactly as the topology gate maintains its counts. The index is therefore correct
*at all times*, not just at sweep start, so an interior edge's true neighbour is always found and every 2-4
split closes both faces: **no T-junction is possible**. The edge-island count (which unions faces sharing an
exact edge, i.e. the T-junction witness) stays 5 / 15 / 1 on the measured maps, where the old per-sweep CSR
lookup went 5 -> 153 on Birtu while the vertex-based component count stayed 5.

**The three "not allowed navmesh" defects and the guards that stop them** (V3.38):

1. **Faces doubling back over themselves / self-intersecting** - the replacement must not fold over the
   surface: every new face has to keep facing the way the removed fan did (within 60 deg), the local surface
   area may not grow (1% on the outline, 5% inside) and may not collapse, and a slide must stay inside its own
   fan (2D point-in-polygon in the fan's plane). That last rule is also why a slide can never push a vertex
   past a ring edge into a neighbour.
2. **Cutting a corner at a corridor turn** - the outline may only *shrink* or stay. Growth is refused, so the
   boundary can no longer bulge outward into a wall (a cut corner is the bot-walks-into-the-wall case).
3. **The mesh dropping under the top of a staircase** - the height is checked *directly*, at the exact spot:
   a fold must reproduce the original surface height within 0.15 m, and a slide must stay within 0.15 m of the
   height the mesh had at that spot before the slide. Smooth slopes are unaffected (there the original surface
   at the new spot is the same height); a fan that spans a step is refused.

Measured (V3.40 + convex-in-both-frames quads; needles = alt < 0.05 m and longest edge > 0.5 m; thin = min angle < 5 deg,
i.e. the user's "width < length/10" ratio class; long-thin = aspect `longest^2/(2*area)` > 8 with
`longest >= 4 m`, the refine criterion; islands are **edge**-islands, the T-junction witness; faces include
what the passes removed and the growth the flip polish re-adds). "before" is the cleanup-off build, "after"
the shipped one:

| map | faces | thin (<5 deg) | needles | long-thin (aspect>8) | mean quality | quads (concave) | worst quad angle (plane / XZ) | islands | inv | build |
|---|---|---|---|---|---|---|---|---|---|---|
| Birtu | 50700 -> 44645 (-12%) | 1981 -> **356 (-82%)** | 104 -> 8 | 3648 -> **760 (-79%)** | 0.649 -> **0.722** | 603 (**0**) | 175.27 / 174.98 deg | 5 -> 5 | PASS | 4.7 s |
| CC_20 (caves) | 266526 -> 241641 (-9%) | 5984 -> **1037 (-83%)** | 187 -> 38 | 15239 -> **1894 (-88%)** | 0.679 -> **0.741** | 1857 (**0**) | 178.38 / 174.92 deg | 15 -> 15 | PASS | 24.9 s |
| Basamu | 540232 -> 492616 (-9%) | 11608 -> **1499 (-87%)** | 596 -> 61 | 21878 -> **2979 (-86%)** | 0.693 -> **0.746** | 2955 (**0**) | 178.78 / 174.96 deg | 1 -> 1 | PASS | 32.0 s |

Re-measured after the two-frame convexity guard, so the comparison is like-for-like on this machine. Note
that the cleanup's decimate/slide/flip passes are **wall-clock budgeted** (their deadlines are shifted by
how long the insert pass actually took), so the last digits of the triangle metrics drift a fraction of a
percent between runs - Basamu was observed at 1497/1498/1501 thin, 2942/2955/2962 quads and long-thin at
2954-2982 across repeats of the *same* binary (its triangle mesh moves, so its quad count moves with it).
Birtu and CC_20 reproduce exactly, and both quad columns track that fixed mesh. The two
worst-angle columns show each guard is genuinely the binding limit, not slack: the plane worst angle sits
just under the `179.9` plane ceiling and the XZ worst angle sits just under the `175` deg XZ ceiling.

The V3.38 reference for the removed classes was Birtu 363/9, CC_20 1031/39, Basamu 1500/61 (harness convention);
the insert pass keeps those within ~1% while cutting the long-thin population by 79-88%. The refine sweep over
the 240k-490k-face meshes adds a few seconds to those builds.

Before the safety guards the same passes reached -93%/-95%/-97%, but they also cut corridor corners, doubled
faces back over each other and pulled the mesh under stair tops; the guards above give back about ten points
and remove all three defects. Each guard is a single constant next to the others in `NavMeshCleanup.cs` if a
map ever justifies loosening one.

Known limits, by design: a needle whose removal would drop or lift the surface at its own spot by more than
0.15 m (a genuine step or fold) or swing the outline by more than 0.35 m (a real zigzag) **survives** - the
pass will not reshape the walkable surface to win a triangle count. Insertion is bounded too: `faces/20` inserted vertices and 8 sweeps
per mesh under the cleanup wall clock, so a map with a huge long-thin population converges as far as the budget
allows rather than exhaustively; a face whose children would themselves be needles is left to removal/slide, and
2-4 splits beside the outline are skipped so the insert pass cannot shrink the walkable coverage. Separately, an
opening with less than ~2 voxel cells (0.5 m) of headroom over a crossing cannot be represented by the voxel
core at all; that tiny-ladder-doorway case is accepted as bad mapping practice.

Performance note (V3.36, measured): the builder's long-keyed maps (weld grid, prune, invariants, cleanup) were
hashing structured `(x<<32)|z` keys with .NET's default `hi ^ lo`, which collapses them into a handful of
buckets and degenerates every map to quadratic chains. With the existing `LongKeyComparer` applied to all of
them, the same builds get much faster - Birtu ~2x, CC_20 ~3.5x (66 s -> 19 s), Basamu ~5.7x (127 s -> 22 s) -
with byte-identical output for the maps that already worked.

### Malformed faces: no downward normals, no bowties, no overlap (V3.46)

The user reported two things on CC_20 (screenshot folder `Feedback/3.42`): at `x 1107.68, y 1042.09,
z 126.13` *"the navmesh overlaps and crisscrosses with itself, like an X"*, and at the
`european_castle_ladder` base (`x 1119.57, y 1079.10, z 108.02`) *"you can see some navmesh bits jumping
way up and one inverted navmesh face among them"*. Both classes are now defined, measured and guarded:

**A navmesh face is a 2D polygon standing on the ground.** It has a *ground footprint* - the signed XZ area
of its walk order, in m^2 - and that footprint has to face up. Three things can break that, and the same
number (`NavMeshCleanup.MinGroundFootprintM2`, 1e-9 m^2, i.e. numerically vertical) judges all of them in the
cleanup's guards, in the repairs and in the export gate:

| class | footprint | what the engine does with it |
|---|---|---|
| walkable | `> 0` | fine |
| inverted | `< 0` | drawn inside out; the writer always writes the per-face `inverted` flag `0`, so nothing downstream ever catches it |
| wall | `= 0` | no ground footprint at all - the engine's n-gon triangulator makes a degenerate polygon out of it |

**What was actually happening, measured stage by stage** (the harness can dump the mesh after every cleanup
stage; the per-stage downward-face count on CC_20 was 1 -> 1 -> **7** -> 3 -> 3 -> 3):

* **RefineLongThin is sound.** Its children are sub-triangles of the parent they replace (the inserted vertex
  IS the midpoint of the split edge), so it inherits each parent's plane and winding by construction.
  Instrumenting every committed child over 13k+ splits found **0** vertical or inverted ones. V3.46 states the
  property as an assertion instead of assuming it (one cross product per child), so a future change cannot
  quietly break it.
* **Decimate was the one stage that manufactured malformed faces.** Its fan removal retriangulates the ring
  in the ring's best-fit plane; `EmitTri` can only flip a *negative* footprint positive, so a **vertical**
  ear (footprint exactly 0) sails through, and the orientation guard did not stop it either: it compares each
  new face against the fan's **mean** normal with a 60 deg cone, and a sideways-facing triangle is inside
  that cone whenever the mean is itself tilted. Measured 22-29 such faces per CC_20 run, every one of them
  exactly vertical (e.g. a ring whose three vertices share one Z: `A=(71.5,84.5,409.25) B=(68.5,86.3,
  409.25) C=(65.75,88.3,409.25)` against a fan whose summed normal was `(68.25,109.56,-86.54)` - cos 0.56,
  inside the cone). **Guard (A)**: every emitted face must have a strictly positive footprint, else the whole
  retriangulation is refused.
* **Decimate could also lay a new face on top of a face it never touched**, which is the X: ear-clipping a
  ring whose *ground projection folds over itself* (a staircase rim, a wall base, a saddle) puts triangles
  outside the hole. **Guard (B)**: the ring's XZ projection must be a **simple polygon** (non-degenerate
  shoelace, no two non-adjacent edges crossing) before it may be retriangulated at all.
* **MergeQuads never produced either class**, and by construction it cannot: it does not touch `tris` at all,
  it only records which triangle pair the writer emits as one 4-gon, and every pair it records is strictly
  convex in its own plane *and* in XZ with the winding of both triangles. Measured on CC_20: 1857 emitted
  quads, **0** bowties, **0** XZ-reflex, **0** orientation flips, and the downward/overlap counts are
  byte-identical before and after that stage. The gate now re-derives each walk anyway
  (`CheckQuadWalks`), because the writer trusts `QuadPairs` completely.
* **The inverted face the user saw at the ladder was never in the cleanup's remit**: it is present in its
  *input*, at `(1120.500, 117.367, 1077.000)`, and it is the detail mesh following the prop geometry -
  `stairs_bo` + `bd_wood_heap_b` + `european_castle_ladder` packed into a 2 x 3 m spot with 6 m of height
  range. A navmesh face cannot be inverted by a cleanup pass that has already run, so this class is repaired
  where it is born.

**The repairs** (`TiledNavMeshBuilder.RepairFaceQuality`, after the cleanup and before the result arrays are
built, with the recorded quad pairs renumbered through the same compaction so a pair can never point at a
removed face):

* every face with a non-positive footprint is dropped and counted (1 on CC_20 - the ladder face - 11 on
  Basamu, 0 on Birtu);
* the redundant face of every **vertex-disjoint** same-level overlapping pair is dropped (2 faces on CC_20),
  but only when the drop does not change the island count: dropping either face of an edge-sharing pair
  disconnects one-face-wide necks, and measured on CC_20 dropping the 64 edge-sharing slivers takes the map
  from 15 islands to 51 - the engine would turn those into 36 fragments. Drops that would fragment the mesh
  are reverted and counted. Both log their own wall clock (`... in <n>ms`), so the cost is visible per run.

**The permanent guard** (`ComputeInvariants`, the same gate that already blocks the write on
`maxEdgeMult>2` / duplicate faces / self-loops) now also measures and blocks `inverted`, `vertical` and
`badQuadWalks`, and reports `selfOverlap=disjoint/all`. `disjoint/all` is deliberately *reported, not
blocking*, for the reason in the bullet above: the whole remaining class on CC_20 is one pair on a
one-face-wide neck. Cost, measured on the 241k-face CC_20 build and logged on both lines
(`faceQualityMs`, and the repair's own `in <n>ms`): **122 ms for the gate's two face scans and 229 ms for
the repairs, ~1.1% of a 33 s build**. Birtu (45k faces) is under 25 ms each.

Before / after, same machine, same snapshots, `--ramps on --quality` (the cleanup's passes are wall-clock
budgeted, so the last digits drift; Birtu and CC_20 reproduce exactly, Basamu is the sensitive one):

| map | downward/vertical faces | disjoint overlapping pairs | all same-level pairs | quads | needles | thin | islands | gate |
|---|---|---|---|---|---|---|---|---|
| Birtu | 0 -> **0** | 0 -> 0 | 9 -> 9 | 603 -> 603 | 8 -> 8 | 356 -> 356 | 5 -> 5 | PASS -> PASS |
| CC_20 | **4 -> 0** | **4 -> 2** | 91 -> 89 | 1857 -> 1855 | 39 -> 39 | 1037 -> 1035 | 15 -> 15 | PASS -> PASS |
| Basamu | **11 -> 0** | 0 -> 0 | 99 -> 85 | 3303 -> 3333 | 65 -> 65 | 1638 -> 1655 | 1 -> 1 | PASS -> PASS |

(Basamu's quad count is 3303 -> 3333 here rather than the 2962 quoted earlier for this map: that number was
measured on a different machine, and its cleanup is the most budget-sensitive of the three - repeats of the
same binary were observed at 2942/2955/2962 elsewhere.)

### Gate / door plate blend - `NavMeshVoxelGatePlateBlend`, **on by default**

The user's brief, verbatim: *"The navmesh IS just a projection from the gate, which is good and we want for our
auto navmesh gen. But, it just runs perfectly perpendicular to the gate. One side is too high and not stitching.
Need to make it so that the projected navmesh for the gate, if treated as a solid plane, rotated by up to 50
degrees (max agent walk slope) to better align with surrounding navmeshes. Otherwise situations like this terrain
sloping or putting a gate on a sloped ramp prop break down and don't connect their navmesh like this."*

**What the plate actually is (V3.51's diagnosis re-measured, and confirmed).** `european_castle_wall_gate_l1` on
CC_20, centre (1130.51, 990.74). It is **not** synthesized (`ramps: synthesized=0` at the gate) and **not** the
door leaf (the `clean` + `state1..4` copies are vertical: H = 85.12-89.75 over a 1.33 x 3.33 m footprint, so
they cannot make a 5 x 4 m horizontal plate). It is the raw voxel projection of the gate's own **flat floor
colliders** - `collider_l` / `collider_r` / `extra_collider_*`, five overlapping copies whose tops sit at
H = 85.013-85.383, quantised to one `ch` span top (85.8) and lifted by the uniform 0.5 m. Measured with
`--census 1130.51,990.74,3.5 --plate off`:

```
f112433 f112437 f112438 f112440 f112441 f112447 f112448 f112449 f112450 f115591
    all slope=0.0deg ny=1.00 y=[86.30..86.30]      <- ten faces, every vertex at exactly 86.30
f112442  c=(1132.67,85.77,989.29) slope=47.1deg y=[85.10..86.30] maxEdgeDy=1.20
f112439  c=(1132.08,86.17,988.92) slope=58.0deg y=[85.90..86.30] maxEdgeDy=0.40
f112446 / f115594 / f115595   slope=16.5 / 21.3 / 14.2deg  y=[86.30..86.90]   <- the NW end, inside the climb
```

and the terrain under the passage falls `87.14 m (x=1122) -> 82.80 m (x=1140)` (`--probe` row by row), so the
plate sits 1.1-1.6 m above the low side. The failure is **asymmetric**: the NW end meets its neighbours inside
the 0.6 m climb, the SE end meets faces 1.20 m below it, and the plate's eastern tip (1133.0, 991.5) is a
**dead end** - a degree-1 vertex whose nearest surrounding vertex is (1133.75, **85.10**), 0.75 m away in XZ and
**1.20 m below**.

**The rule - generic, no prop names, only geometry.** A *plate* is a maximal edge-connected group of dead-flat
(<= 2 deg), up-facing faces that all sit on **one** `ch` level, with 3..120 faces, 1..120 m^2 and no side longer
than **8 m** (bigger is a floor, a deck or a terrace, and tilting it would move real geometry). It is re-generated
only when all of these hold:

1. **PROUD** - the plate's own height is more than one agent climb above, but at most 2.5 m above, the 10th
   percentile of the surrounding mesh's heights in a 2 m ring around the plate's own footprint. That is the
   "the ground falls away from under me" signature, and requiring it to exceed the climb is what keeps
   already-stitching plates out.
2. **FITTABLE** - at least 6 neighbouring faces in the ring, spread over at least 1.5 m, with a well-conditioned
   normal matrix (`det >= 1e-3 * sxx*szz`, i.e. |correlation| < 0.9995) and an intercept within 2x `proudMax` of
   the plate's own level. Weights are the faces' own XZ areas capped at 4 m^2. The fit is over the **built mesh**,
   never over the raw soup - the soup carries the slab's own colliders *and the wall above it*, and a fit over it
   tilts the plate towards the wall. Our own synthesized ramp / connector cells are excluded too: they answer a
   different question, and on the suite's narrow-doorway lab they made a real landing read 1.33 m "proud" and tilt
   it 11 deg off a level it must keep (that is what broke Test 26 until the exclusion went in).
3. **BETTER** - the clamped plane has to beat the flat plate by 0.05 m on the *worst* neighbour mismatch,
   otherwise the plate is left byte-identical.

**The fit and the clamps.** `y = a + b*(x-cx) + c*(z-cz)` about the plate's own XZ centroid, solved as a
least-squares plane through the **centred** moments (the uncentred normal equations carry an `a0*sum(w*dx)` term;
solving them as if they did not is what produced a 10.6 gradient - an 85 deg "plane" - for a 2 m sample band).
The tilt is clamped **twice**: to `tan(NavMeshMaxSlopeDegrees)` (the user's own bound) and to
`2*agentClimb / halfSpan`, so a 5 m plate can never become a 6 m wall - a blend is a tilted floor, not a ramp. The
vertical offset is the **median** of the per-sample residuals (the L1 minimiser, so it balances the two ends),
clamped to `[-climb, 0]`: never raise the plate (raising only makes the proud class worse) and never sink it by
more than one agent climb, which is what the uniform `HeightOffsetMeters` lift already buys.

**The re-emit is a rotation of the plate's OWN vertices** - "treated as a solid plane, rotated", literally. It
adds and removes **no face** and touches **no edge**: the plate keeps every vertex it shared, so it keeps every
connection. What does move is a shared *vertex*, so the handful of faces that met the plate at a corner come
along by at most the plate's own offset (one agent climb, printed per plate).

The alternative was implemented and measured first, because it looks tidier: **clone** the plate's vertices and
bridge each seam with a two-triangle connector. It is manifoldness-safe (target edges must be true boundary
edges, multiplicity 1, consumed once; a plate-vertex fan can only take so many connectors) and it looked good on
the gate - but it placed **172 connector quads on CC_20**, and because a 0.2-0.5 m connector sliver is something
`RepairSteepFaces` or the sliver cleanup can remove, a plate that was the only route between two parts of a
component then split: **Birtu 5 -> 8 islands, CC_20 15 -> 16.** The rotation ships instead, and both numbers are
back where they were.

**Measured at the gate (CC_20, `--platetrace`), before -> after:**

| | before | after |
|---|---|---|
| the plate's own faces | 10 faces, all **86.300**, slope **0.0 deg** | same 8-face group + 2, **12.9-13.0 deg**, `y = 85.329 .. 86.576` |
| plate tilt (cap 24.567 deg, itself inside `MaxSlopeDegrees` 50) | 0.0 deg | **13.013 deg**, offset -0.003 m |
| NW exit faces | 16.5 / 21.3 / 14.2 deg, `y=[86.30..86.90]` | 12.9 / 19.5 / 8.9 deg, `y=[85.84..86.90]` |
| SE exit faces | **47.1 deg** (`dy=1.20`) and **58.0 deg** | **33.8 deg** (`dy=0.80`) and **57.5 deg** (`dy=0.28`) |
| east tip (1133.0, 991.5) vs its neighbour at 85.10 | **1.200 m** apart (dead end) | **0.229 m** apart |
| worst neighbour mismatch over the 25-sample ring | **1.600 m** | **0.495 m** (inside the 0.6 m climb) |
| profile at z = 991, x = 1124..1138 | 87.40 / 86.80 / 86.50 / 86.30 / **none** / 85.03 / 84.97 / 84.19 | 87.57 / 86.79 / 86.24 / 86.15 / **85.53** / 85.03 / 84.97 / 84.19 |
| `--connect` foot -> through -> far-side pairs across the gate | 3 of 5 (2 had no face within 2 m) | **5 of 5 CONNECTED** |
| islands / `selfOverlap` | 15 / `1/118` | **15 / `1/118`** |

#### V3.53: the seam - a void next to the plate is not a slope problem

The user's next report was *"The slope is fixed at the gate. It looks great, but it didn't connect still."* The
measurement says that is a **different defect**, and it is not the plate's slope.

**What the engine needs, and what the harness had been measuring.** `FaceComponents` counts edge adjacency and
`--probe` only asks whether a column is covered; the engine does neither. It re-voxelizes the imported faces and
links two polys when their XZ columns **touch** and their floors differ by at most its climb. A surface can
therefore be edge-connected around a loop and still be uncrossable where the user walks. `--walk "x,z;x,z;..."` now
does that test literally - minimum **footprint** distance between the two triangles (vertex-to-triangle both ways,
which is what "adjacent columns" means), height difference over the whole contact region, breadth-first over faces
- and prints the route with every step's `dy` and footprint gap. `--pinch` and `--soup` were added alongside it:
`--pinch` finds vertices whose incident faces share no edge (a point touch), `--soup` dumps the merged soup's
triangles in a box with their slopes, which is the only way to ask "is there walkable collision in that hole".

**The void.** Probing along `z = 991` through the gate in 0.5 m steps (`--probe`) - and **identically** with
`NavMeshVoxelGatePlateBlend` **off**, so this is not the blend's doing:

```
x=1128 86.24 | 1129 86.24 | 1130 86.15 | 1131 85.77 | 1132 85.53 | 1133 ---- | 1133.5 ---- | 1134 85.03 | 1135 ---- | 1135.5 84.90 | 1136 84.97
```

Two bare columns right at the plate's east end, plus a larger 4 x 3 m patch south-east of the passage
(`x 1131..1135`, `z 992..994`) on 12-18 deg terrain. `--pinch` finds **zero** pinch vertices within 30 m - the
surface is properly edge-connected - so this is coverage, not topology.

**Why the gap fill misses it.** `FillNarrowGaps` runs at `TiledNavMeshBuilder.cs:3077`, **before**
`FilterLedgeSpans` (3103) and `FilterWalkableLowHeightSpans` (3148). The gate threshold beam's own underside sits
0.3-0.9 m above the terrain in the doorway (`--soup 1132,991,1137,996,82,88` shows the beam's vertical faces at
x 1132.3-1133.4 spanning 84.75-85.59 lifted, over terrain at 83.84-84.72), so those terrain columns still hold a
walkable span when the gap fill looks at them and are denied clearance a few lines later. Nothing re-fills them
afterwards, and the resulting band is also wider than the 2-cell (0.5 m) gap-fill cap. `--ledge off` and
`--climb 1.0` each restore 6 of the 13 probed columns, but only at the 92.3 m battlement - never on the ground -
which is the proof that neither the ledge filter nor the climb is the operative rule here.

**What ships: `ClosePlateSeams`.** For every plate the blend has just rotated, each of its **mesh-boundary** edges
(edge multiplicity exactly 1) is offered the nearest foreign **boundary** edge across a gap, and the pair is
bridged by two triangles that **reuse the four existing vertices**: `(a,b,v)` and `(a,v,u)`, so both boundary edges
become interior and the engine gets a real **edge portal**, not a corner touch. Every guard below was added because
its absence was measured, not guessed:

| guard | what happened without it |
|---|---|
| `0.1 m <= gap <= 1.5 m` | a touching pair is already connected; past 1.5 m it is a missing prop, not a seam |
| height step over the whole contact `<= climb` (0.6 m), bounded by the closest pair **plus** each edge's own height variation | measuring only the closest pair lets a steep foreign edge through - and the closest pair of two edge-sharing triangles is always a shared vertex where both surfaces agree, i.e. `dy = 0.000` |
| `gap * tan(MaxSlopeDegrees) >= step` | otherwise the "seam" is a riser, which is what an agent meets at a void |
| no vertex shared between the two edges; strip area `>= 0.08 m^2`; segments must not cross | degenerate strips, and strips whose two triangles overlap |
| `(a,v)` unused; `(a,u)` and `(b,v)` not already shared by two faces | the two triangles add `(a,v)` **twice**, so an existing shared edge there goes four-way: **measured on Birtu, `maxEdgeMult=4`, invariants FAIL, the module refuses to import** |
| **the strip's own footprint must be empty** | the nearest boundary edge is not always across a void - it can be round the side of a step, and the strip then lands on real geometry. **Measured: 23 of CC_20's 24 `selfOverlap` pairs, and once the cleanup deleted the face holding a component together, CC_20 15 -> 16 islands and Basamu 1 -> 2.** Four sample points (both triangle centroids, both diagonal midpoints) must each be uncovered by any face within `climb + ch` of the strip's own height |
| each edge consumed once; `GatePlateSeamMaxCount` (1024) cap | two plates bridging the same void twice |
| whole-mesh recount afterwards, refuse the whole batch if any edge is over-shared | the guards are index-space arithmetic and a mistake costs the entire import |

**Why reusing the vertices is not a detail.** A first version added exact copies of the four vertices and let the
final weld merge them back. They did merge - through the **sliver cleanup**, which welds *by position* - but onto a
**nearby** vertex, so the strip's edges became edges the multiplicity bookkeeping had never seen, and Birtu failed
its invariants. Reusing the originals keeps every added edge in the index space the guards were written in, adds no
vertex, and keeps the pass's "0 vertices added" claim true.

Measured: **CC_20 71 voids, Birtu 80, Basamu 110** bridged (widest 1.5 m, worst step 0.6 m = the climb);
`maxEdgeMult=2`, `inverted=0`, `vertical=0`, `badQuadWalks=0`, invariants **PASS** on all three; **islands CC_20 15,
Birtu 5, Basamu 1**, all unchanged; `selfOverlap` CC_20 **1** (unchanged) and Birtu **0**. `--dumpnear
1130.5,990.9,9` before/after shows the new triangles inside the former bare band.

**Bars.** Suite verdicts **110/110 byte-identical** to the stored baseline (every test PASS, the documented
`detailFaces>=fanFaces False`, **LADDER-CAP the one FAIL**, DETERMINISM PASS); no plate is blended and no seam is
emitted anywhere in the suite, so the suite is untouched by construction. Birtu `ladders total=2`
(`civilian_ladder_5m` at 302.60/314.95, `civilian_ladder_6m` at 300.08/279.32), both probe points on island 1.
`NavMeshVoxelGatePlateBlend=false` emits no `gate plates:` and no `gate plate seams:` line at all, i.e. the
pre-pass output byte for byte. Both variants build 0 warnings / 0 errors, deployed.

**Honest residual - the important one.** The seam does **not** close the specific void immediately east of the
CC_20 gate's plate, and that is the spot the user circled. Its west side is the plate's **degree-1** east tip: a
single vertex with no boundary edge facing the gap, so an edge-to-edge seam has nothing to weld to there, and the
far side is bounded by *interior* terrain edges. `--walk` still reports the gate `WALKABLE` end to end (foot ->
through -> far side, every step `dy=0.000`, widest footprint gap `0.000`) with the blend both on and off, because
the mesh crosses the passage at `z ~ 989` where it is continuous - so the passage line the user walks *is*
connected, and `(1133, 991)` stays bare in both builds. Closing it needs the plate's **footprint to grow** across
the beam's own collision edge (the beam's colliders reach x = 1133.465 while the plate's built faces stop at
x = 1133.0, because erosion eats one cell and the contour simplifier another), and that is a coverage change, not
a seam - it needs its own design that can prove the island count the way the guards above do. Basamu's 11 new
`selfOverlap` pairs (from 0) are the same class: strips that clear all four sample points and still graze a
neighbour. Both are follow-ups, not something this round can claim.

### Stairs & ladders - `NavMeshVoxelSteppedProps`, **off by default**

Two mechanisms for props whose body flags include `HasSteps` (`0x800`) or `Ladder` (`0x40`) - Birtu has 84 of
the former and 2 of the latter. **Everything else keeps the ordinary 50 deg cap** (the builder's
`effectiveMaxSlope`). Both mechanisms only run while this setting is on; in particular `Ladder` bodies are
collected (not excluded) only then, see *V3.16* below.

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
| `v2: stepped: markedCells=...` | - | `markedCells=38312 restored=18191 droppedClearance=2909 droppedOrphan=5 droppedPlane=0 aboveNear=0 aboveFar=16394` |
| mesh | V=32 836 F=50 957 E=84 531 | V=32 930 **F=51 131** E=84 817 |
| islands | 5 | **5** (the exemption's merged fragments are pruned by provenance) |

Per-instance `--coverage` (the exemption is the only active mechanism on Birtu):

| family | off -> on |
|---|---|
| `stairs_bo` (6) | 39.1->50.4, 45.7->51.5, 51.6->63.9, 55.3->76.1, 59.6->73.4, 59.7->78.1 (all improve) |
| `european_castle_ladder` (6) | 61.9->71.4, 65.9->69.6, 67.1->69.5, 69.0->73.1, 69.4->72.7, 69.8->74.1 (all improve) |
| `wooden_platform_stairs_a` (6) | 30.2->55.8, 35.3->43.5, 53.8->63.2, 58.9->69.1 **improve**; 55.9->40.4, 59.1->58.0 (window artefact, see V3.15) |

So the setting is a clear win for `stairs_bo` (the V3.11 regressions 45.7->38.3 / 39.1->35.0 are gone, now +9 to
+20 points each) and for `european_castle_ladder`, and improves four of the six wooden flights.

**V3.13: the exemption is gated per span, not per cell.** After `FilterLedgeSpans` a remembered span is
restored only when
1. its own clearance above survives `walkableHeight` (the same rule `FilterWalkableLowHeightSpans` applies one
   step later), and
2. the restored span ends up connected - through walkable neighbours within `walkableClimb` in adjacent
   columns, restored or not - to a column that was walkable before this pass or to the tile border, so a patch
   can never become an orphan island.
`v2: stepped: markedCells=38312 restored=18191 droppedClearance=2909 droppedOrphan=5` (was `restored=21105`
ungated) and Test 23 stays 4/4 with the controls unchanged. The gates do **not** fix the two wooden flights,
and the measurement says why: (1) the clearance gate is a no-op for the mesh - those 2909 spans are the same
ones `FilterWalkableLowHeightSpans` nulls one step later, so the built mesh is byte-identical
(`F=51643 islands=6`); (2) only 5 spans are orphans, so the extra island is not one; (3) the coverage loss was
attributed to a ~0.2 m sag, but V3.15's measurement corrects that - see below - the *centroid* of the poly
covering (258,245.5) moves (`38.83` off -> `38.63` on) because the poly's footprint changes, while the surface
*local* height there goes slightly **up** (`38.94` off -> `38.98` on). An experimental extra gate that restores only into columns the
trim emptied did give islands 5 and **no** regressions, but it also removed ~97% of the gains (16 of 18
instances unchanged) and broke Test 23 (a/b/d) by starving the rung restorations the ladder cases need, so it
was dropped. **The default therefore stays off**; the remaining lever is to have a restored tread *replace* the
coarse plane span above it (drop the plane where the tread is authored), which is safe only where that plane is
the flight's own merge artefact and not a legitimate storey above it - that rule needs a per-span authorship
signal the heightfield does not carry today.

**V3.14: plane suppression, and the measurement that closes the question.** The same pass now suppresses the
merge plane: when a stepped-prop span is restored, any *other* walkable span in that cell that belongs to the
same prop (i.e. is remembered for that cell), sits within `walkableClimb` above the restored span, and is
therefore the rasterizer's merged top of the same boards, has its area nulled *and* is unlinked from the column
- nulling the area alone cannot work, because `FilterWalkableLowHeightSpans` reads `span.next.smin` regardless
of area and would null the restored span right back. Neighbouring props and legitimately separate storeys are
never touched; `droppedPlane=<n>` reports what was suppressed. Test 23 stays 4/4 with the controls unchanged
and neutrality stays `MATCH`, but on Birtu the counters show the rule has nothing to fire on:

```
v2: stepped: markedCells=38312 restored=18191 droppedClearance=2909 droppedOrphan=5
             droppedPlane=0 aboveNear=0 aboveFar=16394
```

`aboveNear=0` means **no** restored span has a walkable span within `walkableClimb` above it: the restored span
*is* the merged plane (its own top is the boards' merged top for that column), and every span above a restored
span is a legitimate storey at least `walkableClimb` up (16394 of them). The ~0.2 m sag is therefore not a
same-column plane at all - it is the *region* builder joining the restored cells with that storey's cells across
columns, so the contour spans two levels and the poly interpolates. A same-column rule cannot see that, and
nulling the storey is exactly what this rule must not do (it would delete real floors). The build is unchanged
by the rule (`F=51643 islands=6`, identical to V3.12), the two `wooden_platform_stairs_a` regressions and the
extra island remain, and **`NavMeshVoxelSteppedProps` stays `false`**. Closing this needs a different lever:
either region-level separation (keep restored ramp/tread cells out of an existing region that is more than
`walkableClimb` above them) or a per-span authorship tag from rasterisation so a storey can be told from a
merge artefact by construction rather than by height.

**V3.15: the metric settled, and the exemption's leftovers pruned by provenance.** Two things were resolved.

*Pruning the exempt-formed island.* The exemption merges previously-prunable fragments into one island that can
clear the 400-face cap and survive (the 512-face island at `(202.1, 23.9, 297.3)`, `zr=[10.70..36.70]`). The
prune pass now keeps **per-face provenance**: `BuildTile` records the world XZ of every cell whose span the
exemption restored (`SteppedRestoredCellXz`), `Build` collects them into a cell set, and a face whose centroid
falls in one of those cells "only exists because of the exemption". An island is dropped **regardless of the
cap** when, with its exemption faces taken out, every remaining **core** is still at most the cap - i.e. it is a
coalition of fragments that the unmodified build prunes one by one and only the exemption holds together. (A
first try, ">= 90% of the island's faces are exemption faces", was rejected: the 512-face island is only 16.8%
exemption faces - it is a *coalition*, not mostly-exempt.) Real networks keep a core far above the cap, so they
are untouchable: the log reports the worst exemption fraction over the kept islands as `1.946% of 13872 faces`
- the castle network that carries the restored flights is 98% ordinary faces.

```
v2: prune: exempt-formed islands=1 faces=512 largest=512 at (202.647,22.783,297.953) (restored stepped-prop leftovers, pruned regardless of the cap)
```

Islands are back to **5** and the mesh is `V=32930 F=51131 E=84817`, `invariants=PASS`. The unmodified build is
untouched (with the setting off there are no restored cells, so the pass is the old one; `V=32836 F=50957
E=84531 islands=5`, byte-identical).

*Settling the two `wooden_platform_stairs_a` "regressions".* They are an **artefact of the coverage window**, and
the harness now reports why. The window is `[surface - 0.2, surface + 0.5 (offset) + 0.6 (tol)] = [sy-0.2,
sy+1.10]`; the diagnostic `covMaxD` (the largest `faceY - surface` among *covered* samples) is **exactly 1.10 in
all six instances in both modes** - every covered sample sits on the window's top edge, so the raw metric is
knife-edged and any small raise flips samples out. The two instances:

| instance | missBelow (real gaps) | missAbove (floats over tolerance) | max excess | lvlCov | faces |
|---|---|---|---|---|---|
| `(254.917,244.37)` | 20 -> **5** | 32 -> 64 | 0.00 m | 100 -> 100 | 13 -> 14 |
| `(249.037,348.292)` | 48 -> **29** | 0 -> 0 | - | 100 -> 100 | 11 -> 10 |

Real gaps *improve* in both; what is lost are samples where the surface correctly exists but now sits 0.00-0.02 m
past the tolerance edge. Probing the flight at `z=245.5` (local, not centroid, heights; the surface went slightly
*up*, not down): `x=256.4: 39.71->39.77`, `257.4: 38.94->38.98`, `258.4: 38.05->38.18`, `259.4: 37.27->37.41`.
Connectivity is unchanged: both ends of every instance stay in the same island (`isl=1`, the big network, 13706 ->
13872 faces), and stations that only had the terrain island (`isl=2`, `h=7.90`) now additionally get an `isl=1`
face - nothing that was connected becomes disconnected.

*The bar.* Test 23 4/4 with controls unchanged, determinism `PASS`, terrain neutrality `MATCH`
(`e016e53886cb9585` / `ba807f993fde10fc`), islands 5, invariants `PASS`, every instance's real gaps down. The one
item that does **not** hold is "level bands within +/-10 faces": 96 of 102 one-metre bands are within +/-10 (10 of
them a hair lower, -1..-4 faces, where the exemption shifts a poly's centroid across a band edge) and 6 exceed -
`20m +16, 21m +45, 22m +34, 24m +13, 40m +12, 41m +14`, +174 faces total (+0.34%). Those bands are inside the props' own level range (`stairs_bo` `zr=[15.592..49.626]`)
and each added face covers ~5 previously-uncovered samples (the 864 fewer gaps in `stairs_bo`), so the criterion
is in direct tension with the coverage criterion. **`NavMeshVoxelSteppedProps` therefore stays `false`**: the
code is complete and the default-off path is unchanged, but the flip waits on the level-band criterion.

**V3.16: Ladder-flagged props are collected, and the apron is bounded by the prop's own footprint.** The glue
between the two mechanisms was missing: `SceneCollisionGeometry` dropped every `Ladder` body at classification
(`if (isLadder) { exLadder++; continue; }`, before the ramp synthesis) so a ladder prop could never reach
`SteppedPropRamps` at all - Test 23 only passed because its rig feeds the synthesis directly. The recorded Birtu
snapshot's own log said `excluded: [... ladder=2 ...]`, i.e. the two real ladders on the map
(`civilian_ladder_6m` at `(300.05, 280.84, 15.60)` and `civilian_ladder_5m` at `(302.53, 313.77, 16.63)`, leaning
~29 deg off vertical) were always thrown away.

Now, **with `NavMeshVoxelSteppedProps` on**, a `Ladder` body is collected like any stepped prop: the rungs are
steep-walkable surfaces (the prop's own triangles are ledge-exempt), `SteppedPropRamps` bridges the unsupported
stretches between the rungs, and the synthesized ramp is clamped to at least `MinWidthMeters` (1.2 m) so a narrow
(0.6-0.7 m) ladder still leaves a navigable strip after erosion. With the setting off nothing changes: ladders are
still excluded and counted in `ladder=<n>` (the off build is byte-identical, neutrality `MATCH`). The new counter
appears in the assets log and in the snapshot `.txt`:

```
assets: entities=4336 physics=4366 included=4338 laddersIncluded=2 excluded=[flora=0 barrier=28 terrain=0 ladder=0 ...]
```

Harness `Test 24` mirrors the real setup - a ladder leaning 35 deg off vertical against a tall wall, with a small
doorway (1.0 m wide, 2.2 m high) at its top leading to the upper floor - and asserts end-to-end connectivity
through the doorway plus a walled-shut control:

| case | connection |
|---|---|
| a) door open, ramps off | **not connected** (rungs 0.7 m apart > the 0.4 m climb) |
| b) door open, ramps on | connected: lower->threshold, threshold->upper, lower->upper |
| c) door walled shut, ramps on, 2.0 m wall | **not connected** |
| d) door walled shut, ramps on, 0.5 m wall, stitch off | **not connected** (the ramp does not tunnel) |
| e) door walled shut, ramps on, 0.5 m wall, stitch on | **not connected** (V3.17: the merge-stitch refuses) |
| f) door open, ramps on, stitch off | connected (both threshold legs) |

The connection in (b)/(f) is made by the doorway **sill** (the top of the wall under the opening, i.e. the floor
running through it) once the ladder's top reaches it; the top apron itself now stops inside the wall instead of
poking `ApronMeters` (0.55 m) past the prop's footprint. That bound is the V3.16 synthesis change: the bottom and
top aprons are forced-walkable pads whose job is to flatten the flight's *own* ends to the floor / landing level so
their spans merge with what the prop stands on, and a pad authored inside whatever the prop leans on would
fabricate a walkable surface there (over a thin partition it would sit in the next room's air). They now reach at
most half a cell past the prop's own bottom / top-band footprint.

Case (e) was the V3.16 residual and is fixed in V3.17 (see *Stitching*): the merge-stitch pass used to bridge the
~0.3 m air gap over the wall block because its clearance probe sampled the stitch line too sparsely and could not
see the block's solid volume at all.

**V3.18: the two real ladders get ramps, and every skipped prop says why.** `civilian_ladder_6m` (at
`(300.05, 280.84, 15.60)`) and `civilian_ladder_5m` (`(302.53, 313.77, 16.63)`) are tilted ~61 deg from
horizontal, and neither used to synthesize anything. The reason was not steepness: each ladder's collision is a
**slanted plank** - 12 triangles forming a closed prism whose faces span the whole flight - so at every run
station the envelope carried the plank's *global* top (21.08 m), the envelope could not rise, and the synthesis
stopped at `no-rise` (0.000 m of rise over 31 stations) long before the slope rules. The ladder's own surface is
also the wrong shape to be the path: at ~0.7 m wide it is narrower than 3 cells, so `rcErodeWalkableArea` removes
all of it.

Two changes:

1. **Per-prop skip reasons.** Every prop that produces no ramp is now named in the log with the reason and the
   numbers that made it so, folded by (prop, reason) and capped:
   ```
   assets: ramps: skip 'sturgia_castle_stairs_c_l2' no-holes tris=79 z=[18.838..21.314] runSpan=4.307m stations=0/33 (x58 props)
   assets: ramps: skip 'wooden_platform_stairs_a'  no-rise  tris=132 z=[35.442..37.764] runSpan=3.226m stations=33/33 (x12 props)
   ```
   Reasons: `z-range`, `no-direction`, `vertical`, `too-short`, `no-support`, `no-rise`, `no-holes`,
   `within-climb`, `hole-steep`, `hole-long`, `hole-rise`, `hole-short`, `capped`, `chunk-refused`; hole reasons
   carry the offending hole's `run`/`rise`/`-> deg`.
2. **A Ladder fallback chain.** A `Ladder` prop's collision is a climbable plane, not a tread pattern: it has no
   holes to bridge and its own surface is too steep (and too narrow) to be the path. So when the hole-based
   synthesis found nothing to use, the builder now lays **one straight ramp from the prop's own bottom to its top**
   - exactly the chain the hole logic would produce if every station were a hole. It goes through the same
   `EmitChunk`, so the width clamp (>= `MinWidthMeters` = 1.2 m, which is what beats erosion), the slope cap, the
   dedupe and the two end aprons are identical to a hole bridge's. Measured on the re-dumped snapshot:
   `ramps: synthesized=2 props=2 ladders=2` (was 0), and

   | ramp | rise | run | slope | width |
   |---|---|---|---|---|
   | `civilian_ladder_6m` | 5.517 m | 2.920 m | 62.1 deg | 1.2 m |
   | `civilian_ladder_5m` | 4.453 m | 2.251 m | 63.2 deg | 1.2 m |

   with the emitted geometry at the props' own `z` ranges (`[15.563..21.080]` and `[16.597..21.050]`).

The fallback is deliberately restricted to `Ladder`-flagged props that produced no ramp at all, so nothing that
already worked changes: Test 23 (4/4), Test 24, the merge-stitch tests and the 18-instance `--coverage` tables are
untouched (all 36 rows bit-identical, worst |coverage delta| = 0.0). **Residual, same root cause, not covered:**
12 `wooden_platform_stairs_a` entities (and some `sturgia_*` stairs) also report `no-rise`/`no-holes` with
`stations=<all>/<all>`, i.e. their collision faces span the whole run and flatten the envelope too. They are
`HasSteps` props with measured 100 % level coverage, so the ladder rule was not extended to them.

**V3.28/V3.31: the offset symbolic connector (vertical ladders, and small doorways).** A vertical ladder has
no horizontal run, so nothing can be laid inside its own footprint, and its collision (a climbing plane) is
far too narrow to survive erosion. For a `Ladder` prop that the ordinary synthesis cannot connect, the
synthesizer now emits an **offset symbolic connector**: a bottom anchor moved horizontally from the ladder's
top so the slope is shallow enough to rasterise, joined to the ladder's own top. The rationale (from the
editor): a ladder *must* be navmeshed for bots to traverse it, and the navmesh may sit **near** the geometry -
the connector is a **link, not a surface an agent stands on beside the ladder**. The rules:

- **Slope/width.** `ConnectorSlopeDegrees = 45 deg` (V3.31; was 62 deg). A **steeper connector rasterises as
  one tall span per column** (e.g. `52..68` over a 2.9 m rise): the compact heightfield then takes the span's
  *top* as the walkable floor, so the lower floor under the ramp is swallowed and the foot never merges - the
  measured Test 26 break (ramp topped at 2.27 while the landing was at 3.40). At 45 deg each column's span is
  only ~1-2 cells thick and every section merges. Width `ConnectorWidthMeters = 3.4 m` for the ramp itself
  (what beats erosion).
- **The top pad crosses the doorway.** A ladder often leads into an opening that "barely fits a person". The
  pad runs **through the throat and overlaps the floor's first surviving navmesh cell** beyond it, or the link
  dies at the opening. It is emitted in each direction the probe shows clear (the prop's own run + the four
  axes), clipped at solid geometry. The pad is **narrow** (`ConnectorPadWidthMeters = 1.0 m`) because a wide pad
  would overlap the wall on both sides of a narrow throat; being a connector cell it is erosion-exempt, so it
  does not need a wide footprint. `ConnectorTopPadMeters = 3.0 m` (cap `3.6 m`).
- **Scene probe.** `Synthesize` stays soup-free: the caller may set `Prop.IsFreeAt(engineX, engineY, height)` -
  the collector wires it from the collected collision soup (`ScenePointFree`), the harness from the synthetic
  scene. The probe (a) aims the pad at the opening, (b) clips the pad where solid blocks it, and (c) refuses
  the **whole connector** if its ramp's centre line would pass through solid geometry, so a **walled-shut
  doorway is never tunnelled**.
- **Three exemptions**, all scoped to the connector's own cells: the **ledge** trim (a connector span the trim
  nulled is restored), the **clearance** filter (walkable spans within `walkableHeight` above the connector are
  unlinked so the link survives `FilterWalkableLowHeightSpans`), and **erosion** (the connector's own compact
  span the erosion inset killed has its area restored; `ErodeWalkableArea` only writes `areas[]`, never the
  span neighbours). Counters: `v2: connector: forcedInTile=<n> markedCells=<n> clearedCeilings=<n>
  erosionExempt=<n>`; the collector logs each pad's reach/direction as `assets: connector pad '<prop>'
  topPad=<m>m dir=(<x>,<y>)`.
- **Ladder-flag only**, and **byte-neutral** where it does not fire: Birtu's two `civilian_ladder_*` connectors
  stay 62.111/63.181 deg, islands 5, `forcedInTile=0` (both connect within their own footprint via the V3.18
  hole-bridge chain, so the offset connector never runs there).

Harness `Test 25` (a): a 90 deg ladder now connects (`sameIsland(foot,top)=True`, continuous climb).
Harness `Test 26` (new): a 1.0 x 2.0 m doorway through a 1.6 m wall - `lower->upper=True` with the pad
extension, `False` with the legacy 0.5 m pad (the control), and `False` when the doorway is walled shut (no
tunnelling), F=83, invariants PASS. **Residual, open and documented:** `Test 25 (c)`, a 60 deg leaning ladder,
still reports `connected=False` - it reaches the landing through the ordinary station/chain path (never the
offset connector), and its chain geometry tops out short of the landing. The same mechanism (a steep or short
chain not merging with the landing) is the remaining lever.

**V3.32: surface-seeking endpoints, the physics gap, and the geometry-difference sweep.** A ladder's metamesh
is often lined up visually while its **physics object is not**: the ladder's physics top can end a few cm
short of the doorway sill. The connector's endpoints are therefore **surface-seeking**: the ladder's own
top/bottom are *locators*, and `Prop.SeekSurface(engineX, engineY, fromHeight, up, reach)` (wired from the
collected soup, mirroring `IsFreeAt`) finds the nearest **solid top** within the reach - the landing above
(`ConnectorLandingReachMeters = 1.5 m`) or the floor below (`ConnectorFloorReachMeters = 1.5 m`), searched
over a small footprint (`ConnectorSeekRadiusMeters = 2.0 m`). The anchors snap to those surfaces; if none is
in reach the prop is skipped with `no-landing-in-reach` / `no-floor-in-reach`. The pad's flat top is aligned
to the landing, and the landing anchor is dropped 0.1 m (inside `walkableClimb`) so the pad's quad
rasterises into the same row as the floor slab.

Harness `Test 26` now also runs a **6 cm physics gap** (ladder top 2.94 vs sill 3.0): `lower->upper=True`
with the feature on. A **geometry-difference sweep** (`SweepDoorway26`, same scene, cheap parametric
variants) maps which real-geometry differences break the crossing and where the mesh dies:

| variant | lower->upper | dies at x |
|---|---|---|
| baseline (1.0 m opening, lintel 3.4, sill 3.0) | **TRUE** | - |
| (a) opening 0.7 m wide | **TRUE** | - |
| (b) **low lintel at 2.6 m** (2.4 m clear) | false | 5.10 |
| (c) **landing raised +0.2 m** | false | 5.10 |
| (c) landing lowered -0.2 m | TRUE | - |
| (d) ladder offset -0.35 m | TRUE | - |
| (e) wall 0.4 m | TRUE | - |
| (e) **wall 1.0 m** | false | 6.10 |

So the crossing is robust to a narrow opening, a ladder offset, a lower landing and thin walls, but breaks
when **the headroom above the crossing is small** (a low lintel, or a landing raised by ~0.2 m so the pad
sits just under the lintel) and at a **~1.0 m wall**. Both breakers die at the wall's near/mid face, where
the pad's landing-level span does not merge with the floor's voxel row. These are the candidates for the
real-map failure; the direction is a landing whose top sits within ~0.2 m of the opening's lintel/headroom.

**V3.33: the merge rule fixed (null-area low ceilings), and the corrected sweep.** The 1.0 m-wall breaker
was a real bug in `ClearConnectorCeilings`: the **ledge trim nulls a ceiling span's area first**, and the
clearance pass then **skipped** any span whose area was already `RC_NULL_AREA` - but
`FilterWalkableLowHeightSpans` reads `span.next.smin` **regardless of area**, so the linked-but-nulled
ceiling still killed the connector's landing span below it. The pass now **unlinks a low ceiling even when
its area is already null**. The corrected, re-run sweep (only the low-clearance case remains a limit):

| variant | lower->upper | faces | dies at x |
|---|---|---|---|
| baseline (1.0 m opening, lintel 3.4, sill 3.0) | **TRUE** | 80 | - |
| (a) opening 0.7 m wide | **TRUE** | 80 | - |
| (b) lintel only 0.2 m above the sill | false | 75 | 5.10 |
| (c) landing raised +0.2 m (lintel 3.6) | **TRUE** | 82 | - |
| (c) landing lowered -0.2 m | **TRUE** | 80 | - |
| (d) ladder offset -0.35 m | **TRUE** | 92 | - |
| (e) wall 0.4 m | **TRUE** | 70 | - |
| (e) wall 1.0 m | **TRUE** | 80 | - |

So the crossing now survives a narrow opening, a raised or lowered landing, a ladder offset and walls from
0.4 to 1.6 m. The one remaining limit, (b), is geometric: with only **0.2 m (one cell) of headroom** the
rasterizer merges the pad, the opening and the lintel into a **single solid span**, so there is no separate
landing span to keep. That is below the resolution the voxel core can represent, not a merge bug.

**The dump folder.** `DumpSnapshotFromEditor` wrote the three snapshot files without creating the target
folder; a scene in another module (e.g. `CC_20_siege` in `MyMaps`, which has `SceneObj` but no
`NavMeshPrefabs`) failed with `DirectoryNotFoundException`. The dump now calls
`Directory.CreateDirectory(folder)` (the export path already did). Behaviour is unchanged when the folder
exists.

**Basamu (first giant-map datapoint).** `mp_battle_Basamu_village` snapshot (grid 2049x2049, ~1 m spacing,
11084 entities, **laddersIncluded=3**): `--ramps on` builds **16384 tiles in ~16 s** (`polys=125204`,
`failed=0`), soup 6 496 534 v / 3 912 455 t, welded to **V=327018 F=540232 E=872756**, invariants **PASS**,
**islands kept = 1** (5204 islands / 76848 faces pruned), `connector forcedInTile=0` (the 3
`civilian_ladder_4m` entities do not trip the offset connector). The post-build island/probe reporting over
540 k faces exceeds a 5-minute budget (the build itself is 16 s), so per-object foot->top probes on Basamu
are left as a follow-up; the three ladders were located via the new `--ladders` listing mode
(`(1411.28,698.64,27.87)`, `(1397.65,715.14,27.19)`, `(1396.33,682.12,29.42)`).




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
NavMesh: assets: entities=... physics=... included=... laddersIncluded=... excluded=[flora=.. barrier=.. terrain=.. ladder=.. dynamic=.. noShape=.. empty=.. exceptions=.. unsolid=..] tris=..
         (laddersIncluded=<n> only when NavMeshVoxelSteppedProps=true and Ladder-flagged bodies were collected). cappedShapes=... scanMs=...
NavMesh: assets: kinds stairs=.. wall=.. floor=.. other=.. (name heuristic; log only)
NavMesh: assets: stepped: ramps=<n> stairs=<n> ladders=<n> holes=<n> noHoles=<n> skipped=<n> (ramps bridge unsupported stretches only; treads are ledge-exempt)   <- only when NavMeshVoxelSteppedProps=true
NavMesh: assets: stepped: skipped detail flat=<n> steep=<n> long=<n> capped=<n>; band=0.5m ...   <- only when something was skipped
NavMesh: assets: ramps: skip '<prop>' <reason> [run=..m rise=..m -> ..deg (holes=../..)] tris=.. z=[.. ..] runSpan=..m stations=<supported>/<total> [(x N props)]   <- one folded line per skipped prop asset, capped at 20 (V3.18)
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
NavMesh: v2: stepped: markedCells=<n> restored=<n> droppedClearance=<n> droppedOrphan=<n> droppedPlane=<n> aboveNear=<n> aboveFar=<n> (ledge-trim exemption, gated per span)   <- only when NavMeshVoxelSteppedProps=true and stepped props are present
NavMesh: v2: winding: flipped=<n>/<total> asset triangles (downward-wound)          <- per-triangle asset winding normalisation
NavMesh: v2: merge-stitch: pairs=<n> added=<n> refused=<n> (cap=6 cells)             <- cross-tile stitch over the merged mesh, before the prune (clearance probe: 0.1 m line sampling + a +/-0.2 m perpendicular band + crossing test, see Stitching)
NavMesh: v2: prune: removed <n> islands, <n> faces (<=400 faces each); largest removed=<n> at (x,y,z); kept=<n> islands
NavMesh: v2: prune: exempt-formed islands=<n> faces=<n> largest=<n> at (x,y,z) (restored stepped-prop leftovers, pruned regardless of the cap)   <- only when NavMeshVoxelSteppedProps=true and such an island exists
NavMesh: v2: weld verts=... -> ... faces=... edges=... degenerate=... (weldMerged=...)
NavMesh: v2: repaired mesh: removed <n> non-manifold faces
NavMesh: v2: repaired mesh: <n> bad facing(s) (... inverted + ... vertical, first at (x,y,z)), same-level overlaps dropped <n> (<a> of <b> overlapping pair(s) are disjoint, dropped <c> face(s)[, <d> reverted to keep the island count]), quads kept <k>/<m> in <t>ms
NavMesh: v2: invariants: faces=... edges=... boundary=... maxEdgeMult=.. dupFaces=.. selfLoops=.. minEdges=.. inverted=.. vertical=.. selfOverlap=<a>/<b> badQuadWalks=.. faceQualityMs=.. -> PASS
NavMesh: v2: islands=<n> totalFaces=... largest=[0:faces=... area=...m2 centroid=(x,y,z) zr=[min..max] | 1:... ] top10
NavMesh: v2: mesh V=... E=... F=... degenerate=... binBytes=... buildMs=...
NavMesh: v2 timings: tilesMs=... mergeMs=... totalMs=...
NavMesh: v2: tileMs min/avg/max=.../.../...
```

- `v2: tris input=... culledByAabb=...` - input triangles, ones outside every tile, and the number actually
  rasterized (higher than `input` near tile borders because each tile sees its neighbours' geometry).
- `assets: ...` (main thread, before the build) - the collision-geometry scan: entity/physics/included counts,
  `laddersIncluded=<n>` when the stepped-props feature collected `Ladder` bodies, the per-reason exclusion
  counts, total triangles, any capped shapes and the scan time (`scanMs`); plus a name-heuristic kind histogram
  and the twenty largest shapes. `assetsMs` on the perf/`done:` lines is the same scan's wall time.
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
- `v2: prune: exempt-formed islands=<n> faces=<n> largest=<n> at (x,y,z)` - only when
  `NavMeshVoxelSteppedProps` pruned an island that is a coalition of prunable fragments held together by
  exemption faces (see *Stairs & ladders*), plus `v2: prune: kept islands worst exemption fraction=<x>% of <n>
  faces` as the check that the real networks stay mixed-provenance.
- `v2: weld verts=... -> ... faces=... degenerate=...` - cross-tile welding, dropped zero-area faces.
- `v2: repaired mesh: removed <n> non-manifold faces` - the deterministic manifold repair (edge shared by
  more than two faces), unchanged by V3.46.
- `v2: repaired mesh: <n> bad facing(s) ... same-level overlaps dropped <n> ...` - the V3.46
  face-quality repair (see *Malformed faces*), with the first dropped face's position, how many of
  the overlapping pairs are vertex-disjoint (the only ones it will drop, because dropping either face
  of an edge-sharing pair can disconnect a one-face-wide neck), how many of its drops were reverted to
  keep the island count, and how many recorded quads survived the face renumbering.
- `v2: invariants: ... inverted=.. vertical=.. selfOverlap=<a>/<b> badQuadWalks=.. faceQualityMs=..` - the
  export gate; `faceQualityMs` is what the two new face scans cost.
  `inverted`/`vertical`/`badQuadWalks` are **blocking** (any of them means the bin is not written);
  `selfOverlap` is `disjointPairs/allPairs` and is **measured, not blocking** - see
  *Malformed faces* for why clearing the edge-sharing class would cost the island count.
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

### Steep-and-tall faces (the contour-spike "wall")

A face that is both **steeper than the walkable cap** and **taller than two agent climbs** is not a surface an
agent can be on, yet the engine draws it and the navmesh links it - a bot handed a path onto it runs off. The
user hit this on the CC_20 staircase: *"the part of the navmesh on the stairs that's causing a problem seems to
all originate from sharing a point ... it is that one single point that's causing the issue."*

**What it actually is.** One hub vertex, eight faces. Measured on CC_20 (`--census 1120.5,1077,5`,
`--vmesh 1120.5,1078.5,0.45`):

```
v77745 at (1120.500, 115.700, 1078.500) ring=13
    f133046 slope=87.1deg y=[115.70..119.50] A3d=2.745 fp=0.141
    f133047 slope=87.4deg y=[115.70..122.30] A3d=6.168 fp=0.281
    f133053 slope=81.6deg y=[115.70..124.10] A3d=6.649 fp=0.975
    f133055 slope=63.9deg y=[115.70..118.90] A3d=0.664 fp=0.292
    f133056 slope=69.6deg y=[115.70..124.10] A3d=1.504 fp=0.525
    f133061 slope=61.1deg y=[115.70..118.90] A3d=4.533 fp=2.188
    f239538 slope=84.5deg y=[115.70..121.50] A3d=6.862 fp=0.656
    f239539 slope=84.5deg y=[115.70..124.10] A3d=6.862 fp=0.656
```

Eight near-vertical triangles, 61.1-87.4 deg, own height ranges **3.2-8.4 m**, all on that one vertex,
leaping straight from the foot of the flight to treads 4-8 m up while the flight's own treads and risers run
past them at 39-55 deg.

The hub's height is the giveaway. `115.700 - 0.5 (the uniform lift) = 115.20`, and `115.20` is exactly the
`ch`-quantised span top of the plate shared by two props that occupy the same voxel column
(`--vprobe 1119.5,1077.5,1121.5,1079.5,114,117.5`):

| prop | flags | evidence |
|---|---|---|
| `bd_wood_heap_b` | `0x00000000` | top face `H=114.724` -> `ch` 0.2 quantised `114.7` -> `+0.5` lift = **115.2** |
| `stairs_bo` | `0x00000800` (HasSteps) | lowest tread `H=114.716`, same quantised height |
| `european_castle_ladder` | `0x00000000` | the same column, `H=[107.42..123.57]` - a staircase with **no flags at all** |

So the unflagged ladder and its flagged `stairs_bo` twin share a column with a wood heap whose top plate sits
at the height of the bottom tread; the region's contour wraps that vertical stack and the poly mesh wraps a
vertical step. That is a **Recast contour spike**, and it is born before any of our passes - it survives
`--detail off` (not the detail mesh), `--cleanup off` (not the sliver cleanup) and dropping
`RC_CONTOUR_TESS_WALL_EDGES` (not wall-edge tessellation - that experiment also cost 5 islands and was
rejected). V3.46's footprint gate cannot see it: a steep-but-not-exactly-vertical face has a strictly
**positive** XZ footprint, so `inverted=0` and `vertical=0` while the jump ships.

**The rule.** A face is dropped when

1. `slope > NavMeshMaxSlopeDegrees + SteepFaceMarginDegrees` (default **+10 deg**, so 60 deg at the shipped 50), **and**
2. its **own height range** exceeds `2*agentClimb + cellHeight` (**1.4 m** at climb 0.6 / ch 0.2).

Both tests are needed. Slope alone is provably wrong: on the suite's own connectivity lab the single face that
connects *"c) staircase 1.0 m wide, 6 x 0.4 m"* from its ground to its top platform is at **71.5 deg**, and a
pure 60 deg cut removes exactly that face and breaks the scenario. Its own height range is **1.20 m** against
3.2-8.4 m for the fan - and that is the physical difference: an agent moves between navmesh faces only where
they are within `walkableClimb` of each other, so a face taller than about two climbs has no point from which
it can be entered or left. A short steep face (a kerb, a single riser, the lab's stair) is left alone.

**The restore is MINIMAL, and that is the whole point.** Dropping these faces unconditionally (as the reverted
V3.47 did) fixes the staircase but costs CC_20 **two islands (15 -> 13)** and was reverted: one high-altitude
island (centroid `y 479-702 m`) really is only linked by a face over the cap. The island count is a hard
acceptance criterion - the engine creates one island per component - so the pass drops every marked face and
then **restores only the minimum set needed to hold the island count**, per split component, greedily picking
the face that touches the most fragments. A redundant spike is never picked (some other face in the same
component rejoins the fragments just as well); a sole-link connector is (it is the only one touching both
sides). Contour-spike faces - a marked face sharing a vertex with two or more other marked faces - are
deprioritised below every plain candidate, so the restore never picks a fan while any ordinary face can bridge
the same fragments.

Restoring *everything* in a split component (the overlap repair's coarser rule) was measured and is too blunt:
it reverted **1141 of 1205** marks and the "wall" survived untouched.

The pass runs **before the prune**, so any fragment a component that cannot be made whole leaves behind is
judged by the island cap rather than shipped. One exemption: a cell covered by a **forced-walkable** triangle
(a synthesized ramp or offset ladder connector) is the only cell allowed to carry a face above the cap - a
ladder chain legitimately runs steeper, and that is what makes a ladder traversable. A face counts as a ramp's
if its centroid cell **or any of its three vertex cells** is forced (Recast merges a ramp's span with the
surrounding columns, so the seam face lies one span outside the ramp's own footprint).

**Measured, CC_20 (V3.48 -> V3.49):**

| | V3.48 | V3.49 |
|---|---|---|
| islands | 15 | **15** |
| faces | 241727 | 241628 (-0.04%; inside the decimator's own run-to-run noise) |
| steep(>55 deg) faces at the staircase | 11 | **6** |
| steep area there | 39.108 m^2 | **13.657 m^2** |
| worst slope there | 87.39 deg | **72.08 deg** |
| the 81-87 deg leapfrog faces | 4 | **0** |
| pass cost | - | +429 ms on a 29 s build |

Suite verdicts byte-identical to the accepted baseline (all PASS except the documented LADDER-CAP failure).
**Honest residual:** three faces at 61-72 deg survive at the staircase because the measurement says they are
currently the only links - restoring nothing would break that piece of surface, and the pass prefers an honest
61 deg connector over an 87 deg wall. Whether the user's bots walk the flight is their re-import to judge.

Log line (warn-only, so a regression shows up as a number rather than shipping silently):

```
NavMesh: v2: steep faces: dropped 72 of 1205 marked face(s) > 60deg (maxSlope 50deg + 10deg margin) AND taller than 1.4m, worst=89.187deg, 1959 kept as short (<= 1.4m tall), first at (354.074,98.633,8.537), 116 of them in a fan at one hub, 1133 restored as the minimum needed to keep the island count in 429ms (before the prune, so any fragment it leaves is judged by the island cap)
```

and, on the gate line, `steep>60deg=<n> tallSteep=<m>` next to `inverted`/`vertical` (which *do* block).
`tallSteep` is the vertical-jump class: **non-zero means a jump survived.** Set
`TiledNavMeshSettings.SteepFaceMarginDegrees = 100` to disable the pass (the harness does this for its A/B
baseline); `20` is a conservative setting that only removes unmistakable walls (>= 70 deg).

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
  The tool detects this itself (the `prefab index:` mirror) and logs `import SKIPPED` with the reason instead
  of calling the importer, so this failure mode is reported, not silent.
- **`import SKIPPED ... scene module not active`** (or a `FOUND-inactive` line in the `prefab index:` block):
  the scene's module is not loaded in this editor session - not enabled in the launcher, or not a module at
  all (folder without `SubModule.xml`; the engine keys modules by `<Id>`, which is also not always the folder
  name - e.g. `Modules\MyMaps` has `Id="My Maps"`). The engine's index is built from the loaded modules only,
  so the name can never resolve from there. The tool writes a copy into
  `Modules\BetterBannerlordNavmeshing\NavMeshPrefabs` (it logs `fallback bin written: ...`); **restart the
  editor once**, then **Import Only**. To make the *game* and the dedicated server pick the prefab up as well,
  enable the scene's module in the launcher - the canonical bin is and stays in the scene's own module.
- **`folder: ... AMBIGUOUS: N folder(s) share the winning rank`** (or a `folder:   ignored ...` line for a
  module you expected): more than one module folder holds `SceneObj\<this scene>`, so the scene name alone
  cannot pick one. See *Which module gets the write* above. The tool logs **every** match with its rank and
  states the rule; if the loser is the one you want, set `NavMeshExportFolder` to that module's
  `NavMeshPrefabs` - it overrides all of this.
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
