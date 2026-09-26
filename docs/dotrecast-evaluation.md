# DotRecast evaluation — tiled voxel core for navmesh v2

Research only; no code changed. Source inspected: `github.com/ikpil/DotRecast`, shallow clone at the
pinned commit below. Every API claim is cited `path:line` against that clone. Where a name was not found
it is called out as **not found** rather than guessed.

---

## Verdict

**Vendor (selected source files), do not use the NuGet package or a prebuilt DLL.**

- The Recast stage set we need is present and matches the plan §2 architecture exactly
  (rasterize → filters → compact → erode → partition (watershed/monotone/layers) → contours →
  polymesh → detail polymesh), with a genuine per-tile + border API (`RcBuilderConfig`, `RcBuilder.BuildTile`).
- License is permissive (ZLib) — safe to vendor and modify.
- **Blocker for the binary/NuGet route:** DotRecast targets `netstandard2.1;net8.0;net9.0;net10.0`
  (`src/DotRecast.Recast/DotRecast.Recast.csproj`), it is not built for `net472` and .NET Framework 4.7.2
  cannot consume `netstandard2.1`. It also requires **C# 8** (no `LangVersion` is set; `netstandard2.1`
  defaults to C# 8) — above our pinned `LangVersion 7.3`. So the only viable route is copying the needed
  `.cs` files into our project and backporting them (details in *Vendoring mechanics*).
- The backport is mechanical but not free: `Span<T>`/`stackalloc`, `MathF`, `Math.Clamp`, `Array.Fill`,
  `using var`, `[^1]`. `Span<T>` additionally drags in the `System.Memory` runtime package on net472,
  which conflicts with the "one deployed DLL" goal unless merged/rewritten.

Confidence: high on the API and license; the effort estimate for the backport is the main uncertainty.

---

## License + pinned version

| Item | Value |
|---|---|
| License | **ZLib** (README §License states it; `LICENSE.txt` is the canonical zlib text) |
| `LICENSE.txt` copyright | `Copyright (c) 2015 Mikko Mononen memon@inside.org, Piotr Piastucki, choi ikpil ikpil@naver.com` |
| Latest release tag | **2026.3.1** (GitHub release published 2026-08-02) |
| Pinned commit (repo `main` HEAD at clone) | **`5841f99e9881c05ca105079b25bb8faecf8b65a1`** (2026-09-16, "upgrade Microsoft.NET.Test.Sdk 18.10.1") |
| Provenance | port of `recastnavigation` (C++) and `recast4j` (Java) |
| Clone cmd used | `git clone --depth 1 https://github.com/ikpil/DotRecast <temp>` (outside repo) |

**ZLib obligations:** (1) do not misrepresent origin; (2) **altered source versions must be plainly marked
as such**; (3) the notice may not be removed/altered. Every `.cs` file already carries the licence header
(e.g. `src/DotRecast.Recast/RcConfig.cs:1-19`), so vendored files must keep those headers and we must add a
`NOTICE`/README recording the pinned commit and every edit (the net472/C# 7.3 backport).

---

## Verified API entry points

All types are in namespace `DotRecast.Recast` unless noted.

### Config and grid

- `class RcConfig` — `src/DotRecast.Recast/RcConfig.cs:27`. Fields (readonly), with lines:
  - `bool UseTiles` `:31`; `int TileSizeX` `:34`; `int TileSizeZ` `:36`; `int BorderSize` `:93`
  - `float Cs` `:39`; `float Ch` `:42`
  - `float WalkableSlopeAngle` `:45` (degrees)
  - `int WalkableHeight` `:49`; `int WalkableClimb` `:52`; `int WalkableRadius` `:56`
  - `int MaxEdgeLen` `:59`; `float MaxSimplificationError` `:63`
  - `int MinRegionArea` `:66`; `int MergeRegionArea` `:70`
  - `int MaxVertsPerPoly` `:74`
  - `float DetailSampleDist` `:78`; `float DetailSampleMaxError` `:82`
  - `RcAreaModification WalkableAreaMod` `:84`; `bool FilterLowHangingObstacles` `:85`;
    `bool FilterLedgeSpans` `:86`; `bool FilterWalkableLowHeightSpans` `:87`;
    `bool BuildMeshDetail` `:90`; `int Partition` `:29`
  - Derived world-value fields `:96-102` (e.g. `WalkableHeightWorld`).
- Two constructors:
  - Non-tiled `RcConfig(RcPartition partitionType, float cellSize, float cellHeight, ...)` `:107`
    (sets `BorderSize = 0`; takes `regionMinSize/regionMergeSize` as *cell counts*).
  - Tiled `RcConfig(bool useTiles, int tileSizeX, int tileSizeZ, int borderSize, RcPartition, float cellSize, float cellHeight, float agentMaxSlope, float agentHeight, float agentRadius, float agentMaxClimb, float minRegionArea, float mergeRegionArea, float edgeMaxLen, float edgeMaxError, int vertsPerPoly, float detailSampleDist, float detailSampleMaxError, bool filterLow..., bool filterLedge..., bool filterLowHeight..., RcAreaModification, bool buildMeshDetail)` `:131`
    (takes `minRegionArea/mergeRegionArea` in **world units²**).
- **Unit-derivation gotchas** (`:151-166`): `WalkableHeight = ceil(agentHeight/Ch)`,
  `WalkableClimb = floor(agentMaxClimb/Ch)`, `WalkableRadius = ceil(agentRadius/Cs)`,
  `MaxEdgeLen = (int)(edgeMaxLen/Cs)`, `MinRegionArea = round(minRegionArea/(Cs*Cs))`,
  `DetailSampleDist = (dist<0.9 ? 0 : Cs*dist)`, `DetailSampleMaxError = Ch*error`.
  **`detailSampleDist`/`detailSampleMaxError` are multipliers, not metres** — see Integration.
- `static int RcConfig.CalcBorder(float agentRadius, float cs)` `:174` → `3 + ceil(agentRadius/cs)`.
- `RcRecast.CalcGridSize(RcVec3f bmin, RcVec3f bmax, float cs, out int sizeX, out int sizeZ)`
  `src/DotRecast.Recast/RcRecast.cs:170`.
- `RcRecast.CalcTileCount(RcVec3f bmin, RcVec3f bmax, float cs, int tileSizeX, int tileSizeZ, out int tw, out int td)` `:177`.
- Constants `src/DotRecast.Recast/RcRecast.cs`: `RC_NULL_AREA = 0` `:32`, `RC_WALKABLE_AREA = 63` `:37`,
  `RC_NOT_CONNECTED = 0x3f` `:41`, `RC_MAX_LAYERS = 63` `:55`, `RC_MAX_NEIS = 16` `:56` — the plan's
  layer/neighbour limits are real and unchanged.

### Rasterize triangle soup (float verts + int indices + area ids)

- `new RcHeightfield(int width, int height, RcVec3f bmin, RcVec3f bmax, float cs, float ch, int borderSize)`
  `src/DotRecast.Recast/RcHeightfield.cs:44`.
- Area classification for slope:
  `RcRecast.MarkWalkableTriangles(RcContext ctx, float walkableSlopeAngle, float[] verts, int[] tris, int nt, RcAreaModification areaMod) -> int[]` `RcRecast.cs:191`.
- Rasterization (caller-supplied per-triangle area ids):
  - `RcRasterizations.RasterizeTriangles(RcContext context, float[] verts, int[] tris, int[] triAreaIDs, int numTris, RcHeightfield heightfield, int flagMergeThreshold)` `src/DotRecast.Recast/RcRasterizations.cs:527`
  - flat-array overload `RasterizeTriangles(context, float[] verts, int[] triAreaIDs, int numTris, heightfield, flagMergeThreshold)` `:560`
  - single `RasterizeTriangle(context, verts, v0, v1, v2, areaID, heightfield, flagMergeThreshold)` `:499`
  - `AddSpan(...)` `:115`/`:221`.
- `RcVoxelizations.BuildSolidHeightfield(RcContext ctx, IRcInputGeomProvider geomProvider, RcBuilderConfig builderCfg) -> RcHeightfield` `src/DotRecast.Recast/RcVoxelizations.cs:29`
  (the convenience path; internally calls `MarkWalkableTriangles` + `RasterizeTriangles`).

### Filters (operate on the **solid** `RcHeightfield`, before compaction)

`src/DotRecast.Recast/RcFilters.cs`:
- `FilterLowHangingWalkableObstacles(RcContext, int walkableClimb, RcHeightfield)` `:48`
- `FilterLedgeSpans(RcContext, int walkableHeight, int walkableClimb, RcHeightfield)` `:101`
- `FilterWalkableLowHeightSpans(RcContext, int walkableHeight, RcHeightfield)` `:222`

**Slope is not a filter here** — slope is applied at raster time by `MarkWalkableTriangles`
(`RcRecast.cs:191-209`, `norm.Y > cos(slope)`); `RcRecast.ClearUnwalkableTriangles(...)` `:228` is the
inverse helper. There is no separate "span filter by slope".

### Compact heightfield + erosion

- `RcCompacts.BuildCompactHeightfield(RcContext context, int walkableHeight, int walkableClimb, RcHeightfield heightfield) -> RcCompactHeightfield` `src/DotRecast.Recast/RcCompacts.cs:55`
- `RcAreas.ErodeWalkableArea(RcContext context, int erosionRadius, RcCompactHeightfield compactHeightfield)` `src/DotRecast.Recast/RcAreas.cs:46`

### Region partitioning (exact method names)

`src/DotRecast.Recast/RcRegions.cs`:
- Watershed: `BuildRegions(RcContext, RcCompactHeightfield, int minRegionArea, int mergeRegionArea)` `:1657`
  (requires `BuildDistanceField(RcContext, RcCompactHeightfield)` `:1414` first; `CalculateDistanceField(...)` `:35`).
- Monotone: `BuildRegionsMonotone(RcContext, RcCompactHeightfield, int minRegionArea, int mergeRegionArea)` `:1475`.
- Layers: `BuildLayerRegions(RcContext, RcCompactHeightfield, int minRegionArea) -> bool` `:1784`.
- Enum `RcPartition { WATERSHED=0, MONOTONE=1, LAYERS=2 }` `RcPartition.cs:3`; selector
  `RcPartitionType.WATERSHED/MONOTONE/LAYERS` `RcPartitionType.cs:7-9`.
- Heightfield **layers** (2.5D split): `RcLayers.BuildHeightfieldLayers(RcContext ctx, RcCompactHeightfield chf, int borderSize, int walkableHeight, out RcHeightfieldLayerSet lset) -> bool` `RcLayers.cs:69`;
  wrapper `RcBuilder.BuildLayers(IRcInputGeomProvider, RcBuilderConfig) -> RcHeightfieldLayerSet` `RcBuilder.cs:291`.
- `RcBuilder.Build(...)` dispatch is `cfg.Partition` → watershed / monotone / layers (`RcBuilder.cs:190-208`).

### Contours → polymesh → detail polymesh

- `RcContours.BuildContours(RcContext ctx, RcCompactHeightfield chf, float maxError, int maxEdgeLen, int buildFlags) -> RcContourSet` `src/DotRecast.Recast/RcContours.cs:718`
  (flags `RcBuildContoursFlags.RC_CONTOUR_TESS_WALL_EDGES=0x01`, `RC_CONTOUR_TESS_AREA_EDGES=0x02` `RcBuildContoursFlags.cs:7-8`).
- `RcMeshs.BuildPolyMesh(RcContext ctx, RcContourSet cset, int nvp) -> RcPolyMesh` `src/DotRecast.Recast/RcMeshs.cs:975`;
  merge `RcMeshs.MergePolyMeshes(RcContext, RcPolyMesh[], int)` `:1230`.
- `RcMeshDetails.BuildPolyMeshDetail(RcContext ctx, RcPolyMesh mesh, RcCompactHeightfield chf, float sampleDist, float sampleMaxError) -> RcPolyMeshDetail` `src/DotRecast.Recast/RcMeshDetails.cs:1321`;
  merge `RcMeshDetails.MergePolyMeshDetails(RcContext, RcPolyMeshDetail[], int)` `:1516`.
  Here `sampleDist`/`sampleMaxError` are **world units** (unlike the `RcConfig` ctor multipliers).

### Full-build orchestration

`src/DotRecast.Recast/RcBuilder.cs`:
- `List<RcBuilderResult> BuildTiles(IRcInputGeomProvider geom, RcConfig cfg, bool keepInterResults, bool buildAll, int threads = 0, TaskFactory taskFactory = null, CancellationToken cancellation = default)` `:50`
- `RcBuilderResult BuildTile(IRcInputGeomProvider geom, RcConfig cfg, RcVec3f bmin, RcVec3f bmax, int tx, int ty, RcAtomicInteger progress, int total, bool keepInterResults)` `:135`
- `RcBuilderResult Build(IRcInputGeomProvider geom, RcBuilderConfig bcfg, bool keepInterResults)` `:148`
- `RcBuilderResult Build(RcContext ctx, int tileX, int tileZ, IRcInputGeomProvider geom, RcConfig cfg, RcHeightfield solid, bool keepInterResults)` `:159` ← lets us feed a solid heightfield we built ourselves.
- `RcBuilderResult` exposes `TileX/TileZ/SolidHeightfiled/CompactHeightfield/ContourSet/Mesh/MeshDetail/Context` `RcBuilderResult.cs:7-15`.

### Geometry input contracts (we must implement these)

`src/DotRecast.Recast/Geom/IRcInputGeomProvider.cs:27-44`:
`IRcTriMesh GetMesh(); RcVec3f GetMeshBoundsMin(); RcVec3f GetMeshBoundsMax(); IEnumerable<IRcTriMesh> Meshes(); void AddConvexVolume(...); IList<RcConvexVolume> ConvexVolumes(); List<RcOffMeshConnection> GetOffMeshConnections(); void AddOffMeshConnection(...); void RemoveOffMeshConnections(Predicate<...>);`

`src/DotRecast.Recast/Geom/IRcTriMesh.cs:26-33`:
`int[] GetTris(); float[] GetVerts(); List<int[]> GetChunksOverlappingRect(RcVec2f bmin, RcVec2f bmax);`

A ready reference implementation exists at `Geom/RcPartitionedMesh.cs` (BVH chunking) and
`Geom/RcSampleInputGeomProvider.cs`. Convex volumes / off-mesh connections can be no-ops for us
(`RcBuilder` tolerates no volumes; `Build(ctx,...,geom=null,...)` is allowed, `RcBuilder.cs:280`).

`RcVec3f`/`RcVec2f` are simple structs (`src/DotRecast.Core/Numerics/RcVec3f.cs:25`, `RcVec2f.cs:6`).

---

## Tile / border support

**Yes — single bounded tile with border is supported directly**, two ways:

1. **Grid-tile API.** Set `cfg.UseTiles = true`, `cfg.TileSizeX/TileSizeZ` (cells), `cfg.BorderSize`
   (`RcConfig.cs:31-36,93`). `RcBuilderConfig(RcConfig cfg, RcVec3f bmin, RcVec3f bmax, int tileX, int tileZ)`
   (`RcBuilderConfig.cs:48`) computes the tile's world AABB as `bmin + tile*TileSize*Cs`, then **expands it by
   `BorderSize*Cs` on all X/Z sides** and sets `width = TileSizeX + 2*BorderSize`, `height = TileSizeZ + 2*BorderSize`
   (`:55-91`). The comment at `:64-89` explicitly tells the caller to feed geometry from neighbouring tiles
   inside that padded box. Border size helper: `RcConfig.CalcBorder(agentRadius, cs)` `RcConfig.cs:174`.
   `RcBuilder.BuildTile` (`RcBuilder.cs:135`) → `Build` drives the whole pipeline for that tile; contours/polys
   strip the border (`RcContours.cs:730-744,836-861`).

2. **Arbitrary rectangle (our preferred).** Build the `RcHeightfield` ourselves
   (`new RcHeightfield(w, h, bmin, bmax, cs, ch, borderSize)` `RcHeightfield.cs:44`), rasterize exactly the
   triangles we want, then call `RcBuilder.Build(ctx, tileX, tileZ, geom, cfg, solid, keepInterResults)`
   (`RcBuilder.cs:159`). This bypasses DotRecast's tile-grid arithmetic entirely and lets our adaptive
   coarse/fine pass choose per-tile `cs`/bounds. `SolidHeightfiled.borderSize` is carried into the compact
   field and stripped in `RcContours`.

**Caveats:** the grid-tile path slices tiles from one global `bmin/bmax` with **one** `cs`/`ch`
(`RcBuilderConfig.cs:55-91`); per-tile/per-region resolution is not supported by the grid API — use route 2
for that. `GetChunksOverlappingRect` is only consulted when `cfg.UseTiles` is true (`RcVoxelizations.cs:46-61`).

Output provenance: `RcPolyMesh.borderSize` `RcPolyMesh.cs:44`; `RcCompactHeightfield.borderSize`
`RcCompactHeightfield.cs:34`; `RcContourSet.borderSize` `RcContourSet.cs:51`.

---

## Output mapping to our face format

Our NMG8 writer (already implemented) emits per face: vertex indices, edge indices, `is_concave = 0`,
`level_cost_index = 0`, `cost = 0f`, `level_mask`, `inverted = 0`, and a 32-entry group table
(`TerrainNavMeshGenerator.cs:994-1023` and `:1097-1127`, with `LevelMaskAll = 0xFFFFFFFF` at `:27`).

`RcPolyMesh` (`src/DotRecast.Recast/RcPolyMesh.cs:27-45`) exposes:
- `int[] verts` — voxel integer coords, `(x,y,z)*nverts`;
- `int[] polys` — length `maxpolys*2*nvp`; first `nvp` = vertex indices (terminated by `RC_MESH_NULL_IDX`),
  second `nvp` = neighbour index or `0x8000 | dir` portal flags (`RcMeshs.cs:35-118,1180-1214`);
- `int[] regs`, `int[] areas`, `int[] flags`, `nverts`, `npolys`, `nvp`, `bmin/bmax`, `cs`, `ch`, `borderSize`.
  Voxel→world: `x = bmin.X + vx*cs`, `y = bmin.Y + vy*ch`, `z = bmin.Z + vz*cs`.

`RcPolyMeshDetail` (`RcPolyMeshDetail.cs:26-34`) exposes:
- `int[] meshes` — 4 per submesh: `(vertBase, vertCount, triBase, triCount)` (`RcMeshDetails.cs:1454-1457`);
- `float[] verts` — **world space already**: `orig = mesh.bmin` at `RcMeshDetails.cs:1334`, added to every
  vertex at `:1438-1445` (note the Y `+ chf.ch` nudge at `:1439`);
- `int[] tris` — 4 per triangle: `(v0, v1, v2, flags)` (`RcMeshDetails.cs:1504-1507`; flags are
  detail/edge flags, ignore for us); `nmeshes`, `nverts`, `ntris`.

Mapping choices:
- **Per-poly faces** (recommended for cost/level fidelity): for each `i in [0,npolys)`, emit the convex
  polygon `polys[i*nvp .. i*nvp+count]` as one NMG8 face (fan-triangulate only if the engine needs tri/quads),
  and derive `level_mask`/`cost` from `areas[i]` (or our own classification side-table). `regs[i]` is the
  region id, not the engine group id — **group id stays 0** (`level_cost_index` stays 0).
- **Detail triangles** (recommended for geometric fidelity): iterate `meshes[]` and emit each
  `tris` triangle; the vertex positions are already world-space. Area/level is per-submesh, map back to the
  owning poly via `triBase` ranges.
- In both cases `cost = 0`, `is_concave = 0`, `inverted = 0`, group table zeros; `level_mask` comes from our
  scene-declared levels (all-bits `0xFFFFFFFF` is the safe default already used).

---

## Parallelism / determinism notes

- **No mutable static state in Core/Recast.** A precise scan found only `static readonly`
  (e.g. `RcPartitionType.WATERSHED/MONOTONE/LAYERS` `RcPartitionType.cs:7-9`,
  `RcMatrix4x4f.Identity` `Numerics/RcMatrix4x4f.cs:102`) and `const`s (`RcRecast.cs:28-56`,
  `RcBuildContoursFlags.cs:7-8`). Per-build state lives in `RcContext`, created per call
  (`RcBuilder.cs:151`) and using `ThreadLocal<...>` + `ConcurrentDictionary` (`RcContext.cs:43-50`).
  **So independent `RcBuilder.Build(...)` calls on different threads are safe**, which is what we want.
- DotRecast ships its own tile parallelism: `BuildTiles(..., threads>1, ...)` (`RcBuilder.cs:50,83-133`)
  uses `TaskFactory.StartNew` into a `ConcurrentQueue` and busy-waits with `Thread.Sleep(1)`.
  **Its result list is in completion order, not tile order**, and per-tile exceptions are swallowed to
  `Console.WriteLine` (`RcBuilder.cs:109-112`) — a failed tile silently disappears. If we use it we must
  sort by `(TileX,TileZ)` and treat expected tile count as an invariant. **Recommendation: call
  `Build`/`BuildTile` ourselves per tile on our own scheduler and merge in sorted order** — this is exactly
  the plan's deterministic-merge guardrail and avoids the swallow/ordering issues.
- Within a single build everything is single-threaded and deterministic given identical inputs, bounds, and
  `cs/ch` (standard Recast; no thread-order-dependent reductions inside a build).
- Logging: `RcContext.Warn/Error/Log` write to `Console` (`RcContext.cs:70-84`) — noisy in-editor;
  we should route through a quiet context or accept the output.
- Hard limits to respect: `RC_MAX_LAYERS = 63` ("layer overflow") and `RC_MAX_NEIS = 16` (`RcRecast.cs:55-56`),
  matching the plan's §1.5 diagnosis.

---

## Integration sketch

`TiledNavMeshBuilder` would:

1. **Global grid (fixed, power-of-two-friendly):** world `bmin/bmax` snapped to a fixed origin; `cs`, `ch`
   from settings; tile world size = the **16 m coarsest spacing** → `TileSizeX/Z = round(16.0/cs)`;
   `borderSize = RcConfig.CalcBorder(agentRadius, cs)` (`RcConfig.cs:174`), so erosion/distance see neighbours.
2. **Input adapter:** implement `IRcInputGeomProvider` + `IRcTriMesh` (`Geom/IRcInputGeomProvider.cs:27`,
   `Geom/IRcTriMesh.cs:26`). `GetVerts()` = our terrain+collision vertex array; `GetTris()` = `int[3*ntris]`;
   `GetChunksOverlappingRect` = our BVH query (model on `Geom/RcPartitionedMesh.cs`); convex-volume and
   off-mesh methods no-op. **Apply our `+0.5 m` height offset here** (bake it into the vertex Y we hand over).
3. **Per tile (our scheduler, N bounded workers):**
   - build `RcHeightfield` for the tile + border explicitly (`RcHeightfield.cs:44`);
   - classify area ids ourselves (walkable vs blocker/level) and call
     `RcRasterizations.RasterizeTriangles(ctx, verts, tris, triAreaIDs, ntris, hf, walkableClimb)`
     (`RcRasterizations.cs:527`); slope gating can reuse `RcRecast.MarkWalkableTriangles` (`RcRecast.cs:191`)
     or our classifier;
   - `RcBuilder.Build(ctx, tx, tz, geomOrNull, cfg, solid, keepInterResults:false)` (`RcBuilder.cs:159`).
4. **Settings honored (`RcConfig`, tiled ctor `RcConfig.cs:131`; prefer calling `Build` directly and passing a
   config we constructed):**
   - **65° walkable slope** → `WalkableSlopeAngle = 65f`.
   - **0.25 m max error** → detail tolerance. Because the ctor treats `detailSampleMaxError` as a *multiple of
     `Ch`* (`RcConfig.cs:80-82,166`), either pass `0.25f/Ch`, or (cleaner) call
     `RcMeshDetails.BuildPolyMeshDetail(ctx, mesh, chf, sampleDist, 0.25f)` directly (`RcMeshDetails.cs:1321`).
     `MaxSimplificationError` (contour simplification, in voxels) is separate — keep the plan default ~1.3.
   - **agent height/radius/climb**, `MinRegionArea/MergeRegionArea`, `MaxVertsPerPoly` (≤6 for Detour, but we
     are not Detour — we may keep 6 for quad-friendly output), `MaxEdgeLen`.
   - **partition = MONOTONE or LAYERS** (`RcRegions.BuildRegionsMonotone` `:1475` / `BuildLayerRegions` `:1784`),
     not watershed (spiral-stair hole/overlap corner case, plan §1.5).
5. **Export:** read `RcBuilderResult.Mesh` (and `MeshDetail`) → map to NMG8 faces as in the previous section;
   reuse the existing writer (`TerrainNavMeshGenerator.cs:960/1068`).

---

## What we must build ourselves

DotRecast does **not** provide these; they remain on us (consistent with plan §2):

- **Tile dirty tracking + incremental rebuild** (per-tile geometry hash; no such concept in DotRecast).
- **Deterministic merge** across tiles (sort `RcBuilderResult` by `(TileX,TileZ)`; weld/seam handling at
  borders; DotRecast merges nothing for us).
- **Face export** to Bannerlord NMG8 (group 0, cost 0, `is_concave=0`, `inverted=0`, level masks) — existing
  writer, extended to consume `RcPolyMesh`/`RcPolyMeshDetail`.
- **≥ 1.5 m stacked-face separation rule** (`Scene.NavMeshHeightLimit`) — post-filter; not a Recast concept.
- **Level masks from scene-declared levels** — we read the scene and set `level_mask`; Recast only carries a
  per-span `area` id (0–63).
- **Adaptive coarse→fine mask** (plan §2 [1]-[2]) — DotRecast has one `cs`/`ch` per build; implement by
  choosing per-tile configs and route 2.
- **Global island union-find / connectivity stitching** across tiles and levels.

---

## Comparison with other maintained C# Recast ports

- **`Robmaister/SharpNav`** (MIT, ~556★): a *from-scratch* Recast-equivalent ("functionally equivalent to
  Recast", per README), last commit **2022-06-22**, targeting **.NET Framework 4.5.1** via an old-style csproj
  with `Newtonsoft.Json` + `YamlDotNet` references. Very attractive on paper (net451 is loadable from net472,
  MIT, has `TiledNavMesh`/`NavMeshBuilder`/`PolyMesh`/`PolyMeshDetail`), but it is stale, its public API does
  **not** match Recast names (so the plan's "run the real stages" benefit is lost), it drags extra DLLs
  (breaking one-DLL unless stripped/merged), and it predates the modern tiled/heighfield-layer work. Not
  recommended over DotRecast.
- **`ryancheung/RecastSharp`** (dotnet 6 port of `recastnavigation`): last push **2023-07-19**, ~69★, README
  literally says **"NOTE THIS LIB IS NOT TESTED"**, no LICENSE file found at repo root, Detour uses unmanaged
  memory/pointers. Effectively abandoned and legally unclear → **reject**.
- (`recast4j` is Java and `recast-navigation-js` is JS/WASM; both out of scope for an in-process net472 DLL.)

**Bottom line:** DotRecast is the only actively-maintained (release 2026-08-02), license-clear, name-faithful
C# Recast port; its only real cost is the net472/C# 7.3 backport.

**In-house fallback scope** (if the backport is rejected): re-implement the stages we need — triangle
rasterization/spans + pools (~500 LOC), compact heightfield + neighbour connectivity (~400), three filters
(~300), erosion + distance field (~300), region partition (monotone ~400, watershed ~600 extra), contour
trace + RDP simplify + hole merge (~700), convex polymesh build/merge (~900), detail polymesh + height patch
(~800), grid/config/adapter/exporter (~400). Roughly **4.5k–6k LOC plus tests, 3–6 engineer-weeks** to reach
parity, with watershed/detail correctness being the risky parts. That is the true cost of *not* vendoring.

---

## Risks

1. **TFM mismatch is a hard blocker for NuGet/DLL:** no `net472`/`netstandard2.0` asset; net472 cannot load
   `netstandard2.1`. Must vendor source.
2. **C# 8 syntax:** `using var` ×29, index-from-end `[^1]` ×2 (`RcRegions.cs:298,961`) — above our
   `LangVersion 7.3`. Mechanical rewrites (introduce blocks; `stack[stack.Count-1]`).
3. **net472 runtime gaps:** `MathF` ×58, `Math.Clamp` ×18, `Array.Fill` ×14 (Framework lacks all three) need
   shims/rewrites; `Span<T>`/`stackalloc` (92 hits) need the **`System.Memory` NuGet** on net472, i.e. extra
   assemblies (`System.Memory.dll`, `System.Buffers.dll`, `System.Numerics.Vectors.dll`,
   `System.Runtime.CompilerServices.Unsafe.dll`) unless rewritten to arrays or IL-merged into our single DLL.
   `System.Runtime.CompilerServices.Unsafe` is referenced by upstream even for netstandard2.1
   (`DotRecast.Core.csproj`), 26 `Unsafe.*` uses.
4. **`RcConfig` detail-unit trap:** ctor params are multipliers, `BuildPolyMeshDetail` params are metres —
   easy to set 0.25 m wrong. Mitigate by calling `BuildPolyMeshDetail` directly.
5. **Built-in `BuildTiles(threads>1)`** returns completion-ordered results and swallows tile exceptions —
   bypass it for deterministic merges.
6. **Hard Recast limits** remain: `RC_MAX_LAYERS=63`, `RC_MAX_NEIS=16`; dense stacks still fragment/overflow.
7. **Drift/maintenance:** vendored source must be manually re-synced; record the pinned commit.
8. **License hygiene:** keep every file header; mark altered files; ship a NOTICE with the pinned commit and
   modification list.
9. **Not yet validated by us:** DotRecast is itself a port (C++→Java→C#); output parity on Bannerlord
   collision geometry must be checked against the Phase 0 terrain output before adoption (plan Phase 2 gate).

---

## Recommendation on vendoring mechanics

**Vendor a source subset** into e.g. `ThirdParty/DotRecast/`, compiled into our single assembly.

- **Why not NuGet:** no compatible asset (`netstandard2.1`+ only) → cannot restore/reference from `net472`.
- **Why not a prebuilt/retargeted DLL:** even a net472 rebuild of DotRecast produces `DotRecast.Core.dll`
  **and** `DotRecast.Recast.dll` (plus `System.Memory`, `System.Buffers`, `System.Numerics.Vectors`,
  `System.Runtime.CompilerServices.Unsafe` for `Span`) → multiple deployed files, breaking the
  "one self-contained DLL in the module folder" contract (`build.ps1:37-44`, `csproj:66-73`). ILRepack could
  fuse them, but that adds a build step and obfuscates provenance/attribution.
- **Recommended shape:**
  1. Copy only the needed files: all of `src/DotRecast.Recast/**` (minus the Demo/Toolset) and the Core files
     it transitively needs (`Numerics/RcVec2f`, `Numerics/RcVec3f`, `Numerics/RcVec`, `RcContext`,
     `RcAtomicInteger`, `RcAtomicLong`, `RcArrays`, `RcSpans`, `RcMath`, `RcFrequency`, `RcDebug`,
     `RcTimerLabel`, `RcLogCategory`, `RcScopedTimer`, `RcTelemetryTick`, `RcHashCodes`, etc. — start from the
     compiler errors and prune unused Core buffers/IO/compression/OBJ importer).
  2. Keep the license header on **every** file verbatim; add `ThirdParty/DotRecast/NOTICE.md` with
     repo URL, pinned commit `5841f99e9881c05ca105079b25bb8faecf8b65a1`, tag `2026.3.1`, and the exact list of
     alterations (net472/C# 7.3 backport) — satisfies ZLib clauses 2 and 3.
  3. Do the backport at the source level (rewrite `using var`, `[^1]`, `MathF`, `Math.Clamp`, `Array.Fill`) so
     the vendored code compiles under `LangVersion 7.3`. For `Span`/`stackalloc`, prefer **rewriting to arrays**
     in the handful of vendored hot paths to avoid `System.Memory` entirely and keep the single-DLL deploy;
     if that proves too invasive, add `System.Memory` and **ILRepack/ILMerge** everything into
     `BetterBannerlordNavmeshing.dll` (still one deployed file; note the merged licences in NOTICE).
  4. Keep vendored files in their own folder excluded from our style rules; add a small `README` note that they
     must never be hand-edited beyond the recorded backport.

This preserves our TFM, our single-DLL deploy, our licence obligations, and gives us the real Recast stages
rather than the 4.5k–6k LOC in-house fallback.
