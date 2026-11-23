using UnityEngine;

namespace Earthbending {
    // Natural ground ("dirt"), as the game's spade decides it (DiggingImpactor): a collider on Terrain 8, Ground 10 or
    // GroundOverlay 11 that has no attachedRigidbody (a stone, a log or any loose thing has one).
    internal static class Ground {
        internal const int NaturalMask = (1 << 8) | (1 << 10) | (1 << 11);
        internal const float Depth = 3f;

        /// <summary>Casts <see cref="Depth"/> m straight down from <paramref name="from"/> on the natural-ground layers.
        /// True when it hits a collider with no attachedRigidbody. <paramref name="hit"/> is the hit, or default for none.</summary>
        internal static bool Under(Vector3 from, out RaycastHit hit) {
            if (!Physics.Raycast(from, Vector3.down, out hit, Depth, NaturalMask, QueryTriggerInteraction.Ignore)) return false;
            return hit.collider != null && hit.collider.attachedRigidbody == null;
        }

        // The layers the Debug line's second cast ignores: the player's own body, hands and grips (9 Player, 14 Tight Grip,
        // 15 Hand Triggers, 24 CharacterPart, 25 SmoothLocomotion, 28 LocalPlayerIdentifier, 30 PlayerPersonalSpace, 31 Active
        // Tight Grip), so it finds the first solid thing under the feet on any other layer.
        const int OwnBody = (1 << 9) | (1 << 14) | (1 << 15) | (1 << 24) | (1 << 25) | (1 << 28) | (1 << 30) | (1 << 31);

        /// <summary>[EarthLog] Debug, every 2 s on a game: what is under the local player's feet. The first part is
        /// <see cref="Under"/>'s answer; "first solid" is the nearest collider on ANY layer but the player's own, which shows a
        /// floor that the natural-layer cast passes through (a floor on another layer above the terrain).</summary>
        internal static string Describe(Vector3 feet) {
            Vector3 from = feet + Vector3.up * 0.5f;
            bool natural = Under(from, out RaycastHit hit);
            string first = Physics.Raycast(from, Vector3.down, out RaycastHit any, Depth, Physics.DefaultRaycastLayers & ~OwnBody, QueryTriggerInteraction.Ignore)
                ? $"layer {any.collider.gameObject.layer}, {any.collider.name}, {any.distance - 0.5f:0.00} m below the feet"
                : "none";
            if (hit.collider == null)
                return $"[Earth] Ground: layer -, nothing on layers 8, 10, 11 within {Depth - 0.5f:0.#} m, material -, natural no; first solid: {first}";
            PhysicMaterial m = hit.collider.sharedMaterial;
            return $"[Earth] Ground: layer {hit.collider.gameObject.layer}, {hit.collider.name}, material {(m != null ? m.name : "none")}, natural {(natural ? "yes" : "no")}; first solid: {first}";
        }
    }
}
