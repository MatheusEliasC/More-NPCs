using S1API.Entities;
using MoreNPCs.Utils;
using UnityEngine;

namespace MoreNPCs.Supervisor
{
    /// <summary>Supervisor home position and go-home helper.</summary>
    public static class SupervisorIdleController
    {
        public static readonly Vector3 HomePosition = SupervisorConfig.DefaultSpawnPosition;
        private static float ArriveThreshold =>
            !MoreNPCsPreferences.Registered ? 2f : MoreNPCsPreferences.SupervisorIdle_ArriveThreshold.Value;

        public static void GoHome(NPC npc)
        {
            var movement = NpcSafe.Movement(npc);
            if (movement == null) return;
            var home = NPCIdleLocations.GetSupervisorIdlePosition(npc.ID, HomePosition);
            var threshold = ArriveThreshold;
            if (Vector3.Distance(movement.FootPosition, home) < threshold) return;
            movement.SetDestination(home);
        }

        public static bool IsAtHome(NPC npc)
        {
            var movement = NpcSafe.Movement(npc);
            if (movement == null) return false;
            var threshold = ArriveThreshold;
            return Vector3.Distance(movement.FootPosition, NPCIdleLocations.GetSupervisorIdlePosition(npc.ID, HomePosition)) < threshold;
        }
    }
}
