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
                if (customer == null)
                {
                    // Dump every component type on the NPC + children so we can see what the customer
                    // component is actually called / where it lives. Deduped, root vs child noted.
                    var names = new System.Collections.Generic.List<string>();
                    try
                    {
                        foreach (var c in go.GetComponents<Component>() ?? Array.Empty<Component>())
                            if (c != null) names.Add(Il2CppRealTypeName(c));
                    }
                    catch { }
                    var childNames = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                    try
                    {
                        foreach (var c in go.GetComponentsInChildren<Component>(true) ?? Array.Empty<Component>())
                            if (c != null) childNames.Add(c.GetType().FullName ?? c.GetType().Name);
                    }
                    catch { }
                    // Only report child types that mention Customer/Economy/Contract to keep the line short.
                    var interesting = new System.Collections.Generic.List<string>();
                    foreach (var n in childNames)
                        if (n.IndexOf("Customer", StringComparison.OrdinalIgnoreCase) >= 0
                            || n.IndexOf("Economy", StringComparison.OrdinalIgnoreCase) >= 0
                            || n.IndexOf("Contract", StringComparison.OrdinalIgnoreCase) >= 0)
                            interesting.Add(n);
                    return "deal: CUSTOMER_COMPONENT_NOT_FOUND | root=[" + string.Join(", ", names)
                        + "] | interestingChildren=[" + string.Join(", ", interesting) + "]";
                }

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

                // One-shot discovery: dump location-bearing members of the Customer and its Contract so
                // we can find the real deal destination to warp to (the NavMeshAgent destination is empty).
                string locDump = "";
                try
                {
                    if (string.Equals(contract, "SET", StringComparison.Ordinal))
                        locDump = " | LOC{" + DumpLocationMembers(customer) + "}";
                }
                catch (Exception e) { locDump = " | LOC{err:" + e.Message + "}"; }

                return $"deal: type={ct.Name} contract={contract} awaiting={awaiting} dealTime={dealTime} atLoc={atLoc}{locDump}";
            }
            catch (Exception ex)
            {
                return $"deal: ERR({ex.Message})";
            }
        }

        /// <summary>
        /// Reflect over the Customer and its CurrentContract, logging every member (property/field, no args)
        /// whose value is a Vector3 or Transform, or whose NAME hints at a deal location. Prints live values
        /// so we can identify the exact member to warp mod customers to. Diagnostic only.
        /// </summary>
        private static string DumpLocationMembers(object customer)
        {
            var parts = new System.Collections.Generic.List<string>();
            try
            {
                DumpLocationMembersOf("cust", customer, parts);

                object? contractObj = null;
                try
                {
                    var p = customer.GetType().GetProperty("CurrentContract", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    contractObj = p?.GetValue(customer);
                }
                catch { }
                if (contractObj != null)
                    DumpLocationMembersOf("contract:" + contractObj.GetType().Name, contractObj, parts);
            }
            catch (Exception e) { parts.Add("dump-err:" + e.Message); }
            return string.Join(" ", parts);
        }

        private static void DumpLocationMembersOf(string label, object obj, System.Collections.Generic.List<string> parts)
        {
            if (obj == null) return;
            var t = obj.GetType();
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            foreach (var prop in t.GetProperties(F))
            {
                if (prop.GetIndexParameters().Length != 0) continue;
                TryReportMember(label, prop.Name, () => prop.GetValue(obj), parts);
            }
            foreach (var f in t.GetFields(F))
            {
                TryReportMember(label, f.Name, () => f.GetValue(obj), parts);
            }
        }

        private static void TryReportMember(string label, string name, Func<object?> getter, System.Collections.Generic.List<string> parts)
        {
            bool nameHints =
                name.IndexOf("location", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("delivery", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("deaddrop", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("dropoff", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("destination", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("dealpoint", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("meetpoint", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("position", StringComparison.OrdinalIgnoreCase) >= 0;

            object? val;
            try { val = getter(); } catch { return; }
            if (val == null)
            {
                if (nameHints) parts.Add($"{label}.{name}=null");
                return;
            }

            if (val is Vector3 v)
            {
                parts.Add($"{label}.{name}(V3)={v.ToString("F1")}");
                return;
            }
            if (val is Transform tr)
            {
                try { parts.Add($"{label}.{name}(T)={tr.position.ToString("F1")}"); }
                catch { parts.Add($"{label}.{name}(T)=?"); }
                return;
            }
            if (nameHints)
            {
                // Named like a location but not a raw V3/Transform — report its type so we can drill in.
                parts.Add($"{label}.{name}<{val.GetType().Name}>");
            }
        }

        private static bool IsCustomerType(Component c)
        {
            if (c == null) return false;
            // Managed GetType() is reliable when the interop wrapper is loaded.
            var t = c.GetType();
            if (string.Equals(t.Name, "Customer", StringComparison.Ordinal)
                && t.FullName != null
                && t.FullName.IndexOf("Economy.Customer", StringComparison.Ordinal) >= 0)
                return true;
#if IL2CPP
            // Fallback: managed type is the base UnityEngine.Component; ask the IL2CPP runtime.
            try
            {
                var full = ((Il2CppSystem.Object)(object)c).GetIl2CppType()?.FullName;
                if (full != null && full.IndexOf("Economy.Customer", StringComparison.Ordinal) >= 0)
                    return true;
            }
            catch { }
#endif
            return false;
        }

        /// <summary>
        /// The managed GetType() of an IL2CPP component is often the base UnityEngine.Component when the
        /// concrete Il2CppInterop wrapper wasn't generated. Ask the IL2CPP runtime for the real type name.
        /// </summary>
        private static string Il2CppRealTypeName(Component c)
        {
            var managed = c.GetType().FullName ?? c.GetType().Name;
            if (!string.Equals(managed, "UnityEngine.Component", StringComparison.Ordinal))
                return managed;
#if IL2CPP
            try
            {
                var il2cppType = ((Il2CppSystem.Object)(object)c).GetIl2CppType();
                var full = il2cppType?.FullName;
                if (!string.IsNullOrEmpty(full)) return "il2cpp:" + full;
            }
            catch { }
#endif
            return managed;
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
