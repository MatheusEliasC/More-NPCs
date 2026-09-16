using System;
using System.Collections.Generic;
using MelonLoader;
using S1API.Entities;
using UnityEngine;
#if IL2CPP
using GameCustomer = Il2CppScheduleOne.Economy.Customer;
using GameNPCMovement = Il2CppScheduleOne.NPCs.NPCMovement;
using Il2CppInterop.Runtime;
#endif

namespace MoreNPCs.Utils
{
    /// <summary>
    /// Unfreezes mod customers that have an arranged deal but stand idle instead of walking to it.
    ///
    /// <para>Confirmed: the frozen customer has an active <c>CurrentContract</c>, and once we intervene her
    /// NavMeshAgent is <c>enabled=False / onNavMesh=False / PathInvalid</c> — the agent has fallen off the
    /// navmesh, which is why she never gets a path. The clean fix (from the game's own NPCMovement API) is
    /// <c>NPCMovement.WarpToNavMesh()</c> (+ a NavMeshAgent re-enable), which re-seats the agent on the mesh
    /// so the game can drive her again — no knockout, no ragdoll.</para>
    ///
    /// <para>We previously used <c>NPC.KnockOut()</c>, but the game's KnockOut has NO auto-recovery (only
    /// <c>Revive()</c> gets them up), so customers stayed down and got knocked out repeatedly. Replaced with
    /// the movement re-seat. Runs every 2s incl. right after a save load, per-NPC cooldown to avoid churn.</para>
    /// </summary>
    internal sealed class CustomerDealAttendanceNudger
    {
        private const string ModNpcNamespace = "MoreNPCs.NPCs";
        private const float ScanIntervalSeconds = 2f;
        private const float PerNpcCooldownSeconds = 8f;
        private const float IdleConfirmSeconds = 6f;

        private float _nextScanTime;
        private readonly Dictionary<string, float> _lastFixByNpcId = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> _idleSinceByNpcId = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _lastDetectLog = new Dictionary<string, string>(StringComparer.Ordinal);

        public void Update()
        {
#if IL2CPP
            if (Time.time < _nextScanTime) return;
            _nextScanTime = Time.time + ScanIntervalSeconds;

            if (!NPC.CustomNpcsReady) return;

            List<NPC>? all = null;
            try { all = NPC.All; } catch { }
            if (all == null) return;

            foreach (var npc in all)
            {
                try
                {
                    if (npc == null) continue;
                    var t = npc.GetType();
                    if (t == null || !string.Equals(t.Namespace, ModNpcNamespace, StringComparison.Ordinal)) continue;
                    if (!npc.IsCustomer) continue;
                    Evaluate(npc);
                }
                catch { /* never let one NPC break the pass */ }
            }
#endif
        }

#if IL2CPP
        private void Evaluate(NPC npc)
        {
            var id = npc.ID;
            if (string.IsNullOrEmpty(id)) return;

            if (_lastFixByNpcId.TryGetValue(id, out var lastFix) && Time.time - lastFix < PerNpcCooldownSeconds)
                return;

            var customer = GetCustomer(npc);
            bool hasContract = false;
            if (customer != null) { try { hasContract = customer.CurrentContract != null; } catch { } }

            var state = customer == null ? "no-customer" : (hasContract ? "contract=SET" : "contract=null");
            if (!_lastDetectLog.TryGetValue(id, out var prev) || prev != state)
            {
                _lastDetectLog[id] = state;
                MelonLogger.Msg($"[DealNudge] {id}: {state}");
            }

            if (!hasContract)
            {
                _idleSinceByNpcId.Remove(id);
                return;
            }

            bool moving = false;
            var mv = NpcSafe.Movement(npc);
            if (mv != null) { try { moving = mv.IsMoving; } catch { } }
            if (moving)
            {
                _idleSinceByNpcId.Remove(id);
                return;
            }

            if (!_idleSinceByNpcId.TryGetValue(id, out var idleSince))
            {
                _idleSinceByNpcId[id] = Time.time;
                return;
            }
            if (Time.time - idleSince < IdleConfirmSeconds) return;

            // Re-seat the NavMeshAgent on the mesh so the game can path her to the deal — no knockout.
            if (ReseatMovement(npc))
            {
                _lastFixByNpcId[id] = Time.time;
                _idleSinceByNpcId.Remove(id);
                MelonLogger.Msg($"[DealNudge] {id}: active contract + idle {IdleConfirmSeconds:F0}s -> re-seated agent on navmesh.");
            }
        }

        /// <summary>
        /// Get the game NPCMovement component and re-seat the agent on the navmesh (WarpToNavMesh + agent
        /// re-enable). This clears the off-mesh/disabled state that leaves the customer frozen.
        /// </summary>
        private static bool ReseatMovement(NPC npc)
        {
            GameNPCMovement? gm = GetGameMovement(npc);
            if (gm == null) return false;
            try
            {
                // Ensure the agent is on and snapped to the mesh, then clear any stale path.
                try { gm.SetAgentEnabled(true); } catch { }
                gm.WarpToNavMesh();
                try { gm.SetAgentEnabled(true); } catch { }
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[DealNudge] {npc.ID}: reseat failed: {ex.Message}");
                return false;
            }
        }

        private static GameNPCMovement? GetGameMovement(NPC npc)
        {
            GameObject? go = null;
            try { go = npc.gameObject; } catch { }
            if (go == null) return null;
            try
            {
                var c = go.GetComponent<GameNPCMovement>();
                if (c != null) return c;
                return go.GetComponentInChildren<GameNPCMovement>(true);
            }
            catch { return null; }
        }

        private static GameCustomer? GetCustomer(NPC npc)
        {
            GameObject? go = null;
            try { go = npc.gameObject; } catch { }
            if (go == null) return null;

            try { var c = go.GetComponent<GameCustomer>(); if (c != null) return c; } catch { }
            try { var c = go.GetComponentInChildren<GameCustomer>(true); if (c != null) return c; } catch { }

            try
            {
                foreach (var comp in go.GetComponents<Component>() ?? Array.Empty<Component>())
                {
                    if (comp == null) continue;
                    try
                    {
                        var full = ((Il2CppSystem.Object)(object)comp).GetIl2CppType()?.FullName;
                        if (full == null || full.IndexOf("Economy.Customer", StringComparison.Ordinal) < 0) continue;
                        var cast = comp.TryCast<GameCustomer>();
                        if (cast != null) return cast;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }
#endif
    }
}
