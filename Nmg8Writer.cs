using System;
using System.Collections.Generic;
using System.IO;

namespace BetterBannerlordNavmeshing
{
    // ==================================================================
    // NMG8 navmesh prefab serializer
    // ==================================================================
    // Shared by both mesh producers (the v1 adaptive terrain mesher and the
    // v2 tiled voxel core). Plain BCL only -- never touches TaleWorlds types,
    // so it can be verified outside the editor.
    //
    // The .bin is a 4-byte body length (stripped by rglBuffer before
    // import_stream) followed by this magic, then vertices, edges and faces.
    // This file was extracted verbatim from TerrainNavMeshGenerator so the v1
    // output stays byte-identical.
    public static class Nmg8Writer
    {
        // "NMG8" read as a little-endian uint32.
        public const uint Nmg8Version = 0x38474D4E;

        // 0xFFFFFFFF = every agent level. The level groups themselves are
        // left empty; the editor can populate cost data later.
        public const uint LevelMaskAll = 0xFFFFFFFF;
        public const int LevelGroupCount = 32;

        // Legacy edge pad fields. import_stream discards all three.
        public const int PadInt = -1;

        // Builds the unique undirected edge list in first-encounter order over
        // the triangle perimeters. This is exactly the order the NMG8 importer
        // rebuilds adjacency from. Kept as a separate step so callers can time
        // it and reuse the map for serialization.
        public static void BuildEdges(int[] tri, int faceCount,
            out List<int> edgeA, out List<int> edgeB, out Dictionary<long, int> edgeMap)
        {
            edgeMap = new Dictionary<long, int>(LongKeyComparer.Instance);
            edgeA = new List<int>();
            edgeB = new List<int>();
            for (int f = 0; f < faceCount; f++)
            {
                int faceBase = f * 3;
                for (int i = 0; i < 3; i++)
                {
                    int a = tri[faceBase + i];
                    int b = tri[faceBase + (i + 1) % 3];
                    long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    if (!edgeMap.ContainsKey(key))
                    {
                        edgeMap[key] = edgeA.Count;
                        edgeA.Add(a < b ? a : b);
                        edgeB.Add(a < b ? b : a);
                    }
                }
            }
        }

        // Flat-array NMG8 writer used by the v1 mesher: writes straight from
        // the compacted vertex/triangle arrays (no per-face allocations).
        // Byte-identical to the original TerrainNavMeshGenerator.WriteNmg8Flat.
        public static byte[] WriteFlat(float[] vx, float[] vy, float[] vz, int vertexCount,
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

        // Convenience overload for callers that have an interleaved [x,y,z,...]
        // vertex array (the v2 voxel core): builds the edge table itself.
        public static byte[] WriteInterleaved(float[] xyz, int vertexCount,
            int[] tri, int faceCount, out int edgeCount)
        {
            return WriteInterleaved(xyz, vertexCount, tri, faceCount, null, out edgeCount);
        }

        // Quad-aware overload. `quadPairs` holds two triangle indices per merged quad (null/empty =
        // triangles only, in which case the output is byte-identical to the plain writer). Each pair
        // is emitted as ONE 4-vertex face: the shared interior edge is deleted, the four remaining
        // boundary edges are written, and field 0 (is_concave, >= v2) carries the honest reflex flag.
        // Unpaired faces keep their 3-vertex record unchanged. Native navmeshes are polygon-based with
        // exactly this per-face vertex count + is_concave layout, so the importer already accepts n-gons.
        //
        // The flag is computed honestly here and is NOT a licence to emit concave quads: the engine's
        // navmesh validator refuses a concave face outright (the editor raises "Concave Navigation Mesh
        // Face Detected" and refuses the map), so NavMeshCleanup.MergeQuads - the only producer of
        // quadPairs - only ever records strictly convex pairs. The flag therefore comes out 0 for every
        // emitted quad; this stays a real per-face computation so the writer remains correct for any
        // caller that hands it a concave pair.
        public static byte[] WriteInterleaved(float[] xyz, int vertexCount,
            int[] tri, int faceCount, int[] quadPairs, out int edgeCount)
        {
            if (quadPairs == null || quadPairs.Length < 2)
            {
                List<int> plainA, plainB;
                Dictionary<long, int> plainMap;
                BuildEdges(tri, faceCount, out plainA, out plainB, out plainMap);
                edgeCount = plainA.Count;

                var pvx = new float[vertexCount];
                var pvy = new float[vertexCount];
                var pvz = new float[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                {
                    pvx[i] = xyz[i * 3 + 0];
                    pvy[i] = xyz[i * 3 + 1];
                    pvz[i] = xyz[i * 3 + 2];
                }
                return WriteFlat(pvx, pvy, pvz, vertexCount, tri, faceCount, plainA, plainB, plainMap);
            }

            // Which faces are quad anchors (emitted as a 4-gon) and which are the skipped partner.
            var anchor = new int[faceCount];
            var partner = new int[faceCount];
            for (int i = 0; i < faceCount; i++) { anchor[i] = -1; partner[i] = -1; }
            for (int q = 0; q + 1 < quadPairs.Length; q += 2)
            {
                int f0 = quadPairs[q], f1 = quadPairs[q + 1];
                if (f0 < 0 || f1 < 0 || f0 >= faceCount || f1 >= faceCount || f0 == f1) continue;
                if (anchor[f0] >= 0 || partner[f0] >= 0 || anchor[f1] >= 0 || partner[f1] >= 0) continue;
                anchor[f0] = f1;
                partner[f1] = f0;
            }

            // Emit the face walks in the source face order, skipping partners.
            var faceVerts = new List<int>(tri.Length);
            var faceStart = new List<int>(faceCount);
            var faceLen = new List<byte>(faceCount);
            var faceConcave = new List<byte>(faceCount);
            var walk = new int[4];
            for (int f = 0; f < faceCount; f++)
            {
                if (partner[f] >= 0) continue;
                faceStart.Add(faceVerts.Count);
                if (anchor[f] >= 0)
                {
                    bool concave;
                    BuildQuad(tri, xyz, f, anchor[f], walk, out concave);
                    faceVerts.Add(walk[0]); faceVerts.Add(walk[1]);
                    faceVerts.Add(walk[2]); faceVerts.Add(walk[3]);
                    faceLen.Add(4);
                    faceConcave.Add((byte)(concave ? 1 : 0));
                }
                else
                {
                    faceVerts.Add(tri[f * 3]); faceVerts.Add(tri[f * 3 + 1]); faceVerts.Add(tri[f * 3 + 2]);
                    faceLen.Add(3);
                    faceConcave.Add(0);
                }
            }
            int outFaceCount = faceStart.Count;

            // Edge table over the emitted perimeters (the shared edges are gone).
            var edgeMap = new Dictionary<long, int>(LongKeyComparer.Instance);
            var edgeA = new List<int>();
            var edgeB = new List<int>();
            var faceEdges = new List<int>(outFaceCount * 4);
            for (int i = 0; i < outFaceCount; i++)
            {
                int s = faceStart[i], n = faceLen[i];
                for (int j = 0; j < n; j++)
                {
                    int a = faceVerts[s + j];
                    int b = faceVerts[s + (j + 1) % n];
                    int lo = a < b ? a : b, hi = a < b ? b : a;
                    long key = ((long)lo << 32) | (uint)hi;
                    int ei;
                    if (!edgeMap.TryGetValue(key, out ei))
                    {
                        ei = edgeA.Count;
                        edgeMap[key] = ei;
                        edgeA.Add(lo);
                        edgeB.Add(hi);
                    }
                    faceEdges.Add(ei);
                }
            }
            edgeCount = edgeA.Count;

            int bodyLength = 4 + 4 + vertexCount * 12 + 4 + edgeA.Count * 20 + 4;
            for (int i = 0; i < outFaceCount; i++) bodyLength += 25 + 8 * faceLen[i];
            bodyLength += 4 + LevelGroupCount * 8;

            using (var stream = new MemoryStream(bodyLength + 4))
            {
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                {
                    writer.Write((uint)bodyLength);
                    writer.Write(Nmg8Version);

                    writer.Write(vertexCount);
                    for (int i = 0; i < vertexCount; i++)
                    {
                        writer.Write(xyz[i * 3]);
                        writer.Write(xyz[i * 3 + 1]);
                        writer.Write(xyz[i * 3 + 2]);
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

                    writer.Write(outFaceCount);
                    int feBase = 0;
                    for (int i = 0; i < outFaceCount; i++)
                    {
                        int n = faceLen[i];
                        int s = faceStart[i];
                        writer.Write(n);
                        for (int j = 0; j < n; j++) writer.Write(faceVerts[s + j]);
                        for (int j = 0; j < n; j++) writer.Write(faceEdges[feBase + j]);
                        feBase += n;

                        // Per-face versioned fields (version_fields_count = 2):
                        // field 0 (>= v2): is_concave, field 1 (>= v1): level_cost_index.
                        writer.Write(2);
                        writer.Write((int)faceConcave[i]);
                        writer.Write(0);

                        writer.Write(0f);            // cost
                        writer.Write(LevelMaskAll);  // level_mask (all agents)
                        writer.Write((byte)0);       // inverted flag
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

        // Reconstructs the 4-vertex boundary walk of the quad formed by triangles f0 and f1 (which
        // share exactly one edge). The walk keeps f0's winding: with the shared edge directed along
        // f0's winding as (p -> q), the walk is q -> x -> p -> y, where x/y are the two apexes.
        // concave is true when the quad has exactly one reflex vertex (typical when a sliver merges
        // with its neighbour). Pure geometry; it never trusts an inconsistent winding.
        private static void BuildQuad(int[] tri, float[] xyz, int f0, int f1, int[] walk, out bool concave)
        {
            int a0 = tri[f0 * 3], b0 = tri[f0 * 3 + 1], c0 = tri[f0 * 3 + 2];
            int a1 = tri[f1 * 3], b1 = tri[f1 * 3 + 1], c1 = tri[f1 * 3 + 2];
            int p, q, x;
            if (Shares(a0, b0, a1, b1, c1)) { p = a0; q = b0; x = c0; }
            else if (Shares(b0, c0, a1, b1, c1)) { p = b0; q = c0; x = a0; }
            else { p = c0; q = a0; x = b0; }
            int y = OtherOf(a1, b1, c1, p, q);
            walk[0] = q; walk[1] = x; walk[2] = p; walk[3] = y;
            concave = QuadReflex(xyz, walk);
        }

        private static bool Shares(int u, int w, int a, int b, int c)
        {
            return (u == a || u == b || u == c) && (w == a || w == b || w == c);
        }

        private static int OtherOf(int a, int b, int c, int u, int w)
        {
            if (a != u && a != w) return a;
            if (b != u && b != w) return b;
            if (c != u && c != w) return c;
            return -1;
        }

        // True when the quad walk has a reflex corner (folding the polygon onto its own plane).
        private static bool QuadReflex(float[] xyz, int[] walk)
        {
            double nx = 0, ny = 0, nz = 0;
            for (int i = 0; i < 4; i++)
            {
                int a = walk[i], b = walk[(i + 1) % 4];
                double ax = xyz[a * 3], ay = xyz[a * 3 + 1], az = xyz[a * 3 + 2];
                double bx = xyz[b * 3], by = xyz[b * 3 + 1], bz = xyz[b * 3 + 2];
                nx += (ay - by) * (az + bz);
                ny += (az - bz) * (ax + bx);
                nz += (ax - bx) * (ay + by);
            }
            double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (nl < 1e-12) return false;
            nx /= nl; ny /= nl; nz /= nl;
            double ux, uy, uz;
            if (Math.Abs(nx) <= Math.Abs(ny) && Math.Abs(nx) <= Math.Abs(nz)) { ux = 0; uy = -nz; uz = ny; }
            else if (Math.Abs(ny) <= Math.Abs(nz)) { ux = -nz; uy = 0; uz = nx; }
            else { ux = -ny; uy = nx; uz = 0; }
            double ul = Math.Sqrt(ux * ux + uy * uy + uz * uz);
            if (ul < 1e-12) return false;
            ux /= ul; uy /= ul; uz /= ul;
            double wx = ny * uz - nz * uy, wy = nz * ux - nx * uz, wz = nx * uy - ny * ux;

            double cx = 0, cy = 0, cz = 0;
            for (int i = 0; i < 4; i++) { cx += xyz[walk[i] * 3]; cy += xyz[walk[i] * 3 + 1]; cz += xyz[walk[i] * 3 + 2]; }
            cx /= 4; cy /= 4; cz /= 4;
            var px = new double[4];
            var py = new double[4];
            for (int i = 0; i < 4; i++)
            {
                double dx = xyz[walk[i] * 3] - cx, dy = xyz[walk[i] * 3 + 1] - cy, dz = xyz[walk[i] * 3 + 2] - cz;
                px[i] = dx * ux + dy * uy + dz * uz;
                py[i] = dx * wx + dy * wy + dz * wz;
            }
            double area2 = 0;
            for (int i = 0; i < 4; i++) { int j = (i + 1) % 4; area2 += px[i] * py[j] - px[j] * py[i]; }
            if (Math.Abs(area2) < 1e-12) return false;
            for (int i = 0; i < 4; i++)
            {
                int pa = (i + 3) % 4, pb = i, pc = (i + 1) % 4;
                double cr = (px[pb] - px[pa]) * (py[pc] - py[pa]) - (py[pb] - py[pa]) * (px[pc] - px[pa]);
                if (cr * area2 < 0) return true;   // this corner is reflex
            }
            return false;
        }

        // Serialises vertices/faces into a full NMG8 .bin (length prefix
        // included). Edges are rebuilt from the face perimeters, so callers
        // only ever need geometry.
        public static byte[] Serialize(List<float[]> vertices, List<int[]> faces)
        {
            List<int[]> edges;
            List<int[]> faceEdges;
            BuildTopology(faces, out edges, out faceEdges);
            return Write(vertices, faces, edges, faceEdges);
        }

        // Unique undirected edges keyed (min, max), in first-encounter order
        // over the face perimeters, plus each face's edge indices.
        public static void BuildTopology(List<int[]> faces,
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

        private static byte[] Write(List<float[]> vertices, List<int[]> faces,
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

        private static int EdgeIndex(Dictionary<long, int> edgeMap, int a, int b)
        {
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            return edgeMap[key];
        }

        // Murmur3-style mix for the packed (min,max) edge key. The default
        // Int64 hash (lo ^ hi) collides heavily on structured grids and makes
        // the edge dictionary degrade to O(n^2).
        internal sealed class LongKeyComparer : IEqualityComparer<long>
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
    }
}
