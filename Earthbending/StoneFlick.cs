using System;
using System.Collections.Generic;
using Alta.Networking.Scripts.Player;
using MelonLoader;
using UnityEngine;

namespace Earthbending {
    // Task 14 (spec 2.3, 2.4, 3.5; rulings C15, C16; Task 17c: Rulings 40, 41): Flick, the server's side, as a part of StoneBody (its own file, as
    // Hold's StoneHold.cs). A checked Flick (StoneBody.Do; StoneRules.CanApply lets it on a loose stone only: Free, Floating,
    // Held, Flicked) makes the stone Flicked: not kinematic, nothing frozen, Discrete, no gravity (useGravity off: the pull
    // holds it up), its own material. A hold on it ends (its hand lets go), a float ends, and a stone another player flicks
    // is taken over by the newest flicker. A player flicks one stone at most: their Flick on another lets the first go (Free).
    // Holds and a flick are independent (a player may hold a stone in each hand and flick a third).
    // Each physics step (FixedUpdate, after the hold's) the server reads its copy of the flicker's two controllers and head
    // (PlayerController.RightController / LeftController / Head, the transforms Hold reads: StoneHold.LiveBody) and:
    //   - pulls the stone toward Pd.FlickTarget: the midpoint of the two hands plus Pd.HoldReach ([EarthModifiers]
    //     HoldDistance + half the stone's size, Ruling 41) along the head's flat forward (the last one that had a flat way, the
    //     claim's yaw at first: a look straight up or down keeps it), with Pd.Follow (Ruling 40: a damped spring at
    //     FlickFollowRate x Pd.WeightFactor, damping FlickDamping; no integral: gravity is off, so nothing needs one), as a force
    //     (the acceleration times the mass), held so no step leaves the stone faster than [EarthModifiers] MaxStoneSpeed (ruling
    //     C36: Pd.CappedPull);
    //   - turns it toward Pd.FlickTurn: up from the B hand to the A hand (the pose's A, the claim's hand), forward the look made
    //     square to it: FlickTurnRate squared x the turn still to go (Pd.TurnVector, the short way) - 2 x FlickTurnDamping x
    //     FlickTurnRate x its spin (Pd.FlickTorque), an acceleration (ForceMode.Acceleration: the same on every stone).
    // Only Stomp (Stomped: it drops) or a break ends a flick (spec 2.4); there is no let-go by speed. Straight, Kick and
    // Uppercut push it and it stays in the hands; Explode marks it; Parry and Hold are refused on it. It is also let go
    // (Free, gravity back) when its flicker is gone (left, no body or controllers, dead, downed) or flicks another stone. A
    // stone never hits the player who flicks it (ruling C16: Hits.TestPlayers). With [EarthLog] Debug: one line per Flick
    // taken and per let-go (why), and once a second per flicked stone its distance from the target, its speed and its angle
    // off the wanted turn. Nothing here throws into the physics step: a failure is warned once and lets go. Nothing is
    // allocated per step.
    internal sealed partial class StoneBody {
        /// <summary>Who flicks the stone: the player, their account, the pose's A hand (0 right, 1 left: the stone's up runs
        /// from the other hand to it), the look it faces, and the Debug line's bookkeeping.</summary>
        sealed class FlickGrip {
            public Player By = null!;
            public ulong Account;
            public int Hand;
            public string Name = "";
            /// <summary>The head's forward made flat and length 1: the last one that had a flat way (the claim's yaw at first).</summary>
            public Vector3 Look;
            public double Since, LogAt;
        }

        /// <summary>A look whose flat part is under this (within about 6 degrees of straight up or down) keeps the last way.</summary>
        const float MinFlatLook = 0.1f;

        /// <summary>Sets <paramref name="kept"/> to <paramref name="look"/> made flat and length 1 when it has a flat way
        /// (<see cref="MinFlatLook"/>); a look straight up or down (or none) keeps the last. Flick's and Hold's look (ruling
        /// C34: StoneHold.cs aims along it too).</summary>
        static void KeepFlatLook(Vector3 look, ref Vector3 kept) {
            var flat = new Vector3(look.x, 0f, look.z);
            if (flat.sqrMagnitude >= MinFlatLook * MinFlatLook) kept = flat.normalized;
        }

        FlickGrip? flick;
        /// <summary>The stone each player flicks, by account (one at most).</summary>
        static readonly Dictionary<ulong, StoneBody> flicking = new Dictionary<ulong, StoneBody>();
        static bool flickFailed, flickLetGoFailed;

        /// <summary>The player flicking it (Task 14), or null. It never hits them (ruling C16: Hits.TestPlayers).</summary>
        internal Player? FlickedBy => flick?.By;

        /// <summary>A checked Flick by <paramref name="by"/> on this stone (<paramref name="was"/> its state: Free, Floating,
        /// Held or Flicked), <paramref name="hand"/> the pose's A hand, <paramref name="flatForward"/> the claim's yaw (the look
        /// until the server's head gives one). Never throws (it runs in FixedUpdate too): a failure is warned once and lets go.</summary>
        void TakeFlick(Player by, int hand, Vector3 flatForward, StoneState was) {
            try { StartFlick(by, hand, flatForward, was); }
            catch (Exception e) {
                if (!flickFailed) { flickFailed = true; MelonLogger.Warning($"[Earth] Flick: taking a stone failed (said once; it is let go): {e}"); }
                else if (Logs.Debug) MelonLogger.Msg($"[Earth] Flick: taking a stone failed again: {e.Message}");
                if (flick != null) LetGoFlick("taking it failed");
                // Flicked with no flicker, or Held whose hold this Flick ended, would float for good
                else if ((State == StoneState.Flicked || (State == StoneState.Held && grip == null)) && body != null) {
                    Loosen();
                    State = StoneState.Free;
                }
            }
        }

        void StartFlick(Player by, int hand, Vector3 flatForward, StoneState was) {
            if (body == null || by == null) return;
            ulong account = Server.AccountOf(by);
            if (flicking.TryGetValue(account, out StoneBody other) && other != null && !ReferenceEquals(other, this))
                other.LetGoFlick(Logs.Debug ? $"its flicker flicked {Kind} #{Id}" : "");
            string from = flick != null && Logs.Debug ? $" (taken from {flick.Name})" : "";
            EndFlicker();   // the newest flicker takes it
            if (grip != null) EndHold(Logs.Debug ? $"Flick by {Server.NameOf(by)} took it" : "");   // a hold ends (ruling C15)
            body.isKinematic = false;
            Detection(StoneState.Flicked);
            body.useGravity = false;   // the pull holds it up; every way out puts it back (LetGoFlick: Loosen; Stomp: its set-up)
            if (body.maxAngularVelocity < 50f) body.maxAngularVelocity = 50f;   // the review of Task 17c, M4: Unity's 7 rad/s capped a turn past about 110 degrees
            body.constraints = RigidbodyConstraints.None;
            EndSlideMaterial();
            body.WakeUp();
            State = StoneState.Flicked;
            double now = Time.fixedTimeAsDouble;
            var look = new Vector3(flatForward.x, 0f, flatForward.z);
            flick = new FlickGrip {
                By = by, Account = account, Hand = hand & 1, Name = Server.NameOf(by),
                Look = look.sqrMagnitude > 1e-6f ? look.normalized : Vector3.forward, Since = now, LogAt = now + 1.0,
            };
            flicking[account] = this;
            if (Logs.Debug)
                MelonLogger.Msg($"[Earth] Flick: {Kind} #{Id} of {OwnerName} taken by {flick.Name} (A hand {HandName(flick.Hand)}){from}: {was} -> Flicked, {body.mass:0.#} kg, follows at {Server.FlickFollowRate * Pd.WeightFactor(body.mass):0.##} rad/s (x {Pd.WeightFactor(body.mass):0.###} for its weight), damping {Server.FlickDamping:0.##}, no gravity; its centre {Pd.HoldReach(Kind, Server.HoldDistance):0.##} m ahead of the hands (HoldDistance {Server.HoldDistance:0.##} + half its size); turns at {Server.FlickTurnRate:0.##} rad/s, damping {Server.FlickTurnDamping:0.##}");
        }

        // Each physics step of a flick, after the state's own step and the hold's: the flicker's hands and look, the pull
        // toward the target and the turn toward the wanted one, then (Debug) the once-a-second line.
        void DriveFlick() {
            FlickGrip? f = flick;
            if (f == null) return;
            try {
                if (body == null || body.isKinematic || State != StoneState.Flicked) { LetGoFlick(Logs.Debug ? $"it is {State}" : ""); return; }
                if (!HandsOf(f, out Transform handA, out Transform handB, out Vector3 look, out string gone)) { LetGoFlick(gone); return; }
                KeepFlatLook(look, ref f.Look);
                V3 a = V(handA.position), b = V(handB.position), way = V(f.Look);
                V3 target = Pd.FlickTarget(a, b, way, Pd.HoldReach(Kind, Server.HoldDistance));   // Ruling 41: HoldDistance + half its size
                V3 error = target - V(body.worldCenterOfMass);
                V3 vel = V(body.velocity);
                V3 pull = Pd.Follow(error, vel, body.mass, false, Server.FlickFollowRate, Server.FlickDamping, Time.fixedDeltaTime) * body.mass;   // Ruling 40
                Vector3 force = U(Pd.CappedPull(pull, vel, body.mass, Time.fixedDeltaTime, Server.MaxStoneSpeed));   // ruling C36
                if (force != Vector3.zero) body.AddForce(force, ForceMode.Force);
                (V3 forward, V3 up) turn = Pd.FlickTurn(a, b, way);
                Quaternion togo = Quaternion.LookRotation(U(turn.forward), U(turn.up)) * Quaternion.Inverse(body.rotation);
                V3 off = Pd.TurnVector(togo.x, togo.y, togo.z, togo.w);
                Vector3 torque = U(Pd.FlickTorque(off, V(body.angularVelocity), Server.FlickTurnRate, Server.FlickTurnDamping, Time.fixedDeltaTime));
                if (torque != Vector3.zero) body.AddTorque(torque, ForceMode.Acceleration);
                body.WakeUp();   // NetworkRigidbody sends it while it is awake (ruling 21)
                if (Logs.Debug && Time.fixedTimeAsDouble >= f.LogAt) LogFlick(f, target, error, off);
            }
            catch (Exception e) {
                if (!flickFailed) { flickFailed = true; MelonLogger.Warning($"[Earth] Flick: flicking a stone failed (said once; it is let go): {e}"); }
                else if (Logs.Debug) MelonLogger.Msg($"[Earth] Flick: flicking a stone failed again: {e.Message}");
                LetGoFlick("its flick failed");
            }
        }

        // Debug, once a second per flicked stone: its distance from the target, its speed, its angle off the wanted turn and
        // its spin.
        void LogFlick(FlickGrip f, V3 target, V3 error, V3 off) {
            double now = Time.fixedTimeAsDouble;
            float speed = body != null ? body.velocity.magnitude : 0f, spin = body != null ? body.angularVelocity.magnitude : 0f;
            MelonLogger.Msg($"[Earth] Flick: {Kind} #{Id} flicked by {f.Name} for {now - f.Since:0.#} s: {error.Length:0.###} m from its target {target} at {speed:0.##} m/s; {off.Length * Mathf.Rad2Deg:0.#} degrees off the wanted turn, spinning {spin:0.##} rad/s");
            f.LogAt = now + 1.0;
        }

        /// <summary>The flicker's two hands on the server (their controllers' transforms: <paramref name="a"/> the pose's A
        /// hand, <paramref name="b"/> the other) and their look (the head's forward), or why the flick ends: they left, have no
        /// body or no controllers there, died or are downed (StoneHold's LiveBody, Hold's own check).</summary>
        static bool HandsOf(FlickGrip f, out Transform a, out Transform b, out Vector3 look, out string why) {
            a = null!;
            b = null!;
            look = Vector3.zero;
            PlayerController? pc = LiveBody(f.By, out Gone gone);
            if (pc == null) { why = GoneWhy(gone, true); return false; }
            Controller right = pc.RightController, left = pc.LeftController;
            if (right == null || left == null) { why = GoneWhy(Gone.NoHand, true); return false; }
            a = (f.Hand == 0 ? right : left).transform;
            b = (f.Hand == 0 ? left : right).transform;
            Transform head = pc.Head;
            if (head != null) look = head.forward;
            why = "";
            return true;
        }

        /// <summary>Lets the flicked stone go: Free with gravity back (<see cref="Loosen"/>), keeping its velocity held to
        /// [EarthModifiers] MaxStoneSpeed (ruling C10). One Debug line with <paramref name="why"/> (callers build it only with
        /// [EarthLog] Debug). Nothing when it is not flicked; never throws.</summary>
        void LetGoFlick(string why) {
            FlickGrip? f = flick;
            if (f == null) return;
            EndFlicker();
            float speed = 0f;
            try {
                if (body != null && !broken && State == StoneState.Flicked) {
                    Loosen();
                    State = StoneState.Free;
                    Vector3 v = body.velocity, kept = U(StoneRules.Pushed(V(v), V3.Zero, body.mass, Server.MaxStoneSpeed));
                    if (kept != v) body.velocity = kept;
                    speed = kept.magnitude;
                    body.WakeUp();
                }
            }
            catch (Exception e) {
                if (!flickLetGoFailed) { flickLetGoFailed = true; MelonLogger.Warning($"[Earth] Flick: letting a stone go failed (said once): {e.Message}"); }
            }
            if (Logs.Debug) MelonLogger.Msg($"[Earth] Flick: {Kind} #{Id} of {OwnerName} let go by {f.Name} after {Time.fixedTimeAsDouble - f.Since:0.##} s at {transform.position}: {why}; {State} at {speed:0.##} m/s");
        }

        /// <summary>The flick ends where the caller sets the state itself (Stomp, a break): one Debug line with
        /// <paramref name="why"/>, the physics left to the caller. Nothing when it is not flicked.</summary>
        void EndFlick(string why) {
            FlickGrip? f = flick;
            if (f == null) return;
            EndFlicker();
            if (Logs.Debug) MelonLogger.Msg($"[Earth] Flick: {Kind} #{Id} of {OwnerName} dropped by {f.Name} after {Time.fixedTimeAsDouble - f.Since:0.##} s: {why}");
        }

        // Forgets the flicker, and the player's record of this stone.
        void EndFlicker() {
            FlickGrip? f = flick;
            flick = null;
            if (f == null) return;
            if (flicking.TryGetValue(f.Account, out StoneBody known) && (known == null || ReferenceEquals(known, this))) flicking.Remove(f.Account);
        }
    }
}
