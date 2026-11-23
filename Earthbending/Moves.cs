using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace Earthbending {
    // Games only: pushing the local player (Jump, Dash, Straight's pushback, and the server's knockback through message 19) and
    // the hitstun that follows a knockback (spec 3.1 "Push the local player", 3.6, 3.7).
    //
    // How the game moves the player (SmoothLocomotion.UpdateInput, read in _decomp 2026-11-22):
    //   - SmoothLocomotion.velocity (public) is added to each frame's move; gravity is Physics.gravity.y x GravityMultiplier (1)
    //     while airborne or rising; friction decays the flat part.
    //   - While grounded with velocity.y <= 0.1 the velocity is zeroed, so a push keeps y at least MinLift (0.11).
    //   - Fall damage counts from the highest point since the player last stood (fallFromHeight); TreatAsGrounded() on the push
    //     starts that count at the push. The game's curve (SmoothLocomotionSettings.fallDamageOverDistance) hurts from a 3 m
    //     drop, so a 6 m/s push straight up (2.25 m apex) lands unhurt on level ground.
    //   - NOT in spec 3.1: while standable ground is within about 0.3 m under the feet (IsCloseToGround), every frame's move
    //     gets a -0.2 m snap down (SlopeHelperDistance), and SmoothLocomotion.Move then cuts the velocity to what the
    //     CharacterController really managed (zero, against the ground). On level ground that kills any push slower than about
    //     0.2 m per frame up (18 m/s at 90 fps) on its first frame. So while a push still rises, IsCloseToGround answers false
    //     for the local player (NoSnapWhileRising): the player leaves the ground; from the top of the arc the snap is back.
    //   - Nothing moves a player who is downed or dead (ShouldApplyGravityAndMovement), so no push is given then.
    internal static class Moves {
        /// <summary>The game zeroes a grounded player's velocity while its y is at most 0.1 m/s.</summary>
        const float MinLift = 0.11f;
        /// <summary>A safety net: the ground snap is held off at most this long after a push (a 6 m/s rise takes 0.75 s).</summary>
        const float MaxRiseSeconds = 3f;

        static SmoothLocomotion? rising;   // the local player's locomotion while a push still rises
        static float riseUntil;
        static float stunUntil = float.NegativeInfinity;
        static bool warnedNoLocomotion;

        /// <summary>From Main.OnInitializeMelon on games: the server's knockback pushes the local player and stuns them.</summary>
        internal static void Init() {
            Net.KnockbackReceived += OnKnockback;
        }

        static void OnKnockback(Vector3 velocity, float hitstun) {
            Push(velocity);
            Stun(hitstun);
        }

        /// <summary>In hitstun after a knockback: no structure, modifier or movement fires (spec 2.6, 3.3).</summary>
        internal static bool Stunned => Time.time < stunUntil;

        /// <summary>Starts (or lengthens) the hitstun; 0, a negative or a broken number does nothing.</summary>
        internal static void Stun(float seconds) {
            if (!(seconds > 0f) || float.IsInfinity(seconds)) return;
            stunUntil = Mathf.Max(stunUntil, Time.time + seconds);
            if (Logs.Debug) MelonLogger.Msg($"[Earth] Moves: hitstun {seconds:0.###} s");
        }

        /// <summary>Sets the local player's velocity to <paramref name="velocity"/> (m/s, world space), with y at least 0.11 so
        /// the game does not zero it, and starts the fall-damage count here. False (nothing done) on the server, with no local
        /// player, while downed or dead, or for a velocity that is not a number.</summary>
        internal static bool Push(Vector3 velocity) {
            if (Main.IsServer) return false;
            if (!Finite(velocity)) { MelonLogger.Warning($"[Earth] Moves: a push of {velocity} is not a velocity: ignored"); return false; }
            PlayerController pc = PlayerController.Current;
            if (pc == null) {
                if (Logs.Debug) MelonLogger.Msg("[Earth] Moves: no push: no local player");
                return false;
            }
            if (pc.IsDowned || !pc.IsAlive) {
                if (Logs.Debug) MelonLogger.Msg($"[Earth] Moves: no push of {velocity}: the player is {(pc.IsDowned ? "downed" : "dead")}");
                return false;
            }
            var smooth = pc as SmoothLocomotionPlayerController;
            SmoothLocomotion? loco = smooth != null ? smooth.SmoothLocomotion : null;
            if (loco == null) {
                if (!warnedNoLocomotion) { warnedNoLocomotion = true; MelonLogger.Warning($"[Earth] Moves: the local player ({pc.GetType().Name}) has no SmoothLocomotion: pushes do nothing"); }
                return false;
            }
            if (velocity.y < MinLift) velocity.y = MinLift;
            loco.velocity = velocity;
            loco.TreatAsGrounded();
            rising = loco;
            riseUntil = Time.time + MaxRiseSeconds;
            if (Logs.Debug) MelonLogger.Msg($"[Earth] Moves: push {velocity} (grounded {(loco.IsGrounded ? "yes" : "no")})");
            return true;
        }

        static bool Finite(Vector3 v) => !float.IsNaN(v.x) && !float.IsInfinity(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.z);

        // While a push rises, the local player is never "close to the ground", so the game's -0.2 m ground snap does not pull
        // them back down (see the header). Ends at the top of the arc (y no longer above 0, which also covers a push cut short
        // by a ceiling) or after MaxRiseSeconds; then the game's answer stands again.
        [HarmonyPatch(typeof(SmoothLocomotion), "IsCloseToGround")]
        static class NoSnapWhileRising {
            static void Postfix(SmoothLocomotion __instance, ref bool __result) {
                if (!__result || rising == null || !ReferenceEquals(__instance, rising)) return;
                if (__instance.velocity.y > 0f && Time.time < riseUntil) { __result = false; return; }
                rising = null;
            }
        }
    }
}
