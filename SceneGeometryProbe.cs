using System;
using System.Collections.Generic;
using System.Globalization;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace BetterBannerlordNavmeshing
{
    // Phase 1 geometry probe: reports what collision geometry the scene exposes,
    // whether GetBodyShape() is populated (incl. batched/kinematic statics), the
    // coordinate space of PhysicsShape.GetTriangle vs the entity bounding boxes,
    // what a downward column of raycasts hits (topmost vs ground), and the scene's
    // declared navmesh levels. Answers the open questions in plan-navmesh-v2.md S4.
    public static class SceneGeometryProbe
    {
        private struct EntityInfo
        {
            public GameEntity Entity;
            public int TriTotal;
            public bool HasSteps;
            public int MeshCount;
        }

        public static void Run(Scene scene, System.Action<string> log)
        {
            Run(scene, Vec3.Zero, log);
        }

        // columnPosition selects the X/Y of the vertical raycast column (the probe
        // entity should be placed at the spot of interest, e.g. under a bridge).
        public static void Run(Scene scene, Vec3 columnPosition, System.Action<string> log)
        {
            if (log == null)
                return;

            var watch = System.Diagnostics.Stopwatch.StartNew();
            if (scene == null)
            {
                log("ERROR: no scene.");
                return;
            }

            log("probe start: scene=" + SafeName(scene));

            var entities = new List<GameEntity>();
            try
            {
                scene.GetEntities(ref entities);
            }
            catch (Exception ex)
            {
                log("probe: GetEntities failed: " + E(ex));
                entities = new List<GameEntity>();
            }

            var infos = new List<EntityInfo>();
            try
            {
                ScanEntities(entities, log, infos);
            }
            catch (Exception ex)
            {
                log("probe scan error: " + E(ex));
            }

            try
            {
                CoordinateSpaceTest(scene, log, infos);
            }
            catch (Exception ex)
            {
                log("probe coords error: " + E(ex));
            }

            try
            {
                ColumnProbe(scene, log, columnPosition);
            }
            catch (Exception ex)
            {
                log("probe column error: " + E(ex));
            }

            try
            {
                SceneLevels(scene, log);
            }
            catch (Exception ex)
            {
                log("probe levels error: " + E(ex));
            }

            log("probe done: totalMs=" + watch.ElapsedMilliseconds);
        }

        // ------------------------------------------------------------------
        // 1 + 2. Entity scan, classification, shape extraction
        // ------------------------------------------------------------------
        private static void ScanEntities(List<GameEntity> entities, System.Action<string> log, List<EntityInfo> infos)
        {
            var scanWatch = System.Diagnostics.Stopwatch.StartNew();

            int count = 0, physics = 0;
            int shapesOk = 0, shapesNull = 0, shapesEmpty = 0, shapeExceptions = 0;
            int batchedKinematic = 0, trisTotal = 0;
            int cTerrain = 0, cFlora = 0, cStairs = 0, cLadder = 0, cBarrier = 0, cDynamic = 0, cStatic = 0;
            int perEntityLogged = 0;
            const int perEntityCap = 60;

            for (int i = 0; i < entities.Count; i++)
            {
                GameEntity e = entities[i];
                if (e == null)
                    continue;
                count++;

                string name = SafeEntityName(e);
                string prefab = Safe(() => e.GetPrefabName());
                string tags = SafeTags(e);
                BodyFlags flags = SafeFlags(() => e.BodyFlag);
                BodyFlags descFlags = SafeFlags(() => e.PhysicsDescBodyFlag);
                BodyFlags all = flags | descFlags;
                GameEntity.Mobility mobility = SafeMobility(e);

                bool hasPhysics = SafeBool(() => e.HasPhysicsBody());
                bool hasStatic = SafeBool(() => e.HasStaticPhysicsBody());
                bool hasKinematic = SafeBool(() => e.HasKinematicRigidBody());
                bool hasDynamic = SafeBool(() => e.HasDynamicRigidBody());
                bool batched = SafeBool(() => e.WeakEntity.HasBatchedKinematicPhysicsFlag());
                int physicsTriangles = SafeInt(() => e.GetPhysicsTriangleCount());

                // classification (first matching bucket wins)
                bool isTerrain = (all & BodyFlags.BodyOwnerTerrain) != 0;
                bool isFlora = (all & BodyFlags.BodyOwnerFlora) != 0;
                bool isStairs = (all & BodyFlags.HasSteps) != 0;
                bool isLadder = (all & BodyFlags.Ladder) != 0;
                bool isBarrier = (all & (BodyFlags.Barrier | BodyFlags.Barrier3D)) != 0;
                bool isDynamic = (all & (BodyFlags.Dynamic | BodyFlags.Moveable)) != 0
                    || mobility == GameEntity.Mobility.Dynamic
                    || mobility == GameEntity.Mobility.DynamicForced
                    || hasDynamic;

                if (isTerrain) cTerrain++;
                else if (isFlora) cFlora++;
                else if (isStairs) cStairs++;
                else if (isLadder) cLadder++;
                else if (isBarrier) cBarrier++;
                else if (isDynamic) cDynamic++;
                else cStatic++;

                if (batched)
                    batchedKinematic++;

                var info = new EntityInfo();
                info.Entity = e;
                info.HasSteps = isStairs;

                if (hasPhysics)
                {
                    physics++;
                    PhysicsShape shape = null;
                    try
                    {
                        shape = e.GetBodyShape();
                    }
                    catch (Exception ex)
                    {
                        shapeExceptions++;
                        if (perEntityLogged < perEntityCap)
                        {
                            log("shape entity='" + name + "' EXCEPTION " + E(ex));
                            perEntityLogged++;
                        }
                        shape = null;
                    }

                    if (shape == null)
                    {
                        shapesNull++;
                        if (perEntityLogged < perEntityCap)
                        {
                            log("shape entity='" + name + "' prefab='" + prefab + "' flags=" + Flags(all)
                                + " physTris=" + physicsTriangles + " -> null (no shape returned)");
                            perEntityLogged++;
                        }
                    }
                    else
                    {
                        int meshCount = SafeInt(() => shape.TriangleMeshCount());
                        int entityTris = 0;
                        for (int m = 0; m < meshCount; m++)
                            entityTris += SafeInt(() => shape.TriangleCountInTriangleMesh(m));

                        info.MeshCount = meshCount;
                        info.TriTotal = entityTris;

                        if (meshCount <= 0 || entityTris <= 0)
                        {
                            shapesEmpty++;
                            if (perEntityLogged < perEntityCap)
                            {
                                log("shape entity='" + name + "' prefab='" + prefab + "' flags=" + Flags(all)
                                    + " meshes=" + meshCount + " tris=" + entityTris + " -> empty");
                                perEntityLogged++;
                            }
                        }
                        else
                        {
                            shapesOk++;
                            trisTotal += entityTris;
                            if (perEntityLogged < perEntityCap)
                            {
                                log("shape entity='" + name + "' prefab='" + prefab + "' flags=" + Flags(all)
                                    + " mobility=" + mobility + " meshes=" + meshCount + " tris=" + entityTris
                                    + " (t=+" + scanWatch.ElapsedMilliseconds + "ms)");
                                perEntityLogged++;
                            }
                        }
                    }
                }

                infos.Add(info);
            }

            if (perEntityLogged >= perEntityCap)
                log("shape: per-entity detail truncated at " + perEntityCap + " lines");

            log("probe: entities=" + count + " physics=" + physics
                + " shapesOk=" + shapesOk + " shapesNull=" + shapesNull + " shapesEmpty=" + shapesEmpty
                + " shapeExceptions=" + shapeExceptions + " batchedKinematic=" + batchedKinematic
                + " trisTotal=" + trisTotal + " scanMs=" + scanWatch.ElapsedMilliseconds);
            log("probe classes: terrain=" + cTerrain + " flora=" + cFlora + " stairs=" + cStairs
                + " ladder=" + cLadder + " barrier=" + cBarrier + " dynamic=" + cDynamic + " static=" + cStatic);
        }

        // ------------------------------------------------------------------
        // 3. Coordinate space of GetTriangle vs the entity bounding boxes
        // ------------------------------------------------------------------
        private static void CoordinateSpaceTest(Scene scene, System.Action<string> log, List<EntityInfo> infos)
        {
            if (infos.Count == 0)
            {
                log("coords: no entities to sample.");
                return;
            }

            var ranked = new List<EntityInfo>(infos);
            ranked.Sort(delegate (EntityInfo a, EntityInfo b) { return b.TriTotal.CompareTo(a.TriTotal); });

            var chosen = new List<EntityInfo>();
            for (int i = 0; i < ranked.Count && chosen.Count < 8; i++)
            {
                if (ranked[i].TriTotal > 0)
                    chosen.Add(ranked[i]);
            }
            for (int i = 0; i < infos.Count; i++)
            {
                if (infos[i].HasSteps)
                {
                    chosen.Add(infos[i]);
                    break;
                }
            }

            if (chosen.Count == 0)
            {
                log("coords: no physics shapes with triangles to sample.");
                return;
            }

            var tri = new Vec3[3];
            for (int c = 0; c < chosen.Count; c++)
            {
                GameEntity e = chosen[c].Entity;
                string name = SafeEntityName(e);
                string prefab = Safe(() => e.GetPrefabName());

                PhysicsShape shape = null;
                try
                {
                    shape = e.GetBodyShape();
                }
                catch (Exception ex)
                {
                    log("coords entity='" + name + "' shape EXCEPTION " + E(ex));
                    continue;
                }
                if (shape == null)
                {
                    log("coords entity='" + name + "' shape null.");
                    continue;
                }

                MatrixFrame frame = SafeFrame(e);
                Vec3 rawMin = Vec3.Invalid;
                Vec3 rawMax = Vec3.Invalid;
                Vec3 framedMin = Vec3.Invalid;
                Vec3 framedMax = Vec3.Invalid;
                bool any = false;
                int sampled = 0;
                int meshCount = SafeInt(() => shape.TriangleMeshCount());
                for (int m = 0; m < meshCount && sampled < 512; m++)
                {
                    int tc = SafeInt(() => shape.TriangleCountInTriangleMesh(m));
                    for (int t = 0; t < tc && sampled < 512; t++)
                    {
                        try
                        {
                            shape.GetTriangle(tri, m, t);
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            Vec3 v = tri[k];
                            Vec3 fv = frame.TransformToParent(v);
                            if (!any)
                            {
                                rawMin = v; rawMax = v; framedMin = fv; framedMax = fv; any = true;
                            }
                            else
                            {
                                Relax(ref rawMin, ref rawMax, v);
                                Relax(ref framedMin, ref framedMax, fv);
                            }
                        }
                        sampled++;
                    }
                }

                string globalBox = Box(SafeBox(() => e.GetGlobalBoundingBox()));
                string localBox = Box(SafeBox(() => e.GetLocalBoundingBox()));
                string physBoxInc = Box(SafeBox(() => e.GetLocalPhysicsBoundingBox(true)));
                string physBoxExc = Box(SafeBox(() => e.GetLocalPhysicsBoundingBox(false)));

                log("coords: entity='" + name + "' prefab='" + prefab + "' trisSampled=" + sampled
                    + " triRaw=" + Span(rawMin, rawMax)
                    + " globalBox=" + globalBox
                    + " localBox=" + localBox
                    + " physBoxIncChildren=" + physBoxInc
                    + " physBoxExcChildren=" + physBoxExc
                    + " triRaw*globalFrame=" + Span(framedMin, framedMax));
            }
        }

        // ------------------------------------------------------------------
        // 4. Column probe: top-down marching raycasts (no multi-hit API)
        // ------------------------------------------------------------------
        private static void ColumnProbe(Scene scene, System.Action<string> log, Vec3 column)
        {
            float x = column.x;
            float y = column.y;
            float top = column.z + 200f;
            float bottom = column.z - 200f;
            log("column: x=" + F(x) + " y=" + F(y) + " fromZ=" + F(top) + " toZ=" + F(bottom));

            var hits = new List<float>();
            int i = 0;
            float fromZ = top;
            while (i < 16 && fromZ > bottom)
            {
                var source = new Vec3(x, y, fromZ);
                var target = new Vec3(x, y, bottom);
                float distance;
                Vec3 point;
                WeakGameEntity hit;
                bool ok;
                try
                {
                    ok = scene.RayCastForClosestEntityOrTerrain(source, target, out distance, out point,
                        out hit, 0f, BodyFlags.None);
                }
                catch (Exception ex)
                {
                    log("column ray error: " + E(ex));
                    break;
                }
                if (!ok)
                    break;

                hits.Add(point.z);
                log("column hit " + i + ": z=" + F(point.z) + " entity=" + WeakName(hit)
                    + " flags=" + WeakFlags(hit) + " distance=" + F(distance));
                i++;
                fromZ = point.z - 0.05f;
            }

            var gaps = new List<string>();
            for (int k = 1; k < hits.Count; k++)
                gaps.Add(F(hits[k - 1] - hits[k]));
            log("column: hits=" + hits.Count + " gaps=[" + string.Join(",", gaps.ToArray()) + "]");

            // Ground-query comparison at the same X/Y.
            var pos = new Vec3(x, y, column.z);
            float ground = float.MaxValue;
            float groundWithNormal = float.MaxValue;
            var normal = Vec3.Zero;
            try
            {
                ground = scene.GetGroundHeightAtPosition(pos, BodyFlags.None);
            }
            catch (Exception ex)
            {
                log("ground query (plain) error: " + E(ex));
            }
            try
            {
                groundWithNormal = scene.GetGroundHeightAtPosition(pos, out normal, BodyFlags.None);
            }
            catch (Exception ex)
            {
                log("ground query (normal) error: " + E(ex));
            }

            float terrain = float.MaxValue;
            try
            {
                terrain = scene.GetTerrainHeight(new Vec2(x, y), true);
            }
            catch (Exception ex)
            {
                log("GetTerrainHeight error: " + E(ex));
            }

            string faceGroup = "none";
            try
            {
                var navPos = pos;
                int faceGroupId;
                UIntPtr navMesh = scene.GetNavigationMeshForPosition(navPos, out faceGroupId, 1.5f, false);
                faceGroup = navMesh == UIntPtr.Zero ? "none" : faceGroupId.ToString();
            }
            catch (Exception ex)
            {
                faceGroup = "error:" + ex.GetType().Name;
            }

            log("ground query: GetGroundHeightAtPosition=" + Fs(ground)
                + " (normal=" + V(normal) + ") GetTerrainHeight=" + Fs(terrain)
                + " navMeshFaceGroup=" + faceGroup);
        }

        // ------------------------------------------------------------------
        // 5. Scene level declarations
        // ------------------------------------------------------------------
        private static void SceneLevels(Scene scene, System.Action<string> log)
        {
            int levelCount = 0;
            try
            {
                levelCount = scene.GetUpgradeLevelCount();
            }
            catch (Exception ex)
            {
                log("levels: GetUpgradeLevelCount error: " + E(ex));
                return;
            }

            for (int i = 0; i < levelCount; i++)
            {
                string levelName = null;
                uint levelMask = 0;
                try
                {
                    levelName = scene.GetUpgradeLevelNameOfIndex(i);
                }
                catch (Exception ex)
                {
                    log("level " + i + " name error: " + E(ex));
                    continue;
                }
                try
                {
                    levelMask = scene.GetUpgradeLevelMaskOfLevelName(levelName);
                }
                catch (Exception ex)
                {
                    log("level " + i + " mask error: " + E(ex));
                }
                log("level " + i + ": name='" + (levelName ?? "") + "' mask=0x" + levelMask.ToString("X8", CultureInfo.InvariantCulture));
            }

            uint combined = 0;
            try
            {
                combined = scene.GetUpgradeLevelMask();
            }
            catch (Exception ex)
            {
                log("levels: combined mask error: " + E(ex));
            }
            log("levels: count=" + levelCount + " combinedMask=0x" + combined.ToString("X8", CultureInfo.InvariantCulture));
        }

        // ------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------
        private static void Relax(ref Vec3 min, ref Vec3 max, Vec3 v)
        {
            if (v.x < min.x) min.x = v.x;
            if (v.y < min.y) min.y = v.y;
            if (v.z < min.z) min.z = v.z;
            if (v.x > max.x) max.x = v.x;
            if (v.y > max.y) max.y = v.y;
            if (v.z > max.z) max.z = v.z;
        }

        private static string Box(BoundingBox bb)
        {
            return "[" + V(bb.min) + ".." + V(bb.max) + "]";
        }

        private static string Span(Vec3 min, Vec3 max)
        {
            return "[" + V(min) + ".." + V(max) + "]";
        }

        private static string V(Vec3 v)
        {
            return F(v.x) + "," + F(v.y) + "," + F(v.z);
        }

        private static string Flags(BodyFlags flags)
        {
            return "0x" + ((int)flags).ToString("X8", CultureInfo.InvariantCulture);
        }

        private static string F(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string Fs(float value)
        {
            return value == float.MaxValue ? "n/a" : F(value);
        }

        private static string E(Exception ex)
        {
            return ex.GetType().Name + ": " + Flat(ex.Message);
        }

        private static string Flat(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            return value.Replace("\r\n", " | ").Replace("\r", " | ").Replace("\n", " | ");
        }

        private static string SafeName(Scene scene)
        {
            try
            {
                string name = scene.GetName();
                return string.IsNullOrEmpty(name) ? "?" : name;
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static string SafeEntityName(GameEntity e)
        {
            try
            {
                return string.IsNullOrEmpty(e.Name) ? "?" : e.Name;
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static string WeakName(WeakGameEntity e)
        {
            try
            {
                return string.IsNullOrEmpty(e.Name) ? "?" : e.Name;
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static string WeakFlags(WeakGameEntity e)
        {
            try
            {
                return Flags(e.BodyFlag | e.PhysicsDescBodyFlag);
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static string SafeTags(GameEntity e)
        {
            try
            {
                string[] tags = e.Tags;
                if (tags == null || tags.Length == 0)
                    return "";
                return string.Join(",", tags);
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static GameEntity.Mobility SafeMobility(GameEntity e)
        {
            try
            {
                return e.GetMobility();
            }
            catch (Exception)
            {
                return GameEntity.Mobility.Stationary;
            }
        }

        private static MatrixFrame SafeFrame(GameEntity e)
        {
            try
            {
                return e.GetGlobalFrame();
            }
            catch (Exception)
            {
                return MatrixFrame.Identity;
            }
        }

        private static BoundingBox SafeBox(Func<BoundingBox> getter)
        {
            try
            {
                return getter();
            }
            catch (Exception)
            {
                return new BoundingBox();
            }
        }

        private static bool SafeBool(Func<bool> getter)
        {
            try
            {
                return getter();
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static int SafeInt(Func<int> getter)
        {
            try
            {
                return getter();
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static BodyFlags SafeFlags(Func<BodyFlags> getter)
        {
            try
            {
                return getter();
            }
            catch (Exception)
            {
                return BodyFlags.None;
            }
        }

        private static string Safe(Func<string> getter)
        {
            try
            {
                return getter() ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }
    }
}
