using System;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;
using S1API.Entities;
using UnityEngine;

namespace MoreNPCs.Utils
{
    /// <summary>
    /// Unfreezes mod customers that have an active arranged deal but stand idle at their spot instead of
    /// walking to the meet point (runtime logs proved the NavMeshAgent is healthy but has no destination;
    /// the game's attend-deal driver never fires for these runtime-instanced NPCs). A ragdoll knockdown
    /// (baseball bat) reliably resets that state and they then walk to the deal and complete it.
    ///
    /// <para>This reproduces that reset with the official S1API API <see cref="NPC.KnockOut"/> — no
    /// reflection on the movement path, no guessing. For every mod customer that (a) has an active,
    /// in-window, awaited contract and (b) is standing still and (c) is not already knocked out, we call
    /// <c>KnockOut()</c>. When it gets back up the game re-drives it to the deal. Runs on an interval so it
    /// also fires right after a save load, with a per-NPC cooldown so we never ragdoll-loop anyone.</para>
    ///
    /// <para>Deal detection reflects the game <c>Customer</c> component using the object's REAL runtime
    /// type (<c>instance.GetType()</c>) — the IL2CPP-safe pattern already used by GameDealerFinder. Every
    /// step is logged once per NPC transition so failures are diagnosable rather than silent.</para>
    /// </summary>
    internal sealed class CustomerDealAttendanceNudger
    {
        private const string ModNpcNamespace = "MoreNPCs.NPCs";
        private const float ScanIntervalSeconds = 2f;
        // After knocking someone out, wait before considering them again (ragdoll + recovery + walk time).
        private const float PerNpcCooldownSeconds = 20f;
        // How long the customer must be continuously idle-with-deal before we knock them out, so we don't
        // hit someone who is momentarily paused mid-path.
        private const float IdleConfirmSeconds = 4f;

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
                    var type = npc.GetType();
                    if (type == null || !string.Equals(type.Namespace, ModNpcNamespace, StringComparison.Ordinal))
                        continue;
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

            // Cooldown after a knockout.
            if (_lastKnockByNpcId.TryGetValue(id, out var lastKnock) && Time.time - lastKnock < PerNpcCooldownSeconds)
                return;

            // Needs an active, in-window, awaited contract that isn't already being serviced.
            if (!HasPendingDeal(npc, id))
            {
                _idleSinceByNpcId.Remove(id);
                return;
            }

            // Must be standing still. If moving, she's already heading to the deal — clear idle timer.
            bool moving = false;
            var mv = NpcSafe.Movement(npc);
            if (mv != null) { try { moving = mv.IsMoving; } catch { } }
            if (moving)
            {
                _idleSinceByNpcId.Remove(id);
                return;
            }

            // Already knocked out / recovering — let it play out.
            try { if (npc.IsKnockedOut) { return; } } catch { }

            // Require sustained idle before acting.
            if (!_idleSinceByNpcId.TryGetValue(id, out var idleSince))
            {
                _idleSinceByNpcId[id] = Time.time;
                return;
            }
            if (Time.time - idleSince < IdleConfirmSeconds) return;

            // Do the reset the bat does.
            try
            {
                npc.KnockOut();
                _lastKnockByNpcId[id] = Time.time;
                _idleSinceByNpcId.Remove(id);
                MelonLogger.Msg($"[DealNudge] {id}: idle with active deal -> KnockOut() to reset attend-deal.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[DealNudge] {id}: KnockOut failed: {ex.Message}");
            }
        }

        /// <summary>
        /// True if the customer currently has an active arranged deal awaiting delivery in its window and
        /// is not already at the deal spot. Reflects the game Customer component on its real runtime type.
        /// </summary>
        private bool HasPendingDeal(NPC npc, string id)
        {
            var customer = GetCustomerComponent(npc, id);
            if (customer == null) return false;

            var ct = customer.GetType();

            var contract = GetMember(customer, ct, "CurrentContract");
            if (contract == null) return false;

            if (GetMember(customer, ct, "IsAwaitingDelivery") is bool awaiting && !awaiting) return false;
            if (InvokeBool(customer, ct, "IsDealTime", true) == false) return false;       // unknown -> assume true
            if (InvokeBool(customer, ct, "IsAtDealLocation", false) == true) return false; // unknown -> assume not there

            return true;
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
                if (found == null)
                    foreach (var c in go.GetComponentsInChildren<Component>(true) ?? Array.Empty<Component>())
                        if (IsCustomerType(c)) { found = c; break; }
                _customerByNpcId[id] = found;
                return found;
            }
            catch { return null; }
        }

        private static bool IsCustomerType(Component c)
        {
            if (c == null) return false;
            var t = c.GetType();
            return string.Equals(t.Name, "Customer", StringComparison.Ordinal)
                && t.FullName != null
                && t.FullName.IndexOf("Economy.Customer", StringComparison.Ordinal) >= 0;
        }

        // --- reflection helpers operating on the REAL runtime type (IL2CPP-safe) ---

        private static object? GetMember(object instance, Type t, string name)
        {
            try
            {
                var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null) return p.GetValue(instance);
                var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null) return f.GetValue(instance);
            }
            catch { }
            return null;
        }

        private static bool? InvokeBool(object instance, Type t, string name, bool fallback)
        {
            try
            {
                var m = t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (m == null) return fallback;
                var r = m.Invoke(instance, null);
                return r is bool b ? b : fallback;
            }
            catch { return fallback; }
        }
    }
}
