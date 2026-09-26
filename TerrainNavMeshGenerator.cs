using System;
using System.Collections.Generic;
using System.IO;
using TaleWorlds.Engine;

namespace BetterBannerlordNavmeshing
{

    // ==================================================================
    // Navmesh prefab export
    // ==================================================================
    // Builds the "NMG8" navmesh prefab .bin the Bannerlord editor imports.
    // A terrain only provides heights, so vertices and faces are generated
    // here and the edge table is rebuilt from the face perimeters in
    // first-encounter order -- the same semantics the scene converter's
    // NavMeshProcessor uses. Everything in this class is plain BCL: it never
    // touches TaleWorlds types, so it can be verified outside the editor.
    public static class TerrainNavMeshBuilder
    {
        // "NMG8" read as a little-endian uint32. The .bin is a 4-byte body
        // length (stripped by rglBuffer before import_stream) followed by
        // this magic.
        private const uint Nmg8Version = 0x38474D4E;

        // 0xFFFFFFFF = every agent level. The level groups themselves are
        // left empty; the editor can populate cost data later.
        private const uint LevelMaskAll = 0xFFFFFFFF;
        private const int LevelGroupCount = 32;

        // Legacy edge pad fields. import_stream discards all three.
        private const int PadInt = -1;


        // Phase timings (ms) from the most recent Build; read by the test harness.
        public static long LastRefineMs;
        public static long LastBalanceMs;
        public static long LastEmitMs;
        public static long LastCompactMs;
        public static long LastEdgeMs;
        public static long LastSerializeMs;

        public sealed class Settings
        {
            // Sampling stride in terrain vertices. 1 keeps every vertex.
            public int SampleStride = 1;

            // Adaptive meshing: max allowed deviation (meters) between the mesh
            // surface and the bilinear terrain surface inside a cell.
            public float MaxErrorMeters = 0.5f;

            // Largest leaf size in meters, rounded down to a power-of-two
            // multiple of the base cell spacing.
            public float CoarsestSpacingMeters = 32f;

            // Uniform vertical shift applied to emitted vertices only. Culling
            // still uses the true terrain heights; the offset lifts the mesh off
            // the ground to avoid z-fighting.
            public float HeightOffsetMeters = 0.5f;

            // Triangles whose normal tilts more than this are dropped.
            // 90f (or more) disables slope culling entirely.
            public float MaxSlopeDegrees = 65f;

            // When true, triangles whose lowest corner dips below MinHeight
            // are dropped as well.
            public bool CullBelowHeight = false;
            public float MinHeight = 0f;

            // When true, Result.ObjText is filled in as well as the binary.
            public bool WriteObj = false;
        }

        public sealed class Result
        {
            public byte[] Bin;          // full NMG8 bin (with length prefix); never null
            public string ObjText;      // null unless Settings.WriteObj
            public int VertexCount;
            public int EdgeCount;
            public int FaceCount;
            public int CulledBySlope;
            public int CulledByHeight;
            public float MinZ;
            public float MaxZ;
        }

        // Builds a navmesh over a stitched heightmap. stitchedHeights is
        // indexed [x * height + y]; the world position of stitched vertex
        // (x, y) is (x * spacing, y * spacing, height).
        //
        // The mesh is an error-bounded adaptive quadtree: flat or smooth
        // regions collapse into large cells (up to CoarsestSpacingMeters) while
        // rough or steep regions refine down to the base grid (SampleStride).
        public static Result Build(float[] stitchedHeights, int width, int height,
            float terrainVertexSpacing, Settings settings)
        {
            if (width < 2)
                throw new ArgumentException("width must be at least 2.", nameof(width));
            if (height < 2)
                throw new ArgumentException("height must be at least 2.", nameof(height));
            if (terrainVertexSpacing <= 0f)
                throw new ArgumentException("terrainVertexSpacing must be greater than 0.", nameof(terrainVertexSpacing));
            if (stitchedHeights == null)
                throw new ArgumentException("stitchedHeights must not be null.", nameof(stitchedHeights));
            if (stitchedHeights.Length != width * height)
                throw new ArgumentException("stitchedHeights length must equal width * height.", nameof(stitchedHeights));

            // A null Settings just means "use the defaults"; only genuinely
            // invalid numbers are treated as programmer errors.
            if (settings == null)
                settings = new Settings();

            int stride = settings.SampleStride;
            if (stride < 1)
                throw new ArgumentException("Settings.SampleStride must be at least 1.", nameof(settings));

            // Sample axes always include their first and last source vertex,
            // so the mesh still spans the full terrain even when stride does
            // not divide the vertex count evenly.
            int[] sampleX = BuildSampleAxis(width, stride);
            int[] sampleY = BuildSampleAxis(height, stride);
            int samplesX = sampleX.Length;
            int samplesY = sampleY.Length;
            int sampleCount = samplesX * samplesY;

            float[] px = new float[sampleCount];
            float[] py = new float[sampleCount];
            float[] pz = new float[sampleCount];

            for (int sx = 0; sx < samplesX; sx++)
            {
                int sourceX = sampleX[sx];
                for (int sy = 0; sy < samplesY; sy++)
                {
                    int sourceY = sampleY[sy];
                    int s = SampleIndex(sx, sy, samplesY);
                    px[s] = sourceX * terrainVertexSpacing;
                    py[s] = sourceY * terrainVertexSpacing;
                    pz[s] = stitchedHeights[sourceX * height + sourceY];
                }
            }

            var result = new Result();
            int culledBySlope = 0;
            int culledByHeight = 0;

            var vx = new List<float>();
            var vy = new List<float>();
            var vz = new List<float>();
            var triangles = new List<int>();

            int cellsX = samplesX - 1;
            int cellsY = samplesY - 1;
            if (cellsX > 0 && cellsY > 0)
            {
                var mesher = new AdaptiveMesher(
                    px, py, pz, samplesY, cellsX, cellsY, stride * terrainVertexSpacing, settings,
                    vx, vy, vz, triangles);
                mesher.Run();
                culledBySlope = mesher.CulledBySlope;
                culledByHeight = mesher.CulledByHeight;
            }

            var compactWatch = System.Diagnostics.Stopwatch.StartNew();

            // Compact into flat arrays: only vertices a surviving face
            // references are kept. Vertex ids were handed out in deterministic
            // emission order, so the result is stable across runs. Flat arrays
            // (not List<float[]>/List<int[]>) keep large meshes allocation-light.
            int totalVertices = vx.Count;
            bool[] referenced = new bool[totalVertices];
            for (int i = 0; i < triangles.Count; i++)
                referenced[triangles[i]] = true;

            int[] remap = new int[totalVertices];
            float[] cvx = new float[totalVertices];
            float[] cvy = new float[totalVertices];
            float[] cvz = new float[totalVertices];
            int vertexCount = 0;
            float minZ = 0f;
            float maxZ = 0f;
            bool anyVertex = false;

            for (int i = 0; i < totalVertices; i++)
            {
                if (!referenced[i])
                    continue;

                remap[i] = vertexCount;
                float z = vz[i] + settings.HeightOffsetMeters;
                cvx[vertexCount] = vx[i];
                cvy[vertexCount] = vy[i];
                cvz[vertexCount] = z;
                vertexCount++;

                if (!anyVertex)
                {
                    minZ = z;
                    maxZ = z;
                    anyVertex = true;
                }
                else
                {
                    if (z < minZ) minZ = z;
                    if (z > maxZ) maxZ = z;
                }
            }

            int faceCount = triangles.Count / 3;
            int[] ctri = new int[faceCount * 3];
            for (int i = 0; i < ctri.Length; i++)
                ctri[i] = remap[triangles[i]];

            compactWatch.Stop();
            TerrainNavMeshBuilder.LastCompactMs = compactWatch.ElapsedMilliseconds;

            var edgeWatch = System.Diagnostics.Stopwatch.StartNew();
            var edgeMap = new Dictionary<long, int>(LongKeyComparer.Instance);
            var edgeA = new List<int>();
            var edgeB = new List<int>();
            for (int f = 0; f < faceCount; f++)
            {
                int faceBase = f * 3;
                for (int i = 0; i < 3; i++)
                {
                    int a = ctri[faceBase + i];
                    int b = ctri[faceBase + (i + 1) % 3];
                    long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    if (!edgeMap.ContainsKey(key))
                    {
                        edgeMap[key] = edgeA.Count;
                        edgeA.Add(a < b ? a : b);
                        edgeB.Add(a < b ? b : a);
                    }
                }
            }

            edgeWatch.Stop();
            TerrainNavMeshBuilder.LastEdgeMs = edgeWatch.ElapsedMilliseconds;
            var serializeWatch = System.Diagnostics.Stopwatch.StartNew();
            result.Bin = WriteNmg8Flat(cvx, cvy, cvz, vertexCount, ctri, faceCount, edgeA, edgeB, edgeMap);
            result.ObjText = settings.WriteObj
                ? BuildObjTextFlat(cvx, cvy, cvz, vertexCount, ctri, faceCount)
                : null;
            serializeWatch.Stop();
            TerrainNavMeshBuilder.LastSerializeMs = serializeWatch.ElapsedMilliseconds;
            result.VertexCount = vertexCount;
            result.EdgeCount = edgeA.Count;
            result.FaceCount = faceCount;
            result.CulledBySlope = culledBySlope;
            result.CulledByHeight = culledByHeight;
            result.MinZ = minZ;
            result.MaxZ = maxZ;
            return result;
        }

        // Serialises vertices/faces into a full NMG8 .bin (length prefix
        // included). Edges are rebuilt from the face perimeters, so callers
        // only ever need geometry.
        public static byte[] SerializeNmg8(List<float[]> vertices, List<int[]> faces)
        {
            List<int[]> edges;
            List<int[]> faceEdges;
            BuildTopology(faces, out edges, out faceEdges);
            return WriteNmg8(vertices, faces, edges, faceEdges);
        }

        // Source indices actually sampled on one axis. The last row/column
        // is always pulled in so the clipped terrain edge is still covered.
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

        private static int SampleIndex(int sx, int sy, int samplesY)
        {
            return sx * samplesY + sy;
        }

        // Murmur3-style mix for the packed (min,max) edge key. The default
        // Int64 hash (lo ^ hi) collides heavily on structured grids and makes
        // the edge dictionary degrade to O(n^2).
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

        // Error-bounded adaptive quadtree over the base sample grid. Leaves are
        // squares of 2^level base cells; the tree is restricted to 2:1 so every
        // shared edge can be stitched with a single midpoint. That keeps the
        // mesh conforming (no T-junctions) while using triangles only.
        private sealed class AdaptiveMesher
        {
            private const int MaxLeafLevel = 10;

            private readonly float[] _px;
            private readonly float[] _py;
            private readonly float[] _pz;
            private readonly int _samplesY;
            private readonly int _cellsX;
            private readonly int _cellsY;
            private readonly Settings _settings;
            private readonly bool _slopeCulling;
            private readonly float _tanLimitSq;
            private readonly int _maxLevel;

            private readonly bool[] _steep;       // per base cell: slope above limit
            private readonly int[] _levels;       // per base cell: leaf level
            private readonly int[] _nodeVertexId; // per base node: vertex id or -1

            private readonly List<float> _vx;
            private readonly List<float> _vy;
            private readonly List<float> _vz;
            private readonly List<int> _faces;

            private readonly int[] _poly = new int[8];
            private readonly int[] _tri = new int[8];

            private int _culledBySlope;
            private int _culledByHeight;

            public int CulledBySlope { get { return _culledBySlope; } }
            public int CulledByHeight { get { return _culledByHeight; } }

            public AdaptiveMesher(float[] px, float[] py, float[] pz, int samplesY,
                int cellsX, int cellsY, float finestSpacing, Settings settings,
                List<float> vx, List<float> vy, List<float> vz, List<int> faces)
            {
                _px = px;
                _py = py;
                _pz = pz;
                _samplesY = samplesY;
                _cellsX = cellsX;
                _cellsY = cellsY;
                _settings = settings;
                _vx = vx;
                _vy = vy;
                _vz = vz;
                _faces = faces;

                _slopeCulling = settings.MaxSlopeDegrees < 90f;
                double tanLimit = System.Math.Tan(settings.MaxSlopeDegrees * System.Math.PI / 180.0);
                _tanLimitSq = (float)(tanLimit * tanLimit);

                _steep = new bool[cellsX * cellsY];
                _levels = new int[cellsX * cellsY];
                for (int i = 0; i < _levels.Length; i++)
                    _levels[i] = -1;

                _nodeVertexId = new int[px.Length];
                for (int i = 0; i < _nodeVertexId.Length; i++)
                    _nodeVertexId[i] = -1;

                if (_slopeCulling)
                {
                    for (int ci = 0; ci < cellsX; ci++)
                    {
                        for (int cj = 0; cj < cellsY; cj++)
                        {
                            int a = ci * _samplesY + cj;
                            int b = (ci + 1) * _samplesY + cj;
                            int d = (ci + 1) * _samplesY + (cj + 1);
                            int c = ci * _samplesY + (cj + 1);
                            _steep[ci * _cellsY + cj] = TooSteep(a, b, d) || TooSteep(a, d, c);
                        }
                    }
                }

                _maxLevel = ComputeMaxLevel(finestSpacing, settings.CoarsestSpacingMeters);
            }

            public void Run()
            {
                // The base extent is tiled by root blocks of 2^maxLevel cells;
                // the border strip that does not fit a full root resolves to
                // finer levels inside Refine/Reconstruct.
                int rootSize = 1 << _maxLevel;

                var refineWatch = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < _cellsX; i += rootSize)
                {
                    for (int j = 0; j < _cellsY; j += rootSize)
                        Refine(i, j, _maxLevel);
                }
                refineWatch.Stop();
                TerrainNavMeshBuilder.LastRefineMs = refineWatch.ElapsedMilliseconds;

                var balanceWatch = System.Diagnostics.Stopwatch.StartNew();
                Balance();
                balanceWatch.Stop();
                TerrainNavMeshBuilder.LastBalanceMs = balanceWatch.ElapsedMilliseconds;

                var emitWatch = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < _cellsX; i += rootSize)
                {
                    for (int j = 0; j < _cellsY; j += rootSize)
                        Reconstruct(i, j, _maxLevel);
                }
                emitWatch.Stop();
                TerrainNavMeshBuilder.LastEmitMs = emitWatch.ElapsedMilliseconds;
            }

            private static int ComputeMaxLevel(float finestSpacing, float coarsestSpacingMeters)
            {
                int level = 0;
                if (finestSpacing > 0f && coarsestSpacingMeters > 0f)
                {
                    double ratio = coarsestSpacingMeters / finestSpacing;
                    if (ratio >= 1.0)
                        level = (int)System.Math.Floor(System.Math.Log(ratio) / System.Math.Log(2.0));
                }
                if (level < 0) level = 0;
                if (level > MaxLeafLevel) level = MaxLeafLevel;
                return level;
            }

            private bool TooSteep(int a, int b, int c)
            {
                float ux = _px[b] - _px[a];
                float uy = _py[b] - _py[a];
                float uz = _pz[b] - _pz[a];
                float vx = _px[c] - _px[a];
                float vy = _py[c] - _py[a];
                float vz = _pz[c] - _pz[a];
                float nx = uy * vz - uz * vy;
                float ny = uz * vx - ux * vz;
                float nz = ux * vy - uy * vx;
                if (nx == 0f && ny == 0f && nz == 0f) return false;
                if (nz <= 0f) return true;
                return (nx * nx + ny * ny) > nz * nz * _tanLimitSq;
            }

            // Splits a block into 4 unless it fits, is level 0, and is both
            // within the error tolerance and free of cells above the slope limit.
            private void Refine(int i, int j, int level)
            {
                int size = 1 << level;
                if (i >= _cellsX || j >= _cellsY)
                    return;

                bool fits = (i + size <= _cellsX) && (j + size <= _cellsY);
                if (fits && (level == 0 || IsLeaf(i, j, size)))
                {
                    for (int x = i; x < i + size; x++)
                    {
                        int row = x * _cellsY;
                        for (int y = j; y < j + size; y++)
                            _levels[row + y] = level;
                    }
                    return;
                }

                if (level == 0)
                    return;

                int half = size >> 1;
                Refine(i, j, level - 1);
                Refine(i + half, j, level - 1);
                Refine(i, j + half, level - 1);
                Refine(i + half, j + half, level - 1);
            }

            private bool IsLeaf(int i, int j, int size)
            {
                for (int x = i; x < i + size; x++)
                {
                    int row = x * _cellsY;
                    for (int y = j; y < j + size; y++)
                        if (_steep[row + y])
                            return false;
                }

                float tolerance = _settings.MaxErrorMeters;
                if (tolerance < 0f)
                    tolerance = 0f;

                float h00 = _pz[i * _samplesY + j];
                float h10 = _pz[(i + size) * _samplesY + j];
                float h01 = _pz[i * _samplesY + (j + size)];
                float h11 = _pz[(i + size) * _samplesY + (j + size)];

                // Evaluate every base node inside the block (corners
                // included): cheap at these sizes and it makes the error bound
                // honest rather than relying on a sparse subsample.
                for (int ox = 0; ox <= size; ox++)
                {
                    float u = (float)ox / size;
                    for (int oy = 0; oy <= size; oy++)
                    {
                        float v = (float)oy / size;
                        float actual = _pz[(i + ox) * _samplesY + (j + oy)];
                        float bilinear = h00 * (1f - u) * (1f - v)
                            + h10 * u * (1f - v)
                            + h01 * (1f - u) * v
                            + h11 * u * v;
                        if (System.Math.Abs(actual - bilinear) > tolerance)
                            return false;

                        // Also bound the triangulated surface the emitter
                        // builds for an unstitched quad (diagonal BR-TL), so
                        // the mesh itself -- not just the bilinear patch --
                        // stays within tolerance.
                        float triA = (u + v <= 1f)
                            ? (1f - u - v) * h00 + u * h10 + v * h01
                            : (1f - v) * h10 + (u + v - 1f) * h11 + (1f - u) * h01;
                        if (System.Math.Abs(actual - triA) > tolerance)
                            return false;

                        float triB = (u >= v)
                            ? (1f - u) * h00 + (u - v) * h10 + v * h11
                            : (1f - v) * h00 + u * h11 + (v - u) * h01;
                        if (System.Math.Abs(actual - triB) > tolerance)
                            return false;
                    }
                }

                return true;
            }

            // 2:1 restriction as a worklist: a cell is only re-examined when a
            // neighbouring leaf is split, so balancing stays near-linear instead
            // of rescanning the whole tree after every change.
            private void Balance()
            {
                var queue = new Queue<int>(_levels.Length);
                for (int i = 0; i < _levels.Length; i++)
                {
                    if (_levels[i] >= 0)
                        queue.Enqueue(i);
                }

                int guard = 0;
                int maxOps = _levels.Length * 8 + 4096;
                while (queue.Count > 0 && guard++ < maxOps)
                {
                    int idx = queue.Dequeue();
                    if (_levels[idx] < 0)
                        continue;

                    int ci = idx / _cellsY;
                    int cj = idx - ci * _cellsY;

                    if (ci + 1 < _cellsX)
                        BalancePair(idx, idx + _cellsY, queue);
                    if (cj + 1 < _cellsY)
                        BalancePair(idx, idx + 1, queue);
                }
            }

            private void BalancePair(int a, int b, Queue<int> queue)
            {
                int la = _levels[a];
                int lb = _levels[b];
                if (la - lb > 1)
                    SplitCellAt(a, queue);
                else if (lb - la > 1)
                    SplitCellAt(b, queue);
            }

            // Splits the leaf containing cellIndex, then re-queues the split
            // region plus a one-cell ring so adjacent pairs are re-checked.
            private void SplitCellAt(int cellIndex, Queue<int> queue)
            {
                int ci = cellIndex / _cellsY;
                int cj = cellIndex - ci * _cellsY;
                int level = _levels[cellIndex];
                if (level <= 0)
                    return;

                SplitLeaf(ci, cj, level);

                int size = 1 << level;
                int i = ci & ~(size - 1);
                int j = cj & ~(size - 1);
                int x0 = i - 1 < 0 ? 0 : i - 1;
                int y0 = j - 1 < 0 ? 0 : j - 1;
                int x1 = i + size < _cellsX ? i + size : _cellsX - 1;
                int y1 = j + size < _cellsY ? j + size : _cellsY - 1;

                for (int x = x0; x <= x1; x++)
                {
                    int row = x * _cellsY;
                    for (int y = y0; y <= y1; y++)
                        queue.Enqueue(row + y);
                }
            }

            private void SplitLeaf(int ci, int cj, int level)
            {
                if (level <= 0)
                    return;

                int size = 1 << level;
                int i = ci & ~(size - 1);
                int j = cj & ~(size - 1);
                int childLevel = level - 1;

                for (int x = i; x < i + size && x < _cellsX; x++)
                {
                    int row = x * _cellsY;
                    for (int y = j; y < j + size && y < _cellsY; y++)
                        _levels[row + y] = childLevel;
                }
            }

            // Rebuilds the leaves from the level map (a leaf is a maximal
            // uniform square) and emits them top-down.
            private void Reconstruct(int i, int j, int level)
            {
                int size = 1 << level;
                if (i >= _cellsX || j >= _cellsY)
                    return;

                bool fits = (i + size <= _cellsX) && (j + size <= _cellsY);
                if (fits && AllLevel(i, j, size, level))
                {
                    EmitLeaf(i, j, size);
                    return;
                }

                if (level == 0)
                    return;

                int half = size >> 1;
                Reconstruct(i, j, level - 1);
                Reconstruct(i + half, j, level - 1);
                Reconstruct(i, j + half, level - 1);
                Reconstruct(i + half, j + half, level - 1);
            }

            private bool AllLevel(int i, int j, int size, int level)
            {
                for (int x = i; x < i + size; x++)
                {
                    int row = x * _cellsY;
                    for (int y = j; y < j + size; y++)
                        if (_levels[row + y] != level)
                            return false;
                }
                return true;
            }

            private int Node(int nx, int ny)
            {
                return nx * _samplesY + ny;
            }

            private void EmitLeaf(int i, int j, int size)
            {
                int x1 = i + size;
                int y1 = j + size;
                int half = size >> 1;

                bool splitBottom = NeighborFiner(i, j, size, 0);
                bool splitRight = NeighborFiner(i, j, size, 1);
                bool splitTop = NeighborFiner(i, j, size, 2);
                bool splitLeft = NeighborFiner(i, j, size, 3);

                int c0 = Node(i, j);                           // bottom-left
                int c1 = Node(x1, j);                          // bottom-right
                int c2 = Node(x1, y1);                         // top-right
                int c3 = Node(i, y1);                          // top-left

                int n = 0;
                _poly[n++] = c0;
                if (splitBottom)
                    _poly[n++] = Node(i + half, j);            // bottom midpoint
                _poly[n++] = c1;
                if (splitRight)
                    _poly[n++] = Node(x1, j + half);           // right midpoint
                _poly[n++] = c2;
                if (splitTop)
                    _poly[n++] = Node(i + half, y1);           // top midpoint
                _poly[n++] = c3;
                if (splitLeft)
                    _poly[n++] = Node(i, j + half);            // left midpoint

                Triangulate(_poly, n, splitBottom, splitRight, splitTop, splitLeft, c0, c1, c2, c3);
            }

            // A leaf edge needs a midpoint when the leaf across it is one level
            // finer. Balance guarantees the whole edge sees the same neighbour
            // level, so checking every cell across the edge is safe.
            private bool NeighborFiner(int i, int j, int size, int side)
            {
                int level = _levels[i * _cellsY + j];
                switch (side)
                {
                    case 0: // bottom (outside row j-1)
                        if (j - 1 < 0) return false;
                        for (int dx = 0; dx < size; dx++)
                            if (_levels[(i + dx) * _cellsY + (j - 1)] < level) return true;
                        return false;
                    case 1: // right (outside column i+size)
                        if (i + size >= _cellsX) return false;
                        for (int dy = 0; dy < size; dy++)
                            if (_levels[(i + size) * _cellsY + (j + dy)] < level) return true;
                        return false;
                    case 2: // top (outside row j+size)
                        if (j + size >= _cellsY) return false;
                        for (int dx = 0; dx < size; dx++)
                            if (_levels[(i + dx) * _cellsY + (j + size)] < level) return true;
                        return false;
                    default: // left (outside column i-1)
                        if (i - 1 < 0) return false;
                        for (int dy = 0; dy < size; dy++)
                            if (_levels[(i - 1) * _cellsY + (j + dy)] < level) return true;
                        return false;
                }
            }

            // Ear clipping. A corner-hub fan would emit degenerate triangles on
            // stitched edges (the midpoints are collinear with the hub) and,
            // once those are dropped, leave a T-junction behind; ear clipping
            // keeps every stitched half-edge in a positive-area triangle.
            private void Triangulate(int[] poly, int n,
                bool splitBottom, bool splitRight, bool splitTop, bool splitLeft,
                int c0, int c1, int c2, int c3)
            {
                // Fan hub: the corner with the fewest split incident edges
                // (lowest index on ties). A clean hub yields a fan with no
                // degenerate triangles; otherwise fall back to ear clipping.
                int s0 = (splitLeft ? 1 : 0) + (splitBottom ? 1 : 0);
                int s1 = (splitBottom ? 1 : 0) + (splitRight ? 1 : 0);
                int s2 = (splitRight ? 1 : 0) + (splitTop ? 1 : 0);
                int s3 = (splitTop ? 1 : 0) + (splitLeft ? 1 : 0);

                int bestCorner = 0;
                int bestCount = s0;
                if (s1 < bestCount) { bestCorner = 1; bestCount = s1; }
                if (s2 < bestCount) { bestCorner = 2; bestCount = s2; }
                if (s3 < bestCount) { bestCorner = 3; bestCount = s3; }

                if (bestCount == 0)
                {
                    int hub = bestCorner == 0 ? c0 : bestCorner == 1 ? c1 : bestCorner == 2 ? c2 : c3;
                    int start = 0;
                    for (int k = 0; k < n; k++)
                    {
                        if (poly[k] == hub)
                        {
                            start = k;
                            break;
                        }
                    }

                    for (int t = 1; t + 1 < n; t++)
                    {
                        AddTriangle(poly[start], poly[(start + t) % n], poly[(start + t + 1) % n]);
                    }
                    return;
                }

                for (int i = 0; i < n; i++)
                    _tri[i] = poly[i];

                int count = n;
                int guard = 0;
                while (count > 3 && guard++ < 16)
                {
                    bool clipped = false;
                    for (int i = 0; i < count; i++)
                    {
                        int previous = _tri[(i + count - 1) % count];
                        int current = _tri[i];
                        int next = _tri[(i + 1) % count];
                        if (CrossZ(previous, current, next) <= 0f)
                            continue;
                        if (ContainsOtherVertex(previous, current, next, count))
                            continue;

                        AddTriangle(previous, current, next);
                        for (int k = i; k < count - 1; k++)
                            _tri[k] = _tri[k + 1];
                        count--;
                        clipped = true;
                        break;
                    }

                    if (!clipped)
                        break;
                }

                if (count == 3)
                {
                    AddTriangle(_tri[0], _tri[1], _tri[2]);
                }
                else if (count > 3)
                {
                    for (int t = 1; t + 1 < count; t++)
                        AddTriangle(_tri[0], _tri[t], _tri[t + 1]);
                }
            }

            // True when some other polygon vertex lies inside (or on the edge
            // of) the candidate ear. Clipping such a triangle would skip a
            // stitched boundary vertex and leave a T-junction.
            private bool ContainsOtherVertex(int a, int b, int c, int count)
            {
                for (int i = 0; i < count; i++)
                {
                    int v = _tri[i];
                    if (v == a || v == b || v == c)
                        continue;
                    if (InTriangle(a, b, c, v))
                        return true;
                }
                return false;
            }

            private bool InTriangle(int a, int b, int c, int p)
            {
                float ax = _px[a], ay = _py[a];
                float bx = _px[b], by = _py[b];
                float cx = _px[c], cy = _py[c];
                float px = _px[p], py = _py[p];

                float den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
                if (den == 0f)
                    return false;

                float w0 = ((by - cy) * (px - cx) + (cx - bx) * (py - cy)) / den;
                float w1 = ((cy - ay) * (px - cx) + (ax - cx) * (py - cy)) / den;
                float w2 = 1f - w0 - w1;
                const float eps = 1e-6f;
                return w0 >= -eps && w1 >= -eps && w2 >= -eps;
            }

            private float CrossZ(int a, int b, int c)
            {
                return (_px[b] - _px[a]) * (_py[c] - _py[a])
                     - (_py[b] - _py[a]) * (_px[c] - _px[a]);
            }

            private int GetVertex(int nodeIndex)
            {
                int id = _nodeVertexId[nodeIndex];
                if (id >= 0)
                    return id;

                id = _vx.Count;
                _nodeVertexId[nodeIndex] = id;
                _vx.Add(_px[nodeIndex]);
                _vy.Add(_py[nodeIndex]);
                _vz.Add(_pz[nodeIndex]);
                return id;
            }

            // Slope, degeneracy and (optional) height culling for one triangle.
            // Slope compares nx^2+ny^2 against nz^2*tan^2(limit), so there is no
            // atan2 in the hot path.
            private void AddTriangle(int na, int nb, int nc)
            {
                int a = GetVertex(na);
                int b = GetVertex(nb);
                int c = GetVertex(nc);

                float ux = _vx[b] - _vx[a];
                float uy = _vy[b] - _vy[a];
                float uz = _vz[b] - _vz[a];
                float vx = _vx[c] - _vx[a];
                float vy = _vy[c] - _vy[a];
                float vz = _vz[c] - _vz[a];

                float nx = uy * vz - uz * vy;
                float ny = uz * vx - ux * vz;
                float nz = ux * vy - uy * vx;

                if (nx == 0f && ny == 0f && nz == 0f)
                    return;

                if (_slopeCulling)
                {
                    if (nz <= 0f || (nx * nx + ny * ny) > nz * nz * _tanLimitSq)
                    {
                        _culledBySlope++;
                        return;
                    }
                }

                if (_settings.CullBelowHeight)
                {
                    float minCornerZ = System.Math.Min(_vz[a], System.Math.Min(_vz[b], _vz[c]));
                    if (minCornerZ < _settings.MinHeight)
                    {
                        _culledByHeight++;
                        return;
                    }
                }

                _faces.Add(a);
                _faces.Add(b);
                _faces.Add(c);
            }
        }

        // Unique undirected edges keyed (min, max), in first-encounter order
        // over the face perimeters, plus each face's edge indices. This is
        // exactly the order the NMG8 importer rebuilds adjacency from.
        private static void BuildTopology(List<int[]> faces,
            out List<int[]> edges, out List<int[]> faceEdges)
        {
            var edgeMap = new Dictionary<long, int>(LongKeyComparer.Instance);
            edges = new List<int[]>();
            faceEdges = new List<int[]>();

            foreach (int[] face in faces)
            {
                int n = face.Length;
                int[] fe = new int[n];
                for (int i = 0; i < n; i++)
                {
                    int a = face[i];
                    int b = face[(i + 1) % n];
                    int lo = a < b ? a : b;
                    int hi = a < b ? b : a;
                    long key = ((long)lo << 32) | (uint)hi;

                    int edgeIndex;
                    if (!edgeMap.TryGetValue(key, out edgeIndex))
                    {
                        edgeIndex = edges.Count;
                        edgeMap[key] = edgeIndex;
                        edges.Add(new int[] { lo, hi });
                    }
                    fe[i] = edgeIndex;
                }
                faceEdges.Add(fe);
            }
        }

        // Flat-array NMG8 writer used by Build: writes straight from the
        // compacted vertex/triangle arrays (no per-face allocations).
        private static byte[] WriteNmg8Flat(float[] vx, float[] vy, float[] vz, int vertexCount,
            int[] tri, int faceCount, List<int> edgeA, List<int> edgeB,
            Dictionary<long, int> edgeMap)
        {
            // Exact body size: magic(4) + vcount(4) + verts(12V) + ecount(4)
            // + edges(20E) + fcount(4) + faces(49F) + groups(4 + 32*8).
            int bodyLength = 4 + 4 + vertexCount * 12 + 4 + edgeA.Count * 20
                + 4 + faceCount * 49 + 4 + LevelGroupCount * 8;

            using (var stream = new MemoryStream(bodyLength + 4))
            {
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                {
                    writer.Write((uint)bodyLength);
                    writer.Write(Nmg8Version);

                    writer.Write(vertexCount);
                    for (int i = 0; i < vertexCount; i++)
                    {
                        writer.Write(vx[i]);
                        writer.Write(vy[i]);
                        writer.Write(vz[i]);
                    }

                    writer.Write(edgeA.Count);
                    for (int i = 0; i < edgeA.Count; i++)
                    {
                        writer.Write(PadInt);
                        writer.Write(edgeA[i]);
                        writer.Write(edgeB[i]);
                        writer.Write(PadInt);
                        writer.Write(PadInt);
                    }

                    writer.Write(faceCount);
                    for (int f = 0; f < faceCount; f++)
                    {
                        int b0 = tri[f * 3];
                        int b1 = tri[f * 3 + 1];
                        int b2 = tri[f * 3 + 2];

                        writer.Write(3);
                        writer.Write(b0);
                        writer.Write(b1);
                        writer.Write(b2);

                        writer.Write(EdgeIndex(edgeMap, b0, b1));
                        writer.Write(EdgeIndex(edgeMap, b1, b2));
                        writer.Write(EdgeIndex(edgeMap, b2, b0));

                        writer.Write(2);
                        writer.Write(0);
                        writer.Write(0);
                        writer.Write(0f);
                        writer.Write(LevelMaskAll);
                        writer.Write((byte)0);
                    }

                    writer.Write(LevelGroupCount);
                    for (int g = 0; g < LevelGroupCount; g++)
                    {
                        writer.Write(0);
                        writer.Write(0);
                    }

                    writer.Flush();
                }

                return stream.ToArray();
            }
        }

        private static int EdgeIndex(Dictionary<long, int> edgeMap, int a, int b)
        {
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            return edgeMap[key];
        }

        private static string BuildObjTextFlat(float[] vx, float[] vy, float[] vz, int vertexCount,
            int[] tri, int faceCount)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            sb.Append("# terrain navmesh");
            sb.AppendLine();
            sb.Append("# vertices ").Append(vertexCount.ToString(ic))
              .Append(" faces ").Append(faceCount.ToString(ic));
            sb.AppendLine();

            for (int i = 0; i < vertexCount; i++)
            {
                sb.Append("v ")
                  .Append(vx[i].ToString("R", ic)).Append(' ')
                  .Append(vy[i].ToString("R", ic)).Append(' ')
                  .Append(vz[i].ToString("R", ic)).AppendLine();
            }

            for (int f = 0; f < faceCount; f++)
            {
                sb.Append("f ")
                  .Append((tri[f * 3] + 1).ToString(ic)).Append(' ')
                  .Append((tri[f * 3 + 1] + 1).ToString(ic)).Append(' ')
                  .Append((tri[f * 3 + 2] + 1).ToString(ic)).AppendLine();
            }

            return sb.ToString();
        }

        private static byte[] WriteNmg8(List<float[]> vertices, List<int[]> faces,
            List<int[]> edges, List<int[]> faceEdges)
        {
            byte[] body;
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                {
                    writer.Write(Nmg8Version);

                    writer.Write(vertices.Count);
                    foreach (float[] vertex in vertices)
                    {
                        writer.Write(vertex[0]);
                        writer.Write(vertex[1]);
                        writer.Write(vertex[2]);
                    }

                    // File format: [pad(-1), vertA, vertB, pad(-1), pad(-1)].
                    writer.Write(edges.Count);
                    foreach (int[] edge in edges)
                    {
                        writer.Write(PadInt);
                        writer.Write(edge[0]);
                        writer.Write(edge[1]);
                        writer.Write(PadInt);
                        writer.Write(PadInt);
                    }

                    writer.Write(faces.Count);
                    for (int i = 0; i < faces.Count; i++)
                    {
                        int[] face = faces[i];
                        int vertexCount = face.Length;

                        writer.Write(vertexCount);
                        for (int j = 0; j < vertexCount; j++)
                            writer.Write(face[j]);

                        int[] faceEdge = faceEdges[i];
                        for (int j = 0; j < vertexCount; j++)
                            writer.Write(faceEdge[j]);

                        // Per-face versioned fields (version_fields_count = 2):
                        // field 0 (>= v2): is_concave, field 1 (>= v1): level_cost_index.
                        writer.Write(2);
                        writer.Write(0);
                        writer.Write(0);

                        writer.Write(0f);            // cost
                        writer.Write(LevelMaskAll);  // level_mask (all agents)
                        writer.Write((byte)0);       // inverted flag
                    }

                    writer.Write(LevelGroupCount);
                    for (int g = 0; g < LevelGroupCount; g++)
                    {
                        writer.Write(0);   // capacity
                        writer.Write(0);   // used
                    }

                    writer.Flush();
                }

                body = stream.ToArray();
            }

            // Prepend the 4-byte body length the importer strips first.
            var full = new byte[body.Length + 4];
            uint length = (uint)body.Length;
            full[0] = (byte)length;
            full[1] = (byte)(length >> 8);
            full[2] = (byte)(length >> 16);
            full[3] = (byte)(length >> 24);
            System.Buffer.BlockCopy(body, 0, full, 4, body.Length);
            return full;
        }

        // Debug view of the same mesh. OBJ indices are 1-based and every
        // number uses InvariantCulture so the text is machine-stable.
        private static string BuildObjText(List<float[]> vertices, List<int[]> faces)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            sb.Append("# terrain navmesh");
            sb.AppendLine();
            sb.Append("# vertices ").Append(vertices.Count.ToString(ic))
              .Append(" faces ").Append(faces.Count.ToString(ic));
            sb.AppendLine();

            foreach (float[] vertex in vertices)
            {
                sb.Append("v ")
                  .Append(vertex[0].ToString("R", ic)).Append(' ')
                  .Append(vertex[1].ToString("R", ic)).Append(' ')
                  .Append(vertex[2].ToString("R", ic)).AppendLine();
            }

            foreach (int[] face in faces)
            {
                sb.Append('f');
                for (int i = 0; i < face.Length; i++)
                    sb.Append(' ').Append((face[i] + 1).ToString(ic));
                sb.AppendLine();
            }

            return sb.ToString();
        }
    }

    // ==================================================================
    // Navmesh generation / import orchestration
    // ==================================================================
    // Turns a stitched heightmap into an NMG8 navmesh prefab and, when asked,
    // imports its faces straight into a scene -- the same thing the editor's
    // "Import faces from prefabs" does, but this also works in the client at
    // runtime. This class is the thin engine-facing wrapper around
    // TerrainNavMeshBuilder, which stays free of TaleWorlds types so it can be
    // tested on its own.
    public static class TerrainNavMeshGenerator
    {
        public sealed class ExportResult
        {
            public string BinPath;
            public string ObjPath;          // null unless an OBJ was written
            public string PrefabName;
            public int VertexCount;
            public int EdgeCount;
            public int FaceCount;
            public int CulledBySlope;
            public int CulledByHeight;
            public int BinByteCount;
            public bool ImportAttempted;
        }

        public static ExportResult ExportAndImport(
            Scene scene,
            float[] stitchedHeights,
            int width,
            int height,
            float terrainVertexSpacing,
            TerrainNavMeshBuilder.Settings settings,
            string prefabNameOverride,
            string folderOverride,
            bool clearExistingFaces,
            bool importIntoScene,
            System.Action<string> log)
        {
            if (scene == null)
                throw new ArgumentNullException("scene");

            var buildWatch = System.Diagnostics.Stopwatch.StartNew();
            TerrainNavMeshBuilder.Result mesh = TerrainNavMeshBuilder.Build(
                stitchedHeights, width, height, terrainVertexSpacing, settings);
            buildWatch.Stop();

            if (log != null)
            {
                int effectiveStride = settings != null ? settings.SampleStride : 4;
                int samplesX = (width - 1) / effectiveStride + 1;
                int samplesY = (height - 1) / effectiveStride + 1;
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                log("build done: samples=" + samplesX + "x" + samplesY
                    + " V=" + mesh.VertexCount + " E=" + mesh.EdgeCount + " F=" + mesh.FaceCount
                    + " culled(slope=" + mesh.CulledBySlope + ",height=" + mesh.CulledByHeight + ")"
                    + " z=[" + mesh.MinZ.ToString(ic) + ".." + mesh.MaxZ.ToString(ic) + "]"
                    + " binBytes=" + mesh.Bin.Length
                    + " elapsedMs=" + buildWatch.ElapsedMilliseconds);
            }

            if (mesh.FaceCount == 0)
                throw new InvalidOperationException(
                    "No navmesh faces survived culling; nothing was written. " +
                    "Check NavMeshMaxSlopeDegrees / NavMeshSampleStride.");

            string prefabName = ResolvePrefabName(scene, prefabNameOverride, log);
            string folder = ResolveFolder(scene, folderOverride, log);
            Directory.CreateDirectory(folder);

            string binPath = System.IO.Path.Combine(folder, prefabName + ".bin");
            if (log != null)
                log("writing bin: " + binPath);
            File.WriteAllBytes(binPath, mesh.Bin);

            string objPath = null;
            if (settings != null && settings.WriteObj && mesh.ObjText != null)
            {
                objPath = System.IO.Path.Combine(folder, prefabName + ".obj");
                File.WriteAllText(objPath, mesh.ObjText, System.Text.Encoding.UTF8);
            }

            var result = new ExportResult();
            result.BinPath = binPath;
            result.ObjPath = objPath;
            result.PrefabName = prefabName;
            result.VertexCount = mesh.VertexCount;
            result.EdgeCount = mesh.EdgeCount;
            result.FaceCount = mesh.FaceCount;
            result.CulledBySlope = mesh.CulledBySlope;
            result.CulledByHeight = mesh.CulledByHeight;
            result.BinByteCount = mesh.Bin.Length;

            if (importIntoScene)
            {
                // Find out whether there is anything to clear first: clearing a
                // scene that has zero navmesh faces can crash the native
                // navmesh code, so an unknown or zero face count means "don't
                // clear".
                int existingFaceCount = 0;
                bool faceCountKnown = false;
                try
                {
                    existingFaceCount = scene.GetNavMeshFaceCount();
                    faceCountKnown = true;
                }
                catch (Exception ex)
                {
                    if (log != null)
                        log("existing navmesh face count unavailable: " + ex.Message);
                }

                if (faceCountKnown && log != null)
                    log("existing navmesh faces: " + existingFaceCount);

                if (clearExistingFaces && faceCountKnown && existingFaceCount > 0)
                {
                    if (log != null)
                        log("clearing existing navmesh faces...");

                    try
                    {
                        scene.ClearNavMesh();

                        if (log != null)
                            log("cleared.");

                        try
                        {
                            if (log != null)
                                log("faces after clear: " + scene.GetNavMeshFaceCount());
                        }
                        catch (Exception ex)
                        {
                            if (log != null)
                                log("face count after clear unavailable: " + ex.Message);
                        }
                    }
                    catch (Exception ex)
                    {
                        // A failed clear must not stop the import: the prefab
                        // faces are added on top either way.
                        if (log != null)
                            log("clear failed (continuing): " + ex.Message);
                    }
                }

                if (log != null)
                    log("importing prefab '" + prefabName + "' (shift 0)...");

                scene.ImportNavigationMeshPrefab(prefabName, 0);

                if (log != null)
                    log("import call returned.");

                // The key evidence for whether the import actually added faces.
                try
                {
                    if (log != null)
                        log("faces after import: " + scene.GetNavMeshFaceCount());
                }
                catch (Exception ex)
                {
                    if (log != null)
                        log("face count after import unavailable: " + ex.Message);
                }

                // Group 0 is the reserved static group; the engine refuses
                // enable/disable on it, and static faces are always enabled, so
                // no SetAbilityOfFacesWithId call is needed.
                result.ImportAttempted = true;
            }

            return result;
        }

        // prefabNameOverride wins; otherwise the scene name plus "_navmesh".
        private static string ResolvePrefabName(Scene scene, string prefabNameOverride, System.Action<string> log)
        {
            if (!string.IsNullOrEmpty(prefabNameOverride))
            {
                if (log != null)
                    log("prefab name: " + prefabNameOverride);
                return prefabNameOverride;
            }

            string sceneName = scene.GetName();
            if (string.IsNullOrEmpty(sceneName))
                throw new InvalidOperationException(
                    "Could not derive a navmesh prefab name from the scene; set NavMeshPrefabName.");

            string defaultName = sceneName + "_navmesh";
            if (log != null)
                log("prefab name: " + defaultName + " (scene-derived default)");
            return defaultName;
        }

        // Picks the module folder that receives the navmesh prefab. Order:
        // an explicit override, then the module whose SceneObj owns the scene,
        // then the scene's own (verified) module path, then the module that
        // contains this script's assembly. Anything unverifiable is rejected
        // and logged rather than guessed at.
        private static string ResolveFolder(Scene scene, string folderOverride, System.Action<string> log)
        {
            if (!string.IsNullOrEmpty(folderOverride))
            {
                if (log != null)
                    log("folder: override ('NavMeshExportFolder')");
                return folderOverride;
            }

            // Most reliable: find the module whose SceneObj contains the scene.
            string sceneName = null;
            try
            {
                sceneName = scene.GetName();
            }
            catch (Exception)
            {
                sceneName = null;
            }

            if (!string.IsNullOrEmpty(sceneName))
            {
                foreach (string gameRoot in GetGameRootCandidates())
                {
                    try
                    {
                        string modulesDir = System.IO.Path.Combine(gameRoot, "Modules");
                        if (!Directory.Exists(modulesDir))
                            continue;

                        foreach (string moduleDir in Directory.GetDirectories(modulesDir))
                        {
                            if (Directory.Exists(System.IO.Path.Combine(moduleDir, "SceneObj", sceneName)))
                            {
                                if (log != null)
                                    log("folder: module containing the scene: " + moduleDir);
                                return System.IO.Path.Combine(moduleDir, "NavMeshPrefabs");
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // Skip an unreadable candidate root and try the next one.
                    }
                }

                if (log != null)
                    log("folder: scene '" + sceneName + "' not found under any module's SceneObj");
            }

            // Then the scene's own module path -- accepted only if it really is
            // a module (i.e. it has a SubModule.xml).
            try
            {
                string modulePath = scene.GetModulePath();
                if (!string.IsNullOrEmpty(modulePath) &&
                    File.Exists(System.IO.Path.Combine(modulePath, "SubModule.xml")))
                {
                    if (log != null)
                        log("folder: Scene.GetModulePath()");
                    return System.IO.Path.Combine(modulePath, "NavMeshPrefabs");
                }

                if (log != null)
                    log("folder: rejected Scene.GetModulePath() result (not a module): " + modulePath);
            }
            catch (Exception ex)
            {
                if (log != null)
                    log("folder: Scene.GetModulePath() failed: " + ex.Message);
            }

            // Last resort: the module that contains this script's assembly.
            try
            {
                string assemblyLocation = typeof(TerrainNavMeshGenerator).Assembly.Location;
                string dir = string.IsNullOrEmpty(assemblyLocation)
                    ? null
                    : System.IO.Path.GetDirectoryName(assemblyLocation);

                while (!string.IsNullOrEmpty(dir))
                {
                    if (File.Exists(System.IO.Path.Combine(dir, "SubModule.xml")))
                    {
                        if (log != null)
                            log("folder: module containing the script assembly: " + dir);
                        return System.IO.Path.Combine(dir, "NavMeshPrefabs");
                    }

                    dir = System.IO.Path.GetDirectoryName(dir);
                }
            }
            catch (Exception ex)
            {
                if (log != null)
                    log("folder: script assembly location failed: " + ex.Message);
            }

            throw new InvalidOperationException(
                "Could not resolve the module NavMeshPrefabs folder. Set NavMeshExportFolder manually.");
        }

        // Distinct likely game-root folders, from two independent sources so a
        // missing one is simply skipped.
        private static List<string> GetGameRootCandidates()
        {
            var roots = new List<string>();

            // (a) Walk up from the script assembly until an ancestor named
            // "Modules"; its parent is the game root.
            try
            {
                string assemblyLocation = typeof(TerrainNavMeshGenerator).Assembly.Location;
                string dir = string.IsNullOrEmpty(assemblyLocation)
                    ? null
                    : System.IO.Path.GetDirectoryName(assemblyLocation);

                while (!string.IsNullOrEmpty(dir))
                {
                    string leaf = System.IO.Path.GetFileName(dir);
                    if (!string.IsNullOrEmpty(leaf) &&
                        string.Equals(leaf, "Modules", StringComparison.OrdinalIgnoreCase))
                    {
                        string parent = System.IO.Path.GetDirectoryName(dir);
                        if (!string.IsNullOrEmpty(parent))
                            AddDistinct(roots, parent);
                        break;
                    }

                    dir = System.IO.Path.GetDirectoryName(dir);
                }
            }
            catch (Exception)
            {
                // The other candidate source may still work.
            }

            // (b) The engine's own base path, when the running build exposes it.
            try
            {
                string basePath = TryGetLibraryBasePath();
                if (!string.IsNullOrEmpty(basePath))
                {
                    string full = System.IO.Path.GetFullPath(basePath);
                    full = full.TrimEnd(System.IO.Path.DirectorySeparatorChar,
                                        System.IO.Path.AltDirectorySeparatorChar);
                    if (!string.IsNullOrEmpty(full) &&
                        Directory.Exists(System.IO.Path.Combine(full, "Modules")))
                    {
                        AddDistinct(roots, full);
                    }
                }
            }
            catch (Exception)
            {
                // BasePath is not always available.
            }

            return roots;
        }

        private static void AddDistinct(List<string> list, string value)
        {
            foreach (string existing in list)
            {
                if (string.Equals(existing, value, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            list.Add(value);
        }

        // TaleWorlds.Library.BasePath.Name exists in some game builds but not in
        // the editor build this script is compiled against, so it is resolved by
        // reflection rather than a hard reference.
        private static string TryGetLibraryBasePath()
        {
            try
            {
                System.Reflection.Assembly library = typeof(TaleWorlds.Library.Vec3).Assembly;
                Type type = library.GetType("TaleWorlds.Library.BasePath", false);
                if (type == null)
                    return null;

                System.Reflection.PropertyInfo property = type.GetProperty("Name",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (property != null)
                    return property.GetValue(null, null) as string;

                System.Reflection.FieldInfo field = type.GetField("Name",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (field != null)
                    return field.GetValue(null) as string;
            }
            catch (Exception)
            {
                // Not available in this build.
            }

            return null;
        }
    }
}