# TiledCoreHarness — standalone acceptance suite for the navmesh core

`Program.cs` is the project's acceptance matrix: 27 tests (plus a final doorway sweep) that exercise
`TiledNavMeshBuilder`, `SteppedPropRamps`, `NavMeshCleanup`, `Nmg8Writer` and `SceneSnapshot`
**with zero TaleWorlds references**. That isolation is the design proof — the voxel/recast core is
plain C# that builds and runs outside the game.

The harness links those sources in by relative path (`..\..\*.cs`) plus the vendored DotRecast
subset, so it always compiles the *current* core. It is not part of the module DLL: the module
project excludes `tools\**\*.cs` from its default compile glob.

## Build

```
dotnet build tools\TiledCoreHarness\harness.csproj -c Release
```

Output: `tools\TiledCoreHarness\bin\Release\net472\TiledCoreHarness.exe` (net472 / x64 /
C# 7.3). The harness has no TaleWorlds dependency, so no `-p:GameDir=` is needed.

## Run modes

Run from the exe's directory (or anywhere — the modes that read snapshot files use relative paths
against the current directory).

**Full suite** — no arguments. Runs Test 1..27 then the final doorway sweep. ~45 s on a 12-core box,
exit code 0; redirect the output to a file, the suite is verbose (the reference log is ~125 KB).

```
TiledCoreHarness.exe > suite.txt
```

| Mode | What it does |
| --- | --- |
| *(no args)* | Full 27-test acceptance suite + doorway sweep |
| `--quadtest` | Test 27 only (quad merge + NMG8 writer round trip). Seconds; good smoke test |
| `--barriers` | Barrier honour-mask rule check (`BarrierBlocksAgents` over the four engine masks) |
| `--barriercase` | The CC_20_siege barrier regression case: builds the snapshot twice, barriers off vs on, and compares the mesh at the user's crossing |
| `--snapshot` | Recorded-scene mode: rebuilds a `.bin` snapshot through the real heightfield path with setting overrides |

### `--snapshot`

```
TiledCoreHarness --snapshot <prefix> [--pair "x1,y1|x2,y2"]... [setting overrides]
TiledCoreHarness --snapshot <terrainBin> <assetsBin> <outTxt> [same options]
```

**Snapshot prefix convention:** pass the name *without* the `_snapshot_` infix. A prefix `P` is
expanded to `P_snapshot_terrain.bin` / `P_snapshot_assets.bin` / `P_snapshot.txt`, so the file named
`CC_20_siege_navmesh_snapshot_terrain.bin` is loaded with `--snapshot CC_20_siege_navmesh`.

Setting overrides (`--cs`, `--radius`, `--climb`, `--slope`, `--gapfill`, `--stitch`, `--prune`,
`--ledge`, `--detail`, `--plate`, `--ramps`, `--gate`, `--cleanup`, `--steepguard`, `--steepmargin`) plus the inspection
switches (`--probe`, `--vmesh`, `--vprobe`, `--pinch`, `--flood`, `--walk`, `--soup`, `--scan`,
`--census`, `--defect`, `--ladders`, `--props`, `--routing`, `--quality`, `--quiet`, `--xzprobe`,
`--connect`). Run it with a bad prefix to get the one-line usage summary.

## Known documented failure

`LADDER-CAP TEST: FAIL` in Test 25 (ladder flag exempt from the synthesis slope cap) is the one
**known, accepted** failure. Not a regression.

Other `FAIL` strings in the log are *informational per-scenario* rows, not suite verdicts: Test 13
prints `FAIL` for each narrow-feature scenario at the shipped `cs=0.5` config and then reports
`CONNECTIVITY LAB: PASS` once the recommended `cs=0.25` config connects 5/5. Likewise `failed=0`
tile counts are substring noise, not failures. Judge the suite by the per-test `RESULT:` / `... TEST:`
verdict lines, not by a grep for `FAIL`.

## Env knobs

`HARNESS_PLATE=off` disables the Test 23 gate-plate blend (the `GatePlateBlend` setting).