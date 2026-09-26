# Third-party notice: vendored DotRecast subset

This directory contains a **subset of DotRecast**, vendored into the BetterBannerlordNavmeshing module and
compiled into the single deployed `BetterBannerlordNavmeshing.dll`. It is not used as a NuGet package or a
separate assembly.

## Upstream

| | |
|---|---|
| Project | DotRecast — C# port of Recast & Detour |
| Repository | https://github.com/ikpil/DotRecast |
| Pinned commit | `5841f99e9881c05ca105079b25bb8faecf8b65a1` |
| Latest release tag at pin | `2026.3.1` |
| License | **ZLib** (see `LICENSE.txt`, copied verbatim from upstream) |
| Vendored paths | `src/DotRecast.Recast/**` and the transitive `src/DotRecast.Core/**` files |
| Build target | .NET Framework 4.7.2 (`net472`), C# language version **7.3** |

Upstream DotRecast targets `netstandard2.1;net8.0;net9.0;net10.0` and requires C# 8; `net472` cannot consume
`netstandard2.1`, so the sources were copied and backported instead of referenced. ZLib clause 2 requires
altered versions to be marked as altered: every modified file carries a
`// BACKPORTED for net472/C# 7.3 - see ThirdParty/DotRecast/NOTICE.md` comment beneath its original licence
header, and the exact edits are listed below. Original licence headers were preserved on all files.

## Vendored inventory (76 `.cs` files)

- `Recast/` — 56 files. The full `src/DotRecast.Recast` tree **except** the two orphans below: config/grid
  (`RcConfig`, `RcBuilderConfig`), rasterization (`RcRecast`, `RcRasterizations`, `RcVoxelizations`), filters
  (`RcFilters`), compact/erode (`RcCompacts`, `RcAreas`), regions (`RcRegions`), layers (`RcLayers`,
  `RcHeightfieldLayer*`), contours (`RcContours`), polymesh (`RcMeshs`), detail (`RcMeshDetails`),
  orchestration (`RcBuilder`), and the `Geom/` input contracts.
- `Core/` — 19 files. Only the transitive dependencies of the above: `RcArrays`, `RcAtomicInteger`,
  `RcAtomicLong`, `RcContext`, `RcDebug`, `RcEdge`, `RcFrequency`, `RcHashCodes`, `RcIntersections`,
  `RcLogCategory`, `RcMath`, `RcScopedTimer`, `RcTelemetryTick`, `RcThrowHelper`, `RcTimerLabel`,
  `Numerics/{RcVec,RcVec2f,RcVec3f}`, and a reduced `Collections/Extensions/RcCollectionExtensions`.
- `DotRecastBackport.cs` — **ours, not upstream**: the `System.MathF` shim, `RcBackport.Clamp/Fill/IsFinite`.

### Upstream files deliberately NOT vendored

- `src/DotRecast.Recast/Geom/RcSampleInputGeomProvider.cs` — demo-only `.obj` loader; its removal also drops
  the `RcIO`/`RcObjImporter`/`RcImmutableArray`/`RcByteBuffer` dependencies.
- `src/DotRecast.Recast/RcFilledVolumeRasterization.cs` — **dead code** in this commit: no other Recast file
  references it (the `RcAreas.Mark*Area` methods are self-contained), confirmed by source scan.
- `src/DotRecast.Recast/RcPolyMeshRaycast.cs` — unused by the generator pipeline.
- `src/DotRecast.Core/Buffers/**`, `Compression/**`, `RcByteBuffer`, `RcByteUtils`, `RcByteOrder`, `RcIO`,
  `RcObjImporter*`, `RcDirectory`, `RcProcess`, `RcRand`, `IRcCompressor`, `IRcRand`, `RcSpans`,
  `RcFixedArray*`, `RcImmutableArray*`, `RcBinaryMinHeap`, `RcObjectPool`, `RcRentedArray`,
  `RcAtomicBoolean/Float`, `RcConvexUtils`, `RcSegmentVert`, `RcMatrix4x4f`, `RcVec2i`, `RcVec3i` — unused by
  the vendored Recast subset or superseded by the backport shim. Pruning was driven by compiler errors and a
  reference scan.

## Backport: mechanical alterations, per file

Three netstandard2.1-only BCL APIs are replaced by the shim in `DotRecastBackport.cs`:
`MathF.*` → `System.MathF` shim, `Math.Clamp(...)` → `RcBackport.Clamp(...)`,
`Array.Fill(...)` → `RcBackport.Fill(...)`. `Span<T>`/`stackalloc` were rewritten to arrays (no `System.Memory`).
`using var x = ...;` (C# 8) was rewritten to `using (x) { ... }` blocks (C# 7.3).

| File | Alteration |
|---|---|
| `Core/RcArrays.cs` | `CopyOf`: `source.AsSpan(...).ToArray()` → `new T[]` + `Array.Copy`. |
| `Core/RcScopedTimer.cs` | `readonly ref struct` → `struct : IDisposable` (C# 7.3 cannot pattern-dispose a ref struct). |
| `Core/RcThrowHelper.cs` | Removed unused `StackOverflow()` and its `using DotRecast.Core.Collections;` (avoids vendoring `RcFixedArray*`). |
| `Core/Collections/Extensions/RcCollectionExtensions.cs` | Reduced to `InsertSort` only; dropped `ForEach`/`Shuffle`/`AddRange` (needed `RcImmutableArray`/`Span`). |
| `Core/Numerics/RcVec.cs` | Removed the `Span<float>`/`ReadOnlySpan<float>` overloads of `ToVec3`/`Copy` (duplicates of the `float[]` overloads after rewriting); `float.IsFinite(...)` → `RcBackport.IsFinite(...)`. |
| `Core/Numerics/RcVec3f.cs` | Removed `readonly` member modifiers (`Length`, `LengthSquared`, `CopyTo`); `HashCode.Combine(X,Y,Z)` → manual hash. |
| `Recast/RcAreaModification.cs` | Removed `readonly` member modifiers (`GetMaskedValue`, `Apply`). |
| `Recast/RcAreas.cs` | 5× `using var` → `using(...){}`; 1× `Array.Fill` → `RcBackport.Fill`; `using DotRecast.Core.Collections.Extensions;` retained (needed by `InsertSort`). |
| `Recast/RcCompacts.cs` | 1× `using var`; 2× `Math.Clamp`; 1× `MathF`. |
| `Recast/RcContours.cs` | 1× `using var`. |
| `Recast/RcFilters.cs` | 3× `using var`; 1× `MathF`. |
| `Recast/RcLayers.cs` | 1× `using var`; 4× `Span<byte>/Span<int>` + 4× `stackalloc` → arrays; 1× `Array.Fill` + 2× `Span.Fill` → `RcBackport.Fill`. |
| `Recast/RcMeshDetails.cs` | 2× `using var`; 14× `MathF`; 2× `Math.Clamp`; 3× `Array.Fill`. |
| `Recast/RcMeshs.cs` | 2× `using var`; 5× `MathF`; 7× `Array.Fill`. |
| `Recast/RcRasterizations.cs` | 3× `using var`; `DividePoly`/`RasterizeTri` `Span<float>` params + `stackalloc` scratch → arrays; 3× `MathF`; 6× `Math.Clamp`. **Local edit (BetterBannerlordNavmeshing):** `RasterizeTri`'s 84-float and `DividePoly`'s 12-float scratch arrays are now allocate-on-demand `[ThreadStatic]` reusable buffers (per-thread, non-nested calls) instead of per-call allocations — the span backport's per-triangle `new float[84]` was a GC hotspot at map scale. No behaviour change. |
| `Recast/RcRegions.cs` | 4× `using var`; 2× `Array.Fill`; 2× `list[^1]` → `list[list.Count - 1]`. |
| `Recast/Geom/IRcInputGeomProvider.cs` | Removed the C# 8 explicit `public` modifiers on the three off-mesh interface members. |

No algorithm/behaviour changes were made beyond the API/syntax substitutions above.

## Known upstream limitation (not introduced here)

`RcMeshDetails.BuildPolyMeshDetail(..., sampleDist: 0, ...)` throws
`IndexOutOfRangeException` in `TriangulateHull`: when `sampleDist == 0` the `if (sampleDist > 0)` block never
builds `hull`, leaving `nhull == 0`. This reproduces unchanged against the pristine pinned sources (verified
with a separate net8 harness). Callers must pass a positive `sampleDist` (the documented minimum is `0.9`).
This was **not** patched, to keep the vendored sources faithful.

## Regenerating this vendor tree

1. `git clone https://github.com/ikpil/DotRecast` and `git checkout 5841f99e9881c05ca105079b25bb8faecf8b65a1`.
2. Copy `src/DotRecast.Recast/**` and the `Core` files listed under *Vendored inventory* into `ThirdParty/DotRecast/{Recast,Core}` (same relative layout), plus `LICENSE.txt`.
3. Re-apply the alterations in the table above (and keep `DotRecastBackport.cs`).
4. Re-add the `// BACKPORTED ...` marker to every modified file.
