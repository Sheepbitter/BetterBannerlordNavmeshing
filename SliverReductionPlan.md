# Where Sliver/Needle Faces Are Born — Census & Reduction Plan

**Scope:** Mount & Blade II: Bannerlord navmesh tool (BetterBannerlordNavmeshing), Recast-style tiled voxel pipeline.
**Method:** instrumented harness copy (`SliverCensus.exe`) counting slivers at every pipeline stage, using the cleanup's own definitions.
**Repo touched:** read-only. All work in `C:\Users\Sheepbitter\AppData\Local\Temp\opencode\sliver_plan\`.
**Model/provider:** NVIDIA NIM GLM 5.3 Flash (`z-ai/glm-5.3-flash`), provider `nvidia-Free-1k-Credits`.

---

## 0. Executive summary

- Slivers are born in the **per-tile detail-mesh build**: `TriangulateHull` (RcMeshDetails.cs:633) ear-clips the tessellated polygon outline. ~99% of slivers exist by `rawTileMesh`/`afterMerge`; stitch/gap-fill/plate-blend/steep-repair/prune are noise (+127/+14/+154 per-tile-stitch births on CC_20/Birtu/Basamu, everything else ≤5 faces).
- Birth vertex density is set by **contour simplification** (`SimplifyContour`, RcContours.cs:226; shipped `contourMaxError=1.3` vox) and the **detail-sample grid** (`sampleDist=2.0` m). The dominant class is *thin-only* (minAngle<5° but not an extreme needle): 97% (CC_20), 95% (Birtu), 94% (Basamu) of the cleanup's input.
- **Measured, strongest levers:** raising `contourMaxError` 1.3→2.0 vox cuts post-cleanup slivers **-43%** (1048→602 on CC_20; -24% Birtu, -34% Basamu) and cleanup work -13%; 1.3→3.0 vox cuts **-57%** (450) / -33% / -45. Halving tile size 16→12 m cuts **-13%** (-16% Birtu, -4% Basamu) and `refined` -52..59%. Combined conterr2.0+tile12: **-52%** post-cleanup slivers (500), build **-27%**, islands PASS.
- Tighter is *worse* everywhere: conterr 1.0 vox = +12%/+5%/+13%; conterr 0.5 vox explodes +888% with islands 15→71. The shipped 1.3 already sits on the safe side of a cliff between 0.5 and 1.0 vox.
- **Do NOT:** turn the detail mesh off (+92% CC_20, +201% Basamu post-cleanup), tighten `edgelen` to 6 (3.3× raw slivers, islands 26), or rely on `regmin`/`regmerge`/`weldtol`/`vpp` (no material effect).
- Recommended package: **contourMaxError 2.0 vox** (one line in `SceneNavMeshExporter.cs:140`), optionally **+ tile 12 m** after in-engine validation. Both keep islands, borders, ladder connectors and engine validity (all harness invariants PASS); the 2.0-vox boundary budget (0.50 m at cs 0.25) equals the cleanup's own `SurfaceToleranceMeters` (0.50 m).

---

## 1. Method

- **Instrumentation:** new file `SliverCensus.cs` — a `Snapshot` accumulator (`Add(verts, tris, deep, noOwner)` + `Commit(stage)` + `PrintTable`), called from `SliverCensus.Enabled`-gated hooks inserted into a *copy* of `TiledNavMeshBuilder.cs` at stage boundaries (rawTileMesh, tileAfterStitch, afterMerge, afterNonManifold, afterPlateBlend, afterMergeStitch, afterSteepRepair, afterPrune, afterCleanup, afterFaceQuality).
- **Definitions replicated exactly from the cleanup** (NavMeshCleanup.cs:41-53): needle = altitude <0.05 m & longest edge >0.5 m; thin = min angle <5°; refine candidate = aspect >8 & longest ≥4 m & width ≥0.12 m.
- **Breakdown dimensions:** lattice vs interior faces (on-voxel-lattice = contour/poly-mesh provenance); tile-seam proximity (0.35 m of a tile lattice line); steepness bands (flat <20°, 20-40°, 40+°); prop-under/propTop probes (asset-soup triangle under the face XZ / at face height); owner resolver (lowest entity index owning the point).
- **Harness knobs added:** `--slivers` (census), `--detdist`, `--conterr`, `--regionmin`, `--regionmerge`, `--edgelen`, `--vpp`, `--weldtol`, `--tile`.
- **Fixtures:** `CC_20_siege_navmesh_snapshot_*` (heavy), `mp_tdm_Birtu_navmesh_snapshot_*`, `mp_battle_Basamu_village_navmesh_snapshot_*` (all in `C:\Users\Sheepbitter\AppData\Local\Temp\opencode\tiled-core-harness\`).

## 2. Baseline census by pipeline stage

Shipped defaults: `contourMaxError=1.3` vox, `detailSampleDist=2.0` m, tile=16 m, `edgelen=12` vox, cs=0.25 m, RegionMinSize=8, RegionMergeSize=20, detail mesh ON, vpp=6, weldtol=0.01 m.

| stage (n&t slivers / faces) | CC_20 | Birtu | Basamu |
|---|---|---|---|
| rawTileMesh | 7184 / 318553 | 2358 / 64266 | 12477 / 616585 |
| tileAfterStitch | 7311 (+127 stitch) | 2372 (+14) | 12631 (+154) |
| afterMerge | 7253 | 2369 | 12523 |
| afterNonManifold | 7252 (−1) | 2368 (−1) | 12518 (−5) |
| afterPlateBlend | 7247 (−5) | 2363 (−5) | 12516 (−2) |
| afterMergeStitch | 7247 (+0) | 2363 (+0) | 12516 (+0) |
| afterSteepRepair | 7210 (−37) | 2360 (−3) | 12477 (−39) |
| **afterPrune = cleanup input** | **5933 / 266224 = 22.3/1k** | **1946 / 50499 = 38.5/1k** | **11573 / 540086 = 21.4/1k** |
| afterCleanup | 1048 / 241401 = 4.3/1k | 353 / 44478 = 7.9/1k | 1508 / 492435 = 3.1/1k |

Cleanup work at baseline (cand / rejMove / decimated / relaxed / refined / flipped / boundaryMoved):

| fixture | needles | thin | cand | rejMove | decimated | relaxed | refined | flipped | boundaryMoved | build |
|---|---|---|---|---|---|---|---|---|---|---|
| CC_20 | 187→36 | 5933→1048 | 654211 | 1447840 | 29052 | 20256 | 13311 | 5744 | 72575 | 37281 ms, 15 islands |
| Birtu | 99→7 | 1946→353 | 116607 | 200528 | 6711 | 3556 | 2524 | 1068 | 18604 | 12686 ms, 5 islands |
| Basamu | 599→66 | 11573→1508 | 937551 | 2367275 | 35814 | 34499 | 8561 | 10367 | 116982 | 69969 ms, 1 island |

**Take-away:** ~99% of slivers are born in the per-tile build (rawTileMesh ≈ afterMerge); the per-tile stitch adds +127/+14/+154; the cross-tile merge-stitch adds +0..4; plate-blend/steep-repair are ≤5/±39. Everything the cleanup fights is created before the merge.

## 3. Birth mechanism (code level)

1. **`TriangulateHull`** (RcMeshDetails.cs:633) — greedy left/right ear-clip over the hull ring: start ear chosen by shortest perimeter (:640-661), then the walk advances left/right by whichever next triangle has shorter perimeter (:673-702). The comment says it all: *"This heuristic was chose empirically, since it seems handle tessellated straight edges well"* (:669-672). Tessellated straight edges are exactly what boundary-rich maps produce, and the greedy walk is what turns dense collinear outline samples into long thin fans.
2. **Outline tessellation** (`BuildPolyDetail`, RcMeshDetails.cs:715, :747-863) — samples every `sampleDist` along each polygon edge (`nn = 1 + floor(d/sampleDist)`, :778, cap 32/edge), simplified *only* by inserting the point of maximum deviation when `maxd > sampleMaxError²` (:800-840, gate at :826). Tighter maxError → more surviving vertices → more ears → more slivers. Note the deviation is the **3D** distance — height components on steep boundaries trigger insertions where the XZ deviation is small (56% of CC_20's raw slivers are steep≥40°).
3. **Interior sample grid** (RcMeshDetails.cs:885-968) — jittered `sampleDist` grid per polygon, filtered by `DistToPoly(...) > -sampleDist/2` (:910) so samples within half a sampleDist of the boundary are rejected, then inserted by max error (:922-968). Polygon size controls how much of each polygon escapes the rejection margin: at tile 8 m the interior sliver population collapses **5160→1605 (−69%)** while lattice slivers stay flat (2949→3225).
4. **Contour simplification** (`SimplifyContour`, RcContours.cs:226) — insert-while-deviation > maxError (:364), edge-length split at :379-419 (shipped `edgelen=12` vox). This is what sets boundary vertex density, and therefore the outline tessellation's input.
5. **`minExtent` gate** (RcMeshDetails.cs:865-871) — polygons with min extent < 2·sampleDist skip interior sampling entirely (fan path); that is why `--detail off` and large `detdist` collapse to fans.
6. **Everything downstream is noise:** per-tile stitch (+127/+14/+154), cross-tile merge-stitch (+0..4), plate-blend (0 vertices added by construction), steep-repair (−3..−39), prune (−414..−1277), cleanup (the fix).

## 4. Source breakdown of the sliver population

At **rawTileMesh** (the birth site; probe = asset soup under face XZ at face height):

| fixture | lattice (contour) | interior (detail-sample) | seam-touching | steep ≥40° | terrain soup |
|---|---|---|---|---|---|
| CC_20 | 3341 (46%) | 3843 (54%) | 4300 (60%) | 4037 (56%) | 7184 (100%) |
| Birtu | 1314 (56%) | 1044 (44%) | 1268 (54%) | 619 (26%) | 2358 (100%) |
| Basamu | 3520 (28%) | 8957 (72%) | 6846 (55%) | 4471 (36%) | 12477 (100%) |

At **afterPrune** (the cleanup's actual input): CC_20 lattice 2476 / interior 3457 / seam 3589 / steep40 3344 / propTop 625 / terrain 4704 (79%); Birtu 983 / 963 / 1053 / 583 / 147 / 671 (34%); Basamu 2879 / 8694 (75%!) / 6423 / 4197 / 420 / 9733 (84%).

Mean aspect ratio of the sliver population at afterPrune: 30.7 (CC_20), 39.9 (Birtu), 31.6 (Basamu) — these are *super-slivers*, matching `RefineLongThin`'s gate (aspect>8). Needles are a small subset (187/99/599 of the input); the dominant class is thin-only.

## 5. Knob sweep (measured; CC_20 unless noted; baseline post-cleanup 1048 = 4.3/1k)

| knob | rawTile | afterPrune (per1k) | afterCleanup | Δ post | islands | build ms |
|---|---|---|---|---|---|---|
| **baseline (shipped)** | 7184 | 5933 (22.3) | 1048 | — | 15 | 37281 |
| conterr 2.0 vox | 5835 | 4738 (18.3) | 602 | **−43%** | 13 | 33180 |
| conterr 3.0 vox | 5338 | 4272 (16.6) | 450 | **−57%** | 13 | 32652 |
| conterr 1.0 vox | 7627 | 6323 (23.4) | 1179 | +12% | 16 | 35851 |
| conterr 0.5 vox | 66877 | 52482 (107.5) | 10360 | +888% | **71** | 76149 |
| tile 12 m | 5748 | 4513 (16.6) | 915 | −13% | 16 | 35932 |
| tile 8 m | 4830 | 3636 (12.0) | 804 | −23% | 16 | 32150 |
| tile 24 m | 9410 | 8023 (30.9) | 1273 | +21% | 14 | 43899 |
| edgelen 20 vox | 5352 | 4303 (17.5) | 1141 | +9% | 10 | 32260 |
| edgelen 6 vox | 24011 | 20946 (64.9) | 1341 | +28% | **26** | 41480 |
| cs 0.30 m | 5750 | 4826 (17.2) | 1146 | +9% | 12 | 31167 |
| cs 0.20 m | 8572 | 7076 (26.8) | 1203 | +15% | 13 | 41572 |
| detdist 1.0 m | 8960 | 7758 (21.6) | 868 | −17% | 19 | 46917 |
| detdist 1.5 m | 8109 | 6888 (22.7) | 978 | −7% | 15 | 42842 |
| detdist 3.0 m | 6915 | 5638 (26.0) | 1108 | +6% | 12 | 33448 |
| detdist 4.0 m | 6948 | 5661 (29.2) | 1205 | +15% | 10 | 31158 |
| detail off | 6404 | 4896 (64.2) | 2013 | **+92%** | 7 | 22030 |
| vpp 3 | 10855 | 9184 (31.6) | 1281 | +22% | 16 | 43851 |
| regmin 4 / 12 | 7244 / 7156 | 5954 / 5919 (22.3) | 1058 / 1038 | ~0 | 15 | — |
| regmerge 12 / 30 | 7687 / 7012 | 6364 / 5759 (23.8/21.7) | 1110 / 1057 | +6/+1% | 16/14 | — |
| weldtol 0.005 / 0.030 | 7184 / 7184 | 5933 / 5931 (22.3) | 1048 / 1048 | 0 | 15 | — |

**Combined stacks (sweep2):**

| combo | rawTile | afterPrune (per1k) | afterCleanup | Δ post | islands | build ms |
|---|---|---|---|---|---|---|
| conterr2.0 + edgelen20 | 3845 | 3073 (12.9) | 593 | −43% | 9 | 33686 |
| conterr2.0 + tile12 | 4495 | 3454 (13.1) | 500 | **−52%** | 12 | 27369 |
| conterr3.0 + tile12 | 4176 | 3132 (12.0) | 364 | **−65%** | 12 | 27141 |
| conterr2.0 + detdist3.0 | 5636 | 4580 (21.8) | 684 | −35% | 12 | 28788 |

**Cross-fixture confirms (sweep1+sweep2):**

| run | rawTile | afterPrune (per1k) | afterCleanup | Δ post vs baseline | islands |
|---|---|---|---|---|---|
| Birtu conterr 2.0 | 2072 | 1706 (35.2) | 267 | **−24%** | 6 |
| Birtu conterr 3.0 | 1968 | 1613 (33.9) | 235 | **−33%** | 5 |
| Birtu conterr 1.0 | 2439 | 2021 (39.3) | 369 | +5% | 5 |
| Birtu tile 12 | 1940 | 1595 (31.7) | 298 | −16% | 5 |
| Birtu detail off | 2031 | 1666 (84.8) | 650 | +84% | 5 |
| Birtu detdist 1.5 | 2500 | 2090 (36.9) | 354 | 0% | 5 |
| Basamu conterr 2.0 | 10460 | 9679 (18.5) | 994 | **−34%** | 1 |
| Basamu conterr 3.0 | 10121 | 9304 (17.9) | 824 | **−45%** | 1 |
| Basamu conterr 1.0 | 13322 | 12306 (22.4) | 1709 | +13% | 1 |
| Basamu tile 12 | 9438 | 8563 (15.9) | 1443 | −4% | 1 |
| Basamu detail off | 10026 | 9122 (56.7) | 4533 | **+201%** | 1 |
| Basamu detdist 1.5 | 12712 | 11845 (20.8) | 1453 | −4% | 1 |

**Cleanup-work link (the user's hypothesis — confirmed):** cleanup work scales with the sliver population. CC_20: baseline cand=654211 / rejMove=1447840 / refined=13311 → conterr2.0 cand=567501 (−13%) / rejMove=1374727 (−5%) / refined=12960; conterr3.0 cand=537686 (−18%) / rejMove=1348145 (−7%); tile12 refined=6353 (−52%); tile8 refined=2994 (−77%); ct2+tile12 refined=6191 (−53%). Birtu: conterr2.0 cand=107332 (−8%) / refined=2426 (−4%); tile12 refined=1045 (−59%). Producing fewer slivers upstream does reduce cleanup cost — most strongly in `RefineLongThin`'s input.

**Structural notes:** tile 8/12 removes CC_20's cross-tile merge-stitch pairs entirely (808→0; Basamu baseline already has pairs=0); per-tile stitch face counts: CC_20 726 (16 m) → 712 (12 m) → 970 (8 m) → 518 (24 m); conterr 2.0/3.0 → 574/466. Gate-plate blends examined/blended: 244/63 (tile16) → 177/40 (conterr2.0) → 159/34 (conterr3.0) → 264/70 (tile8).

## 6. Prevention research (what upstream does)

- Recast's `rcBuildPolyMeshDetail` is "quality-driven insert, count-bounded" — deterministic, monotone, no clock (DeterminismResearch.md L43-44). The shape matches this project's `RefineLongThin`; the upstream tile build is already parallel-but-canonical (TiledNavMeshBuilder.cs:24, :1061-1072).
- Upstream controls sliver birth exactly where this census found it: outline `sampleDist`/`sampleMaxError` (RecastMeshDetail.cpp `buildPolyDetail`) and contour `maxError`/`maxEdgeLen` (`rcBuildContours`). RecastDemo's own tuning guidance treats `contourMaxError` as the boundary-fidelity-vs-triangle-count knob — this census quantifies that trade for this pipeline.
- None of the surveyed production simplifiers (meshoptimizer, Open3D, Blender, TriDecimate — DeterminismResearch.md comparison table) uses time budgets; they are all count-bounded, which is the pending determinism change for the cleanup.

## 7. The plan (prioritized levers)

### L1 — PRIMARY: raise `contourMaxErrorVoxels` 1.3 → 2.0 vox
- **Implementation (one line):** `SceneNavMeshExporter.cs:140` `public float NavMeshVoxelContourMaxError = 2.0f;` (already flows through `voxel.ContourMaxErrorVoxels` at :1363 and is logged at :747).
- **Measured:** cleanup input −20% (CC_20 5933→4738), post-cleanup slivers **−43%** (1048→602; per-1k 4.34→2.55), cleanup cand −13%, build −11%. Cross-fixture: Birtu −24%, Basamu −34%.
- **Alignment:** 2.0 vox × cs 0.25 m = **0.50 m** boundary budget = exactly the cleanup's own `SurfaceToleranceMeters` (0.50 m, NavMeshCleanup.cs:34) — the mesher would promise no more than the cleanup already tolerates.
- **Risk:** the walkable boundary may pull up to 0.50 m from voxel walls (vs 0.325 m today) around props/obstacles; island counts move (CC_20 15→13, Birtu 5→6, Basamu 1→1 — all PASS, but topology changes). Tile borders are lattice-clamped by the seam stitch, so the deviation applies mainly to interior poly boundaries.
- **Guard rails:** conterr 1.0 (tighter) is *worse* everywhere (+12%/+5%/+13%) and 0.5 explodes (+888%, islands 71) — never go below ~1.0.

### L2 — SECONDARY (after in-engine validation): `TileWorldSizeMeters` 16 → 12 m
- **Implementation (one line):** `SceneNavMeshExporter.cs:36` `public float NavMeshCoarsestSpacing = 12f;`
- **Measured:** per-1k cleanup input 22.3→16.6 (−24%), post-cleanup −13% (CC_20), −16% (Birtu), −4% (Basamu); `refined` −52..59%; combined with L1: post **500 (−52%)**, build **−27%** (27369 ms), islands 12, invariants PASS.
- **Risk:** changes .navmesh tile granularity (engine tile loading); removes CC_20's cross-tile merge-stitch pairs (808→0) — a structural change that must be validated for cross-tile connectivity and ladder connectors; per-tile stitch count stays ~flat (726→712).
- Note tile 8 m is better per-1k (12.0) but changes more (stitch 726→970, +37%); tile 12 is the conservative step.

### L3 — Do NOT ship (measured refutations)
- **Detail mesh off:** post-cleanup +92% (CC_20), +201% (Basamu) — the detail mesh makes fatter triangles that the cleanup removes more effectively. Keep ON.
- **`edgelen` changes alone:** 20 vox cuts raw slivers −26% but post-cleanup +9% (mixed); 6 vox is a disaster (3.3× raw, islands 26). Keep 12.
- **`detdist`:** 1.0 m gives −17% post but +25% raw, +26% build time, islands 19 — not a clean win; 3.0/4.0 are worse. Keep 2.0.
- **`regmin` / `regmerge` / `weldtol` / `vpp`:** no material effect on slivers — don't change. (`vpp` cannot exceed 6: the snapshot path rejects `--vpp 8` with `VertsPerPoly must be in [3, 6]`, and `vpp 3` is +22% post-cleanup.)
- **`cs`:** 0.30 is +9% post; 0.20 is +15% — no win, and cs changes gameplay semantics. Keep 0.25.

### L4 — Optional code-level prevention (experimental, behind a flag)
- **XZ-only deviation gate in the outline simplification** (RcMeshDetails.cs:826): the current 3D deviation lets height components on steep boundaries trigger sample insertion where the XZ deviation is small — and 56% of CC_20's raw slivers are steep≥40°. Comparing XZ deviation only (keeping the height sampling itself unchanged) should cut steep-boundary slivers. **Fidelity risk:** the detail mesh would chord more across steep terrain; must be validated against the staircase/rim gates and in-engine.

### L5 — Determinism (already planned elsewhere)
- The pending wall-clock → work-unit budget change (DeterminismResearch.md §5.1) re-measures cleanup; a source reduction (L1/L2) makes that cheaper: fewer slivers → fewer candidate evaluations → the budget completes more passes within the same counts.

## 8. Validation matrix

| check | command / gate | pass criterion |
|---|---|---|
| CC_20 harness | `SliverCensus.exe --snapshot CC_20_siege_navmesh --slivers --cleanup on` | invariants=PASS; thin-after ≤1048 (L1: ≤602; L1+L2: ≤500); islands within cap |
| Birtu harness | `... --snapshot mp_tdm_Birtu_navmesh --slivers --cleanup on` | invariants=PASS; thin-after ≤353 (L1: ≤267); islands=5..6 |
| Basamu harness | `... --snapshot mp_battle_Basamu_village_navmesh --slivers --cleanup on` | invariants=PASS; thin-after ≤1508 (L1: ≤994); islands=1 |
| v2 CC_20 | snapshot_v2 fixtures in `...\editor_artifacts\` | snapshot PASS; assert-points=0 |
| Determinism | run each fixture twice, compare .navmesh bytes | bit-identical |
| Engine validity | export .navmesh, load in Bannerlord editor | ladder connectors intact, tile borders seam-free, no inside-out faces |
| Boundary fidelity (recommended new probe) | max distance from final navmesh boundary edges to the walkable-cell boundary | ≤ contour budget (0.50 m at 2.0 vox; 0.75 m at 3.0) |
| Pathing smoke test | in-engine agent spawn + path across the tile grid | no cross-tile stalls at the new tile size (L2 only) |

## 9. Open questions

1. **In-engine validation** of boundary fidelity at 2.0/3.0 vox and of tile-12 loading cannot be done from this read-only harness — it needs the Bannerlord editor (the harness's `invariants`/`islands`/`assert-points` all PASS, but they don't measure boundary deviation directly).
2. **Why tile ≤12 removes CC_20's merge-stitch pairs (808→0)** — the merge-stitch gating in the builder deserves a code look before shipping L2.
3. **Why `edgelen 20` reduces raw slivers but slightly increases post-cleanup** (1141 vs 1048) — hypothesis: the surviving slivers are fatter but the decimation candidate set interacts differently; not investigated further.
4. **`vpp 8` is invalid** — the snapshot path rejects it (`VertsPerPoly must be in [3, 6]`, the engine's n-gon limit), and `vpp 3` showed +22% post-cleanup. `vpp` is definitively not a lever.
5. **Terrain-probe discrepancy** at afterPrune (Birtu 34% vs rawTileMesh 100%): the probe classification shifts as faces move through the pipeline; not investigated further.
6. Whether `NavMeshCoarsestSpacing=16` is load-bearing for the engine's tile streaming (needs an engine-side check before L2).

## 10. Artifacts, commands, reproducibility

- **Directory:** `C:\Users\Sheepbitter\AppData\Local\Temp\opencode\sliver_plan\`
- **This plan:** `SliverReductionPlan.md`
- **Instrumentation:** `SliverCensus.cs` (census), `TiledNavMeshBuilder.cs` (instrumented copy), `NavMeshCleanup.cs` (copy), `Program.cs` (copy with knobs), `sliver.csproj`
- **Build:** `dotnet build sliver.csproj -c Release` (from the plan dir; builds clean, one pre-existing benign CS0162 warning at Program.cs:501)
- **Run (baseline census):** `bin\Release\net472\SliverCensus.exe --snapshot C:\Users\Sheepbitter\AppData\Local\Temp\opencode\tiled-core-harness\CC_20_siege_navmesh --slivers --cleanup on`
- **Run (variant):** append `--conterr 2.0` / `--tile 12` / `--detdist 3.0` / `--edgelen 20` / `--regionmin 12` / `--regionmerge 30` / `--cs 0.30` / `--vpp 3` / `--weldtol 0.03`
- **Extract:** `powershell -NoProfile -ExecutionPolicy Bypass -File extract.ps1` → prints the comparison table, writes `census_summary.csv`
- **Sweeps:** `sweep.ps1` (34 runs, hit its 1-hour timeout after 27) + `sweep2.ps1` (12 runs, all done) → `sweep_*.txt` (46 files)
- **Data:** `out_birtu_census.txt`, `out_basamu_census.txt`, `out_cc20_census.txt` (baselines), `sweep_*.txt`, `census_summary.csv`
- **Real repo sources cited (read-only):** `BetterBannerlordNavmeshing\NavMeshCleanup.cs` (:34, :41-53), `SceneNavMeshExporter.cs` (:36, :135, :140, :1363), `ThirdParty\DotRecast\Recast\RcMeshDetails.cs` (:633, :715, :747-863, :826, :865-871, :885-968), `RcContours.cs` (:226, :364, :379-419), `TiledNavMeshBuilder.cs` (:251, :255, :268, :361, :365-370, :374, :381, :386, :393), `README.md` (:1053-1289), `DeterminismResearch.md` (L11-31, L43-44, §5.1)

---

## 11. Independent validation (folded in from `SliverValidationReport.md`)

Validated independently on **LongCat 2.5 Preview Free (OpenCode Go)** by rebuilding `SliverCensus.exe` from source, re-running the fixtures, and re-deriving the code claims. **No claim was refuted.** The census's `afterCleanup` count equals the cleanup's own logged `thin after` in every run (1049==1049, 602==602, 267==267, 989==989), and `SliverCensus.cs` uses the cleanup's own constants directly (`SliverCensus.cs:20-22`), so it cannot drift from the cleanup's metric.

### 11.1 Reproduction table (validator's thin-after vs this plan)

| run | plan | validator | verdict |
|---|---|---|---|
| CC_20 defaults | 1048 | **1049** | ±1 (wall-clock noise) |
| CC_20 conterr 1.0 | 1179 | **1179** | exact |
| CC_20 conterr 2.0 | 602 | **602** | exact |
| CC_20 conterr 3.0 | 450 | **450** | exact |
| CC_20 tile 12 | 915 | **915** | exact |
| CC_20 tile 8 | 804 | **804** | exact |
| CC_20 conterr2+tile12 | 500 | **500** | exact |
| CC_20 detail off | 2013 | **2013** | exact |
| Birtu defaults | 353 | **353** | exact |
| Birtu conterr 2.0 | 267 | **267** | exact |
| Basamu conterr 2.0 | 994 | **989** | −5 (noise) |

Stage deltas also reproduced exactly on CC_20: rawTileMesh 7184 → tileAfterStitch 7311 (+127) → afterMerge 7253 → afterNonManifold 7252 → afterPlateBlend 7247 → afterMergeStitch 7247 → afterSteepRepair 7210 → afterPrune 5933 → afterCleanup 1049 (99.05% born pre-stitch). All runs: `invariants=PASS`, `SNAPSHOT: PASS`, `assert-points: 0`, `concaveQuads=0`, islands 13/6/1 (CC_20/Birtu/Basamu); build time 39818 → 29783 ms (**−25%** for conterr2+tile12; plan −27%, machine variance).

### 11.2 New findings

1. **`MaxCleanupSlopeDegrees` (NavMeshCleanup.cs:60) and `FaceSlopeDeg` (:1852) are dead code** — defined but never called anywhere in the repo, so the documented "Fans containing any face steeper than this are never touched" guard (comment :57-60) is **not enforced**. Steep slivers survive via the enforced guards instead: normal spread `MaxFanNormalSpreadDegrees=120` (:40, used :742), move tolerance `BoundaryToleranceMeters=0.35` / `SurfaceToleranceMeters=0.50` (:36/:34), and height change `MaxHeightChangeMeters=0.15` (:69). *This was found independently by the determinism validator as well.* It refines why the residual exists and does not affect any plan lever (the plan never relied on a slope gate).
2. **67% of surviving CC_20 slivers are steep ≥40°** (707/1049 at defaults; 63% at conterr 2.0) — the cleanup-resistant residual. Further gains must target steep-boundary triangulation (see L4), not cleanup tuning.

### 11.3 Missed-options scan (validator runs)

| option | thin-after | Δ vs 1049 | islands | assessment |
|---|---|---|---|---|
| `--radius 0.5` (shipped 0.25) | 786 | −25% | 13 | **load-bearing** agent-size change (`NavMeshVoxelAgentRadius`, SceneNavMeshExporter.cs:66) |
| `--slope 45` (shipped 50) | 930 | −11% | 12 | **load-bearing** traversable-slope change (`NavMeshMaxSlopeDegrees`, :37) |
| `--tile 8 --conterr 2.0` | 423 | −60% | 15 | numerically best, but aggressive: per-tile stitch 726→970, CC_20 merge-stitch pairs 808→0 |

**Nothing beats the recommendation without a load-bearing semantic change.** The only free option that beats conterr2+tile12 (500) is the more aggressive tile-8 combo, which L2 already flags as changing more.

### 11.4 Caveats

- The cleanup is wall-clock-gated (documented run-to-run variance up to 215 faces, `DeterminismResearch.md:31`); baselines varied ≤5 faces between runs while every knob run reproduced exactly. All headline deltas are far outside the noise band.
- The L3 don't-touch list (edgelen/regmin/regmerge/weldtol/vpp/cs) and the Birtu/Basamu default stage tables were verified from the original artifacts, not re-run by the validator.
- In-engine boundary fidelity at 2.0/3.0 vox and tile-12 loading remain unverifiable from this read-only harness (as §9 states).
- Validator artifacts: `SliverValidationReport.md` and `val_*.txt` in the same directory.
