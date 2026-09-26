using System;
using System.Globalization;
using TaleWorlds.Engine;

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
        }

        private void ExportFromEditor()
        {
            Scene scene = Scene;
            if (scene == null)
            {
                Console.WriteLine("NavMesh: ERROR: no scene.");
                return;
            }

            var settings = new TerrainNavMeshBuilder.Settings();
            settings.SampleStride = NavMeshSampleStride;
            settings.MaxErrorMeters = NavMeshMaxErrorMeters;
            settings.CoarsestSpacingMeters = NavMeshCoarsestSpacing;
            settings.MaxSlopeDegrees = NavMeshMaxSlopeDegrees;
            settings.CullBelowHeight = NavMeshCullBelowHeight;
            settings.MinHeight = NavMeshMinHeight;
            settings.HeightOffsetMeters = NavMeshHeightOffsetMeters;
            settings.WriteObj = NavMeshWriteObj;

            RunPipeline(scene, "editor-button", settings, NavMeshPrefabName, NavMeshExportFolder,
                NavMeshClearExistingFaces, NavMeshImportIntoScene);
        }

        // Shared by the editor button and the in-game mission logic. Console-only,
        // single-line logging; never throws into the caller.
        public static TerrainNavMeshGenerator.ExportResult RunPipeline(
            Scene scene,
            string trigger,
            TerrainNavMeshBuilder.Settings settings,
            string prefabNameOverride,
            string folderOverride,
            bool clearExistingFaces,
            bool importIntoScene)
        {
            System.Action<string> log = message => Console.WriteLine("NavMesh: " + Flat(message));

            try
            {
                if (scene == null)
                {
                    log("ERROR: no scene.");
                    return null;
                }

                log("run: build=" + BuildTag + " trigger=" + trigger + " scene=" + SafeName(scene));
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
                    + " writeObj=" + settings.WriteObj);

                // Reads the scene's live in-memory terrain (including unsaved edits);
                // the sampler's per-node min/max + GetTerrainHeight cross-check lines
                // are what prove the bulk grid matches the live surface.
                SceneTerrainSampler.TerrainGrid grid = SceneTerrainSampler.Read(scene, log);
                log("grid ready: " + grid.Width + "x" + grid.Height + " spacing=" + F(grid.Spacing)
                    + "m fallback=" + grid.UsedPointFallback
                    + " liveTerrainValidation=" + (grid.UsedPointFallback ? "point-sampled" : "bulk+cross-checked"));

                TerrainNavMeshGenerator.ExportResult result = TerrainNavMeshGenerator.ExportAndImport(
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

                log(result.FaceCount + " faces, " + result.VertexCount + " vertices, "
                    + result.EdgeCount + " edges (culled: " + result.CulledBySlope + " slope, "
                    + result.CulledByHeight + " height)");
                log("bin written: " + result.BinPath);

                if (!string.IsNullOrEmpty(result.ObjPath))
                    log("OBJ written: " + result.ObjPath);

                if (result.ImportAttempted)
                    log("faces imported into scene as '" + result.PrefabName + "' (static).");
                else
                    log("import skipped; import '" + result.PrefabName
                        + "' manually from the editor's navmesh prefab panel.");

                log("done: bin=" + result.BinByteCount + " bytes, V=" + result.VertexCount
                    + " E=" + result.EdgeCount + " F=" + result.FaceCount);
                return result;
            }
            catch (Exception ex)
            {
                log("ERROR: " + ex.GetType().Name + ": " + Flat(ex.Message));
                log("ERROR stack: " + Flat(ex.StackTrace));
                return null;
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
