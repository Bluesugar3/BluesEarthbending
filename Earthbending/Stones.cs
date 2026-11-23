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
}
