using System;
using System.Collections.Generic;
using MelonLoader;
using S1API.Entities;
using UnityEngine;
#if IL2CPP
using GameCustomer = Il2CppScheduleOne.Economy.Customer;
using Il2CppInterop.Runtime;
#endif

namespace MoreNPCs.Utils
{
    /// <summary>
    /// Unfreezes mod customers that have an arranged deal but stand idle instead of walking to it.
    ///
    /// <para>Logs proved the customer has an active <c>CurrentContract</c> but stays idle (healthy agent,
    /// no destination) and never enters the deal-active flags. A baseball-bat ragdoll resets that stuck
    /// state and she proceeds through the deal. So: any mod customer with an active contract that stands
    /// still for a few seconds gets the official <see cref="NPC.KnockOut"/> — the same reset the bat does.</para>
    ///
    /// <para>The game <c>Customer</c> is an IL2CPP component whose managed wrapper isn't generated, so
    /// <c>GetComponents&lt;Component&gt;().GetType()</c> reports the base type and managed reflection on it
    /// is unreliable. We resolve it two ways and cast to the typed interop <c>Customer</c>: first the typed
    /// <c>GetComponent&lt;Customer&gt;()</c>, then a fallback that scans components and <c>TryCast</c>s the
    /// one whose IL2CPP type is Customer. Reading <c>CurrentContract</c> then goes through the typed
    /// property — no fragile reflection. Detection outcome is logged for the watched NPCs.</para>
    /// </summary>
    internal sealed class CustomerDealAttendanceNudger
    {
        private const string ModNpcNamespace = "MoreNPCs.NPCs";
        private const float ScanIntervalSeconds = 2f;
        private const float PerNpcCooldownSeconds = 20f;
        private const float IdleConfirmSeconds = 6f;

        private float _nextScanTime;
        private readonly Dictionary<string, float> _lastKnockByNpcId = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> _idleSinceByNpcId = new Dictionary<string, float>(StringComparer.Ordinal);
        // Log the detection result once per NPC transition so we can see what the trigger sees.
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

            if (_lastKnockByNpcId.TryGetValue(id, out var lastKnock) && Time.time - lastKnock < PerNpcCooldownSeconds)
                return;

            var customer = GetCustomer(npc);
            bool hasContract = false;
            if (customer != null)
            {
                try { hasContract = customer.CurrentContract != null; } catch { }
            }

            // Log detection state on change so we can diagnose without a second component.
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
                MelonLogger.Msg($"[DealNudge] {id}: active contract + idle {IdleConfirmSeconds:F0}s -> KnockOut().");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[DealNudge] {id}: KnockOut failed: {ex.Message}");
            }
        }

        /// <summary>Resolve the typed game Customer component (typed GetComponent, then IL2CPP TryCast fallback).</summary>
        private static GameCustomer? GetCustomer(NPC npc)
        {
            GameObject? go = null;
            try { go = npc.gameObject; } catch { }
            if (go == null) return null;

            // 1) Typed lookup.
            try
            {
                var c = go.GetComponent<GameCustomer>();
                if (c != null) return c;
            }
            catch { }
            try
            {
                var c = go.GetComponentInChildren<GameCustomer>(true);
                if (c != null) return c;
            }
            catch { }

            // 2) Fallback: scan raw components, find the one whose IL2CPP type is Customer, TryCast it.
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
