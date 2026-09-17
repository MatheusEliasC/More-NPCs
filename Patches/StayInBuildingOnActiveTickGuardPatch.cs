using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using MoreNPCs.Utils;

namespace MoreNPCs.Patches
{
    /// <summary>
    /// Suppresses <see cref="NullReferenceException"/> thrown inside the game's
    /// <c>NPCEvent_StayInBuilding.OnActiveTick()</c>.
    ///
    /// WHY: the mod fabricates custom <c>NPCEnterableBuilding</c>s at runtime (Utils/BuildingSetup.cs).
    /// A game update added members to NPCEnterableBuilding (e.g. Bounds / collider / Volume / EntryPoint) that
    /// OnActiveTick now dereferences every tick. The fabricated buildings don't populate those, so OnActiveTick
    /// throws an NRE — once per tick, per NPC in a StayInBuilding block — flooding the log and freezing the game
    /// (it also disrupts vanilla NPC schedule processing).
    ///
    /// This finalizer swallows only that NRE so the tick fails gracefully instead of crashing. Vanilla buildings
    /// (which have valid Bounds) are unaffected because they don't throw. This is a stopgap: the proper fix is to
    /// fully initialize the custom buildings' Bounds/collider in BuildingSetup once their exact shape is confirmed.
    /// </summary>
    [HarmonyPatch]
    internal static class StayInBuildingOnActiveTickGuardPatch
    {
        private static MethodBase? _target;
        // Throttle the log so the suppression itself doesn't spam.
        private static float _nextLogTime;

        private static bool Prepare()
        {
            var type = Il2CppTypeHelper.ResolveGameType("ScheduleOne.NPCs.Schedules.NPCEvent_StayInBuilding");
            if (type == null)
            {
                MelonLogger.Msg("[MoreNPCs] StayInBuilding guard skipped: NPCEvent_StayInBuilding not found.");
                return false;
            }
            try
            {
                _target = type.GetMethod("OnActiveTick", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            }
            catch { _target = null; }
            if (_target == null)
                MelonLogger.Msg("[MoreNPCs] StayInBuilding guard skipped: NPCEvent_StayInBuilding.OnActiveTick() not found.");
            return _target != null;
        }

        private static MethodBase TargetMethod() => _target!;

        [HarmonyFinalizer]
        private static Exception? Finalizer(Exception? __exception)
        {
            if (__exception == null) return null;

            // Only swallow the NRE we expect from unpopulated custom-building members; rethrow anything else.
            if (__exception is NullReferenceException ||
                __exception.GetType().Name.IndexOf("NullReference", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (UnityEngine.Time.time >= _nextLogTime)
                {
                    _nextLogTime = UnityEngine.Time.time + 30f;
                    MelonLogger.Msg("[MoreNPCs] Suppressed NPCEvent_StayInBuilding.OnActiveTick NRE (custom building lacks new Bounds/collider members). Throttled 30s.");
                }
                return null; // swallow
            }

            return __exception; // let unexpected exceptions propagate
        }
    }
}
