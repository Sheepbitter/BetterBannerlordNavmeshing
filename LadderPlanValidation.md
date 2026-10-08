# Independent Validation Report — Ladder Spawner Claims

**Validator:** LongCat 2.5 Preview Free / opencode-go (model id `longcat-2.5-preview-free`)
**Date:** 2026-10-07
**Method:** All claims re-derived from scratch. Own Python parser written independently (no reuse of previous PowerShell parsers). All code/decompiled sources re-read and cross-checked.

---

## Claim 1: THE BREAKTHROUGH — f1 is the face group id

### Verdict: **CONFIRMED**

#### 1a. f1 = `PathFaceRecord.FaceGroupIndex`

**Evidence:**
- `PathFaceRecord` struct (decompiled `TaleWorlds.Library.dll`): `{ int FaceIndex; int FaceGroupIndex; int FaceIslandIndex; }` — the second field is the group index.
- Vanilla prefab f1 values **exactly match** engine code group-id references:
  - `siege_tower_12m_dnm.bin`: f1 = **{1,2,3,4,5,6,7}** — matches `SiegeTower.cs` `AttachNavigationMeshFaces(DynamicNavmeshIdStart + 1..7)` (lines 493–498, 312)
  - `batteringram_c_dnm.bin`: f1 = **{4,8}** — matches `MissionObject.cs` `AttachNavigationMeshFaces(DynamicNavmeshIdStart + 4)` (line 87) and `+ 8` (line 88); `BatteringRamSpawner.cs` `BridgeNavMeshID_1 = 8`
  - `siege_tower_warband_dnm.bin`: f1 = **{1,2,3,4}** — consistent with warband tower group usage
  - `mangonel_a_dnm.bin`: f1 = **{4}** — matches `MissionObject.cs` `+ 4` reference
- The probability of these exact matches occurring by chance if f1 were "level_cost_index" is negligible.

#### 1b. Face record layout = 25 + 8n bytes

**Evidence:** My independent Python parser (`nmg_parse.py`) verifies this for every face in all 4 files. For a triangle (n=3): 49 bytes; quad (n=4): 57 bytes. Arithmetic checks:
- `siege_tower_12m_dnm.bin`: 10 tri × 49 + 55 quad × 57 = 3625 bytes; facesStart=4520, facesEnd=8145 → 8145−4520 = 3625 ✓
- `batteringram_c_dnm.bin`: 10 quad × 57 = 570; facesStart=1460, facesEnd=2030 → 570 ✓
- `mangonel_a_dnm.bin`: 9 quad × 57 = 513; facesStart=820, facesEnd=1333 → 513 ✓
- `siege_tower_warband_dnm.bin`: 7 tri × 49 + 45 quad × 57 = 3048; facesStart=5060, facesEnd=7968 → 2908... wait, 7×49=343 + 45×57=2565 → 2908. 7968−5060=2908 ✓

#### 1c. Zero-gap parses

**Evidence:** All 4 files parse with **zero gaps** — parser consumes exactly the file size:
| File | Size | facesEnd | Trailing | Zero-gap |
|---|---|---|---|---|
| siege_tower_12m_dnm.bin | 8405 | 8145 | 260 | ✓ |
| batteringram_c_dnm.bin | 2290 | 2030 | 260 | ✓ |
| mangonel_a_dnm.bin | 1593 | 1333 | 260 | ✓ |
| siege_tower_warband_dnm.bin | 8228 | 7968 | 260 | ✓ |

All edge references valid, all vertex references valid, edge pad field all −1, trailing block = 4 + 32×8 = 260 bytes.

#### 1d. NMG9 edge record = 24 bytes

**Evidence:** `batteringram_c_dnm.bin` (NMG9) and `siege_tower_warband_dnm.bin` (NMG9) both parse with zero gaps using 24-byte edges. Arithmetic: 8 + 4 + 480 + 4 + 40×24 + 4 + 570 + 260 = 2290 ✓. The 6th int (extra field) = 0 in all NMG9 edges.

---

## Claim 2: OUR WRITER — f1 always 0, ladder machinery can never engage

### Verdict: **CONFIRMED**

#### 2a. Nmg8Writer.cs writes 0 for every face's field 1

**Evidence:** Three writer paths, all write 0 for field 1:
- `WriteFlat` line 114: `writer.Write(0);` (after `writer.Write(0);` for field 0 at line 113)
- `WriteInterleaved` line 290: `writer.Write(0);` (after `writer.Write((int)faceConcave[i]);` for field 0 at line 289)
- `Serialize→Write` line 482: `writer.Write(0);` (after `writer.Write(0);` for field 0 at line 481)

The comment at lines 286–287 / 478–479 labels field 1 as "level_cost_index" — this is **mislabelled**. The vanilla evidence (Claim 1a) proves field 1 is the face group id.

#### 2b. Both import paths pass shift 0

**Evidence:**
- `SceneNavMeshExporter.cs:565`: `scene.ImportNavigationMeshPrefab(prefabName, 0);`
- `TerrainNavMeshGenerator.cs:1243`: `scene.ImportNavigationMeshPrefab(prefabName, 0);`

Shift 0 means runtime group id = f1 + 0 = f1 = 0 for all faces.

#### 2c. `SetAbilityOfFacesWithId(0, ...)` is refused

**Evidence:** Native string **"Can not enable/disable navigation faces with group id 0"** found in both:
- `TaleWorlds.Native.dll` (Win64_Shipping_Client)
- `TaleWorlds.Native.dll` (Win64_Shipping_wEditor)

#### 2d. `MarkFacesWithIdAsLadder(X≠0)` marks nothing

**Evidence:** Since all faces have group id 0, no face carries any non-zero group id X. The engine's `MarkFacesWithIdAsLadder(X, true)` searches for faces with group id X — finds none.

#### 2e. Falsification attempt — any path for ladder climbing without non-zero group id?

**Searched:**
- `SiegeLadder.cs` — ladder machinery is entirely group-id-based (`MarkFacesWithIdAsLadder(OnWallNavMeshId, ...)`, `SetAbilityOfFacesWithId(OnWallNavMeshId, ...)`, `LadderQueueManager.Initialize(OnWallNavMeshId, ...)`)
- `LadderQueueManager.cs` — `FaceGroupIndex % 10` side detection (line 670), cost setting only when `ManagedNavigationFaceId > 1` (line 414)
- `SiegeTower.cs` — marks +5/+6/+7 as ladder (lines 686–688)
- `MissionObject.cs` — dynamic navmesh ids at ≥ 1000050, blocks of 50
- All decompiled siege/ladder/navmesh files — **no special handling of group id 0 found**

**Conclusion:** No fallback path exists. The engine's ladder machinery is fundamentally group-id-based, and group 0 is explicitly refused. **CONFIRMED** — the ladder machinery can never engage on our bakes.

---

## Claim 3: SPAWNER FACTS — SiegeLadderSpawner in CC_20

### Verdict: **CONFIRMED**

#### 3a. 5 SiegeLadderSpawner entities in CC_20 scene.xscene

**Evidence:** Regex scan of `D:\...\Modules\MyMaps\SceneObj\CC_20_siege\scene.xscene` found exactly **5** entities:
- 2× `siege_ladder_7m_spawner` — (1169.947,1138.738,102.329) rot z=1.915; (1169.555,1140.646,103.141) rot z=2.198
- 2× `siege_ladder_6m_spawner` — (1131.903,983.102,85.918) rot z=1.205; (1237.708,1138.579,102.196) rot z=1.875
- 1× `siege_ladder_8m_spawner` — (1139.228,996.402,82.804) rot z=1.299

All positions, rotations, and Upper/Down state degrees match the report exactly.

#### 3b. All 5 have `OnWallNavMeshId="-1"`

**Evidence:** Confirmed by reading each entity block. All 5 have `<variable name="OnWallNavMeshId" value="-1"/>`.

#### 3c. No physics shape → `HasPhysicsBody()` false

**Evidence:** None of the 5 entity blocks contain a `<physics>` element. The `SiegeLadderSpawner` decompiled code confirms it's a script entity (SpawnerBase-derived) with no physics shape.

#### 3d. Skipped at SceneCollisionGeometry.cs:210

**Evidence:** Line 210: `if (!e.HasPhysicsBody()) continue;` — confirmed. The spawner is skipped before classification. Only the raw entity `count` at line 206 sees it.

#### 3e. Ladder is a runtime-spawned separate prefab

**Evidence:** `SiegeLadderSpawner.cs` line 166: `firstScriptOfType.AssignParametersFromSpawner(...)` — the spawner creates a `SiegeLadder` instance at runtime. The `SiegeLadder` prefab (`props_siege_ladders.xml`) has the actual physics shape with `BodyFlags.Ladder`.

---

## Claim 4: Format notes — NMG9 edge size, RNM1 container

### Verdict: **CONFIRMED**

#### 4a. NMG9 edge record = 24 bytes

**Evidence:** Confirmed by zero-gap parse of `batteringram_c_dnm.bin` (NMG9) and `siege_tower_warband_dnm.bin` (NMG9) using 24-byte edges. The 6th int (extra field) = 0 in all edges.

#### 4b. RNM1 scene container

**Evidence:** `CC_20_siege\navmesh.bin`:
- File size: **7,689,594 bytes** — matches report
- Magic: **"RNM1"** at offset 0
- NMG9 magic at **offset 50** — matches report
- 4 bytes after NMG9 magic (offset 54): **144155** — matches report's "sublen=144155"
- The RNM1 container wraps one NMG9 blob — confirmed

---

## Summary Table

| Claim | Verdict | Key Evidence |
|---|---|---|
| 1. f1 = face group id | **CONFIRMED** | Own parser: f1 values match engine code group refs exactly |
| 1. Face layout 25+8n | **CONFIRMED** | Arithmetic verified for all 4 files |
| 1. Zero-gap parses | **CONFIRMED** | All 4 files parse with zero gaps |
| 1. NMG9 edge = 24B | **CONFIRMED** | Zero-gap parse with 24-byte edges |
| 2. Writer writes f1=0 | **CONFIRMED** | 3 writer paths all write 0 at lines 114/290/482 |
| 2. Import shift=0 | **CONFIRMED** | Both paths pass 0 |
| 2. Group 0 refused | **CONFIRMED** | Native string in TaleWorlds.Native.dll |
| 2. No fallback path | **CONFIRMED** | Exhaustive search of decompiled code |
| 3. 5 spawners in CC_20 | **CONFIRMED** | Regex scan: exactly 5, all OnWallNavMeshId="-1" |
| 3. No physics shape | **CONFIRMED** | No `<physics>` in any spawner block |
| 3. Skipped at line 210 | **CONFIRMED** | `if (!e.HasPhysicsBody()) continue;` |
| 3. Runtime-spawned ladder | **CONFIRMED** | SiegeLadderSpawner creates SiegeLadder at runtime |
| 4. NMG9 edge 24B | **CONFIRMED** | Zero-gap parse |
| 4. RNM1 container | **CONFIRMED** | Magic "RNM1", NMG9 at offset 50, sublen=144155 |

## What I Could Not Validate

- **In-editor behavior**: Cannot run the Bannerlord editor to verify that baked navmesh with f1=0 actually refuses ladder marking at runtime. The evidence is code-level only.
- **The exact semantics of f0**: The warband tower has f0={0,1,2}, which is inconsistent with f0 being a simple boolean is_concave. This suggests f0 may be a bitmask or the writer's comment about f0 is also incomplete. This does not affect the f1=group id claim.
- **The 260-byte trailing block semantics**: Confirmed structurally (4 + 32×8 = 260) but the meaning of the 32 level groups is not fully decoded.
- **NMG4/NMG5/NMG7 formats**: Not parsed (not needed for this validation).

## Artifacts

- Own parser: `C:\Users\Sheepbitter\AppData\Local\Temp\opencode\ladder_validate\nmg_parse.py`
- This report: `C:\Users\Sheepbitter\AppData\Local\Temp\opencode\ladder_validate\VALIDATION_REPORT.md`
