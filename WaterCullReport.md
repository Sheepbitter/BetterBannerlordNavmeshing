# Water-Level Navmesh Cull / Raise Report (V3.67)

Date: 2026-10-09. Feature (user request): an option to handle navmesh below the scene's water level -
**cull it**, or **raise it to the water surface**. Both modes account for the height offset so areas
shallow enough to wade are not culled.

## Semantics

- The editor **always** reads `Scene.GetWaterLevel()` - scenes always have a water level (dry maps set
  it well below the terrain, so both modes are a natural no-op there). A failed read disables the
  feature for the run and logs (`SafeWaterLevel`).
- **Offset-aware**: the comparison is on the LIFTED height (`soup y + NavMeshHeightOffsetMeters`, the
  +0.5 lift) - so a wadeable shallow (mesh at/above the level after the offset) is kept.
- **Mode A - cull ON** (`NavMeshCullBelowWaterLevel = true`): a soup triangle is culled when
  `minCornerY + offset < level` (strict `<`, min-corner rule - a triangle dipping below is culled
  whole; documented shoreline behaviour).
- **Mode B - cull OFF (the editor default)**: every soup vertex whose lifted height is below the level
  is clamped pre-lift to `level - offset` (idempotent `max`), so the emitted mesh sits at the water
  level. V2 only (V1 keeps the cull wiring; the raise is unsupported there).
- Placement: the V2 core's binning path in `TiledNavMeshBuilder.Build`, on the merged terrain+asset
  soup, before rasterisation (true heights; the lift applies afterwards).
- Harness mirror: `--cullwater <level>` / `--raisewater <level>`; the default path is a no-op (existing
  fixtures unchanged -> goldens untouched). Snapshot dumps record `water: <level>`.

## Validation (verified)

- **Test 29** (suite): min-corner rule; offset accounting (offset 0.5 flips the cull boundary exactly);
  raise exactness (the pre-lift soup lands exactly at `level - offset`; `soupExact=True`); strict `<` at
  the level; off-inert. PASS. Test 1's stale tile expectation fixed.
- **Default identity**: all three goldens PASS; `--cullwater/-raisewater -10000` are no-ops with
  identical hashes; the default settings echo `water=off`.
- **A/B, both modes** (tile 10; baseline F: CC_20_v2 246028, Birtu 43656, Basamu 491641):

| fixture | mode | effect | F | shoreline |
|---|---|---|---|---|
| CC_20_v2 @90 | cull | 385172/1282661 tris | 164719 | transition=0 |
| CC_20_v2 @90 | raise | 519246/1801264 verts | 191699 | sheet=23030, submerged=0, transition=1080 (1072 walkable), max=89.31deg |
| Birtu @30 | cull | 443445/665341 tris | 17668 | transition=21 (19 walkable), max=88.37deg |
| Birtu @30 | raise | 1191439/1812088 verts | 23394 | sheet=4507, submerged=0, transition=576 (567 walkable), max=80.00deg |
| Basamu @30 | cull | 788618/3912455 tris | 419462 | transition=0 |
| Basamu @30 | raise | 1296168/6496534 verts | 439241 | sheet=16015, submerged=0, transition=1394 (1380 walkable), max=79.13deg |

`submergedFaces=0` in every raise build - nothing is left under the level. The deep/shallow boundary is
98-99% walkable (<= 50 deg); a handful of steep outliers (79-89 deg) are reported by the shoreline line.

## Notes

- **Lattice, not exact**: the soup clamp is exact, but the emitted surface is quantised to
  `CellHeightMeters` (0.2 m), so the mesh lands within ~1 cell of the level (measured 29.95 vs 30,
  89.91 vs 90). A one-line knob (`level - offset + ch`) would guarantee at/above if ever required.
- **The editor default changed by design**: toggle off now raises on water maps (dry maps no-op); the
  harness default stays a no-op, which is why the goldens are unaffected.
- Mode B increases face count vs baseline (the raised sheet adds walkable area over deep water);
  Mode A decreases it.

## Files

`TiledNavMeshBuilder.cs` (settings, cull+raise, counters, logs), `VoxelNavMeshGenerator.cs` (summary
propagation), `SceneNavMeshExporter.cs` (setting, `SafeWaterLevel`, sync+async, snapshot `water:`),
`tools/TiledCoreHarness/Program.cs` (flags, Test 29, Test 1 fix, shoreline report), READMEs.
