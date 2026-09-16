using System;
using System.Reflection;
using MelonLoader;
using S1API.Entities;
using UnityEngine;

namespace MoreNPCs.Utils
{
    /// <summary>
    /// Re-enables the daily schedule of this mod's custom NPCs after a save is loaded.
    ///
    /// <para>Each mod NPC calls <c>Schedule.Enable()</c> exactly once, inside its <c>OnCreated()</c>
    /// override, which only runs on first spawn. When a save is reloaded mid-game the game/S1API does
    /// not reliably re-arm custom-NPC schedules, so they reset to their spawn point and never walk to
    /// their next scheduled action (missing timed deals). This is the regression that the removed
    /// <c>NPCScheduleResumeWatcher</c> used to compensate for.</para>
    ///
    /// <para>Unlike that watcher (which polled every tick and threw <see cref="NullReferenceException"/>s),
    /// this runs a SINGLE guarded pass per load: it is armed from the mod save's <c>OnLoaded</c> and
    /// executed once <see cref="NPC.CustomNpcsReady"/> becomes true. Each NPC access is wrapped in
    /// try/catch, and it only touches NPCs whose concrete type lives in the <c>MoreNPCs.NPCs</c>
    /// namespace, so vanilla NPCs are never affected.</para>
    /// </summary>
    internal static class ScheduleResumeOnLoad
    {
        private const string ModNpcNamespace = "MoreNPCs.NPCs";

        private static bool _pending;
        private static float _readyDeadline;

        /// <summary>Arm a one-shot schedule resume. Called from the mod save's OnLoaded.</summary>
        public static void RequestResume()
        {
            _pending = true;
            // Give the spawn/load pipeline a window to finish bringing custom NPCs online.
            _readyDeadline = Time.time + 30f;
        }

        /// <summary>Pump from Core.OnUpdate. Runs the resume pass once NPCs are ready, then disarms.</summary>
        public static void Update()
        {
            if (!_pending) return;

            // Wait until custom NPCs are online. Bail out after the deadline so we never spin forever.
            if (!NPC.CustomNpcsReady)
            {
                if (Time.time >= _readyDeadline) _pending = false;
                return;
            }

            _pending = false;
            ResumeAll();
        }

        private static void ResumeAll()
        {
            try
            {
                var all = NPC.All;
                if (all == null) return;

                int resumed = 0;
                foreach (var npc in all)
                {
                    if (npc == null) continue;
                    // Only our own NPCs: identify by concrete type namespace, never vanilla NPCs.
                    var type = npc.GetType();
                    if (type == null || !string.Equals(type.Namespace, ModNpcNamespace, StringComparison.Ordinal))
                        continue;

                    if (TryEnableSchedule(npc)) resumed++;
                }

                if (resumed > 0)
                    MelonLogger.Msg($"[ScheduleResumeOnLoad] Re-armed schedules for {resumed} mod NPC(s) after load.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[ScheduleResumeOnLoad] Resume pass failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Re-enable one NPC's schedule, mirroring what OnCreated does at spawn. Reflection + try/catch
        /// so a single bad NPC (movement/native not ready) can never throw into the shared update loop.
        /// </summary>
        private static bool TryEnableSchedule(NPC npc)
        {
            try
            {
                // Access the Schedule property defensively (its getter can touch native state).
                var scheduleProp = npc.GetType().GetProperty("Schedule", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (scheduleProp == null) return false;

                object? schedule;
                try { schedule = scheduleProp.GetValue(npc); }
                catch { return false; }
                if (schedule == null) return false;

                var enable = schedule.GetType().GetMethod("Enable", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (enable == null) return false;

                enable.Invoke(schedule, null);
                return true;
            }
            catch
            {
                // Never let a single NPC break the pass.
                return false;
            }
        }
    }
}
