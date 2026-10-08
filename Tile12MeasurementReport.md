# Tile-12 Measurement Report

> Superseded for the full tile curve by `TileSweepReport.md` (16-6 m across five maps, 2026-10-08).

Date: 2026-10-08. Harness: `tools/TiledCoreHarness` (whole-map replay; the walk-area border clamp is not
applied offline). Fixtures: Na'ilu (dump 2026-10-08 14:07), Tasksatu (dump 2026-10-08 14:07), Birtu
(2026-09-30 fixture, control). Command: `TiledCoreHarness --snapshot <fixture-base> --quality --cleanup on`
vs the same with `--tile 12`. Settings: cs=0.25, radius=0.25, slope=50, climb=0.6 (auto), gapFill=2,
stitch=6, prune=400, detail on, `contourMaxError` 2.0 vox, cleanup on.

## Results (tile 16 = shipped default; tile 12 = candidate)

| map      | tile | buildMs¹ | faces  | needles | tiny | long-thin >4 | long-thin >8 | meanQuality | islands |
|----------|------|----------|--------|---------|------|--------------|--------------|-------------|---------|
| Birtu    | 16   | 4516     | 42283  | 6       | 0    | 4033         | 564          | 0.7330      | 4       |
| Birtu    | 12   | 4420     | 42096  | 5       | 0    | 3518         | 465          | 0.7378      | 4       |
| Tasksatu | 16   | 5975     | 91848  | 7       | 0    | 6666         | 711          | 0.7395      | 1       |
| Tasksatu | 12   | 5641     | 91740  | 14      | 0    | 6069         | 555          | 0.7431      | 1       |
| Na'ilu   | 16   | 42832    | 527525 | 50      | 0    | 36854        | 3468         | 0.7466      | 3       |
| Na'ilu   | 12   | 34944    | 524118 | 52      | 0    | 33383        | 2645         | 0.7505      | 2       |

¹ `build: ... ms=` line. An independent re-run reproduced every mesh metric byte-identically
(faces/quads/needles/long-thin/meanQuality/islands); only wall time moved 1-3% (Na'ilu 42832/42163 and
34944/33725 ms). The `--tile` override is visible in the settings line (`tile=16m cellsPerTile=64` vs
`tile=12m cellsPerTile=48`). Raw outputs: `C:\Users\Sheepbitter\AppData\Local\Temp\opencode\tile_ab\`
(worker runs `*_t16/*_t12`, verification re-runs `verify_*`).

## Observations

- **Tile 12 is faster on all three maps**: -18.4% (Na'ilu), -5.6% (Tasksatu), -2.1% (Birtu).
- The win is post-raster: Na'ilu `tilesMs` 9885 -> 9569 while the total drops 42813 -> 34927 ms. The
  cleanup does less work because fewer slivers are born: Na'ilu `needles before` 787 -> 428,
  `thin before` 17976 -> 12511; cleanup `refine` 17.0 s -> 10.7 s, `decimate` 3.6 s -> 3.1 s.
- **Long-thin faces drop**: aspect>4 by 9-13%, the sharper aspect>8 class by 18-24%. `meanQuality` rises
  ~0.004 on all three; `tiny` = 0 and `invariants=PASS` everywhere.
- Na'ilu islands 3 -> 2 (the 726- and 368-face islands merge into one 783-face island).
- Tasksatu needles 7 -> 14 (0.015% of faces) - the only counter that moved the wrong way, at a negligible
  absolute scale.
- `deadline=6000 exceeded=1` shows on Na'ilu in both configs (the post-determinism cleanup budget
  diagnostic); re-runs are byte-identical, so it is not a determinism concern here.

## Caveats

- The offline replay is whole-map; the editor's bordered builds produce smaller absolute numbers (the
  direction should hold).
- Na'ilu is terrain-only (4 assets, 146 triangles - confirmed by both the snapshot and the editor log
  `entities=1457 physics=4 included=4`); its numbers are terrain-dominated.

## Next

- In-engine validation: set `coarsest` to 12 m on one or two maps, rebuild, and compare walkable borders
  and timing. If accepted, flip the default in the exporter, the builder settings, the harness
  `SnapshotSettings`, and the README (one round, like the `contourMaxError` change).
