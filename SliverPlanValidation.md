# Independent Validation — Sliver Census & Reduction Plan

**Validator model/provider:** LongCat 2.5 Preview Free (OpenCode Go), `longcat-2.5-preview-free`.
**Target:** `sliver_plan\SliverReductionPlan.md` (produced on other models: Space Bunny Free, NVIDIA NIM GLM 5.3 Flash).
**Date:** 2026-10-07. **Repo:** read-only; all work in `C:\Users\Sheepbitter\AppData\Local\Temp\opencode\sliver_plan\`.
**Method:** rebuilt `SliverCensus.exe` from source (`dotnet build sliver.csproj -c Release`), verified the code claims by inspection, then re-ran the fixtures with my own commands. `val_*.txt` are my runs; `out_*`/`sweep_*` are the originals.

## Verdict summary

| # | Claim | Verdict |
|---|---|---|
| 1 | ~99% of slivers born in the per-tile build; per-tile stitch +127/+14/+154; merge-stitch +0..4; prune/cleanup are removers; definitions = cleanup's | **CONFIRMED** |
| 2 | conterr 1.3→2.0 vox: −43/−24/−34%; 3.0: −57/−33/−45%; tile 16→12: −13/−16/−4%; 2.0+tile12 −52%, build −27% | **CONFIRMED** (all CC_20 magnitudes exact; cross-fixture 2.0 exact) |
| 3 | `SceneNavMeshExporter.cs:140` one-line change; 0.50 m budget == `SurfaceToleranceMeters`; detail off worse; edgelen/regmin/regmerge/weldtol/vpp/cs don't touch | **CONFIRMED** (don't-touch list by artifact, not re-run) |
| 4 | Falsify detail-off and conterr 1.0 | **CONFIRMED worse** (+92%, +12.4%); see "missed options" below |
| — | New: `MaxCleanupSlopeDegrees`/`FaceSlopeDeg` dead code; 67% of survivors are steep≥40° | **FINDING** (refines, does not refute) |

No claim was refuted. Two small refinements/caveats are noted at the end.

---

## Claim 1 — WHERE THEY ARE BORN — CONFIRMED

**Definitions.** `SliverCensus.cs:20-22` references `NavMeshCleanup.NeedleAltitudeMeters` / `NeedleLongestEdgeMeters` / `ThinMinAngleDegrees` **directly**, and its `Len`/`Area`/`MinAngle` (SliverCensus.cs:181-206) are byte-identical to the cleanup's (NavMeshCleanup.cs:239-264). The cleanup constants are needle = alt<0.05 m & longest>0.5 m; thin = minAngle<5° (NavMeshCleanup.cs:41-44) — verified against the real repo. So the census cannot drift from the cleanup's metric.

**Strongest check:** the census's `afterCleanup` sliver count equals the cleanup's *own* logged `thin after` count in every run (1049==1049, 602==602, 267==267, 989==989). The census is not a parallel possibly-buggy implementation; it agrees exactly with the pipeline's counter.

**Stage deltas — my independent CC_20 defaults run** (`val_cc_defaults.txt`):

| stage | my run (n&t) | plan | match |
|---|---|---|---|
| rawTileMesh | 7184 | 7184 | exact |
| tileAfterStitch | 7311 (+127) | 7311 (+127) | exact |
| afterMerge | 7253 | 7253 | exact |
| afterNonManifold | 7252 (−1) | 7252 | exact |
| afterPlateBlend | 7247 (−5) | 7247 | exact |
| afterMergeStitch | 7247 (+0) | 7247 | exact |
| afterSteepRepair | 7210 (−37) | 7210 | exact |
| afterPrune | 5933 / 266224 | 5933 / 266224 | exact |
| afterCleanup | 1049 / 241328 | 1048 / 241401 | ±1 (wall-clock noise) |

7184/7253 = **99.05%** of slivers exist at `rawTileMesh` (before any stitching); the per-tile stitch adds 127 (net +69 after border dedup); cross-tile merge-stitch +0. **Confirmed.**

**Hook placement** (`TiledNavMeshBuilder.cs`): `TileRaw` is filled at L4208, immediately after `BuildDetailOutput`/`BuildFanOutput` (L4199-4202) and *before* `StitchTileComponents` (L4211-4219); `TileStitched` at L4225. So `rawTileMesh` is genuinely pre-stitch and the attribution to the detail/fan triangulation is code-grounded. Birth-mechanism citations all verified line-exact: `TriangulateHull` RcMeshDetails.cs:633, `BuildPolyDetail` :715, `maxd > sampleMaxError²` :826, `minExtent < sampleDist*2` :866, `DistToPoly > -sampleDist/2` :910; `SimplifyContour` RcContours.cs:226, `maxd > maxError²` :364, edge-length split :379/:415.

**Birtu/Basamu:** independently reproduced the baseline for Birtu (353, islands 5 — exact) and the conterr-2.0 pattern for both. The Birtu/Basamu default stage deltas were not re-run stage-by-stage, but the same hook/definition code applies.

## Claim 2 — THE LEVERS — CONFIRMED (all magnitudes exact)

My independent runs vs the plan:

| run | my thin-after | plan | verdict |
|---|---|---|---|
| CC_20 defaults | 1049 | 1048 | CONFIRMED (±1 noise) |
| CC_20 conterr 1.0 | **1179** | 1179 | exact |
| CC_20 conterr 2.0 | **602** | 602 | exact (−42.6% vs my 1049) |
| CC_20 conterr 3.0 | **450** | 450 | exact (−57.1%) |
| CC_20 tile 12 | **915** | 915 | exact (−12.8%) |
| CC_20 tile 8 | **804** | 804 | exact (−23.4%) |
| CC_20 conterr2+tile12 | **500** | 500 | exact (−52.3%) |
| Birtu defaults | **353** | 353 | exact |
| Birtu conterr 2.0 | **267** | 267 | exact (−24.4%) |
| Basamu conterr 2.0 | **989** | 994 | CONFIRMED (−34.4% vs 1508; ±5 noise) |

**Invariants/sanity at conterr 2.0:** `invariants=PASS`, `SNAPSHOT: PASS`, `assert-points: 0`, `concaveQuads=0`, `xzConcave/xzOrientBad/xzReflex=0` on all three fixtures; islands 13 (CC_20) / 6 (Birtu) / 1 (Basamu) with `components N->N (kept)`. **Build time:** defaults 39818 ms → conterr2+tile12 29783 ms (**−25%**, plan −27%; machine variance). The reduction is real quality, not just smaller mesh: afterPrune per-1k 22.3→18.3, afterCleanup per-1k 4.35→2.56 (conterr2.0) and 2.10 (c2t12) / 1.56 (t8c2).

## Claim 3 — THE RECOMMENDATION — CONFIRMED

- **One-line change:** `SceneNavMeshExporter.cs:140` `public float NavMeshVoxelContourMaxError = 1.3f;` is the single source. Path B uses it directly (`:1363 voxel.ContourMaxErrorVoxels = NavMeshVoxelContourMaxError`); path A receives it as the `voxelContourMaxError` argument at the `:844` call (signature `:994`). Both derive from `:140`.
- **Tolerance chain:** the builder logs `contour maxError=2vox (= 0.5m at cs=0.25m)` (defaults log `1.3vox (= 0.325m)`); `maxSimplificationError` is passed to `RcContours.BuildContours` (L4184) and is voxels (`EffectiveContourMaxError` L6222). `NavMeshCleanup.cs:34 SurfaceToleranceMeters = 0.50f`. So 2.0 vox × 0.25 m = 0.50 m **equals** the cleanup's own surface tolerance. **Confirmed.** (The codebase itself already makes this equivalence at NavMeshCleanup.cs:32: "0.30 m equals the contour simplification's own budget (1.3 vox at cs 0.25 m)".)
- **Detail off is worse:** reproduced exactly — CC_20 2013 (+92% vs 1049), `refined=0`; Basamu artifact 4533 (+201%). **Confirmed.**
- **Don't-touch list (edgelen/regmin/regmerge/weldtol/vpp/cs):** confirmed **from the original artifacts**, not re-run by me. The numbers are internally consistent (edgelen20 1141 +9%, edgelen6 1341 +28%; regmin 1058/1038 ~0; regmerge 1110/1057; weldtol 1048/1048; vpp3 1281; cs 1203/1146). `--vpp 8` is rejected (`VertsPerPoly must be in [3, 6]`), confirming that open question.

## Claim 4 — FALSIFICATION & MISSED OPTIONS

**Falsifications reproduced:** detail off = 2013 (worse), conterr 1.0 = 1179 (worse). Both confirmed, exactly matching the originals.

**Options the census did not test** (my runs):

| option | thin-after | Δ vs 1049 | islands | assessment |
|---|---|---|---|---|
| `--radius 0.5` (shipped 0.25) | 786 | −25% | 13 | reduces slivers but is a **load-bearing agent-size change** (`NavMeshVoxelAgentRadius`, SceneNavMeshExporter.cs:66) — not a free lever |
| `--slope 45` (shipped 50) | 930 | −11% | 12 | load-bearing (`NavMeshMaxSlopeDegrees`, :37) — not a free lever |
| `--tile 8 --conterr 2.0` | **423** | **−60%** | 15 | **numerically better than the recommended combo (500)**, but tile 8 is the more aggressive tile change (per-tile stitch 726→970, CC_20 merge-stitch pairs 808→0) |

**Conclusion for Claim 4:** nothing beats the recommendation *without* a load-bearing semantic change. The only free (semantics-preserving) option that beats conterr2+tile12 is the more aggressive tile 8 combo, which the plan already flags as changing more. radius/slope are cheaper-looking but change agent behavior.

## New findings (not in the plan)

1. **`MaxCleanupSlopeDegrees` (NavMeshCleanup.cs:60) and `FaceSlopeDeg` (:1852) are dead code** — defined but never called anywhere in the repo. The documented guard "Fans containing any face steeper than this are never touched" (comment :57-60) is **not enforced**. Steep slivers survive instead because of the enforced guards: normal spread `MaxFanNormalSpreadDegrees=120` (:40, used :742), move tolerance `BoundaryToleranceMeters=0.35` / `SurfaceToleranceMeters=0.50` (:36/:34), and height change `MaxHeightChangeMeters=0.15` (:69). This *refines* why the residual exists; it does not refute any plan claim (the plan never relied on a slope gate).
2. **67% of surviving CC_20 slivers are steep≥40°** (707/1049 at defaults; 63% at conterr2.0) — the dominant, cleanup-resistant residual. Worth a follow-up: the residual is now majority steep faces, so further sliver reduction must target steep-boundary triangulation (the plan's L4 hypothesis) rather than the cleanup.

## Caveats / limits of this validation

- The cleanup is wall-clock-gated (documented run-to-run variance up to 215 faces, DeterminismResearch.md:31). Baseline counts varied ≤5 faces between runs (1048/1049; 989/994); every *knob* run reproduced exactly. All headline deltas are far outside the noise band.
- The don't-touch list (edgelen/regmin/regmerge/weldtol/vpp/cs) and the Birtu/Basamu default stage tables were verified from the original artifacts, not re-run by me (time budget); the CC_20 levers, the three conterr-2.0 cross-fixture runs, and the Birtu default baseline were all re-run.
- In-engine boundary fidelity at 2.0/3.0 vox and tile-12 loading remain unverifiable from this read-only harness (as the plan already states).

## Artifacts (my runs) and commands

- Dir: `C:\Users\Sheepbitter\AppData\Local\Temp\opencode\sliver_plan\`
- Build: `dotnet build sliver.csproj -c Release`
- Run: `bin\Release\net472\SliverCensus.exe --snapshot <fixture> --slivers --cleanup on [--conterr V] [--tile V] [--detail off] [--radius V] [--slope V]`
- My files: `val_cc_defaults.txt`, `val_cc_conterr10.txt`, `val_cc_conterr20.txt`, `val_cc_conterr30.txt`, `val_cc_tile12.txt`, `val_cc_tile8.txt`, `val_cc_c2t12.txt`, `val_cc_detailoff.txt`, `val_bir_defaults.txt`, `val_bir_conterr20.txt`, `val_bas_conterr20.txt`, `val_cc_radius50.txt`, `val_cc_slope45.txt`, `val_cc_t8c2.txt`
- Scripts: `val_batch2.ps1`, `val_batch3.ps1`
