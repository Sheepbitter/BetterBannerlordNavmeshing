# Tile-Size Sweep Report (16 / 14 / 12 / 10 / 8 / 6 m)

Date: 2026-10-08. Harness: `tools/TiledCoreHarness` (whole-map replay; the walk-area border clamp is not
applied offline). Command: `TiledCoreHarness --snapshot <fixture> --quality --cleanup on --tile <N>`.
Settings: cs=0.25, radius=0.25, slope=50, climb=0.6 (auto), gapFill=2, stitch=6, prune=400, detail on,
`contourMaxError` 2.0 vox, cleanup on.

Fixtures: Birtu (09-30), Tasksatu (10-08 dump), Na'ilu (10-08 dump), Basamu (09-30), CC_20 v2 (10-06 dump,
staged as `CC_20_v2`; the plain `CC_20_siege_navmesh` in the harness dir is the stale v1). Fixture identity
cross-checked against the golden notes: CC_20 v2 t16 F=235660 and Basamu t16 F=482993 match exactly.

Raw logs + `sweep_table.csv`: `C:\Users\Sheepbitter\AppData\Local\Temp\opencode\tile_sweep\` (the CSV is
also committed as `TileSweepTable.csv`).

## Results

### Birtu
| tile | buildMs | faces | needles | tiny | lt>4 | lt>8 | meanQ | islands | cln refine ms |
|------|---------|-------|---------|------|------|------|-------|---------|---------------|
| 16 | 4516 | 42283 | 6 | 0 | 4033 | 564 | 0.7330 | 4 | 211 |
| 14 | 4475 | 42089 | 4 | 0 | 3901 | 554 | 0.7333 | 5 | 216 |
| 12 | 4420 | 42096 | 5 | 0 | 3518 | 465 | 0.7378 | 4 | 170 |
| 10 | 4333 | 43656 | 2 | 0 | 3710 | 415 | 0.7397 | 5 | 155 |
| 8  | 4388 | 46549 | 4 | 0 | 3765 | 384 | 0.7427 | 5 | 154 |
| 6  | 4627 | 51503 | 5 | 0 | 2685 | 232 | 0.7610 | 5 | 175 |

### Tasksatu
| tile | buildMs | faces | needles | tiny | lt>4 | lt>8 | meanQ | islands | cln refine ms |
|------|---------|-------|---------|------|------|------|-------|---------|---------------|
| 16 | 5975 | 91848 | 7 | 0 | 6666 | 711 | 0.7395 | 1 | 678 |
| 14 | 6000 | 91312 | 18 | 0 | 6240 | 655 | 0.7417 | 1 | 644 |
| 12 | 5641 | 91740 | 14 | 0 | 6069 | 555 | 0.7431 | 1 | 532 |
| 10 | 5756 | 92933 | 14 | 0 | 6001 | 497 | 0.7442 | 1 | 427 |
| 8  | 5496 | 95404 | 8 | 0 | 5687 | 379 | 0.7497 | 1 | 416 |
| 6  | 5496 | 104533 | 16 | 0 | 4547 | 310 | 0.7606 | 1 | 433 |

### Na'ilu
| tile | buildMs | faces | needles | tiny | lt>4 | lt>8 | meanQ | islands | cln refine ms |
|------|---------|-------|---------|------|------|------|-------|---------|---------------|
| 16 | 42832 | 527525 | 50 | 0 | 36854 | 3468 | 0.7466 | 3 | 17040 |
| 14 | 38303 | 525170 | 67 | 0 | 34836 | 3136 | 0.7483 | 2 | 14002 |
| 12 | 34944 | 524118 | 52 | 0 | 33383 | 2645 | 0.7505 | 2 | 11169 |
| 10 | 30099 | 527174 | 65 | 0 | 32007 | 2276 | 0.7521 | 2 | 8252 |
| 8  | 26655 | 534267 | 41 | 0 | 31385 | 1777 | 0.7545 | 2 | 5982 |
| 6  | 25083 | 558485 | 40 | 0 | 25660 | 1275 | 0.7630 | 3 | 5383 |

### Basamu
| tile | buildMs | faces | needles | tiny | lt>4 | lt>8 | meanQ | islands | cln refine ms |
|------|---------|-------|---------|------|------|------|-------|---------|---------------|
| 16 | 41320 | 482993 | 33 | 0 | 34765 | 2284 | 0.7560 | 1 | 13714 |
| 14 | 37695 | 479638 | 35 | 0 | 33960 | 1961 | 0.7567 | 1 | 10550 |
| 12 | 32773 | 482435 | 34 | 0 | 32278 | 1756 | 0.7593 | 1 | 6379 |
| 10 | 31860 | 491641 | 35 | 0 | 32784 | 1488 | 0.7603 | 1 | 4951 |
| 8  | 28908 | 506591 | 49 | 0 | 33062 | 1219 | 0.7646 | 1 | 4186 |
| 6  | 29699 | 561168 | 82 | 0 | 22703 | 1036 | 0.7809 | 1 | 3807 |

### CC_20 (v2)
| tile | buildMs | faces | needles | tiny | lt>4 | lt>8 | meanQ | islands | cln refine ms |
|------|---------|-------|---------|------|------|------|-------|---------|---------------|
| 16 | 24822 | 235660 | 19 | 0 | 20252 | 1305 | 0.7468 | 13 | 4161 |
| 14 | 25032 | 234417 | 20 | 0 | 19312 | 1211 | 0.7471 | 13 | 4200 |
| 12 | 19605 | 237760 | 18 | 0 | 19073 | 1083 | 0.7487 | 12 | 2220 |
| 10 | 19953 | 246028 | 18 | 0 | 19676 | 963 | 0.7498 | 14 | 1990 |
| 8  | 18860 | 270834 | 10 | 0 | 22049 | 843 | 0.7532 | 16 | 1752 |
| 6  | 18300 | 318015 | 9 | 0 | 12978 | 663 | 0.7795 | 15 | 1540 |

### Deltas vs 16 m (build time / faces / long-thin aspect>4)
| map | 12 m | 10 m | 8 m | 6 m |
|-----|------|------|-----|-----|
| Birtu    | -2.1% / -0.4% / -12.8% | -4.1% / +3.2% / -8.0% | -2.8% / +10.1% / -6.6% | +2.5% / +21.8% / -33.4% |
| Tasksatu | -5.6% / -0.1% / -8.9%  | -3.7% / +1.2% / -10.0% | -8.0% / +3.9% / -14.7% | -8.0% / +13.8% / -31.8% |
| Na'ilu   | -18.4% / -0.6% / -9.4% | -29.7% / -0.1% / -13.2% | -37.8% / +1.3% / -14.8% | -41.4% / +5.9% / -30.4% |
| Basamu   | -20.7% / -0.1% / -7.2% | -22.9% / +1.8% / -5.7% | -30.0% / +4.9% / -4.9% | -28.1% / +16.2% / -34.7% |
| CC_20    | -21.0% / +0.9% / -5.8% | -19.6% / +4.4% / -2.8% | -24.0% / +14.9% / +8.9% | -26.3% / +35.0% / -35.9% |

## Observations

- **Build time** falls with the tile, bottoming around 8-10 m for most maps. Na'ilu keeps improving to 6 m
  (-41.4%), Basamu bottoms at 8 m (28.9 s, -30.0%), CC_20 at 6 m (-26.3%), Tasksatu at 8/6 m (-8.0%),
  Birtu at 10 m (-4.1%). Tile 14 m adds nothing (a few reversals vs 16 m).
- **Faces inflate as the tile shrinks** (per-tile stitching + weaker quad merging): at 12 m the counts are
  flat (+/-1%); at 10 m +2..4%; at 8 m +1..15% (CC_20 +14.9% is the outlier); at 6 m +6..35% (CC_20 +35.0%,
  Birtu +21.8%, Basamu +16.2%, Tasksatu +13.8%, Na'ilu +5.9%).
- **Long-thin (aspect>4) drops**: -5..13% at 12 m, -3..13% at 10 m, then sharply at 6 m (-30..36% everywhere).
  CC_20 is non-monotonic (lt>4 rises +8.9% at 8 m, then collapses -35.9% at 6 m).
- **meanQuality improves monotonically** as the tile shrinks on every map (e.g. CC_20 0.7468 -> 0.7795,
  Basamu 0.7560 -> 0.7809, Birtu 0.7330 -> 0.7610).
- **Needles** stay near-flat except Tasksatu t14 = 18 (vs 7 at 16 m; reproduced) and Basamu t6 = 82 (vs 33
  at 16 m). Na'ilu runs 40-67 across all tiles.
- **Cleanup refine ms collapses** with the tile (Na'ilu 17.0 -> 5.4 s; Basamu 13.7 -> 3.8 s; CC_20 4.2 ->
  1.5 s) - the "fewer slivers born" mechanism at work.
- **Islands are non-monotonic** (Birtu 4-5, Na'ilu 2-3, CC_20 12-16) - tile boundaries change which small
  islands merge. Tasksatu and Basamu stay at 1 island everywhere.
- `deadline=6000 exceeded=1` appears on Na'ilu/Basamu (all tiles) and CC_20 16/14 - the pre-existing
  cleanup-budget diagnostic, not tile-related (re-runs are byte-identical).

## Verification

Ten of the 24 new configurations were re-run independently (Na'ilu t8/t6, CC_20 t8/t6, Basamu t12, Tasksatu
t14/t8/t6, Birtu t10/t6): every mesh metric (faces/verts/edges/quads/needles/long-thin/meanQuality/islands)
reproduced byte-identically; wall times moved 1-6%. All 30 runs report `invariants=PASS`, `degenerate=0`,
`tiny=0`.

## Read (for the tile decision)

- **12 m**: essentially free - faces flat (+/-1%), build time -18..21% on the heavy maps (-2..6% on the
  light ones), quality and long-thin improved. The safe default candidate.
- **10 m**: a modest step beyond - a few % more faces, -20..30% on the heavy maps.
- **8 m**: the aggressive option - -24..38% on the heavy maps, but CC_20 pays +14.9% faces and its long-thin
  count rises; Basamu's time minimum.
- **6 m**: overshoots - face inflation +6..35% and build times reverse on Birtu/Basamu vs smaller settings;
  Basamu needles jump to 82. Quality is the best of the sweep, but the cost is real.
- **14 m**: no benefit over 16 m; drop it.

Next: in-engine validation (set `coarsest`, rebuild, compare walkable borders and timing) at the chosen
tile - 10 or 12 m are the natural candidates. If accepted, flip the default in the exporter, the builder
settings, the harness `SnapshotSettings`, and the README.
