# Deterministic mesh cleanup / simplification — research report

BetterBannerlordNavmeshing / `NavMeshCleanup.cs` (C# 7.3, net472, Recast-style tiled voxel pipeline)
Research subagent, read-only on the repo. Model: `opencode/space-bunny-free`.

---

## 0. Verdict up front

* Every established navmesh/mesh-simplification implementation surveyed binds its work with **counts**, never with wall clock: meshoptimizer (`while (result_count > target_index_count)`), Open3D (`while (n_triangles > target_number_of_triangles && !queue.empty())`), Blender (`while (bm->totface > face_tot_target)`), Recast (fixed per-poly scans). Unity's *asynchronous* bake is the counter-example, and its core is closed source.
* **Your four wall-clock gates are the single source of nondeterminism in the whole pipeline.** A repo-wide grep for `ElapsedMilliseconds` shows every other `Stopwatch` in the project is measurement/logging only; the only comparisons against elapsed time (`ElapsedMilliseconds >`, `ElapsedSeconds >`, "deadline") are the 4 gates in `NavMeshCleanup.cs` (lines 443, 1363, 1585, 1920). The upstream tile build is already parallel-but-canonical: `TiledNavMeshBuilder.cs:24` ("sorted tile order, so the result is deterministic under `Parallel.For`") and `:1061-1072` (per-index output slots).
* **None of your 4 gated passes lacks a count cap already.** `Decimate` has `sweep < 16`; `RefineLongThin` has `sweep < 8` *and* `splits < faces/20`; `RelaxThin` has `sweep < 3`; `FlipEdges` has `iter < 6`. The wall clock is a *second* limiter stacked on an existing count bound — so replacing it is a small, low-risk change, not a redesign.
* Dictionary/hash iteration is **not** currently a source of nondeterminism in this file: all 8 `foreach (KeyValuePair<…>)` sites are order-independent aggregations (set a boundary flag) or min-reductions (`MinKey`), and `LongHash` is a deterministic bijective finalizer. No RNG anywhere in the pipeline.
* Recommendation: replace the clock with (1) **sized work-unit budgets** for guard evaluations / split attempts / flip attempts, plus (2) the existing count caps as the hard outer bound, plus (3) a *cos-space* comparison instead of `Math.Acos` in the guard predicates so topology decisions don't depend on transcendental rounding. Do **not** restructure `Decimate` into QEM-style priority decimation as part of this change.

---

## 1. Current-code inventory (read-only findings)

Gated passes and their gates:

| Pass | Line | Gate | Existing count caps | Iteration order |
|---|---|---|---|---|
| `RefineLongThin` | 1585 | `(t & 1023)==0 && watch.ElapsedMilliseconds > maxMs` | `sweep < 8`, `splits < faces/20` | face index ascending |
| `Decimate` | 443 | `(vert & 255)==0 && watch.ElapsedMilliseconds > maxMs` → `timeUp` | `sweep < 16` | CSR vertex index ascending (Gauss-Seidel, in place) |
| `RelaxThin` | 1363 | `(vert & 255)==0 && … > maxMs` | `sweep < 3` | vertex index ascending (Gauss-Seidel) |
| `FlipEdges` | 1920 | `watch.ElapsedMilliseconds > maxMs` checked **per edge key** | `iter < 6` | `keys.Sort()` ascending, then skip `touched[]` |

Additional observations that matter:

* **Cascade:** `Run` computes `refineUsed = elapsed` and shifts every later deadline (lines 176-196), so a slow machine changes *which* passes get budget and by how much. This is the amplifier that turns a 1-face difference into a 215-face difference (241,717 vs 241,502).
* **Log nondeterminism is separate and cheap to kill:** `st.DecimDiag` (line 438) embeds `watch.ElapsedMilliseconds`, and `NavMeshLog` writes `DateTime.Now` timestamps. The "editor outputs differ by KB" symptom is at least partly these lines, independent of the mesh.
* **Ordering is already canonical everywhere it matters.** `MergeQuads` walks faces and the 3 edges of each face in index order and its `better` predicate is strict (`perim < bestPerim - 1e-9`), so ties resolve to the lowest edge index. `BuildRing` picks its start as the minimum-index degree-1 endpoint, else `MinKey(adj)`. `FlipEdges` sorts unique `long` keys, so `List<long>.Sort()` has no ties to break.
* **Guard rejections are already an exact work counter.** `st.RejMove/RejFan/RejRing/RejTopo` + `st.CandVerts` count exactly the `TryRemoveVertex` calls. Your own note ("0.20 m refused ~240k candidate folds on Birtu") is a measured guard-eval count — the ideal calibration input for a work-unit budget.
* **Transcendentals gate every topology decision.** `MinAngle` (line 253) computes `Math.Acos(cosA) * 180/PI`; `AngleDeg` (2071) and `FaceSlopeDeg` (1852) likewise. These are not required to be correctly rounded and are runtime/CPU-dependent, so they are the one thing that can break *cross-machine* determinism even after the clocks are gone.

---

## 2. Findings — how established tools handle this

### 2.1 Recast / Detour (upstream `main`)

* `rcBuildPolyMeshDetail` iterates `for (int i = 0; i < mesh.npolys; ++i)` — a fixed scan over polys, each independent, no budget of any kind. (https://github.com/recastnavigation/recastnavigation/blob/main/Recast/Source/RecastMeshDetail.cpp, `rcBuildPolyMeshDetail`, ~L1289)
* `buildPolyDetail` tessellates outline edges then **simplifies by inserting the point of maximum deviation**: `if (maxi != -1 && maxd > rcSqr(sampleMaxError)) { insert at k+1; nidx++ } else ++k;`. Deterministic (`>` strict ⇒ first maximum wins), monotone, bounded by point count. This is exactly the "quality-driven insert, count-bounded" shape your `RefineLongThin` already has — Recast just has no clock on top of it.
* Recast contains an explicit determinism idiom, commented in the source: *"Make sure the segments are always handled in same order using lexological sort or else there will be seams."* (RecastMeshDetail.cpp ~L708-725) — canonical ordering of unordered work to remove seam ambiguity.
* `simplifyContour` (RecastContour.cpp ~L209-360) is the same insert-max-deviation loop driven by `maxError`/`maxEdgeLen` on contour vertices.
* **No parallelism in the mesh build:** grepped `Recast.cpp`, `RecastMesh.cpp`, `RecastMeshDetail.cpp`, `RecastContour.cpp`, `RecastArea.cpp`, `RecastLayers.cpp`, `RecastFilter.cpp`, `RecastRegion.cpp`, `RecastRasterization.cpp` for `#pragma omp|omp_get|std::thread|parallel for` → **0 hits each**.

### 2.2 meshoptimizer (zeux) — the closest model to what you want

* `src/simplifier.cpp` contains **no `#pragma omp` and no threading of any kind** (verified across all 21 files in `src/`). Simplify is single-threaded by construction.
* Termination is purely count-based: `while (result_count > target_index_count) { … }`, with a secondary `if (collapses == 0) break;` and an error limit `error_limit`. Inner loop: `pickEdgeCollapses → rankEdgeCollapses → sortEdgeCollapses → performEdgeCollapses(…, triangle_collapse_goal, …)`. Note `triangle_collapse_goal = (result_count - target_index_count)/3` — a **per-pass work quota**, the direct analogue of what you want for `Decimate`.
* The ordering is a canonical total order with a deliberate tie-break: `sortEdgeCollapses` is a **counting sort on the top 12 bits of the float error** (`key = (error << 1) >> (32 - sort_bits)`), filled by a stable ascending scan over the candidate array. The source comment says so: *"we use counting sort to order collapses by error; since the exact sort order is not as critical, only top 12 bits of exponent+mantissa … are used."* Ties therefore fall back to candidate enumeration order, which comes from a face-index-order scan. This is a cheap, stable, integer-key sort — no comparison sort instability, no heap.
* `meshopt_simplifyWithUpdate` / `meshopt_remesh` (voxel remeshing) are the same discipline: fixed-pass, resolution-bounded.
* **No documented determinism guarantee.** My claim here is from source inspection, not from a vendor statement.

### 2.3 Open3D `TriangleMeshSimplification` (QEM)

* `while (n_triangles > target_number_of_triangles && !queue.empty())`, plus `if (cost > maximum_error) break;` — **two orthogonal deterministic stop conditions**: a target count and an error bound. (https://github.com/isl-org/Open3D/blob/main/cpp/open3d/geometry/TriangleMeshSimplification.cpp L320-400)
* Priority queue with a comparator that compares **cost only** (`std::get<0>(a) > std::get<0>(b)`), so equal-cost order is left to `std::push_heap` internals: deterministic for a given STL implementation, *unspecified* in the standard. Reproducible in practice, not canonical.
* Lazy invalidation is the validity test: a popped edge is recomputed and skipped unless `cost == cost_vbar.first` — a clean deterministic re-check.

### 2.4 Blender decimate (QEM modifier)

* `while ((bm->totface > face_tot_target) && (BLI_heap_is_empty(eheap) == false) && …)` in `source/blender/bmesh/tools/bmesh_decimate_collapse.cc` (L1371, L1401). Count-based termination, `BLI_heap_*` cost heap, `insert_or_update` re-scoring, `COST_INVALID` for degenerate edges. Same tie-order caveat as Open3D.

### 2.5 TriDecimate — the best explicit "deterministic with parallelism" reference

* `docs/algorithms.md`: *"The selection order is fully deterministic. The heap is keyed on `edge_key = min(a,b) * n_vert + max(a,b)` … Because `edge_key` depends only on the unordered vertex pair — **not** on insertion order, thread scheduling, or device — two entries with an equal `cost` are always ordered the same way."*
* *"CPU: OpenMP parallelizes per-edge cost computation and heap seeding. The greedy loop is host-driven and single-threaded for determinism. GPU: … the greedy selection loop is host-driven with the *identical* `edge_key` tie-break, so the result is bit-identical to the CPU on ties."*
* Bounded by `target_reduction` (a count) and by `iterations = 6` for the optional Lloyd/CVT regularisation pass — a fixed iteration count, not a clock.
* Caveat: README says "work in progress… research-grade reference implementation… no independent third-party review". Cite it as a *pattern*, not a dependency.

### 2.6 Godot navmesh baking

* `modules/navigation_3d/3d/nav_mesh_generator_3d.cpp` runs the whole Recast pipeline on **one** `WorkerThreadPool` task per navmesh (`use_threads`, `baking_use_multiple_threads` only control whether several navmeshes bake concurrently). Recast itself is single-threaded.
* The Recast→native conversion welds with `HashMap<Vector3,int> recast_vertex_to_native_index` in a **linear scan over `detail_mesh->nverts`**, and the map is only ever used for lookup, never iterated. Same pattern as your `Weld`.
* No determinism statement in docs or source; conclusion from inspection only.

### 2.7 Unity

* The C# layer is a thin wrapper (`NavMeshSurface.BuildNavMesh` → `NavMeshBuilder.BuildNavMeshData`; `UpdateNavMesh` → `NavMeshBuilder.UpdateNavMeshDataAsync`). The bake core is native/closed. `CollectSources` order comes from scene traversal, which is deterministic for a fixed scene but not a documented guarantee.
* I found **no** official Unity determinism statement. The only hit is a practitioner forum reply: *"Navmesh is not deterministic. If you need to make a multiplayer game you cannot use NavMesh, unfortunately."* (Unity Discussions, "Is navmesh deterministic?", 2022-06-16) — treat as anecdote, not documentation.
* The relevant architectural lesson is that Unity's *asynchronous* bake is incremental and therefore inherently budget-shaped — i.e. the one place a clock belongs is an interactive/incremental API, not a batch asset build.

### 2.8 Academic / industry practice

* **QEM** — Garland & Heckbert, "Surface simplification using quadric error metrics", SIGGRAPH '97, DOI `10.1145/258734.258849` (PDF: http://mgarland.org/files/papers/quadrics.pdf). Priority-queue edge contraction by quadric error: a best-first greedy with a cost order.
* **Progressive meshes** — Hoppe, "Progressive meshes", SIGGRAPH '96, DOI `10.1145/237170.237216`; and Hoppe 1999 quadric-metric-with-attributes, DOI `10.1109/visual.1999.809869`. Inward decimation sweeps ordered by a geometric/appearance cost, stopping at a target vertex count.
* **Recent survey** — Kulkarni & Narayanan, "A Comprehensive Guide to Mesh Simplification using Edge Collapse", arXiv:2512.19959. Section 4 states the canonical structure: cost per edge → priority queue → *"A target triangle count is either defined internally by the program or specified externally by the client code. Then, the following steps are repeated until the target triangle count is reached"*, with validity checks (flipped-normal, two-common-neighbour, boundary-merge) that map almost one-to-one onto your `RejTopo`/`RejFan` guards.
* **Out-of-core / streaming** — Lindstrom, "Out-of-core simplification of large polygonal models", SIGGRAPH '00, DOI `10.1145/344779.344912`. Partition → per-partition simplification with locked boundaries → merge. The transferable idea: **lock the boundaries of each part so parts are independent, then process parts in a canonical (index) order** — a "static schedule" guarantee instead of a clock. Directly applicable if a future mesh outgrows one pass's working set.
* **Reproducible floating-point reductions** — Demmel & Nguyen, "Fast Reproducible Floating-Point Summation", ARITH 2013, DOI `10.1109/arith.2013.9`; and "Algorithms for Efficient Reproducible Floating Point Summation", ACM TOMS 2020, DOI `10.1145/3389360`. The lesson for us is *avoid* needing order-independent sums: keep every reduction short (≤ ring size) and in a fixed order — which your `PlaneMove`, `Newell`, and area sums already do.

---

## 3. Comparison table

| System | Stop condition | Ordering | Parallelism | Deterministic by construction? | Notes |
|---|---|---|---|---|---|
| Recast detail mesh | fixed per-poly scan; insert-while-deviation > error | canonical edge direction ("lexological sort" comment) | none in build | **Yes** | upstream `main` has no OpenMP in Recast/Source |
| Recast contour | insert-while-deviation > maxError | contour index order | none | **Yes** | |
| meshoptimizer simplify | `result_count > target_index_count`; per-pass `triangle_collapse_goal` | stable counting sort on top-12 error bits, tie = candidate order | none | **Yes** (undocumented) | closest model for `Decimate` |
| Open3D QEM | target triangle count + max error | `std::priority_queue`, cost-only comparator | none | Reproducible per-STL, **tie order unspecified** | |
| Blender decimate | `bm->totface > face_tot_target` | `BLI_heap` cost heap | none | Reproducible per-build, **tie order unspecified** | |
| TriDecimate | `target_reduction`; `iterations` | canonical `min*n+max` edge key | OpenMP cost phase only; greedy loop single-threaded | **Yes**, CPU≡GPU | pattern reference |
| Godot navmesh | fixed per-poly scans (Recast) | index order; weld map never iterated | one task per navmesh | Likely yes (undocumented) | |
| Unity bake | undocumented (closed) | undocumented | yes, tile-based | **No documented guarantee** | async API is budget-shaped by design |
| **This project (today)** | 4 wall-clock gates + count caps | index order (canonical) | none in cleanup | **No** — clock-dependent | `Parallel.For` upstream is already canonical |
| **This project (proposed)** | sized work-unit budgets + existing count caps + quality/no-progress stops | index order, explicit total orders where ties exist | none | **Yes**, decision-identical | ms retained only as a diagnostic warning |

---

## 4. Determinism techniques (RQ3), with what each buys you here

1. **Work-unit (count) budgets instead of time.** Counters are functions of the input, so the schedule is a function of the input. Every surveyed production simplifier does this. Use counts that scale with the mesh (candidate count, face count, interior-edge count), never absolute constants.
2. **Fixed sweep counts as the hard outer bound.** You already have them (`16 / 8 / 3 / 6`). A time budget *on top of* a sweep cap is the bug, not the sweep cap.
3. **Quality-driven loops with monotone progress.** "Insert the worst offender if it exceeds a threshold" (Recast) or "collapse the cheapest edge if it is under target error" (QEM family) terminate because the metric itself bounds the work — no clock needed.
4. **Stable canonical tie-breaks.** Either a stable counting sort on a quantized key (meshoptimizer's `sortEdgeCollapses`, top 12 bits of the float) or a key derived only from the *unordered* object identity (`min*n+max`, TriDecimate). Never `std::priority_queue`/`BLI_heap` with a cost-only comparator and no secondary key — that's the Open3D/Blender weakness.
5. **Order-independent (bulk-synchronous) local operators.** Compute all decisions from the round-start state, apply them, repeat a fixed number of rounds. Your `FlipEdges.touched[]` mask and your `Decimate.dirty[]` mask are already exactly this pattern; the masks make each round's *result* independent of intra-round ordering.
6. **Parallelize only the pure batch phase.** Keep the greedy/sequential accept loop host-driven (TriDecimate). If you ever parallelise `Decimate`, parallelise guard *evaluation* into per-vertex result arrays indexed by vertex, then apply serially in index order — the same trick `TiledNavMeshBuilder` already uses with `Parallel.For` per-index output slots.
7. **Avoid FP non-determinism structurally.** (a) Single-threaded, no SIMD reductions, fixed reduction order — already true. (b) Never let `Math.Acos`/`Sin`/`Atan2` gate a *decision*: compare in cos space (`cosA >= Math.Cos(thresholdRad)`) so the branch is bit-identical anywhere. (c) If a sum must be order-independent, use a reproducible summation method — you don't need it, your reductions are over ≤ ring-size in fixed order. (d) No `/fp:fast`, no reassociation, no `float` accumulators where `double` matters (your `Len`/`Area` already use `double` on `float[]` inputs — good).
8. **Hash containers.** .NET Framework `Dictionary<TKey,TValue>` docs state plainly: *"The order in which the items are returned is undefined."* (https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.dictionary-2?view=net-472). Rule: hash containers are lookup tables only; any enumeration that could reach an output must be preceded by a sort, or replaced by an array scan. Your file currently complies; `LongHash` should be kept because it makes hashing itself platform-stable.

---

## 5. Concrete recommendation for `NavMeshCleanup`

### 5.0 Cross-cutting, do first (lowest risk, visible immediately)

1. **Delete the four `watch.ElapsedMilliseconds > maxMs` comparisons** and the `Stopwatch` parameter from `Decimate`, `RelaxThin`, `RefineLongThin`, `FlipEdges`. Keep the `Stopwatch` in `Run` **only** to log `totalMs`.
2. **Repurpose `CleanupMaxMs` (TiledNavMeshBuilder.cs:307) into a work-unit budget** (`CleanupWorkUnits`, `int`). Add a *diagnostic* deadline that, when exceeded, only logs a warning and marks `Stats.DeadlineExceeded` — never changes output.
3. **Replace the millisecond strings in `DecimDiag`** with the deterministic counters (`sweeps`, `guardEvals`, `faceVisits`, `splits`, `flips`). Also make `NavMeshLog`'s run header and filename use a caller-supplied timestamp rather than `DateTime.Now` when reproducibility of the *artefact* matters. This alone removes the KB-scale editor-output churn that is not the mesh.
4. **Convert the guard predicates to cos space.** Add `static readonly double CosNeedleAngle = Math.Cos(5 * Math.PI / 180)` etc., and change `MinAngle(v,a,b,c) < ThinMinAngleDegrees` into `CosOfMinAngle(v,a,b,c) > CosThinMinAngle` (remember `acos` is decreasing, so the inequality flips). Keep `MinAngle` for reporting only. Same for `AngleDeg` (line 2071) and `FaceSlopeDeg` (1852), which drive `QuadMergeFoldMaxDegrees`, `MaxFanNormalSpreadDegrees`, `QuadConvexMaxInteriorAngleDegrees`, `QuadXzMaxInteriorAngleDegrees`, `FlipNormalAngleDegrees`.
   *Why this matters even for same-machine reproducibility:* it doesn't help there, but it is the only thing that makes the pipeline's **decisions** independent of the .NET runtime's libm, so a user on a different machine/runtime gets the same mesh rather than a merely similar one.
5. **Add a determinism self-test**: call `Run()` twice on the same input and assert byte-identical `verts`/`tris`/`QuadPairs`; plus a golden FNV-1a/xxHash over the emitted floats+indices per map, committed to the repo and asserted. This converts "we believe it's deterministic" into a regression gate — which is what a procedural-terrain pipeline actually needs.
6. **Optional, later:** a `CleanupAuditDeterminism` mode that re-runs dictionary-enumerating paths through sorted-key enumeration and asserts equality, so a future edit that starts depending on hash order fails loudly.

### 5.1 Per-pass budgets (work units, worst-case bounds)

Let `F0` = faces at pass entry, `V0` = CSR vertices, `E0` = interior edges at pass entry, `C0` = candidate vertices selected by the pass's own mask (already counted as `CandVerts`; for `RefineLongThin`, `C0 = F0`).

| Pass | Work unit | Budget (all functions of the input) | Hard outer bound | Worst case |
|---|---|---|---|---|
| `RefineLongThin` | one face evaluation in the scan | `faceVisits <= F0 * RefineSweepCap` (`RefineSweepCap` default 5, down from 8) | `sweep < RefineSweepCap` **and** existing `splits < F0/20` | `5 * F0` face evaluations + `F0/20` `SplitEdge` calls |
| `Decimate` | one `TryRemoveVertex` call (= one guard evaluation) | `guardEvals <= DecimateGuardMultiple * C0 + F0/2` (`DecimateGuardMultiple` default 8) | `sweep < 16` **and** `!any` → break | 16 × `O(F)` rebuilds + `8*C0` guards |
| `RelaxThin` | one `TryRelax` call | `relaxEvals <= 2 * C0 + 65536` | `sweep < 3` | `3 * O(F)` rebuilds + `2*C0` relaxations |
| `FlipEdges` | one edge considered (`TryFlip` attempted) | `edgeVisits <= 8 * E0` (total across all 6 iterations) | `iter < 6` | 6 × `O(E log E)` sorts + `8*E0` attempts |

Why these bounds are the right shape:

* The dominant costs are the **per-sweep `O(F)` rebuilds** (`CompactFaces` + `BuildFaceAdjacency` + component BFS + candidate mask) and the **guard evaluations**. The former is already bounded by the sweep caps; the latter is what the clock was silently truncating, and it is exactly what `CandVerts`/`Rej*` already measure. So the budget is placed on the guard count, where the pathological case lives (your own measurement: ~240k guard rejections on Birtu at the 0.20 m tolerance).
* `F0/2`, `F0/20`, `65536` are proportional floor terms so that tiny or unusual meshes still get enough work to converge, while huge meshes stay linear (a 2 M-face mesh costs ~8× a 240 k-face mesh, not a fixed 6 s).
* `DecimateGuardMultiple` is a single knob that directly trades quality for time. Calibrate it once: run the harness with the counters instrumented on Birtu / CC_20 / Basamu, record `(guardEvals, ms)`, and pick the multiple whose predicted worst case matches the old 6 s on the slowest reference machine. **Expect quality to change** (see §6).

Two extra cheap wins inside `FlipEdges`:

* Hoist the time check removal only — do not restructure. But note the current code rebuilds and re-sorts *all* edge keys after every flipping round (`edgeFaces = BuildEdgeFaces(tris)`). Since a flip only destroys two edges and creates the diagonal, you can keep the sorted key array and filter stale keys, which removes `iter-1` full re-sorts. Deterministic either way; do it only if measurement motivates it.
* `touched[]` is allocated once at `nf` but `nf` changes between iterations; re-check the bounds (`for (int i = 0; i < nf; i++) touched[i] = false;` uses the pre-rebuild `nf` while the map may now be smaller). Cheap correctness/robustness fix, not a determinism issue.

### 5.2 Should any pass be restructured?

* **`Decimate`: no, not now.** Converting to QEM-style priority decimation (cost = surface move, stable counting sort on quantized cost, per-pass collapse quota à la `triangle_collapse_goal`) is the textbook "meshoptimizer" structure and would give better quality per unit work. But your guards are an *accept/reject predicate*, not a cost; you would have to invent a severity metric, and it would invalidate every measurement currently in your README (quad-merge counts, boundary moves, the 127/587/1214 merge deltas). If you ever do it: score candidates, then `sortEdgeCollapses`-style stable counting sort on the top bits of the score with candidate index as the tie-break, and use `triangle_collapse_goal`-style per-sweep quotas as the work budget. Sequence it as its own change, after the clock gates are gone and the self-test is in place.
* **`FlipEdges`: no structural change.** The `touched[]` mask already makes it a bounded bulk-synchronous local operator.
* **`RefineLongThin`, `RelaxThin`: no change.** Both already have count caps and monotone quality criteria.
* **`MergeQuads`: no change — it is already fully deterministic** (index-order greedy with strict tie comparisons). Leave it alone; it is the model for the rest of the file.
* **Longer-term alternative worth recording:** a deterministic *voxel remesh* (fix a grid resolution, run marching-cubes-style extraction, then one `meshopt_simplifyWithUpdate`-style pass) is bounded by construction and has no sliver population to clean, at the price of a large behavioural change and loss of exact boundary/island control that your engine-facing guarantees depend on. Not recommended for this codebase; noting it because it is the design point the field converged on for "cleanup is too hard".

### 5.3 Calibration procedure (one-time, ~1 hour)

1. Add the four counters to `Stats` and zero the clocks out.
2. Run the harness on Birtu / CC_20 / Basamu / Basamu-small. Record per pass: `sweeps`, `guardEvals`, `faceVisits`, `splits`, `edgeVisits`, and the wall ms.
3. Derive `msPerUnit` per pass on the slowest reference machine. Set the multipliers so that `predictedWorstCase ≈ 6000 ms` at the largest observed mesh.
4. Re-run the quality harness (Test 7 `detailCoversFan`, needle/thin before/after, `QuadXzWorstAngle`, `QuadXzNearStraight`, boundary moves) and reconcile any regression by tuning the multiplier, not by reintroducing a clock.
5. Commit the golden hashes; add the double-run self-test to CI.

---

## 6. Tradeoffs, risks, unknowns

**Tradeoffs**

* *Quality vs. runtime becomes a compile-time/user-time constant* instead of adapting to machine speed. On a slow machine a deterministic budget takes longer than 6 s; on a fast machine it finishes earlier. That is the point, but it means the `CleanupMaxMs` contract has to be re-expressed in the README.
* *Worst case may exceed the old budget* if the old clock was usually the binding constraint. Mitigation: the diagnostic deadline warning + multipliers.
* *Sizing budgets off `C0` couples runtime to the candidate population*, which itself depends on tolerances. If you later tighten `SurfaceToleranceMeters`, runtime moves. Prefer `F0`/`E0` (topology-only, tolerance-independent) as the primary scaling term and `C0` as the multiplier term.

**Risks**

1. **Quality regression is the main risk.** Today the clock silently truncates; with a deterministic budget you will decimate *more* on a slow machine (and exactly as much on all machines). Any of your tuned thresholds (`QuadXzMaxInteriorAngleDegrees`, `QuadMergeFoldMaxDegrees`, `MaxCleanupSlopeDegrees`) was measured against clock-truncated behaviour. Re-measure.
2. **Guard counters change.** `RejMove/RejFan/RejRing/RejTopo` become deterministic; the run log will differ from historical logs, and any golden log comparison must be updated.
3. **Per-sweep `O(F)` rebuilds may dominate** on the largest maps once guard evaluations are no longer the throttle. If so, lower `sweep` caps (16 → 8 for `Decimate`) — still deterministic, and cheaper. Measure before assuming.
4. **Cross-machine bit-identity is not fully achievable.** `Math.Acos/Sin/Cos/Atan2/Exp` in .NET are not required to be correctly rounded; the CLR version and CPU can change the last ULP, which can flip a comparison like `cosA < 0.5`. §5.0.4 removes this from the decision path. State the contract honestly as: *identical decisions and identical topology for any input on any machine with the same constants; byte-identical output for the same binary + runtime.*
5. **Input determinism is upstream of you.** The tile build is already canonical under `Parallel.For` and DotRecast's multithread path writes per-index results, but scene/prop traversal order, physics/collision state, and any procedural generator with a clock or RNG upstream would still produce different input. I found no RNG in this repo; I did not audit the terrain generator's inputs.

**Unknowns / thin evidence (stated plainly)**

* No vendor documents a determinism guarantee for meshoptimizer, Godot, or Recast. Every "deterministic" claim above for those is from source inspection (verified: single-threaded, count-bounded, explicit ordering).
* Unity's bake core is closed source; the "not deterministic" claim is a single forum reply, not documentation.
* I did not find a peer-reviewed paper specifically on determinism in mesh simplification. The nearest are the deterministic-parallel-graph-processing line (LPG / Chen & Lerner PPoPP '11, DOI `10.1145/1993746.1993766` — Crossref could not resolve it for me, so treat the DOI as unverified) and TriDecimate (a project, not peer-reviewed). The determinism techniques themselves are well established in reproducible-HPC and fixed-timestep simulation work.
* I did not measure anything. All cost estimates (e.g. "8 × 360k dictionary inserts ≈ sub-second") are order-of-magnitude reasoning from the code, not measurements.

---

## 7. Sources

Verified by fetching the source/text directly unless marked.

1. meshoptimizer `src/simplifier.cpp` — `while (result_count > target_index_count)`, `triangle_collapse_goal`, `sortEdgeCollapses` stable counting sort on top-12 error bits, no OpenMP. https://raw.githubusercontent.com/zeux/meshoptimizer/master/src/simplifier.cpp ; docs https://meshoptimizer.org/#simplification
2. meshoptimizer `README.md` / all of `src/` — verified zero `#pragma omp` / threading. https://github.com/zeux/meshoptimizer
3. Recast `Recast/Source/RecastMeshDetail.cpp` — `rcBuildPolyMeshDetail` fixed poly scan; `buildPolyDetail` "Simplify samples" max-deviation insertion; the "lexological sort … or else there will be seams" comment. https://github.com/recastnavigation/recastnavigation/blob/main/Recast/Source/RecastMeshDetail.cpp
4. Recast `Recast/Source/RecastContour.cpp` — `simplifyContour`, insert-while-deviation > `maxError`. https://github.com/recastnavigation/recastnavigation/blob/main/Recast/Source/RecastContour.cpp
5. Recast `Recast/Source/*.cpp` (8 files) — verified no OpenMP/threads. https://github.com/recastnavigation/recastnavigation/tree/main/Recast/Source
6. Open3D `TriangleMeshSimplification.cpp` — count + error stop conditions, cost-only comparator, lazy invalidation. https://github.com/isl-org/Open3D/blob/main/cpp/open3d/geometry/TriangleMeshSimplification.cpp
7. Blender `bmesh_decimate_collapse.cc` — `while (bm->totface > face_tot_target)`, `BLI_heap` cost heap. https://github.com/blender/blender/blob/main/source/blender/bmesh/tools/bmesh_decimate_collapse.cc
8. TriDecimate README + `docs/algorithms.md` — canonical `min*n+max` tie-break, host-driven greedy loop, `iterations=6`. https://github.com/stefanomoriconi/TriDecimate
9. Godot `nav_mesh_generator_3d.cpp` — one worker task per navmesh; index-order weld with a lookup-only `HashMap`. https://github.com/godotengine/godot/blob/master/modules/navigation_3d/3d/nav_mesh_generator_3d.cpp
10. Unity `NavMeshComponents` (NavMeshSurface.cs, NavMeshAssetManager.cs, LocalNavMeshBuilder.cs) — thin wrapper over closed-source `NavMeshBuilder`. https://github.com/Unity-Technologies/NavMeshComponents
11. Unity Discussions, "Is navmesh deterministic?" (practitioner claim, not docs). https://discussions.unity.com/t/is-navmesh-deterministic/884832
12. .NET Framework `Dictionary<TKey,TValue>` docs — "The order in which the items are returned is undefined." https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.dictionary-2?view=net-472
13. Garland & Heckbert, *Surface simplification using quadric error metrics*, SIGGRAPH '97. DOI 10.1145/258734.258849 · PDF http://mgarland.org/files/papers/quadrics.pdf
14. Hoppe, *Progressive meshes*, SIGGRAPH '96. DOI 10.1145/237170.237216 (Crossref-verified)
15. Lindstrom, *Out-of-core simplification of large polygonal models*, SIGGRAPH '00. DOI 10.1145/344779.344912 (Crossref-verified)
16. Demmel & Nguyen, *Fast Reproducible Floating-Point Summation*, ARITH 2013. DOI 10.1109/arith.2013.9 (Crossref-verified)
17. Demmel & Nguyen, *Algorithms for Efficient Reproducible Floating Point Summation*, ACM TOMS 2020. DOI 10.1145/3389360 (Crossref-verified)
18. Kulkarni & Narayanan, *A Comprehensive Guide to Mesh Simplification using Edge Collapse*, arXiv:2512.19959 — §4 canonical algorithm, count-based termination. https://arxiv.org/html/2512.19959v1
19. Callahan, Herlihy & Wagner, *Parallel floating-point reductions are deterministic: answers to 18 open questions*, SIAM Review 2007 — **could not verify; DOI/URL lookup failed. Do not cite without checking.**
20. Chen & Lerner, LPG (PPoPP '11) — **DOI 10.1145/1993746.1993766 could not be resolved via Crossref; treat as unverified.**

---

**Model/provider:** `opencode` / `space-bunny-free` (OpenCode Space Bunny Free), session `ses_eeb909943ffernCqh9SKXDfGai`.