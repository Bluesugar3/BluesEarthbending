using System;
using System.Collections.Generic;
using Alta.Networking;
using Alta.Networking.Scripts.Player;
using MelonLoader;
using UnityEngine;

namespace Earthbending {
    // Task 13 (spec 2.3, 2.4, 3.2, 3.5; rulings C13, C14, C16): Hold, the server's side, as a part of StoneBody (its own file
    // to keep StoneBody.cs short). A checked Hold (StoneBody.Do; one taken while Rising waits for the rise, ruling C6) grips the
    // stone with the player's holding hand (the pose's A hand: 0 right, 1 left):
    //   - a grounded stone (Still, Sliding) slides, held (Sliding: ruling 22's slide set-up, gravity on); the pull is along x and
    //     z only, x [EarthModifiers] HoldGroundScale, and while held its slide neither slows nor stands still (StoneBody.Slide);
    //   - a loose one (Free, Floating, Held) is Held: not kinematic, nothing frozen, Discrete, and no gravity (useGravity off:
    //     the PD's force holds it up); the pull is on all three axes, x HoldFreeScale. Every way out of Held puts gravity back
    //     (the let-go and Kick/Uppercut: Loosen; Stomp: its set-up; Parry: Floating has none).
    // Each physics step (FixedUpdate, after the state's own step) the server reads its copy of the holder's controller
    // (PlayerController.RightController / LeftController .transform, a NetworkTransform about 22 ms plus ping behind) and adds
    // Pd.Hold's force toward Pd.HoldTarget: the hand plus [EarthModifiers] HoldDistance along the hand's flat forward (the
    // look's, when the hand points within 45 degrees of up or down, as the Hold pose's fingers do). The stone is let go
    // (StoneRules.HoldLetGo) once it moves faster than HoldLetGoSpeed (the throw: it keeps its velocity, held to MaxStoneSpeed
    // by ruling C10), counted only once it has settled near its target since the hold began: in one step within HoldArmMetres
    // of it (flat, on a held slide: Pd.Trail) and at HoldLetGoSpeed or slower (rulings C27, C28, StoneRules.HoldArmed: a stone
    // caught flying or pulled in from afar is not thrown by its own rush to the hand); or at once when it changes between
    // grounded and free (a held slide left the ground); and when the holder is gone (left,
    // no body or hand, dead, downed), the same hand strikes Hold on it again (StoneRules.HoldAgain), that hand takes another
    // stone, another modifier changes its state (Kick, Uppercut, Stomp, Parry; Straight and Explode keep it held), or it
    // breaks. A Held stone let go is Free (gravity back); a held slide slides on as any slide (slows, stands still). A Hold by
    // another hand or player takes the stone over. A player holds at most one stone per hand (both hands may each hold one).
    // A stone never hits the player who holds it (ruling C16: Hits.TestPlayers). With [EarthLog] Debug: one line per Hold
    // taken and per let-go (why), and once a second per held stone the server's hand, the target, how far the stone trails it
    // and its speed (spec 4, unknown 3). Nothing here throws into the physics step: a failure is warned once and lets go.
    internal sealed partial class StoneBody {
        /// <summary>Who holds the stone: the player, their account, the hand (0 right, 1 left), whether the hold is grounded (a
        /// held slide) or free (Held), and the Debug line's bookkeeping.</summary>
        sealed class Grip {
            public Player By = null!;
            public ulong Account;
            public int Hand;
            public string Name = "";
            public bool Grounded;
            public double Since, LogAt, LoggedAt;
            public Vector3 LoggedHand;
            public bool Logged;
            /// <summary>Rulings C27, C28: it has settled near its target since the hold began, so its speed test counts.</summary>
            public bool Armed;
            /// <summary>Physics steps driven so far (a stone not armed at its first step was caught fast).</summary>
            public int Steps;
        }

        Grip? grip;
        /// <summary>The stone each player's hand holds, keyed by <see cref="HandKey"/>.</summary>
        static readonly Dictionary<ulong, StoneBody> gripped = new Dictionary<ulong, StoneBody>();
        static bool holdFailed, letGoFailed;

        /// <summary>The player holding it (Task 13), or null. It never hits them (ruling C16: Hits.TestPlayers).</summary>
        internal Player? Holder => grip?.By;

        static ulong HandKey(ulong account, int hand) => (account << 1) | (uint)(hand & 1);

        static string HandName(int hand) => hand == 0 ? "right" : "left";

        /// <summary>A checked Hold by <paramref name="by"/>'s <paramref name="hand"/> on this stone, <paramref name="was"/> its
        /// state and <paramref name="next"/> StoneRules.After's (Sliding or Held). The same hand on the stone it holds lets it
        /// go (StoneRules.HoldAgain); a hand that holds another stone lets that one go first; a stone held by another hand or
        /// player is taken over. Never throws (a Hold taken while Rising is applied in FixedUpdate): a failure is warned once
        /// and lets go.</summary>
        void TakeHold(Player by, int hand, StoneState was, StoneState next) {
            try { Take(by, hand, was, next); }
            catch (Exception e) {
                if (!holdFailed) { holdFailed = true; MelonLogger.Warning($"[Earth] Hold: taking a stone failed (said once; it is let go): {e}"); }
                else if (Logs.Debug) MelonLogger.Msg($"[Earth] Hold: taking a stone failed again: {e.Message}");
                if (grip != null) LetGoHold("taking it failed");
                else if (State == StoneState.Held && body != null) {   // Held with no grip would float for good
                    Loosen();
                    State = StoneState.Free;
                }
            }
        }

        void Take(Player by, int hand, StoneState was, StoneState next) {
            if (body == null || by == null) return;
            ulong account = Server.AccountOf(by);
            int heldBy = grip != null && grip.Account == account ? grip.Hand : -1;
            if (StoneRules.HoldAgain(heldBy, hand)) { LetGoHold("its holder struck Hold on it again with the same hand"); return; }
            if (gripped.TryGetValue(HandKey(account, hand), out StoneBody other) && other != null && !ReferenceEquals(other, this))
                other.LetGoHold(Logs.Debug ? $"that hand took {Kind} #{Id}" : "");
            string from = grip != null && Logs.Debug ? $" (taken from {grip.Name}'s {HandName(grip.Hand)} hand)" : "";
            EndGrip();
            if (next == StoneState.Sliding) {
                if (was != StoneState.Sliding) StartSlide();   // ruling C14: a Still stone slides, held
            }
            else {   // Held: loose, no gravity (the pull holds it up), nothing frozen
                body.isKinematic = false;
                Detection(StoneState.Held);
                body.useGravity = false;
                body.constraints = RigidbodyConstraints.None;
                EndSlideMaterial();
                body.WakeUp();
            }
            State = next;
            double now = Time.fixedTimeAsDouble;
            grip = new Grip { By = by, Account = account, Hand = hand & 1, Name = Server.NameOf(by), Grounded = StoneRules.IsGrounded(next), Since = now, LogAt = now + 1.0 };
            gripped[HandKey(account, hand)] = this;
            if (Logs.Debug) {
                (float p, float dxz, float dy) g = Pd.HoldGains(body.mass);
                MelonLogger.Msg($"[Earth] Hold: {Kind} #{Id} of {OwnerName} taken by {grip.Name}'s {HandName(grip.Hand)} hand{from}: {was} -> {next}, {body.mass:0.#} kg, p {g.p:0.##}, d {g.dxz:0.##} ({g.dy:0.##} up) per {Server.PdStepSeconds:0.###} s, x {(grip.Grounded ? Server.HoldGroundScale : Server.HoldFreeScale):0.#}{(grip.Grounded ? " along the ground" : ", no gravity")}; {Server.HoldDistance:0.##} m ahead of the hand, let go past {Server.HoldLetGoSpeed:0.##} m/s");
            }
        }

        // Each physics step of a hold, after the state's own step (a held slide that left the ground is Free by now): the
        // holder and the target, the arming and the let-go (StoneRules.HoldArmed, HoldLetGo, in the order the tests' model
        // runs them), then the pull toward the target, then (Debug) the once-a-second line.
        void DriveHold() {
            Grip? g = grip;
            if (g == null) return;
            try {
                if (body == null || body.isKinematic) { LetGoHold(Logs.Debug ? $"it is {State} and does not move" : ""); return; }
                bool groundedChanged = StoneRules.IsGrounded(State) != g.Grounded;
                if (!groundedChanged && State != StoneState.Held && State != StoneState.Sliding) { LetGoHold(Logs.Debug ? $"it is {State}" : ""); return; }
                if (!HandOf(g, out Transform hand, out Vector3 look, out string gone)) { LetGoHold(gone); return; }
                float speed = body.velocity.magnitude;
                Vector3 at = hand.position;
                V3 target = Pd.HoldTarget(V(at), V(hand.forward), V(look), Server.HoldDistance);
                V3 error = target - V(body.worldCenterOfMass);
                float trail = Pd.Trail(error, g.Grounded);
                bool wasArmed = g.Armed;
                g.Armed = StoneRules.HoldArmed(wasArmed, trail, speed, Server.HoldArmMetres, Server.HoldLetGoSpeed);   // rulings C27, C28
                if (Logs.Debug && g.Armed && !wasArmed && g.Steps > 0)
                    MelonLogger.Msg($"[Earth] Hold: {Kind} #{Id} held by {g.Name} has settled {trail:0.##} m from its place at {speed:0.##} m/s after {Time.fixedTimeAsDouble - g.Since:0.##} s; a throw counts from now (past {Server.HoldLetGoSpeed:0.##} m/s)");
                g.Steps++;
                if (StoneRules.HoldLetGo(speed, groundedChanged, Server.HoldLetGoSpeed, g.Armed)) {
                    LetGoHold(!Logs.Debug ? "" : groundedChanged ? $"it {(g.Grounded ? "left the ground" : "became grounded")} ({State})"
                        : $"thrown at {speed:0.##} m/s (past [EarthModifiers] HoldLetGoSpeed {Server.HoldLetGoSpeed:0.##})");
                    return;
                }
                Vector3 force = U(Pd.Hold(error, V(body.velocity), body.mass, g.Grounded, Server.HoldFreeScale, Server.HoldGroundScale, Server.PdStepSeconds));
                if (force != Vector3.zero) {
                    body.AddForce(force, ForceMode.Force);
                    body.WakeUp();   // NetworkRigidbody sends it while it is awake (ruling 21)
                }
                if (Logs.Debug && Time.fixedTimeAsDouble >= g.LogAt) LogHold(g, at, target, trail, speed);
            }
            catch (Exception e) {
                if (!holdFailed) { holdFailed = true; MelonLogger.Warning($"[Earth] Hold: holding a stone failed (said once; it is let go): {e}"); }
                else if (Logs.Debug) MelonLogger.Msg($"[Earth] Hold: holding a stone failed again: {e.Message}");
                LetGoHold("its hold failed");
            }
        }

        // Debug, once a second per held stone (spec 4, unknown 3): the server's copy of the hand and how fast it moved since
        // the last line, the target, how far the stone trails it (flat for a held slide) and its speed.
        void LogHold(Grip g, Vector3 hand, V3 target, float trail, float speed) {
            double now = Time.fixedTimeAsDouble;
            string moved = g.Logged && now > g.LoggedAt ? $", moving {(hand - g.LoggedHand).magnitude / (float)(now - g.LoggedAt):0.##} m/s" : "";
            MelonLogger.Msg($"[Earth] Hold: {Kind} #{Id} in {g.Name}'s {HandName(g.Hand)} hand for {now - g.Since:0.#} s ({State}): the server's hand at {V(hand)}{moved}, the target {target}; the stone trails it by {trail:0.###} m{(g.Grounded ? " (flat)" : "")} at {speed:0.##} m/s{(g.Armed ? "" : $" (not settled yet: a throw counts once it is within {Server.HoldArmMetres:0.##} m at {Server.HoldLetGoSpeed:0.##} m/s or slower)")}");
            g.LogAt = now + 1.0;
            g.LoggedAt = now;
            g.LoggedHand = hand;
            g.Logged = true;
        }

        /// <summary>The holder's hand on the server (their controller's transform) and their look (the head's forward), or why
        /// the hold ends: they left (not in the game's list, or no live connection), have no body or no controller there, died
        /// or are downed.</summary>
        static bool HandOf(Grip g, out Transform hand, out Vector3 look, out string why) {
            hand = null!;
            look = Vector3.zero;
            Player p = g.By;
            Connection? c = p != null && Player.AllPlayers.Contains(p) ? p.ConnectionToRemotePlayer : null;
            if (p == null || c == null || c.IsDisposed) { why = "its holder left"; return false; }
            PlayerController pc = p.PlayerController;
            if (pc == null) { why = "its holder has no body"; return false; }
            if (!pc.IsAlive) { why = "its holder died"; return false; }
            if (pc.IsDowned) { why = "its holder is downed"; return false; }
            Controller ctl = g.Hand == 0 ? pc.RightController : pc.LeftController;
            if (ctl == null) { why = "its holder has no controller for that hand on the server"; return false; }
            hand = ctl.transform;
            Transform head = pc.Head;
            if (head != null) look = head.forward;
            why = "";
            return true;
        }

        /// <summary>Lets go (spec 2.4): it keeps its velocity (the throw), held to [EarthModifiers] MaxStoneSpeed (ruling C10);
        /// a Held stone is Free with gravity back (<see cref="Loosen"/>); a held slide slides on as any slide. One Debug line
        /// with <paramref name="why"/> (callers build it only with [EarthLog] Debug). Nothing when it is not held; never
        /// throws.</summary>
        void LetGoHold(string why) {
            Grip? g = grip;
            if (g == null) return;
            EndGrip();
            float speed = 0f;
            try {
                if (body != null && !broken) {
                    if (State == StoneState.Held) {
                        Loosen();
                        State = StoneState.Free;
                    }
                    if (!body.isKinematic) {
                        Vector3 v = body.velocity, kept = U(StoneRules.Pushed(V(v), V3.Zero, body.mass, Server.MaxStoneSpeed));
                        if (kept != v) body.velocity = kept;
                        speed = kept.magnitude;
                        body.WakeUp();
                    }
                }
            }
            catch (Exception e) {
                if (!letGoFailed) { letGoFailed = true; MelonLogger.Warning($"[Earth] Hold: letting a stone go failed (said once): {e.Message}"); }
            }
            if (Logs.Debug) MelonLogger.Msg($"[Earth] Hold: {Kind} #{Id} of {OwnerName} let go from {g.Name}'s {HandName(g.Hand)} hand after {Time.fixedTimeAsDouble - g.Since:0.##} s at {transform.position}: {why}; {State} at {speed:0.##} m/s");
        }

        /// <summary>The hold ends where the caller sets the state itself (a modifier that changed it, a break): one Debug line
        /// with <paramref name="why"/>, the physics left to the caller. Nothing when it is not held.</summary>
        void EndHold(string why) {
            Grip? g = grip;
            if (g == null) return;
            EndGrip();
            if (Logs.Debug) MelonLogger.Msg($"[Earth] Hold: {Kind} #{Id} of {OwnerName} let go from {g.Name}'s {HandName(g.Hand)} hand after {Time.fixedTimeAsDouble - g.Since:0.##} s: {why}");
        }

        // Forgets the grip, and the hand's record of this stone.
        void EndGrip() {
            Grip? g = grip;
            grip = null;
            if (g == null) return;
            ulong key = HandKey(g.Account, g.Hand);
            if (gripped.TryGetValue(key, out StoneBody known) && (known == null || ReferenceEquals(known, this))) gripped.Remove(key);
        }
    }
}
