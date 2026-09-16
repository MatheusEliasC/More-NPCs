using System;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;
using S1API.Entities;
using UnityEngine;

namespace MoreNPCs.Utils
{
    /// <summary>
    /// Fixes mod customers freezing in place instead of walking to their arranged deal.
    ///
    /// <para>ROOT CAUSE (confirmed via runtime NavMeshAgent logs + game decompile): the game normally
    /// drives a customer to a deal through <c>Customer.UpdateDealAttendance()</c> /
    /// <c>CustomerAttendDealBehaviour</c>, which sets the NavMeshAgent destination to the delivery spot.
    /// For runtime-instanced custom NPCs that never fires at deal time, so the customer just stands with a
    /// healthy-but-idle agent (onNavMesh, PathComplete, hasPath=False, no destination) until a ragdoll or
    /// deal expiry resets state.</para>
    ///
    /// <para>FIX: for every mod customer that has an active, in-window, awaited contract and is standing
    /// idle, resolve the deal's delivery stand point and drive her there with the S1API movement API
    /// (<c>Movement.SetDestination</c>) — the same walk the game should have issued. Re-issues on an
    /// interval (retry) until she is moving / has arrived. Reflection uses the object's REAL runtime type
    /// (via <c>instance.GetType()</c>), which is how IL2CPP components must be reflected — using a
    /// name-resolved System.Type as the invocation target throws "Object does not match target type".</para>
    /// </summary>
    internal sealed class CustomerDealAttendanceNudger
    {
        private const string ModNpcNamespace = "MoreNPCs.NPCs";
        private const float ScanIntervalSeconds = 2f;
        // Re-issue the destination at most this often per NPC (retry cadence) so we keep nudging a stuck
        // customer until she actually starts moving, without spamming every frame.
        private const float PerNpcRetrySeconds = 4f;

        private float _nextScanTime;
        private readonly Dictionary<string, float> _lastPushByNpcId = new Dictionary<string, float>(StringComparer.Ordinal);

        // Cache the resolved game Customer component per NPC id (its GetType() is the real runtime type).
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
                    TryDriveToDeal(npc);
                }
                catch { /* never let one NPC break the pass */ }
            }
        }

        private void TryDriveToDeal(NPC npc)
        {
            var id = npc.ID;
            if (string.IsNullOrEmpty(id)) return;

            // Retry cadence per NPC.
            if (_lastPushByNpcId.TryGetValue(id, out var last) && Time.time - last < PerNpcRetrySeconds)
                return;

            var customer = GetCustomerComponent(npc, id);
            if (customer == null) return;

            // Reflect using the component's REAL runtime type (IL2CPP-safe).
            var ct = customer.GetType();

            // Active, awaited, in-window contract that isn't already being serviced.
            var contract = GetMember(customer, ct, "CurrentContract");
            if (contract == null) return;

            if (GetMember(customer, ct, "IsAwaitingDelivery") is bool awaiting && !awaiting) return;
            if (InvokeBool(customer, ct, "IsDealTime", true) == false) return;         // if unknown, assume true
            if (InvokeBool(customer, ct, "IsAtDealLocation", false) == true) return;   // if unknown, assume not there

            // Resolve the delivery stand point.
            var standPoint = ResolveDealStandPoint(customer, ct);
            if (!standPoint.HasValue) return;

            // Only push if she is idle (not already walking there).
            var mv = NpcSafe.Movement(npc);
            if (mv == null) return;
            try { if (mv.IsMoving) { return; } } catch { }

            try
            {
                mv.SetDestination(standPoint.Value);
                _lastPushByNpcId[id] = Time.time;
                MelonLogger.Msg($"[DealNudge] sent {id} to deal at {standPoint.Value:F1}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[DealNudge] {id}: SetDestination failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Resolve the world position the customer should stand at for the handover:
        /// Customer.GetDeliveryLocation().CustomerStandPoint.position (reflected on real runtime types).
        /// </summary>
        private static Vector3? ResolveDealStandPoint(Component customer, Type ct)
        {
            try
            {
                var loc = InvokeMethod(customer, ct, "GetDeliveryLocation");
                if (loc == null) return null;

                var lt = loc.GetType();
                var standPoint = GetMember(loc, lt, "CustomerStandPoint");
                if (standPoint is Transform tr && tr != null) return tr.position;

                // Fallback: TeleportPoint transform.
                var teleport = GetMember(loc, lt, "TeleportPoint");
                if (teleport is Transform tt && tt != null) return tt.position;
            }
            catch { }
            return null;
        }

        private Component? GetCustomerComponent(NPC npc, string id)
        {
            if (_customerByNpcId.TryGetValue(id, out var cached) && cached != null)
                return cached;

            try
            {
                var go = npc.gameObject;
                if (go == null) return null;

                // Find the Customer component by real runtime type name (IL2CPP-safe: no name-resolved
                // System.Type used as an invocation target). Search self + children.
                var found = FindComponentByTypeName(go, "Customer");
                _customerByNpcId[id] = found;
                return found;
            }
            catch { return null; }
        }

        private static Component? FindComponentByTypeName(GameObject go, string simpleTypeName)
        {
            try
            {
                foreach (var c in go.GetComponents<Component>() ?? Array.Empty<Component>())
                    if (IsCustomerType(c)) return c;
                foreach (var c in go.GetComponentsInChildren<Component>(true) ?? Array.Empty<Component>())
                    if (IsCustomerType(c)) return c;
            }
            catch { }
            return null;
        }

        private static bool IsCustomerType(Component c)
        {
            if (c == null) return false;
            var t = c.GetType();
            // Match ScheduleOne.Economy.Customer (Il2Cpp namespace variant included), exact type name "Customer".
            return string.Equals(t.Name, "Customer", StringComparison.Ordinal)
                && t.FullName != null
                && t.FullName.IndexOf("Economy.Customer", StringComparison.Ordinal) >= 0;
        }

        // --- reflection helpers that operate on the REAL runtime type ---

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

        private static object? InvokeMethod(object instance, Type t, string name)
        {
            try
            {
                var m = t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (m != null) return m.Invoke(instance, null);
            }
            catch { }
            return null;
        }

        /// <summary>Invoke a parameterless bool method; return <paramref name="fallback"/> if it can't be called.</summary>
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
