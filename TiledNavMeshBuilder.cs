using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Recast;

namespace BetterBannerlordNavmeshing
{
    // ==================================================================
    // Tiled voxel core (navmesh v2, Phase 2) -- terrain only
    // ==================================================================
    // Pure data-in / data-out: it never touches TaleWorlds types, so it is
    // fully testable in a standalone net472 harness (see docs/plan-navmesh-v2.md
    // section 2 and docs/dotrecast-evaluation.md).
    //
    // Pipeline per tile (DotRecast Recast stages, terrain-only input):
    //   rasterize triangles -> 3 filters -> compact heightfield -> erode ->
    //   monotone regions -> contours -> convex polys -> fan to triangles.
    // Tiles are built independently (padded AABB + border cells) and merged in
    // sorted tile order, so the result is deterministic under Parallel.For.
    //
    // Coordinate convention: the input triangle soup and all world coordinates
    // here are in Recast's Y-up space (x, height, z). The v1 mesher and the
    // NMG8 prefab use Z-up (x, y, height); call ToEngineSpace(...) once before
    // serializing with Nmg8Writer.
    public static class TiledNavMeshBuilder
    {
        // Harness-only hook: when true the raw RcPolyMeshDetail of the first detail tile is kept in
        // DebugDetailMesh so a test can inspect the per-submesh index space. Never set in production.
        public static bool CaptureDetailDebug;
        public static RcPolyMeshDetail DebugDetailMesh;

        public sealed class TiledNavMeshSettings
        {
            // Tile world size (m). Comes from NavMeshCoarsestSpacing (16 m).
            public float TileWorldSizeMeters = 16f;

            // Voxel cell size (m). New NavMeshVoxelCellSize setting (default 0.5 m).
            // Regular voxel stage; tile span is rounded to a whole number of cells.
            public float VoxelCellSizeMeters = 0.5f;

            // Fixed cell height (m). Constant, not exposed.
            public float CellHeightMeters = 0.2f;

            // Walkable slope limit (deg) -> RcConfig.WalkableSlopeAngle.
            public float MaxSlopeDegrees = 65f;

            // NavMeshMaxErrorMeters (0.25 m). Used two ways:
            //  - contour simplification: MaxSimplificationError = MaxErrorMeters / cs
            //    (voxels; the plan's ~1.3 default is a derived-voxel heuristic and
            //    is NOT used here -- the metre budget is honoured instead);
            //  - detail mesh max error (metres) when UseDetailMesh is on.
            public float MaxErrorMeters = 0.25f;

            // Agent radius. Erosion = ceil(radius/cs) cells per side, and the tile border is
            // 3 + ceil(radius/cs). Default 0.25 m: at the default cs 0.5 this is still erosion 1
            // (identical to 0.35), but at the recommended cs 0.25 it stays erosion 1 instead of 2,
            // which is what lets 1.0-1.2 m doorways/walkways/stairs survive.
            public float AgentRadiusMeters = 0.25f;
            public float AgentHeightMeters = 1.8f;
            public float AgentMaxClimbMeters = 0.4f;

            // Recast's ledge filter (FilterLedgeSpans). It removes spans that sit within walkableClimb
            // of a drop, which is what keeps the mesh off cliff edges -- but it also eats the border
            // cells of narrow walkways/bridges that have drops on both sides. Toggle off to keep those
            // (at the cost of mesh reaching closer to real ledges). Default on = Recast behaviour.
            public bool UseLedgeFilter = true;

            // Gap fill (cells, before erosion). 0 = off. Fills a column with no walkable span when it
            // is flanked by walkable spans on opposite sides along one axis (N/S, E/W or the two
            // diagonal axes) with an empty run of at most this many cells and a floor difference
            // within walkableClimb. Fixes abutting-prefab / paved-terrace seams whose hairline
            // collision gap erosion would otherwise widen into a break. 2 by default.
            public int GapFillCells = 2;

            // Stitch (per tile, after the tile mesh is built). 0 = off. Reconnects two mesh
            // components of the same tile whose nearest boundary edges are within this many cells
            // (default 4 = 1.0 m at cs 0.25) and whose floors differ by at most walkableClimb, by
            // adding a small quad that reuses existing boundary vertices. Refused when solid geometry
            // sits inside the gap envelope (a wall / closed door / low ceiling).
            public int StitchMaxGapCells = 4;

            // Prune (after the global merge + non-manifold repair). Removes small isolated islands
            // (sealed-void lids and stray fragments) so they cannot sit where no agent can reach.
            public bool PruneIsolatedIslands = true;
            public int PruneMaxIslandFaces = 400;

            // Uniform lift applied to emitted vertices only (m).
            public float HeightOffsetMeters = 0.5f;

            // Contour edge length cap (voxels).
            public int MaxEdgeLenVoxels = 12;

            // RecastDemo region parameters (cell counts); the voxel areas passed
            // to Recast are RegionMinSize^2 / RegionMergeSize^2.
            public int RegionMinSize = 8;
            public int RegionMergeSize = 20;

            public int VertsPerPoly = 6;

            // Optional detail mesh. Adds interior vertices so faces follow the surface instead of
            // chording under knolls; bounded by MaxErrorMeters. Default off here (the exporter
            // enables it); can be disabled for a leaner bin.
            public bool UseDetailMesh = false;

            // Cluster radius (m) for the detail-path seam weld. Cross-tile/cross-poly coincident
            // detail samples differ only by float lerp error (~1e-4 m), so a small radius welds
            // them; it must stay well below the smallest real stacked-surface separation (6.7 cm in
            // probe data) so distinct levels are never merged.
            public float DetailWeldToleranceMeters = 0.01f;

            // Detail-mesh sampling distance (m). >0 is used directly; 0 falls back to max(0.9, cs).
            // Default 2.0 m: the sweep shows it collapses the tight/sliver population along
            // walkable boundaries (hill 31 -> 1 slivers) while staying inside the fidelity budget.
            // Larger values tessellate edges into fewer samples but lose interior refinement; at a
            // value >= 2*minPolyExtent Recast skips interior sampling entirely (detail -> fan).
            public float DetailSampleDistMeters = 2.0f;

            // Contour simplification error (voxels). >0 is used directly; 0 falls back to
            // MaxErrorMeters / cs (0.5 vox at the defaults). Default 1.3 vox (the plan's value):
            // a simpler walkable boundary removes the tight/sliver faces that Recast's detail edge
            // tessellation makes at every boundary vertex on boundary-rich maps, with no fidelity
            // change (contour error is separate from the surface error budget).
            public float ContourMaxErrorVoxels = 1.3f;
        }

        public sealed class VoxelTileResult
        {
            public int Index;
            public int TileX;
            public int TileY;
            public bool Failed;
            public string Error;
            public long ElapsedMs;

            // Recast-space (Y-up) world vertices and triangles for this tile.
            public float[] VertsXyz;
            public int[] Tris;

            // Global weld key per vertex: fan path = integer voxel-lattice triple
            // (gx, gy, gz); null for the detail path (which is clustered by position).
            public int[] WeldLattice;

            public int PolyCount;
            public int VertexCount;
            public int FaceCount;
            public int RasterizedTris;
            public int GapFilled;      // columns filled by the gap-fill pre-pass (before erosion)
            public int GapEmptyColumns; // own-area columns with no walkable span that the fill scanned
            public int Stitched;       // connector faces added by the per-tile stitch pass
        }

        public sealed class VoxelMeshResult
        {
            // Recast-space (Y-up), welded, height-offset applied.
            public float[] VertsXyz;
            public int[] Tris;
            public int VertexCount;
            public int FaceCount;
            public int EdgeCount;
            public int DegenerateFaces;
            public int WeldMergedVertices;

            public int TileCount;
            public int BuiltTiles;
            public int FailedTiles;
            public int Workers;

            public int InputTriangles;
            public int CulledByAabb;
            public int RasterizedTriangles;

            public long TilesMs;
            public long MergeMs;
            public long TotalMs;
            public long TileMsMin;
            public long TileMsAvg;
            public long TileMsMax;

            public string ObjText;  // null unless requested
            public float MinZ;
            public float MaxZ;

            // Topology invariants (computed after merge). When InvariantsPassed is false the mesh
            // must NOT be written or imported.
            public bool InvariantsPassed;
            public int UniqueEdges;
            public int BoundaryEdges;
            public int MaxEdgeMultiplicity;
            public int DuplicateFaces;
            public int SelfLoops;
            public int RequiredEdges;   // (3*faces + boundary) / 2
        }

        // ------------------------------------------------------------------
        // Terrain grid -> triangle soup
        // ------------------------------------------------------------------
        // Same sampling semantics as the v1 NavMeshSampleStride path
        // (BuildSampleAxis: sources = k*stride with the last row/column always
        // included), so face counts are comparable at equal settings.
        //
        // Input heights are indexed [x * height + y] with world
        // (terrainX, terrainY) = (x*spacing, y*spacing) and terrain Z-up.
        // Output is Recast Y-up: (terrainX, height, terrainY).
        public static void BuildTerrainSoup(float[] stitchedHeights, int width, int height,
            float terrainVertexSpacing, int sampleStride, out float[] vertsXyz, out int[] tris)
        {
            if (stitchedHeights == null)
                throw new ArgumentNullException("stitchedHeights");
            if (width < 2 || height < 2)
                throw new ArgumentException("width and height must be at least 2.");
            if (stitchedHeights.Length != width * height)
                throw new ArgumentException("stitchedHeights length must equal width * height.");
            if (sampleStride < 1)
                throw new ArgumentException("sampleStride must be at least 1.");

            int[] sampleX = BuildSampleAxis(width, sampleStride);
            int[] sampleY = BuildSampleAxis(height, sampleStride);
            int samplesX = sampleX.Length;
            int samplesY = sampleY.Length;

            vertsXyz = new float[samplesX * samplesY * 3];
            for (int sx = 0; sx < samplesX; sx++)
            {
                int sourceX = sampleX[sx];
                for (int sy = 0; sy < samplesY; sy++)
                {
                    int sourceY = sampleY[sy];
                    int s = (sx * samplesY + sy) * 3;
                    vertsXyz[s + 0] = sourceX * terrainVertexSpacing;                 // terrainX
                    vertsXyz[s + 1] = stitchedHeights[sourceX * height + sourceY];    // height (Recast up)
                    vertsXyz[s + 2] = sourceY * terrainVertexSpacing;                 // terrainY
                }
            }

            int cellsX = samplesX - 1;
            int cellsY = samplesY - 1;
            tris = cellsX > 0 && cellsY > 0 ? new int[cellsX * cellsY * 6] : new int[0];
            int t = 0;
            for (int sx = 0; sx < cellsX; sx++)
            {
                for (int sy = 0; sy < cellsY; sy++)
                {
                    int i00 = sx * samplesY + sy;
                    int i10 = (sx + 1) * samplesY + sy;
                    int i11 = (sx + 1) * samplesY + (sy + 1);
                    int i01 = sx * samplesY + (sy + 1);
                    // Winding gives +Y normals in Recast space (walkable).
                    tris[t++] = i00; tris[t++] = i11; tris[t++] = i10;
                    tris[t++] = i00; tris[t++] = i01; tris[t++] = i11;
                }
            }
        }

        // Terrain soup with a hole mask (indexed like stitchedHeights; true = terrain hole/void).
        // Any cell whose four corners touch a hole is skipped entirely, so no walkable lid is emitted
        // over a carve -- the real floor below (an asset) is then the only surface there.
        public static void BuildTerrainSoup(float[] stitchedHeights, int width, int height,
            float terrainVertexSpacing, int sampleStride, bool[] holeMask,
            out float[] vertsXyz, out int[] tris)
        {
            if (stitchedHeights == null)
                throw new ArgumentNullException("stitchedHeights");
            if (width < 2 || height < 2)
                throw new ArgumentException("width and height must be at least 2.");
            if (stitchedHeights.Length != width * height)
                throw new ArgumentException("stitchedHeights length must equal width * height.");
            if (sampleStride < 1)
                throw new ArgumentException("sampleStride must be at least 1.");
            if (holeMask == null)
                throw new ArgumentNullException("holeMask");

            int[] sampleX = BuildSampleAxis(width, sampleStride);
            int[] sampleY = BuildSampleAxis(height, sampleStride);
            int samplesX = sampleX.Length;
            int samplesY = sampleY.Length;

            vertsXyz = new float[samplesX * samplesY * 3];
            for (int sx = 0; sx < samplesX; sx++)
            {
                int sourceX = sampleX[sx];
                for (int sy = 0; sy < samplesY; sy++)
                {
                    int sourceY = sampleY[sy];
                    int s = (sx * samplesY + sy) * 3;
                    vertsXyz[s + 0] = sourceX * terrainVertexSpacing;
                    vertsXyz[s + 1] = stitchedHeights[sourceX * height + sourceY];
                    vertsXyz[s + 2] = sourceY * terrainVertexSpacing;
                }
            }

            int cellsX = samplesX - 1;
            int cellsY = samplesY - 1;
            var tri = new List<int>(cellsX > 0 && cellsY > 0 ? cellsX * cellsY * 6 : 0);
            for (int sx = 0; sx < cellsX; sx++)
            {
                for (int sy = 0; sy < cellsY; sy++)
                {
                    if (holeMask[sampleX[sx] * height + sampleY[sy]]
                        || holeMask[sampleX[sx + 1] * height + sampleY[sy]]
                        || holeMask[sampleX[sx + 1] * height + sampleY[sy + 1]]
                        || holeMask[sampleX[sx] * height + sampleY[sy + 1]])
                        continue;

                    int i00 = sx * samplesY + sy;
                    int i10 = (sx + 1) * samplesY + sy;
                    int i11 = (sx + 1) * samplesY + (sy + 1);
                    int i01 = sx * samplesY + (sy + 1);
                    tri.Add(i00); tri.Add(i11); tri.Add(i10);
                    tri.Add(i00); tri.Add(i01); tri.Add(i11);
                }
            }
            tris = tri.ToArray();
        }

        // Appends engine-space (Z-up) asset triangles to a Recast Y-up terrain soup. Asset
        // vertices are mapped engine (x,y,z) -> Recast (x, z, y) like BuildTerrainSoup, and each
        // asset triangle's winding is reversed because that axis swap is orientation-reversing
        // (without the reversal every engine-up floor would get a Recast-down normal and be
        // rejected by the slope filter). No height compensation is applied here: the merged soup is
        // lifted uniformly later by HeightOffsetMeters, so terrain AND structures share the same
        // anti-intersection offset (a navmesh that intersected the terrain/assets would make units
        // lose the mesh).
        //
        // Per-triangle winding normalisation: the blanket reversal above only fixes the axis swap.
        // Real collision data is frequently wound inconsistently per triangle, and a DOWNWARD-wound
        // floor (Recast normal.Y < 0) is rejected by MarkWalkableTriangles -> NULL area -> no navmesh
        // face at any cell size, with or without pruning (measured on Birtu: the corridor floors at
        // z 20.0-20.4 produced no navmesh at all). So each asset triangle whose Recast normal.Y < 0
        // has two indices swapped, i.e. such collision geometry is treated as DOUBLE-SIDED for
        // walkability. This changes only the input soup: the rasterizer merges the two orientations
        // into the same solid span, so the output mesh is unchanged apart from surfaces that were
        // previously dropped entirely.
        public static void BuildCombinedSoup(float[] terrainVertsXyz, int[] terrainTris,
            float[] assetVertsEngineXyz, int[] assetTris,
            out float[] vertsXyz, out int[] tris, out int flippedTriangles)
        {
            int tv = terrainVertsXyz != null ? terrainVertsXyz.Length / 3 : 0;
            int tt = terrainTris != null ? terrainTris.Length / 3 : 0;
            int av = assetVertsEngineXyz != null ? assetVertsEngineXyz.Length / 3 : 0;
            int at = assetTris != null ? assetTris.Length / 3 : 0;
            flippedTriangles = 0;

            vertsXyz = new float[(tv + av) * 3];
            if (tv > 0)
                Array.Copy(terrainVertsXyz, 0, vertsXyz, 0, tv * 3);
            for (int i = 0; i < av; i++)
            {
                int s = i * 3;
                int d = (tv + i) * 3;
                vertsXyz[d + 0] = assetVertsEngineXyz[s + 0];                 // engine X -> Recast X
                vertsXyz[d + 1] = assetVertsEngineXyz[s + 2];                 // engine Z -> Recast Y (height)
                vertsXyz[d + 2] = assetVertsEngineXyz[s + 1];                 // engine Y -> Recast Z
            }

            tris = new int[(tt + at) * 3];
            if (tt > 0)
                Array.Copy(terrainTris, 0, tris, 0, tt * 3);
            for (int i = 0; i < at; i++)
            {
                int s = i * 3;
                int d = (tt + i) * 3;
                // Reversed winding (see comment above): (b, b+2, b+1).
                int r0 = assetTris[s + 0] + tv;
                int r1 = assetTris[s + 2] + tv;
                int r2 = assetTris[s + 1] + tv;
                if (TriNormalY(vertsXyz, r0, r1, r2) < 0f)
                {
                    int tmp = r1;
                    r1 = r2;
                    r2 = tmp;
                    flippedTriangles++;
                }
                tris[d + 0] = r0;
                tris[d + 1] = r1;
                tris[d + 2] = r2;
            }
        }

        private static int[] BuildSampleAxis(int count, int stride)
        {
            int samples = (count - 1) / stride + 1;
            int[] sources = new int[samples];
            for (int k = 0; k < samples; k++)
                sources[k] = k * stride;
            if (samples > 1)
                sources[samples - 1] = count - 1;
            return sources;
        }

        // Converts Recast Y-up (x, height, z) to the engine/v1 Z-up convention
        // (x, y, height) and reverses triangle winding, so the emitted faces are
        // CCW seen from +Z -- the same convention the v1 mesher emits.
        public static void ToEngineSpace(float[] vertsXyz, int[] tris)
        {
            if (vertsXyz != null)
            {
                for (int i = 0; i + 2 < vertsXyz.Length; i += 3)
                {
                    float height = vertsXyz[i + 1];
                    vertsXyz[i + 1] = vertsXyz[i + 2];
                    vertsXyz[i + 2] = height;
                }
            }

            if (tris != null)
            {
                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    int b = tris[i + 1];
                    tris[i + 1] = tris[i + 2];
                    tris[i + 2] = b;
                }
            }
        }

        // ------------------------------------------------------------------
        // Tiled build
        // ------------------------------------------------------------------
        // forceWalkableTriangles (optional, same length as the input triangle count): triangles that
        // must be marked walkable even when their slope exceeds MaxSlopeDegrees. SteppedPropRamps uses
        // it for synthesized stair/ladder ramps, which are meant to be traversed. Null (the default)
        // keeps the input byte-for-byte identical to the pre-ramp behaviour.
        public static VoxelMeshResult Build(float[] vertsXyz, int[] tris, TiledNavMeshSettings s,
            int maxWorkers, Action<string> log, bool[] forceWalkableTriangles = null)
        {
            if (vertsXyz == null)
                throw new ArgumentNullException("vertsXyz");
            if (tris == null)
                throw new ArgumentNullException("tris");
            if (s == null)
                throw new ArgumentNullException("s");
            if (vertsXyz.Length == 0 || vertsXyz.Length % 3 != 0)
                throw new ArgumentException("vertsXyz length must be a positive multiple of 3.");
            if (tris.Length % 3 != 0)
                throw new ArgumentException("tris length must be a multiple of 3.");

            var total = Stopwatch.StartNew();
            int inputTris = tris.Length / 3;
            if (forceWalkableTriangles != null && forceWalkableTriangles.Length != inputTris)
                throw new ArgumentException("forceWalkableTriangles length must match the triangle count.");

            float cs = s.VoxelCellSizeMeters;
            float ch = s.CellHeightMeters;
            if (cs <= 0f) throw new ArgumentException("VoxelCellSizeMeters must be > 0.");
            if (ch <= 0f) throw new ArgumentException("CellHeightMeters must be > 0.");
            if (s.TileWorldSizeMeters <= 0f) throw new ArgumentException("TileWorldSizeMeters must be > 0.");
            if (s.VertsPerPoly < 3 || s.VertsPerPoly > 6) throw new ArgumentException("VertsPerPoly must be in [3, 6].");

            int cellsPerTile = (int)Math.Round(s.TileWorldSizeMeters / cs);
            if (cellsPerTile < 1) cellsPerTile = 1;
            float tileSpan = cellsPerTile * cs;

            // Global bounds of the input.
            float minX = vertsXyz[0], minY = vertsXyz[1], minZ = vertsXyz[2];
            float maxX = minX, maxY = minY, maxZ = minZ;
            for (int i = 0; i < vertsXyz.Length; i += 3)
            {
                float x = vertsXyz[i], y = vertsXyz[i + 1], z = vertsXyz[i + 2];
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (z < minZ) minZ = z;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
                if (z > maxZ) maxZ = z;
            }

            // Fixed snapped world origin on the tile lattice. Every tile boundary
            // and every emitted vertex then lies on the global lattice
            // origin + integer*cs (X/Z) / +integer*ch (Y), which is what makes
            // cross-tile welding exact.
            float originX = (float)(Math.Floor((double)minX / tileSpan) * tileSpan);
            float originZ = (float)(Math.Floor((double)minZ / tileSpan) * tileSpan);
            float margin = Math.Max(2f, s.AgentHeightMeters + 0.5f);
            float bminY = (float)(Math.Floor((double)(minY - margin) / ch) * ch);
            float bmaxY = (float)(Math.Ceiling((double)(maxY + margin) / ch) * ch);

            int tilesX = Math.Max(1, (int)Math.Ceiling((maxX - originX) / tileSpan));
            int tilesZ = Math.Max(1, (int)Math.Ceiling((maxZ - originZ) / tileSpan));
            int tileCount = tilesX * tilesZ;

            int border = RcConfig.CalcBorder(s.AgentRadiusMeters, cs);
            int width = cellsPerTile + 2 * border;
            int height = cellsPerTile + 2 * border;
            float pad = border * cs;

            int walkableHeight = (int)Math.Ceiling(s.AgentHeightMeters / ch);
            int walkableClimb = (int)Math.Floor(s.AgentMaxClimbMeters / ch + 1e-4f);
            int walkableRadius = (int)Math.Ceiling(s.AgentRadiusMeters / cs);
            float maxSimplificationError = EffectiveContourMaxError(s);
            int minRegionArea = s.RegionMinSize * s.RegionMinSize;
            int mergeRegionArea = s.RegionMergeSize * s.RegionMergeSize;
            var areaMod = new RcAreaModification(RcRecast.RC_WALKABLE_AREA);

            int requestedWorkers = maxWorkers;
            int workers = maxWorkers < 1 ? Environment.ProcessorCount : maxWorkers;
            if (workers > tileCount) workers = tileCount;
            if (workers < 1) workers = 1;

            Log(log, "v2: settings cs=" + F(cs) + "m ch=" + F(ch) + "m tile=" + F(tileSpan)
                + "m cellsPerTile=" + cellsPerTile + " tiles=" + tilesX + "x" + tilesZ
                + " border=" + border + " hf=" + width + "x" + height
                + " origin=(" + F(originX) + "," + F(originZ) + ")"
                + " bminY=" + F(bminY) + " bmaxY=" + F(bmaxY));
            Log(log, "v2: params slope=" + F(s.MaxSlopeDegrees) + "deg radius=" + F(s.AgentRadiusMeters)
                + "m height=" + F(s.AgentHeightMeters) + "m climb=" + F(s.AgentMaxClimbMeters)
                + "m walkable(r,h,c)=" + walkableRadius + "/" + walkableHeight + "/" + walkableClimb
                + " edgeLen=" + s.MaxEdgeLenVoxels + "vox vpp=" + s.VertsPerPoly
                + " regionMin=" + s.RegionMinSize + " regionMerge=" + s.RegionMergeSize
                + " gapFill=" + s.GapFillCells
                + " stitch=" + s.StitchMaxGapCells
                + " prune=" + (s.PruneIsolatedIslands ? s.PruneMaxIslandFaces.ToString(CultureInfo.InvariantCulture) : "off")
                + " ledgeFilter=" + s.UseLedgeFilter
                + " detail=" + s.UseDetailMesh
                + " detSampleDist=" + F(EffectiveDetailSampleDist(s)) + "m"
                + " contourSimpErr=" + F(EffectiveContourMaxError(s)) + "vox");
            Log(log, "v2: contour maxError=" + F(maxSimplificationError) + "vox (= " + F(maxSimplificationError * cs)
                + "m at cs=" + F(cs) + "m; NavMeshVoxelContourMaxError, 0=auto=MaxErrorMeters/cs)");
            Log(log, "v2: workers=" + workers + " (requested " + requestedWorkers + ", tileCount=" + tileCount + ")");

            // --- bin input triangles into tiles (padded AABB overlap) ---
            var tileTris = new List<int>[tileCount];
            int culledByAabb = 0;
            int rasterizedTris = 0;
            for (int t = 0; t < inputTris; t++)
            {
                int i0 = tris[t * 3 + 0], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
                float tx0 = vertsXyz[i0 * 3], tx1 = vertsXyz[i1 * 3], tx2 = vertsXyz[i2 * 3];
                float tz0 = vertsXyz[i0 * 3 + 2], tz1 = vertsXyz[i1 * 3 + 2], tz2 = vertsXyz[i2 * 3 + 2];
                float triMinX = Math.Min(tx0, Math.Min(tx1, tx2));
                float triMaxX = Math.Max(tx0, Math.Max(tx1, tx2));
                float triMinZ = Math.Min(tz0, Math.Min(tz1, tz2));
                float triMaxZ = Math.Max(tz0, Math.Max(tz1, tz2));

                int xMin = (int)Math.Floor((triMinX - originX - pad) / tileSpan);
                int xMax = (int)Math.Floor((triMaxX - originX + pad) / tileSpan);
                int zMin = (int)Math.Floor((triMinZ - originZ - pad) / tileSpan);
                int zMax = (int)Math.Floor((triMaxZ - originZ + pad) / tileSpan);
                if (xMin < 0) xMin = 0;
                if (zMin < 0) zMin = 0;
                if (xMax > tilesX - 1) xMax = tilesX - 1;
                if (zMax > tilesZ - 1) zMax = tilesZ - 1;

                if (xMin > xMax || zMin > zMax)
                {
                    culledByAabb++;
                    continue;
                }

                for (int tz = zMin; tz <= zMax; tz++)
                {
                    for (int tx = xMin; tx <= xMax; tx++)
                    {
                        int idx = tz * tilesX + tx;
                        List<int> list = tileTris[idx];
                        if (list == null)
                        {
                            list = new List<int>();
                            tileTris[idx] = list;
                        }
                        list.Add(t);
                        rasterizedTris++;
                    }
                }
            }

            Log(log, "v2: tris input=" + inputTris + " culledByAabb=" + culledByAabb
                + " rasterized=" + rasterizedTris + " (rasterized counts per-tile border duplicates)");
            if (forceWalkableTriangles != null)
            {
                int forced = 0;
                for (int i = 0; i < forceWalkableTriangles.Length; i++)
                    if (forceWalkableTriangles[i]) forced++;
                Log(log, "v2: forced-walkable tris=" + forced + "/" + inputTris
                    + " (synthesized stair/ladder ramps; marked walkable regardless of slope)");
            }

            // --- parallel per-tile build ---
            var results = new VoxelTileResult[tileCount];
            var tilesWatch = Stopwatch.StartNew();
            var parallelOptions = new ParallelOptions();
            parallelOptions.MaxDegreeOfParallelism = workers;

            // Coarse progress for long builds: log every ~5% of tiles (and at the end),
            // throttled to at most one line per ~2 s. Purely observational -- the counter
            // and logs never influence the built output (determinism is unaffected).
            int tilesCompleted = 0;
            int progressStep = Math.Max(1, tileCount / 20);
            long lastProgressMs = 0;
            var progressLock = new object();
            var progressWatch = Stopwatch.StartNew();

            Parallel.For(0, tileCount, parallelOptions, delegate (int index)
            {
                int tx = index % tilesX;
                int ty = index / tilesX;
                results[index] = BuildTile(index, tx, ty, vertsXyz, tris, tileTris[index], s,
                    originX, originZ, tileSpan, cellsPerTile, bminY, bmaxY, width, height, border, pad,
                    walkableHeight, walkableClimb, walkableRadius, maxSimplificationError,
                    minRegionArea, mergeRegionArea, areaMod, forceWalkableTriangles);

                if (log != null)
                {
                    int done = Interlocked.Increment(ref tilesCompleted);
                    if (done == tileCount || done % progressStep == 0)
                    {
                        long elapsed = progressWatch.ElapsedMilliseconds;
                        bool emit;
                        lock (progressLock)
                        {
                            emit = done == tileCount || elapsed - lastProgressMs >= 2000;
                            if (emit)
                                lastProgressMs = elapsed;
                        }
                        if (emit)
                            log("v2: progress tiles=" + done + "/" + tileCount
                                + " (" + (done * 100 / tileCount) + "%) elapsedMs=" + elapsed);
                    }
                }
            });
            tilesWatch.Stop();

            // --- aggregate + log failures (never silently dropped) ---
            int builtTiles = 0;
            int failedTiles = 0;
            long tileMsSum = 0;
            long tileMsMin = long.MaxValue;
            long tileMsMax = 0;
            int polyTotal = 0;
            var failures = new List<string>();
            for (int i = 0; i < tileCount; i++)
            {
                VoxelTileResult r = results[i];
                if (r == null)
                {
                    failedTiles++;
                    failures.Add("tile#" + i + " (tx=" + (i % tilesX) + ",ty=" + (i / tilesX) + ") produced no result");
                    continue;
                }
                if (r.Failed)
                {
                    failedTiles++;
                    failures.Add("tile#" + i + " (tx=" + r.TileX + ",ty=" + r.TileY + ") failed: " + r.Error);
                    continue;
                }
                builtTiles++;
                polyTotal += r.PolyCount;
                tileMsSum += r.ElapsedMs;
                if (r.ElapsedMs < tileMsMin) tileMsMin = r.ElapsedMs;
                if (r.ElapsedMs > tileMsMax) tileMsMax = r.ElapsedMs;
            }
            for (int i = 0; i < failures.Count; i++)
                Log(log, "v2: FAILED " + failures[i]);
            if (tileMsMin == long.MaxValue) tileMsMin = 0;
            long tileMsAvg = builtTiles > 0 ? tileMsSum / builtTiles : 0;

            Log(log, "v2: tiles=" + tileCount + " built=" + builtTiles + " failed=" + failedTiles
                + " workers=" + workers + " polys=" + polyTotal);

            if (s.GapFillCells > 0)
            {
                int gapFilled = 0;
                int gapEmpty = 0;
                for (int i = 0; i < results.Length; i++)
                    if (results[i] != null && !results[i].Failed)
                    {
                        gapFilled += results[i].GapFilled;
                        gapEmpty += results[i].GapEmptyColumns;
                    }
                Log(log, "v2: gap fill: filled=" + gapFilled + " emptyColumns=" + gapEmpty
                    + " (cap=" + s.GapFillCells + " cells, before erosion)");
            }

            if (s.StitchMaxGapCells > 0)
            {
                int stitched = 0;
                for (int i = 0; i < results.Length; i++)
                    if (results[i] != null && !results[i].Failed)
                        stitched += results[i].Stitched;
                Log(log, "v2: stitch: added=" + stitched + " faces across tiles (cap=" + s.StitchMaxGapCells
                    + " cells" + (stitched > 0 ? "" : " - no near-miss cuts found") + ")");
            }

            // --- merge in sorted order (deterministic) ---
            var mergeWatch = Stopwatch.StartNew();
            MergeOutput merged = s.UseDetailMesh
                ? MergeDetailTiles(results, tilesX, tilesZ, s.DetailWeldToleranceMeters)
                : MergeFanTiles(results, tilesX, tilesZ);
            mergeWatch.Stop();

            // Deterministic repair: Recast's per-tile detail tessellation can emit a few
            // non-manifold fans at tile corners (upstream; its delaunayHull even logs "Removing
            // dangling face"). Remove the surplus faces so every edge is shared by <= 2 faces.
            int nonManifoldRepaired = RepairNonManifold(merged);
            if (nonManifoldRepaired > 0)
                Log(log, "v2: repaired mesh: removed " + nonManifoldRepaired + " non-manifold faces");

            // Cross-tile stitch (before pruning): reconnect near-miss pieces that the per-tile stitch
            // could not reach (different tiles, or beyond its cap). A stitch is refused when the input
            // soup has solid geometry spanning the gap envelope (wall, closed door, low ceiling).
            if (s.StitchMaxGapCells > 0)
            {
                int msPairs, msAdded, msRefused;
                MergeStitch(merged, vertsXyz, tris, inputTris, s.VoxelCellSizeMeters, s.CellHeightMeters,
                    s.HeightOffsetMeters, bminY, walkableHeight, walkableClimb, s.StitchMaxGapCells,
                    out msPairs, out msAdded, out msRefused);
                Log(log, "v2: merge-stitch: pairs=" + msPairs + " added=" + msAdded + " refused=" + msRefused
                    + " (cap=" + s.StitchMaxGapCells + " cells)");
            }

            // Prune small isolated islands (sealed-void lids / stray fragments) before the gate, so an
            // unreachable fragment never reaches the engine. Never touches the big networks.
            if (s.PruneIsolatedIslands && s.PruneMaxIslandFaces > 0 && merged.Tris.Count >= 3)
            {
                int prunedIslands, prunedFaces, prunedLargest, keptIslands;
                float px, py, pz;
                PruneSmallIslands(merged, s.PruneMaxIslandFaces, out prunedIslands, out prunedFaces,
                    out prunedLargest, out px, out py, out pz, out keptIslands);
                Log(log, "v2: prune: removed " + prunedIslands + " islands, " + prunedFaces
                    + " faces (<=" + s.PruneMaxIslandFaces + " faces each); largest removed="
                    + prunedLargest + " at (" + F(px) + "," + F(py) + "," + F(pz) + "); kept="
                    + keptIslands + " islands");
            }

            int mergedVertices = merged.X.Count;
            var result = new VoxelMeshResult();
            result.VertexCount = mergedVertices;
            result.FaceCount = merged.Tris.Count / 3;
            result.DegenerateFaces = merged.Degenerate;
            result.WeldMergedVertices = SumTileVertices(results) - mergedVertices;
            result.TileCount = tileCount;
            result.BuiltTiles = builtTiles;
            result.FailedTiles = failedTiles;
            result.Workers = workers;
            result.InputTriangles = inputTris;
            result.CulledByAabb = culledByAabb;
            result.RasterizedTriangles = rasterizedTris;
            result.TilesMs = tilesWatch.ElapsedMilliseconds;
            result.MergeMs = mergeWatch.ElapsedMilliseconds;
            result.TileMsMin = tileMsMin;
            result.TileMsAvg = tileMsAvg;
            result.TileMsMax = tileMsMax;
            result.MinZ = mergedVertices > 0 ? merged.MinY : 0f;
            result.MaxZ = mergedVertices > 0 ? merged.MaxY : 0f;

            result.VertsXyz = new float[mergedVertices * 3];
            for (int i = 0; i < mergedVertices; i++)
            {
                result.VertsXyz[i * 3 + 0] = merged.X[i];
                result.VertsXyz[i * 3 + 1] = merged.Y[i];
                result.VertsXyz[i * 3 + 2] = merged.Z[i];
            }
            result.Tris = merged.Tris.ToArray();

            // --- topology + invariants (gate) ---
            ComputeInvariants(result, result.Tris, result.FaceCount);

            Log(log, "v2: weld verts=" + SumTileVertices(results) + " -> " + mergedVertices
                + " faces=" + result.FaceCount + " edges=" + result.EdgeCount
                + " degenerate=" + merged.Degenerate + " (weldMerged=" + result.WeldMergedVertices + ")");
            Log(log, "v2: invariants: faces=" + result.FaceCount + " edges=" + result.EdgeCount
                + " boundary=" + result.BoundaryEdges + " maxEdgeMult=" + result.MaxEdgeMultiplicity
                + " dupFaces=" + result.DuplicateFaces + " selfLoops=" + result.SelfLoops
                + " minEdges=" + result.RequiredEdges + " -> " + (result.InvariantsPassed ? "PASS" : "FAIL"));
            if (!result.InvariantsPassed)
                Log(log, "v2: ERROR mesh invariants failed: maxEdgeMult=" + result.MaxEdgeMultiplicity
                    + " dupFaces=" + result.DuplicateFaces + " selfLoops=" + result.SelfLoops
                    + " edges=" + result.EdgeCount + " required=" + result.RequiredEdges
                    + "; mesh is non-manifold/duplicated and must not be imported");

            LogIslands(result, log);

            total.Stop();
            result.TotalMs = total.ElapsedMilliseconds;
            Log(log, "v2 timings: tilesMs=" + result.TilesMs + " mergeMs=" + result.MergeMs
                + " totalMs=" + result.TotalMs);
            Log(log, "v2: tileMs min/avg/max=" + result.TileMsMin + "/" + result.TileMsAvg + "/" + result.TileMsMax);

            return result;
        }

        // Connected components by face adjacency (faces sharing an edge are one island). Always-on and
        // cheap; it is the data needed to choose a rule that prunes "sealed void" lids (small walkable
        // islands with no access, e.g. the tops of dungeon-corridor modules under a plank floor).
        private static void LogIslands(VoxelMeshResult r, System.Action<string> log)
        {
            if (log == null || r.FaceCount == 0)
                return;

            int nf = r.FaceCount;
            var fp = new int[nf];
            for (int i = 0; i < nf; i++) fp[i] = i;
            var edgeFirst = new Dictionary<long, int>();
            for (int f = 0; f < nf; f++)
            {
                int a = r.Tris[f * 3], b = r.Tris[f * 3 + 1], c = r.Tris[f * 3 + 2];
                FaceUnionEdge(edgeFirst, fp, f, a, b);
                FaceUnionEdge(edgeFirst, fp, f, b, c);
                FaceUnionEdge(edgeFirst, fp, f, c, a);
            }

            var faceCount = new Dictionary<int, int>();
            var areaSum = new Dictionary<int, double>();
            var cxSum = new Dictionary<int, double>();
            var cySum = new Dictionary<int, double>();
            var czSum = new Dictionary<int, double>();
            var zMin = new Dictionary<int, float>();
            var zMax = new Dictionary<int, float>();

            for (int f = 0; f < nf; f++)
            {
                int root = FaceFind(fp, f);
                int a = r.Tris[f * 3], b = r.Tris[f * 3 + 1], c = r.Tris[f * 3 + 2];
                double ax = r.VertsXyz[a * 3], ay = r.VertsXyz[a * 3 + 1], az = r.VertsXyz[a * 3 + 2];
                double bx = r.VertsXyz[b * 3], by = r.VertsXyz[b * 3 + 1], bz = r.VertsXyz[b * 3 + 2];
                double cxx = r.VertsXyz[c * 3], cyy = r.VertsXyz[c * 3 + 1], czz = r.VertsXyz[c * 3 + 2];
                double area = 0.5 * Math.Abs((bx - ax) * (czz - az) - (bz - az) * (cxx - ax));
                if (area <= 0.0) area = 1e-9;

                int n;
                faceCount.TryGetValue(root, out n);
                faceCount[root] = n + 1;
                double s;
                areaSum.TryGetValue(root, out s);
                areaSum[root] = s + area;
                cxSum.TryGetValue(root, out s); cxSum[root] = s + (ax + bx + cxx) / 3.0 * area;
                cySum.TryGetValue(root, out s); cySum[root] = s + (ay + by + cyy) / 3.0 * area;
                czSum.TryGetValue(root, out s); czSum[root] = s + (az + bz + czz) / 3.0 * area;

                float fmin = (float)Math.Min(ay, Math.Min(by, cyy));
                float fmax = (float)Math.Max(ay, Math.Max(by, cyy));
                float zmn;
                if (zMin.TryGetValue(root, out zmn))
                {
                    if (fmin < zmn) zMin[root] = fmin;
                    if (fmax > zMax[root]) zMax[root] = fmax;
                }
                else
                {
                    zMin[root] = fmin;
                    zMax[root] = fmax;
                }
            }

            var roots = new List<int>(areaSum.Keys);
            roots.Sort(delegate (int x, int y)
            {
                int cmp = areaSum[y].CompareTo(areaSum[x]);
                if (cmp != 0) return cmp;
                cmp = faceCount[y].CompareTo(faceCount[x]);
                if (cmp != 0) return cmp;
                return x.CompareTo(y);
            });

            var ic = CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            int listed = Math.Min(10, roots.Count);
            for (int i = 0; i < listed; i++)
            {
                int root = roots[i];
                double area = areaSum[root];
                double inv = area > 0.0 ? 1.0 / area : 0.0;
                if (i > 0) sb.Append(" | ");
                sb.Append(i)
                  .Append(":faces=").Append(faceCount[root].ToString(ic))
                  .Append(" area=").Append(area.ToString("F2", ic)).Append("m2")
                  .Append(" centroid=(")
                  .Append((cxSum[root] * inv).ToString("F2", ic)).Append(",")
                  .Append((cySum[root] * inv).ToString("F2", ic)).Append(",")
                  .Append((czSum[root] * inv).ToString("F2", ic)).Append(")")
                  .Append(" zr=[").Append(zMin[root].ToString("F2", ic)).Append("..")
                  .Append(zMax[root].ToString("F2", ic)).Append("]");
            }
            log("v2: islands=" + roots.Count + " totalFaces=" + nf + " largest=[" + sb.ToString() + "] top" + listed);
        }

        private static int FaceFind(int[] p, int x)
        {
            while (p[x] != x) { p[x] = p[p[x]]; x = p[x]; }
            return x;
        }

        private static int FaceUnion(int[] p, int a, int b)
        {
            int ra = FaceFind(p, a);
            int rb = FaceFind(p, b);
            if (ra != rb) p[ra] = rb;
            return FaceFind(p, a);
        }

        private static void FaceUnionEdge(Dictionary<long, int> edgeFirstFace, int[] fparent, int face, int a, int b)
        {
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            int other;
            if (edgeFirstFace.TryGetValue(key, out other))
                FaceUnion(fparent, other, face);
            else
                edgeFirstFace[key] = face;
        }

        // Removes every face-adjacency island with at most maxFaces faces and compacts the vertex array.
        // Reports the removed island/face counts, the largest removed island (faces + centroid) and the
        // islands kept. Vertex/face removal only, so the invariant gate is unaffected.
        private static void PruneSmallIslands(MergeOutput o, int maxFaces,
            out int removedIslands, out int removedFaces, out int largestRemoved, out float largestX,
            out float largestY, out float largestZ, out int keptIslands)
        {
            removedIslands = 0;
            removedFaces = 0;
            largestRemoved = 0;
            largestX = 0f;
            largestY = 0f;
            largestZ = 0f;
            keptIslands = 0;

            int nf = o.Tris.Count / 3;
            if (nf == 0)
                return;

            var fp = new int[nf];
            for (int i = 0; i < nf; i++) fp[i] = i;
            var edgeFirst = new Dictionary<long, int>();
            for (int f = 0; f < nf; f++)
            {
                int a = o.Tris[f * 3], b = o.Tris[f * 3 + 1], c = o.Tris[f * 3 + 2];
                FaceUnionEdge(edgeFirst, fp, f, a, b);
                FaceUnionEdge(edgeFirst, fp, f, b, c);
                FaceUnionEdge(edgeFirst, fp, f, c, a);
            }

            var count = new Dictionary<int, int>();
            for (int f = 0; f < nf; f++)
            {
                int root = FaceFind(fp, f);
                int c;
                count.TryGetValue(root, out c);
                count[root] = c + 1;
            }

            var dropped = new Dictionary<int, bool>();
            int largestRoot = -1;
            foreach (KeyValuePair<int, int> kv in count)
            {
                if (kv.Value <= maxFaces)
                {
                    dropped[kv.Key] = true;
                    removedIslands++;
                    removedFaces += kv.Value;
                    if (kv.Value > largestRemoved)
                    {
                        largestRemoved = kv.Value;
                        largestRoot = kv.Key;
                    }
                }
                else
                {
                    keptIslands++;
                }
            }
            if (removedIslands == 0)
                return;

            if (largestRoot >= 0)
            {
                double sx = 0, sy = 0, sz = 0;
                int n = 0;
                for (int f = 0; f < nf; f++)
                {
                    if (FaceFind(fp, f) != largestRoot)
                        continue;
                    for (int k = 0; k < 3; k++)
                    {
                        int v = o.Tris[f * 3 + k];
                        sx += o.X[v];
                        sy += o.Y[v];
                        sz += o.Z[v];
                        n++;
                    }
                }
                if (n > 0)
                {
                    largestX = (float)(sx / n);
                    largestY = (float)(sy / n);
                    largestZ = (float)(sz / n);
                }
            }

            var remap = new int[o.X.Count];
            for (int i = 0; i < remap.Length; i++) remap[i] = -1;
            var newX = new List<float>();
            var newY = new List<float>();
            var newZ = new List<float>();
            var newTris = new List<int>(o.Tris.Count);
            for (int f = 0; f < nf; f++)
            {
                bool drop;
                if (dropped.TryGetValue(FaceFind(fp, f), out drop) && drop)
                    continue;
                for (int k = 0; k < 3; k++)
                {
                    int v = o.Tris[f * 3 + k];
                    if (remap[v] < 0)
                    {
                        remap[v] = newX.Count;
                        newX.Add(o.X[v]);
                        newY.Add(o.Y[v]);
                        newZ.Add(o.Z[v]);
                    }
                    newTris.Add(remap[v]);
                }
            }

            o.X.Clear(); o.X.AddRange(newX);
            o.Y.Clear(); o.Y.AddRange(newY);
            o.Z.Clear(); o.Z.AddRange(newZ);
            o.Tris.Clear(); o.Tris.AddRange(newTris);
            o.MinY = float.MaxValue;
            o.MaxY = float.MinValue;
            for (int i = 0; i < o.Y.Count; i++)
            {
                if (o.Y[i] < o.MinY) o.MinY = o.Y[i];
                if (o.Y[i] > o.MaxY) o.MaxY = o.Y[i];
            }
        }

        // ================================================================ merge-level stitch
        // Cross-tile stitch over the MERGED mesh, run before the prune so near-miss pieces join the big
        // networks (and therefore survive pruning). Same rule as the per-tile stitch; the clearance
        // check re-reads the input soup through a coarse XZ grid and refuses the stitch when solid
        // geometry spans the gap envelope.
        private static void MergeStitch(MergeOutput o, float[] soupVerts, int[] soupTris, int soupTriCount,
            float cs, float ch, float offset, float bminY, int walkableHeight, int walkableClimb,
            int stitchMaxGapCells, out int pairs, out int added, out int refused)
        {
            pairs = 0;
            added = 0;
            refused = 0;
            int nf = o.Tris.Count / 3;
            if (nf < 2 || o.X.Count < 3)
                return;
            float maxGap = stitchMaxGapCells * cs;
            float maxGap2 = maxGap * maxGap;

            int[] fp = new int[nf];
            for (int i = 0; i < nf; i++) fp[i] = i;
            var edgeMult = new Dictionary<long, int>();
            for (int f = 0; f < nf; f++)
            {
                int a = o.Tris[f * 3], b = o.Tris[f * 3 + 1], c = o.Tris[f * 3 + 2];
                StitchAddEdge(edgeMult, fp, f, a, b);
                StitchAddEdge(edgeMult, fp, f, b, c);
                StitchAddEdge(edgeMult, fp, f, c, a);
            }

            var compIndex = new Dictionary<int, int>();
            var compRoots = new List<int>();
            for (int f = 0; f < nf; f++)
            {
                int root = StitchFind(fp, f);
                if (!compIndex.ContainsKey(root))
                {
                    compIndex[root] = compRoots.Count;
                    compRoots.Add(root);
                }
            }
            int nc = compRoots.Count;
            if (nc < 2 || nc > 4096)
                return;

            var bA = new List<int>[nc];
            var bB = new List<int>[nc];
            for (int i = 0; i < nc; i++) { bA[i] = new List<int>(); bB[i] = new List<int>(); }
            for (int f = 0; f < nf; f++)
            {
                int ci = compIndex[StitchFind(fp, f)];
                for (int k = 0; k < 3; k++)
                {
                    int i0 = o.Tris[f * 3 + k];
                    int i1 = o.Tris[f * 3 + ((k + 1) % 3)];
                    if (edgeMult[StitchEdgeKey(i0, i1)] != 1)
                        continue;                     // interior edge
                    bA[ci].Add(i0);
                    bB[ci].Add(i1);
                }
            }

            // Coarse soup grid for the clearance probe.
            const float soupCell = 4f;
            Dictionary<long, List<int>> grid;
            List<int> bigList;
            BuildSoupGrid(soupVerts, soupTris, soupTriCount, soupCell, out grid, out bigList);

            var compParent = new int[nc];
            for (int i = 0; i < nc; i++) compParent[i] = i;
            var edgeDistinct = new Dictionary<long, int>();

            for (int i = 0; i < nc; i++)
            {
                int ei = bA[i].Count;
                if (ei == 0 || ei > 4096)
                    continue;
                for (int j = i + 1; j < nc; j++)
                {
                    int ej = bA[j].Count;
                    if (ej == 0 || ej > 4096)
                        continue;
                    if (StitchFind(compParent, i) == StitchFind(compParent, j))
                        continue;

                    // Coarse AABB pre-reject.
                    float aminX = float.MaxValue, amaxX = float.MinValue, aminZ = float.MaxValue, amaxZ = float.MinValue;
                    for (int k = 0; k < ei; k++)
                    {
                        float x0 = o.X[bA[i][k]], z0 = o.Z[bA[i][k]], x1 = o.X[bB[i][k]], z1 = o.Z[bB[i][k]];
                        if (x0 < aminX) aminX = x0;
                        if (x1 < aminX) aminX = x1;
                        if (x0 > amaxX) amaxX = x0;
                        if (x1 > amaxX) amaxX = x1;
                        if (z0 < aminZ) aminZ = z0;
                        if (z1 < aminZ) aminZ = z1;
                        if (z0 > amaxZ) amaxZ = z0;
                        if (z1 > amaxZ) amaxZ = z1;
                    }
                    float bminX = float.MaxValue, bmaxX = float.MinValue, bminZ = float.MaxValue, bmaxZ = float.MinValue;
                    for (int k = 0; k < ej; k++)
                    {
                        float x0 = o.X[bA[j][k]], z0 = o.Z[bA[j][k]], x1 = o.X[bB[j][k]], z1 = o.Z[bB[j][k]];
                        if (x0 < bminX) bminX = x0;
                        if (x1 < bminX) bminX = x1;
                        if (x0 > bmaxX) bmaxX = x0;
                        if (x1 > bmaxX) bmaxX = x1;
                        if (z0 < bminZ) bminZ = z0;
                        if (z1 < bminZ) bminZ = z1;
                        if (z0 > bmaxZ) bmaxZ = z0;
                        if (z1 > bmaxZ) bmaxZ = z1;
                    }
                    double ddx = Math.Max(0.0, Math.Max(aminX - bmaxX, bminX - amaxX));
                    double ddz = Math.Max(0.0, Math.Max(aminZ - bmaxZ, bminZ - amaxZ));
                    if (ddx * ddx + ddz * ddz > maxGap2)
                        continue;

                    // Nearest boundary-edge pair (3D distance).
                    int bestIA = -1, bestIB = -1;
                    double best = double.MaxValue;
                    for (int ka = 0; ka < ei; ka++)
                    {
                        int a0 = bA[i][ka], a1 = bB[i][ka];
                        for (int kb = 0; kb < ej; kb++)
                        {
                            int c0 = bA[j][kb], c1 = bB[j][kb];
                            double d = EdgePairDist2(o, a0, a1, c0, c1);
                            if (d < best) { best = d; bestIA = ka; bestIB = kb; }
                        }
                    }
                    if (bestIA < 0 || best > maxGap2)
                        continue;

                    int ua = bA[i][bestIA], ub = bB[i][bestIA];
                    int va = bA[j][bestIB], vb = bB[j][bestIB];
                    if (ua == va || ua == vb || ub == va || ub == vb)
                        continue;
                    float yLo = Math.Min(o.Y[ua], o.Y[va]);
                    float yHi = Math.Max(o.Y[ua], o.Y[va]);
                    if (yHi - yLo > walkableClimb * ch + 1e-4f)
                        continue;

                    pairs++;

                    // Clearance: solid geometry must not span the gap envelope. The floors themselves
                    // sit at the envelope's bottom (excluded by the 0.6*ch lift); a wall, a closed door
                    // or a ceiling within walkableHeight of the floor trips it.
                    float mxA = (o.X[ua] + o.X[ub]) * 0.5f, mzA = (o.Z[ua] + o.Z[ub]) * 0.5f;
                    float mxB = (o.X[va] + o.X[vb]) * 0.5f, mzB = (o.Z[va] + o.Z[vb]) * 0.5f;
                    float soupFloor = yLo - offset;
                    float envLo = soupFloor + 0.6f * ch;
                    float envHi = soupFloor + walkableHeight * ch;
                    if (GapEnvelopeSolid(soupVerts, soupTris, grid, bigList, soupCell,
                            mxA, mzA, mxB, mzB, cs, envLo, envHi))
                    {
                        refused++;
                        continue;
                    }

                    // Fresh-edge check, then add the quad (reusing existing vertices).
                    int mu, mv;
                    edgeMult.TryGetValue(StitchEdgeKey(ua, ub), out mu);
                    edgeMult.TryGetValue(StitchEdgeKey(va, vb), out mv);
                    if (mu != 1 || mv != 1)
                    {
                        refused++;                        // an earlier stitch already closed one side
                        continue;
                    }
                    if (edgeMult.ContainsKey(StitchEdgeKey(ua, va))
                        || edgeMult.ContainsKey(StitchEdgeKey(ub, vb))
                        || edgeMult.ContainsKey(StitchEdgeKey(ua, vb)))
                    {
                        refused++;
                        continue;
                    }
                    int t1a = ua, t1b = va, t1c = vb;
                    if (TriNormalYList(o, t1a, t1b, t1c) < 0f) { t1b = vb; t1c = va; }
                    int t2a = ua, t2b = vb, t2c = ub;
                    if (TriNormalYList(o, t2a, t2b, t2c) < 0f) { t2b = ub; t2c = vb; }
                    if (t1a == t1b || t1b == t1c || t1a == t1c || t2a == t2b || t2b == t2c || t2a == t2c)
                    {
                        refused++;
                        continue;
                    }
                    o.Tris.Add(t1a); o.Tris.Add(t1b); o.Tris.Add(t1c);
                    o.Tris.Add(t2a); o.Tris.Add(t2b); o.Tris.Add(t2c);
                    StitchRegister(edgeMult, ua, va);
                    StitchRegister(edgeMult, ub, vb);
                    StitchRegister(edgeMult, ua, vb);
                    StitchRegister(edgeMult, ua, ub);
                    StitchRegister(edgeMult, va, vb);
                    added += 2;
                    StitchUnion(compParent, i, j);
                }
            }
        }

        private static double EdgePairDist2(MergeOutput o, int a0, int a1, int b0, int b1)
        {
            double d = VDist2List(o, a0, b0);
            double t = VDist2List(o, a0, b1);
            if (t < d) d = t;
            t = VDist2List(o, a1, b0);
            if (t < d) d = t;
            t = VDist2List(o, a1, b1);
            if (t < d) d = t;
            return d;
        }

        private static double VDist2List(MergeOutput o, int i, int j)
        {
            double dx = o.X[i] - o.X[j];
            double dy = o.Y[i] - o.Y[j];
            double dz = o.Z[i] - o.Z[j];
            return dx * dx + dy * dy + dz * dz;
        }

        private static float TriNormalYList(MergeOutput o, int a, int b, int c)
        {
            float ux = o.X[b] - o.X[a], uy = o.Y[b] - o.Y[a], uz = o.Z[b] - o.Z[a];
            float wx = o.X[c] - o.X[a], wy = o.Y[c] - o.Y[a], wz = o.Z[c] - o.Z[a];
            return uz * wx - ux * wz;
        }

        private static long SoupCellKey(int x, int z)
        {
            return ((long)x << 32) | (uint)z;
        }

        // Coarse XZ grid over the input soup; triangles covering too many cells go to bigList.
        private static void BuildSoupGrid(float[] soupVerts, int[] soupTris, int triCount, float cellSize,
            out Dictionary<long, List<int>> grid, out List<int> bigList)
        {
            grid = new Dictionary<long, List<int>>();
            bigList = new List<int>();
            for (int t = 0; t < triCount; t++)
            {
                int a = soupTris[t * 3], b = soupTris[t * 3 + 1], c = soupTris[t * 3 + 2];
                float minX = Math.Min(soupVerts[a * 3], Math.Min(soupVerts[b * 3], soupVerts[c * 3]));
                float maxX = Math.Max(soupVerts[a * 3], Math.Max(soupVerts[b * 3], soupVerts[c * 3]));
                float minZ = Math.Min(soupVerts[a * 3 + 2], Math.Min(soupVerts[b * 3 + 2], soupVerts[c * 3 + 2]));
                float maxZ = Math.Max(soupVerts[a * 3 + 2], Math.Max(soupVerts[b * 3 + 2], soupVerts[c * 3 + 2]));
                int cx0 = (int)Math.Floor(minX / cellSize), cx1 = (int)Math.Floor(maxX / cellSize);
                int cz0 = (int)Math.Floor(minZ / cellSize), cz1 = (int)Math.Floor(maxZ / cellSize);
                long cells = (long)(cx1 - cx0 + 1) * (cz1 - cz0 + 1);
                if (cells > 256)
                {
                    bigList.Add(t);
                    continue;
                }
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    for (int cz = cz0; cz <= cz1; cz++)
                    {
                        long key = SoupCellKey(cx, cz);
                        List<int> list;
                        if (!grid.TryGetValue(key, out list))
                        {
                            list = new List<int>();
                            grid[key] = list;
                        }
                        list.Add(t);
                    }
                }
            }
        }

        // Walks the straight line between the two stitch edges and refuses when solid geometry spans the
        // envelope OVER that line (a wall/door in the gap, or a ceiling within walkableHeight). Testing
        // the line rather than a box keeps walls beside the corridor from refusing a valid stitch.
        private static bool GapEnvelopeSolid(float[] soupVerts, int[] soupTris,
            Dictionary<long, List<int>> grid, List<int> bigList, float cellSize,
            float x0, float z0, float x1, float z1, float cs, float yLo, float yHi)
        {
            float dx = x1 - x0, dz = z1 - z0;
            int steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dz)) / cs));
            if (steps <= 1)
                return false;
            for (int k = 1; k < steps; k++)
            {
                float px = x0 + dx * k / steps;
                float pz = z0 + dz * k / steps;
                List<int> list;
                if (grid.TryGetValue(SoupCellKey((int)Math.Floor(px / cellSize), (int)Math.Floor(pz / cellSize)),
                        out list))
                {
                    for (int i = 0; i < list.Count; i++)
                        if (TriCoversPoint(soupVerts, soupTris, list[i], px, pz, yLo, yHi))
                            return true;
                }
            }
            for (int i = 0; i < bigList.Count; i++)
            {
                float tdx = x1 - x0, tdz = z1 - z0;
                int tsteps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(tdx), Math.Abs(tdz)) / cs));
                for (int k = 1; k < tsteps; k++)
                {
                    float px = x0 + tdx * k / tsteps;
                    float pz = z0 + tdz * k / tsteps;
                    if (TriCoversPoint(soupVerts, soupTris, bigList[i], px, pz, yLo, yHi))
                        return true;
                }
            }
            return false;
        }

        // Conservative point test: the triangle's Y range spans the envelope and its XZ bbox covers the
        // sample point.
        private static bool TriCoversPoint(float[] v, int[] tris, int t, float px, float pz, float yLo, float yHi)
        {
            int a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];
            float minY = Math.Min(v[a * 3 + 1], Math.Min(v[b * 3 + 1], v[c * 3 + 1]));
            float maxY = Math.Max(v[a * 3 + 1], Math.Max(v[b * 3 + 1], v[c * 3 + 1]));
            if (maxY < yLo || minY > yHi)
                return false;
            float minX = Math.Min(v[a * 3], Math.Min(v[b * 3], v[c * 3]));
            float maxX = Math.Max(v[a * 3], Math.Max(v[b * 3], v[c * 3]));
            if (px < minX || px > maxX)
                return false;
            float minZ = Math.Min(v[a * 3 + 2], Math.Min(v[b * 3 + 2], v[c * 3 + 2]));
            float maxZ = Math.Max(v[a * 3 + 2], Math.Max(v[b * 3 + 2], v[c * 3 + 2]));
            return pz >= minZ && pz <= maxZ;
        }

        private sealed class MergeOutput
        {
            public readonly List<float> X = new List<float>();
            public readonly List<float> Y = new List<float>();
            public readonly List<float> Z = new List<float>();
            public readonly List<int> Tris = new List<int>();
            public int Degenerate;
            public float MinY = float.MaxValue;
            public float MaxY = float.MinValue;
        }

        // Fan path: exact integer voxel-lattice weld (behaviour unchanged).
        private static MergeOutput MergeFanTiles(VoxelTileResult[] results, int tilesX, int tilesZ)
        {
            var o = new MergeOutput();
            var weld = new Dictionary<VertexKey, int>();
            int mergedVertices = 0;
            for (int ty = 0; ty < tilesZ; ty++)
            {
                for (int tx = 0; tx < tilesX; tx++)
                {
                    VoxelTileResult r = results[ty * tilesX + tx];
                    if (r == null || r.Failed || r.VertsXyz == null || r.Tris == null || r.Tris.Length == 0)
                        continue;

                    int vCount = r.VertexCount;
                    var remap = new int[vCount];
                    for (int k = 0; k < vCount; k++)
                    {
                        float x = r.VertsXyz[k * 3 + 0];
                        float y = r.VertsXyz[k * 3 + 1];
                        float z = r.VertsXyz[k * 3 + 2];
                        int qx = r.WeldLattice[k * 3 + 0];
                        int qy = r.WeldLattice[k * 3 + 1];
                        int qz = r.WeldLattice[k * 3 + 2];

                        var key = new VertexKey(qx, qy, qz);
                        int mapped;
                        if (!weld.TryGetValue(key, out mapped))
                        {
                            mapped = mergedVertices;
                            weld[key] = mapped;
                            o.X.Add(x);
                            o.Y.Add(y);
                            o.Z.Add(z);
                            mergedVertices++;
                            if (y < o.MinY) o.MinY = y;
                            if (y > o.MaxY) o.MaxY = y;
                        }
                        remap[k] = mapped;
                    }

                    AppendFaces(o, r, remap);
                }
            }
            return o;
        }

        // Detail path: robust deterministic weld. Vertices are gathered from every tile in sorted
        // tile order, then clustered by a lexicographic sweep: the sorted vertices are visited in
        // order and each is merged into the first existing cluster anchor within DetailWeldTolerance
        // (3D). The first vertex of a cluster (in lexicographic order) is the canonical
        // representative. This is stable regardless of which side of a rounding boundary a float
        // lerp lands on, and it only merges points that are genuinely co-located (so distinct
        // stacked surfaces stay distinct).
        private static MergeOutput MergeDetailTiles(VoxelTileResult[] results, int tilesX, int tilesZ, float tol)
        {
            var o = new MergeOutput();
            int tileCount = results.Length;
            var tileStart = new int[tileCount];
            var gx = new List<float>();
            var gy = new List<float>();
            var gz = new List<float>();

            for (int i = 0; i < tileCount; i++)
            {
                VoxelTileResult r = results[i];
                tileStart[i] = -1;
                if (r == null || r.Failed || r.VertsXyz == null || r.Tris == null || r.Tris.Length == 0)
                    continue;
                tileStart[i] = gx.Count;
                for (int k = 0; k < r.VertexCount; k++)
                {
                    gx.Add(r.VertsXyz[k * 3 + 0]);
                    gy.Add(r.VertsXyz[k * 3 + 1]);
                    gz.Add(r.VertsXyz[k * 3 + 2]);
                }
            }

            int n = gx.Count;
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(order, new LexicographicVertexComparer(gx, gy, gz));

            float tol2 = tol * tol;
            var clusterOf = new int[n];
            var anchors = new List<int>();
            int windowStart = 0;
            for (int s = 0; s < n; s++)
            {
                int p = order[s];
                float x = gx[p], y = gy[p], z = gz[p];

                // Anchors are created in ascending x order, so drop the ones that can no longer match.
                while (windowStart < anchors.Count && gx[anchors[windowStart]] < x - tol)
                    windowStart++;

                int found = -1;
                for (int a = windowStart; a < anchors.Count; a++)
                {
                    int q = anchors[a];
                    float dx = gx[q] - x, dy = gy[q] - y, dz = gz[q] - z;
                    if (dx * dx + dy * dy + dz * dz <= tol2)
                    {
                        found = a;
                        break;
                    }
                }

                if (found >= 0)
                {
                    clusterOf[p] = found;
                }
                else
                {
                    clusterOf[p] = anchors.Count;
                    anchors.Add(p);
                }
            }

            for (int c = 0; c < anchors.Count; c++)
            {
                int p = anchors[c];
                float y = gy[p];
                o.X.Add(gx[p]);
                o.Y.Add(y);
                o.Z.Add(gz[p]);
                if (y < o.MinY) o.MinY = y;
                if (y > o.MaxY) o.MaxY = y;
            }

            for (int ty = 0; ty < tilesZ; ty++)
            {
                for (int tx = 0; tx < tilesX; tx++)
                {
                    int ti = ty * tilesX + tx;
                    if (tileStart[ti] < 0)
                        continue;
                    VoxelTileResult r = results[ti];
                    int baseIndex = tileStart[ti];
                    int vCount = r.VertexCount;
                    var remap = new int[vCount];
                    for (int k = 0; k < vCount; k++)
                        remap[k] = clusterOf[baseIndex + k];
                    AppendFaces(o, r, remap);
                }
            }
            return o;
        }

        private static void AppendFaces(MergeOutput o, VoxelTileResult r, int[] remap)
        {
            int faceCount = r.FaceCount;
            for (int f = 0; f < faceCount; f++)
            {
                int a = remap[r.Tris[f * 3 + 0]];
                int b = remap[r.Tris[f * 3 + 1]];
                int c = remap[r.Tris[f * 3 + 2]];
                if (a == b || b == c || a == c || Math.Abs(Area2(o.X, o.Y, o.Z, a, b, c)) < 1e-6f)
                {
                    o.Degenerate++;
                    continue;
                }
                o.Tris.Add(a);
                o.Tris.Add(b);
                o.Tris.Add(c);
            }
        }

        // Total order on vertex indices by (x, y, z, index) -- deterministic under Array.Sort.
        private sealed class LexicographicVertexComparer : IComparer<int>
        {
            private readonly List<float> _x;
            private readonly List<float> _y;
            private readonly List<float> _z;

            public LexicographicVertexComparer(List<float> x, List<float> y, List<float> z)
            {
                _x = x;
                _y = y;
                _z = z;
            }

            public int Compare(int a, int b)
            {
                int c = _x[a].CompareTo(_x[b]);
                if (c != 0) return c;
                c = _y[a].CompareTo(_y[b]);
                if (c != 0) return c;
                c = _z[a].CompareTo(_z[b]);
                if (c != 0) return c;
                return a.CompareTo(b);
            }
        }

        private static int SumTileVertices(VoxelTileResult[] results)
        {
            int sum = 0;
            for (int i = 0; i < results.Length; i++)
            {
                VoxelTileResult r = results[i];
                if (r != null && !r.Failed && r.VertsXyz != null)
                    sum += r.VertexCount;
            }
            return sum;
        }

        private static VoxelTileResult BuildTile(int index, int tx, int ty,
            float[] vertsXyz, int[] tris, List<int> tileList, TiledNavMeshSettings s,
            float originX, float originZ, float tileSpan, int cellsPerTile, float bminY, float bmaxY,
            int width, int height, int border, float pad,
            int walkableHeight, int walkableClimb, int walkableRadius, float maxSimplificationError,
            int minRegionArea, int mergeRegionArea, RcAreaModification areaMod,
            bool[] forceWalkableTriangles)
        {
            var r = new VoxelTileResult();
            r.Index = index;
            r.TileX = tx;
            r.TileY = ty;

            var tileWatch = Stopwatch.StartNew();
            try
            {
                float tileMinX = originX + tx * tileSpan;
                float tileMinZ = originZ + ty * tileSpan;
                float hfMinX = tileMinX - pad;
                float hfMinZ = tileMinZ - pad;
                float hfMaxX = hfMinX + width * s.VoxelCellSizeMeters;
                float hfMaxZ = hfMinZ + height * s.VoxelCellSizeMeters;

                int assigned = tileList != null ? tileList.Count : 0;
                r.RasterizedTris = assigned;
                if (assigned == 0)
                {
                    r.VertsXyz = new float[0];
                    r.Tris = new int[0];
                    r.WeldLattice = new int[0];
                    tileWatch.Stop();
                    r.ElapsedMs = tileWatch.ElapsedMilliseconds;
                    return r;
                }

                var ctx = new RcContext();
                var hf = new RcHeightfield(width, height,
                    new RcVec3f(hfMinX, bminY, hfMinZ),
                    new RcVec3f(hfMaxX, bmaxY, hfMaxZ),
                    s.VoxelCellSizeMeters, s.CellHeightMeters, border);

                // Sub-triangle index array for this tile.
                int[] subTris = new int[assigned * 3];
                for (int k = 0; k < assigned; k++)
                {
                    int src = tileList[k] * 3;
                    subTris[k * 3 + 0] = tris[src + 0];
                    subTris[k * 3 + 1] = tris[src + 1];
                    subTris[k * 3 + 2] = tris[src + 2];
                }

                int[] areaIds = RcRecast.MarkWalkableTriangles(ctx, s.MaxSlopeDegrees, vertsXyz, subTris,
                    assigned, areaMod);
                if (forceWalkableTriangles != null)
                {
                    // Synthesized ramps are walkable by design even when steeper than MaxSlopeDegrees
                    // (a ladder or a stair is meant to be traversed). Everything else is untouched.
                    for (int k = 0; k < assigned; k++)
                    {
                        int src = tileList[k];
                        if (src >= 0 && src < forceWalkableTriangles.Length && forceWalkableTriangles[src])
                            areaIds[k] = RcRecast.RC_WALKABLE_AREA;
                    }
                }
                RcRasterizations.RasterizeTriangles(ctx, vertsXyz, subTris, areaIds, assigned, hf, walkableClimb);

                RcFilters.FilterLowHangingWalkableObstacles(ctx, walkableClimb, hf);
                if (s.GapFillCells > 0)
                {
                    // Must run before the ledge filter: FilterLedgeSpans reads an in-bounds EMPTY
                    // column as a drop and deletes the floor cell beside a gap, which would widen a
                    // hairline seam by one cell per side and defeat the fill.
                    int gapFilled, gapEmpty;
                    gapFilled = FillNarrowGaps(hf, s.GapFillCells, walkableClimb, border, cellsPerTile,
                        out gapEmpty);
                    r.GapFilled = gapFilled;
                    r.GapEmptyColumns = gapEmpty;
                }
                if (s.UseLedgeFilter)
                    RcFilters.FilterLedgeSpans(ctx, walkableHeight, walkableClimb, hf);
                RcFilters.FilterWalkableLowHeightSpans(ctx, walkableHeight, hf);

                RcCompactHeightfield chf = RcCompacts.BuildCompactHeightfield(ctx, walkableHeight, walkableClimb, hf);
                RcAreas.ErodeWalkableArea(ctx, walkableRadius, chf);
                RcRegions.BuildRegionsMonotone(ctx, chf, minRegionArea, mergeRegionArea);

                RcContourSet cset = RcContours.BuildContours(ctx, chf, maxSimplificationError,
                    s.MaxEdgeLenVoxels, RcBuildContoursFlags.RC_CONTOUR_TESS_WALL_EDGES);
                RcPolyMesh pmesh = RcMeshs.BuildPolyMesh(ctx, cset, s.VertsPerPoly);
                r.PolyCount = pmesh.npolys;

                if (r.PolyCount == 0)
                {
                    r.VertsXyz = new float[0];
                    r.Tris = new int[0];
                    r.WeldLattice = new int[0];
                    tileWatch.Stop();
                    r.ElapsedMs = tileWatch.ElapsedMilliseconds;
                    return r;
                }

                if (s.UseDetailMesh)
                    BuildDetailOutput(r, pmesh, chf, ctx, s);
                else
                    BuildFanOutput(r, pmesh, tx, ty, cellsPerTile, tileSpan, originX, originZ, bminY, s);

                if (s.StitchMaxGapCells > 0 && r.FaceCount > 1)
                    r.Stitched = StitchTileComponents(r, hf, bminY, s.CellHeightMeters, s.HeightOffsetMeters,
                        walkableHeight, walkableClimb, s.StitchMaxGapCells, s.DetailWeldToleranceMeters);
            }
            catch (Exception ex)
            {
                r.Failed = true;
                r.Error = ex.GetType().Name + ": " + ex.Message;
            }

            tileWatch.Stop();
            r.ElapsedMs = tileWatch.ElapsedMilliseconds;
            return r;
        }

        // Gap fill. Runs right after the low-obstacle filter and BEFORE the ledge filter: FilterLedgeSpans
        // treats an in-bounds EMPTY column as a drop and deletes the floor cell beside a gap, which would
        // widen a hairline seam by one cell per side and defeat this pass entirely.
        //
        // A column with no walkable span is filled when walkable spans flank it on OPPOSITE sides along
        // one axis (N/S, E/W or the two diagonal axes) with an empty run of at most gapFillCells cells
        // and a floor difference within walkableClimb; the filled span takes the LOWER neighbour floor.
        //
        // Tile consistency: only cells inside THIS tile's own area are filled and only columns inside
        // this tile's padded heightfield are probed, so exactly one tile decides each world cell and the
        // fill is identical on both sides of a tile boundary (no new cracks).
        private static int FillNarrowGaps(RcHeightfield hf, int gapFillCells, int walkableClimb,
            int border, int cellsPerTile, out int emptyColumns)
        {
            emptyColumns = 0;
            int w = hf.width;
            int h = hf.height;
            int ownX0 = border;
            int ownZ0 = border;
            int ownX1 = border + cellsPerTile;
            int ownZ1 = border + cellsPerTile;
            if (ownX1 > w) ownX1 = w;
            if (ownZ1 > h) ownZ1 = h;
            if (ownX0 >= ownX1 || ownZ0 >= ownZ1)
                return 0;

            // Floor (voxel) of the first walkable span per column, -1 when the column has none. Taken
            // once so a column's flanks are only ever ORIGINAL walkable columns (no cascade).
            var floor = new int[w * h];
            for (int ci = 0; ci < w * h; ci++)
                floor[ci] = WalkableFloor(hf.spans[ci]);

            int filled = 0;
            for (int z = ownZ0; z < ownZ1; z++)
            {
                for (int x = ownX0; x < ownX1; x++)
                {
                    int ci = x + z * w;
                    if (floor[ci] >= 0)
                        continue;                       // column already walkable
                    emptyColumns++;
                    int y;
                    if (!TryGapFloor(floor, w, h, x, z, gapFillCells, walkableClimb, out y))
                        continue;
                    if (!CanPlaceSpan(hf.spans[ci], y))
                        continue;                       // something solid already occupies that height
                    InsertSpanSorted(hf, ci, y);
                    filled++;
                }
            }
            return filled;
        }

        private static int WalkableFloor(RcSpan span)
        {
            for (RcSpan s = span; s != null; s = s.next)
                if (s.area != RcRecast.RC_NULL_AREA)
                    return s.smax;
            return -1;
        }

        // True when a one-voxel solid span [y-1, y] fits without overlapping existing geometry.
        private static bool CanPlaceSpan(RcSpan span, int y)
        {
            for (RcSpan s = span; s != null; s = s.next)
                if (!(s.smax <= y - 1 || s.smin >= y))
                    return false;
            return true;
        }

        // Keeps the column sorted bottom-to-top (Recast expects spans in ascending smin order).
        private static void InsertSpanSorted(RcHeightfield hf, int columnIndex, int y)
        {
            var s = new RcSpan();
            s.smin = y - 1;
            s.smax = y;
            s.area = RcRecast.RC_WALKABLE_AREA;
            RcSpan cur = hf.spans[columnIndex];
            if (cur == null || s.smax <= cur.smin)
            {
                s.next = cur;
                hf.spans[columnIndex] = s;
                return;
            }
            while (cur.next != null && cur.next.smin < s.smin)
                cur = cur.next;
            s.next = cur.next;
            cur.next = s;
        }

        // The four axis pairs: E/W, N/S, NE/SW, NW/SE. A diagonal pair is still one axis (its own
        // diagonal line), so a fill is always justified along a single axis.
        private static readonly int[] GapAxisX = { 1, 0, 1, 1 };
        private static readonly int[] GapAxisZ = { 0, 1, 1, -1 };

        private static bool TryGapFloor(int[] floor, int w, int h, int x, int z,
            int maxRun, int walkableClimb, out int floorY)
        {
            floorY = 0;
            int bestRun = int.MaxValue;
            bool found = false;
            for (int axis = 0; axis < 4; axis++)
            {
                int dx = GapAxisX[axis];
                int dz = GapAxisZ[axis];
                int y0, d0, y1, d1;
                if (!ScanFlank(floor, w, h, x, z, dx, dz, maxRun, out y0, out d0))
                    continue;
                if (!ScanFlank(floor, w, h, x, z, -dx, -dz, maxRun, out y1, out d1))
                    continue;
                int run = d0 + d1 - 1;                 // empty cells between the two flanks
                if (run > maxRun)
                    continue;
                if (Math.Abs(y0 - y1) > walkableClimb)
                    continue;
                if (run < bestRun)
                {
                    bestRun = run;
                    floorY = Math.Min(y0, y1);
                    found = true;
                }
            }
            return found;
        }

        private static bool ScanFlank(int[] floor, int w, int h, int x, int z,
            int dx, int dz, int maxRun, out int spanY, out int dist)
        {
            spanY = 0;
            dist = 0;
            for (int d = 1; d <= maxRun; d++)
            {
                int nx = x + dx * d;
                int nz = z + dz * d;
                if (nx < 0 || nz < 0 || nx >= w || nz >= h)
                    return false;
                int ci = nx + nz * w;
                if (floor[ci] >= 0)
                {
                    spanY = floor[ci];
                    dist = d;
                    return true;
                }
            }
            return false;
        }
        // Per-tile stitch. Reconnects components of the tile's own mesh whose nearest boundary edges are
        // within stitchMaxGapCells cells and within walkableClimb vertically, by adding a small quad that
        // REUSES existing boundary vertices (so both weld paths stay exact). It is refused when the
        // tile's solid spans put geometry inside the gap envelope (a wall or closed door) or leave too
        // little headroom (a ceiling). Deterministic, and per-tile only (cross-tile continuity is
        // already exact on the lattice).
        private static int StitchTileComponents(VoxelTileResult r, RcHeightfield hf, float bminY, float ch,
            float offset, int walkableHeight, int walkableClimb, int stitchMaxGapCells, float weldTol)
        {
            int nv = r.VertexCount;
            int nf = r.FaceCount;
            if (nv < 3 || nf < 2)
                return 0;
            float cs = hf.cs;
            float maxGap = stitchMaxGapCells * cs;
            float maxGap2 = maxGap * maxGap;

            int[] weld = BuildPositionWeld(r.VertsXyz, nv, weldTol);

            // Face adjacency + edge multiplicity over welded ids.
            int[] fp = new int[nf];
            for (int i = 0; i < nf; i++) fp[i] = i;
            var edgeMult = new Dictionary<long, int>();
            for (int f = 0; f < nf; f++)
            {
                int a = weld[r.Tris[f * 3]], b = weld[r.Tris[f * 3 + 1]], c = weld[r.Tris[f * 3 + 2]];
                StitchAddEdge(edgeMult, fp, f, a, b);
                StitchAddEdge(edgeMult, fp, f, b, c);
                StitchAddEdge(edgeMult, fp, f, c, a);
            }
            // Early out: a single component (the common case) needs no stitching.
            bool multi = false;
            for (int i = 0; i < nf; i++) if (StitchFind(fp, i) != StitchFind(fp, 0)) { multi = true; break; }
            if (!multi)
                return 0;

            var compFaces = new Dictionary<int, int>();
            var compList = new List<int>();
            for (int i = 0; i < nf; i++)
            {
                int root = StitchFind(fp, i);
                if (!compFaces.ContainsKey(root)) { compFaces[root] = 0; compList.Add(root); }
                compFaces[root]++;
            }
            compList.Sort();
            int nc = compList.Count;
            if (nc < 2)
                return 0;

            // Boundary edges per component (welded edge multiplicity 1), with their original vertices.
            var compIndex = new Dictionary<int, int>();
            for (int i = 0; i < nc; i++) compIndex[compList[i]] = i;
            var bA = new List<int>[nc];
            var bB = new List<int>[nc];
            var bMin = new List<float>[nc];
            var bMax = new List<float>[nc];
            for (int i = 0; i < nc; i++)
            {
                bA[i] = new List<int>();
                bB[i] = new List<int>();
                bMin[i] = new List<float>();
                bMax[i] = new List<float>();
            }
            for (int f = 0; f < nf; f++)
            {
                int ci = compIndex[StitchFind(fp, f)];
                for (int k = 0; k < 3; k++)
                {
                    int i0 = r.Tris[f * 3 + k];
                    int i1 = r.Tris[f * 3 + ((k + 1) % 3)];
                    int w0 = weld[i0], w1 = weld[i1];
                    if (edgeMult[StitchEdgeKey(w0, w1)] != 1)
                        continue;                       // interior edge
                    // One entry per welded boundary edge (the face loop may see it twice for the two
                    // faces that share it; multiplicity 1 means it belongs to exactly one face).
                    bA[ci].Add(i0);
                    bB[ci].Add(i1);
                    float x0 = r.VertsXyz[i0 * 3], y0 = r.VertsXyz[i0 * 3 + 1], z0 = r.VertsXyz[i0 * 3 + 2];
                    float x1 = r.VertsXyz[i1 * 3], y1 = r.VertsXyz[i1 * 3 + 1], z1 = r.VertsXyz[i1 * 3 + 2];
                    bMin[ci].Add(Math.Min(x0, x1)); bMax[ci].Add(Math.Max(x0, x1));
                    bMin[ci].Add(Math.Min(z0, z1)); bMax[ci].Add(Math.Max(z0, z1));
                }
            }

            int[] compParent = new int[nc];
            for (int i = 0; i < nc; i++) compParent[i] = i;

            var tris = new List<int>(r.Tris);
            int added = 0;
            for (int i = 0; i < nc; i++)
            {
                int ei = bA[i].Count;
                if (ei == 0 || ei > 4096)
                    continue;
                for (int j = i + 1; j < nc; j++)
                {
                    int ej = bA[j].Count;
                    if (ej == 0 || ej > 4096)
                        continue;
                    if (StitchFind(compParent, i) == StitchFind(compParent, j))
                        continue;
                    // Coarse reject on the boundary AABB (XZ), in cell units.
                    float aminX = float.MaxValue, amaxX = float.MinValue, aminZ = float.MaxValue, amaxZ = float.MinValue;
                    for (int k = 0; k < ei; k++)
                    {
                        if (bMin[i][k * 2] < aminX) aminX = bMin[i][k * 2];
                        if (bMax[i][k * 2] > amaxX) amaxX = bMax[i][k * 2];
                        if (bMin[i][k * 2 + 1] < aminZ) aminZ = bMin[i][k * 2 + 1];
                        if (bMax[i][k * 2 + 1] > amaxZ) amaxZ = bMax[i][k * 2 + 1];
                    }
                    float bminX = float.MaxValue, bmaxX = float.MinValue, bminZ = float.MaxValue, bmaxZ = float.MinValue;
                    for (int k = 0; k < ej; k++)
                    {
                        if (bMin[j][k * 2] < bminX) bminX = bMin[j][k * 2];
                        if (bMax[j][k * 2] > bmaxX) bmaxX = bMax[j][k * 2];
                        if (bMin[j][k * 2 + 1] < bminZ) bminZ = bMin[j][k * 2 + 1];
                        if (bMax[j][k * 2 + 1] > bmaxZ) bmaxZ = bMax[j][k * 2 + 1];
                    }
                    double ddx = Math.Max(0.0, Math.Max(aminX - bmaxX, bminX - amaxX));
                    double ddz = Math.Max(0.0, Math.Max(aminZ - bmaxZ, bminZ - amaxZ));
                    if (ddx * ddx + ddz * ddz > maxGap2)
                        continue;

                    // Nearest boundary-edge pair.
                    int bestIA = -1, bestIB = -1;
                    double best = double.MaxValue;
                    for (int ka = 0; ka < ei; ka++)
                    {
                        int a0 = bA[i][ka], a1 = bB[i][ka];
                        for (int kb = 0; kb < ej; kb++)
                        {
                            int c0 = bA[j][kb], c1 = bB[j][kb];
                            double d = EdgePairDist2(r.VertsXyz, a0, a1, c0, c1);
                            if (d < best) { best = d; bestIA = ka; bestIB = kb; }
                        }
                    }
                    if (bestIA < 0 || best > maxGap2)
                        continue;

                    int ua = bA[i][bestIA], ub = bB[i][bestIA];
                    int va = bA[j][bestIB], vb = bB[j][bestIB];
                    if (ua == va || ua == vb || ub == va || ub == vb)
                        continue;                       // components share a vertex: skip
                    float dyAB = Math.Abs(r.VertsXyz[ua * 3 + 1] - r.VertsXyz[va * 3 + 1]);
                    if (dyAB > walkableClimb * ch + 1e-4f)
                        continue;

                    int yLo = (int)Math.Floor((Math.Min(r.VertsXyz[ua * 3 + 1], r.VertsXyz[va * 3 + 1]) - offset - bminY) / ch) - 1;
                    int yHi = (int)Math.Ceiling((Math.Max(r.VertsXyz[ua * 3 + 1], r.VertsXyz[va * 3 + 1]) - offset - bminY) / ch) + walkableHeight;
                    if (!StitchGapClear(r, hf, ua, va, yLo, yHi))
                        continue;

                    // Quad (two triangles) reusing the four boundary vertices. Keep the new edges fresh
                    // so no edge ends up shared by more than two faces, and wind with +Y normals.
                    int pmu, pmv;
                    edgeMult.TryGetValue(StitchEdgeKey(weld[ua], weld[ub]), out pmu);
                    edgeMult.TryGetValue(StitchEdgeKey(weld[va], weld[vb]), out pmv);
                    if (pmu != 1 || pmv != 1)
                        continue;   // a previous stitch already closed one side
                    if (edgeMult.ContainsKey(StitchEdgeKey(weld[ua], weld[va]))
                        || edgeMult.ContainsKey(StitchEdgeKey(weld[ub], weld[vb]))
                        || edgeMult.ContainsKey(StitchEdgeKey(weld[ua], weld[vb])))
                        continue;

                    int t1a = ua, t1b = va, t1c = vb;
                    if (TriNormalY(r.VertsXyz, t1a, t1b, t1c) < 0f) { t1b = vb; t1c = va; }
                    int t2a = ua, t2b = vb, t2c = ub;
                    if (TriNormalY(r.VertsXyz, t2a, t2b, t2c) < 0f) { t2b = ub; t2c = vb; }
                    if (t1a == t1b || t1b == t1c || t1a == t1c || t2a == t2b || t2b == t2c || t2a == t2c)
                        continue;

                    tris.Add(t1a); tris.Add(t1b); tris.Add(t1c);
                    tris.Add(t2a); tris.Add(t2b); tris.Add(t2c);
                    StitchRegister(edgeMult, weld[ua], weld[va]);
                    StitchRegister(edgeMult, weld[ub], weld[vb]);
                    StitchRegister(edgeMult, weld[ua], weld[vb]);
                    StitchRegister(edgeMult, weld[ua], weld[ub]);
                    StitchRegister(edgeMult, weld[va], weld[vb]);
                    added += 2;
                    StitchUnion(compParent, i, j);
                }
            }

            if (added == 0)
                return 0;
            r.Tris = tris.ToArray();
            r.FaceCount = r.Tris.Length / 3;
            return added;
        }

        private static void StitchAddEdge(Dictionary<long, int> mult, int[] fp, int face, int a, int b)
        {
            long key = StitchEdgeKey(a, b);
            int c;
            mult.TryGetValue(key, out c);
            mult[key] = c + 1;
            long tagged = key | FaceTagBit;
            if (c == 0)
                mult[tagged] = face;
            else
                StitchUnion(fp, mult[tagged], face);
        }

        private static long StitchEdgeKey(int a, int b)
        {
            return a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        }

        private const long FaceTagBit = long.MinValue;

        private static int StitchFind(int[] p, int x)
        {
            while (p[x] != x) { p[x] = p[p[x]]; x = p[x]; }
            return x;
        }

        private static void StitchUnion(int[] p, int a, int b)
        {
            int ra = StitchFind(p, a), rb = StitchFind(p, b);
            if (ra != rb) p[ra] = rb;
        }

        private static double EdgePairDist2(float[] v, int a0, int a1, int b0, int b1)
        {
            double d = VDist2(v, a0, b0);
            double t = VDist2(v, a0, b1);
            if (t < d) d = t;
            t = VDist2(v, a1, b0);
            if (t < d) d = t;
            t = VDist2(v, a1, b1);
            if (t < d) d = t;
            return d;
        }

        private static double VDist2(float[] v, int i, int j)
        {
            double dx = v[i * 3] - v[j * 3];
            double dy = v[i * 3 + 1] - v[j * 3 + 1];
            double dz = v[i * 3 + 2] - v[j * 3 + 2];
            return dx * dx + dy * dy + dz * dz;
        }

        private static float TriNormalY(float[] v, int a, int b, int c)
        {
            float ux = v[b * 3] - v[a * 3], uy = v[b * 3 + 1] - v[a * 3 + 1], uz = v[b * 3 + 2] - v[a * 3 + 2];
            float wx = v[c * 3] - v[a * 3], wy = v[c * 3 + 1] - v[a * 3 + 1], wz = v[c * 3 + 2] - v[a * 3 + 2];
            return uz * wx - ux * wz;
        }

        // A new edge may only be added when it does not already exist (any multiplicity).
        private static void StitchRegister(Dictionary<long, int> mult, int a, int b)
        {
            long key = StitchEdgeKey(a, b);
            int c;
            mult.TryGetValue(key, out c);
            mult[key] = c + 1;
        }

        // Clearance: sample the heightfield cells strictly between the two boundary vertices and refuse
        // when a solid span overlaps the gap envelope [yLo, yHi] (wall / closed door / low ceiling).
        private static bool StitchGapClear(VoxelTileResult r, RcHeightfield hf, int ia, int ib, int yLo, int yHi)
        {
            float cs = hf.cs;
            int cx0 = (int)Math.Floor((r.VertsXyz[ia * 3] - hf.bmin.X) / cs);
            int cz0 = (int)Math.Floor((r.VertsXyz[ia * 3 + 2] - hf.bmin.Z) / cs);
            int cx1 = (int)Math.Floor((r.VertsXyz[ib * 3] - hf.bmin.X) / cs);
            int cz1 = (int)Math.Floor((r.VertsXyz[ib * 3 + 2] - hf.bmin.Z) / cs);
            int steps = Math.Max(Math.Abs(cx1 - cx0), Math.Abs(cz1 - cz0));
            if (steps <= 1)
                return true;
            for (int i = 1; i < steps; i++)
            {
                int cx = cx0 + (int)Math.Round((double)(cx1 - cx0) * i / steps);
                int cz = cz0 + (int)Math.Round((double)(cz1 - cz0) * i / steps);
                if (cx < 0 || cz < 0 || cx >= hf.width || cz >= hf.height)
                    continue;
                for (RcSpan s = hf.spans[cx + cz * hf.width]; s != null; s = s.next)
                    if (s.smax > yLo && s.smin < yHi)
                        return false;
            }
            return true;
        }

        private static long Hash3(long x, long y, long z)
        {
            return (x * 73856093L) ^ (y * 19349663L) ^ (z * 83492791L);
        }

        // Position-based vertex weld (exact for lattice coordinates, tolerance-based for detail meshes).
        private static int[] BuildPositionWeld(float[] verts, int nv, float tol)
        {
            var ids = new int[nv];
            var grid = new Dictionary<long, List<int>>();
            var cx = new List<float>();
            var cy = new List<float>();
            var cz = new List<float>();
            float inv = 1f / tol;
            float tol2 = tol * tol;
            int next = 0;
            for (int i = 0; i < nv; i++)
            {
                float x = verts[i * 3], y = verts[i * 3 + 1], z = verts[i * 3 + 2];
                long gx = (long)Math.Floor(x * inv);
                long gy = (long)Math.Floor(y * inv);
                long gz = (long)Math.Floor(z * inv);
                int found = -1;
                for (long dx = -1; dx <= 1 && found < 0; dx++)
                {
                    for (long dy = -1; dy <= 1 && found < 0; dy++)
                    {
                        for (long dz = -1; dz <= 1 && found < 0; dz++)
                        {
                            List<int> list;
                            if (!grid.TryGetValue(Hash3(gx + dx, gy + dy, gz + dz), out list))
                                continue;
                            for (int k = 0; k < list.Count; k++)
                            {
                                int id = list[k];
                                float ddx = x - cx[id], ddy = y - cy[id], ddz = z - cz[id];
                                if (ddx * ddx + ddy * ddy + ddz * ddz <= tol2) { found = id; break; }
                            }
                        }
                    }
                }
                if (found < 0)
                {
                    found = next++;
                    cx.Add(x); cy.Add(y); cz.Add(z);
                    long key = Hash3(gx, gy, gz);
                    List<int> list;
                    if (!grid.TryGetValue(key, out list)) { list = new List<int>(); grid[key] = list; }
                    list.Add(found);
                }
                ids[i] = found;
            }
            return ids;
        }

        // Fan-triangulate each convex Recast polygon. Vertices are placed on the
        // global integer voxel lattice so adjacent tiles emit bit-identical
        // coordinates at shared boundaries (exact welding, no cracks).
        private static void BuildFanOutput(VoxelTileResult r, RcPolyMesh pmesh,
            int tx, int ty, int cellsPerTile, float tileSpan, float originX, float originZ,
            float bminY, TiledNavMeshSettings s)
        {
            int nvp = pmesh.nvp;
            int maxVerts = pmesh.nverts;
            var xs = new List<float>();
            var ys = new List<float>();
            var zs = new List<float>();
            var lat = new List<int>();
            var t = new List<int>();
            var localToOutput = new int[maxVerts];
            for (int i = 0; i < maxVerts; i++) localToOutput[i] = -1;

            float cs = s.VoxelCellSizeMeters;
            float ch = s.CellHeightMeters;
            float offset = s.HeightOffsetMeters;

            for (int poly = 0; poly < pmesh.npolys; poly++)
            {
                int p = poly * nvp * 2;
                int nv = 0;
                while (nv < nvp && pmesh.polys[p + nv] != RcRecast.RC_MESH_NULL_IDX) nv++;
                if (nv < 3) continue;

                int fan0 = -1;
                for (int k = 1; k < nv - 1; k++)
                {
                    int lv0 = pmesh.polys[p + 0];
                    int lv1 = pmesh.polys[p + k];
                    int lv2 = pmesh.polys[p + k + 1];
                    fan0 = EmitVertex(pmesh, lv0, tx, ty, cellsPerTile, originX, originZ, bminY, cs, ch, offset,
                        localToOutput, xs, ys, zs, lat);
                    int o1 = EmitVertex(pmesh, lv1, tx, ty, cellsPerTile, originX, originZ, bminY, cs, ch, offset,
                        localToOutput, xs, ys, zs, lat);
                    int o2 = EmitVertex(pmesh, lv2, tx, ty, cellsPerTile, originX, originZ, bminY, cs, ch, offset,
                        localToOutput, xs, ys, zs, lat);
                    t.Add(fan0); t.Add(o1); t.Add(o2);
                }
            }

            r.VertexCount = xs.Count;
            r.FaceCount = t.Count / 3;
            r.VertsXyz = new float[xs.Count * 3];
            r.WeldLattice = new int[xs.Count * 3];
            for (int i = 0; i < xs.Count; i++)
            {
                r.VertsXyz[i * 3 + 0] = xs[i];
                r.VertsXyz[i * 3 + 1] = ys[i];
                r.VertsXyz[i * 3 + 2] = zs[i];
                r.WeldLattice[i * 3 + 0] = lat[i * 3 + 0];
                r.WeldLattice[i * 3 + 1] = lat[i * 3 + 1];
                r.WeldLattice[i * 3 + 2] = lat[i * 3 + 2];
            }
            r.Tris = t.ToArray();
        }

        private static int EmitVertex(RcPolyMesh pmesh, int localVertex, int tx, int ty, int cellsPerTile,
            float originX, float originZ, float bminY, float cs, float ch, float offset,
            int[] localToOutput, List<float> xs, List<float> ys, List<float> zs, List<int> lat)
        {
            int existing = localToOutput[localVertex];
            if (existing >= 0)
                return existing;

            int lx = pmesh.verts[localVertex * 3 + 0];
            int ly = pmesh.verts[localVertex * 3 + 1];
            int lz = pmesh.verts[localVertex * 3 + 2];

            int gx = tx * cellsPerTile + lx;   // global voxel-lattice indices (exact)
            int gy = ly;                        // bminY is global, so ly is already global
            int gz = ty * cellsPerTile + lz;

            float x = originX + gx * cs;
            float y = bminY + gy * ch + offset;
            float z = originZ + gz * cs;

            int outIndex = xs.Count;
            xs.Add(x); ys.Add(y); zs.Add(z);
            lat.Add(gx); lat.Add(gy); lat.Add(gz);
            localToOutput[localVertex] = outIndex;
            return outIndex;
        }

        // Optional detail mesh: Recast emits world-space (Y-up) detail triangles, including the
        // upstream +chf.ch nudge. WeldLattice is left null; MergeDetailTiles clusters by position.
        private static void BuildDetailOutput(VoxelTileResult r, RcPolyMesh pmesh, RcCompactHeightfield chf,
            RcContext ctx, TiledNavMeshSettings s)
        {
            float sampleDist = EffectiveDetailSampleDist(s);
            RcPolyMeshDetail dmesh = RcMeshDetails.BuildPolyMeshDetail(ctx, pmesh, chf, sampleDist, s.MaxErrorMeters);
            if (CaptureDetailDebug && DebugDetailMesh == null)
                DebugDetailMesh = dmesh;
            if (dmesh == null || dmesh.nverts == 0 || dmesh.ntris == 0)
            {
                r.VertsXyz = new float[0];
                r.Tris = new int[0];
                r.WeldLattice = new int[0];
                return;
            }

            r.VertsXyz = new float[dmesh.nverts * 3];
            for (int i = 0; i < dmesh.nverts; i++)
            {
                r.VertsXyz[i * 3 + 0] = dmesh.verts[i * 3 + 0];
                r.VertsXyz[i * 3 + 1] = dmesh.verts[i * 3 + 1] + s.HeightOffsetMeters;
                r.VertsXyz[i * 3 + 2] = dmesh.verts[i * 3 + 2];
            }
            r.VertexCount = dmesh.nverts;
            r.WeldLattice = null;

            // ROOT-CAUSE FIX: RcMeshDetails.BuildPolyMeshDetail stores each submesh's triangles with
            // indices LOCAL to that submesh's vertex range (dmesh.meshes[sm] = (vertBase, vertCount,
            // triBase, triCount)); dmesh.tris values are 0..#vertCount-1. They must be offset by
            // vertBase to index the global dmesh.verts array. Treating them as global (the previous
            // bug) made every submesh reference submesh 0's vertices -> mass duplicate / non-manifold
            // faces (E < F) and a broken mesh the engine could not import.
            var outTris = new List<int>(dmesh.ntris * 3);
            for (int sm = 0; sm < dmesh.nmeshes; sm++)
            {
                int vertBase = dmesh.meshes[sm * 4 + 0];
                int vertCount = dmesh.meshes[sm * 4 + 1];
                int triBase = dmesh.meshes[sm * 4 + 2];
                int triCount = dmesh.meshes[sm * 4 + 3];
                if (vertCount == 0 || triCount == 0)
                    continue;

                for (int k = 0; k < triCount; k++)
                {
                    int t = (triBase + k) * 4;
                    outTris.Add(dmesh.tris[t + 0] + vertBase);
                    outTris.Add(dmesh.tris[t + 1] + vertBase);
                    outTris.Add(dmesh.tris[t + 2] + vertBase);
                }
            }
            r.Tris = outTris.ToArray();
            r.FaceCount = r.Tris.Length / 3;
        }

        // Twice the signed area of the triangle projected on the XZ plane
        // (Recast up-axis is Y, so we test the horizontal footprint).
        private static float Area2(List<float> x, List<float> y, List<float> z, int a, int b, int c)
        {
            float abx = x[b] - x[a];
            float abz = z[b] - z[a];
            float acx = x[c] - x[a];
            float acz = z[c] - z[a];
            return abx * acz - abz * acx;
        }

        private struct VertexKey : IEquatable<VertexKey>
        {
            public readonly int X;
            public readonly int Y;
            public readonly int Z;

            public VertexKey(int x, int y, int z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            public bool Equals(VertexKey other)
            {
                return X == other.X && Y == other.Y && Z == other.Z;
            }

            public override bool Equals(object obj)
            {
                return obj is VertexKey && Equals((VertexKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = 17;
                    h = h * 31 + X;
                    h = h * 31 + Y;
                    h = h * 31 + Z;
                    return h;
                }
            }
        }

        // Deterministic non-manifold repair: while any edge is shared by > 2 faces, remove the
        // highest-index face on that edge (and re-evaluate). Removes as few faces as possible and is
        // order-independent (edges processed in sorted key order). Returns the number removed.
        private static int RepairNonManifold(MergeOutput o)
        {
            int faceCount = o.Tris.Count / 3;
            if (faceCount == 0)
                return 0;

            var count = new Dictionary<long, int>(LongKeyComparer.Instance);
            var perEdge = new Dictionary<long, List<int>>(LongKeyComparer.Instance);
            for (int i = 0; i < faceCount; i++)
            {
                int a = o.Tris[i * 3], b = o.Tris[i * 3 + 1], c = o.Tris[i * 3 + 2];
                AddFaceEdge(count, perEdge, i, a, b);
                AddFaceEdge(count, perEdge, i, b, c);
                AddFaceEdge(count, perEdge, i, c, a);
            }

            var removed = new bool[faceCount];
            int removedCount = 0;
            bool changed = true;
            while (changed)
            {
                changed = false;
                var bad = new List<long>();
                foreach (KeyValuePair<long, int> kv in count)
                    if (kv.Value > 2) bad.Add(kv.Key);
                if (bad.Count == 0) break;
                bad.Sort();

                for (int e = 0; e < bad.Count; e++)
                {
                    List<int> list = perEdge[bad[e]];
                    int active = 0;
                    for (int i = 0; i < list.Count; i++) if (!removed[list[i]]) active++;
                    if (active <= 2) continue;

                    list.Sort();
                    for (int i = list.Count - 1; i >= 0 && active > 2; i--)
                    {
                        int ff = list[i];
                        if (removed[ff]) continue;
                        removed[ff] = true;
                        removedCount++;
                        active--;
                        changed = true;
                        int a = o.Tris[ff * 3], b = o.Tris[ff * 3 + 1], c = o.Tris[ff * 3 + 2];
                        DecEdge(count, a, b);
                        DecEdge(count, b, c);
                        DecEdge(count, c, a);
                    }
                }
            }

            if (removedCount > 0)
            {
                var keep = new List<int>(o.Tris.Count);
                for (int i = 0; i < faceCount; i++)
                {
                    if (removed[i]) continue;
                    keep.Add(o.Tris[i * 3]);
                    keep.Add(o.Tris[i * 3 + 1]);
                    keep.Add(o.Tris[i * 3 + 2]);
                }
                o.Tris.Clear();
                o.Tris.AddRange(keep);
            }
            return removedCount;
        }

        private static void AddFaceEdge(Dictionary<long, int> count, Dictionary<long, List<int>> perEdge,
            int face, int a, int b)
        {
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            int c;
            count.TryGetValue(key, out c);
            count[key] = c + 1;
            List<int> list;
            if (!perEdge.TryGetValue(key, out list))
            {
                list = new List<int>();
                perEdge[key] = list;
            }
            list.Add(face);
        }

        private static void DecEdge(Dictionary<long, int> count, int a, int b)
        {
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            int c;
            if (count.TryGetValue(key, out c) && c > 0)
                count[key] = c - 1;
        }

        // Topology invariants for the merged mesh. A well-formed triangle soup over a surface has
        // every edge shared by at most 2 faces, no duplicate faces and no self-loops; then
        // E == (3F + boundary)/2 exactly. These gate the bin write + import.
        private static void ComputeInvariants(VoxelMeshResult result, int[] tris, int faceCount)
        {
            if (tris == null || faceCount == 0)
            {
                result.EdgeCount = 0;
                result.BoundaryEdges = 0;
                result.MaxEdgeMultiplicity = 0;
                result.DuplicateFaces = 0;
                result.SelfLoops = 0;
                result.RequiredEdges = 0;
                result.InvariantsPassed = faceCount == 0;
                return;
            }

            var edgeMult = new Dictionary<long, int>(LongKeyComparer.Instance);
            var faces = new Dictionary<FaceKey, int>();
            int selfLoops = 0;
            int dupFaces = 0;

            for (int f = 0; f < faceCount; f++)
            {
                int a = tris[f * 3 + 0];
                int b = tris[f * 3 + 1];
                int c = tris[f * 3 + 2];
                if (a == b || b == c || a == c)
                {
                    selfLoops++;
                    continue;
                }

                var fk = new FaceKey(a, b, c);
                int seen;
                if (faces.TryGetValue(fk, out seen))
                {
                    faces[fk] = seen + 1;
                    dupFaces++;
                }
                else
                {
                    faces[fk] = 1;
                }

                AddEdgeMult(edgeMult, a, b);
                AddEdgeMult(edgeMult, b, c);
                AddEdgeMult(edgeMult, c, a);
            }

            int boundary = 0;
            int maxMult = 0;
            foreach (KeyValuePair<long, int> kv in edgeMult)
            {
                if (kv.Value == 1) boundary++;
                if (kv.Value > maxMult) maxMult = kv.Value;
            }

            result.EdgeCount = edgeMult.Count;
            result.BoundaryEdges = boundary;
            result.MaxEdgeMultiplicity = maxMult;
            result.DuplicateFaces = dupFaces;
            result.SelfLoops = selfLoops;
            result.RequiredEdges = (3 * faceCount + boundary) / 2;
            result.InvariantsPassed = maxMult <= 2 && dupFaces == 0 && selfLoops == 0
                && result.EdgeCount >= result.RequiredEdges;
        }

        private static void AddEdgeMult(Dictionary<long, int> map, int a, int b)
        {
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            int c;
            map.TryGetValue(key, out c);
            map[key] = c + 1;
        }

        // Undirected face key (vertices sorted ascending), for duplicate detection.
        private struct FaceKey : IEquatable<FaceKey>
        {
            private readonly int _a;
            private readonly int _b;
            private readonly int _c;

            public FaceKey(int a, int b, int c)
            {
                int lo, mid, hi;
                if (a <= b && a <= c) { lo = a; if (b <= c) { mid = b; hi = c; } else { mid = c; hi = b; } }
                else if (b <= a && b <= c) { lo = b; if (a <= c) { mid = a; hi = c; } else { mid = c; hi = a; } }
                else { lo = c; if (a <= b) { mid = a; hi = b; } else { mid = b; hi = a; } }
                _a = lo; _b = mid; _c = hi;
            }

            public bool Equals(FaceKey other)
            {
                return _a == other._a && _b == other._b && _c == other._c;
            }

            public override bool Equals(object obj)
            {
                return obj is FaceKey && Equals((FaceKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = 17;
                    h = h * 31 + _a;
                    h = h * 31 + _b;
                    h = h * 31 + _c;
                    return h;
                }
            }
        }

        private sealed class LongKeyComparer : IEqualityComparer<long>
        {
            public static readonly LongKeyComparer Instance = new LongKeyComparer();

            public bool Equals(long a, long b)
            {
                return a == b;
            }

            public int GetHashCode(long value)
            {
                unchecked
                {
                    ulong x = (ulong)value;
                    x ^= x >> 33;
                    x *= 0xff51afd7ed558ccdUL;
                    x ^= x >> 33;
                    x *= 0xc4ceb9fe1a85ec53UL;
                    x ^= x >> 33;
                    return (int)x;
                }
            }
        }

        // ------------------------------------------------------------------
        // Agent climb derivation (pure; used by VoxelNavMeshGenerator and the harness)
        // ------------------------------------------------------------------
        // Recast rejects a span when the spread of its traversable neighbours' floors exceeds
        // walkableClimb. On a uniform slope the rise per cell is cs*tan(slope)/ch voxels and the
        // spread is twice that, so walkableClimb must be >= 2*cs*tan(slope)/ch for the slope to
        // survive the ledge filter. Derive the climb in whole ch steps, clamped to minClimb.
        // (A larger climb also connects single vertical steps up to that height; the trade-off is
        // documented in the README. Lowering VoxelCellSizeMeters also lowers the required climb.)
        public static float ResolveAgentClimbMeters(float cs, float ch, float slopeDegrees, float minClimb)
        {
            if (cs <= 0f || ch <= 0f)
                return minClimb;

            double slopeRad = slopeDegrees * Math.PI / 180.0;
            int climbCells = (int)Math.Ceiling(2.0 * cs * Math.Tan(slopeRad) / ch);
            if (climbCells < 1)
                climbCells = 1;

            float climb = climbCells * ch;
            return climb < minClimb ? minClimb : climb;
        }

        // walkableClimb in voxel steps for a resolved climb. Mirrors the build's derivation,
        // including the float-roundoff epsilon so e.g. 2.4/0.2 is 12, not 11.
        public static int WalkableClimbVoxels(float climbMeters, float ch)
        {
            if (ch <= 0f)
                return 0;
            return (int)Math.Floor(climbMeters / ch + 1e-4f);
        }

        // Effective detail-mesh sampling distance (m): explicit override, else max(0.9, cs).
        public static float EffectiveDetailSampleDist(TiledNavMeshSettings s)
        {
            return s.DetailSampleDistMeters > 0f ? s.DetailSampleDistMeters : Math.Max(0.9f, s.VoxelCellSizeMeters);
        }

        // Effective contour simplification error (voxels): explicit override, else MaxErrorMeters/cs.
        public static float EffectiveContourMaxError(TiledNavMeshSettings s)
        {
            return s.ContourMaxErrorVoxels > 0f ? s.ContourMaxErrorVoxels : s.MaxErrorMeters / s.VoxelCellSizeMeters;
        }

        private static void Log(Action<string> log, string message)
        {
            if (log != null)
                log(message);
        }

        private static string F(float value)
        {
            return value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
