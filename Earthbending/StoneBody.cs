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
    /// place and the kinematic flag to the games), then it is Still (the Ball loose: StoneRules.Risen). <see cref="Apply"/>
    /// takes a modifier the server checked (Task 7: Straight; Task 11: Kick, Stomp, Uppercut). One taken while Rising waits
    /// until the rise has ended (ruling C6: a rising stone is half in the ground), then applies in the next physics step as if
    /// it had just arrived; a later one replaces it. Every modifier's push sets the rigidbody's velocity to
    /// StoneRules.Pushed (its velocity + impulse / mass, held to [EarthModifiers] MaxStoneSpeed: ruling C10); Uppercut on a
    /// grounded stone adds its spin. Kick and Uppercut set a grounded stone loose first. A Stomped stone (spec 2.3, ruling C7)
    /// moves only up and down (its x, z and turn frozen), with continuous collision detection so its fast fall cannot pass
    /// through thin ground (every way out of Stomped puts Discrete back before the body is kinematic), until any contact whose
    /// normal points at most 60 degrees from up:
    /// there it stands Still and the games within 80 m get Applied(Stomp) under its middle (the slam). A Sliding stone (spec
    /// 2.3, 3.4, ruling 22) is not kinematic, has gravity, only its turn frozen
    /// (FreezeRotation), and while it slides its collider wears a frictionless material, so it follows slopes and the ground
    /// does not stop it: [EarthModifiers] SlideDeceleration slows it while it touches ground. Each physics step
    /// (StoneRules.Slide) it stands Still again (kinematic, no velocity, its own material back) once its flat speed is under
    /// 0.1 m/s while touching ground, and is Free (its own material back, nothing frozen) after 0.2 s touching no ground (it
    /// slid off a drop). Ground is any contact whose normal points at most 60 degrees from up (OnCollisionEnter/Stay).
    /// Task 8: each physics step it keeps its velocity from before the step (<see cref="Velocity"/>) and tests itself against
    /// every player (Hits.TestPlayers); each new contact goes to Hits.OnContact (a creature hit, a clash with another stone).
    /// Task 9 (rulings C1, C2, C4): <see cref="Launch"/> pushes a summoned Disc (spawned loose) at once and again after a
    /// delay, and a Ball once it has risen (then it is Free, not Still); <see cref="LiftOff"/> sets a standing stone loose and
    /// throws it up when another is summoned where it stands. <see cref="Break"/> is the one way a stone is ended on purpose.</summary>
    /// <remarks>Straight's own numbers are Server.StraightGrounded and StraightFree; Kick's, Stomp's and Uppercut's come from
    /// StoneRules.Push. None of them pushes the player back but Straight (on their own game, Bend.cs).</remarks>
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
        /// <summary>When the last Straight claim on it was accepted, held while it rose or not (Time.unscaledTimeAsDouble; minus
        /// infinity: never). Task 12's double Straight reads it (ruling C8).</summary>
        internal double LastStraightAt = double.NegativeInfinity;

        Rigidbody? body;
        Collider? shape;
        PhysicMaterial? ownMaterial;   // the collider's material before the slide put the frictionless one on
        (Modifier m, Player by, int hand, Vector3 flatForward)? queued;   // ruling C6: a modifier taken while Rising
        (Vector3 first, float delay, Vector3 second)? launchHeld;   // Task 9: a launch taken while Rising (the Ball's), fired as it ends
        Vector3? pushLater;   // Task 9: a launch's second push (the Disc's), waiting pushIn more seconds of physics steps
        float pushIn;
        Vector3 start, end;
        float riseSeconds, risen;
        int slideSteps;      // physics steps since the last push of a slide (the push's impulse lands in the first)
        bool touchedGround;  // a ground contact in the last physics step (set by the collision callbacks, read by Slide)
        double groundAt;     // Time.fixedTimeAsDouble of the last ground contact, or of the push
        bool broken;
        static bool sendFailed, landSendFailed, destroyFailed, noBodyWarned;
        static PhysicMaterial? slideMaterial;

        /// <summary>It broke, or was destroyed: it takes no more modifiers, and hits and clashes no more.</summary>
        internal bool Broken => broken;

        /// <summary>Task 8: the rigidbody's velocity before this physics step (zero while kinematic), set at the end of each
        /// FixedUpdate: a collision callback comes after the step, which has already slowed the stone against what it struck.</summary>
        internal Vector3 Velocity { get; private set; }
        /// <summary>The same for the physics step before (Hits: a stone that bounced off a player in between still came toward them).</summary>
        internal Vector3 LastVelocity { get; private set; }
        /// <summary>The rigidbody's angular velocity before this physics step (zero while kinematic), kept with <see cref="Velocity"/>.</summary>
        internal Vector3 AngularVelocity { get; private set; }

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
            else if (queued is { } q) {   // ruling C6: the step after the rise ended, so a launch fired then has landed
                queued = null;
                Do(q.m, q.by, q.hand, q.flatForward);
            }
            else if (State == StoneState.Sliding) Slide();
            if (pushLater != null) PushLater();
            LastVelocity = Velocity;
            bool moved = body != null && !body.isKinematic;
            Velocity = moved ? body!.velocity : Vector3.zero;
            AngularVelocity = moved ? body!.angularVelocity : Vector3.zero;
            Hits.TestPlayers(this);   // Task 8 (ruling 24): a moving stone against every player's capsule; never throws
        }

        /// <summary>Task 8 (spec 3.7): this stone won a clash and carries on at its speed from before the physics step: the
        /// step's solver stopped or bounced it against the loser (a kinematic Still loser stops it dead). Its rigidbody gets
        /// <see cref="Velocity"/> and <see cref="AngularVelocity"/> back. Set from the collision callback, after the step and
        /// before the next FixedUpdate, so the next Slide reads the restored speed and does not anchor a winner the loser had
        /// stopped (the loser no longer collides: <see cref="Break"/>). Nothing for a broken or kinematic stone; false then.</summary>
        internal bool CarryOn() {
            if (broken || body == null || body.isKinematic) return false;
            body.velocity = Velocity;
            body.angularVelocity = AngularVelocity;
            return true;
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
            // ruling C1: the kind decides how it ends (StoneRules.Risen): the Ball is loose once up, and launched when its
            // launch has a push (BallLaunchSpeed 0: it just lies there); every other kind stands still. A modifier it took while
            // rising applies in the next physics step (FixedUpdate), after this step has moved it here and landed the launch.
            (Vector3 first, float delay, Vector3 second)? launch = launchHeld;
            launchHeld = null;
            State = StoneRules.Risen(Kind);
            bool loose = State == StoneState.Free;
            if (Logs.Debug) MelonLogger.Msg($"[Earth] Stones: {Kind} #{Id} of {OwnerName} has risen to {end} and {(loose ? "is loose" : "stands still")}{(queued is { } q ? $"; the {q.m} it took while rising applies in the next step" : "")}");
            if (!loose) return;
            if (body != null) {
                body.position = end;   // where the last MovePosition takes it, before it stops being kinematic
                Loosen();
                body.velocity = Vector3.zero;   // the launch alone sets its speed, whatever the kinematic rise left in it
                body.angularVelocity = Vector3.zero;
            }
            if (launch is { } l) Fire(l.first, l.delay, l.second);
        }

        /// <summary>Server, from Server.Summon right after the spawn (ruling C2): the kind's launch, world impulses in N s
        /// (SpawnPlan.Launch turned to the yaw). A Rising stone keeps it until its rise ends, then is loose (Free) and launched
        /// (the Ball); any other is launched at once (the Disc, spawned loose). The first push lands in the next physics step,
        /// the second in the first step at least <paramref name="delay"/> seconds after it, unless a modifier is applied to the
        /// stone or it breaks first. Nothing for an all-zero launch (the Pillar, Cube and Wall).</summary>
        internal void Launch(Vector3 first, float delay, Vector3 second) {
            if (broken || (first == Vector3.zero && second == Vector3.zero)) return;
            if (State == StoneState.Rising) launchHeld = (first, delay, second);
            else Fire(first, delay, second);
        }

        void Fire(Vector3 first, float delay, Vector3 second) {
            if (body == null) {
                if (!noBodyWarned) { noBodyWarned = true; MelonLogger.Warning($"[Earth] Stones: {Kind} #{Id} has no Rigidbody to launch (said once)"); }
                return;
            }
            body.WakeUp();
            if (first != Vector3.zero) body.AddForce(first, ForceMode.Impulse);
            if (second != Vector3.zero) {
                pushLater = second;
                pushIn = delay;
            }
            if (Logs.Debug) MelonLogger.Msg($"[Earth] Stones: {Kind} #{Id} of {OwnerName} is launched at {body.position}: {first} N s now{(second != Vector3.zero ? $", {second} N s in {delay:0.##} s" : "")}");
        }

        // A launch's second push (the Disc's), in the first physics step at least its delay after the first push's step: the
        // first lands in the step after Fire, and this runs before each step.
        void PushLater() {
            if (pushIn > 1e-4f) { pushIn -= Time.fixedDeltaTime; return; }
            Vector3 push = pushLater ?? Vector3.zero;
            pushLater = null;
            if (body == null) return;
            body.WakeUp();
            body.AddForce(push, ForceMode.Impulse);
            if (Logs.Debug) MelonLogger.Msg($"[Earth] Stones: {Kind} #{Id} of {OwnerName} gets its launch's second push, {push} N s, at {transform.position}");
        }

        /// <summary>Server (ruling C4): another stone is summoned where this one stands. When StoneRules.SummonUnder sets it
        /// loose (Still, Sliding, Stomped) it is loose (<see cref="Loosen"/>, Free) and thrown up at the rule's lift, a velocity
        /// change (a lift of 0: loose, not thrown; Task 9's CARRY); true then. False, and nothing changes, in any other state
        /// (the rule leaves it as it is) or broken.</summary>
        internal bool LiftOff() {
            if (broken || body == null) return false;
            (StoneState next, float lift) = StoneRules.SummonUnder(State);
            if (next == State) return false;
            StoneState was = State;
            Loosen();
            State = next;
            body.WakeUp();
            if (lift > 0f) body.AddForce(Vector3.up * lift, ForceMode.VelocityChange);
            if (Logs.Debug) MelonLogger.Msg($"[Earth] Stones: {Kind} #{Id} of {OwnerName} was {was} at {transform.position}; a stone summoned where it stands {(lift > 0f ? $"lifts it off at {lift:0.##} m/s up" : "sets it loose ([EarthStones] SummonUnderLift 0: not thrown)")} ({next})");
            return true;
        }

        /// <summary>The collision detection a state needs (StoneRules.Continuous): ContinuousDynamic while Stomped, else
        /// Discrete (the stones' default; NetworkRigidbody does the same before it makes a body kinematic). Call it BEFORE any
        /// <c>isKinematic = true</c>: Unity does not allow ContinuousDynamic on a kinematic body. Set only when it differs, so
        /// the other states keep what they have.</summary>
        void Detection(StoneState s) {
            if (body == null) return;
            CollisionDetectionMode want = StoneRules.Continuous(s) ? CollisionDetectionMode.ContinuousDynamic : CollisionDetectionMode.Discrete;
            if (body.collisionDetectionMode != want) body.collisionDetectionMode = want;
        }

        /// <summary>Normal physics (Free's): not kinematic, gravity, nothing frozen, its own material back, Discrete collision.</summary>
        void Loosen() {
            if (body == null) return;
            Detection(StoneState.Free);   // out of Stomped (a Kick, an Uppercut, a lift-off)
            body.isKinematic = false;
            body.useGravity = true;
            body.constraints = RigidbodyConstraints.None;
            EndSlideMaterial();
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
            Land(c);   // after the hit or clash: a stone that broke there does not land, nor on a stone it broke
        }

        void OnCollisionStay(Collision c) {
            Touched(c);
            Land(c);   // a stone stomped while it already lay on the ground lands in the next step
        }

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

        // Ruling C7: a Stomped stone lands on any up-facing contact (the ground, a floor, another stone's top): Still where it
        // is (StoneRules.Landed), and the games within 80 m get Applied(Stomp) under its middle at the contact's height, where
        // they play the slam. A collider a clash disabled in this same contact (StoneBody.Break) holds nothing up.
        void Land(Collision c) {
            if (State != StoneState.Stomped || broken || body == null || c == null) return;
            Collider other = c.collider;
            if (other == null || !other.enabled) return;
            for (int i = 0; i < c.contactCount; i++) {
                ContactPoint p = c.GetContact(i);
                if (!StoneRules.IsGroundContact(p.normal.y)) continue;
                Vector3 centre = body.position;
                var at = new Vector3(centre.x, p.point.y, centre.z);
                float speed = Velocity.magnitude;   // before this step: how fast it came down
                Stand();
                State = StoneRules.Landed(State);
                int games = 0;
                try { games = Net.SendApplied(Id, (byte)Modifier.Stomp, at); }
                catch (Exception e) {
                    if (!landSendFailed) { landSendFailed = true; MelonLogger.Warning($"[Earth] Stones: telling the games a stomped stone landed failed (said once): {e.Message}"); }
                }
                if (Logs.Debug) MelonLogger.Msg($"[Earth] Stones: {Kind} #{Id} of {OwnerName} landed from its Stomp at {at} on '{other.name}' ({speed:0.##} m/s before the step) and stands still; the slam goes to {games} game(s)");
                return;
            }
        }

        /// <summary>Still's physics where it is: no velocity or spin, Discrete collision, kinematic, nothing frozen, the slide's
        /// material off. The caller sets the state.</summary>
        void Stand() {
            if (body == null) return;
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            Detection(StoneState.Still);   // before it is kinematic (a stomped stone's landing)
            body.isKinematic = true;
            body.constraints = RigidbodyConstraints.None;
            EndSlideMaterial();
        }

        /// <summary>Back to Still where it is: no velocity, kinematic, the slide's constraint and material off.</summary>
        void Anchor(float speed) {
            if (body == null) return;
            Stand();
            State = StoneState.Still;
            if (Logs.Debug) MelonLogger.Msg($"[Earth] Stones: {Kind} #{Id} of {OwnerName} stopped sliding at {transform.position} ({speed:0.###} m/s) and stands still");
        }

        /// <summary>It slid off the ground: Free, normal physics (nothing frozen, gravity, its own material back).</summary>
        void LetGo() {
            if (body == null) return;
            Loosen();
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

        /// <summary>The modifier taken while Rising that waits for the rise to end (ruling C6), or null.</summary>
        internal Modifier? Queued => queued?.m;

        /// <summary>Server: applies a modifier the server checked (ModifyCheck) for <paramref name="by"/>, whose pose's A hand is
        /// <paramref name="hand"/> (0 right, 1 left), pushed along <paramref name="flatForward"/> (the claimed yaw's flat
        /// direction, length 1). A modifier this build does not apply (StoneRules.CanApply) does nothing. A Straight stamps
        /// <see cref="LastStraightAt"/> now, held or not (ruling C8). On a Rising stone every modifier is held until the rise
        /// has ended (ruling C6), then applied as if it had just arrived; a later claim replaces a held one (also in the one
        /// physics step between the rise's end and the held one's turn).</summary>
        internal void Apply(Modifier m, Player by, int hand, Vector3 flatForward) {
            if (broken || !StoneRules.CanApply(State, m)) return;
            if (pushLater != null) {   // ruling C1: a modifier before the Disc's second push cancels it
                pushLater = null;
                if (Logs.Debug) MelonLogger.Msg($"[Earth] Stones: {Kind} #{Id} of {OwnerName}: {m} came before its launch's second push, which is dropped");
            }
            if (m == Modifier.Straight) LastStraightAt = Time.unscaledTimeAsDouble;
            if (queued is { } old && Logs.Debug) MelonLogger.Msg($"[Earth] Stones: {Kind} #{Id} of {OwnerName}: {m} replaces the {old.m} it took while rising");
            queued = null;
            if (State == StoneState.Rising) queued = (m, by, hand, flatForward);   // spec 2.3: a rising stone is half in the ground
            else Do(m, by, hand, flatForward);
        }

        /// <summary>A checked modifier on a stone that is not Rising (spec 3.5): its state (StoneRules.After) and its push, set as
        /// the rigidbody's velocity (StoneRules.Pushed: the velocity it had, zero while kinematic, + the impulse over its
        /// mass, held to [EarthModifiers] MaxStoneSpeed, ruling C10). Straight: StraightGrounded along the yaw on a grounded
        /// stone, StraightFree on any other; a Still one slides (ruling 22's set-up). Kick and Uppercut (StoneRules.Push): a
        /// grounded stone is set loose first (Free), and a grounded Uppercut adds its spin. Stomp on a loose stone: it moves only
        /// up and down from now on (x, z and its turn frozen, its sideways speed and spin gone) and is pushed down until it
        /// lands (<see cref="Land"/>); on a grounded one nothing happens. A Stomped stone takes only the up and down part of any
        /// push. One Debug line.</summary>
        void Do(Modifier m, Player by, int hand, Vector3 flatForward) {
            if (broken || !StoneRules.CanApply(State, m)) return;
            if (body == null) {
                if (!noBodyWarned) { noBodyWarned = true; MelonLogger.Warning($"[Earth] Modifiers: {Kind} #{Id} has no Rigidbody to push (said once)"); }
                return;
            }
            StoneState was = State, next = StoneRules.After(was, m);
            bool grounded = StoneRules.IsGrounded(was);
            Vector3 impulse, spin = Vector3.zero;
            if (m == Modifier.Straight) impulse = flatForward * (grounded ? Server.StraightGrounded : Server.StraightFree);
            else {
                (V3 i, V3 s) = StoneRules.Push(m, grounded, V(flatForward));
                impulse = U(i);
                spin = U(s);
            }
            if (next == was && impulse == Vector3.zero && spin == Vector3.zero) {   // Stomp on a grounded stone (spec 3.5: it stays grounded)
                if (Logs.Debug) MelonLogger.Msg($"[Earth] Modifiers: {m} on {Kind} #{Id} of {OwnerName}: {was}, nothing to push");
                return;
            }
            Vector3 before = body.isKinematic ? Vector3.zero : body.velocity;
            Vector3 beforeSpin = body.isKinematic ? Vector3.zero : body.angularVelocity;
            if (next == StoneState.Sliding) {
                body.isKinematic = false;
                body.useGravity = true;
                body.constraints = RigidbodyConstraints.FreezeRotation;
                StartSlideMaterial();
                slideSteps = 0;
                touchedGround = false;
                groundAt = Time.fixedTimeAsDouble;   // it stood on the ground at the push
            }
            else if (next == StoneState.Free && was != StoneState.Free) Loosen();   // Kick, Uppercut: set loose first
            else if (next == StoneState.Stomped) {   // ruling C7: only up and down from now on
                body.isKinematic = false;
                Detection(next);   // ContinuousDynamic, once it is not kinematic: driven down fast, it must not pass through thin ground
                body.useGravity = true;
                body.constraints = RigidbodyConstraints.FreezePositionX | RigidbodyConstraints.FreezePositionZ | RigidbodyConstraints.FreezeRotation;
                EndSlideMaterial();
                before = new Vector3(0f, before.y, 0f);
                impulse = new Vector3(0f, impulse.y, 0f);
                spin = Vector3.zero;
                beforeSpin = Vector3.zero;
            }
            State = next;
            if (body.isKinematic) return;   // a state this build leaves kinematic (none today) takes no push
            Vector3 after = U(StoneRules.Pushed(V(before), V(impulse), body.mass, Server.MaxStoneSpeed));
            body.WakeUp();
            body.velocity = after;
            body.angularVelocity = beforeSpin + spin;
            if (Logs.Debug) {
                float uncapped = (before + impulse / Mathf.Max(body.mass, 1e-6f)).magnitude;
                MelonLogger.Msg($"[Earth] Modifiers: {m} on {Kind} #{Id} of {OwnerName}: {was} -> {next}, {impulse} N s on {body.mass:0.#} kg; velocity {before} -> {after} m/s{(uncapped > after.magnitude + 1e-3f ? $" (held to [EarthModifiers] MaxStoneSpeed {Server.MaxStoneSpeed:0.##})" : "")}{(spin != Vector3.zero ? $", spin {spin} rad/s" : "")}");
            }
        }

        /// <summary>Server: the stone breaks. It stops colliding at once and holds still where it is; the games within 80 m get
        /// Applied(Broke) at its place (they play the break), then the entity is destroyed. One Debug line with
        /// <paramref name="why"/>. Twice is once.</summary>
        internal void Break(string why) {
            if (broken) return;
            broken = true;
            Server.Forget(this);
            // the entity goes at the end of the frame, and a slow server runs several physics steps in one frame: a broken
            // stone must not stop a clash's winner again in the next one (CarryOn), nor go on shoving what it hit
            if (shape != null) shape.enabled = false;
            if (body != null) {
                Detection(StoneState.Still);   // a stomped stone breaking in its fall: Discrete before kinematic
                body.isKinematic = true;
            }
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

        static V3 V(Vector3 v) => new V3(v.x, v.y, v.z);
        static Vector3 U(V3 v) => new Vector3(v.X, v.Y, v.Z);
    }
}
