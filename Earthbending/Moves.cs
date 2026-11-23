using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace Earthbending {
    // Games only: pushing the local player (Jump, Dash, Straight's pushback, and the server's knockback through message 19) and
    // the hitstun that follows a knockback (spec 3.1 "Push the local player", 3.6, 3.7). [EarthMovement] PushEndSpeed and
    // PushMaxSeconds, per game.
    //
    // How the game moves the player (SmoothLocomotion.UpdateInput and ApplyGravityAndMovement, read in _decomp 2026-11-22):
    //   - SmoothLocomotion.velocity (public) is added to each frame's move; gravity is Physics.gravity.y x GravityMultiplier (1)
    //     while airborne or rising; friction (GetFrictionMultiplier, 1 by default) takes velocity x 1 x deltaTime off the flat
    //     part every frame.
    //   - While grounded (and nothing overrides movement, e.g. climbing) with velocity.y <= 0.1 the velocity is zeroed. A push
    //     keeps y at least MinLift (0.11) for its first frame, and while it lasts the zeroing is held off (KeepWhilePushed), so a
    //     push along the ground slides and the game's own friction slows it.
    //   - Fall damage counts from the highest point since the player last stood (fallFromHeight); TreatAsGrounded() on the push
    //     starts that count at the push. The game's curve (SmoothLocomotionSettings.fallDamageOverDistance) hurts from a 3 m
    //     drop, so a 6 m/s push straight up (2.25 m apex) lands unhurt on level ground.
    //   - NOT in spec 3.1: while standable ground is within about 0.3 m under the feet (IsCloseToGround), every frame's move
    //     gets a -0.2 m snap down (SlopeHelperDistance), and SmoothLocomotion.Move then cuts the velocity to what the
    //     CharacterController really managed (zero, against the ground). On level ground that kills any push slower than about
    //     0.2 m per frame up (18 m/s at 90 fps) on its first frame. So while a push still rises, IsCloseToGround answers false
    //     for the local player (NoSnapWhileRising): the player leaves the ground; from the top of the arc the snap is back
    //     (it keeps a slide on the ground, downhill too).
    //   - Nothing moves a player who is downed or dead (ShouldApplyGravityAndMovement), so no push is given then, and a held
    //     push ends.
    internal static class Moves {
        /// <summary>The game zeroes a grounded player's velocity while its y is at most 0.1 m/s.</summary>
        const float GameZeroBelow = 0.1f;
        const float MinLift = 0.11f;
        /// <summary>A safety net: the ground snap is held off at most this long after a push (a 6 m/s rise takes 0.75 s).</summary>
        const float MaxRiseSeconds = 3f;
        const float DefaultPushEndSpeed = 0.3f, DefaultPushMaxSeconds = 1.5f;

        static MelonPreferences_Entry<float>? pushEndSpeed, pushMaxSeconds;
        static float endSpeed = DefaultPushEndSpeed, maxSeconds = DefaultPushMaxSeconds;

        static SmoothLocomotion? rising;   // the local player's locomotion while a push still rises (ground snap held off)
        static float riseUntil;
        static SmoothLocomotion? holding;  // ... while a push lasts (grounded zeroing held off)
        static float holdUntil, pushedAt;
        static float stunUntil = float.NegativeInfinity;
        static bool warnedNoLocomotion;

        /// <summary>From Main.OnInitializeMelon on games: the [EarthMovement] settings, and the server's knockback pushes the
        /// local player and stuns them.</summary>
        internal static void Init() {
            MelonPreferences_Category cat = Cfg.Category("EarthMovement");
            pushEndSpeed = cat.CreateEntry("PushEndSpeed", DefaultPushEndSpeed, description: "Per game. A push (a jump, a dash, Straight's pushback, a knockback) slides the player along the ground until its flat speed falls under this, in m/s; the game's own friction slows it.");
            pushMaxSeconds = cat.CreateEntry("PushMaxSeconds", DefaultPushMaxSeconds, description: "Per game. The longest a push slides the player along the ground, in seconds after the push (0: a push stops as soon as the player stands).");
            Apply();
            Cfg.Changed += Apply;
            Net.KnockbackReceived += OnKnockback;
        }

        static void Apply() {
            endSpeed = Read(pushEndSpeed, "PushEndSpeed", DefaultPushEndSpeed);
            maxSeconds = Read(pushMaxSeconds, "PushMaxSeconds", DefaultPushMaxSeconds);
        }

        static float Read(MelonPreferences_Entry<float>? e, string key, float fallback) {
            if (e == null) return fallback;
            float v = e.Value;
            if (v >= 0f && !float.IsInfinity(v)) return v;
            MelonLogger.Warning($"[Earth] Settings: [EarthMovement] {key} {v} is not 0 or more; using {fallback}");
            return fallback;
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

        /// <summary>Adds <paramref name="velocity"/> (m/s, world space) to the local player's velocity, so pushes stack (spec
        /// 3.7), with y then at least 0.11 so the game does not zero it, and starts the fall-damage count here. The push lasts
        /// until its flat speed falls under [EarthMovement] PushEndSpeed or PushMaxSeconds have passed. False (nothing done) on
        /// the server, with no local player, while downed or dead, or for a velocity that is not a number.</summary>
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
            Vector3 was = loco.velocity;
            Vector3 v = Finite(was) ? was + velocity : velocity;
            if (v.y < MinLift) v.y = MinLift;
            loco.velocity = v;
            loco.TreatAsGrounded();
            float now = Time.time;
            rising = loco;
            riseUntil = now + MaxRiseSeconds;
            holding = loco;
            pushedAt = now;
            holdUntil = now + maxSeconds;
            if (Logs.Debug) MelonLogger.Msg($"[Earth] Moves: push {velocity} on {was} -> {v} (grounded {(loco.IsGrounded ? "yes" : "no")})");
            return true;
        }

        static bool Finite(Vector3 v) => !float.IsNaN(v.x) && !float.IsInfinity(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.z);

        /// <summary>In place of the 0.1 in the game's grounded zeroing test (<c>velocity.y &lt;= 0.1f</c> in
        /// SmoothLocomotion.ApplyGravityAndMovement; reached only while grounded and nothing overrides movement): 0.1, or minus
        /// infinity (never zero) while the local player's push lasts. Called every grounded frame, so it ends the push too.</summary>
        internal static float ZeroBelow(SmoothLocomotion s) {
            try {
                if (!ReferenceEquals(s, holding)) return GameZeroBelow;
                Vector3 v = s.velocity;
                float flat = v.x * v.x + v.z * v.z;
                float now = Time.time;
                PlayerController pc = PlayerController.Current;
                bool standing = pc != null && pc.IsAlive && !pc.IsDowned;
                if (standing && now < holdUntil && flat >= endSpeed * endSpeed) return float.NegativeInfinity;
                holding = null;
                if (Logs.Debug) MelonLogger.Msg($"[Earth] Moves: push ended after {now - pushedAt:0.00} s ({(!standing ? "downed or dead" : now >= holdUntil ? "PushMaxSeconds" : "slower than PushEndSpeed")}; flat speed {Mathf.Sqrt(flat):0.00} m/s)");
            }
            catch (Exception e) {
                holding = null;
                MelonLogger.Warning($"[Earth] Moves: a push could not be held: {e.Message}");
            }
            return GameZeroBelow;
        }

        // The grounded zeroing in SmoothLocomotion.ApplyGravityAndMovement (IL, Root.Township.dll, 2026-11-22):
        //   ldarg.0; ldflda velocity; ldfld y; ldc.r4 0.1; bgt.un.s KEEP; ldarg.0; call Vector3.get_zero; stfld velocity; ret
        // The ldc.r4 0.1 (the method's only one, never a branch target) becomes ldarg.0; call Moves.ZeroBelow(SmoothLocomotion),
        // so the test is velocity.y <= ZeroBelow(this). Everything after KEEP (the friction, then gravity unless grounded with
        // y <= 0) is the game's own and runs as before. If the method does not look like that, nothing is changed: one warning,
        // and pushes along the ground stop when the player stands, as in the game.
        [HarmonyPatch(typeof(SmoothLocomotion), "ApplyGravityAndMovement")]
        static class KeepWhilePushed {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
                var list = new List<CodeInstruction>(instructions);
                try {
                    int at = -1, found = 0;
                    for (int i = 0; i < list.Count; i++) {
                        if (list[i].opcode == OpCodes.Ldc_R4 && list[i].operand is float f && f == GameZeroBelow) { at = i; found++; }
                    }
                    bool branch = at >= 0 && at + 1 < list.Count && (list[at + 1].opcode == OpCodes.Bgt_Un_S || list[at + 1].opcode == OpCodes.Bgt_Un);
                    var helper = AccessTools.Method(typeof(Moves), nameof(ZeroBelow));
                    if (found != 1 || !branch || helper == null) {
                        MelonLogger.Warning($"[Earth] Moves: the game's grounded zeroing test was not where expected ({found} x 0.1, branch {(branch ? "yes" : "no")}): pushes along the ground stop when the player stands");
                        return list;
                    }
                    var load = new CodeInstruction(OpCodes.Ldarg_0);
                    load.labels.AddRange(list[at].labels);
                    load.blocks.AddRange(list[at].blocks);
                    var copy = new List<CodeInstruction>(list);
                    copy[at] = load;
                    copy.Insert(at + 1, new CodeInstruction(OpCodes.Call, helper));
                    return copy;
                }
                catch (Exception e) {
                    MelonLogger.Warning($"[Earth] Moves: could not patch the game's grounded zeroing ({e.Message}): pushes along the ground stop when the player stands");
                    return list;
                }
            }
        }

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
