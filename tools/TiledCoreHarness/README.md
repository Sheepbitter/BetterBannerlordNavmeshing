# TiledCoreHarness — standalone acceptance suite for the navmesh core

`Program.cs` is the project's acceptance matrix: 29 tests (plus a final doorway sweep) that exercise
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

**Full suite** — no arguments. Runs Test 1..29 then the final doorway sweep. ~45 s on a 12-core box,
exit code 0; redirect the output to a file, the suite is verbose (the reference log is ~125 KB).

```
TiledCoreHarness.exe > suite.txt
```

| Mode | What it does |
| --- | --- |
| *(no args)* | Full 29-test acceptance suite + doorway sweep |
| `--test11` | Test 11 only (multi-level assets). For triage after a tile/contour change |
| `--test25` | Test 25 only (ladder slope exemption). The suite's one known red - see *Known documented failure* |
| `--extractstair <terrain.bin> <assets.bin> [--hassteps] [--minimal]` | Real-prop CC_20 staircase extraction: pulls the six props around (1119,1079), adds ground at z=112.98 and a top landing, builds, prints component/slope stats and writes `%TEMP%\stair_extract_nav.obj` |
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
`--ledge`, `--detail`, `--plate`, `--ramps`, `--gate`, `--cleanup`, `--steepguard`, `--steepmargin`,
`--cullwater`, `--raisewater`) plus the inspection
switches (`--probe`, `--vmesh`, `--vprobe`, `--pinch`, `--flood`, `--walk`, `--soup`, `--scan`,
`--census`, `--defect`, `--ladders`, `--props`, `--routing`, `--quality`, `--quiet`, `--xzprobe`,
`--connect`). Run it with a bad prefix to get the one-line usage summary.

`--ramps`, `--gate` and `--cleanup` take an **optional** `on`/`off` value. A bare flag means on, and
the next token is consumed only when it is literally `on` or `off`, so `--cleanup --quality` is a
cleanup build plus the quality report, not a silently swallowed `--quality` (the V3.61 log had the
latter bug).

**Water level** (`--cullwater M` = Mode A cull, `--raisewater M` = Mode B raise): both compare the
**lifted** height (`soupY + HeightOffsetMeters`) and act on **floor-like triangles only** (Recast-space
upward normal >= `cos(MaxSlopeDegrees)`) - wall/obstacle geometry is never culled or clamped. A build
with either flag also prints a `shoreline:`
line classifying faces by height around the level (`sheetFaces`, `submergedFaces`, `transitionFaces`,
`transitionWalkable`, `transitionMaxSlope`) so the deep/shallow boundary can be judged. No flag = no water
handling (byte-identical, the goldens).

## Known documented failure

`LADDER-CAP TEST: FAIL` in Test 25 is a **real top-landing defect**, not a general ladder failure. The
user confirms ladders on Basamu work; on Birtu the failure is the **small doorway at the ladder's
top**. Case (c) is the synthetic analogue. Check the shipped ladders with:

```
TiledCoreHarness --snapshot <Birtu> --ramps on --coverage civilian_ladder
```

Baseline: `civilian_ladder_5m` (the doorway case) and `civilian_ladder_6m` both report 0% coverage.
`--ramps` defaults to **off**, so a plain snapshot run does not exercise the feature. The chain's top
apron is only 0.5 cell and cannot cross the threshold; the experiment branch `exp/ladder-raster` adds
8-direction landing pads (6 m reaches 78.4%, 5 m still 0% because its top is sealed by ruin cubes in
the snapshot). The `european_castle_ladder` asset is an unflagged staircase and never enters this path.
Mechanism, experiments and next steps are in `TODO.md` (ladder-chain item).


Other `FAIL` strings in the log are *informational per-scenario* rows, not suite verdicts: Test 13
prints `FAIL` for each narrow-feature scenario at the shipped `cs=0.5` config and then reports
`CONNECTIVITY LAB: PASS` once the recommended `cs=0.25` config connects 5/5. Likewise `failed=0`
tile counts are substring noise, not failures. Judge the suite by the per-test `RESULT:` / `... TEST:`
verdict lines, not by a grep for `FAIL`.

## Env knobs

`HARNESS_PLATE=off` disables the Test 23 gate-plate blend (the `GatePlateBlend` setting).