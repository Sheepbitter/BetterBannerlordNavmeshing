using System;
using System.Collections.Generic;
using System.Globalization;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace BetterBannerlordNavmeshing
{
    // ==================================================================
    // Phase 3: static asset collision geometry collector (main thread)
    // ==================================================================
    // Enumerates a scene's entities, classifies them and extracts their collision
    // shapes as engine-space (Z-up) world triangles. Mirrors SceneGeometryProbe's
    // engine API usage exactly:
    //   - tri are shape-local; world = entity.GetGlobalFrame().TransformToParent(v)
    //   - classify by BodyFlags (| PhysicsDescBodyFlag) + GetMobility(), not prefab name.
    //
    // The result is plain data (float[]/int[]) so it can cross to the background build
    // thread. Triangle winding is left as authored; the voxel mixer reverses it when it
    // maps engine (Z-up) to Recast (Y-up), because that axis swap is orientation-reversing.
    public static class SceneCollisionGeometry
    {
        // Guard against a pathological shape: never read more than this many triangles from
        // one entity (Birtu's largest was ~5.6k; 200k is far above anything real).
        private const int PerShapeTriCap = 200000;

        public sealed class AssetTriangles
        {
            public float[] VertsXyz;   // engine world space (Z-up): x, y, z
            public int[] Tris;         // indices into VertsXyz
            public int VertexCount;
            public int TriangleCount;
            public int EntitiesIncluded;
            public int ShapesCapped;
            public long ScanMs;
        }

        public static AssetTriangles Collect(Scene scene, bool includeDynamic, bool includeFlora,
            System.Action<string> log)
        {
            var result = new AssetTriangles();
            result.VertsXyz = new float[0];
            result.Tris = new int[0];
            if (scene == null)
            {
                if (log != null) log("assets: no scene.");
                return result;
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var entities = new List<GameEntity>();
            try
            {
                scene.GetEntities(ref entities);
            }
            catch (Exception ex)
            {
                if (log != null) log("assets: GetEntities failed: " + Err(ex));
                return result;
            }

            var verts = new List<float>();
            var tris = new List<int>();
            var largestTris = new List<int>();
            var largestName = new List<string>();

            int count = 0, physics = 0, included = 0;
            int exFlora = 0, exBarrier = 0, exTerrain = 0, exLadder = 0;
            int exDynamic = 0, exNoShape = 0, exEmpty = 0, exExceptions = 0;
            int shapesCapped = 0;
            int cStairs = 0, cWall = 0, cFloor = 0, cOther = 0;
            var tri = new Vec3[3];

            for (int i = 0; i < entities.Count; i++)
            {
                GameEntity e = entities[i];
                if (e == null)
                    continue;
                count++;

                try
                {
                    if (!e.HasPhysicsBody())
                        continue;
                    physics++;

                    string name = SafeEntityName(e);
                    BodyFlags all = SafeFlags(() => e.BodyFlag) | SafeFlags(() => e.PhysicsDescBodyFlag);
                    GameEntity.Mobility mobility = SafeMobility(e);
                    bool hasDynamic = SafeBool(() => e.HasDynamicRigidBody());

                    bool isTerrain = (all & BodyFlags.BodyOwnerTerrain) != 0;
                    bool isFlora = (all & BodyFlags.BodyOwnerFlora) != 0;
                    bool isStairs = (all & BodyFlags.HasSteps) != 0;
                    bool isLadder = (all & BodyFlags.Ladder) != 0;
                    bool isBarrier = (all & (BodyFlags.Barrier | BodyFlags.Barrier3D)) != 0;
                    bool isDynamic = (all & (BodyFlags.Dynamic | BodyFlags.Moveable)) != 0
                        || mobility == GameEntity.Mobility.Dynamic
                        || mobility == GameEntity.Mobility.DynamicForced
                        || hasDynamic;

                    // Exclusions (defaults): terrain (meshed from the heightmap), barriers
                    // (invisible blocker volumes), ladders, flora. Dynamic props are optional.
                    if (isTerrain) { exTerrain++; continue; }
                    if (isFlora && !includeFlora) { exFlora++; continue; }
                    if (isBarrier) { exBarrier++; continue; }
                    if (isLadder) { exLadder++; continue; }
                    if (isDynamic && !includeDynamic) { exDynamic++; continue; }

                    PhysicsShape shape;
                    try
                    {
                        shape = e.GetBodyShape();
                    }
                    catch (Exception ex)
                    {
                        exExceptions++;
                        if (log != null && exExceptions <= 8)
                            log("assets: shape entity='" + name + "' EXCEPTION " + Err(ex));
                        continue;
                    }
                    if (shape == null)
                    {
                        exNoShape++;
                        continue;
                    }

                    int meshCount = SafeInt(() => shape.TriangleMeshCount());
                    int shapeTris = 0;
                    for (int m = 0; m < meshCount; m++)
                        shapeTris += SafeInt(() => shape.TriangleCountInTriangleMesh(m));
                    if (meshCount <= 0 || shapeTris <= 0)
                    {
                        exEmpty++;
                        continue;
                    }

                    MatrixFrame frame = SafeFrame(e);
                    int wrote = 0;
                    bool capped = false;
                    for (int m = 0; m < meshCount && !capped; m++)
                    {
                        int tc = SafeInt(() => shape.TriangleCountInTriangleMesh(m));
                        for (int t = 0; t < tc; t++)
                        {
                            if (wrote >= PerShapeTriCap)
                            {
                                capped = true;
                                break;
                            }
                            try
                            {
                                shape.GetTriangle(tri, m, t);
                            }
                            catch (Exception)
                            {
                                continue;
                            }

                            int b = verts.Count / 3;
                            for (int k = 0; k < 3; k++)
                            {
                                Vec3 w = frame.TransformToParent(tri[k]);
                                verts.Add(w.x);
                                verts.Add(w.y);
                                verts.Add(w.z);
                            }
                            // Keep the authored winding; the Recast mixer reverses it.
                            tris.Add(b);
                            tris.Add(b + 1);
                            tris.Add(b + 2);
                            wrote++;
                        }
                    }

                    if (capped)
                    {
                        shapesCapped++;
                        if (log != null)
                            log("assets: WARNING shape '" + name + "' capped at " + PerShapeTriCap
                                + " tris (had " + shapeTris + ")");
                    }

                    included++;
                    TrackLargest(largestTris, largestName, name, wrote);

                    if (isStairs) cStairs++;
                    else if (IsWall(name)) cWall++;
                    else if (IsFloor(name)) cFloor++;
                    else cOther++;
                }
                catch (Exception ex)
                {
                    exExceptions++;
                    if (log != null && exExceptions <= 8)
                        log("assets: entity '" + SafeEntityName(e) + "' EXCEPTION " + Err(ex));
                }
            }

            watch.Stop();
            result.VertsXyz = verts.ToArray();
            result.Tris = tris.ToArray();
            result.VertexCount = verts.Count / 3;
            result.TriangleCount = tris.Count / 3;
            result.EntitiesIncluded = included;
            result.ShapesCapped = shapesCapped;
            result.ScanMs = watch.ElapsedMilliseconds;

            if (log != null)
            {
                log("assets: entities=" + count + " physics=" + physics + " included=" + included
                    + " excluded=[flora=" + exFlora + " barrier=" + exBarrier + " terrain=" + exTerrain
                    + " ladder=" + exLadder + " dynamic=" + exDynamic + " noShape=" + exNoShape
                    + " empty=" + exEmpty + " exceptions=" + exExceptions + "]"
                    + " tris=" + result.TriangleCount + " cappedShapes=" + shapesCapped
                    + " scanMs=" + result.ScanMs);
                log("assets: kinds stairs=" + cStairs + " wall=" + cWall + " floor=" + cFloor
                    + " other=" + cOther + " (name heuristic; log only)");
                log("assets: largest shapes: " + LargestString(largestTris, largestName));
            }
            return result;
        }

        // ---------------------------------------------------------------- helpers
        private static bool IsWall(string name)
        {
            return name.IndexOf("wall", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("fence", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("tower", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("gate", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsFloor(string name)
        {
            return name.IndexOf("floor", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("ground", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("deck", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("bridge", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("platform", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void TrackLargest(List<int> tris, List<string> names, string name, int triCount)
        {
            const int cap = 10;
            if (tris.Count < cap)
            {
                tris.Add(triCount);
                names.Add(name);
                return;
            }
            int minIndex = 0;
            for (int i = 1; i < tris.Count; i++)
                if (tris[i] < tris[minIndex]) minIndex = i;
            if (triCount > tris[minIndex])
            {
                tris[minIndex] = triCount;
                names[minIndex] = name;
            }
        }

        private static string LargestString(List<int> tris, List<string> names)
        {
            var order = new List<int>();
            for (int i = 0; i < tris.Count; i++) order.Add(i);
            order.Sort(delegate (int a, int b) { return tris[b].CompareTo(tris[a]); });
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < order.Count; i++)
            {
                if (i > 0) sb.Append(" ");
                sb.Append("'").Append(names[order[i]]).Append("'=").Append(tris[order[i]]);
            }
            return sb.ToString();
        }

        private static string SafeEntityName(GameEntity e)
        {
            try
            {
                string n = e.Name;
                return string.IsNullOrEmpty(n) ? "?" : n;
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static BodyFlags SafeFlags(Func<BodyFlags> getter)
        {
            try { return getter(); }
            catch (Exception) { return BodyFlags.None; }
        }

        private static GameEntity.Mobility SafeMobility(GameEntity e)
        {
            try { return e.GetMobility(); }
            catch (Exception) { return GameEntity.Mobility.Stationary; }
        }

        private static MatrixFrame SafeFrame(GameEntity e)
        {
            try { return e.GetGlobalFrame(); }
            catch (Exception) { return MatrixFrame.Identity; }
        }

        private static bool SafeBool(Func<bool> getter)
        {
            try { return getter(); }
            catch (Exception) { return false; }
        }

        private static int SafeInt(Func<int> getter)
        {
            try { return getter(); }
            catch (Exception) { return 0; }
        }

        private static string Err(Exception ex)
        {
            return ex.GetType().Name + ": " + Flat(ex.Message);
        }

        private static string Flat(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            return value.Replace("\r\n", " | ").Replace("\r", " | ").Replace("\n", " | ");
        }
    }
}
