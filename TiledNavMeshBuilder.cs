using System;
using System.Collections.Generic;
using System.Diagnostics;
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

            public float AgentRadiusMeters = 0.35f;
            public float AgentHeightMeters = 1.8f;
            public float AgentMaxClimbMeters = 0.4f;

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
        public static VoxelMeshResult Build(float[] vertsXyz, int[] tris, TiledNavMeshSettings s,
            int maxWorkers, Action<string> log)
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
            float maxSimplificationError = s.MaxErrorMeters / cs;
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
                + " detail=" + s.UseDetailMesh);
            Log(log, "v2: contour maxError=" + F(maxSimplificationError) + "vox (= " + F(s.MaxErrorMeters)
                + "m at cs=" + F(cs) + "m); plan's ~1.3vox heuristic not used, metre budget honoured");
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

            // --- parallel per-tile build ---
            var results = new VoxelTileResult[tileCount];
            var tilesWatch = Stopwatch.StartNew();
            var parallelOptions = new ParallelOptions();
            parallelOptions.MaxDegreeOfParallelism = workers;
            Parallel.For(0, tileCount, parallelOptions, delegate (int index)
            {
                int tx = index % tilesX;
                int ty = index / tilesX;
                results[index] = BuildTile(index, tx, ty, vertsXyz, tris, tileTris[index], s,
                    originX, originZ, tileSpan, cellsPerTile, bminY, bmaxY, width, height, border, pad,
                    walkableHeight, walkableClimb, walkableRadius, maxSimplificationError,
                    minRegionArea, mergeRegionArea, areaMod);
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

            total.Stop();
            result.TotalMs = total.ElapsedMilliseconds;
            Log(log, "v2 timings: tilesMs=" + result.TilesMs + " mergeMs=" + result.MergeMs
                + " totalMs=" + result.TotalMs);
            Log(log, "v2: tileMs min/avg/max=" + result.TileMsMin + "/" + result.TileMsAvg + "/" + result.TileMsMax);

            return result;
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
            int minRegionArea, int mergeRegionArea, RcAreaModification areaMod)
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
                RcRasterizations.RasterizeTriangles(ctx, vertsXyz, subTris, areaIds, assigned, hf, walkableClimb);

                RcFilters.FilterLowHangingWalkableObstacles(ctx, walkableClimb, hf);
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
            float sampleDist = Math.Max(0.9f, s.VoxelCellSizeMeters);
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
