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

        // Appended to a barrier's name in the snapshot's per-entity records, so tooling can tell a
        // barrier from ordinary geometry without re-reading every record's flags. Mirrors
        // SteppedPropRamps.RampNameSuffix's role for synthesized ramps. The authoritative marker is
        // still BodyFlags.Barrier / Barrier3D - the suffix is a convenience, never the test.
        public const string BarrierRecordSuffix = "#barrier";

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
            // Honour-able barrier bodies, in the same engine Z-up world space. NEVER mixed into
            // VertsXyz/Tris: the builder rasterises these separately as solid spans (see
            // TiledNavMeshBuilder.CutBarriersIntoHeightfield), so they must not also enter the
            // walkable soup or the wall would be cut twice.
            public TiledNavMeshBuilder.BarrierWalls Barriers;
            public int BarriersMeshed, BarriersSkipped;
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
            // Honour-able barrier bodies, engine Z-up world space (see TiledNavMeshBuilder.BarrierWalls).
            // Their triangles are kept OUT of VertsXyz/Tris on purpose; the builder cuts them separately.
            public TiledNavMeshBuilder.BarrierWalls Barriers = new TiledNavMeshBuilder.BarrierWalls();
            // How many barrier bodies we actually read geometry for, and how many we refused because
            // their mask is missile-only. ExBarrier keeps its original meaning - barriers kept out of the
            // walkable soup - which is still every barrier, so the existing log line is unchanged.
            public int BarriersMeshed, BarriersSkipped;
            // Ladder-flagged bodies admitted by the classifier because the stepped-props feature is on
            // (they are excluded, and counted in ExLadder, when it is off).
            public int LadderIncluded;
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
            // A barrier body whose mask blocks agents. Such a candidate is read into the Barriers
            // list instead of the walkable soup.
            public bool IsBarrier;
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
            r.Barriers = g.Barriers;
            r.BarriersMeshed = g.BarriersMeshed;
            r.BarriersSkipped = g.BarriersSkipped;
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
            // Honour-able barriers: read exactly like any other body, but never mixed into the soup.
            var barrierCandidates = new List<Candidate>();
            var preloadNames = new List<string>();
            var preloadSeen = new HashSet<string>(StringComparer.Ordinal);

            int count = 0, physics = 0;
            int exFlora = 0, exBarrier = 0, exTerrain = 0, exLadder = 0, ladderIncluded = 0;
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
                    bool isBarrierForCarve = false;
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
                    if (isBarrier)
                    {
                        // A barrier is an invisible wall, and whether the navmesh must respect it is
                        // decided entirely by its blocking mask - never by its name (see the mask table
                        // and the engine's own exclude-flag presets in TiledNavMeshBuilder). ExBarrier
                        // keeps counting every barrier that stays out of the walkable soup, which is all
                        // of them: the honoured ones are read, but into Barriers, not into the soup.
                        exBarrier++;
                        if (!TiledNavMeshBuilder.BarrierBlocksAgents(unchecked((int)all)))
                        {
                            result.BarriersSkipped++;
                            result.Barriers.AddSkipped(name, unchecked((int)all));
                            continue;
                        }
                        // fall through: read it, but tag it so pass 2 routes it to Barriers
                        isBarrierForCarve = true;
                    }
                    // Ladders: excluded by default - a rung set is not a walkable surface and the engine
                    // handles ladders separately. With the stepped-props feature on they are collected
                    // like any other stepped prop: the rungs are steep-walkable surfaces, SteppedPropRamps
                    // bridges the unsupported stretches between them, and the synthesized ramp is widened
                    // to at least MinWidthMeters, so a narrow (0.6 m) ladder still leaves a navigable
                    // strip after erosion. The prop's own triangles are ledge-exempt like any stair's.
                    if (isLadder && !propRamps) { exLadder++; continue; }
                    if (isLadder) ladderIncluded++;
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
                    c.IsBarrier = isBarrierForCarve;
                    // A barrier never enters the walkable soup, so it goes into its own list and is
                    // preloaded with everything else (bo_barrier is a real physics shape and GetTriangle
                    // read-AVs the process on a body whose native mesh table is not loaded yet).
                    if (isBarrierForCarve) barrierCandidates.Add(c);
                    else candidates.Add(c);

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
            var rampSkips = new List<SteppedPropRamps.Skip>();
            var rampConnectorInfos = new List<SteppedPropRamps.RampInfo>();
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

                        // Scene probe for the connector: is the engine point free of the collected solid
                        // geometry? The soup is engine Z-up; a point is blocked when any triangle's XZ
                        // projection contains it and its plane is at/above the point's height (within a
                        // small band). O(soup) per probe but only for ladder connectors.
                        prop.IsFreeAt = delegate (float px, float py, float pz)
                        {
                            return ScenePointFree(verts, tris, px, py, pz);
                        };
                        // Surface seeker: the engine height of the highest soup top within a small XZ
                        // footprint around the point, bounded by the reach. Used to anchor the connector
                        // on the landing/floor even when the ladder's physics does not reach it.
                        prop.SeekSurface = delegate (float px, float py, float fromHeight, bool up, float reach)
                        {
                            return SceneSeekSurface(verts, tris, px, py, fromHeight, up, reach);
                        };

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
                        for (int k = 0; k < ramps.Skips.Count; k++) rampSkips.Add(ramps.Skips[k]);
                        for (int k = 0; k < ramps.Infos.Count; k++)
                            if (ramps.Infos[k].TopPadMeters > 0f) rampConnectorInfos.Add(ramps.Infos[k]);

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

            // ---------------------------------------------------------------- pass 2b
            // Barrier bodies. Identical read to the soup pass (same shape API, same preload, same
            // crash hardening, same trace line) but the output goes into result.Barriers and NEVER
            // into verts/tris: the builder cuts a barrier as solid geometry on its own terms (with the
            // height rule), so letting it into the soup as well would cut it twice.
            //
            // This is also where the two `barrier_builder` entities' GENERATED bodies are picked up.
            // Their game_entity has no transform and no authored triangles - the engine builds the
            // `bo_barrier` body from the referenced path when the scene loads - but the built body is
            // an ordinary physics body, so it is simply one more entity in the scan.
            for (int i = 0; i < barrierCandidates.Count; i++)
            {
                Candidate c = barrierCandidates[i];
                try
                {
                    int meshCount = SafeInt(() => c.Shape.TriangleMeshCount());
                    int shapeTris = 0;
                    for (int m = 0; m < meshCount; m++)
                        shapeTris += SafeInt(() => c.Shape.TriangleCountInTriangleMesh(m));
                    if (trace && log != null)
                        log("assets: TRACE barrier '" + c.Name + "' body='" + c.BodyName + "' flags=0x"
                            + FlagsHex(c.Flags) + " meshCount=" + meshCount + " tris=" + shapeTris);

                    if (meshCount <= 0 || shapeTris <= 0)
                    {
                        // An empty barrier body is a barrier with nothing to carve; count it as skipped
                        // so the log cannot claim a wall the scene does not actually have.
                        result.BarriersSkipped++;
                        result.Barriers.AddSkipped(c.Name, unchecked((int)c.Flags));
                        exEmpty++;
                        continue;
                    }

                    MatrixFrame frame = SafeFrame(c.Entity);
                    var bv = new List<float>();
                    var bt = new List<int>();
                    bool capped = false;
                    int wrote = 0;
                    for (int m = 0; m < meshCount && !capped; m++)
                    {
                        int tc = SafeInt(() => c.Shape.TriangleCountInTriangleMesh(m));
                        for (int t = 0; t < tc; t++)
                        {
                            if (wrote >= PerShapeTriCap) { capped = true; break; }
                            try { c.Shape.GetTriangle(tri, m, t); }
                            catch (Exception) { continue; }
                            int b = bv.Count / 3;
                            for (int k = 0; k < 3; k++)
                            {
                                Vec3 w = frame.TransformToParent(tri[k]);
                                bv.Add(w.x); bv.Add(w.y); bv.Add(w.z);
                            }
                            bt.Add(b); bt.Add(b + 1); bt.Add(b + 2);
                            wrote++;
                        }
                    }
                    if (capped) shapesCapped++;
                    if (wrote <= 0)
                    {
                        result.BarriersSkipped++;
                        result.Barriers.AddSkipped(c.Name, unchecked((int)c.Flags));
                        continue;
                    }

                    float zMin = float.MaxValue, zMax = float.MinValue;
                    for (int v = 0; v < bv.Count / 3; v++)
                    {
                        float z = bv[v * 3 + 2];
                        if (z < zMin) zMin = z;
                        if (z > zMax) zMax = z;
                    }
                    var wall = new TiledNavMeshBuilder.BarrierWall();
                    wall.Name = c.Name;
                    wall.Flags = unchecked((int)c.Flags);
                    wall.VertsXyz = bv.ToArray();
                    wall.Tris = bt.ToArray();
                    // `Flip` is the component's one-way direction flag. Recorded, not honoured: the
                    // navmesh is undirected, so the cut is two-sided either way (see README.md).
                    wall.Flip = false;
                    wall.ZMin = zMin;
                    wall.ZMax = zMax;
                    result.Barriers.Add(wall);
                    result.BarriersMeshed++;

                    // The snapshot is the offline test case, so a barrier has to be IN it: that is
                    // what makes the flag histogram (and any offline re-run) able to see the wall at
                    // all. Kept as its own record with the barrier's real BodyFlags so tooling can tell
                    // it from ordinary geometry by flag, never by name.
                    if (recordEntities)
                    {
                        var rec = new AssetEntityRecord();
                        rec.Name = c.Name + BarrierRecordSuffix;
                        rec.Flags = c.Flags;
                        rec.Mobility = (int)c.Mobility;
                        rec.TriCount = wrote;
                        rec.Capped = capped;
                        rec.VertsXyz = wall.VertsXyz;
                        rec.Tris = wall.Tris;
                        result.Entities.Add(rec);
                    }
                    if (log != null)
                        log("assets: barrier '" + c.Name + "' flags=0x" + FlagsHex(c.Flags)
                            + " meshCount=" + meshCount + " tris=" + wrote
                            + " z=[" + zMin.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                            + ".." + zMax.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                            + "]m (carved as solid; excluded from the walkable soup)");
                }
                catch (Exception ex)
                {
                    exceptions++;
                    if (log != null && exceptions <= 8)
                        log("assets: barrier entity '" + c.Name + "' EXCEPTION " + Err(ex));
                }
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
            result.LadderIncluded = ladderIncluded;
            // Honour masks: a barrier body is carved only when its mask blocks agents. Decided from
            // BodyFlags, never from the entity's name.
            if (exBarrier > 0)
            {
                log("assets: barriers: bodies=" + (result.BarriersMeshed + result.BarriersSkipped)
                    + " carved=" + result.BarriersMeshed + " tris=" + result.Barriers.TotalTris
                    + " skipped=" + result.BarriersSkipped
                    + " (missile-only masks are not carved; carved walls stay out of the walkable soup)");
                for (int k = 0; k < result.Barriers.Skipped.Count && k < 8; k++)
                    log("assets: barriers: skipped " + result.Barriers.Skipped[k]);
                if (result.Barriers.Skipped.Count > 8)
                    log("assets: barriers: skipped ... (+" + (result.Barriers.Skipped.Count - 8) + " more)");
            }
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
                    + (ladderIncluded > 0 ? " laddersIncluded=" + ladderIncluded : "")
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
                    // One line per skipped prop asset, folded by (name, reason), capped.
                    List<SteppedPropRamps.Skip> foldedSkips = SteppedPropRamps.FoldSkips(rampSkips);
                    for (int k = 0; k < foldedSkips.Count && k < 20; k++)
                        log("assets: " + SteppedPropRamps.DescribeSkip(foldedSkips[k]));
                    if (foldedSkips.Count > 20)
                        log("assets: ramps: skip ... (+" + (foldedSkips.Count - 20) + " more)");
                    // Connector top-pad decisions (offset symbolic links): reach + direction.
                    for (int k = 0; k < rampConnectorInfos.Count && k < 20; k++)
                    {
                        SteppedPropRamps.RampInfo ri = rampConnectorInfos[k];
                        log("assets: connector pad '" + ri.Prop + "' topPad=" + ri.TopPadMeters.ToString("0.###",
                            System.Globalization.CultureInfo.InvariantCulture) + "m dir=("
                            + ri.DirX.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ","
                            + ri.DirHs.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ")");
                    }
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

        // Scene collision probe for the connector: true when the engine-space point (px,py,pz) is free
        // of the collected solid soup. A triangle blocks the point when its XZ projection contains the
        // point and the triangle's plane there is at/above the point's height within a small band (i.e.
        // there is a solid surface just above/at the point). Engine Z-up: verts are (x, y, z).
        private static bool ScenePointFree(List<float> verts, List<int> tris, float px, float py, float pz)
        {
            int triCount = tris.Count / 3;
            for (int t = 0; t < triCount; t++)
            {
                int i0 = tris[t * 3 + 0], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
                float ax = verts[i0 * 3], ay = verts[i0 * 3 + 1], az = verts[i0 * 3 + 2];
                float bx = verts[i1 * 3], by = verts[i1 * 3 + 1], bz = verts[i1 * 3 + 2];
                float cx = verts[i2 * 3], cy = verts[i2 * 3 + 1], cz = verts[i2 * 3 + 2];
                // Quick AABB reject in XZ.
                if (px < Math.Min(ax, Math.Min(bx, cx)) || px > Math.Max(ax, Math.Max(bx, cx))) continue;
                if (py < Math.Min(ay, Math.Min(by, cy)) || py > Math.Max(ay, Math.Max(by, cy))) continue;
                if (!PointInTriXZ(px, py, ax, ay, bx, by, cx, cy)) continue;
                float planeZ = TriHeightAtXZ(px, py, ax, az, ay, bx, bz, by, cx, cz, cy);
                if (float.IsNaN(planeZ)) continue;
                // Solid just at/above the probe blocks it.
                if (planeZ >= pz - 0.3f) return false;
            }
            return true;
        }

        // Surface seeker for the connector's endpoints: the engine height of the highest soup TOP surface
        // whose XZ projection contains the point, within the reach band from fromHeight. up=true band is
        // [fromHeight, fromHeight+reach] (the landing above); up=false band is [fromHeight-reach,
        // fromHeight] (the floor below). NaN when nothing is found in the band. Deterministic.
        private static float SceneSeekSurface(List<float> verts, List<int> tris, float px, float py,
            float fromHeight, bool up, float reach)
        {
            float lo = up ? fromHeight - 0.5f : fromHeight - reach;
            float hi = up ? fromHeight + reach : fromHeight;
            int triCount = tris.Count / 3;
            float best = float.NaN;
            for (int t = 0; t < triCount; t++)
            {
                int i0 = tris[t * 3 + 0], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
                float ax = verts[i0 * 3], ay = verts[i0 * 3 + 1], az = verts[i0 * 3 + 2];
                float bx = verts[i1 * 3], by = verts[i1 * 3 + 1], bz = verts[i1 * 3 + 2];
                float cx = verts[i2 * 3], cy = verts[i2 * 3 + 1], cz = verts[i2 * 3 + 2];
                if (px < Math.Min(ax, Math.Min(bx, cx)) || px > Math.Max(ax, Math.Max(bx, cx))) continue;
                if (py < Math.Min(ay, Math.Min(by, cy)) || py > Math.Max(ay, Math.Max(by, cy))) continue;
                if (!PointInTriXZ(px, py, ax, ay, bx, by, cx, cy)) continue;
                float z = TriHeightAtXZ(px, py, ax, az, ay, bx, bz, by, cx, cz, cy);
                if (float.IsNaN(z)) continue;
                if (z < lo - 1e-4f || z > hi + 1e-4f) continue;
                if (float.IsNaN(best) || z > best) best = z;   // highest top within the band
            }
            return best;
        }

        // Inside-or-on-edge XZ triangle test (py is the horizontal second axis here).
        private static bool PointInTriXZ(float px, float py, float ax, float ay, float bx, float by,
            float cx, float cy)
        {
            float d1 = (px - bx) * (ay - by) - (ax - bx) * (py - by);
            float d2 = (px - cx) * (by - cy) - (bx - cx) * (py - cy);
            float d3 = (px - ax) * (cy - ay) - (cx - ax) * (py - ay);
            bool neg = d1 < 0f || d2 < 0f || d3 < 0f;
            bool pos = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(neg && pos);
        }

        // Barycentric height (engine Z) of a triangle at an XZ point (xa/ya, xb/yb, xc/yc are the
        // horizontal coords; za/zb/zc the heights). NaN when the XZ projection is degenerate.
        private static float TriHeightAtXZ(float px, float py, float xa, float za, float ya,
            float xb, float zb, float yb, float xc, float zc, float yc)
        {
            float den = (yb - yc) * (xa - xc) + (xc - xb) * (ya - yc);
            if (den > -1e-9f && den < 1e-9f) return float.NaN;
            float inv = 1f / den;
            float wa = ((yb - yc) * (px - xc) + (xc - xb) * (py - yc)) * inv;
            float wb = ((yc - ya) * (px - xc) + (xa - xc) * (py - yc)) * inv;
            float wc = 1f - wa - wb;
            return wa * za + wb * zb + wc * zc;
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
