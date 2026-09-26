# Navmesh v2 — Design & Phased Plan

Synthesis of three research streams (industry methods / Bannerlord engine capabilities / editor geometry
access). Companion documents: `navmesh-generation-research.md` (industry pipeline detail). This document is
the working plan for turning the tool from "terrain heightfield mesher" into a full-scene generator.

---

## 1. What the research settled

### 1.1 The stock generator is Recast/Detour — be output-compatible, not a clone
Native strings prove TW's generator calls `rcRasterizeTriangles`, `rcBuildCompactHeightfield`,
`rcBuildDistanceField`, `rcBuildContours`, `rcBuildRegions`, `rcBuildPolyMesh`, `rcBuildPolyMeshDetail`
via `rglNavigation_mesh_generator::generate_navigation_mesh`, and the editor exposes the standard Recast
parameter set (Cell Size/Height, Agent Height/Radius/Max Climb/Max Slope, Edge Max Error, Region Min/Merge
Size, Detail Sample*). The engine, its path queries, editor tools and validation messages are built around
Recast-shaped **output** — so the output contract (face topology, islands, masks, conventions) must be
matched. The **generation internals are ours to choose**, and the stock pipeline is demonstrably inadequate
at scale: it freezes for over an hour on large/complex scenes (e.g. Basamu) and sometimes crashes, which is
why that map was navmeshed by hand instead. Performance, robustness and quality at scale are therefore the
headline goals — a separate research pass is evaluating post-Recast and modern methods (adaptive
resolution, geometry-based meshing, tiling, parallel/incremental generation) to inform the design.

### 1.2 Exact asset collision geometry is readable from an editor script
`GameEntity.GetBodyShape()` (extension in `TaleWorlds.Engine.GameEntityPhysicsExtensions`) returns a
`PhysicsShape` with `TriangleMeshCount()`, `TriangleCountInTriangleMesh(m)`, `GetTriangle(Vec3[3], m, t)`.
Render-mesh vertices are NOT readable; **physics is the geometry source**. Classifiers available per entity:
`BodyFlags` (`HasSteps` = stairs, `Ladder`, `Barrier`/`Barrier3D`, `BodyOwnerFlora` = skip vegetation,
`BodyOwnerTerrain`, `Dynamic`/`Moveable`), `Mobility` (Stationary/Dynamic), `GetPrefabName()`, `Tags`.
Client and wEditor API surfaces are identical in 1.4.8.

### 1.3 Engine-side consumption requirements (hard constraints for our output)
1. **Connectivity/islands**: one connected face graph per traversable area — deck + its stairs/ramps +
   ground must share edges into ONE island (`PathFaceRecord.FaceIslandIndex`;
   `are_navigation_faces_on_same_island`). Our meshing must be edge-conforming at every place surfaces
   physically connect.
2. **Stacked surfaces**: face selection uses `heightDifferenceLimit = 1.5 m` (`Scene.NavMeshHeightLimit`).
   Stacked decks closer than 1.5 m are ambiguous; separate levels must be > 1.5 m apart (bridges normally
   are) or the query position must disambiguate.
3. **Level masks**: a face with `level_mask = 0` is never usable. `0xFFFFFFFF` works but the editor checker
   flags faces carrying more than one bit of `level_1/2/3` or of `civilian/siege` when the scene declares
   those levels. → Read the scene's declared levels at generation time and set masks to match (all-bits only
   when the scene declares none).
4. **Static face conventions**: group id **0**, `cost = 0`, `is_concave = 0`, `inverted = 0`, 32-entry group
   table left as-is. Do not bake dynamic ids 1–9 into a static prefab.
5. **Clearance**: ≥ 1.5 m clear width or AI cannot path (official guide) — enforced naturally by
   agent-radius erosion plus a min-width sanity check.
6. **Blockers are optional for correctness**: omitting faces already blocks movement; blocker faces add AI
   avoidance/tactics only, and there is no managed API for static blockers (editor-authored). Defer.
7. **No off-mesh links in NMG8**: not part of the prefab format. Ladders exist as a face attribute
   (`MarkFacesWithIdAsLadder`); treat as a later quality item.
8. **Cost/level_cost_index**: baked natively, no managed setter for static faces; leave 0 unless a concrete
   need appears.

### 1.4 Raycast-only sampling is the fallback, not the path
Top-down raycasts cannot see interiors, have no multi-hit API, and are ±½-cell approximate. With collision
triangles readable, the primary pipeline is true voxelization; raycasts remain the cross-check/oracle
(as already used by the terrain sampler).

---

## 2. Architecture (v2)

```
scene entities ──► classification (flags/tags/prefab) ──┐
terrain heightmap (exact, from GetTerrainHeightData) ────┤
                                                         ▼
                                   span-column voxelization (cs, ch)
                                                         │
                    filters: low-hanging obstacles, ledge, low-height (ceilings)
                                                         │
                          connectivity: |Δfloor| ≤ walkableClimb, clearance ≥ walkableHeight
                                                         │
                                   erosion by agent radius (distance field)
                                                         │
                          regions (watershed | layers) + min/merge area
                                                         │
                        contours (RDP ~1.3) ─► convex polys ─► tri/quad faces (≤4 verts)
                                                         │
                    culling/validation ─► NMG8 prefab (group 0, cost 0, scene level masks)
                                                         │
                                    write → NavMeshPrefabs → ImportNavigationMeshPrefab → save
```

Key points:
- **Span columns** (multi-surface per X/Y) are what make bridges/interiors work; this replaces the
  single-height assumption at every stage.
- The existing **adaptive heightfield mesher stays** as the terrain-only fast path (nice, error-bounded
  terrain triangles), but the primary full-scene mode is the voxel pipeline.
- Terrain is rasterized **directly from its heightmap** (exact, fast); props are rasterized from their
  collision triangles. No raycast grid needed in the primary path.
- Determinism: fixed bounds, fixed cell sizes, stable ordering, no thread-order reductions.

### Parameter defaults (from the industry research; expose in the editor panel)
| Param | Default | Notes |
|---|---|---|
| `cs` cell size | 0.30 m | r/2 outdoor; r/3 indoors/stairs |
| `ch` cell height | 0.20 m | cs/2 |
| agent height | 2.00 m | 1.5 m is the engine's pathing minimum, 2.0 is safe |
| agent radius | 0.60 m | erosion radius |
| agent max climb | 0.90 m | stairs: strengthen to real riser + margin |
| agent max slope | 45° | separate from climb |
| region min/merge | 8 / 20 | lower min indoors so closets survive |
| edge max error | 1.3 | contour simplification |
| edge max len | ≈ 8 × radius | 0 disables |

---

## 3. Phased plan

**Phase 0 — DONE.** Terrain-only pipeline running in the editor (sampler → v1 mesher → prefab → import).

**Phase 1 — Probe & verify (small, do next).**
One `Probe Geometry` button that produces a report (log file; toggle) answering the live questions:
- entity scan stats: counts and triangle totals by class (flora/terrain/static/dynamic/steps/ladder/barrier);
- `GetBodyShape` validity for batched/static props (`HasBatchedKinematicPhysicsFlag`) and empty shapes;
- `GetTriangle` coordinate space: dump a sample entity's triangle bounds under shape-local, body-local and
  world interpretations next to `GetGlobalBoundingBox()` — the matching one wins;
- ground-query layering: iterative down-march at the probe entity (ε 0.05) vs `GetGroundHeightAtPosition`
  under a known multi-level spot (topmost? ground?) ;
- scene level declarations: `GetUpgradeLevelCount/NameOfIndex/MaskOfLevelName` for the open scene.
Also in this phase: post-import verification message ("faces after import did not increase — the engine
builds its prefab list at startup; restart the editor and import again") + the log-file toggle.

**Phase 2 — Voxel core (terrain-only first).** Span columns, filters, connectivity, erosion, regions,
contours, poly→NMG8 emission; validated against the v1 terrain output on the same scenes.

**Phase 3 — Assets.** Rasterize prop collision triangles (flora skipped, dynamic skipped or static-snapped),
buildings block by absence of faces, walkable right up to walls via erosion; stairs via `HasSteps` + climb.

**Phase 4 — Multi-level.** Spans make bridges/tunnels/interiors work; enforce the > 1.5 m separation rule;
verify ramp/stair connectivity (single island) with the editor's `Select Unconnected Faces` checker.

**Phase 5 — Quality.** Region/contour tuning, per-scene level masks, parameter presets (outdoor
battlefield / town / interior), performance at scale (voxel budget, optional tiling), validation against the
editor's own tools (`Fix Concave Faces`, `Ensure Agent Height Probes`, spawn coverage).

**Phase 6 — Optional.** Blocker faces for AI tactics, ladder face marking, cost tuning.

---

## 4. Open live-test questions (Phase 1 answers these)
1. Does `GetBodyShape()` return populated shapes for batched/kinematic static props (buildings, stairs)?
2. Coordinate space of `PhysicsShape.GetTriangle` (shape/body/world)?
3. Which surface does `GetGroundHeightAtPosition` return with stacked geometry (topmost or ground)?
4. What levels do typical battle scenes declare (to choose face masks)?
5. Performance of per-entity triangle enumeration on a full scene.

## 5. Risks
- **Visuals without collision** → holes in the navmesh (mitigation: probe stats show uncollidable props).
- **Batched/instanced statics** may not expose per-entity shapes (Phase 1 decides; fallback = scene-level
  physics queries / raycast probing).
- **Large scenes**: voxel count grows with area/cs²; mitigations: coarse cs outdoors, tiling, and the
  adaptive mesher for terrain-only areas.
- **Quality bar ("make it good")**: mirroring the stock Recast semantics plus the editor's own validation
  tools is the standard; anything beyond that (nicer terrain triangles, smarter simplification) is polish
  on top of a correct base.
