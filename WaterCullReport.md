# Water-Level Navmesh Cull / Raise Report (V3.67, floor-like gate V3.68)

Date: 2026-10-09. Feature (user request): an option to handle navmesh below the scene's water level -
**cull it**, or **raise it to the water surface**. Both modes account for the height offset so areas
shallow enough to wade are not culled. **Both modes act on floor-like surfaces only; obstacle geometry is
never culled or clamped** (see the bug fix below).

## Semantics

- The editor **always** reads `Scene.GetWaterLevel()` - scenes always have a water level (dry maps set
  it well below the terrain, so both modes are a natural no-op there). A failed read disables the
  feature for the run and logs (`SafeWaterLevel`).
- **Offset-aware**: the comparison is on the LIFTED height (`soup y + NavMeshHeightOffsetMeters`, the
  +0.5 lift) - so a wadeable shallow (mesh at/above the level after the offset) is kept.
- **Floor-like gate (both modes)**: a triangle is floor-like when its Recast-space upward normal is at
  least `cos(NavMeshMaxSlopeDegrees)` (the same walkable-slope threshold `MarkWalkableTriangles` uses),
  plus a small margin so a floor exactly at the cap is kept. Synthesized ramps (force-walkable) count
  too. Vertical / tapered wall geometry has a near-zero upward normal and is **never** culled or
  clamped. Floors and banks up to the cap are included; anything steeper is treated as an obstacle.
- **Mode A - cull ON** (`NavMeshCullBelowWaterLevel = true`): a **floor-like** soup triangle is culled
  when `minCornerY + offset < level` (strict `<`, min-corner rule - a floor dipping below is culled
  whole; documented shoreline behaviour).
- **Mode B - cull OFF (the editor default)**: every **floor-like** soup vertex whose lifted height is
  below the level is clamped pre-lift to `level - offset` (idempotent `max`), so the emitted mesh sits at
  the water level. Walls are untouched: their submerged extent stays and they keep blocking through the
  raised sheet. V2 only (V1 keeps the cull wiring; the raise is unsupported there).
- Placement: the V2 core's binning path in `TiledNavMeshBuilder.Build`, on the merged terrain+asset
  soup, before rasterisation (true heights; the lift applies afterwards).
- Harness mirror: `--cullwater <level>` / `--raisewater <level>`; the default path is a no-op (existing
  fixtures unchanged -> goldens untouched). Snapshot dumps record `water: <level>`.

## Bug fix (V3.68): walls were being culled/clamped

**Reported:** "when culling below water level is enabled, it just starts ignoring walls."

**Mechanism:** the min-corner rule culled a WHOLE triangle when any corner was below the level. Wall /
structure side quads span from below the level to above it, so they were removed entirely and stopped
blocking. Evidence from the pre-fix A/B: CC_20 @90 removed triangles with corner heights up to 113 m at a
level of 90 - geometry extending 23 m ABOVE the level. Mode B had the same flaw (it clamped wall
vertices).

**Fix:** both modes are gated on floor-like triangles (the upward-normal test above). Walls are never
culled or clamped. `TiledNavMeshBuilder.cs`: `WaterFloorLikeMargin` / `WaterFloorLikeMinNormal`, the
per-triangle normal test, and the gated cull/raise. The log line now prints
`floorLike(ny>=...)` and says "wall/obstacle geometry is never culled/clamped".

## Validation (verified)

- **Test 29** (suite): min-corner rule; offset accounting (offset 0.5 flips the cull boundary exactly);
  raise exactness (the pre-lift soup lands exactly at `level - offset`; `soupExact=True`); strict `<` at
  the level; off-inert; **wall case** - a vertical wall spanning y 0..12 (level 5) between two y=10 floors
  survives BOTH modes (`culled=0`, `raised=0`) and still blocks (0 emitted faces cross the wall plane).
  PASS. Test 1's stale tile expectation fixed.
- **Default identity**: all three goldens PASS; `--cullwater/-raisewater -10000` are no-ops with
  identical hashes; the default settings echo `water=off`.
- **A/B, both modes** (tile 10; baseline F: CC_20_v2 246028, Birtu 43656, Basamu 491641). Pre-fix
  counts in brackets show how much wall geometry the gate now protects:

| fixture | mode | effect | F | shoreline |
|---|---|---|---|---|
| CC_20_v2 @90 | cull | 261986/1282661 tris [was 385172] | 163983 | transition=0 |
| CC_20_v2 @90 | raise | 237548/1801264 verts [was 519246] | 224458 | sheet=56292, submerged=2, transition=931 (928 walkable), max=53.35deg |
| Birtu @30 | cull | 222358/665341 [was 443445] | 17401 | transition=33 (29 walkable), max=74.97deg |
| Birtu @30 | raise | 547767/1812088 [was 1191439] | 30608 | sheet=11341, submerged=2, transition=495 (488 walkable), max=76.44deg |
| Basamu @30 | cull | 555313/3912455 [was 788618] | 419354 | transition=0 |
| Basamu @30 | raise | 605002/6496534 [was 1296168] | 481207 | sheet=57431, submerged=0, transition=1067 (1061 walkable), max=67.58deg |

The removed corner heights are now bounded by the floor slope instead of unbounded wall spans:
CC_20 max 113 -> **98.79 m**, Birtu 38.094 -> **35.229 m**, Basamu 46.551 -> **33.074 m**. The deep/shallow
boundary stays 99% walkable (<= 50 deg); a handful of steep outliers are reported by the shoreline line.

## Notes

- **Lattice, not exact**: the soup clamp is exact, but the emitted surface is quantised to
  `CellHeightMeters` (0.2 m), so the mesh lands within ~1 cell of the level (measured 29.95 vs 30,
  89.91 vs 90). A one-line knob (`level - offset + ch`) would guarantee at/above if ever required.
- **Steep terrain is treated like a wall** (normal below the cap): its submerged extent is left in place,
  which is why a raise build can still contain a face or two below the level (`submergedFaces=2` on
  CC_20/Birtu) and the mesh min Y can sit a few metres under the level at a steep bank. Floors and banks
  up to the cap are still raised.
- **The editor default changed by design**: toggle off now raises on water maps (dry maps no-op); the
  harness default stays a no-op, which is why the goldens are unaffected.
- Mode B face counts are higher than the pre-fix build because walls (and their submerged extents) are
  now retained; Mode A culls less for the same reason.

## Files

`TiledNavMeshBuilder.cs` (settings, floor-like gate, cull+raise, counters, logs),
`VoxelNavMeshGenerator.cs` (summary propagation), `SceneNavMeshExporter.cs` (setting, `SafeWaterLevel`,
sync+async, snapshot `water:`), `tools/TiledCoreHarness/Program.cs` (flags, Test 29 incl. the wall case,
Test 1 fix, shoreline report), READMEs.
