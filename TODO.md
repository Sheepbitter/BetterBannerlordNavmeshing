# TODO

Working list for BetterBannerlordNavmeshing. Newest/current items first.

## 0. Harness/module parity + harness ergonomics (DONE 2026-10-20)

- In-game hotkey vs editor divergence fixed. The 9-argument `SceneNavMeshExporter.RunPipeline` overload
  (the mission path) still carried stale literals - v1 core, contour 1.3, stitch 4, gate connectors off -
  so the hotkey silently built a different mesh from the editor button. It now delegates every voxel/core
  setting through the new `SceneNavMeshExporter.NavMeshPipelineDefaults`; the 31 matching exporter field
  initializers use those constants, and the mission path resolves to the same v2 defaults (v2 core, cell
  0.25 m, contour 2.0 vox, stitch 6, gate connectors and plate blend on). No intended output change for the
  editor.
- Harness optional-argument parsing fixed. `--cleanup`, `--gate` and `--ramps` consumed the next argv
  unconditionally, so `--cleanup --quality` silently became a plain build and invalidated older baseline
  rows. They now take an optional `on|off`: a bare flag means **on** and the next token is consumed only
  when it is literally `on`/`off`.
- Harness triage flags added: `--test11` and `--test25` run those tests alone (seconds) instead of the
  ~45 s suite.
- Test 11 unblocked as a **stale assertion**, not a code regression. The shipped contract allows a steep
  face when it is *short* (tall-steep only is a defect). Added `CountTallSteepAbove`; the 74.6 deg face at
  10 m is 1.2 m tall against a 2.60 m limit (two agent climbs), so it is allowed. `ASSET TEST: PASS`.
- Docs reconciled: README stairs header now says "on by default", the in-game section documents the shared
  v2 defaults and the caveat that the editor keeps script settings per scene, harness README gains the new
  modes plus the optional-argument semantics.
- Dead/inert cleanup knobs documented with **no behavior change**: `MaxFanNormalSpreadDegrees` (120 deg) is
  inert because `FanPlanar` folds the angle to <=90 deg first; `FaceSlopeDeg` and
  `MaxCleanupSlopeDegrees` are dead code (zero callers), so the "steep fans are never touched" rule in
  their comment is not enforced. Code comments + README say so. Making either load-bearing changes output
  and gets its own round.

## 1. Determinism: remove the wall clock (required for the procedural terrain generator pairing)

- Every cleanup pass in `NavMeshCleanup.cs` is bounded by a wall-clock budget (`CleanupMaxMs = 6000` ms,
  polled via `Stopwatch.ElapsedMilliseconds` every 256/1024 iterations with early exit), so the same input
  can produce a slightly different mesh run to run (observed: 241,717 vs 241,502 faces on CC_20 with
  identical code; editor outputs differ by kilobytes).
- The tool will be paired with the procedural terrain generator and MUST be deterministic:
  same input -> identical output, ideally across machines.
- Required design properties:
  - deterministic work budgets (counts of candidates/steps) instead of time;
  - fixed sweep/pass structure with stable ordering and tie-breaks;
  - no hash-map iteration order or unordered parallelism in result-affecting paths;
  - predictable worst-case runtime.
- RESEARCH DONE (2026-10-06): full report in `DeterminismResearch.md`. Verdict: the four wall-clock gates
  in `NavMeshCleanup.cs` (lines 443, 1363, 1585, 1920) are the ONLY nondeterminism in the pipeline - no RNG,
  no hash-order dependence, and the tile build is already canonical under `Parallel.For`. Every pass already
  has a sweep cap, so the clock is a redundant second limiter and this is a small change, not a redesign.
- Recommended plan (from the report):
  - delete the four elapsed-time comparisons; repurpose `CleanupMaxMs` into a work-unit budget scaled off
    topology counts (faces/vertices/edges/candidates), keeping the sweep caps as the hard bound;
  - keep a diagnostic deadline that only logs/warns, never changes output;
  - make diagnostic strings deterministic - the KB-level editor-output churn is partly elapsed-ms/timestamps
    embedded in log lines, not mesh differences;
  - convert decision-path transcendentals (`Acos` etc.) to cos-space comparisons for cross-machine identity;
  - add a determinism self-test (run twice, assert identical verts/tris/QuadPairs) + golden hashes per map;
  - one-time calibration of the budget multiples on Birtu/CC_20/Basamu against the old 6000 ms worst case,
    then re-measure cleanup quality (thresholds were tuned against clock-truncated behaviour).
- Established tools are deterministic by construction (Recast/Godot single-threaded per navmesh, meshoptimizer
  count-bounded); Unity's async bake is the documented counter-example.
- Performance leverage (from the same report, so the same wall-clock target buys a larger budget):
  - cheap, fold into the determinism round: `FlipEdges` currently rebuilds and re-sorts ALL edge keys every
    iteration - keep the sorted array and filter the 2 stale edges per flip (removes iter-1 full sorts);
    convert decision-path trig to cos-space (also removes `Acos` from hot loops); fix the `touched[]` reset
    using the pre-rebuild face count;
  - the real lever: the per-sweep `O(F)` rebuilds (compact + CSR + component BFS + candidate mask) dominate
    once guard-eval throttling is gone - make them incremental/dirty-region; if needed, evaluate guards in
    parallel into per-vertex arrays and apply serially in index order (deterministic; the tile builder
    already uses this pattern);
  - bigger project (later, separately): QEM-style priority decimation is "better quality per unit work"
    but would invalidate all current README measurements - only after the gates are gone and the
    determinism self-test exists;
  - note the report measured nothing: the calibration step (per-pass counters + ms on Birtu/CC_20/Basamu)
    is what turns these estimates into a real budget number.
- IMPLEMENTATION PLAN READY (2026-10-07): `DeterminismImplementationPlan.md` - S0-S10 sequenced with
  per-line anchors, the four budget formulas and counter sites, settings-vs-constant classification,
  a cos-conversion table, the FlipEdges incremental-key design, a determinism self-test + golden hashes,
  the calibration derivation, 17 acceptance criteria and a before/after table template. It also folds in
  item 6's per-pass timings (S1). Starts ONLY after the roof fix (item 2) has landed and the tree is
  committed - the S0 baseline must be captured against the fixed binary.
- Plan corrections to record: `MaxFanNormalSpreadDegrees` is inert (the `180 - na` fold bounds the test
  below its threshold) and `FaceSlopeDeg`/`MaxCleanupSlopeDegrees` are dead code - making either mean
  something changes output and needs its own round; the FlipEdges stale-key filter is 5 keys per flip
  (shared + four ring edges), not 2; `touched[]` is latent, not a live bug.
- PLAN v2 + VALIDATION (2026-10-07): independent validation (LongCat 2.5) CONFIRMED the core (the four
  gates are the only output-affecting wall-clock comparisons; all four corrections hold; no missed mesh
  nondeterminism) and REFUTED two things, both fixed in v2:
  (a) "only Decimate binds" - the Refine budget is ~35% tighter than its sweep cap implies and Relax can
      bind when C0 > 65536; the calibration must measure ALL FOUR counters, and the single-knob framing
      is Decimate-only;
  (b) the S0-S10 order had four execution defects - Test28 creation moved to S1b (so S2 can observe it),
      S0 split into separate `--quality` / `--xzprobe` invocations, A17's grep scoped to the `Deadline`
      class + the two reporting sites, and S9 made executable via new settings overrides + nine harness
      CLI flags (`--cleanup-deadline`, `--dec-guard-mult`, `--dec-sweep-cap`, ...).
  Also folded in: culture-sensitive log formatting (S5), the dead `RcBuilder.BuildMultiThread`
  unordered-queue trap (guard step S5b), the `NavMeshPrefabIndex` hash-order note, and the canonical
  harness location `tools/TiledCoreHarness`. Files: `DeterminismImplementationPlan.md` (v2) +
  `DeterminismPlanValidation.md`.
- IMPLEMENTED + DEPLOYED (2026-10-08 04:35 as V3.61+fixB+fixC+fixE+det; report
  `DeterminismImplementationReport.md`; patch `det_impl\det\patch.py` + `files\*.{before,after}` +
  `run.ps1`):
  - Determinism achieved: pre-change Basamu swung 64.8% on `rejMove` / 35.6% on `refined` run-to-run;
    post-change every counter on all four maps is byte-identical across repeats.
  - The plan's mandatory shadow assert caught TWO defects in the plan's own formulas (S6 rows 5/10:
    `Acos` returns RADIANS, so the gain conversion `cos((Acos(cos)+gainDEGREES)*PI/180)` turned a ~20 deg
    threshold into ~0.8 deg; row 10: inverted cos comparison - acos is decreasing). Both fixed;
    A16 = 0 on every map and in the suite.
  - S7 decision-identity proven (Flipped/RejMove/iters/edgeVisits identical vs a pre-S7 build);
    FlipEdges now one sort per pass + five-key filter + backward in-place merge (Birtu 132 -> 111 ms).
  - A15 NOT literally met - the plan's premise is wrong here: Refine is 42-55% of the cleanup and on
    Basamu IS the binding budget; no `DecimateGuardMultiple` reaches 6000 ms with `RefineSweepCap` = 8
    (a keep-the-caps non-goal). Proposed lever: lower `RefineSweepCap` (needs its own round). Also the
    old 6000 ms was never a total-run budget (envelope ~14,500 ms), so the comparison is non-equivalent.
  - A12: all 20 suite verdicts PASS; 6 synthetic-mesh sizes move <1% (fixed vs clock-truncated work).
  - Incidental: `--cleanup` consumes the next argv as its on/off value (`--cleanup --quality` silently
    becomes a plain build - it invalidated older baseline rows; fix the arg parsing); the `F0/2` floor
    can structurally mask the Decimate budget; Test 3's neutrality constants are stale for this stack
    (pre-existing, left alone per plan); `rebuildMsPerSweep` = 380 ms (Basamu), flipRebuild = 44% of the
    flip pass -> incremental/dirty-region rebuild is the next perf lever (proposed, not implemented).
  - Before the eventual commit: remove the TEMPORARY S6 shadow code (it is the A16 evidence), and apply
    the harness edits (`files\Program.cs.{before,after}`: Test28, `--goldenhash`, calibration flags) to
    `tools\TiledCoreHarness\Program.cs`.

## 2. Roof regression from V3.50 (in progress)

- Commit `386a1fb` (V3.50, `RepairSteepFaces` + `RestoreSplitComponents`) introduced ridge-straddling
  roof faces on `european_city_house_a` (CC_20). V3.46 (`8ed87f7`) and V3.48 (`67455a2`) are good;
  V3.50 and later are bad (user-verified in-editor).
- Fix must keep the staircase repair V3.50 was made for.
- Editor good/bad samples preserved (read-only) in
  `C:\Users\Sheepbitter\AppData\Local\Temp\opencode\editor_artifacts\`:
  `CC_20_siege__fresh348.bin` (good), `CC_20_siege__fresh350_bad.bin` (bad), plus both soup exports.
  Note: build outputs are not byte-reproducible (item 1), so compare geometry, not hashes.
- FIX B VERIFIED AND DEPLOYED (2026-10-07): fixB = fixA v2 (`roof_fix\patches\fixB\patch.py`; applies to
  the pristine sources of both `386a1fb` and `9f7477a`). It (a) moves the steep pass after the cleanup AND
  makes it quad-aware via the extracted `RemapQuadPairsAfterCompaction` helper (fixes the badQuadWalks gate
  failure), and (b) adds a second island prune that judges ONLY islands whose pre-drop component lost faces
  to the post-prune drops - untouched components are never re-judged. Two bugs were found and fixed during
  verification (fragment gating; raw union-find parent normalization). Full matrix, both bases:
  roof identical to V3.48 (0 faces below, min +0.067 m), staircase steep 11 -> 6, no vertical faces,
  islands 15/15/5/1, Birtu -0.02% / Basamu +0.06% faces, badQuadWalks = 0. Deployed as V3.61+fixB at 06:10
  for the user's in-engine test. Honest difference: the remaining staircase steep face is 81.57 deg
  (V3.50: 72.08 deg) - count and islands match; needs the user's eye. Report: `RoofFixReport.md`.
- fixB VALIDATED (2026-10-07): an independent validator (LongCat 2.5) re-derived everything from pristine
  `git archive` copies: the mechanism stage counts reproduced exactly and its ablation roof obj is
  BYTE-IDENTICAL to V3.48's replay obj (not just metric-equal); the quad remap is proven load-bearing
  (counterfactual without it -> badQuadWalks=1225 FAIL); the second prune judged exactly the 3 fragments
  and never an untouched island (per-island log; the marginal 398/362/356-face islands kept); both
  verification bugs reproduced with counterfactuals; the deployed marker DLLs are proven to be
  `9f7477a`+fixB via a PDB document-hash match against an independent patch + an export-tree diff
  (106 identical files, exactly 1 differing). Baseline nuance: "11 -> 6" is V3.48 -> fixB; V3.50 itself
  measured 11 -> 7. Birtu PASS; a Basamu completeness run was pending. Report: `RoofFixValidation.md`.
- fixC (staircase-top regression reported after the fixB test; **DEPLOYED as V3.61+fixB+fixC at 18:10**):
  analysis - the staircase "wall" is a contour-spike fan at hub (1120.5, 115.7, 1078.5) (foot 115.7 ->
  deck 124.1); V3.50 dropped/restored it so the cleanup produced the accepted ~70.5/72.1 deg pair, while
  fixB let the cleanup produce a single 81.6 deg face spanning hub->deck (deck bare). KEY INSIGHT: the
  roof-breaking pre-cleanup drops are only the 5 SHORT (2.0-3.0 m) steep faces under the collision roof;
  the other 1203 marked faces are roof-clean. Fix = re-run the steep pass at V3.50's position
  (restore-first), then drop only contour-spike fan faces taller than `SteepFanMinHeightMeters` 3.5 m
  (fan is 3.2-8.4 m). Matrix (both bases): roof 0 below / min +0.067; staircase steep 6, 11.857 m2,
  worst 70.53 deg; gate PASS badQuadWalks=0; islands 15/5/1; Birtu probes 4/4. Honest: V3.50-class not
  byte-exact (70.53 vs 72.08 deg); the 3.5 m floor is scene-tuned; Basamu -0.24% faces. Report:
  `FixCReport.md`.
- USER DIRECTIVE (2026-10-07) for the fan/hub issue (still 1-2 fans left at the hub; shelved, not solved):
  the intended resolution is a CONNECTIVITY POLICY change - stop forcing a connection with the wood-heap
  prop (`bd_wood_heap_b`-like) that shares only a single vertex with the stair structure; let that piece
  island and be culled by the island prune. This likely enables REVERTING the V3.50+ steep-face machinery
  (fixA/B/C chain) entirely. Under evaluation by the fixC2 investigator alongside the revert option.

## 9. Stairs-landing regression at the user's staircase - V3.52 gate-plate blend (found 2026-10-07)

- The user's staircase at (1166.69, 1122.40, 100.79) is `stairs_bo` + `european_castle_ladder` #33/#34
  (platform top y~117.10), NOT the hub fixC targeted (that was ladder #6). Bisect: V3.48/V3.50/V3.51 keep
  the landing flat at 117.10; V3.52 through V3.61 (and +fixB/+fixC) rotate it off (62.6 deg edge + 5.7 deg
  surface at 116.2) via `BlendGatePlates` - the V3.52 pass matches the landing as a "plate" and rotates
  its vertices in place. fixB/fixC neither caused nor fixed it; the standing fixC matrix is unaffected.
- Recommendation: NARROW `BlendGatePlates` so it cannot rotate a stepped-prop landing (require the ring
  to touch a gate/door collider, or exclude plates whose ring contains stepped-prop faces) - the gate
  blend and its 5/5 acceptance must survive. Being prototyped as fixD on top of fixB+fixC. Failure mode:
  V3.61 log reference `gate plates: examined=229 blended=53 (308 faces rotated in place)`; the landing
  must leave that set, few plates total should be excluded. Report: `FixCFollowupReport.md`.
- FIXD DONE AND DEPLOYED (2026-10-07 23:43, as V3.61+fixB+fixC+fixD): one rejection added to
  `BlendGatePlates` - a candidate plate is declined when HALF OR MORE of its OWN faces sit on stepped-prop
  provenance cells (`SteppedRestoredCellXz`), hoisted in as `steppedCellsForPlate`. Geometry-only, no prop
  names; excludes exactly 1 of 228 CC_20 plates (the landing). Ring-based criteria were measured WRONG
  (the gate's ring has MORE stepped cells (3) than the landing's (0) - a ring test would kill the gate).
  Mechanism proof (case a - the landing is itself a plate): its own vertices drop ~1.0-1.1 m between V3.51
  (117.100) and V3.52 (115.99/116.09); fixD restores 117.100. Matrix (9f7477a+fixB+fixC+fixD): staircase
  flat 0 deg at 117.10 (7 faces), census steep 4 / 1.455 m2 / 67.04 deg; gate connection 5/5; roof
  0 below / min +0.067; first hub fixC kept (6 / 11.857 / 70.53); gate PASS badQuadWalks=0; islands
  15/5/1. Blended plates 229/53/308 -> 228/52/299, `steppedLanding=1`. The 386a1fb leg is a documented
  no-op (BlendGatePlates does not exist before V3.52). Noise: the CC_20 landing leg is identical across 3
  repeats; Basamu wobbles (492854 vs 492921) - recorded as evidence for item 1. Report: `FixDReport.md`.

## 10. Gate-scope the plate blend (fixE) - wall-deck regression + fixD replacement (2026-10-08)

- The user found the same blend false-positive on `castle_wall_9_corner_b` (prefab 1160.99, 1021.39,
  113.20; deck top ~129.77; child tower `european_castle_corner_c_l3`). fixD's stepped-prop rule is blind
  to it - and is now SUPERSEDED: user called fixD a half-solution; the final chain is fixB+fixC+fixE.
- SIGNAL (user's marker, name-free): every gate/usable door has a child entity carrying the scene TAG
  `gate` (vanilla calls it `clean`). xscene evidence: `<tag name="gate"/>` on 478 entities (clean +
  state1..4 children) under gate roots (`european_castle_gate_outer_l1` @1130.466,990.709,85.124; l2;
  city gatehouse; castle gatehouse; siege barricades); negatives (empty tags): `castle_wall_9_corner_b` /
  `european_castle_corner_c_l3`, `stairs_bo`, `european_castle_ladder`, `european_castle_wall_gate_l1`.
  Body flags do NOT separate (all 0x0).
- IMPLEMENTATION: the collector reads the tag live (`SafeGateTag` walks `HasTag` up the Parent chain -
  the same API the border probes use) -> per-entity Gate bit -> snapshot AssetsVersion 2 (v1 still reads;
  Gate=0) -> gate triangle mask threaded through `VoxelNavMeshGenerator` -> `Build(..., gateTriangles)` ->
  `BlendGatePlates` accepts a plate ONLY when the MAJORITY of its own faces are gate cells (new `notGate`
  counter). Everything else - walls, tower decks, stair landings - is left untouched.
- DEPLOYED 2026-10-08 00:44 as V3.61+fixB+fixC+fixE (fixD removed). The deploy caught one real compile
  error the harness cannot see (the editor-path call site passed the gate mask / border in swapped
  positions) - fixed in the export and in `patch.py`.
- Acceptance was SKIPPED by user request (they check in-editor directly). Knob if the gate stops
  blending: relax majority to any-gate-face (`_gateOwn == 0`). Note: the tag also marks siege barricades -
  included by design. Report: `FixEReport.md`.

## 3. Gate connection at CC_20 (in progress)

- The four user-marked vertices are separated by a 0.76-0.96 m XZ footprint gap (dy only ~0.30 m); the
  ledge filter widens it; merge-stitch refuses the crossing despite its 1.5 m cap.
- User's fix direction: the gate projection should run its own local ledge/connectivity check and walk
  itself back until it connects normally (no scene-wide filter changes).
- PLAN READY (2026-10-07): `GateProjectionFixPlan.md`. Pinned to code: the eater is `FilterLedgeSpans`
  rule (b) - `highestTraversableNeighborFloor - lowestTraversableNeighborFloor > walkableClimb` (0.6 m) -
  which nulls the gate's own intermediate colliders (their traversable flanks span 1.05 m across the
  plate above / terrain below), with rule (a) nulling the plate rim; erosion then widens the void to the
  1.08 m band. Gap fill refused (flanks > climb; cap 2 < 3-4 cells); per-tile stitch and merge-stitch
  refused (same-component skip, caps, envelope). `BlendGatePlates` is Y-only and runs after the trim -
  the architectural crux. Fix reuses the existing `ledgeExemptTriangles` -> `MarkSteepCells` ->
  `RestoreSteppedSpans` machinery: (Part 1) mark the plate + same-prop ring-2 m approach triangles
  ledge-exempt (one-line hook at `SceneCollisionGeometry.cs:424`); (Part 2) tile-level walk-back of
  non-linking marked cells. Prototype = Part 1 alone + the six-point `--walk` (expect C covered, all
  six WALKABLE, void band 1.080 -> ~0.5 m, plate still blended, islands <= 15). Six open questions in
  plan §5.
- PLAN v2 + VALIDATION (2026-10-07): independent validation (LongCat 2.5) rebuilt the harness fresh and
  reproduced the mechanism, the refusals and the fix's foundations - and corrected four things, all folded
  into v2: (a) the v1 walk-back loop (Part 2) is largely REDUNDANT - `RestoreSteppedSpans` pass 2/3
  already implements it; the primary fix is Part 1 (mark only); (b) the validation checklist could
  FALSE-PASS - upper floors at y~92.30/97.10 overhang the gate, so every walk check must assert the
  picked face's ELEVATION is gate level (~85.x), not merely WALKABLE; (c) "islands <= 15" is not
  guaranteed (ledge-off gives 16) and "plate still blended / y unchanged" is a risk to measure (blend
  counts swing 27-77); (d) the (a)/(b) attribution is inferred, not measured (threshold between climb
  0.8-1.0). Files: `GateProjectionFixPlan.md` (v2) + `GatePlanValidation.md`.

## 4. Staircase restore round (queued)

- Restore the staircase work last, with the gate ramp folded in; deploy a testable build before it.
- USER-CONFIRMED (2026-10-08): the staircase regressions seen after the recent default changes (2.0 vox /
  10 m tile) are real ("The stairs regression is real") and are to be handled by THIS round together with
  the pre-existing V3.52 gate-plate staircase issue (section 9). Do not chase them separately. Attribution
  (2.0 vs 10 m vs both) can be bisected with the harness levers `--conterr` / `--tile` when the round starts.

## 5. Fixture refresh (queued/partially done)

- The CC_20 harness baseline used the stale 2026-10-01 v1 snapshot; the fresh 2026-10-06 v2 snapshot
  (walk-area border block) is the correct current fixture.

## 6. Cleanup profiling + upstream sliver reduction (requested 2026-10-06)

- We log cleanup COUNTERS (welded/removed/decimated/relaxed/refined/flipped/quads, needles/thin before->after,
  components, guard rejects `cand/rejMove/rejFan/rejRing/rejTopo`) and decimate's `start=/budget=` line, plus
  `faceQualityMs` for the repair. Per-pass durations: DELIVERED by the determinism S1 (per-pass counters +
  ms in the cleanup log line - see item 1).
- Stage census: count thin/needle faces at the cleanup's input and after each upstream stage (detail mesh,
  merge) so the "produce fewer slivers in the first place" hypothesis can be targeted (a candidate lever:
  detail-mesh tessellation / contour tolerance).
- Border A/B: no toggle needed - the user will remove the walk-area tags from the map to test the heavy
  whole-map case (~241k faces, ~35 s, clock-bound cleanup, 15 islands) against the bordered case
  (~31k faces, ~5 s, 2 islands).
- CENSUS DONE (2026-10-07): `SliverReductionPlan.md`. ~99% of slivers are born in the per-tile
  detail-mesh build (`TriangulateHull` greedy ear-clip); vertex density is set upstream by
  `contourMaxError` (shipped 1.3 vox) and the detail sampling (2.0 m). Measured levers, post-cleanup
  sliver counts vs baseline (CC_20 1048 / Birtu 353 / Basamu 1508): `contourMaxError` 2.0 vox ->
  -43% / -24% / -34%; 3.0 vox -> -57% / -33% / -45%; tile 16 -> 12 m -> -13% / -16% / -4%;
  2.0 vox + 12 m -> -52% AND build time -27% (CC_20); detail off -> +92..+201% (never). Cleanup cost
  scales with the sliver population (candidates -13% at 2.0 vox; refined -52..77% at tile 12/8) -
  the hypothesis is confirmed.
- RECOMMENDATION: ship `contourMaxErrorVoxels = 2.0` (one line: `SceneNavMeshExporter.cs:140`; the
  0.50 m boundary budget at cs 0.25 equals the cleanup's own `SurfaceToleranceMeters`; all invariants
  PASS). Tile 12 m optional after in-engine validation. Do NOT touch edgelen/regmin/regmerge/weldtol/vpp/cs
  or turn detail off.
- Open: in-engine boundary-fidelity validation of the tile choice (candidates 10/12 m; 8 m aggressive); why
  tile <= 12 zeroes CC_20's merge-stitch pairs (808 -> 0); the `edgelen 20` anomaly; the afterPrune
  terrain-probe discrepancy.
- VALIDATED (2026-10-07): independent validation (LongCat 2.5) CONFIRMED all four claims with exact number
  reproduction (its `afterCleanup` count equals the cleanup's own logged `thin after`; every lever
  magnitude matched; invariants PASS). Refinements: `MaxCleanupSlopeDegrees`/`FaceSlopeDeg` are dead code
  (the "steep fans are never touched" guard is not enforced - found independently by the determinism
  validator too); 67% of surviving CC_20 slivers are steep >= 40 deg (the cleanup-resistant residual -
  further gains target steep-boundary triangulation, not cleanup tuning); options scan found nothing
  better without a load-bearing semantic change (`--radius 0.5`/`--slope 45` alter agent semantics;
  `tile 8 + 2.0 vox` is -60% but aggressive). Report: `SliverPlanValidation.md`; the refinements are
  folded into `SliverReductionPlan.md` (v2, appendix section 11).
- IMPLEMENTED (2026-10-08, V3.64): `contourMaxError` default 1.3 -> 2.0 vox in the exporter
  (`SceneNavMeshExporter.cs`), the builder settings default, the in-repo harness SnapshotSettings, and the
  README (3 spots). Golden hashes re-captured and verified PASS. USER-VALIDATED in-engine (2026-10-08):
  "does look an improvement"; small staircase regressions noted, same style as the existing CC_20
  staircase issues (fold into the staircase round). Note: the editor keeps the script's settings per
  scene, so scenes whose script predates the change still read 1.3 until the value is set to 2.0 in the
  panel (explains the earlier no-change runs). Do NOT touch edgelen/regmin/regmerge/weldtol/vpp/cs or
  turn detail off.
- IMPLEMENTED (2026-10-08, V3.65): navmesh tile (coarsest spacing) default 16 -> 10 m in the exporter
  (`SceneNavMeshExporter.cs`), the builder settings default (`TiledNavMeshBuilder.cs`), the in-repo
  harness SnapshotSettings, and the README (settings table + tiled-core section + v2 log examples).
  Golden hashes re-captured at 10 m on all four fixtures and verified PASS. Based on `TileSweepReport.md`
  (five-map sweep): 10 m measures -20..30% build time on the heavy maps (Na'ilu -29.7%, Basamu -22.9%,
  CC_20 -19.6%) at +2..4% faces, with meanQuality and long-thin aspect improving on every map. Note:
  the editor keeps the script's settings per scene, so scenes whose script predates the change still
  read 16 until the value is set to 10 in the panel.
- IMPLEMENTED (2026-10-08, V3.66): gate connector - synthesized bridge strips at gate plates
  (`GateBridgeStrips.cs`; gate-bit detection via the fixE `gate` tag; force-walkable + ledge-exempt
  strips; editor path + harness mirror `--gate on|off`; `NavMeshVoxelGateConnectors` default true).
  Fixes the CC_20 gate crossing hole (C) at gate level on tile 16 and tile 10; base/Birtu/Basamu
  unchanged; goldens unchanged. Honest deviation: t5 rides the ramp at ~86.0 vs the base ground ~85.0
  (per-run ramp lengths would be needed to match exactly). Report: `GateConnectorReport.md`. Supersedes
  the marking approach (`GateProjectionFixPlan.md` - dead: nothing to mark at the defect).
- IMPLEMENTED (2026-10-09, V3.68): FIX - the water modes culled/clamped WALL geometry (user report:
  "when culling below water level is enabled, it just starts ignoring walls"). The min-corner rule removed
  whole wall quads (their lowest corner is below the level; pre-fix CC_20 @90 removed triangles reaching
  23 m above the level). Both modes are now gated on FLOOR-LIKE triangles only: Recast-space upward normal
  >= `cos(NavMeshMaxSlopeDegrees)` (the `MarkWalkableTriangles` threshold) + a small margin, plus
  force-walkable ramps. Wall/obstacle geometry is never culled or clamped, so walls keep standing and keep
  blocking through the raised sheet. Test 29 gains the wall case (a y 0..12 wall at level 5 survives both
  modes and blocks: 0 crossing faces). A/B re-run: CC_20 @90 cull 385172 -> 261986, Birtu 443445 -> 222358,
  Basamu 788618 -> 555313; removed corner heights capped (CC_20 113 -> 98.79 m). Report: `WaterCullReport.md`.
- IMPLEMENTED (2026-10-09, V3.67): water-level navmesh handling - cull OR raise (user request). The
  editor always reads `Scene.GetWaterLevel()` (dry maps set it below the terrain -> natural no-op;
  `SafeWaterLevel` guards). Offset-aware (`NavMeshHeightOffsetMeters`): Mode A (cull ON) drops triangles
  with lifted-min < level; Mode B (cull OFF, the default) clamps below-level vertices to the water
  surface (V2 only; V1 cull wired). Harness `--cullwater` / `--raisewater` (default no-op; goldens
  PASS); snapshot records `water:`. Test 29 covers min-corner/offset/raise-exact/off-inert; Test 1's
  stale tile expectation fixed. Lattice note: the emitted surface lands within ~1 cell of the level
  (a `level - offset + ch` knob would guarantee at/above). Report: `WaterCullReport.md`.
- SUITE NOTE (2026-10-08, V3.65): the harness suite at the 10 m default passes 11/13 groups. Test 25
  (LADDER-CAP) fails identically on unmodified HEAD (pre-existing, unrelated). Test 11 (ASSET TEST) is NEW
  at 10 m: its synthetic gully/stairs scene grows a 74.6 deg fan poly face vs the 52 deg assert (24.7 deg
  at 16 m). Real maps are unaffected in the aggregate - steep>60 counts are LOWER at 10 m on all five
  (Na'ilu 4964 -> 3743, Basamu 2705 -> 2251, Birtu 341 -> 291, Tasksatu 1010 -> 840, CC_20 771 -> 713) -
  so it is a scene-partitioning artifact of the small synthetic scene; triage later (vs a real steep-face
  class on small scenes).
- MEASURED (2026-10-08): tile 12 m vs the shipped 16 m on Na'ilu / Tasksatu / Birtu (current code,
  contour 2.0 vox, harness whole-map replay). Tile 12 is faster on all three (buildMs 42832 -> 34944 =
  -18.4% Na'ilu, 5975 -> 5641 = -5.6% Tasksatu, 4516 -> 4420 = -2.1% Birtu), long-thin aspect>4 down
  9-13% (aspect>8 down 18-24%), meanQuality up ~0.004 on all, tiny=0, invariants PASS everywhere.
  Mechanism: sliver births drop (Na'ilu needles before 787 -> 428, thin before 17976 -> 12511) so cleanup
  refine work drops (17.0 s -> 10.7 s). Na'ilu islands 3 -> 2 (two small islands merge). Tasksatu needles
  7 -> 14 (0.015% of faces - negligible). Independent re-runs reproduce every mesh metric
  byte-identically. Report: `Tile12MeasurementReport.md`. Tile 12 remains optional pending in-engine
  validation (set coarsest=12m, rebuild, compare visually).
- SWEPT (2026-10-08): tile 16/14/12/10/8/6 on all five fixtures (Na'ilu, Tasksatu, Birtu, Basamu, CC_20 v2).
  Build time falls with the tile, bottoming around 8-10 m (Na'ilu -41.4% at 6 m; Basamu -30.0% at 8 m;
  CC_20 -26.3% at 6 m; Birtu -4.1% at 10 m); faces stay flat at 12 m and inflate from 10 m down (CC_20
  +14.9% at 8, +35.0% at 6; Birtu +21.8% at 6); long-thin aspect>4 down 30-36% by 6 m; meanQuality improves
  monotonically; needles near-flat except Basamu t6=82 and Tasksatu t14=18 (both reproduced). 14 m adds
  nothing; 6 m overshoots (time reversals on Birtu/Basamu). Read: 12 m nearly free, 10 m modest step,
  8 m aggressive. Verification: 10 of 24 new configs re-run byte-identically; fixture identity
  cross-checked via golden F (CC_20 v2 235660, Basamu 482993). Report + CSV: `TileSweepReport.md` /
  `TileSweepTable.csv`. In-engine validation still pending.

## 11. Crisscross / X artifacts (user request 2026-10-08 - handle later)

- From the 3.54 feedback (`Feedback\3.54\criscros that bannerlord seems to accept like the doorway
  before.PNG`): the navmesh crisscrosses/overlaps itself like an X; two examples known. The doorway
  variant is engine-accepted, so severity varies - not yet investigated. Related machinery when the round
  comes: `MarkSelfOverlaps` / `RepairFaceQuality` (the V3.46 same-level overlap repairs) and the `--scan`
  self-overlap report. Later, per the user.

## 12. Ladder chain on a leaning ladder (REAL product defect - Test 25 case c)

- USER REPORT (2026-10-31): ladders on **Basamu work great**. On **Birtu the failure is specifically the
  small doorway right at the ladder's top**, not the climb itself. That reframes this item: the chain
  geometry is adequate where the top lands openly; the remaining defect is the top landing through a
  narrow throat.
- Failing case: a 60 deg leaning ladder from `(2.6, 3.4, 0.2)` to `(4.332, 3.4, 3.2)` in the Test 25
  scene. `--test25` prints `c) 60 deg leaning ladder: rampsOn connected=False rampTris=6 F=56`, then
  `LADDER-CAP TEST: FAIL`. Cases (a) 90 deg and (b) HasSteps control pass. The synthetic hatch is the
  analogue of Birtu's doorway.
- SCOPE: this is about actual `Ladder`-flagged props (`BodyFlags 0x40`), not the `european_castle_ladder`
  asset on CC_20 (flags `0x0`, an unflagged staircase handled by the staircase round). The real shipped
  ladders are Birtu's `civilian_ladder_5m` / `civilian_ladder_6m`; with the harness snapshot they are
  **also broken**: `--snapshot <Birtu> --ramps on --coverage civilian_ladder` reports
  `cov=0.0% covEroded=0.0% ramp=0.0% walkArea=0.3m2 rampArea=0.31/0.00m2 rampTris=6` for both.
  (A `--ramps off` run looks connected only because the ladder feature is not exercised - the default
  snapshot mode is ramps off, which is why the first scope check was misleading.)
- Mechanism: `TryLadderChain` emits the 3-quad chain (foot pad, ramp, top pad; 6 tris) but almost none of
  it becomes walkable navmesh. The chain's own footprint is correct (`--propdump civilian_ladder_5m`
  prints `rampXY=(301.918,313.626)-(303.275,316.199)`, matching the prop bbox, `z=[16.597..21.050]`).
  Probes along its centreline find no face at the foot, one large face at mid-height whose local height
  runs 18.10..20.12, one at 20.57 local 21.31, and **no face at the top** - so the poly stage turns the
  ramp into two mid fragments instead of a continuous climb. A `--ramps off` run looks connected only
  because the ladder feature is not exercised; always pass `--ramps on`.
- Experiments run and reverted (all with `--ramps on --coverage civilian_ladder` as the metric):
  - endpoint anchoring at the prop's true extreme vertices: chain 60.6 deg (was 63.7), still split;
  - marking the whole chain as connector (force && exempt): connector cells/erosion restoration appear
    (`forcedInTile=26`, `erosionExempt=114`) but coverage stays 0%;
  - forcing the ladder's own box triangles walkable: 5 m 60% coverage, 6 m 0%;
  - excluding the ladder's own collision from the soup: coverage still 0% (the chain alone does not
    rasterise);
  - offset connector first for ladders: 6 m 68.2% coverage, 5 m 0.9% (its 45 deg foot is 4.45 m from the
    top and lands off the platform);
  - Test 25 fixture replaced with the real 12-tri box / a closed slab: still `connected=False`, and the
    slab broke case (a).
- Next direction: instrument the chain's rasterisation (heightfield spans / where the 3 quads go) instead
  of guessing shapes. The 0.31 m2 result says the problem is before the region/contour stage. A narrower
  offset (or an on-plane chain plus a forced span marker that `MarkSteppedCells`/`RestoreSteppedSpans`
  actually retains) is the likely lever.
- EXPERIMENTS 2026-10-31 (branch `exp/ladder-raster`, NOT merged; main is back at d02c692):
  - Added a `--spanprobe "x0,z0,x1,z1[,y0,y1]"` stage dump (`raster -> ledge -> restored -> clearance`).
    On Birtu the continuous 63 deg chain produces ~1-2 m tall spans per cell (one cell spans several
    chain heights). `FilterWalkableLowHeightSpans` keeps the ladder's own upper span and nulls the chain;
    the chain's own span is not identified. That is why only ~0.3 m2 survives.
  - Tagging connector spans with a private area id (62) at rasterisation, using the cell-centre height
    for `connectorTop`, and making erosion skip tagged spans by identity got the chain spans to survive
    the heightfield (`clearance` shows continuous area-62 spans 15.6..21.2 on the 5 m ladder), but
    region/contour still do not turn them into a walkable path (coverage 0%).
  - Replacing the chain with a staircase of flat treads (on-plane, 0.5 m rise) gives coverage 2.1% /
    4.2% (5 m / 6 m); footprints correct, probes find mid-height faces, ends still missing.
  - A 45 deg OFFSET connector emitted as flat treads gives 44.7% on `civilian_ladder_6m`, 2.8% on
    `civilian_ladder_5m` (its foot lands 4.45 m from the top, off the platform).
  - This work also broke PROP-RAMP, DOORWAY-LADDER and NARROW-DOORWAY in the suite, so it stays on the
    experiment branch only.
  - Read: a 63 deg ladder cannot be represented as a walkable Recast staircase at 0.25 m cells with a
    0.25 m tread (several treads merge into one tall span). A walkable staircase needs a ~45 deg
    effective run (tread >= 2 cells). For a ladder at a platform edge there may be no room for that run,
    which points at the engine-native mechanism instead: per-face group ids in `Nmg8Writer` (f1, now
    always 0) so the engine's ladder machinery can mark the faces, not a geometric ramp.
  - TOP-PAD / DOORWAY EXPERIMENT (same branch, 2026-10-31): the chain's top apron is only 0.5 cell, so
    it cannot cross the threshold at Birtu. Added 3 m flat landing pads at the chain top in eight
    directions (connector-marked, clipped by the scene probe), and made the pad test ignore the surface
    it sits on (`OwnGeometryBlocks` now skips geometry at/below padTop+0.05 instead of padTop-0.3).
    The harness snapshot now carries `ConnectorTris` into the force+exempt masks (it previously marked
    every ramp force-only, so connector pads were never exempt).
    Result with `--ramps on --coverage civilian_ladder`:
    - `civilian_ladder_6m` (open top): 13.7% -> **78.4%** coverage; probes now find landing faces.
    - `civilian_ladder_5m` (Birtu doorway): still **0%**; its top is surrounded by `editor_cube` /
      stone-heap geometry at (302.7,316.1..317.0) and no pad face appears at 21.0-21.5 even with 8
      directions and a 0.6 m pad.
    - Basamu `civilian_ladder_4m` coverage unchanged (the user-visible working case is not regressed).
    - Full suite otherwise green (PROP-RAMP / DOORWAY-LADDER / NARROW-DOORWAY PASS); Test 25 still red.
    The 5m top needs the scene's own `SeekSurface`/`IsFreeAt` behaviour, which the snapshot harness does
    not reproduce; this is the first thing to check in-editor.


- Acceptance: Birtu `civilian_ladder_5m/6m` coverage near 100% with `--ramps on`, `--test25` PASS,
  full suite green elsewhere, module build clean.

## 13. Native small doorways are below the ordinary connectivity threshold (measured)

- USER REPORT (2026-11-02): the Birtu ladder-top doorway is open but **short and thin**; the tool
  generally struggles with small native doorways. The door is small enough that a full-height
  character would clip its head. This is a doorway-handling issue, not a ladder-climb issue (Basamu
  ladders work).
- MEASURED with `--doorsweep` (harness): a 10 x 10 floor, a 1.0 m thick wall at x=5, and a flat or
  arched opening swept in 0.1 m steps. cs=0.25, radius=0.25, region defaults. Y = the two sides are
  connected.
  - `agentHeight=1.8 m` (walkableHeight 9 cells): FLAT connects only at **h >= 2.0 m AND w >= 2.2 m**.
    Below either threshold the sides are separate components.
  - `agentHeight=1.7 m`: **identical** (ceil(1.7/0.2) is still 9 cells, 1.8 m). Changing 1.8 -> 1.7
    therefore has no effect.
  - `agentHeight=1.6 m` (walkableHeight 8 cells): FLAT height threshold drops to **1.9 m**, width stays
    **2.2 m**. The width floor is the dominant limit.
  - ARCH is stricter: at 1.8 m it does not connect anywhere up to 3.0 m wide x 2.4 m high; at 1.6 m it
    needs w >= 2.8 m at h=2.4 m. The arch narrows toward the top, so the walkable band is thinner than
    a flat opening of the same apex height.
  - Width loss (2.2 m for a 1.0 m wall) is the agent-radius erosion (0.5 m total) plus contour/tile
    margins; height loss is walkableHeight + one cell. It is not a bug in the doorway geometry.
- Consequence: a native doorway around 1 m wide / <2 m high cannot pass ordinary geometry at cs=0.25 /
  radius=0.25, no matter how the height is nudged. It needs the same treatment as a gate or a ladder top:
  a forced connector strip through the throat (force-walkable + ledge/clearance/erosion exempt), or a
  locally exempt doorway region. Lowering `AgentHeightMeters` to 1.7 is a no-op; 1.6 only buys 0.1 m of
  height and does not help width, so the shipped default stays 1.8.
- Reproduce: `TiledCoreHarness --doorsweep` (prints the flat/arch grid for 1.8 / 1.7 / 1.6).
- RADIUS SWEEP (2026-11-02): `--doorradius` holds agentHeight=1.8 and sweeps the agent radius.
  - radius 0.25, 0.20, 0.15, 0.10, 0.05 all give walkableRadius = ceil(r/0.25) = **1 cell (0.5 m)** and
    produce **identical** grids: flat connects only at h>=2.0 **and w>=2.2 m**.
  - radius 0.00 gives **0 cells** and moves the width threshold to **w>=1.0 m**; the height threshold
    stays 2.0 m.
  - So radius is a cliff, not a dial, at cs=0.25: anything above 0 erodes the same one cell. Only
    radius 0 helps, and it removes the agent inset from the entire map (navmesh tight to walls and
    ledges), which is not acceptable as a global default.
  - Birtu check: `--ramps on --radius 0 --coverage civilian_ladder` moves `civilian_ladder_5m` from
    0% to **40%** coverage, but the probe 0.4 m beyond the ladder top still has no face (gap 0.25 m at
    (303.043,316.126)). Radius 0 alone does not cross the doorway; it only removes the erosion loss.
- Next: a doorway connector strip (or a locally exempt throat) remains the right fix. Radius tuning
  alone cannot open a ~1 m doorway without setting the global radius to 0.
- Next: detect doorway throats geometrically (a short, narrow gap between walls with a floor through it)
  and emit a connector strip, reusing `GateBridgeStrips`/the ladder top-pad exemptions. Validate against
  this sweep first: the strip should connect 0.8-1.2 m doorways without opening walls.

## 7. Persist the acceptance suite in the repo (accepted; queued - no repo changes yet)

- The 27-test harness suite (plus flagged modes: barriers, quadtest) lives only in the temp harness project
  `C:\Users\Sheepbitter\AppData\Local\Temp\opencode\tiled-core-harness\Program.cs`; run logs are `*_suite.txt`.
  Workers run it from their own copies - same suite, but not versioned, easy to drift, and lost with any
  temp cleanup.
- Move the harness project (Program.cs + harness.csproj with relative include paths) into the repo
  (e.g. `tools/`) so the acceptance matrix is versioned and shared.
- Fold per-task regression checks into it as tests: the roof clearance metric (no face below the collision
  roof), the CC_20 gate connection check, and the determinism run-twice + golden hashes (item 1).
- Note when executing: the module csproj uses default compile globbing, so adding a harness under
  `tools/` requires `<Compile Remove="tools\**\*.cs" />` in the module project (or the harness `Main`
  would get compiled into the game DLL).

## 8. Ladder spawners - DECIDED 2026-10-07: DROPPED for the procedural generator

- USER DECISION (2026-10-07): no ladder-spawner handling - the procedural generator will use civilian
  ladders instead. The spawner-specific plan below is archived context, kept for the record.
- PARKED AS OPTIONAL (not spawner-specific): the group-id writer finding - `Nmg8Writer` writes f1=0 for
  every face, so the engine's ladder machinery cannot engage on ANY of our bakes (civilian ladders
  included) unless the writer gains face group ids. Only needed if engine-native ladder climbing is ever
  wanted; our synthesized ladder connectors carry bots up without it.
- Original request (2026-10-06) and findings follow for the record:
- Siege ladder spawner props: work out how they should be handled by the navmesh pipeline. Questions:
  spawner entity vs the ladder spawned at runtime; does the baked navmesh need a connector at the spawn
  location (bots must be able to climb spawned ladders); what flags/geometry the spawner carries in the
  editor scene; what the current flag-based classification does with it.
- Starting reference: `Feedback\3.48\siege_ladder_spawner.PNG` (ladder leaning on the palisade, agents at
  its base, navmesh fan at the base).
- Tie-in: the ladder connector work (top/bottom connections including ladder-to-ladder; the ladder-flag
  exemption from the synthesis slope cap).
- PLAN READY (2026-10-07): `LadderSpawnerPlan.md` - and it found the ROOT CAUSE: the engine's ladder
  machinery keys off per-face GROUP IDS (the NMG8 face record's f1 = `PathFaceRecord.FaceGroupIndex`);
  our `Nmg8Writer` writes 0 for every face (a mislabelled field), so `SetAbilityOfFacesWithId` is refused
  for group 0 ("Can not enable/disable navigation faces with group id 0" in the native DLL) and
  `MarkFacesWithIdAsLadder` marks nothing - engine-side climbing can never engage on our bakes, even for
  `--ramps on` civilian ladders. Verified by zero-gap byte parses of 4 vanilla prefabs (siege tower f1 =
  1..7 exactly matching its code refs) + the decompiled runtime chain.
- Spawners themselves: `siege_ladder_*m_spawner` script entities (5 in CC_20, ALL with the invalid
  `OnWallNavMeshId=-1`), skipped by the pipeline (`HasPhysicsBody()` false - no shape); the ladder is a
  runtime-spawned separate prefab.
- Plan: optional `faceGroupIds` in `Nmg8Writer`; enumerate spawners by script + global frame in
  `SceneCollisionGeometry`; group-id-aware synthesis in `SteppedPropRamps`; carry the id through tiles;
  exempt tagged faces from steep pruning. Full validation plan in the report.
- OPEN QUESTIONS (need the user): (1) auto-assign `OnWallNavMeshId` + patch scene.xscene, or refuse to
  bake unset spawners? (2) assign scheme: sequential (2,3,4...) or ones-digit lane encoding (the engine
  compares `FaceGroupIndex % 10`)? (3) should `--ramps on` civilian ladders also get group ids (making
  them engine-climbable) or stay unmarked? (4) deployed-ladder geometry: prefab `bo_siege_ladder_*m`
  shape at the computed frame, or a simple plank ramp?
- VALIDATED (2026-10-07): an independent validator (LongCat 2.5) wrote its own parser from scratch,
  re-parsed all four vanilla prefabs with zero gaps (f1 values exact against the engine refs), re-read
  the three writer paths (all write 0) and both import paths, found the native refusal string, re-checked
  the 5 CC_20 spawners, and exhaustively searched the decompiled code for ANY path that could make ladder
  climbing work with group id 0 - none exists. All claims CONFIRMED. Report: `LadderPlanValidation.md`.
