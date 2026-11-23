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
    /// 2.3). A Sliding stone (spec 2.3, 3.4, ruling 22) is not kinematic, has gravity, only its turn frozen
    /// (FreezeRotation), and while it slides its collider wears a frictionless material, so it follows slopes and the ground
    /// does not stop it: [EarthModifiers] SlideDeceleration slows it while it touches ground. Each physics step
    /// (StoneRules.Slide) it stands Still again (kinematic, no velocity, its own material back) once its flat speed is under
    /// 0.1 m/s while touching ground, and is Free (its own material back, nothing frozen) after 0.2 s touching no ground (it
    /// slid off a drop). Ground is any contact whose normal points at most 60 degrees from up (OnCollisionEnter/Stay).
    /// Task 8: each physics step it keeps its velocity from before the step (<see cref="Velocity"/>) and tests itself against
    /// every player (Hits.TestPlayers); each new contact goes to Hits.OnContact (a creature hit, a clash with another stone).
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
        PhysicMaterial? ownMaterial;   // the collider's material before the slide put the frictionless one on
        Vector3? queued;   // a Straight taken while Rising
        Vector3 start, end;
        float riseSeconds, risen;
        int slideSteps;      // physics steps since the last push of a slide (the push's impulse lands in the first)
        bool touchedGround;  // a ground contact in the last physics step (set by the collision callbacks, read by Slide)
        double groundAt;     // Time.fixedTimeAsDouble of the last ground contact, or of the push
        bool broken;
        static bool sendFailed, destroyFailed, noBodyWarned;
        static PhysicMaterial? slideMaterial;

        /// <summary>It broke, or was destroyed: it takes no more modifiers, and hits and clashes no more.</summary>
        internal bool Broken => broken;

        /// <summary>Task 8: the rigidbody's velocity before this physics step (zero while kinematic), set at the end of each
        /// FixedUpdate: a collision callback comes after the step, which has already slowed the stone against what it struck.</summary>
        internal Vector3 Velocity { get; private set; }
        /// <summary>The same for the physics step before (Hits: a stone that bounced off a player in between still came toward them).</summary>
        internal Vector3 LastVelocity { get; private set; }

        /// <summary>Its collider (a box, a sphere for the Ball), for the players' capsule test (Hits.TestPlayers).</summary>
        internal Collider? Shape => shape;

        /// <summary>Spec 3.4: its power for hits and clashes now (StoneRules.Power, its kind's + grounded + moving faster than
        /// [EarthDamage] MovingSpeed, from <see cref="Velocity"/>).</summary>
        internal int Power => StoneRules.Power(Kind, State, Velocity.magnitude, Hits.MovingSpeed);

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
            LastVelocity = Velocity;
            Velocity = body != null && !body.isKinematic ? body.velocity : Vector3.zero;
            Hits.TestPlayers(this);   // Task 8 (ruling 24): a moving stone against every player's capsule; never throws
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

        // Called before each physics step (its collision callbacks come after it), so the first step after a push still reads
        // the velocity from before it: the stop test waits for the second (pushLanded). While touching ground the slide slows
        // along its own way (on a slope too), as friction would; for a moment in the air, gravity alone.
        void Slide() {
            if (body == null) return;
            slideSteps++;
            bool touching = touchedGround;
            touchedGround = false;
            Vector3 v = body.velocity;
            float flat = Mathf.Sqrt(v.x * v.x + v.z * v.z);
            SlideStep step = StoneRules.Slide(touching, Time.fixedTimeAsDouble - groundAt, flat, slideSteps > 1);
            if (step == SlideStep.Still) { Anchor(flat); return; }
            if (step == SlideStep.Free) { LetGo(); return; }
            if (!touching) return;
            float speed = v.magnitude;
            float next = StoneRules.SlideSpeed(speed, Server.SlideDeceleration, Time.fixedDeltaTime);
            if (speed > 0f && next < speed) body.velocity = v * (next / speed);
        }

        void OnCollisionEnter(Collision c) {
            Touched(c);
            Hits.OnContact(this, c);   // Task 8: a creature is hit, another stone clashes (ruling 24); never throws
        }

        void OnCollisionStay(Collision c) => Touched(c);

        // Only a sliding stone looks; one up-facing contact point is enough.
        void Touched(Collision c) {
            if (State != StoneState.Sliding || touchedGround || c == null) return;
            for (int i = 0; i < c.contactCount; i++) {
                if (!StoneRules.IsGroundContact(c.GetContact(i).normal.y)) continue;
                touchedGround = true;
                groundAt = Time.fixedTimeAsDouble;
                return;
            }
        }

        /// <summary>Back to Still where it is: no velocity, kinematic, the slide's constraint and material off.</summary>
        void Anchor(float speed) {
            if (body == null) return;
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.isKinematic = true;
            body.constraints = RigidbodyConstraints.None;
            EndSlideMaterial();
            State = StoneState.Still;
            if (Logs.Debug) MelonLogger.Msg($"[Earth] Stones: {Kind} #{Id} of {OwnerName} stopped sliding at {transform.position} ({speed:0.###} m/s) and stands still");
        }

        /// <summary>It slid off the ground: Free, normal physics (nothing frozen, gravity, its own material back).</summary>
        void LetGo() {
            if (body == null) return;
            body.constraints = RigidbodyConstraints.None;
            body.useGravity = true;
            EndSlideMaterial();
            State = StoneState.Free;
            if (Logs.Debug) MelonLogger.Msg($"[Earth] Stones: {Kind} #{Id} of {OwnerName} slid off the ground at {transform.position} (no ground for {StoneRules.SlideAirSeconds:0.##} s) and is loose");
        }

        /// <summary>The frictionless material a slide puts on the collider (ruling 22): friction 0, combined by the minimum, so
        /// whatever it slides on has no grip on it. The mod's own, made once.</summary>
        static PhysicMaterial SlideMaterial() {
            if (slideMaterial == null)
                slideMaterial = new PhysicMaterial("Earth slide") { dynamicFriction = 0f, staticFriction = 0f, frictionCombine = PhysicMaterialCombine.Minimum };
            return slideMaterial;
        }

        void StartSlideMaterial() {
            if (shape == null) return;
            PhysicMaterial slide = SlideMaterial();
            if (shape.sharedMaterial != slide) ownMaterial = shape.sharedMaterial;
            shape.sharedMaterial = slide;
        }

        void EndSlideMaterial() {
            if (shape != null && slideMaterial != null && shape.sharedMaterial == slideMaterial) shape.sharedMaterial = ownMaterial;
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
                body.useGravity = true;
                body.constraints = RigidbodyConstraints.FreezeRotation;
                StartSlideMaterial();
                slideSteps = 0;
                touchedGround = false;
                groundAt = Time.fixedTimeAsDouble;   // it stood on the ground at the push
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
