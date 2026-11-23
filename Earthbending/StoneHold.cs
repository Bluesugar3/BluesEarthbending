using System;
using System.Collections.Generic;
using Alta.Networking;
using Alta.Networking.Scripts.Player;
using MelonLoader;
using UnityEngine;

namespace Earthbending {
    // Task 13 (spec 2.3, 2.4, 3.2, 3.5; rulings C14, C16; Task 17c: Rulings 40, 41, 43): Hold, the server's side, as a part of StoneBody (its own file
    // to keep StoneBody.cs short). A checked Hold (StoneBody.Do; one taken while Rising waits for the rise, ruling C6) grips the
    // stone with the player's holding hand (the pose's A hand: 0 right, 1 left):
    //   - a grounded stone (Still, Sliding) slides, held (Sliding: ruling 22's slide set-up, gravity on); the pull is along x and
    //     z only, and while held its slide neither slows nor stands still (StoneBody.Slide);
    //   - a loose one (Free, Floating, Held) is Held: not kinematic, nothing frozen, Discrete, and no gravity (useGravity off:
    //     the pull holds it up); the pull is on all three axes. Every way out of Held puts gravity back (the let-go and
    //     Kick/Uppercut: Loosen; Stomp: its set-up; Parry: Floating has none).
    // Each physics step (FixedUpdate, after the state's own step) the server reads its copy of the holder's controller
    // (PlayerController.RightController / LeftController .transform, a NetworkTransform about 22 ms plus ping behind) and head
    // and adds Pd.Follow's pull (Ruling 40: a damped spring at [EarthModifiers] HoldFollowRate x Pd.WeightFactor, damping
    // HoldDamping; the acceleration times the mass) toward Pd.HoldTarget: the hand plus Pd.HoldReach (HoldDistance to the
    // stone's near side, Ruling 41) along the holder's look made
    // flat (PlayerController.Head's forward; the last one that had a flat way, the claim's yaw at first: StoneFlick's
    // KeepFlatLook, as Flick; ruling C34). Never the controller's own forward: its +z is not the fingers on every controller,
    // and the finger axis is a per-game [EarthPose] setting the server cannot see. The pull is held so no step leaves the
    // stone faster than [EarthModifiers] MaxStoneSpeed (ruling C36, Pd.CappedPull: it grows with the distance, as for a stone
    // stuck behind something or a holder who teleports). Ruling 43: each step the server's copies of the hand and the head go
    // into a HandTrack, which gives the hand's velocity RELATIVE TO THE HEAD and the head's own over about 0.1 s. The stone is
    // thrown (StoneRules.HoldLetGo) once the hand, relative to the head, moves faster than HoldLetGoSpeed (walking, a dash or
    // a knock-back move hand and head together and never throw; a head turn swings the target but not the hand): it takes
    // the head's velocity + the hand's relative velocity x HoldThrowScale when that is faster than its own (Pd.ThrowVelocity;
    // a held slide only the flat part), held to MaxStoneSpeed. The throw counts only once the stone has settled near its
    // target since the hold began: in one step within HoldArmMetres of it (flat, on a held slide: Pd.Trail) while the hand
    // was at HoldLetGoSpeed or slower (rulings C27, C28, StoneRules.HoldArmed: a stone caught flying or pulled in from afar
    // settles first). It is let go with no throw when its velocity is not a number, or at once when it changes between
    // grounded and free (a held slide left the ground); and when the holder is gone (left,
    // no body or hand, dead, downed), the same hand strikes Hold on it again (StoneRules.HoldAgain), that hand takes another
    // stone, another modifier changes its state (Kick, Uppercut, Stomp, Parry; Straight and Explode keep it held), or it
    // breaks. A Held stone let go is Free (gravity back); a held slide slides on as any slide (slows, stands still). A Hold by
    // another hand or player takes the stone over. A player holds at most one stone per hand (both hands may each hold one).
    // A stone never hits the player who holds it (ruling C16: Hits.TestPlayers). With [EarthLog] Debug: one line per Hold
    // taken and per let-go (why), and once a second per held stone the server's hand and its speed relative to the head, the
    // target, how far the stone trails it and its speed (spec 4, unknown 3). Nothing here throws into the physics step: a failure is warned once and lets go.
    internal sealed partial class StoneBody {
        /// <summary>Who holds the stone: the player, their account, the hand (0 right, 1 left), whether the hold is grounded (a
        /// held slide) or free (Held), and the Debug line's bookkeeping.</summary>
        sealed class Grip {
            public Player By = null!;
            public ulong Account;
            public int Hand;
            public string Name = "";
            public bool Grounded;
            /// <summary>Ruling C34: the holder's look made flat and length 1, the last one that had a flat way (the claim's yaw
            /// at first); the stone hangs HoldDistance ahead of the hand along it.</summary>
            public Vector3 Look;
            public double Since, LogAt;
            /// <summary>Ruling 43: the hand's and the head's recent places, for the hand's speed relative to the head.</summary>
            public readonly HandTrack Track = new HandTrack();
            /// <summary>Rulings C27, C28, 43: it has settled near its target since the hold began, so a fast hand throws it.</summary>
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
        /// state and <paramref name="next"/> StoneRules.After's (Sliding or Held), <paramref name="flatForward"/> the claim's
        /// yaw (the look until the server's head gives one). The same hand on the stone it holds lets it go
        /// (StoneRules.HoldAgain); a hand that holds another stone lets that one go first; a stone held by another hand or
        /// player is taken over. Never throws (a Hold taken while Rising is applied in FixedUpdate): a failure is warned once
        /// and lets go.</summary>
        void TakeHold(Player by, int hand, Vector3 flatForward, StoneState was, StoneState next) {
            try { Take(by, hand, flatForward, was, next); }
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

        void Take(Player by, int hand, Vector3 flatForward, StoneState was, StoneState next) {
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
            Vector3 look = Vector3.forward;
            KeepFlatLook(flatForward, ref look);   // the claim's yaw until the server's head gives a look (ruling C34)
            grip = new Grip { By = by, Account = account, Hand = hand & 1, Name = Server.NameOf(by), Grounded = StoneRules.IsGrounded(next), Look = look, Since = now, LogAt = now + 1.0 };
            gripped[HandKey(account, hand)] = this;
            if (Logs.Debug)
                MelonLogger.Msg($"[Earth] Hold: {Kind} #{Id} of {OwnerName} taken by {grip.Name}'s {HandName(grip.Hand)} hand{from}: {was} -> {next}, {body.mass:0.#} kg, follows at {Server.HoldFollowRate * Pd.WeightFactor(body.mass):0.##} rad/s (x {Pd.WeightFactor(body.mass):0.###} for its weight), damping {Server.HoldDamping:0.##}{(grip.Grounded ? ", along the ground" : ", no gravity")}; its centre {Pd.HoldReach(Kind, Server.HoldDistance):0.##} m ahead of the hand along the look (its near side {Server.HoldDistance:0.##} m), no faster than {Server.MaxStoneSpeed:0.##} m/s; thrown once the hand moves past {Server.HoldLetGoSpeed:0.##} m/s relative to the head, at the swing x {Server.HoldThrowScale:0.##}");
        }

        // Each physics step of a hold, after the state's own step (a held slide that left the ground is Free by now): the
        // holder and the target (along the look, ruling C34), the arming and the let-go (StoneRules.HoldArmed, HoldLetGo, in
        // the order the tests' model runs them), then the pull toward the target held to MaxStoneSpeed (ruling C36), then
        // (Debug) the once-a-second line.
        void DriveHold() {
            Grip? g = grip;
            if (g == null) return;
            try {
                if (body == null || body.isKinematic) { LetGoHold(Logs.Debug ? $"it is {State} and does not move" : ""); return; }
                bool groundedChanged = StoneRules.IsGrounded(State) != g.Grounded;
                if (!groundedChanged && State != StoneState.Held && State != StoneState.Sliding) { LetGoHold(Logs.Debug ? $"it is {State}" : ""); return; }
                if (!HandOf(g, out Transform hand, out Vector3 look, out Vector3 headAt, out bool hasHead, out string gone)) { LetGoHold(gone); return; }
                float speed = body.velocity.magnitude;
                if (float.IsNaN(speed) || float.IsInfinity(speed)) { LetGoHold("its velocity is not a number"); return; }
                Vector3 at = hand.position;
                if (hasHead) g.Track.Add(Time.fixedTimeAsDouble, V(at), V(headAt));   // Ruling 43
                bool tracked = g.Track.Velocities(out V3 handRel, out V3 headVel);
                float handSpeed = tracked ? handRel.Length : float.NaN;   // not a number until 0.1 s of track: no arming, no throw
                KeepFlatLook(look, ref g.Look);   // ruling C34: the look, never the controller's own forward
                V3 target = Pd.HoldTarget(V(at), V(g.Look), Pd.HoldReach(Kind, Server.HoldDistance));   // Ruling 41: to its near side
                V3 error = target - V(body.worldCenterOfMass);
                float trail = Pd.Trail(error, g.Grounded);
                bool wasArmed = g.Armed;
                g.Armed = StoneRules.HoldArmed(wasArmed, trail, handSpeed, Server.HoldArmMetres, Server.HoldLetGoSpeed);   // rulings C27, C28, 43
                if (Logs.Debug && g.Armed && !wasArmed && g.Steps > 0)
                    MelonLogger.Msg($"[Earth] Hold: {Kind} #{Id} held by {g.Name} has settled {trail:0.##} m from its place at {speed:0.##} m/s (the hand {handSpeed:0.##} m/s relative to the head) after {Time.fixedTimeAsDouble - g.Since:0.##} s; a throw counts from now (the hand past {Server.HoldLetGoSpeed:0.##} m/s)");
                g.Steps++;
                if (StoneRules.HoldLetGo(handSpeed, groundedChanged, Server.HoldLetGoSpeed, g.Armed)) {
                    if (groundedChanged) {
                        LetGoHold(Logs.Debug ? $"it {(g.Grounded ? "left the ground" : "became grounded")} ({State})" : "");
                        return;
                    }
                    Vector3 before = body.velocity;   // Ruling 43: the throw takes the swing when it is faster than the stone
                    body.velocity = U(Pd.ThrowVelocity(V(before), headVel, handRel, Server.HoldThrowScale, g.Grounded, Server.MaxStoneSpeed));
                    LetGoHold(Logs.Debug ? $"thrown: the hand at {handSpeed:0.##} m/s relative to the head (past [EarthModifiers] HoldLetGoSpeed {Server.HoldLetGoSpeed:0.##}), the head at {headVel.Length:0.##} m/s; the stone {before.magnitude:0.##} -> {body.velocity.magnitude:0.##} m/s (the swing x {Server.HoldThrowScale:0.##}{(g.Grounded ? ", flat" : "")})" : "");
                    return;
                }
                V3 vel = V(body.velocity);
                V3 pull = Pd.Follow(error, vel, body.mass, g.Grounded, Server.HoldFollowRate, Server.HoldDamping, Time.fixedDeltaTime) * body.mass;   // Ruling 40
                Vector3 force = U(Pd.CappedPull(pull, vel, body.mass, Time.fixedDeltaTime, Server.MaxStoneSpeed));   // ruling C36
                if (force != Vector3.zero) {
                    body.AddForce(force, ForceMode.Force);
                    body.WakeUp();   // NetworkRigidbody sends it while it is awake (ruling 21)
                }
                if (Logs.Debug && Time.fixedTimeAsDouble >= g.LogAt) LogHold(g, at, handSpeed, target, trail, speed);
            }
            catch (Exception e) {
                if (!holdFailed) { holdFailed = true; MelonLogger.Warning($"[Earth] Hold: holding a stone failed (said once; it is let go): {e}"); }
                else if (Logs.Debug) MelonLogger.Msg($"[Earth] Hold: holding a stone failed again: {e.Message}");
                LetGoHold("its hold failed");
            }
        }

        // Debug, once a second per held stone (spec 4, unknown 3): the server's copy of the hand and its speed relative to the
        // head over the last 0.1 s (Ruling 43: what a throw is judged on), the target, how far the stone trails it (flat for a
        // held slide) and its speed.
        void LogHold(Grip g, Vector3 hand, float handSpeed, V3 target, float trail, float speed) {
            double now = Time.fixedTimeAsDouble;
            MelonLogger.Msg($"[Earth] Hold: {Kind} #{Id} in {g.Name}'s {HandName(g.Hand)} hand for {now - g.Since:0.#} s ({State}): the server's hand at {V(hand)}, moving {handSpeed:0.##} m/s relative to the head (a throw past {Server.HoldLetGoSpeed:0.##}), the target {target}; the stone trails it by {trail:0.###} m{(g.Grounded ? " (flat)" : "")} at {speed:0.##} m/s{(g.Armed ? "" : $" (not settled yet: a throw counts once it is within {Server.HoldArmMetres:0.##} m while the hand is at {Server.HoldLetGoSpeed:0.##} m/s or slower)")}");
            g.LogAt = now + 1.0;
        }

        /// <summary>The holder's hand on the server (their controller's transform: only its place is used), their look (the
        /// head's forward; zero when the server has no head for them, which keeps the last flat look) and their head's place
        /// (<paramref name="hasHead"/> false with none), or why the hold ends: they left (not in the game's list, or no live
        /// connection), have no body or no controller there, died or are downed (<see cref="LiveBody"/>).</summary>
        static bool HandOf(Grip g, out Transform hand, out Vector3 look, out Vector3 headAt, out bool hasHead, out string why) {
            hand = null!;
            look = Vector3.zero;
            headAt = Vector3.zero;
            hasHead = false;
            PlayerController? pc = LiveBody(g.By, out Gone gone);
            if (pc == null) { why = GoneWhy(gone, false); return false; }
            Controller ctl = g.Hand == 0 ? pc.RightController : pc.LeftController;
            if (ctl == null) { why = GoneWhy(Gone.NoHand, false); return false; }
            hand = ctl.transform;
            Transform head = pc.Head;
            if (head != null) {
                look = head.forward;
                headAt = head.position;
                hasHead = true;
            }
            why = "";
            return true;
        }

        /// <summary>Why a player's body no longer drives a stone on the server (Hold's holder, Task 14's flicker).</summary>
        enum Gone : byte { None, Left, NoBody, Died, Downed, NoHand }

        /// <summary>The player's body on the server, or null and why it is gone: they left (not in the game's list, or no
        /// live connection), have no body there, died or are downed. Hold's and Flick's (StoneFlick.cs) shared check.</summary>
        static PlayerController? LiveBody(Player p, out Gone gone) {
            Connection? c = p != null && Player.AllPlayers.Contains(p) ? p.ConnectionToRemotePlayer : null;
            if (p == null || c == null || c.IsDisposed) { gone = Gone.Left; return null; }
            PlayerController pc = p.PlayerController;
            if (pc == null) { gone = Gone.NoBody; return null; }
            if (!pc.IsAlive) { gone = Gone.Died; return null; }
            if (pc.IsDowned) { gone = Gone.Downed; return null; }
            gone = Gone.None;
            return pc;
        }

        /// <summary>A let-go's reason for <see cref="Gone"/>, the holder's or the <paramref name="flicker"/>'s (constant
        /// strings: nothing is built per step).</summary>
        static string GoneWhy(Gone g, bool flicker) {
            switch (g) {
                case Gone.Left: return flicker ? "its flicker left" : "its holder left";
                case Gone.NoBody: return flicker ? "its flicker has no body" : "its holder has no body";
                case Gone.Died: return flicker ? "its flicker died" : "its holder died";
                case Gone.Downed: return flicker ? "its flicker is downed" : "its holder is downed";
                case Gone.NoHand: return flicker ? "its flicker has no controller for a hand on the server" : "its holder has no controller for that hand on the server";
                default: return "";
            }
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
