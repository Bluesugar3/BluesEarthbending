using MelonLoader;
using UnityEngine;

namespace Earthbending {
    /// <summary>
    /// For the arena mod (or any other mod) to tell Blue's Earthbending where its ring is, on the dedicated server. A stone that
    /// hits a player on that ring takes fight health instead of real health, and the hit that reaches 0 flings them out
    /// (docs/superpowers/specs/2026-11-23-earthbending-fight-health-design.md). This ring wins over the console's and the
    /// [EarthFight] Ring setting's, and is not drawn on the games (the arena has its own floor). Calls on a game (not the
    /// dedicated server) are ignored. Nothing is saved: set it again after every server start.
    /// </summary>
    public static class Arena {
        /// <summary>The ring: <paramref name="centre"/> on its floor (y is the floor's height), the fighting floor's radius and
        /// the arena's (the pit and the rim round it, at least the ring's), in metres. Setting it (or moving it) ends every fight
        /// on the ring before. Numbers that are not a ring (a radius of 0 or less, an arena smaller than the ring, a number that
        /// is not finite) are refused with a warning, and the ring before stays.</summary>
        public static void SetRing(Vector3 centre, float ringRadius, float arenaRadius) {
            if (!Main.IsServer) return;
            var r = new FightRing(new V3(centre.x, centre.y, centre.z), ringRadius, arenaRadius, false);
            if (!r.Valid) {
                MelonLogger.Warning($"[Earth] Arena: SetRing({centre}, {ringRadius}, {arenaRadius}) is not a ring (radius above 0, arena radius at least the ring's, finite numbers): ignored");
                return;
            }
            FightServer.SetArena(r);
        }

        /// <summary>Removes the ring <see cref="SetRing"/> set: every fight on it ends, and the console's or the setting's ring
        /// (if any) counts again.</summary>
        public static void ClearRing() {
            if (!Main.IsServer) return;
            FightServer.SetArena(null);
        }
    }
}
