using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace Earthbending {
    // Games only: pushing the local player (Jump, Dash, Straight's pushback, and the server's knockback through message 19), the
    // hitstun that follows a knockback (spec 3.1 "Push the local player", 3.6, 3.7) and the movement moves' dust (Dust).
    // [EarthMovement] PushEndSpeed, PushMaxSeconds, MaxPushSpeed and Straight's PushbackGrounded, PushbackFree and PushbackLift
    // (Pushback), [EarthLooks] DustEffect, per game. "Grounded" is the game's own flag, LocomotionController.IsGrounded (Grounded).
    //
    // Public, with ZeroBelow: the game's own SmoothLocomotion.ApplyGravityAndMovement calls ZeroBelow once transpiled, and a
    // call into a type the game's assembly may not see could fail in the JIT, which would stop every player's walking.
    //
    // How the game moves the player (SmoothLocomotion.UpdateInput and ApplyGravityAndMovement, read in _decomp 2026-11-22):
    //   - SmoothLocomotion.velocity (public) is added to each frame's move; gravity is Physics.gravity.y x GravityMultiplier (1)
    //     while airborne or rising; friction (GetFrictionMultiplier, 1 by default) takes velocity x 1 x deltaTime off the flat
    //     part every frame.
    //   - While grounded (and nothing overrides movement, e.g. climbing) with velocity.y <= 0.1 the velocity is zeroed. A push
    //     from the ground keeps y at least PushRule.MinLift (0.11) for its first frame, and while it lasts the zeroing is held
    //     off (KeepWhilePushed), so a push along the ground slides and the game's own friction slows it.
    //   - Fall damage counts from the highest point since the player last stood (fallFromHeight); TreatAsGrounded() on a push
    //     from the ground starts that count at the push. The game's curve (SmoothLocomotionSettings.fallDamageOverDistance)
    //     hurts from a 3 m drop, so a 6 m/s push straight up (2.25 m apex) lands unhurt on level ground. A push in the air
    //     only adds (ruling 14, PushRule): a knockback in mid-fall keeps the fall and its damage.
    //   - Every push leaves the player at most [EarthMovement] MaxPushSpeed (20 m/s), however many stack.
    //   - NOT in spec 3.1: while standable ground is within about 0.3 m under the feet (IsCloseToGround), every frame's move
    //     gets a -0.2 m snap down (SlopeHelperDistance), and SmoothLocomotion.Move then cuts the velocity to what the
    //     CharacterController really managed (zero, against the ground). On level ground that kills any push slower than about
    //     0.2 m per frame up (18 m/s at 90 fps) on its first frame. So while a push still rises, IsCloseToGround answers false
    //     for the local player (NoSnapWhileRising): the player leaves the ground; from the top of the arc the snap is back
    //     (it keeps a slide on the ground, downhill too).
    //   - Nothing moves a player who is downed or dead (ShouldApplyGravityAndMovement), so no push is given then, and a held
    //     push ends.
    public static class Moves {
        /// <summary>The game zeroes a grounded player's velocity while its y is at most 0.1 m/s.</summary>
        const float GameZeroBelow = 0.1f;
        /// <summary>A safety net: the ground snap is held off at most this long after a push (a 6 m/s rise takes 0.75 s).</summary>
        const float MaxRiseSeconds = 3f;
        const float DefaultPushEndSpeed = 0.3f, DefaultPushMaxSeconds = 1.5f, DefaultMaxPushSpeed = 20f;
        const float DefaultPushbackGrounded = 2.5f, DefaultPushbackFree = 1.7f, DefaultPushbackLift = 0.25f;
        const string DefaultDustEffect = "GroundPuffSmall";
        /// <summary>A dust effect that is not loaded is looked for again after this long (each search walks every loaded asset).</summary>
        const float DustRetrySeconds = 10f;

        static MelonPreferences_Entry<float>? pushEndSpeed, pushMaxSeconds, maxPushSpeed, pushbackGroundedEntry, pushbackFreeEntry, pushbackLiftEntry;
        static MelonPreferences_Entry<string>? dustEffect;
        static float endSpeed = DefaultPushEndSpeed, maxSeconds = DefaultPushMaxSeconds, maxSpeed = DefaultMaxPushSpeed;
        static float pushbackGrounded = DefaultPushbackGrounded, pushbackFree = DefaultPushbackFree, pushbackLift = DefaultPushbackLift;

        static PooledObjectDefinition? dust;   // the effect found for dustFor
        static string dustFor = "";
        static float dustRetryAt;
        static bool dustWarned, dustFailed;

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
            maxPushSpeed = cat.CreateEntry("MaxPushSpeed", DefaultMaxPushSpeed, description: "Per game. However many pushes stack (jumps, dashes, knockbacks), the player is never left faster than this, in m/s. Above 0.");
            pushbackGroundedEntry = cat.CreateEntry("PushbackGrounded", DefaultPushbackGrounded, description: "Per game. A Straight at a stone anchored to the ground (standing still, or rising) pushes you back this many m/s, away from where you look (half in the air). 0 or more.");
            pushbackFreeEntry = cat.CreateEntry("PushbackFree", DefaultPushbackFree, description: "Per game. A Straight at a stone that is not anchored (loose, or already sliding) pushes you back this many m/s (half in the air). 0 or more.");
            pushbackLiftEntry = cat.CreateEntry("PushbackLift", DefaultPushbackLift, description: "Per game. A Straight's pushback also lifts you this many m/s (half in the air); above 0.1 keeps the game from stopping the push on the ground. 0 or more.");
            dustEffect = Cfg.Category("EarthLooks").CreateEntry("DustEffect", DefaultDustEffect, description: "Per game. The game's pooled effect played at the feet by a jump or a dash (its name; several names split by ; are tried in order, the first loaded one is used; empty: no dust).");
            Apply();
            Cfg.Changed += Apply;
            // a Net whose static initialiser failed (Main says so) throws on every touch: no knockback, the rest still starts
            try { Net.KnockbackReceived += OnKnockback; }
            catch (Exception e) { MelonLogger.Warning($"[Earth] Moves: no knockback from the server (message 19 is not set up: {e.GetType().Name})"); }
        }

        static void Apply() {
            endSpeed = Cfg.NotNegative(pushEndSpeed, "EarthMovement", "PushEndSpeed", DefaultPushEndSpeed);
            maxSeconds = Cfg.NotNegative(pushMaxSeconds, "EarthMovement", "PushMaxSeconds", DefaultPushMaxSeconds);
            maxSpeed = Cfg.Positive(maxPushSpeed, "EarthMovement", "MaxPushSpeed", DefaultMaxPushSpeed);
            pushbackGrounded = Cfg.NotNegative(pushbackGroundedEntry, "EarthMovement", "PushbackGrounded", DefaultPushbackGrounded);
            pushbackFree = Cfg.NotNegative(pushbackFreeEntry, "EarthMovement", "PushbackFree", DefaultPushbackFree);
            pushbackLift = Cfg.NotNegative(pushbackLiftEntry, "EarthMovement", "PushbackLift", DefaultPushbackLift);
            dustRetryAt = 0f;   // a changed name, or the same name saved again, is looked for at the next dust
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
        /// 3.7), held to [EarthMovement] MaxPushSpeed; from the ground y is then at least 0.11 so the game does not zero it, and
        /// the fall-damage count starts here; in the air it only adds (PushRule, ruling 14). The push lasts until its flat speed
        /// falls under [EarthMovement] PushEndSpeed or PushMaxSeconds have passed. False (nothing done) on the server, with no
        /// local player, while downed or dead, or for a velocity that is not a number.</summary>
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
            bool grounded = Grounded(pc);
            V3 next = PushRule.Next(new V3(was.x, was.y, was.z), new V3(velocity.x, velocity.y, velocity.z), grounded, maxSpeed);
            Vector3 v = new Vector3(next.X, next.Y, next.Z);
            loco.velocity = v;
            if (grounded) loco.TreatAsGrounded();
            float now = Time.time;
            rising = loco;
            riseUntil = now + MaxRiseSeconds;
            holding = loco;
            pushedAt = now;
            holdUntil = now + maxSeconds;
            if (Logs.Debug) MelonLogger.Msg($"[Earth] Moves: push {velocity} on {was} -> {v} (grounded {(grounded ? "yes" : "no: it only adds")})");
            return true;
        }

        /// <summary>The game's own grounded flag for <paramref name="pc"/>: LocomotionController.IsGrounded, the one
        /// SmoothLocomotion zeroes a grounded player's velocity on and counts fall damage from (set from the
        /// CharacterController at the end of each update). Not SmoothLocomotion.IsGrounded: that is only the collision flags of
        /// the frame's last Move, the reposition, which often touches nothing below. False with no player or controller.</summary>
        internal static bool Grounded(PlayerController? pc) {
            LocomotionController? lc = pc != null ? pc.LocomotionController : null;
            return lc != null && lc.IsGrounded;
        }

        /// <summary>Straight's push on the local player (spec 3.5): back along <paramref name="yawDeg"/> at [EarthMovement]
        /// PushbackGrounded m/s when the target stone is anchored (<paramref name="targetGrounded"/>: its rigidbody is kinematic
        /// on this game), PushbackFree when it is not, PushbackLift up; half of it in the air (MoveRules.Pushback). False when
        /// no push was given (see <see cref="Push"/>).</summary>
        internal static bool Pushback(float yawDeg, bool targetGrounded) {
            bool airborne = !Grounded(PlayerController.Current);
            V3 v = MoveRules.Pushback(yawDeg, targetGrounded, airborne, pushbackGrounded, pushbackFree, pushbackLift);
            return Push(new Vector3(v.X, v.Y, v.Z));
        }

        /// <summary>[EarthLooks] DustEffect, the game's pooled effect, at <paramref name="at"/> (a jump's or a dash's feet).
        /// Nothing on the server, for an empty setting, or while the effect is not loaded: a name not found is said once and
        /// looked for again after 10 s or a settings reload.</summary>
        internal static void Dust(Vector3 at) {
            if (Main.IsServer) return;
            try {
                PooledObjectDefinition? d = DustDefinition();
                if (d != null) d.Get(at, Quaternion.identity);
            }
            catch (Exception e) {
                if (dustFailed) return;
                dustFailed = true;
                MelonLogger.Warning($"[Earth] Moves: the dust effect failed (said once): {e.Message}");
            }
        }

        static PooledObjectDefinition? DustDefinition() {
            string names = (dustEffect?.Value ?? "").Trim();
            if (names != dustFor) {
                dustFor = names;
                dust = null;
                dustRetryAt = 0f;
                dustWarned = false;
            }
            if (dust != null) return dust;   // Unity's null: an asset unloaded since is looked for again
            if (names.Length == 0 || Time.unscaledTime < dustRetryAt) return null;
            dustRetryAt = Time.unscaledTime + DustRetrySeconds;
            PooledObjectDefinition[] all = Resources.FindObjectsOfTypeAll<PooledObjectDefinition>();
            foreach (string raw in names.Split(';')) {
                string n = raw.Trim();
                if (n.Length == 0) continue;
                foreach (PooledObjectDefinition def in all)
                    if (def != null && (def.name == n || (def.Prefab != null && def.Prefab.name == n))) {
                        dust = def;
                        if (Logs.Debug) MelonLogger.Msg($"[Earth] Moves: dust effect '{n}' found");
                        return dust;
                    }
            }
            if (!dustWarned) {
                dustWarned = true;
                MelonLogger.Warning($"[Earth] Moves: no loaded effect named '{names}' ([EarthLooks] DustEffect): no dust; looked for again every {DustRetrySeconds:0} s");
            }
            return null;
        }

        static bool Finite(Vector3 v) => !float.IsNaN(v.x) && !float.IsInfinity(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.z);

        /// <summary>In place of the 0.1 in the game's grounded zeroing test (<c>velocity.y &lt;= 0.1f</c> in
        /// SmoothLocomotion.ApplyGravityAndMovement; reached only while grounded and nothing overrides movement): 0.1, or minus
        /// infinity (never zero) while the local player's push lasts. Called every grounded frame, so it ends the push too.</summary>
        public static float ZeroBelow(SmoothLocomotion s) {
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
