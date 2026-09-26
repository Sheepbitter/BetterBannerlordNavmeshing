# Phase 1 probe findings (2026-09-26)

Probe build: commit `016e7df`. Evidence: `NavMesh_20260926_115434.log` (5 probe runs + 4 exports) and
`NavMesh_20260926_121211.log` (post-restart Birtu import) in
`Documents\Mount and Blade II Bannerlord\BetterBannerlordNavmeshing\Logs`.

Scenes probed: `mp_tdm_Birtu` (600x600 m, 6x6 nodes of 100 m, 10,936 entities) and
`mp_battle_na'ilu_L` (1600x1600 m, 16x16 nodes of 100 m, terrain layers=8, 1,457 entities).

## 1. Collision shapes: complete coverage, negligible cost

```
Birtu : probe: entities=10936 physics=4366 shapesOk=4366 shapesNull=0 shapesEmpty=0 shapeExceptions=0 batchedKinematic=0 trisTotal=591931 scanMs=38
Na'ilu: probe: entities=1457  physics=4    shapesOk=4    shapesNull=0 shapesEmpty=0 shapeExceptions=0 batchedKinematic=0 trisTotal=146    scanMs=2
```

- `GetBodyShape()` returned a usable shape for **every** physics entity on both maps: no nulls, no empties,
  no exceptions, nothing lost to batched/kinematic batching on these scenes.
- Whole-map collision triangle budget is tiny: 591,931 (Birtu) - about 1/7 of the Na'ilu terrain samples at
  stride 1 (4.2 M). Rasterizing collision geometry will not be the bottleneck.
- Most shapes are a single triangle mesh. Classify by entity **name + BodyFlags + mobility**, not
  `GetPrefabName()` (returns empty string for placed entities in the editor).

## 2. Coordinate space: shape-local, place via the entity global frame

`sturgia_castle_stairs_c_l2` (all 79 triangles sampled):

```
triRaw               =[-1.235,-1.615,-0.845 .. 1.323,1.253,1.631]   (from PhysicsShape.GetTriangle)
physBoxIncChildren   =[-1.235,-1.615,-0.845 .. 1.323,1.253,1.631]   (entity-local, matches triRaw)
globalBox            =[214.944,197.57,18.787 .. 218.73,202.042,21.364]
triRaw*globalFrame   =[214.996,197.609,18.855 .. 218.582,202.019,21.331]   (= globalBox)
```

Same pattern on the big prefabs (gatehouse 5,626 tris, dungeon: framed AABB falls inside the global box).
=> **triangles are shape-local; world position = entity.GetGlobalFrame().TransformToParent(vertex).**
Multiple instances of one prefab return identical raw triangles and differ only by their frame.

## 3. Ground queries

- `GetGroundHeightAtPosition` returns the **topmost collision hit**, not the terrain (Birtu: 39.044 on a
  floor vs `GetTerrainHeight` 6.99). It is a "what would I stand on" query, not a terrain query.
- Its `out normal` overload returned `NaN,NaN,NaN` in all six probe runs -> do not use; compute normals
  from the triangles.
- `GetNavigationMeshForPosition`: face group 0 on Na'ilu (our imported navmesh) and `none` on Birtu
  before its import -> group-0 faces answer engine queries as expected.

## 4. Multi-level columns (the real stress test)

```
A (340.603,339.525): 7 hits z=[39.044, 38.977, 29.519, 20.200, 20.106, 11.281, 6.990]
                     gaps=[0.067, 9.458, 9.319, 0.094, 8.824, 4.291]
B (353.495,339.314): 5 hits z=[39.026, 29.520, 20.190, 11.290, 6.990]
C (294.359,358.219): 6 hits z=[29.703, 25.650, 21.100, 20.160, 20.080, 6.990]
                     gaps=[4.057, 4.540, 0.952, 0.076, 13.088]
Na'ilu (929.486,1068.057): 1 hit z=22.478 (terrain); navMeshFaceGroup=0
```

- Columns pass through **5-7 walkable levels**; vertical gaps up to 13.09 m (and 32 m from the top floor
  to the terrain). All of it must live in ONE continuous navmesh, with no refusal when a deck sits above
  another walkable surface.
- The 39.044/38.977 (6.7 cm) and 20.200/20.106 (9.4 cm) pairs are **duplicate overlapping floor prefabs**.
  The >= 1.5 m stacking rule must de-duplicate near-identical surfaces, not just pick the topmost.
- The probe marches downward with a fresh raycast per hit; on these maps no hit was missed or doubled.

## 5. Classification & levels

```
Birtu:          probe classes: terrain=0 flora=0 stairs=84 ladder=2 barrier=29 dynamic=873 static=9948
Na'ilu (12:06): probe classes: terrain=0 flora=0 stairs=0  ladder=0 barrier=1  dynamic=19  static=1437
levels: count=1 combinedMask=0x00000001   (both scenes)
```

- Stairs flag (`0x00000800`) fires on 84 Birtu entities - stairs are identifiable for the Phase 3
  ramp treatment.
- `barrier` entities are invisible blocker volumes -> exclude from meshing.
- `dynamic` mobility (873 on Birtu, e.g. merlons) still exposes full collision shapes; include/exclude
  by flags, not by mobility alone.
- Both scenes declare a single navmesh level -> exported faces carry level mask `0x1`.

## 6. Performance / flow notes

- Probe scan: 38 ms (Birtu) / 2 ms (Na'ilu).
- Terrain-only builds: Birtu 11-38 ms (129-193 samples per side), Na'ilu 537 ms (683 per side).
- Birtu import after the restart: 27,885 faces, 27 ms of native import, `totalMs=66` - the earlier
  failures were purely the startup prefab list, the bin itself was fine.
- Sampler: layout resolved to ix-major on all runs; `pointCrossCheck` 100% everywhere; the reported
  node min/max can differ from the live array (worst delta 1.65 m on Birtu) but is informational only.

## 7. Implications for the next phases

1. Collision extraction is a solved input: (name, flags, mobility, triangles, frame) per entity,
   transformed at raster time; exclude barriers.
2. Multi-level stacking is confirmed as the core case, with 5-7 layers per column and near-duplicate
   surfaces - the tile rasterizer must handle overlapping spans without refusing.
3. Stairs get flagged treatment (ramps), not per-step rasterization.
4. Single level mask (0x1) on both test scenes; keep the mask plumbing for future scenes.
5. Normals: compute from triangles; ground-query normals are unusable.
