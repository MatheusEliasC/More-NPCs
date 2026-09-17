using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using MoreNPCs.Utils;

namespace MoreNPCs.Patches
{
    /// <summary>
    /// Ensures every <c>NPCEnterableBuilding</c> has a valid <c>BakedGUID</c> before its native Awake parses it.
    ///
    /// WHY: the game's <c>NPCEnterableBuilding.Awake()</c> does <c>new Guid(BakedGUID)</c>. The mod fabricates
    /// custom buildings at runtime via <c>AddComponent</c> (Utils/BuildingSetup.cs), which fires Awake synchronously
    /// with an empty <c>BakedGUID</c> — throwing "Unrecognized Guid format", so the building never registers and any
    /// NPC whose schedule targets it (StayInBuilding / Dealer Home) gets stuck at spawn.
    ///
    /// This Prefix runs at the very start of Awake and, if <c>BakedGUID</c> is missing/blank, writes a valid GUID
    /// string first (preferring the value BuildingSetup pre-seeded for the component it is creating). Vanilla
    /// buildings already have a baked GUID, so they are left untouched.
    /// </summary>
    [HarmonyPatch]
    internal static class NPCEnterableBuildingAwakeGuidPatch
    {
        /// <summary>GUID string that BuildingSetup wants applied to the component it is about to AddComponent.</summary>
        internal static string? PreseedGuid;

        private static Type? _type;
        private static PropertyInfo? _bakedGuidProp;
        private static FieldInfo? _bakedGuidField;

        private static bool Prepare()
        {
            _type = Il2CppTypeHelper.ResolveGameType("ScheduleOne.Map.NPCEnterableBuilding");
            if (_type == null)
            {
                MelonLogger.Msg("[MoreNPCs] NPCEnterableBuilding Awake GUID patch skipped: type not found.");
                return false;
            }
            _bakedGuidProp = _type.GetProperty("BakedGUID", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            _bakedGuidField = _type.GetField("BakedGUID", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return _bakedGuidProp != null || _bakedGuidField != null;
        }

        private static MethodBase TargetMethod()
        {
            return _type!.GetMethod("Awake", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null)!;
        }

        private static PropertyInfo? _doorsProp;
        private static PropertyInfo? _occupantsProp;
        private static float _nextErrLog;

        private static void Prefix(object __instance)
        {
            try
            {
                // 1) Valid GUID so `new Guid(BakedGUID)` in Awake doesn't throw FormatException.
                var current = ReadBakedGuid(__instance);
                if (!IsValidGuid(current))
                {
                    var guid = !string.IsNullOrEmpty(PreseedGuid) && IsValidGuid(PreseedGuid)
                        ? PreseedGuid!
                        : Guid.NewGuid().ToString();
                    WriteBakedGuid(__instance, guid);
                }

                // 2) Initialize collections Awake may iterate (Doors array / Occupants list). On fabricated
                //    buildings these are null when Awake runs (we call GetDoors() only afterwards), and Awake
                //    dereferencing them causes the NullReferenceException seen after the GUID fix.
                EnsureCollectionsInitialized(__instance);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[MoreNPCs] NPCEnterableBuilding Awake prefix failed: {ex.Message}");
            }
        }

        /// <summary>Swallow any residual NRE from Awake so a fabricated building doesn't crash the trampoline.</summary>
        private static Exception? Finalizer(Exception? __exception)
        {
            if (__exception == null) return null;
            if (UnityEngine.Time.time >= _nextErrLog)
            {
                _nextErrLog = UnityEngine.Time.time + 30f;
                MelonLogger.Msg($"[MoreNPCs] Suppressed NPCEnterableBuilding.Awake {__exception.GetType().Name} (fabricated building). Throttled 30s.");
            }
            return null;
        }

        private static void EnsureCollectionsInitialized(object instance)
        {
            _type ??= instance.GetType();
            _doorsProp ??= _type.GetProperty("Doors", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            _occupantsProp ??= _type.GetProperty("Occupants", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            TryInitEmpty(instance, _doorsProp);
            TryInitEmpty(instance, _occupantsProp);
        }

        private static void TryInitEmpty(object instance, PropertyInfo? prop)
        {
            if (prop == null || !prop.CanRead || !prop.CanWrite) return;
            try
            {
                if (prop.GetValue(instance) != null) return; // already set
                // Instantiate the exact IL2CPP type (Il2CppReferenceArray<T> needs an int-length ctor; List<T> a default ctor).
                var t = prop.PropertyType;
                object? value = null;
                var lenCtor = t.GetConstructor(new[] { typeof(long) }) ?? t.GetConstructor(new[] { typeof(int) });
                if (lenCtor != null)
                {
                    var p = lenCtor.GetParameters()[0].ParameterType;
                    value = lenCtor.Invoke(new object[] { p == typeof(long) ? (object)0L : 0 });
                }
                else
                {
                    var defCtor = t.GetConstructor(Type.EmptyTypes);
                    if (defCtor != null) value = defCtor.Invoke(null);
                }
                if (value != null) prop.SetValue(instance, value);
            }
            catch { /* best-effort */ }
        }

        private static string? ReadBakedGuid(object instance)
        {
            if (_bakedGuidProp != null && _bakedGuidProp.CanRead)
                return _bakedGuidProp.GetValue(instance) as string;
            return _bakedGuidField?.GetValue(instance) as string;
        }

        private static void WriteBakedGuid(object instance, string value)
        {
            if (_bakedGuidProp != null && _bakedGuidProp.CanWrite)
            {
                _bakedGuidProp.SetValue(instance, value);
                return;
            }
            _bakedGuidField?.SetValue(instance, value);
        }

        private static bool IsValidGuid(string? s) => !string.IsNullOrEmpty(s) && Guid.TryParse(s, out _);
    }
}
