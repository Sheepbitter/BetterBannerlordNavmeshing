using System;
using System.Globalization;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace BetterBannerlordNavmeshing
{
    // Editor entry point (primary). Attach to any entity in a scene that has
    // terrain; the "Export NavMesh" button reads the scene's live in-memory
    // terrain, builds a navmesh prefab and imports its faces into the scene.
    // Also hosts the shared pipeline used by the in-game mission logic.
    public class SceneNavMeshExporter : ScriptComponentBehavior
    {
        private const string BuildTag = "Build 3 (adaptive navmesh)";

        // ---------------- Settings (same names as the terrain script) ----------------
        public SimpleButton ExportNavMesh;
        public SimpleButton ImportNavMeshOnly;
        public SimpleButton ProbeGeometry;

        // Offline test case: write the export's main-thread inputs (terrain grid + per-entity collision
        // triangles) plus a human-readable header, so the standalone harness can rebuild the exact soup
        // for this scene without the editor. See SceneSnapshot / README.
        public SimpleButton DumpSceneSnapshot;
        public int NavMeshSampleStride = 2;
        public float NavMeshMaxErrorMeters = 0.25f;
        public float NavMeshCoarsestSpacing = 16f;
        public float NavMeshMaxSlopeDegrees = 50f;
        public bool NavMeshCullBelowHeight = false;
        public float NavMeshMinHeight = 0f;
        public float NavMeshHeightOffsetMeters = 0.5f;
        public string NavMeshPrefabName = "";
        public string NavMeshExportFolder = "";
        public bool NavMeshClearExistingFaces = true;
        public bool NavMeshImportIntoScene = true;
        public bool NavMeshWriteObj = false;

        // Voxel core (navmesh v2, the default since 2026-09-29). The Export button runs the tiled
        // voxel pipeline (TiledNavMeshBuilder) instead of the v1 adaptive terrain mesher.
        // Terrain-only for now; set false to use the v1 mesher.
        public bool NavMeshUseVoxelCore = true;
        // Voxel cell size (m). Default 0.25 m since 2026-09-29: the connectivity lab shows 1.0-1.2 m
        // doorways, walkways and stair treads systematically vanish at 0.5 m (erosion + quantization
        // leave 0 cells regardless of the radius setting). 0.25 costs ~4x build time and ~2-4x faces;
        // raise it to 0.5 for fast iteration on open terrain.
        public float NavMeshVoxelCellSize = 0.25f;

        // Agent max climb (m) for the v2 voxel core. 0 = auto-derive so that
        // NavMeshMaxSlopeDegrees is actually traversable (Recast's ledge filter
        // needs walkableClimb >= 2*cs*tan(slope)/ch). See the README.
        public float NavMeshVoxelAgentClimbMeters = 0f;

        // Agent radius (m) for the v2 voxel core. Erosion = ceil(radius/cellSize) cells each side and
        // the tile border is 3 + ceil(radius/cellSize). Default 0.25 m: at cellSize 0.5 this is still
        // erosion 1, but at cellSize 0.25 it stays 1 instead of 2, which is what keeps 1.0-1.2 m
        // doorways / walkways / stair treads connected. See the connectivity lab in the README.
        public float NavMeshVoxelAgentRadius = 0.25f;

        // Recast ledge filter (default true = Recast behaviour). It trims spans within walkableClimb of
        // a drop, which keeps the mesh off real cliff edges but also eats the border cells of narrow
        // walkways/bridges that have drops on both sides. Turn off if narrow structures keep losing
        // their edges (or if a level is connected on one side but not the other).
        public bool NavMeshVoxelLedgeFilter = true;

        // Gap fill (cells, default 2, 0 = off). Before erosion, fills a column with no walkable span
        // when walkable spans flank it on opposite sides along one axis with an empty run of at most
        // this many cells and a floor difference within the agent climb. Closes abutting-prefab /
        // paved-terrace seams (a hairline collision gap that erosion would widen into a break).
        public int NavMeshVoxelGapFillCells = 2;

        // Stitch (cells, default 4 = 1.0 m at cellSize 0.25, 0 = off). Per tile: reconnects two mesh
        // components whose nearest boundary edges are within this many cells and within the agent climb,
        // by adding a small quad that reuses existing vertices. Refused across walls/doors/ceilings.
        public int NavMeshVoxelStitchMaxGapCells = 6;

        // Prune small isolated islands after the merge (sealed-void lids, stray fragments).
        public bool NavMeshVoxelPruneIsolatedIslands = true;
        public int NavMeshVoxelPruneMaxIslandFaces = 400;

        // Final-mesh sliver cleanup (thin/tiny triangles). Default ON, documented in the README.
        public bool NavMeshVoxelCleanup = true;

        // Final quad merge, part of the cleanup (default ON). Thin faces are combined with a
        // near-coplanar neighbour into one emitted 4-vertex face; geometry is unchanged (only the
        // shared interior edge disappears). Switch OFF instantly if the engine's importer ever
        // dislikes n-gon faces.
        public bool NavMeshVoxelQuadMerge = true;

        // Extra probe points for the scene snapshot (format "x,y;x,y;...", default empty). The harness
        // asserts each point is covered by a face on the largest island after the build.
        public string NavMeshSnapshotAssertPoints = "";

        // Detail mesh for the v2 voxel core (default true). Adds interior vertices so faces follow
        // the terrain surface instead of chording under hills/knolls; bounded by
        // NavMeshMaxErrorMeters. Disable for a leaner bin (flat/plateau scenes only).
        public bool NavMeshVoxelDetailMesh = true;

        // v2 detail-mesh sampling distance (m). 0 = auto (max(0.9, cellSize)). Default 2.0 m: the
        // harness sweep shows it removes the tight/sliver faces Recast's detail edge tessellation
        // makes along walkable boundaries, with no fidelity loss. Lower it for more surface detail
        // (more faces); raise it (up to ~2*minPolyExtent) for fewer faces.
        public float NavMeshVoxelDetailSampleDist = 2.0f;

        // v2 contour simplification error (voxels). 0 = auto (NavMeshMaxErrorMeters / cellSize).
        // Default 1.3 vox (the plan's value): a simpler walkable boundary removes most of the
        // remaining boundary slivers on boundary-rich maps at no fidelity cost.
        public float NavMeshVoxelContourMaxError = 1.3f;

        // Phase 3: ingest static asset collision geometry (interiors, stairs, decks, bridges over
        // terrain) into the v2 voxel core. NavMeshHeightOffsetMeters lifts the whole merged mesh
        // (terrain and structures alike) so it never intersects either. Dynamic props (e.g. merlons)
        // and flora are toggled separately.
        public bool NavMeshVoxelIncludeAssets = true;
        public bool NavMeshVoxelIncludeDynamic = true;
        public bool NavMeshVoxelIncludeFlora = false;

        // Diagnostic (default off): log one line per included entity immediately before its native
        // PhysicsShape.GetTriangle read. If that native call ever read-AVs the process, the last line
        // in the log names the culprit entity/body. Cheap enough to leave off in normal runs.
        public bool NavMeshVoxelAssetTrace = false;

        // Stepped-prop handling (v2 voxel core only, DEFAULT OFF until the acceptance bar holds).
        // Two mechanisms, both keeping the ordinary 50 deg cap for everything else:
        //  - stairs: the triangles of a HasSteps/Ladder-flagged entity keep their ordinary slope
        //    decision but their spans are exempt from Recast's ledge trim (a per-tile marker pass in
        //    the builder restores exactly the spans the trim nulled). A 51-56 deg flight then stays
        //    walkable without any synthesized geometry;
        //  - ladders/holes: SteppedPropRamps synthesizes a ramp ONLY across a stretch of the run with
        //    no collision (a hole up to MaxBridgeRunMeters), with true-level end aprons, so a ramp can
        //    never hover above an existing tread.
        // See SteppedPropRamps and the README "Stairs & ladders".
        public bool NavMeshVoxelSteppedProps = true;

        // Diagnostic (default off): also write <prefab>_soup_terrain.obj and <prefab>_soup_assets.obj
        // next to the bin (engine space). Lets a failing spot be inspected with geometry provenance
        // (terrain soup vs asset triangles) in the editor or Blender.
        public bool NavMeshVoxelWriteSoupObj = false;

        // Terrain-hole audit (default off). The engine's bulk node arrays carry no hole information, so
        // a carve can become a false walkable lid. When on, every sampled terrain vertex is compared
        // against the hole-aware point query Scene.GetTerrainHeight(pos, checkHoles: true) and any
        // vertex that disagrees by more than 1 m is treated as a hole: its terrain cells are dropped
        // from the soup. Costs one native point query per vertex (see the README for the measured cost).
        public bool NavMeshVoxelTerrainHoleAudit = false;

        // Run the v2 voxel build on a background thread so the editor UI stays alive during the
        // 3-60 s builds. Only the pure-managed voxel build runs off-thread; the terrain read and the
        // bin write + native import stay on the editor main thread (scene access must be there).
        // Completion is picked up on the editor tick (decompiled: ManagedScriptHolder
        // .TickComponentsEditor -> ScriptComponentBehavior.OnEditorTick, called for every script
        // component in the open scene). Ignored for the v1 mesher (NavMeshUseVoxelCore=false), which
        // stays synchronous. See the README ("Async build").
        public bool NavMeshAsyncBuild = true;

        // ---------------- Logging ----------------
        // In-editor Console.WriteLine is invisible, so every run is also written
        // to a per-session log file (see NavMeshLog).
        public bool NavMeshWriteLogFile = true;
        public string NavMeshLogFolder = "";

        // ---------------- Async build state ----------------
        // Main-thread only, except the worker's writes to AsyncVoxelBuild.Mesh/BuildMs/Error and the
        // final volatile AsyncVoxelBuild.Done publish.
        private AsyncVoxelBuild _asyncBuild;

        // Editor-tick instrumentation for the async path.
        private bool _periodActive;
        private bool _periodIsBuild;
        private long _lastTickTimestamp;
        private int _buildTicks;
        private double _buildMaxTickMs;
        private int _postImportTicksRemaining;
        private int _postImportTicks;
        private double _postImportMinMs;
        private double _postImportMaxMs;
        private double _postImportSumMs;

        // Carries one async v2 build from the dispatch on the main thread to the worker and back.
        // Plain data only: the worker never touches Scene/game objects (only the terrain height
        // array, the pure voxel builder, the NMG8 serializer and the thread-safe NavMeshLog).
        private sealed class AsyncVoxelBuild
        {
            public Scene Scene;
            public float[] GridHeights;
            public int GridWidth;
            public int GridHeight;
            public float GridSpacing;
            public TerrainNavMeshBuilder.Settings Settings;
            public TiledNavMeshBuilder.TiledNavMeshSettings Voxel;
            public float AgentClimbMeters;
            public float[] AssetVertsEngineXyz;
            public int[] AssetTris;
            public bool[] AssetForceWalkable;
            public bool[] AssetLedgeExempt;
            public bool[] TerrainHoleMask;
            public VoxelNavMeshGenerator.SoupCapture SoupCapture;
            public long AssetsMs;
            public int FacesBefore;
            public long SampleMs;
            public string PrefabNameOverride;
            public string FolderOverride;
            public bool ClearExisting;
            public bool ImportIntoScene;
            public System.Action<string> Log;
            public System.Diagnostics.Stopwatch RunWatch;

            public volatile bool Done;
            public TerrainNavMeshBuilder.Result Mesh;
            public long BuildMs;
            public Exception Error;
        }

        public override TickRequirement GetTickRequirement()
            => TickRequirement.Tick | base.GetTickRequirement();

        protected override void OnInit()
        {
            base.OnInit();
            SetScriptComponentToTick(GetTickRequirement());
        }

        protected override void OnTick(float dt)
        {
        }

        // The editor calls this for every script component in the open scene each editor frame
        // (decompiled: ManagedScriptHolder.SetScriptComponentHolder adds the component to
        // _toTickForEditor, and TickComponentsEditor invokes OnEditorTick(dt)). It is used to pick
        // up a finished background build and to sample the editor frame time during/after a build.
        protected override void OnEditorTick(float dt)
        {
            base.OnEditorTick(dt);
            TrackEditorTick();
            CompleteAsyncBuildIfReady();
        }

        protected override void OnEditorVariableChanged(string variableName)
        {
            base.OnEditorVariableChanged(variableName);

            // Safety net: also pick up a finished background build here, in case the editor
            // ever stops delivering OnEditorTick for this component.
            CompleteAsyncBuildIfReady();

            if (variableName == nameof(ExportNavMesh))
                ExportFromEditor();
            else if (variableName == nameof(ImportNavMeshOnly))
                ImportOnlyFromEditor();
            else if (variableName == nameof(ProbeGeometry))
                ProbeFromEditor();
            else if (variableName == nameof(DumpSceneSnapshot))
                DumpSnapshotFromEditor();
        }

        // Imports an already-written prefab without sampling or building. This is
        // the cheap second half of the startup-prefab-list workflow: Export once
        // (writes the bin), restart the editor, then press Import Only.
        private void ImportOnlyFromEditor()
        {
            try
            {
                NavMeshLog.Initialize(NavMeshWriteLogFile, NavMeshLogFolder);
                System.Action<string> log = NavMeshLog.Write;

                Scene scene = Scene;
                if (scene == null)
                {
                    log("ERROR: no scene.");
                    return;
                }

                log("run: build=" + BuildTag + " trigger=editor-import-only scene=" + SafeName(scene)
                    + " machine=" + Environment.MachineName
                    + " time=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    + " logFile=" + (NavMeshLog.CurrentFilePath ?? "(disabled)"));
                log("settings: importOnly=True clearExisting=" + NavMeshClearExistingFaces
                    + " prefabName='" + (NavMeshPrefabName ?? "") + "'"
                    + " exportFolder='" + (NavMeshExportFolder ?? "") + "'"
                    + " writeLogFile=" + NavMeshWriteLogFile
                    + " logFolder='" + (NavMeshLogFolder ?? "") + "'");

                string prefabName = TerrainNavMeshGenerator.ResolvePrefabName(scene, NavMeshPrefabName, log);
                string folder = TerrainNavMeshGenerator.ResolveFolder(scene, NavMeshExportFolder, log);
                log("import only: prefab='" + prefabName + "' folder=" + folder);

                int facesBeforeRun = SafeFaceCount(scene);
                log("faces before run: " + (facesBeforeRun < 0 ? "unavailable" : facesBeforeRun.ToString()));

                if (NavMeshClearExistingFaces && facesBeforeRun > 0)
                {
                    log("clearing existing navmesh faces...");
                    try
                    {
                        scene.ClearNavMesh();
                        log("cleared.");
                    }
                    catch (Exception ex)
                    {
                        log("clear failed (continuing): " + ex.Message);
                    }
                }

                log("importing prefab '" + prefabName + "' (shift 0)...");
                scene.ImportNavigationMeshPrefab(prefabName, 0);
                log("import call returned.");

                int facesAfterRun = SafeFaceCount(scene);
                log("faces after import: " + (facesAfterRun < 0 ? "unavailable" : facesAfterRun.ToString()));
                LogImportCheck(log, facesBeforeRun, facesAfterRun, NavMeshClearExistingFaces);

                log("done: importOnly=true prefab='" + prefabName + "'");
            }
            catch (Exception ex)
            {
                NavMeshLog.Write("ERROR: " + ex.GetType().Name + ": " + Flat(ex.Message));
                NavMeshLog.Write("ERROR stack: " + Flat(ex.StackTrace));
            }
        }

        // Shared post-import diagnosis for both the full run and Import Only. The
        // engine builds its prefab list at startup, so a prefab written this
        // session is not found on the first attempt.
        private static void LogImportCheck(System.Action<string> log, int facesBeforeRun, int facesAfterRun, bool clearExistingFaces)
        {
            bool increased;
            if (facesBeforeRun < 0 || facesAfterRun < 0)
                increased = true; // unknown count: do not warn
            else if (clearExistingFaces)
                increased = facesAfterRun > 0;
            else
                increased = facesAfterRun > facesBeforeRun;

            log("import check: existing=" + (facesBeforeRun < 0 ? "?" : facesBeforeRun.ToString())
                + " afterImport=" + (facesAfterRun < 0 ? "?" : facesAfterRun.ToString())
                + " clearExisting=" + clearExistingFaces
                + " increased=" + increased);

            if (!increased)
            {
                log("import warning: the engine's prefab list is built at startup; if this prefab was just written, restart the editor and press Export again (or use the navmesh panel -> Import faces from prefabs).");
            }
        }

        // Phase 1 diagnostic: dump the scene's collision-geometry capabilities to
        // the log file. Never throws.
        private void ProbeFromEditor()
        {
            try
            {
                NavMeshLog.Initialize(NavMeshWriteLogFile, NavMeshLogFolder);
                NavMeshLog.Write("===== probe run @ "
                    + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " =====");

                Vec3 column = Vec3.Zero;
                try
                {
                    WeakGameEntity entity = GameEntity;
                    BoundingBox bb = entity.GetGlobalBoundingBox();
                    column = new Vec3((bb.min.x + bb.max.x) * 0.5f,
                        (bb.min.y + bb.max.y) * 0.5f,
                        (bb.min.z + bb.max.z) * 0.5f);
                }
                catch (Exception)
                {
                }

                NavMeshLog.Write("probe: scene=" + SafeName(Scene) + " column=("
                    + F(column.x) + "," + F(column.y) + "," + F(column.z) + ")");
                SceneGeometryProbe.Run(Scene, column, NavMeshLog.Write);
            }
            catch (Exception ex)
            {
                NavMeshLog.Write("ERROR: " + ex.GetType().Name + ": " + Flat(ex.Message));
            }
        }

        // Offline test case: writes the export's main-thread inputs next to the bin so the harness can
        // rebuild the exact soup without the editor. Never throws.
        private void DumpSnapshotFromEditor()
        {
            try
            {
                NavMeshLog.Initialize(NavMeshWriteLogFile, NavMeshLogFolder);
                System.Action<string> log = NavMeshLog.Write;
                Scene scene = Scene;
                if (scene == null)
                {
                    log("ERROR: no scene.");
                    return;
                }

                var watch = System.Diagnostics.Stopwatch.StartNew();
                log("===== snapshot dump @ "
                    + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " =====");
                log("snapshot: scene=" + SafeName(scene) + " machine=" + Environment.MachineName);

                string prefabName = TerrainNavMeshGenerator.ResolvePrefabName(scene, NavMeshPrefabName, log);
                string folder = TerrainNavMeshGenerator.ResolveFolder(scene, NavMeshExportFolder, log);

                // The dump writes three files into this folder; a scene in another module may resolve a
                // NavMeshPrefabs folder that does not exist yet (the export path creates it, the dump did
                // not). Create it here so a dump of e.g. CC_20_siege never throws DirectoryNotFoundException.
                if (!string.IsNullOrEmpty(folder))
                    System.IO.Directory.CreateDirectory(folder);

                // Terrain: the same sampler (and the same hole-audit setting) the export uses.
                var terrainWatch = System.Diagnostics.Stopwatch.StartNew();
                SceneTerrainSampler.TerrainGrid grid =
                    SceneTerrainSampler.Read(scene, NavMeshVoxelTerrainHoleAudit, log);
                terrainWatch.Stop();

                var td = new SceneSnapshot.TerrainData();
                td.Width = grid.Width;
                td.Height = grid.Height;
                td.Spacing = grid.Spacing;
                td.Stride = NavMeshSampleStride;
                td.Heights = grid.Heights;
                string terrainPath = System.IO.Path.Combine(folder, prefabName + "_snapshot_terrain.bin");
                SceneSnapshot.WriteTerrainBin(terrainPath, td);

                // Assets: the same collector policy the export uses, keeping per-entity groups.
                var assetsWatch = System.Diagnostics.Stopwatch.StartNew();
                SceneCollisionGeometry.AssetGeometry assets = SceneCollisionGeometry.CollectDetailed(
                    scene, NavMeshVoxelIncludeDynamic, NavMeshVoxelIncludeFlora, NavMeshVoxelAssetTrace,
                    true, NavMeshVoxelSteppedProps, log, NavMeshVoxelCellSize,
                    ResolvedRampClimb(NavMeshVoxelAgentClimbMeters, NavMeshVoxelCellSize, NavMeshMaxSlopeDegrees), NavMeshVoxelLedgeFilter);
                assetsWatch.Stop();

                var records = new System.Collections.Generic.List<SceneSnapshot.EntityRecord>();
                for (int i = 0; i < assets.Entities.Count; i++)
                {
                    SceneCollisionGeometry.AssetEntityRecord src = assets.Entities[i];
                    records.Add(SceneSnapshot.Expand(src.Name, unchecked((int)src.Flags), src.Mobility,
                        src.Capped, src.VertsXyz, src.Tris));
                }
                string assetsPath = System.IO.Path.Combine(folder, prefabName + "_snapshot_assets.bin");
                SceneSnapshot.WriteAssetsBin(assetsPath, records);

                // Assert points: the two probe spots reported from Birtu, plus anything the user adds.
                var points = new System.Collections.Generic.List<string>();
                points.Add("329.336,285.954");
                points.Add("273.119,313.297");
                System.Collections.Generic.List<float[]> extra =
                    SceneSnapshot.ParsePoints(NavMeshSnapshotAssertPoints);
                for (int i = 0; i < extra.Count; i++)
                    points.Add(extra[i][0].ToString("0.###", CultureInfo.InvariantCulture) + ","
                        + extra[i][1].ToString("0.###", CultureInfo.InvariantCulture));

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("scene: " + SafeName(scene));
                sb.AppendLine("date: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                sb.AppendLine("machine: " + Environment.MachineName);
                sb.AppendLine("settings: stride=" + NavMeshSampleStride
                    + " maxSlope=" + F(NavMeshMaxSlopeDegrees) + "deg"
                    + " maxError=" + F(NavMeshMaxErrorMeters) + "m"
                    + " coarsest=" + F(NavMeshCoarsestSpacing) + "m"
                    + " heightOffset=" + F(NavMeshHeightOffsetMeters) + "m"
                    + " cellSize=" + F(NavMeshVoxelCellSize) + "m"
                    + " radius=" + F(NavMeshVoxelAgentRadius) + "m"
                    + " climb=" + F(NavMeshVoxelAgentClimbMeters) + "m"
                    + " detail=" + NavMeshVoxelDetailMesh
                    + " detailSampleDist=" + F(NavMeshVoxelDetailSampleDist) + "m"
                    + " contourMaxError=" + F(NavMeshVoxelContourMaxError) + "vox"
                    + " gapFill=" + NavMeshVoxelGapFillCells
                    + " stitch=" + NavMeshVoxelStitchMaxGapCells
                    + " prune=" + (NavMeshVoxelPruneIsolatedIslands
                        ? NavMeshVoxelPruneMaxIslandFaces.ToString(CultureInfo.InvariantCulture) : "off")
                    + " steppedProps=" + NavMeshVoxelSteppedProps
                    + " quadMerge=" + NavMeshVoxelQuadMerge
                    + " includeAssets=" + NavMeshVoxelIncludeAssets
                    + " includeDynamic=" + NavMeshVoxelIncludeDynamic
                    + " includeFlora=" + NavMeshVoxelIncludeFlora);
                sb.AppendLine("grid: " + grid.Width + "x" + grid.Height + " spacing=" + F(grid.Spacing)
                    + "m stride=" + NavMeshSampleStride + " fallback=" + grid.UsedPointFallback
                    + " heights=" + grid.Heights.Length);
                sb.AppendLine("assets: entities=" + records.Count + " included=" + assets.EntitiesIncluded
                    + (assets.LadderIncluded > 0 ? " laddersIncluded=" + assets.LadderIncluded : "")
                    + " tris=" + assets.TriangleCount + " cappedShapes=" + assets.ShapesCapped
                    + " scanMs=" + assets.ScanMs);
                sb.AppendLine("excluded: [flora=" + assets.ExFlora + " barrier=" + assets.ExBarrier
                    + " terrain=" + assets.ExTerrain + " ladder=" + assets.ExLadder
                    + " dynamic=" + assets.ExDynamic + " noShape=" + assets.ExNoShape
                    + " empty=" + assets.ExEmpty + " exceptions=" + assets.ExExceptions
                    + " unsolid=" + assets.ExUnsolid + "]"
                    + " entitiesSeen=" + assets.Count + " physics=" + assets.Physics
                    + (assets.LadderIncluded > 0 ? " laddersIncluded=" + assets.LadderIncluded : ""));
                sb.AppendLine("largest:");
                var order = new System.Collections.Generic.List<int>();
                for (int i = 0; i < assets.LargestTris.Count; i++) order.Add(i);
                order.Sort(delegate (int a, int b) { return assets.LargestTris[b].CompareTo(assets.LargestTris[a]); });
                for (int i = 0; i < order.Count; i++)
                    sb.AppendLine("  '" + assets.LargestNames[order[i]] + "'="
                        + assets.LargestTris[order[i]].ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("assert-points:");
                for (int i = 0; i < points.Count; i++)
                    sb.AppendLine("  " + points[i]);
                string txtPath = System.IO.Path.Combine(folder, prefabName + "_snapshot.txt");
                SceneSnapshot.WriteText(txtPath, sb.ToString());

                watch.Stop();
                log("snapshot: terrain=" + terrainPath + " (" + td.Width + "x" + td.Height
                    + " spacing=" + F(grid.Spacing) + "m stride=" + td.Stride + " heights=" + td.Heights.Length + ")");
                log("snapshot: assets=" + assetsPath + " (entities=" + records.Count
                    + " tris=" + assets.TriangleCount + " cappedShapes=" + assets.ShapesCapped + ")");
                log("snapshot: txt=" + txtPath + " (largest=" + order.Count
                    + " assertPoints=" + points.Count + ")");
                log("snapshot: wrote 3 files in " + watch.ElapsedMilliseconds + "ms"
                    + " terrainMs=" + terrainWatch.ElapsedMilliseconds
                    + " assetsMs=" + assetsWatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                try { NavMeshLog.Write("ERROR: " + ex.GetType().Name + ": " + Flat(ex.Message)); }
                catch (Exception) { }
            }
        }

        // The ramp synthesizer needs the agent climb the build will actually use (0 = auto), because
        // Recast's ledge filter caps the steepest navigable surface at atan(climb / (2 * cellSize)).
        private static float ResolvedRampClimb(float climbSetting, float cellSize, float maxSlopeDegrees)
        {
            if (climbSetting > 0f) return climbSetting;
            return TiledNavMeshBuilder.ResolveAgentClimbMeters(cellSize,
                new TiledNavMeshBuilder.TiledNavMeshSettings().CellHeightMeters, maxSlopeDegrees, 0.4f);
        }

        private void ExportFromEditor()
        {
            var settings = new TerrainNavMeshBuilder.Settings();
            settings.SampleStride = NavMeshSampleStride;
            settings.MaxErrorMeters = NavMeshMaxErrorMeters;
            settings.CoarsestSpacingMeters = NavMeshCoarsestSpacing;
            settings.MaxSlopeDegrees = NavMeshMaxSlopeDegrees;
            settings.CullBelowHeight = NavMeshCullBelowHeight;
            settings.MinHeight = NavMeshMinHeight;
            settings.HeightOffsetMeters = NavMeshHeightOffsetMeters;
            settings.WriteObj = NavMeshWriteObj;

            if (NavMeshAsyncBuild && NavMeshUseVoxelCore)
            {
                ExportVoxelAsyncFromEditor(settings);
                return;
            }

            RunPipeline(Scene, "editor-button", settings, NavMeshPrefabName, NavMeshExportFolder,
                NavMeshClearExistingFaces, NavMeshImportIntoScene,
                NavMeshWriteLogFile, NavMeshLogFolder,
                NavMeshUseVoxelCore, NavMeshVoxelCellSize, NavMeshVoxelAgentClimbMeters,
                NavMeshVoxelDetailMesh, NavMeshVoxelDetailSampleDist, NavMeshVoxelContourMaxError,
                NavMeshVoxelIncludeAssets, NavMeshVoxelIncludeDynamic, NavMeshVoxelIncludeFlora,
                NavMeshVoxelAssetTrace, NavMeshVoxelAgentRadius, NavMeshVoxelLedgeFilter,
                NavMeshVoxelWriteSoupObj, NavMeshVoxelTerrainHoleAudit, NavMeshVoxelGapFillCells,
                NavMeshVoxelStitchMaxGapCells, NavMeshVoxelPruneIsolatedIslands,
                NavMeshVoxelPruneMaxIslandFaces, NavMeshVoxelSteppedProps, NavMeshVoxelQuadMerge);
        }

        // Shared by the editor button and the in-game mission logic. Every message
        // is one atomic single line (to the log file and the console); never throws
        // into the caller.
        //
        // v1 path (in-game hotkey and any caller that does not opt in).
        public static TerrainNavMeshGenerator.ExportResult RunPipeline(
            Scene scene,
            string trigger,
            TerrainNavMeshBuilder.Settings settings,
            string prefabNameOverride,
            string folderOverride,
            bool clearExistingFaces,
            bool importIntoScene,
            bool writeLogFile,
            string logFolder)
        {
            return RunPipeline(scene, trigger, settings, prefabNameOverride, folderOverride,
                clearExistingFaces, importIntoScene, writeLogFile, logFolder, false, 0.5f, 0f, true, 2.0f, 1.3f,
                true, true, false, false, 0.25f, true, false, false, 2, 4, true, 400, false, true);
        }

        // Full signature: useVoxelCore selects the v2 tiled voxel pipeline.
        public static TerrainNavMeshGenerator.ExportResult RunPipeline(
            Scene scene,
            string trigger,
            TerrainNavMeshBuilder.Settings settings,
            string prefabNameOverride,
            string folderOverride,
            bool clearExistingFaces,
            bool importIntoScene,
            bool writeLogFile,
            string logFolder,
            bool useVoxelCore,
            float voxelCellSizeMeters,
            float voxelAgentClimbMeters,
            bool voxelDetailMesh,
            float voxelDetailSampleDist,
            float voxelContourMaxError,
            bool voxelIncludeAssets,
            bool voxelIncludeDynamic,
            bool voxelIncludeFlora,
            bool voxelAssetTrace,
            float voxelAgentRadius,
            bool voxelLedgeFilter,
            bool voxelWriteSoupObj,
            bool voxelTerrainHoleAudit,
            int voxelGapFillCells,
            int voxelStitchMaxGapCells,
            bool voxelPruneIsolatedIslands,
            int voxelPruneMaxIslandFaces,
            bool voxelSteppedProps,
            bool voxelQuadMerge)
        {
            var runWatch = System.Diagnostics.Stopwatch.StartNew();
            NavMeshLog.Initialize(writeLogFile, logFolder);
            System.Action<string> log = NavMeshLog.Write;

            try
            {
                if (scene == null)
                {
                    log("ERROR: no scene.");
                    return null;
                }

                log("run: build=" + BuildTag + " trigger=" + trigger + " scene=" + SafeName(scene)
                    + " machine=" + Environment.MachineName
                    + " time=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    + " logFile=" + (NavMeshLog.CurrentFilePath ?? "(disabled)"));
                log("settings: stride=" + settings.SampleStride
                    + " maxSlope=" + F(settings.MaxSlopeDegrees) + "deg"
                    + " maxError=" + F(settings.MaxErrorMeters) + "m"
                    + " coarsest=" + F(settings.CoarsestSpacingMeters) + "m"
                    + " cullBelowHeight=" + settings.CullBelowHeight
                    + " minHeight=" + F(settings.MinHeight)
                    + " heightOffset=" + F(settings.HeightOffsetMeters) + "m"
                    + " prefabName='" + (prefabNameOverride ?? "") + "'"
                    + " exportFolder='" + (folderOverride ?? "") + "'"
                    + " clearExisting=" + clearExistingFaces
                    + " importIntoScene=" + importIntoScene
                    + " writeObj=" + settings.WriteObj
                    + " useVoxelCore=" + useVoxelCore
                    + " voxelCellSize=" + F(voxelCellSizeMeters) + "m"
                    + " voxelAgentClimb=" + F(voxelAgentClimbMeters) + "m"
                    + " voxelAgentRadius=" + F(voxelAgentRadius) + "m"
                    + " voxelLedgeFilter=" + voxelLedgeFilter
                    + " voxelGapFillCells=" + voxelGapFillCells
                    + " voxelStitchMaxGapCells=" + voxelStitchMaxGapCells
                    + " voxelPrune=" + (voxelPruneIsolatedIslands ? voxelPruneMaxIslandFaces.ToString(CultureInfo.InvariantCulture) : "off")
                    + " steppedProps=" + voxelSteppedProps
                    + " quadMerge=" + voxelQuadMerge
                    + " detail=" + voxelDetailMesh
                    + " voxelDetailSampleDist=" + F(voxelDetailSampleDist) + "m"
                    + " voxelContourMaxError=" + F(voxelContourMaxError) + "vox"
                    + " includeAssets=" + voxelIncludeAssets
                    + " includeDynamic=" + voxelIncludeDynamic
                    + " includeFlora=" + voxelIncludeFlora
                    + " assetTrace=" + voxelAssetTrace
                    + " writeSoupObj=" + voxelWriteSoupObj
                    + " terrainHoleAudit=" + voxelTerrainHoleAudit
                    + " writeLogFile=" + writeLogFile
                    + " logFolder='" + (logFolder ?? "") + "'");

                int facesBeforeRun = SafeFaceCount(scene);
                log("faces before run: " + (facesBeforeRun < 0 ? "unavailable" : facesBeforeRun.ToString()));

                // Reads the scene's live in-memory terrain (including unsaved edits);
                // the sampler's per-node min/max + GetTerrainHeight cross-check lines
                // are what prove the bulk grid matches the live surface.
                SceneTerrainSampler.TerrainGrid grid = SceneTerrainSampler.Read(scene, voxelTerrainHoleAudit, log);
                log("grid ready: " + grid.Width + "x" + grid.Height + " spacing=" + F(grid.Spacing)
                    + "m fallback=" + grid.UsedPointFallback
                    + " liveTerrainValidation=" + (grid.UsedPointFallback ? "point-sampled" : "bulk+cross-checked"));

                TerrainNavMeshGenerator.ExportResult result;
                long assetsMs = 0;
                if (useVoxelCore)
                {
                    // v2 tiled voxel pipeline. The write-bin + import + verify
                    // half is the shared, unchanged WriteAndImport path.
                    var voxel = new TiledNavMeshBuilder.TiledNavMeshSettings();
                    voxel.TileWorldSizeMeters = settings.CoarsestSpacingMeters;
                    voxel.VoxelCellSizeMeters = voxelCellSizeMeters;
                    voxel.MaxSlopeDegrees = settings.MaxSlopeDegrees;
                    voxel.MaxErrorMeters = settings.MaxErrorMeters;
                    voxel.HeightOffsetMeters = settings.HeightOffsetMeters;
                    voxel.UseDetailMesh = voxelDetailMesh;
                    voxel.DetailSampleDistMeters = voxelDetailSampleDist;
                    voxel.ContourMaxErrorVoxels = voxelContourMaxError;
                    voxel.AgentRadiusMeters = voxelAgentRadius;
                    voxel.UseLedgeFilter = voxelLedgeFilter;
                    voxel.GapFillCells = voxelGapFillCells;
                    voxel.StitchMaxGapCells = voxelStitchMaxGapCells;
                    voxel.PruneIsolatedIslands = voxelPruneIsolatedIslands;
                    voxel.PruneMaxIslandFaces = voxelPruneMaxIslandFaces;
                    voxel.QuadMerge = voxelQuadMerge;

                    float[] assetVerts = null;
                    int[] assetTris = null;
                    bool[] assetForceWalkable = null;
                    bool[] assetLedgeExempt = null;
                    if (voxelIncludeAssets)
                    {
                        SceneCollisionGeometry.AssetTriangles assets =
                            SceneCollisionGeometry.Collect(scene, voxelIncludeDynamic, voxelIncludeFlora,
                                voxelAssetTrace, voxelSteppedProps, log, voxelCellSizeMeters,
                                ResolvedRampClimb(voxelAgentClimbMeters, voxelCellSizeMeters, voxel.MaxSlopeDegrees), voxelLedgeFilter);
                        assetVerts = assets.VertsXyz;
                        assetTris = assets.Tris;
                        assetForceWalkable = assets.ForceWalkableTris;
                        assetLedgeExempt = assets.LedgeExemptTris;
                        assetsMs = assets.ScanMs;
                    }

                    VoxelNavMeshGenerator.SoupCapture soupCapture =
                        voxelWriteSoupObj ? new VoxelNavMeshGenerator.SoupCapture() : null;

                    long voxelBuildMs;
                    TerrainNavMeshBuilder.Result voxelMesh = VoxelNavMeshGenerator.BuildExportMesh(
                        grid.Heights, grid.Width, grid.Height, grid.Spacing, settings.SampleStride,
                        voxel, settings.WriteObj, voxelAgentClimbMeters, Environment.ProcessorCount,
                        assetVerts, assetTris, assetForceWalkable, assetLedgeExempt, grid.HoleMask,
                        soupCapture, log, out voxelBuildMs);
                    if (voxelMesh == null)
                    {
                        log("v2: mesh build aborted by the invariant gate; bin NOT written and import NOT attempted.");
                        log("done: totalMs=" + runWatch.ElapsedMilliseconds + " failed=true aborted=true");
                        return null;
                    }

                    result = TerrainNavMeshGenerator.WriteAndImport(scene, voxelMesh, settings.WriteObj,
                        prefabNameOverride, folderOverride, clearExistingFaces, importIntoScene, log,
                        voxelBuildMs);
                    WriteSoupObjs(result, soupCapture, log);
                }
                else
                {
                    result = TerrainNavMeshGenerator.ExportAndImport(
                        scene,
                        grid.Heights,
                        grid.Width,
                        grid.Height,
                        grid.Spacing,
                        settings,
                        prefabNameOverride,
                        folderOverride,
                        clearExistingFaces,
                        importIntoScene,
                        log);
                }

                int facesAfterRun = SafeFaceCount(scene);

                log(result.FaceCount + " faces, " + result.VertexCount + " vertices, "
                    + result.EdgeCount + " edges (culled: " + result.CulledBySlope + " slope, "
                    + result.CulledByHeight + " height)");
                log("bin written: " + result.BinPath);

                if (!string.IsNullOrEmpty(result.ObjPath))
                    log("OBJ written: " + result.ObjPath);

                if (result.ImportAttempted)
                {
                    log("faces imported into scene as '" + result.PrefabName + "' (static).");
                    LogImportCheck(log, facesBeforeRun, facesAfterRun, clearExistingFaces);
                }
                else
                {
                    log("import skipped; import '" + result.PrefabName
                        + "' manually from the editor's navmesh prefab panel.");
                }

                log("done: totalMs=" + runWatch.ElapsedMilliseconds
                    + " assetsMs=" + assetsMs
                    + " bin=" + result.BinByteCount
                    + " V=" + result.VertexCount
                    + " E=" + result.EdgeCount
                    + " F=" + result.FaceCount);
                return result;
            }
            catch (Exception ex)
            {
                log("ERROR: " + ex.GetType().Name + ": " + Flat(ex.Message));
                log("ERROR stack: " + Flat(ex.StackTrace));
                log("done: totalMs=" + runWatch.ElapsedMilliseconds + " failed=true");
                return null;
            }
        }

        // ------------------------------------------------------------------
        // Async v2 build (editor). Everything that touches the scene runs on the
        // main thread; only VoxelNavMeshGenerator.BuildExportMesh (pure managed:
        // terrain grid -> Recast -> NMG8 bytes) runs on a background thread. The
        // finished mesh is picked up by OnEditorTick, which then runs the unchanged
        // WriteAndImport half on the main thread.
        // ------------------------------------------------------------------
        private void ExportVoxelAsyncFromEditor(TerrainNavMeshBuilder.Settings settings)
        {
            var runWatch = System.Diagnostics.Stopwatch.StartNew();
            NavMeshLog.Initialize(NavMeshWriteLogFile, NavMeshLogFolder);
            System.Action<string> log = NavMeshLog.Write;

            if (_asyncBuild != null)
            {
                log("v2: build already running - ignoring");
                return;
            }

            try
            {
                Scene scene = Scene;
                if (scene == null)
                {
                    log("ERROR: no scene.");
                    return;
                }

                log("run: build=" + BuildTag + " trigger=editor-button-async scene=" + SafeName(scene)
                    + " machine=" + Environment.MachineName
                    + " time=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    + " logFile=" + (NavMeshLog.CurrentFilePath ?? "(disabled)"));
                log("settings: stride=" + settings.SampleStride
                    + " maxSlope=" + F(settings.MaxSlopeDegrees) + "deg"
                    + " maxError=" + F(settings.MaxErrorMeters) + "m"
                    + " coarsest=" + F(settings.CoarsestSpacingMeters) + "m"
                    + " cullBelowHeight=" + settings.CullBelowHeight
                    + " minHeight=" + F(settings.MinHeight)
                    + " heightOffset=" + F(settings.HeightOffsetMeters) + "m"
                    + " prefabName='" + (NavMeshPrefabName ?? "") + "'"
                    + " exportFolder='" + (NavMeshExportFolder ?? "") + "'"
                    + " clearExisting=" + NavMeshClearExistingFaces
                    + " importIntoScene=" + NavMeshImportIntoScene
                    + " writeObj=" + settings.WriteObj
                    + " useVoxelCore=True"
                    + " voxelCellSize=" + F(NavMeshVoxelCellSize) + "m"
                    + " voxelAgentClimb=" + F(NavMeshVoxelAgentClimbMeters) + "m"
                    + " voxelAgentRadius=" + F(NavMeshVoxelAgentRadius) + "m"
                    + " voxelLedgeFilter=" + NavMeshVoxelLedgeFilter
                    + " voxelGapFillCells=" + NavMeshVoxelGapFillCells
                    + " voxelStitchMaxGapCells=" + NavMeshVoxelStitchMaxGapCells
                    + " voxelPrune=" + (NavMeshVoxelPruneIsolatedIslands ? NavMeshVoxelPruneMaxIslandFaces.ToString(CultureInfo.InvariantCulture) : "off")
                    + " steppedProps=" + NavMeshVoxelSteppedProps
                    + " detail=" + NavMeshVoxelDetailMesh
                    + " voxelDetailSampleDist=" + F(NavMeshVoxelDetailSampleDist) + "m"
                    + " voxelContourMaxError=" + F(NavMeshVoxelContourMaxError) + "vox"
                    + " includeAssets=" + NavMeshVoxelIncludeAssets
                    + " includeDynamic=" + NavMeshVoxelIncludeDynamic
                    + " includeFlora=" + NavMeshVoxelIncludeFlora
                    + " assetTrace=" + NavMeshVoxelAssetTrace
                    + " writeSoupObj=" + NavMeshVoxelWriteSoupObj
                    + " terrainHoleAudit=" + NavMeshVoxelTerrainHoleAudit
                    + " asyncBuild=True"
                    + " writeLogFile=" + NavMeshWriteLogFile
                    + " logFolder='" + (NavMeshLogFolder ?? "") + "'");

                int facesBeforeRun = SafeFaceCount(scene);
                log("faces before run: " + (facesBeforeRun < 0 ? "unavailable" : facesBeforeRun.ToString()));

                // Scene reads stay on the main thread.
                var sampleWatch = System.Diagnostics.Stopwatch.StartNew();
                SceneTerrainSampler.TerrainGrid grid =
                    SceneTerrainSampler.Read(scene, NavMeshVoxelTerrainHoleAudit, log);
                sampleWatch.Stop();
                log("grid ready: " + grid.Width + "x" + grid.Height + " spacing=" + F(grid.Spacing)
                    + "m fallback=" + grid.UsedPointFallback
                    + " liveTerrainValidation=" + (grid.UsedPointFallback ? "point-sampled" : "bulk+cross-checked"));

                // Phase 3: asset collision geometry is read on the main thread (scene access) and
                // carried as plain arrays to the background build.
                long assetsMs = 0;
                float[] assetVerts = null;
                int[] assetTris = null;
                bool[] assetForceWalkable = null;
                    bool[] assetLedgeExempt = null;
                if (NavMeshVoxelIncludeAssets)
                {
                    var assetsWatch = System.Diagnostics.Stopwatch.StartNew();
                    SceneCollisionGeometry.AssetTriangles assets =
                        SceneCollisionGeometry.Collect(scene, NavMeshVoxelIncludeDynamic, NavMeshVoxelIncludeFlora,
                            NavMeshVoxelAssetTrace, NavMeshVoxelSteppedProps, log, NavMeshVoxelCellSize,
                            ResolvedRampClimb(NavMeshVoxelAgentClimbMeters, NavMeshVoxelCellSize, settings.MaxSlopeDegrees), NavMeshVoxelLedgeFilter);
                    assetsWatch.Stop();
                    assetVerts = assets.VertsXyz;
                    assetTris = assets.Tris;
                    assetForceWalkable = assets.ForceWalkableTris;
                    assetLedgeExempt = assets.LedgeExemptTris;
                    assetsMs = assetsWatch.ElapsedMilliseconds;
                }
                else
                {
                    log("assets: collection disabled (NavMeshVoxelIncludeAssets=false).");
                }

                var voxel = new TiledNavMeshBuilder.TiledNavMeshSettings();
                voxel.TileWorldSizeMeters = settings.CoarsestSpacingMeters;
                voxel.VoxelCellSizeMeters = NavMeshVoxelCellSize;
                voxel.MaxSlopeDegrees = settings.MaxSlopeDegrees;
                voxel.MaxErrorMeters = settings.MaxErrorMeters;
                voxel.HeightOffsetMeters = settings.HeightOffsetMeters;
                voxel.UseDetailMesh = NavMeshVoxelDetailMesh;
                voxel.DetailSampleDistMeters = NavMeshVoxelDetailSampleDist;
                voxel.ContourMaxErrorVoxels = NavMeshVoxelContourMaxError;
                voxel.AgentRadiusMeters = NavMeshVoxelAgentRadius;
                voxel.UseLedgeFilter = NavMeshVoxelLedgeFilter;
                voxel.GapFillCells = NavMeshVoxelGapFillCells;
                voxel.StitchMaxGapCells = NavMeshVoxelStitchMaxGapCells;
                voxel.PruneIsolatedIslands = NavMeshVoxelPruneIsolatedIslands;
                voxel.PruneMaxIslandFaces = NavMeshVoxelPruneMaxIslandFaces;
                voxel.CleanupSlivers = NavMeshVoxelCleanup;
                voxel.QuadMerge = NavMeshVoxelQuadMerge;

                var req = new AsyncVoxelBuild();
                req.Scene = scene;
                req.GridHeights = grid.Heights;
                req.GridWidth = grid.Width;
                req.GridHeight = grid.Height;
                req.GridSpacing = grid.Spacing;
                req.Settings = settings;
                req.Voxel = voxel;
                req.AgentClimbMeters = NavMeshVoxelAgentClimbMeters;
                req.AssetVertsEngineXyz = assetVerts;
                req.AssetTris = assetTris;
                req.AssetForceWalkable = assetForceWalkable;
                req.AssetLedgeExempt = assetLedgeExempt;
                req.TerrainHoleMask = grid.HoleMask;
                req.SoupCapture = NavMeshVoxelWriteSoupObj ? new VoxelNavMeshGenerator.SoupCapture() : null;
                req.AssetsMs = assetsMs;
                req.FacesBefore = facesBeforeRun;
                req.SampleMs = sampleWatch.ElapsedMilliseconds;
                req.PrefabNameOverride = NavMeshPrefabName;
                req.FolderOverride = NavMeshExportFolder;
                req.ClearExisting = NavMeshClearExistingFaces;
                req.ImportIntoScene = NavMeshImportIntoScene;
                req.Log = log;
                req.RunWatch = runWatch;

                _asyncBuild = req;
                BeginBuildTickSampling();

                log("v2: async build started on a background thread; the editor stays responsive (progress lines follow).");

                var worker = new System.Threading.Thread(RunAsyncBuild);
                worker.IsBackground = true;
                worker.Name = "BetterBannerlordNavmeshing-v2build";
                worker.Start(req);
            }
            catch (Exception ex)
            {
                _asyncBuild = null;
                _periodActive = false;
                log("ERROR: " + ex.GetType().Name + ": " + Flat(ex.Message));
                log("ERROR stack: " + Flat(ex.StackTrace));
                log("done: totalMs=" + runWatch.ElapsedMilliseconds + " failed=true");
            }
        }

        // Background entry point. Catches everything so the worker can never tear the
        // editor down; the outcome is published via the volatile Done flag.
        private static void RunAsyncBuild(object state)
        {
            var req = (AsyncVoxelBuild)state;
            try
            {
                long buildMs;
                req.Mesh = VoxelNavMeshGenerator.BuildExportMesh(
                    req.GridHeights, req.GridWidth, req.GridHeight, req.GridSpacing, req.Settings.SampleStride,
                    req.Voxel, req.Settings.WriteObj, req.AgentClimbMeters, Environment.ProcessorCount,
                    req.AssetVertsEngineXyz, req.AssetTris, req.AssetForceWalkable,
                    req.AssetLedgeExempt, req.TerrainHoleMask,
                    req.SoupCapture, req.Log, out buildMs);
                req.BuildMs = buildMs;
            }
            catch (Exception ex)
            {
                req.Error = ex;
            }
            req.Done = true;
        }

        // Main-thread completion, polled from OnEditorTick. Runs the unchanged
        // WriteAndImport half, adds the per-phase "v2: perf:" line, and starts the
        // post-import tick sampling.
        private void CompleteAsyncBuildIfReady()
        {
            AsyncVoxelBuild req = _asyncBuild;
            if (req == null || !req.Done)
                return;

            _asyncBuild = null; // clear first so a new Export is allowed immediately
            _periodActive = false;
            System.Action<string> log = req.Log;

            try
            {
                log("v2: async build finished on the background thread.");
                log("v2: perf: ticksDuringBuild=" + _buildTicks + " maxTickMs=" + F1(_buildMaxTickMs));

                if (req.Error != null)
                {
                    log("v2: ERROR (background build): " + req.Error.GetType().Name + ": " + Flat(req.Error.Message));
                    log("v2: ERROR stack (background build): " + Flat(req.Error.StackTrace));
                    log("done: totalMs=" + req.RunWatch.ElapsedMilliseconds + " failed=true");
                    return;
                }

                if (req.Mesh == null)
                {
                    log("v2: mesh build aborted by the invariant gate; bin NOT written and import NOT attempted.");
                    log("done: totalMs=" + req.RunWatch.ElapsedMilliseconds + " failed=true aborted=true");
                    return;
                }

                TerrainNavMeshGenerator.ExportResult result = TerrainNavMeshGenerator.WriteAndImport(
                    req.Scene, req.Mesh, req.Settings.WriteObj, req.PrefabNameOverride, req.FolderOverride,
                    req.ClearExisting, req.ImportIntoScene, log, req.BuildMs);
                WriteSoupObjs(result, req.SoupCapture, log);

                log("v2: perf: clickToDoneMs=" + req.RunWatch.ElapsedMilliseconds
                    + " sampleMs=" + req.SampleMs
                    + " assetsMs=" + req.AssetsMs
                    + " buildMs=" + req.BuildMs
                    + " writeMs=" + result.WriteMs
                    + " importMs=" + result.ImportMs);

                int facesAfterRun = SafeFaceCount(req.Scene);
                log(result.FaceCount + " faces, " + result.VertexCount + " vertices, "
                    + result.EdgeCount + " edges (culled: " + result.CulledBySlope + " slope, "
                    + result.CulledByHeight + " height)");
                log("bin written: " + result.BinPath);

                if (!string.IsNullOrEmpty(result.ObjPath))
                    log("OBJ written: " + result.ObjPath);

                if (result.ImportAttempted)
                {
                    log("faces imported into scene as '" + result.PrefabName + "' (static).");
                    LogImportCheck(log, req.FacesBefore, facesAfterRun, req.ClearExisting);
                }
                else
                {
                    log("import skipped; import '" + result.PrefabName
                        + "' manually from the editor's navmesh prefab panel.");
                }

                log("done: totalMs=" + req.RunWatch.ElapsedMilliseconds
                    + " bin=" + result.BinByteCount
                    + " V=" + result.VertexCount
                    + " E=" + result.EdgeCount
                    + " F=" + result.FaceCount);

                if (result.ImportAttempted)
                    BeginPostImportTickSampling();
            }
            catch (Exception ex)
            {
                _periodActive = false;
                log("ERROR: " + ex.GetType().Name + ": " + Flat(ex.Message));
                log("ERROR stack: " + Flat(ex.StackTrace));
                log("done: totalMs=" + req.RunWatch.ElapsedMilliseconds + " failed=true");
            }
        }

        // Editor-tick sampler. Wall-clock delta between OnEditorTick calls is the real
        // editor frame time (the engine's dt is not relied on). During a build it feeds
        // ticksDuringBuild/maxTickMs; after an import it feeds the postImportTickMs window.
        private void TrackEditorTick()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!_periodActive)
            {
                _lastTickTimestamp = now;
                return;
            }

            double ms = (now - _lastTickTimestamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _lastTickTimestamp = now;

            if (_periodIsBuild)
            {
                _buildTicks++;
                if (ms > _buildMaxTickMs)
                    _buildMaxTickMs = ms;
                return;
            }

            if (_postImportTicksRemaining > 0)
            {
                _postImportTicks++;
                _postImportSumMs += ms;
                if (ms < _postImportMinMs) _postImportMinMs = ms;
                if (ms > _postImportMaxMs) _postImportMaxMs = ms;

                _postImportTicksRemaining--;
                if (_postImportTicksRemaining == 0)
                {
                    NavMeshLog.Write("v2: perf: postImportTickMs min=" + F1(_postImportMinMs)
                        + " avg=" + F1(_postImportTicks > 0 ? _postImportSumMs / _postImportTicks : 0.0)
                        + " max=" + F1(_postImportMaxMs)
                        + " over " + _postImportTicks + " ticks");
                    _periodActive = false;
                }
            }
        }

        private void BeginBuildTickSampling()
        {
            _periodActive = true;
            _periodIsBuild = true;
            _buildTicks = 0;
            _buildMaxTickMs = 0.0;
            _lastTickTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        private void BeginPostImportTickSampling()
        {
            _periodActive = true;
            _periodIsBuild = false;
            _postImportTicksRemaining = 30;
            _postImportTicks = 0;
            _postImportMinMs = double.MaxValue;
            _postImportMaxMs = 0.0;
            _postImportSumMs = 0.0;
            _lastTickTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        private static int SafeFaceCount(Scene scene)
        {
            try
            {
                return scene.GetNavMeshFaceCount();
            }
            catch (Exception)
            {
                return -1;
            }
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

        private static string F(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        // NavMeshVoxelWriteSoupObj: dump the raw input soup (engine space) next to the bin so a failing
        // spot can be inspected with geometry provenance. Uses the resolved bin path + prefab name.
        private static void WriteSoupObjs(TerrainNavMeshGenerator.ExportResult result,
            VoxelNavMeshGenerator.SoupCapture cap, System.Action<string> log)
        {
            if (cap == null || result == null || string.IsNullOrEmpty(result.BinPath))
                return;
            try
            {
                string dir = System.IO.Path.GetDirectoryName(result.BinPath);
                string name = result.PrefabName;
                string terrainPath = System.IO.Path.Combine(dir, name + "_soup_terrain.obj");
                string assetsPath = System.IO.Path.Combine(dir, name + "_soup_assets.obj");
                WriteObjEngine(terrainPath, cap.TerrainVertsEngine, cap.TerrainTris);
                WriteObjEngine(assetsPath, cap.AssetVertsEngine, cap.AssetTris);
                log("soup OBJ written: " + terrainPath);
                log("soup OBJ written: " + assetsPath);
            }
            catch (Exception ex)
            {
                log("soup OBJ ERROR: " + ex.GetType().Name + ": " + Flat(ex.Message));
            }
        }

        private static void WriteObjEngine(string path, float[] verts, int[] tris)
        {
            var ic = CultureInfo.InvariantCulture;
            int vc = verts != null ? verts.Length / 3 : 0;
            int fc = tris != null ? tris.Length / 3 : 0;
            var sb = new System.Text.StringBuilder();
            sb.Append("# engine-space soup (v2 voxel core input): vertices ").Append(vc.ToString(ic))
              .Append(" faces ").Append(fc.ToString(ic)).AppendLine();
            for (int i = 0; i < vc; i++)
            {
                sb.Append("v ")
                  .Append(verts[i * 3 + 0].ToString("R", ic)).Append(' ')
                  .Append(verts[i * 3 + 1].ToString("R", ic)).Append(' ')
                  .Append(verts[i * 3 + 2].ToString("R", ic)).AppendLine();
            }
            for (int f = 0; f < fc; f++)
            {
                sb.Append("f ")
                  .Append((tris[f * 3 + 0] + 1).ToString(ic)).Append(' ')
                  .Append((tris[f * 3 + 1] + 1).ToString(ic)).Append(' ')
                  .Append((tris[f * 3 + 2] + 1).ToString(ic)).AppendLine();
            }
            System.IO.File.WriteAllText(path, sb.ToString(), System.Text.Encoding.UTF8);
        }

        private static string F1(double value)
        {
            return value.ToString("0.0", CultureInfo.InvariantCulture);
        }

        private static string Flat(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            return value.Replace("\r\n", " | ").Replace("\r", " | ").Replace("\n", " | ");
        }
    }
}
