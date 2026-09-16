using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MelonLoader;
using MoreNPCs.Utils;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.NPCs.Relation;
using UnityEngine;

namespace MoreNPCs.Patches
{
    /// <summary>
    /// Replaces the single <c>ldc.r4 -0.075</c> passed to <c>ChangeInfluence</c> in
    /// <see cref="Customer.OnCustomerUnlocked"/> so one RPC carries the configured delta.
    /// A postfix second call was unreliable with networking and still drove the 75 popup (0.075×1000).
    /// </summary>
    [HarmonyPatch]
    internal static class CustomerNewCustomerCartelInfluencePatch
    {
        /// <summary>Skip the patch entirely if the target method no longer exists (game update), instead of throwing.</summary>
        private static bool Prepare()
        {
            var m = AccessTools.Method(
                typeof(Customer),
                "OnCustomerUnlocked",
                new[] { typeof(NPCRelationData.EUnlockType), typeof(bool) });
            if (m == null)
                MelonLogger.Msg("[MoreNPCs] Cartel influence patch skipped: Customer.OnCustomerUnlocked(EUnlockType,bool) not found in this game version.");
            return m != null;
        }

        private static MethodBase TargetMethod() =>
            AccessTools.Method(
                typeof(Customer),
                "OnCustomerUnlocked",
                new[] { typeof(NPCRelationData.EUnlockType), typeof(bool) });

        /// <summary>Emitted by transpiler instead of loading -0.075f.</summary>
        public static float GetCartelInfluenceDeltaForNewCustomerUnlock()
        {
            if (!MoreNPCsPreferences.RegionChanges_ReducedCartelInfluenceOnNewCustomers.Value)
                return -0.075f;
            return -0.025f;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var helper = AccessTools.Method(
                typeof(CustomerNewCustomerCartelInfluencePatch),
                nameof(GetCartelInfluenceDeltaForNewCustomerUnlock));
            var list = new List<CodeInstruction>(instructions);
            var replaced = false;
            for (var i = 0; i < list.Count; i++)
            {
                var code = list[i];
                if (code.opcode == OpCodes.Ldc_R4 && code.operand is float f && Mathf.Approximately(f, -0.075f))
                {
                    list[i] = new CodeInstruction(OpCodes.Call, helper);
                    replaced = true;
                    break;
                }
            }

            if (!replaced)
                MelonLogger.Msg("[MoreNPCs] Cartel influence tweak inactive: the -0.075 constant is no longer inlined in Customer.OnCustomerUnlocked (game update). Vanilla influence applies.");

            return list;
        }
    }
}
