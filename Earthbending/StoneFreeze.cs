using System;
using MelonLoader;
using UnityEngine;

namespace Earthbending {
    // Task 15 (spec 2.2, 3.7; rulings C17, C18, C19): the hitstop, the server's side, as a part of StoneBody (its own file, as
    // Hold's StoneHold.cs and Flick's StoneFlick.cs). Two stones that clash (Hits.Clash, on their powers at the contact) both
    // freeze for StoneRules.HitstopSeconds ([EarthDamage] HitstopSeconds + HitstopPerPower x both powers):
    //   - Freeze keeps the rigidbody's kinematic flag, constraints, gravity and collision mode, and its velocity
    //     and spin from before the physics step of the contact (Velocity, AngularVelocity: what CarryOn gave a winner before
    //     Task 15), then stops it where it is: no velocity or spin, Discrete collision (Task 11: never ContinuousDynamic on a
    //     kinematic body), kinematic. Its kept Velocity reads zero. The games within 80 m get Applied(Hitstop) at its place
    //     (they shake its model, StoneLook.cs).
    //   - While frozen (StoneBody.FixedUpdate returns at once): no rise, slide, float, hold or flick drive, no launch's second
    //     push, no players' test. A held or flicked stone stays held or flicked and is driven again after. A modifier taken
    //     while frozen waits until the freeze ends, as while Rising (StoneBody.Apply; Explode marks at once, and a double
    //     Straight bursts at once). Ruling C19: no contact breaks it (Hits: StoneRules.Breakable), a second clash does
    //     nothing, a stomped one does not land, it is not lifted off by a summon, and a blast leaves it alone; its life, its
    //     owner leaving, the server's quit, `earth clear` and a double Straight still break it, and then what its clash would
    //     have done next is dropped (Break clears it; the other stone's outcome still happens). A let-go in the freeze (its
    //     holder's hand took another stone, its flicker flicked another) sets the kept physics to Free's (Loosen).
    //   - In the first physics step at or past its end (FixedUpdate, counted on Time.fixedTimeAsDouble, never a coroutine):
    //     Thaw. The loser (both on a tie) breaks (a marked one blasts then), with its kept velocity back for the
    //     blast's heading and power. The winner gets its kept physics back (a Still winner stays Still: kinematic) and carries
    //     on at its kept velocity and spin (CarryOn), then its state's own step runs as usual, a modifier it took while frozen
    //     first. A loser that thaws before its winner (two stones' FixedUpdates run in no set order) thaws the winner first, so
    //     a marked loser's blast adds to the speed the winner carries on at. The time spent frozen does not count toward a
    //     slide's time off the ground or a float's time.
    // With [EarthLog] Debug: one line per freeze and one per clash outcome after it (a loser's is its break line). Nothing here
    // throws into the physics step: a failure is warned once, then Debug lines, and a loser still breaks.
    internal sealed partial class StoneBody {
        const double FreezeEps = 1e-6;   // seconds: a physics step that lands on the freeze's end counts as past it

        bool frozen;
        double frozenAt, frozenUntil;   // Time.fixedTimeAsDouble at the contact, and when the freeze ends
        StoneBody? frozenWith;          // the other stone of the clash (null for a freeze of its own)
        string? breakAfter;             // the break's reason when this stone lost the clash; null: it carries on
        Vector3 keptVelocity, keptSpin;
        bool keptKinematic, keptGravity;
        RigidbodyConstraints keptConstraints;
        CollisionDetectionMode keptDetection;
        static bool hitstopSendFailed, thawFailed;

        /// <summary>Task 15: frozen in a hitstop (spec 2.2: frozen, unbreakable, shaking). Ruling C19: no contact breaks it.</summary>
        internal bool Frozen => frozen;

        /// <summary>Freezes the stone where it is for <paramref name="seconds"/> (see the file's header), then it carries on as
        /// it was, or breaks when <paramref name="breakWhy"/> (the break's reason) is given: a clash's loser (Hits.Clash, with
        /// <paramref name="with"/> the other stone). Called from a collision callback, after the physics step, its kept
        /// velocity and spin are those from before that step. True when it froze; false, and nothing changes, when it is
        /// broken, already frozen (a second clash does nothing), has no rigidbody, or the time is not above 0 (or not
        /// finite).</summary>
        internal bool Freeze(float seconds, StoneBody? with = null, string? breakWhy = null) {
            if (broken || frozen || body == null || !(seconds > 0f) || float.IsInfinity(seconds)) return false;
            keptVelocity = Velocity;
            keptSpin = AngularVelocity;
            keptKinematic = body.isKinematic;
            keptGravity = body.useGravity;
            keptConstraints = body.constraints;
            keptDetection = body.collisionDetectionMode;
            if (!body.isKinematic) {
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            if (body.collisionDetectionMode != CollisionDetectionMode.Discrete) body.collisionDetectionMode = CollisionDetectionMode.Discrete;   // before it is kinematic
            body.isKinematic = true;
            Velocity = Vector3.zero;
            LastVelocity = Vector3.zero;
            AngularVelocity = Vector3.zero;
            frozen = true;
            frozenAt = Time.fixedTimeAsDouble;
            frozenUntil = frozenAt + seconds;
            frozenWith = with;
            breakAfter = breakWhy;
            Vector3 at = transform.position;
            int games = 0;
            try { games = Net.SendApplied(Id, Wire.Hitstop, at); }
            catch (Exception e) {
                if (!hitstopSendFailed) { hitstopSendFailed = true; MelonLogger.Warning($"[Earth] Stones: telling the games a stone froze failed (said once): {e.Message}"); }
            }
            if (Logs.Debug)
                MelonLogger.Msg($"[Earth] Stones: {Kind} #{Id} of {OwnerName} freezes for {seconds:0.###} s at {at} ({State}, {keptVelocity.magnitude:0.##} m/s kept){(breakWhy != null ? "; then it breaks" : with != null ? "; then it carries on" : "")}; the shake goes to {games} game(s)");
            return true;
        }

        /// <summary>From FixedUpdate while frozen: false while the freeze lasts (nothing else runs this step); at or past its
        /// end, <see cref="Thaw"/>, then whether the stone still stands (a loser has broken).</summary>
        bool Thawed() {
            if (Time.fixedTimeAsDouble < frozenUntil - FreezeEps) return false;
            Thaw();
            return !broken;
        }

        /// <summary>The freeze ends (see the file's header): the loser breaks, the winner (or a freeze of its own) gets its kept
        /// physics back and carries on at its kept velocity and spin. Nothing when it is not frozen; never throws.</summary>
        void Thaw() {
            if (!frozen) return;
            frozen = false;   // first: the other stone's thaw, run from here, must not come back to this one
            StoneBody? with = frozenWith;
            string? why = breakAfter;
            frozenWith = null;
            breakAfter = null;
            double now = Time.fixedTimeAsDouble, lasted = now - frozenAt;
            try {
                // the winner first, so a marked loser's blast adds to the speed it carries on at (a frozen stone is not blasted)
                if (why != null && with != null && with.frozen && with.breakAfter == null && now >= with.frozenUntil - FreezeEps) with.Thaw();
                Velocity = keptVelocity;   // the blast's heading and power (a loser's), the speed it carries on at (the winner's)
                AngularVelocity = keptSpin;
                if (why != null) {
                    Break(Logs.Debug ? $"{why}, after a {lasted:0.###} s hitstop" : why);
                    return;
                }
                groundAt += lasted;   // the time frozen is neither time off the ground for a slide ...
                floatAt += lasted;    // ... nor time afloat for a parried stone
                if (body == null) return;
                body.isKinematic = keptKinematic;
                if (!keptKinematic && body.collisionDetectionMode != keptDetection) body.collisionDetectionMode = keptDetection;   // once it is not kinematic
                body.constraints = keptConstraints;
                body.useGravity = keptGravity;
                body.WakeUp();   // NetworkRigidbody sends it while it is awake (ruling 21)
                bool carried = CarryOn();
                if (Logs.Debug)
                    MelonLogger.Msg($"[Earth] Stones: {Kind} #{Id} of {OwnerName}'s hitstop ended after {lasted:0.###} s at {transform.position}: {(carried ? $"it carries on at {Velocity.magnitude:0.##} m/s" : "it stands (kinematic)")} ({State})");
            }
            catch (Exception e) {
                if (!thawFailed) { thawFailed = true; MelonLogger.Warning($"[Earth] Stones: ending a stone's hitstop failed (said once): {e}"); }
                else if (Logs.Debug) MelonLogger.Msg($"[Earth] Stones: ending a stone's hitstop failed again: {e.Message}");
                if (why != null) Break("its clash's hitstop failed");   // a loser still breaks
            }
        }

        /// <summary>A let-go in the freeze (Loosen): Free's physics (not kinematic, gravity, nothing frozen, Discrete) once it
        /// ends, the body left frozen until then.</summary>
        void LoosenKept() {
            keptKinematic = false;
            keptGravity = true;
            keptConstraints = RigidbodyConstraints.None;
            keptDetection = CollisionDetectionMode.Discrete;
        }

        /// <summary>From Break: the freeze is dropped with what its clash would have done next (ruling C19). True when it was frozen.</summary>
        bool DropFreeze() {
            if (!frozen) return false;
            frozen = false;
            frozenWith = null;
            breakAfter = null;
            return true;
        }
    }
}
