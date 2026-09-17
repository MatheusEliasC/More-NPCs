using System;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;
using S1API.Entities;
using UnityEngine;
using UnityEngine.AI;
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
    /// <para>Testing established: a physical reset of the movement state unfreezes her (WarpToNavMesh did
    /// nothing because the agent was already on-mesh — the freeze is a stuck movement/behaviour state). A
    /// full KnockOut worked to reset it BUT cancelled the contract (the game clears CurrentContract on a
    /// knocked-out customer), zeroing the deal. So we must reset WITHOUT touching health/knockout.</para>
    ///
    /// <para>FIX: use the game's short physical ragdoll on the movement component only —
    /// <c>NPCMovement.ActivateRagdoll(...)</c> then <c>DeactivateRagdoll()</c> ~1s later. This is the
    /// first-hit "reaction" the bat produces, it resets the stuck movement state, and it does NOT involve
    /// NPCHealth, so the contract stays intact. For any mod customer with an active <c>CurrentContract</c>
    /// standing idle >=6s we ragdoll then un-ragdoll. Runs every 2s incl. after load; per-NPC cooldown.</para>
    /// </summary>
    internal sealed class CustomerDealAttendanceNudger
    {
        private const string ModNpcNamespace = "MoreNPCs.NPCs";
        private const float ScanIntervalSeconds = 1f;
        // Re-arm quickly: if a customer with an active deal refreezes after a reset, hit it again soon.
        private const float PerNpcCooldownSeconds = 6f;
        // Unfreeze almost immediately on load instead of making the player wait ~6s.
        private const float IdleConfirmSeconds = 1.5f;
        private const float RagdollDurationSeconds = 1.0f;
        private const float RagdollForce = 5f;

        private float _nextScanTime;
        private readonly Dictionary<string, float> _lastKnockByNpcId = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> _idleSinceByNpcId = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _lastDetectLog = new Dictionary<string, string>(StringComparer.Ordinal);
        // NPCs we ragdolled, with the time we should end the ragdoll (deactivate).
        private readonly Dictionary<string, float> _reviveAtByNpcId = new Dictionary<string, float>(StringComparer.Ordinal);

        public void Update()
        {
#if IL2CPP
            // Process pending revives every tick (cheap: usually empty).
            if (_reviveAtByNpcId.Count > 0) ProcessRevives();

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
        private void ProcessRevives()
        {
            List<string>? due = null;
            foreach (var kv in _reviveAtByNpcId)
                if (Time.time >= kv.Value) (due ??= new List<string>()).Add(kv.Key);
            if (due == null) return;

            foreach (var id in due)
            {
                _reviveAtByNpcId.Remove(id);
                try
                {
                    var npc = FindById(id);
                    if (npc == null) continue;
                    var gm = GetGameMovement(npc);
                    if (gm != null) { try { gm.DeactivateRagdoll(); } catch { } }
                    MelonLogger.Msg($"[DealNudge] {id}: ragdoll ended (reset done).");
                }
                catch { }
            }
        }

        private void Evaluate(NPC npc)
        {
            var id = npc.ID;
            if (string.IsNullOrEmpty(id)) return;

            // Skip if we recently acted or a revive is pending for this NPC.
            if (_reviveAtByNpcId.ContainsKey(id)) return;
            if (_lastKnockByNpcId.TryGetValue(id, out var lastKnock) && Time.time - lastKnock < PerNpcCooldownSeconds)
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

            // If she's currently knocked out (by anything), let it be.
            try { if (npc.IsKnockedOut) return; } catch { }

            // If she has already ARRIVED at the deal spot, do nothing — otherwise we'd keep
            // ragdolling/teleporting her after she got there (the bug seen in testing).
            if (IsAtDealLocation(customer))
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

            var gm = GetGameMovement(npc);
            if (gm == null) return;

            // PREFERRED: teleport her straight to the deal delivery spot. The deal location lives on
            // Contract.DeliveryLocation (discovered via runtime dump). Warping the NavMeshAgent there is
            // instant and avoids the slow walk that nearly expired the deal.
            Vector3 dealPos;
            if (TryGetDealPosition(customer, out dealPos))
            {
                if (TryWarpTo(npc, gm, dealPos))
                {
                    _lastKnockByNpcId[id] = Time.time;
                    _idleSinceByNpcId.Remove(id);
                    MelonLogger.Msg($"[DealNudge] {id}: active contract + idle {IdleConfirmSeconds:F0}s -> Warped to deal {dealPos:F1}.");
                    return;
                }
            }

            // FALLBACK: short physical ragdoll resets the stuck movement state WITHOUT touching
            // health/contract; schedule DeactivateRagdoll so she gets back up and walks to the deal.
            try
            {
                Vector3 foot;
                try { foot = gm.FootPosition; } catch { foot = npc.gameObject.transform.position; }
                gm.ActivateRagdoll(foot, Vector3.up, RagdollForce);
                _lastKnockByNpcId[id] = Time.time;
                _idleSinceByNpcId.Remove(id);
                _reviveAtByNpcId[id] = Time.time + RagdollDurationSeconds;
                MelonLogger.Msg($"[DealNudge] {id}: active contract + idle {IdleConfirmSeconds:F0}s -> Ragdoll reset (no deal pos; contract kept).");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[DealNudge] {id}: ragdoll failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns true if the game already considers the customer to be at their deal location.
        /// Uses the runtime Customer.IsAtDealLocation() method (probed via reflection since the typed
        /// wrapper isn't always available). Fails safe to false.
        /// </summary>
        private static bool IsAtDealLocation(GameCustomer? customer)
        {
            if (customer == null) return false;
            try
            {
                var m = ((object)customer).GetType().GetMethod("IsAtDealLocation",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (m == null) return false;
                var r = m.Invoke(customer, null);
                return r is bool b && b;
            }
            catch { return false; }
        }

        /// <summary>
        /// Resolve the world position the customer must reach for the deal, from
        /// Customer.CurrentContract.DeliveryLocation. The DeliveryLocation object exposes its position
        /// either directly (a Transform / Vector3 member) or via its own transform. Returns false if we
        /// can't find a usable non-zero position.
        /// </summary>
        private static bool TryGetDealPosition(GameCustomer? customer, out Vector3 pos)
        {
            pos = Vector3.zero;
            if (customer == null) return false;
            try
            {
                object? contract = null;
                try { contract = customer.CurrentContract; } catch { }
                if (contract == null) return false;

                var dlProp = contract.GetType().GetProperty("DeliveryLocation",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var delivery = dlProp?.GetValue(contract);
                if (delivery == null) return false;

                return TryExtractPosition(delivery, out pos);
            }
            catch { return false; }
        }

        /// <summary>
        /// Extract a world position from a DeliveryLocation-like object: try common Transform/Vector3
        /// members by name, then fall back to the object's own transform.position.
        /// </summary>
        private static bool TryExtractPosition(object delivery, out Vector3 pos)
        {
            pos = Vector3.zero;
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var t = delivery.GetType();

            // Preferred named members that hold the actual stand/meeting point.
            string[] candidates =
            {
                "CustomerStandPoint", "StandPoint", "CustomerPosition", "DealPosition",
                "Position", "Point", "Transform", "transform"
            };

            foreach (var name in candidates)
            {
                try
                {
                    var p = t.GetProperty(name, F);
                    object? val = p != null ? p.GetValue(delivery) : t.GetField(name, F)?.GetValue(delivery);
                    if (val == null) continue;
                    if (TryValueToPosition(val, out pos)) return true;
                }
                catch { }
            }

            // Last resort: any Transform/Vector3 member on the object.
            try
            {
                foreach (var p in t.GetProperties(F))
                {
                    if (p.GetIndexParameters().Length != 0) continue;
                    object? val; try { val = p.GetValue(delivery); } catch { continue; }
                    if (val != null && TryValueToPosition(val, out pos) && pos != Vector3.zero) return true;
                }
                foreach (var f in t.GetFields(F))
                {
                    object? val; try { val = f.GetValue(delivery); } catch { continue; }
                    if (val != null && TryValueToPosition(val, out pos) && pos != Vector3.zero) return true;
                }
            }
            catch { }

            return pos != Vector3.zero;
        }

        private static bool TryValueToPosition(object val, out Vector3 pos)
        {
            pos = Vector3.zero;
            if (val is Vector3 v) { pos = v; return pos != Vector3.zero; }
            if (val is Transform tr) { try { pos = tr.position; } catch { return false; } return pos != Vector3.zero; }
            if (val is GameObject go) { try { pos = go.transform.position; } catch { return false; } return pos != Vector3.zero; }
            return false;
        }

        /// <summary>
        /// Teleport the NPC to <paramref name="target"/>. Prefers NavMeshAgent.Warp (keeps the agent on
        /// the mesh); falls back to the game NPCMovement.Warp. Returns false if neither worked.
        /// </summary>
        private static bool TryWarpTo(NPC npc, GameNPCMovement gm, Vector3 target)
        {
            // Snap the target onto the navmesh so Warp lands on a walkable spot.
            Vector3 dest = target;
            try
            {
                if (NavMesh.SamplePosition(target, out var hit, 4f, NavMesh.AllAreas))
                    dest = hit.position;
            }
            catch { }

            // Preferred: native NavMeshAgent.Warp.
            try
            {
                var agent = npc.gameObject.GetComponentInChildren<NavMeshAgent>(true);
                if (agent != null)
                {
                    if (agent.Warp(dest)) return true;
                }
            }
            catch { }

            // Fallback: game movement Warp.
            try { gm.Warp(dest); return true; } catch { }

            return false;
        }

        private static NPC? FindById(string id)
        {
            try
            {
                var all = NPC.All;
                if (all == null) return null;
                foreach (var n in all)
                    if (n != null && string.Equals(n.ID, id, StringComparison.Ordinal)) return n;
            }
            catch { }
            return null;
        }

        private static GameNPCMovement? GetGameMovement(NPC npc)
        {
            GameObject? go = null;
            try { go = npc.gameObject; } catch { }
            if (go == null) return null;
            try
            {
                var m = go.GetComponent<GameNPCMovement>();
                if (m != null) return m;
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
