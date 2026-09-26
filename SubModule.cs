using TaleWorlds.MountAndBlade;

namespace BetterBannerlordNavmeshing
{
    // Module entry point. The editor component needs no registration; this hook
    // only adds the experimental in-game export behavior.
    public class SubModule : MBSubModuleBase
    {
        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            // Navmesh work is server-authoritative in vanilla (MissionObject.Init
            // guards with GameNetwork.IsClientOrReplay); keep the dev hook off
            // clients/replays.
            if (!GameNetwork.IsClientOrReplay)
                mission.AddMissionBehavior(new SceneNavMeshMissionLogic());
        }
    }
}
