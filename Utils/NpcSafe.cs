using S1API.Entities;

namespace MoreNPCs.Utils
{
    /// <summary>
    /// Safe accessors for NPC members whose S1API getters can throw at runtime.
    ///
    /// <para><see cref="NPC.Movement"/> is a lazy wrapper: the getter constructs an
    /// <c>S1API.Entities.NPCMovement</c> the first time it is called, and that constructor touches the
    /// underlying game component (e.g. <c>Behaviour.enabled</c>). If the NPC's native movement component
    /// is not initialized yet (spawn still in progress) or already destroyed, the getter throws a
    /// <see cref="System.NullReferenceException"/> before returning — so a plain <c>npc?.Movement == null</c>
    /// guard does NOT protect callers. These helpers wrap the access in try/catch.</para>
    /// </summary>
    internal static class NpcSafe
    {
        /// <summary>Returns the NPC's Movement wrapper, or null if the NPC is null or the getter throws.</summary>
        public static NPCMovement? Movement(NPC npc)
        {
            if (npc == null) return null;
            try { return npc.Movement; }
            catch { return null; }
        }
    }
}
