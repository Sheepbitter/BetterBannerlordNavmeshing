# TODO

Working list for BetterBannerlordNavmeshing. Newest/current items first.

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
