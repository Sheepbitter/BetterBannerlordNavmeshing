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
            List<int> edgeA, edgeB;
            Dictionary<long, int> edgeMap;
            BuildEdges(tri, faceCount, out edgeA, out edgeB, out edgeMap);
            edgeCount = edgeA.Count;

            var vx = new float[vertexCount];
            var vy = new float[vertexCount];
            var vz = new float[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                vx[i] = xyz[i * 3 + 0];
                vy[i] = xyz[i * 3 + 1];
                vz[i] = xyz[i * 3 + 2];
            }

            return WriteFlat(vx, vy, vz, vertexCount, tri, faceCount, edgeA, edgeB, edgeMap);
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
