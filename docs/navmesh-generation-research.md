# Navmesh Generation for Complex 3D Scenes
### A practitioner-grade research report for a Recast-style generator that must grow beyond a terrain heightfield

Scope: building a navmesh generator for Mount & Blade II: Bannerlord scenes. Today it meshes only a terrain heightfield. It must add buildings/assets (mesh right up to edges), stairs, building interiors, multiple vertical levels (bridge above ground), and general quality. Final output is a constrained prefab format: triangle/quad faces + per-face cost + per-face level.

All URLs are inline. All numeric defaults below are the Recast/Detour reference values, the Unreal/Unity wrappers around them, or explicit recommendations derived from them.

---

## Executive summary (methods to adopt)

1. **Adopt the Recast/Detour architecture wholesale rather than inventing one.** The canonical pipeline — triangle-soup → solid heightfield (span columns) → filters (low-hanging, ledge, low-height) → **compact heightfield** → agent-radius erosion → distance field → region partition → contour trace + simplify → convex poly mesh → detail mesh → Detour tile — already solves all five of the stated problems, including bridges and interiors, because a *column* holds a *stack of spans*, not one height. ([Recast intro](https://recastnav.com/md_Docs_2__1__Introduction.html), [Recast module functions](https://recastnav.com/group__recast.html))
2. **Model multi-level geometry as a multi-span heightfield (2.5D "layers"), not as a single heightfield.** One XZ column = a sorted list of solid spans; the open gaps between them are walkable. This is exactly how bridges over ground, tunnels, and stacked floors are represented. ([Voxel spans / compact heightfield](https://www.unrealdoc.com/p/navigation-mesh), [Mononen heightfield layers](https://digestingduck.blogspot.com/2011/03/heightfield-layer-progress-pt-2.html))
3. **Compute all agent-capability parameters in voxel units with the standard derivations**: `cs = r/2..r/3`, `ch = cs/2`, `walkableHeight = ceil(h/ch)`, `walkableClimb = ceil(maxClimb/ch)` (or `floor` as the sample does), `walkableRadius = ceil(r/cs)`, `maxEdgeLen = edgeMaxLen/cs`, `minRegionArea = regionMinSize²`, `mergeRegionArea = regionMergeSize²`, `detailSampleDist = cs*detailSampleDist`, `detailSampleMaxError = ch*detailSampleMaxError`. ([rcConfig](https://recastnav.com/structrcConfig.html), [Sample_SoloMesh.cpp](https://raw.githubusercontent.com/recastnavigation/recastnavigation/main/RecastDemo/Source/Sample_SoloMesh.cpp), [defaults in Sample.cpp](https://raw.githubusercontent.com/recastnavigation/recastnavigation/main/RecastDemo/Source/Sample.cpp))
4. **Erode by the agent radius in the compact heightfield (`rcErodeWalkableArea`) so runtime collision is a point-in-polygon test.** This is what makes navmesh reach "right up to edges" while remaining traversable by a capsule-width agent; erosion — not a post-process shave — is the correct place to enforce radius. ([rcErodeWalkableArea](https://recastnav.com/group__recast.html#gaf2bf6010fc80e9b6e8f4c0c1fef7a97f))
5. **Treat stairs as controlled vertical discontinuities, not as ramps.** Steps connect when riser ≤ `walkableClimb`; `ch` controls whether the navmesh follows each tread or collapses to a ramp; the detail mesh restores real step heights for agent placement. ([rcConfig `ch`/`walkableClimb`](https://recastnav.com/structrcConfig.html), [Unreal stairs tuning](https://www.youtube.com/watch?v=II5agHlxUe4))
6. **Use `walkableHeight`/`rcFilterWalkableLowHeightSpans` for interiors and overhangs**, and tune `cs`/`minRegionArea` downward indoors so doorways survive erosion and small rooms are not pruned. ([rcFilterWalkableLowHeightSpans](https://recastnav.com/group__recast.html), [Unity voxel/region guidance](https://docs.unity3d.com/2020.2/Documentation/Manual/nav-AdvancedSettings.html))
7. **Use off-mesh links for anything that is not a walkable surface** (jumps, drops, ladders, lifts, narrow doorways, teleports), with per-link area cost and direction; they are first-class Detour polys. ([dtOffMeshConnection](https://recastnav.com/structdtOffMeshConnection.html), [dtNavMeshCreateParams](https://recastnav.com/structdtNavMeshCreateParams.html))
8. **Generate on tiles with a border overlap, and make generation deterministic** (fixed snapped bounds, fixed cell sizes, stable ordering, deterministic reductions). Tiles enable streaming and localized rebuilds; `borderSize` prevents tile-edge erosion artifacts. ([rcConfig `tileSize`/`borderSize`](https://recastnav.com/structrcConfig.html), [Unity min-region/tile caveat](https://docs.unity3d.com/Packages/com.unity.ai.navigation@1.1/manual/NavMeshSurface.html), [tile-gap failure](https://forum.arongranberg.com/t/how-to-avoid-gaps-between-tiles-in-a-recast-graph/3107))
9. **Express gameplay semantics through Detour areas (cost) and user flags (traversal class)**, not through geometry rewrites: up to 64 areas with per-area cost on `dtQueryFilter`, 16-bit poly flags for include/exclude filtering. This maps cleanly to Bannerlord's per-face cost/level prefab fields. ([dtQueryFilter](https://recastnav.com/classdtQueryFilter.html), [Detour module](https://recastnav.com/group__detour.html), [Unity areas & costs](https://docs.unity3d.com/Packages/com.unity.ai.navigation@1.1/manual/AreasAndCosts.html))
10. **For your constrained prefab output (tri/quad faces + cost/level), emit each convex navmesh poly as a face, copy the Detour area → `cost`, and derive `level` from the dominant span's layer/tile-layer.** Keep off-mesh links and multi-level connectivity as explicit link records or reserved face flags; preserve the tile (x, y, layer) provenance until the very last export step.

---

## 1. Recast/Detour full pipeline

### 1.1 Input and triangle classification

Input is a triangle soup: floating-point vertices plus triples of indices. Recast is intentionally robust to overlapping geometry, small triangles, and imperfect meshes ([Recast intro](https://recastnav.com/md_Docs_2__1__Introduction.html)). Each triangle is assigned an 8-bit **area id**:

- `rcMarkWalkableTriangles(context, walkableSlopeAngle, verts, nverts, tris, ntris, triAreaIDs)` sets the area of every triangle whose surface normal differs from world up by less than `walkableSlopeAngle` to `RC_WALKABLE_AREA`.
- `rcClearUnwalkableTriangles` marks triangles at/above the slope as `RC_NULL_AREA` instead. ([Recast module](https://recastnav.com/group__recast.html))

`walkableSlopeAngle` is in **degrees**, practical upper limit ~85°; 45° is the RecastDemo default ([rcConfig](https://recastnav.com/structrcConfig.html), [Sample.cpp](https://raw.githubusercontent.com/recastnavigation/recastnavigation/main/RecastDemo/Source/Sample.cpp)). Vertical faces (walls, risers) are non-walkable; they become **obstructions**, not steps — step traversal is handled by `walkableClimb`, not slope.

### 1.2 Rasterization into spans (the "solid heightfield")

1. `rcCalcGridSize(bmin, bmax, cs, &width, &height)` computes the XZ grid extent.
2. `rcCreateHeightfield(..., width, height, bmin, bmax, cs, ch)` allocates a 2D array (width × height) of **columns**; each column is a linked list of **spans** sorted by height.
3. `rcRasterizeTriangles(...)` projects each triangle onto XZ, clips it to the cells it overlaps (conservative rasterization), and for each overlapped column computes the Y interval the triangle covers; that interval becomes a span `{smin, smax, area, flags, next}`. `flagMergeThreshold` (normally `walkableClimb`) controls how readily the area flags of two nearby spans merge. ([Recast module](https://recastnav.com/group__recast.html), [Unreal analysis](https://www.unrealdoc.com/p/navigation-mesh))

A span is **solid** from `smin` to `smax`; the open interval from one span's `smax` up to the next span's `smin` is candidate walkable space. **This stack is the entire multi-level mechanism** — one column can hold the ground span, the bridge-deck span, and a roof span simultaneously.

### 1.3 Filters (clean-up before compaction)

Run in this order; each is toggleable in RecastDemo:

- `rcFilterLowHangingWalkableObstacles(context, walkableClimb, hf)` — marks a non-walkable span walkable if its top is within `walkableClimb` of a walkable span below. Recovers low curbs and shallow steps that rasterize as separate spans. ([Recast module](https://recastnav.com/group__recast.html))
- `rcFilterLedgeSpans(context, walkableHeight, walkableClimb, hf)` — marks spans as non-walkable when the drop to any neighbor exceeds `walkableClimb` or the climb up to any neighbor exceeds it. This trims cliff tops and platform edges so agents are not routed off ledges. ([Recast module](https://recastnav.com/group__recast.html))
- `rcFilterWalkableLowHeightSpans(context, walkableHeight, hf)` — marks a walkable span non-walkable when the clearance to the span above (its ceiling) is less than `walkableHeight`. **This is the interior/overhang filter.** ([Recast module](https://recastnav.com/group__recast.html))

### 1.4 Compact heightfield and neighbor connectivity

`rcBuildCompactHeightfield(context, walkableHeight, walkableClimb, hf, chf)` converts the span linked lists into a flat array of `rcCompactSpan`s. Each compact span stores:

- `y` — floor height in voxels,
- `h` — clearance (floor-to-ceiling) to the span above, or 0xFF if there is none,
- `area`, `reg` (region id, later),
- `con[4]` — neighbor index per direction, or `RC_NOT_CONNECTED`.

Two adjacent columns' spans are connected only when their floor heights differ by **no more than `walkableClimb`** and they do not overlap. Larger vertical differences become **ledges** (disconnected). This one rule is what makes stairs, bridges with ramps, and terraced ground connect — and what keeps a bridge deck disconnected from the ground several metres below. ([Recast module](https://recastnav.com/group__recast.html), [Unreal analysis](https://www.unrealdoc.com/p/navigation-mesh))

### 1.5 Erosion

`rcErodeWalkableArea(context, erosionRadius = walkableRadius, chf)` marks any span closer to a border or obstruction than `walkableRadius` cells as non-walkable. With a non-zero radius, the runtime only needs to confirm the agent's **center** is inside a poly — no capsule-vs-navmesh-edge collision. This is the step that makes the mesh "reach up to the edge" in the sense that the *edge* is then offset by exactly the agent radius. ([rcErodeWalkableArea](https://recastnav.com/group__recast.html#gaf2bf6010fc80e9b6e8f4c0c1fef7a97f), [rcConfig `walkableRadius`](https://recastnav.com/structrcConfig.html))

Optional: `rcMedianFilterWalkableArea` smooths area ids (removes speckle); `rcMarkBoxArea` / `rcMarkConvexPolyArea` / `rcMarkCylinderArea` stamp gameplay areas (roads, water, doors) into the compact field. ([Recast module](https://recastnav.com/group__recast.html))

### 1.6 Distance field + watershed partitioning

- `rcBuildDistanceField(ctx, chf)` computes, per span, the distance to the nearest border (two-pass chamfer), with an optional box blur. Required by watershed. Results in `chf.dist` / `chf.maxDistance`. ([rcBuildDistanceField](https://recastnav.com/group__recast.html#ga255c830cc0d30fa0229a05033242e77d))
- `rcBuildRegions(ctx, chf, borderSize, minRegionArea, mergeRegionArea)` performs watershed partitioning seeded from local maxima of the distance field. ([rcBuildRegions](https://recastnav.com/group__recast.html#gaf89da5f503884e43f955639343333cea))

**Choose the partitioner deliberately.** The RecastDemo source documents the trade-offs precisely ([Sample_SoloMesh.cpp](https://raw.githubusercontent.com/recastnavigation/recastnavigation/main/RecastDemo/Source/Sample_SoloMesh.cpp)):

| Method | Function | Pros | Cons |
|---|---|---|---|
| Watershed | `rcBuildRegions` (needs distance field) | nicest tessellation; best for large open areas / precomputed mesh | slowest; can leave **holes** where a small obstacle sits in a large open area (harmless); can create **overlaps on narrow spiral corridors** (spiral stairs) → triangulation failure |
| Monotone | `rcBuildRegionsMonotone` (no distance field) | fastest; guaranteed no holes/overlaps | long thin polygons → path detours |
| Layers | `rcBuildLayerRegions` | fast; non-overlapping; better triangles than monotone; no watershed corner cases; good for tiled meshes | relies on triangulation to cope with holes (slower than monotone); can look ugly on large open areas with small obstacles (less so when tiled) |

`rcBuildHeightfieldLayers` further splits the compact field into 2.5D **layers** (`rcHeightfieldLayerSet`), the basis for Unreal's layered tiles and runtime tile rebuild. ([rcBuildHeightfieldLayers](https://recastnav.com/group__recast.html), [Mononen](https://digestingduck.blogspot.com/2011/03/heightfield-layer-progress-pt-2.html))

Region merging: `minRegionArea` deletes isolated regions smaller than `minRegionArea` voxels (set to `regionMinSize²`); `mergeRegionArea` merges regions with fewer spans than that into neighbors (set to `regionMergeSize²`). Watershed is noisy, so both matter. ([rcConfig](https://recastnav.com/structrcConfig.html))

### 1.7 Contours, polygon mesh, detail mesh

- `rcBuildContours(ctx, chf, maxError, maxEdgeLen, cset, RC_CONTOUR_TESS_WALL_EDGES)` traces each region's boundary at voxel resolution (raw contour), then simplifies it with **Ramer–Douglas–Peucker** bounded by `maxError` in voxels. Vertices at **portals between regions are mandatory and matched up**, so adjacent polygons' shared edges line up exactly. `maxEdgeLen` inserts extra vertices to break long edges. ([rcBuildContours](https://recastnav.com/group__recast.html#gae211dbabbd2a9e3e3e374452f3fa4dfc))
- `rcBuildPolyMesh(ctx, cset, nvp, pm)` produces convex polygons with ≤ `nvp` vertices. `nvp` must be ≤ `DT_VERTS_PER_POLYGON` = **6** for Detour. ([rcBuildPolyMesh](https://recastnav.com/group__recast.html#ga8688f9cb5dab904bbbe43c362a69e769), [Detour module](https://recastnav.com/group__detour.html))
- `rcBuildPolyMeshDetail(ctx, pm, chf, sampleDist, sampleMaxError, dmesh)` adds interior height-detail triangles so `dtNavMeshQuery::getPolyHeight` and `closestPointOnPoly` return the true surface height. **This is what keeps an agent's feet on stair treads and uneven ground.** ([rcBuildPolyMeshDetail](https://recastnav.com/group__recast.html#ga27bbc4ef9f8c09b68ec63ebe908521a2), [getPolyHeight](https://recastnav.com/classdtNavMeshQuery.html#af8303a12749e252c6688c15c5c887b69))

### 1.8 Detour build and runtime

**Build.** Marshal the Recast output into `dtNavMeshCreateParams`: `verts`, `vertCount`, `polys`, `polyFlags`, `polyAreas`, `polyCount`, `nvp`; optional detail mesh and off-mesh connections; `walkableHeight`, `walkableRadius`, `walkableClimb` (world units); `cs`, `ch`; `bmin`/`bmax`; `tileX`/`tileY`/`tileLayer`; `buildBvTree`. Then `dtCreateNavMeshData` → tile bytes → `dtNavMesh::init` (single tile) or `addTile` (multi-tile). Each tile is a `dtMeshTile`; layers are addressed as (x, y, layer). ([dtNavMeshCreateParams](https://recastnav.com/structdtNavMeshCreateParams.html), [dtNavMesh](https://recastnav.com/classdtNavMesh.html))

**Runtime** (`dtNavMeshQuery`, initialized with a node budget, e.g. 2048):

- `findNearestPoly(center, halfExtents, filter, &ref, &pt)` — box search; returns `DT_SUCCESS` with `ref == 0` if nothing found, so always check the ref. ([findNearestPoly](https://recastnav.com/classdtNavMeshQuery.html#a5317939092cb4b55fed4b0bb61afc74a))
- `findPath(startRef, endRef, startPos, endPos, filter, path, pathCount, maxPath)` — A* over the poly graph, including off-mesh links; returns a poly corridor (partial if unreachable). ([findPath](https://recastnav.com/classdtNavMeshQuery.html#a9d86502949fcd4f96ffd350d4ef69e3c))
- `findStraightPath(...)` — **string-pulling** (funnel) to turn the corridor into waypoints; flags each vertex `DT_STRAIGHTPATH_START` / `END` / `OFFMESH_CONNECTION`. ([findStraightPath](https://recastnav.com/classdtNavMeshQuery.html#ac160434c85bdf9edec24fa621f120d2d), [straight path flags](https://recastnav.com/group__detour.html))
- Sliced queries (`initSlicedFindPath` / `updateSlicedFindPath` / `finalizeSlicedFindPath`) amortize long searches across frames. ([sliced path](https://recastnav.com/classdtNavMeshQuery.html))
- Dijkstra utilities: `findPolysAroundCircle` / `AroundShape`, `getPathFromDijkstraSearch`, `findRandomPoint(AroundCircle)` for spawning/patrolling.
- Local movement/query: `moveAlongSurface`, `raycast`, `findDistanceToWall`, `getPolyWallSegments` (walls vs portals), `getPolyHeight`. ([dtNavMeshQuery](https://recastnav.com/classdtNavMeshQuery.html))
- Node pool is exposed via `getNodePool()`; `init(nav, maxNodes)` caps search nodes. ([getNodePool](https://recastnav.com/classdtNavMeshQuery.html#ac703297afb992c9e79f4a6e8754c8a89))

**Filters / costs.** `dtQueryFilter` has 16-bit `includeFlags` / `excludeFlags` and per-area cost. Default: all costs 1.0, all flags included, none excluded; a poly must have at least one flag to be considered; `includeFlags == 0` excludes everything; a poly with both an include and an exclude flag is excluded. Custom filters override `passFilter`/`getCost`. **Costs below 1.0 break A\***; keep cost proportional to travel distance. `DT_MAX_AREAS = 64`. ([dtQueryFilter](https://recastnav.com/classdtQueryFilter.html), [Detour module](https://recastnav.com/group__detour.html))

### 1.9 Parameter derivation (copy this table)

Formulas are from [rcConfig](https://recastnav.com/structrcConfig.html) and [Sample_SoloMesh.cpp](https://raw.githubusercontent.com/recastnavigation/recastnavigation/main/RecastDemo/Source/Sample_SoloMesh.cpp):

| Param | Formula (world → voxel) | Notes / default |
|---|---|---|
| `cs` | `r/2` (outdoor) … `r/3` (indoor) | cell size; practical min ~0.05; halving cs ≈ 4× RAM and build time |
| `ch` | `cs/2` typical | separate vertical resolution; smaller ch connects small curbs/ditches and helps stairs; practical min ~0.05 |
| `walkableSlopeAngle` | designer value (deg) | 45° default; ≤85° practical |
| `walkableHeight` | `ceil(h / ch)`, ≥3 | agent height clearance; detects overhangs |
| `walkableClimb` | `ceil(maxClimb / ch)` | rcConfig says ceil; `Sample_SoloMesh` uses `floorf` — prefer a conservative value that still clears the tallest real step |
| `walkableRadius` | `ceil(r / cs)` | erosion radius |
| `maxEdgeLen` | `edgeMaxLen / cs` | `edgeMaxLen ≈ walkableRadius*8`; 12 world default; 0 disables |
| `maxSimplificationError` | `edgeMaxError` (vx) | good range 1.1–1.5; **1.3 default** |
| `minRegionArea` | `regionMinSize²` | sample default regionMinSize 8 → 64 vx |
| `mergeRegionArea` | `regionMergeSize²` | sample default 20 → 400 vx |
| `maxVertsPerPoly` | 6 for Detour | `DT_VERTS_PER_POLYGON = 6` |
| `detailSampleDist` | `cs * detailSampleDist` | if `< 0.9` → 0 (off); sample factor 6.0 |
| `detailSampleMaxError` | `ch * detailSampleMaxError` | sample factor 1.0 |
| `tileSize` / `borderSize` | voxels | only for tiled builds; border overlap ≥ walkableRadius |

Reference baseline ([Sample.cpp](https://raw.githubusercontent.com/recastnavigation/recastnavigation/main/RecastDemo/Source/Sample.cpp)): `cs=0.30, ch=0.20, agentHeight=2.00, agentRadius=0.60, agentMaxClimb=0.90, agentMaxSlope=45, regionMinSize=8, regionMergeSize=20, edgeMaxLen=12.0, edgeMaxError=1.3, vertsPerPoly=6, detailSampleDist=6.0, detailSampleMaxError=1.0, partition=Watershed`.

---

## 2. Multi-level geometry (bridges, overpasses, interiors, stacked floors)

### 2.1 The span-column representation

The "heightfield" is *not* one height per (x,z). It is a raster over XZ whose cells each own a **sorted list of solid spans**. `rcCompactHeightfield` flattens these into spans that know their floor height `y` and ceiling clearance `h`. Consequently, at the (x,z) under a bridge there are (at least) two walkable surfaces: the deck span and the ground span beneath it. ([Unreal analysis](https://www.unrealdoc.com/p/navigation-mesh), [Recast module](https://recastnav.com/group__recast.html))

Mononen calls this **2.5D data**: the voxelized heightfield is split into 2D layers, stored compressed, and a tile is addressed as `(x, y, layer)`, "where the layer is just some number you decide yourself"; this also enables overlapping/streaming sections ([Heightfield layer progress pt. 2](https://digestingduck.blogspot.com/2011/03/heightfield-layer-progress-pt-2.html)). Detour exposes exactly this: `dtNavMesh::getTileAt(x, y, layer)` and `getTilesAt(x, y)` ("all layers"). ([dtNavMesh](https://recastnav.com/classdtNavMesh.html))

### 2.2 How levels are connected

- **Adjacent polys in the same level / tile** connect through shared edges (`dtLink`). (See [dtNavMesh overview](https://recastnav.com/classdtNavMesh.html).)
- **Across tiles**, edges are matched in the overlapping `borderSize` region; Unreal generates layered tiles in parallel and the layers cover a height range, so an inclined ramp can span multiple layers ([Unreal static generation](https://www.unrealdoc.com/p/navigation-mesh-static-gen)).
- **Between genuinely separate levels** (bridge deck to ground, ledge to ledge), there is normally **no portal** because the height difference exceeds `walkableClimb` — the spans are not connected. You connect them with an **off-mesh link** (a ramp of geometry is also acceptable if you can build it). ([dtOffMeshConnection](https://recastnav.com/structdtOffMeshConnection.html))
- **Region/portal semantics:** a *portal* is a passable segment between polys; a *wall* is impassable. `getPolyWallSegments` returns both, and a portal becomes a wall under a filter that excludes the neighbor. ([dtNavMeshQuery](https://recastnav.com/classdtNavMeshQuery.html))

### 2.3 What breaks if you assume one height per (x,z)

If your current generator writes a single terrain heightfield, it is equivalent to a Recast heightfield with only one span per column and a huge `walkableHeight`:

1. **Bridge / overpass:** the deck and the ground below collide in one value. Either the deck is lost or the ground is, depending on which sample wins; the mesh cannot represent both.
2. **Interiors / roofs:** a roof between the exterior ray and the floor hides the interior entirely — no interior navmesh.
3. **Tunnels / arches / porticos:** same problem; the arch top masks the walkable ground.
4. **Multi-storey buildings:** floor 2's height replaces floor 3's; only one survives.
5. **Ledges become invisible:** with no span stack you cannot run the ledge filter, so paths can run off cliff tops and players see AI walking off edges.
6. **Clearance can't be tested:** without a floor-to-ceiling `h` there is no way to say "this floor has a 1.6 m ceiling and the agent is 2.0 m tall."

**Migration path:** keep the terrain raster, but change the data structure from `height[x][z]` to `spans[x][z]` (a sorted list). Rasterize terrain into it as today, then rasterize building/asset **collision** triangles into the *same* field. Everything downstream (filters, compaction, erosion, regions, contours, Detour) is unchanged. This single structural change unlocks requirements (1)–(4).

---

## 3. Stairs

### 3.1 What the voxelizer does

1. Each tread is rasterized as its own span at its own height.
2. `rcFilterLowHangingWalkableObstacles(walkableClimb, ...)` promotes a higher span to walkable when its top is within `walkableClimb` of a walkable span below — this is what rescues a step that rasterized as a distinct "obstacle".
3. `rcBuildCompactHeightfield(..., walkableClimb, ...)` connects neighboring spans whose floor-height difference is ≤ `walkableClimb`. Steps within the climb budget therefore become a connected run; larger risers become ledges. ([Recast module](https://recastnav.com/group__recast.html))
4. `rcFilterLedgeSpans` ensures the *sides* of the staircase are not treated as walkable edges.

### 3.2 Stairs become a ramp or a staircase depending on `ch`

- **Large `ch` relative to the riser:** consecutive treads quantize into the same voxel row; their height differences vanish and the whole flight collapses toward a smooth **ramp**. This is cheaper (fewer polys) but less accurate.
- **Small `ch`:** each tread is distinguishable; the navmesh follows the **step tops** ("staircase" of small polys), and `rcBuildPolyMeshDetail` restores the exact tread heights so the agent's feet track the geometry.

The Unreal navmesh video demonstrates both ends of this: raising cell height generalizes all the step corners into a single walking plane (fewer polys, lower accuracy), while lowering it forces the mesh to resolve each elevation change ([Unreal navmesh tuning](https://www.youtube.com/watch?v=II5agHlxUe4)). Unity's "height mesh" toggle is the same idea: without it the navmesh is a sloping approximation of the stairs; with it the mesh matches the treads and the agent steps up like the render geometry ([Unity navmesh tutorial](https://www.youtube.com/watch?v=atCOd4o7tG4)).

### 3.3 Tuning and pitfalls

- **Set `walkableClimb` to the real maximum step-up**, usually a little above the stair riser (e.g. 0.4 m for a 0.2–0.3 m riser). Too low → the flight disconnects at every step; too high → the agent can climb walls, crates, and fences it should not ([Babylon.js Recast stairs thread](https://forum.babylonjs.com/t/using-recast-nav-mesh-with-stairs/32402)).
- **Keep `ch` small enough** that a riser is ≥ ~1–2 voxels; otherwise the step detail is lost. If you see "small holes" near stairs or curbs, decrease `ch` ([rcConfig](https://recastnav.com/structrcConfig.html)).
- **`walkableSlopeAngle` is irrelevant to risers** — the riser is a vertical, non-walkable triangle. Only `walkableClimb` bridges the step.
- **Agent capsule vs step geometry:** Recast models the agent as a *vertical cylinder/capsule* — radius enforced in 2D by erosion, height by `walkableHeight`. It does **not** model the capsule's rounded bottom or tuck (the foot). On stairs with short treads, erosion can pinch the walkable strip at the stringers, and the generated path can sit slightly above the nosing. Mitigations: use `cs = r/3` or finer for staircases, keep `detailSampleDist` small, and keep treads at least 2–3 cells deep.
- **Spiral stairs are a known watershed failure case** (overlapping regions → triangulation failure). Switch to **monotone** or **layer** partitioning for spiral stair flights, or generate them as off-mesh links / ramps. ([Sample_SoloMesh.cpp](https://raw.githubusercontent.com/recastnavigation/recastnavigation/main/RecastDemo/Source/Sample_SoloMesh.cpp))
- **If a flight is genuinely unwalkable** (too steep, too narrow for the agent), do not fight the parameters — represent it as an **off-mesh link** so an animation/controller drives the traversal.

---

## 4. Interiors & doorways

### 4.1 Ceilings and clearance

Every interior column has a floor span and a ceiling span. `walkableHeight` (in voxels) is the acceptance test:

- `rcFilterWalkableLowHeightSpans(walkableHeight, ...)` marks a floor non-walkable if the gap to the ceiling above is < `walkableHeight`. ([Recast module](https://recastnav.com/group__recast.html))
- **Set `walkableHeight` from the agent's height plus margin, and make sure it is smaller than the building's real floor-to-ceiling height** (account for beams, door lintels, and sloped ceilings, which are the tightest points). A lintel that creates a span can silently kill the floor under a doorway.
- Use a small `ch` so that thin ceilings/overhangs rasterize as their own span rather than merging into the floor.

### 4.2 Doorways and erosion

Erosion by `walkableRadius` shrinks every opening by roughly the agent radius on each side. An opening is navigable only if:

```
opening_width  >  2 * agent_radius  +  discretization_allowance
```

- Unity's guidance: leave **at least `4 * voxelSize` clearance beyond the agent radius**, especially for angled corridors ([Unity advanced bake settings](https://docs.unity3d.com/2020.2/Documentation/Manual/nav-AdvancedSettings.html)).
- A voxel-clearance planner recommends the agent capsule diameter **plus one horizontal cell per side**: for a 34 cm radius and 19 cm cell, a target doorway of ~106 cm (68 cm capsule + 2×19 cm) ([Navimo clearance planner](https://www.numivo.org/tools/navmesh-clearance)).
- Unity's own diagnosis checklist for "rooms won't connect": door opening < 2× Agent Radius, or threshold-to-lintel < Agent Height ([Unity forum: navmesh and doors](https://discussions.unity.com/t/navmesh-and-doors/657896)).

**Fixes, in order:** (1) reduce `cs` (finer grid preserves more of the opening); (2) reduce the agent radius / build a dedicated smaller agent; (3) model the door threshold as a ramp or confirm sill height ≤ `walkableClimb`; (4) if the doorway is still too narrow, bridge it with an **off-mesh link** ([Unity explicitly recommends off-mesh links for corridors smaller than baking supports](https://docs.unity3d.com/2020.2/Documentation/Manual/nav-AdvancedSettings.html)).

### 4.3 Small rooms and `minRegionArea`

`minRegionArea` deletes isolated regions below the threshold — "useful in removing useless regions that can sometimes form on geometry such as table tops, box tops, etc." ([rcConfig](https://recastnav.com/structrcConfig.html)). But a small storage room, alcove, or closet can be deleted too. Indoor advice: **lower `minRegionArea`** (often toward 0) and rely on `mergeRegionArea` plus `rcMedianFilterWalkableArea` to remove noise instead. Note that with tiled generation, an area straddling a tile boundary may **not** be pruned, so results can differ between tiled and solo builds ([Unity min region caveat](https://docs.unity3d.com/Packages/com.unity.ai.navigation@1.1/manual/NavMeshSurface.html)).

### 4.4 Door linking approaches

1. **Door as walkable area + flag/cost.** Keep the doorway walkable; assign a `DOOR` area/flag. The RecastDemo does exactly this: `SAMPLE_POLYAREA_DOOR` with flags `WALK | DOOR`, so a filter can exclude doors until opened, or charge a higher cost. ([Sample_SoloMesh.cpp](https://raw.githubusercontent.com/recastnavigation/recastnavigation/main/RecastDemo/Source/Sample_SoloMesh.cpp))
2. **Door as off-mesh link.** If the closed door's geometry would disconnect the rooms (or the opening is narrower than the capacity), place a link across the threshold, give it an area cost, and let the agent's controller decide to open the door. Unity lists "opening a door before walking through it" as a canonical off-mesh-link use ([Create Off-mesh Link](https://docs.unity3d.com/Packages/com.unity.ai.navigation@1.1/manual/CreateOffMeshLink.html)).
3. **Two builds / two flags** (closed vs open) and swap poly flags/area at runtime — cheap because Detour tile state (flags, areas) is separately storable/restorable (`storeTileState`/`restoreTileState`). ([dtNavMesh](https://recastnav.com/classdtNavMesh.html))

### 4.5 Indoor parameter starting point

`cs = r/3` (or finer); `ch = cs/2` or smaller; `walkableClimb` = real step; `walkableSlopeAngle` ~45°; `walkableHeight` = agent height + margin but < tightest real clearance; `minRegionArea` low/0; `mergeRegionArea` modest; partition = **monotone or layers** (avoid watershed overlaps in tight geometry); `maxSimplificationError` 1.3; `maxEdgeLen` small. If any of this is too expensive, build a **second, smaller navmesh for a dedicated small-agent** rather than over-resolving the main one (Unreal/Unity both support multiple agents).

---

## 5. Off-mesh links

### 5.1 What they are

An off-mesh connection is a **user-defined traversable edge between two points**, stored as a special 2-vertex polygon with a single edge (`DT_POLYTYPE_OFFMESH_CONNECTION`) linked to a normal ground polygon. At least one endpoint is inside a normal poly; the connection is "entered" from that poly. ([dtOffMeshConnection](https://recastnav.com/structdtOffMeshConnection.html), [dtNavMesh](https://recastnav.com/classdtNavMesh.html))

`dtOffMeshConnection` fields: `pos[6]` (ax,ay,az,bx,by,bz), `rad` (endpoint radius), `poly` (the connection's poly ref), `flags` (internal link flags — *not* the user flags), `side`, `userId`. Build-time data goes into `dtNavMeshCreateParams`: `offMeshConVerts`, `offMeshConRad`, `offMeshConFlags`, `offMeshConAreas`, `offMeshConDir`, `offMeshConUserID`, `offMeshConCount`. Direction is `0` (A→B only) or `DT_OFFMESH_CON_BIDIR`. ([dtOffMeshConnection](https://recastnav.com/structdtOffMeshConnection.html), [dtNavMeshCreateParams](https://recastnav.com/structdtNavMeshCreateParams.html))

### 5.2 How engines use them

- **Jumps, drops, ladders, lifts, teleports, narrow doors, vaults** — anything where the traversal is not ordinary walking on the surface. Unity's examples: jumping a ditch, dropping from a ledge, opening a door ([Create Off-mesh Link](https://docs.unity3d.com/Packages/com.unity.ai.navigation@1.1/manual/CreateOffMeshLink.html)).
- **Runtime handling:** `findStraightPath` marks the relevant vertex with `DT_STRAIGHTPATH_OFFMESH_CONNECTION`; the agent recognises it and plays the matching animation/controller. `dtNavMesh::getOffMeshConnectionPolyEndPoints(prevRef, polyRef, &start, &end)` returns the endpoints **ordered by direction of travel**. ([findStraightPath](https://recastnav.com/classdtNavMeshQuery.html#ac160434c85bdf9edec24fa621f120d2d), [getOffMeshConnectionPolyEndPoints](https://recastnav.com/classdtNavMesh.html))
- **Auto-generation:** Recast itself ships only an authoring tool; engines auto-detect common cases. Unity walks navmesh edges and tests the trajectory, creating **drop-down** and **jump-across** links. Practical advice from Unity: make the configured drop height / jump distance slightly *larger* than measured (to tolerate bake error) and add `2 * agentRadius + margin` to the jump distance because the measurement is edge-to-edge ([Unity automatic off-mesh links](https://docs.unity3d.com/2017.1/Documentation/Manual/nav-BuildingOffMeshLinksAutomatically.html)).

### 5.3 Cost / area semantics

- The connection has its own **area id**, so its cost is whatever the `dtQueryFilter` charges that area. ([dtNavMeshCreateParams `offMeshConAreas`](https://recastnav.com/structdtNavMeshCreateParams.html), [dtQueryFilter](https://recastnav.com/classdtQueryFilter.html))
- Higher-level engines add a **cost factor** (A* Pathfinding Project exposes one). Keep it **≥ 1.0**: a cost below 1 makes a route look shorter than its geometry and breaks A*'s admissibility guarantees. Use > 1 to make an agent prefer the free path, and a large cost to make it a last resort. ([A* Pathfinding off-mesh links](https://arongranberg.com/astar/documentation/stable/offmeshlinks2.html), [dtQueryFilter warning](https://recastnav.com/classdtQueryFilter.html))
- Use **one-way** links for intentional drops/jumps and **bidirectional** for ladders/doors.

### 5.4 When they are preferable to geometry

Use a link when:
- the traversal is not a walkable surface (ladder, rope, lift, teleport, climb),
- it needs a **custom animation or controller** (and you want the runtime to know when it happens),
- the geometry would be too narrow/steep for the agent,
- the geometry is expensive or cannot be rasterized reliably (spiral stairs, thin catwalks),
- it is a gameplay gate (open a door, pay a cost, require a key).

Do **not** use a link when a real walkable ramp/stair exists and should be navigated normally — extra links complicate crowd movement, animation state, and cost tuning.

---

## 6. Large worlds / quality

### 6.1 Tiling and streaming

- Build in **tiles** (`rcConfig.tileSize`), each a `dtMeshTile` at `(tileX, tileY, tileLayer)`. `dtNavMesh::addTile`/`removeTile` support streaming; `calcTileLoc` maps a world position to a tile; `getTilesAt(x,y)` returns all layers. ([dtNavMesh](https://recastnav.com/classdtNavMesh.html))
- **Overlap tiles by `borderSize`** so erosion/distance-field/region operations near a tile edge see neighboring geometry. `borderSize` is a "non-navigable border around the heightfield"; it does not affect internal obstructions. Without adequate border, tiles generated independently produce **gaps/seams and spurious detours along tile borders** ([rcConfig](https://recastnav.com/structrcConfig.html), [tile-gap report](https://forum.arongranberg.com/t/how-to-avoid-gaps-between-tiles-in-a-recast-graph/3107), [tile holes against nav bounds](https://forums.unrealengine.com/t/holes-in-navmesh/2762945)).
- **Tile size trade-off:** small tiles → more fragmentation, more seams, more per-tile overhead; large tiles → more memory and longer rebuild spikes. Unity's default of 256 voxels is described as "a good tradeoff between memory usage and NavMesh fragmentation" ([Unity NavMesh Surface](https://docs.unity3d.com/Packages/com.unity.ai.navigation@1.1/manual/NavMeshSurface.html)). Unreal parallelizes tile generation with its task system and offers low/default/high resolution presets per tile ([Unreal static generation](https://www.unrealdoc.com/p/navigation-mesh-static-gen)).
- Prefer the **layer** partitioner for small/medium tiles ([Sample_SoloMesh.cpp](https://raw.githubusercontent.com/recastnavigation/recastnavigation/main/RecastDemo/Source/Sample_SoloMesh.cpp)); it is designed for tiled meshes and avoids watershed's corner cases.

### 6.2 Deterministic generation

Make builds byte-reproducible across runs (and ideally platforms):

1. Fix `bmin`/`bmax` and snap them to the grid; never derive bounds from floating-point mesh bounds if those can vary.
2. Fix `cs`/`ch` and all voxel-unit parameters; avoid auto-deriving them from measured geometry.
3. Sort input triangles deterministically (weld + sort) before rasterizing; do not iterate hash maps keyed by pointer.
4. Use deterministic tie-breaks in region growing and contour tracing; avoid parallel reductions whose summation order depends on thread scheduling. Unreal exposes `GNavmeshSynchronousTileGeneration` precisely to debug tile-order effects ([Unreal static generation](https://www.unrealdoc.com/p/navigation-mesh-static-gen)).
5. Round positions to the grid before comparison, and store quantized ints (Recast already does this in `rcPolyMesh`).
6. Note that Unity's navmesh is reported to be **not cross-platform deterministic**, which matters for lockstep multiplayer; if that is a requirement, budget for a fixed-point implementation ([jdxdev CDT/RTS notes](https://www.jdxdev.com/blog/2021/07/06/rts-pathfinding-2-dynamic-navmesh-with-constrained-delaunay-triangles)).

### 6.3 Avoiding slivers / degenerate polygons

- Cap `maxEdgeLen` (`≈ walkableRadius*8`) — long contour edges become long thin triangles otherwise ([rcConfig](https://recastnav.com/structrcConfig.html)).
- Keep `maxSimplificationError` in `[1.1, 1.5]`; `1.3` is the sweet spot. Below 1.1 you get sawtooth edges; above 1.5 the simplifier "cuts corners it shouldn't" ([rcConfig](https://recastnav.com/structrcConfig.html)).
- Cap `maxVertsPerPoly` at 6 for Detour, and weld/merge collinear vertices during export.
- Prefer **layers/watershed** over **monotone** if path detours matter; monotone is fastest but produces long thin polys that cause detours ([Sample_SoloMesh.cpp](https://raw.githubusercontent.com/recastnavigation/recastnavigation/main/RecastDemo/Source/Sample_SoloMesh.cpp)).
- After generation, drop zero-area polys, enforce CCW winding, and validate convexity and simple boundaries (Detour assumes convex, non-overlapping polys).

### 6.4 Region size vs path quality

Large regions → fewer polys, faster search and lower memory, but the A* node can sit anywhere inside a big poly, so the corridor may look like a detour next to small obstacles. Small regions → better portal placement and more natural paths, at higher poly count. Unity documents this directly: node placement in big polygons "can be noticeable in scenarios where big open areas are next to tiny obstacles" ([Unity areas and costs](https://docs.unity3d.com/Packages/com.unity.ai.navigation@1.1/manual/AreasAndCosts.html)). Tune `minRegionArea`/`mergeRegionArea` and `maxEdgeLen` to trade this against poly count.

### 6.5 Gameplay area costs

Assign a **poly area id** (0–63) and let `dtQueryFilter::setAreaCost(id, cost)` express gameplay: roads cheap (0.8–1.0), grass normal (1.0), mud/sand costly (2–3), shallow water very costly (4–8) or excluded, deep water excluded, doors costly until opened. Cost must be **proportional to distance and ≥ 1**. Stamp areas with `rcMarkBoxArea` / `rcMarkConvexPolyArea` / `rcMarkCylinderArea` and smooth them with `rcMedianFilterWalkableArea`; the RecastDemo carries `GROUND/WATER/ROAD/DOOR/GRASS/JUMP` as example areas ([Recast module](https://recastnav.com/group__recast.html), [Sample.cpp](https://raw.githubusercontent.com/recastnavigation/recastnavigation/main/RecastDemo/Source/Sample.cpp), [dtQueryFilter](https://recastnav.com/classdtQueryFilter.html)).

### 6.6 Common failure modes and mitigations

| Symptom | Likely cause | Mitigation |
|---|---|---|
| "Bald spots"/holes in the mesh | bad/tiny/overlapping collision geometry, open meshes, render mesh used instead of collision | fix or simplify collision; navmesh from colliders, not visuals; Unreal demo shows geometry is the root cause, not Recast settings ([Unreal navmesh tuning](https://www.youtube.com/watch?v=II5agHlxUe4)) |
| Mesh floats above or sinks below the ground | `ch` too large, or no detail mesh | reduce `ch`; build `rcPolyMeshDetail`; use `getPolyHeight`/`closestPointOnPoly` for placement ([rcConfig](https://recastnav.com/structrcConfig.html)) |
| Corridors/doorways close up | erosion by agent radius + coarse `cs` | smaller `cs` (r/3), smaller agent, or off-mesh link ([Unity](https://docs.unity3d.com/2020.2/Documentation/Manual/nav-AdvancedSettings.html)) |
| Stairs break / agent won't climb | `walkableClimb` too low or `ch` too high | set climb to real step; reduce `ch`; use detail mesh ([rcConfig](https://recastnav.com/structrcConfig.html)) |
| Small rooms / ledges vanish | `minRegionArea` too high | lower it; use `mergeRegionArea` + median filter for noise ([rcConfig](https://recastnav.com/structrcConfig.html), [Unity](https://docs.unity3d.com/2020.2/Documentation/Manual/nav-AdvancedSettings.html)) |
| Holes/overlaps on spiral stairs | watershed partitioning corner case | use monotone or layers ([Sample_SoloMesh.cpp](https://raw.githubusercontent.com/recastnavigation/recastnavigation/main/RecastDemo/Source/Sample_SoloMesh.cpp)) |
| Paths detour in big open areas | watershed subdividing around small obstacles, or huge regions | increase `mergeRegionArea`; add `maxEdgeLen`; prefer watershed for open worlds ([Sample_SoloMesh.cpp](https://raw.githubusercontent.com/recastnavigation/recastnavigation/main/RecastDemo/Source/Sample_SoloMesh.cpp)) |
| Gaps/seams along tile borders | insufficient `borderSize`, tiles built without neighbor context | increase border overlap; don't use very small tiles ([tile-gap report](https://forum.arongranberg.com/t/how-to-avoid-gaps-between-tiles-in-a-recast-graph/3107)) |
| Non-reproducible builds | thread-order dependent reductions, unstable bounds/ordering | fix bounds and ordering; synchronous tile debug; fixed-point if cross-platform needed ([Unreal static generation](https://www.unrealdoc.com/p/navigation-mesh-static-gen), [jdxdev](https://www.jdxdev.com/blog/2021/07/06/rts-pathfinding-2-dynamic-navmesh-with-constrained-delaunay-triangles)) |
| Agent walks under a low overhang | `walkableHeight` too small / `ch` too large | increase `walkableHeight`; reduce `ch`; re-check lintels |

---

## 7. Alternatives and critiques

### 7.1 Heightfield-only vs true volumetric

- **Single-height heightfield:** cheapest, fine for open terrain, but cannot represent bridges, roofs, tunnels, or stacked floors (Section 2.3). It is a special case of Recast with one span per column.
- **Recast's 2.5D span stack ("multi-layer heightfield"):** a *generalization* of a heightfield that keeps 1..n spans per column. It handles overhangs, bridges, and interiors while retaining a regular XZ grid, which is why tiling, erosion, and distance fields remain simple. Recast is explicit that this is a 2D grid with height variation, not a full 3D occupancy grid ([Unreal analysis](https://www.unrealdoc.com/p/navigation-mesh), [Mononen 2.5D layers](https://digestingduck.blogspot.com/2011/03/heightfield-layer-progress-pt-2.html)).
- **True 3D volumetric / voxel:** can represent arbitrary non-manifold navigation (e.g. helical tunnels, caves), but costs far more memory and time, complicates region/contour logic, and is unnecessary for the common "bridge over ground" case. The practical sweet spot for Bannerlord scenes is the 2.5D span stack.
- **Known Recast limitations to design around:** conservative rasterization means sub-voxel features are lost; watershed has documented hole/overlap corner cases; penetration/thin geometry can create spurious spans; the mesh is only as good as the collision geometry it is fed.

### 7.2 Post-Recast thinking worth borrowing

- **Heightfield layers for streaming and dynamic rebuilds.** Mononen's layer work splits the voxelized field into compressed 2D layers, addresses tiles as `(x, y, layer)`, and shows one tile layer builds in 0.01–0.2 ms using < 15 kB, with the whole test level at 545 kB (77 kB compressed). This is the model for fast incremental rebuilds and overlapping/streaming navmesh sections ([pt. 2](https://digestingduck.blogspot.com/2011/03/heightfield-layer-progress-pt-2.html), [pt. 3](https://digestingduck.blogspot.com/2011/03/heightfield-layer-progress-pt-3.html)).
- **GPU-assisted rasterization.** Broden & Bohlin implemented GPU compute rasterization of the triangle soup, then converted the resulting voxel field back into Recast's run-length-encoded heightfield so the rest of the pipeline is reused. Reported limits: VRAM-bound field size, no custom-area support, and raster results that can differ from Recast's CPU rasterizer. Worth borrowing only the coverage pass for very large scenes; keep the CPU pipeline for determinism and areas ([Towards Real-Time NavMesh Generation Using GPU, DiVA](https://www.diva-portal.org/smash/get/diva2:1104795/FULLTEXT02.pdf)).
- **Constrained Delaunay triangulation (CDT) / geometry-based generation.** Build a 2D CDT of floor footprints with obstacle edges as constraints; this is how StarCraft II-style navmeshes are described, and it hugs obstacle boundaries exactly with no voxel quantization ([jdxdev RTS navmesh with CDT](https://www.jdxdev.com/blog/2021/07/06/rts-pathfinding-2-dynamic-navmesh-with-constrained-delaunay-triangles), [tchayen CDT](https://tchayen.com/handmade-pathfinding-mesh-for-games)). It is excellent for clean planar/indoor layouts and dynamic obstacle insertion, but it needs robust 2D footprint extraction, robust geometric predicates, and careful dynamic updates; it is weak on irregular terrain and genuinely 3D overhangs. Use it as a complement (e.g. to get exact building-edge fitting), not as a wholesale replacement.
- **Hybrid recommendation:** voxel spans for terrain, stairs, multi-level and interiors; CDT or contour-snap for exact asset-edge fitting where the rasterizer rounds corners; off-mesh links for everything else.

### 7.3 Supporting techniques

- Hierarchical/clustered pathfinding (HPA*-style) and path caching for very large worlds, plus query batching to avoid CPU spikes ([navmesh overview](https://moonjump.com/game-dev-mechanics-navigation-meshes-navmesh-how-it-works)).
- Detail mesh for accurate placement (getPolyHeight), and tile state save/restore for door/area toggles ([dtNavMesh](https://recastnav.com/classdtNavMesh.html)).
- Multi-agent navmeshes (one build per agent size) rather than over-resolving a single mesh ([Unreal static generation](https://www.unrealdoc.com/p/navigation-mesh-static-gen)).

---

## 8. Bannerlord-adjacent notes (bonus)

Public information is sparse, but it is enough to constrain the design:

- **The stock scene editor navmesh is hand-authored.** Designers place vertices/edges/faces (quads by default) with an inspector; there is **no automatic generation and no undo** for navmesh edits. Faces carry an **ID** (used like an area/role) and can be assigned to **visibility layers/masks**. ([Official Navmesh guide](https://moddocs.bannerlord.com/editor/scene-editor/nav_mesh/))
- **Rules designers are taught:** apply navmesh to every surface the player can walk on and *not* under physical objects; maintain a **1.5 m diameter** clear width or AI cannot path; **spawn-point pivots must lie inside navmesh faces** or spawns fail; AI **cannot jump**; a debug "Terrain Angle" view (limit 50°) shows traversable (green) vs steep (red) ground. ([Official Navmesh guide](https://moddocs.bannerlord.com/editor/scene-editor/nav_mesh/))
- **IDs are the gameplay/cost/level mechanism:** the official guide documents ID 0 (basic), village scenes with multiple IDs, and siege-town scenes with separate civilian/siege visibility. The `navigation_mesh_deactivator` script exposes `DisableFaceWithId` and `DisableFaceWithIdForAnimals`; ID 2 is the walk-active faces, ID 1 is present-but-inactive-until-raid, ID 3 separates animals. ([Official Navmesh guide](https://moddocs.bannerlord.com/editor/scene-editor/nav_mesh/))
- **Civilian vs siege is layer-based.** Community guidance: keep separate navmeshes and organize them into **6 layers** (civilian levels 1–3, siege levels 1–3) selected by visibility masks; individual faces can be hidden per level to disable ladders or destroyed walls ([BannerlordModding.LT Navmesh](https://docs.bannerlordmodding.lt/editor/navmesh)). This maps directly to your **per-face level** field.
- **Runtime pathfinding is face-based.** Public crash reports reference `SandBox.MapScene.GetNavigationMeshCenterPosition(PathFaceRecord face)` and `PathFaceRecord`, indicating the engine stores navigation as **faces with IDs and center positions** and that pathfinding operates on face records — consistent with your quad/tri face prefab and per-face cost/level design. Known pain points in the reports are memory corruption/errors when editing or resolving pathfinding in large/complex scenes, i.e. the manual workflow is fragile at scale. ([Taleworlds forum: Map Pathfinding Error](https://forums.taleworlds.com/index.php?threads/map-pathfinding-error-sandbox-mapscene-getnavigationmeshcenterposition-pathfacerecord-face.462408/))
- **Design implication:** your automatic generator should emit exactly the data the engine already consumes — faces (tri/quad) with an **ID → cost**, a **level → visibility mask**, and spawn-point coverage guaranteed. Treat Bannerlord's `ID` as your Detour *area*, and its civilian/siege layers as your *level*. Keep the generator's internal tile/layer data until the final export, then flatten to the constrained prefab.

---

## 9. If you can only sample surfaces via raycasts

Use this section when you cannot rasterize the source triangles directly (no mesh access, only physics queries — e.g. only `Physics.Raycast*` against colliders). The goal is to reconstruct enough of a **span/heightfield** that the same downstream algorithms (filters, erosion, regions, contours, poly mesh) still apply. It can be accurate, but it is more effort and more fragile than true voxelization; exhaust the "can we get the collider triangles?" path first.

### 9.1 Grid and multi-hit downward rays

1. Choose `cs` from the required accuracy: **at least 2–3 cells per agent radius** (Unity's default is 3 voxels per radius; the whole agent width is 6 voxels) ([Unity](https://docs.unity3d.com/2020.2/Documentation/Manual/nav-AdvancedSettings.html)). Snapping the origin grid to `bmin` keeps results stable.
2. For each column `(x, z)`, cast a ray from well above the scene's highest surface straight down, and collect **all** hits, not just the first: `Physics.RaycastAll(origin, Vector3.down, maxDistance, mask)` (or `RaycastNonAlloc` with a pooled buffer for GC-free operation), then **sort by hit distance / height**.
3. Disambiguate hits: reject backfaces by checking the hit normal, reject layers that are not navigable, and add a small epsilon below each hit when continuing so the same triangle is not re-hit.
4. Optionally supersample: 2×2 or 3×3 sub-rays per cell (or jitter) to reduce aliasing of thin geometry; more samples per cell linearly increase cost.

### 9.2 Banding hits into levels/layers

Sort hits by height and **band** them:

- Merge hits whose height difference is ≤ a band tolerance (e.g. `max(walkableClimb, 2*ch)`); each remaining group is one **surface sample** with a representative height (mean or lowest) and an area id from the hit's material/layer.
- Assign each band a **level index** (0, 1, 2, … from the bottom up). This is your per-column span stack; a bridge over ground yields two bands, a two-storey building yields two, etc.
- A big gap between bands (≫ `walkableClimb`) means the surfaces are **not connected** — record a ledge, not a link.

### 9.3 Quantize to spans and compute clearance

- Quantize each band height to the `ch` grid: `spanTop = floor((y - bminY) / ch)`. Create spans per column.
- **Clearance:** for each span, `h = (next span above's smin) - this span's y`. If `h < walkableHeight`, the floor is under an overhang/ceiling and is **not walkable** (the raycast analogue of `rcFilterWalkableLowHeightSpans`). If there is no span above, clearance is effectively infinite.
- **Connectivity:** two adjacent columns' spans connect when `|Δy| ≤ walkableClimb`, mirroring `rcBuildCompactHeightfield`. Larger differences are ledges. Run a ledge filter to drop cliff-top edges.
- **Low-hanging obstacles:** if a non-walkable hit sits within `walkableClimb` above a walkable band, treat it as a step (the analogue of `rcFilterLowHangingWalkableObstacles`).

### 9.4 Erosion via morphological operations

Erosion by the agent radius becomes a **binary morphological erosion** of the walkable mask by a disk of radius `walkableRadius` cells:

- Fast implementation: a two-pass **chamfer distance transform** over the walkable mask (the same idea as `rcBuildDistanceField`), then mark cells with `distance < walkableRadius` as non-walkable. This is effectively identical to `rcErodeWalkableArea`.
- Multi-level: erode each level's mask independently, but only against *same-level* neighbors (a bridge deck must not be eroded by the ground far below). Vertical obstructions that pierce a level (a pillar through a floor) should erode that level where the pillar's footprint is non-walkable in the column.
- Optional **median filter** over area ids to remove speckle (the analogue of `rcMedianFilterWalkableArea`).

### 9.5 Hole detection via unshared-edge heuristics

With no triangle source, holes are regions that "should" be walkable but were not sampled or were over-eroded:

1. **Enclosed-background test (raster):** flood-fill non-walkable cells from the outer boundary; any non-walkable cell that the flood **cannot reach** is an enclosed hole (pinhole, sampling gap, thin obstacle footprint). Either fill it, or classify it as a real interior void.
2. **Boundary-edge test (mesh):** after building a candidate mesh, boundary edges are those not shared by two walkable faces. For each boundary edge, probe the cell just outside it with an additional raycast (or check the sampled mask): if that outside cell is walkable at the same level, the boundary is a **false gap** (missing coverage) → bridge or refill; if it is a wall/ledge (height difference > `walkableClimb`, or a non-walkable hit), it is a **legitimate wall** → keep it.
3. **Closing:** apply a morphological **closing** (dilate then erode) of the walkable mask to bridge 1-cell-wide "pinch" gaps caused by raycast sparsity, then re-run erosion to restore the radius.
4. **Seeded flood-fill for connectivity:** start from known-good navigable points (spawn points, doors, path nodes) and flood-fill across the sampled graph using `|Δy| ≤ walkableClimb`; anything unreached is either genuinely disconnected (needs an off-mesh link) or a missed interior (needs more sampling seeds).

### 9.6 Turning the sampled graph into polygons

Either (a) rebuild a Recast-compatible compact heightfield from the spans and reuse the **region → contour → poly mesh → detail mesh** stages, or (b) run contour tracing directly on the per-level walkable mask (marching squares / Recast-style boundary trace), simplify with RDP at `maxSimplificationError ≈ 1.3`, then merge/triangulate into convex polys capped at 6 verts. In both cases the border/portal matching rule from `rcBuildContours` still applies: vertices where two regions meet must coincide.

### 9.7 Accuracy / robustness trade-offs vs true voxelization

| Aspect | True triangle rasterization | Raycast sampling |
|---|---|---|
| Coverage of sub-cell features | exact (conservative rasterization clips triangles to cells) | misses anything not hit; needs supersampling |
| Openings/thin walls | preserved to cell resolution | can vanish if rays straddle them; sample finer than half the narrowest feature |
| Overhangs / bridges | handled naturally by the span stack | handled **if** you use multi-hit rays (a single-hit ray stops at the deck); ignore this and you lose the lower level |
| **Interiors under a roof** | rasterization sees the roof *and* the floor beneath it | **top-down rays cannot see under a roof**; you must seed rays from inside (spawn points, doorways) or cast horizontally from navigable seeds |
| Slopes | derived from real triangle normals | derived from hit normals; jitter near edges |
| Cost | one pass over triangles | O(columns × samples × hits); more expensive at high resolution |
| Determinism | deterministic given fixed input/order | depends on physics engine, collider representation, float precision; stabilised by grid snapping and deterministic sorting, not guaranteed |
| Area/level tagging | from triangle area id / volume stamps | from hit material/layer + banding; coarser |
| Erosion | distance field on the true mask | morphological erosion on the sampled mask; sub-cell obstacles are missed |
| Missing geometry | only sees geometry that exists | a ray from above also can't see the underside of solid geometry (usually fine for navmesh) |

**Biggest practical weakness:** interiors. A purely top-down multi-ray scheme is blind under roofs. Mitigate by:
1. seeding sample columns from **spawn points, doorways, and existing navmesh/path nodes** and casting from a point known to be inside;
2. casting **horizontal rays** from those seeds to map rooms and corridors, then a downward ray at each reached cell;
3. multiplying seeds until the reachable area stops growing (a BFS over the sampled graph);
4. keeping off-mesh links across thresholds the sampler cannot resolve.

**Recommendation:** use raycast sampling as a **fallback and a validator**, not the primary path. If the engine can expose collider triangles (even as a separate physics mesh), feed those to true voxelization: it is simpler, more accurate, handles interiors and overhangs for free, and lets you reuse Recast's entire downstream pipeline.

---

### Source index

- Recast introduction & pipeline: https://recastnav.com/md_Docs_2__1__Introduction.html
- `rcConfig` parameter reference: https://recastnav.com/structrcConfig.html
- Recast module functions (`rcRasterizeTriangles`, filters, `rcErodeWalkableArea`, `rcBuildRegions*`, `rcBuildContours`, `rcBuildPolyMesh(Detail)`, area marking): https://recastnav.com/group__recast.html
- Reference build implementation: https://raw.githubusercontent.com/recastnavigation/recastnavigation/main/RecastDemo/Source/Sample_SoloMesh.cpp
- Reference defaults: https://raw.githubusercontent.com/recastnavigation/recastnavigation/main/RecastDemo/Source/Sample.cpp
- Detour module (limits, straight-path flags): https://recastnav.com/group__detour.html
- `dtNavMeshCreateParams`: https://recastnav.com/structdtNavMeshCreateParams.html
- `dtNavMesh` (tiles, layers, off-mesh endpoints, state): https://recastnav.com/classdtNavMesh.html
- `dtNavMeshQuery` (nearest poly, path, straight path, Dijkstra, raycast, node pool): https://recastnav.com/classdtNavMeshQuery.html
- `dtQueryFilter` (flags, area costs): https://recastnav.com/classdtQueryFilter.html
- `dtOffMeshConnection`: https://recastnav.com/structdtOffMeshConnection.html
- Unreal/Recast analysis (spans, erosion, watershed): https://www.unrealdoc.com/p/navigation-mesh
- Unreal static generation (tiles/layers/parallelism): https://www.unrealdoc.com/p/navigation-mesh-static-gen
- Unreal optimization docs: https://dev.epicgames.com/documentation/unreal-engine/optimizing-navigation-mesh-generation-speed-in-unreal-engine
- Unreal navmesh settings reference: https://dev.epicgames.com/documentation/en-us/unreal-engine/navigation-mesh-settings-in-the-unreal-engine-project-settings
- RecastDemo navmesh parameter walkthrough (stairs, cell height, region size): https://www.youtube.com/watch?v=II5agHlxUe4
- Unity advanced bake settings (voxels per radius, clearance, min region): https://docs.unity3d.com/2020.2/Documentation/Manual/nav-AdvancedSettings.html
- Unity NavMesh Surface (tile size, min region/tile caveat): https://docs.unity3d.com/Packages/com.unity.ai.navigation@1.1/manual/NavMeshSurface.html
- Unity areas & costs: https://docs.unity3d.com/Packages/com.unity.ai.navigation@1.1/manual/AreasAndCosts.html
- Unity off-mesh links (create): https://docs.unity3d.com/Packages/com.unity.ai.navigation@1.1/manual/CreateOffMeshLink.html
- Unity automatic off-mesh links (drop/jump, sizing): https://docs.unity3d.com/2017.1/Documentation/Manual/nav-BuildingOffMeshLinksAutomatically.html
- Unity forum: navmesh and doors diagnosis: https://discussions.unity.com/t/navmesh-and-doors/657896
- A* Pathfinding off-mesh link cost factor: https://arongranberg.com/astar/documentation/stable/offmeshlinks2.html
- Babylon.js Recast stairs tuning: https://forum.babylonjs.com/t/using-recast-nav-mesh-with-stairs/32402
- Doorway/clearance calculator: https://www.numivo.org/tools/navmesh-clearance
- Mononen heightfield layers (2.5D, `(x,y,layer)`, perf): https://digestingduck.blogspot.com/2011/03/heightfield-layer-progress-pt-2.html and https://digestingduck.blogspot.com/2011/03/heightfield-layer-progress-pt-3.html
- GPU rasterization thesis: https://www.diva-portal.org/smash/get/diva2:1104795/FULLTEXT02.pdf
- CDT navmesh (RTS, determinism): https://www.jdxdev.com/blog/2021/07/06/rts-pathfinding-2-dynamic-navmesh-with-constrained-delaunay-triangles
- CDT walkthrough: https://tchayen.com/handmade-pathfinding-mesh-for-games
- Tile-gap failure report: https://forum.arongranberg.com/t/how-to-avoid-gaps-between-tiles-in-a-recast-graph/3107
- Tile holes at nav bounds: https://forums.unrealengine.com/t/holes-in-navmesh/2762945
- Navmesh overview / large-world techniques: https://moonjump.com/game-dev-mechanics-navigation-meshes-navmesh-how-it-works
- Bannerlord official navmesh guide: https://moddocs.bannerlord.com/editor/scene-editor/nav_mesh/
- BannerlordModding.LT navmesh (civilian/siege layers): https://docs.bannerlordmodding.lt/editor/navmesh
- Bannerlord `PathFaceRecord` pathfinding error thread: https://forums.taleworlds.com/index.php?threads/map-pathfinding-error-sandbox-mapscene-getnavigationmeshcenterposition-pathfacerecord-face.462408/
