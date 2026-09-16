using System;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;
using S1API.Entities;
using UnityEngine;

namespace MoreNPCs.Utils
{
    /// <summary>
    /// Unfreezes mod customers that have an arranged deal but stand idle instead of walking to it.
    ///
    /// <para>Runtime logs proved: the customer has an active contract (<c>CurrentContract</c> is SET) yet
    /// stands with a healthy-but-idle NavMeshAgent (onNavMesh, PathComplete, no destination). Critically,
    /// while frozen the game reports <c>IsAwaitingDelivery=False</c> and <c>IsDealTime=False</c> — she never
    /// even enters the deal-active state, so gating on those flags never triggers. A baseball-bat ragdoll
    /// resets the stuck state and she then proceeds through the deal normally.</para>
    ///
    /// <para>So the trigger is simply: a mod customer that HAS an active contract and has been standing
    /// still for a while (and isn't already knocked out) gets the official S1API <see cref="NPC.KnockOut"/>
    /// — the same reset the bat does. Runs every 2s incl. right after a save load, with a per-NPC cooldown.
    /// The Customer component is found via reflection on the component's REAL runtime type (typed
    /// GetComponent proved unreliable for these IL2CPP components).</para>
    /// </summary>
    internal sealed class CustomerDealAttendanceNudger
    {
        private const string ModNpcNamespace = "MoreNPCs.NPCs";
        private const float ScanIntervalSeconds = 2f;
        private const float PerNpcCooldownSeconds = 20f;
        // Stand still this long with an active contract before we ragdoll-reset (avoids hitting someone
        // who is only briefly paused between path legs).
        private const float IdleConfirmSeconds = 6f;

        private float _nextScanTime;
        private readonly Dictionary<string, float> _lastKnockByNpcId = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> _idleSinceByNpcId = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, Component?> _customerByNpcId = new Dictionary<string, Component?>(StringComparer.Ordinal);

        public void Update()
        {
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
        }

        private void Evaluate(NPC npc)
        {
            var id = npc.ID;
            if (string.IsNullOrEmpty(id)) return;

            if (_lastKnockByNpcId.TryGetValue(id, out var lastKnock) && Time.time - lastKnock < PerNpcCooldownSeconds)
                return;

            // Has an active arranged contract? (CurrentContract SET). We do NOT gate on IsAwaitingDelivery /
            // IsDealTime because the logs show a frozen customer never reaches those states.
            if (!HasActiveContract(npc, id))
            {
                _idleSinceByNpcId.Remove(id);
                return;
            }

            // If she is already moving, she's on her way — clear idle timer, no action.
            bool moving = false;
            var mv = NpcSafe.Movement(npc);
            if (mv != null) { try { moving = mv.IsMoving; } catch { } }
            if (moving)
            {
                _idleSinceByNpcId.Remove(id);
                return;
            }

            // Don't re-hit someone mid-ragdoll.
            try { if (npc.IsKnockedOut) return; } catch { }

            // Require sustained idle.
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
                MelonLogger.Msg($"[DealNudge] {id}: active contract + idle {IdleConfirmSeconds:F0}s -> KnockOut() to force attendance.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[DealNudge] {id}: KnockOut failed: {ex.Message}");
            }
        }

        /// <summary>True if the customer has a non-null CurrentContract. Reflection on the real runtime type.</summary>
        private bool HasActiveContract(NPC npc, string id)
        {
            var customer = GetCustomerComponent(npc, id);
            if (customer == null) return false;
            try
            {
                var ct = customer.GetType();
                var p = ct.GetProperty("CurrentContract", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p == null) return false;
                return p.GetValue(customer) != null;
            }
            catch { return false; }
        }

        private Component? GetCustomerComponent(NPC npc, string id)
        {
            if (_customerByNpcId.TryGetValue(id, out var cached) && cached != null)
                return cached;
            try
            {
                var go = npc.gameObject;
                if (go == null) return null;
                Component? found = null;
                foreach (var c in go.GetComponents<Component>() ?? Array.Empty<Component>())
                    if (IsCustomerType(c)) { found = c; break; }
                _customerByNpcId[id] = found;
                return found;
            }
            catch { return null; }
        }

        /// <summary>
        /// Match the game Customer component by its REAL IL2CPP type name. The managed GetType() returns the
        /// base UnityEngine.Component for these components, so we ask the IL2CPP runtime for the real type.
        /// </summary>
        private static bool IsCustomerType(Component c)
        {
            if (c == null) return false;
            var t = c.GetType();
            if (string.Equals(t.Name, "Customer", StringComparison.Ordinal)
                && t.FullName != null && t.FullName.IndexOf("Economy.Customer", StringComparison.Ordinal) >= 0)
                return true;
#if IL2CPP
            if (string.Equals(t.FullName, "UnityEngine.Component", StringComparison.Ordinal))
            {
                try
                {
                    var full = ((Il2CppSystem.Object)(object)c).GetIl2CppType()?.FullName;
                    return full != null && full.IndexOf("Economy.Customer", StringComparison.Ordinal) >= 0;
                }
                catch { return false; }
            }
#endif
            return false;
        }
    }
}
