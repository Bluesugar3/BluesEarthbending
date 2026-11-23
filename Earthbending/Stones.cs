using System;
using System.Collections.Generic;
using System.Globalization;

// Unity-free, compiled by Earthbending.Tests, so only System.* in here: the stones' kinds and numbers (spec 3.4), V3, the
// vector every Unity-free file uses, and what message 19 carries (spec 3.8: the wire's version and kind bytes, the modifiers
// and the two claims a game sends). Prefabs.cs turns the table into the five network prefabs; Net.cs writes and reads the
// claims. Task 6 (summoning, at the end of this file): the stones' states (StoneState, StoneRules), the server's per-player
// cooldowns (Ledger), its check of a Summon claim (SummonCheck), where a stone rises (SpawnPlan), when it breaks (StoneLife),
// how many may stand (Caps) and which kinds are on (StoneKinds); Server.cs and StoneBody.cs run them. Task 8: a stone's power,
// clashes, damage, knockback and hitstun (StoneRules), the players' capsule against a stone (Contact) and who was hit by what
// (HitLedger); Hits.cs runs them. Task 9: the Disc's and the Ball's launch (SpawnPlan.Launch) and what a stone standing where
// another is summoned does (StoneRules.SummonUnder). Task 11: Kick, Stomp and Uppercut (StoneRules.After, CanApply, Impulse,
// Push, Landed), every modifier's capped push (StoneRules.Pushed) and the state a stone is in once risen (StoneRules.Risen).
// Task 12: Parry and Explode in After and CanApply, when a parried stone falls again (StoneRules.ParryEnds), how much of an
// explosion reaches what (ExplodeShare), the way it knocks a player (BlastDir) and the double Straight (DoubleStraight);
// StoneBody.cs and Hits.Explode run them. Task 13: Hold in After and CanApply, when a held stone is let go (StoneRules.HoldLetGo,
// HoldAgain) and the hold's pull (Pd: the follow, the target ahead of the hand); StoneHold.cs runs them. Task 14: Flick in
// After and CanApply, and the flick's pull and turn (Pd: the follow, the target between the hands, the turn from the hands,
// the turn still to go and its acceleration); StoneFlick.cs runs them. Task 15: how long two clashing stones freeze
// (StoneRules.HitstopSeconds), that a frozen one is broken by no contact (StoneRules.Breakable), and Dash's push
// (MoveRules.Dash); StoneFreeze.cs, Hits.cs and Moves.cs run them. Task 17c: the follow by a rate (Pd.Follow, Ruling 40), the
// centre HoldDistance + half its size from the hand (Pd.HoldReach, Ruling 41) and every push x PushScale (StoneRules.PushScale, Ruling 42).
namespace Earthbending {
    /// <summary>The five stones. A byte on the wire; the order is fixed.</summary>
    internal enum StoneKind : byte { Disc, Ball, Pillar, Cube, Wall }

    /// <summary>The eight modifiers (spec 2.4, 3.5). A byte on the wire (a Modify claim, and an Applied event); the order is fixed.</summary>
    internal enum Modifier : byte { Straight, Kick, Stomp, Uppercut, Parry, Hold, Flick, Explode }

    /// <summary>Message 19's header (spec 3.8): the first byte is the kind, the second the version. A message of another
    /// version is dropped, so change <see cref="Version"/> whenever a kind's fields change.</summary>
    internal static class Wire {
        public const byte Version = 1;

        /// <summary>The kinds; the numbers are the bytes on the wire.</summary>
        internal enum Kind : byte { Summon = 1, Modify = 2, Knockback = 10, Applied = 11, Fight = 12, Ring = 13 }

        /// <summary>An Applied event's byte when it is not a <see cref="Modifier"/>: the stone broke, froze for a hitstop, or
        /// exploded; or a player's fight health reached 0 (<see cref="KnockedOut"/>: entity 0, at that player's head).</summary>
        public const byte Broke = 20, Hitstop = 21, Exploded = 22, KnockedOut = 23;

        /// <summary>A Fight message's points byte for "no fight health" (points are 0 to 250).</summary>
        public const byte NoPoints = 255;
    }

    /// <summary>A game asks the server for a stone (kind 1). <see cref="Head"/> is the asker's head (camera) in world space,
    /// <see cref="Yaw"/> its heading in degrees; the server checks both against its own copy of the head.</summary>
    internal struct SummonClaim {
        public StoneKind Kind;
        public V3 Head;
        public float Yaw;

        public SummonClaim(StoneKind kind, V3 head, float yaw) { Kind = kind; Head = head; Yaw = yaw; }
    }

    /// <summary>A game asks the server to apply a modifier to a stone (kind 2). <see cref="Hand"/> is the holding hand (0
    /// right, 1 left), <see cref="Target"/> the stone entity's Identifier the game picked; head and yaw as in a summon.</summary>
    internal struct ModifyClaim {
        public Modifier Mod;
        public byte Hand;
        public uint Target;
        public V3 Head;
        public float Yaw;

        public ModifyClaim(Modifier mod, byte hand, uint target, V3 head, float yaw) { Mod = mod; Hand = hand; Target = target; Head = head; Yaw = yaw; }
    }

    /// <summary>A vector for the Unity-free files (metres, or a direction). Unity code converts at its edges.</summary>
    internal readonly struct V3 {
        public readonly float X, Y, Z;

        public V3(float x, float y, float z) { X = x; Y = y; Z = z; }

        public static readonly V3 Zero = new V3(0f, 0f, 0f);

        public static V3 operator +(V3 a, V3 b) => new V3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static V3 operator -(V3 a, V3 b) => new V3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static V3 operator -(V3 a) => new V3(-a.X, -a.Y, -a.Z);
        public static V3 operator *(V3 a, float s) => new V3(a.X * s, a.Y * s, a.Z * s);
        public static V3 operator *(float s, V3 a) => a * s;

        public static float Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public float Length => (float)Math.Sqrt(X * X + Y * Y + Z * Z);

        /// <summary>The same direction, length 1; <see cref="Zero"/> for a vector too short to have one.</summary>
        public V3 Normalized {
            get {
                float l = Length;
                return l > 1e-6f && !float.IsInfinity(l) ? this * (1f / l) : Zero;
            }
        }

        /// <summary>Mirrored left for right (x flipped): a pose of the right hand as the left hand's.</summary>
        public V3 MirrorX() => new V3(-X, Y, Z);

        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###})", X, Y, Z);
    }

    /// <summary>One kind's numbers. <see cref="Size"/> is the collider's whole extent in metres: a box's sides (x across,
    /// y up, z ahead), the Ball's diameter on every axis.</summary>
    internal struct StoneStats {
        public int Power;
        public float Mass;
        public V3 Size;

        public StoneStats(int power, float mass, V3 size) { Power = power; Mass = mass; Size = size; }
    }

    /// <summary>Spec 3.4's numbers per kind. The [EarthStones] settings (Prefabs.cs, server) replace Power and Mass at start-up
    /// and on every reload through <see cref="Set"/>; <see cref="Default"/> keeps the spec's values for the settings' defaults.
    /// Size is a constant: every machine builds the colliders, so they must be the same everywhere.</summary>
    internal static class StoneTable {
        public const int Count = 5;

        static readonly StoneStats[] Defaults = {
            new StoneStats(0, 100f, new V3(0.70f, 0.14f, 0.70f)),   // Disc
            new StoneStats(1, 150f, new V3(0.60f, 0.60f, 0.60f)),   // Ball: a sphere of radius 0.30
            new StoneStats(1, 275f, new V3(0.55f, 1.80f, 0.55f)),   // Pillar
            new StoneStats(2, 300f, new V3(1.00f, 1.00f, 1.00f)),   // Cube
            new StoneStats(3, 400f, new V3(2.40f, 2.00f, 0.50f)),   // Wall
        };

        static readonly StoneStats[] now = (StoneStats[])Defaults.Clone();

        public static bool Valid(StoneKind k) => (byte)k < Count;

        /// <summary>The kind's numbers now; all zero for a byte that is not a kind.</summary>
        public static StoneStats Of(StoneKind k) => Valid(k) ? now[(int)k] : default;

        public static StoneStats Default(StoneKind k) => Valid(k) ? Defaults[(int)k] : default;

        /// <summary>The kind's power and mass from the settings; its size stays the spec's.</summary>
        public static void Set(StoneKind k, int power, float mass) {
            if (Valid(k)) now[(int)k] = new StoneStats(power, mass, Defaults[(int)k].Size);
        }
    }

    // ==================================================================== Task 6: summoning

    /// <summary>What a stone is doing (spec 2.3). Rising, Still, Sliding and Stomped are grounded; Still and Sliding add 1 to
    /// its power (spec 3.4, ruling C35).</summary>
    internal enum StoneState : byte { Rising, Still, Sliding, Free, Stomped, Floating, Held, Flicked }

    internal static class StoneRules {
        /// <summary>Rising, Still, Sliding and Stomped: the states that count as on the ground.</summary>
        public static bool IsGrounded(StoneState s) => s == StoneState.Rising || s == StoneState.Still || s == StoneState.Sliding || s == StoneState.Stomped;

        /// <summary>Whether the stone's rigidbody is kinematic in this state (spec 3.4: Rising is moved by the server each tick,
        /// Still is anchored); every other state is moved by physics.</summary>
        public static bool Kinematic(StoneState s) => s == StoneState.Rising || s == StoneState.Still;

        /// <summary>Ruling C4: what a stone standing where another is summoned does. Still, Sliding and Stomped are lifted off:
        /// Free, thrown up at <see cref="SpawnPlan.SummonUnderLift"/> x <see cref="PushScale"/> m/s (a velocity change; 0 sets
        /// it loose without a throw). Every other state stays, lift 0: a loose stone is pushed aside by physics, and a Rising
        /// one is half in the ground.</summary>
        public static (StoneState state, float lift) SummonUnder(StoneState s) =>
            s == StoneState.Still || s == StoneState.Sliding || s == StoneState.Stomped ? (StoneState.Free, SpawnPlan.SummonUnderLift * PushScale) : (s, 0f);

        // ---------------------------------------------------------------- Task 17c: every push x PushScale (Ruling 42)

        /// <summary>Ruling 42: [EarthModifiers] PushScale's default.</summary>
        public const float DefaultPushScale = 1.25f;

        /// <summary>Ruling 42: [EarthModifiers] PushScale: multiplies every push a modifier gives a stone (Straight
        /// (<see cref="Straight"/>), Kick, Stomp and Uppercut (<see cref="Impulse"/>; Uppercut's spin is not scaled), on
        /// anchored and loose stones), the Disc's two launch pushes and the Ball's launch speed (SpawnPlan.Launch) and
        /// SpawnPlan.SummonUnderLift (<see cref="SummonUnder"/>). Not a blast's force, a player's knockback or pushback, Hold
        /// or Flick. 1 gives the numbers before Ruling 42 exactly.</summary>
        public static float PushScale { get; private set; } = DefaultPushScale;

        /// <summary>PushScale from the settings (Server.cs); one not above 0 or not a finite number is the default.</summary>
        public static void SetPushScale(float scale) => PushScale = scale > 0f && !float.IsInfinity(scale) ? scale : DefaultPushScale;

        /// <summary>Straight's impulse in N s (Ruling 42): <paramref name="groundedImpulse"/> ([EarthModifiers]
        /// StraightGrounded) on a <paramref name="grounded"/> stone, <paramref name="freeImpulse"/> (StraightFree) on any other,
        /// times <see cref="PushScale"/>.</summary>
        public static float Straight(bool grounded, float groundedImpulse, float freeImpulse) => (grounded ? groundedImpulse : freeImpulse) * PushScale;

        // ---------------------------------------------------------------- Task 7: the modifiers' states

        /// <summary>The state a modifier leaves a stone in (spec 2.3, 3.5). A Rising stone counts as the Still one it becomes:
        /// StoneBody holds every modifier taken while it rises until the rise ends (ruling C6), then applies it as if it had
        /// just arrived (a Ball, loose once up, is Free by then). Straight: a Still stone slides; every other state stays (Free
        /// flies, Sliding slides on, Stomped falls on, Floating floats on with the push, whose speed then ends the float, Held
        /// is let go and flies, Free (the review of Task 17c, I3: <see cref="PushLetsGo"/>), Flicked stays in the hands). Kick and Uppercut: a grounded stone (Still, Sliding, Stomped), a Floating
        /// and a Held one are set loose, Free; Free stays Free; a Flicked one is pushed and stays in the hands. Stomp: a Free,
        /// Floating, Held or Flicked stone is Stomped (a flicked one drops: spec 2.4, "dropped only by Stomp or breaking"); a
        /// grounded one stays as it is. Parry (Task 12): Free, Still, Sliding, Stomped, Floating (its time starts over) and
        /// Held float, Floating; Flicked stays (refused: <see cref="CanApply"/>). Hold (Task 13, ruling C14): a grounded Still
        /// or Sliding stone slides, held (Sliding); a Free, Floating or Held one is Held; Stomped and Flicked stay (Hold is
        /// refused there). A held stone (Held, or a held slide) is let go before Straight, Kick, Uppercut or Stomp pushes it
        /// (<see cref="PushLetsGo"/>); Parry lets it go by changing its state; Explode keeps it held. Flick (Task 14, ruling C15): a loose stone (Free, Floating, Held, Flicked) is Flicked (a hold or a float
        /// ends; a flicked one goes to the newest flicker); a grounded one (Rising, Still, Sliding, Stomped) stays (refused).
        /// Explode only marks the stone (StoneBody.Exploding): every state stays; so does it for a modifier this build does
        /// not apply.</summary>
        public static StoneState After(StoneState s, Modifier m) {
            if (m == Modifier.Hold) return HoldOn(s == StoneState.Rising ? StoneState.Still : s);
            if (m == Modifier.Flick) return CanFlick(s) ? StoneState.Flicked : s;
            if (m > Modifier.Parry) return s;
            if (s == StoneState.Rising) s = StoneState.Still;
            bool loose = s == StoneState.Free || s == StoneState.Floating || s == StoneState.Held;
            bool anchored = s == StoneState.Still || s == StoneState.Sliding || s == StoneState.Stomped;
            switch (m) {
                case Modifier.Straight:
                    return s == StoneState.Still ? StoneState.Sliding : s == StoneState.Held ? StoneState.Free : s;
                case Modifier.Kick:
                case Modifier.Uppercut:
                    return anchored || loose ? StoneState.Free : s;   // Flicked: pushed, it stays in the hands
                case Modifier.Stomp:
                    return loose || s == StoneState.Flicked ? StoneState.Stomped : s;
                default:   // Parry
                    return anchored || loose ? StoneState.Floating : s;
            }
        }

        // ruling C14: Still and Sliding slide held; the loose states (Free, Floating, Held) are Held; Stomped and Flicked stay
        static StoneState HoldOn(StoneState s) {
            switch (s) {
                case StoneState.Still:
                case StoneState.Sliding:
                    return StoneState.Sliding;
                case StoneState.Free:
                case StoneState.Floating:
                case StoneState.Held:
                    return StoneState.Held;
                default:   // Stomped, Flicked: refused (CanApply)
                    return s;
            }
        }

        /// <summary>Whether a modifier may be applied to a stone in this state. Straight, Kick, Stomp, Uppercut and Explode
        /// apply in any state; Parry in any but Flicked (ruling C15: a flicked stone is dropped only by Stomp or a break);
        /// Hold (Task 13, ruling C14) in every state but Stomped (it lands first) and Flicked; Flick (Task 14, ruling C15, spec
        /// 3.5: "grounded cannot be flicked") only on a stone that is not grounded: Free, Floating, Held, Flicked. A Rising
        /// stone takes a modifier once it has risen (ruling C6). A byte that is not a modifier never applies.</summary>
        public static bool CanApply(StoneState s, Modifier m) {
            switch (m) {
                case Modifier.Straight:
                case Modifier.Kick:
                case Modifier.Stomp:
                case Modifier.Uppercut:
                case Modifier.Explode:
                    return true;
                case Modifier.Parry:
                    return s != StoneState.Flicked;
                case Modifier.Hold:
                    return s != StoneState.Stomped && s != StoneState.Flicked;
                case Modifier.Flick:
                    return CanFlick(s);
                default:
                    return false;
            }
        }

        // ruling C15: only a stone that is not grounded is flicked (Free, Floating, Held, Flicked)
        static bool CanFlick(StoneState s) => !IsGrounded(s);

        /// <summary>Spec 2.3: a sliding stone is anchored again (Still) once its flat speed is under this while it touches
        /// ground, in m/s.</summary>
        public const float SlideStopSpeed = 0.1f;

        /// <summary>Ruling 22: a sliding stone that has touched no ground for this long (it slid off a drop) is Free, in seconds.</summary>
        public const float SlideAirSeconds = 0.2f;

        /// <summary>A contact is ground when its normal (out of the other collider, toward the stone) points at least this much
        /// up: a slope of at most 60 degrees. A wall or a stone beside it is not ground.</summary>
        public const float GroundNormalY = 0.5f;

        public static bool IsGroundContact(float normalY) => normalY >= GroundNormalY;

        /// <summary>A sliding stone's speed after <paramref name="dt"/> seconds of slowing by <paramref name="deceleration"/> m/s
        /// every second (never below 0; a speed that is not a number is 0). It slides on a frictionless material (ruling 22), so
        /// this is what slows it.</summary>
        public static float SlideSpeed(float speed, float deceleration, float dt) {
            if (!(speed > 0f) || float.IsInfinity(speed)) return 0f;
            float slow = deceleration > 0f && dt > 0f ? deceleration * dt : 0f;
            return speed > slow ? speed - slow : 0f;
        }

        /// <summary>Whether a sliding stone at this speed stands still again (under <see cref="SlideStopSpeed"/>, or not a number).</summary>
        public static bool SlideStops(float speed) => !(speed >= SlideStopSpeed);

        /// <summary>One physics step of a slide (ruling 22). Touching ground in the last step (<paramref name="touching"/>): it
        /// stands Still once the push has landed (<paramref name="pushLanded"/>: the impulse is in the velocity only after the
        /// step that follows the push) and its flat speed stops (<see cref="SlideStops"/>), else it slides on. Not touching: it
        /// slides on until <paramref name="sinceGround"/> (seconds since it last touched ground, or was pushed while standing on
        /// it) reaches <see cref="SlideAirSeconds"/>, then it is Free (a time that is not a number is Free). It is never Still in
        /// the air.</summary>
        public static SlideStep Slide(bool touching, double sinceGround, float flatSpeed, bool pushLanded) {
            if (touching) return pushLanded && SlideStops(flatSpeed) ? SlideStep.Still : SlideStep.Slides;
            return sinceGround < SlideAirSeconds - 1e-6 ? SlideStep.Slides : SlideStep.Free;   // a step landing on 0.2 s reaches it
        }

        // ---------------------------------------------------------------- Task 8: hits, clashes and knockback

        /// <summary>Ruling 24: a player's body for a stone's hit is a capsule of this radius from the head down to the feet, in metres.</summary>
        public const float PlayerRadius = 0.3f;
        /// <summary>A struck creature's stun per point of the stone's power, in seconds (the controller's ruling).</summary>
        public const float StunPerPower = 0.3f;
        /// <summary>Spec 2.6: a struck player's hitstun per point of power, in seconds.</summary>
        public const float HitstunPerPower = 0.7f / 3f;

        static readonly float Tan22 = (float)Math.Tan(22.5 * Math.PI / 180.0);   // half way between flat and 45 degrees up
        static readonly float Diagonal = (float)Math.Sqrt(0.5);

        /// <summary>Spec 3.4: a stone's power for hits and clashes, its kind's (StoneTable, from the settings) + 1 when grounded,
        /// which for power is Still or Sliding only (ruling C35: "+ 1 if grounded (still or sliding)"; a Rising or Stomped
        /// stone, grounded for <see cref="IsGrounded"/>, adds nothing here) + 1 when moving faster than
        /// <paramref name="movingSpeed"/> m/s (a speed that is not a number is not moving). 0 for a byte that is not a kind
        /// (still or sliding and moving still add).</summary>
        public static int Power(StoneKind k, StoneState s, float speed, float movingSpeed) {
            int p = StoneTable.Of(k).Power;
            if (s == StoneState.Still || s == StoneState.Sliding) p++;
            if (speed > movingSpeed) p++;
            return p;
        }

        /// <summary>Spec 2.2: two stones meet, <paramref name="a"/> and <paramref name="b"/> their powers.</summary>
        public static ClashResult Clash(int a, int b) => a == b ? ClashResult.BothBreak : a > b ? ClashResult.BBreaks : ClashResult.ABreaks;

        /// <summary>Spec 3.7: <paramref name="perPower"/> x power, once the stone and what it struck meet at
        /// <paramref name="relSpeed"/> of at least <paramref name="minSpeed"/> m/s; 0 slower, at power 0 or below, or with a
        /// damage per power that is not above 0 (anything that is not a number is 0).</summary>
        public static float Damage(int power, float relSpeed, float perPower, float minSpeed) {
            if (power <= 0 || !(relSpeed >= minSpeed) || !(perPower > 0f) || float.IsInfinity(perPower)) return 0f;
            return perPower * power;
        }

        /// <summary>Spec 3.7: the hit's cripple (a creature's knock-down), <paramref name="perPower"/> x power; 0 at power 0 or
        /// below, or with a cripple per power that is not above 0.</summary>
        public static float Cripple(int power, float perPower) =>
            power > 0 && perPower > 0f && !float.IsInfinity(perPower) ? perPower * power : 0f;

        /// <summary>How long a struck creature is stunned: power x <see cref="StunPerPower"/> seconds (0 at power 0 or below).</summary>
        public static float StunSeconds(int power) => power > 0 ? power * StunPerPower : 0f;

        /// <summary>Spec 2.6: the way a struck player is knocked, from the stone's velocity <paramref name="v"/>: along its flat
        /// heading, flat or 45 degrees up, whichever is nearer (22.5 degrees up and more is 45); length 1. A velocity that is not
        /// a number, or nearly straight up or down (its flat part under a tenth of it), takes its heading from
        /// <paramref name="away"/> (from the stone toward the player) instead: 45 degrees up when it rose, flat otherwise. Zero
        /// when neither gives a heading.</summary>
        public static V3 KnockDir(V3 v, V3 away) {
            bool finite = Finite(v);
            float flat = finite ? Flat(v) : 0f;
            V3 heading;
            bool up;
            if (finite && flat > 1e-4f && flat >= 0.1f * v.Length) {
                heading = new V3(v.X / flat, 0f, v.Z / flat);
                up = v.Y >= flat * Tan22;
            }
            else {
                float af = Finite(away) ? Flat(away) : 0f;
                if (!(af > 1e-4f)) return V3.Zero;
                heading = new V3(away.X / af, 0f, away.Z / af);
                up = finite && v.Y > 0f;
            }
            return up ? new V3(heading.X * Diagonal, Diagonal, heading.Z * Diagonal) : heading;
        }

        /// <summary>As <see cref="KnockDir(V3, V3)"/> with no fallback heading.</summary>
        public static V3 KnockDir(V3 v) => KnockDir(v, V3.Zero);

        /// <summary>Spec 2.6: a struck player's hitstun, power x 0.7 / 3 seconds; never from a Disc (and 0 at power 0 or below).</summary>
        public static float HitstunSeconds(StoneKind k, int power) => k == StoneKind.Disc || power <= 0 ? 0f : power * HitstunPerPower;

        /// <summary>Whether a stone in state <paramref name="s"/> moving at <paramref name="speed"/> m/s (its OWN speed, for a
        /// creature and a player alike: the controller's ruling on Task 8, so a creature walking into a standing stone is not
        /// hit and does not break it) can hit: never while Rising (ruling 25), and only at <paramref name="minHitSpeed"/> or faster.</summary>
        public static bool CanHit(StoneState s, float speed, float minHitSpeed) => s != StoneState.Rising && speed >= minHitSpeed;

        /// <summary>Whether a stone moving at <paramref name="velocity"/> that touches a player's capsule hits them: only with
        /// <paramref name="hurtPlayers"/> ([EarthDamage] HurtPlayers; off, stones ignore players entirely: no damage, no
        /// knockback, no break), when it can hit at its own speed (<see cref="CanHit"/>) and while it comes toward them
        /// (<see cref="Approaching"/>, <paramref name="toward"/> from the stone's nearest point to the player's).</summary>
        public static bool HitsPlayer(StoneState s, V3 velocity, V3 toward, float minHitSpeed, bool hurtPlayers) =>
            hurtPlayers && CanHit(s, velocity.Length, minHitSpeed) && Approaching(velocity, toward);

        /// <summary>Whether two touching stones clash: neither is Rising (ruling 25), and they meet at
        /// <paramref name="minHitSpeed"/> or faster relative to each other.</summary>
        public static bool Clashes(StoneState a, StoneState b, float relSpeed, float minHitSpeed) =>
            a != StoneState.Rising && b != StoneState.Rising && relSpeed >= minHitSpeed;

        /// <summary>Whether a stone moving at <paramref name="velocity"/> comes toward a player <paramref name="toward"/> of it
        /// (from the stone's nearest point to the player's): a stone that slides away from a player touching it (its owner who
        /// just pushed it) or past beside them does not hit them. More than 0.06 degrees short of square to it; false when either
        /// has no length.</summary>
        public static bool Approaching(V3 velocity, V3 toward) => V3.Dot(velocity.Normalized, toward.Normalized) > 1e-3f;

        static float Flat(V3 v) => (float)Math.Sqrt(v.X * v.X + v.Z * v.Z);

        static bool Finite(V3 v) => !float.IsNaN(v.X) && !float.IsInfinity(v.X) && !float.IsNaN(v.Y) && !float.IsInfinity(v.Y) && !float.IsNaN(v.Z) && !float.IsInfinity(v.Z);

        // ---------------------------------------------------------------- Task 11: Kick, Stomp, Uppercut, and every push

        public const float DefaultKickUp = 1500f, DefaultStompDown = 1500f, DefaultUppercutFree = 900f, DefaultUppercutGrounded = 1300f, DefaultUppercutSpin = 5f;
        /// <summary>Ruling C10: <c>[EarthModifiers] MaxStoneSpeed</c>'s default, and the cap <see cref="Pushed"/> holds to when
        /// it is given one that is not above 0 or not a number, in m/s (Ruling 42: 25, was 20).</summary>
        public const float DefaultMaxStoneSpeed = 25f;

        /// <summary>[EarthModifiers] KickUp: Kick's push straight up, in N s.</summary>
        public static float KickUp { get; private set; } = DefaultKickUp;
        /// <summary>[EarthModifiers] StompDown: Stomp's push straight down on a loose stone, in N s.</summary>
        public static float StompDown { get; private set; } = DefaultStompDown;
        /// <summary>[EarthModifiers] UppercutFree: Uppercut's push on a loose stone, as much forward along the yaw as up, in N s.</summary>
        public static float UppercutFree { get; private set; } = DefaultUppercutFree;
        /// <summary>[EarthModifiers] UppercutGrounded: Uppercut's push on a grounded stone, as much forward as up, in N s.</summary>
        public static float UppercutGrounded { get; private set; } = DefaultUppercutGrounded;
        /// <summary>[EarthModifiers] UppercutSpin: Uppercut's spin on a grounded stone, in rad/s (its top goes forward).</summary>
        public static float UppercutSpin { get; private set; } = DefaultUppercutSpin;

        /// <summary>Kick's, Stomp's and Uppercut's numbers from the settings (Server.cs).</summary>
        public static void SetImpulses(float kickUp, float stompDown, float uppercutFree, float uppercutGrounded, float uppercutSpin) {
            KickUp = kickUp;
            StompDown = stompDown;
            UppercutFree = uppercutFree;
            UppercutGrounded = uppercutGrounded;
            UppercutSpin = uppercutSpin;
        }

        /// <summary>Ruling C5 (spec 3.5): a modifier's push on a stone that is <paramref name="grounded"/> (<see cref="IsGrounded"/>)
        /// or not. <c>forward</c> is along the claimed flat yaw and <c>up</c> straight up (negative: down), both in N s;
        /// <c>spin</c> is in rad/s about the yaw's flat right axis, positive when the stone's top goes forward. Kick:
        /// <see cref="KickUp"/> up either way. Uppercut: <see cref="UppercutFree"/> forward and as much up on a loose stone;
        /// <see cref="UppercutGrounded"/> each way and <see cref="UppercutSpin"/> on a grounded one. Stomp:
        /// <see cref="StompDown"/> down on a loose stone; none on a grounded one (it stays grounded). Every other modifier none
        /// here (Straight's is <see cref="Straight"/>). Ruling 42: forward and up are times <see cref="PushScale"/>; the spin
        /// is not.</summary>
        public static (float forward, float up, float spin) Impulse(Modifier m, bool grounded) {
            float s = PushScale;
            switch (m) {
                case Modifier.Kick: return (0f, KickUp * s, 0f);
                case Modifier.Stomp: return grounded ? (0f, 0f, 0f) : (0f, -StompDown * s, 0f);
                case Modifier.Uppercut: return grounded ? (UppercutGrounded * s, UppercutGrounded * s, UppercutSpin) : (UppercutFree * s, UppercutFree * s, 0f);
                default: return (0f, 0f, 0f);
            }
        }

        /// <summary><see cref="Impulse"/> in world terms: the impulse (N s) and the angular velocity to add (rad/s), along
        /// <paramref name="flatForward"/> (the claimed yaw's direction; made flat and length 1 here, none when it has no
        /// direction or is not a number) and about its <see cref="Right"/>.</summary>
        public static (V3 impulse, V3 spin) Push(Modifier m, bool grounded, V3 flatForward) {
            (float forward, float up, float spin) i = Impulse(m, grounded);
            V3 f = new V3(flatForward.X, 0f, flatForward.Z).Normalized;
            return (f * i.forward + new V3(0f, i.up, 0f), Right(f) * i.spin);
        }

        /// <summary>The flat right of a flat forward of length 1 (up x forward in Unity's axes: yaw 0's right is +X, yaw 90's
        /// is -Z). An angular velocity along it turns the stone's top forward.</summary>
        public static V3 Right(V3 flatForward) => new V3(flatForward.Z, 0f, -flatForward.X);

        /// <summary>Ruling C10: a stone's velocity after a modifier's push, for every modifier (Straight too):
        /// <paramref name="velocity"/> + <paramref name="impulse"/> / <paramref name="mass"/>, its length held to
        /// <paramref name="cap"/> m/s (the way kept). Safe with anything: a velocity that is not a number counts as none, an
        /// impulse that is not a number or a mass that is not above 0 (or not finite) pushes nothing, a cap that is not above
        /// 0 (or not finite) is <see cref="DefaultMaxStoneSpeed"/>. Worked in doubles, so it never overflows.</summary>
        public static V3 Pushed(V3 velocity, V3 impulse, float mass, float cap) {
            double c = cap > 0f && !float.IsInfinity(cap) ? cap : DefaultMaxStoneSpeed;
            double x = 0.0, y = 0.0, z = 0.0;
            if (Finite(velocity)) { x = velocity.X; y = velocity.Y; z = velocity.Z; }
            if (Finite(impulse) && mass > 0f && !float.IsInfinity(mass)) {
                x += impulse.X / (double)mass;
                y += impulse.Y / (double)mass;
                z += impulse.Z / (double)mass;
            }
            double len = Math.Sqrt(x * x + y * y + z * z);
            if (len > c) {
                double s = c / len;
                x *= s;
                y *= s;
                z *= s;
            }
            return new V3((float)x, (float)y, (float)z);
        }

        /// <summary>Spec 2.3 (ruling C7): a Stomped stone that touches ground stands Still; every other state is not a landing.</summary>
        public static StoneState Landed(StoneState s) => s == StoneState.Stomped ? StoneState.Still : s;

        /// <summary>Whether a stone in this state needs continuous collision detection (Unity's ContinuousDynamic): only
        /// Stomped, which is driven down at up to MaxStoneSpeed (25 m/s is 0.55 m in a 22 ms step, against a 0.14 m Disc) and
        /// would otherwise end a step wholly under thin ground and never land. Every other state keeps Unity's Discrete; a
        /// kinematic state never needs it (a kinematic body cannot be ContinuousDynamic).</summary>
        public static bool Continuous(StoneState s) => s == StoneState.Stomped;

        /// <summary>Ruling C1 (and Task 9's CARRY): the state a stone is in once its rise ends, by its kind alone: the Ball is
        /// loose (Free; launched when its launch has a push, SpawnPlan.Launch), and so is the Disc (it never rises: it appears
        /// loose); the Pillar, Cube and Wall stand Still.</summary>
        public static StoneState Risen(StoneKind k) => k == StoneKind.Ball || k == StoneKind.Disc ? StoneState.Free : StoneState.Still;

        // ---------------------------------------------------------------- Task 12: Parry, Explode, the double Straight

        /// <summary>[EarthModifiers] ParrySeconds' and ParryBreakSpeed's defaults (spec 2.3: a parried stone floats 3 s, or
        /// until it moves faster than 0.1 m/s).</summary>
        public const float DefaultParrySeconds = 3f, DefaultParryBreakSpeed = 0.1f;
        /// <summary>[EarthModifiers] ExplodeRadius' (m) and ExplodeForce's (N s) defaults (spec 3.5).</summary>
        public const float DefaultExplodeRadius = 3f, DefaultExplodeForce = 1000f;
        /// <summary>Ruling C26 (spec 3.5): [EarthModifiers] DoubleStraightSeconds' default. One player can never land two
        /// Straights on one stone in any window (the game's own 0.5 s Straight cooldown, and the slide carries the stone out of
        /// reach), so a double Straight is a two-player move: a Straight answered by another player's within this time.</summary>
        public const float DefaultDoubleStraightSeconds = 0.4f;
        /// <summary>Ruling C12 (spec 3.7): an explosion staggers each living creature in it with this much cripple and no damage.</summary>
        public const float ExplodeCripple = 1f;

        /// <summary>Spec 2.3: whether a Floating (parried) stone falls again: once it has floated longer than
        /// <paramref name="seconds"/> ([EarthModifiers] ParrySeconds) or moves faster than <paramref name="breakSpeed"/> m/s
        /// (ParryBreakSpeed; a Straight on it, a blast, a bump). At either number it still floats. An age or a speed that is
        /// not a number ends it.</summary>
        public static bool ParryEnds(float age, float speed, float seconds, float breakSpeed) => !(age <= seconds) || !(speed <= breakSpeed);

        /// <summary>Ruling C12: how much of an explosion reaches something <paramref name="dist"/> metres from its centre (to
        /// its nearest point): all of it within half the <paramref name="radius"/>, falling in a straight line to a quarter at
        /// the radius, none beyond. A distance below 0 is at the centre; 0 for a distance that is not a number (or infinite)
        /// and for a radius not above 0 or not finite.</summary>
        public static float ExplodeShare(float dist, float radius) {
            if (!(radius > 0f) || float.IsInfinity(radius) || float.IsNaN(dist) || float.IsInfinity(dist)) return 0f;
            float half = radius * 0.5f;
            if (dist <= half) return 1f;
            if (dist > radius) return 0f;
            return 1f - 0.75f * (dist - half) / (radius - half);
        }

        /// <summary>Rulings C8, C26: a Straight claim at <paramref name="now"/> on a stone whose last accepted Straight claim (by
        /// anyone) was at <paramref name="last"/> (minus infinity: never) is a double Straight when it came at most
        /// <paramref name="window"/> seconds later ([EarthModifiers] DoubleStraightSeconds). Never for a last or a now that is
        /// not a finite number.</summary>
        public static bool DoubleStraight(double last, double now, float window) =>
            !double.IsNaN(last) && !double.IsInfinity(last) && !double.IsNaN(now) && !double.IsInfinity(now) && now - last <= window;

        /// <summary>Ruling C12 (spec 2.6): the way an explosion knocks a player, from <paramref name="away"/> (the blast's
        /// centre to the player's nearest point): <see cref="KnockDir(V3, V3)"/>, flat or 45 degrees up. A player (nearly)
        /// straight above or below the centre, or at it, takes the flat heading of <paramref name="fallback"/> (the exploding
        /// stone's velocity), 45 degrees up when above. With no heading from either: straight up when above (or at) the centre,
        /// none when below it or when <paramref name="away"/> is not a number. Length 1 or zero.</summary>
        public static V3 BlastDir(V3 away, V3 fallback) {
            if (!Finite(away)) return V3.Zero;
            V3 d = KnockDir(away, fallback);
            if (d.Length > 0.5f) return d;
            return away.Y >= 0f ? new V3(0f, 1f, 0f) : V3.Zero;
        }

        // ---------------------------------------------------------------- Task 13: Hold

        /// <summary>Spec 2.4, Ruling 43 (amended): a held stone is let go when a swing of the holding hand ends
        /// (<paramref name="swung"/>: <see cref="SwingGate"/> says the hand, relative to the holder's head, stayed above
        /// HoldLetGoSpeed long enough and has passed its peak), which is the throw (<see cref="Pd.ThrowVelocity"/>), or when it
        /// changed between grounded and free (<paramref name="groundedChanged"/>: a held sliding stone left the ground, a Held
        /// one became grounded). Rulings C27, C28: a swing throws only when <paramref name="armed"/> (<see cref="HoldArmed"/>:
        /// the stone has settled near its target since the hold began), so a stone caught flying, or pulled in from afar,
        /// settles first; grounded changed lets go armed or not.</summary>
        public static bool HoldLetGo(bool swung, bool groundedChanged, bool armed) => groundedChanged || (armed && swung);

        /// <summary>Ruling C28 (refines C27), Ruling 43: whether a hold's throw counts from this step: it already did
        /// (<paramref name="wasArmed"/>; false when the hold begins), or in this one step the stone is within
        /// <paramref name="armMetres"/> ([EarthModifiers] HoldArmMetres) of its target (<paramref name="trail"/>, Pd.Trail)
        /// while the hand, relative to the head (<paramref name="handSpeed"/>), is at <paramref name="letGoSpeed"/> m/s or
        /// slower. A distance or speed that is not a number does not arm it.</summary>
        public static bool HoldArmed(bool wasArmed, float trail, float handSpeed, float armMetres, float letGoSpeed) =>
            wasArmed || (trail <= armMetres && handSpeed <= letGoSpeed);

        /// <summary>A Hold by a hand on the stone that hand already holds lets it go. <paramref name="heldBy"/> is the hand
        /// (0 right, 1 left) that holds the stone for the SAME player, -1 when that player does not hold it (not held, or held
        /// by another player); <paramref name="hand"/> is the new Hold's hand.</summary>
        public static bool HoldAgain(int heldBy, int hand) => heldBy >= 0 && heldBy == hand;

        /// <summary>The review of Task 17c, I3: a push lets a held stone go first. Straight, Kick, Uppercut and Stomp on a held
        /// stone (Held, or a held slide) end the hold, then push it as an unheld one (in 1.0.0 the push's own speed did it);
        /// Parry, Explode, Flick and Hold do not (Parry lets go by changing the state, Flick takes it out of the hand, Explode
        /// keeps it held, Hold is the hold's own).</summary>
        public static bool PushLetsGo(Modifier m) => m == Modifier.Straight || m == Modifier.Kick || m == Modifier.Uppercut || m == Modifier.Stomp;

        /// <summary>The review of Task 17c, I2: for this many seconds after a hold ends (any let-go, a throw included) the stone
        /// does not hit the player who held it.</summary>
        public const float HolderSpareSeconds = 0.5f;

        /// <summary>Whether a stone let go <paramref name="sinceLetGo"/> seconds ago still spares its last holder: from 0 up to
        /// <see cref="HolderSpareSeconds"/>; a time that is not a number (or below 0) does not.</summary>
        public static bool SparesHolder(double sinceLetGo) => sinceLetGo >= 0.0 && sinceLetGo <= HolderSpareSeconds;

        /// <summary>The review of Task 17c, I2: whether a swing would throw the stone back toward its holder: the flat part of
        /// its highest relative velocity <paramref name="swing"/> against the flat way from the holder's head to the stone's
        /// target <paramref name="away"/> is below 0. A swing straight across (0) throws; either with no flat way, or not a
        /// number, does not count as back.</summary>
        public static bool ThrowsBack(V3 swing, V3 away) {
            double d = (double)swing.X * away.X + (double)swing.Z * away.Z;
            return d < 0.0;
        }

        // ---------------------------------------------------------------- Task 15: hitstop

        /// <summary>[EarthDamage] HitstopSeconds' and HitstopPerPower's defaults (spec 3.7: two stones that clash freeze for
        /// 0.06 + 0.02 x (powerA + powerB) seconds first).</summary>
        public const float DefaultHitstopSeconds = 0.06f, DefaultHitstopPerPower = 0.02f;

        /// <summary>[EarthDamage] HitstopSeconds: every clash's freeze before the powers add to it, in seconds (0 or more).</summary>
        public static float HitstopBase { get; private set; } = DefaultHitstopSeconds;
        /// <summary>[EarthDamage] HitstopPerPower: the freeze's seconds per point of the two stones' power together (0 or more).</summary>
        public static float HitstopPerPower { get; private set; } = DefaultHitstopPerPower;

        /// <summary>The hitstop's numbers from the settings (Hits.cs); one below 0 or not a finite number is 0.</summary>
        public static void SetHitstop(float seconds, float perPower) {
            HitstopBase = NotNegative(seconds);
            HitstopPerPower = NotNegative(perPower);
        }

        /// <summary>Spec 2.2, 3.7: how long two clashing stones of powers <paramref name="a"/> and <paramref name="b"/>
        /// freeze, <see cref="HitstopBase"/> + <see cref="HitstopPerPower"/> x (a + b) seconds.</summary>
        public static float HitstopSeconds(int a, int b) => HitstopSeconds(a, b, HitstopBase, HitstopPerPower);

        /// <summary>As the other, with the numbers given: <paramref name="baseSeconds"/> + <paramref name="perPower"/> x (a +
        /// b). Never negative and never a number that is not one: a power below 0 counts as 0, a base or a per-power below 0
        /// (or not a finite number) is 0; worked in doubles and held to the range of a float. 0 means no freeze.</summary>
        public static float HitstopSeconds(int a, int b, float baseSeconds, float perPower) {
            double powers = (double)Math.Max(a, 0) + Math.Max(b, 0);
            double s = NotNegative(baseSeconds) + NotNegative(perPower) * powers;
            return s < float.MaxValue ? (float)s : float.MaxValue;
        }

        /// <summary>Ruling C19: whether a contact (a creature's, a player's capsule, another stone's clash) can break a stone:
        /// never while it is <paramref name="frozen"/> in a hitstop (spec 2.2: frozen, unbreakable). Its life, its owner
        /// leaving, the server's quit, `earth clear` and a double Straight still break a frozen stone.</summary>
        public static bool Breakable(bool frozen) => !frozen;

        static float NotNegative(float v) => v >= 0f && !float.IsInfinity(v) ? v : 0f;
    }

    /// <summary>Task 13 (spec 2.4, rulings C14, C34) and Task 17c (Ruling 40): the pull that makes a held stone follow its
    /// target ahead of the holding hand, along the holder's look. Unity-free: StoneHold.cs reads the server's copy of the hand
    /// and adds the pull each physics step. Task 14 (ruling C15) and Ruling 40: Flick's pull toward the point ahead of the
    /// two hands, and its turn; StoneFlick.cs runs them. Ruling C36: both pulls are held to MaxStoneSpeed each step
    /// (<see cref="CappedPull"/>). Ruling 40 replaced spec 2.4's gains by weight (a soft, under-damped spring: a Wall at 2.5
    /// rad/s, damping 0.28, trailed a walking hand by half a metre and swung past its place) with a damped spring set by a
    /// rate: <see cref="Follow"/>, the same acceleration on every stone but for <see cref="WeightFactor"/>. Ruling 41: a held
    /// or flicked stone's centre is <c>HoldDistance</c> + half its size ahead (<see cref="HoldReach"/>).</summary>
    internal static class Pd {
        /// <summary>[EarthModifiers] HoldDistance (m: plus half the stone's size, Ruling 41), HoldLetGoSpeed (m/s),
        /// HoldArmMetres (m) defaults (spec 2.4; ruling C28: armed within 0.5 m; Task 17c: the let-go speed re-checked against
        /// the firm follow's model swing: <see cref="DefaultHoldLetGoSpeed"/>).</summary>
        public const float DefaultHoldDistance = 1f, DefaultHoldArmMetres = 0.5f;

        /// <summary>Ruling 43: [EarthModifiers] HoldLetGoSpeed's default, in m/s of the hand RELATIVE TO THE HEAD
        /// (<see cref="HandTrack"/>). A turn of the whole body with the arm out (the hand 0.6 m from the head's axis) moves the
        /// hand 1.88 m/s relative to the head at 180 degrees a second and 2.83 at 270; a swing of 1.0 m in 0.25 s is 4.0. 3.5
        /// sits between them. Walking, a dash and a knock-back move hand and head together (0); a head turn moves the target,
        /// not the hand. (Task 17c judged the stone's own speed, 5 m/s, and a 140 degree a second head turn threw a held Wall:
        /// the target is along the look.)</summary>
        public const float DefaultHoldLetGoSpeed = 3.5f;

        /// <summary>Ruling 43: [EarthModifiers] HoldThrowScale's default: a thrown stone takes the head's velocity plus the
        /// hand's velocity relative to the head times this (<see cref="ThrowVelocity"/>).</summary>
        public const float DefaultHoldThrowScale = 1.5f;

        /// <summary>Ruling 43 (amended): [EarthModifiers] HoldSwingSeconds' and HoldSwingMaxSeconds' defaults
        /// (<see cref="SwingGate"/>): the hand must stay above HoldLetGoSpeed this long before a throw can come, and the
        /// throw comes at the latest this long after it passed it. HoldSwingSeconds is 0.1 (the ruling said 0.05, then 0.11).
        /// A single jump of the hand's copy (a late packet, a hitch, lost tracking, a snap turn) or a copy that freezes and
        /// catches up is kept from throwing by HandTrack's lone-jump guard, at every step rate from 15 to 50 (the review of Task
        /// 17c, I1: without it a jump stayed in the 0.1 s reading for ceil(0.1 / step) steps, 0.111 s at 45 steps a second, and
        /// threw past 0.11). The sustain is then only for what the guard leaves; at 0.11 a swing of 0.8 m in a quarter second,
        /// whose reading stays above 3.5 for 0.115 s, threw on only about half the start moments at 30 and 50 steps a second
        /// (9 and 15 of 20); at 0.1 it throws 20 of 20 at 20, 30, 35, 40, 45 and 50. A short hard flick (0.6 m in 0.15 s)
        /// throws only sometimes.</summary>
        public const float DefaultHoldSwingSeconds = 0.1f, DefaultHoldSwingMaxSeconds = 0.2f;

        /// <summary>Ruling 43: the velocity a held stone is thrown with: <paramref name="headVelocity"/> +
        /// <paramref name="handRelative"/> (the hand's velocity relative to the head) x <paramref name="scale"/>
        /// ([EarthModifiers] HoldThrowScale; below 0 or not a number is 0), when that is faster than the stone's own
        /// <paramref name="stoneVelocity"/>; otherwise its own. A stone held along the ground (<paramref name="grounded"/>, the
        /// slide-hold) takes only the flat part of the throw and keeps its own up and down. Then held to
        /// <paramref name="cap"/> m/s (MaxStoneSpeed, as StoneRules.Pushed: a cap not above 0 or not a number is the default).
        /// A head or hand velocity that is not a number throws nothing (the stone keeps its own); a stone velocity that is not a
        /// number counts as none.</summary>
        public static V3 ThrowVelocity(V3 stoneVelocity, V3 headVelocity, V3 handRelative, float scale, bool grounded, float cap) {
            if (!Finite(stoneVelocity)) stoneVelocity = V3.Zero;
            V3 kept = stoneVelocity;
            if (Finite(headVelocity) && Finite(handRelative)) {
                float s = scale >= 0f && !float.IsInfinity(scale) ? scale : 0f;
                V3 swing = headVelocity + handRelative * s;
                if (grounded) swing = new V3(swing.X, stoneVelocity.Y, swing.Z);
                if (Finite(swing) && swing.Length > stoneVelocity.Length) kept = swing;
            }
            return StoneRules.Pushed(kept, V3.Zero, 1f, cap);
        }

        /// <summary>Ruling 40: [EarthModifiers] HoldFollowRate (rad/s), HoldDamping, FlickFollowRate (rad/s), FlickDamping,
        /// FlickTurnRate (rad/s) and FlickTurnDamping defaults. A damping of 1 is critical: the stone closes on its target as
        /// fast as it can without passing it.</summary>
        public const float DefaultHoldFollowRate = 12f, DefaultHoldDamping = 1f, DefaultFlickFollowRate = 10f, DefaultFlickDamping = 1f,
            DefaultFlickTurnRate = 10f, DefaultFlickTurnDamping = 1f;

        /// <summary>Ruling 40's guard against a slow physics step: the rate a step of dt seconds uses is at most this / (dt x
        /// the damping, or 1 if less). At 50 steps a second that is 30 rad/s (never reached by the defaults); at 20 it is 12 (the
        /// Disc's 12.6 is held to it); slower steps follow more softly instead of swinging ever wider (semi-implicit Euler
        /// with a damping term past 2 per step runs away).</summary>
        public const float StepLimit = 0.6f;

        /// <summary>The flick's turn fallback: the look made square to up shorter than this (up within about 6 degrees of the
        /// look, or of straight back along it) has too little way left to face along.</summary>
        public const float TurnFallback = 0.1f;

        /// <summary>How far a held stone trails its target, from <paramref name="error"/> (target minus its centre): the whole
        /// distance on a free hold, the flat distance on a grounded one (it slides at its own height under a target at hand
        /// height, and <see cref="Follow"/> pulls it along x and z only). Infinitely far for an error that is not a number.</summary>
        public static float Trail(V3 error, bool grounded) {
            if (!Finite(error)) return float.PositiveInfinity;
            return grounded ? new V3(error.X, 0f, error.Z).Length : error.Length;
        }

        /// <summary>Ruling 40: the weight factor that multiplies the follow's rate, clamp(1.15 - mass / 1000, 0.7, 1.05): the
        /// Disc 1.05, the Ball 1.0, the Pillar 0.875, the Cube 0.85, the Wall 0.75. A mass that is not a number is the
        /// heaviest (0.7).</summary>
        public static float WeightFactor(float mass) {
            if (float.IsNaN(mass)) return 0.7f;
            double w = 1.15 - mass / 1000.0;
            return w < 0.7 ? 0.7f : w > 1.05 ? 1.05f : (float)w;
        }

        /// <summary>Ruling 40's guard (<see cref="StepLimit"/>): <paramref name="rate"/> held to StepLimit / (dt x
        /// max(<paramref name="damping"/>, 1)) for a physics step of <paramref name="dt"/> s. A dt that is not above 0 (or not
        /// finite) leaves the rate.</summary>
        public static double StepRate(double rate, double damping, float dt) {
            if (!(dt > 0f) || float.IsInfinity(dt)) return rate;
            double most = StepLimit / (dt * Math.Max(damping, 1.0));
            return rate < most ? rate : most;
        }

        /// <summary>Ruling 40: the follow's acceleration in m/s every second (ForceMode.Acceleration; times the mass for a
        /// force), from <paramref name="error"/> (target minus the stone's centre, m) and its <paramref name="velocity"/>
        /// (m/s): w² x error - 2 x <paramref name="damping"/> x w x velocity per axis, w = <paramref name="rate"/> (rad/s) x
        /// <see cref="WeightFactor"/>(<paramref name="mass"/>), held by <see cref="StepRate"/> for a step of
        /// <paramref name="dt"/> s. On a <paramref name="grounded"/> hold (a held slide, ruling C14) only x and z: it slides
        /// along the ground. The caller cancels gravity on a free hold or a flick. Never a number that is not one: an error that
        /// is not one pulls nothing, a velocity that is not one counts as none, a rate not above 0 (or not finite) pulls nothing,
        /// a damping below 0 (or not finite) does not damp. Worked in doubles, held to the range of a float.</summary>
        public static V3 Follow(V3 error, V3 velocity, float mass, bool grounded, float rate, float damping, float dt) {
            if (!Finite(error) || !(rate > 0f) || float.IsInfinity(rate)) return V3.Zero;
            if (!Finite(velocity)) velocity = V3.Zero;
            double z = damping >= 0f && !float.IsInfinity(damping) ? damping : 0.0;
            double w = StepRate((double)rate * WeightFactor(mass), z, dt);
            double k = w * w, c = 2.0 * z * w;
            double ax = k * error.X - c * velocity.X;
            double ay = grounded ? 0.0 : k * error.Y - c * velocity.Y;
            double az = k * error.Z - c * velocity.Z;
            return new V3(InRange(ax), InRange(ay), InRange(az));
        }

        /// <summary>Ruling 41 (the review of Task 17c, I4): a stone's size term for Hold's and Flick's distance, in metres:
        /// (width + depth) / 4 of its collider (StoneTable's size; half its mean flat half-size), the Ball's radius. The Disc
        /// 0.35, the Ball 0.30, the Pillar 0.275, the Cube 0.50, the Wall 0.725 (the flat half-diagonal, 1.23 for a Wall, put
        /// a held Wall past the game's reach). 0 for a byte that is not a kind.</summary>
        public static float HalfSize(StoneKind k) {
            if (!StoneTable.Valid(k)) return 0f;
            V3 s = StoneTable.Of(k).Size;
            if (k == StoneKind.Ball) return s.X * 0.5f;
            return (s.X + s.Z) * 0.25f;
        }

        /// <summary>Ruling 41: how far ahead of the hand (Hold) or of the point between the hands (Flick) the stone's centre
        /// is pulled: <paramref name="distance"/> ([EarthModifiers] HoldDistance, about the gap from the hand to the stone) +
        /// <see cref="HalfSize"/>. With the default 1 m: the Disc 1.35, the Ball 1.30, the Pillar 1.275, the Cube 1.50, the
        /// Wall 1.725. A distance below 0 or not a number counts as 0.</summary>
        public static float HoldReach(StoneKind k, float distance) =>
            (distance > 0f && !float.IsInfinity(distance) ? distance : 0f) + HalfSize(k);

        /// <summary>Ruling C34: where a held stone is pulled to: <paramref name="hand"/> (the holding controller's place) plus
        /// <paramref name="distance"/> m (<see cref="HoldReach"/>) along <paramref name="headForward"/> (the holder's look)
        /// made flat and length 1, as Flick (<see cref="FlickTarget"/> with both hands at the holding hand). Never the
        /// controller's own forward: its +z is not the fingers on every controller, and the finger axis is a per-game
        /// [EarthPose] setting the server cannot see. The hand itself when the look has no flat way (straight up or down, or
        /// not a number) or the distance is not above 0 (or not finite).</summary>
        public static V3 HoldTarget(V3 hand, V3 headForward, float distance) {
            if (!(distance > 0f) || float.IsInfinity(distance) || !Finite(headForward)) return hand;
            return hand + new V3(headForward.X, 0f, headForward.Z).Normalized * distance;
        }

        /// <summary>Ruling C36: a hold's or a flick's <paramref name="force"/> (N) for one physics step of <paramref name="dt"/>
        /// s on a stone of <paramref name="mass"/> kg moving at <paramref name="velocity"/> (m/s), held so the step leaves it
        /// no faster than <paramref name="cap"/> m/s ([EarthModifiers] MaxStoneSpeed; a cap not above 0 or not finite is
        /// StoneRules.DefaultMaxStoneSpeed, as StoneRules.Pushed). The force itself, untouched, when velocity + force x dt /
        /// mass is within the cap; otherwise the force that ends the step at the cap along that way (a stone already past it
        /// is slowed to it). The pull grows with the distance (a stone stuck behind something, a holder who teleports), and
        /// Unity applies a force in the step after FixedUpdate, so the cap is on the velocity the step ends with. A force that
        /// is not a number pulls nothing; a velocity that is not a number, or a mass or dt not above 0 (or not finite), leaves
        /// the force. Worked in doubles, held to the range of a float.</summary>
        public static V3 CappedPull(V3 force, V3 velocity, float mass, float dt, float cap) {
            if (!Finite(force)) return V3.Zero;
            if (!Finite(velocity) || !(mass > 0f) || float.IsInfinity(mass) || !(dt > 0f) || float.IsInfinity(dt)) return force;
            double c = cap > 0f && !float.IsInfinity(cap) ? cap : StoneRules.DefaultMaxStoneSpeed;
            double k = dt / (double)mass;
            double x = velocity.X + force.X * k, y = velocity.Y + force.Y * k, z = velocity.Z + force.Z * k;
            double len = Math.Sqrt(x * x + y * y + z * z);
            if (!(len > c)) return force;
            double s = c / len;
            return new V3(InRange((x * s - velocity.X) / k), InRange((y * s - velocity.Y) / k), InRange((z * s - velocity.Z) / k));
        }

        // ---------------------------------------------------------------- Task 14: Flick

        /// <summary>Where a flicked stone is pulled to: the midpoint of the two hands plus <paramref name="distance"/> m
        /// (<see cref="HoldReach"/>: HoldDistance + half its size, Ruling 41) along <paramref name="headForward"/> (the
        /// flicker's look) made flat and length 1. The midpoint itself when the look has no flat way (straight up or down, or
        /// not a number) or the distance is not above 0 (or not finite).</summary>
        public static V3 FlickTarget(V3 handA, V3 handB, V3 headForward, float distance) {
            V3 mid = (handA + handB) * 0.5f;
            if (!(distance > 0f) || float.IsInfinity(distance) || !Finite(headForward)) return mid;
            return mid + new V3(headForward.X, 0f, headForward.Z).Normalized * distance;
        }

        /// <summary>Ruling C15: the turn a flicked stone is turned to, as its forward and up (both length 1, square to each
        /// other; Unity's LookRotation(forward, up)). Up is from <paramref name="handB"/> to <paramref name="handA"/> (the
        /// pose's A hand above its B: upright); straight up when the hands are at one place or not a number. Forward is
        /// <paramref name="flatForward"/> (the flicker's look; made flat and length 1 here, +Z when it has no flat way) made
        /// square to up, so the stone faces the look whatever the hands' tilt. When up is (nearly) along the look or straight
        /// back along it (<see cref="TurnFallback"/>), that leaves no way to face along; then forward is straight down made
        /// square to up when A is ahead of B, straight up when behind: the way the forward goes as the hands tilt there from
        /// upright, so tilting through it turns the stone smoothly.</summary>
        public static (V3 forward, V3 up) FlickTurn(V3 handA, V3 handB, V3 flatForward) {
            V3 up = Finite(handA) && Finite(handB) ? (handA - handB).Normalized : V3.Zero;
            if (up.Length < 0.5f) up = new V3(0f, 1f, 0f);
            V3 look = Finite(flatForward) ? new V3(flatForward.X, 0f, flatForward.Z).Normalized : V3.Zero;
            if (look.Length < 0.5f) look = new V3(0f, 0f, 1f);
            float along = V3.Dot(look, up);
            V3 forward = look - up * along;
            if (forward.Length < TurnFallback) {
                var vertical = new V3(0f, along > 0f ? -1f : 1f, 0f);
                forward = vertical - up * V3.Dot(vertical, up);
            }
            return (forward.Normalized, up);
        }

        /// <summary>The turn of the quaternion (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>,
        /// <paramref name="w"/>) (Unity's order; any length) as its axis x its angle in radians, the short way (at most pi):
        /// the wanted turn times the inverse of the stone's is the turn still to go. None for no turn, or anything that is not
        /// a number.</summary>
        public static V3 TurnVector(float x, float y, float z, float w) {
            if (float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(y) || float.IsInfinity(y) || float.IsNaN(z) || float.IsInfinity(z) || float.IsNaN(w) || float.IsInfinity(w))
                return V3.Zero;
            double qx = x, qy = y, qz = z, qw = w;
            if (qw < 0.0) { qx = -qx; qy = -qy; qz = -qz; qw = -qw; }   // q and -q are the same turn; w >= 0 is the short way
            double s = Math.Sqrt(qx * qx + qy * qy + qz * qz);
            if (!(s > 1e-12)) return V3.Zero;
            double k = 2.0 * Math.Atan2(s, qw) / s;
            return new V3(InRange(qx * k), InRange(qy * k), InRange(qz * k));
        }

        /// <summary>The flick's turn (ruling C15, Ruling 40): <paramref name="rate"/>² ([EarthModifiers] FlickTurnRate, rad/s)
        /// x <paramref name="turn"/> (<see cref="TurnVector"/>: the turn still to go) - 2 x <paramref name="damping"/>
        /// (FlickTurnDamping) x rate x <paramref name="angularVelocity"/> (rad/s), the rate held by <see cref="StepRate"/>
        /// for a step of <paramref name="dt"/> s: an angular acceleration in rad/s every second (Unity's
        /// ForceMode.Acceleration: the same on every stone, whatever its inertia). A turn that is not a number turns nothing,
        /// a spin that is not one counts as none, a rate not above 0 (or not finite) turns nothing, a damping below 0 (or not
        /// finite) does not damp. Worked in doubles, held to the range of a float.</summary>
        public static V3 FlickTorque(V3 turn, V3 angularVelocity, float rate, float damping, float dt) {
            if (!Finite(turn)) turn = V3.Zero;
            if (!Finite(angularVelocity)) angularVelocity = V3.Zero;
            if (!(rate > 0f) || float.IsInfinity(rate)) return V3.Zero;
            double z = damping >= 0f && !float.IsInfinity(damping) ? damping : 0.0;
            double w = StepRate(rate, z, dt), k = w * w, c = 2.0 * z * w;
            return new V3(InRange(k * turn.X - c * angularVelocity.X), InRange(k * turn.Y - c * angularVelocity.Y), InRange(k * turn.Z - c * angularVelocity.Z));
        }

        static float InRange(double v) => v > float.MaxValue ? float.MaxValue : v < -float.MaxValue ? -float.MaxValue : (float)v;

        static bool Finite(V3 v) => !float.IsNaN(v.X) && !float.IsInfinity(v.X) && !float.IsNaN(v.Y) && !float.IsInfinity(v.Y) && !float.IsNaN(v.Z) && !float.IsInfinity(v.Z);
    }

    /// <summary>Ruling 43 (amended): when a swing of the holding hand throws. Fed each physics step the hand's speed and
    /// velocity relative to the head (<see cref="HandTrack"/>). A swing starts when the speed passes HoldLetGoSpeed. It is
    /// sustained once it has stayed above it for HoldSwingSeconds; a swing that drops back under before then throws nothing
    /// and the next starts over (a spike: the server's hand and head copies arriving out of step in a dash or a knock-back).
    /// A sustained swing throws at its peak: in the first step whose reading is lower than the highest since the swing
    /// started (dropping under HoldLetGoSpeed is lower too), or HoldSwingMaxSeconds after it started, whichever comes first.
    /// The throw carries the HIGHEST relative velocity read in the swing (its way too). Nothing is allocated.</summary>
    internal sealed class SwingGate {
        const double Eps = 1e-6;   // a step that lands on a time counts as past it

        bool fast;
        double since;
        float peak;
        V3 peakVelocity;

        /// <summary>Whether a swing is under way (the hand above HoldLetGoSpeed since <see cref="Since"/>).</summary>
        public bool Swinging => fast;
        /// <summary>When the swing under way passed HoldLetGoSpeed.</summary>
        public double Since => since;
        /// <summary>The highest relative speed read in the swing under way (0 with none).</summary>
        public float Peak => fast ? peak : 0f;

        /// <summary>Forgets the swing under way.</summary>
        public void Clear() {
            fast = false;
            peak = 0f;
            peakVelocity = V3.Zero;
        }

        /// <summary>One physics step at <paramref name="now"/> (s) with the hand's relative <paramref name="speed"/> and
        /// <paramref name="velocity"/> (m/s), the let-go speed, <paramref name="sustainSeconds"/> (HoldSwingSeconds) and
        /// <paramref name="maxSeconds"/> (HoldSwingMaxSeconds). True when the swing throws now: <paramref name="swing"/> is
        /// then its highest relative velocity, and the gate is clear again. A speed or velocity that is not a number (no track
        /// yet) is not fast; a time that is not a number clears the gate.</summary>
        public bool Step(double now, float speed, V3 velocity, float letGoSpeed, float sustainSeconds, float maxSeconds, out V3 swing) {
            swing = V3.Zero;
            if (double.IsNaN(now) || double.IsInfinity(now)) { Clear(); return false; }
            bool above = speed > letGoSpeed && !float.IsInfinity(speed) && Finite(velocity);
            double sustain = sustainSeconds > 0f && !float.IsInfinity(sustainSeconds) ? sustainSeconds : 0.0;
            double most = maxSeconds > 0f && !float.IsInfinity(maxSeconds) ? maxSeconds : 0.0;
            if (!above) {
                bool sustained = fast && now - since >= sustain - Eps;
                if (sustained) swing = peakVelocity;
                Clear();
                return sustained;   // a sustained swing that drops under has passed its peak; one that was not throws nothing
            }
            if (!fast) {
                fast = true;
                since = now;
                peak = speed;
                peakVelocity = velocity;
            }
            else if (speed > peak) {
                peak = speed;
                peakVelocity = velocity;
            }
            double held = now - since;
            if (held >= sustain - Eps && (speed < peak || held >= most - Eps)) {
                swing = peakVelocity;
                Clear();
                return true;
            }
            return false;
        }

        static bool Finite(V3 v) => !float.IsNaN(v.X) && !float.IsInfinity(v.X) && !float.IsNaN(v.Y) && !float.IsInfinity(v.Y) && !float.IsNaN(v.Z) && !float.IsInfinity(v.Z);
    }

    /// <summary>The settings one hold step reads ([EarthModifiers]): HoldLetGoSpeed, HoldSwingSeconds, HoldSwingMaxSeconds,
    /// HoldArmMetres, HoldThrowScale and MaxStoneSpeed.</summary>
    internal struct HoldThrowSettings {
        public float LetGoSpeed, SwingSeconds, SwingMaxSeconds, ArmMetres, ThrowScale, MaxSpeed;

        public HoldThrowSettings(float letGoSpeed, float swingSeconds, float swingMaxSeconds, float armMetres, float throwScale, float maxSpeed) {
            LetGoSpeed = letGoSpeed;
            SwingSeconds = swingSeconds;
            SwingMaxSeconds = swingMaxSeconds;
            ArmMetres = armMetres;
            ThrowScale = throwScale;
            MaxSpeed = maxSpeed;
        }

        /// <summary>The defaults.</summary>
        public static HoldThrowSettings Default => new HoldThrowSettings(Pd.DefaultHoldLetGoSpeed, Pd.DefaultHoldSwingSeconds, Pd.DefaultHoldSwingMaxSeconds,
            Pd.DefaultHoldArmMetres, Pd.DefaultHoldThrowScale, StoneRules.DefaultMaxStoneSpeed);
    }

    /// <summary>What one hold step decided: hold on, throw, or a swing back toward the holder that was let pass (held).</summary>
    internal enum HoldOutcome : byte { Hold, Thrown, Back }

    /// <summary>One physics step of a hold's throw (Ruling 43 and its review; the review's M5: StoneHold.DriveHold and the
    /// tests' model both run this, so they cannot drift). In this order: the swing (<see cref="SwingGate"/>, fed the hand's
    /// relative velocity when <paramref name="tracked"/>); a swing that ends on an armed stone throws it
    /// (<see cref="Pd.ThrowVelocity"/> into <paramref name="thrown"/>), unless it points back toward the holder
    /// (<see cref="StoneRules.ThrowsBack"/> against <paramref name="away"/>, the way from the head to the target: then the
    /// stone stays held, <see cref="HoldOutcome.Back"/>, and the next swing starts over); then the arming
    /// (<see cref="StoneRules.HoldArmed"/>, after the let-go: a swing ending in the very step the stone settles throws
    /// nothing). <paramref name="swing"/> is the swing that ended this step (zero with none). The caller lets go first when the
    /// stone changed between grounded and free.</summary>
    internal static class HoldStep {
        public static HoldOutcome Step(SwingGate gate, ref bool armed, double now, bool tracked, V3 handRelative, V3 headVelocity, V3 stoneVelocity,
            float trail, V3 away, bool grounded, in HoldThrowSettings s, out V3 thrown, out V3 swing) {
            float handSpeed = tracked ? handRelative.Length : float.NaN;
            bool swung = gate.Step(now, handSpeed, handRelative, s.LetGoSpeed, s.SwingSeconds, s.SwingMaxSeconds, out swing);
            thrown = stoneVelocity;
            if (StoneRules.HoldLetGo(swung, false, armed)) {
                if (StoneRules.ThrowsBack(swing, away)) return HoldOutcome.Back;
                thrown = Pd.ThrowVelocity(stoneVelocity, headVelocity, swing, s.ThrowScale, grounded, s.MaxSpeed);
                return HoldOutcome.Thrown;
            }
            armed = StoneRules.HoldArmed(armed, trail, handSpeed, s.ArmMetres, s.LetGoSpeed);
            return HoldOutcome.Hold;
        }
    }

    /// <summary>Ruling 43: the holding hand's and the head's recent places (the server's copies), one sample a step, and
    /// from them the hand's velocity RELATIVE TO THE HEAD and the head's own over the newest stretch of at least
    /// <see cref="Window"/> seconds (about 0.1 s). The head's is its change in place over that time. The hand's adds up the
    /// stretch's step-by-step changes of (hand - head), but the largest single one counts for at most <see cref="JumpFactor"/>
    /// times the second largest, and for nothing when every other step is still (the review of Task 17c, I1): a lone jump
    /// of the hand's copy (a late packet, a hitch, lost tracking, a snap turn) is not a swing, whatever the step rate.
    /// Unity-free: StoneHold.cs feeds it. A sample that is not a number is skipped (<see cref="Add"/> says so); a sample at
    /// the newest sample's time replaces it (several physics steps in one frame read the same copies); a time that goes back
    /// starts the track again. Nothing is allocated after it is made.</summary>
    internal sealed class HandTrack {
        /// <summary>The time the velocities are measured over, at least, in seconds.</summary>
        public const double Window = 0.1;
        /// <summary>The largest step of a stretch counts for at most this many times the second largest.</summary>
        public const float JumpFactor = 3f;
        const int Size = 64;   // 0.1 s at up to 640 samples a second
        const double Eps = 1e-6;   // a time summed from a float step (review M1)

        readonly double[] at = new double[Size];
        readonly V3[] hand = new V3[Size], head = new V3[Size];
        int count, newest = -1;

        /// <summary>One step's sample: the time (s), the hand's place and the head's place. False, and nothing kept, for a
        /// sample that is not a number (the caller then treats the step as untracked: review M2).</summary>
        public bool Add(double time, V3 handAt, V3 headAt) {
            if (double.IsNaN(time) || double.IsInfinity(time) || !Finite(handAt) || !Finite(headAt)) return false;
            if (count > 0 && time < at[newest]) Clear();
            if (count == 0 || time > at[newest]) {
                newest = (newest + 1) % Size;
                if (count < Size) count++;
            }
            at[newest] = time;
            hand[newest] = handAt;
            head[newest] = headAt;
            return true;
        }

        /// <summary>Forgets every sample.</summary>
        public void Clear() {
            count = 0;
            newest = -1;
        }

        /// <summary>The hand's velocity relative to the head (a lone jump trimmed) and the head's velocity (m/s), over the
        /// newest stretch of at least <see cref="Window"/> seconds the track holds; false (both zero) until it holds one.</summary>
        public bool Velocities(out V3 handRelative, out V3 headVelocity) {
            handRelative = V3.Zero;
            headVelocity = V3.Zero;
            if (count < 2) return false;
            for (int k = 1; k < count; k++) {
                int i = Index(k);
                double span = at[newest] - at[i];
                if (span < Window - Eps) continue;
                float inv = (float)(1.0 / span);
                headVelocity = (head[newest] - head[i]) * inv;
                // the steps from sample i to the newest: the largest and the second largest
                float m1 = 0f, m2 = 0f;
                int big = -1;
                for (int s = k; s >= 1; s--) {
                    float m = Step(s).Length;
                    if (m > m1) { m2 = m1; m1 = m; big = s; }
                    else if (m > m2) m2 = m;
                }
                V3 sum = Rel(0) - Rel(k);
                if (big >= 0 && m1 > JumpFactor * m2) sum = sum - Step(big) * (1f - JumpFactor * m2 / m1);
                handRelative = sum * inv;
                return true;
            }
            return false;
        }

        /// <summary>The hand's speed relative to the head (m/s); not a number until the track holds a whole window.</summary>
        public float RelativeSpeed => Velocities(out V3 rel, out _) ? rel.Length : float.NaN;

        // the sample k back from the newest, its (hand - head), and the step into the sample s - 1 back from sample s back
        int Index(int k) => ((newest - k) % Size + Size) % Size;
        V3 Rel(int k) => hand[Index(k)] - head[Index(k)];
        V3 Step(int s) => Rel(s - 1) - Rel(s);

        static bool Finite(V3 v) => !float.IsNaN(v.X) && !float.IsInfinity(v.X) && !float.IsNaN(v.Y) && !float.IsInfinity(v.Y) && !float.IsNaN(v.Z) && !float.IsInfinity(v.Z);
    }

    /// <summary>What one physics step of a slide ends in (<see cref="StoneRules.Slide"/>): it slides on, it stands Still, or
    /// it is Free (it left the ground).</summary>
    internal enum SlideStep : byte { Slides, Still, Free }

    /// <summary>The server's cooldowns, per player and move (keyed as <c>(int)Move.X</c>). The game keeps its own cooldowns
    /// (PoseMachine); this one only stops a game that sends claims faster than a pose machine can fire them.</summary>
    internal sealed class Ledger {
        const double Eps = 1e-6;   // seconds: a claim that lands on the cooldown's end counts as past it

        readonly Dictionary<ulong, Dictionary<int, double>> spent = new Dictionary<ulong, Dictionary<int, double>>();

        /// <summary>True, and the move's cooldown starts again at <paramref name="now"/>, when this player's last spend of this
        /// move was at least <paramref name="cooldown"/> seconds ago (or never); false, and nothing changes, when it was not.
        /// A <paramref name="now"/> that is not a number is refused.</summary>
        public bool TrySpend(ulong account, int move, double now, float cooldown) {
            if (double.IsNaN(now) || double.IsInfinity(now)) return false;
            if (!spent.TryGetValue(account, out Dictionary<int, double> moves)) spent[account] = moves = new Dictionary<int, double>();
            if (moves.TryGetValue(move, out double last) && now - last < cooldown - Eps) return false;
            moves[move] = now;
            return true;
        }

        /// <summary>Drops a player's cooldowns (they left).</summary>
        public void Forget(ulong account) => spent.Remove(account);
    }

    /// <summary>Why the server refused a Summon claim. <see cref="Bad"/> (ruling 13): a kind byte that is not a stone, or a
    /// head or yaw that is not a finite number (or a yaw past a full turn): a claim no game of this mod sends.</summary>
    internal enum SummonRefusal { None, Cooldown, HeadFar, NotNatural, KindOff, NoPlayer, Bad }

    internal static class SummonCheck {
        /// <summary>A game's yaw is an Atan2 in degrees (-180 to 180); a larger one is not a game's, and its sine may not be a
        /// number.</summary>
        public const float MaxYaw = 360f;

        /// <summary>The claim could have come from a game of this mod: a stone's kind, a finite head, a finite yaw within a
        /// turn.</summary>
        public static bool Sane(in SummonClaim c) => StoneTable.Valid(c.Kind) && Finite(c.Head) && Finite(c.Yaw) && Math.Abs(c.Yaw) <= MaxYaw;

        /// <summary>The server's decision on a claim, in this order: <see cref="SummonRefusal.Bad"/>, the kind is off
        /// (<see cref="SummonRefusal.KindOff"/>), the player's cooldown (<see cref="SummonRefusal.Cooldown"/>), the claimed head
        /// further than <paramref name="headSlack"/> metres from the server's own copy (<see cref="SummonRefusal.HeadFar"/>; a
        /// server head that is not a number is far), the ground at the spot is not natural (<see cref="SummonRefusal.NotNatural"/>).
        /// <see cref="SummonRefusal.NoPlayer"/> is the caller's (the sender has no body on the server).</summary>
        public static SummonRefusal Decide(in SummonClaim c, V3 serverHead, bool spotNatural, bool kindEnabled, bool cooldownOk, float headSlack) {
            if (!Sane(in c)) return SummonRefusal.Bad;
            if (!kindEnabled) return SummonRefusal.KindOff;
            if (!cooldownOk) return SummonRefusal.Cooldown;
            if (!((c.Head - serverHead).Length <= headSlack)) return SummonRefusal.HeadFar;
            if (!spotNatural) return SummonRefusal.NotNatural;
            return SummonRefusal.None;
        }

        /// <summary>The structure move that summons a kind (Disc ... Wall in both enums' order).</summary>
        public static Move MoveOf(StoneKind k) => (Move)((int)Move.Disc + (int)k);

        /// <summary>The kind a structure move summons; false for every other move.</summary>
        public static bool KindOf(Move m, out StoneKind kind) {
            kind = (StoneKind)(m - Move.Disc);
            return m >= Move.Disc && m <= Move.Wall;
        }

        static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        static bool Finite(V3 v) => Finite(v.X) && Finite(v.Y) && Finite(v.Z);
    }

    /// <summary>Where a summoned stone rises (spec 3.4): <c>Ahead</c> metres in front of the head along the yaw, its bottom on
    /// the ground there at the end; it starts lowered by its own height (its top at ground level, ruling 5) and rises straight
    /// up over <c>RiseSeconds</c>, turned to the yaw. The [EarthStones] settings (Server.cs) replace both numbers per kind
    /// through <see cref="Set"/>. Task 9 (rulings C1, C2): the Disc does not rise (<see cref="Rises"/>): it appears loose,
    /// <see cref="DiscHeight"/> above the ground; the Disc and the Ball are launched (<see cref="Launch"/>). Those numbers,
    /// and <see cref="SummonUnderLift"/>, are [EarthStones] settings too (<see cref="SetLaunch"/>).</summary>
    internal static class SpawnPlan {
        static readonly float[] AheadDefaults = { 1.2f, 1.2f, 1.3f, 1.5f, 1.8f };     // Disc, Ball, Pillar, Cube, Wall
        static readonly float[] RiseDefaults = { 0f, 0.12f, 0.25f, 0.25f, 0.32f };    // the Disc never rises; the Ball's 0.6 m at 5 m/s; Ruling 42: Pillar, Cube, Wall quicker (were 0.35, 0.35, 0.45)
        static readonly float[] ahead = (float[])AheadDefaults.Clone(), rise = (float[])RiseDefaults.Clone();

        public const float DefaultDiscLift = 300f, DefaultDiscPush = 1400f, DefaultDiscPushDelay = 0.12f, DefaultDiscHeight = 0.5f;
        public const float DefaultBallLaunchSpeed = 5f, DefaultSummonUnderLift = 4f;

        /// <summary>[EarthStones] DiscLift: the Disc's first push, straight up as it appears, in N s.</summary>
        public static float DiscLift { get; private set; } = DefaultDiscLift;
        /// <summary>[EarthStones] DiscPush: the Disc's second push, ahead along the yaw, in N s.</summary>
        public static float DiscPush { get; private set; } = DefaultDiscPush;
        /// <summary>[EarthStones] DiscPushDelaySeconds: the time from the Disc's first push to its second.</summary>
        public static float DiscPushDelay { get; private set; } = DefaultDiscPushDelay;
        /// <summary>[EarthStones] DiscHeightMetres: the Disc's centre above the ground where it appears.</summary>
        public static float DiscHeight { get; private set; } = DefaultDiscHeight;
        /// <summary>[EarthStones] BallLaunchSpeed: the Ball's speed straight up once it has risen, in m/s.</summary>
        public static float BallLaunchSpeed { get; private set; } = DefaultBallLaunchSpeed;
        /// <summary>[EarthStones] SummonUnderLift: the speed a standing stone is thrown up at when another is summoned where it
        /// stands (<see cref="StoneRules.SummonUnder"/>), in m/s.</summary>
        public static float SummonUnderLift { get; private set; } = DefaultSummonUnderLift;

        public static float DefaultAhead(StoneKind k) => StoneTable.Valid(k) ? AheadDefaults[(int)k] : 0f;
        public static float DefaultRise(StoneKind k) => StoneTable.Valid(k) ? RiseDefaults[(int)k] : 0f;

        /// <summary>Ruling C1: every kind rises out of the ground (Rising, kinematic) but the Disc, which appears loose (Free) in
        /// the air where its plan ends.</summary>
        public static bool Rises(StoneKind k) => k != StoneKind.Disc;

        /// <summary>The launch numbers and <see cref="SummonUnderLift"/> from the settings.</summary>
        public static void SetLaunch(float discLift, float discPush, float discPushDelaySeconds, float discHeightMetres, float ballLaunchSpeed, float summonUnderLift) {
            DiscLift = discLift;
            DiscPush = discPush;
            DiscPushDelay = discPushDelaySeconds;
            DiscHeight = discHeightMetres;
            BallLaunchSpeed = ballLaunchSpeed;
            SummonUnderLift = summonUnderLift;
        }

        /// <summary>Ruling C2: the kind's launch, impulses in N s in the stone's yaw frame (x right, y up, z ahead; the caller
        /// turns them by the yaw). The first lands as the Disc appears, or as the Ball's rise ends (it is loose then); the second
        /// <c>delay</c> seconds after the first. The Disc: <see cref="DiscLift"/> up, then <see cref="DiscPush"/> ahead after
        /// <see cref="DiscPushDelay"/>; the Ball: <see cref="BallLaunchSpeed"/> up on its mass now (StoneTable); every other kind
        /// none (all zero). Ruling 42: every push times StoneRules.PushScale (the delay is not).</summary>
        public static (V3 first, float delay, V3 second) Launch(StoneKind k) {
            float s = StoneRules.PushScale;
            if (k == StoneKind.Disc) return (new V3(0f, DiscLift * s, 0f), DiscPushDelay, new V3(0f, 0f, DiscPush * s));
            if (k == StoneKind.Ball) return (new V3(0f, BallLaunchSpeed * s * StoneTable.Of(k).Mass, 0f), 0f, V3.Zero);
            return (V3.Zero, 0f, V3.Zero);
        }

        /// <summary>The kind's distance ahead (metres) and rise time (seconds) from the settings.</summary>
        public static void Set(StoneKind k, float aheadMetres, float riseSeconds) {
            if (!StoneTable.Valid(k)) return;
            ahead[(int)k] = aheadMetres;
            rise[(int)k] = riseSeconds;
        }

        /// <summary>The stone's centre at the start and at the end, the rise time and the yaw it is turned to. Yaw is Unity's
        /// (0 faces +Z, 90 faces +X); <paramref name="groundY"/> is the ground's height under the end. The Disc
        /// (<see cref="Rises"/>) starts where it ends, <see cref="DiscHeight"/> above the ground, and takes no time.</summary>
        public static (V3 start, V3 end, float riseSeconds, float yawDeg) For(StoneKind k, V3 head, float yawDeg, float groundY) {
            int i = StoneTable.Valid(k) ? (int)k : (int)StoneKind.Pillar;
            float h = StoneTable.Of((StoneKind)i).Size.Y;
            V3 f = Forward(yawDeg);
            float x = head.X + f.X * ahead[i], z = head.Z + f.Z * ahead[i];
            if (!Rises((StoneKind)i)) {
                var at = new V3(x, groundY + DiscHeight, z);
                return (at, at, 0f, yawDeg);
            }
            var end = new V3(x, groundY + h * 0.5f, z);
            var start = new V3(end.X, end.Y - h, end.Z);
            return (start, end, rise[i], yawDeg);
        }

        /// <summary>The flat direction a yaw faces, length 1.</summary>
        public static V3 Forward(float yawDeg) {
            double r = yawDeg * Math.PI / 180.0;
            return new V3((float)Math.Sin(r), 0f, (float)Math.Cos(r));
        }
    }

    /// <summary>Why a stone breaks on its own: it lived <c>LifeSeconds</c>, it fell out of the world, or its owner left.</summary>
    internal enum LifeEnd { None, Old, Void, OwnerGone }

    internal static class StoneLife {
        /// <summary>Spec 3.4: a stone below this height has left the world.</summary>
        public const float VoidY = -100f;

        public static bool ShouldBreak(float age, float life, float y, bool ownerOnline) => Why(age, life, y, ownerOnline) != LifeEnd.None;

        /// <summary>The first reason that holds, in the order old, void, owner gone; an age or a height that is not a number
        /// breaks the stone.</summary>
        public static LifeEnd Why(float age, float life, float y, bool ownerOnline) {
            if (!(age <= life)) return LifeEnd.Old;
            if (!(y >= VoidY)) return LifeEnd.Void;
            if (!ownerOnline) return LifeEnd.OwnerGone;
            return LifeEnd.None;
        }
    }

    internal enum CapAction { Spawn, BreakOldest, Refuse }

    internal static class Caps {
        /// <summary>Spec 3.4: a player at (or over) <paramref name="maxPerPlayer"/> loses their oldest stone to the new one, even on
        /// a full server (it frees its own room); otherwise a server at <paramref name="maxTotal"/> refuses. Asked again after each
        /// break, so a player left over the max by a lower setting loses as many as it takes.</summary>
        public static CapAction Decide(int ownedByPlayer, int total, int maxPerPlayer, int maxTotal) {
            if (ownedByPlayer >= maxPerPlayer) return ownedByPlayer > 0 ? CapAction.BreakOldest : CapAction.Refuse;
            if (total >= maxTotal) return CapAction.Refuse;
            return CapAction.Spawn;
        }
    }

    internal static class StoneKinds {
        /// <summary><c>[EarthStones] Enabled</c>'s default: every stone.</summary>
        public const string DefaultEnabled = "Disc, Ball, Pillar, Cube, Wall";

        /// <summary>The kinds a list names (<c>[EarthStones] Enabled</c>: "Pillar", "pillar, Cube", "Disc;Ball"): split by commas,
        /// semicolons and spaces, any case. <paramref name="unknown"/> lists the words that name no kind ("" when none).</summary>
        public static bool[] Parse(string? text, out string unknown) {
            var on = new bool[StoneTable.Count];
            var bad = new List<string>();
            foreach (string word in (text ?? "").Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)) {
                bool found = false;
                for (int i = 0; i < StoneTable.Count; i++)
                    if (string.Equals(((StoneKind)i).ToString(), word, StringComparison.OrdinalIgnoreCase)) { on[i] = true; found = true; }
                if (!found) bad.Add(word);
            }
            unknown = string.Join(", ", bad.ToArray());
            return on;
        }
    }

    // ==================================================================== Task 7: the modifiers (Straight first)

    /// <summary>Which stone a modifier can reach (spec 2.4). A stone is in reach of a head looking along a yaw when its centre
    /// is in the half-space in front of the head (ahead of the plane through the head square to the flat yaw), the nearest
    /// point of its box is within <c>reach</c> of the head, and its centre is at most <c>maxHeight</c> above the feet. The
    /// server adds its slack (<c>[EarthModifiers] RangeSlackMetres</c>) to both distances, never to the half-space. The box is
    /// the stone's world AABB (its collider's bounds). Anything that is not a number is out of reach.</summary>
    internal static class Reach {
        /// <summary>Angles closer than this (degrees) are a tie, which the nearer stone wins.</summary>
        const float TieDegrees = 1e-3f;

        public static bool InReach(V3 head, float yawDeg, V3 boxMin, V3 boxMax, float feetY, float reach, float maxHeight, float slack) {
            V3 centre = Centre(boxMin, boxMax);
            if (!(V3.Dot(centre - head, SpawnPlan.Forward(yawDeg)) > 0f)) return false;
            if (!(Nearest(head, boxMin, boxMax) <= reach + slack)) return false;
            return centre.Y - feetY <= maxHeight + slack;
        }

        /// <summary>As the other, with the box as (min, max).</summary>
        public static bool InReach(V3 head, float yawDeg, (V3 min, V3 max) box, float feetY, float reach, float maxHeight, float slack) =>
            InReach(head, yawDeg, box.min, box.max, feetY, reach, maxHeight, slack);

        /// <summary>The game's pick (no slack): of the stones in reach, the one whose centre is the smallest angle from the flat
        /// forward, seen from above; on a tie the one whose box is nearer the head (then the first in the list). 0 when none is
        /// in reach (0 is never a stone's id).</summary>
        public static uint Pick(V3 head, float yawDeg, IList<(uint id, V3 min, V3 max)>? stones, float feetY, float reach, float maxHeight) {
            if (stones == null) return 0;
            V3 forward = SpawnPlan.Forward(yawDeg);
            uint best = 0;
            float bestAngle = float.PositiveInfinity, bestNear = float.PositiveInfinity;
            for (int i = 0; i < stones.Count; i++) {
                (uint id, V3 min, V3 max) s = stones[i];
                if (s.id == 0 || !InReach(head, yawDeg, s.min, s.max, feetY, reach, maxHeight, 0f)) continue;
                float angle = FlatAngle(Centre(s.min, s.max) - head, forward), near = Nearest(head, s.min, s.max);
                bool better = angle < bestAngle - TieDegrees || (angle <= bestAngle + TieDegrees && near < bestNear);
                if (!better) continue;
                best = s.id;
                bestAngle = angle;
                bestNear = near;
            }
            return best;
        }

        public static V3 Centre(V3 min, V3 max) => (min + max) * 0.5f;

        /// <summary>The distance from <paramref name="p"/> to the nearest point of the box (0 inside it).</summary>
        public static float Nearest(V3 p, V3 min, V3 max) =>
            (new V3(Clamp(p.X, min.X, max.X), Clamp(p.Y, min.Y, max.Y), Clamp(p.Z, min.Z, max.Z)) - p).Length;

        static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;

        /// <summary>The angle in degrees between <paramref name="d"/> and the flat <paramref name="forward"/>, seen from above.</summary>
        static float FlatAngle(V3 d, V3 forward) {
            double cross = d.Z * forward.X - d.X * forward.Z, dot = d.X * forward.X + d.Z * forward.Z;
            return (float)(Math.Atan2(Math.Abs(cross), dot) * 180.0 / Math.PI);
        }
    }

    /// <summary>Why the server refused a Modify claim. <see cref="Bad"/> (ruling 13): a modifier or hand byte out of range, or
    /// a head or yaw that is not a finite number (or a yaw past a full turn): a claim no game of this mod sends.</summary>
    internal enum ModifyRefusal { None, Cooldown, NoStone, OutOfReach, NotAllowed, Bad }

    internal static class ModifyCheck {
        public const int ModifierCount = (int)Modifier.Explode + 1;

        /// <summary>The claim could have come from a game of this mod: a modifier, a hand of 0 (right) or 1 (left), a finite
        /// head, a finite yaw within a turn (<see cref="SummonCheck.MaxYaw"/>).</summary>
        public static bool Sane(in ModifyClaim c) =>
            (int)c.Mod < ModifierCount && c.Hand <= 1 && Finite(c.Head.X) && Finite(c.Head.Y) && Finite(c.Head.Z) && Finite(c.Yaw) && Math.Abs(c.Yaw) <= SummonCheck.MaxYaw;

        /// <summary>The server's decision on a claim, in this order: <see cref="ModifyRefusal.Bad"/>, the player's cooldown for
        /// that modifier (<see cref="ModifyRefusal.Cooldown"/>), the target is no standing stone (<paramref name="state"/> null:
        /// <see cref="ModifyRefusal.NoStone"/>), the stone's box out of reach of the SERVER's copy of the head and feet looking
        /// along the claimed yaw, with <paramref name="slack"/> (<see cref="ModifyRefusal.OutOfReach"/>; a server head that is
        /// not a number is out of reach), the modifier cannot be applied in the stone's state
        /// (<see cref="ModifyRefusal.NotAllowed"/>). The claimed head is only checked for being a number.</summary>
        public static ModifyRefusal Decide(in ModifyClaim c, bool cooldownOk, StoneState? state, V3 boxMin, V3 boxMax, V3 serverHead, float serverFeetY,
            float reach, float maxHeight, float slack) {
            if (!Sane(in c)) return ModifyRefusal.Bad;
            if (!cooldownOk) return ModifyRefusal.Cooldown;
            if (!(state is StoneState s)) return ModifyRefusal.NoStone;
            if (!Reach.InReach(serverHead, c.Yaw, boxMin, boxMax, serverFeetY, reach, maxHeight, slack)) return ModifyRefusal.OutOfReach;
            if (!StoneRules.CanApply(s, c.Mod)) return ModifyRefusal.NotAllowed;
            return ModifyRefusal.None;
        }

        /// <summary>The modifier move of a modifier (Straight ... Explode in both enums' order): the ledger's key.</summary>
        public static Move MoveOf(Modifier m) => (Move)((int)Move.Straight + (int)m);

        /// <summary>The modifier a move is; false for every other move.</summary>
        public static bool ModifierOf(Move m, out Modifier mod) {
            mod = (Modifier)(m - Move.Straight);
            return m >= Move.Straight && m <= Move.Explode;
        }

        static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    }

    // ==================================================================== Task 8: hits, damage, knockback, clashes

    /// <summary>Who breaks when two stones meet (spec 2.2): equal power both, otherwise the weaker one.</summary>
    internal enum ClashResult : byte { BothBreak, ABreaks, BBreaks }

    /// <summary>Ruling 24: the distances behind a hit on a player, whose body is a capsule from the head down to the feet
    /// (<see cref="StoneRules.PlayerRadius"/>). Everything is in the stone's own frame: a box is centred at the origin with half
    /// sides <c>half</c> (its collider turned with the stone), a sphere (the Ball) is its centre. Anything that is not a number is
    /// infinitely far.</summary>
    internal static class Contact {
        const int Steps = 64;   // ternary search on the segment: (2/3)^64 of its length, far under a millimetre

        /// <summary>The shortest distance between the segment <paramref name="a"/>-<paramref name="b"/> and the box centred at
        /// the origin with half sides <paramref name="half"/> (0 where they meet), and the nearest pair of points: one on the
        /// segment, one on (or in) the box. The squared distance from a point moving along a segment to a box is convex, so a
        /// ternary search finds its least value.</summary>
        public static float SegmentBox(V3 a, V3 b, V3 half, out V3 onSegment, out V3 onBox) {
            onSegment = a;
            onBox = a;
            if (!Finite(a) || !Finite(b) || !Finite(half) || half.X < 0f || half.Y < 0f || half.Z < 0f) return float.PositiveInfinity;
            double lo = 0.0, hi = 1.0;
            for (int i = 0; i < Steps; i++) {
                double m1 = lo + (hi - lo) / 3.0, m2 = hi - (hi - lo) / 3.0;
                if (BoxSq(a, b, half, m1) < BoxSq(a, b, half, m2)) hi = m2;
                else lo = m1;
            }
            double t = (lo + hi) * 0.5;
            // the ends: a minimum at an end is reached only in the limit
            if (BoxSq(a, b, half, 0.0) <= BoxSq(a, b, half, t)) t = 0.0;
            if (BoxSq(a, b, half, 1.0) < BoxSq(a, b, half, t)) t = 1.0;
            onSegment = At(a, b, t);
            onBox = ClampToBox(onSegment, half);
            return (float)Math.Sqrt(BoxSq(a, b, half, t));
        }

        /// <summary>Whether a capsule (the segment <paramref name="a"/>-<paramref name="b"/>, <paramref name="radius"/> round it)
        /// touches the box centred at the origin with half sides <paramref name="half"/>.</summary>
        public static bool CapsuleTouchesBox(V3 a, V3 b, float radius, V3 half) =>
            radius >= 0f && SegmentBox(a, b, half, out _, out _) <= radius;

        /// <summary>The shortest distance between the segment <paramref name="a"/>-<paramref name="b"/> and the point
        /// <paramref name="p"/>, and the segment's nearest point.</summary>
        public static float SegmentPoint(V3 a, V3 b, V3 p, out V3 onSegment) {
            onSegment = a;
            if (!Finite(a) || !Finite(b) || !Finite(p)) return float.PositiveInfinity;
            V3 ab = b - a;
            float len2 = V3.Dot(ab, ab);
            float t = len2 > 1e-12f ? V3.Dot(p - a, ab) / len2 : 0f;
            t = t < 0f ? 0f : t > 1f ? 1f : t;
            onSegment = a + ab * t;
            return (p - onSegment).Length;
        }

        /// <summary>The point of the box centred at the origin with half sides <paramref name="half"/> nearest to <paramref name="p"/>.</summary>
        public static V3 ClampToBox(V3 p, V3 half) => new V3(Clamp(p.X, half.X), Clamp(p.Y, half.Y), Clamp(p.Z, half.Z));

        static float Clamp(float v, float h) => v < -h ? -h : v > h ? h : v;

        static V3 At(V3 a, V3 b, double t) =>
            new V3((float)(a.X + (b.X - a.X) * t), (float)(a.Y + (b.Y - a.Y) * t), (float)(a.Z + (b.Z - a.Z) * t));

        // the squared distance from the segment's point at t to the box, in doubles
        static double BoxSq(V3 a, V3 b, V3 h, double t) {
            double x = a.X + (b.X - a.X) * t, y = a.Y + (b.Y - a.Y) * t, z = a.Z + (b.Z - a.Z) * t;
            double dx = Math.Max(Math.Abs(x) - h.X, 0.0), dy = Math.Max(Math.Abs(y) - h.Y, 0.0), dz = Math.Max(Math.Abs(z) - h.Z, 0.0);
            return dx * dx + dy * dy + dz * dz;
        }

        static bool Finite(V3 v) => !float.IsNaN(v.X) && !float.IsInfinity(v.X) && !float.IsNaN(v.Y) && !float.IsInfinity(v.Y) && !float.IsNaN(v.Z) && !float.IsInfinity(v.Z);
    }

    /// <summary>The server's record of who was hit by what (spec 3.7): one stone hits one target at most once per
    /// <c>HitCooldownSeconds</c>. A target is the health object's instance id. A stone that breaks is forgotten; once the record
    /// holds <see cref="MaxEntries"/>, the entries past their cooldown go.</summary>
    internal sealed class HitLedger {
        public const int MaxEntries = 1024;
        const double Eps = 1e-6;   // a hit that lands on the cooldown's end counts as past it

        readonly Dictionary<ulong, double> last = new Dictionary<ulong, double>();
        readonly List<ulong> drop = new List<ulong>();

        public int Count => last.Count;

        /// <summary>True, and the pair's cooldown starts at <paramref name="now"/>, when this stone has not hit this target in the
        /// last <paramref name="cooldown"/> seconds; false, and nothing changes, when it has. A time that is not a number is refused.</summary>
        public bool TryHit(uint stone, int target, double now, float cooldown) {
            if (double.IsNaN(now) || double.IsInfinity(now)) return false;
            ulong key = Key(stone, target);
            if (last.TryGetValue(key, out double at) && now - at < cooldown - Eps) return false;
            if (last.Count >= MaxEntries && !last.ContainsKey(key)) Prune(now, cooldown);
            last[key] = now;
            return true;
        }

        /// <summary>Drops every record of a stone (it broke).</summary>
        public void Forget(uint stone) {
            drop.Clear();
            foreach (ulong k in last.Keys) if ((uint)(k >> 32) == stone) drop.Add(k);
            foreach (ulong k in drop) last.Remove(k);
            drop.Clear();
        }

        // the entries past their cooldown; when none is, the oldest half (a record never grows past MaxEntries)
        void Prune(double now, float cooldown) {
            drop.Clear();
            foreach (KeyValuePair<ulong, double> kv in last) if (!(now - kv.Value < cooldown - Eps)) drop.Add(kv.Key);
            if (drop.Count == 0) {
                var times = new List<double>(last.Values);
                times.Sort();
                double cut = times[times.Count / 2];
                foreach (KeyValuePair<ulong, double> kv in last) if (kv.Value <= cut) drop.Add(kv.Key);
            }
            foreach (ulong k in drop) last.Remove(k);
            drop.Clear();
        }

        static ulong Key(uint stone, int target) => ((ulong)stone << 32) | unchecked((uint)target);
    }

    internal static class MoveRules {
        /// <summary>[EarthMovement] DashSpeed's (m/s along the yaw) and DashLift's (m/s up) defaults (spec 3.6).</summary>
        public const float DefaultDashSpeed = 8f, DefaultDashLift = 0.5f;

        /// <summary>Ruling C20 (spec 2.5, 3.6): Dash's push on the player who struck it (on their own game): along
        /// <paramref name="yawDeg"/> (Unity's: 0 faces +Z, 90 faces +X) at <paramref name="speed"/> m/s, and
        /// <paramref name="lift"/> m/s up (it keeps the game from zeroing a push along the ground).</summary>
        public static V3 Dash(float yawDeg, float speed, float lift) {
            V3 f = SpawnPlan.Forward(yawDeg);
            return new V3(f.X * speed, lift, f.Z * speed);
        }

        /// <summary>Straight's push on the player who struck it (spec 3.5, on their own game): back along the yaw at
        /// <paramref name="grounded"/> m/s when the target stone is anchored (as that game sees it), <paramref name="free"/> when
        /// it is not, and <paramref name="lift"/> up; all of it halved while the player is in the air.</summary>
        public static V3 Pushback(float yawDeg, bool targetGrounded, bool airborne, float grounded, float free, float lift) {
            V3 back = -SpawnPlan.Forward(yawDeg) * (targetGrounded ? grounded : free);
            V3 push = new V3(back.X, lift, back.Z);
            return airborne ? push * 0.5f : push;
        }
    }
}
