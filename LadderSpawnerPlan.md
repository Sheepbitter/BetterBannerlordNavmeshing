# Ladder spawner handling — findings & implementation-ready plan
Model: GLM 5.3 Flash / nvidia-Free-1k-Credits (model id `z-ai/glm-5.3-flash`)
Date: 2026-10-07. Read-only investigation of `C:\Users\Sheepbitter\source\repos\BetterBannerlordNavmeshing`; all experiments in `C:\Users\Sheepbitter\AppData\Local\Temp\opencode\ladder_plan\`.

---

## 1. Findings

### F1. What a siege ladder spawner is
- `SiegeLadderSpawner` is a **script entity** (SpawnerBase-derived `MissionObject` script) placed via the `siege_ladder_*m_spawner` prefab from `D:\Secondary Steam\steamapps\common\Mount & Blade II Bannerlord\Modules\Native\Prefabs\spawners.xml`.
- `D:\...\Modules\MyMaps\SceneObj\CC_20_siege\scene.xscene` contains **5** (regex `<game_entity name="siege_ladder_\d+m_spawner">`):
  - 2× `siege_ladder_7m_spawner` — (1169.947,1138.738,102.329) rot z=1.915 Upper=-20.131 Down=89.427; (1169.555,1140.646,103.141) rot z=2.198 Upper=-26.878 Down=91.146
  - 2× `siege_ladder_6m_spawner` — (1131.903,983.102,85.918) rot z=1.205 Upper=-19.000 Down=103.178; (1237.708,1138.579,102.196) rot z=1.875 Upper=-18.427 Down=125.094
  - 1× `siege_ladder_8m_spawner` — (1139.228,996.402,82.804) rot z=1.299 Upper=-19.000 Down=110.483
- **All 5 have `OnWallNavMeshId="-1"`** — invalid per `SiegeLadderSpawner.OnCheckForProblems`: OnWallNavMeshId must not be -1 or 0 in a singleplayer scene, and its ones digit must not be 1.
- The spawner entity has `<physics mass="1.000"/>` but **no shape** → `GameEntity.HasPhysicsBody()` is false.
- The real ladder is a **separate prefab spawned at runtime**: `Modules\Native\Prefabs\props_siege_ladders.xml` `siege_ladder_6m` — script `SiegeLadder`, tags `ladder_body`/`ladder_collision_body`/`ladderDown`/`ladderUp`; child `body_interact` (tag `ladder_body`) physics shape `bo_siege_ladder_6m`, body_flags `moveable` + `ladder` + `agent_only`; `ladder_down_state` shape `bo_ambush_spots` (`only_collide_with_raycast` + `dont_transfer_to_physics_engine`); `siege_ladder_6m_ghost` is editor-only visuals.

### F2. How the pipeline treats spawners today
- `SceneCollisionGeometry.cs` **line 210**: `if (!e.HasPhysicsBody()) continue;` — the spawner is skipped before classification (only the raw entity `count` at line 206 sees it). Lines 220–233 flag-classify; **line 262**: `if (isLadder && !propRamps) { exLadder++; continue; }` excludes pre-placed ladders by default; line 263 `ladderIncluded++` collects them when `--ramps on` (SteppedPropRamps bridges, `FlagLadder=0x40`, `FlagHasSteps=0x800`).
- Evidence (fixture scans, outputs in `...\ladder_plan\ladders_*.txt`, `props_spawner.txt`):
  - CC_20 v1+v2 → **0 ladders**, 8 `ladder`-named props (`european_castle_ladder` ×6 flags=0x0, `wooden_platform_ladder_a` ×2)
  - mp_tdm_Birtu → 2 ladders (`civilian_ladder_5m/6m` flags=0x40, tris=12)
  - mp_battle_Basamu → 3× `civilian_ladder_4m` flags=0x40, `#ramp` flags=0 tris=6 each
  - **Zero `spawner`-named entities in any snapshot** — consistent with the HasPhysicsBody skip.
- `Feedback\3.48\siege_ladder_spawner.PNG`: navmesh only at ground/base; nothing on ladders or wall top.

### F3. ROOT CAUSE — the runtime climb mechanism needs face group ids our prefab never carries
Decompiled runtime chain (`TaleWorlds.MountAndBlade.dll`, saves in `...\ladder_plan\`):
- `SiegeLadder` **line 387**: `Scene.MarkFacesWithIdAsLadder(OnWallNavMeshId, true)` at init — bots climb via **navmesh faces carrying that group id**, not via the ladder's collision mesh.
- Lines 376/385: `_queueManagerForAttackers/Defenders.Initialize(OnWallNavMeshId, ...)`; lines 573–577 & 1200: `SetAbilityOfFaces` / `SetAbilityOfFacesWithId(OnWallNavMeshId, enabled)` on state change; line 1357: `GetNavmeshFaceIds` returns `{OnWallNavMeshId}`; `OverTheWallNavMeshID => 13` (hardcoded); `SiegeTower` marks ids +5/+6/+7 as ladder.
- `LadderQueueManager` **line 671**: side detection compares `FaceGroupIndex % 10` — the group id's ones digit is semantically loaded; line 415: cost setting only when `ManagedNavigationFaceId > 1`; the queue manager sets costs via `SetNavigationFaceCostWithIdAroundPosition(ManagedNavigationFaceId, ...)`.
- `Mission.GetNextDynamicNavMeshIdStart` (lines 2547–2552): `_nextDynamicNavMeshIdStart = 1000050`, `+= 50` per siege equipment → **dynamic group ids live at ≥ 1000050, in blocks of 50** (each equipment uses +1..+10 within its block: `MissionObject.AttachDynamicNavmeshToEntity` lines 83–89).
- `TaleWorlds.Library.PathFaceRecord` = `{ int FaceIndex; int FaceGroupIndex; int FaceIslandIndex; }` — face records DO carry a group id.

**f1 IS the face group id in the NMG8 prefab format** — three independent vanilla confirmations, all zero-gap parses (own parsers `parse_nmg8_full.ps1` / `parse_nmg9_full.ps1`):
| file | magic | vc/ec/fc | facesEnd | trailing | f0 | f1 (group id) |
|---|---|---|---|---|---|---|
| `Native\NavMeshPrefabs\siege_tower_12m_dnm.bin` (8405 B) | NMG8 | 100/165/65 (10 tri+55 quad) | 8145 | 260 = 4+32×8 | {0} | **{1,2,3,4,5,6,7}** — exactly the tower's `AttachNavigationMeshFaces(DynamicNavmeshIdStart + 1..7)` refs |
| `Native\NavMeshPrefabs\batteringram_c_dnm.bin` (2290 B) | NMG9 | 40/40/10 (all quad) | 2030 | 260 | {0} | **{4,8}** — matches BatteringRam `+ 4` (line 218) / MissionObject `+ 8` (line 89) |
| `Full Invasion 3 mapping\NavMeshPrefabs\siege_tower_warband_dnm.bin` (8228 B) | NMG9 | 112/154/52 (7 tri+45 quad) | 7968 | 260 | {0,1,2} | {1,2,3,4} |
| `Native\NavMeshPrefabs\mangonel_a_dnm.bin` (1593 B) | NMG8 | 20/28/9 (all quad) | 1333 | 260 | {0} | {4} |

- Our `Nmg8Writer.cs` **line 290** writes `writer.Write(0);` for field 1 (mislabelled "level_cost_index" in the comment at line 287) for **every face** → every imported face lands in group 0.
- Both import paths pass **shift 0** (`SceneNavMeshExporter.cs:565`, `TerrainNavMeshGenerator.cs:1243`), so f1 values are **absolute scene group ids**; at runtime group id = f1 + shift.
- Consequence: `SetAbilityOfFacesWithId(0, ...)` is refused by the engine (native string **"Can not enable/disable navigation faces with group id 0"** in `TaleWorlds.Native.dll`) and `MarkFacesWithIdAsLadder(X≠0)` marks nothing (no face carries X). The engine's ladder machinery can never engage on our bake — including the `--ramps on` collected `civilian_ladder_*` props.

### F4. Format notes (verified byte-level)
- **NMG8**: `[bodyLen(4)]["NMG8"] + vcount(4) + verts(12 B) + ecount(4) + edges(20 B: [-1, a, b, -1, -1]) + fcount(4) + faces(25 + 8n: n, verts×n, edges×n, vfc=2, f0, f1=group id, cost f32, level_mask u32, inverted u8) + trailing(4 + 32×8 = 260)`.
- **NMG9**: same face record; edges are **24 B** (`[-1, a, b, -1, -1, extra]`), extra int observed 0. Scene-level `navmesh.bin` is an **RNM1 container** wrapping one NMG9 blob (CC_20_siege: len=7689594, NMG9 at offset 50, sublen=144155) — a genuinely different structure (not `[bodyLen][magic][vcount]`), not decoded further; not needed for prefab writes.
- NMG4/NMG5/NMG7 also exist in vanilla `NavMeshPrefabs` (older DNM layouts, unparsed).

---

## 2. Desired behaviour
1. **Detect spawners**: scan scene entities for the `SiegeLadderSpawner` script (raw entity name `siege_ladder_<n>m_spawner`) — not via physics bodies.
2. **Synthesize the deployed ladder span**: from the spawner's global frame (z-rotation = horizontal direction; ladder leans to the wall), build a plank/ramp surface from ground to wall-top using the ladder prefab's collision shape (`bo_siege_ladder_<n>m`) or a rung-friendly synthesized ramp (reuse `SteppedPropRamps` ladder connector logic).
3. **Tag ladder-span faces with a real group id**: f1 = OnWallNavMeshId (absolute, shift=0).
4. Keep everything else unmarked (f1=0) — unmarked faces coexist with marked ones (vanilla tower DNM proves marked faces work; our current all-0 prefab proves unmarked import works).
5. **Exempt marked ladder faces** from steep-pruning / tallSteep classes in the voxel builder, and never erode the ladder strip below MinWidthMeters.
6. The map author (or the tool, auto-assign + patch) must set OnWallNavMeshId ≠ -1/0, ones digit ≠ 1, < 1000050.

## 3. Concrete file-level changes
1. **`Nmg8Writer.cs`** — the core one-liner + plumbing: add an optional per-face group-id array (`int[] faceGroupIds`) to the Write* overloads; line 290 `writer.Write(0);` → `writer.Write(faceGroupIds != null ? faceGroupIds[i] : 0);`; fix the comment at line 287 (field 1 = face group id, not level_cost_index). Both producers (v1 `TerrainNavMeshGenerator.cs`, v2 `TiledNavMeshBuilder.cs`) pass the array.
2. **`SceneCollisionGeometry.cs` / `CollectDetailed`** — new pass after the physics loop: enumerate entities with a `SiegeLadderSpawner` script (or raw-name regex `siege_ladder_\d+m_spawner`), read `OnWallNavMeshId` + global frame, emit synthesized ladder triangles tagged `faceGroupId=OnWallNavMeshId`. Skip (with a warning mirroring `OnCheckForProblems`) spawners whose id is -1/0 or ones-digit 1.
3. **`SteppedPropRamps.cs`** — group-id-aware entry point for spawner ladders (existing `TryLadderOffsetConnector`/`TryLadderChain` works on physics-derived geometry; the spawner path synthesizes from frame + prefab shape).
4. **`TiledNavMeshBuilder.cs`** — carry faceGroupId through tile detail generation; exempt marked faces from steep/tallSteep pruning consistently across tile borders + stitching.
5. Optional: **scene.xscene patcher** to auto-assign OnWallNavMeshId (next free id ≥ 2, ones digit ≠ 1, < 1000050) for spawners with -1.

## 4. Risks
- f1 semantics inferred from 3–4 vanilla files + code cross-refs (very strong, but not read out of the decompiled importer). Mitigation: in-editor validation below.
- Group-id collisions: author ids must avoid ≥ 1000050 and ones-digit 1; auto-assign must also avoid ids already present in the authored scene navmesh.
- ~75–90° ladder faces may trip the v2 builder's steep>75deg warn / tallSteep class — the marked-face exemption must be consistent across tiles and stitch/merge paths.
- Spawner frame → deployed geometry: the spawned ladder animates to the wall; the ghost prefab's Upper/Down meta-mesh positions may be the authoritative deployed frame — needs in-editor verification.
- The NMG9 edge extra int and the 260-byte trailing block semantics are partially unverified (NMG8 — what we write — is fully verified).
- Runtime-only spawn: bake-time synthesis must track prefab deployed-state drift across mods.

## 5. Validation plan
1. **Unit**: extend the own harness copy with a test writing an NMG8 with f1={1..7}; re-parse with `parse_nmg8_full.ps1` → identical field layout, zero gaps.
2. **Byte-diff regression**: write a prefab with all f1=0 → must be byte-identical to the current v1 output.
3. **In-editor**: bake CC_20_siege with the new writer + a test spawner OnWallNavMeshId=2; verify in the editor's navmesh tool that ladder-span faces carry group 2; `OnCheckForProblems` clean; play mode → bots climb (`MarkFacesWithIdAsLadder(2)` marks faces).
4. **Fixture**: re-run `--snapshot` on CC_20_siege → spawner entries appear with synthesized triangles + faceGroupId.
5. **Engine cross-check**: `SetAbilityOfFacesWithId(2, false)` on ladder drop disables the span (engine refuses only group 0).

## 6. Open user questions
1. Auto-assign OnWallNavMeshId and patch scene.xscene, or refuse to bake spawners whose ids are unset (author fixes in the editor)?
2. Auto-assign id scheme: per-ladder sequential (2, 3, 4, …) or ones-digit lane encoding mirroring the `% 10` side semantics?
3. Should `--ramps on`-collected `civilian_ladder_*` props also get group ids (engine-climbable) or stay unmarked walkable ramps?
4. Deployed geometry source: instantiate the prefab's `bo_siege_ladder_*m` shape at a computed deployed frame, or synthesize a simple plank ramp?
