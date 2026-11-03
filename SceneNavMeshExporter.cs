using System;
using System.Globalization;
using System.Linq;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BetterBannerlordNavmeshing
{
    // The shipped defaults for the pipeline, in ONE place. The exporter fields below, the
    // 9-argument RunPipeline convenience overload (the in-game mission path) and any other
    // caller must all start from these values. Before this class existed the overload carried
    // stale literals (v1 core, contour 1.3, stitch 4, gate connectors off), so the in-game
    // hotkey silently built a different mesh from the editor button.
    internal static class NavMeshPipelineDefaults
    {
        // v1/v2 shared TerrainNavMeshBuilder.Settings
        public const int SampleStride = 2;
        public const float MaxErrorMeters = 0.25f;
        public const float CoarsestSpacingMeters = 10f;
        public const float MaxSlopeDegrees = 50f;
        public const float HeightOffsetMeters = 0.5f;

        // v2 tiled voxel core
        public const bool UseVoxelCore = true;
        public const float VoxelCellSizeMeters = 0.25f;
        public const float VoxelAgentClimbMeters = 0f;
        public const float VoxelAgentRadiusMeters = 0.25f;
        public const bool VoxelLedgeFilter = true;
        public const int VoxelGapFillCells = 2;
        public const int VoxelStitchMaxGapCells = 6;
        public const bool VoxelPruneIsolatedIslands = true;
        public const int VoxelPruneMaxIslandFaces = 400;
        public const bool VoxelCleanup = true;
        public const bool VoxelQuadMerge = true;
        public const bool VoxelGatePlateBlend = true;
        public const bool VoxelBarriers = true;
        public const bool VoxelDetailMesh = true;
        public const float VoxelDetailSampleDistMeters = 2f;
        public const float VoxelContourMaxErrorVoxels = 2f;
        public const bool VoxelIncludeAssets = true;
        public const bool VoxelIncludeDynamic = true;
        public const bool VoxelIncludeFlora = false;
        public const bool VoxelAssetTrace = false;
        public const bool VoxelSteppedProps = true;
        public const bool VoxelGateConnectors = true;
        public const bool VoxelWriteSoupObj = false;
        public const bool VoxelTerrainHoleAudit = false;
        public const bool CullBelowWaterLevel = true;
        // Steep-face repair defaults. The slope threshold is MaxSlopeDegrees + margin (0 = the cap
        // itself); the height allowance is in agent-climb units (1.5 = 1.1 m at climb 0.6 / cell 0.2).
        public const float SteepFaceMarginDegrees = 10f;
        public const float SteepFaceMinHeightClimbs = 1.5f;
        public const bool ConsoleMessages = true;
    }

    // Editor entry point (primary). Attach to any entity in a scene that has
    // terrain; the "Export NavMesh" button reads the scene's live in-memory
    // terrain, builds a navmesh prefab and imports its faces into the scene.
    // Also hosts the shared pipeline used by the in-game mission logic.
    public class SceneNavMeshExporter : ScriptComponentBehavior
    {
        private const string BuildTag = "Build 3 (adaptive navmesh)";

        // Overlay tints, matching Debug.DebugColor's palette (which is what
        // MBDebug.RenderText's `color` argument is documented to take). Only the
        // terminal lines differ; everything else is white.
        private const uint OverlayWhite = 0xFFFFFFFFu;
        private const uint OverlayYellow = 0xFFFFFF00u;
        private const uint OverlayRed = 0xFF4040FFu;

        // ---------------- Settings (same names as the terrain script) ----------------
        public SimpleButton ExportNavMesh;
        public SimpleButton ImportNavMeshOnly;
        public SimpleButton ProbeGeometry;

        // Offline test case: write the export's main-thread inputs (terrain grid + per-entity collision
        // triangles) plus a human-readable header, so the standalone harness can rebuild the exact soup
        // for this scene without the editor. See SceneSnapshot / README.
        public SimpleButton DumpSceneSnapshot;
        public int NavMeshSampleStride = NavMeshPipelineDefaults.SampleStride;
        public float NavMeshMaxErrorMeters = NavMeshPipelineDefaults.MaxErrorMeters;
        public float NavMeshCoarsestSpacing = NavMeshPipelineDefaults.CoarsestSpacingMeters;
        public float NavMeshMaxSlopeDegrees = NavMeshPipelineDefaults.MaxSlopeDegrees;
        // Steep-face repair: threshold = NavMeshMaxSlopeDegrees + margin. 10 (default) = 60 deg at the
        // shipped 50 deg cap; 0 = exactly the cap (more aggressive, map-changing).
        public float NavMeshSteepFaceMarginDegrees = NavMeshPipelineDefaults.SteepFaceMarginDegrees;
        // Height allowance in agent-climb units: faces up to this tall are kept as step/kerb connectors.
        public float NavMeshSteepFaceMinHeightClimbs = NavMeshPipelineDefaults.SteepFaceMinHeightClimbs;
        public bool NavMeshCullBelowHeight = false;
        public float NavMeshMinHeight = 0f;
        // Water-level mode (always active; scenes always have a water level, dry maps set it well below
        // the terrain so this is a natural no-op). ON = CULL: drop every FLOOR-LIKE soup triangle whose
        // lifted lowest corner is below the level. OFF = RAISE (v2 only): clamp every FLOOR-LIKE soup
        // vertex whose lifted height is below the level up to exactly the level, so the mesh never sits
        // under the surface. Floor-like = Recast-space upward normal >= cos(NavMeshMaxSlopeDegrees): wall
        // and obstacle geometry is NEVER culled or clamped, so walls keep standing and keep blocking. The
        // comparison is on the lifted height (soupY + NavMeshHeightOffsetMeters). The v1 mesher supports
        // the cull only; a v1 raise is a documented no-op.
        public bool NavMeshCullBelowWaterLevel = NavMeshPipelineDefaults.CullBelowWaterLevel;
        public float NavMeshHeightOffsetMeters = NavMeshPipelineDefaults.HeightOffsetMeters;
        public string NavMeshPrefabName = "";
        public string NavMeshExportFolder = "";
        public bool NavMeshClearExistingFaces = true;
        public bool NavMeshImportIntoScene = true;
        public bool NavMeshWriteObj = false;

        // Voxel core (navmesh v2, the default since 2026-09-29). The Export button runs the tiled
        // voxel pipeline (TiledNavMeshBuilder) instead of the v1 adaptive terrain mesher.
        // Terrain-only for now; set false to use the v1 mesher.
        public bool NavMeshUseVoxelCore = NavMeshPipelineDefaults.UseVoxelCore;
        // Voxel cell size (m). Default 0.25 m since 2026-09-29: the connectivity lab shows 1.0-1.2 m
        // doorways, walkways and stair treads systematically vanish at 0.5 m (erosion + quantization
        // leave 0 cells regardless of the radius setting). 0.25 costs ~4x build time and ~2-4x faces;
        // raise it to 0.5 for fast iteration on open terrain.
        public float NavMeshVoxelCellSize = NavMeshPipelineDefaults.VoxelCellSizeMeters;

        // Agent max climb (m) for the v2 voxel core. 0 = auto-derive so that
        // NavMeshMaxSlopeDegrees is actually traversable (Recast's ledge filter
        // needs walkableClimb >= 2*cs*tan(slope)/ch). See the README.
        public float NavMeshVoxelAgentClimbMeters = NavMeshPipelineDefaults.VoxelAgentClimbMeters;

        // Agent radius (m) for the v2 voxel core. Erosion = ceil(radius/cellSize) cells each side and
        // the tile border is 3 + ceil(radius/cellSize). Default 0.25 m: at cellSize 0.5 this is still
        // erosion 1, but at cellSize 0.25 it stays 1 instead of 2, which is what keeps 1.0-1.2 m
        // doorways / walkways / stair treads connected. See the connectivity lab in the README.
        public float NavMeshVoxelAgentRadius = NavMeshPipelineDefaults.VoxelAgentRadiusMeters;

        // Recast ledge filter (default true = Recast behaviour). It trims spans within walkableClimb of
        // a drop, which keeps the mesh off real cliff edges but also eats the border cells of narrow
        // walkways/bridges that have drops on both sides. Turn off if narrow structures keep losing
        // their edges (or if a level is connected on one side but not the other).
        public bool NavMeshVoxelLedgeFilter = NavMeshPipelineDefaults.VoxelLedgeFilter;

        // Gap fill (cells, default 2, 0 = off). Before erosion, fills a column with no walkable span
        // when walkable spans flank it on opposite sides along one axis with an empty run of at most
        // this many cells and a floor difference within the agent climb. Closes abutting-prefab /
        // paved-terrace seams (a hairline collision gap that erosion would widen into a break).
        public int NavMeshVoxelGapFillCells = NavMeshPipelineDefaults.VoxelGapFillCells;

        // Stitch (cells, default 4 = 1.0 m at cellSize 0.25, 0 = off). Per tile: reconnects two mesh
        // components whose nearest boundary edges are within this many cells and within the agent climb,
        // by adding a small quad that reuses existing vertices. Refused across walls/doors/ceilings.
        public int NavMeshVoxelStitchMaxGapCells = NavMeshPipelineDefaults.VoxelStitchMaxGapCells;

        // Prune small isolated islands after the merge (sealed-void lids, stray fragments).
        public bool NavMeshVoxelPruneIsolatedIslands = NavMeshPipelineDefaults.VoxelPruneIsolatedIslands;
        public int NavMeshVoxelPruneMaxIslandFaces = NavMeshPipelineDefaults.VoxelPruneMaxIslandFaces;

        // Final-mesh sliver cleanup (thin/tiny triangles). Default ON, documented in the README.
        public bool NavMeshVoxelCleanup = NavMeshPipelineDefaults.VoxelCleanup;

        // Final quad merge, part of the cleanup (default ON). Thin faces are combined with a
        // near-coplanar neighbour into one emitted 4-vertex face; geometry is unchanged (only the
        // shared interior edge disappears). Switch OFF instantly if the engine's importer ever
        // dislikes n-gon faces.
        public bool NavMeshVoxelQuadMerge = NavMeshPipelineDefaults.VoxelQuadMerge;

        // Gate / door PLATE BLEND (v2 voxel core, default ON). A prop that carries a rigid, dead-flat
        // floor slab - a castle gate's floor colliders are the reported case - projects into the voxel
        // grid as a perfectly horizontal plate that ignores the ground it is dropped onto. On sloping
        // terrain one end of that plate then ends up more than an agent climb above its neighbour and
        // stops stitching (measured on CC_20: the plate at y=86.30 across a passage whose terrain falls
        // 87.1 -> 82.8 m, so the far end met faces 1.2 m below it). With this on, such a plate is
        // ROTATED - by at most NavMeshMaxSlopeDegrees, and by at most its own rise budget - onto a plane
        // least-squares fitted to the built mesh around it, so both ends meet the surroundings inside the
        // agent climb. Only the plate's own vertices move, so no face is added or removed and no edge is
        // touched. See TiledNavMeshBuilder.BlendGatePlates and the README.
        public bool NavMeshVoxelGatePlateBlend = NavMeshPipelineDefaults.VoxelGatePlateBlend;

        // BARRIERS (default ON). When on, every barrier body whose blocking mask stops agents is
        // collected and carved out of the navmesh as a solid wall, so an agent can no longer path
        // across it. Masks that only stop missiles/weapons are deliberately NOT carved - the engine
        // excludes MissileOnly bodies from every agent raycast, so the game itself lets agents walk
        // through those, and honouring them here would stop agents at a wall that does not exist for
        // them. Set false to get the pre-barrier behaviour back byte for byte.
        //
        // One-way is NOT reproduced: the barrier's Flip flag is a physics-normal direction, and a
        // navmesh has no directed edges, so the carved wall blocks both ways. That is strictly more
        // restrictive than the runtime rule and is documented in README.md.
        public bool NavMeshVoxelBarriers = NavMeshPipelineDefaults.VoxelBarriers;

        // Extra probe points for the scene snapshot (format "x,y;x,y;...", default empty). The harness
        // asserts each point is covered by a face on the largest island after the build.
        public string NavMeshSnapshotAssertPoints = "";

        // Detail mesh for the v2 voxel core (default true). Adds interior vertices so faces follow
        // the terrain surface instead of chording under hills/knolls; bounded by
        // NavMeshMaxErrorMeters. Disable for a leaner bin (flat/plateau scenes only).
        public bool NavMeshVoxelDetailMesh = NavMeshPipelineDefaults.VoxelDetailMesh;

        // v2 detail-mesh sampling distance (m). 0 = auto (max(0.9, cellSize)). Default 2.0 m: the
        // harness sweep shows it removes the tight/sliver faces Recast's detail edge tessellation
        // makes along walkable boundaries, with no fidelity loss. Lower it for more surface detail
        // (more faces); raise it (up to ~2*minPolyExtent) for fewer faces.
        public float NavMeshVoxelDetailSampleDist = NavMeshPipelineDefaults.VoxelDetailSampleDistMeters;

        // v2 contour simplification error (voxels). 0 = auto (NavMeshMaxErrorMeters / cellSize).
        // Default 2.0 vox (V3.64): the sliver census measured the post-cleanup sliver population
        // falling -43% / -24% / -34% on CC_20 / Birtu / Basamu versus 1.3 vox, and the 0.50 m
        // boundary budget at cs 0.25 m equals the cleanup's own SurfaceToleranceMeters. 1.3 vox was
        // the pre-census default.
        public float NavMeshVoxelContourMaxError = NavMeshPipelineDefaults.VoxelContourMaxErrorVoxels;

        // Phase 3: ingest static asset collision geometry (interiors, stairs, decks, bridges over
        // terrain) into the v2 voxel core. NavMeshHeightOffsetMeters lifts the whole merged mesh
        // (terrain and structures alike) so it never intersects either. Dynamic props (e.g. merlons)
        // and flora are toggled separately.
        public bool NavMeshVoxelIncludeAssets = NavMeshPipelineDefaults.VoxelIncludeAssets;
        public bool NavMeshVoxelIncludeDynamic = NavMeshPipelineDefaults.VoxelIncludeDynamic;
        public bool NavMeshVoxelIncludeFlora = NavMeshPipelineDefaults.VoxelIncludeFlora;

        // Diagnostic (default off): log one line per included entity immediately before its native
        // PhysicsShape.GetTriangle read. If that native call ever read-AVs the process, the last line
        // in the log names the culprit entity/body. Cheap enough to leave off in normal runs.
        public bool NavMeshVoxelAssetTrace = NavMeshPipelineDefaults.VoxelAssetTrace;

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
        public bool NavMeshVoxelSteppedProps = NavMeshPipelineDefaults.VoxelSteppedProps;

        // Gate / door BRIDGE STRIPS (v2 voxel core, default ON). A gate's passage floor is a stack of
        // separate rigid collider slabs whose edge stops short of the ground beyond the doorway; the
        // voxel core projects them as a proud plate and the ledge trim eats the crossing, so the direct
        // path through the gate is a hole. With this on, GateBridgeStrips finds the gate-flagged
        // thin-slab passage plate and emits a force-walkable bridge strip from its edge to the first
        // surface beyond the gap (within the agent climb). Name-free (the fixE gate tag + geometry). See
        // GateBridgeStrips and the README "Gate bridge strips".
        public bool NavMeshVoxelGateConnectors = NavMeshPipelineDefaults.VoxelGateConnectors;

        // Diagnostic (default off): also write <prefab>_soup_terrain.obj and <prefab>_soup_assets.obj
        // next to the bin (engine space). Lets a failing spot be inspected with geometry provenance
        // (terrain soup vs asset triangles) in the editor or Blender.
        public bool NavMeshVoxelWriteSoupObj = NavMeshPipelineDefaults.VoxelWriteSoupObj;

        // Terrain-hole audit (default off). The engine's bulk node arrays carry no hole information, so
        // a carve can become a false walkable lid. When on, every sampled terrain vertex is compared
        // against the hole-aware point query Scene.GetTerrainHeight(pos, checkHoles: true) and any
        // vertex that disagrees by more than 1 m is treated as a hole: its terrain cells are dropped
        // from the soup. Costs one native point query per vertex (see the README for the measured cost).
        public bool NavMeshVoxelTerrainHoleAudit = NavMeshPipelineDefaults.VoxelTerrainHoleAudit;

        // Run the v2 voxel build on a background thread so the editor UI stays alive during the
        // 3-60 s builds. Only the pure-managed voxel build runs off-thread; the terrain read and the
        // bin write + native import stay on the editor main thread (scene access must be there).
        // Completion is picked up on the editor tick (decompiled: ManagedScriptHolder
        // .TickComponentsEditor -> ScriptComponentBehavior.OnEditorTick, called for every script
        // component in the open scene). Ignored for the v1 mesher (NavMeshUseVoxelCore=false), which
        // stays synchronous. See the README ("Async build").
        public bool NavMeshAsyncBuild = true;

        // Mirror the important run lines (start / stage / progress / finish-or-failure) to the
        // editor console (Debug.Print) and, in game, to the player (InformationManager). The
        // verbose per-session log FILE is unaffected - it stays exactly as detailed as it was.
        // Without this, a 30 s - 5 min build is a silent wait. See NavMeshVisibleLog + README.
        public bool NavMeshConsoleMessages = NavMeshPipelineDefaults.ConsoleMessages;

        // LIVE build status, shown as its own row in the editor's script-variables
        // panel for this entity - the panel that already carries the Export NavMesh
        // button and the settings above. It is what answers "what stage is it at?"
        // during a multi-minute build; the editor's own Console panels cannot be
        // written to from a mod (see NavMeshVisibleLog for the whole story).
        //
        // A public instance field is all that is needed: ScriptComponentBehavior
        // .CollectEditableFields() gives every public non-static field a widget, so
        // no [EditableScriptComponentVariable] is required. The value is owned here
        // (it is a saved script variable, so it survives a scene reload); the editor
        // tick additionally asks the engine to repaint the row.
        //
        // Driven by NavMeshVisibleLog.StatusText, i.e. by the same line the visible
        // log produces - the wording is shared, never duplicated. Advances whatever
        // NavMeshConsoleMessages is set to.
        public string NavMeshBuildStatus = NavMeshVisibleLog.IdleStatus;

        // Draw the same status line as floating text in the editor viewport, on top of
        // the panel row. Off by default because the engine gates the debug-text overlay
        // on the ini/CVar option `show_debug_text`, which the shipped
        // Modules\Native\engine_module.ini does not set (so it is off). Turn this on
        // AND add `show_debug_text = 1` to that file, then restart the editor.
        public bool NavMeshOverlayText = false;

        // Where the overlay line is drawn, in the engine's own screen coordinates.
        // (0, 0) is the top-left corner whether the engine treats these as pixels or
        // as fractions, so it is the safe default; nudge them here if your build puts
        // the text somewhere else - no rebuild needed, and the values save with the
        // scene. NavMeshOverlaySeconds is how long the DONE / import-skipped / FAILED
        // line lingers before the overlay goes away again.
        public float NavMeshOverlayX = 0f;
        public float NavMeshOverlayY = 0f;
        public float NavMeshOverlaySeconds = 5f;

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

        // Last status text already pushed to the script panel, so the tick only
        // touches the engine when the line actually changed.
        private string _pushedStatus = "";

        // Viewport overlay state. Main thread only (driven from OnEditorTick).
        private const int OverlayIntervalMs = 200;      // ~5 Hz: a few draws a second
        private const float OverlayDrawLifetime = 0.6f; // seconds, > the interval, so
                                                        // consecutive draws overlap and
                                                        // the line never flickers
        private long _nextOverlayMs;
        private long _overlayHoldUntilMs;
        private string _latchedStatus = "";
        private bool _overlayHintLogged;
        private uint _overlayColour = OverlayWhite;

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
            public bool[] AssetGate;
            public bool[] TerrainHoleMask;
            public TiledNavMeshBuilder.BorderPolygon Border;
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

            // Whatever the background build queued while it was running (progress lines
            // are only safe to print from the thread that owns the editor UI).
            NavMeshVisibleLog.Pump();

            // Then mirror the line that just went out into this component's own
            // script-panel row, and draw it in the viewport.
            PushBuildStatusToPanel();
            DrawStatusOverlay();
        }

        // Mirrors NavMeshVisibleLog.StatusText into NavMeshBuildStatus and asks the
        // engine to repaint that row. Main thread only (this is called from
        // OnEditorTick); never throws.
        //
        // SetVariableEditorWidgetValue is the engine's own "update this variable's
        // editor widget" call (TaleWorlds.Engine.IScriptComponent, engine method
        // set_variable_editor_widget_value; RglSftString == 0). It is best-effort
        // by design: if the editor does not act on it the row still holds the right
        // text and is correct whenever the panel is rebuilt or reselected, and the
        // authoritative value is the managed field either way.
        private void PushBuildStatusToPanel()
        {
            string status = NavMeshVisibleLog.StatusText;
            if (string.Equals(status, _pushedStatus, StringComparison.Ordinal))
                return;

            _pushedStatus = status;
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
                // The engine logs "Field name %s can not be found for script component %s."
                // and does nothing when the name is unknown, and an older engine may not
                // have the method at all. Never let this break the editor tick: the row
                // still carries NavMeshBuildStatus.
                _pushedStatus = NavMeshBuildStatus;
            }
        }

        // Draws the same status line as floating text in the editor viewport, using the
        // engine's own debug-text overlay:
        //   MBDebug.RenderText(float screenX, float screenY, string text, uint color, float time)
        //     -> TaleWorlds.Engine.IScriptComponent.RenderDebugText -> engine method
        //        "render_debug_text"
        // RenderText (not RenderDebugText) on purpose: RenderDebugText is
        // [Conditional("_RGL_KEEP_ASSERTS")] and would be compiled out of an assembly
        // that does not define that symbol; RenderText is unconditional.
        //
        // Rules, so the viewport is never cluttered:
        //   * nothing is drawn while there is no run (status == IdleStatus);
        //   * while a run is live the line tracks the status, redrawn every
        //     OverlayIntervalMs (~5 Hz) - never per frame;
        //   * a DONE / import-skipped / FAILED line lingers for NavMeshOverlaySeconds,
        //     then the overlay goes away completely.
        // Main thread only (called from OnEditorTick); never throws.
        private void DrawStatusOverlay()
        {
            if (!NavMeshOverlayText)
                return;

            long now = NowMs();
            string status = NavMeshVisibleLog.StatusText;
            bool live = !string.Equals(status, NavMeshVisibleLog.IdleStatus, StringComparison.Ordinal);

            if (live)
            {
                _latchedStatus = status;
                _overlayColour = ColourFor(status);
                _overlayHoldUntilMs = now + (long)(NavMeshOverlaySeconds * 1000.0);
            }
            else if (now >= _overlayHoldUntilMs)
            {
                // Run over and the linger window has passed: draw nothing at all.
                _latchedStatus = "";
                return;
            }

            if (now < _nextOverlayMs)
                return;

            _nextOverlayMs = now + OverlayIntervalMs;

            try
            {
                if (!_overlayHintLogged)
                {
                    _overlayHintLogged = true;
                    // The engine has no managed setter for show_debug_text (IConfig
                    // .SetRGLConfig takes an opaque numeric id that no TaleWorlds.*
                    // assembly maps to it), so the option has to be enabled in the ini
                    // before start-up. Say so once in the engine log rather than
                    // leaving the user staring at an empty viewport. This does NOT touch
                    // NavMeshLog, which stays byte-identical.
                    TaleWorlds.Library.Debug.Print(
                        "NavMesh: viewport overlay on, but the engine draws debug text only"
                        + " with `show_debug_text = 1` in Modules\\Native\\engine_module.ini"
                        + " (needs an editor restart). The NavMeshBuildStatus row in the"
                        + " script panel is the reliable fallback.");
                }

                MBDebug.RenderText(NavMeshOverlayX, NavMeshOverlayY, _latchedStatus,
                    _overlayColour, OverlayDrawLifetime);
            }
            catch (Exception)
            {
                // Debug text can be unavailable (no debug renderer, stripped engine).
                // Never let it break the editor tick, and stop retrying every tick.
                NavMeshOverlayText = false;
            }
        }

        // Terminal lines are tinted from the same prefix constants NavMeshVisibleLog
        // builds them from, so the wording has a single source.
        private static uint ColourFor(string status)
        {
            if (status.StartsWith(NavMeshVisibleLog.FailPrefix, StringComparison.Ordinal))
                return OverlayRed;
            if (status.StartsWith(NavMeshVisibleLog.SkipPrefix, StringComparison.Ordinal))
                return OverlayYellow;
            return OverlayWhite;
        }

        private static long NowMs()
        {
            return System.Diagnostics.Stopwatch.GetTimestamp()
                * 1000L / System.Diagnostics.Stopwatch.Frequency;
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
                NavMeshVisibleLog.BeginRun(NavMeshVisibleLog.Context.Editor, NavMeshConsoleMessages);

                Scene scene = Scene;
                if (scene == null)
                {
                    log("ERROR: no scene.");
                    NavMeshVisibleLog.Fail("no scene");
                    return;
                }

                NavMeshVisibleLog.Start(SafeName(scene),
                    PreviewPrefabName(scene, NavMeshPrefabName), BuildTag + " / Import Only", false);

                log("run: build=" + BuildTag + " trigger=editor-import-only scene=" + SafeName(scene)
                    + " machine=" + Environment.MachineName
                    + " time=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    + " logFile=" + (NavMeshLog.CurrentFilePath ?? "(disabled)"));
                log("settings: importOnly=True clearExisting=" + NavMeshClearExistingFaces
                    + " prefabName='" + (NavMeshPrefabName ?? "") + "'"
                    + " exportFolder='" + (NavMeshExportFolder ?? "") + "'"
                    + " consoleMessages=" + NavMeshConsoleMessages
                    + " writeLogFile=" + NavMeshWriteLogFile
                    + " logFolder='" + (NavMeshLogFolder ?? "") + "'");

                string prefabName = TerrainNavMeshGenerator.ResolvePrefabName(scene, NavMeshPrefabName, log);
                string folder = TerrainNavMeshGenerator.ResolveFolder(scene, NavMeshExportFolder, log);
                log("import only: prefab='" + prefabName + "' folder=" + folder);

                // Same resolvability guard as the export half, from the same
                // mirror. Import Only exists for the "export once, restart the
                // editor, import" loop, so the bin is normally there; when the name
                // is not a key in the engine's index - most often because the
                // scene's module is not loaded in this session, and the copy the
                // export wrote into a loaded module only became a key at the last
                // start-up - calling the importer would only make the engine log
                // 'No navigation mesh prefab was found with name "..."!' and add
                // nothing.
                NavMeshPrefabIndex.Analysis analysis = NavMeshPrefabIndex.Analyze(prefabName, folder);
                log("prefab index: folderExistedBefore=" + analysis.FolderExistedBefore
                    + " binExistedBefore=" + analysis.CanonicalBinExistedBefore
                    + " module=" + (analysis.CanonicalModuleId ?? "?")
                    + " moduleId='" + (analysis.CanonicalSubModuleId ?? "") + "'"
                    + " moduleActive=" + (analysis.CanonicalModuleActiveKnown
                        ? analysis.CanonicalModuleActive.ToString()
                        : "unknown"));
                NavMeshPrefabIndex.DescribeLookup(log, analysis);

                string unresolvable = NavMeshPrefabIndex.ExplainUnresolvable(log, analysis);
                if (unresolvable != null)
                {
                    log("import SKIPPED (the engine cannot resolve this prefab name in this"
                        + " session): " + unresolvable);
                    log("done: importOnly=true prefab='" + prefabName + "' imported=false");
                    NavMeshVisibleLog.Skip(unresolvable);
                    return;
                }

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
                NavMeshVisibleLog.Stage("importing");
                scene.ImportNavigationMeshPrefab(prefabName, 0);
                log("import call returned.");

                int facesAfterRun = SafeFaceCount(scene);
                log("faces after import: " + (facesAfterRun < 0 ? "unavailable" : facesAfterRun.ToString()));
                LogImportCheck(log, facesBeforeRun, facesAfterRun, NavMeshClearExistingFaces);

                log("done: importOnly=true prefab='" + prefabName + "'");
                NavMeshVisibleLog.Finish("imported '" + prefabName + "' (scene faces "
                    + (facesBeforeRun < 0 ? "?" : facesBeforeRun.ToString(CultureInfo.InvariantCulture))
                    + " -> "
                    + (facesAfterRun < 0 ? "?" : facesAfterRun.ToString(CultureInfo.InvariantCulture))
                    + "); the .bin at " + folder + " is unchanged");
            }
            catch (Exception ex)
            {
                NavMeshLog.Write("ERROR: " + ex.GetType().Name + ": " + Flat(ex.Message));
                NavMeshLog.Write("ERROR stack: " + Flat(ex.StackTrace));
                NavMeshVisibleLog.Fail(ex.GetType().Name + ": " + Flat(ex.Message));
            }
        }

        // Shared post-import diagnosis for both the full run and Import Only.
        // The engine resolves a name through an index it built while the modules
        // were loading (native rva 0x4EBCE0: every *.bin stem under an existing
        // Modules/<M>/NavMeshPrefabs), so the face count is the only reliable
        // evidence that an import actually landed.
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
                log("import warning: no faces appeared. Either this prefab name was not in the engine's"
                    + " index (it is built from the *.bin files that existed, in the modules that were"
                    + " loaded, when the modules loaded; nothing re-runs it - look for a 'FOUND-inactive'"
                    + " or 'FOUND-new' line above), or the imported bin was empty for this scene.");
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
                // Border polygon, written next to the heights so the harness can replay the clamp
                // offline. Same read the export uses, so the dump and the build cannot disagree.
                int snapSoftProbes, snapHardProbes;
                TiledNavMeshBuilder.BorderPolygon snapBorder =
                    ReadBorderPolygon(scene, log, out snapSoftProbes, out snapHardProbes);
                if (snapBorder != null)
                {
                    td.BorderXy = snapBorder.ToArray();
                    td.BorderSource = snapBorder.Source;
                }
                td.BorderSoftProbes = snapSoftProbes;
                td.BorderHardProbes = snapHardProbes;
                string terrainPath = System.IO.Path.Combine(folder, prefabName + "_snapshot_terrain.bin");
                SceneSnapshot.WriteTerrainBin(terrainPath, td);

                // Assets: the same collector policy the export uses, keeping per-entity groups.
                var assetsWatch = System.Diagnostics.Stopwatch.StartNew();
                SceneCollisionGeometry.AssetGeometry assets = SceneCollisionGeometry.CollectDetailed(
                    scene, NavMeshVoxelIncludeDynamic, NavMeshVoxelIncludeFlora, NavMeshVoxelAssetTrace,
                    true, NavMeshVoxelSteppedProps, log, NavMeshVoxelCellSize,
                    ResolvedRampClimb(NavMeshVoxelAgentClimbMeters, NavMeshVoxelCellSize, NavMeshMaxSlopeDegrees), NavMeshVoxelLedgeFilter,
                    NavMeshVoxelGateConnectors);
                assetsWatch.Stop();

                var records = new System.Collections.Generic.List<SceneSnapshot.EntityRecord>();
                for (int i = 0; i < assets.Entities.Count; i++)
                {
                    SceneCollisionGeometry.AssetEntityRecord src = assets.Entities[i];
                    records.Add(SceneSnapshot.Expand(src.Name, unchecked((int)src.Flags), src.Mobility,
                        src.Capped, src.VertsXyz, src.Tris, src.Gate));
                }
                string assetsPath = System.IO.Path.Combine(folder, prefabName + "_snapshot_assets.bin");
                SceneSnapshot.WriteAssetsBin(assetsPath, records);

                // The scene's water level, recorded in the header for the record (and so a later
                // offline --cullwater / --raisewater A/B can reuse it). Never throws.
                float snapshotWater = SafeWaterLevel(scene, log);

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
                    + " gatePlateBlend=" + NavMeshVoxelGatePlateBlend
                    + " barriers=" + NavMeshVoxelBarriers
                    + " quadMerge=" + NavMeshVoxelQuadMerge
                    + " includeAssets=" + NavMeshVoxelIncludeAssets
                    + " includeDynamic=" + NavMeshVoxelIncludeDynamic
                    + " includeFlora=" + NavMeshVoxelIncludeFlora);
                sb.AppendLine("grid: " + grid.Width + "x" + grid.Height + " spacing=" + F(grid.Spacing)
                    + "m stride=" + NavMeshSampleStride + " fallback=" + grid.UsedPointFallback
                    + " heights=" + grid.Heights.Length);
                sb.AppendLine("water: " + (float.IsNegativeInfinity(snapshotWater)
                    ? "unavailable" : F(snapshotWater) + "m (scene.GetWaterLevel)"));
                sb.AppendLine("border: source=" + TiledNavMeshBuilder.BorderPolygon.SourceName(
                        snapBorder != null ? snapBorder.Source : TiledNavMeshBuilder.BorderPolygon.SourceNone)
                    + " vertices=" + (snapBorder != null ? snapBorder.VertexCount : 0)
                    + (snapBorder != null
                        ? " area=" + F(snapBorder.AreaM2) + "m2 min=" + F(snapBorder.MinX) + ","
                            + F(snapBorder.MinZ) + " max=" + F(snapBorder.MaxX) + "," + F(snapBorder.MaxZ)
                        : "")
                    + " probes soft=" + snapSoftProbes + " hard=" + snapHardProbes
                    + " (hard wins when >=3; the engine's default-rectangle hard count is never trusted)");
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
                NavMeshVoxelPruneMaxIslandFaces, NavMeshVoxelSteppedProps, NavMeshVoxelGateConnectors, NavMeshVoxelQuadMerge,
                NavMeshVoxelBarriers, NavMeshCullBelowWaterLevel, NavMeshConsoleMessages,
                NavMeshSteepFaceMarginDegrees, NavMeshSteepFaceMinHeightClimbs);
        }

        // BORDER POLYGON READ (V3.59). Runs on the MAIN THREAD and is shared by the editor button, the async
        // editor dispatch and the in-game mission logic, so both triggers get the same playable area.
        //
        // The scene declares its playable bounds with point entities: prefabs `border_soft` /
        // `border_hard`, tagged `walk_area_vertex` / `walk_area_vertex_hard` (see
        // Modules\Native\Prefabs\scene_prop_prefabs.xml). The engine convex-hulls them natively and
        // exposes the hull through Scene.GetSoftBoundaryVertex* / GetHardBoundaryVertex*;
        // MBSceneUtilities.GetSoftBoundaryPoints / GetHardBoundaryPoints are the managed wrappers and
        // already return that hull, so nothing here re-derives geometry.
        //
        // The one trap, and the reason this is not a two-line call: the hard side is gated on the PROBE
        // COUNT, never on Scene.GetHardBoundaryVertexCount(). On a scene with fewer than three hard probes
        // the engine does not report "no border" - its native hard-polygon builder substitutes a
        // four-vertex default rectangle spanning about (-0.045,-100)..(50,50) and clears a private
        // "hard border defined" flag that managed code cannot read. Trusting the count would clamp every
        // borderless scene (and CC_20_siege, Birtu and Basamu, which have soft probes only) to that
        // rectangle. BannerlordSceneChecker.CheckBorders gates on the count for the same reason; the
        // policy itself lives in TiledNavMeshBuilder.SelectBorderSource so it can be tested without a scene.
        //
        // `flora_bounding_rect` in scene.xscene is NOT used: it is the probes' AABB (looser than the hull -
        // ~17% slack on CC_20) and no TaleWorlds assembly exposes it.
        private static TiledNavMeshBuilder.BorderPolygon ReadBorderPolygon(Scene scene,
            System.Action<string> log, out int softProbes, out int hardProbes)
        {
            softProbes = 0;
            hardProbes = 0;
            if (scene == null)
                return null;
            try
            {
                softProbes = scene.FindWeakEntitiesWithTag(MBSceneUtilities.SoftBorderVertexTag).Count();
                hardProbes = scene.FindWeakEntitiesWithTag(MBSceneUtilities.HardBorderVertexTag).Count();
            }
            catch (Exception ex)
            {
                // A scene type that cannot answer the tag query must not fail the export: fall through to
                // the no-border path, which is exactly the pre-V3.59 behaviour.
                if (log != null)
                    log("border: probe query failed (" + Flat(ex.GetType().Name) + ": "
                        + Flat(ex.Message) + "); no border clamp this run");
                return null;
            }

            int source = TiledNavMeshBuilder.SelectBorderSource(softProbes, hardProbes);
            if (source == TiledNavMeshBuilder.BorderPolygon.SourceNone)
            {
                if (log != null)
                    log("border: source=none probes soft=" + softProbes + " hard=" + hardProbes
                        + " (<3 each; needs at least three to describe a border) - no clamp, build unchanged");
                return null;
            }

            var points = new MBList<Vec2>();
            try
            {
                if (source == TiledNavMeshBuilder.BorderPolygon.SourceHard)
                    points = MBSceneUtilities.GetHardBoundaryPoints(scene);
                else
                    points = MBSceneUtilities.GetSoftBoundaryPoints(scene);
            }
            catch (Exception ex)
            {
                if (log != null)
                    log("border: boundary query failed (" + Flat(ex.GetType().Name) + ": "
                        + Flat(ex.Message) + "); no border clamp this run");
                return null;
            }
            if (points == null || points.Count < 3)
            {
                if (log != null)
                    log("border: engine returned " + (points == null ? 0 : points.Count)
                        + " hull vertices for " + TiledNavMeshBuilder.BorderPolygon.SourceName(source)
                        + " - no clamp this run");
                return null;
            }

            var ring = new float[points.Count * 2];
            for (int i = 0; i < points.Count; i++)
            {
                ring[i * 2] = points[i].x;
                ring[i * 2 + 1] = points[i].y;
            }
            var poly = TiledNavMeshBuilder.BorderPolygon.FromPoints(ring, source);
            if (poly == null)
            {
                if (log != null)
                    log("border: hull from " + points.Count + " vertices encloses no area - no clamp this run");
                return null;
            }

            if (log != null)
                log("border: source=" + TiledNavMeshBuilder.BorderPolygon.SourceName(source)
                    + " vertices=" + poly.VertexCount
                    + " area=" + F(poly.AreaM2) + "m2"
                    + " min=" + F(poly.MinX) + "," + F(poly.MinZ)
                    + " max=" + F(poly.MaxX) + "," + F(poly.MaxZ)
                    + " probes soft=" + softProbes + " hard=" + hardProbes
                    + " (hard wins when >=3; the engine's default-rectangle hard count is never trusted)");
            return poly;
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
                clearExistingFaces, importIntoScene, writeLogFile, logFolder,
                NavMeshPipelineDefaults.UseVoxelCore,
                NavMeshPipelineDefaults.VoxelCellSizeMeters,
                NavMeshPipelineDefaults.VoxelAgentClimbMeters,
                NavMeshPipelineDefaults.VoxelDetailMesh,
                NavMeshPipelineDefaults.VoxelDetailSampleDistMeters,
                NavMeshPipelineDefaults.VoxelContourMaxErrorVoxels,
                NavMeshPipelineDefaults.VoxelIncludeAssets,
                NavMeshPipelineDefaults.VoxelIncludeDynamic,
                NavMeshPipelineDefaults.VoxelIncludeFlora,
                NavMeshPipelineDefaults.VoxelAssetTrace,
                NavMeshPipelineDefaults.VoxelAgentRadiusMeters,
                NavMeshPipelineDefaults.VoxelLedgeFilter,
                NavMeshPipelineDefaults.VoxelWriteSoupObj,
                NavMeshPipelineDefaults.VoxelTerrainHoleAudit,
                NavMeshPipelineDefaults.VoxelGapFillCells,
                NavMeshPipelineDefaults.VoxelStitchMaxGapCells,
                NavMeshPipelineDefaults.VoxelPruneIsolatedIslands,
                NavMeshPipelineDefaults.VoxelPruneMaxIslandFaces,
                NavMeshPipelineDefaults.VoxelSteppedProps,
                NavMeshPipelineDefaults.VoxelGateConnectors,
                NavMeshPipelineDefaults.VoxelQuadMerge,
                NavMeshPipelineDefaults.VoxelBarriers,
                NavMeshPipelineDefaults.CullBelowWaterLevel,
                NavMeshPipelineDefaults.ConsoleMessages);
        }

        // Full signature: useVoxelCore selects the v2 tiled voxel pipeline, and
        // consoleMessages mirrors the important run lines to the editor console /
        // the player (NavMeshVisibleLog; the log FILE is unaffected).
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
            bool voxelGateConnectors,
            bool voxelQuadMerge,
            bool voxelBarriers,
            bool cullBelowWaterLevel,
            bool consoleMessages,
            float steepFaceMarginDegrees = NavMeshPipelineDefaults.SteepFaceMarginDegrees,
            float steepFaceMinHeightClimbs = NavMeshPipelineDefaults.SteepFaceMinHeightClimbs)
        {
            var runWatch = System.Diagnostics.Stopwatch.StartNew();
            NavMeshLog.Initialize(writeLogFile, logFolder);
            System.Action<string> log = NavMeshLog.Write;

            // Editor triggers come from the component's buttons (and are the only ones
            // that may run the async path); anything else is the in-game hotkey. Both
            // are BLOCKING here - RunPipeline never hands work to a background thread -
            // and the start line says so, instead of the user discovering it as a freeze.
            NavMeshVisibleLog.BeginRun(IsEditorTrigger(trigger)
                ? NavMeshVisibleLog.Context.Editor
                : NavMeshVisibleLog.Context.InGame, consoleMessages);

            try
            {
                if (scene == null)
                {
                    log("ERROR: no scene.");
                    NavMeshVisibleLog.Fail("no scene");
                    return null;
                }

                NavMeshVisibleLog.Start(SafeName(scene),
                    PreviewPrefabName(scene, prefabNameOverride), BuildTag, false);

                log("run: build=" + BuildTag + " trigger=" + trigger + " scene=" + SafeName(scene)
                    + " machine=" + Environment.MachineName
                    + " time=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    + " logFile=" + (NavMeshLog.CurrentFilePath ?? "(disabled)"));

                // Water handling is always active: the NavMeshCullBelowWaterLevel toggle picks the MODE
                // (ON = cull below the level, OFF = raise the navmesh up to it), so there is never navmesh
                // under the surface either way. Scenes always have a water level (dry maps set it well
                // below the terrain), so this is a natural no-op on dry maps - no has-water detection.
                // Read on the MAIN THREAD (the only scene access it needs).
                float waterLevel = 0f;
                string waterMode = "off";
                bool waterCull = cullBelowWaterLevel;
                float waterReading = SafeWaterLevel(scene, log);
                if (!float.IsNegativeInfinity(waterReading))
                {
                    waterLevel = waterReading;
                    if (waterCull)
                    {
                        waterMode = "cull";
                        settings.CullBelowHeight = true;   // v1 mechanism: true-height min-corner cull
                        settings.MinHeight = waterLevel;
                        log("water: mode=cull (v1 cullBelowHeight=true minHeight=" + F(waterLevel) + "m)");
                    }
                    else
                    {
                        waterMode = "raise";
                        // Mode B is v2-only: the v1 mesher has no clamp, so a v1 run is left unchanged.
                        log("water: mode=raise level=" + F(waterLevel) + "m (v2 only; the v1 mesher has no raise)");
                    }
                }
                else
                {
                    waterCull = false;   // the read failed; the feature is disabled for this run
                }

                log("settings: stride=" + settings.SampleStride
                    + " maxSlope=" + F(settings.MaxSlopeDegrees) + "deg"
                    + " maxError=" + F(settings.MaxErrorMeters) + "m"
                    + " coarsest=" + F(settings.CoarsestSpacingMeters) + "m"
                    + " cullBelowHeight=" + settings.CullBelowHeight
                    + " minHeight=" + F(settings.MinHeight)
                    + " water=" + waterMode
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
                    + " barriers=" + voxelBarriers
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
                    + " consoleMessages=" + consoleMessages
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
                NavMeshVisibleLog.Stage("collecting assets");

                // The playable area, read on the MAIN THREAD before anything is handed to a worker.
                // A null polygon makes every border stage a structural no-op, so a scene with no (or
                // fewer than three) probes builds exactly what it built before V3.59.
                int borderSoftProbes, borderHardProbes;
                TiledNavMeshBuilder.BorderPolygon border =
                    ReadBorderPolygon(scene, log, out borderSoftProbes, out borderHardProbes);

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
                    voxel.SteepFaceMarginDegrees = steepFaceMarginDegrees;
                    voxel.SteepFaceMinHeightClimbs = steepFaceMinHeightClimbs;
                    voxel.CullBelowWaterLevel = waterCull;                  // Mode A (toggle on)
                    voxel.RaiseBelowWaterLevel = waterMode == "raise";      // Mode B (toggle off)
                    voxel.WaterLevelMeters = waterLevel;

                    float[] assetVerts = null;
                    int[] assetTris = null;
                    bool[] assetForceWalkable = null;
                    bool[] assetLedgeExempt = null;
                    bool[] assetGate = null;
                    TiledNavMeshBuilder.BarrierWalls assetBarriersSync = null;
                    if (voxelIncludeAssets)
                    {
                        SceneCollisionGeometry.AssetTriangles assets =
                            SceneCollisionGeometry.Collect(scene, voxelIncludeDynamic, voxelIncludeFlora,
                                voxelAssetTrace, voxelSteppedProps, RunLog(log), voxelCellSizeMeters,
                                ResolvedRampClimb(voxelAgentClimbMeters, voxelCellSizeMeters, voxel.MaxSlopeDegrees), voxelLedgeFilter,
                                voxelGateConnectors);
                        assetVerts = assets.VertsXyz;
                        assetTris = assets.Tris;
                        assetForceWalkable = assets.ForceWalkableTris;
                        assetLedgeExempt = assets.LedgeExemptTris;
                        assetGate = assets.GateTris;
                        assetBarriersSync = assets.Barriers;
                        assetsMs = assets.ScanMs;
                    }
                    // Carved as solid spans by the builder; never mixed into assetVerts/assetTris.
                    voxel.Barriers = voxelBarriers ? assetBarriersSync : null;

                    VoxelNavMeshGenerator.SoupCapture soupCapture =
                        voxelWriteSoupObj ? new VoxelNavMeshGenerator.SoupCapture() : null;

                    long voxelBuildMs;
                    TerrainNavMeshBuilder.Result voxelMesh = VoxelNavMeshGenerator.BuildExportMesh(
                        grid.Heights, grid.Width, grid.Height, grid.Spacing, settings.SampleStride,
                        voxel, settings.WriteObj, voxelAgentClimbMeters, Environment.ProcessorCount,
                        assetVerts, assetTris, assetForceWalkable, assetLedgeExempt, assetGate, grid.HoleMask,
                        soupCapture, RunLog(log), out voxelBuildMs, border);
                    if (voxelMesh == null)
                    {
                        log("v2: mesh build aborted by the invariant gate; bin NOT written and import NOT attempted.");
                        log("done: totalMs=" + runWatch.ElapsedMilliseconds + " failed=true aborted=true");
                        NavMeshVisibleLog.Fail("the invariant gate rejected the mesh; nothing was written"
                            + " or imported (see the log for the offending counts)");
                        return null;
                    }

                    result = TerrainNavMeshGenerator.WriteAndImport(scene, voxelMesh, settings.WriteObj,
                        prefabNameOverride, folderOverride, clearExistingFaces, importIntoScene, RunLog(log),
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
                        RunLog(log));
                }

                int facesAfterRun = SafeFaceCount(scene);

                log(result.FaceCount + " faces, " + result.VertexCount + " vertices, "
                    + result.EdgeCount + " edges (culled: " + result.CulledBySlope + " slope, "
                    + result.CulledByHeight + " height)");
                log("bin written: " + result.BinPath);

                if (!string.IsNullOrEmpty(result.FallbackBinPath))
                    log("fallback bin written: " + result.FallbackBinPath);

                if (!string.IsNullOrEmpty(result.ObjPath))
                    log("OBJ written: " + result.ObjPath);

                if (result.ImportAttempted)
                {
                    log("faces imported into scene as '" + result.PrefabName + "' (static).");
                    LogImportCheck(log, facesBeforeRun, facesAfterRun, clearExistingFaces);
                }
                else
                {
                    log("import skipped" + (string.IsNullOrEmpty(result.ImportSkipReason)
                        ? " by request; import '" + result.PrefabName
                          + "' manually from the editor's navmesh prefab panel."
                        : "; import '" + result.PrefabName + "': " + result.ImportSkipReason));
                    NavMeshVisibleLog.Skip(ImportSkipNotice(result));
                }

                log("done: totalMs=" + runWatch.ElapsedMilliseconds
                    + " assetsMs=" + assetsMs
                    + " bin=" + result.BinByteCount
                    + " V=" + result.VertexCount
                    + " E=" + result.EdgeCount
                    + " F=" + result.FaceCount);

                // Always the last console line of the run (blocking mode).
                NavMeshVisibleLog.Finish("'" + result.PrefabName + "' V=" + result.VertexCount
                    + " F=" + result.FaceCount + " E=" + result.EdgeCount
                    + " in " + Secs(runWatch.ElapsedMilliseconds) + "s -> " + result.BinPath
                    + ImportOutcome(result, facesBeforeRun, facesAfterRun));
                return result;
            }
            catch (Exception ex)
            {
                log("ERROR: " + ex.GetType().Name + ": " + Flat(ex.Message));
                log("ERROR stack: " + Flat(ex.StackTrace));
                log("done: totalMs=" + runWatch.ElapsedMilliseconds + " failed=true");
                NavMeshVisibleLog.Fail(ex.GetType().Name + ": " + Flat(ex.Message));
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
                // Announced on the running build's console stream (no BeginRun here:
                // resetting it would wipe the throttle state of the run in flight).
                NavMeshVisibleLog.Stage("waiting for the running build");
                return;
            }

            NavMeshVisibleLog.BeginRun(NavMeshVisibleLog.Context.Editor, NavMeshConsoleMessages);

            try
            {
                Scene scene = Scene;
                if (scene == null)
                {
                    log("ERROR: no scene.");
                    NavMeshVisibleLog.Fail("no scene");
                    return;
                }

                NavMeshVisibleLog.Start(SafeName(scene), PreviewPrefabName(scene, NavMeshPrefabName),
                    BuildTag, true);

                log("run: build=" + BuildTag + " trigger=editor-button-async scene=" + SafeName(scene)
                    + " machine=" + Environment.MachineName
                    + " time=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    + " logFile=" + (NavMeshLog.CurrentFilePath ?? "(disabled)"));

                // Water handling is always active; the toggle picks cull (on) vs raise (off). Read on the
                // MAIN THREAD before the build is handed to the background worker (the worker never
                // touches the scene). Carried to the voxel settings.
                float waterLevel = 0f;
                string waterMode = "off";
                bool waterCull = NavMeshCullBelowWaterLevel;
                float waterReading = SafeWaterLevel(scene, log);
                if (!float.IsNegativeInfinity(waterReading))
                {
                    waterLevel = waterReading;
                    waterMode = waterCull ? "cull" : "raise";
                }
                else
                {
                    waterCull = false;
                }

                log("settings: stride=" + settings.SampleStride
                    + " maxSlope=" + F(settings.MaxSlopeDegrees) + "deg"
                    + " maxError=" + F(settings.MaxErrorMeters) + "m"
                    + " coarsest=" + F(settings.CoarsestSpacingMeters) + "m"
                    + " cullBelowHeight=" + settings.CullBelowHeight
                    + " minHeight=" + F(settings.MinHeight)
                    + " water=" + waterMode
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
                    + " gatePlateBlend=" + NavMeshVoxelGatePlateBlend
                    + " barriers=" + NavMeshVoxelBarriers
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
                    + " consoleMessages=" + NavMeshConsoleMessages
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
                NavMeshVisibleLog.Stage("collecting assets");

                // The playable area, read on the MAIN THREAD before anything is handed to a worker.
                // A null polygon makes every border stage a structural no-op, so a scene with no (or
                // fewer than three) probes builds exactly what it built before V3.59.
                int borderSoftProbes, borderHardProbes;
                TiledNavMeshBuilder.BorderPolygon border =
                    ReadBorderPolygon(scene, log, out borderSoftProbes, out borderHardProbes);

                // Phase 3: asset collision geometry is read on the main thread (scene access) and
                // carried as plain arrays to the background build.
                long assetsMs = 0;
                float[] assetVerts = null;
                int[] assetTris = null;
                bool[] assetForceWalkable = null;
                    bool[] assetLedgeExempt = null;
                    bool[] assetGate = null;
                    TiledNavMeshBuilder.BarrierWalls assetBarriers = null;
                if (NavMeshVoxelIncludeAssets)
                {
                    var assetsWatch = System.Diagnostics.Stopwatch.StartNew();
                    SceneCollisionGeometry.AssetTriangles assets =
                        SceneCollisionGeometry.Collect(scene, NavMeshVoxelIncludeDynamic, NavMeshVoxelIncludeFlora,
                            NavMeshVoxelAssetTrace, NavMeshVoxelSteppedProps, RunLog(log), NavMeshVoxelCellSize,
                            ResolvedRampClimb(NavMeshVoxelAgentClimbMeters, NavMeshVoxelCellSize, settings.MaxSlopeDegrees), NavMeshVoxelLedgeFilter,
                            NavMeshVoxelGateConnectors);
                    assetsWatch.Stop();
                    assetVerts = assets.VertsXyz;
                    assetTris = assets.Tris;
                    assetForceWalkable = assets.ForceWalkableTris;
                    assetLedgeExempt = assets.LedgeExemptTris;
                    assetGate = assets.GateTris;
                    assetBarriers = assets.Barriers;
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
                voxel.GatePlateBlend = NavMeshVoxelGatePlateBlend;
                voxel.SteepFaceMarginDegrees = NavMeshSteepFaceMarginDegrees;
                voxel.SteepFaceMinHeightClimbs = NavMeshSteepFaceMinHeightClimbs;
                voxel.CullBelowWaterLevel = waterCull;                  // Mode A (toggle on)
                voxel.RaiseBelowWaterLevel = waterMode == "raise";      // Mode B (toggle off)
                voxel.WaterLevelMeters = waterLevel;
                voxel.Barriers = NavMeshVoxelBarriers ? assetBarriers : null;

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
                req.AssetGate = assetGate;
                req.TerrainHoleMask = grid.HoleMask;
                req.Border = border;
                req.SoupCapture = NavMeshVoxelWriteSoupObj ? new VoxelNavMeshGenerator.SoupCapture() : null;
                req.AssetsMs = assetsMs;
                req.FacesBefore = facesBeforeRun;
                req.SampleMs = sampleWatch.ElapsedMilliseconds;
                req.PrefabNameOverride = NavMeshPrefabName;
                req.FolderOverride = NavMeshExportFolder;
                req.ClearExisting = NavMeshClearExistingFaces;
                req.ImportIntoScene = NavMeshImportIntoScene;
                req.Log = RunLog(log);
                req.RunWatch = runWatch;

                _asyncBuild = req;
                BeginBuildTickSampling();

                log("v2: async build started on a background thread; the editor stays responsive (progress lines follow).");
                NavMeshVisibleLog.Stage("building tiles");

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
                NavMeshVisibleLog.Fail("dispatch failed: " + ex.GetType().Name + ": " + Flat(ex.Message));
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
                    req.AssetLedgeExempt, req.AssetGate, req.TerrainHoleMask,
                    req.SoupCapture, req.Log, out buildMs, req.Border);
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

                // Flush anything the worker queued, so the console lines stay in order
                // (its last progress line must not appear after "writing bin").
                NavMeshVisibleLog.Pump();

                if (req.Error != null)
                {
                    log("v2: ERROR (background build): " + req.Error.GetType().Name + ": " + Flat(req.Error.Message));
                    log("v2: ERROR stack (background build): " + Flat(req.Error.StackTrace));
                    log("done: totalMs=" + req.RunWatch.ElapsedMilliseconds + " failed=true");
                    NavMeshVisibleLog.Fail("background build: " + req.Error.GetType().Name
                        + ": " + Flat(req.Error.Message));
                    return;
                }

                if (req.Mesh == null)
                {
                    log("v2: mesh build aborted by the invariant gate; bin NOT written and import NOT attempted.");
                    log("done: totalMs=" + req.RunWatch.ElapsedMilliseconds + " failed=true aborted=true");
                    NavMeshVisibleLog.Fail("the invariant gate rejected the mesh; nothing was written"
                        + " or imported (see the log for the offending counts)");
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

                if (!string.IsNullOrEmpty(result.FallbackBinPath))
                    log("fallback bin written: " + result.FallbackBinPath);

                if (!string.IsNullOrEmpty(result.ObjPath))
                    log("OBJ written: " + result.ObjPath);

                if (result.ImportAttempted)
                {
                    log("faces imported into scene as '" + result.PrefabName + "' (static).");
                    LogImportCheck(log, req.FacesBefore, facesAfterRun, req.ClearExisting);
                }
                else
                {
                    log("import skipped" + (string.IsNullOrEmpty(result.ImportSkipReason)
                        ? "; import '" + result.PrefabName
                          + "' manually from the editor's navmesh prefab panel."
                        : "; import '" + result.PrefabName + "': " + result.ImportSkipReason));
                    NavMeshVisibleLog.Skip(ImportSkipNotice(result));
                }

                log("done: totalMs=" + req.RunWatch.ElapsedMilliseconds
                    + " bin=" + result.BinByteCount
                    + " V=" + result.VertexCount
                    + " E=" + result.EdgeCount
                    + " F=" + result.FaceCount);

                // Always the last console line of the run, in BOTH modes: the async path
                // must never leave the "is it still working?" question open.
                NavMeshVisibleLog.Pump();
                NavMeshVisibleLog.Finish("'" + result.PrefabName + "' V=" + result.VertexCount
                    + " F=" + result.FaceCount + " E=" + result.EdgeCount
                    + " in " + Secs(req.RunWatch.ElapsedMilliseconds) + "s -> " + result.BinPath
                    + ImportOutcome(result, req.FacesBefore, facesAfterRun));

                if (result.ImportAttempted)
                    BeginPostImportTickSampling();
            }
            catch (Exception ex)
            {
                _periodActive = false;
                log("ERROR: " + ex.GetType().Name + ": " + Flat(ex.Message));
                log("ERROR stack: " + Flat(ex.StackTrace));
                log("done: totalMs=" + req.RunWatch.ElapsedMilliseconds + " failed=true");
                NavMeshVisibleLog.Fail("finish failed: " + ex.GetType().Name + ": " + Flat(ex.Message));
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

        // The scene's water level: the value the renderer/gameplay use (SpawnPathData's
        // SnapToWaterLevel, MissionScreen.UpdateCamera). Wrapped like the other scene reads, because the
        // editor can be an unusual context and a scene that cannot answer must not fail the export: it
        // logs and returns -infinity, which disables the water handling for this run.
        private static float SafeWaterLevel(Scene scene, System.Action<string> log)
        {
            try
            {
                float level = scene.GetWaterLevel();
                if (log != null)
                    log("water: level=" + F(level) + "m (scene.GetWaterLevel)");
                return level;
            }
            catch (Exception ex)
            {
                if (log != null)
                    log("water: scene.GetWaterLevel failed (" + Flat(ex.GetType().Name) + ": "
                        + Flat(ex.Message) + "); water cull disabled this run");
                return float.NegativeInfinity;
            }
        }

        // Wraps the log file writer so the same run also feeds the console mirror.
        // The FILE gets the original, untouched line first, so it stays exactly as
        // verbose as it was; NavMeshVisibleLog only reads the line to decide whether
        // it is worth showing.
        private static System.Action<string> RunLog(System.Action<string> log)
        {
            if (log == null)
                return null;

            return delegate (string line)
            {
                log(line);
                NavMeshVisibleLog.Mirror(line);
            };
        }

        // The editor component's buttons pass "editor-button" / "editor-button-async";
        // the in-game hotkey passes "mission-hotkey (~)". Only the former may use the
        // async path, and only the latter should message the player.
        private static bool IsEditorTrigger(string trigger)
        {
            return !string.IsNullOrEmpty(trigger)
                && trigger.StartsWith("editor", StringComparison.OrdinalIgnoreCase);
        }

        // A non-throwing preview of the prefab name for the START console line. The
        // real name is resolved (and validated) later by ResolvePrefabName, which may
        // throw - that must not stop the run before it has said anything.
        private static string PreviewPrefabName(Scene scene, string overrideName)
        {
            if (!string.IsNullOrEmpty(overrideName))
                return overrideName;

            string sceneName = SafeName(scene);
            if (sceneName == "?")
                return "?";

            try
            {
                return NavMeshPrefabIndex.DeriveBoundedName(sceneName, "_navmesh");
            }
            catch (Exception)
            {
                return sceneName + "_navmesh";
            }
        }

        // One decimal of seconds from a millisecond span, for the console lines.
        private static string Secs(long ms)
        {
            return (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);
        }

        // Console wording for the "the .bin is written but the import did not
        // happen" case. Deliberately parallel to - and derived from - the log
        // FILE's own "import skipped ..." line a few lines above it, so the two
        // never drift apart; the file line itself is untouched.
        private static string ImportSkipNotice(TerrainNavMeshGenerator.ExportResult result)
        {
            if (result == null)
                return "(no result)";

            if (string.IsNullOrEmpty(result.ImportSkipReason))
                return "import '" + result.PrefabName
                    + "' manually from the editor's navmesh prefab panel.";

            return "import '" + result.PrefabName + "': " + result.ImportSkipReason;
        }

        // Short tail for the DONE line: did the faces actually land in the scene?
        private static string ImportOutcome(TerrainNavMeshGenerator.ExportResult result,
            int facesBeforeRun, int facesAfterRun)
        {
            if (result == null)
                return "";

            if (!result.ImportAttempted)
                return " (not imported into the scene)";

            if (facesAfterRun < 0)
                return " (imported; face count unavailable)";

            if (facesBeforeRun >= 0 && facesAfterRun == facesBeforeRun)
                return " (imported, but the scene face count did not change - see the log)";

            return " (imported)";
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
