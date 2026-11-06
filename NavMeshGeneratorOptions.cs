using System;
using TaleWorlds.Engine;

namespace BetterBannerlordNavmeshing
{
    // Optional companion components for NavMeshGenerator. The editor has no collapsible
    // field groups inside one script component, but each script component is its own node in
    // the editor's script tree, so splitting the options gives the simple view a clean panel.
    //
    // All three components are expected on the SAME entity (the exporter prefab). The main
    // component copies these values into its own fields at export time. When a component is
    // absent - e.g. an older scene that only has NavMeshGenerator - the main component's
    // own serialized values are used unchanged.
    public class NavMeshGenerator_AdvancedOptions : ScriptComponentBehavior
    {
        public int NavMeshSampleStride = NavMeshPipelineDefaults.SampleStride;
        public float NavMeshMaxErrorMeters = NavMeshPipelineDefaults.MaxErrorMeters;
        public float NavMeshCoarsestSpacing = NavMeshPipelineDefaults.CoarsestSpacingMeters;
        public float NavMeshSteepFaceMarginDegrees = NavMeshPipelineDefaults.SteepFaceMarginDegrees;
        public float NavMeshSteepFaceMinHeightClimbs = NavMeshPipelineDefaults.SteepFaceMinHeightClimbs;

        public string NavMeshPrefabName = "";
        public string NavMeshExportFolder = "";
        public bool NavMeshClearExistingFaces = true;
        public bool NavMeshImportIntoScene = true;

        public bool NavMeshUseVoxelCore = NavMeshPipelineDefaults.UseVoxelCore;
        public float NavMeshVoxelCellSize = NavMeshPipelineDefaults.VoxelCellSizeMeters;
        public float NavMeshVoxelAgentClimbMeters = NavMeshPipelineDefaults.VoxelAgentClimbMeters;
        public float NavMeshVoxelAgentRadius = NavMeshPipelineDefaults.VoxelAgentRadiusMeters;
        public bool NavMeshVoxelLedgeFilter = NavMeshPipelineDefaults.VoxelLedgeFilter;
        public int NavMeshVoxelGapFillCells = NavMeshPipelineDefaults.VoxelGapFillCells;
        public int NavMeshVoxelStitchMaxGapCells = NavMeshPipelineDefaults.VoxelStitchMaxGapCells;
        public bool NavMeshVoxelPruneIsolatedIslands = NavMeshPipelineDefaults.VoxelPruneIsolatedIslands;
        public int NavMeshVoxelPruneMaxIslandFaces = NavMeshPipelineDefaults.VoxelPruneMaxIslandFaces;
        public bool NavMeshVoxelCleanup = NavMeshPipelineDefaults.VoxelCleanup;
        public bool NavMeshVoxelQuadMerge = NavMeshPipelineDefaults.VoxelQuadMerge;
        public bool NavMeshVoxelBarriers = NavMeshPipelineDefaults.VoxelBarriers;
        public bool NavMeshVoxelDetailMesh = NavMeshPipelineDefaults.VoxelDetailMesh;
        public float NavMeshVoxelDetailSampleDist = NavMeshPipelineDefaults.VoxelDetailSampleDistMeters;
        public float NavMeshVoxelContourMaxError = NavMeshPipelineDefaults.VoxelContourMaxErrorVoxels;
        public bool NavMeshVoxelIncludeAssets = NavMeshPipelineDefaults.VoxelIncludeAssets;
        public bool NavMeshVoxelIncludeDynamic = NavMeshPipelineDefaults.VoxelIncludeDynamic;
        public bool NavMeshVoxelIncludeFlora = NavMeshPipelineDefaults.VoxelIncludeFlora;
        public bool NavMeshVoxelSteppedProps = NavMeshPipelineDefaults.VoxelSteppedProps;
        public bool NavMeshAsyncBuild = true;

        protected override void OnEditorVariableChanged(string variableName)
        {
            base.OnEditorVariableChanged(variableName);
            NavMeshGenerator main = GameEntity != null
                ? GameEntity.GetFirstScriptOfType<NavMeshGenerator>()
                : null;
            if (main != null)
                main.ApplyOptionsFromComponents();
        }
    }

    public class NavMeshGenerator_DebugOptions : ScriptComponentBehavior
    {
        public SimpleButton ProbeGeometry;
        public SimpleButton DumpSceneSnapshot;

        public bool NavMeshCullBelowHeight = false;
        public float NavMeshMinHeight = 0f;
        public bool NavMeshWriteObj = false;
        public string NavMeshSnapshotAssertPoints = "";

        public bool NavMeshVoxelAssetTrace = NavMeshPipelineDefaults.VoxelAssetTrace;
        public bool NavMeshVoxelWriteSoupObj = NavMeshPipelineDefaults.VoxelWriteSoupObj;
        public bool NavMeshVoxelTerrainHoleAudit = NavMeshPipelineDefaults.VoxelTerrainHoleAudit;

        public bool NavMeshConsoleMessages = NavMeshPipelineDefaults.ConsoleMessages;
        public bool NavMeshWriteLogFile = true;
        public string NavMeshLogFolder = "";

        public bool NavMeshOverlayText = false;
        public float NavMeshOverlayX = 0f;
        public float NavMeshOverlayY = 0f;
        public float NavMeshOverlaySeconds = 5f;

        // Live build status shown in this component's panel row. Updated by the main exporter's
        // editor tick through SetBuildStatus (the main component no longer carries a status field).
        public string NavMeshBuildStatus = NavMeshVisibleLog.IdleStatus;

        protected override void OnEditorVariableChanged(string variableName)
        {
            base.OnEditorVariableChanged(variableName);
            NavMeshGenerator main = GameEntity != null
                ? GameEntity.GetFirstScriptOfType<NavMeshGenerator>()
                : null;
            if (main == null)
                return;

            if (variableName == nameof(ProbeGeometry))
                main.RunProbeFromEditor();
            else if (variableName == nameof(DumpSceneSnapshot))
                main.RunDumpSnapshotFromEditor();
            else
                main.ApplyOptionsFromComponents();
        }

        // Mirrors the status into this component's own panel row and asks the engine to repaint
        // it. Best-effort, exactly like the old main-component path: a missing or older engine
        // method must never break the editor tick.
        public void SetBuildStatus(string status)
        {
            if (string.Equals(NavMeshBuildStatus, status, StringComparison.Ordinal))
                return;

            NavMeshBuildStatus = status;
            try
            {
                ManagedScriptComponent component = ScriptComponent;
                if (component == null)
                    return;

                component.SetVariableEditorWidgetValue(
                    nameof(NavMeshBuildStatus), RglScriptFieldType.RglSftString, 0.0);
            }
            catch (Exception)
            {
            }
        }
    }
}
