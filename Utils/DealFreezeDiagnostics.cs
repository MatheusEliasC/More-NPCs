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

                // Deal-detection view: proves whether the mod can see the pending contract at all.
                var dealState = DescribeDeal(npc);

                MelonLogger.Msg($"[FreezeDiag] {id}: {wrapperState} | {agentState} | {dealState}");
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[FreezeDiag] {id}: log failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Describe whether the mod can detect a pending deal on this customer (reflection on the real
        /// runtime Customer type). Proves the KnockOut trigger's inputs.
        /// </summary>
        private static string DescribeDeal(NPC npc)
        {
            try
            {
                var go = npc.gameObject;
                if (go == null) return "deal: NO_GAMEOBJECT";

                Component? customer = null;
                foreach (var c in go.GetComponents<Component>() ?? Array.Empty<Component>())
                    if (IsCustomerType(c)) { customer = c; break; }
                if (customer == null)
                    foreach (var c in go.GetComponentsInChildren<Component>(true) ?? Array.Empty<Component>())
                        if (IsCustomerType(c)) { customer = c; break; }
                if (customer == null) return "deal: CUSTOMER_COMPONENT_NOT_FOUND";

                var ct = customer.GetType();
                string contract = "?", awaiting = "?", dealTime = "?", atLoc = "?";
                try
                {
                    var p = ct.GetProperty("CurrentContract", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    contract = (p != null) ? (p.GetValue(customer) == null ? "null" : "SET") : "no-prop";
                }
                catch (Exception e) { contract = "err:" + e.Message; }
                try
                {
                    var p = ct.GetProperty("IsAwaitingDelivery", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    awaiting = (p != null) ? p.GetValue(customer)?.ToString() ?? "?" : "no-prop";
                }
                catch (Exception e) { awaiting = "err:" + e.Message; }
                try
                {
                    var m = ct.GetMethod("IsDealTime", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    dealTime = (m != null) ? m.Invoke(customer, null)?.ToString() ?? "?" : "no-method";
                }
                catch (Exception e) { dealTime = "err:" + e.Message; }
                try
                {
                    var m = ct.GetMethod("IsAtDealLocation", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    atLoc = (m != null) ? m.Invoke(customer, null)?.ToString() ?? "?" : "no-method";
                }
                catch (Exception e) { atLoc = "err:" + e.Message; }

                return $"deal: type={ct.Name} contract={contract} awaiting={awaiting} dealTime={dealTime} atLoc={atLoc}";
            }
            catch (Exception ex)
            {
                return $"deal: ERR({ex.Message})";
            }
        }

        private static bool IsCustomerType(Component c)
        {
            if (c == null) return false;
            var t = c.GetType();
            return string.Equals(t.Name, "Customer", StringComparison.Ordinal)
                && t.FullName != null
                && t.FullName.IndexOf("Economy.Customer", StringComparison.Ordinal) >= 0;
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
