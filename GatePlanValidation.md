# Independent Validation Report — CC_20 gate projection plan

**Model/provider line:** LongCat 2.5 Preview Free (OpenCode Go).
**Independence method:** rebuilt the harness copy fresh (`MSBuild /p:Configuration=Release`, OK) against the **real repo** sources at HEAD `9f7477a` (the csproj compiles `...\BetterBannerlordNavmeshing\TiledNavMeshBuilder.cs` etc. by absolute path; only `Program.cs` is local). All numbers below are from **my own runs**, not the prior models' artifacts (the harness dir is littered with their `v3xx_*` files, which I ignored). Repo tracked sources untouched by me.

## Claim 1 — THE EATER — **CONFIRMED** (one sub-claim UNCERTAIN)

| Sub-claim | Verdict | Evidence |
|---|---|---|
| rule (a)/(b) predicates | **CONFIRMED** verbatim | `RcFilters.cs:199` `if (lowestNeighborFloorDifference < -walkableClimb) span.area = RC_NULL_AREA;` ; `:204` `else if ((highestTraversableNeighborFloor - lowestTraversableNeighborFloor) > walkableClimb)` |
| `--climb 1.2` → 4 user points walkable | **CONFIRMED** | fresh `val_climb12_walk.txt`: flood=2528, **all six** WALKABLE, C→f113374 **y=85.57** (gate's own lower-floor collider); gap fill 468→11103; islands 15→14 |
| `--ledge off` OBJ diff (gate's own colliders survive) | **CONFIRMED** | fresh `val_gate_noledge.obj` = 185 v/186 f vs base 97/99; C→f113535 y=85.43 in `val_noledge_walk.txt`; `conn.py` classifies f113535 as a 35.8° ramp |
| erosion alone does NOT eat the bridge | **CONFIRMED** | `UseLedgeFilter` gates only lines 3996 & 4035; `ErodeWalkableArea` (`:4104`) is unconditional → the ledge-off run (erosion ON) still connects all six |
| geometry (C void, B degree-1, gap 0.763/dy 0.129, void band 1.075 m) | **CONFIRMED** | fresh `val_gate_area.obj`: `C (1133.53,991.0) []`; B (1133.0,991.5,85.329) used by **1** face (f112420); plate edge→ground f23 `gap=0.763 dy=0.129`; `band.py` void at z=990.25 = **1.075 m** (plan 1.080), ledge-off ~0.5 m (plan 0.495) |
| exclusive (a)→rim / (b)→intermediate colliders | **UNCERTAIN** | outcome confirmed, but the climb threshold is between 0.8 and 1.0 m: `--climb 0.8` leaves C covered only at **y=92.30** (gate-level void persists), `--climb 1.0` gives C at y=85.37. The plan's 1.05 m traversable-span sits in range; the per-cell split is inferred, not measured |

## Claim 2 — refusal story — **CONFIRMED** (one figure UNCERTAIN)

- **gap fill** — `TryGapFloor` (`:4273-4301`): cap = `maxRun`(=`gapFillCells`=2), flanks on opposite sides along one axis, `if (Math.Abs(y0-y1) > walkableClimb) continue`, filled at `Math.Min(y0,y1)`. **CONFIRMED**. Refusal at the gate holds because gap fill runs **before** the blend (per-tile `:4020` vs merge `:1306`): at that instant the plate is flat 86.30 vs terrain 85.0–85.5 → dy 0.8–1.3 m > 0.6, and cap 2 < the 3–4-cell void. (Caveat the plan doesn't state: on the *final* mesh at z=990.25 the flank dy is only 0.51 m, i.e. the **cap** is the binding constraint there.)
- **per-tile stitch** — `StitchTileComponents` (`:4330`) *does* handle intra-tile components; refusals = same-component skip (`:4426`), vertical `dyAB > walkableClimb*ch` (`:4470-4472`), `StitchGapClear` envelope (`:4476`), 4096 caps (`:4419/4424`). **CONFIRMED**.
- **merge-stitch** — `nc>4096` early-out (`:2344`), per-component `ei>4096` caps (`:2375/2380`), same-component skip (`:2382`). **CONFIRMED verbatim**. The same-component claim is **directly CONFIRMED** by my fresh edge-adjacency check (`conn.py`, welding by the OBJ `# vNNN` original id): the 8 plate faces (f112419/20/22/23/29/30/31/32, 13.0°) and ground-east f112426 (5.5°) share **component root 95**.
- "giant island ~49.5k boundary edges" — **UNCERTAIN** (cap logic confirmed; the specific count is unverified).

## Claim 3 — fix foundations — **CONFIRMED**

- Machinery exists: `ledgeExemptTriangles` → `MarkSteppedCells` (`:3409`) → snapshot before `FilterLedgeSpans` (`:4005-4015`) → `RestoreSteppedSpans` (`:3519`: clearance gate `:3545`, connectivity/orphan gate `:3605-3660`, merge-plane suppression `:3569-3600`). **CONFIRMED**. (Plan's `MarkSteepCells` is a typo for `MarkSteppedCells`.)
- Hook is the only feeder: whole-repo search finds a single `.Add` — `SceneCollisionGeometry.cs:424 ledgeExempt.Add(isSteppedProp)` → `:691 result.LedgeExemptTris` → `VoxelNavMeshGenerator.cs:131` → `Build(..., ledgeExemptTriangles, ...)`. **CONFIRMED**.
- Walk-back rule vs engine link rule: engine rule is `if (gap > linkGap || Math.Abs(dy) > climb) continue;` (`Program.cs:5114-5116`, linkGap 0.02, climb 0.6). The proposed "4-neighbour walkable top span with |dy| ≤ walkableClimb" is the heightfield-cell analogue (adjacent cells touch by construction) and **matches the existing `ColumnsLink(hf, ci, nci, walkableClimb)`** used by `RestoreSteppedSpans`. **CONFIRMED with the nuance that it is an analogue, not literally the mesh-face rule.**

## Claim 4 — prototype expectations — **2 CONFIRMED, 2 UNCERTAIN/RISK**

- C covered + all six WALKABLE — **follows from the mechanism** (both baselines show it).
- void band 1.080 → ~0.5 m — **CONFIRMED** (fresh 1.075 → ~0.5 m).
- "plate still blended / y range unchanged" — **UNCERTAIN / RISK**. Blend counts swing with config: base `blended=54`, ledge-off `77`, climb-1.2 `27`, climb-0.8 `68`, climb-1.0 `38`. Marking the gate's own cells changes the merged mesh that `BlendGatePlates` fits, so the plate's fitted plane (and y range) can move. Do not assert "unchanged".
- "islands ≤ 15" — **UNCERTAIN / RISK**. climb-1.2 gives 14, but **ledge-off gives 16** (> base 15). Removing the trim is not monotonically island-reducing; a *local* exemption should be safer, but ≤15 is not guaranteed.

## Claim 5 — missed / wrong (explicit flags)

1. **The plan's Part 2 (new walk-back loop) is largely redundant.** `RestoreSteppedSpans` pass 2/3 (`:3605-3660`) already floods from pre-walkable columns and drops restores that don't connect within `walkableClimb` as `droppedOrphan` — i.e. it *already implements the walk-back*. Marking the gate's own triangles (Part 1) feeds that gate; the fix may be simply "mark", not "mark + new walk-back".
2. **The validation checklist can false-pass.** Upper castle floors at **y≈92.30 and 97.10** overhang the gate. When the gate-level face is missing, the walk's "lowest covering face" pick falls through to y=92.30 and still prints **WALKABLE** (seen in my climb-0.8 and climb-1.0 runs). The checklist must assert the picked **y is at gate level (~85.x)**, not merely WALKABLE.
3. **islands ≤ 15 not guaranteed** (ledge-off = 16).
4. **"plate still blended / y unchanged" not guaranteed** (blend counts 27–77).
5. **(a)/(b) exclusive attribution unproven** (threshold between 0.8 and 1.0 m).
6. Minor: gap-fill "flanks 1.05–1.3 m > climb" is the gap-fill-**time** state (pre-blend); a reader measuring the final mesh sees 0.51 m at z=990.25 — worth stating.
7. The repo now contains a **byte-identical copy of the plan** (`GateProjectionFixPlan.md`, 20098 B) plus unrelated task plans (`SliverReductionPlan.md`, `RoofFixReport.md`, `LadderSpawnerPlan.md`, `DeterminismImplementationPlan.md`, `DeterminismPlanValidation.md`) — placed by other sessions, not a competing plan.

## Artifacts (all in `gate_plan\`, mine)
`val_base_walk.txt`, `val_climb08_walk.txt`, `val_climb10_walk.txt`, `val_climb12_walk.txt`, `val_noledge_walk.txt`, `val_gate_area.obj`, `val_gate_noledge.obj`, `val_gate_dump.txt`, `val_gate_noledge_dump.txt`, `val_soup_gate.txt`, `conn.py`, `band.py`.

**Bottom line:** the plan's mechanism (claims 1–3) is independently reproduced and code-verified; claim 4's expectations are only partly safe (blend state and island count are risks), and claim 5 flags that the proposed walk-back is probably already implemented and that the walk-based validation can false-pass unless it checks the target's elevation.
