using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BetterBannerlordNavmeshing
{
    // ==================================================================
    // Scene snapshot (offline test case) - PURE, no TaleWorlds types
    // ==================================================================
    // The editor's "Dump Scene Snapshot" button writes the exact main-thread inputs the export uses
    // (the terrain height grid + per-entity collision triangles) so the standalone harness can rebuild
    // the same soup and iterate on real scene data in seconds, without launching the editor.
    //
    // Both files are little-endian. Formats (version 1):
    //
    //   <prefab>_snapshot_terrain.bin
    //     int32   version   (= 2; 1 = pre-V3.59, no border block)
    //     int32   width
    //     int32   height
    //     float32 spacing   (m per grid step)
    //     int32   stride    (NavMeshSampleStride used for the run)
    //     float32 heights[width * height]     exactly as SceneTerrainSampler produced them
    //     -- version 2 only, the border block (V3.59):
    //     int32   borderVertexCount  (0 = this scene declared no border probes)
    //     int32   borderSource       (0 none / 1 soft / 2 hard)
    //     float32 borderXy[2 * borderVertexCount]   CCW ring in engine XZ, as the engine's hull
    //     int32   borderSoftProbeCount, borderHardProbeCount   (why the source was chosen)
    //
    //   <prefab>_snapshot_assets.bin
    //     int32   version   (= 1)
    //     int32   entityCount
    //     per entity:
    //       int32   nameLen, byte nameLen (UTF-8)
    //       int32   flags                     (BodyFlags as int)
    //       int32   mobility                  (GameEntity.Mobility as int)
    //       int32   triCount
    //       int32   capped                    (1 when the collector capped the shape)
    //       float32 verts[triCount * 3 * 3]   engine Z-up world space, 3 floats per vertex
    //       int32   tris[triCount * 3]        0-based indices into this entity's own verts
    //
    // The harness mirrors VoxelNavMeshGenerator when it rebuilds: engine Z-up -> Recast Y-up, asset
    // triangle winding reversal, and the builder's uniform height offset.
    public static class SceneSnapshot
    {
        // TERRAIN version. 2 = the V3.59 border block; 1 = the pre-border layout.
        public const int Version = 2;
        // Accepted on read so a pre-V3.59 terrain snapshot (no border block) still replays - it simply
        // reports no border, which is the structural no-op path.
        private const int VersionNoBorder = 1;

        // ASSETS version. 1 = the original per-entity layout. 2 = fixE: one extra int per entity,
        // `Gate` (1 when the entity is gate/usable-door provenance, from the scene tag `gate`). A v1
        // file still reads (Gate defaults to 0) so old fixtures keep replaying.
        private const int AssetsVersion = 2;
        private const int AssetsVersionNoGate = 1;

        public sealed class EntityRecord
        {
            public string Name;
            public int Flags;
            public int Mobility;
            public int TriCount;
            public bool Capped;
            public float[] VertsXyz;   // 3 per vertex, engine Z-up
            public int[] Tris;         // 3 per triangle, 0-based into VertsXyz
            // fixE: gate/usable-door provenance (scene tag `gate` on this entity or an ancestor).
            // 0 on a v1 assets file (no gate information recorded).
            public int Gate;
            // In-memory only (never serialized): how many of TriCount's triangles are synthesized
            // SteppedPropRamps ramp quads (always the LAST ones). Set by the snapshot dump path via
            // SceneCollisionGeometry, and by the harness when it synthesizes ramps itself.
            public int RampTris;
        }

        public sealed class TerrainData
        {
            public int Width;
            public int Height;
            public float Spacing;
            public int Stride;
            public float[] Heights;

            // Border polygon (V3.59), exactly the ring the exporter handed the builder: the engine's
            // native convex hull of the tagged probes, or null when the scene declared fewer than three.
            // Carried in the terrain artefact (rather than a third file) because it is one small
            // per-scene block the harness needs alongside the heights anyway.
            public float[] BorderXy;
            public int BorderSource;
            public int BorderSoftProbes;
            public int BorderHardProbes;
        }

        public static void WriteTerrainBin(string path, TerrainData t)
        {
            using (var w = new BinaryWriter(File.Create(path), Encoding.UTF8))
            {
                w.Write(Version);
                w.Write(t.Width);
                w.Write(t.Height);
                w.Write(t.Spacing);
                w.Write(t.Stride);
                for (int i = 0; i < t.Heights.Length; i++)
                    w.Write(t.Heights[i]);
                // Border block (V3.59).
                int bn = (t.BorderXy != null && t.BorderSource != TiledNavMeshBuilder.BorderPolygon.SourceNone)
                    ? t.BorderXy.Length / 2 : 0;
                w.Write(bn);
                w.Write(bn > 0 ? t.BorderSource : TiledNavMeshBuilder.BorderPolygon.SourceNone);
                for (int i = 0; i < bn * 2; i++)
                    w.Write(t.BorderXy[i]);
                w.Write(t.BorderSoftProbes);
                w.Write(t.BorderHardProbes);
            }
        }

        public static TerrainData ReadTerrainBin(string path)
        {
            using (var r = new BinaryReader(File.OpenRead(path), Encoding.UTF8))
            {
                int version = r.ReadInt32();
                if (version != Version && version != VersionNoBorder)
                    throw new InvalidDataException("terrain snapshot version " + version + " not in {"
                        + VersionNoBorder + "," + Version + "}");
                var t = new TerrainData();
                t.Width = r.ReadInt32();
                t.Height = r.ReadInt32();
                t.Spacing = r.ReadSingle();
                t.Stride = r.ReadInt32();
                if (t.Width <= 0 || t.Height <= 0)
                    throw new InvalidDataException("terrain snapshot has bad dimensions " + t.Width + "x" + t.Height);
                t.Heights = new float[t.Width * t.Height];
                for (int i = 0; i < t.Heights.Length; i++)
                    t.Heights[i] = r.ReadSingle();
                if (version >= Version)
                {
                    int bn = r.ReadInt32();
                    t.BorderSource = r.ReadInt32();
                    if (bn < 0 || bn > 4096)
                        throw new InvalidDataException("terrain snapshot border vertex count " + bn + " is absurd");
                    if (bn > 0)
                    {
                        t.BorderXy = new float[bn * 2];
                        for (int i = 0; i < t.BorderXy.Length; i++)
                            t.BorderXy[i] = r.ReadSingle();
                    }
                    t.BorderSoftProbes = r.ReadInt32();
                    t.BorderHardProbes = r.ReadInt32();
                    if (bn == 0) t.BorderSource = TiledNavMeshBuilder.BorderPolygon.SourceNone;
                }
                return t;
            }
        }

        public static void WriteAssetsBin(string path, List<EntityRecord> entities)
        {
            using (var w = new BinaryWriter(File.Create(path), Encoding.UTF8))
            {
                w.Write(AssetsVersion);
                w.Write(entities != null ? entities.Count : 0);
                if (entities == null)
                    return;
                for (int i = 0; i < entities.Count; i++)
                {
                    EntityRecord e = entities[i];
                    byte[] name = Encoding.UTF8.GetBytes(e.Name ?? "");
                    w.Write(name.Length);
                    w.Write(name);
                    w.Write(e.Flags);
                    w.Write(e.Mobility);
                    w.Write(e.TriCount);
                    w.Write(e.Capped ? 1 : 0);
                    w.Write(e.Gate);
                    for (int k = 0; k < e.VertsXyz.Length; k++)
                        w.Write(e.VertsXyz[k]);
                    for (int k = 0; k < e.Tris.Length; k++)
                        w.Write(e.Tris[k]);
                }
            }
        }

        public static List<EntityRecord> ReadAssetsBin(string path)
        {
            var list = new List<EntityRecord>();
            using (var r = new BinaryReader(File.OpenRead(path), Encoding.UTF8))
            {
                int version = r.ReadInt32();
                if (version != AssetsVersion && version != AssetsVersionNoGate)
                    throw new InvalidDataException("assets snapshot version " + version + " not in {"
                        + AssetsVersionNoGate + "," + AssetsVersion + "}");
                int count = r.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    var e = new EntityRecord();
                    int nameLen = r.ReadInt32();
                    e.Name = Encoding.UTF8.GetString(r.ReadBytes(nameLen));
                    e.Flags = r.ReadInt32();
                    e.Mobility = r.ReadInt32();
                    e.TriCount = r.ReadInt32();
                    e.Capped = r.ReadInt32() != 0;
                    e.Gate = version >= 2 ? r.ReadInt32() : 0;
                    e.VertsXyz = new float[e.TriCount * 9];
                    for (int k = 0; k < e.VertsXyz.Length; k++)
                        e.VertsXyz[k] = r.ReadSingle();
                    e.Tris = new int[e.TriCount * 3];
                    for (int k = 0; k < e.Tris.Length; k++)
                        e.Tris[k] = r.ReadInt32();
                    list.Add(e);
                }
            }
            return list;
        }

        // Expands an indexed mesh (shared vertices) into the file's per-triangle layout: 3 unique
        // vertices per triangle (9 floats each) and trivial 0-based indices.
        public static EntityRecord Expand(string name, int flags, int mobility, bool capped,
            float[] vertsXyz, int[] tris, int gate = 0)
        {
            int triCount = tris != null ? tris.Length / 3 : 0;
            var rec = new EntityRecord();
            rec.Name = name;
            rec.Flags = flags;
            rec.Mobility = mobility;
            rec.Capped = capped;
            rec.Gate = gate;
            rec.TriCount = triCount;
            rec.VertsXyz = new float[triCount * 9];
            rec.Tris = new int[triCount * 3];
            for (int t = 0; t < triCount; t++)
            {
                for (int k = 0; k < 3; k++)
                {
                    int src = tris[t * 3 + k] * 3;
                    rec.VertsXyz[t * 9 + k * 3 + 0] = vertsXyz[src + 0];
                    rec.VertsXyz[t * 9 + k * 3 + 1] = vertsXyz[src + 1];
                    rec.VertsXyz[t * 9 + k * 3 + 2] = vertsXyz[src + 2];
                    rec.Tris[t * 3 + k] = t * 3 + k;
                }
            }
            return rec;
        }

        public static void WriteText(string path, string text)
        {
            File.WriteAllText(path, text ?? "", new UTF8Encoding(false));
        }

        public static string ReadAllText(string path)
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }

        // "x,y;x,y;..." -> list of (x, z). Malformed pairs are skipped.
        public static List<float[]> ParsePoints(string spec)
        {
            var points = new List<float[]>();
            if (string.IsNullOrEmpty(spec))
                return points;
            string[] parts = spec.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length == 0)
                    continue;
                string[] xy = p.Split(',');
                if (xy.Length != 2)
                    continue;
                float x, y;
                if (!float.TryParse(xy[0].Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out x))
                    continue;
                if (!float.TryParse(xy[1].Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out y))
                    continue;
                points.Add(new float[] { x, y });
            }
            return points;
        }
    }
}
