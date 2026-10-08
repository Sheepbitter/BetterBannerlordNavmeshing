# Deterministic cleanup — implementation plan

BetterBannerlordNavmeshing · `NavMeshCleanup.cs` (C# 7.3 / net472, Recast-style tiled voxel pipeline)
Planning subagent, **read-only on the repo, no commits**. Plan derived from `DeterminismResearch.md`
(= `…\det_research\deterministic-mesh-cleanup-report.md`) and verified line-by-line against the current
tree. Every anchor below is a line number in the tree as it stands today.

**Model/provider:** `opencode` / `space-bunny-free` (OpenCode Space Bunny Free), session
`ses_eeb4508deffenW7gqSKszDAf7d`.
**v2 revision:** `opencode-go` / `longcat-2.5-preview-free` (LongCat 2.5 Preview Free). This v2 applies
the corrections from the independent validation run (`determinism-plan-validation.md`, reproduced in
§6.1): the Test28 scheduling contradiction, the S0 flag split, the A17/S2 grep scope, the S9
runtime-override mechanism, the Refine/Relax budget re-characterisation, the Claim-1 scoping note, the
canonical harness location, and the four secondary audit items (§0.6). Everything else is unchanged.

---

## 0. Scope, non-goals, and two findings that change the research report

### 0.1 Scope

Make `NavMeshCleanup` output a function of its input only, so the same input yields byte-identical
`verts` / `tris` / `QuadPairs` on any machine with the same binary + runtime. That means: delete the
four wall-clock gates, size the work with counted work units, make the log text reproducible, and take
`Math.Acos` off the decision path.

The four gates are the **only output-affecting** elapsed-time comparisons in the pipeline. For
completeness the other elapsed comparisons are listed here so a later grep does not look like it
missed something (see the Claim-1 scoping note in §1.1 and the A17 criterion in §2.1):

* `TiledNavMeshBuilder.cs:1184-1185` — `r.ElapsedMs < tileMsMin` / `r.ElapsedMs > tileMsMax`, a
  min/max reduction for the `tileMs min/avg/max` log line (`:1546`). Reporting only.
* `TiledNavMeshBuilder.cs:1096` — `elapsed - lastProgressMs >= 2000`, a 2 s throttle on the progress
  log line, explicitly documented as observational at `:1050-1052`. Gates a `log(...)` call only.
* `TiledNavMeshBuilder.cs:1488-1493, 1543, 5902` and ~30 `SceneNavMeshExporter.cs` sites — assignments
  into `result.*Ms` / log strings.

Beyond the four gates, four secondary items are in scope for this round or flagged here (§0.6).

### 0.2 Non-goals (do not do these in this round)

* **No restructure of `Decimate` into QEM priority decimation.** It would invalidate every number in
  `README.md`'s sliver-cleanup table and needs its own determinism proof. Separate change, later.
* **Do not change any quality threshold.** `SurfaceToleranceMeters`, `MaxFanNormalSpreadDegrees`,
  `QuadMergeFoldMaxDegrees`, `QuadConvexMaxInteriorAngleDegrees`, `QuadXzMaxInteriorAngleDegrees`,
  `MaxCleanupSlopeDegrees`, `FlipNormalAngleDegrees`, … all stay at their current values. This round
  changes *how much work happens*, never *what the answer is*.
* **Do not parallelise guard evaluation.** Deterministic, but a separate change with its own proof.
* **Do not lower the hard sweep caps** (16 / 8 / 3 / 6 stay, per the assignment). They are the escape
  hatch if calibration shows a regression, applied in a follow-up, not here.

### 0.3 FINDING A — `MaxFanNormalSpreadDegrees` is inert; `FaceSlopeDeg` is dead code

Two things the research report lists as decision-path trig do not exist as such. Verified by reading
the code and by a repo-wide grep (`*.cs`, whole repo, plus the temp harness):

`FanPlanar`, `NavMeshCleanup.cs:740-742`:

```csharp
double na = AngleDeg(refN, n);      // acos ∈ [0,180]
if (na > 90) na = 180 - na;         // folds to [0,90]
if (na > MaxFanNormalSpreadDegrees) return false;   // 120 -> unreachable
```

After the fold `na ≤ 90`, so `na > 120` **can never be true**. `FanPlanar` rejects only via
`return refN != null` (all faces degenerate). The guard the README relies on ("a fan whose normals
spread more than `MaxFanNormalSpread` is never touched, so two levels over the same XZ are never
joined", README:25-26) is not enforced as written. Stacked levels *are* protected in practice — by the
60° facing test (`NavMeshCleanup.cs:657-658`), `RingSimpleXz` (871), and `MaxHeightChangeMeters` (669) —
so nothing is currently broken; but the documented rationale is wrong.

Dead (defined, zero callers, whole repo): `FaceSlopeDeg` (1852-1860), `MaxCleanupSlopeDegrees` (60),
`TriQuality` (1041-1047), `Stats.EdgeHist` (145).

**Consequence for this plan:** the cos-space conversion list (§1.5) contains **no**
`MaxFanNormalSpreadDegrees` / `FaceSlopeDeg` entry, because there is nothing to convert. Deleting the
dead members changes nothing observable and is in scope as a cleanup; *fixing* `FanPlanar` (making the
spread test mean what the README says) **changes output and is out of scope** — raise it separately,
and correct the README prose in the same follow-up that makes the code match.

### 0.4 FINDING B — the `FlipEdges` stale-key set is 5 keys per flip, not 2

`TryFlip` (1961-1989) rewrites `(u,x,w)`/`(u,w,y)` into `(x,y,u)`/`(x,y,w)`. Per successful flip:

| edge | before (uses) | after (uses) | still a flip candidate? |
|---|---|---|---|
| `(u,w)` shared | 2 | 0 | no — gone |
| `(u,x)`, `(x,w)` | 2 (or 1) | 1 (or 0) | no |
| `(w,y)`, `(y,u)` | 2 (or 1) | 1 (or 0) | no |
| `(x,y)` diagonal | 0 | 2 | yes, but **only next round** in today's code |

So five keys must leave the live list per flip, and the new diagonal must **not** be added within the
same round (today it can only appear via the next round's rebuild — adding it early would change the
result). Filtering all five unconditionally is safe: a key that had 1 use was never a candidate anyway
(`TryFlip` is reached only when `fl.Count == 2`, line 1922), and one that had 2 now has 1.

### 0.5 FINDING C — `touched[]` is not currently a bug

`nf` is read once at `NavMeshCleanup.cs:1911` and `TryFlip` never adds or removes faces (it rewrites
face *contents*, lines 1983-1986), so `nf` is invariant across the six iterations and the reset at
line 1930 is correct today. The report's concern is **latent**: it becomes a real out-of-range write the
moment any future edit appends a face. Spec'd in §1.6 as a cheap hardening, labelled as such.

### 0.6 Secondary items from the independent audit

The validation run (`determinism-plan-validation.md`) re-audited the `TiledNavMeshBuilder.cs` map
iterations the research report never examined and found **no mesh-affecting nondeterminism beyond the
four gates** (all eight are order-independent: `:1134` commutative OR-merge, `:1614` sorted total
order, `:2191` flag-set, `:4895` sorted, `:5393`/`:5404` disjoint per-component, `:5688` boolean set via
`seen`, `:5856` reductions). It did surface four secondary items, handled as follows:

1. **Culture-sensitive log formatting beyond `DecimDiag` — in scope, but log-only.** Many log values in
   `TiledNavMeshBuilder.cs` (e.g. `:2982`, `:5028`, `:5188-5195`) format with `.ToString("F2")` without
   `InvariantCulture`. The mesh `.bin` is unaffected (`NmgWriter` is binary; the `.obj` writers already
   use `InvariantCulture` + `"R"`, `TerrainNavMeshGenerator.cs:872-923`, `VoxelNavMeshGenerator.cs:217-238`).
   Add to the §1.4 diagnostics pass: sweep the cleanup-related log formatting to
   `CultureInfo.InvariantCulture`. Broader whole-file conversion is optional; if "reproducible artefact
   runs" is meant to include the run log under a non-invariant locale, it is required.
2. **`RcBuilder.BuildMultiThread` nondeterministic-order trap — add a guard/comment as a step.** It
   returns `results.ToList()` from a `ConcurrentQueue` (`ThirdParty/DotRecast/Recast/RcBuilder.cs:83-133`,
   esp. `:131`), i.e. completion order. It is **dead code** today (the only product reference to
   `RcBuilder` is the static `RcConfig.CalcBorder` at `TiledNavMeshBuilder.cs:859`; the tiled builder uses
   `Parallel.For` with per-index slots at `:1045`/`:1072-1085`). Add a comment at `RcBuilder.cs:83` and a
   `[Obsolete]`/assert that it must not be wired into the product build without a canonical sort — see
   step S5b in §5.
3. **`NavMeshPrefabIndex` hash-order reflection — outside mesh scope, flagged.** `NavMeshPrefabIndex.cs`
   iterates a `HashSet<string>` (`:131`) and reflects module lists (`:780`); this is editor-side
   name→module resolution and can affect which module a prefab lands in, not the mesh. Flagged for a
   separate editor-side review; not touched here.
4. **Timestamps in paths — already covered.** The only timestamp in an artefact *filename* is
   `NavMeshLog.cs:112` (`NavMesh_yyyyMMdd_HHmmss.log`), covered by the §1.4 clock seam. The snapshot
   sidecar `.txt` carries `date:`/`machine:` (`SceneNavMeshExporter.cs:735`), already treated as
   golden-hash context (§1.7b). `TerrainNavMeshGenerator.cs:1126` has no timestamp in the `.obj` name.

---

## 1. Exact edits, per file, per pass

Files touched:

| file | role |
|---|---|
| `NavMeshCleanup.cs` | all four gates, budgets, counters, deadline, cos conversions, `FlipEdges` |
| `TiledNavMeshBuilder.cs` | settings field (`:307`), call site (`:1411`), cleanup log line (`:1425-1440`), runtime override plumbing (§1.3.1) |
| `NavMeshLog.cs` | clock seam (`:60`, `:112`, `:153`) |
| `tools/TiledCoreHarness/Program.cs` | **canonical harness location.** Call site (`:7721`), CLI overrides (§1.3.1), self-test (§1.7a), goldens (§1.7b). The temp copy at `…\Temp\opencode\tiled-core-harness\Program.cs` is byte-identical today (SHA-256 `B7A41AA1…A61AA`, both 488,495 bytes); edit `tools/TiledCoreHarness` and mirror, or run from whichever is wired into the csproj — do not let the two drift. |
| `ThirdParty/DotRecast/Recast/RcBuilder.cs` | dead-code guard comment on `BuildMultiThread` (§0.6 item 2, step S5b) |
| `README.md`, `TODO.md`, `DeterminismResearch.md` | contract wording (S10) |

### 1.1 Pass A — delete the four wall-clock gates and the `Stopwatch` threading

`NavMeshCleanup.cs`, signature at 152-154 and body 156-211:

```csharp
// 152-154  BEFORE
// vertsXyz: flat x,y,z. tris: flat indices. maxMilliseconds bounds the decimation + flips.
public static Stats Run(List<float> vertsXyz, List<int> tris, int maxMilliseconds,
    bool quadMerge = true)
{
    var st = new Stats();
    var watch = System.Diagnostics.Stopwatch.StartNew();
```

```csharp
// AFTER
// vertsXyz: flat x,y,z. tris: flat indices.
// `b.DeadlineMs` is DIAGNOSTIC ONLY: it can set Stats.DeadlineExceeded and it is logged, but no
// pass branches on it and nothing in the output depends on it. The output is a function of
// (vertsXyz, tris, b) alone. `b.DeadlineMs` <= 0 disables the warning.
public static Stats Run(List<float> vertsXyz, List<int> tris, Budgets b, bool quadMerge = true)
{
    var st = new Stats();
    st.Deadline = new Deadline(b.DeadlineMs);
```

Then in the body:

| line | delete | replace with |
|---|---|---|
| 176-180 | `runStart`, the `refineUsed` cascade, the elapsed-time deadline expression | `if (EnableRefine) RefineLongThin(vertsXyz, tris, st, b);` |
| 187 | `Decimate(vertsXyz, tris, st, (int)(maxMilliseconds + refineUsed), watch);` | `Decimate(vertsXyz, tris, st, b);` |
| 192 | `RelaxThin(…, (int)(maxMilliseconds + 1500 + refineUsed), watch);` | `RelaxThin(vertsXyz, tris, st, b);` |
| 196 | `FlipEdges(v, tris, st, (int)(maxMilliseconds + 2500 + refineUsed), watch);` | `FlipEdges(v, tris, st, b);` |
| 173-175 | the comment explaining the cascade | rewritten: "the passes are bounded by counted work units (§1.3), not by a clock; there is no cascade between them" |

The cascade comment at 168-175 must go with it: it is the single most important reason the clock was
harmful (a slow refine shifted every later deadline), and leaving it would mislead the next reader.

**Gate deletions, one per pass** (this is the whole determinism fix):

| pass | line | current text | new text |
|---|---|---|---|
| `Decimate` | 443 | `if ((vert & 255) == 0 && watch.ElapsedMilliseconds > maxMs) { timeUp = true; break; }` | `if (guardEvals >= guardBudget) { budgetUp = true; break; }` (§1.3) |
| `RelaxThin` | 1363 | `if ((vert & 255) == 0 && watch.ElapsedMilliseconds > maxMs) goto done;` | `if (relaxEvals >= relaxBudget) goto done;` |
| `RefineLongThin` | 1585 | `if ((t & 1023) == 0 && watch.ElapsedMilliseconds > maxMs) goto done;` | `if (faceVisits >= faceVisitBudget) goto done;` |
| `FlipEdges` | 1920 | `if (watch.ElapsedMilliseconds > maxMs) return;` | `if (edgeVisits >= edgeVisitBudget) return;` |

The `&255` / `&1023` poll strides go away with the clock: the counters make the check exact, so there is
no reason to sample. Each replacement also renames the local flag `timeUp` → `budgetUp` (declared at
384, consumed at 476-479 — the commit path is unchanged, so a budget-truncated sweep is still
`CompactFaces`-ed exactly like a clock-truncated one is today).

**Scope of "the only elapsed comparison".** After this pass the only elapsed comparison anywhere in the
pipeline whose result feeds control flow is the diagnostic `Deadline.Poll` (§1.2), and it only sets a
log flag. The two reporting-only `tileMs` comparisons (`TiledNavMeshBuilder.cs:1184-1185`) and the 2 s
progress-log throttle (`:1096`) are intentionally left alone. The A17 grep criterion (§2.1) is scoped to
exclude the `Deadline` class and those two reporting sites, rather than asserting a literal zero.

Signature changes to match:

```csharp
// 361-362
private static void Decimate(List<float> verts, List<int> tris, Stats st, Budgets b)
// 1349-1350
private static void RelaxThin(List<float> verts, List<int> tris, Stats st, Budgets b)
// 1566-1567
private static void RefineLongThin(List<float> verts, List<int> tris, Stats st, Budgets b)
// 1907-1908
private static void FlipEdges(float[] v, List<int> tris, Stats st, Budgets b)
```

**Compatibility overload.** `Program.cs:7721` (`SelectionQuads`) and `TiledNavMeshBuilder.cs:1411` pass
an `int` third argument. Keep a 4-arg overload so the diff at both sites is a rename of the third
argument's *meaning*, not a signature migration:

```csharp
public static Stats Run(List<float> vertsXyz, List<int> tris, int deadlineMs, bool quadMerge = true)
{ return Run(vertsXyz, tris, Budgets.Default(deadlineMs), quadMerge); }
```

### 1.2 Pass B — one diagnostic deadline that cannot change output

There is no `Stopwatch` left threaded through any pass, so the deadline rides on `Stats`, which every
pass already receives.

```csharp
// inside Stats (NavMeshCleanup.cs:117-150)
internal Deadline Deadline;
public bool DeadlineExceeded;   // log only - see the equality-assert carve-out in §1.7
public long DeadlineBudgetMs;
public long CleanupMs;          // whole-run measurement; log only
public long RefineMs, DecimateMs, RelaxMs, FlipMs, QuadMergeMs;   // log only (TODO item 6)

private sealed class Deadline
{
    private readonly System.Diagnostics.Stopwatch _w = System.Diagnostics.Stopwatch.StartNew();
    private readonly long _budgetMs;
    private bool _tripped;
    public Deadline(long budgetMs) { _budgetMs = budgetMs; }
    public bool Tripped { get { return _tripped; } }
    public bool Poll()
    {
        // Sets a flag and nothing else. The return value is deliberately NOT used by any pass.
        if (!_tripped && _budgetMs > 0 && _w.ElapsedMilliseconds > _budgetMs) _tripped = true;
        return _tripped;
    }
}
```

Poll sites (all three are **statements whose value is discarded** — this is what makes the deadline
provably output-neutral):

* `Run`, after each pass (four sites): `st.Deadline.Poll();`
* `Decimate`, replacing the old gate's stride: `if ((vert & 1023) == 0) st.Deadline.Poll();`
* `RefineLongThin`: `if ((t & 4095) == 0) st.Deadline.Poll();`
* `FlipEdges`: inside the edge loop, `if ((edgeVisits & 4095) == 0) st.Deadline.Poll();`

`Run` finishes with `st.DeadlineExceeded = st.Deadline.Tripped; st.CleanupMs = …;`.

If a pass trips the deadline, the only effect is one extra log line (see §1.4). Nothing is truncated,
nothing is skipped, no counter changes.

**Per-pass `*Ms` stopwatches are not deadlines.** They are `var w = Stopwatch.StartNew()` created inside
each pass and read into a `long` field; they cannot influence anything. State that distinction in a
comment so a future reader (or a grep for `Stopwatch`) does not mistake them for the removed gates.

### 1.3 Pass C — work-unit budgets, per pass

#### Settings vs build constants

Line of demarcation: **anything a user could legitimately trade quality for time is a setting; anything
that encodes a termination/ordering property is a build constant.**

| constant | current | new home | default | class | why |
|---|---|---|---|---|---|
| `CleanupMaxMs` | `TiledNavMeshBuilder.cs:307` | `Budgets.DeadlineMs` (diagnostic) | `6000` | setting (`TiledNavMeshSettings`), renamed `CleanupDeadlineMs` | no longer bounds anything; it only warns |
| — | — | `CleanupDecimateGuardMultiple` (new settings field beside it) | `8` | setting | the shipped quality/time knob; the calibration's primary output. It is not the *only* budget that can bind — see the "which budget binds" note below the budget table and §1.3.1. |
| — | literal `16` at `382` | `Budgets.DecimateSweepCap` | `16` | build constant | hard outer bound |
| — | literal `8` at `1572` | `Budgets.RefineSweepCap` | `8` | build constant | hard outer bound (**unchanged**; the report's 5 is a calibration lever, off by default) |
| — | literal `3` at `1354` | `Budgets.RelaxSweepCap` | `3` | build constant | hard outer bound |
| — | literal `6` at `1913` | `Budgets.FlipIterationCap` | `6` | build constant | hard outer bound |
| `RefineMaxSplitsFactor` | `:1560` | stays `public const int` | `20` | build constant | already a named const; `faces/20` split budget |
| — | new | `Budgets.DecimateGuardFloorDivisor` | `2` | build constant | the `F0/2` floor term's denominator |
| — | new | `Budgets.RelaxBudgetFloor` | `65536` | build constant | floor term so tiny meshes still converge |
| — | new | `Budgets.FlipEdgeVisitMultiple` | `8` | build constant | the `8 * E0` term |
| — | new | `Budgets.DecimateGuardMultiple` | `8` | build constant (mirror of the setting) | the pass reads it from `Budgets`; `CleanupDecimateGuardMultiple` is what populates it |

```csharp
public struct Budgets
{
    public int DeadlineMs;                 // diagnostic only
    public int DecimateSweepCap, RefineSweepCap, RelaxSweepCap, FlipIterationCap;
    public int DecimateGuardMultiple, DecimateGuardFloorDivisor;
    public int RelaxBudgetFloor;
    public int FlipEdgeVisitMultiple;

    public static Budgets Default(int deadlineMs)
    {
        return new Budgets {
            DeadlineMs = deadlineMs,
            DecimateSweepCap = 16, RefineSweepCap = 8, RelaxSweepCap = 3, FlipIterationCap = 6,
            DecimateGuardMultiple = 8, DecimateGuardFloorDivisor = 2,
            RelaxBudgetFloor = 65536, FlipEdgeVisitMultiple = 8,
        };
    }
}
```

C# 7.3 note: a struct gets no implicit parameterless constructor, so `Default()` is a static factory
returning a fully-initialised instance — as written. Every member is initialised, so no
`default(Budgets)` path can silently mean "zero sweeps".

#### The four budgets

Let **F0** = faces at pass entry, **E0** = keys in the round-0 sorted key array of `FlipEdges`,
**C0** = the candidate set the pass's *own input-derived mask* selects. **C0 must be computable before
the pass runs**, otherwise the "budget" is circular — see each row.

| pass | work unit | counter (incremented where) | budget formula | hard outer bound | role |
|---|---|---|---|---|---|
| `RefineLongThin` | one face evaluation in the inner scan | `faceVisits++` as the **first statement** of the loop body at 1583, replacing the goto line | `faceVisits <= RefineSweepCap * F0 + maxSplits`, where `F0 = tris.Count/3` at entry (1569) and `maxSplits = F0 / RefineMaxSplitsFactor` | `sweep < RefineSweepCap` **and** the existing `splits < maxSplits` | **CAN BIND — not a strictly derived bound.** A split appends 1–2 faces that the *same* sweep rescans, and up to `maxSplits` are appended across the pass. The sweep cap alone implies ≈ `8*F0 + 8*(F0/20)` (`8*F0 + 0.4*F0`); this budget is `8*F0 + F0/20` (`8*F0 + 0.05*F0`) — about **35 % tighter**. It binds on a mesh whose splits land late in the sweeps. |
| `Decimate` | one guard evaluation = one `TryRemoveVertex` call | `guardEvals++` immediately before the call at 458, i.e. **beside the existing `st.CandVerts++` at 457** — the two counters are the same event and must stay adjacent | `guardEvals <= DecimateGuardMultiple * C0 + F0 / DecimateGuardFloorDivisor` | `sweep < DecimateSweepCap` **and** `!any` → `break` (484) | **expected to be the DOMINANT limiter** — it replaces the gate that was actually truncating. Not the *only* budget that can bind (see the Refine/Relax rows). |
| `RelaxThin` | one `TryRelax` call | `relaxEvals++` immediately before the call at 1381 | `relaxEvals <= 2 * C0 + RelaxBudgetFloor` | `sweep < RelaxSweepCap` and `!any` → `break` (1383) | **CAN BIND when `C0 > RelaxBudgetFloor` (65536).** 3 sweeps × ≤ C0 calls implies ≤ `3*C0`; this budget is `2*C0 + 65536`, so for C0 > 65536 the budget is the binding limit (Basamu-scale). The floor term keeps small meshes converging. |
| `FlipEdges` | one edge considered in the round scan | `edgeVisits++` as the first statement of the `foreach (long key in keys)` body at 1918 | `edgeVisits <= FlipEdgeVisitMultiple * E0`, **cumulative across all iterations** | `iter < FlipIterationCap` | **strictly derived bound — the only one.** 6 rounds × ≤ E0 keys implies `6*E0 < 8*E0`, and the edge count is ~constant (a flip replaces one edge with one). Exists so the flip cost is explicit and survives a raised iteration cap. |

**Which budget binds — and therefore what the calibration must measure.** Only `FlipEdges` is a
strictly derived bound. `RefineLongThin` and `RelaxThin` are **tighter** than their sweep caps imply and
can truncate work on their own; `Decimate` is expected to be the dominant limiter but is not the sole
one. The "single knob" framing applies to `DecimateGuardMultiple` **only for time**; a quality
regression after this change could just as well be caused by the Refine or Relax budget. Therefore §3
MUST measure all four counters (`faceVisits`, `guardEvals`, `relaxEvals`, `edgeVisits`) plus per-pass
ms, and the acceptance table (§2.2) must show all four. Do not tune only `DecimateGuardMultiple` and
assume the rest are slack.

`C0` derivation (this is the part that has to be right, or the budget is circular):

* **`Decimate`, `C0 = decCand0`.** The candidate mask at 422-433 is a pure function of
  `(v, tris, csrVerts)` at sweep 0. `csrVerts = ringStart.Length - 1` is already known before the loop
  (built at 366, read at 386). Extract the mask into a helper and call it **once before the sweep loop**
  to get `decCand0`; the loop then keeps recomputing it per sweep exactly as today.
  ```csharp
  private static int BuildCandidateMask(float[] v, List<int> tris, int csrVerts, bool[] cand)
  {   // body lifted verbatim from 422-433; returns the number of true entries
  }
  ```
  One extra `O(F)` scan outside the loop is negligible against 16 `O(F)` rebuilds, and it makes the
  budget a function of the input rather than of the run.
* **`RelaxThin`, `C0 = relaxCand0`.** The set that reaches `TryRelax` (passes `IncidentSliver`, has ≥3
  incident faces, `FanPlanar`, a ≥3 ring) is not cheaply derivable. Use the cheap superset: count the
  vertices in `[0, nv)` for which `IncidentSliver(v, tris, ringStart, ringFaces, vert)` holds at entry,
  computed once before the sweep loop. That is one extra pass of the same shape as one sweep's scan, so
  it costs `O(V × fan)`.
  ```csharp
  // before the sweep loop in RelaxThin:
  int[] rs0, rf0; BuildFaceAdjacency(tris, out rs0, out rf0);
  int relaxCand0 = 0;
  for (int i = 0; i < nv; i++) if (IncidentSliver(v, tris, rs0, rf0, i)) relaxCand0++;
  ```
* **`RefineLongThin`, `C0 = F0`** (the report says so, and it is right: every face is a candidate).
* **`FlipEdges`, `E0 = keys.Count` at iteration 0** — i.e. every distinct edge key in the mesh, not just
  the interior ones, because the counter increments for every key the loop *walks*, including the
  boundary keys it skips at 1922.

Record on `Stats` so the log shows the budget next to the spend: `DecimateGuardEvals`,
`DecimateCand0`, `DecimateGuardBudget`, `DecimateBudgetHit` (0/1), `DecimateSweeps`,
`RefineFaceVisits`, `RefineFaceVisitBudget`, `RefineSweeps`, `RelaxEvals`, `RelaxCand0`,
`RelaxBudget`, `RelaxSweeps`, `FlipEdgeVisits`, `FlipEdgeVisitBudget`, `FlipE0`, `FlipIters`.
`Budgets` itself should be echoed into the log so a log line is self-describing.

Overflow: `F0 * 8` on a 2 M-face mesh is 16 M, well inside `int`. But compute the products in `long`
and clamp the result to `int.MaxValue`, so a pathological input cannot overflow the budget into a
negative number and disable the guard.

#### 1.3.1 Runtime override mechanism (required so S9 is executable)

The shipped knobs live on `TiledNavMeshSettings` (beside `CleanupSlivers` at `TiledNavMeshBuilder.cs:306`):

```csharp
public bool CleanupSlivers = true;
public int CleanupDeadlineMs = 6000;            // renamed from CleanupMaxMs; DIAGNOSTIC ONLY
public int CleanupDecimateGuardMultiple = 8;    // the shipped quality/time knob
// Calibration-only overrides. 0 (or <= 0 for the multiplier) = "use Budgets.Default()".
public int CleanupDecimateSweepCap = 0;
public int CleanupRefineSweepCap = 0;
public int CleanupRelaxSweepCap = 0;
public int CleanupFlipIterationCap = 0;
public int CleanupRelaxBudgetFloor = 0;
public int CleanupFlipEdgeVisitMultiple = 0;
public int CleanupDecimateGuardFloorDivisor = 0;
```

A single helper builds the effective budgets, so there is one place where a setting becomes a budget:

```csharp
// in TiledNavMeshBuilder (or NavMeshCleanup.Budgets.ApplyOverrides)
internal static NavMeshCleanup.Budgets BuildCleanupBudgets(TiledNavMeshSettings s)
{
    var b = NavMeshCleanup.Budgets.Default(s.CleanupDeadlineMs);
    b.DecimateGuardMultiple = s.CleanupDecimateGuardMultiple;
    if (s.CleanupDecimateSweepCap > 0) b.DecimateSweepCap = s.CleanupDecimateSweepCap;
    if (s.CleanupRefineSweepCap > 0) b.RefineSweepCap = s.CleanupRefineSweepCap;
    if (s.CleanupRelaxSweepCap > 0) b.RelaxSweepCap = s.CleanupRelaxSweepCap;
    if (s.CleanupFlipIterationCap > 0) b.FlipIterationCap = s.CleanupFlipIterationCap;
    if (s.CleanupRelaxBudgetFloor > 0) b.RelaxBudgetFloor = s.CleanupRelaxBudgetFloor;
    if (s.CleanupFlipEdgeVisitMultiple > 0) b.FlipEdgeVisitMultiple = s.CleanupFlipEdgeVisitMultiple;
    if (s.CleanupDecimateGuardFloorDivisor > 0) b.DecimateGuardFloorDivisor = s.CleanupDecimateGuardFloorDivisor;
    return b;
}
```

The `TiledNavMeshBuilder.cs:1411` call site becomes
`NavMeshCleanup.Stats cln = NavMeshCleanup.Run(cv, ct, BuildCleanupBudgets(s), s.QuadMerge);`.

The harness gets matching flags in the `SnapshotMode` argument loop (`Program.cs:897-994`, beside
`--cleanup` at `:934`); each `case` assigns the corresponding `s.` field:

| flag | sets |
|---|---|
| `--cleanup-deadline <ms>` | `s.CleanupDeadlineMs` |
| `--dec-guard-mult <n>` | `s.CleanupDecimateGuardMultiple` |
| `--dec-sweep-cap <n>` | `s.CleanupDecimateSweepCap` |
| `--refine-sweep-cap <n>` | `s.CleanupRefineSweepCap` |
| `--relax-sweep-cap <n>` | `s.CleanupRelaxSweepCap` |
| `--flip-iter-cap <n>` | `s.CleanupFlipIterationCap` |
| `--relax-floor <n>` | `s.CleanupRelaxBudgetFloor` |
| `--flip-visit-mult <n>` | `s.CleanupFlipEdgeVisitMultiple` |
| `--dec-guard-floor-div <n>` | `s.CleanupDecimateGuardFloorDivisor` |

With these, the S9 calibration commands are literal: `--dec-guard-mult 1000000 --cleanup-deadline 0`,
and `--dec-sweep-cap 1` / `--dec-sweep-cap 2` (§3.2). The harness must print the effective `Budgets`
in its `settings:` line so every captured log is self-describing.

### 1.4 Pass D — deterministic diagnostics

#### `DecimDiag` (`NavMeshCleanup.cs:434-440`)

Current string, with two wall-clock fields:

```csharp
st.DecimDiag = "decimate: start=" + watch.ElapsedMilliseconds + "ms budget=" + maxMs
    + "ms faces=" + (tris.Count / 3) + " csrVerts=" + csrVerts + " mask=" + maskCount;
```

Two problems: the ms fields, and that it is written **during** sweep 0, before `sweeps` and
`guardEvals` exist. Move the assignment to the end of `Decimate` (after the sweep loop, before the
method returns, covering all `break` paths by assigning once after the loop) and make it fully derived
from counters:

```
decimate: faces0=<F0> csrVerts0=<csrVerts0> cand0=<C0> guardBudget=<budget> sweeps=<n>/<cap>
  guardEvals=<n> budgetHit=<0|1> removals=<st.Decimated> rejMove= rejFan= rejRing= rejTopo= <st.RejMove>,<…>
```

All integers, no culture-sensitive formatting, no timing. `TiledNavMeshBuilder.cs:1440` prints it with
`Log(log, "cleanup: " + cln.DecimDiag)` — unchanged, so the call site does not move.

#### What `NavMeshLog` needs

`NavMeshLog.cs` writes `DateTime.Now` in three places: the run banner (`:60`), the log filename
(`:112`), and every line prefix (`:153`). Two artefact runs therefore differ in the banner, the
filename and every single line, no matter how deterministic the mesh is — that is the "editor outputs
differ by KB" symptom, and it is independent of `NavMeshCleanup`.

Minimal change that keeps the current behaviour as the default:

```csharp
// NavMeshLog.cs
private static Func<DateTime> _now = delegate { return DateTime.Now; };   // seam for tests/tools
public static Func<DateTime> NowProvider { get { return _now; } set { _now = value; } }

// Initialize: same 3-arg call still works
public static void Initialize(bool enabled, string folderOverride, DateTime? stamp = null)
```

`OpenLocked` and `WriteLocked` take the resolved stamp through a field `_runStamp` set by
`Initialize`. When a stamp is supplied, **all three uses derive from it**: the filename becomes
`<stamp:yyyyMMdd_HHmmss>`, the banner `<stamp:yyyy-MM-dd HH:mm:ss>`, and every line prefix
`<stamp:HH:mm:ss.fff>` — i.e. identical on every line, which is what makes two runs of the same mesh
byte-comparable after the filename. With no stamp, behaviour is byte-identical to today.

Scope note, stated plainly: the caller-side `DateTime.Now` sites in `SceneNavMeshExporter.cs` (`:504`,
`:624`, `:666`, `:735`, `:1038`, `:1268`) write the *editor's visible log and the soup header*, not the
navmesh artefact. Reproducible editor logs need those six changed too — worth doing, but it is a
different artefact and should not gate the mesh determinism work. Flag it, don't bundle it.

#### The cleanup log line (`TiledNavMeshBuilder.cs:1425-1437`)

Append the counters and a `[det v1]` tag so a future format change is greppable:

```
cleanup: [det v1] welded=… removed=… decimated=… relaxed=… refined=… flipped=… quads=…
  quadsXZworst=… needles before=… after=… thin before=… after=… boundaryMoved=… boundaryMaxMove=…m
  components X->Y (kept) [cand=… rejMove=… rejFan=… rejRing=… rejTopo=…]
  work: decCand0=… guardEvals=…/… sweeps=…/… | refineVisits=…/… sweeps=…/…
        relaxEvals=…/… sweeps=…/… | flipVisits=…/… iters=…/…
  ms: refine=… decimate=… relax=… flip=… quads=… total=… deadline=<ms> exceeded=<0|1>
```

The existing fields keep their order and names so old and new log lines can be eyeballed side by side.

### 1.5 Pass E — cos-space conversions (decision path only)

`Math.Acos` is not required to be correctly rounded by the CLR and the last ULP is runtime/CPU
dependent, so it is the one thing that can flip a topology decision between machines. Every
`Acos` on a decision path moves to a cos comparison. `acos` is strictly decreasing on [0, π], so the
inequality direction flips; **getting one of these backwards silently inverts a guard**, which is why
§1.5.3 mandates a shadow-assert build.

#### 1.5.1 The conversion list (every site, with the derived constant)

New helpers (replace-in-place, keep the old functions for reporting):

```csharp
private static double CosDeg(double deg) { return Math.Cos(deg * Math.PI / 180.0); }

// Cos of the SMALLEST interior angle of (a,b,c). Same law-of-cosines branch selection as MinAngle,
// same [-1,1] clamp, same degenerate convention: MinAngle returned 0.0 for a < 1e-9 shortest edge,
// so this returns +1.0 (cos 0) and every "< threshold" test keeps firing on degenerate input.
private static double CosMinAngle(float[] v, int a, int b, int c)

// Clamped cos between two normals. The degenerate sentinel is -1.0, exactly mirroring the 180.0 that
// AngleDeg / NormalAngleDeg returned (cos 180 = -1). See the TryFlip row for why -1 and not NaN.
private static double NormalCos(double[] n0, double[] n1)
private static double NormalCos(float[] v, int a, int b, int c, int d, int e, int f)
```

New constants, all `static readonly double`, each derived from the existing `const float` so the two
cannot drift apart:

```csharp
private static readonly double CosCandidateMinAngle    = CosDeg(CandidateMinAngleDegrees);            // cos 15
private static readonly double CosNewFaceMinAngle      = CosDeg(NewFaceMinAngleDegrees);              // cos 1.5
private static readonly double CosQuadMergeThinAngle   = CosDeg(QuadMergeThinMinAngleDegrees);         // cos 6
private static readonly double CosQuadMergeFoldMax     = CosDeg(QuadMergeFoldMaxDegrees);              // cos 25
private static readonly double CosQuadConvexCeiling    = CosDeg(QuadConvexMaxInteriorAngleDegrees);    // cos 179.9
private static readonly double CosQuadXzCeiling        = CosDeg(QuadXzMaxInteriorAngleDegrees);        // cos 175
private static readonly double CosFlipNormalAngle      = CosDeg(FlipNormalAngleDegrees);              // cos 18
```

| # | site | current | new | note |
|---|---|---|---|---|
| 1 | `Decimate` candidate mask, **427** | `MinAngle(v,a,b,c) >= CandidateMinAngleDegrees` | `CosMinAngle(v,a,b,c) <= CosCandidateMinAngle` | combined with the three `Len` short-edge tests at 428-429 |
| 2 | `IncidentSliver`, **498** | `MinAngle(v,a,b,c) < CandidateMinAngleDegrees` | `CosMinAngle(v,a,b,c) > CosCandidateMinAngle` | hot path: called per candidate per sweep |
| 3 | new-face guard, **630** | `MinAngle(v,…) < NewFaceMinAngleDegrees` | `CosMinAngle(v,…) > CosNewFaceMinAngle` | |
| 4 | `TryFlip` coplanarity, **1968-1970** | `coplanar = NormalAngleDeg(...); if (coplanar > 90) coplanar = 180 - coplanar; if (coplanar > 18) return false;` | `if (Math.Abs(NormalCos(v,u,x,w,u,y,w)) < CosFlipNormalAngle) return false;` | the fold makes the test symmetric: for threshold θ < 90 the pair of branches collapses to `\|d\| < cos θ`. The degenerate sentinel `d = -1` gives `\|d\| = 1 ≥ cos 18`, i.e. a degenerate normal is **allowed**, exactly as today (`180 → 0 → passes`). Do not increment `st.RejMove` here — today's early return does not. |
| 5 | `TryFlip` min-angle gain, **1972-1974** | `before = min(MinAngle,MnAngle); after = min(…); if (after < before + gain) return false;` | `beforeCos = max(CosMinAngle(v,u,x,w), CosMinAngle(v,u,y,w)); afterCos = max(CosMinAngle(v,x,y,u), CosMinAngle(v,x,y,w)); if (afterCos > Math.Cos((Math.Acos(beforeCos) + FlipMinAngleGainDegrees) * Math.PI / 180.0)) return false;` | the min-reduction (which *is* the decision) is now pure cos; one `Acos` remains, once per flip attempt, on the **threshold** side. Gain is 0.5° ≈ 8.7e-3 rad ≫ 1 ULP, so a last-ULP difference cannot flip it. |
| 6 | `MergeQuads` fold, **2142-2143** | `fold = NormalAngleDeg(...); if (fold > 25) continue;` | `if (NormalCos(v,a0,b0,c0,a1,b1,c1) < CosQuadMergeFoldMax) continue;` | **no** fold in the current code — a 170° fold is refused. One-sided test; preserve that. |
| 7 | `QuadWorstAndPerimeter` plane ceiling, **2327-2330** | `cosA = clamp(...); ang = Acos(cosA)*180/PI; if (ang >= 179.9) return false; if (ang < worst) worst = ang;` | reject: `if (cosA <= CosQuadConvexCeiling) return false;` ranking: `if (cosA > worstCos) worstCos = cosA;` | `worst` becomes `worstCos` (max-of-cos == min-of-angle). It feeds the `better` tie-break at 2146-2148 (`worst > bestWorst + 1e-9`), which must also become `worstCos < bestWorstCos - 1e-9`… **careful**: `worst` is the *smallest* angle and the ranking prefers the *largest* `worst`, so in cos space that is the *smallest* cos. `better = bestF1 < 0 \|\| worstCos < bestWorstCos - 1e-9 \|\| (‖worstCos-bestWorstCos‖ <= 1e-9 && perim < bestPerim - 1e-9)`. The `perim` tie-break is untouched. |
| 8 | `QuadWorstAndPerimeter` XZ ceiling, **2372-2374** | `cc = clamp(...); if (Acos(cc)*180/PI >= 175) return false;` | `if (cc <= CosQuadXzCeiling) return false;` | the `Math.Acos` here is inline, so there is nothing else to change |
| 9 | `IsThinForQuad`, **2227** | `MinAngle(v,a,b,c) < QuadMergeThinMinAngleDegrees` | `CosMinAngle(v,a,b,c) > CosQuadMergeThinAngle` | the aspect test at 2231 is already cos-free |
| 10 | `FanWorstAngle` + `TryRelax`, **1393-1404 / 1458 / 1502-1503** | `worst = min over i of MinAngle(...)`, sentinel `180.0`; `best = current + SlideMinGainDegrees`; `if (ang2 <= best) continue;` | `worstCos = max over i of CosMinAngle(...)`, sentinel `-1.0`; `bestCos = CosDeg(Math.Acos(currentCos) + SlideMinGainDegrees)`; `if (candCos <= bestCos) continue;` | 25 candidate offsets per vertex (1463-1512) each call `FanWorstAngle` — this is the hottest `Acos` in the file and the conversion removes `FanCount` of them per offset. Gain 1.0° ≫ ULP, same argument as row 5. |

#### 1.5.2 Stays reporting-only (deliberately not converted)

| function | line | why it stays |
|---|---|---|
| `MinAngle` | 253 | still used by `CountThin` (235) and by `st.Diag` / `RejectReason` strings. Its *values* go into the log; a 1-ULP difference in a printed number is not a determinism failure. |
| `WorstXzAngle` | 2196-2222 | feeds `st.QuadXzWorstAngle` (a max-reduction) and `st.QuadXzNearStraight` (`>= 175 - 0.5` at 2184). Both are log/report values. The *decision* that produced the quads is row 8, which is converted. |
| `NormalAngleDeg`, `AngleDeg` | 2060, 2071 | kept for reporting; `NormalCos` added alongside. |
| `FaceSlopeDeg` | 1852 | **dead code** (Finding A). Delete it, or mark `// unused: not on any decision path`. Do not convert a function nothing calls. |
| `TriQuality` | 1041 | **dead code**, same treatment. |
| harness `PrintTriangleQuality` / `ProbeXzQuads` | harness 3306, 8815 | test instrumentation; its own `Acos` is fine and its numbers are compared, not gated. |

#### 1.5.3 Verification the conversions are direction-correct (mandatory, temporary)

An inverted guard is a silent geometry regression. Ship a temporary shadow build that keeps both
predicates and asserts agreement:

```csharp
// in each converted site, TEMPORARILY:
bool oldWay = <the original expression>;
bool newWay = <the cos expression>;
if (oldWay != newWay) shadowDisagreements++;
```

Run all three maps. Require **exactly 0 disagreements** over every evaluated triangle/quad (hundreds of
thousands per map, including the four hand-made patches in `Test 27`). Print the counter in the log.
Any non-zero value stops the change. Remove the shadow code before commit. Note in the commit message
that the counter was 0 — that is the evidence the inequality flips were right.

### 1.6 Pass F — `FlipEdges`: keep the sorted key array, filter stale keys

Current shape (1907-1932): every iteration does `var keys = new List<long>(edgeFaces.Keys); keys.Sort();`,
then rebuilds `edgeFaces` from scratch at 1929. That is 5 full `O(E log E)` sorts + 5 `O(F)` rebuilds.

Target: sort **once**, then filter.

```csharp
private static void FlipEdges(float[] v, List<int> tris, Stats st, Budgets b)
{
    var edgeFaces = BuildEdgeFaces(tris);
    int nf = tris.Count / 3;
    var touched = new bool[nf];
    // One sort for the whole pass. `keys` stays ascending for its entire life: filtering preserves
    // order, and new keys are merged in sorted at the round boundary.
    var keys = new List<long>(edgeFaces.Keys);
    keys.Sort();
    int e0 = keys.Count;
    int edgeVisits = 0;
    var newKeys = new List<long>();          // diagonals created this round
    int cursor = 0;                          // index into `keys`; compacted in place on stale hits

    for (int iter = 0; iter < b.FlipIterationCap; iter++)
    {
        bool any = false;
        newKeys.Clear();
        for (int i = cursor; i < keys.Count; i++)
        {
            long key = keys[i];
            edgeVisits++;
            if (edgeVisits >= edgeVisitBudget) { CommitKeys(keys, cursor, newKeys); return; }
            List<int> fl;
            if (!edgeFaces.TryGetValue(key, out fl) || fl.Count != 2) continue;
            int f0 = fl[0], f1 = fl[1];
            if (touched[f0] || touched[f1]) continue;
            int u = (int)(key >> 32), w = (int)(key & 0xffffffffL);
            int x = OtherVertex(tris, f0, u, w), y = OtherVertex(tris, f1, u, w);
            if (!TryFlip(v, tris, f0, f1, key, edgeFaces, st, out int diagX, out int diagY)) continue;
            touched[f0] = true; touched[f1] = true; any = true;
            // A flip destroys the shared edge and drops the four ring edges below two uses, and it
            // creates the diagonal (x,y). Filter the five stale keys; add the diagonal only for the
            // NEXT round (today it can only appear via the rebuild) - adding it now would change the
            // result. `u,x / x,w / w,y / y,u` are filtered unconditionally: at two uses they drop to
            // one, at one use they were never candidates.
            StaleKey(keys, ref cursor, i, key);                       // (u,w)
            StaleKey(keys, ref cursor, i, EdgeKey(u, x));
            StaleKey(keys, ref cursor, i, EdgeKey(x, w));
            StaleKey(keys, ref cursor, i, EdgeKey(w, y));
            StaleKey(keys, ref cursor, i, EdgeKey(y, u));
            newKeys.Add(EdgeKey(x, y));
        }
        CommitKeys(keys, cursor, newKeys);   // compact the survivors + merge the sorted new keys
        if (!any) break;
        edgeFaces = BuildEdgeFaces(tris);    // next round's snapshot (still one rebuild per round)
        for (int i = 0; i < nf; i++) touched[i] = false;
    }
}
```

Two invariants that make this **decision-identical** to today, and both must be stated in the comment
block:

1. **`edgeFaces` is not mutated during a round.** It stays the round-start snapshot, so `TryFlip`'s
   `edges.TryGetValue(EdgeKey(x,y))` and the four `UseCount` calls (1976-1979) see exactly what they
   see today. Incrementally maintaining the map would change those inputs and therefore the flips.
2. **A face is flipped at most once per round** (the `touched[]` mask), so the surviving key order
   ascending is the same order the full re-sort would produce.

`TryFlip` needs two new `out` params (or a small struct) for the diagonal endpoints, because the
caller now needs `x` and `y` to filter. Change the signature to
`TryFlip(..., out int diagX, out int diagY)` and set both on success; on every `return false` path they
are simply not read. This is the only signature change in `FlipEdges`' callee set.

**`touched[]` hardening (Finding C):** `nf` is invariant today, but make the reset explicit about it
so the next reader does not have to re-derive it, and add the guard that makes it correct if that
changes:

```csharp
// nf is constant across iterations (TryFlip rewrites face CONTENTS; it never adds or removes a
// face), so this reset covers every slot the mask can address. Asserted by the bound below so a
// future edit that appends a face fails loudly instead of silently reading past the end.
if (nf > touched.Length) { st.Deadline.Poll(); /* log a warning; do not throw */ }
else for (int i = 0; i < nf; i++) touched[i] = false;
```

Better still: allocate `touched` as `new bool[Math.Max(nf, tris.Count / 3)]` at the top and re-allocate
at the round boundary from the live `tris.Count / 3`. Cheap, removes the whole class of bug.

Net effect: 1 sort instead of 5, `Acos` removed from the flip's hot path, and the same flips in the
same order.

### 1.7 Pass G — determinism self-test and golden hashes

Both live in the harness. **Canonical location: `tools/TiledCoreHarness/Program.cs`** (+ `harness.csproj`,
net472 / C# 7.3, compiles `NavMeshCleanup.cs` directly at `<Compile Include=…\NavMeshCleanup.cs />`). The
temp copy at `…\Temp\opencode\tiled-core-harness\` is byte-identical today (SHA-256 `B7A41AA1…A61AA`,
both 488,495 bytes); edit the canonical one and mirror it, or run from whichever the csproj points at —
do not let the two drift.

#### (a) Double-run self-test — `Test28_CleanupDeterminism` (new; registered after `Test27` at harness 826)

Spec:

1. Build a mesh that actually produces slivers: `MakeGrid(80f, 20f, 0.5f, CliffHeight, …)` with
   `UseDetailMesh = true`, `MaxErrorMeters = 0.25f`, `DetailSampleDistMeters = 2f` — the same shape
   `Test3_Determinism` (3638) already uses.
2. Run the **cleanup** twice on independent copies of the same input:
   ```csharp
   var a = new List<float>(verts); var at = new List<int>(tris);
   var s1 = NavMeshCleanup.Run(a, at, budget, true);
   var b = new List<float>(verts); var bt = new List<int>(tris);
   var s2 = NavMeshCleanup.Run(b, bt, budget, true);
   ```
3. Assert, in this order, printing each:
   * `a.Count == b.Count` and `Hash(a.ToArray()) == Hash(b.ToArray())` (**byte-identical verts**)
   * `at.Count == bt.Count` and `Hash(at.ToArray()) == Hash(bt.ToArray())` (**byte-identical tris**)
   * `s1.QuadPairs` vs `s2.QuadPairs`: same count, `Hash(ToArray())` equal
   * every deterministic `Stats` field equal: `Welded, Removed, Decimated, Flipped, Relaxed, Refined,
     Quads, NeedlesBefore/After, ThinBefore/After, BoundaryMovedCount, ComponentsBefore/After,
     CandVerts, RejMove, RejFan, RejRing, RejTopo, QuadXzWorstAngle, QuadXzNearStraight`,
     `QuadXzWorstAngle` compared with a tolerance of `0` (it must be bit-identical: same inputs, same
     `Acos`, same order).
   * **Explicit carve-out:** `DeadlineExceeded` and every `*Ms` field are **excluded** from the
     equality assert. They are wall-clock dependent by construction. Put the exclusion and the reason
     in a comment so nobody "fixes" it by making the deadline deterministic — that would defeat §1.2.
4. Then a third pass: run with `Budgets.DecimateGuardMultiple = 1` and again with `= 64`, and assert
   both are self-consistent (run twice each, compare). This proves the *budget* is deterministic, not
   just the default configuration — otherwise a budget that bites on some machines and not others
   would slip through.
5. Also assert the **budget actually bites somewhere**: with `DecimateGuardMultiple = 1`, assert
   `DecimateBudgetHit == 1` on the cliff grid. A budget that never triggers is not a budget.

Print `RESULT: CLEANUP DETERMINISM PASS/FAIL`. **Create this test in step S1b — before S2 — so S2 has an
observable.** With the four gates still present it fails on at least one map/run; after S2 deletes them
it must pass. The test is *created* in S1b but only *hardened into a committed assertion alongside the
goldens* in S8; the two are deliberately split so S2 can observe the transition and S8 owns the golden
table. If it already passes on every fixture before S2, that is itself a finding — the clock was not
binding on this machine — and it changes the calibration in §3.4.

#### (b) Golden hashes per map

1. Add a `--goldenhash` flag to `SnapshotMode` (harness 901-993, alongside `--quality` at 930): build
   once, print the three hashes in a copy-pasteable form, exit.
2. **Reuse the harness's existing FNV-1a 64-bit helpers** — `Hash(float[])` at 9365 and `Hash(int[])`
   at 9355. Do not add a second hash function: `Test3_Determinism` already hard-codes
   `e016e53886cb9585` / `ba807f993fde10fc` from these, and a second implementation would make those
   constants incomparable.
3. Hash three arrays per map: `r.VertsXyz` (float), `r.Tris` (int), `r.QuadPairs` (int, possibly
   `null` → hash an empty array and record `QuadPairs=null`).
4. Add a `GoldenHashes` table in the harness, asserted after every snapshot build:

   | map | fixture | varsHash | trisHash | quadHash |
   |---|---|---|---|---|
   | Birtu | `tiled-core-harness\mp_tdm_Birtu_navmesh` | *capture* | *capture* | *capture* |
   | CC_20 | `tiled-core-harness\CC_20_siege_navmesh` | *capture* | *capture* | *capture* |
   | CC_20 (v2 fixture) | `editor_artifacts\snapshot_v2_terrain.bin` / `_assets.bin` / `.txt` | *capture* | *capture* | *capture* |
   | Basamu | `tiled-core-harness\mp_battle_Basamu_village_navmesh` | *capture* | *capture* | *capture* |

5. The golden record **must include its context**, and the assertion must fail loudly, not silently
   pass, when the context differs. Store with each entry: the `settings:` line (harness 1167-1174), the
   `loaded:`/`grid:`/`date:` lines from the snapshot `.txt`, and the snapshot file's length + last-write
   timestamp. A refreshed fixture invalidates the goldens by design — that is the point.
6. Add a **cross-process repeat**: run each fixture 3× in one session and in 2 fresh processes; all
   nine hashes must agree. This catches state leakage between builds (the harness has been bitten by
   `RcRasterizations` scratch buffers before — see the comment at harness 3662-3663), which a
   single-process double-run cannot see.
7. `Test 3`'s existing neutrality assertion (`e016e53886cb9585` / `ba807f993fde10fc`, harness
   3668-3672) must keep passing unchanged. It is a pre-cleanup fixture, so if it moves, something
   upstream of the cleanup changed — investigate, do not re-baseline.

---

## 2. Acceptance criteria

### 2.1 Quality metrics that must hold, per map

Baseline = `README.md:1251-1255`, measured with the shipped binary on the reference machine. These are
the **"before"** column of the table in §2.2 (i.e. the state of the tree today, with the clock gates
still present).

Hard criteria — any failure blocks the change:

| # | criterion | threshold | where measured |
|---|---|---|---|
| A1 | invariant gate | `invariants=PASS` | harness `build:` line, `Program.cs:1184` |
| A2 | island count | unchanged vs the pre-change build on the same binary + settings; cross-check against the README edge-island figures 5 / 15 / 1 | `cleanup: components X->Y (kept)` + `islands:` (1364) |
| A3 | `ComponentsBefore == ComponentsAfter` **inside** the cleanup | must be equal (the code already logs `(kept)` / `(CHANGED!)`) | `NavMeshCleanup.cs:1435` |
| A4 | `quads` | ≥ baseline − 5% (603 / 1857 / 2955) | `build: quads=` |
| A5 | quad frame guards | `concaveQuads == 0`, `xzConcave == 0`, `xzReflex == 0`, `xzStraight == 0`, `xzOrientBad == 0`, `xzDegenerate == 0` | `build:` line + `--xzprobe` |
| A6 | worst quad angles | `worstQuadAngle < 179.9` **and** `worstXzAngle < 175.0` (both ceilings are genuinely binding, per README 1262-1264 — if either comes back with slack, a guard changed) | `build:` line |
| A7 | `thin` after | ≤ baseline + 5% (356 / 1037 / 1499) | `PrintTriangleQuality`, `Program.cs:3342` |
| A8 | `needles` after | ≤ baseline + 10% **and** ≤ 15 / 60 / 100 absolute (baseline 8 / 38 / 61) | same |
| A9 | `meanQuality` | ≥ baseline − 0.01 (0.722 / 0.741 / 0.746) | `Program.cs:3344` |
| A10 | boundary moves | `BoundaryMaxMove <= BoundaryToleranceMeters` (0.35) — structurally guaranteed by `:619`, so this is a smoke test; `BoundaryMovedCount` recorded, not bounded | `cleanup:` line |
| A11 | long-thin (aspect > 8, longest ≥ 4 m) | recorded, expected to improve or hold: 760 / 1894 / 2979 | `Program.cs:3345` |
| A12 | harness suite | every `PASS` line identical to the pre-change suite log; `Test 27`'s three sub-cases still refuse (hand-made concave quad, real Birtu XZ-reflex patch, NMG8 round trip) | suite log |
| A13 | **new** determinism | `Test28` double-run byte-compare PASS (incl. the `DecimateGuardMultiple = 1` and `= 64` variants, and `DecimateBudgetHit == 1` at `= 1`) | harness |
| A14 | **new** goldens | all three fixture golden triples stable over 3 repeats × 2 processes | `--goldenhash` |
| A15 | **new** deadline | `deadline exceeded=0` on all three maps — i.e. the deterministic budget really does fit inside the old 6000 ms on the reference machine. This is the acceptance test for the calibration. | `cleanup:` line |
| A16 | **new** shadow assert | 0 disagreements on all three maps (temporary build, §1.5.3) | `cleanup:` line |
| A17 | **new** no gates left | repo-wide grep for `ElapsedMilliseconds >` / `ElapsedSeconds >` returns hits **only** inside the `Deadline` class and the two reporting sites (`TiledNavMeshBuilder.cs:1184-1185`); every other elapsed comparison that feeds control flow is gone. Inspect each hit rather than asserting a literal zero. | `rg "Elapsed(Milliseconds\|Seconds) >"` |

### 2.2 Before/after table format to fill in

One row per map, filled from the `build:` + `cleanup:` + `--quality` output of the three fixtures
(Birtu, CC_20 v2, Basamu). `Δ` is a percentage; `verdict` is PASS/FAIL against §2.1.

```markdown
| metric | Birtu before | Birtu after | Δ | CC_20 before | CC_20 after | Δ | Basamu before | Basamu after | Δ | verdict |
|---|---|---|---|---|---|---|---|---|---|---|
| faces | 44645 | | | 241641 | | | 492616 | | | A1 |
| thin (<5 deg) | 356 | | | 1037 | | | 1499 | | | A7 |
| needles (alt<0.05, edge>0.5) | 8 | | | 38 | | | 61 | | | A8 |
| long-thin (aspect>8, longest>=4m) | 760 | | | 1894 | | | 2979 | | | A11 |
| mean quality | 0.722 | | | 0.741 | | | 0.746 | | | A9 |
| quads (concave) | 603 (0) | | | 1857 (0) | | | 2955 (0) | | | A4,A5 |
| worst quad angle plane / XZ (deg) | 175.27 / 174.98 | | | 178.38 / 174.92 | | | 178.78 / 174.96 | | | A6 |
| xzReflex / xzStraight / xzOrientBad | 0 / 0 / 0 | | | 0 / 0 / 0 | | | 0 / 0 / 0 | | | A5 |
| islands | 5 | | | 15 | | | 1 | | | A2 |
| components before->after | kept | | | kept | | | kept | | | A3 |
| boundaryMoved / boundaryMaxMove | / <=0.35 m | | | / <=0.35 m | | | / <=0.35 m | | | A10 |
| invariants | PASS | | | PASS | | | PASS | | | A1 |
| **decCand0 / guardEvals / budget** | n/a | | | n/a | | | n/a | | | §1.3 |
| **refineVisits / sweeps** | n/a | | | n/a | | | n/a | | | §1.3 |
| **relaxEvals / sweeps** | n/a | | | n/a | | | n/a | | | §1.3 |
| **flipVisits / iters** | n/a | | | n/a | | | n/a | | | §1.3 |
| **build ms (total)** | 4.7 s | | | 24.9 s | | | 32.0 s | | | A15 |
| **cleanup ms: refine/dec/relax/flip/quads** | n/a | | | n/a | | | n/a | | | |
| **deadline exceeded** | n/a | | | n/a | | | n/a | | | A15 |
| varsHash / trisHash / quadHash | - | | | - | | | - | | | A14 |
| determinism (double run) | - | | | - | | | - | | | A13 |
| suite (PASS count) | 27 | | | 27 | | | 27 | | | A12 |
```

Capture script shape. **`--quality` and `--xzprobe` are mutually exclusive** — in `SnapshotMode`,
`--xzprobe` early-returns at `Program.cs:1193` and `--quality` at `:1341`, so xzprobe wins and quality
never prints. Run them as separate invocations. The default path (neither flag) prints `build:` with the
quad/frame columns and the island report.

```powershell
$root = "C:\Users\Sheepbitter\AppData\Local\Temp\opencode\tiled-core-harness"
$ea   = "C:\Users\Sheepbitter\AppData\Local\Temp\opencode\editor_artifacts"
$h    = "$root\bin\Release\net472\TiledCoreHarness.exe"
$out  = "C:\Users\Sheepbitter\AppData\Local\Temp\opencode\det_research"

# triangle metrics (one invocation per map)
& $h --snapshot "$root\mp_tdm_Birtu_navmesh"               --quality 2>&1 | Out-File "$out\after_birtu_quality.txt"
& $h --snapshot "$root\CC_20_siege_navmesh"                --quality 2>&1 | Out-File "$out\after_cc20_quality.txt"
& $h --snapshot "$root\mp_battle_Basamu_village_navmesh"  --quality 2>&1 | Out-File "$out\after_basamu_quality.txt"
# the current CC_20 fixture (v2, walk-area border block - TODO item 5)
& $h --snapshot "$ea\snapshot_v2_terrain.bin" "$ea\snapshot_v2_assets.bin" "$ea\snapshot_v2.txt" --quality 2>&1 |
    Out-File "$out\after_cc20_v2_quality.txt"

# xz quad-frame probe (SEPARATE invocation - --xzprobe and --quality cannot both run)
& $h --snapshot "$root\mp_tdm_Birtu_navmesh"               --xzprobe 2>&1 | Out-File "$out\after_birtu_xz.txt"
& $h --snapshot "$root\CC_20_siege_navmesh"                --xzprobe 2>&1 | Out-File "$out\after_cc20_xz.txt"
& $h --snapshot "$root\mp_battle_Basamu_village_navmesh"  --xzprobe 2>&1 | Out-File "$out\after_basamu_xz.txt"
& $h --snapshot "$ea\snapshot_v2_terrain.bin" "$ea\snapshot_v2_assets.bin" "$ea\snapshot_v2.txt" --xzprobe 2>&1 |
    Out-File "$out\after_cc20_v2_xz.txt"

# default path (build: line + island report) and repeats x3 + a second process for the golden check (A14)
```

---

## 3. Calibration procedure

### 3.1 Counters and timers to add (all in the same change as §1.3)

`Stats` gains the fourteen work counters listed at the end of §1.3 plus
`DeadlineExceeded, DeadlineBudgetMs, CleanupMs, RefineMs, DecimateMs, RelaxMs, FlipMs, QuadMergeMs`.

The per-pass `*Ms` fields are what turns the report's order-of-magnitude reasoning into a number, and
they are safe: a `Stopwatch` read and two `long` stores, per pass, with no effect on any control flow.
(They are also TODO item 6's request, so fold them in rather than leaving a second instrumentation
round later.)

`Budgets` is echoed into the log so every run is self-describing.

### 3.2 Measurement configuration

For the calibration run only. Every knob below is settable at runtime via the §1.3.1 harness flags, so
the calibration is executable as written (no code edit between runs):

```powershell
$h = "C:\Users\Sheepbitter\AppData\Local\Temp\opencode\tiled-core-harness\bin\Release\net472\TiledCoreHarness.exe"
$p = "$root\mp_battle_Basamu_village_navmesh"   # slowest reference map

# 1) unbounded: DeadlineMs=0 disables the warning; guard multiple huge -> convergence cost measured
& $h --snapshot $p --cleanup-deadline 0 --dec-guard-mult 1000000 --quality 2>&1 | Tee-Object "$out\cal_basamu_unbounded.txt"

# 2) isolate the per-sweep O(F) rebuild cost (two extra runs on the biggest map)
& $h --snapshot $p --cleanup-deadline 0 --dec-guard-mult 1000000 --dec-sweep-cap 1 --quality 2>&1 | Tee-Object "$out\cal_basamu_sweep1.txt"
& $h --snapshot $p --cleanup-deadline 0 --dec-guard-mult 1000000 --dec-sweep-cap 2 --quality 2>&1 | Tee-Object "$out\cal_basamu_sweep2.txt"
```

* `--cleanup-deadline 0` disables the diagnostic warning — otherwise the very thing being measured warns.
* `--dec-guard-mult 1000000` makes the Decimate guard budget effectively unbounded, so the pass runs to
  convergence and the *unbounded* cost is what gets measured. The multiple is then derived from it.
* `--dec-sweep-cap 1` / `--dec-sweep-cap 2` isolate the per-sweep `O(F)` rebuild cost:
  `rebuildMsPerSweep = ms(sweep=2) − ms(sweep=1)`. This decides whether to lower `DecimateSweepCap`
  later (§4, risk 3); one extra run on the biggest map.
* Run the same three invocations on Birtu / CC_20 v1 / CC_20 v2 so `msPerUnit` is taken across maps.

The default `settings:` line must echo the effective `Budgets` (§1.3.1), so each captured log is
self-describing and a run cannot be mistaken for the default configuration.

### 3.3 Fixtures

| map | prefix (harness `--snapshot <prefix>`) | notes |
|---|---|---|
| Birtu | `C:\Users\Sheepbitter\AppData\Local\Temp\opencode\tiled-core-harness\mp_tdm_Birtu_navmesh` | smallest; also the only map the README says reproduces exactly today |
| CC_20 (caves, stale v1) | `…\tiled-core-harness\CC_20_siege_navmesh` | the historical baseline set |
| CC_20 (v2, current) | 3-path form: `…\editor_artifacts\snapshot_v2_terrain.bin …\snapshot_v2_assets.bin …\snapshot_v2.txt` | **this is the correct current fixture** (TODO item 5); `snapshot_v2.txt` header: `date: 2026-10-06`, `grid: 1281x1281 spacing=1.563m`, `assets: entities=3337` |
| Basamu | `…\tiled-core-harness\mp_battle_Basamu_village_navmesh` | largest; the one the README observed drifting |

Run each with `--quality` (triangle metrics) and without (the full report incl. islands and the
`build:` line). Note `RunSnapshot` is also reachable via the barrier path (`Program.cs:238`) — not
needed here.

### 3.4 Derivation

Per pass, from the calibration run:

```
msPerGuardEval  = DecimateMs  / max(1, DecimateGuardEvals)
msPerFaceVisit  = RefineMs    / max(1, RefineFaceVisits)
msPerRelaxEval  = RelaxMs     / max(1, RelaxEvals)
msPerEdgeVisit  = FlipMs      / max(1, FlipEdgeVisits)
```

`DecimateGuardEvals` at an unbounded multiple is the *observed convergence cost*, i.e. the number the
old 6000 ms deadline was truncating (the report's own figure: ~240k refused candidate folds on Birtu
at the 0.20 m tolerance).

Then solve for the multiple, targeting the old worst case on the **slowest reference machine**:

```
otherPassesMs  = RefineMs + RelaxMs + FlipMs + QuadMergeMs          (at the chosen caps)
floorMs        = msPerGuardEval * (F0 / DecimateGuardFloorDivisor)
mult           = floor((6000 - otherPassesMs - floorMs) / (msPerGuardEval * C0))
```

* Clamp `mult` to ≥ 1.
* If the unclamped value is **≤ 1**, the clock was *not* the binding constraint on that machine. Do not
  silently pick 1 and call it a match: record the finding, pick the `mult` whose predicted worst case is
  ≈ 6000 ms anyway (which will be > 1, i.e. *more* work than the old clock allowed), and say so
  explicitly in the round's writeup — because quality will change materially and the acceptance bands
  in §2.1 are the ones that decide whether that is acceptable.
* Take the **maximum** predicted worst case over the four maps. The reference machine is the one where
  the prediction is largest, not the one where it is most convenient.
* Cross-check with `predictedWorstCase = Σ_passes msPerUnit_p × units_p(at the chosen budgets)` and
  require it to land within ±25% of 6000 ms on the slowest map. A larger miss means the linear
  ms/unit model is wrong (the per-sweep rebuilds are superlinear in F, or GC is dominating) — then
  re-derive from measured data instead of trusting the formula, and note it.

### 3.5 Calibration record (commit this table with the change)

```markdown
| map | F0 | E0 | C0 | guardEvals@1x | DecimateMs | ms/guardEval | rebuildMsPerSweep | RefineFaceVisits | RefineMs | ms/faceVisit | RelaxEvals | RelaxMs | ms/relaxEval | FlipEdgeVisits | FlipMs | ms/edgeVisit | predicted worst case | measured | DeadlineExceeded |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Birtu | | | | | | | | | | | | | | | | | | | | |
| CC_20 v1 | | | | | | | | | | | | | | | | | | | | | |
| CC_20 v2 | | | | | | | | | | | | | | | | | | |
| Basamu | | | | | | | | | | | | | | | | | | | | |

chosen: DecimateGuardMultiple = <n>   (was: clock, 6000 ms)
predicted worst case = <ms> on <map>   target 6000 ms   A15 deadline exceeded = 0
```

### 3.6 Post-calibration quality reconciliation

Re-run the §2.2 matrix with the chosen constants. If a metric leaves its band, first identify **which
budget was the binding one** for that map from the §2.2 work rows (`budgetHit`, `sweeps == cap`, and
the four `*Evals` against their budgets). Tune that budget: `DecimateGuardMultiple` for Decimate,
`RefineSweepCap` for Refine (it is a hard cap, so this is a real quality lever), `RelaxBudgetFloor` for
Relax. Never reintroduce a clock, never change a quality threshold. Record which budget and value made
the metric fit, so the next calibration starts from data rather than from a guess.

---

## 4. Risks and mitigations

| # | risk | evidence | mitigation | detected by |
|---|---|---|---|---|
| 1 | **Thresholds were tuned under clock truncation.** Every measured quality number came from a run the clock truncated at an unpredictable point. | README 1257-1264 already documents the drift ("Basamu observed at 1497/1498/1501 thin, 2942/2955/2962 quads… across repeats of the *same* binary"). | Calibrate first (§3), then re-measure the whole table; identify the binding budget per map (§1.3) and tune it — `DecimateGuardMultiple` is the primary one, not the only one (Refine/Relax can bind). Bands in §2.1, not equality. | A7-A11 |
| 2 | **Guard counters change**, so `CandVerts/RejMove/RejFan/RejRing/RejTopo` become deterministic and differ from every historical log. | They currently vary run to run. | Tag the block `[det v1]`; never diff old logs automatically; the README tables are the only golden data and they are about geometry, not counters. | S10 review |
| 3 | **Per-sweep `O(F)` rebuilds become the bottleneck** once guard evaluation is no longer throttled (`CompactFaces` + `BuildFaceAdjacency` + component BFS + candidate mask, ×16 sweeps × 3 meshes). | Risk 3 of the research report; unmeasured. | The per-pass `*Ms` counters land in the *same* change, and §3.2 measures `rebuildMsPerSweep` directly. If it dominates, lower `DecimateSweepCap` 16 → 8 — still deterministic, still cheap. **Do not** parallelise guard evaluation in this round. | §3.5 table |
| 4 | **Cross-machine contract wording.** Bit-identity across machines is not achievable: the CLR does not require `Acos`/`Cos`/`Sin` to be correctly rounded. | Research report §6.4. | State it precisely in the README: *for the same input, the same constants and the same binary + runtime, the output is byte-identical on any machine; the cos conversion additionally removes the per-triangle input-dependent transcendental from the decision path, so the decisions are stable even if the runtime's libm differs in the last ULP — except for the seven precomputed `CosDeg` constants themselves.* Do **not** write "deterministic across machines". | S10 review |
| 5 | **An inverted guard from a missed inequality flip** (cos is decreasing). Silent, and it shows up as a geometry regression far from the change. | Nine conversion sites (§1.5.1), each with a flipped inequality. | Mandatory shadow-assert build: old and new predicates side by side, 0 disagreements required over all three maps (A16). Non-zero stops the change. | A16 |
| 6 | **A `Math.Cos` creeping back onto a decision path**, or a cos constant derived from a runtime value. | Seven `static readonly` constants; the temptation to "just compare the angle". | All constants derived only from `const float` degrees, initialised once in the static ctor. Comment on each: "derived from a const; do not compute from runtime input." | Code review |
| 7 | **`C0` coupling**: `DecimateGuardMultiple * C0` makes runtime depend on the candidate population, which depends on `CandidateMinAngleDegrees`, `ShortEdgeMeters`, `SurfaceToleranceMeters`. | Research report §6. Tightening a tolerance moves runtime. | The `F0/2` floor term is topology-only, so the budget stays linear in `F0` even if the candidate set explodes. Document that `SurfaceToleranceMeters` is a runtime knob now, not just a quality knob. | §3.5 |
| 8 | **Two of the four budgets are derived bounds, not new limiters** (`RefineLongThin`, `RelaxThin`, `FlipEdges` are all implied by their sweep caps). Raising a cap silently raises the work. | Arithmetic in §1.3. | Every cap is a named constant instead of a literal; the per-pass `*Ms` counters make an increase visible immediately. Do not present them as "the budgets that control runtime" — only `DecimateGuardMultiple` is expected to bind. | §3.5 |
| 9 | **`FlipEdges` incremental keys change the result** if `edgeFaces` is mutated mid-round or the diagonal is added early. | §1.6. | Two stated invariants (`edgeFaces` is the round-start snapshot; `touched` bounds one flip per face per round). Verify by asserting `st.Flipped` and `st.RejMove` are **identical** to pre-§1.6 on all three maps — the only permitted change is time. | S7 verify |
| 10 | **The self-test can pass while the pipeline is still nondeterministic upstream** (scene traversal, physics, the terrain generator's own inputs). | Research report §6.5; no RNG in this repo, but the generator's inputs were not audited. | Two layers: `Test28` covers the cleanup in isolation, the golden triples cover the full build end-to-end on real fixtures. Say explicitly in the README that the determinism claim covers `TiledNavMeshBuilder.Build` given identical inputs. | A14 |
| 11 | **Dead code invites a future decision-path edit without cos conversion** (`FaceSlopeDeg`, `MaxCleanupSlopeDegrees`, `TriQuality`). | Finding A. | Delete them, or mark `// unused: not on any decision path`. Separately: `MaxFanNormalSpreadDegrees` is inert (§0.3) and the README claims otherwise — fixing the code *or* the prose is a follow-up, because fixing the code changes output. | S10 |
| 12 | **`NavMeshLog` clock seam is insufficient for editor-artefact reproducibility.** Six more `DateTime.Now` sites in `SceneNavMeshExporter.cs`. | grep at `:504, :624, :666, :735, :1038, :1268`. | Scope the seam to `NavMeshLog` and say so; list the six exporter sites as follow-up work. Mesh determinism does not depend on any of them. | S10 |

---

## 5. Order of operations

Each step is independently revertible and has one mechanical verification. **Instrument before you
remove** — S1 without S2 is what lets you attribute any S2 quality change to the gate removal rather
than to a measurement artifact.

| step | change | verify immediately |
|---|---|---|
| **S0** | **baseline capture, no code change.** Three maps × {`--cleanup on`, `--cleanup off`} × 3 repeats, as **two separate invocations per config** — `--quality` alone and `--xzprobe` alone (they are mutually exclusive early-returns in `SnapshotMode`: `Program.cs:1193` vs `:1341`) — plus the default path for the `build:`/island line. Saved as `det_base_<map>_<rep>_<mode>_<quality\|xz\|plain>.txt`. | The three cleanup-on repeats agree on Birtu and CC_20 exactly and drift on Basamu by ≲ 0.2% (README 1262). **That drift is the quantified "before" nondeterminism** and the "before" column of §2.2. If the repeats agree perfectly on all three, record that — the gates were not binding on this machine and the calibration in §3.4 must read `mult ≤ 1`. |
| **S1** | **instrument only** — §1.2 counters, `Deadline`, per-pass `*Ms`, §1.3 counters (incremented but **not** yet enforced, budgets logged with an unreachable ceiling). No control flow changes. | Build (both projects). Re-run S0's matrix. Every geometry metric must be **within S0's own variance** (S1 cannot reduce variance, only observe it — if a metric moves outside S0's observed range, something was changed and it must be found before S2). `Test 3` neutrality constants `e016e53886cb9585` / `ba807f993fde10fc` unchanged. |
| **S1b** | **create the determinism self-test** — §1.7(a): add `Test28_CleanupDeterminism` and register it after `Test27` in the canonical harness (`tools/TiledCoreHarness/Program.cs`, mirror to the temp copy). **Do not add the goldens yet.** This is the pre-S2 observable. | `Test28` runs and, with the four gates still present, **FAILS** on at least one map/run. If it already passes everywhere, record that the clock was not binding on this machine (it feeds §3.4). `Test 3` neutrality constants unchanged. |
| **S2** | **delete the four gates** — §1.1: signature, `refineUsed` cascade (176-196), the four gate lines, the `Stopwatch` parameters, and the `DecimDiag` move to the end of `Decimate` (§1.4). Budgets deliberately **not** enforced yet — with no budgets and no gates the passes are bounded only by their sweep caps. | `Test28` (created in S1b) must now **PASS** — if it does not, something else is nondeterministic and S2 is not the whole story; stop and investigate. Repo grep for `ElapsedMilliseconds >` returns hits **only** in the `Deadline` class and the two reporting sites (`TiledNavMeshBuilder.cs:1184-1185`); inspect each rather than asserting a literal zero (A17). `DeadlineExceeded` may be true; that is fine and expected (nothing bounds the work yet). Quality metrics must be within S0's variance — the passes still stop at the same sweep caps. |
| **S3** | **settings plumbing** — §1.3 + §1.3.1: `Budgets` struct, `CleanupMaxMs` → `CleanupDeadlineMs`, new `CleanupDecimateGuardMultiple` and the calibration-only override fields, the four named sweep caps replacing the literals at 382 / 1572 / 1354 / 1913, `BuildCleanupBudgets(s)`, and the harness CLI flags in `SnapshotMode`. Update `TiledNavMeshBuilder.cs:307`, `:1411` and harness `Program.cs:7721`. | Build both projects. `--cleanup on/off` still works; `SelectionQuads` still passes (Test 27 exercises it). The caps read back at their current values — a silent default would shrink the work and quietly change quality. Every new flag round-trips (run one `--dec-guard-mult` and see it in the echoed `settings:` line). |
| **S4** | **enforce the four budgets** — the four new check lines from §1.1, with `timeUp` → `budgetUp`. | `Test28` still PASS, including the `DecimateGuardMultiple = 1` case with `DecimateBudgetHit == 1` (proves the budget can bite). `sweeps ≤ cap` in every log line. `budgetHit = 1` on **at least one** map — if it is 0 everywhere, the budget is not calibrated and S9 has not really happened. |
| **S5** | **deterministic diagnostics** — §1.4: the new `DecimDiag`, the `[det v1]` block in the `TiledNavMeshBuilder` log line, the `NavMeshLog` clock seam, and the culture sweep of cleanup-related log formatting to `InvariantCulture` (§0.6 item 1). | `NavMeshLog` still never throws (its `try/catch` structure is untouched — verify by passing a bad `folderOverride`). Two runs with the same supplied stamp produce byte-identical log text; the no-stamp default is byte-identical to today's format. Log line field order preserved so old/new can be eyeballed side by side. |
| **S5b** | **dead-code guard** — §0.6 item 2: comment at `ThirdParty/DotRecast/Recast/RcBuilder.cs:83` and a guard/`[Obsolete]` marking `BuildMultiThread` as not-for-product (its `ConcurrentQueue`→`ToList()` at `:131` returns completion order). | Build; confirm the guard does not break the `RcConfig.CalcBorder` static use (`TiledNavMeshBuilder.cs:859`). No mesh change. |
| **S6** | **cos conversions** — §1.5, with the shadow-assert build live. | **0 disagreements** on all three maps (A16). `Test28` PASS. `Test 27`'s three sub-cases pass (they exercise the converted XZ and plane ceilings directly — the cheapest net for this step). §2.2's quad counts, `worstQuadAngle` and `worstXzAngle` **identical** to S4's (the conversions must be exact, not approximately exact). |
| **S7** | **`FlipEdges`** — §1.6: one sort, filter five stale keys per flip, merge sorted diagonals at the round boundary; `touched[]` hardening. | `st.Flipped` and `st.RejMove` **identical** to S6's on all three maps (decision-identity, not merely "still works"). `Test28` PASS. `iter` count and `edgeVisits` in the log; `FlipMs` should drop, and that is the only metric allowed to change here. |
| **S8** | **goldens** — §1.7(b): `--goldenhash`, the `GoldenHashes` table, the context-pinned assertion, and promote `Test28` (created in S1b) into the committed suite assertion. | `Test28` PASS. Goldens captured and re-verified across 3 repeats × 2 processes. **A deliberately perturbed constant** (e.g. `QuadXzMaxInteriorAngleDegrees` 175 → 174) must make the golden assertion FAIL — a gate that cannot fail is not a gate. Revert the perturbation. |
| **S9** | **calibration** — §3 in full, driven by the §1.3.1 flags (`--cleanup-deadline 0 --dec-guard-mult 1000000`, `--dec-sweep-cap 1`/`2`): unbounded run, **all four** per-pass ms/unit counters, `mult` derivation, predicted-vs-measured check, §2.2 table. | `deadline exceeded = 0` on all three maps (A15). Predicted vs measured worst case within ±25% on the slowest map. All of §2.1 PASS. If A7-A11 fail, tune `DecimateGuardMultiple` (and, if the Refine/Relax budgets are the binding one per §1.3, `RefineSweepCap`/`RelaxBudgetFloor`) and re-run — never a threshold, never a clock. |
| **S10** | **docs** — README: the `NavMeshVoxelCleanup` settings row (:392), the "wall-clock budgeted" note (:1257-1264) replaced with the budget contract, the §2.2 table refreshed, and the honest cross-machine wording (risk 4). `TODO.md`: item 1 → done with the record; item 6 → done (per-pass ms landed in S1); note the Finding A mismatch for follow-up. `DeterminismResearch.md`: an "implemented" footer listing the corrections (Finding A: `FaceSlopeDeg`/`MaxCleanupSlopeDegrees` dead and `MaxFanNormalSpreadDegrees` inert; Finding B: five stale keys, not two; Finding C: `touched[]` is latent, not live). | `rg -i "wall-clock|CleanupMaxMs|budget=" README.md TODO.md` returns nothing stale. The README's setting name matches the code's field name. Every number in the refreshed table is traceable to a saved log under `…\det_research\`. |

**Sequencing rationale.** instrument → remove the cause → give it a size → make the logs reproducible →
make the decisions cross-machine-stable → optimise → gate them → calibrate → document. The two
orderings that matter and why:

* **S1 and S1b before S2**: without counters, a quality change at S2 is unattributable — was it the gate
  removal, or a different binary? With S1 you already know the work each pass did. S1b exists so S2 has a
  concrete failing-then-passing observable (`Test28`) rather than a metric that can drift inside its
  noise band.
* **S6 before S7**: S7's decision-identity assertion is only meaningful if the flip counts are already
  frozen by the cos conversions having been verified exact.
* **S8 before S9**: the calibration loop changes `mult` repeatedly; the golden hashes must exist *first*
  so that each iteration is provably a budget change and nothing else.
* **S9 measures all four budgets**: because Refine and Relax can bind (§1.3), tuning only
  `DecimateGuardMultiple` can leave a quality regression unexplained. The `--dec-guard-mult` /
  `--dec-sweep-cap` flags make each budget isolatable without a rebuild.

**Do not start this round until the roof-fix round (TODO item 2) has landed and the tree is committed.**
S0's baseline is only meaningful against a known-good binary, and the roof fix changes the mesh that
`NavMeshCleanup` sees — so a baseline captured before it would make every §2.1 comparison wrong.

---

## 6. Appendix — corrections to `DeterminismResearch.md`, for the record

The report's conclusions hold (delete the four gates; size the work with counts; cos-space the
decision path; don't restructure `Decimate`). Four specifics in it are wrong against this tree and are
corrected above; recording them so the next reader doesn't re-derive them:

| report claim | reality |
|---|---|
| "the gates are at lines 443, 1363, 1585, 1920" | correct — all four confirmed. |
| "convert `FaceSlopeDeg` (1852), which drives `MaxCleanupSlopeDegrees`" | both are **dead code** with zero callers repo-wide. Nothing to convert (§0.3). |
| "convert the `MaxFanNormalSpreadDegrees` predicate" | that predicate **cannot fire** at 120° (the `180 - na` fold bounds `na ≤ 90`). No conversion, and fixing it is out of scope (§0.3). |
| "a flip only destroys two edges … filter the two stale keys per flip" | **five** keys leave the live list per flip: the shared edge plus the four ring edges. The diagonal must not be added until the next round (§0.4). |
| "`touched[]` … uses the pre-rebuild `nf` while the map may now be smaller" — a live bug | `nf` is invariant (`TryFlip` rewrites face contents, never adds/removes faces), so the reset is correct today. Latent, not live (§0.5). |

### 6.1 Independent validation (v2)

The v2 corrections come from `determinism-plan-validation.md` (model/provider `opencode-go` /
`longcat-2.5-preview-free`). Verdicts against the v1 plan:

| # | claim | verdict | v2 action |
|---|---|---|---|
| 1 | four gates are the only elapsed-time comparisons | CONFIRMED (output-affecting); wording imprecise — 2 reporting comparisons + 1 log throttle | §0.1 + §1.1 scoping note |
| 2 | counter sites / C0 derivations match the loops | CONFIRMED | unchanged |
| 2 | three of four budgets derived, only Decimate binds | **REFUTED** — only FlipEdges is derived; Refine ~35 % tighter, Relax binds when C0 > 65536 | §1.3 role column + "which budget binds" paragraph + §3/§2.2 "measure all four" |
| 3a | `MaxFanNormalSpreadDegrees` inert | CONFIRMED | unchanged (§0.3) |
| 3b | `FaceSlopeDeg`/`MaxCleanupSlopeDegrees` dead | CONFIRMED (+ `TriQuality`) | unchanged (§0.3) |
| 3c | 5 stale keys, diagonal deferred | CONFIRMED | unchanged (§0.4) |
| 3d | `touched[]` latent | CONFIRMED | unchanged (§0.5) |
| 4 | S0–S10 internally consistent | **REFUTED** | Test28 → S1b; S0 flag split; A17/S2 grep scope; S9 runtime-override mechanism (§1.3.1) |
| 5 | missed nondeterminism sources | no mesh-affecting miss; 4 secondary items | §0.6 + S5 culture sweep + S5b `RcBuilder` guard |

Also fixed in v2: the §1.8 typo in the §2.2 table (`§1.8` → `§1.3`), and the harness location is now
named canonically (`tools/TiledCoreHarness`, temp copy byte-identical).