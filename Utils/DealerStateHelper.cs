using System;
using System.Collections;
using System.Reflection;
using MelonLoader;
using UnityEngine;

namespace MoreNPCs.Utils
{
    /// <summary>
    /// Forces a custom dealer into the "potential" (not-yet-recruited) state so it shows on the map as a
    /// Potential Dealer and follows the normal unlock/recruit flow, instead of appearing already contracted.
    ///
    /// The game's Dealer component (Il2CppScheduleOne.Economy.Dealer) has an <c>IsRecruited</c> bool (private setter),
    /// map markers <c>PotentialDealerPoI</c>/<c>DealerPoI</c> + <c>UpdatePotentialDealerPoI()</c>, and recruited-only
    /// dialogue choices <c>recruitChoice</c>/<c>collectCashChoice</c>/<c>assignCustomersChoice</c>.
    ///
    /// The Dealer's own Awake/initialization can set IsRecruited AFTER our OnCreated runs (order varies per NPC —
    /// this is why one dealer showed up half-recruited while others didn't). So we refresh the map PoI and re-apply
    /// a few times over the next couple seconds to win the race regardless of when the Dealer initializes.
    ///
    /// IMPORTANT — save safety: we only reset a dealer to potential when it is "falsely recruited", i.e.
    /// IsRecruited=true WHILE its relationship is still locked (Unlocked=false). A dealer the player legitimately
    /// recruited has Unlocked=true, so on save reload we leave it recruited and never un-recruit it.
    /// </summary>
    internal static class DealerStateHelper
    {
        public static void EnsurePotentialDealer(object s1apiDealer)
        {
            if (s1apiDealer == null) return;
            try
            {
                var component = GetComponent(s1apiDealer);
                if (component == null) return;

                ForcePotential(component);
                // Re-apply over time: the game may flip IsRecruited during its own init after OnCreated.
                MelonCoroutines.Start(ReapplyRoutine(component));
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[MoreNPCs] EnsurePotentialDealer failed: {ex.Message}");
            }
        }

        private static object? GetComponent(object s1apiDealer)
        {
            return s1apiDealer.GetType()
                .GetProperty("Component", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(s1apiDealer);
        }

        private static IEnumerator ReapplyRoutine(object dealerComponent)
        {
            // A short burst covers the Dealer's Awake/Start and any late recruited-state application.
            // Stop the moment the relationship reports Unlocked=true (legit progression / save restore) so we
            // never fight a genuine recruit — ForcePotential also guards on this, this just ends the loop early.
            var t = dealerComponent.GetType();
            for (int i = 0; i < 6; i++)
            {
                yield return new WaitForSeconds(0.5f);
                if (IsRelationshipUnlocked(dealerComponent, t)) yield break;
                try { ForcePotential(dealerComponent); } catch { }
            }
        }

        private static void ForcePotential(object dealerComponent)
        {
            var t = dealerComponent.GetType();

            // Save safety: never touch a dealer the player legitimately unlocked/recruited.
            // A genuine recruit has Unlocked=true; the "false recruit" bug is IsRecruited=true while still locked.
            if (IsRelationshipUnlocked(dealerComponent, t))
                return;

            bool wasRecruited = ReadIsRecruited(dealerComponent, t);

            // (1) IsRecruited = false (only reached while relationship is still locked → safe).
            WriteIsRecruited(dealerComponent, t, false);

            // (2) Hide the recruited-only dialogue choices so "trade / collect / assign" don't linger.
            HideRecruitedChoices(dealerComponent, t);

            // (3) Refresh the map marker to the Potential Dealer PoI.
            RefreshPoI(dealerComponent, t);

            if (wasRecruited)
                MelonLogger.Msg($"[MoreNPCs] Reset falsely-recruited dealer to potential ({t.Name}).");
        }

        /// <summary>True if the dealer's relationship is unlocked (player has progressed past the gate).</summary>
        private static bool IsRelationshipUnlocked(object dealerComponent, Type t)
        {
            try
            {
                // Dealer is an NPC; NPC.RelationData is an NPCRelationData with an Unlocked bool.
                var relData = t.GetProperty("RelationData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(dealerComponent);
                if (relData == null) return false;
                var unlockedProp = relData.GetType().GetProperty("Unlocked", BindingFlags.Public | BindingFlags.Instance);
                if (unlockedProp != null && unlockedProp.GetValue(relData) is bool b) return b;
            }
            catch { }
            return false;
        }

        private static bool ReadIsRecruited(object dealerComponent, Type t)
        {
            try
            {
                var prop = t.GetProperty("IsRecruited", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.CanRead && prop.GetValue(dealerComponent) is bool b) return b;
            }
            catch { }
            return false;
        }

        private static void WriteIsRecruited(object dealerComponent, Type t, bool value)
        {
            var prop = t.GetProperty("IsRecruited", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null && prop.CanWrite)
            {
                try { prop.SetValue(dealerComponent, value); return; } catch { }
            }
            var field = t.GetField("_IsRecruited_k__BackingField", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        ?? t.GetField("<IsRecruited>k__BackingField", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            try { field?.SetValue(dealerComponent, value); } catch { }
        }

        /// <summary>Disable the recruited-only dialogue choices (recruit-management: trade/collect/assign).</summary>
        private static void HideRecruitedChoices(object dealerComponent, Type t)
        {
            foreach (var choiceName in new[] { "collectCashChoice", "assignCustomersChoice" })
            {
                try
                {
                    var choice = t.GetProperty(choiceName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(dealerComponent)
                                 ?? t.GetField(choiceName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(dealerComponent);
                    SetChoiceEnabled(choice, false);
                }
                catch { }
            }
        }

        private static void SetChoiceEnabled(object? choice, bool enabled)
        {
            if (choice == null) return;
            var ct = choice.GetType();
            // DialogueChoice usually exposes an 'enabled' bool and/or a GameObject to toggle.
            try
            {
                var enabledProp = ct.GetProperty("enabled", BindingFlags.Public | BindingFlags.Instance);
                if (enabledProp != null && enabledProp.CanWrite) { enabledProp.SetValue(choice, enabled); return; }
            }
            catch { }
            try
            {
                var goProp = ct.GetProperty("gameObject", BindingFlags.Public | BindingFlags.Instance);
                if (goProp?.GetValue(choice) is GameObject go) go.SetActive(enabled);
            }
            catch { }
        }

        private static void RefreshPoI(object dealerComponent, Type t)
        {
            try
            {
                var m = t.GetMethod("UpdatePotentialDealerPoI", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                m?.Invoke(dealerComponent, null);
            }
            catch { }
        }
    }
}
