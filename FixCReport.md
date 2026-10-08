# fixC — the staircase-top regression introduced by fixB: analysis, fix, acceptance

Model/provider: **LongCat 2.5 Preview Free (OpenCode Go)**

Work root: `C:\Users\Sheepbitter\AppData\Local\Temp\opencode\fixC2\`. The real repo
(`C:\Users\Sheepbitter\source\repos\BetterBannerlordNavmeshing`) was read-only (pristine `9f7477a`); the
dead session's `fixC\` and the `roof_fix\` workspaces were read-only inputs. No commits, no deploys.

---

## 1. The regression, in faces and vertices

CC_20 staircase (`1120.50,1077.00` r=5, `--ramps on`), final mesh:

| build | steep>55 | steep area | worst slope | wall faces (>55deg, >4m tall) |
|---|---|---|---|---|
| `67455a2` V3.48 (no steep pass) | 11 | 39.108 m² | 87.39° | fan of 6: 87.3/87.3/87.4/72.4/84.5/69.6° |
| `386a1fb` V3.50 (steep pass BEFORE cleanup) | 7 | 14.320 m² | 72.08° | **f132990 72.1°, f132993 70.5°** |
| `386a1fb`+fixB (steep pass AFTER cleanup) | 6 | 14.967 m² | 81.57° | **f133022 81.6°** |
| `9f7477a`+fixB | 6 | 14.967 m² | 81.57° | **f133022-equivalent 81.6°** |

The "staircase wall" is a Recast **contour spike**: a fan of faces that all share the single hub vertex
`H = (1120.500, 115.700, 1078.500)` — the contour vertex of the voxel column where the stepped prop
`stairs_bo` and the `european_castle_ladder` share a column. The fan leaps from the foot (`y=115.7`)
straight to the deck edge (`y=124.1`), spanning 8.4 m.

### What V3.50 does that fixB no longer does

Stage-by-stage (from `work\386a1fb_stairinstr` and `work\386a1fb_fixB_stairinstr`, the dead session's
`ZZStairDbg` dumps):

| stage | V3.50 wall faces | fixB wall faces |
|---|---|---|
| merge (identical) | 6: 87.3, 87.3, 87.4, 72.4, 84.5, 69.6° | 6: same |
| after steep pass | **2: f181176 72.4°, f181182 69.6°** | (pass not run yet) |
| after prune | 2 | 6 |
| after NavMeshCleanup.Run | **2: f132993 70.5°, f132990 72.1°** | **5: 87.4, 81.6, 69.6, 84.5, 84.5°** |
| after steep pass (fixB only) | — | **1: f133022 81.6°** |
| final | **f132993 70.5°, f132990 72.1°** | **f133022 81.6°** |

**V3.50:** the steep pass ran *before* the cleanup. It marked the fan, and `RestoreSplitComponents`
brought back the minimum needed to keep the island count — at the staircase, the two shallowest fan
faces `f181176` (72.4°) and `f181182` (69.6°), both sharing `H`. The cleanup then **retriangulated those
two into the accepted wall** `f132993` (70.5°) + `f132990` (72.1°).

**fixB:** the cleanup ran *first*, on the mesh with the fan still present. Its own retriangulation
turned the 6-face fan into 5 faces including a **single 81.6° face** (`f133033`, 6.649 m²) that spans
from `H` all the way to the deck. The moved steep pass then marked those 5 and the restore kept the
81.6° face — because it is the best connector (measured `touched=7, size=17`, the max at that hub;
V3.50's identical 69.6° face `f133036` scores only `touched=2, size=5` and is dropped).

So the deck where the flights meet is bounded by **two ~71° faces in V3.50** but by **one 81.6° face in
fixB**; the 40° approach face (`f133017`) is common to both. Measured XZ coverage of up-facing faces is
essentially identical (294.1 vs 294.5 m² in the window; the deck window 17.24 vs 16.30 m²), so the
visible change is the wall's shape and the near-vertical fan face under the deck — the user's "deck
bare, fan beneath".

**The trigger of the roof fix.** fixB moved the steep pass after the cleanup because the cleanup,
running on the post-steep mesh, creates under-roof faces at `(1187,1078)`. Measured here (see §3):
the roof bug is caused by pre-cleanup steep drops of a handful of faces **under the collision roof**
(dropping the 5 marked faces in `x[1170,1205] z[1065,1092]` reproduces the bug: 6 faces clearance<0,
min −2.891 m), while dropping the other **1203** marked faces is roof-clean (0 clearance<0, min
+0.067 m). The staircase drops are among the roof-clean ones.

## 2. The fix — `patches\fixC\patch.py` (+ `patch.ps1`)

Applied on top of fixB: `run.ps1 -Sha <sha> -Patch patches\fixB\patch.ps1 -Patch2 patches\fixC\patch.ps1`.
Applies to `<workdir>\src\TiledNavMeshBuilder.cs`; auto-adapts to both bases (the barrier-era V3.61
source already carries the restore guard, so that edit is skipped there).

### Candidate directions assessed

1. **Carry pre-cleanup steep markings into the post-cleanup pass** — rejected. The post-cleanup wall is
   the *cleanup's own* product (the 81.6° face is created inside `NavMeshCleanup.Run`); carrying the
   pre-cleanup marking cannot reconstruct V3.50's two-face wall, only re-drop the face the cleanup made.
2. **Adjust the restore preference at the fan** — rejected. Measured (`patches\diag_restore`): fixB's
   restore keeps `f133033` (81.6°) because it is the best connector (`touched=7, size=17`, the max at
   that hub); V3.50's geometrically identical 69.6° face `f133036` scores only `touched=2, size=5`.
   Any preference change alters every component's restore and still cannot reintroduce the ~71° pair
   (which is the cleanup's retriangulation, not a restore choice).
3. **Handle the top-of-flight faces specifically** — rejected; no clean geometric handle.
4. **Chosen: run the steep pass before the cleanup (V3.50's position) with V3.50's own connectivity
   restore, then narrow its drop set to the tall contour-spike fan faces.** This is V3.50's mechanism,
   narrowed so the roof stays clean; because the narrowed set is a *subset* of V3.50's restored set, it
   can only coarsen the components V3.50 already kept, so the island count is preserved by construction.

### The narrowing that keeps the roof clean

The whole reason fixB moved the pass after the cleanup was the under-roof faces the cleanup creates on
the post-steep mesh. Measured here, that regression is **not** caused by the pass in general: dropping
the **1203** marked faces that lie *outside* `x[1170,1205] z[1065,1092]` is roof-clean
(0 clearance<0, min +0.067 m), while dropping just the **5** marked faces *inside* that window
reproduces it (6 clearance<0, min −2.891 m). Those 5 are the short steep faces (own height range
2.0–3.0 m) under the collision roof. The reported fan's faces are 3.2–8.4 m tall. fixC therefore drops
a pre-cleanup marked face only when it is **both** part of a contour-spike fan **and** taller than
`SteepFanMinHeightMeters = 3.5f`.

The order matters and was measured: fixC runs V3.50's `RestoreSplitComponents` on the **full** marked
set first (it restores 1138/1208 on CC_20, exactly V3.50's count), and only then narrows the surviving
70 to the 15 tall fan faces. Filtering *before* the restore (islands 15→17/19) or restoring at the
wrong position (after the prune instead of before it) both break the island count; restoring first at
V3.50's own position keeps it at 15 by construction (the narrowed set is a subset of V3.50's, so it can
only coarsen the components V3.50 already kept).

### The second edit: a latent restore bug the drop exposes

`RestoreSplitComponents` (fixB, on the V3.50 base) indexes `fragRoot` with any fragment id returned
from the vertex→face map; faces are joined by shared **edge**, so a vertex can also be shared with a
face of *another* component and the id is not in `fragRoot` → `KeyNotFoundException`. Pristine fixB
never hits it, but the pre-cleanup drop changes the mesh enough that it does (measured: the experiment
crashed here). fixC ships the two-line guard (`if (!fragRoot.ContainsKey(fr)) continue;`). The
barrier-era V3.61 source already has an equivalent `inComponent` guard, so fixC skips that edit there.

### Measured effect (`386a1fb`, `--ramps on --census 1120.50,1077.00,5`)

| build | pre-cleanup fan drops | staircase steep | steep area | worst slope | roof clearance<0 | islands | gate |
|---|---|---|---|---|---|---|---|
| V3.50 pristine | (pass before prune, all 70) | 7 | 14.320 m² | 72.08° | 8 (min −2.960) | 15 | PASS |
| fixB | 0 | 6 | 14.967 m² | 81.57° | 0 (min +0.067) | 15 | PASS |
| **fixB + fixC** | **15** (of V3.50's restored 70) | **6** | **11.857 m²** | **70.53°** | **0 (min +0.067)** | **15** | **PASS** |

fixC's wall is back in V3.50's ~71° class (70.53° vs 72.08°, and the same 45.7° deck-spanning face
`f132976` V3.50 had) instead of fixB's single 81.6° face. The 1.5° residual vs V3.50 is because fixC
drops only 15 of V3.50's 70 pre-cleanup faces (the cleanup is time-budgeted, so the exact retriangulation
depends on the whole drop set); the deck-relevant result — no near-vertical face at the top of the
flight — is restored.

## 3. Acceptance matrix (both bases)

`run.ps1`/`Accept.ps1` at `fixC2\`; artifacts under `accept\fixC4_9f7477a\` and `work\386a1fb_fixC4_*`.

| variant | `386a1fb`+fixB+fixC | `9f7477a`+fixB+fixC | criterion | verdict |
|---|---|---|---|---|
| roof (CC_20) | 109 faces, 7 exact straddlers, **0 clearance<0**, min **+0.067 m** | 109 / 7 / **0** / **+0.067** | 0 faces below the collision roof | **PASS** |
| staircase (CC_20 census `1120.50,1077.00` r=5) | steep **6**, 11.857 m², worst **70.53°**, islands 15 | steep 6, 11.857 m², **70.53°**, islands 15 | V3.50's ~71° wall (72.08°); 81.57° gone | **PASS (class)** |
| gate (invariants) | **PASS, badQuadWalks=0**, inverted=0, vertical=0 | **PASS, badQuadWalks=0** | PASS | **PASS** |
| islands (CC_20 / Birtu / Basamu) | **15 / 5 / 1** | **15 / 5 / 1** | 15/15/5/1 | **PASS** |
| Birtu (`--probe`) | islands 5, 44481 faces, 4/4 probes on island #0 (main net), PASS | islands 5, 44485 faces, 4/4 probes, PASS | held | **PASS** |
| Basamu | islands **1**, 491579 faces, PASS | islands **1**, 492203 faces, PASS | islands 1 | **PASS** |

Face-count deltas vs the deployed fixB (Birtu 44499/44496, Basamu 492741/493154): Birtu −18/−11
(−0.04 %), Basamu −1162/−951 (−0.24 %/−0.19 %). The island counts, the gate and the ladder probes are
held; the small face delta is the pre-cleanup drop changing the time-budgeted cleanup globally (fixB's
own report allowed ≤0.06 %; fixC is larger on Basamu). **Note:** the "staircase (V3.50 face set
restored)" criterion is met in class, not byte-exactly: fixC's wall is 70.53° where V3.50's is 72.08°
(both the ~71° two-face class; fixB was the single 81.57° face).


## 4. Artifacts & commands

Under `C:\Users\Sheepbitter\AppData\Local\Temp\opencode\fixC2\`:

- `patches\fixC\patch.py` / `patch.ps1` — **the fix (ships)**, applied after fixB.
- `patches\fixc_exp\patch.py` — the env-controlled experiment (FIXC_MODE: regional/spikes/roofbox/
  norof/steppedcells/tallspikes/…) that established the roof-safe narrowing.
- `patches\diag_restore\patch.py` — the restore-candidate logger.
- `patches\fixB\patch.py` — the deployed fixB (input, unchanged).
- `run.ps1`, `Accept.ps1`, `runargs\fixcroof.txt` — the rig (a copy of the dead session's, `$root`
  repointed at `fixC2`).
- `cls\` — the dead session's classifiers (`stairdiff.py`, `roofobj.py`, …) plus `stairdeck.py`,
  `stairsteep.py`, `coverage.py`, `render*.py` written here.
- `work\<sha>_<variant>\` — every built harness copy (reusable); `accept\fixC4_386a1fb\` and
  `accept\fixC4_9f7477a\` — the acceptance matrix artifacts.
- `REPORT_fixC.md` — this file.

## 5. Honest differences / what could not be determined

1. **fixC is V3.50-class, not byte-exact.** fixC's staircase is steep 6 / 11.857 m² / 70.53°, V3.50's
   is 7 / 14.320 m² / 72.08°. fixC keeps only 15 of V3.50's 70 pre-cleanup drops (the tall fan faces),
   and the time-budgeted cleanup retriangulates slightly differently. The wall is the accepted ~71°
   class again (not fixB's 81.6° face), and the deck-spanning 45.7° face is back; the exact V3.50 face
   set is *not* byte-reproduced.
2. **The 3.5 m fan-height floor is tuned to this scene.** It separates the reported fan (3.2–8.4 m)
   from the roof-breaking steep faces (2.0–3.0 m); a different scene could sit differently. It is the
   honest cost of keeping the roof fix while re-running the pass before the cleanup.
3. **Basamu's face count moves −0.24 %** (491579 vs fixB's 492741); its island count (1), gate and
   SNAPSHOT verdict are held. The change is the pre-cleanup drop perturbing the time-budgeted cleanup.
4. **The latent restore bug** (`KeyNotFoundException`) is real in the V3.50 base and is fixed here;
   the barrier-era V3.61 base already had the equivalent guard. It was not the assignment, but fixC's
   drop exposes it, so it ships.
5. **The user's deck rendering** was not reproduced pixel-for-pixel (the two screenshots use different
   cameras); the offline target used is the face set / census at the staircase.
6. **`67455a2` (V3.48)** was compared only from its census (`work\67455a2_stairs`) — no stair stage dump
   exists for it (its instrumentation run used the roof dumper).

## 6. Model / provider

**LongCat 2.5 Preview Free** (provider `opencode-go`). No model switch occurred; no rate-limit or
glitch was hit.
