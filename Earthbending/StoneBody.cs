using System;
using Alta.Networking;
using Alta.Networking.Scripts.Player;
using MelonLoader;
using UnityEngine;

namespace Earthbending {
    /// <summary>The server's side of one standing stone (spec 3.2: the server owns every stone), added by Server.Spawn to the
    /// spawned copy on the server only; games never have one. It knows the stone's kind, owner, state and age. While Rising it
    /// moves the kinematic rigidbody from the start to the end over the rise time (Rigidbody.MovePosition each physics step, and
    /// WakeUp, because NetworkRigidbody sends a body's place only while it is awake: ruling 21; NetworkRigidbody carries the
    /// place and the kinematic flag to the games), then it is Still. <see cref="Apply"/> takes a modifier the server checked
    /// (Task 7: Straight; one taken while Rising waits for the rise to end, since a rising stone cannot move sideways, spec
    /// 2.3). A Sliding stone (spec 2.3, 3.4) is not kinematic, its height and turn are frozen
    /// (FreezePositionY | FreezeRotation), it slows by [EarthModifiers] SlideDeceleration (a frozen height gives the ground no
    /// weight to grip it) and is anchored again (Still: kinematic, no velocity) once slower than 0.1 m/s.
    /// <see cref="Break"/> is the one way a stone is ended on purpose.</summary>
    internal sealed class StoneBody : MonoBehaviour {
        internal StoneKind Kind;
        /// <summary>The owner's account (Server.AccountOf); 0 for a stone nobody owns.</summary>
        internal ulong Owner;
        internal string OwnerName = "";
        internal StoneState State;
        /// <summary>When it was spawned (Time.unscaledTimeAsDouble on the server).</summary>
        internal double Born;
        internal uint Id;
        internal NetworkEntity? Entity;
        /// <summary>When the last Straight was applied to it (Time.unscaledTimeAsDouble; minus infinity: never). Task 12's double
        /// Straight reads it.</summary>
        internal double LastStraightAt = double.NegativeInfinity;

        Rigidbody? body;
        Collider? shape;
        Vector3? queued;   // a Straight taken while Rising
        Vector3 start, end;
        float riseSeconds, risen;
        int slideSteps;   // physics steps since the last push of a slide (the push's impulse lands in the first)
        bool broken;
        static bool sendFailed, destroyFailed, noBodyWarned;

        /// <summary>It broke, or was destroyed: it takes no more modifiers.</summary>
        internal bool Broken => broken;

        /// <summary>Its world box (the collider's bounds), what a modifier's reach is judged on; a point at its place without a
        /// collider.</summary>
        internal Bounds Box => shape != null ? shape.bounds : new Bounds(transform.position, Vector3.zero);

        internal void Init(NetworkEntity entity, StoneKind kind, ulong owner, string ownerName, StoneState state, Vector3 from, Vector3 to, float rise, double born) {
            Entity = entity;
            Id = entity.Identifier;
            Kind = kind;
            Owner = owner;
            OwnerName = ownerName;
            State = state;
            start = from;
            end = to;
            riseSeconds = rise;
            risen = 0f;
            Born = born;
            body = GetComponent<Rigidbody>();
            shape = GetComponent<Collider>();
        }

        void FixedUpdate() {
            if (broken) return;
            if (State == StoneState.Rising) Rise();
            else if (State == StoneState.Sliding) Slide();
        }

        void Rise() {
            risen += Time.fixedDeltaTime;
            float t = riseSeconds > 0f ? Mathf.Clamp01(risen / riseSeconds) : 1f;
            Vector3 at = Vector3.LerpUnclamped(start, end, t);
            if (body != null) {
                body.MovePosition(at);
                body.WakeUp();   // ruling 21: NetworkRigidbody sends the rise only while the body is awake
            }
            else transform.position = at;
            if (t < 1f) return;
            State = StoneState.Still;
            if (Logs.Debug) MelonLogger.Msg($"[Earth] Stones: {Kind} #{Id} of {OwnerName} has risen to {end} and stands still{(queued != null ? "; the Straight it took while rising pushes it now" : "")}");
            if (!(queued is Vector3 dir)) return;
            queued = null;
            if (body != null) body.position = end;   // where the last MovePosition takes it, before it stops being kinematic
            Straight(dir);
        }

        // Called before each physics step, so the first step after a push still reads the speed from before it: the stop test
        // waits for the second.
        void Slide() {
            if (body == null) return;
            slideSteps++;
            Vector3 v = body.velocity;
            float speed = Mathf.Sqrt(v.x * v.x + v.z * v.z);
            if (slideSteps > 1 && StoneRules.SlideStops(speed)) {
                Anchor(speed);
                return;
            }
            float next = StoneRules.SlideSpeed(speed, Server.SlideDeceleration, Time.fixedDeltaTime);
            if (speed > 0f && next < speed) body.velocity = new Vector3(v.x * (next / speed), v.y, v.z * (next / speed));
        }

        /// <summary>Back to Still where it is: no velocity, kinematic, the slide's constraints off.</summary>
        void Anchor(float speed) {
            if (body == null) return;
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.isKinematic = true;
            body.constraints = RigidbodyConstraints.None;
            State = StoneState.Still;
            if (Logs.Debug) MelonLogger.Msg($"[Earth] Stones: {Kind} #{Id} of {OwnerName} stopped sliding at {transform.position} ({speed:0.###} m/s) and stands still");
        }

        /// <summary>A Straight taken while Rising waits here (its flat direction) until the rise ends.</summary>
        internal bool StraightQueued => queued != null;

        /// <summary>Server: applies a modifier the server checked (ModifyCheck) for <paramref name="by"/>, whose pose's A hand is
        /// <paramref name="hand"/> (0 right, 1 left), pushed along <paramref name="flatForward"/> (the claimed yaw's flat
        /// direction, length 1). A modifier this build does not apply (StoneRules.CanApply) does nothing.</summary>
        internal void Apply(Modifier m, Player by, int hand, Vector3 flatForward) {
            if (broken || !StoneRules.CanApply(State, m)) return;
            if (m != Modifier.Straight) return;
            LastStraightAt = Time.unscaledTimeAsDouble;
            if (State == StoneState.Rising) queued = flatForward;   // spec 2.3: a rising stone cannot move sideways; it slides once up
            else Straight(flatForward);
        }

        // Straight (spec 3.5): StraightGrounded on an anchored stone, which slides along the ground; StraightFree on any other,
        // which keeps its state (a free one flies). Never on a Rising stone: Apply queues it until the rise ends (Rise).
        void Straight(Vector3 flatForward) {
            if (body == null) {
                if (!noBodyWarned) { noBodyWarned = true; MelonLogger.Warning($"[Earth] Modifiers: {Kind} #{Id} has no Rigidbody to push (said once)"); }
                return;
            }
            StoneState was = State, next = StoneRules.After(was, Modifier.Straight);
            float impulse = StoneRules.IsGrounded(was) ? Server.StraightGrounded : Server.StraightFree;
            if (next == StoneState.Sliding) {
                body.isKinematic = false;
                body.constraints = RigidbodyConstraints.FreezePositionY | RigidbodyConstraints.FreezeRotation;
                slideSteps = 0;
            }
            State = next;
            body.WakeUp();
            body.AddForce(flatForward * impulse, ForceMode.Impulse);
        }

        /// <summary>Server: the stone breaks. The games within 80 m get Applied(Broke) at its place (they play the break), then
        /// the entity is destroyed. One Debug line with <paramref name="why"/>. Twice is once.</summary>
        internal void Break(string why) {
            if (broken) return;
            broken = true;
            Server.Forget(this);
            Vector3 at = transform.position;
            try { Net.SendApplied(Id, Wire.Broke, at); }
            catch (Exception e) {
                if (!sendFailed) { sendFailed = true; MelonLogger.Warning($"[Earth] Stones: telling the games a stone broke failed (said once): {e.Message}"); }
            }
            if (Logs.Debug) MelonLogger.Msg($"[Earth] Stones: {Kind} #{Id} of {OwnerName} broke at {at}: {why}");
            try {
                if (Entity != null && !Entity.IsBeingDestroyed) Entity.SceneDestroy();
            }
            catch (Exception e) {
                if (!destroyFailed) { destroyFailed = true; MelonLogger.Warning($"[Earth] Stones: destroying a broken stone failed (said once): {e.Message}"); }
            }
        }

        // destroyed by anything (Break, `earth clear`, its chunk unloading): it no longer stands
        void OnDestroy() {
            broken = true;
            Server.Forget(this);
        }
    }
}
