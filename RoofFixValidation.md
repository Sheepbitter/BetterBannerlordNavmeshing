# Independent validation of the fixB roof fix

Validator model/provider: **LongCat 2.5 Preview Free (OpenCode Go)**

Scope: independently re-derive the 5 claims about `patches\fixB\patch.py` and the deployed V3.61+fixB
build. All work in my own temp copies under
`C:\Users\Sheepbitter\AppData\Local\Temp\opencode\roof_fix\` (`lc_val\`, `lc_test\`, `lc_pdbtool\`,
`accept_val\`, `val_runs*.ps1`, `patches\lc_*`). Real repo read-only (it is pristine `9f7477a`;
`git diff` empty). No commits, no deploys.

Method notes: every claim was re-measured from scratch — I re-extracted pristine `386a1fb` / `9f7477a`
via `git archive`, applied `patches\fixB\patch.py` to fresh copies, built the harness myself, and ran
the same classifier/stage tools. Several of my outputs are **byte-identical** to the prior session's
artifacts, which also demonstrates run-to-run reproducibility.

---

## Claim 1 — MECHANISM: under-roof faces are born in `NavMeshCleanup.Run`, downstream of the steep pass

**VERDICT: CONFIRMED.**

Per-stage negative-clearance counts in the CC_20 roof window (my own instrumented builds,
`patches\instr` + `stages.py`):

| stage | steep ON (my `r386_instr_roof`) | steep OFF `--steepmargin 100` (my `r386_instr_ab_roof`) |
|---|---|---|
| merge | 18 | 18 |
| steep | **17** | **18** |
| prune | 4 | 5 |
| **cleanup** | **8** (minClr −2.960) | **0** (minClr **+0.067**) |
| final | 8 | 0 |

This reproduces the claimed `merge 18 -> steep 17 -> prune 4-5 -> cleanup 8` (ON) and `cleanup 0`
(OFF) exactly.

Per-face birth trace (`stages.py`, steep ON): four of the eight final under-roof faces
(`f133225` −0.401, `f133218` −0.381, `f133224` −0.242, `f133216` −0.113) are **positive** clearance
(+0.26…+0.41) at merge/prestitch/steep/prune and turn **negative only at the cleanup stage**. The
other faces (`f133136` −2.960, `f133135` −0.238) were negative since merge. So the cleanup literally
creates the new under-roof faces.

Decisive ablation re-run (my `r386_ablate_roof.obj`, `--steepmargin 100` on pristine `386a1fb`):
`roofobj.py` → 109 faces in window, 7 exact ridge straddlers, **0 clearance<0**, min **+0.067 m**,
and the file is **byte-identical (SHA-256 `B959261E…`) to BOTH `accept\good48\roof.obj` AND the
actual V3.48 commit's replay `work\67455a2_roof\obj.obj`** — i.e. the ablation reproduces V3.48's
roof **byte-for-byte**, not merely by metric. Pristine V3.50 (`r386_base_roof.obj`): 116 faces,
11 straddlers, **8 clearance<0**, min −2.960 — also byte-identical to `accept\orig_386a1fb\roof.obj`.

Metric credibility check (new): I classified the **user's own editor-verified** NMG8 bins with the
same roof model + `measure()` (`cls\lc_bincheck.py`):
- `CC_20_siege__fresh348.bin` (user GOOD) → 110 faces, 7 exact straddlers, **0 clearance<0**, min +0.059
- `CC_20_siege__fresh350_bad.bin` (user BAD) → 116 faces, 11 straddlers, **8 clearance<0**, min −3.054
- `CC_20_siege__20261001_2231.bin` (older V3.48-era) → 110 faces, 7 straddlers, **0 clearance<0**

The metric cleanly separates the user's good/bad samples and mirrors the replay numbers.

---

## Claim 2 — PATCH: moved pass quad-aware + second prune judges only touched components

**VERDICT: CONFIRMED** (quad-aware necessity confirmed by counterfactual; second-prune semantics
confirmed by direct per-island logging; **one wording nuance on the second prune**, see below).

Diff: I applied `patches\fixB\patch.py` to fresh pristine copies of both commits and diffed
(`git diff --no-index`). It matches the description: steep call removed from before the prune;
`steppedCells`/`qcs` hoisted; the pass re-inserted after the cleanup with a pre-drop component
capture (`FaceComponentsOf` + `FaceFind` normalization + `preSteepSizes`); quad-aware
`RepairSteepFaces`/`RepairFaceQuality` calls; the second prune; new helpers
`RemapQuadPairsAfterCompaction` (exactly the block formerly inline in `RepairFaceQuality`) and
`CompactPerFace`; `PruneSmallIslands` returns `int[]` and remaps its own drops; `PruneStats` gains
`QuadPairsKept/Lost`. Applies cleanly to both bases (pre-barrier / barrier).

**Quad-aware is load-bearing (my counterfactual `patches\lc_noquad`).** Same move, but the steep
pass's remap made a no-op (all-false mask → pairs keep pre-compaction indices):

| build | faces | badQuadWalks | quads / concaveQuads | worstQuadAngle | gate |
|---|---|---|---|---|---|
| real fixB (`r386_fixb_roof`) | 241472 | **0** | 1844 / 0 | 178.38° | **PASS** |
| no steep remap (`r386_noquad_roof`) | 241472 | **1225** | 1844 / **1173** | **359.99°** | **FAIL** |

So the extracted helper is genuinely required by the moved pass. (The prior session reported
`badQuadWalks=1208` for a pure call-site move; my counterfactual — which still runs the second prune
and its remap — gives 1225, same order/effect.)

**Second-prune semantics — falsification attempt (my `patches\lc_diag`, own per-island log).**
Direct measurement at the second prune (CC_20 roof, 18 islands present; `cap=400`):

- **Genuinely split components are judged and culled:** the 3 fragments `size=2`, `size=20`,
  `size=1` all have `touched=1 judged=1 cull=1`. Sum = 23 faces = the log's
  `removed 3 islands, 23 faces`. (Their pre-drop components are the big island and a 3670-face
  island — e.g. one dropped face split a 20-face fragment off the 3670-face component.)
- **Touched-but-oversized components are judged and kept:** `225975`, `3650`, `945`, `2807` all
  `touched=1 judged=1 cull=0` (size > cap). Correct.
- **Untouched components are NEVER re-judged:** every `touched=0` island has `judged=0 cull=0`.
  Crucially the three marginal islands the cleanup shrank below the cap — **398, 362, 356 faces** —
  are untouched → kept. Also 447/436/426/532/571/976/3122 etc. kept.

Cross-check on the barrier-era base (`9f7477a`, my `r9f_fixb_diag_roof`): identical behaviour —
fragments 2/20/1 culled, marginal 398/362/356 kept, `removed 3 islands, 23 faces; kept=15`.

Nuance on wording: the code does not literally test "did the pass split this island"; it tests
"did this island's **pre-drop component** lose faces" (split OR shrunk). Both cases are handled
correctly and the property that matters (untouched ⇒ never re-judged) holds.

---

## Claim 3 — ACCEPTANCE re-run

**VERDICT: CONFIRMED** (with one numeric nuance on the staircase baseline).

CC_20 roof window (my fresh runs, `roofobj.py`):

| build | faces | exact straddlers | clearance<0 | min | gate |
|---|---|---|---|---|---|
| `9f7477a`+fixB (my `r9f_fixb_roof`) | 109 | 7 | **0** | **+0.067** | PASS |
| `386a1fb`+fixB (my `r386_fixb_roof`) | 109 | 7 | **0** | **+0.067** | PASS |
| V3.48-equiv (`r386_ablate_roof`) | 109 | 7 | 0 | +0.067 | PASS |
| pristine V3.50 (`r386_base_roof`) | 116 | 11 | 8 | −2.960 | PASS |

Both fixB roof dumps are **byte-identical** to the prior session's artifacts.

CC_20 staircase (census spot `1120.50,1077.00` r=5, `--ramps on`):

| build | steep(>55°) | steep area | vertical | worst slope | islands | gate |
|---|---|---|---|---|---|---|
| V3.48-equiv (my `r386_ablate_stairs`) | **11** | 39.108 m² | 0 | 87.39° | 15 | PASS |
| pristine V3.50 (my `r386_base_stairs`) | **7** | 14.320 m² | 0 | 72.08° | 15 | PASS |
| `386a1fb`+fixB (my `r386_fixb_stairs`) | **6** | 14.967 m² | **0** | 81.57° | **15** | PASS |
| `9f7477a`+fixB (my `r9f_fixb_stairs`) | **6** | 14.967 m² | **0** | 81.57° | **15** | PASS |

Invariants lines (both bases, roof and stairs): `inverted=0 vertical=0 badQuadWalks=0 … -> PASS`;
prune1 `kept=15`, second prune `removed 3 islands, 23 faces; kept=15 islands`.

Nuance: the claim "steep 11 → 6" is true as the **V3.48→fixB** change; the pristine V3.50 baseline is
**7**, not 6 (so V3.50 itself was 11→7, and fixB is one face better than V3.50). "No vertical faces"
is confirmed. The 81.57° vs V3.50's 72.08° residual is real and matches the report's honest note.
My ablation staircase census (11 / 39.108 / 87.39, face `f133025`) is identical to the **actual V3.48
commit's** staircase census (`work\67455a2_stairs\run.txt`), so the V3.48 baseline is genuine.

---

## Claim 4 — the two verification bugs are real and fixed

**VERDICT: CONFIRMED** (both reproduced as failures).

**(a) Fragment gating.** Removing the `islandTouched.Count == 0 ||` fallback (`patches\lc_bug_a`,
stacked after fixB) makes the **first** prune (no pre-drop reference ⇒ empty `islandTouched`) judge
nothing: my `r386_buga_roof` log shows
`v2: prune: removed 0 islands, 0 faces …; kept=3865 islands`, final `islands=3851`, faces=290029
(unpruned). The shipped patch has the fallback, and my verified runs show prune1
`removed 3850 islands, 52862 faces; kept=15`. Real and fixed.

**(b) Raw union-find parent normalization.** Removing the `FaceFind` normalization
(`patches\lc_bug_b`) breaks the second prune. My `r386_bugb_roof` shows
`v2: prune (post steep): removed 0 islands, 0 faces …; kept=18 islands` — the 3 orphan fragments
ship (`islands=18`, faces=241495 vs the correct 241472). Root cause measured directly by my
`lc_diag` capture line:
`LCDIAG raw-capture faces=241509 distinctRawLabels=111640` vs
`LCDIAG normalized-capture … distinctComponents=15 facesWithRawLabelNotRoot=241450`.
So **241,450 of 241,509 faces** carry a raw union-find parent that is not their component root
(111,640 distinct raw values vs 15 true components) — using raw labels as component ids cannot
match a face to its component. Same on `9f7477a` (241444/241504 mismatched). Real and fixed.

---

## Claim 5 — deployed DLLs at 06:10 are `9f7477a` + fixB

**VERDICT: CONFIRMED (byte-exact).**

- Module DLLs at `D:\…\Modules\BetterBannerlordNavmeshing\bin\{Win64_Shipping_wEditor,
  Win64_Shipping_Client}\BetterBannerlordNavmeshing.dll` are timestamped **2026-10-07 06:10:01–02**,
  383488 bytes each, both variants.
- Both contain fixB-specific metadata and UTF-16 string literals: `RemapQuadPairsAfterCompaction`,
  `CompactPerFace`, `preDropSizes`, `QuadPairsKept`, `"v2: prune (post steep): removed "`,
  `"split or reduced islands only"`, and the steep-pass suffix
  `"ms (followed by the island prune, which judges any fragment it leaves by the island cap)"`.
  A pre-fixB repo DLL contains **none** of these.
- I wrote a portable-PDB reader (`lc_pdbtool`, `System.Reflection.Metadata`) and read the deployed
  PDBs' document table. Both variants list
  `C:\Users\Sheepbitter\AppData\Local\Temp\opencode\bbnm_v361_fixB\TiledNavMeshBuilder.cs` with
  document hash **`b9c00bc03d9af8a73b927ab686712b1f0a54063daa66f6a005bd6f2d4ddbf798`**.
- That hash **exactly equals the SHA-256 of my independently fixB-patched `9f7477a` source**
  (`lc_val\9f7477a\src\TiledNavMeshBuilder.cs`); pristine `9f7477a` hashes to
  `62c59ca7…` (differs), and `386a1fb`+fixB to `f11784ca…` (differs).
- The build export directory `…\opencode\bbnm_v361_fixB\` still exists. I extracted the **full**
  `9f7477a` tree and hashed every file: **106 files identical, exactly 1 differing**
  (`TiledNavMeshBuilder.cs`, = my fixB-patched source), and **no extra files** outside `bin/obj`.
  Its own `bin\Release\net472\BetterBannerlordNavmeshing.dll` is byte-identical to the deployed
  **Client** DLL.

So the deployed build is `9f7477a` + the fixB patch on `TiledNavMeshBuilder.cs`, compiled from the
`bbnm_v361_fixB` export, both variants. (Could not do a byte-for-byte DLL *rebuild* — .NET builds
are not byte-reproducible — but the PDB source hash is byte-exact and stronger for identity.)

---

## Summary

| claim | verdict |
|---|---|
| 1 mechanism (cleanup-born under-roof faces; ablation = V3.48) | **CONFIRMED** |
| 2 patch (quad-aware; second prune touched-only) | **CONFIRMED** (wording nuance: "touched", not literally "split") |
| 3 acceptance (roof 0 / min +0.067; stairs 11→6; badQuadWalks 0) | **CONFIRMED** (V3.50 stairs baseline is 7, not 6) |
| 4 two verification bugs real and fixed | **CONFIRMED** (both reproduced) |
| 5 deployed DLLs = 9f7477a + fixB | **CONFIRMED (byte-exact via PDB source hash)** |

### Extra (not required by claim 3): Birtu / Basamu non-regression

Birtu (`--probe`, both bases, my runs):

| build | islands | faces | ladder probes | gate |
|---|---|---|---|---|
| `9f7477a`+fixB (my `r9f_fixb_birtu`) | **5** | 44496 | all 4 on island #1 (30387 faces) | PASS badQuadWalks=0 |
| `386a1fb`+fixB (my `r386_fixb_birtu`) | **5** | 44499 | all 4 on island #1 | PASS badQuadWalks=0 |

Basamu: see `val_runs4.log` / `accept_val\r*_fixb_basamu.run.txt` (appended when the run completes).

## What I could not validate / caveats

- **Birtu/Basamu** (`islands 5 / 1`, face deltas) were not required by claim 3 and are only being
  re-run for completeness (see `val_runs4.log`); results appended if present.
- **Byte-for-byte DLL rebuild** was not attempted (builds are not byte-reproducible); identity rests
  on the PDB document hash + export dir + marker strings.
- The **absolute** roof geometry model (`house_roof_model.json`) is taken as given; I did cross-check
  that the same metric separates the user's own editor-verified good/bad bins, which is the
  decision-relevant property.
- The counterfactual `badQuadWalks` magnitude (1225) differs slightly from the prior report's 1208
  because my counterfactual keeps the second prune's remap; the qualitative conclusion is identical.

## Key artifacts (mine)

- `lc_val\{386a1fb,9f7477a}\` — fresh pristine copies + fixB-patched sources
- `patches\lc_diag\`, `patches\lc_bug_a\`, `patches\lc_bug_b\`, `patches\lc_noquad\` — my diagnostics
- `cls\lc_bincheck.py` — editor-bin classifier (new); `lc_pdbtool\` — portable-PDB reader (new)
- `accept_val\` — my run outputs; `val_runs{,2,3,4}.ps1` + logs
