using System;
using System.Collections.Generic;
using System.Globalization;

// Unity-free, compiled by Earthbending.Tests, so only System.* in here: the stones' kinds and numbers (spec 3.4), V3, the
// vector every Unity-free file uses, and what message 19 carries (spec 3.8: the wire's version and kind bytes, the modifiers
// and the two claims a game sends). Prefabs.cs turns the table into the five network prefabs; Net.cs writes and reads the
// claims. Task 6 (summoning, at the end of this file): the stones' states (StoneState, StoneRules), the server's per-player
// cooldowns (Ledger), its check of a Summon claim (SummonCheck), where a stone rises (SpawnPlan), when it breaks (StoneLife),
// how many may stand (Caps) and which kinds are on (StoneKinds); Server.cs and StoneBody.cs run them.
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
        internal enum Kind : byte { Summon = 1, Modify = 2, Knockback = 10, Applied = 11 }

        /// <summary>An Applied event's byte when it is not a <see cref="Modifier"/>: the stone broke, froze for a hitstop, or
        /// exploded.</summary>
        public const byte Broke = 20, Hitstop = 21, Exploded = 22;
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

    /// <summary>What a stone is doing (spec 2.3). Rising, Still, Sliding and Stomped are grounded (+1 power, spec 3.4).</summary>
    internal enum StoneState : byte { Rising, Still, Sliding, Free, Stomped, Floating, Held, Flicked }

    internal static class StoneRules {
        /// <summary>Rising, Still, Sliding and Stomped: the states that count as on the ground.</summary>
        public static bool IsGrounded(StoneState s) => s == StoneState.Rising || s == StoneState.Still || s == StoneState.Sliding || s == StoneState.Stomped;

        /// <summary>Whether the stone's rigidbody is kinematic in this state (spec 3.4: Rising is moved by the server each tick,
        /// Still is anchored); every other state is moved by physics.</summary>
        public static bool Kinematic(StoneState s) => s == StoneState.Rising || s == StoneState.Still;

        // ---------------------------------------------------------------- Task 7: the modifiers' states

        /// <summary>The state a modifier leaves a stone in. Straight (spec 2.3, 3.5): an anchored stone (Still, or Rising once it
        /// has risen: StoneBody holds the push until then) slides;
        /// every other state stays (Free flies, Sliding slides on, Flicked stays in the hands). A modifier this build does not
        /// apply leaves the state as it is.</summary>
        public static StoneState After(StoneState s, Modifier m) {
            if (m == Modifier.Straight) return s == StoneState.Rising || s == StoneState.Still ? StoneState.Sliding : s;
            return s;
        }

        /// <summary>Whether a modifier may be applied to a stone in this state. This build applies Straight only, to a stone in
        /// any state; every other modifier is refused (later tasks add theirs).</summary>
        public static bool CanApply(StoneState s, Modifier m) => m == Modifier.Straight;

        /// <summary>Spec 2.3: a sliding stone is anchored again (Still) once it is slower than this, in m/s.</summary>
        public const float SlideStopSpeed = 0.1f;

        /// <summary>A sliding stone's speed after <paramref name="dt"/> seconds of slowing by <paramref name="deceleration"/> m/s every second
        /// (never below 0; a speed that is not a number is 0). Its height is frozen, so the ground has no weight on it to grip:
        /// this is what slows it.</summary>
        public static float SlideSpeed(float speed, float deceleration, float dt) {
            if (!(speed > 0f) || float.IsInfinity(speed)) return 0f;
            float slow = deceleration > 0f && dt > 0f ? deceleration * dt : 0f;
            return speed > slow ? speed - slow : 0f;
        }

        /// <summary>Whether a sliding stone at this speed stands still again (under <see cref="SlideStopSpeed"/>, or not a number).</summary>
        public static bool SlideStops(float speed) => !(speed >= SlideStopSpeed);
    }

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
    /// through <see cref="Set"/>.</summary>
    internal static class SpawnPlan {
        static readonly float[] Aheads = { 1.2f, 1.2f, 1.3f, 1.5f, 1.8f };     // Disc, Ball, Pillar, Cube, Wall
        static readonly float[] Rises = { 0f, 0f, 0.35f, 0.35f, 0.45f };       // the Disc and Ball start free (Task 9)
        static readonly float[] ahead = (float[])Aheads.Clone(), rise = (float[])Rises.Clone();

        public static float DefaultAhead(StoneKind k) => StoneTable.Valid(k) ? Aheads[(int)k] : 0f;
        public static float DefaultRise(StoneKind k) => StoneTable.Valid(k) ? Rises[(int)k] : 0f;

        /// <summary>The kind's distance ahead (metres) and rise time (seconds) from the settings.</summary>
        public static void Set(StoneKind k, float aheadMetres, float riseSeconds) {
            if (!StoneTable.Valid(k)) return;
            ahead[(int)k] = aheadMetres;
            rise[(int)k] = riseSeconds;
        }

        /// <summary>The stone's centre at the start and at the end, the rise time and the yaw it is turned to. Yaw is Unity's
        /// (0 faces +Z, 90 faces +X); <paramref name="groundY"/> is the ground's height under the end.</summary>
        public static (V3 start, V3 end, float riseSeconds, float yawDeg) For(StoneKind k, V3 head, float yawDeg, float groundY) {
            int i = StoneTable.Valid(k) ? (int)k : (int)StoneKind.Pillar;
            float h = StoneTable.Of((StoneKind)i).Size.Y;
            V3 f = Forward(yawDeg);
            var end = new V3(head.X + f.X * ahead[i], groundY + h * 0.5f, head.Z + f.Z * ahead[i]);
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

    internal static class MoveRules {
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
