using System;
using System.Reflection;
using MelonLoader;
using S1API.Entities;
using UnityEngine;
using UnityEngine.AI;

namespace MoreNPCs.Utils
{
    /// <summary>
    /// TEMPORARY diagnostic. Logs the movement / NavMeshAgent state of two specific mod customers
    /// (PiperSloan, LilaPark) every few seconds so we can see WHY they freeze in place (the ragdoll
    /// from a baseball bat un-freezes them, which points at a stuck / off-navmesh NavMeshAgent).
    ///
    /// <para>Scoped to just two NPC ids, guarded by try/catch, throttled to a few seconds — negligible
    /// cost, cannot crash. Remove once the freeze root cause is confirmed.</para>
    /// </summary>
    internal static class DealFreezeDiagnostics
    {
        private static readonly string[] WatchedIds = { "piper_sloan", "lila_park" };
        private const float IntervalSeconds = 3f;

        private static float _nextTime;

        public static void Update()
        {
            if (Time.time < _nextTime) return;
            _nextTime = Time.time + IntervalSeconds;

            if (!NPC.CustomNpcsReady) return;

            try
            {
                var all = NPC.All;
                if (all == null) return;

                foreach (var npc in all)
                {
                    if (npc == null) continue;
                    var id = npc.ID;
                    if (string.IsNullOrEmpty(id)) continue;

                    bool watched = false;
                    foreach (var w in WatchedIds)
                        if (string.Equals(id, w, StringComparison.OrdinalIgnoreCase)) { watched = true; break; }
                    if (!watched) continue;

                    LogOne(npc, id);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[FreezeDiag] pass failed: {ex.Message}");
            }
        }

        private static void LogOne(NPC npc, string id)
        {
            try
            {
                // S1API wrapper view (safe accessor: the getter can throw on a half-initialized NPC).
                var mv = NpcSafe.Movement(npc);
                string wrapperState;
                if (mv == null)
                {
                    wrapperState = "wrapper=NULL";
                }
                else
                {
                    string moving = "?", dest = "?", foot = "?";
                    try { moving = mv.IsMoving.ToString(); } catch { }
                    try { dest = mv.CurrentDestination.ToString("F1"); } catch { }
                    try { foot = mv.FootPosition.ToString("F1"); } catch { }
                    wrapperState = $"IsMoving={moving} dest={dest} foot={foot}";
                }

                // Native NavMeshAgent view (the actual thing that gets stuck / goes off-mesh).
                var agentState = DescribeAgent(npc);

                MelonLogger.Msg($"[FreezeDiag] {id}: {wrapperState} | {agentState}");
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[FreezeDiag] {id}: log failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Find the NavMeshAgent on the NPC's game object hierarchy and describe its stuck-relevant state.
        /// </summary>
        private static string DescribeAgent(NPC npc)
        {
            try
            {
                var go = npc.gameObject;
                if (go == null) return "agent=NO_GAMEOBJECT";

                var agent = go.GetComponentInChildren<NavMeshAgent>(true);
                if (agent == null) return "agent=NONE";

                bool onMesh = false, stopped = false, hasPath = false, pending = false, active = false, enabled = false;
                string status = "?";
                float vel = -1f, remaining = -1f;
                try { active = agent.gameObject.activeInHierarchy; } catch { }
                try { enabled = agent.enabled; } catch { }
                try { onMesh = agent.isOnNavMesh; } catch { }
                try { stopped = agent.isStopped; } catch { }
                try { hasPath = agent.hasPath; } catch { }
                try { pending = agent.pathPending; } catch { }
                try { status = agent.pathStatus.ToString(); } catch { }
                try { vel = agent.velocity.magnitude; } catch { }
                try { remaining = agent.remainingDistance; } catch { }

                return $"agent: active={active} enabled={enabled} onNavMesh={onMesh} isStopped={stopped} " +
                       $"hasPath={hasPath} pending={pending} status={status} vel={vel:F2} remaining={remaining:F1}";
            }
            catch (Exception ex)
            {
                return $"agent=ERR({ex.Message})";
            }
        }
    }
}
