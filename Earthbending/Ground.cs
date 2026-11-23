using UnityEngine;

namespace Earthbending {
    // Natural ground ("dirt"), as the game's spade decides it (DiggingImpactor): the collider actually under the point is on
    // Terrain 8, Ground 10 or GroundOverlay 11 and has no attachedRigidbody. One cast decides: the first solid thing below, on
    // every layer but the players' bodies, hands, triggers and water, so a floor on any other layer above the terrain reads
    // "not natural", and so does anything loose or one of our own stones (each has a Rigidbody, and sits on layer 0). The
    // server's summon alone looks past our stones (UnderPastStones, ruling C3).
    internal static class Ground {
        // 2 Ignore Raycast, 4 Water, 9 Player, 12 Interact Trigger, 15 Hand Triggers, 24 CharacterPart, 25 SmoothLocomotion,
        // 28 LocalPlayerIdentifier, 30 PlayerPersonalSpace
        internal const int SolidMask = ~((1 << 2) | (1 << 4) | (1 << 9) | (1 << 12) | (1 << 15) | (1 << 24) | (1 << 25) | (1 << 28) | (1 << 30));
        internal const int NaturalLayers = (1 << 8) | (1 << 10) | (1 << 11);
        internal const float Depth = 3f;

        internal static bool IsNatural(Collider? c) => c != null && (NaturalLayers & (1 << c.gameObject.layer)) != 0 && c.attachedRigidbody == null;

        /// <summary>Casts <see cref="Depth"/> m straight down from <paramref name="from"/> on <see cref="SolidMask"/>, triggers
        /// ignored. True when the first hit is natural ground. <paramref name="hit"/> is that hit, or default for none.</summary>
        internal static bool Under(Vector3 from, out RaycastHit hit) {
            if (!Physics.Raycast(from, Vector3.down, out hit, Depth, SolidMask, QueryTriggerInteraction.Ignore)) return false;
            return IsNatural(hit.collider);
        }

        /// <summary>The summon's cast on the server (ruling C3): as <see cref="Under"/>, but it looks past our own stones (a
        /// StoneBody): the first hit that is not one decides, so a stone standing on the spot leaves the ground under it to
        /// decide (and the summon lifts that stone off: Server.LiftOff). Every other test, the player's own included, is
        /// <see cref="Under"/>: a player standing on a stone is not on natural ground.</summary>
        internal static bool UnderPastStones(Vector3 from, out RaycastHit hit) {
            hit = default;
            foreach (RaycastHit h in Physics.RaycastAll(from, Vector3.down, Depth, SolidMask, QueryTriggerInteraction.Ignore)) {
                if (h.collider == null || (hit.collider != null && h.distance >= hit.distance)) continue;
                if (h.collider.GetComponentInParent<StoneBody>() != null) continue;
                hit = h;
            }
            return IsNatural(hit.collider);
        }

        /// <summary>[EarthLog] Debug, every 2 s on a game: what is under the local player's feet, as <see cref="Under"/> sees it.</summary>
        internal static string Describe(Vector3 feet) {
            bool natural = Under(feet + Vector3.up * 0.5f, out RaycastHit hit);
            if (hit.collider == null) return $"[Earth] Ground: layer -, nothing solid within {Depth - 0.5f:0.#} m under the feet, material -, natural no";
            PhysicMaterial m = hit.collider.sharedMaterial;
            return $"[Earth] Ground: layer {hit.collider.gameObject.layer}, {hit.collider.name}, material {(m != null ? m.name : "none")}, natural {(natural ? "yes" : "no")}";
        }
    }
}
