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
        public int NavMeshSampleStride = 1;
        public float NavMeshMaxErrorMeters = 0.25f;
        public float NavMeshCoarsestSpacing = 16f;
        public float NavMeshMaxSlopeDegrees = 65f;
        public bool NavMeshCullBelowHeight = false;
        public float NavMeshMinHeight = 0f;
        public float NavMeshHeightOffsetMeters = 0.5f;
        public string NavMeshPrefabName = "";
        public string NavMeshExportFolder = "";
        public bool NavMeshClearExistingFaces = true;
        public bool NavMeshImportIntoScene = true;
        public bool NavMeshWriteObj = false;

        // Voxel core (navmesh v2, experimental). When enabled the Export button
        // runs the tiled voxel pipeline (TiledNavMeshBuilder) instead of the v1
        // adaptive terrain mesher. Terrain-only for now.
        public bool NavMeshUseVoxelCore = false;
        public float NavMeshVoxelCellSize = 0.5f;

        // Agent max climb (m) for the v2 voxel core. 0 = auto-derive so that
        // NavMeshMaxSlopeDegrees is actually traversable (Recast's ledge filter
        // needs walkableClimb >= 2*cs*tan(slope)/ch). See the README.
        public float NavMeshVoxelAgentClimbMeters = 0f;

        // Detail mesh for the v2 voxel core (default true). Adds interior vertices so faces follow
        // the terrain surface instead of chording under hills/knolls; bounded by
        // NavMeshMaxErrorMeters. Disable for a leaner bin (flat/plateau scenes only).
        public bool NavMeshVoxelDetailMesh = true;

        // ---------------- Logging ----------------
        // In-editor Console.WriteLine is invisible, so every run is also written
        // to a per-session log file (see NavMeshLog).
        public bool NavMeshWriteLogFile = true;
        public string NavMeshLogFolder = "";

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

        protected override void OnEditorVariableChanged(string variableName)
        {
            base.OnEditorVariableChanged(variableName);
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

            RunPipeline(Scene, "editor-button", settings, NavMeshPrefabName, NavMeshExportFolder,
                NavMeshClearExistingFaces, NavMeshImportIntoScene,
                NavMeshWriteLogFile, NavMeshLogFolder,
                NavMeshUseVoxelCore, NavMeshVoxelCellSize, NavMeshVoxelAgentClimbMeters,
                NavMeshVoxelDetailMesh);
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
                clearExistingFaces, importIntoScene, writeLogFile, logFolder, false, 0.5f, 0f, true);
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
            bool voxelDetailMesh)
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

        private static string Flat(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            return value.Replace("\r\n", " | ").Replace("\r", " | ").Replace("\n", " | ");
        }
    }
}
