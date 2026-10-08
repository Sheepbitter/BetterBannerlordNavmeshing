# fixB (fixA v2) — under-roof navmesh faces at the CC_20 house roof: mechanism, minimal fix, acceptance

Model/provider: **NVIDIA NIM GLM 5.3 Flash (z-ai/glm-5.3-flash)**

All work was done in `C:\Users\Sheepbitter\AppData\Local\Temp\opencode\roof_fix\`. The real repo
(`C:\Users\Sheepbitter\source\repos\BetterBannerlordNavmeshing`) was **never modified** — it still has
fixA v1 deployed (which failed the gate); it must be reverted to pristine `9f7477a` before fixB is applied.

---

## 1. The mechanism (why commit `386a1fb` = V3.50 put faces under the CC_20 roof)

Pipeline order at `386a1fb`:

```
merge → RepairNonManifold → forcedRampCells → BuildSoupGrid+MergeStitch
      → RepairSteepFaces            ← V3.50 runs it HERE
      → PruneSmallIslands
      → results = null
      → NavMeshCleanup.Run          ← time-budgeted; returns quadPairs
      → RepairFaceQuality
      → ComputeInvariants
```

Per-stage count of faces with **negative roof clearance** in the CC_20 roof window (radius-22 dump
around `1187,1078`):

| stage | V3.50 (steep ON) | steep OFF (V3.48-equivalent) |
|---|---|---|
| after merge | 18 | 18 |
| after steep pass | 17 | 18 |
| after prune | 4–5 | 4–5 |
| **after NavMeshCleanup.Run** | **8** | **0** |

The under-roof faces are **born inside `NavMeshCleanup.Run`, downstream of the steep pass**. The
cleanup is time-budgeted; its sliver/decimate work competes for that budget, and the mesh the
cleanup receives decides which needles it decimates and where its boundary vertices move. The steep
pass running *ahead* of it changes exactly that, and the cleanup then leaves 8 under-roof faces the
pre-V3.50 builds did not have. Ablation proof: `--steepmargin 100` (which clamps the steep threshold
to 89.9°, making the pass a no-op) reproduces the V3.48 roof **exactly** — so the trigger is the
steep pass's *presence before the cleanup*, not what it removes.

Two side effects follow from moving the pass after the cleanup, and both are repaired by fixB:

1. **Stale quad pairs → gate FAIL.** `NavMeshCleanup.Run` records `quadPairs` against the final face
   numbering; the moved pass drops faces and `CompactFaces` renumbers, so `RepairFaceQuality`'s remap
   maps stale indices to *wrong* faces. Measured: gate FAIL `badQuadWalks=1208` (CC_20) / `310`
   (the user's map) for a pure call-site move. The original `386a1fb` itself PASSES with
   `badQuadWalks=0`, so this must be repaired, not tolerated.
2. **Orphan fragments.** A pass that drops faces *after* the prune splits components;
   `RestoreSplitComponents` rejoins what it can ("restored as the minimum needed to keep the island
   count") but leaves fragments. The user's export gate saw exactly this: island #2 = 1 face,
   0.41 m². Measured on CC_20: islands 15 → 18 with a pure call-site move.

## 2. The fix — `patches\fixB\patch.py` (+ `patch.ps1` wrapper)

Python patcher, applies to the **pristine** source of both `386a1fb` (V3.50) and `9f7477a` (V3.61)
(auto-detects the barrier-era base via `'ObstructionCells obstructions)'`). It edits
`<workdir>\src\TiledNavMeshBuilder.cs`. Line numbers below are in the **patched** 386a1fb file.

| # | change | function | patched lines |
|---|---|---|---|
| 1 | remove the steep call from V3.50's position (before the prune) | build pipeline | — |
| 2 | hoist `steppedCells`/`qcs` above the prune's if-block so pass 2 reuses them | build pipeline | ~930–950 |
| 3 | insert the moved steep call **after the cleanup**, capturing component labels **+ their pre-drop face counts**, and the **second island prune** | build pipeline | 954–1000 |
| 4 | new shared helpers `RemapQuadPairsAfterCompaction` (the V3.46 "lost pairs mark −1" machinery, extracted) and `CompactPerFace` (per-face label compaction) | new | 3528–3591 |
| 5 | `RepairFaceQuality`'s inline remap → the helper (behaviour unchanged); labels compacted | RepairFaceQuality | 3593, 3604 |
| 6–7 | `RepairSteepFaces` becomes quad-aware: returns `int[] quadPairs`, remaps through the helper, compacts the labels | RepairSteepFaces | 3719, 3745 |
| 8–12 | `PruneSmallIslands` becomes quad-aware (returns the pairs, remaps through its own drops); `PruneStats` gains `QuadPairsKept/QuadPairsLost` | PruneSmallIslands | 1247, 1239 |
| 13 | **the touched test** (see below) | PruneSmallIslands | 1344–1360 |
| 14 | the island judgement honours the touched test | PruneSmallIslands | 1381–1385 |

### The second prune's rule ("touched islands only")

The cap runs a **second time** after the steep/face-quality repairs, but it judges an island **only
if its pre-drop component lost faces to those drops** — split into fragments *or* shrunk to a
remnant. A component the drops never touched is never re-judged: the first prune already judged it
at its full size, and the cleanup's decimation — which also runs after the first prune, in V3.50
just as here — is not a reason to re-judge it (re-judging would cull three real CC_20 islands of
356–400 faces / ~1000–1200 m² that the accepted V3.50 mesh has). This replicates V3.50's prune
semantics exactly in every case, because V3.50's prune also ran *after* its drops.

Implementation detail that mattered: `FaceComponentsOf` returns the **raw union-find parents** —
faces of one component can carry *different* intermediate parents. Without compressing first
(`preSteepComponents[i] = FaceFind(preSteepComponents, i)` for every entry, line 959), a face cannot
be matched to its pre-drop component at all: the diagnostic build showed a 20-face island whose
first face's label had `totalNow=1`, the touched set contained only 1 of 18 islands, and prune2
culled **nothing**. With the normalization: `sizes=15`, `touched=7`, prune2 culled exactly the 3
fragments (23 faces = 2+20+1) and kept 15.

Also kept from the earlier analysis: `RepairFaceQuality` must stay **after** the steep pass (the
steep restore leaves 1 inverted face at the staircase hub that faceq catches), and prune1's
behaviour is byte-identical (no pre-drop reference ⇒ the cap judges every island, as it always has).

## 3. Before / after numbers

### CC_20 roof window (radius-22 dump at `1187,1078`)

| build | faces in window | exact ridge crossers | clearance < 0 | min clearance | gate |
|---|---|---|---|---|---|
| V3.48-equivalent (`--steepmargin 100`) | 109 | 7 | **0** | **+0.067 m** | PASS |
| V3.50 = `386a1fb` pristine | 116 | 11 | **8** | **−2.960 m** | PASS (badQuadWalks=0) |
| `386a1fb` + **fixB** | **109** | **7** | **0** | **+0.067 m** | **PASS (badQuadWalks=0)** |
| `9f7477a` + **fixB** | **109** | **7** | **0** | **+0.067 m** | **PASS (badQuadWalks=0)** |

fixB's roof is **identical to V3.48** — the regression is fully reversed.

### CC_20 staircase (census spot `1120.50,1077.00` r=5, `--ramps on`)

| build | steep(>55°) | steep area | worst slope | vertical | islands |
|---|---|---|---|---|---|
| V3.48-equivalent | 11 | 39.108 m² | 87.39° | 0 | 15 |
| V3.50 pristine | 7 | 14.320 m² | 72.08° | 0 | 15 |
| `386a1fb` + fixB | **6** | **14.967 m²** | **81.57°** | **0** | **15** |
| `9f7477a` + fixB | **6** | **14.967 m²** | **81.57°** | **0** | **15** |

The staircase repair V3.50 was made for is preserved: steep faces 11 → 6, steep area 39.1 → 15.0 m²,
no vertical faces, islands=15. Two honest differences vs V3.50: (a) the cleanup retriangulates
before the steep pass marks, so the *faces* differ — the one remaining steep face is `f132999`
(81.57°, 8.4 m tall, 6.6 m², at `1120.60,120.70,1080.05`) where V3.50's worst was 72.08°; the count
and islands match. (b) V3.50's own acceptance logs recorded 6/13.657 — the pristine baseline as
measured here is 7/14.320; the difference is most likely an args difference in those earlier runs
(could not be determined).

### Birtu / Basamu

| build | birtu islands / faces | basamu islands / faces | ladder probes | gate |
|---|---|---|---|---|
| V3.48-equivalent | 5 / 44505 | 1 / 492847 | isl=0 (main net) | PASS |
| `386a1fb` + fixB | **5 / 44499 (−0.02%)** | **1 / 492741 (−0.02%)** | all 4 on main net | **PASS (badQuadWalks=0)** |
| `9f7477a` + fixB | **5 / 44496 (−0.02%)** | **1 / 493154 (+0.06%)** | all 4 on main net | **PASS (badQuadWalks=0)** |

Face deltas vs V3.48-equivalent are ≤ 0.06% — V3.50's were +0.33% / −0.24%.

### No orphan ships

fixB final island sets: 15/15/5/1 at both bases — exactly V3.50's counts, no tiny islands. The
second prune's arithmetic proves it: prune1 keeps 15 originals, the repairs create 3 fragments
(23 faces = 2+20+1), prune2 culls exactly those 3 → 15 remain, all originals. The three marginal
islands V3.50 shipped (362/356/398 faces at `(1076.0,318.5,534.4)`, `(1881.2,294.6,1567.5)`,
`(1452.1,545.7,151.8)`) are untouched by the drops and therefore kept.

## 4. Artifacts & commands

All under `C:\Users\Sheepbitter\AppData\Local\Temp\opencode\roof_fix\`:

- `patches\fixB\patch.py` / `patch.ps1` — **the fix (ships)**. `python patch.py <workdir>`.
- `patches\diag3\patch.py` / `patch.ps1` — the touched-test diagnostic (never ships).
- `patches\diag2\patch.py` / `patch.ps1` — islands-log widener top10→top40 (never ships).
- `Accept.ps1` — acceptance matrix: `-Sha <sha> -Patch <patch.ps1> [-Patch2 <diag.ps1>] [-Tag <tag>] [-Only roof|stairs|birtu|basamu]`.
- `accept\fixB_386a1fb_final\` — roof/stairs/birtu/basamu (clean fixB, no diag).
- `accept\fixB_9f7477a_final\` — same for V3.61.
- `accept\fixB3diag_386a1fb\` — fixB + diag3 (the diagnostic numbers quoted in §2).
- `accept\orig_386a1fb\`, `accept\origdiag_386a1fb\` — pristine V3.50 baselines.
- `accept\good48\` — V3.48-equivalent baselines incl. new `birtu_nosteep.txt`/`basamu_nosteep.txt`
  (args `runargs\nosteepbirtu.txt`, `runargs\nosteepbasamu.txt`).
- `work\<sha>_<variant>\` — per-(sha,variant) built harness copies (reusable).

Verification commands actually run: the full `Accept.ps1` matrix for both bases (all four variants
built + ran), `cls\roofobj.py` on every roof dump, `cls\islands.py` on the roof dumps, and the
prune/census/invariants lines grepped from every run log.

## 5. What could not be determined

1. **The user's own map** (the editor log with `badQuadWalks=310`, islands=3): verified only by
   mechanism class on CC_20/Birtu/Basamu; the user cannot test until v2 ships.
2. **V3.50's earlier census numbers** (6/13.657 vs 7/14.320 measured here on the pristine baseline):
   most likely an args difference in the earlier acceptance runs; not determinable from the logs.
3. **The remaining 81.57° staircase face** (`f132999`): whether it matters in-engine needs an
   in-engine test; V3.50's analogue was 72.08°.
4. **`67455a2`** (a third commit present in `work\`): untested — the mandate covers `9f7477a` and
   `386a1fb` only; the patch's pre-barrier branch would likely apply but is unverified.
