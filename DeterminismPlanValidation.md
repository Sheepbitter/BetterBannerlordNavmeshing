# Independent validation — Determinism Implementation Plan

**Model/provider:** `opencode-go` / `longcat-2.5-preview-free` (LongCat 2.5 Preview Free), session `ses_eeb4508deffenW7gqSKszDAf7d`.
**Method:** read-only re-derivation against `C:\Users\Sheepbitter\source\repos\BetterBannerlordNavmeshing`; repo-wide `rg`; no builds, no writes, no commits.
**Note:** `DeterminismImplementationPlan.md` is untracked and currently present in the repo root; its line anchors match the tree. Repo `tools/TiledCoreHarness/Program.cs` and the temp harness are **byte-identical** (SHA-256 `B7A41AA1…A61AA`, both 488,495 bytes), so the plan's harness anchors are unambiguous.

---

## Claim 1 — the four gates are the only elapsed-time comparisons

**Verdict: CONFIRMED for output-affecting comparisons; the literal claim ("only comparisons") is REFUTED.**

The four gates exist exactly as stated and are the only elapsed comparisons that change the build result:

- `NavMeshCleanup.cs:443` `if ((vert & 255) == 0 && watch.ElapsedMilliseconds > maxMs) { timeUp = true; break; }`
- `NavMeshCleanup.cs:1363` `if ((vert & 255) == 0 && watch.ElapsedMilliseconds > maxMs) goto done;`
- `NavMeshCleanup.cs:1585` `if ((t & 1023) == 0 && watch.ElapsedMilliseconds > maxMs) goto done;`
- `NavMeshCleanup.cs:1920` `if (watch.ElapsedMilliseconds > maxMs) return;`

A repo-wide search for every `ElapsedMilliseconds`/`ElapsedSeconds`/`ElapsedTicks`/`GetTimestamp` shows all other sites are **assignments into log strings or result fields**, never branches that alter geometry. Evidence:

- `TiledNavMeshBuilder.cs:1488-1493, 1543, 5902` — `result.TilesMs/MergeMs/TotalMs/FaceQualityMs`; `:2982, :5028, :5188-5195` — log strings; `:3853, :4125, :4151` — `r.ElapsedMs` per tile.
- `SceneNavMeshExporter.cs` (30 sites), `TerrainNavMeshGenerator.cs`, `SceneCollisionGeometry.cs`, `SceneGeometryProbe.cs`, `SceneTerrainSampler.cs`, `VoxelNavMeshGenerator.cs:135` — all log/measurement.
- `Nmg8Writer.cs` — writes only verts/edges/faces; **no timing field reaches the `.bin`** (read `Nmg8Writer.cs:18-57`).

Two extra comparisons exist and are **reporting-only**, so they do not contradict the plan's *intent* but do contradict its *wording*:

- `TiledNavMeshBuilder.cs:1184-1185` `if (r.ElapsedMs < tileMsMin) …; if (r.ElapsedMs > tileMsMax) …` — min/max reduction for the `tileMs min/avg/max` log line (`:1546`).
- `TiledNavMeshBuilder.cs:1096` `emit = done == tileCount || elapsed - lastProgressMs >= 2000;` — a 2 s log throttle, explicitly commented at `:1050-1052` as observational. It gates a `log(...)` call only.

So: **the plan's operative claim is correct** — deleting the four gates removes the only output-affecting wall-clock dependence in the pipeline. The absolute wording ("the only elapsed-time comparisons") is inaccurate; the two `tileMs` comparisons and the progress throttle are additional elapsed comparisons. Low risk (the plan already scopes the deadline as diagnostic-only), but worth tightening so A17's grep does not later look wrong.

---

## Claim 2 — sweep caps make three of four budgets derived; only Decimate binds; counter sites/C0 derivations match

**Verdict: counter sites and C0 derivations CONFIRMED. The "three of four are derived, only Decimate binds" characterization is REFUTED.**

### Confirmed

Sweep caps are all present and match the plan: `Decimate` `sweep < 16` (`:382`), `RefineLongThin` `sweep < 8` (`:1572`), `RelaxThin` `sweep < 3` (`:1354`), `FlipEdges` `iter < 6` (`:1913`).

Counter sites match the loops:
- `Decimate`: `st.CandVerts++` at `:457` immediately precedes `TryRemoveVertex` at `:458` — the plan's `guardEvals++` beside it is correct, and it counts exactly the guard evaluations.
- `RelaxThin`: `TryRelax` at `:1381`; `RefineLongThin`: inner loop `for (int t = 0; t + 2 < tris.Count && splits < maxSplits; t += 3)` at `:1583` with the gate as its first statement; `FlipEdges`: `foreach (long key in keys)` at `:1918` with the gate as its first statement. All correct.

`C0` derivations:
- **Decimate mask pre-computable — CONFIRMED.** The mask at `:422-433` reads only `v`, `tris`, `csrVerts`. `v` is snapshotted at `:377`, `tris` is untouched before the loop, and `ringStart` is built at `:366`; `csrVerts = ringStart.Length - 1` (`:386`) is therefore available before `for (int sweep…)` at `:382`. No dependency on `faceComp`/`compSize`. One extra `O(F)` scan is the only cost.
- **RelaxThin `IncidentSliver` superset — CONFIRMED.** `IncidentSliver` (`:488-503`) is a pure read over `v/tris/start/faces`; the CSR must be built once before the loop (`RelaxThin` currently builds it *inside* the sweep at `:1357`), which the plan does. `v = verts.ToArray()` at `:1353` is available.

### Refuted

The claim that three budgets are "derived bounds, not a new limiter" does not survive the arithmetic, because the plan's formulas are **tighter** than what the sweep caps actually imply:

| pass | plan budget | implied by sweep cap | binds first? |
|---|---|---|---|
| `FlipEdges` | `8·E0` | `6·E0` (one edge replaced per flip ⇒ edge count ~constant) | **No — strictly derived.** Plan is right here. |
| `RefineLongThin` | `8·F0 + F0/20` | `8·F0 + ~8·(F0/10)` (each split appends 1–2 faces that the *same* sweep then scans) | **Yes — budget is ~35 % tighter.** |
| `RelaxThin` | `2·C0 + 65536` | `3·C0` (3 sweeps × ≤ C0 calls) | **Yes whenever C0 > 65536** (Basamu-scale). |
| `Decimate` | `8·C0 + F0/2` | `16·C0` | **Yes** — as the plan says. |

The plan's own derivation for `RefineLongThin` ("one sweep scans F_start + splitsInSweep … so the sweep cap already implies `8*F0 + F0/20`") omits that the appended faces are rescanned in the same sweep and that up to `maxSplits` faces are appended **per pass, accumulating across sweeps**. Correct implied bound is ≈ `8·F0 + 8·(F0/20)`.

Consequence: the plan under-states that `RefineLongThin` and `RelaxThin` budgets can bind and therefore can themselves truncate work (and change quality) on large maps. The calibration in §3 must measure all four counters, not just the Decimate one — otherwise a quality regression could be caused by the Refine/Relax budgets while the operator only tunes `DecimateGuardMultiple`. (The plan does say to add all counters, so the instrumentation is there; the *characterization* and the "single knob" framing need correcting.)

---

## Claim 3 — the four report corrections

**Verdict: (a) CONFIRMED, (b) CONFIRMED (and broader), (c) CONFIRMED, (d) CONFIRMED.**

**(a) `MaxFanNormalSpreadDegrees` is inert — CONFIRMED.**
`NavMeshCleanup.cs:740-742`:
```
double na = AngleDeg(refN, n);          // :740
if (na > 90) na = 180 - na;             // :741
if (na > MaxFanNormalSpreadDegrees) return false;   // :742, value 120 (:40)
```
`AngleDeg` (`:2071-2079`) clamps `cosA` to `[-1,1]` and returns `acos·180/π ∈ [0,180]` (or `180` on a degenerate normal). After `:741`, `na ∈ [0,90]`, so `na > 120` is unreachable. `FanPlanar` can therefore only reject via `return refN != null` (`:744`, all-degenerate fan). Only two references to the constant exist (`:40` def, `:742` use) — confirmed by grep.

**(b) `FaceSlopeDeg` / `MaxCleanupSlopeDegrees` are dead — CONFIRMED, and `TriQuality` too.**
Repo-wide grep for each symbol returns **only the definition**:
- `FaceSlopeDeg` — `:1852` only.
- `MaxCleanupSlopeDegrees` — `:60` only.
- `TriQuality` — `:1041` only (the *used* function is the distinct `TriQuality2D` at `:1028`, called from `:977`).

**(c) FlipEdges stale-key set is 5 keys, diagonal deferred — CONFIRMED.**
`TryFlip` (`:1961-1989`) rewrites `f0=(u,x,w)`, `f1=(u,w,y)` into `(x,y,u)`, `(x,y,w)`. Topology delta: shared `(u,w)` 2→0; ring `(u,x),(x,w),(w,y),(y,u)` each −1 (the guard at `:1978-1979` requires all four ≥2 before allowing the flip, and the manifold invariant caps them at 2, so each drops to exactly 1); diagonal `(x,y)` 0→2. That is **five keys leaving, one entering**. `edgeFaces` is the round-start snapshot and is only rebuilt at `:1929` after the round, and `TryFlip` never mutates it (only `TryGetValue`/`UseCount`), so it is safe to filter a live copy. The diagonal cannot be picked up in the same round because `keys` is a fixed list per iteration (`:1916-1917` today), so **deferring it to the next round is required for decision-identity**. Confirmed.

**(d) `touched[]` is latent, not live — CONFIRMED.**
`nf = tris.Count / 3` is read once at `:1911`; `TryFlip` writes only `tris[f0*3..]` / `tris[f1*3..]` (`:1983-1986`) and nothing in `FlipEdges` appends/removes faces, so `nf` is invariant and the reset at `:1930` is correct today. The plan's "latent, not live" label is accurate.

---

## Claim 4 — S0–S10 sequencing and calibration are internally consistent

**Verdict: REFUTED — four defects, two of them blocking.**

1. **Test28 is scheduled twice / out of order (blocking).** §1.7(a) (`DeterminismImplementationPlan.md:597`) says the self-test "must be added **before** the gates are deleted (S2) so it can be observed failing first", but the order-of-operations table puts "self-test + goldens" at **S8** (`:848`), while S2's verification (`:842`) asserts "`Test28` (§1.7a) must now **PASS**". S2 cannot be verified as written unless Test28 is created before S2. Fix: move the Test28 *creation* (not the goldens) to S1 or a new S1b, or reword S2 to use a different observable.

2. **S0's command line cannot run (blocking).** S0 (`:840`) says "`--quality` + `--xzprobe`". In `SnapshotMode`, `--xzprobe` early-returns at `Program.cs:1193` and `--quality` early-returns at `:1341`; xzprobe wins and `--quality` never prints. They must be two separate invocations. (The §2.2 capture script correctly uses `--quality` alone, so only S0's prose is wrong.)

3. **S2/A17's "grep returns zero" contradicts the plan's own deadline.** S2 (`:842`) and A17 (`:662`) require a repo-wide grep for `ElapsedMilliseconds >` to return zero, but §1.2's `Deadline.Poll` (`:198`) is exactly `_w.ElapsedMilliseconds > _budgetMs`. After S1+S2 the grep is non-zero. Fix: scope the grep to exclude the `Deadline` class, or assert "no comparison whose result feeds control flow".

4. **S9 has no mechanism to vary the budgets (blocking for the calibration).** §3.2 (`:737`) requires runs with `Budgets.DeadlineMs = 0`, `Budgets.DecimateGuardMultiple = 1000000`, and `DecimateSweepCap = 1`/`2`, but the plan defines no CLI flag and no settings field to set them at runtime; the harness can only pass `Budgets` if a new flag is added. S9 is not executable as written.

**Non-blocking:** the file table (`:97`) labels the repo path `tools/TiledCoreHarness/Program.cs` as "(temp fixture project)"; the two harness copies are byte-identical today, so the anchors hold, but the plan should name one canonical location. The "S1 before S2 / S6 before S7 / S8 before S9 / baseline after the roof fix" *rationales* are sound and mutually consistent — the defects are scheduling/mechanism details, not conceptual.

---

## Claim 5 — missed nondeterminism sources

**Verdict: no missed source that affects the MESH beyond the four gates. Four secondary items worth naming; the plan's un-audited `TiledNavMeshBuilder` map iterations all check out.**

I re-audited the 8 `Dictionary`/`HashSet` iterations in `TiledNavMeshBuilder.cs` that the research report never examined (it only audited `NavMeshCleanup.cs`). All are order-independent:

- `:1134` merge of per-tile obstructions into `AddRaw` — OR-combines kinds (`:189-198`), commutative/idempotent; the map is later only probed via `Contains`/`SegmentCrosses`.
- `:1614` `roots = areaSum.Keys` then `roots.Sort(...)` with a total-order comparer (area desc, faceCount desc, index asc) — deterministic; reporting only.
- `:2191` sets `dropped[kv.Key] = true` per key — order-independent.
- `:4895` collects `bad` keys then `bad.Sort()` — deterministic.
- `:5393`/`:5404` `RestoreSplitComponents` — iterates `candsOfComp` in hash order, but each iteration builds its own `fragRoot`/`frags`/`cands` (`:5407-5435`) and `drop[best]=false` (`:5490`) touches only faces of its own component, which are disjoint across components. Order-independent (verified by reading `:5404-5494`).
- `:5688` `ScanSelfOverlaps` — builds a `List<int> drop` in hash order, but `MarkSelfOverlaps` (`:5590-5595`) only sets `drop[f]=true` from it and `seen` (`:5687`) dedups; the resulting boolean set is order-independent. (Reachable only via `--scan`, and via `result.OverlappingFacesAny` with `drop == null`, `:5868`.)
- `:5856` counts boundary/`maxMult` — reductions, order-independent.

`NavMeshCleanup`'s 8 map iterations likewise check out (set flags, min-reductions, or `.Sort()`), and `LongHash` is a bijective finalizer. `Nmg8Writer.BuildEdges` builds edges in first-encounter order and never iterates the map. **So the plan's central premise holds: the four gates are the only mesh-affecting nondeterminism.**

Secondary items the plan does **not** cover:

1. **Culture-sensitive log formatting (log reproducibility, not mesh).** `TiledNavMeshBuilder.cs` formats many log values with `.ToString("F2")` etc. without `InvariantCulture` (e.g. `:143`, `:1635-1641` uses `ic`; but the tile/island/log helpers at `:2982`, `:5028`, `:5188`, `:6149` are mixed). Under a non-invariant locale two runs produce different log text. The plan fixes only `DecimDiag` (`§1.4`); if "artefact runs are reproducible" is meant to include the run log, this is a broader fix than the plan scopes. Mesh `.bin` is unaffected (binary; `Nmg8Writer` has no `ToString`).
2. **`RcBuilder.BuildMultiThread` is a latent trap.** `ThirdParty/DotRecast/Recast/RcBuilder.cs:83-133` enqueues tile results into a `ConcurrentQueue` and returns `results.ToList()` (`:131`) — **queue order, i.e. nondeterministic**. It is currently **dead code**: the only reference to `RcBuilder` in the product is `RcConfig.CalcBorder` (`TiledNavMeshBuilder.cs:859`); the tiled builder uses `Parallel.For` with per-index slots (`:1045`, `:1072-1085`). Worth a comment/guard so a future wiring does not silently reintroduce nondeterminism.
3. **`NavMeshPrefabIndex.cs`** iterates a `HashSet<string>` and reflects module lists (`:131`, `:780`) — editor-side name→module resolution, not mesh output, but it can affect which module a prefab lands in. Outside the plan's stated scope; flag only.
4. **`DateTime` in paths:** the only timestamp in an artifact **filename** is `NavMeshLog.cs:112` (`NavMesh_yyyyMMdd_HHmmss.log`), which the plan's §1.4 seam covers. The snapshot sidecar `.txt` carries `date:`/`machine:` (`SceneNavMeshExporter.cs:735`), which the plan already treats as golden-hash context, not an assertion target. `TerrainNavMeshGenerator.cs:1126` builds `prefabName + ".obj"` with no timestamp. So no uncovered timestamp-in-path.

---

## Bottom line

| # | claim | verdict |
|---|---|---|
| 1 | four gates are the only elapsed-time comparisons | **CONFIRMED** (output-affecting); wording imprecise — 2 reporting comparisons + 1 log throttle exist |
| 2 | counter sites / C0 derivations match the loops | **CONFIRMED** |
| 2 | three of four budgets are derived, only Decimate binds | **REFUTED** — only FlipEdges is derived; Refine and Relax budgets are tighter than their sweep caps and can bind |
| 3a | `MaxFanNormalSpreadDegrees` inert | **CONFIRMED** |
| 3b | `FaceSlopeDeg`/`MaxCleanupSlopeDegrees` dead | **CONFIRMED** (+ `TriQuality` also dead) |
| 3c | 5 stale keys, diagonal deferred | **CONFIRMED** |
| 3d | `touched[]` latent | **CONFIRMED** |
| 4 | S0–S10 internally consistent | **REFUTED** — Test28 scheduled at S8 but required at S2; S0 combines mutually exclusive flags; S2/A17 grep contradicted by the deadline; S9 has no budget-setting mechanism |
| 5 | missed nondeterminism sources | **No mesh-affecting miss found**; 4 secondary items named (culture-sensitive logs, dead `BuildMultiThread` trap, prefab-index reflection, timestamps — the last already covered) |

The plan's core diagnosis and its four corrections are accurate and safe to build on. The two things that must be fixed before execution are the **Test28 scheduling contradiction** and the **Refine/Relax budget characterization** (which affects what the calibration actually controls); the S0 flag conflict and the S9 missing knob are mechanical but will stop a literal execution.
