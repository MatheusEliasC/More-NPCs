using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MelonLoader;
using MoreNPCs.Utils;

namespace MoreNPCs.Patches
{
    /// <summary>
    /// The game's UI clamps prices with an inlined <c>9999f</c> constant; this transpiler replaces those loads with <c>9999999f</c>
    /// in handover and counteroffer flows.
    ///
    /// NOTE (game update): the handover/counteroffer UI was refactored — <c>HandoverScreenPriceSelector</c> no longer exists and
    /// <c>CounterofferInterface</c> renamed its members (e.g. <c>Send</c> → <c>SendCounteroffer</c>, <c>ChangePrice</c>/<c>price</c> removed).
    /// To stay compilable and self-disabling across game versions, targets are resolved by name at runtime via <see cref="AccessTools"/>.
    /// If none resolve, <see cref="Prepare"/> returns false and the patch is skipped (no crash, price cap stays vanilla).
    /// </summary>
    [HarmonyPatch]
    internal static class MaxPriceLimitTranspilerPatch
    {
        private const float OldLimit = 9999f;
        private const float NewLimit = 9999999f;

        // Candidate (typeName, methodName) pairs where the vanilla 9999f price clamp may live.
        // Resolved by name so a renamed/removed target simply yields null instead of a compile error.
        private static readonly (string Type, string Method)[] Candidates =
        {
            ("Il2CppScheduleOne.UI.Handover.HandoverScreenPriceSelector", "SetPrice"),
            ("Il2CppScheduleOne.UI.Phone.CounterofferInterface", "SendCounteroffer"),
            ("Il2CppScheduleOne.UI.Phone.CounterofferInterface", "Send"),
            ("Il2CppScheduleOne.UI.Phone.CounterofferInterface", "ChangePrice"),
            // Also try the plain namespace in case a future build strips the Il2Cpp prefix.
            ("ScheduleOne.UI.Handover.HandoverScreenPriceSelector", "SetPrice"),
            ("ScheduleOne.UI.Phone.CounterofferInterface", "SendCounteroffer"),
        };

        private static IEnumerable<MethodBase> ResolveTargets()
        {
            // Resolve via Il2CppTypeHelper (uses asm.GetType by name) instead of AccessTools.TypeByName,
            // which enumerates GetTypes() on every loaded assembly and floods the log with ReflectionTypeLoadException
            // warnings from unrelated interop assemblies (Ookii.Dialogs, Il2CppSystem, etc.).
            var seen = new HashSet<MethodBase>();
            foreach (var (typeName, methodName) in Candidates)
            {
                var type = Il2CppTypeHelper.ResolveGameType(typeName);
                if (type == null) continue;
                // Direct reflection lookup; returns null quietly if the method was renamed/removed (no AccessTools log).
                MethodInfo? method = null;
                try { method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static); }
                catch { /* ambiguous/overloaded: ignore this candidate */ }
                if (method != null && seen.Add(method))
                    yield return method;
            }
        }

        static bool Prepare()
        {
            var any = false;
            foreach (var _ in ResolveTargets()) { any = true; break; }
            if (!any)
                MelonLogger.Msg("[MaxPriceLimitTranspilerPatch] No price-clamp targets found in this game version; patch skipped.");
            return any;
        }

        static IEnumerable<MethodBase> TargetMethods() => ResolveTargets();

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Ldc_R4
                    && instruction.operand is float f
                    && f == OldLimit)
                {
                    instruction.operand = NewLimit;
                }

                yield return instruction;
            }
        }
    }
}
