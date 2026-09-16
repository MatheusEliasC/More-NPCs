using System;
using System.Collections.Generic;
using MelonLoader;
using S1API.Entities;
using UnityEngine;
#if IL2CPP
using GameCustomer = Il2CppScheduleOne.Economy.Customer;
#endif

namespace MoreNPCs.Utils
{
    /// <summary>
    /// Unfreezes mod customers that have an active arranged deal but stand idle at their spot instead of
    /// walking to the meet point (confirmed via runtime logs: healthy NavMeshAgent, PathComplete, but
    /// hasPath=False and no destination — the game's attend-deal driver never fires for these
    /// runtime-instanced NPCs). A baseball-bat ragdoll reliably resets that state and they then walk to
    /// the deal and complete it.
    ///
    /// <para>The diagnostic proved the game <c>ScheduleOne.Economy.Customer</c> component IS present on the
    /// NPC root — earlier detection failed only because <c>GetComponents&lt;Component&gt;().GetType()</c>
    /// returns the base <c>UnityEngine.Component</c> for IL2CPP components. We now grab it with the typed
    /// <c>GetComponent&lt;Il2CppScheduleOne.Economy.Customer&gt;()</c> (same pattern the mod's patches use)
    /// and read its members directly — no fragile reflection.</para>
    ///
    /// <para>For any mod customer with an active, in-window, awaited contract that is standing idle we call
    /// the official S1API <see cref="NPC.KnockOut"/> (the same reset the bat does); on recovery the game
    /// re-drives it to the deal. Runs every 2s incl. right after a save load, per-NPC 20s cooldown.</para>
    /// </summary>
    internal sealed class CustomerDealAttendanceNudger
    {
        private const string ModNpcNamespace = "MoreNPCs.NPCs";
        private const float ScanIntervalSeconds = 2f;
        private const float PerNpcCooldownSeconds = 20f;
        private const float IdleConfirmSeconds = 4f;

        private float _nextScanTime;
        private readonly Dictionary<string, float> _lastKnockByNpcId = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> _idleSinceByNpcId = new Dictionary<string, float>(StringComparer.Ordinal);

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
                    var type = npc.GetType();
                    if (type == null || !string.Equals(type.Namespace, ModNpcNamespace, StringComparison.Ordinal))
                        continue;
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

            if (_lastKnockByNpcId.TryGetValue(id, out var lastKnock) && Time.time - lastKnock < PerNpcCooldownSeconds)
                return;

            if (!HasPendingDeal(npc))
            {
                _idleSinceByNpcId.Remove(id);
                return;
            }

            // If already moving, she's heading to the deal — clear idle timer.
            bool moving = false;
            var mv = NpcSafe.Movement(npc);
            if (mv != null) { try { moving = mv.IsMoving; } catch { } }
            if (moving)
            {
                _idleSinceByNpcId.Remove(id);
                return;
            }

            try { if (npc.IsKnockedOut) return; } catch { }

            if (!_idleSinceByNpcId.TryGetValue(id, out var idleSince))
            {
                _idleSinceByNpcId[id] = Time.time;
                return;
            }
            if (Time.time - idleSince < IdleConfirmSeconds) return;

            try
            {
                npc.KnockOut();
                _lastKnockByNpcId[id] = Time.time;
                _idleSinceByNpcId.Remove(id);
                MelonLogger.Msg($"[DealNudge] {id}: idle with active deal -> KnockOut() to force attendance.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[DealNudge] {id}: KnockOut failed: {ex.Message}");
            }
        }

        private static bool HasPendingDeal(NPC npc)
        {
            GameCustomer? customer = GetCustomer(npc);
            if (customer == null) return false;

            try
            {
                // Must have an active accepted contract.
                if (customer.CurrentContract == null) return false;
                // Must be awaiting the handover.
                if (!customer.IsAwaitingDelivery) return false;
                // Must be inside the deal window and not already at the spot.
                if (!customer.IsDealTime()) return false;
                if (customer.IsAtDealLocation()) return false;
                return true;
            }
            catch { return false; }
        }

        private static GameCustomer? GetCustomer(NPC npc)
        {
            try
            {
                var go = npc.gameObject;
                if (go == null) return null;
                var c = go.GetComponent<GameCustomer>();
                if (c != null) return c;
                return go.GetComponentInChildren<GameCustomer>(true);
            }
            catch { return null; }
        }
#endif
    }
}
