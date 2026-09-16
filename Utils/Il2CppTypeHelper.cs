using System;

namespace MoreNPCs.Utils
{
    /// <summary>
    /// Bridges reflection-obtained <see cref="System.Type"/> values to the type that Unity's
    /// runtime APIs (GetComponent, FindObjectsOfType, FindObjectsOfTypeAll, …) expect.
    ///
    /// On the IL2CPP build those overloads take <c>Il2CppSystem.Type</c>, so we convert with
    /// <c>Il2CppInterop.Runtime.Il2CppType.From</c>. On the Mono/CrossCompat builds the overloads
    /// take a plain <see cref="System.Type"/>, so we pass it straight through. Callers use the
    /// returned value directly and stay identical across configurations.
    /// </summary>
    internal static class Il2CppTypeHelper
    {
        /// <summary>
        /// Resolves a game type by full name across all loaded assemblies. On the IL2CPP build the real
        /// game types live under the <c>Il2CppScheduleOne.*</c> namespace, so if a plain <c>ScheduleOne.*</c>
        /// name is passed we also try the <c>Il2Cpp</c>-prefixed variant (and vice versa). This lets existing
        /// reflection lookups keep passing their original strings while still resolving at runtime.
        /// </summary>
        // Cache resolved (and failed) lookups. Resolving a type walks every loaded assembly, and this is called
        // very frequently (BuildingSetup, GameDealerFinder per-root, NPCIdleLocations). Caching removes that cost
        // after the first lookup. Failed lookups are cached as null so we don't rescan assemblies repeatedly.
        private static readonly System.Collections.Generic.Dictionary<string, Type?> _typeCache =
            new System.Collections.Generic.Dictionary<string, Type?>(System.StringComparer.Ordinal);

        public static Type? ResolveGameType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;

            if (_typeCache.TryGetValue(fullName, out var cached))
                return cached;

            var candidates = new System.Collections.Generic.List<string>(3) { fullName };
            if (fullName.StartsWith("ScheduleOne.", System.StringComparison.Ordinal))
                candidates.Add("Il2Cpp" + fullName);
            else if (fullName.StartsWith("Il2CppScheduleOne.", System.StringComparison.Ordinal))
                candidates.Add(fullName.Substring("Il2Cpp".Length));

            var assemblies = System.AppDomain.CurrentDomain.GetAssemblies();
            foreach (var name in candidates)
            {
                foreach (var asm in assemblies)
                {
                    try
                    {
                        var t = asm.GetType(name);
                        if (t != null) { _typeCache[fullName] = t; return t; }
                    }
                    catch { }
                }
            }
            _typeCache[fullName] = null;
            return null;
        }

#if IL2CPP
        /// <summary>Convert a managed <see cref="System.Type"/> to the Il2Cpp type Unity APIs expect.</summary>
        public static Il2CppSystem.Type? To(Type? managedType)
        {
            if (managedType == null) return null;
            // throwOnFailure=false: reflection can hand us types with no Il2Cpp counterpart; return null instead of crashing.
            return Il2CppInterop.Runtime.Il2CppType.From(managedType, throwOnFailure: false);
        }

        /// <summary>
        /// Builds an <c>Il2CppSystem.Guid</c> from a string. Game members like NPCEnterableBuilding.GUID are
        /// <c>Il2CppSystem.Guid</c>, and a managed <see cref="System.Guid"/> cannot be passed to them via reflection
        /// ("cannot be converted to type 'Il2CppSystem.Guid'"). This boxes the correct Il2Cpp type for such setters.
        /// </summary>
        public static object? MakeIl2CppGuid(string guidString)
        {
            try { return new Il2CppSystem.Guid(guidString); }
            catch { return null; }
        }
#else
        /// <summary>Mono/CrossCompat: Unity APIs already accept System.Type, so pass through.</summary>
        public static Type? To(Type? managedType) => managedType;

        /// <summary>Mono/CrossCompat: game members take a plain System.Guid.</summary>
        public static object? MakeIl2CppGuid(string guidString)
        {
            return Guid.TryParse(guidString, out var g) ? (object)g : null;
        }
#endif
    }
}
