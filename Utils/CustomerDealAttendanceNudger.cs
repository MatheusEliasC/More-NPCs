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
    /// <para>ROOT CAUSE (confirmed via runtime NavMeshAgent logs + game decompile): the game drives a
    /// customer to a deal through <c>Customer.UpdateDealAttendance()</c> → <c>CustomerAttendDealBehaviour</c>,
    /// which issues the SetDestination to the delivery spot. On runtime-instanced custom NPCs that call is
    /// not firing at deal time, so no destination is ever set (agent healthy, PathComplete, hasPath=False,
    /// velocity 0). A ragdoll or the deal expiring forces a re-evaluation that finally moves her — which is
    /// exactly why hitting her with a bat "unfreezes" her.</para>
    ///
    /// <para>FIX: a lightweight poller that, for every mod customer standing idle with an active,
    /// in-window, not-yet-attended contract, calls the game's own <c>Customer.UpdateDealAttendance()</c>.
    /// That re-activates the attend-deal behaviour and issues the correct walk — the same effect the ragdoll
    /// has, done cleanly and generically for ALL mod customers (keyed off the game Customer component, not
    /// hardcoded ids). Everything is reflection + try/catch so it degrades to a no-op if the game changes.</para>
    /// </summary>
    internal sealed class CustomerDealAttendanceNudger
    {
        private const string ModNpcNamespace = "MoreNPCs.NPCs";
        private const float ScanIntervalSeconds = 1.5f;
        // After a successful nudge, wait before nudging the same NPC again so we don't spam UpdateDealAttendance.
        private const float PerNpcCooldownSeconds = 5f;

        private float _nextScanTime;

        // Cached reflection handles for the game Customer type (resolved once).
        private bool _reflectionReady;
        private bool _reflectionFailed;
        private Type? _customerType;
        private PropertyInfo? _currentContractProp;
        private PropertyInfo? _isAwaitingDeliveryProp;
        private MethodInfo? _isDealTimeMethod;
        private MethodInfo? _isAtDealLocationMethod;
        private MethodInfo? _updateDealAttendanceMethod;

        // Per-NPC cached Customer component + last-nudge time.
        private readonly Dictionary<string, Component?> _customerByNpcId = new Dictionary<string, Component?>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> _lastNudgeByNpcId = new Dictionary<string, float>(StringComparer.Ordinal);

        public void Update()
        {
            if (Time.time < _nextScanTime) return;
            _nextScanTime = Time.time + ScanIntervalSeconds;

            if (!NPC.CustomNpcsReady) return;
            EnsureReflection();
            if (_reflectionFailed) return;

            try
            {
                var all = NPC.All;
                if (all == null) return;

                foreach (var npc in all)
                {
                    if (npc == null) continue;
                    var type = npc.GetType();
                    if (type == null || !string.Equals(type.Namespace, ModNpcNamespace, StringComparison.Ordinal))
                        continue;
                    if (!npc.IsCustomer) continue;

                    TryNudge(npc);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[DealNudge] scan failed: {ex.Message}");
            }
        }

        private void TryNudge(NPC npc)
        {
            try
            {
                var id = npc.ID;
                if (string.IsNullOrEmpty(id)) return;

                // Per-NPC cooldown so a single nudge gets a chance to take effect before we try again.
                if (_lastNudgeByNpcId.TryGetValue(id, out var last) && Time.time - last < PerNpcCooldownSeconds)
                    return;

                var customer = GetCustomerComponent(npc, id);
                if (customer == null) return;

                // Must have an active, awaited, in-window contract that isn't already being serviced.
                var contract = _currentContractProp?.GetValue(customer);
                if (contract == null) return;

                if (_isAwaitingDeliveryProp != null &&
                    _isAwaitingDeliveryProp.GetValue(customer) is bool awaiting && !awaiting)
                    return;

                if (_isDealTimeMethod != null &&
                    _isDealTimeMethod.Invoke(customer, null) is bool dealTime && !dealTime)
                    return;

                if (_isAtDealLocationMethod != null &&
                    _isAtDealLocationMethod.Invoke(customer, null) is bool atLocation && atLocation)
                    return;

                // Only nudge if she is idle (not already walking somewhere).
                var mv = NpcSafe.Movement(npc);
                if (mv != null)
                {
                    try { if (mv.IsMoving) return; } catch { }
                }

                // Re-run the game's own deal-attendance driver: activates CustomerAttendDealBehaviour and
                // issues the walk to the delivery spot — the clean equivalent of the ragdoll reset.
                _updateDealAttendanceMethod?.Invoke(customer, null);
                _lastNudgeByNpcId[id] = Time.time;
                MelonLogger.Msg($"[DealNudge] nudged deal attendance for {id}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[DealNudge] {npc?.ID}: {ex.Message}");
            }
        }

        private Component? GetCustomerComponent(NPC npc, string id)
        {
            if (_customerByNpcId.TryGetValue(id, out var cached) && cached != null)
                return cached;

            try
            {
                var go = npc.gameObject;
                if (go == null || _customerType == null) return null;
                var comp = go.GetComponentInChildren(Il2CppTypeHelper.To(_customerType), true) as Component;
                _customerByNpcId[id] = comp;
                return comp;
            }
            catch { return null; }
        }

        private void EnsureReflection()
        {
            if (_reflectionReady || _reflectionFailed) return;

            try
            {
                _customerType = Il2CppTypeHelper.ResolveGameType("ScheduleOne.Economy.Customer");
                if (_customerType == null) { _reflectionFailed = true; return; }

                const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                _currentContractProp = _customerType.GetProperty("CurrentContract", Flags);
                _isAwaitingDeliveryProp = _customerType.GetProperty("IsAwaitingDelivery", Flags);
                _isDealTimeMethod = _customerType.GetMethod("IsDealTime", Flags, null, Type.EmptyTypes, null);
                _isAtDealLocationMethod = _customerType.GetMethod("IsAtDealLocation", Flags, null, Type.EmptyTypes, null);
                _updateDealAttendanceMethod = _customerType.GetMethod("UpdateDealAttendance", Flags, null, Type.EmptyTypes, null);

                // The one member we truly cannot do without is UpdateDealAttendance + CurrentContract.
                if (_updateDealAttendanceMethod == null || _currentContractProp == null)
                {
                    MelonLogger.Msg("[DealNudge] disabled: Customer.UpdateDealAttendance/CurrentContract not found in this game version.");
                    _reflectionFailed = true;
                    return;
                }

                _reflectionReady = true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[DealNudge] reflection init failed: {ex.Message}");
                _reflectionFailed = true;
            }
        }
    }
}
