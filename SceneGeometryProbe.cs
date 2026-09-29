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

            TerrainColumnProbe(scene, log, x, y, terrain);
            AssetColumnProbe(scene, log, x, y);
        }

        // (a) Terrain source check at the attached entity's column: the stored (bulk) node value vs the
        // hole-aware point query. If they disagree, the bulk grid our terrain soup is built from is not
        // the live surface at this spot (a terrain hole/carve, or a stale node array).
        private static void TerrainColumnProbe(Scene scene, System.Action<string> log, float x, float y,
            float pointWithHoles)
        {
            try
            {
                if (!scene.HasTerrainHeightmap)
                {
                    log("probe terrain column: no terrain heightmap on this scene.");
                    return;
                }
                Vec2i nodeDimension;
                float nodeSize;
                int layerCount, layerVersion;
                scene.GetTerrainData(out nodeDimension, out nodeSize, out layerCount, out layerVersion);
                int nodeCountX = nodeDimension.Item1;
                int nodeCountY = nodeDimension.Item2;
                int nx = (int)Math.Floor(x / nodeSize);
                int ny = (int)Math.Floor(y / nodeSize);
                if (nx < 0) nx = 0; if (ny < 0) ny = 0;
                if (nx >= nodeCountX) nx = nodeCountX - 1;
                if (ny >= nodeCountY) ny = nodeCountY - 1;

                int verts;
                float quad, nmin, nmax;
                scene.GetTerrainNodeData(nx, ny, out verts, out quad, out nmin, out nmax);
                float[] node = scene.GetTerrainHeightData(nx, ny);
                if (node == null || verts < 2 || node.Length != verts * verts)
                {
                    log("probe terrain column: node=(" + nx + "," + ny + ") data unavailable (verts="
                        + verts + " len=" + (node == null ? -1 : node.Length) + ").");
                    return;
                }

                float lx = x - nx * nodeSize;
                float ly = y - ny * nodeSize;
                int ix = (int)Math.Round(lx / quad);
                int iy = (int)Math.Round(ly / quad);
                if (ix < 0) ix = 0; if (iy < 0) iy = 0;
                if (ix >= verts) ix = verts - 1;
                if (iy >= verts) iy = verts - 1;
                float storedIxMajor = node[ix * verts + iy];
                float storedIyMajor = node[iy * verts + ix];

                float pointNoHoles = float.MaxValue;
                try
                {
                    pointNoHoles = scene.GetTerrainHeight(new Vec2(x, y), false);
                }
                catch (Exception ex)
                {
                    log("probe terrain column: GetTerrainHeight(noHoles) error: " + E(ex));
                }

                float tol = 0.25f;
                bool agreeIx = Math.Abs(storedIxMajor - pointWithHoles) <= tol;
                bool agreeIy = Math.Abs(storedIyMajor - pointWithHoles) <= tol;
                log("probe terrain column: node=(" + nx + "," + ny + ") verts=" + verts + " quad=" + F(quad)
                    + "m local=(" + F(lx) + "," + F(ly) + ")m index=(" + ix + "," + iy + ")"
                    + " storedIxMajor=" + Fs(storedIxMajor) + "m storedIyMajor=" + Fs(storedIyMajor) + "m"
                    + " pointCheckHoles=" + Fs(pointWithHoles) + "m pointNoHoles=" + Fs(pointNoHoles) + "m"
                    + " agree(ix)=" + agreeIx + " agree(iy)=" + agreeIy
                    + " holeAwareDiffersFromStored=" + (!agreeIx && !agreeIy));
            }
            catch (Exception ex)
            {
                log("probe terrain column error: " + E(ex));
            }
        }

        // (b) Asset geometry within 2 m of the attached entity's column: triangle count, z-range and the
        // biggest contributors by name. Bodies are preloaded by name first (the engine's own recipe):
        // GetTriangle read-AVs the process on a body whose native mesh table is not loaded yet.
        private static void AssetColumnProbe(Scene scene, System.Action<string> log, float x, float y)
        {
            const float radius = 2f;
            var entities = new List<GameEntity>();
            try
            {
                scene.GetEntities(ref entities);
            }
            catch (Exception ex)
            {
                log("assets within 2m error: " + E(ex));
                return;
            }

            var cand = new List<GameEntity>();
            var names = new List<string>();
            var triCounts = new List<int>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int boxTris = 0;
            int boxZMin = 0, boxZMax = 0;
            bool haveBox = false;

            for (int i = 0; i < entities.Count; i++)
            {
                GameEntity e = entities[i];
                if (e == null) continue;
                try
                {
                    if (!e.HasPhysicsBody()) continue;
                    BoundingBox bb = SafeBox(() => e.GetGlobalBoundingBox());
                    double dx = Math.Max(0.0, Math.Max(bb.min.x - x, x - bb.max.x));
                    double dy = Math.Max(0.0, Math.Max(bb.min.y - y, y - bb.max.y));
                    if (dx * dx + dy * dy > (double)radius * radius) continue;

                    PhysicsShape shape = e.GetBodyShape();
                    if (shape == null) continue;
                    int meshCount = SafeInt(() => shape.TriangleMeshCount());
                    int et = 0;
                    for (int m = 0; m < meshCount; m++)
                        et += SafeInt(() => shape.TriangleCountInTriangleMesh(m));
                    if (et <= 0) continue;

                    cand.Add(e);
                    triCounts.Add(et);
                    names.Add(SafeEntityName(e));
                    boxTris += et;
                    if (!haveBox) { boxZMin = (int)bb.min.z; boxZMax = (int)bb.max.z; haveBox = true; }
                    else
                    {
                        if (bb.min.z < boxZMin) boxZMin = (int)bb.min.z;
                        if (bb.max.z > boxZMax) boxZMax = (int)bb.max.z;
                    }

                    string bodyName = null;
                    try { bodyName = shape.GetName(); } catch (Exception) { }
                    if (!string.IsNullOrEmpty(bodyName) && seen.Add(bodyName))
                        PhysicsShape.AddPreloadQueueWithName(bodyName, new Vec3(1f, 1f, 1f, -1f));
                }
                catch (Exception ex)
                {
                    log("assets within 2m entity error: " + E(ex));
                }
            }

            if (cand.Count == 0)
            {
                log("assets within 2m: none (boxTris=0)");
                return;
            }

            try
            {
                PhysicsShape.ProcessPreloadQueue();
            }
            catch (Exception ex)
            {
                log("assets within 2m preload error: " + E(ex));
            }

            // Triangle-precise pass (safe now that the bodies are queued).
            var tri = new Vec3[3];
            int nearTris = 0;
            float tzMin = float.MaxValue, tzMax = float.MinValue;
            int boxOnly = 0;
            for (int c = 0; c < cand.Count; c++)
            {
                GameEntity e = cand[c];
                PhysicsShape shape;
                try { shape = e.GetBodyShape(); }
                catch (Exception) { continue; }
                if (shape == null) { boxOnly++; continue; }
                int meshCount = SafeInt(() => shape.TriangleMeshCount());
                bool read = true;
                for (int m = 0; m < meshCount && read && nearTris < 200000; m++)
                {
                    int tc = SafeInt(() => shape.TriangleCountInTriangleMesh(m));
                    for (int t = 0; t < tc; t++)
                    {
                        if (nearTris >= 200000) break;
                        try { shape.GetTriangle(tri, m, t); }
                        catch (Exception) { continue; }
                        float cx = (tri[0].x + tri[1].x + tri[2].x) / 3f;
                        float cy = (tri[0].y + tri[1].y + tri[2].y) / 3f;
                        float dx = cx - x, dy = cy - y;
                        if (dx * dx + dy * dy > radius * radius) continue;
                        nearTris++;
                        for (int k = 0; k < 3; k++)
                        {
                            if (tri[k].z < tzMin) tzMin = tri[k].z;
                            if (tri[k].z > tzMax) tzMax = tri[k].z;
                        }
                    }
                }
            }

            log("assets within 2m: entities=" + cand.Count + " boxTris=" + boxTris
                + " boxZ=[" + boxZMin + ".." + boxZMax + "]"
                + " nearTris=" + nearTris
                + (nearTris > 0 ? " nearZ=[" + Fs(tzMin) + ".." + Fs(tzMax) + "]" : "")
                + (boxOnly > 0 ? " (shapeUnavailable=" + boxOnly + ")" : ""));

            var order = new List<int>();
            for (int i = 0; i < cand.Count; i++) order.Add(i);
            order.Sort(delegate (int a, int b)
            {
                int cmp = triCounts[b].CompareTo(triCounts[a]);
                if (cmp != 0) return cmp;
                return names[a].CompareTo(names[b]);
            });
            int top = Math.Min(5, order.Count);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < top; i++)
            {
                if (i > 0) sb.Append(" | ");
                sb.Append("'").Append(names[order[i]]).Append("'=").Append(triCounts[order[i]]);
            }
            log("assets within 2m top: " + sb.ToString());
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
