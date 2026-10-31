using System;
using TaleWorlds.Engine;
using TaleWorlds.InputSystem;
using TaleWorlds.MountAndBlade;

namespace BetterBannerlordNavmeshing
{
    // EXPERIMENTAL in-game test path (secondary to the editor component).
    //
    // Press the hotkey (~ / Tilde) during a mission to read the live scene
    // terrain and run the same pipeline as the editor button. The heavy
    // read/build is deferred to the NEXT tick so the input call never blocks.
    //
    // Singleplayer only for now: in vanilla this whole path is
    // server-authoritative (MissionObject.OnInit guards on
    // GameNetwork.IsClientOrReplay) and that guard is NOT added here.
    public class SceneNavMeshMissionLogic : MissionLogic
    {
        public bool Enabled = true;

        private bool _pendingExport;

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);

            // Process work queued on a previous tick first, so the export never
            // runs inside the same input-handling call that requested it.
            if (_pendingExport)
            {
                _pendingExport = false;
                RunExport();
                return;
            }

            Mission mission = Mission.Current;
            if (Enabled && mission != null && Input.IsKeyPressed(InputKey.Tilde)
                && mission.CurrentState == Mission.State.Continuing)
            {
                _pendingExport = true;
            }
        }

        private void RunExport()
        {
            Mission mission = Mission.Current;
            if (mission == null)
            {
                Console.WriteLine("NavMesh: ERROR: no mission.");
                return;
            }

            Scene scene = mission.Scene;
            if (scene == null)
            {
                Console.WriteLine("NavMesh: ERROR: no scene.");
                return;
            }

            var settings = new TerrainNavMeshBuilder.Settings();
            settings.SampleStride = NavMeshPipelineDefaults.SampleStride;
            settings.MaxErrorMeters = NavMeshPipelineDefaults.MaxErrorMeters;
            settings.CoarsestSpacingMeters = NavMeshPipelineDefaults.CoarsestSpacingMeters;
            settings.MaxSlopeDegrees = NavMeshPipelineDefaults.MaxSlopeDegrees;
            settings.HeightOffsetMeters = NavMeshPipelineDefaults.HeightOffsetMeters;
            settings.CullBelowHeight = false;
            settings.MinHeight = 0f;
            settings.WriteObj = false;

            SceneNavMeshExporter.RunPipeline(scene, "mission-hotkey (~)", settings, "", "", true, true, true, "");
        }
    }
}
