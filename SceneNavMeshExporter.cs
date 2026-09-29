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
        public float NavMeshVoxelCellSize = 0.5f;

        // Agent max climb (m) for the v2 voxel core. 0 = auto-derive so that
        // NavMeshMaxSlopeDegrees is actually traversable (Recast's ledge filter
        // needs walkableClimb >= 2*cs*tan(slope)/ch). See the README.
        public float NavMeshVoxelAgentClimbMeters = 0f;

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
                NavMeshVoxelDetailMesh, NavMeshVoxelDetailSampleDist, NavMeshVoxelContourMaxError);
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
                clearExistingFaces, importIntoScene, writeLogFile, logFolder, false, 0.5f, 0f, true, 2.0f, 1.3f);
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
            float voxelContourMaxError)
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
                    + " detail=" + voxelDetailMesh
                    + " voxelDetailSampleDist=" + F(voxelDetailSampleDist) + "m"
                    + " voxelContourMaxError=" + F(voxelContourMaxError) + "vox"
                    + " writeLogFile=" + writeLogFile
                    + " logFolder='" + (logFolder ?? "") + "'");

                int facesBeforeRun = SafeFaceCount(scene);
                log("faces before run: " + (facesBeforeRun < 0 ? "unavailable" : facesBeforeRun.ToString()));

                // Reads the scene's live in-memory terrain (including unsaved edits);
                // the sampler's per-node min/max + GetTerrainHeight cross-check lines
                // are what prove the bulk grid matches the live surface.
                SceneTerrainSampler.TerrainGrid grid = SceneTerrainSampler.Read(scene, log);
                log("grid ready: " + grid.Width + "x" + grid.Height + " spacing=" + F(grid.Spacing)
                    + "m fallback=" + grid.UsedPointFallback
                    + " liveTerrainValidation=" + (grid.UsedPointFallback ? "point-sampled" : "bulk+cross-checked"));

                TerrainNavMeshGenerator.ExportResult result;
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

                    long voxelBuildMs;
                    TerrainNavMeshBuilder.Result voxelMesh = VoxelNavMeshGenerator.BuildExportMesh(
                        grid.Heights, grid.Width, grid.Height, grid.Spacing, settings.SampleStride,
                        voxel, settings.WriteObj, voxelAgentClimbMeters, Environment.ProcessorCount, log,
                        out voxelBuildMs);

                    if (voxelMesh == null)
                    {
                        log("v2: mesh build aborted by the invariant gate; bin NOT written and import NOT attempted.");
                        log("done: totalMs=" + runWatch.ElapsedMilliseconds + " failed=true aborted=true");
                        return null;
                    }

                    result = TerrainNavMeshGenerator.WriteAndImport(scene, voxelMesh, settings.WriteObj,
                        prefabNameOverride, folderOverride, clearExistingFaces, importIntoScene, log,
                        voxelBuildMs);
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
                    + " detail=" + NavMeshVoxelDetailMesh
                    + " voxelDetailSampleDist=" + F(NavMeshVoxelDetailSampleDist) + "m"
                    + " voxelContourMaxError=" + F(NavMeshVoxelContourMaxError) + "vox"
                    + " asyncBuild=True"
                    + " writeLogFile=" + NavMeshWriteLogFile
                    + " logFolder='" + (NavMeshLogFolder ?? "") + "'");

                int facesBeforeRun = SafeFaceCount(scene);
                log("faces before run: " + (facesBeforeRun < 0 ? "unavailable" : facesBeforeRun.ToString()));

                // Scene reads stay on the main thread.
                var sampleWatch = System.Diagnostics.Stopwatch.StartNew();
                SceneTerrainSampler.TerrainGrid grid = SceneTerrainSampler.Read(scene, log);
                sampleWatch.Stop();
                log("grid ready: " + grid.Width + "x" + grid.Height + " spacing=" + F(grid.Spacing)
                    + "m fallback=" + grid.UsedPointFallback
                    + " liveTerrainValidation=" + (grid.UsedPointFallback ? "point-sampled" : "bulk+cross-checked"));

                var voxel = new TiledNavMeshBuilder.TiledNavMeshSettings();
                voxel.TileWorldSizeMeters = settings.CoarsestSpacingMeters;
                voxel.VoxelCellSizeMeters = NavMeshVoxelCellSize;
                voxel.MaxSlopeDegrees = settings.MaxSlopeDegrees;
                voxel.MaxErrorMeters = settings.MaxErrorMeters;
                voxel.HeightOffsetMeters = settings.HeightOffsetMeters;
                voxel.UseDetailMesh = NavMeshVoxelDetailMesh;
                voxel.DetailSampleDistMeters = NavMeshVoxelDetailSampleDist;
                voxel.ContourMaxErrorVoxels = NavMeshVoxelContourMaxError;

                var req = new AsyncVoxelBuild();
                req.Scene = scene;
                req.GridHeights = grid.Heights;
                req.GridWidth = grid.Width;
                req.GridHeight = grid.Height;
                req.GridSpacing = grid.Spacing;
                req.Settings = settings;
                req.Voxel = voxel;
                req.AgentClimbMeters = NavMeshVoxelAgentClimbMeters;
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
                    req.Log, out buildMs);
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

                log("v2: perf: clickToDoneMs=" + req.RunWatch.ElapsedMilliseconds
                    + " sampleMs=" + req.SampleMs
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
