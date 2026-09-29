# Navmesh v2 — Design & Phased Plan

Synthesis of three research streams (industry methods / Bannerlord engine capabilities / editor geometry
access) plus a dedicated modern-methods & scale pass. Companion documents: `navmesh-generation-research.md`
(industry pipeline detail). This document is the working plan for turning the tool from "terrain heightfield
mesher" into a fast, robust full-scene generator.

---

## 1. What the research settled

### 1.1 The stock generator is Recast/Detour — be output-compatible, not a clone
Native strings prove TW's generator calls `rcRasterizeTriangles`, `rcBuildCompactHeightfield`,
`rcBuildDistanceField`, `rcBuildContours`, `rcBuildRegions`, `rcBuildPolyMesh`, `rcBuildPolyMeshDetail`
via `rglNavigation_mesh_generator::generate_navigation_mesh`, and the editor exposes the standard Recast
parameter set. The engine, its path queries, editor tools and validation messages are built around
Recast-shaped **output** — so the output contract (face topology, islands, masks, conventions) must be
matched. The **generation internals are ours to choose**, and the stock pipeline is demonstrably inadequate
at scale: it freezes for over an hour on large/complex scenes (e.g. Basamu) and sometimes crashes, which is
why that map was navmeshed by hand instead.

### 1.2 Exact asset collision geometry is readable from an editor script
`GameEntity.GetBodyShape()` (extension in `TaleWorlds.Engine.GameEntityPhysicsExtensions`) returns a
`PhysicsShape` with `TriangleMeshCount()`, `TriangleCountInTriangleMesh(m)`, `GetTriangle(Vec3[3], m, t)`.
Render-mesh vertices are NOT readable; **physics is the geometry source**. Classifiers per entity:
`BodyFlags` (`HasSteps` = stairs, `Ladder`, `Barrier`/`Barrier3D`, `BodyOwnerFlora` = skip vegetation,
`BodyOwnerTerrain`, `Dynamic`/`Moveable`), `Mobility`, `GetPrefabName()`, `Tags`.
Client and wEditor API surfaces are identical in 1.4.8.

### 1.3 Engine-side consumption requirements (hard constraints for our output)
1. **Connectivity/islands**: one connected face graph per traversable area — deck + stairs/ramps + ground
   must share edges into ONE island (`PathFaceRecord.FaceIslandIndex`).
2. **Stacked surfaces**: face selection uses `heightDifferenceLimit = 1.5 m` (`Scene.NavMeshHeightLimit`).
   Stacked decks must be > 1.5 m apart or the query position must disambiguate.
3. **Level masks**: a face with `level_mask = 0` is never usable; all-bits works but the editor checker flags
   faces carrying more than one bit of `level_1/2/3` or `civilian/siege` when the scene declares those
   levels → read the scene's declared levels and set masks to match.
4. **Static face conventions**: group id 0, `cost = 0`, `is_concave = 0`, `inverted = 0`, 32-entry group
   table as-is. No dynamic ids 1–9 in static prefabs.
5. **Clearance**: ≥ 1.5 m clear width or AI cannot path.
6. **Blockers optional** for correctness (absence of faces blocks movement; blockers add AI tactics).
7. **No off-mesh links in NMG8**; ladders exist as a face attribute (`MarkFacesWithIdAsLadder`) — later item.
8. **cost / level_cost_index**: baked natively, no managed setter for static faces; leave 0.

### 1.4 Raycast-only sampling is the fallback, not the path
Top-down rays cannot see interiors, there is no multi-hit API, and samples are ±½-cell approximate. With
collision triangles readable, the primary pipeline is true voxelization; raycasts stay as cross-check/oracle
(as the terrain sampler already uses).

### 1.5 Modern methods & scale — why the stock generator freezes, and what we do differently
**Diagnosis (from the modern-methods research):**
- Recast's internal stages (rasterize → filter → distance field → region → contour → poly → detail) are
  **single-threaded**; cost is proportional to voxel columns over the whole scene (`Area / cs²`),
  so uniform fine cells over a big complex map multiply into hours.
- Watershed partitioning is the slowest stage on large open maps.
- Multi-storey/dense stacks hit a **hard failure** in `rcBuildHeightfieldLayers`:
  `RC_MAX_LAYERS = 63` ("layer overflow … too many overlapping walkable platforms"); and `RC_MAX_NEIS = 16`
  silently fragments regions in dense geometry.
- Production engines never run the classic uniform single-threaded pass on big maps.

**How production engines do it (portable to us):**
- **Tiled builds + tile-level parallelism** (UE `FRecastTileGenerator` tasks; O3DE, Unity, Godot all tiled).
  Tiles are independent → parallel and deterministic if merged in sorted order.
- **Dirty-tile incremental rebuild** (DetourTileCache "half-baked" voxels; Unity's hashed
  `UpdateNavMeshDataAsync`; Unreal navigation data chunks).
- **Per-region resolution** (UE Low/Default/High via modifier volumes).
- **Chunk borders** for seam-free stitching (Godot `border_size`).

**Ranked techniques for us (impact / effort / contract risk):**
| # | Technique | Impact | Effort | Contract risk |
|---|---|---|---|---|
| 1 | Tile decomposition + tile-parallel build, bounded workers | ★★★★★ | Low–Med | None |
| 2 | Feed only nav-relevant collision triangles; BVH culling per tile | ★★★★☆ | Low | None |
| 3 | Largest acceptable `cs`/`ch` + **Monotone/Chunky-Monotone** partitioning (drop watershed) | ★★★★☆ | Low | Low |
| 4 | Adaptive coarse-to-fine refinement mask (slope/step/obstacle-distance driven, dilated at seams) | ★★★★☆ | Med | Low–Med |
| 5 | Incremental dirty-tile rebuild (per-tile geometry hash) | ★★★☆☆ | Med | None |
| 6 | Explicit 2.5D layer decomposition; split/raise layer limits; enforce ≥ 1.5 m stacking | ★★★☆☆ | Med | Medium |
| 7 | Hybrid: CDT-mesh large flat walkable surfaces, voxelize only complex/stepped areas | ★★★★★ | High | High (gate behind validation) |
| 8 | GPU rasterization | ★★★☆☆ | High | None (but no GPU plumbing in-editor → skip) |
| 9 | Intra-tile stage parallelism | ★★☆☆☆ | Very High | None → skip |

**Building block:** a C# port of Recast/Detour exists (`ikpil/DotRecast`) — running the real stages
in-process would cut implementation time massively; check its license terms before adopting (or port the
needed stages ourselves, as we did for the NMG8 writer). **DECIDED 2026-09-26: vendor a source subset of
DotRecast (pinned commit `5841f99e9881c05ca105079b25bb8faecf8b65a1`, ZLib license) into
`ThirdParty/DotRecast` and backport it to C# 7.3/net472 — see `docs/dotrecast-evaluation.md`.**

**Determinism & contract guardrails (apply throughout):**
fixed world origin for coarse/fine grids at power-of-two ratios; dilate the fine mask so seams are refined on
both sides; deterministic tile merge order; stable ordering for layer ids; convex output only, split to
tri/quad for NMG8; global union-find for islands; post-filter polys violating the ≥ 1.5 m stacking rule;
carry level masks through both meshing paths.

### 1.6 Verified API notes (from the Phase 1 implementation)
- Physics flag checks: `HasBatchedKinematicPhysicsFlag()` exists only on `WeakGameEntity`; `GameEntity.GetMobility()`
  returns the nested enum `GameEntity.Mobility { Stationary, Dynamic, DynamicForced }`.
- `BoundingBox` is `TaleWorlds.Engine.BoundingBox` with public `min`/`max` `Vec3` fields.
- Ground queries require an explicit `BodyFlags excludeFlags` (pass `BodyFlags.None` to include barriers too).
- `Scene.GetNavigationMeshForPosition(in Vec3, out int faceGroupId, float heightDifferenceLimit, bool excludeDynamicNavMesh)`.
- Raycast with entity: `Scene.RayCastForClosestEntityOrTerrain(Vec3 source, Vec3 target, out float distance, out Vec3 point, out WeakGameEntity entity, float thickness, BodyFlags excludeFlags)` — no normal out param (use the ground-query overload for normals).
- Whole-scene enumeration: `Scene.GetEntities(ref List<GameEntity>)`.
- A script's own `GameEntity` is a `WeakGameEntity` (no `GetGlobalFrame()`); use its bounding-box centre and `WeakEntity` members.

---

## 2. Architecture (v2-fast)

```
[0] INPUT & CLASSIFICATION      collision triangles (GetBodyShape) + terrain heightmap + flags/tags
[1] COARSE GLOBAL PASS          coarse span-column rasterize + Recast-style filters; reject empty tiles
[2] FINE MASK                   refine tiles/cells near slopes, steps, obstacles (dilate by one cell)
[3] TILED PARALLEL BUILD        per tile: fine voxelization + filters + Monotone/Chunky-Monotone regions
                                + contours → polys (tri/quad). (Later: CDT for large flat areas — gate.)
[4] LAYERS & STACKS             explicit layer resolution; enforce ≥ 1.5 m separation; assign level masks
[5] ASSEMBLY & CONNECTIVITY     stitch tiles with border overlap; global union-find islands
[6] DETERMINISM & INCREMENTAL   sorted merges; per-tile geometry hash; rebuild only dirty tiles
[7] VALIDATION GATE             convexity, winding, islands, stacking, coverage vs coarse reference
[8] EMIT & IMPORT               NMG8 prefab (group 0, cost 0), write, ImportNavigationMeshPrefab, save
```

Key points:
- **Span columns** (multi-surface per X/Y) make bridges/interiors work — no single-height assumption.
- Terrain is rasterized directly from its heightmap; props from collision triangles; raycasts only as oracle.
- The existing **adaptive heightfield mesher stays** as the terrain-only fast path (nice, error-bounded
  terrain triangles); the full-scene mode is the tiled voxel pipeline.
- All the classification/emission/import plumbing from Phase 0 stays.

### Parameter defaults (industry research; expose in the editor panel)
| Param | Default | Notes |
|---|---|---|
| `cs` cell size | 0.30 m | r/2 outdoor; r/3 indoors/stairs |
| `ch` cell height | 0.20 m | cs/2 |
| agent height | 2.00 m | 1.5 m is the engine minimum |
| agent radius | 0.60 m | erosion radius |
| agent max climb | 0.90 m | stairs: real riser + margin |
| agent max slope | 45° | independent of climb |
| region min/merge | 8 / 20 | lower min indoors |
| edge max error | 1.3 | contour simplification |
| edge max len | ≈ 8 × radius | 0 disables |
| tile size | 32–128 cells | production norm; bounded peak memory |

---

## 3. Phased plan

**Phase 0 — DONE.** Terrain-only pipeline running in the editor (sampler → v1 mesher → prefab → import;
whole map in 2–3 s).

**Phase 1 — Probe & verify (next).** `Probe Geometry` button reporting: entity scan by class/flags, triangle
totals, `GetBodyShape` validity (incl. batched statics), `GetTriangle` coordinate space vs
`GetGlobalBoundingBox`, ground-query layering, scene level declarations. Plus the detailed per-run log file
(done: `NavMeshLog`) and the startup-prefab-list hint (done).

**Phase 2 — Tiled voxel core (terrain-only first).** Span columns, filters, connectivity, erosion,
**Monotone/Chunky-Monotone** regions, contours → tri/quad polys → NMG8; **tile-parallel with sorted merge**;
validated against Phase 0 output on the same scenes. Kickoff decision: vendor the DotRecast source subset +
C# 7.3 backport (`docs/dotrecast-evaluation.md`).

**Status 2026-09-29.** Terrain-only v2 validated in-editor (Birtu + Na'ilu); detail mesh on by default;
hard mesh-invariant gate; native import fast (`docs/import-performance-investigation.md`). Closed the same
day: sliver/tight-face density (`sampleDist` 2 m, contour error 1.3 vox), off-main-thread build + perf
instrumentation, new defaults (stride 2, slope 50, voxel core on). **Phase 3 (asset collision ingestion)
landed:** entity classifier + combined terrain/asset soup, uniform +0.5 m anti-intersection lift across
terrain and assets, asset winding fix; harness multi-level tests (platform/room/stairs/bridge over a gully)
pass. Open: isolated walkable islands under ~16 m² are pruned by the region minimum (connected structures
unaffected); the detail mesh can carry locally steeper faces than the poly skeleton; height-snap for steep
terrain remains a candidate if it shows in-editor; in-editor validation of structures (Birtu) pending.

**Phase 3 — Assets.** Collision triangles with BVH culling; flora excluded, dynamic skipped or static-snapped;
buildings block by absence of faces, walkable to agent-radius edges; stairs via `HasSteps` + climb.

**Phase 4 — Multi-level.** Explicit layer handling; stacked separation ≥ 1.5 m; single-island connectivity
through ramps/stairs (verify with the editor's `Select Unconnected Faces`).

**Phase 5 — Adaptive resolution + incremental + quality.** Coarse-to-fine refinement mask; per-tile geometry
hash and dirty rebuild; parameter presets (battlefield/town/interior); level masks per scene; validation
against the editor's own checkers; performance targets at Basamu scale.

**Phase 6 — Hybrid CDT for flat surfaces (stretch).** Behind a strict validation gate; biggest potential
speedup for huge open maps.

**Phase 7 — Optional.** Blocker faces for AI tactics, ladder face marking, cost tuning.

---

## 4. Open live-test questions (Phase 1 answers these)
1. Does `GetBodyShape()` return populated shapes for batched/kinematic static props (buildings, stairs)?
2. Coordinate space of `PhysicsShape.GetTriangle` (shape/body/world)?
3. Which surface does `GetGroundHeightAtPosition` return with stacked geometry (topmost or ground)?
4. What levels do typical battle scenes declare (to choose face masks)?
5. Performance of per-entity triangle enumeration on a full scene.

## 5. Risks
- **Visuals without collision** → holes (probe stats reveal uncollidable props).
- **Batched/instanced statics** may not expose per-entity shapes (Phase 1 decides; fallback = scene-level
  physics queries / raycast probing).
- **Large scenes**: mitigated by tiling, coarse base cells, adaptive refinement, monotone partitioning and
  empty-tile rejection; the hybrid CDT path is the stretch goal.
- **DotRecast adoption**: DONE 2026-09-26 — ZLib license and API fit verified; vendoring + C# 7.3 backport is the first Phase 2 task (`docs/dotrecast-evaluation.md`).
- **Tile seams / adaptive seams**: fixed origin + border overlap + dilated fine mask; validated in Phase 2.

## 6. References
See `navmesh-generation-research.md` (Recast/Detour pipeline, parameters, multi-level, stairs, interiors,
off-mesh links, tiling/determinism). Modern-methods sources (tile-parallel builds, RC_MAX_LAYERS/RC_MAX_NEIS,
UE/O3DE/Unity/Godot production practice, NEOGEN/van Toll geometry-based meshing, DotRecast) are quoted in the
models' research report; key ones to keep at hand: RecastLayers.cpp (`RC_MAX_LAYERS_DEF 63`, `RC_MAX_NEIS 16`),
UE "Optimizing Navigation Mesh Generation Speed", Unity `UpdateNavMeshDataAsync`, Godot navigation meshes doc.
