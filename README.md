# BetterBannerlordNavmeshing

**Version 3.73**

Generates a navmesh for Mount & Blade II: Bannerlord scenes.

It reads the scene's live terrain and static collision geometry, builds a walkable navmesh, writes a
navmesh prefab, and imports its faces into the scene. It is intended for large or complex custom scenes
where the editor's built-in navmesh generator is not enough.

## Usage

- Start the editor with the **BetterBannerlordNavmeshing** module (available in Releases).
- Ensure the module for the map you're working on has a `NavMeshPrefabs` folder. If you use the tool
  without one, it will generate one for you, but you'll need to restart your editor.
- Ensure there's at least a small navmesh on the map already. If there isn't, quickly make one - even a
  single quad will do. If there wasn't one already, save and restart your editor. This is due to a
  limitation from not being able to run engine code directly.
- Place a **NavMeshGen** prefab on the map.
- Place your border probes if you haven't already, or it will do the entire map.
- Press **`ExportNavMesh`**.
- Wait a few seconds. You can go to the Visibility tab and enable Navmesh, or just click the navmesh
  button.
- Once the navmesh is generated, delete the **NavMeshGen** prefab and save.

Advanced options are available, but the tool has been tuned on a wide variety of stress-test maps to balance speed and quality.
Users are unlikely to get a better output outside of very specific scenarios. See the Settings section
below for more detail.

## Limitations
- Can do civil ladders, but not spawned ladders.
- As mentioned above, does not have some engine functions, so one must make a small navmesh, save and restart the editor 
  if it doesn't have one already or if the module doesn't have a NavMeshPrefabs folder.
- Small doorways can screw it up. From testing, I've learned that this is actually a blessing in disguise.
  If a doorway is too small for the navmesher to handle, a tall agent will clip through it.
  Increase the size of the prop, don't try to manually navmesh or adjust the settings.
  Commonly an issue with gatehouses, they're just undersized.
- Sometimes will screw up a little on staircases and switchbacks. Plan to refine this in the future.
- Likes to throw an occasional extra triangle between merlons or climb a small prop like arrow barrels.
  Has no effect on the actual in-game behavior of the AI, so I left this as-is. Low priority.
  
## Settings

### Simple view - `NavMeshGenerator`

| Control / field | Default | What it does |
|---|---|---|
| `ExportNavMesh` (button) | - | Builds the navmesh from the live scene and imports it. This is the normal action. |
| `ImportNavMeshOnly` (button) | - | Imports an already-written prefab without sampling or rebuilding. |
| `NavMeshMaxSlopeDegrees` | `50` | Maximum walkable slope. Steeper faces are not walkable. It also drives the agent-climb auto-derivation and the floor-like test used by water handling. Higher values open steep terrain, stairs and ramps but can make walls or roofs walkable. |
| `NavMeshWaterMode` | `Wadable` | How geometry below the scene's water level is handled. `Raise` lifts floor-like geometry to the water surface without culling it. `Cull` removes floor-like geometry below the water surface. `Wadable` behaves like `Cull` but raises and keeps ground within `WaterWadeDepthMeters` of the surface, so agents can wade it. All modes act only on floor-like triangles; walls and obstacles are never culled or clamped. Scenes whose water level is far below the terrain see no effect. |
| `WaterWadeDepthMeters` | `1.3` | Only used by `Wadable`. Water depth an agent can still traverse. Ground no deeper than this is raised to the surface and kept. The default is rounded from the human monster's swimming threshold (`Monster.StandingChestHeight` = 1.31 m in `Native\ModuleData\monsters.xml`). The saved C# field is `NavMeshWaterWadeDepthMeters`. |
| `NavMeshHeightOffsetMeters` | `0.5` | Uniform lift applied to the whole emitted mesh (terrain and structures), so it does not intersect the visual surface. `0` = as flush as the voxel grid allows. |

### Advanced options - `NavMeshGenerator_AdvancedOptions`

| Field | Default | What it does |
|---|---|---|
| `NavMeshSampleStride` | `2` | Terrain sampling decimation. `1` = every vertex, `2` = every other vertex. Higher values are faster and lower-fidelity. |
| `NavMeshMaxErrorMeters` | `0.25` | Maximum surface deviation from the sampled ground. Also bounds the detail mesh. Lower = tighter fit and more faces. |
| `NavMeshCoarsestSpacing` | `10` | Largest cell size in metres; this is the voxel tile size. Smaller = tighter bounds and more faces; larger = faster and coarser. |
| `NavMeshSteepFaceMarginDegrees` | `10` | Steep-face repair threshold, expressed as `NavMeshMaxSlopeDegrees + this`. `10` = 60 degrees at the default 50 degree cap. `0` = exactly the cap (more aggressive). Values `>= 100` disable the repair. |
| `NavMeshSteepFaceMinHeightClimbs` | `1.5` | Height allowance for the same repair, in agent-climb units (`1.5` = 1.1 m at climb 0.6 / cell height 0.2). A steep face shorter than this is kept as a step/kerb connector; taller faces are dropped unless needed for connectivity. |
| `NavMeshPrefabName` | `""` | Output prefab name. Empty = `<scene name>_navmesh`. |
| `NavMeshExportFolder` | `""` | Explicit output folder, overriding automatic scene/module resolution. It must be the `NavMeshPrefabs` folder of a loaded module for the in-editor import to resolve it. Empty = automatic. |
| `NavMeshClearExistingFaces` | `true` | Clear existing navmesh faces before importing the new ones (only when faces exist). |
| `NavMeshImportIntoScene` | `true` | Import the written prefab after writing. The v2 editor **Export** path always imports; this setting is mainly for v1 and standalone callers. |
| `NavMeshUseVoxelCore` | `true` | Use the v2 tiled voxel core. `false` = the older v1 adaptive terrain mesher (terrain only). |
| `NavMeshVoxelCellSize` | `0.25` | Voxel cell size in metres. `0.25` is required for 1.0-1.2 m doorways, walkways and stair treads to survive erosion and quantisation. `0.5` is fine for open terrain and much faster. |
| `NavMeshVoxelAgentClimbMeters` | `0` | Agent maximum climb in metres. `0` = auto-derive the smallest whole-voxel climb that makes `NavMeshMaxSlopeDegrees` traversable. A larger explicit value also connects taller single steps. |
| `NavMeshVoxelAgentRadius` | `0.25` | Agent radius in metres. Erosion is `ceil(radius / cellSize)` cells per side; the tile border is `3 + ceil(radius / cellSize)`. |
| `NavMeshVoxelLedgeFilter` | `true` | Recast ledge filter. On trims spans within `walkableClimb` of a drop, keeping the mesh off cliff edges, but it can also eat the border of narrow walkways that have drops on both sides. |
| `NavMeshVoxelGapFillCells` | `2` | Gap bridging in cells (`0` = off). Before erosion, fills a column with no walkable span when walkable spans flank it on opposite sides and their floors are within the agent climb. Closes hairline prefab and terrace seams. |
| `NavMeshVoxelStitchMaxGapCells` | `6` | Per-tile stitching in cells (`0` = off). Reconnects two mesh pieces whose nearest boundary edges are within this many cells and within the agent climb, by adding a small quad that reuses existing vertices. Refused across walls, doors and ceilings. |
| `NavMeshVoxelPruneIsolatedIslands` | `true` | Remove small isolated islands after the merge (sealed-void lids and stray fragments). |
| `NavMeshVoxelPruneMaxIslandFaces` | `400` | Face cap for the prune. Islands with at most this many faces are removed; `0` = off. |
| `NavMeshVoxelCleanup` | `true` | Final-mesh sliver cleanup: welds, drops degenerates, inserts midpoints into long thin faces, removes needle vertices and finishes with a Delaunay-style flip polish. `false` = the pre-cleanup mesh, for comparison. |
| `NavMeshVoxelQuadMerge` | `true` | Merge a still-thin face with a near-coplanar neighbour into one convex four-vertex face. Geometry is unchanged; only the shared interior edge disappears. `false` = triangles only. |
| `NavMeshVoxelBarriers` | `true` | Carve barrier bodies whose blocking mask stops agents into the navmesh as solid walls, so agents cannot path across them. Missile-only and weapon-only masks are deliberately not carved. `false` = pre-barrier output. |
| `NavMeshVoxelDetailMesh` | `true` | Detail mesh. Adds interior vertices so faces follow the terrain instead of chording under hills and knolls. `false` = leaner bin, but faces cut through terrain. |
| `NavMeshVoxelDetailSampleDist` | `2` | Detail-mesh sampling distance in metres. `0` = auto (`max(0.9, cellSize)`). Lower = more surface detail and more faces. |
| `NavMeshVoxelContourMaxError` | `2.0` | Contour simplification error in voxels. `0` = auto (`NavMeshMaxErrorMeters / cellSize`). Higher = fewer faces and looser contours. |
| `NavMeshVoxelIncludeAssets` | `true` | Include static asset collision geometry (interiors, stairs, decks, bridges) in the navmesh so structures are navigable. `false` = terrain only. |
| `NavMeshVoxelIncludeDynamic` | `true` | Include movable/dynamic physics props at their placed pose. |
| `NavMeshVoxelIncludeFlora` | `false` | Include flora collision (trees and bushes). Off by default: many small shapes add faces without being reliably navigable. |
| `NavMeshVoxelSteppedProps` | `true` | Stairs and ladders. Stair/ladder prop triangles are exempt from the ledge trim, and synthesized ramps bridge unsupported stretches of a ladder or run with true-level ends. |
| `NavMeshAsyncBuild` | `true` | Run the build on a background thread so the editor stays responsive during long builds. The v1 mesher is always synchronous. |

### Debug options - `NavMeshGenerator_DebugOptions`

| Control / field | Default | What it does |
|---|---|---|
| `ProbeGeometry` (button) | - | Writes a geometry probe to the log: entity classification, collision counts, coordinate-space checks, a column probe and scene level masks. Useful when the navmesh is missing surfaces or using the wrong geometry. |
| `DumpSceneSnapshot` (button) | - | Writes the terrain grid and per-entity collision triangles next to the bin, for offline inspection. Rarely needed for normal use. |
| `NavMeshCullBelowHeight` | `false` | v1-only absolute-height cull. Ignored by the v2 voxel core; use `NavMeshWaterMode` for v2 water handling. |
| `NavMeshMinHeight` | `0` | Height used by `NavMeshCullBelowHeight` (v1 only). |
| `NavMeshWriteObj` | `false` | Also write a `.obj` next to the bin (includes the height offset). |
| `NavMeshSnapshotAssertPoints` | `""` | Extra assert points for the snapshot header, format `"x,y;x,y;..."`. |
| `NavMeshVoxelAssetTrace` | `false` | Log one trace line per included entity immediately before its native triangle read. If a native call ever crashes, the last line names the culprit. |
| `NavMeshVoxelWriteSoupObj` | `false` | Also write the terrain and asset soup as `.obj` files next to the bin, for geometry inspection. |
| `NavMeshVoxelTerrainHoleAudit` | `false` | Compare sampled terrain vertices against the hole-aware terrain query and drop cells that disagree by more than 1 m, preventing false walkable lids over holes. Costs one native query per vertex. |
| `NavMeshConsoleMessages` | `true` | Mirror the important run lines (start, stage, progress, finish or failure) to the editor console, and in game to the player. The log file is unaffected. |
| `NavMeshWriteLogFile` | `true` | Write the per-session log file. |
| `NavMeshLogFolder` | `""` | Log folder override. Empty = the default Documents path. |
| `NavMeshOverlayText` | `false` | Draw the build status as floating text in the editor viewport. Requires `show_debug_text = 1` in the engine ini. |
| `NavMeshOverlayX` | `0` | Overlay text horizontal position in engine screen coordinates. |
| `NavMeshOverlayY` | `0` | Overlay text vertical position in engine screen coordinates. |
| `NavMeshOverlaySeconds` | `5` | How long the final `DONE`, import-skipped or `FAILED` line stays in the viewport. |
| `NavMeshBuildStatus` | live status | Updated automatically by `NavMeshGenerator`. Shows the current build stage. Editing it has no effect; it is overwritten on the next status change. |
