# Determinism implementation — evidence report (S0–S10)

Project: BetterBannerlordNavmeshing · implementation of `DeterminismImplementationPlan.md` (v2)
Working dir: `C:\Users\Sheepbitter\AppData\Local\Temp\opencode\det_impl\`
Read-only on the real repo; no commits; no deploys.

Deliverable patch: **`det\patch.py`** (+ `det\files\*.before|.after` payloads). Applies on the stacked
base, verified clean (see "Patch verification").

---

## Base tree (stacked)

`git archive 9f7477a` → `base\`, the five fixE-chained files under `src\`, `fixE\patch.py` run (which
chains fixB → fixC → fixE), patched files copied back to the work root. Verified:

| check | result |
|---|---|
| `base\{5 files}` == `src\{5 files}` (fixE output copied back) | SHA-256 equal on all 5 |
| `build_s0\src\TiledNavMeshBuilder.cs` == `base\…` + a `partial` shim only | 4-line diff, `partial` keywords only |
| clean stacked base builds a harness copy | `Build succeeded`, 0 errors |

`fixC2\patches\fixE\patch.py` is at `…\Temp\opencode\fixC2\patches\fixE\patch.py` (**not** in the
repo) and chains fixB → fixC → fixE via `runpy`.

Rigs created: `capture.ps1` (matrix runner), `summarise.ps1` (median + range per metric), `cal.ps1`
(S9 calibration). Build dir `build\` = a **copy** of `tools\TiledCoreHarness\Program.cs` + `src\` +
a csproj compiling only the cleanup's files. The shared `tiled-core-harness` dir is never built in.

**Three corrections to the previous attempt's baseline rig, all material:**

1. It passed the 3-path CC_20 v2 fixture as ONE space-joined string, so all 18 `cc20v2` logs contain
   `snapshot mode ERROR: NotSupportedException` and are void. The harness's `src.Count == 3` branch
   needs three separate argv entries.
2. **`--cleanup` takes the NEXT argv as its on/off value.** `--cleanup --quality` therefore makes
   `--cleanup` swallow `--quality` and the run silently becomes a plain build. Every calibration and
   quality invocation must write `--cleanup on` explicitly. (This bit the first `capture.ps1` too: the
   `--quality`/`--xzprobe` rows of the S0/S4 matrices were plain builds until it was caught and
   re-captured by `qcap.ps1`.)
3. `--quality` and `--xzprobe` are mutually exclusive early-returns, so they need separate
   invocations — as the plan says.

---

## S0 — baseline (pre-change, stacked base)

`invariants=PASS`, `concaveQuads=0`, `xzConcave/xzOrientBad/xzStraight/xzReflex = 0` on all four;
`components X->Y (kept)` everywhere.

| map | build: F | quads | worst quad / XZ | thin after | needles after | islands |
|---|---|---|---|---|---|---|
| Birtu | 44481 | 605 | 175.2694 / 174.9778 | 353 | 8 | 5 |
| CC_20 v1 | 241443 | 1842 | 178.3817 / 174.9233 | 1034 | 36 | 15 |
| CC_20 v2 | 241424 | 1850 | 178.3817 / 174.9233 | 1033 | 35 | 15 |
| Basamu | 492399 (median; **range 1079**) | 2978 | 178.7752 / 174.9575 | 1504 | 63 | 1 |

### The pre-change nondeterminism, quantified (3 repeats, same binary + settings)

| map | metric | rep1 | rep2 | rep3 | drift |
|---|---|---|---|---|---|
| Birtu | decimated / quads / rejMove | 6724 / 605 / 200588 | 6724 / 605 / 200588 | 6724 / 605 / 200588 | **none** |
| CC_20 v1 | decimated / quads / rejMove | 29057 / 1842 / 1448120 | 29057 / 1842 / 1448120 | 29057 / 1842 / 1448120 | **none** |
| CC_20 v2 | decimated / quads / rejMove | 29067 / 1850 / 1447963 | 29067 / 1850 / 1447963 | 29067 / 1850 / 1447963 | **none** |
| **Basamu** | **decimated** | 36623 | 35639 | 34634 | **5.7 %** |
| **Basamu** | **refined** | 9603 | 8345 | 7085 | **35.6 %** |
| **Basamu** | **cand** | 1016851 | 937627 | 1011453 | **8.4 %** |
| **Basamu** | **rejMove** | 1438984 | 2367265 | 2370672 | **64.8 %** |
| **Basamu** | **quads** | 3010 | 2978 | 2969 | **1.4 %** |
| **Basamu** | **thin after** | 1520 | 1502 | 1504 | 1.2 % |

**Finding: the clock was only binding on Basamu.** Birtu and both CC_20 fixtures were byte-stable
across three repeats on this machine, so on those three the wall-clock gate was never the limiter —
which is why the plan warned that "if the repeats agree perfectly, record that; the gates were not
binding on this machine" and that §3.4 must then read `mult ≤ 1`. Basamu is the map the README
already identified as the drifting one, and it drifts hard: `rejMove` swings 65 % and `refined` 36 %,
moving `quads` by 41 (1.4 %) and `thin after` by 18. Feeds S9 directly.

(Correcting the previous attempt's log-derived numbers: they came from a different build and are not
comparable. These are from this session's own `capture.ps1` rig.)

---

## S1 — instrumentation

`Stats` gains the 18 work counters (`DecimateGuardEvals/Cand0/GuardBudget/BudgetHit/Sweeps`,
`RefineFaceVisits/FaceVisitBudget/Sweeps`, `RelaxEvals/Cand0/Budget/Sweeps`,
`FlipEdgeVisits/EdgeVisitBudget/E0/Iters`, plus `FlipRebuildMs/FlipRebuilds`), the per-pass timers
(`RefineMs/DecimateMs/RelaxMs/FlipMs/QuadMergeMs/CleanupMs`), the `Deadline` class and `Budgets`.

The per-pass `*Ms` are `Stopwatch` reads into `long` fields — commented at each site as **log only,
not deadlines**, so a later `Stopwatch` grep cannot mistake them for removed gates. This also lands
TODO item 6's per-pass timings.

---

## S2 — the four wall-clock gates deleted; Test28 PASSES

A17 verified: the only live `ElapsedMilliseconds >` in `NavMeshCleanup.cs` is inside `Deadline`
(plus the four comments recording what each gate used to be). The two reporting-only sites in
`TiledNavMeshBuilder.cs:1184-1185` and the 2 s progress throttle are intentionally untouched.

| pass | was | now |
|---|---|---|
| `Decimate` | `watch.ElapsedMilliseconds > maxMs` | `guardEvals >= guardBudget` |
| `RelaxThin` | `watch.ElapsedMilliseconds > maxMs` | `relaxEvals >= relaxBudget` |
| `RefineLongThin` | `watch.ElapsedMilliseconds > maxMs` | `faceVisits >= faceVisitBudget` |
| `FlipEdges` | `watch.ElapsedMilliseconds > maxMs` | `edgeVisits >= edgeVisitBudget` |

The `refineUsed` cascade is gone. Each replacement also renames `timeUp` → `budgetUp`; the commit
path is unchanged, so a budget-truncated sweep is still `CompactFaces`-ed exactly as a clock-truncated
one was.

```
== Test 28: cleanup determinism (double-run byte compare) ==
  input: V=3422 F=6505
  ok  verts byte-identical (10056 floats)
  ok  tris byte-identical (19107 ints)
  ok  QuadPairs byte-identical (2 pairs)
  ok  37 deterministic Stats fields + QuadXzWorstAngle identical
  ok  DecimateGuardMultiple=1 self-consistent (5036 guardEvals, budgetHit=1)
  ok  DecimateGuardMultiple=64 self-consistent (12705 guardEvals, budgetHit=0)
  ok  rough probe at multiple=1 floorDiv=100000: cand0=1481 guardBudget=1481 guardEvals=1481 budgetHit=1
  RESULT: CLEANUP DETERMINISM PASS
```

**Two fixture-design findings:**

1. The plan's suggested `MakeGrid(80f, 20f, 0.5f, …)` yields **V=51 F=61** — the cleanup has nothing
   to do, so every determinism assertion passes vacuously. Scaled to
   `MakeGrid(400f, 200f, 0.25f, …)` + `MaxErrorMeters=0.02`, `DetailSampleDistMeters=0.15` →
   V=3422 F=6505.
2. **The `F0/2` floor can mask the budget.** At `multiple=1` the budget is `C0 + F0/2`; on this mesh
   `F0/2` = 3439 vs a converged 1137 evals, so `budgetHit=0` at *every* multiple — the assertion was
   testing the floor, not the budget. The "does it bite" probe therefore also raises
   `DecimateGuardFloorDivisor` on a hash-perturbed rough surface. **This is a property of the shipped
   formula, not a test artefact**: a mesh whose converged work is below `F0/2` can never be truncated
   by the multiple. Feeds S9.

### Pre-existing, NOT caused by this change

`Test 3`'s RcRasterizations neutrality assertion reports `DIFF`: it expects
`e016e53886cb9585`/`ba807f993fde10fc` (V=62 F=58 E=117) but the stack produces
`526cae85e73a3189`/`1ff579c820b6ed48` (V=53 F=49 E=99). **Verified pre-existing** — the unmodified
pre-change build `build_s0\` emits exactly `526cae85e73a3189`/`1ff579c820b6ed48`. The harness constant
is stale for this stack (fixB/fixC/fixE changed the pre-cleanup mesh Test 3 measures). It has not
*moved*; per the plan's "if it moves, investigate, do not re-baseline", reported and left alone.

---

## S3 — settings plumbing

`CleanupMaxMs` → `CleanupDeadlineMs` (DIAGNOSTIC ONLY; ≤ 0 disables the warning), plus
`CleanupDecimateGuardMultiple = 8` and seven calibration-only overrides. Every new flag round-trips
into the `settings:` line, which now echoes the effective `Budgets` so no captured log can be mistaken
for the default configuration.

```
settings: cs=… cleanup=true budgets=[Budgets[deadlineMs=6000 decGuardMult=8 decFloorDiv=2
  relaxFloor=65536 flipVisitMult=8 caps=16/8/3/6]]
```

## S4 — budgets enforced

`budgetHit=1` on **all four maps** (guardEvals == guardBudget exactly), so the budget is genuinely
calibrated, not slack:

| map | decCand0 | guardEvals / budget | hit | sweeps | refineVisits / budget | sweeps | relaxEvals / budget | flipVisits / budget | iters |
|---|---|---|---|---|---|---|---|---|---|
| Birtu | 9237 | 101680 / 101680 | 1 | 12/16 | 374377 / 406677 | 8/8 | 16224 / 82840 | 293732 / 587464 | 3/6 |
| CC_20 v1 | 39761 | 464549 / 464549 | 1 | 12/16 | 1399208 / 2143674 | 5/8 | 60050 / 145278 | 1935625 / 3097000 | 4/6 |
| CC_20 v2 | 39764 | 464648 / 464648 | 1 | 12/16 | 1399397 / 2143928 | 5/8 | 60053 / 145289 | 1935420 / 3096720 | 4/6 |
| Basamu | 73208 | 879176 / 879176 | 1 | 12/16 | 4348988 / 4348988 | 8/8 | 126574 / 218924 | 4026435 / 6442296 | 4/6 |

Note the asymmetry the plan predicted: Decimate's budget binds everywhere, but on Basamu the
**Refine** budget binds too (`4348988 / 4348988`, sweeps at the 8 cap). See S9.

---

## S5 — deterministic diagnostics + NavMeshLog clock seam

* `DecimDiag` is now all-integer and written **after** the sweep loop (so it covers every exit path
  including the budget break) and is fully counter-derived:
  `decimate: faces0=55569 csrVerts0=34929 cand0=9237 guardBudget=101680 sweeps=12/16 guardEvals=101680 budgetHit=1 removals=6722 rejMove=74663 rejFan=0 rejRing=436 rejTopo=19512`
* The cleanup log line gains a `[det v1]` tag and a `work:`/`ms:` block; all pre-existing fields keep
  their order and names so old and new lines read side by side.
* `NavMeshLog`: `Initialize(bool, string, DateTime? stamp = null)` plus a `NowProvider` seam. The
  banner, the **filename** and every line prefix all derive from one `_runStamp`. No stamp ⇒
  byte-identical to before. The class's `try/catch` structure is untouched, so it still never throws.

### Optimisation applied here (S0.6 item 1): InvariantCulture on the new formatting
All new log fields are integers or existing invariant-formatted values; no `.ToString("F2")` without
`InvariantCulture` was introduced. `NavMeshLog`'s three existing formats already pass
`InvariantCulture`.

## S5b — dead `RcBuilder.BuildMultiThread` guard

`[Obsolete("…returns results in nondeterministic completion order…")]` plus a comment naming the
`ConcurrentQueue → ToList()` completion-order trap and the safe alternative. The build emits the
expected `warning CS0618` at its sole internal caller, which **confirms the guard is live and that the
method really is dead**; `RcConfig.CalcBorder` (the only product reference to `RcBuilder`) is
unaffected. `Build succeeded`, no mesh change.

---

## S6 — cos-space conversions: **A16 = 0 disagreements**

All ten decision-path `Acos` sites converted, with the mandatory shadow assert (both predicates
evaluated, disagreements counted) live.

The shadow build did its job — it caught **two real defects**:

1. **Radian/degree unit bug** (plan §1.5 rows 5 and 10). Both are written in the plan as
   `cos((Acos(cos) + gainDEGREES) * PI/180)`. `Math.Acos` returns **radians**, so that scales the whole
   angle as if it were degrees: a ~20° threshold became ~0.8°. Fixed by converting the *gain* to
   radians. Shadow count before the fix: **476506** (site 5) and **98394** (site 10).
2. **Inverted comparison** (plan §1.5 row 10). `acos` is decreasing, so a *better* fan (larger worst
   angle) has a *smaller* worst cos. The plan's `if (candCos <= bestCos) continue;` accepts exactly
   the wrong side; the correct test is `candCos >= bestCos`. (The plan's row-7 note gets the same
   class of flip right, and that one I followed as written.)

Both are commented at their sites with the shadow count that exposed them.

```
SHADOW bySite: 0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0   total=0
cosShadowDisagreements=0     (Birtu, CC_20 v1, CC_20 v2, Basamu, and the whole harness suite)
```

**Decision-identity confirmed:** after S6 the pre-cleanup neutral hashes are back to the pre-change
values (`526cae85e73a3189` / `1ff579c820b6ed48`), and `Test 3`'s `run1/run2` pair is unchanged.

The `FanWorstAngle` twin is kept (angle-valued) purely for the shadow comparison and reporting;
`QuadWorstAndPerimeter`'s `worst` out-param now carries **cos**, and `MergeQuads`' tie-break inverts
to "prefer the smallest worstCos" (the `perim` tie-break untouched). `MinAngle` / `WorstXzAngle` /
`NormalAngleDeg` / `AngleDeg` stay for reporting; `FaceSlopeDeg`, `MaxCleanupSlopeDegrees` and
`TriQuality` remain dead code, untouched per the plan's non-goals.

---

## S7 — FlipEdges: one sort, five stale keys, in-place merge

Two invariants are stated in the code because both are load-bearing: `edgeFaces` is **not** mutated
during a round (it stays the round-start snapshot, so `TryFlip`'s `TryGetValue` and four `UseCount`
calls see what they saw before), and `touched[]` bounds one flip per face per round (which is what
makes the surviving ascending key order equal to a full re-sort). **Five** keys are filtered per flip
(the shared edge plus four ring edges), and the new diagonal is merged only at the round boundary.

`TryFlip` gained `out int diagX, out int diagY`, set before every early return.

**Decision-identity — measured against a pre-S7 build of the same tree (`build_s6\`):**

| map | S7 flipped / rejMove / iters / edgeVisits | S6 flipped / rejMove / iters / edgeVisits | identical |
|---|---|---|---|
| Birtu | 1075 / 188669 / 3 / 293732 | 1075 / 188669 / 3 / 293732 | YES |
| CC_20 v1 | 5756 / 1287721 / 4 / 1935625 | 5756 / 1287721 / 4 / 1935625 | YES |
| CC_20 v2 | 5744 / 1287719 / 4 / 1935420 | 5744 / 1287719 / 4 / 1935420 | YES |

Only `FlipMs` changed. Measured (first cut vs the in-place merge below vs S6):

| map | S6 FlipMs | S7 v1 (allocating merge) | S7 final (in-place merge) |
|---|---|---|---|
| Birtu | 132 | 139 (**slower**) | **111** |
| CC_20 v1 | 997 | 1156 (**slower**) | **998** |
| CC_20 v2 | 1030 | 949 | 957 |

**Optimisation note (real finding):** the obvious merge — build a separate merged list — allocates and
copies an `n+m` list *every round* (~1.9 M longs/round on CC_20) and made the pass **slower** than the
5 sorts it replaced. The shipped version merges **backward in place** into the owned array, one pass,
no allocation. `keys` is a plain owned `long[]` + `len` rather than a `List<long>` for the same reason.

---

## §2.2 Quality matrix — before (S0) vs after (S4), same fixtures, `--quality`/`--xzprobe`

All acceptance bands **PASS**. `invariants=PASS`, `concaveQuads=0`, `xzConcave/xzOrientBad/xzStraight/
xzReflex = 0` on all four maps, before and after.

| metric | Birtu before → after | Δ | CC_20 v1 before → after | Δ | CC_20 v2 before → after | Δ | Basamu before → after | Δ | verdict |
|---|---|---|---|---|---|---|---|---|---|
| thin (<5 deg) | 289 → 292 | +1.04 % | 885 → 885 | 0 % | 887 → 887 | 0 % | 1272 → 1269 | −0.24 % | A7 (≤ +5 %) PASS |
| needles (alt<0.05, edge>0.5) | 8 → 8 | 0 % | 35 → 35 | 0 % | 34 → 34 | 0 % | 62 → 62 | 0 % | A8 PASS |
| meanQuality | 0.7227 → 0.7227 | 0 | 0.7406 → 0.7406 | 0 | 0.7406 → 0.7406 | 0 | 0.7463 → 0.7465 | +0.03 % | A9 (≥ −0.01) PASS |
| long-thin (aspect>8, longest≥4 m) | 748 → 750 | +0.27 % | 1902 → 1896 | −0.32 % | 1905 → 1899 | −0.31 % | 2969 → 2939 | −1.01 % | A11 PASS |
| quads | 605 → 606 | +0.17 % | 1841 → 1836 | −0.27 % | 1849 → 1844 | −0.27 % | 2961 → 2951 | −0.34 % | A4 (≥ −5 %) PASS |
| worst quad angle (plane) | 175.2694 → 175.2694 | 0 | 178.3817 → 178.3817 | 0 | 178.3817 → 178.3817 | 0 | 178.7752 → **179.5883** | +0.45 % | A6 (< 179.9) PASS |
| worst XZ angle | 174.9778 → 174.9778 | 0 | 174.9233 → 174.9233 | 0 | 174.9233 → 174.9233 | 0 | 174.9575 → 174.9575 | 0 | A6 (< 175.0) PASS |
| components | kept | | kept | | kept | | kept | | A3 PASS |
| islands | 5 | | 15 | | 15 | | 1 | | A2 PASS |
| boundaryMaxMove | 0.35 m | | 0.35 m | | 0.35 m | | 0.35 m | | A10 PASS |
| invariants | PASS | | PASS | | PASS | | PASS | | A1 PASS |

**On Basamu's `worstQuadAngle` 178.7752 → 179.5883 (+0.81°).** This is the one visible geometry
movement, and it is *tighter to the ceiling, not through it*: the plane ceiling is
`QuadConvexMaxInteriorAngleDegrees = 179.9`, so the margin goes 1.1248° → **0.3117°**, and the XZ
ceiling margin goes 0.9425° → **0.0425°**. Both remain genuinely binding, which is what A6 is
actually testing ("if either comes back with slack, a guard changed") — neither came back with slack.
The cause is mechanical and expected: with the clock gate gone, Decimate now runs a fixed, slightly
different amount of work than it used to get truncated at, so a different (still valid, still convex,
still under the ceiling) set of quads is chosen. `xzReflex`/`xzStraight`/`xzConcave` remain 0, i.e.
the engine's validator-visible frame is unchanged.

## S9 — calibration (plan §3)

### 3.5 Calibration record (measured; unbounded Decimate guard, `DeadlineMs=0`)

| map | F0 | E0 | C0 | guardEvals@∞ | DecMs | ms/guardEval | rebuildMsPerSweep | RefineFaceVisits | RefineMs | ms/faceVisit | RelaxEvals | RelaxMs | ms/relaxEval | FlipEdgeVisits | FlipMs | ms/edgeVisit | flipRebuildMs (share) | **total** |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Birtu | 55569 | 73429 | 9237 | 116594 | 417 | 0.003577 | ~19 | 374377 | 211 | 0.000564 | 16220 | 223 | 0.013748 | 293716 | 114 | 0.000388 | 34 (30 %) | **1023** |
| CC_20 v1 | 292923 | 386965 | 39761 | 654582 | 2676 | 0.004088 | ~100 | 1399208 | 4057 | 0.002899 | 60080 | 968 | 0.016112 | 1934825 | 935 | 0.000483 | 291 (31 %) | **8883** |
| CC_20 v2 | 292913 | 386924 | 39774 | 654544 | 3459 | 0.005285 | ~101 | 1399174 | 4658 | 0.003329 | 60071 | 1216 | 0.020243 | 1934620 | 1361 | 0.000703 | 473 (35 %) | **11046** |
| Basamu | 587025 | 805262 | 73208 | 1261960 | 8535 | 0.006763 | **380** | 4348988 | 18123 | 0.004167 | 126569 | 2683 | 0.021198 | 4026310 | 2873 | 0.000714 | 1270 (44 %) | **32770** |

`rebuildMsPerSweep` measured directly per §3.2 on the biggest map:
`ms(cap=1)=710`, `ms(cap=2)=1090` ⇒ **380 ms/sweep** on Basamu (F0=587025). Cross-check from the
shipped run: sweeps 1→12 with decMs 710→3804 ⇒ 281 ms/sweep. (Birtu/CC_20 per-sweep values are scaled
from the same measurement; the plan only requires the direct measurement on the biggest map.)

### The `mult` derivation — and why it fails, which is the real finding

Applying §3.4 verbatim, `mult = floor((6000 − otherPassesMs − floorMs) / (msPerGuardEval × C0))`:

| map | otherPassesMs | floorMs | denominator | derived `mult` |
|---|---|---|---|---|
| Birtu | 548 | 99 | 33.04 | **162** |
| CC_20 v1 | 5960 | 599 | 162.54 | **−4** |
| CC_20 v2 | 7235 | 774 | 210.21 | **−10** |
| Basamu | 23679 | 1985 | 495.11 | **−40** |

`mult` comes out **negative on three of the four maps**. Per the plan's own instruction
("If the unclamped value is ≤ 1 … Do not silently pick 1 and call it a match: record the finding … and
say so explicitly"), the finding is:

> **The plan's premise that `DecimateGuardMultiple` is the primary time knob is wrong on this
> machine.** `otherPassesMs` alone (Refine + Relax + Flip + QuadMerge) is 5960 ms on CC_20 and
> **23679 ms on Basamu** — i.e. Refine *by itself* already exceeds the 6000 ms target before Decimate
> spends anything. Refine is 42–55 % of the cleanup on the three big maps (Birtu is the exception at
> 21 %, because it is small enough that nothing binds). No value of `DecimateGuardMultiple` can bring
> the worst case to 6000 ms while `RefineSweepCap` stays at 8.

Where the work actually sits at the shipped `mult=8`:

| map | total | refine | decimate | relax | flip | refine share |
|---|---|---|---|---|---|---|
| Birtu | 985 | 226 | 369 | 212 | 112 | 21 % |
| CC_20 v1 | 8528 | 4282 | 2031 | 1005 | 969 | 46 % |
| CC_20 v2 | ~9000 | ~4300 | ~2400 | ~1200 | ~1000 | ~42 % |
| Basamu | 23863 | 15340 | 3804 | 2015 | 2145 | **64 %** |

This is precisely the risk the plan's v2 correction (§6.1 claim 2, "REFUTED — only FlipEdges is
derived; Refine ~35 % tighter, Relax binds when C0 > 65536") warned about, and it is now measured:
**on Basamu the Refine budget is the binding one** (`refineVisits 4348988 / 4348988`, sweeps at the 8
cap), not the Decimate budget — even though `budgetHit=1` is reported for Decimate on every map.

### A15 outcome — reported honestly, not papered over

`deadline exceeded` at the shipped `CleanupDeadlineMs=6000`: **0 on Birtu, 1 on CC_20 v1, 1 on CC_20
v2, 1 on Basamu.** A15 as literally written is **NOT met** on three maps, and cannot be met by tuning
`DecimateGuardMultiple`.

Two things make the comparison non-equivalent, and both should be recorded:

1. **The old `CleanupMaxMs=6000` was never a total-run budget.** It was *Decimate's* window; Refine
   got its own 6000 ms, Relax `6000+1500+refineUsed`, Flip `6000+2500+refineUsed`, all measured as
   absolute elapsed since run start. The old **worst-case envelope was `8500 + refineUsed` ms
   ≈ 14500 ms**, not 6000 ms. Comparing a new whole-run number against the old 6000 compares two
   different quantities.
2. Under that like-for-like envelope (~14500 ms) the deterministic build fits Birtu (1023 ms) and
   CC_20 (8883 ms) comfortably, but **Basamu at 32770 ms does not** — because unbounded-convergence
   Refine does 4.35 M face evaluations that the old clock truncated at 6 s.

**Recommendation (proposed, NOT implemented — it would violate the "keep the sweep caps" non-goal):**
the lever is `RefineSweepCap` (or the Refine budget), which is what §3.6 names as "a real quality
lever". To land Basamu's *whole run* at ≈6000 ms with `DecimateGuardMultiple` left at 8 needs Refine
cut to ≈3000 ms, i.e. roughly `RefineSweepCap 8 → 1–2` — a large quality change that must be
calibrated against the §2.1 bands in its own round. `CleanupDeadlineMs` should in the meantime be
re-documented as a *warning threshold on the whole run*, not as the old Decimate window.

### The next performance lever — proposed, not implemented

`rebuildMsPerSweep` is **380 ms/sweep on Basamu** and `flipRebuildMs` is **44 % of the flip pass**
(1270 of 2873 ms; 30–35 % on the other maps). Both are the *same* mechanism: the per-round
`BuildEdgeFaces(tris)` inside `FlipEdges`, which rebuilds a `Dictionary<long, List<int>>` over ~805 k
edges from scratch every round (5 rebuilds on Basamu). `FlipEdges` still does **one rebuild per
round** by design — invariant 1 in the code requires `edgeFaces` to stay the round-start snapshot,
so this cannot be fixed by mutating the map in place without changing which flips happen.

The proposed next lever is therefore an **incremental / dirty-region rebuild**: maintain the
edge→faces map through a round, applying each accepted flip's five removals and one insertion, and
have `TryFlip` read it at a point where the incremental state is provably equal to the snapshot
(e.g. by rolling the round's flips into a small overlay that `TryFlip` consults first). This is a
behaviour-preserving refactor with its own decision-identity proof (`Flipped`/`RejMove` must match),
which is why it is out of scope for this round. **Not implemented here**, per the assignment.

## S8 — self-test + golden hashes (A13, A14)

`Test28_CleanupDeterminism` (S1b/S8) is registered after `Test27` and prints
`RESULT: CLEANUP DETERMINISM PASS` (see S2).

`--goldenhash` builds once, prints the three FNV-1a hashes in copy-pasteable form, and asserts them
against the `GoldenTable`. It reuses the harness's **existing** `Hash(float[])` / `Hash(int[])` —
the same helpers `Test 3`'s `e016e53886cb9585` / `ba807f993fde10fc` constants come from — so the two
sets of constants stay comparable.

### A14: 3 repeats × 3 separate processes per fixture, all identical

| map | fixture | varsHash | trisHash | quadHash | quadPairs | F |
|---|---|---|---|---|---|---|
| Birtu | `mp_tdm_Birtu_navmesh` | `7e198fa9684a1089` | `5e51880fea25110d` | `2c04fe8d17e4cc93` | 1212 | 44483 |
| CC_20 v1 | `CC_20_siege_navmesh` | `0ded2d149bd50c78` | `dff73d5f3ae9bcf6` | `f9241d97dd2522bb` | 3672 | 241539 |
| CC_20 v2 | `snapshot_v2_*` | `b05377aaeae6bca6` | `d5ae4a2384968e73` | `19f4c1d713bf77c2` | 3688 | 241518 |
| Basamu | `mp_battle_Basamu_village_navmesh` | `7c03be757ec35941` | `86b29fad003361ec` | `6cbd8475b23fc687` | 5902 | 497408 |

12/12 hashes identical across 3 fresh processes per fixture (the cross-process repeat the plan
requires, to catch state leaking between builds — the harness has been bitten by `RcRasterizations`
scratch buffers before).

### Context-pinned, and proven able to FAIL

The key is the fixture's context (terrain file name + length + last-write ticks), so a refreshed
snapshot reports `GOLDEN-UNRECORDED` rather than a spurious mismatch; a mismatch can then only mean
"the code changed the mesh for the same input". A refreshed fixture invalidating its goldens is by
design.

**Perturbed-constant check** (the plan's "a gate that cannot fail is not a gate"). `QuadXzMaxInteriorAngleDegrees`
175 → 174 in a throwaway copy:

```
GOLDENHASH vars=7e198fa9684a1089 tris=5e51880fea25110d quads=a97da6e36b514d3b quadPairs=1206 F=44483
GOLDEN-MISMATCH key=mp_tdm_Birtu_navmesh_snapshot_terrain.bin|592920|639263973584410258
  RESULT: GOLDEN HASH FAIL        (exit code 1)
```

`quadPairs` 1212 → 1206, the gate fails and returns exit 1. The perturbation was made in a throwaway
directory and **reverted**; `work\` and the patch payload were never touched. All four fixtures
re-verify `GOLDEN-OK … PASS` at exit 0 afterwards.

---

## A12 — harness suite (pre- vs post-change, same stack)

Both suites: **20/20 verdict lines PASS, 0 FAIL**, plus the new Test28 line (21 post-change).
`Test 27`'s three sub-cases still behave exactly as required
(`partB(convex accepted) True, partB(concave refused) True, partB2(XZ-reflex refused) True,
partC True, partC2(XZ frame) True -> PASS`), and `Test 3`'s double-run is
`RESULT: DETERMINISM PASS` with `run1 == run2`.

**Honest deviation from A12's literal wording.** A12 asks for "every `PASS` line *identical*" to the
pre-change log. The verdicts are all still PASS, but six lines carry slightly different mesh sizes,
because those tests run the **cleanup on synthetic meshes** and the cleanup now does a fixed amount of
work instead of a clock-truncated amount:

| test | before | after | Δ |
|---|---|---|---|
| topology detail ON | V=540 E=1540 F=1001 | V=537 E=1531 F=995 | −0.6 % |
| weld/tolerance block (5 lines) | V=44380 F=87429 E=131808 | V=44411 F=87488 E=131898 | +0.07 % |
| invariants (87429-face case) | F=87429 | F=87488 | +0.07 % |
| invariants (1005-face case) | F=1005 | F=999 | −0.6 % |

Every one of those lines still ends `-> PASS`, and all of them are **deterministic now** (identical
across repeats), which they were not before. This is the expected consequence of changing *how much
work happens* and is in the same family as the Basamu `worstQuadAngle` movement above: a different,
still-valid, now-fixed choice. Flagged rather than papered over.

## A17 — no gates left

`rg "Elapsed(Milliseconds|Seconds) >"` over the patched product files:

| file | hits | inspected |
|---|---|---|
| `NavMeshCleanup.cs` | 1 | inside `Deadline.Poll` (the diagnostic flag setter) + 4 comments recording what each gate used to be |
| `TiledNavMeshBuilder.cs` | 2 | `:1184-1185` `r.ElapsedMs < tileMsMin / > tileMsMax` — the min/max reduction for the `tileMs min/avg/max` log line. Reporting only. |
| `TiledNavMeshBuilder.cs` | 1 | `:1096` `elapsed - lastProgressMs >= 2000` — the 2 s progress-log throttle, documented as observational. Gates a `log(...)` call only. |
| `RcBuilder.cs`, `SceneNavMeshExporter.cs` | — | assignments into `result.*Ms` / log strings only; no comparison feeds control flow. |

No elapsed comparison that feeds control flow survives outside `Deadline`. **A17 PASS.**

## S10 — docs (status)

**Not done in this round, deliberately.** S10 rewrites `README.md` / `TODO.md` /
`DeterminismResearch.md` in the real repo, which is read-only here and which the assignment scopes out
("read-only on the real repo; no commits"). The facts S10 needs are all recorded above. The two
README claims this work *invalidates* and which must be corrected when it is applied:

1. `README.md:1257-1264`'s "the cleanup is **wall-clock budgeted**" note and the
   `CleanupMaxMs` setting name (`README.md:392` `NavMeshVoxelCleanup` row) — both now wrong. The
   setting is `CleanupDeadlineMs` and it is diagnostic only.
2. `README.md:25-26`'s stated rationale that a fan whose normals spread more than
   `MaxFanNormalSpread` is never joined. Per the plan's Finding A that predicate **cannot fire** (the
   `180 - na` fold bounds `na ≤ 90`, so `na > 120` is unreachable) — `MaxFanNormalSpreadDegrees` is
   inert. Fixing the code changes output, so it stays a follow-up; only the prose should change.
   `FaceSlopeDeg`, `MaxCleanupSlopeDegrees` and `TriQuality` are dead code with zero callers.

---

## Acceptance summary

| # | criterion | verdict |
|---|---|---|
| A1 | `invariants=PASS` | **PASS** (4/4 maps, before and after) |
| A2 | island count unchanged | **PASS** (5 / 15 / 15 / 1, both sides) |
| A3 | `ComponentsBefore == ComponentsAfter` | **PASS** (`(kept)` on all) |
| A4 | `quads` ≥ baseline − 5 % | **PASS** (worst −0.34 %) |
| A5 | quad frame guards all 0 | **PASS** (concave/xzConcave/xzOrientBad/xzStraight/xzReflex all 0) |
| A6 | worst quad < 179.9 and XZ < 175.0 | **PASS** (both ceilings still binding; see the Basamu note) |
| A7 | `thin` ≤ baseline + 5 % | **PASS** (worst +1.04 %) |
| A8 | `needles` ≤ +10 % and ≤ 15/60/100 | **PASS** (8 / 35 / 34 / 62, unchanged) |
| A9 | `meanQuality` ≥ baseline − 0.01 | **PASS** (0 / 0 / 0 / +0.0002) |
| A10 | `BoundaryMaxMove` ≤ 0.35 | **PASS** (0.35 everywhere) |
| A11 | long-thin recorded | **PASS** (worst −1.01 %, i.e. improved) |
| A12 | suite identical | **PASS on verdicts** (0 FAIL); six synthetic-mesh sizes move < 1 % — reported above |
| A13 | Test28 double-run + budget variants | **PASS** |
| A14 | goldens stable, and the gate can fail | **PASS** (12/12 hashes; perturbed constant → `GOLDEN HASH FAIL`, exit 1) |
| A15 | `deadline exceeded = 0` on all maps | **NOT MET as literally written** — 0 on Birtu, 1 on CC_20 v1/v2 and Basamu. Unreachable via `DecimateGuardMultiple`; see S9. |
| A16 | 0 shadow disagreements | **PASS** (0 on all maps + whole suite) |
| A17 | no gates left | **PASS** |

## Optimisations delivered

| lever | result |
|---|---|
| **cos conversion** (S6) | removes `Math.Acos` from 10 decision-path sites; the hottest (`FanWorstCos` in `TryRelax`, 25 offsets × fan per vertex) loses one `Acos` per ring pair per offset |
| **`FanWorstCos` vs `FanWorstAngle`** | the angle-valued twin is retained only for reporting and the shadow build |
| **FlipEdges single-sort** (S7) | 1 sort instead of 5; measured Birtu 132 → 111 ms |
| **FlipEdges 5-key filter + deferred diagonal** (S7) | removes the per-round re-sort's key churn; decision-identical (verified on 3 maps) |
| **backward in-place merge + owned `long[]`** (S7) | the naive merged-list version was *slower* than what it replaced (139 vs 132 ms on Birtu); the shipped version is faster than the 5-sort original. This was the single most useful optimisation finding of the round. |
| `rebuildMsPerSweep` measured | **380 ms/sweep** on Basamu; `flipRebuildMs` is **44 %** of the flip pass |

### Next lever — PROPOSED, NOT IMPLEMENTED

`FlipEdges` still rebuilds the whole `edge->faces` map once per round (`BuildEdgeFaces`: ~805 k
edges on Basamu, 5 rebuilds, 1270 ms = 44 % of the pass). It cannot be fixed by mutating the map in
place, because invariant 1 (`edgeFaces` is the round-start snapshot) is what makes the single-sort
version decision-identical. The proposed next step is an **incremental / dirty-region rebuild**:
carry the round's accepted flips as a small overlay that `TryFlip` consults, so the O(F) rebuild
happens once per pass instead of once per round. It is behaviour-preserving *in principle* but needs
its own decision-identity proof (`Flipped`/`RejMove` must match the values in the S7 table above),
which is why it is out of scope here.

---

`det\patch.py` applies to a **fresh** copy of the stacked base, all five files, no anchors missed, and
is idempotent on re-run. The patched tree then **compiles** (`Build succeeded`) and produces output
identical to the hand-verified work tree on Birtu — every geometry and counter field matches, the only
differing fields being wall-clock timings.

## Patch verification — detail

`det\patch.py` applies to a **fresh** copy of the stacked base, all five files, no anchors missed, and
is idempotent on re-run. The patched tree then **compiles** (`Build succeeded`, 0 errors) and produces
output identical to the hand-verified work tree on Birtu — every geometry and counter field matches,
the only differing fields being wall-clock timings.

The patch refuses to apply to unexpected content (it compares SHA-256 against the recorded `before`
payload and stops with a clear message), so it cannot half-apply onto a tree that is not the stacked
base.

`det\run.ps1` reproduces the **entire chain from nothing**: `git archive 9f7477a` → lay the five files
under `src\` → `fixE\patch.py` (fixB → fixC → fixE) → copy back → `det\patch.py` → build a harness
copy. Run end-to-end from a clean slate, that build then re-verifies **all four golden hashes**:

```
birtu   exit=0  GOLDEN-OK … RESULT: GOLDEN HASH PASS
cc20v1  exit=0  GOLDEN-OK … RESULT: GOLDEN HASH PASS
cc20v2  exit=0  GOLDEN-OK … RESULT: GOLDEN HASH PASS
basamu  exit=0  GOLDEN-OK … RESULT: GOLDEN HASH PASS
```

### Files touched

| file | change |
|---|---|
| `NavMeshCleanup.cs` | S1 counters + `Deadline` + `Budgets`; S2 the four gates + the `refineUsed` cascade; S4 budget enforcement + `BuildCandidateMask`/`CommitKeys` helpers; S5 counter-derived `DecimDiag`; S6 ten cos-space conversions + the shadow assert; S7 single-sort `FlipEdges` + `StaleKey`/`CommitKeys` + `TryFlip` out-params |
| `TiledNavMeshBuilder.cs` | S3 `CleanupDeadlineMs` + `CleanupDecimateGuardMultiple` + 7 overrides + `BuildCleanupBudgets`; S5 the `[det v1]` log block |
| `NavMeshLog.cs` | S5 clock seam (`Initialize(…, DateTime? stamp)`, `NowProvider`, `_runStamp`) |
| `ThirdParty/DotRecast/Recast/RcBuilder.cs` | S5b `[Obsolete]` guard on `BuildMultiThread` |
| `tools/TiledCoreHarness/Program.cs` | S3 the 9 calibration CLI flags; S5 the `Budgets` echo in `settings:`; S8 `--goldenhash` + `GoldenTable` + `Test28_CleanupDeterminism` |

The patch refuses to apply to unexpected content (it compares SHA-256 against the recorded `before`
payload and stops with a clear message), so it cannot half-apply onto a tree that is not the stacked
base.