# fixD — narrow `BlendGatePlates` so it cannot rotate a stepped-prop landing (keep the gate blend)

Model/provider: **LongCat 2.5 Preview Free (OpenCode Go)**. Work root
`C:\Users\Sheepbitter\AppData\Local\Temp\opencode\fixC2\`; real repo read-only; no commits/deploys.
Applied on top of fixB+fixC. (Written incrementally — see the sections as they land.)

---

## 1. What fixD does

V3.52 (`c6cb279`) added `BlendGatePlates`, which classifies a maximal group of dead-flat up-facing
faces as a "plate" and **rotates its own vertices** onto a plane fitted to the mesh around it. It
false-positives on the user's `stairs_bo` / `european_castle_ladder` landing (platform top y≈117.10),
rotating it off its level to y≈116.2 with a 62.6° edge face.

fixD adds ONE rejection to the candidate acceptance, after the plate's own faces are known and its
`h` is in scope:

> reject the plate when **HALF OR MORE of its own faces sit on stepped-prop provenance cells**
> (`results[i].SteppedRestoredCellXz`, the same set the island prune builds).

Geometry-only, no prop names. Measured on CC_20 (`9f7477a`+fixB+fixC):
- the user's landing plate (bbox 1158.5..1163.75 × 1121.75..1126.75, h=117.10, 9 faces): **5/9 stepped** → rejected;
- the CC_20 gate plate (bbox 1127.75..1133 × 989..992, h=86.30, 8 faces): **0/8 stepped** → still blended;
- scene-wide: **1 of 228** candidate plates excluded (the landing).

fixD also hoists a `steppedCellsForPlate` set above the plate-blend call (fixB already hoists its own
`steppedCells` for the prune, so a distinct name is used) and passes it into `BlendGatePlates`.

## 2. Mechanism proof — which plate/vertices move the landing (V3.51 → V3.52)

`--vmesh 1161,1124,3` (the landing) on the three builds:

| vertex (x, z) | V3.51 `90acb62` | V3.52 `c6cb279` | fixD (`9f7477a`+fixB+fixC+fixD) |
|---|---|---|---|
| (1160.500, 1122.000) | **117.100** | **115.991** (−1.109 m) | **117.100** |
| (1162.250, 1121.750) | **117.100** | **116.092** (−1.008 m) | **117.100** |
| (1162.500, 1122.500) | 117.100 | (dropped to ~116.x) | 117.100 |

V3.51 has flat 0° faces at y=117.10 (`f141804/141808/141817/141818/141819/141820/141813`); V3.52 has the
same faces at 5.7° spanning y=[115.99..116.53] (`f141822/141826/141835/141836/141837/141838`). So the
landing's **own vertices** were rotated.

**It is case (a): the landing IS itself a plate.** Evidence:
- the moved vertex (1160.500, 1122.000) lies inside the landing plate's bbox (1158.5..1163.75 ×
  1121.75..1126.75, h=117.10, 9 faces, `diagD3`), and **outside** the only other blended plate near the
  staircase — `(1154.25,1116.5)-(1157.25,1122) h=113.9 tilt 5.515°` (the orchestrator's trace, and a
  different coplanar level, so it is a separate plate);
- a neighbour-plate drag (case b) could only move *shared* vertices, but this vertex is not shared with
  that neighbour's bbox.
- `BlendGatePlates` moves "the plate's OWN vertices" — so the plate whose vertices moved is the plate
  that was blended.

**fixD leaves it alone:** the vertex is back at 117.100 (byte-equal to V3.51) and the landing's flat 0°
faces are re-emitted (`f141764/141768/141777/141778/141779/141780`), because fixD rejects the landing
plate before any fit. (The `--platetrace` cap of 4 named plates is why the landing did not appear in the
orchestrator's trace; the vertex motion and the plate bbox are the decisive evidence.)

**Invariant vs noise.** The cleanup is still wall-clock-budgeted (`CleanupMaxMs=6000`), so face indices
and exact coordinates wobble a few 1e-3 m run-to-run. The invariant, reproduced on every run:
- the **same** landing vertices — the cluster around (1160.5, 1122.0), (1162.25, 1121.75),
  (1162.5, 1122.5) — drop **~1.0–1.1 m** (sign: down) from V3.51 to V3.52, and are **back at 117.10**
  under fixD; the landing's flat 0° faces at 117.10 are present in V3.51 and fixD and absent in V3.52.

## 3. Acceptance matrix

`9f7477a`+fixB+fixC+fixD (`work\9f7477a_fixD*`), against the fixB+fixC baseline:

| check | result | verdict |
|---|---|---|
| user's staircase platform top | flat 0° faces at **y=117.10** (`f141857/141858/141862/141863/141864/141865`), census `steep=4, 1.455 m², worst 67.04°` — V3.48 behaviour restored | **PASS** |
| CC_20 gate connection (`--connect` + 5 `--pair` foot→far-side) | **5 of 5 CONNECTED** (all `c1=c2=241434`) | **PASS** |
| roof | 109 faces, 7 exact straddlers, **0 clearance<0**, min **+0.067 m** | **PASS** |
| first hub (fixC gain kept) | census `1120.50,1077.00`: steep **6**, **11.857 m²**, worst **70.53°** | **PASS** |
| gate (invariants) | **PASS, badQuadWalks=0**, inverted=0, vertical=0 | **PASS** |
| islands | CC_20 **15**, Birtu **5**, Basamu **1** | **PASS** |
| Birtu (`--probe`) | islands 5, 44485 faces, PASS | **PASS** |
| Basamu | islands **1**, PASS | **PASS** |
| blended-plate counts | V3.61+fixB+fixC `examined=229 blended=53 (308 faces)` → +fixD **`examined=228 blended=52 (299 faces)`**: exactly **1 plate / 9 faces** excluded, and the landing top is back | **PASS** |

The excluded plate is the landing (9 faces); scene-wide only 1 of 228 CC_20 candidates is majority-stepped.

**Repeatability (wall-clock-budget caveat).** `NavMeshCleanup.Run` is still budgeted (`CleanupMaxMs=6000`),
so counts can wobble. The CC_20 landing leg was run **3×** with the final patch: all three give
`steep=4 / 1.455 m² / 67.04°`, **7 flat 0° faces at y=117.10**, and `examined=228 blended=52 (299
faces) steppedLanding=1` — identical. The exclusion is a per-plate binary rule, so it is robust; on
CC_20 even the counts were stable across the three repeats. (Basamu, a much larger scene, did show the
budget noise — see §5.)

`386a1fb` (V3.50) leg — fixD is a **documented no-op** there (no `BlendGatePlates`), so it validates
fixB+fixC: gate **5/5 CONNECTED**, roof 7 straddlers / **0 clearance<0** / min **+0.067**, and the
landing is already flat at 117.10 (V3.50 has no plate blend). See §5.

## 4. The patch — `patches\fixD\patch.py` (+ `patch.ps1`)

Applied on top of fixB+fixC (`run.ps1 -Sha <sha> -Patch patches\fixD\patch.ps1`; the patch chains
fixB then fixC then its own edits, so one `-Patch` applies all three). It edits
`<workdir>\src\TiledNavMeshBuilder.cs`:

1. hoists a `steppedCellsForPlate` set above the plate-blend call (a distinct name — fixB already
   hoists its own `steppedCells` for the prune);
2. passes it into `BlendGatePlates` (new parameter);
3. after the plate's level check (where `h` is in scope) and before the neighbour fit, rejects the
   plate when **`steppedOwn * 2 >= g.Count`** (half or more of its own faces on stepped-provenance
   cells), logging it under `steppedLanding=` in the summary and `TracePlate`.

Why majority-stepped and not a ring test: measured (`diagD3`, `9f7477a`+fixB+fixC), the landing's own
faces are **5/9** stepped while the gate's are **0/8**; but the RING is the opposite (gate
`ringStepped=3` > landing `ringStepped=0`), so a ring test would reject the gate. The "own faces" test
is the precise, geometry-only discriminator.

## 5. Honest differences / limits

- **fixD is N/A on pre-V3.52 bases.** `BlendGatePlates` was added by V3.52 (`c6cb279`); `386a1fb`
  (V3.50) and `90acb62` (V3.51) have **zero** occurrences of it, so there is no plate blend to narrow
  and no landing regression there (V3.48/V3.50/V3.51 keep the landing flat at 117.10). The fixD patch
  therefore no-ops on those bases (verified: `git show <sha>:TiledNavMeshBuilder.cs` has 0 matches).
  The "both bases" matrix is thus 9f7477a (the deployed V3.61 base) plus a 386a1fb fixB+fixC leg where
  fixD is a documented no-op.
- **Basamu's face count is noisy run-to-run, and fixD excludes 0 plates there.** Measured:
  fixB+fixC (no fixD) = **492133**; +fixD (fixed patch, `steppedLanding=0`) = **492921**; +fixD (an
  earlier run) = 492854. The same patch gave 492854 and 492921 on two runs, so the spread is **~67 faces
  of run-to-run nondeterminism** (the repo has a determinism plan), and `steppedLanding=0` proves fixD
  itself declines no Basamu plate. Islands (1), gate and `badQuadWalks=0` are held throughout.
- **fixD is not a substitute for the V3.50 machinery** — it only stops the V3.52 plate blend rotating a
  stepped-prop landing; it does not touch the first hub's fan (that is fixC) or the roof (fixB).
- The `--platetrace` cap (4 named plates) means the landing plate is not in the orchestrator's trace;
  the decisive evidence is the vertex motion (117.100 → 115.991 → 117.100) and the plate bbox.
- The exclusion threshold is "majority of own faces" — tuned so the landing (5/9) is caught and the
  gate (0/8) is not; a scene with a landing split exactly 50/50 would be caught.

### Run-to-run deltas observed (wall-clock-budget noise — evidence for the pending determinism work)

The cleanup's `CleanupMaxMs=6000` budget makes the same build differ slightly run-to-run. Observed here:

| scene / build | repeat 1 | repeat 2 | repeat 3 | spread |
|---|---|---|---|---|
| CC_20 `9f7477a`+fixD, landing leg (census + plate counts) | 228/52/299, 7 flat faces, steep 4/1.455/67.04 | identical | identical | **0** |
| Basamu `9f7477a`+fixD (same patch) | 492854 faces | 492921 faces | — | **67** |
| Basamu `9f7477a`+fixB+fixC (no fixD) | 492133 faces | — | — | — |
| Basamu `9f7477a`+fixB+fixC (fixC4 earlier) | 492203 faces | — | — | — |

So on Basamu the face count moves ~67+ between repeats of the **same** build, which is why the
fixB+fixC-vs-fixD difference there (~700) is **not** attributable to fixD: `steppedLanding=0` proves
fixD declines no Basamu plate. Structural criteria (islands 1, gate PASS, badQuadWalks=0) hold in every
run. CC_20's landing leg was stable across three repeats.

## 6. Artifacts

Under `C:\Users\Sheepbitter\AppData\Local\Temp\opencode\fixC2\`:

- `patches\fixD\patch.py` / `patch.ps1` — **the fix (ships)**, chains fixB+fixC.
- `patches\diagD\patch.py` — the stepped-provenance diagnostic (`PLATEDIAG2`).
- `runargs\gate_connect.txt` (`--connect` + 5 `--pair`), `runargs\vmesh_landing.txt`,
  `runargs\census_new.txt`, `runargs\roofverbose.txt`, `runargs\val_cc20.txt`.
- `work\9f7477a_fixD*` — matrix (gate/roof/cc20/vmesh/birtu/basamu); `work\9f7477a_fixD_final{,2,3}` —
  the 3 CC_20 landing repeats; `work\9f7477a_fixBC_basamu` + `work\9f7477a_fixD_basamu{,2}` — the Basamu
  noise comparison; `work\{90acb62,c6cb279}_vmesh` — the V3.51/V3.52 landing dumps;
  `work\9f7477a_diagD3` — the provenance data.
- `cls\` — the dead session's classifiers plus `platform.py`; `REPORT_fixD.md` — this file.

## 7. Model / provider

**LongCat 2.5 Preview Free** (provider `opencode-go`). No model switch; no rate-limit or glitch hit.
