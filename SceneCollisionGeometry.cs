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
    // Crash hardening (2026-09-29): PhysicsShape.GetTriangle read-AVs the process when the
    // shape's native triangle-mesh table (shape+0xB8) is NULL -- an engine bug: the native
    // function asserts about the null table, fails to return, then dereferences NULL+0x40
    // anyway. That happens for bodies whose physics has not been loaded, even though the
    // managed TriangleMeshCount()/TriangleCountInTriangleMesh() report > 0. It is not
    // catchable from managed code (it kills the process), so we load the bodies first with
    // the engine's own recipe (PhysicsShape.AddPreloadQueueWithName(name, ...) per unique
    // body name, then a single PhysicsShape.ProcessPreloadQueue()) before any triangle read.
    // A per-entity TRACE line (exporter setting NavMeshVoxelAssetTrace) can be enabled so a
    // future crash log ends with the culprit's name.
    //
    // The result is plain data (float[]/int[]) so it can cross to the background build
    // thread. Triangle winding is left as authored; the voxel mixer reverses it when it
    // maps engine (Z-up) to Recast (Y-up), because that axis swap is orientation-reversing.
    public static class SceneCollisionGeometry
    {
        // Guard against a pathological shape: never read more than this many triangles from
        // one entity (Birtu's largest was ~5.6k; 200k is far above anything real).
        private const int PerShapeTriCap = 200000;

        // Emit a cheap progress line every N entities during the triangle-read pass.
        private const int ProgressEvery = 250;

        public sealed class AssetTriangles
        {
            public float[] VertsXyz;   // engine world space (Z-up): x, y, z
            public int[] Tris;         // indices into VertsXyz
            public int VertexCount;
            public int TriangleCount;
            public int EntitiesIncluded;
            public int ShapesCapped;
            public long ScanMs;
            // Same length as Tris/3 when any ramp was synthesized; null otherwise. True = the triangle
            // is a synthesized stair/ladder ramp and must be walkable regardless of its slope.
            public bool[] ForceWalkableTris;
            // Same length as Tris/3 when any HasSteps/Ladder prop was seen; null otherwise. True =
            // that triangle belongs to a stepped prop, so the builder keeps it off the ledge trim.
            public bool[] LedgeExemptTris;
        }

        // Per-entity view of the same collection, for the scene snapshot (offline test case).
        public sealed class AssetEntityRecord
        {
            public string Name;
            public BodyFlags Flags;
            public int Mobility;
            public int TriCount;       // triangles actually read (capped)
            public bool Capped;
            public float[] VertsXyz;   // engine Z-up, 3 per vertex, entity-local indexing
            public int[] Tris;         // 0-based into VertsXyz
        }

        public sealed class AssetGeometry
        {
            public float[] VertsXyz;   // merged soup, engine Z-up
            public int[] Tris;
            public int VertexCount;
            public int TriangleCount;
            public int EntitiesIncluded;
            public int ShapesCapped;
            public long ScanMs;
            public List<AssetEntityRecord> Entities;
            public List<int> LargestTris;
            public List<string> LargestNames;
            public int Count, Physics;
            public int ExFlora, ExBarrier, ExTerrain, ExLadder, ExDynamic, ExNoShape, ExEmpty,
                ExExceptions, ExUnsolid;
            // Synthesized stair/ladder ramp triangles (SteppedPropRamps); null when none/disabled.
            public bool[] ForceWalkableTris;
            public bool[] LedgeExemptTris;
            public int RampQuads, RampStairs, RampLadders, RampHoles, RampNoHoles, RampSkippedFlat,
                RampSkippedSteep, RampSkippedLong, RampCapped;
        }

        // An included entity carried from the classification pass to the triangle-read pass.
        private sealed class Candidate
        {
            public GameEntity Entity;
            public PhysicsShape Shape;
            public string Name;       // display name (never empty; "?" if unknown)
            public string BodyName;   // shape.GetName() or the raw entity name (may be empty)
            public BodyFlags Flags;
            public GameEntity.Mobility Mobility;
            public bool IsDynamic;
            public bool IsStairs;
        }

        public static AssetTriangles Collect(Scene scene, bool includeDynamic, bool includeFlora, bool trace,
            bool propRamps, System.Action<string> log, float rampCellSize = 0.25f, float rampClimb = 0.6f,
            bool rampLedge = true)
        {
            AssetGeometry g = CollectDetailed(scene, includeDynamic, includeFlora, trace, false, propRamps, log,
                rampCellSize, rampClimb, rampLedge);
            var r = new AssetTriangles();
            r.VertsXyz = g.VertsXyz;
            r.Tris = g.Tris;
            r.VertexCount = g.VertexCount;
            r.TriangleCount = g.TriangleCount;
            r.EntitiesIncluded = g.EntitiesIncluded;
            r.ShapesCapped = g.ShapesCapped;
            r.ScanMs = g.ScanMs;
            r.ForceWalkableTris = g.ForceWalkableTris;
            r.LedgeExemptTris = g.LedgeExemptTris;
            return r;
        }

        // recordEntities: also keep each entity's own vertices/indices (scene snapshot). The merged
        // arrays and every policy/counter are identical either way.
        // propRamps: synthesize a walkable ramp for HasSteps/Ladder props (SteppedPropRamps).
        public static AssetGeometry CollectDetailed(Scene scene, bool includeDynamic, bool includeFlora,
            bool trace, bool recordEntities, bool propRamps, System.Action<string> log,
            float rampCellSize = 0.25f, float rampClimb = 0.6f, bool rampLedge = true)
        {
            var result = new AssetGeometry();
            result.VertsXyz = new float[0];
            result.Tris = new int[0];
            result.Entities = new List<AssetEntityRecord>();
            result.LargestTris = new List<int>();
            result.LargestNames = new List<string>();
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

            if (log != null && trace)
                log("assets: TRACE enabled: one line per included entity is emitted before its triangle read.");

            // ---------------------------------------------------------------- pass 1
            // Classify only. No triangle is read here, so nothing in this pass can hit the
            // null-mesh-table crash. Collection is bounded by the same try/catch as before.
            var candidates = new List<Candidate>();
            var preloadNames = new List<string>();
            var preloadSeen = new HashSet<string>(StringComparer.Ordinal);

            int count = 0, physics = 0;
            int exFlora = 0, exBarrier = 0, exTerrain = 0, exLadder = 0;
            int exDynamic = 0, exNoShape = 0, exceptions = 0;
            int preloadNamesEmpty = 0;
            int exUnsolid = 0;

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

                    string rawName = SafeRawEntityName(e);
                    string name = string.IsNullOrEmpty(rawName) ? "?" : rawName;
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
                    // Non-solid-for-agents markers: such a body must not become walkable navmesh.
                    // (Not present on Birtu today; future-proofing.)
                    bool isUnsolid = (all & (BodyFlags.Disabled | BodyFlags.OnlyCollideWithRaycast
                        | BodyFlags.DontTransferToPhysicsEngine | BodyFlags.MissileOnly)) != 0;

                    // Exclusions (defaults): terrain (meshed from the heightmap), barriers
                    // (invisible blocker volumes), ladders, flora. Dynamic props are optional.
                    if (isTerrain) { exTerrain++; continue; }
                    if (isFlora && !includeFlora) { exFlora++; continue; }
                    if (isBarrier) { exBarrier++; continue; }
                    if (isLadder) { exLadder++; continue; }
                    if (isUnsolid) { exUnsolid++; continue; }
                    if (isDynamic && !includeDynamic) { exDynamic++; continue; }

                    PhysicsShape shape;
                    try
                    {
                        shape = e.GetBodyShape();
                    }
                    catch (Exception ex)
                    {
                        exceptions++;
                        if (log != null && exceptions <= 8)
                            log("assets: shape entity='" + name + "' EXCEPTION " + Err(ex));
                        continue;
                    }
                    if (shape == null)
                    {
                        exNoShape++;
                        continue;
                    }

                    string bodyName = SafeShapeName(shape, rawName);
                    var c = new Candidate();
                    c.Entity = e;
                    c.Shape = shape;
                    c.Name = name;
                    c.BodyName = bodyName;
                    c.Flags = all;
                    c.Mobility = mobility;
                    c.IsDynamic = isDynamic;
                    c.IsStairs = isStairs;
                    candidates.Add(c);

                    if (string.IsNullOrEmpty(bodyName))
                        preloadNamesEmpty++;
                    else if (preloadSeen.Add(bodyName))
                        preloadNames.Add(bodyName);
                }
                catch (Exception ex)
                {
                    exceptions++;
                    if (log != null && exceptions <= 8)
                        log("assets: entity '" + SafeEntityName(e) + "' EXCEPTION " + Err(ex));
                }
            }

            // ---------------------------------------------------------------- preload pass
            // Load exactly the bodies we are about to read, by body name, with the engine's own
            // recipe (PreloadHelper.PreloadMeshesAndPhysics does the same). This is what makes the
            // native triangle-mesh table non-null so GetTriangle cannot read-AV the process.
            var preloadWatch = System.Diagnostics.Stopwatch.StartNew();
            if (log != null)
            {
                if (preloadNamesEmpty > 0)
                    log("assets: " + preloadNamesEmpty + " included bodies had no preload name (not queued).");
                log("assets: preloading " + preloadNames.Count + " unique physics bodies by name");
            }
            if (preloadNames.Count > 0)
            {
                try
                {
                    for (int i = 0; i < preloadNames.Count; i++)
                        PhysicsShape.AddPreloadQueueWithName(preloadNames[i], new Vec3(1f, 1f, 1f, -1f));
                    PhysicsShape.ProcessPreloadQueue();
                }
                catch (Exception ex)
                {
                    if (log != null) log("assets: preload EXCEPTION " + Err(ex));
                }
            }
            preloadWatch.Stop();
            long preloadMs = preloadWatch.ElapsedMilliseconds;
            if (log != null)
                log("assets: preloaded " + preloadNames.Count + " bodies by name (preloadMs=" + preloadMs + ")");

            // ---------------------------------------------------------------- pass 2
            // Triangle read. Every native GetTriangle is now preceded (in trace mode) by a line
            // naming the entity, so a crash log's last line identifies the culprit.
            var verts = new List<float>();
            var ledgeExempt = new List<bool>();   // one entry per real triangle (stepped props)
            var tris = new List<int>();
            var largestTris = new List<int>();
            var largestName = new List<string>();
            var tri = new Vec3[3];

            // Synthesized stair/ladder ramps (see SteppedPropRamps). They are collected separately and
            // appended after every real triangle, so the force-walkable mask is a simple true-tail and
            // a build without ramps produces byte-identical soup to the pre-ramp code.
            var rampVerts = new List<float>();
            var rampTris = new List<int>();
            int rampQuads = 0, rampStairs = 0, rampLadders = 0, rampHoles = 0, rampNoHoles = 0;
            int rampSkippedFlat = 0, rampSkippedSteep = 0, rampSkippedLong = 0, rampCapped = 0;
            int included = 0, exEmpty = 0, shapesCapped = 0;
            int cStairs = 0, cWall = 0, cFloor = 0, cOther = 0;

            for (int i = 0; i < candidates.Count; i++)
            {
                Candidate c = candidates[i];
                try
                {
                    bool isSteppedProp = (c.Flags & (BodyFlags.HasSteps | BodyFlags.Ladder)) != 0;
                    int meshCount = SafeInt(() => c.Shape.TriangleMeshCount());
                    int shapeTris = 0;
                    for (int m = 0; m < meshCount; m++)
                        shapeTris += SafeInt(() => c.Shape.TriangleCountInTriangleMesh(m));

                    if (trace && log != null)
                        log("assets: TRACE '" + c.Name + "' body='" + c.BodyName + "' flags=0x"
                            + FlagsHex(c.Flags) + " mobility=" + c.Mobility + " dyn=" + c.IsDynamic
                            + " meshCount=" + meshCount + " tris=" + shapeTris);

                    if (meshCount <= 0 || shapeTris <= 0)
                    {
                        exEmpty++;
                        continue;
                    }

                    MatrixFrame frame = SafeFrame(c.Entity);
                    int firstVert = verts.Count / 3;
                    int firstTri = tris.Count / 3;
                    int wrote = 0;
                    bool capped = false;
                    for (int m = 0; m < meshCount && !capped; m++)
                    {
                        int tc = SafeInt(() => c.Shape.TriangleCountInTriangleMesh(m));
                        for (int t = 0; t < tc; t++)
                        {
                            if (wrote >= PerShapeTriCap)
                            {
                                capped = true;
                                break;
                            }
                            try
                            {
                                c.Shape.GetTriangle(tri, m, t);
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
                            ledgeExempt.Add(isSteppedProp);
                            wrote++;
                        }
                    }

                    if (capped)
                    {
                        shapesCapped++;
                        if (log != null)
                            log("assets: WARNING shape '" + c.Name + "' capped at " + PerShapeTriCap
                                + " tris (had " + shapeTris + ")");
                    }

                    included++;
                    TrackLargest(largestTris, largestName, c.Name, wrote, 20);

                    if (recordEntities && wrote > 0)
                    {
                        var rec = new AssetEntityRecord();
                        rec.Name = c.Name;
                        rec.Flags = c.Flags;
                        rec.Mobility = (int)c.Mobility;
                        rec.TriCount = wrote;
                        rec.Capped = capped;
                        rec.VertsXyz = new float[wrote * 9];
                        int vCount = verts.Count / 3 - firstVert;
                        for (int v = 0; v < vCount; v++)
                        {
                            rec.VertsXyz[v * 3 + 0] = verts[(firstVert + v) * 3 + 0];
                            rec.VertsXyz[v * 3 + 1] = verts[(firstVert + v) * 3 + 1];
                            rec.VertsXyz[v * 3 + 2] = verts[(firstVert + v) * 3 + 2];
                        }
                        rec.Tris = new int[wrote * 3];
                        for (int t = 0; t < wrote * 3; t++)
                            rec.Tris[t] = tris[firstTri * 3 + t] - firstVert;
                        result.Entities.Add(rec);
                    }

                    // -------- synthesized ramp for a stepped / ladder prop
                    if (propRamps && wrote > 0 && SteppedPropRamps.IsSteppedOrLadder(unchecked((int)c.Flags)))
                    {
                        var prop = new SteppedPropRamps.Prop();
                        prop.Name = c.Name;
                        prop.Flags = unchecked((int)c.Flags);
                        int propVerts = verts.Count / 3 - firstVert;
                        prop.VertsXyz = new float[propVerts * 3];
                        for (int v = 0; v < propVerts; v++)
                        {
                            prop.VertsXyz[v * 3 + 0] = verts[(firstVert + v) * 3 + 0];
                            prop.VertsXyz[v * 3 + 1] = verts[(firstVert + v) * 3 + 1];
                            prop.VertsXyz[v * 3 + 2] = verts[(firstVert + v) * 3 + 2];
                        }
                        prop.Tris = new int[wrote * 3];
                        for (int t = 0; t < wrote * 3; t++)
                            prop.Tris[t] = tris[firstTri * 3 + t] - firstVert;

                        SteppedPropRamps.Result ramps = SteppedPropRamps.Synthesize(
                            new SteppedPropRamps.Prop[] { prop },
                            rampCellSize, rampClimb, rampLedge);
                        rampQuads += ramps.Ramps;
                        rampStairs += ramps.Stairs;
                        rampLadders += ramps.Ladders;
                        rampSkippedFlat += ramps.SkippedFlat;
                        rampSkippedSteep += ramps.SkippedSteep;
                        rampSkippedLong += ramps.SkippedLong;
                        rampCapped += ramps.Capped;

                        rampHoles += ramps.Holes;
                        rampNoHoles += ramps.NoHoles;
                        if (ramps.Tris.Count > 0)
                        {
                            int rampBase = rampVerts.Count / 3;
                            for (int k = 0; k < ramps.VertsXyz.Count; k++) rampVerts.Add(ramps.VertsXyz[k]);
                            for (int k = 0; k < ramps.Tris.Count; k++) rampTris.Add(ramps.Tris[k] + rampBase);

                            if (recordEntities)
                            {
                                // Kept as its own record: tooling can then tell a prop's real collision
                                // surface from the synthesized ramp, and a re-run of the snapshot must
                                // not synthesize a second ramp on top of this one.
                                var rr = new AssetEntityRecord();
                                rr.Name = c.Name + SteppedPropRamps.RampNameSuffix;
                                rr.Flags = (BodyFlags)0;
                                rr.Mobility = (int)c.Mobility;
                                rr.TriCount = ramps.Tris.Count / 3;
                                rr.Capped = false;
                                rr.VertsXyz = ramps.VertsXyz.ToArray();
                                rr.Tris = ramps.Tris.ToArray();
                                result.Entities.Add(rr);
                            }
                        }
                    }

                    if (c.IsStairs) cStairs++;
                    else if (IsWall(c.Name)) cWall++;
                    else if (IsFloor(c.Name)) cFloor++;
                    else cOther++;
                }
                catch (Exception ex)
                {
                    exceptions++;
                    if (log != null && exceptions <= 8)
                        log("assets: entity '" + c.Name + "' EXCEPTION " + Err(ex));
                }

                if (log != null && ((i + 1) % ProgressEvery == 0 || i + 1 == candidates.Count))
                    log("assets: progress entities=" + (i + 1) + "/" + candidates.Count
                        + " included=" + included + " tris=" + (tris.Count / 3));
            }

            watch.Stop();
            // Ramps are appended after every real triangle, so the mask is a true-tail and a build with
            // no ramps leaves ForceWalkableTris null (the builder then takes its unchanged path).
            int realTris = tris.Count / 3;
            int rampTriCount = rampTris.Count / 3;
            if (rampTriCount > 0)
            {
                int vBase = verts.Count / 3;
                for (int k = 0; k < rampVerts.Count; k++) verts.Add(rampVerts[k]);
                for (int k = 0; k < rampTris.Count; k++) tris.Add(rampTris[k] + vBase);
                var rampMask = new bool[realTris + rampTriCount];
                for (int k = realTris; k < rampMask.Length; k++) rampMask[k] = true;
                result.ForceWalkableTris = rampMask;
            }
            bool anyExempt = false;
            for (int k = 0; k < ledgeExempt.Count; k++)
                if (ledgeExempt[k]) { anyExempt = true; break; }
            if (anyExempt)
            {
                // A stepped prop's own triangles: the builder keeps their spans off the ledge trim.
                var exemptMask = new bool[realTris + rampTriCount];
                for (int k = 0; k < realTris && k < ledgeExempt.Count; k++)
                    exemptMask[k] = ledgeExempt[k];
                result.LedgeExemptTris = exemptMask;
            }
            result.VertsXyz = verts.ToArray();
            result.Tris = tris.ToArray();
            result.VertexCount = verts.Count / 3;
            result.TriangleCount = tris.Count / 3;
            result.EntitiesIncluded = included;
            result.ShapesCapped = shapesCapped;
            result.ScanMs = watch.ElapsedMilliseconds;
            result.LargestTris = largestTris;
            result.LargestNames = largestName;
            result.Count = count;
            result.Physics = physics;
            result.ExFlora = exFlora;
            result.ExBarrier = exBarrier;
            result.ExTerrain = exTerrain;
            result.ExLadder = exLadder;
            result.ExDynamic = exDynamic;
            result.ExNoShape = exNoShape;
            result.ExEmpty = exEmpty;
            result.ExExceptions = exceptions;
            result.ExUnsolid = exUnsolid;
            result.RampQuads = rampQuads;
            result.RampStairs = rampStairs;
            result.RampHoles = rampHoles;
            result.RampNoHoles = rampNoHoles;
            result.RampLadders = rampLadders;
            result.RampSkippedFlat = rampSkippedFlat;
            result.RampSkippedSteep = rampSkippedSteep;
            result.RampSkippedLong = rampSkippedLong;
            result.RampCapped = rampCapped;

            if (log != null)
            {
                log("assets: entities=" + count + " physics=" + physics + " included=" + included
                    + " excluded=[flora=" + exFlora + " barrier=" + exBarrier + " terrain=" + exTerrain
                    + " ladder=" + exLadder + " dynamic=" + exDynamic + " noShape=" + exNoShape
                    + " empty=" + exEmpty + " exceptions=" + exceptions + " unsolid=" + exUnsolid + "]"
                    + " tris=" + result.TriangleCount + " cappedShapes=" + shapesCapped
                    + " scanMs=" + result.ScanMs);
                log("assets: kinds stairs=" + cStairs + " wall=" + cWall + " floor=" + cFloor
                    + " other=" + cOther + " (name heuristic; log only)");
                if (propRamps)
                {
                    log("assets: stepped: ramps=" + rampQuads + " stairs=" + rampStairs
                        + " ladders=" + rampLadders + " holes=" + rampHoles + " noHoles=" + rampNoHoles
                        + " skipped=" + (rampSkippedFlat + rampSkippedSteep + rampSkippedLong)
                        + " (ramps bridge unsupported stretches only; treads are ledge-exempt)");
                    if (rampSkippedFlat > 0 || rampSkippedSteep > 0 || rampSkippedLong > 0 || rampCapped > 0)
                        log("assets: stepped: skipped detail flat=" + rampSkippedFlat + " steep="
                            + rampSkippedSteep + " long=" + rampSkippedLong + " capped=" + rampCapped
                            + "; " + SteppedPropRamps.Describe());
                }
                else
                {
                    log("assets: stepped: disabled (NavMeshVoxelSteppedProps=false)");
                }
                log("assets: largest shapes: " + LargestString(largestTris, largestName));
            }
            return result;
        }

        // ---------------------------------------------------------------- helpers
        private static string FlagsHex(BodyFlags f)
        {
            return unchecked((int)f).ToString("X8");
        }

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

        private static void TrackLargest(List<int> tris, List<string> names, string name, int triCount, int cap)
        {
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

        private static string SafeRawEntityName(GameEntity e)
        {
            try { return e.Name ?? ""; }
            catch (Exception) { return ""; }
        }

        private static string SafeEntityName(GameEntity e)
        {
            string n = SafeRawEntityName(e);
            return string.IsNullOrEmpty(n) ? "?" : n;
        }

        private static string SafeShapeName(PhysicsShape shape, string fallback)
        {
            try
            {
                string n = shape.GetName();
                if (!string.IsNullOrEmpty(n))
                    return n;
            }
            catch (Exception)
            {
            }
            return fallback ?? "";
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
