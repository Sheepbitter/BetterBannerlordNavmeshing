# CC_20 Castle-Gate Navmesh Projection — Implementation-Ready Plan (v2)

> **SUPERSEDED (2026-10-08):** the marking / ledge-exemption approach in this plan cannot work on the
> current scene data - there is no geometry to mark at the defect (verified: C's column has no horizontal
> soup triangle; the plan's ramp premise is absent from the data, and its baselines predate the
> determinism + 2.0-vox changes). The approach was ripped out. The fix that shipped is the synthesized
> bridge strip: see `GateConnectorReport.md`.
Repo: `C:\Users\Sheepbitter\source\repos\BetterBannerlordNavmeshing` (read-only; HEAD `9f7477a` "V3.61").
All experiments were run in `C:\Users\Sheepbitter\AppData\Local\Temp\opencode\gate_plan\` against a **copy** of `tools\TiledCoreHarness` (built OK, never built in the shared dir).
Fix constraint honoured: **no scene-wide filter changes** — the fix is local to the gate projection's own boundary.

> **v2 changes (from the independent validation, `gate-plan-validation.md`).** Everything else is unchanged from v1.
> 1. **Part 2 is largely redundant** — `RestoreSteppedSpans` pass 2/3 already implements the orphan walk-back (`droppedOrphan`, `ColumnsLink`). §2 now proposes **Part 1 only** as the primary fix; the walk-back is demoted to optional verification.
> 2. **Checklist false-pass** — upper castle floors at y≈92.30/97.10 overhang the gate; the walk's "lowest covering face" falls through to them and still prints WALKABLE. §4 now requires asserting the picked face's **elevation is at gate level (~85.x)**, with the two observed false-pass runs as evidence.
> 3. **Prototype expectations corrected** — "islands ≤ 15" and "plate still blended / y range unchanged" are **not guaranteed**; both are now risks to be measured, with the observed numbers (islands: ledge-off 16; blend counts 27–77).
> 4. **Attribution (a)/(b)** is **inferred, not measured** — the threshold lies between climb 0.8 and 1.0 m; §1.3 marks it as such and adds the experiment that would settle it.
> 5. Typo swept: `MarkSteppedCells` everywhere (v1 already used the correct name; kept consistent).
> 6. Gap-fill nuance: the "flanks 1.05–1.3 m > climb" is the **gap-fill-time** state (pre-blend); on the final mesh at z=990.25 the flank dy is 0.51 m, so the **cap** is the binding constraint there. Both stated.
> 7. Fresh validation numbers folded in (void band **1.075 m**; the `conn.py` component-root check; the fresh walk runs).

---

## 1. Why the machinery refuses / cannot bridge the gate crossing (pinned to code)

### 1.0 Scene constants (from run log, every run)
```
cs=0.25m ch=0.2m tile=16m cellsPerTile=64 tiles=125x125 border=4 hf=72x72
params slope=50deg radius=0.25m height=1.8m climb=0.6m walkable(r,h,c)=1/9/3
edgeLen=12vox gapFill=2 stitch=6 prune=400
```
walkableClimb = 3 vx = 0.6 m. Erosion radius = ceil(0.25/0.25) = **1 cell per side**.

### 1.1 The local geometry (measured, `gate_area.obj` = 97 verts / 99 faces, `--defect 1130.5,990.5,8 --dumpnear`)
*(Re-derived independently in v2 from a fresh dump `val_gate_area.obj`; all numbers below reproduce exactly.)*
- The plate (8 faces, slope 13.0°, y=[85.329..86.576], bbox x[1127.75..1133.0] z[989..992]) keeps its full collider footprint.
- **C = (1133.53, 991.00) has NO covering face — `[VOID]`** (`near.py`/`conn.py` coverage: `C (1133.53, 991.0) []`). It sits in a wedge between the plate's east edge and the ground's north boundary.
- The plate's east tip B = (1133.0, 991.5, 85.329) is a **degree-1 vertex** (fresh check: used by exactly 1 face, f112420); its two boundary edges' nearest non-plate boundary edge is ground f23's west edge (1133.000,990.125)→(1133.750,991.250):
  `gap=0.763 dy=0.129` (fresh re-derivation, exact match). **dy is 0.129–0.229 m — far inside the 0.6 m climb. The failure is pure footprint distance, not height.**
- With `--ledge off` the same box has **186 faces** and C is covered by the gate's **own** ramp collider f32 (35.8°, y=[85.100..85.900], x[1132.75..1133.75] z[989.25..991.25]); the chain plate(f30/f31/f39/f41, 11.9°) → f29(9.7°) → f32(35.8°) → f33/f34(22–23°) → ground exists.
- The gate is **not isolated**: `--walk "1140.000,990.000;1132.000,990.250;1133.000,991.500;1120.000,990.000;1133.750,991.250"` → flood=3792, all five targets WALKABLE (plate + ground east + castle interior in one component).
- The gate prop's own collider stack (soup dump t885xxx): horizontal quads at soup y = 84.75 / 84.97 / 85.04 / 85.11 / 85.31 / 85.59 plus vertical (90°) door-frame quads. The plate is the projection of the topmost (85.8 = one ch span top, +0.5 lift = 86.30 at build time). *(v2 fresh re-dump `val_soup_gate.txt` x[1132.2..1134.6] z[989.8..991.6] shows horizontal levels 84.97/85.11/85.29/85.35 + verticals spanning 84.75–85.59 — the same stack.)*

### 1.2 Tile-level pipeline order (TiledNavMeshBuilder.cs `BuildTile`, in execution order)
| # | Lines | Stage | What it does to the gate |
|---|-------|-------|--------------------------|
| 1 | 3964–4019 | `MarkSteppedCells` + snapshot `steppedSpans` | **Nothing** — only HasSteps/Ladder props are marked today (`SceneCollisionGeometry.cs:424` `ledgeExempt.Add(isSteppedProp)`) |
| 2 | 4020–4034 | `FillNarrowGaps` (cap 2 cells, flanks \|dy\| ≤ climb, *before* the ledge filter by design, see comment 4022/4155) | Refused. At **gap-fill time (pre-blend)** the plate is flat 86.30 vs terrain 85.0–85.5 → flanks span 0.8–1.3 m > 0.6 m climb, **and** cap 2 < the 3–4 cell void. *(v2 nuance: on the **final** mesh at z=990.25 the flank dy is only **0.51 m** — inside climb — so there the **cap** is the binding constraint; both constraints fail at gap-fill time.)* |
| 3 | 4035–4057 | `RcFilters.FilterLedgeSpans` | **The eater.** See 1.3 |
| 4 | 4058–4089 | `RestoreSteppedSpans` | Nothing to restore for the gate today |
| 5 | 4101 | `FilterWalkableLowHeightSpans` (clearance) | Kills terrain spans under the gate's colliders (clearance 0.18–0.36 m < 1.8 m) |
| 6 | 4103–4104 | compact + `ErodeWalkableArea` (radius 1, `minBoundaryDistance = erosionRadius*2` RcAreas.cs:245) | Nulls boundary cells — both sides of any step > climb: the plate's rim (non-connected to terrain) and the cells adjacent to the nulled spans → void widens to 3–4 cells |
| 7 | 4134–4142 | `StitchTileComponents` (cap 6 cells = 1.5 m) | Refused — see 1.4 |

### 1.3 The exact ledge predicate that eats the gate (RcFilters.cs:105–212)
A walkable span is nulled when:
- **Rule (a)** (line 199): `lowestNeighborFloorDifference < -walkableClimb` — the lowest traversable-overlap neighbour floor is more than 3 vx below. → **nulls the plate's rim** (drop to terrain ≈ 86.30−85.0 = 1.3 m = 6.5 vx) and the ramps' outer rims.
- **Rule (b)** (line 204): `(highestTraversableNeighborFloor - lowestTraversableNeighborFloor) > walkableClimb` — the traversable neighbours' floors span more than 3 vx. → **nulls the gate's own intermediate colliders** (the doorway ramps / lower floors): their traversable flanks are the plate above (+0.49 m) and the terrain below (−0.56 m) → span 1.05 m > 0.6 m. **This is the rule that eats the bridge.**
- Out-of-bounds early-out (144–148) is harmless here: the tile heightfield is padded (border=4 → 1 m), so the plate's spans at its own north edge z=992 (tile 61/62 boundary) see real neighbours.

> **v2 — attribution is INFERRED, not measured.** The **outcome** is confirmed (the climb-based rules are the eater: `--climb 1.2` connects all six). The **per-cell split** above (rule (a) → plate rim, rule (b) → intermediate colliders) is a geometric inference. The threshold is bracketed by two fresh runs: `--climb 0.8` leaves the gate-level void (C is covered only by the **y=92.30** upper floor) while `--climb 1.0` gives C at gate level (**y=85.37**) → the binding traversable span lies between 0.8 and 1.0 m, consistent with the plan's ~1.05 m figure but not equal to it. **Experiment that would settle it:** copy `FilterLedgeSpans` into the harness with a trace hook and, for the gate columns only, log each span's `lowestNeighborFloorDifference` and `highestTraversableNeighborFloor - lowestTraversableNeighborFloor` immediately before the nulling decision. Expected: the plate-rim spans show min-diff ≈ −6.5 vx (rule a); the gate's intermediate colliders show a traversable span ≈ 5 vx (rule b). Until that is run, treat the (a)/(b) split as a hypothesis.

### 1.4 Why neither stitch bridges it
`StitchTileComponents` (4330, header 4324–4329) *does* stitch intra-tile components ("Reconnects components of the tile's own mesh whose nearest boundary edges are within stitchMaxGapCells cells and within walkableClimb vertically") — but:
1. **Same-component skip** (4358–4361 early-out; pairing loop only over distinct components): the plate and the ground east are edge-connected around the south via the doorway's south ramp (plate f112430's south edge (1132.25,989.0)→(1131.25,989.0) is *shared* with f112421) → no candidate pair.
2. **Vertical check** (4470–4472 `dyAB > walkableClimb*ch`): at tile level the stitch runs BEFORE the merge-level plate blend — the plate is still flat at 86.30, so dy to the ground is 0.8–1.3 m > 0.6 m climb → refused.
3. **Envelope refusal** (`StitchGapClear` 4476): the gate's own colliders (vertical door-frame quads at x≈1133.3) put geometry inside the gap envelope.

`MergeStitch` (2302, merge level, after the blend) — three independent reasons:
1. **Same-component skip** (line 2382 `StitchFind(compParent,i)==StitchFind(compParent,j)`). *(v2: confirmed **directly**, not just via the walk flood — the fresh `conn.py` edge-adjacency check welds by the OBJ original vertex id and finds the 8 plate faces (f112419/20/22/23/29/30/31/32, 13.0°) and ground-east f112426 (5.5°) sharing **component root 95** in the r=8 dump.)*
2. **Per-component cap** (2375/2380 `ei == 0 || ei > 4096`) and the **nc > 4096 early-out** (2344): the giant island (~225k faces) never participates. *(The "~49.5k boundary edges" figure is unverified — the cap logic is confirmed, the count is not.)*
3. Even if considered, the soup-grid clearance probe (`GapEnvelopeSolid`) would refuse — the gate's own colliders span the envelope.

### 1.5 Why the plate blend cannot save it
`BlendGatePlates` (2597, called at merge level 1304–1308) runs **after** all tiles: it rotates the plate's own vertices onto the fitted plane (dy at the crossing drops 1.27 m → 0.23–0.30 m) but by construction "emits no face and removes none, no edge is touched" (comment 1299–1302). The 3–4 cell void was already carved while the plate was 1.27 m proud. **This ordering is the architectural crux.**

### 1.6 Experimental validation of 1.3–1.5 (fresh v2 runs)
| Diagnostic | Command | Result |
|---|---|---|
| base (climb 0.6, ledge on) | `--walk "<six points>"` | `val_base_walk.txt`: flood=**1815**; **C → `NO MESH AT ALL [VOID]`**; merge-stitch pairs=808 added=4 refused=806; islands=**15**; gate plates examined=226 blended=54; gap fill 468 |
| ledge off | `--ledge off --walk` (same 6) | `val_noledge_walk.txt`: flood=**2672**; **all 6 WALKABLE**; C → f113535 **y=85.43**; islands=**16**; gate plates blended=**77**; gap fill 468 |
| climb 1.2 (ledge ON) | `--climb 1.2 --walk` (same 6) | `val_climb12_walk.txt`: flood=**2528**; **all 6 WALKABLE**; C → f113374 **y=85.57** = the gate's own lower-floor collider; gap fill **11103**; islands=**14**; gate plates blended=**27** |
| climb 0.8 / climb 1.0 | `--climb 0.8` / `--climb 1.0 --walk` (same 6) | `val_climb08_walk.txt`: C → **y=92.30** (upper floor — gate-level void persists). `val_climb10_walk.txt`: C → **y=85.37** (gate level). Brackets the threshold at 0.8–1.0 m |
| OBJ geometry | `--defect 1130.5,990.5,8 --dumpnear` | `val_gate_area.obj` 97 v/99 f, `val_gate_noledge.obj` 185 v/186 f; `band.py` void at z=990.25 = **1.075 m** (base) → **~0.5 m** (ledge off); `conn.py` plate + ground-east = **root 95** |

Erosion still ran in the ledge-off run (`UseLedgeFilter` gates only the snapshot 3996 and `FilterLedgeSpans` 4035; `ErodeWalkableArea` 4104 is unconditional) → **erosion alone does not eat the bridge; the trim is the only eater.**

---

## 2. The fix design (user's direction, verbatim intent)

> "the gate navmesh projection should run its OWN local ledge/connectivity check and walk itself back until it can connect normally — i.e. resolve the projection's boundary locally at the point where it links (footprint touch + dy within climb), instead of leaving proud edges for the global ledge trim to eat."

The infrastructure **already exists and is proven** — the fix reuses it, it does not add a filter branch:
- **Ledge-trim exemption**: `ledgeExemptTriangles` (per-input-triangle, `Build` param 768) → per-tile binning (3890–3910) → `MarkSteppedCells` (3409) rasterises the marked triangles' cells → snapshot of the marked cells' walkable spans **before** `FilterLedgeSpans` (4005–4015) → `RestoreSteppedSpans` (4065) **after**, with soundness checks (`droppedClearance / droppedOrphan / droppedPlane / aboveNear / aboveFar`).
- **Erosion exemption pattern**: `RestoreConnectorErosion` (4109, area-only restore after erosion).
- **Clearance exemption pattern**: `ClearConnectorCeilings` (4097).
- Today the exemption array is fed **only** by `SceneCollisionGeometry.cs:424` (`ledgeExempt.Add(isSteppedProp)` for HasSteps/Ladder props). The gate plate is not detected. That single line is the hook.

### 2.1 Primary proposal — **Part 1 only** (mark the plate + same-prop ring-2 m approach triangles)

> **v2 — this is now the primary fix.** The original v1 "Part 2" (a new per-cell walk-back loop) is **largely redundant**: `RestoreSteppedSpans` already implements it. See 2.2.

**Part 1 — the projection marks its own collider triangles (caller side, `SceneCollisionGeometry.cs` ~371/424).**
Per candidate prop, detect the gate plate's own collider triangles with **BlendGatePlates' own predicate** evaluated on the prop's collider triangles (so the two passes agree on what a plate is): dead-flat ≤ `flatDeg=2°`, up-facing, one ch level thick, 3..120 tris, 1..120 m², ≤8 m a side. The gate's floor slab (5.25×3 m, 8 faces) qualifies trivially. Then mark as ledge-exempt:
- the plate's triangles, **and**
- the **same prop's** other collider triangles within `ring=2 m` of the plate's footprint (the gate's own approach: the doorway ramps + intermediate floors). Same-prop attribution (the candidate's own triangle set), not a spatial scene ring.
Vertical door-frame quads (90°) are harmless to mark — they are never walkable, so nothing is snapshotted for them.

The surviving marked cells then flow through the **existing** snapshot → `RestoreSteppedSpans` path, which already applies the local connectivity gate.

**Why this is exactly the user's direction:** the projection's boundary is resolved by the projection's own colliders (its own spans), not by the global trim. Where the boundary links (dy ≤ climb) the exemption keeps it — the trim no longer eats it. Where it cannot link, `RestoreSteppedSpans` drops it (the existing orphan gate) — i.e. the projection walks itself back, without new code. The global ledge trim, gap fill, erosion, merge and blend all keep today's caps — **no scene-wide filter change**.

**Why it fixes CC_20's crossing:** the `--ledge off` baseline proves that when the trim does not eat the gate's own colliders, they survive erosion (they are connected to both sides within climb: plate +0.49 m, terrain −0.56 m) and bridge the plate to the ground — C covered, all six points walkable. The exemption replicates precisely that for the gate's own cells; the existing orphan gate keeps proud edges (a plate rim above a >climb drop with no approach collider) under the trim, which is what protects cliffs/rooftops.

### 2.2 Secondary / optional — the local link check (v1 "Part 2"), now demoted

`RestoreSteppedSpans` **already** implements the walk-back the v1 plan proposed:
- pass 1 (`:3545`) — clearance gate (`droppedClearance`);
- pass 2 (`:3605–3645`) — a 4-neighbour flood seeded from columns that were walkable **before** the pass (plus the tile border), stepping via `ColumnsLink(hf, ci, nci, walkableClimb)` — i.e. **"4-neighbour walkable top span with |dy| ≤ walkableClimb"**, the same rule v1 proposed to add;
- pass 3 (`:3647–3660`) — restored spans whose column was not reached are dropped as **`droppedOrphan`** ("A patch that would only connect to itself is left nulled rather than becoming an orphan island").

So the v1 Part 2 loop is **largely redundant**. It is retained here only as **optional verification/secondary** — a pre-marking filter that would shrink the candidate set before the snapshot. The prototype does **not** implement it (see §3). Only if the suite shows `droppedOrphan` is failing to protect a cliff/rooftop case should Part 2 be revisited.

---

## 3. Smallest prototype experiment (harness copy only)

**The experiment: Part 1 only — no walk-back loop, no tile-level changes.**
1. In `gate_plan\harness\Program.cs` (the copy), where the soup is assembled, add a `--gateplate on` flag that, per candidate prop, runs the plate predicate on the prop's collider triangles and marks plate + same-prop ring-2 m triangles as ledge-exempt (append to the existing `ledgeExemptTriangles` bool array).
2. Run (six-point spec; A/B/D at exact vmesh coordinates to avoid the seed's "no face at" flakiness):
```
TiledCoreHarness.exe --snapshot <terrain> <assets> <txt> --gateplate on --walk ^
  "1132.000,990.250;1133.000,991.500;1133.530,991.000;1132.250,989.000;1133.000,990.125;1133.750,991.250"
```
3. **Expected outcomes** (anchored on the `--ledge off` / `--climb 1.2` baselines), **with the v2 corrections**:
   - C (1133.530, 991.000) **covered at gate level** — by the gate's own ramp/lower-floor collider — and all six targets WALKABLE in one flood component. **Assert the picked face's elevation ≈ 85.x** (see §4 — not merely WALKABLE; the y=92.30 upper floor can false-pass).
   - The void band at z=990.25: **1.075 m → ~0.5 m**.
   - Gate-area face count rises (99 → ~186 in the r=8 OBJ, per the ledge-off baseline).
   - **RISK to measure — islands are NOT guaranteed ≤ 15.** Observed: base **15**, climb-1.2 **14**, but **ledge-off 16** (the trim's removal is not monotonically island-reducing). Record the prototype's island count and compare against base; a local exemption should be far milder than full ledge-off, but do not assume.
   - **RISK to measure — "plate still blended / y range unchanged" is NOT guaranteed.** Observed blend counts swing with config: base **54**, ledge-off **77**, climb-1.2 **27**, climb-0.8 **68**, climb-1.0 **38** — because the exemption changes the merged mesh that `BlendGatePlates` fits. Record `--platetrace` plate#9's bbox/tilt/y-range for the prototype; assert it is *still blended and still a plate*, not that the numbers are identical.
   - Check `SteppedRestoredSpans / SteppedDroppedClearance / SteppedDroppedOrphan / SteppedDroppedPlane` for the gate tile: restores for the doorway cells must survive; `droppedClearance` drops inside the plate's own footprint are expected and harmless (the plate covers those columns); `droppedOrphan` must not drop the doorway chain.

**No Step 2** (the v1 walk-back) — see §2.2. If the suite exposes a proud-edge case the existing orphan gate misses, revisit then.

**Baseline commands for A/B (fresh v2 captures):**
- `val_base_walk.txt` (ledge on): flood=1815, C `NO MESH AT ALL [VOID]`, islands=15.
- `val_noledge_walk.txt` (ledge off): flood=2672, all 6 WALKABLE, C y=85.43, islands=16.
- `val_climb12_walk.txt` (climb 1.2): flood=2528, all 6 WALKABLE, C y=85.57, gap fill 11103, islands=14.

---

## 4. Risks and validation checklist

### Risks
1. **Restore soundness may drop gate restores.** `RestoreSteppedSpans` was never exercised for the gate (today nothing is marked). `droppedClearance` will drop ramp/floor spans inside the plate's own footprint (harmless — the plate covers those columns) but must NOT drop the doorway cells outside it. → The prototype must print the per-tile restore counts and the walk result.
2. **Same-prop attribution.** Ring-2 m spatial marking could catch a *neighbouring* prop's colliders (the castle wall). Must attribute by the candidate's own triangle set, not a scene ring. (If the gate's approach colliders belong to a different prop in some scenes, fall back to connected-collider-group attribution within the ring.)
3. **Proud-edge protection for cliffs/rooftops.** Exempting a plate rim above a >climb drop with no approach collider would keep a proud edge — this is exactly the failure that made the user revert `GrowPlateFootprint`/`ClosePlateSeams` (stray unpruned islands on cliff props and rooftops, README 1374–1486). The guard is now the **existing `RestoreSteppedSpans` orphan gate** (v2 — not a new walk-back); the prototype must still be run on the suite.
4. **Erosion is not exempted by Part 1.** Acceptable for CC_20 (the baselines prove the bridge survives erosion), but a plate rim above open ground is still eaten by erosion — functionally the walk-back, decided by erosion rather than by the projection. Note it in the code comment.
5. **Run-to-run nondeterminism.** The final decimate stage is time-budgeted; face/vertex IDs and totals drift (F = 241394 / 240977 / 241399; gate-area OBJ 99 vs 186 faces). Assertions must tolerate this; use walk/coverage predicates, not exact counts.
6. **Face-count regression risk.** Exempting the gate's boundary adds faces locally (ledge-off gate area +87 faces). The acceptance criterion is island counts and suite numbers, not raw face counts — but record both.
7. **(v2) Island count is not guaranteed non-regressing.** Full ledge-off gives **16** islands vs base 15. A local exemption should be far milder, but the prototype's island count must be measured, not assumed.
8. **(v2) Plate-blend state may change.** Blend counts swing 27–77 across configs; the exemption changes the fit input. Measure `--platetrace` rather than asserting the v1 "unchanged" numbers.
9. **(v2) The walk can FALSE-PASS.** Upper castle floors at **y≈92.30 and y≈97.10** overhang the gate. When the gate-level face is missing, the walk's "lowest covering face" pick falls through to y=92.30 and still prints **WALKABLE**. Observed in the fresh `--climb 0.8` run (C → f112876 y=92.30) and the `--climb 1.0` run (B → f113077 y=92.30). **Every walk check must assert the picked face's elevation is at gate level (~85.x), not merely the WALKABLE verdict.**

### Validation checklist
- [ ] `--walk` six-point spec: all six WALKABLE in **one** flood component **AND each picked face's elevation is gate level (~85.x)** — specifically C at ~85.4–85.6, not 92.30/97.10 (the v2 false-pass guard).
- [ ] Gate-area OBJ (`--defect 1130.5,990.5,8 --dumpnear`): C covered at gate level; the plate→f29→f32→ground chain present (the ledge-off shape); `band.py` void at z=990.25 shrinks toward ~0.5 m.
- [ ] `conn.py`: the plate faces and the ground-east face remain in one edge-connected component (root 95 in the base dump).
- [ ] `--platetrace`: plate#9 **still blended and still a plate**; record bbox/tilt/y-range (do **not** assert the v1 "unchanged" numbers — see risk 8).
- [ ] CC_20: record `islands` (base 15; do **not** assume ≤15 — ledge-off is 16); invariants PASS; `merge-stitch pairs/added/refused` reported.
- [ ] Birtu/Basamu suite runs held; compare against `editor_artifacts\CC_20_siege__fresh348.bin` (good) and `fresh350_bad.bin`.
- [ ] Cliff/rooftop props: no stray islands (the user's revert rationale); confirm `droppedOrphan` is doing the walk-back.
- [ ] `--ledge off` / `--climb 1.2` diagnostics unchanged (the fix must not perturb them).
- [ ] Per-tile restore counts for the gate tile (`SteppedRestoredSpans > 0`; doorway cells not dropped; `SteppedDroppedOrphan` does not remove the doorway chain).

---

## 5. Open questions
1. **Where the plate predicate runs**: caller-side on the prop's collider triangles (recommended — reuses the `SceneCollisionGeometry.cs:424` hook and matches `MarkSteppedCells`' input model) vs. a new per-tile heightfield pass. Caller-side needs the predicate re-implemented on soup triangles; confirm it agrees with `BlendGatePlates` on all plates.
2. **Same-prop attribution edge case**: if a scene's gate approach colliders are separate props, is connected-collider-group-within-ring-2 m the right fallback?
3. **Does Part 1 alone suffice?** *(v2: this is now the primary question, and the answer is expected to be yes.)* CC_20 evidence says Part 1 suffices; the suite may say otherwise. Related: is the erosion exemption (Part 1 + `RestoreConnectorErosion` pattern) needed at all?
4. **(v2) Should the (a)/(b) split be settled by instrumentation?** The threshold is bracketed at climb 0.8–1.0 m; the trace-hook experiment in §1.3 would attribute each nulled gate span to rule (a) or (b). Not load-bearing for the fix (the exemption covers both), but it would make the mechanism claim exact.
5. **(v2, optional) Walk-back loop** — only if the suite shows the existing `droppedOrphan` gate fails to protect a proud-edge case. Bound would be 3 iterations / 8 cells; tune on the suite's cliff/rooftop props.
6. **Should the exemption also cover the clearance rules** (`ClearConnectorCeilings` pattern)? C is covered in the climb-1.2 run, so probably not — verify in the prototype.
7. **In-game confirmation**: the mesh-side fix should be confirmed by an in-game pathing check through the gate (the south detour exists today, so pathability may already partially work; the defect is the hole at C and the broken direct crossing).

---

## Appendix — captured artifacts (all in `gate_plan\`)
| File | What |
|---|---|
| `harness\` | Copy of `tools\TiledCoreHarness`, rebuilt v2 against the repo sources by absolute path (MSBuild 2022 Community) |
| `val_gate_area.obj` | **v2 fresh** ledge-on r=8 dump: 97 v/99 f; C uncovered; B degree-1 (1 face); gap 0.763 / dy 0.129 |
| `val_gate_noledge.obj` | **v2 fresh** ledge-off r=8 dump: 185 v/186 f; C covered by the gate's ramp (35.8°) |
| `val_base_walk.txt` | **v2 fresh** ledge-on walk: flood=1815; C → `NO MESH AT ALL [VOID]`; islands=15; gap fill 468 |
| `val_noledge_walk.txt` | **v2 fresh** ledge-off walk: flood=2672; all 6 WALKABLE; C → f113535 y=85.43; islands=16 |
| `val_climb12_walk.txt` | **v2 fresh** climb=1.2 walk: flood=2528; all 6 WALKABLE; C → f113374 y=85.57; gap fill 11103; islands=14 |
| `val_climb08_walk.txt` / `val_climb10_walk.txt` | **v2 fresh** threshold bracket: climb 0.8 C → y=92.30 (void persists); climb 1.0 C → y=85.37 |
| `val_soup_gate.txt` | **v2 fresh** soup dump x[1132.2..1134.6] z[989.8..991.6]: the gate collider stack (84.97/85.11/85.29/85.35 + verticals 84.75–85.59) |
| `conn.py` | **v2 fresh** edge-adjacency (welded by OBJ original id): plate + ground-east share component root 95 |
| `band.py` | **v2 fresh** row scan: void at z=990.25 = 1.075 m (base) → ~0.5 m (ledge off) |
| `gate-plan-validation.md` | The independent validation report these corrections come from |
| `gate_area.obj` / `gate_area_noledge.obj` | v1 dumps (97/99 and 185/186) |
| `base_walk2.txt` / `noledge_walk.txt` / `climb12_walk.txt` | v1 walks (flood 1811 / 2652 / 2503) |
| `iso_walk.txt` | Isolation check: seed (1140,990) flood=3792; plate + castle interior + ground east all WALKABLE (the gate is NOT isolated) |
| `census1.txt` | `--platetrace`: plate#9 bbox/h/tilt/proud/worst, merge-stitch pairs=808 added=4 refused=806, islands=15 |
| `soup_gate.txt` / `vmesh1.txt` | v1 soup stack; `--vmesh` user A/B/D + ground-east vertices (B ring=1) |
| `an.py` / `near.py` | OBJ analysis: plane-fit plate detection, boundary-edge pair tables, per-plate-edge nearest-ground gap/dy, point coverage |

**Key code lines**: `TiledNavMeshBuilder.cs` — tile block 3964–4142 (`MarkSteppedCells` → gap fill → ledge → restore → clearance → compact/erode → stitch), `RestoreSteppedSpans` 3519 (clearance gate 3545, orphan flood/`ColumnsLink` 3605–3645, orphan drop 3647–3660), `BlendGatePlates` 2597 (called 1304–1308), `MergeStitch` 2302 (same-component skip 2382, caps 2344/2375/2380), `FillNarrowGaps` 4166 / `TryGapFloor` 4273 (comment 4155–4165), `StitchTileComponents` 4330 (same-component 4426, vertical 4470–4472, envelope 4476), constants 5113–5141; `RcFilters.cs` — FilterLedgeSpans 105–212 (rules a/b at 199/204); `RcAreas.cs` — ErodeWalkableArea 48 (minBoundaryDistance 245); `SceneCollisionGeometry.cs` — the exemption hook 424; `VoxelNavMeshGenerator.cs` — Build call 129–132; harness `Program.cs` — `--walk` 5020 (engine link rule 5114–5116).
