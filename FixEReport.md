# fixE — scope the plate blend to genuine gate projections (wall-corner deck regression)

Model/provider: **LongCat 2.5 Preview Free (OpenCode Go)**. Work root
`C:\Users\Sheepbitter\AppData\Local\Temp\opencode\fixC2\`; real repo read-only; no commits/deploys.
**Final chain: fixB + fixC + fixE (fixD undone).** Validation was skipped per the user (they will
check in the editor); one compile check was run (BUILD OK).

## Context

V3.61+fixB+fixC fixed the user's staircase, but the same class shows on `castle_wall_9_corner_b`
(prefab ~1160.99,1021.39, h≈113.2; wall-corner deck top ~Z 129.77): the deck's flat slab is blended
(rotated) by `BlendGatePlates`, leaving a bare patch + fan (image `Feedback\3.61 w fixD\Capture.PNG`).
fixE implements the user's real scope: **the blend must accept only plates a real gate/usable door
projects** — so the deck AND the staircase landing are left untouched by construction.

## 1. Signal hunt — the marker is the scene entity TAG `gate`

The user's decisive signal: **every gate / usable door has a child entity carrying the scene tag
`gate`** (name-free; in the vanilla prefabs the child happens to be called `clean`, but that name is
NOT the criterion). Raw scene tags are NOT in the snapshot or in `--props` dumps (those carry
BodyFlags — the ladder `0x40` — and the border system's *result* polygon, not raw tags), so they must
be read from the scene file:

`D:\Secondary Steam\steamapps\common\Mount & Blade II Bannerlord\Modules\MyMaps\SceneObj\CC_20_siege\scene.xscene`

That file is XML (`<?xml version="1.0"?><scene name="CC_20_siege" version="2">`). Tags are
`<tags><tag name="..."/></tags>` blocks on `<game_entity>` elements. Parsed with ElementTree
(`work\parse_xscene_tags.py`), the raw evidence:

- **`<tag name="gate"/>` appears on 478 entities** — exactly the `clean` children (94) plus
  `state1..4` (96 each): 4×96 + 94 = 478. There are no other `gate` tags in the scene.
- The gate/usable-door **roots** that own a `gate`-tagged child (raw XML):
  - `european_castle_gate_outer_l1` @ transform `1130.466, 990.709, 85.124` — children
    `clean, state1..4` tagged `gate` (+`operational`/`state_*`/`reference`), physics
    `bo_european_castle_gate_outer_l1`. This is the CC_20 wall gate.
  - `european_castle_gate_outer_l2`, `european_city_gatehouse_a`, `european_castle_gatehouse_a_l3`
    — same `clean`/`state*` + `gate` pattern.
  - `siege_barricade_a` / `siege_barricade_group_a..d` — same pattern (barricades are also
    `gate`-tagged usable/destructible objects; their children carry `bo_siege_barricade_a_state_*`).
- The tagged child's own collider children are tagged separately: `collider_l`, `collider_r`,
  `extra_collider_l`, `extra_collider_r` (12 each) and `collider_agent_l`/`collider_agent_r` (3 each)
  — e.g. raw lines 7876–7958: `clean` (tags `operational,state_0,gate,reference`, physics
  `bo_european_castle_gate_outer_l1`) has children `collider_l`/`collider_r`/`extra_collider_l`/
  `extra_collider_r`. These are the door-leaf colliders of the same prop.
- **Negatives (no `gate` tag, empty `<tags>`):**
  - `castle_wall_9_corner_b` (the deck's prefab root) and its child `european_castle_corner_c_l3`
    (physics `bo_european_castle_corner_c_l3`) — the wall-corner deck regression. tags = none.
  - `stairs_bo` (child of `european_castle_ladder`, physics `bo_2_european_castle_ladder`) — the
    user's staircase landing. tags = none.
  - `european_castle_ladder` (root) — tags = none.
  - `european_castle_wall_gate_l1` (the wall *segment* the gate sits in; physics
    `bo_european_castle_wall_gate_l1`) — tags = none. (The gate itself is the separate
    `european_castle_gate_outer_l1` mission object.)
- Other scene tags seen: `reference` (797), `spawnpoint` (147), `state_*`, `operational`,
  `attacker`/`defender`, `walk_area_vertex` (5, the soft border probes), `inner_gate`/`outer_gate`
  (the gate *tower* roots), etc. Only `gate` marks the usable gate/door children.

So the **gate provenance source = the collision of every entity that carries the `gate` tag plus its
prop's collider set** (the tagged `clean`/`state*` child and its `collider_*` descendants). BodyFlags
do NOT separate gates: across all 3321 snapshot entities the only flags are `0x0`, `HasSteps 0x800`,
`AgentOnly|IgnoreSoundOcclusion 0x10200000`, `IgnoreSoundOcclusion 0x10000000`, `WaterBody 0x100000`.
The gate entities (`european_castle_wall_gate_l1`, `european_castle_gate_outer_l1`, gatehouses) are
all `0x0`, identical to the walls/stairs/deck.

## 2. The patch — `patches\fixE\patch.py` (+ `patch.ps1`)

`python patch.py <workdir>` (or `run.ps1 -Sha 9f7477a -Variant fixE -Patch patches\fixE\patch.ps1`).
It chains **fixB → fixC → fixE** (fixD is deliberately NOT chained). It applies to a pristine
`9f7477a` export (`<workdir>\src\*.cs`). `run.ps1` was extended with a `$candExtra` list so the three
editor-path files are extracted (but NOT compiled by the harness) and the patch can edit them.

Edits, by file:

- **`TiledNavMeshBuilder.cs`** (the core):
  - `Build(...)` gains `bool[] gateTriangles = null` (same shape/contract as `forceWalkableTriangles`
    / `ledgeExemptTriangles`) + a length check.
  - Before `BlendGatePlates`, the gate triangles' XZ footprint is rasterised into a `HashSet<long>`
    `gateCells` via a new `RasterizeTriCellsXz` helper (the same per-face-provenance shape
    `forcedRampCells` already uses).
  - `BlendGatePlates(...)` gains `HashSet<long> gateCells`; after the plate's one-level check (where
    `h` is in scope) it counts the plate's own faces whose centroid cell is a gate cell and rejects
    the plate (`rejNoGate`, `TracePlate "no-gate n/m"`) unless a **majority** are gate cells. With no
    gate cells (a base/fixture without gate info) nothing is blended. Summary lines gained `notGate=`.
- **`SceneCollisionGeometry.cs`** (editor path, reads the tag live): new `GateTag = "gate"` const and
  `SafeGateTag(e)` which walks `e` and its `Parent` chain via `HasTag` (the same API the border probes
  use). `AssetTriangles`/`AssetGeometry` gain `bool[] GateTris`; `AssetEntityRecord` gains `int Gate`;
  `Candidate` gains `IsGate`; pass 2 builds the per-triangle mask (a true-tail after the real tris,
  mirroring `LedgeExemptTris`) and sets `rec.Gate`.
- **`VoxelNavMeshGenerator.cs`**: `BuildExportMesh` gains `bool[] assetGate` and passes
  `BuildCombinedForceMask(terrainSoupTris, assetTriCount, assetGate)` as the builder's gate mask.
- **`SceneNavMeshExporter.cs`**: threads `assetGate` through both the sync and async paths
  (`AsyncVoxelBuild.AssetGate`), and writes `src.Gate` into the snapshot via
  `SceneSnapshot.Expand(..., src.Gate)`.
- **`SceneSnapshot.cs`**: `AssetsVersion = 2` (v1 still reads, `Gate=0`); `EntityRecord.Gate`;
  writer writes it; reader reads it on v2; `Expand(..., int gate = 0)` sets it.

The offline harness (`tiled-core-harness\Program.cs`) was extended so a v2 fixture can be validated:
it builds a `soupGate` mask from the per-entity gate bit (aligned like `soupForce`/`soupExempt`) and
passes it to `Build`. An annotated copy of the CC_20 fixture (v2, 524 gate entities) is produced by
`work\annotate_fixture.py` → `CC_20_siege_navmesh_gate_snapshot_*`.

## 3. Compile check

`run.ps1 9f7477a fixE patches\fixE\patch.ps1` → every anchor applied, **`BUILD OK`**
(`build.txt`: "Build succeeded."), run completed. The core (`TiledNavMeshBuilder.cs`,
`SceneSnapshot.cs`) and the harness are compiled; the three editor-path files are extracted but not
compiled by the harness (they reference TaleWorlds types).

## 4. Risks / uncertainties (for the orchestrator before deploying)

- **The editor path (`SceneCollisionGeometry` / `VoxelNavMeshGenerator` / `SceneNavMeshExporter`) is
  NOT compiled by the harness** — it was reviewed by hand and the anchors all matched, but it needs a
  real in-editor build to confirm. Watch for: `GameEntity.Parent` availability (present in the
  decompiled `TaleWorlds.Engine.GameEntity`, line 1191), `HasTag` (line 1150), and the
  `AsyncVoxelBuild` field name.
- **The gate scope is a MAJORITY of the plate's own faces on gate cells.** This was chosen to mirror
  fixD's shape, but it was NOT acceptance-tested (validation skipped). If the editor shows the CC_20
  wall-gate plate no longer blending/connecting, relax `_gateOwn * 2 < g.Count` to `_gateOwn == 0`
  (i.e. "any face on a gate cell"). The plate's XZ (≈1127.75..1133 × 989..992) lies inside the
  `european_castle_gate_outer_l1` colliders' footprint (X≈1127.5..1133.5, Z≈986..993), so majority is
  expected to hold — but this is the one thing to eyeball.
- **Provenance is XZ-only (no height).** A flat plate anywhere whose XZ overlaps a gate/barricade
  prop would qualify. The false positives (the corner deck at 1161,1021 and the stairs landing at
  1161,1124) are tens of metres from any gate, so they are rejected; but a gate-tower *roof* plate
  directly above a gate could in principle qualify. Not observed.
- **`gate` is also on siege barricades** (478 tags: the `clean`/`state1..4` children of CastleGates
  AND of `siege_barricade_a`/`siege_barricade_group_*`). They are `gate`-tagged usable/destructible
  objects, so they are included by the tag rule. If a barricade causes an unwanted blend, the scope
  could be narrowed to CastleGate roots only — but the user's marker is exactly this tag.
- **`gate`-tagged entities' collider children** (`collider_l/r`, `extra_collider_l/r`) do NOT
  themselves carry `gate`; they are covered because `SafeGateTag` walks the **ancestor** chain and the
  tagged `clean` is their parent. `collider_agent_l/r` (children of the gate root, not of `clean`)
  are agent-only and excluded from the soup anyway.
- **The fixture annotation is ONLY for the offline harness** — the editor reads tags live and needs no
  annotation. A re-dumped v2 snapshot is not required for deployment.

## 5. Artifacts / model

- `patches\fixE\patch.py`, `patches\fixE\patch.ps1` — **the fix (ships)**; chain fixB+fixC+fixE.
- `run.ps1` — extended with `$candExtra` (editor-path files extracted, not compiled).
- `work\parse_xscene_tags.py` (xscene tag evidence), `work\parse_gate_owner.py` / `parse_gate_owner2.py`
  (gate owner + plate), `work\annotate_fixture.py` (v2 annotated fixture),
  `CC_20_siege_navmesh_gate_snapshot_*` in `tiled-core-harness\`.
- `work\9f7477a_fixE` — the compile-check build.
- **Model/provider:** LongCat 2.5 Preview Free (provider `opencode-go`). No model switch, no glitch.
