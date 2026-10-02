# Navmesh prefab lookup: what the editor expects, and what we do about it

This file documents the `No navigation mesh prefab was found with name "<name>"!`
message, where it comes from, why it appears, and what this module does to avoid
it. It is a standalone reference; `README.md` is maintained separately.

* Scope verified: the **editor** build only,
  `D:\Secondary Steam\steamapps\common\Mount & Blade II Bannerlord\bin\Win64_Shipping_wEditor\`
  (`TaleWorlds.Native.dll` + `TaleWorlds.Engine.dll` + `TaleWorlds.MountAndBlade.dll`).
* Managed code lives in `NavMeshPrefabIndex.cs` (new) and is called from
  `TerrainNavMeshGenerator.WriteAndImport` and `SceneNavMeshExporter`.

---

## 1. The message is the engine's, not ours

It does not exist in any file in this repository. In the editor build it lives in
`TaleWorlds.Native.dll` and is emitted by the native body behind
`TaleWorlds.Engine.IScene.LoadNavMeshPrefab`:

```csharp
// TaleWorlds.Engine.IScene            (bin\Win64_Shipping_wEditor\TaleWorlds.Engine.dll)
// Token: 0x06000498 RID: 1176
[EngineMethod("load_nav_mesh_prefab", false, null, false)]
void LoadNavMeshPrefab(UIntPtr scenePointer, string navMeshPrefabName, int navMeshGroupIdShift);

// TaleWorlds.Engine.Scene
// Token: 0x06000B9B RID: 2971
public void ImportNavigationMeshPrefab(string navMeshPrefabName, int navMeshGroupShift)
{
    EngineApplicationInterface.IScene.LoadNavMeshPrefab(base.Pointer, navMeshPrefabName, navMeshGroupShift);
}
```

The exact rendered message is:

```
No navigation mesh prefab was found with name "<name>"!
```

(47-character prefix `No navigation mesh prefab was found with name "`, then the
name, then `"!`.)

---

## 2. Managed call chain that can request a prefab by name

`FindCallers` on the **editor** `TaleWorlds.MountAndBlade.dll` gives exactly two
callers of `Scene.ImportNavigationMeshPrefab`, and zero callers of
`ImportNavigationMeshPrefabWithFrame` and `SaveNavMeshPrefabWithFrame`:

```
MissionObject.OnInit()                        RID 8740
  -> if (!GameNetwork.IsClientOrReplay)
       AttachDynamicNavmeshToEntity();

MissionObject.AttachDynamicNavmeshToEntity()   RID 8741
  if (this.NavMeshPrefabName.Length > 0) {
      this.DynamicNavmeshIdStart = Mission.Current.GetNextDynamicNavMeshIdStart();
      base.GameEntity.Scene.ImportNavigationMeshPrefab(this.NavMeshPrefabName, this.DynamicNavmeshIdStart);
      ... AttachNavigationMeshFaces(DynamicNavmeshIdStart + n, ...)
  }

SiegeTower.AttachDynamicNavmeshToEntity()      RID 12408   (override, same shape)
```

`MissionObject.NavMeshPrefabName` is a **public string field**, read at IL_0001 and
IL_0030 of both overrides and written only by `MissionObject..ctor` (IL_0006). Its
value comes from the prefab XML, e.g.

```xml
<script name="Ballista">
  <variables>
    <variable name="NavMeshPrefabName" value="ballista_a_dnm"/>
  </variables>
</script>
```

So there are exactly **three** candidates for "who asks for a prefab by name". Only
the first two can produce the message, and only the first fires while a scene is
merely open in the editor:

| # | Caller | When |
|---|--------|------|
| 1 | `TerrainNavMeshGenerator.WriteAndImport` -> `ImportNavigationMeshPrefab(prefabName, 0)` | our Export / Import Only button |
| 2 | `MissionObject.AttachDynamicNavmeshToEntity` | a **mission** starts or an entity with a non-empty `NavMeshPrefabName` is created (siege props, mangonels, ballistas, siege towers) |
| 3 | the editor's own navmesh inspector ("Import/Export faces as navigation mesh prefab") | native Qt UI, `NavigationMeshInspector`; **cannot** produce the message - see below |

**Case 3 was previously listed here as a possible source. It is not.** On the
on-disk editor image the whole message literal (rva `0x14107E0`) has exactly
**two** reference sites in `.text`, and both are inside the loader pair
`[0x9795F0,0x979D72)` / `[0x979D80,0x97A49F)` (sites `0x979AC9`, `0x97A1F8`) -
the bodies behind `IScene.LoadNavMeshPrefab` / `LoadNavMeshPrefabWithFrame`. No UI
function references it, so no editor panel can print it. The panel cannot reach it
indirectly either:

* The inspector function `[0x8D9580,0x8DDE38)` (it references
  `...\MB3QT\Inspector\NavigationMeshInspector.cpp`) holds `Nav. Mesh Prefab name: `,
  `Import faces as navigation mesh prefab`, `Export faces as navigation mesh
  prefab`, its own Qt `File not found`, and `Please select a module path for the
  nav mesh prefab:` - but not the message, and not `file not found: %s` either
  (that literal, rva `0x1410810`, also has exactly four reference sites, all in
  the same loader pair).
* The editor's own *export* helper `[0x97A4A0,0x97AD8E)` does assert that the index
  exists and does log `file not found: %s`, but it never references the message
  literal either.
* Nothing in the read path can **insert**: the only function that references the
  `NavMeshPrefabs` literal (rva `0x13C3CB8`) is the index builder of §3.4
  (`[0x4EBCE0,0x4ECA2B)`, single site `0x4EBED8`). The read-side accessor is the
  0x6A-byte getter at `0x4E7580`, which references nothing but the
  `modules_of_nav_mesh_prefabs` assert pair.

The two "prefab" buttons (`Export faces as prefab`, `Import faces from prefabs`,
and the whole `Debugging Tools` menu) are in a **different** function,
`[0x8DF6C0,0x8E26EE)`: that is the navmesh *tools/menu builder*, a table of menu
labels for the inspector, and it never touches the prefab index at all. Pairing
it with case 3 was the mistake this section used to make.

Case 2 needs a running `Mission`, so it does not fire while a scene is merely open
in the editor. **In the editor the message therefore comes from case 1 - our own
`ImportNavigationMeshPrefab` call.** That makes case 1 the only source this module
owns, and it is fully self-contained.

---

## 3. What the native loader actually does

Verified in Ghidra on the editor native DLL (see §6 for the exact image identity).

### 3.1 Name lookup is an in-memory index, not a disk scan

`FUN_7ff8d070ec50(longlong scene, void* name, uint groupIdShift)`
(the `load_nav_mesh_prefab` body; `FUN_7ff8d070f3d0` is the `..._with_frame`
twin - both xref the same message string at `7ff8d11a2f10`):

```c
if (DAT_7ff8d1673160 == 0)                      // registry not built yet
    rgl_assert("rglModule_data::modules_of_nav_mesh_prefabs",   // rglModule_data.cpp:0x82
               "C:\\BuildAgent\\work\\mb3\\Source\\Engine\\Rgl\\rglModule_data.cpp", 0x82, ...);

puVar18 = (undefined8 *)DAT_7ff8d1673160[2];    // tree root node
puVar9  = DAT_7ff8d1673160;                     // sentinel
do {
    if (nameLength == 0) { bVar2 = false; next = node[1]; }
    else if (node_length /* node+0x30 */ == 0) { bVar2 = true; ... }
    else {
        pbVar13 = name_ptr;
        do {                                   // byte-wise compare, case SENSITIVE
            uVar15 = pbVar13[node_keylen - name_ptr];
            iVar8  = *pbVar13 - uVar15;
            if (*pbVar13 != uVar15) break;
            pbVar13++;
        } while (uVar15 != 0);
        bVar2 = 0 < iVar8;
    }
    ...
} while (next != 0);

if (found == DAT_7ff8d1673160 || (moduleIndex = *(int*)(found + 8)) < 0) {
    // ---- MISS: build and print the message ----
    local_1c0 = 0x2f;                          // 47 = len("No navigation mesh prefab was found with name \"")
    memcpy(prefix, s_No_navigation_mesh_prefab_was_fo_7ff8d11a2f10, 0x2f);
    memcpy(prefix + 0x2f, param_2, strlen(param_2) + 1);
    append 2 bytes DAT_7ff8d11200cc             // "\"!"
    rgl_log(...)
    return;
}
```

* The key is the **file-name stem**, compared with `memcmp` -> **case sensitive**,
  exact.
* The value is an `int32` module index; bounds-checked against
  `*(module_data + 0x890) - *(module_data + 0x888)) >> 5` and used as
  `index * 0x20 + *(module_data + 0x888)` - the loaded-module array, 32-byte stride.
* A miss prints **only** the prefab message. `file not found: %s` is the *other*
  branch (index hit, file gone), so seeing the prefab message proves the **name
  is not a key in the index**.

### 3.2 The path it composes

```c
local_e8 = rglConstString<128>::vftable;          // <-- 128-byte fixed buffer
if (0x7f < nameLength)
    rgl_assert("str_length < size", "rglConstString.inl", 0x25, ...);   // name must be < 128
strcpy_s(local_d0, 0x80, namePtr);                 // name copied, TRUNCATED at 128

tail = FUN_7ff8d00ab330(&local_130, &local_e8, scene->module_path_ /* scene+0x2b20 */);
FUN_7ff8d0b9cbe0(moduleEntry, &local_150, tail);   // join module path + tail
f = fopen(local_150, DAT_7ff8d112467c /* "rb" */);
if (!f) rgl_log("file not found: %s", local_150);
```

`FUN_7ff8d00ab330` (the only user of the `NavMeshPrefabs/` literal) builds:

```c
*out = "NavMeshPrefabs/";                 // 0xf = 15 chars, s_NavMeshPrefabs__7ff8d1135c58
tmp  = modulePath + out;                  // FUN_7ff8d0b9cae0(A,B) == A + B
append(out, name);                        // FUN_7ff8d0b9d2b0(out, name)
append(out, ".bin" + '\0');               // 4 bytes from _DAT_7ff8d112b30c + 1 NUL
```

=> the engine opens exactly

```
<Modules\<Module>\>\NavMeshPrefabs\<name>.bin
```

**That is precisely where we already write.** No layout change is needed.

Two further native facts:

* **Name length must be < 128.** The name is copied through
  `rglConstString<128>` / `strcpy_s(..., 0x80, ...)`. A 128-character-or-longer
  name is truncated before the lookup and can never equal a file stem.
* **The index itself is capped at 512** per file name
  (`iVar11 < 0x201` in the index builder, next to
  `Navigation mesh prefab file name length cannot be greater than 512!`).

### 3.3 The `NavMeshPrefabs/` + `SceneEditData/` + `/navmesh.bin` cluster is three unrelated paths

This is worth stating explicitly, because the three literals sit next to each
other in `.rdata` and look like one composed path. They are not - they belong to
three different functions:

| Literal | Function | Composes |
|---|---|---|
| `NavMeshPrefabs/` (0x7ff8d1135c58) | `FUN_7ff8d00ab330` | `<module>\NavMeshPrefabs\<name>.bin` |
| `SceneObj/` + `/navmesh.bin` (0x7ff8d1129500 / 0x7ff8d1135c48) | `FUN_7ff8d00ab1f0` | `<module>\SceneObj\<scene>\navmesh.bin` |
| `SceneEditData/` (0x7ff8d1135ba8) | `FUN_7ff8d00ab450` | stores two path globals: `SceneObj/<scene>` and `<module>\SceneEditData\<scene>` |

`FUN_7ff8d00ab450` appends nothing itself; the edit-data folder is where
`%s/terrain_ed.bin` lives (string 0x7ff8d11359c8-region, observed on disk as
`Modules/MapConverter/SceneEditData/scn_village_Boras/terrain_ed.bin`).

`SceneObj\<scene>\navmesh.bin` is the **scene's own** baked navmesh (748 such
files in this install). Its on-disk magic is `RNM1`
(`52 4e 4d 31 ...`), whereas a navmesh *prefab* bin is a 4-byte length prefix
followed by `NMG8` - **different formats**, so our NMG8 prefab must never be
written there.

### 3.4 When the index is built

`FUN_7ff8d0285180()` - the index builder:

```c
if (DAT_7ff8d1673160 == 0) DAT_7ff8d1673160 = new_tree(0x30, 8, 3);
else                       clear_tree(DAT_7ff8d1673160);          // always rebuilt

module_manager = *DAT_7ff8d166ecf0;              // rglEngine_interface.h:0x141
count = (*(manager + 0x890) - *(manager + 0x888)) >> 5;         // loaded modules
for (i = 0; i < count; i++) {
    entry = manager[0x888] + i * 0x20;
    folder = modulePath(entry) + "NavMeshPrefabs";              // 0xe = 14 chars
    rglConstString<16> ext = ".bin";
    list = list_directory(folder, ext);                         // 32-byte stride
    for (each file) {
        if (fileNameLength < 0x201)                             // <= 512
            insert(stem_without_extension(file) -> i);          // -> the tree
    }
}
```

Its caller chain is engine/module bring-up, not scene load and not import:

```
FUN_7ff8d08bd990  ->  FUN_7ff8d08bc880  ->  FUN_7ff8d0285180
```

`FUN_7ff8d08bc880` is a module-registration routine (`module_manager`
singleton, `FUN_7ff8cff9bcb0(module_manager, 1)`, `FUN_7ff8cff9ed50(module_manager, id)`
to look a module up by id, `iVar6 != -1` -> register it). So the index is
**rebuilt from the file system during engine/module (re)load**. Consequences:

1. A `NavMeshPrefabs` folder that did not exist at that moment is invisible for
   the whole session.
2. A prefab **name** that was not in a `*.bin` stem at that moment can never be
   resolved in that session, however many times the `.bin` is rewritten.
3. Rewriting an **existing** bin is fine - same name, same module, still a key.

There is no managed API that re-runs it (`Reload`-shaped members in
`TaleWorlds.Engine` are all `Preload*`/terrain-reload; `TaleWorlds.ModuleManager`
exposes only `ModuleHelper` accessors).

---

## 4. Root causes, ranked

| # | Cause | Who can fix it |
|---|-------|----------------|
| A | Our own `ImportNavigationMeshPrefab` called with a name the index has never seen (first export of a new scene, or the target module had no `NavMeshPrefabs` folder until this run) | **us** |
| B | The derived prefab name is >= 128 characters, so the engine truncates it and can never match the file stem | **us** |
| C | The bin sits in a module that was not in the loaded-module list when the index was built | partly us (folder resolution) |
| D | A mission object's `NavMeshPrefabName` (siege prop prefabs) names a prefab no loaded module provides | not us - prefab XML / module set |

D cannot happen while merely editing a scene (it needs a `Mission`).

---

## 5. What this module now does

### 5.1 `NavMeshPrefabIndex.cs` (new)

A managed mirror of the native index, used only for logging and for deciding
whether an import call is worth making.

* `ValidateName(name)` - empty / path separator / invalid char / **`>= 128`
  characters** / over the 512-char file-name cap.
* `DeriveBoundedName(sceneName, suffix)` - if `scene + "_navmesh"` exceeds 127
  characters it returns `scene[0..keep] + "~" + 8 hex digits` of an FNV-1a hash
  of the full original, always exactly <= 127, stable across runs and machines.
* `FindCandidates(log, name)` - every `Modules/<M>/NavMeshPrefabs` visible from
  the game install, with `FolderExists` / `BinExists` / `ModuleActive`. Game
  roots come from `TerrainNavMeshGenerator.GetGameRootCandidates()` **and** from
  the folders the loaded `TaleWorlds.*` assemblies came from
  (`.../bin/Win64_Shipping_wEditor/` -> its parent), so the scan works even if
  our assembly is relocated. `ModuleActive` uses
  `TaleWorlds.ModuleManager.ModuleHelper.IsModuleActive` by reflection and
  degrades to `unknown`.
* `DescribeLookup(log, name, folderUsed)` - the log block quoted below.
* `ExplainUnresolvable(log, name, folder, folderExisted, binExistedBefore)` -
  `null` when the import is worth attempting, otherwise a one-sentence reason.

### 5.2 `TerrainNavMeshGenerator.WriteAndImport`

Before anything touches disk it snapshots the two facts that decide
resolvability, then writes the bin, then decides:

```csharp
bool folderExisted    = Directory.Exists(folder);
string binPath        = Path.Combine(folder, prefabName + ".bin");
bool binExistedBefore = File.Exists(binPath);
Directory.CreateDirectory(folder);
NavMeshPrefabIndex.DescribeLookup(log, prefabName, folder);
... File.WriteAllBytes(binPath, mesh.Bin) ...

if (importIntoScene) {
    string unresolvable = NavMeshPrefabIndex.ExplainUnresolvable(
        log, prefabName, folder, folderExisted, binExistedBefore);
    if (unresolvable != null) {
        result.ImportSkipReason = unresolvable;
        // no ClearNavMesh, no ImportNavigationMeshPrefab -> no engine message
    } else {
        ... unchanged clear + ImportNavigationMeshPrefab(prefabName, 0) ...
    }
}
```

`binExistedBefore == true` is the necessary condition for the name being a key;
when it is false the engine message is guaranteed, so the call is skipped and the
reason is logged instead. The `.bin` is still written, so the work is never lost:
after the index is rebuilt the very next run imports it.

`ResolvePrefabName` now returns `DeriveBoundedName(sceneName, "_navmesh")` for the
derived default, which closes cause B. Names that already fit are unchanged, so
**every already-exported bin stays valid**. An explicit `NavMeshPrefabName`
override is still honoured verbatim (with a warning if it is unusable).

### 5.3 `SceneNavMeshExporter`

* `ImportOnlyFromEditor` runs the same resolvability check before calling
  `ImportNavigationMeshPrefab`.
* Both "import skipped" sites now print `result.ImportSkipReason` when set.
* `LogImportCheck`'s warning text was corrected: it used to claim the prefab list
  is built at startup in general, which is wrong for a rewrite of an existing
  prefab.

### 5.4 `build.ps1`

Creates `Modules\BetterBannerlordNavmeshing\NavMeshPrefabs` (plus a `README.txt`
explaining why it must pre-exist) at deploy time, so our module is in the index
from the first editor start instead of from the first export. A `.txt` is not a
`*.bin`, so it registers nothing.

### 5.5 Sample log output

```
prefab index: folder=D:\...\Modules\MyMaps\NavMeshPrefabs folderExistedBefore=True binExistedBefore=False
prefab index: name='mp_tdm_Birtu_v2_navmesh' length=23 ok
prefab index:   FOUND    D:\...\Modules\MyMaps\NavMeshPrefabs\mp_tdm_Birtu_v2_navmesh.bin module=MyMaps moduleActive=True  <- this run
prefab index:   absent   D:\...\Modules\Native\NavMeshPrefabs\mp_tdm_Birtu_v2_navmesh.bin module=Native moduleActive=True
import SKIPPED (the engine cannot resolve this prefab name in this session): no D:\...\mp_tdm_Birtu_v2_navmesh.bin existed before this run, ...
import SKIPPED: the .bin at D:\...\mp_tdm_Birtu_v2_navmesh.bin is correct; it becomes usable once the engine's prefab index has been rebuilt.
```

---

## 6. Evidence and provenance

**Managed** - dnSpy, decompiled from
`...\bin\Win64_Shipping_wEditor\TaleWorlds.Engine.dll` and
`...\bin\Win64_Shipping_wEditor\TaleWorlds.MountAndBlade.dll`
(both explicitly opened with `Open_Assembly`, so these are the **editor**
variants, not the client ones that happened to be pre-loaded):

* `IScene.LoadNavMeshPrefab` RID 1176, `[EngineMethod("load_nav_mesh_prefab", false, null, false)]`
* `IScene.LoadNavMeshPrefabWithFrame` RID 1177, `IScene.SaveNavMeshPrefabWithFrame` RID 1178
* `Scene.ImportNavigationMeshPrefab` RID 2971 -> `LoadNavMeshPrefab`
* `FindCallers(Scene.ImportNavigationMeshPrefab)` -> `MissionObject.AttachDynamicNavmeshToEntity`,
  `SiegeTower.AttachDynamicNavmeshToEntity` (and, in a third-party module,
  `FullInvasion3.SceneScripts.FI3_MobileSpawn.OnInit`)
* `FindCallers(ImportNavigationMeshPrefabWithFrame)` / `(SaveNavMeshPrefabWithFrame)` -> none
* `Find_Field_References(MissionObject.NavMeshPrefabName)` -> only `..ctor` (write) and the two
  `AttachDynamicNavmeshToEntity` overrides (read)

**Native** - Ghidra, against the editor native DLL loaded in the tool
(image base `0x7ff8cfda0000`; `.text` `+0x1000..+0x2815FF`, `.rdata`
`+0x1283000`, `.pdata` `+0x9BD000`). Caveat worth recording: that image's section
layout does **not** match
`...\bin\Win64_Shipping_wEditor\TaleWorlds.Native.dll` as it sits on disk
(26,269,144 bytes, sha256 `b87f2439e0869945e6aa4a0ada3d8ee735d6e12276aecfea84dce44379243413`,
`.rdata` at `+0x1290000`), so the Ghidra image is a different editor-family build.
The navmesh code path was therefore cross-checked a second time by parsing the
on-disk editor file directly, which reproduced the same structure: message string
at file `0x140F7E0`, loader at rva `[0x9795F0,0x979D72)`, prefab path builder at
`[0x310DB0,0x310ECA)`, scene navmesh path builder at `[0x310C70,0x310DA4)`,
scene path globals at `[0x310ED0,0x3112A1)`, index builder at `[0x4EBCE0,0x4ECA2B)`
with its caller chain `[0xB28490,0xB28DF9)` -> `[0xB295A0,0xB29D08)`.

Key Ghidra addresses used above:

| What | Address in the Ghidra image |
|---|---|
| message string | `7ff8d11a2f10` |
| `"file not found: %s"` | `7ff8d11a2ae8` |
| loader (`load_nav_mesh_prefab`) | `FUN_7ff8d070ec50` |
| loader twin (`..._with_frame`) | `FUN_7ff8d070f3d0` |
| `NavMeshPrefabs/` literal | `7ff8d1135c58` |
| prefab path builder | `FUN_7ff8d00ab330` |
| `SceneObj/` literal | `7ff8d1129500` |
| `/navmesh.bin` literal | `7ff8d1135c48` |
| scene navmesh path builder | `FUN_7ff8d00ab1f0` |
| `SceneEditData/` literal | `7ff8d1135ba8` |
| scene path globals builder | `FUN_7ff8d00ab450` |
| `NavMeshPrefabs` literal (no slash) | `7ff8d1156530` |
| index builder | `FUN_7ff8d0285180` |
| index builder's caller | `FUN_7ff8d08bc880` |
| index registry global | `DAT_7ff8d1673160` |
| module list singleton | `DAT_7ff8d166ecf0` |

**On disk** (`...\Modules`, this install):

* `NavMeshPrefabs/<name>.bin` in 9 module folders (Native, SandBoxCore, SandBox,
  MyMaps, MapConverter, Full-Invasion-3, "Full Invasion 3 mapping",
  DedicatedCustomServerHelper, NotInUse/Full Invasion 3 0.4) - flat, matching the
  loader.
* `SceneObj/<Scene>/navmesh.bin` - 748 files, magic `RNM1`.
* prefab bins, magic `NMG8` behind a 4-byte length prefix.
* all 25 distinct non-empty `NavMeshPrefabName` values referenced by module XML
  have a matching `.bin` on disk.
* `Modules/BetterBannerlordNavmeshing` had **no** `NavMeshPrefabs` folder, which is
  cause A waiting to happen; `build.ps1` now creates it.

---

## 7. Still needs the editor to confirm

* That pressing Export on a never-before-exported scene now logs
  `import SKIPPED` (instead of printing the engine message) and that the *next*
  editor start, followed by Import Only, imports it.
* Whether any remaining occurrence the user sees comes from cause D (a mission
  object's `NavMeshPrefabName`) - that would show up while a mission runs, not
  while editing, and needs the full name from the message plus the log file.
* `moduleActive` accuracy depends on `TaleWorlds.ModuleManager.ModuleHelper`
  being initialised; it reports `unknown` outside a running game/editor.