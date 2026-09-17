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
        // After a warp+ragdoll reset, give the game ~10s to enter the deal state before retrying.
        private const float PerNpcCooldownSeconds = 10f;
        // Unfreeze almost immediately on load instead of making the player wait.
        private const float IdleConfirmSeconds = 1.5f;
        private const float RagdollDurationSeconds = 1.0f;
        private const float RagdollForce = 5f;
        // The freeze only happens right after a save load; deals accepted during normal play work on
        // their own. So only nudge within this window after NPCs become ready (a load), and cap the
        // number of resets per NPC per load — together this stops the warp/ragdoll loops (e.g. a deal
        // set for the next morning must not be reset all night).
        private const float PostLoadActiveWindowSeconds = 120f;
        private const int MaxResetsPerNpcPerLoad = 3;
        // If she's within this distance of the deal stand point after a reset, treat her as arrived and
        // stop resetting (avoids re-ragdolling while the player talks to her at the spot).
        private const float DealArrivedRadius = 2.5f;

        private float _nextScanTime;
        private readonly Dictionary<string, float> _lastKnockByNpcId = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> _idleSinceByNpcId = new Dictionary<string, float>(StringComparer.Ordinal);
        // NPCs we ragdolled, with the time we should end the ragdoll (deactivate).
        private readonly Dictionary<string, float> _reviveAtByNpcId = new Dictionary<string, float>(StringComparer.Ordinal);
        // Reset attempts per NPC in the current post-load window (hard cap against loops).
        private readonly Dictionary<string, int> _resetCountByNpcId = new Dictionary<string, int>(StringComparer.Ordinal);

        // Post-load window bookkeeping: when NPCs became ready (a load) and whether the window is open.
        private bool _wasReady;
        private float _readyAtTime;

        public void Update()
        {
#if IL2CPP
            // Process pending revives every tick (cheap: usually empty).
            if (_reviveAtByNpcId.Count > 0) ProcessRevives();

            if (Time.time < _nextScanTime) return;
            _nextScanTime = Time.time + ScanIntervalSeconds;

            bool ready = false;
            try { ready = NPC.CustomNpcsReady; } catch { }
            if (!ready)
            {
                // Not loaded (or unloaded): reset window state so the NEXT load reopens the window.
                if (_wasReady)
                {
                    _wasReady = false;
                    _idleSinceByNpcId.Clear();
                    _resetCountByNpcId.Clear();
                }
                return;
            }

            // First tick after becoming ready = a fresh load: (re)open the active window.
            if (!_wasReady)
            {
                _wasReady = true;
                _readyAtTime = Time.time;
                _resetCountByNpcId.Clear();
                _idleSinceByNpcId.Clear();
            }

            // The freeze only manifests shortly after load. Once the window closes, stop nudging so
            // deals accepted during normal play (e.g. one set for the next morning) are never reset.
            if (Time.time - _readyAtTime > PostLoadActiveWindowSeconds) return;

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

            // Hard cap: never reset the same NPC more than a few times per load (loop guard).
            if (_resetCountByNpcId.TryGetValue(id, out var resets) && resets >= MaxResetsPerNpcPerLoad)
                return;

            var customer = GetCustomer(npc);
            bool hasContract = false;
            if (customer != null) { try { hasContract = customer.CurrentContract != null; } catch { } }

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

            // If the game already registers her as attending/awaiting the deal, we're done — leave her
            // alone (otherwise we'd keep resetting her at the spot, the loop seen in testing).
            if (IsAtDealLocation(customer) || IsAwaitingDelivery(customer))
            {
                _idleSinceByNpcId.Remove(id);
                return;
            }

            // Never reset her while the player is talking to her (opening the chat would otherwise
            // trigger a warp+ragdoll mid-conversation). Wait until the dialogue is closed.
            if (IsInDialogue(npc))
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

            Vector3 dealPos;
            bool haveDealPos = TryGetDealPosition(customer, out dealPos);

            // TEMP DIAGNOSTIC (owen_crowe only): capture why the guards didn't block, so the next version
            // can pinpoint the remaining loop. Cheap, scoped to one id, remove once confirmed.
            if (string.Equals(id, "owen_crowe", StringComparison.OrdinalIgnoreCase))
            {
                float dist = -1f;
                try { if (haveDealPos) dist = Vector2.Distance(new Vector2(gm.FootPosition.x, gm.FootPosition.z), new Vector2(dealPos.x, dealPos.z)); } catch { }
                int rcNow = _resetCountByNpcId.TryGetValue(id, out var rcx) ? rcx : 0;
                MelonLogger.Msg($"[DealNudge][DIAG] owen_crowe: haveDealPos={haveDealPos} distToSpot={dist:F2} resetsThisLoad={rcNow} awaiting={IsAwaitingDelivery(customer)} atLoc={IsAtDealLocation(customer)} inDialogue={IsInDialogue(npc)} customerResolved={(customer != null)}");
            }

            // If she's ALREADY standing on the deal spot AND we've reset her at least once this load,
            // she has arrived — stop (the deal is being served here). This is a position-based guard that
            // does not rely on IsAwaitingDelivery/IsAtDealLocation, which read intermittently on mod NPCs
            // and let the ragdoll keep firing during the delivery chat (the Owen loop).
            if (haveDealPos)
            {
                bool alreadyResetThisLoad = _resetCountByNpcId.TryGetValue(id, out var priorResets) && priorResets > 0;
                if (alreadyResetThisLoad && IsAtPosition(npc, gm, dealPos, DealArrivedRadius))
                {
                    _idleSinceByNpcId.Remove(id);
                    return;
                }
            }

            // Sequence (per testing): WARP her onto the deal stand point, THEN ragdoll-reset in place.
            // The warp puts her body exactly where the deal expects her; the ragdoll is what actually
            // transitions her out of the frozen movement/behaviour state so the game's deal system picks
            // her up (a warp alone moves the body but leaves the state stuck -> no delivery dialogue).
            if (haveDealPos)
            {
                try { TryWarpTo(npc, gm, dealPos); } catch { }
            }

            try
            {
                Vector3 foot;
                try { foot = gm.FootPosition; } catch { foot = npc.gameObject.transform.position; }
                gm.ActivateRagdoll(foot, Vector3.up, RagdollForce);
                _lastKnockByNpcId[id] = Time.time;
                _idleSinceByNpcId.Remove(id);
                _reviveAtByNpcId[id] = Time.time + RagdollDurationSeconds;
                _resetCountByNpcId[id] = (_resetCountByNpcId.TryGetValue(id, out var rc) ? rc : 0) + 1;
                if (haveDealPos)
                    MelonLogger.Msg($"[DealNudge] {id}: warped to deal {dealPos:F1} + ragdoll reset (contract kept). Waiting {PerNpcCooldownSeconds:F0}s to validate.");
                else
                    MelonLogger.Msg($"[DealNudge] {id}: ragdoll reset (no deal pos; contract kept). Waiting {PerNpcCooldownSeconds:F0}s to validate.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[DealNudge] {id}: reset failed: {ex.Message}");
            }
        }

        /// <summary>
        /// True if the NPC's current position is within <paramref name="radius"/> of <paramref name="target"/>
        /// (horizontal distance; ignores small Y differences). Uses the movement foot position, falling back
        /// to the transform. Fails safe to false.
        /// </summary>
        private static bool IsAtPosition(NPC npc, GameNPCMovement gm, Vector3 target, float radius)
        {
            try
            {
                Vector3 pos;
                try { pos = gm.FootPosition; }
                catch { pos = npc.gameObject.transform.position; }

                var a = new Vector2(pos.x, pos.z);
                var b = new Vector2(target.x, target.z);
                return Vector2.Distance(a, b) <= radius;
            }
            catch { return false; }
        }

        // Cached DialogueHandler type lookup (resolved once).
        private static Type? _dialogueHandlerType;
        private static bool _dialogueHandlerResolved;

        /// <summary>
        /// Returns true if the player is currently in a conversation with this NPC (its DialogueHandler
        /// reports an active conversation). Uses the same game type PPHylandDialogue relies on
        /// (ScheduleOne.Dialogue.DialogueHandler). Reflective + fails safe to false.
        /// </summary>
        private static bool IsInDialogue(NPC npc)
        {
            try
            {
                if (!_dialogueHandlerResolved)
                {
                    _dialogueHandlerType = Il2CppTypeHelper.ResolveGameType("ScheduleOne.Dialogue.DialogueHandler")
                        ?? Il2CppTypeHelper.ResolveGameType("Il2CppScheduleOne.Dialogue.DialogueHandler");
                    _dialogueHandlerResolved = true;
                }
                if (_dialogueHandlerType == null) return false;

                GameObject? go = null;
                try { go = npc.gameObject; } catch { }
                if (go == null) return false;

                var handler = go.GetComponentInChildren(Il2CppTypeHelper.To(_dialogueHandlerType), true) as Component;
                if (handler == null) return false;

                var ht = handler.GetType();
                const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                // Try common member names that flag an active conversation.
                string[] boolNames = { "IsConversing", "isConversing", "InConversation", "isInConversation", "Conversing", "conversing" };
                foreach (var name in boolNames)
                {
                    try
                    {
                        var p = ht.GetProperty(name, F);
                        if (p != null && p.GetValue(handler) is bool pb) return pb;
                        var f = ht.GetField(name, F);
                        if (f != null && f.GetValue(handler) is bool fb) return fb;
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Returns true if the game already considers the customer to be awaiting the player's delivery.
        /// Probed reflectively (IsAwaitingDelivery property). Fails safe to false.
        /// </summary>
        private static bool IsAwaitingDelivery(GameCustomer? customer)
        {
            if (customer == null) return false;
            try
            {
                var p = ((object)customer).GetType().GetProperty("IsAwaitingDelivery",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p == null) return false;
                var r = p.GetValue(customer);
                return r is bool b && b;
            }
            catch { return false; }
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
