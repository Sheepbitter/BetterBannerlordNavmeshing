# Gate Connector Report (V3.66)

Date: 2026-10-08. Round: the CC_20 gate crossing fix - a **synthesized bridge strip at gate plates**
(the user's explicit requirement), modeled on `SteppedPropRamps`. This replaces the marking /
ledge-exemption approach, which was ripped out (it cannot work: there is no geometry to mark at the
defect - see below).

## Problem (verified facts)

- The gate crossing hole at C (1133.530, 991.000) is a ~0.8 m footprint gap with **no horizontal soup
  geometry**: only vertical door-frame quads at y≈85.0-85.1 plus faces 25 m overhead.
- The passage-floor colliders are separate props (`collider_l/r`, `extra_collider_l/r`,
  `collider_agent_l/r`; flat slabs y≈85.0-85.6) ending at x≈1132.8-1132.9; the gate prop
  (`european_castle_wall_gate_l1`) itself has no passage floor.
- The old plan's premise (a gate ramp eaten by the ledge trim) is invalid on the current data; its
  baselines predate the determinism + 2.0-vox changes and do not reproduce.

## Design (`GateBridgeStrips.cs`)

- **Detection: gate-bit only** - the fixE `gate` scene tag (the passage colliders carry it; verified
  by parsing the v2 snapshot assets). Name-free; a shared `IsThinSlab` predicate gates plate shape.
- **Synthesis**: rasterize the plate footprint; per boundary run, march outward to the first walkable
  surface within `MaxBridgeRun=2 m`; emit one sloped quad per run (force-walkable + ledge-exempt),
  with a flat plate-level apron (`PlateApronMeters`, >=1 cell) and a target apron.
- **Anchoring**: the plate's REAL walkable top = `max(flagged-slab top, asset up-facing top within
  [+0.6/-0.3])` - never the flagged sub-slabs, never the overhead decks (91.5/96 m).
- **Target grid band-limited** to `[plateTop - (MaxDrop+0.5), plateTop + 1.0]` so overhead structure
  cannot mask the ground; the exact terrain is passed as geometry (not a height probe).
- **No inset under the plate**: the strip's descent starts exactly at the plate boundary, so no span
  sits below the plate (the regression fix below).
- **Wiring**: editor path (`SceneCollisionGeometry`), harness mirror (`--gate on|off`, default off;
  `ApplyGateConnectors`), shipped setting `NavMeshVoxelGateConnectors` (default true), log lines, README.

## Regression found and fixed during the round

The first implementation placed strip spans under existing walkable faces: `FilterWalkableLowHeightSpans`
then nulled the plate/ground faces (t1/t5 fell to the 92.30 m upper-floor false-pass). Fixed by the
anchoring, no-inset, flat-apron and band-limited-target-grid changes above. (My verification caught the
net 5/6 -> 4/6 before it shipped as "done".)

## Validation (verified, raw outputs in `C:\Users\Sheepbitter\AppData\Local\Temp\opencode\gate_connector\`)

Six-point walk on `CC_20_siege_navmesh_gate`, `--gate on` - **all six WALKABLE at gate level, one flood**:

| target | base (gate off) t16 | gate on t16 | gate on t10 |
|---|---|---|---|
| 0 (1132.00,990.25) | 86.30 | 86.30 | 86.30 |
| 1 (1133.00,991.50) | 86.30 | 86.03 | 86.30 |
| 2 C (1133.53,991.00) | **VOID** | **86.03** | **85.77** |
| 3 (1132.25,989.00) | 85.77 | 86.23 | 86.30 |
| 4 (1133.00,990.13) | 85.17 | 86.23 | 86.30 |
| 5 (1133.75,991.25) | 85.03 | 86.03 | 85.77 |

- Islands unchanged (t16 13, t10 14); invariants PASS both.
- Base (`--gate off`) unchanged (F 235699, C VOID).
- Birtu/Basamu with `--gate on`: no gate bits -> no strips -> identical to base (F 43656 / 491641,
  islands 5 / 1). The broad v1 slab fallback was removed (it fired on unrelated village slabs).
- Goldens: default path unchanged (`GOLDEN-OK ... GOLDEN HASH PASS`); no re-capture.
- Suite (no-args): exit 0 (the two documented informational/accepted rows).
- Deployed both variants 2026-10-08 21:53.

## Honest deviation

t5 (and t1/t2 at tile 16) ride the bridge ramp at ~86.0 (plate level) rather than the base's ground
(~85.0). It is gate level and the crossing is continuous; reproducing the base's exact ground height at
t5 would need per-run ramp lengths (a possible polish item, not an acceptance failure).

## Open items

- Per-run ramp lengths if the exact ground height at t5 ever matters.
- The v1 snapshot format carries no gate bits (the editor writes v2); a v1 fixture cannot exercise the
  connector - by design, since the broad fallback was removed.
