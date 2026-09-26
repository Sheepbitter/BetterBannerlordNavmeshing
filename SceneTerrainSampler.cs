using System;
using System.Globalization;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace BetterBannerlordNavmeshing
{
    // Reads a scene's terrain into a flat height grid the mesher can consume,
    // independently of any terrain generator. Primary path uses the bulk
    // per-node API (GetTerrainNodeData + GetTerrainHeightData); if that fails,
    // it falls back to point-sampling every grid vertex with GetTerrainHeight.
    //
    // Every validation result is emitted as one atomic single-line log entry.
    public static class SceneTerrainSampler
    {
        public sealed class TerrainGrid
        {
            public float[] Heights;
            public int Width;
            public int Height;
            public float Spacing;
            public bool UsedPointFallback;
        }

        // Tolerance for comparing a node's stored vertex against a point sample.
        private const float SampleTolerance = 0.25f;

        public static TerrainGrid Read(Scene scene, System.Action<string> log)
        {
            if (scene == null)
                throw new ArgumentNullException("scene");

            if (!scene.HasTerrainHeightmap)
                throw new InvalidOperationException(
                    "Scene has no terrain heightmap (HasTerrainHeightmap == false).");

            Vec2i nodeDimension;
            float nodeSize;
            int layerCount;
            int layerVersion;
            scene.GetTerrainData(out nodeDimension, out nodeSize, out layerCount, out layerVersion);

            int nodeCountX = nodeDimension.Item1;
            int nodeCountY = nodeDimension.Item2;
            Emit(log, "terrain: nodes=" + nodeCountX + "x" + nodeCountY
                + " nodeSize=" + F(nodeSize)
                + " layers=" + layerCount + " layerVersion=" + layerVersion
                + " extent=" + F(nodeSize * nodeCountX) + "x" + F(nodeSize * nodeCountY) + "m");

            if (nodeCountX <= 0 || nodeCountY <= 0)
                throw new InvalidOperationException("Terrain node dimensions are zero.");

            int probeVerts;
            float probeQuad;
            float probeMin;
            float probeMax;
            scene.GetTerrainNodeData(0, 0, out probeVerts, out probeQuad, out probeMin, out probeMax);
            if (probeVerts < 2 || probeQuad <= 0f)
                throw new InvalidOperationException(
                    "GetTerrainNodeData returned invalid node geometry (verts=" + probeVerts
                    + " quadLen=" + F(probeQuad) + ").");

            int verts = probeVerts;
            float quadLen = probeQuad;
            float expectedQuad = nodeSize / (verts - 1);
            Emit(log, "node(0,0): verts=" + verts + " quadLen=" + F(quadLen)
                + " expectedQuad=" + F(expectedQuad)
                + " minHeight=" + F(probeMin) + " maxHeight=" + F(probeMax));

            int globalWidth = nodeCountX * (verts - 1) + 1;
            int globalHeight = nodeCountY * (verts - 1) + 1;
            Emit(log, "grid: " + globalWidth + "x" + globalHeight + " spacing=" + F(quadLen) + "m");

            try
            {
                TerrainGrid grid = ReadBulk(scene, log, nodeCountX, nodeCountY, verts, quadLen,
                    globalWidth, globalHeight);
                if (grid != null)
                    return grid;
            }
            catch (Exception ex)
            {
                Emit(log, "bulk read failed: " + ex.GetType().Name + ": " + Flat(ex.Message)
                    + " -- falling back to point sampling");
            }

            return ReadPointFallback(scene, log, globalWidth, globalHeight, quadLen);
        }

        // Bulk path: one GetTerrainHeightData call per node, stitched into the
        // global grid. Discovers the node array's index order empirically.
        private static TerrainGrid ReadBulk(Scene scene, System.Action<string> log,
            int nodeCountX, int nodeCountY, int verts, float quadLen, int globalWidth, int globalHeight)
        {
            float[] heights = new float[globalWidth * globalHeight];

            int order = 0;              // 0 = unknown, 1 = ix-major, 2 = iy-major, -1 = unresolved
            int nodesChecked = 0;
            int minMaxBad = 0;
            float worstMinMaxDeltaMin = 0f;
            float worstMinMaxDeltaMax = 0f;
            int sampleMatches = 0;
            int sampleTotal = 0;
            int sampleBadNodes = 0;

            for (int ny = 0; ny < nodeCountY; ny++)
            {
                for (int nx = 0; nx < nodeCountX; nx++)
                {
                    int nverts;
                    float nquad;
                    float nmin;
                    float nmax;
                    scene.GetTerrainNodeData(nx, ny, out nverts, out nquad, out nmin, out nmax);

                    if (nverts != verts)
                        throw new InvalidOperationException(
                            "node(" + nx + "," + ny + ") verts=" + nverts + " != " + verts);

                    float[] node = scene.GetTerrainHeightData(nx, ny);
                    if (node == null)
                        throw new InvalidOperationException(
                            "GetTerrainHeightData(" + nx + "," + ny + ") returned null.");
                    if (node.Length != verts * verts)
                        throw new InvalidOperationException(
                            "node(" + nx + "," + ny + ") array length " + node.Length + " != " + (verts * verts));
                    if (Math.Abs(nquad - quadLen) > 1e-3f)
                        throw new InvalidOperationException(
                            "node(" + nx + "," + ny + ") quadLen " + F(nquad) + " != " + F(quadLen));

                    nodesChecked++;

                    float arrayMin = node[0];
                    float arrayMax = node[0];
                    for (int i = 0; i < node.Length; i++)
                    {
                        float v = node[i];
                        if (v < arrayMin) arrayMin = v;
                        if (v > arrayMax) arrayMax = v;
                    }

                    // The reported node min/max is informational: it can differ
                    // slightly from the live array (observed up to ~1 m on
                    // edited terrain), while the point cross-check below is the
                    // authoritative test. Keep worst-case deltas instead of one
                    // log line per node.
                    if (Math.Abs(arrayMin - nmin) > 0.05f || Math.Abs(arrayMax - nmax) > 0.05f)
                    {
                        minMaxBad++;
                        float dMin = Math.Abs(arrayMin - nmin);
                        float dMax = Math.Abs(arrayMax - nmax);
                        if (dMin > worstMinMaxDeltaMin) worstMinMaxDeltaMin = dMin;
                        if (dMax > worstMinMaxDeltaMax) worstMinMaxDeltaMax = dMax;
                    }

                    // Discover the index order once, from the first node.
                    if (order == 0)
                        order = DiscoverOrder(scene, log, nx, ny, node, verts, quadLen);

                    if (order == -1)
                        throw new InvalidOperationException(
                            "Could not match either index order against GetTerrainHeight.");

                    // Cross-check ~9 samples against the point oracle.
                    int step = Math.Max(1, (verts - 1) / 2);
                    int nodeBad = 0;
                    for (int ix = 0; ix < verts; ix += step)
                    {
                        for (int iy = 0; iy < verts; iy += step)
                        {
                            float x = (nx * (verts - 1) + ix) * quadLen;
                            float y = (ny * (verts - 1) + iy) * quadLen;
                            float actual = scene.GetTerrainHeight(new Vec2(x, y), true);
                            float stored = order == 1 ? node[ix * verts + iy] : node[iy * verts + ix];
                            sampleTotal++;
                            if (Math.Abs(stored - actual) <= SampleTolerance)
                                sampleMatches++;
                            else
                                nodeBad++;
                        }
                    }
                    if (nodeBad > 0)
                        sampleBadNodes++;

                    // Stitch this node into the global grid.
                    for (int ix = 0; ix < verts; ix++)
                    {
                        int stitchedX = nx * (verts - 1) + ix;
                        int rowBase = stitchedX * globalHeight;
                        for (int iy = 0; iy < verts; iy++)
                        {
                            int stitchedY = ny * (verts - 1) + iy;
                            heights[rowBase + stitchedY] = order == 1 ? node[ix * verts + iy] : node[iy * verts + ix];
                        }
                    }
                }
            }

            Emit(log, "bulk validation: nodes=" + nodesChecked
                + " arraysOk=yes minMaxMismatchNodes=" + minMaxBad
                + " worstMinMaxDelta=[" + F(worstMinMaxDeltaMin) + ".." + F(worstMinMaxDeltaMax)
                + "] (informational)"
                + " pointCrossCheck=" + sampleMatches + "/" + sampleTotal
                + " (tol " + F(SampleTolerance) + "m, mismatchedNodes=" + sampleBadNodes + ")");

            // If the point oracle mostly disagrees, the bulk grid is not the
            // live surface (wrong index order or unexpected layout). Reject it
            // so the caller falls back to point sampling instead of shipping a
            // transposed/wrong grid.
            if (sampleTotal > 0 && sampleMatches * 2 < sampleTotal)
                throw new InvalidOperationException(
                    "point cross-check mostly failed (" + sampleMatches + "/" + sampleTotal
                    + " matched) -- bulk grid rejected.");

            var grid = new TerrainGrid();
            grid.Heights = heights;
            grid.Width = globalWidth;
            grid.Height = globalHeight;
            grid.Spacing = quadLen;
            grid.UsedPointFallback = false;
            ValidateGlobal(scene, log, grid);
            return grid;
        }

        // Tries both node index orders against point samples and returns the one
        // that matches (1 = node[ix*verts+iy], 2 = node[iy*verts+ix], -1 = none).
        private static int DiscoverOrder(Scene scene, System.Action<string> log,
            int nx, int ny, float[] node, int verts, float quadLen)
        {
            int matchA = 0;
            int matchB = 0;
            int samples = 0;
            int step = Math.Max(1, (verts - 1) / 2);

            for (int ix = 0; ix < verts; ix += step)
            {
                for (int iy = 0; iy < verts; iy += step)
                {
                    float x = (nx * (verts - 1) + ix) * quadLen;
                    float y = (ny * (verts - 1) + iy) * quadLen;
                    float actual = scene.GetTerrainHeight(new Vec2(x, y), true);
                    if (Math.Abs(node[ix * verts + iy] - actual) <= SampleTolerance)
                        matchA++;
                    if (Math.Abs(node[iy * verts + ix] - actual) <= SampleTolerance)
                        matchB++;
                    samples++;
                }
            }

            int order;
            if (matchA > 0 && matchA >= matchB)
                order = 1;
            else if (matchB > 0 && matchB > matchA)
                order = 2;
            else
                order = -1;

            Emit(log, "layout node(" + nx + "," + ny + "): " + samples + " samples, ix-major (node[ix*verts+iy]) "
                + matchA + "/" + samples + ", iy-major (node[iy*verts+ix]) " + matchB + "/" + samples
                + (order == 1 ? " -> using ix-major"
                    : order == 2 ? " -> using iy-major"
                    : " -> NEITHER order matches (bulk path rejected)"));
            return order;
        }

        private static void ValidateGlobal(Scene scene, System.Action<string> log, TerrainGrid grid)
        {
            float arrayMin = grid.Heights[0];
            float arrayMax = grid.Heights[0];
            for (int i = 0; i < grid.Heights.Length; i++)
            {
                float v = grid.Heights[i];
                if (v < arrayMin) arrayMin = v;
                if (v > arrayMax) arrayMax = v;
            }

            float sceneMin;
            float sceneMax;
            bool haveSceneMinMax = scene.GetTerrainMinMaxHeight(out sceneMin, out sceneMax);
            Emit(log, "global min/max: grid=[" + F(arrayMin) + ".." + F(arrayMax) + "] scene=["
                + (haveSceneMinMax ? F(sceneMin) + ".." + F(sceneMax) : "unavailable")
                + "] deltaMin=" + (haveSceneMinMax ? F(Math.Abs(arrayMin - sceneMin)) : "n/a")
                + " deltaMax=" + (haveSceneMinMax ? F(Math.Abs(arrayMax - sceneMax)) : "n/a"));
        }

        // Fallback path: point-sample the whole grid at the node spacing.
        private static TerrainGrid ReadPointFallback(Scene scene, System.Action<string> log,
            int globalWidth, int globalHeight, float quadLen)
        {
            Emit(log, "fallback: point-sampling " + globalWidth + "x" + globalHeight
                + " at spacing " + F(quadLen) + "m via GetTerrainHeight");

            float[] heights = new float[globalWidth * globalHeight];
            for (int sx = 0; sx < globalWidth; sx++)
            {
                float x = sx * quadLen;
                for (int sy = 0; sy < globalHeight; sy++)
                {
                    float y = sy * quadLen;
                    heights[sx * globalHeight + sy] = scene.GetTerrainHeight(new Vec2(x, y), true);
                }
            }

            var grid = new TerrainGrid();
            grid.Heights = heights;
            grid.Width = globalWidth;
            grid.Height = globalHeight;
            grid.Spacing = quadLen;
            grid.UsedPointFallback = true;
            ValidateGlobal(scene, log, grid);
            return grid;
        }

        // One atomic single-line log entry.
        private static void Emit(System.Action<string> log, string message)
        {
            if (log != null)
                log(Flat(message));
        }

        private static string F(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string Flat(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            return value.Replace("\r\n", " | ").Replace("\r", " | ").Replace("\n", " | ");
        }
    }
}
