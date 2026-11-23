using System;
using System.Collections.Generic;
using System.Globalization;

namespace Earthbending {
    // Unity-free (the tests compile it): fight health (docs/superpowers/specs/2026-11-23-earthbending-fight-health-design.md).
    // The ring's zones (FightRing), RUMBLE's damage share outside a ring and the finishing hit's fling (FightRules), and the
    // ledger of each player's fight health (FightLedger). The server (FightServer.cs) feeds the ledger every frame and on
    // every stone hit; nothing here is ever saved.

    /// <summary>Where a player's feet are, for one ring (spec 3.1).</summary>
    internal enum FightWhere : byte { Outside, Arena, Ring }

    /// <summary>One ring (spec 3.1): <see cref="Centre"/> (its Y is the floor), the ring's radius (the fighting floor), the
    /// arena's (the pit and the rim round it), and whether the games draw its edge.</summary>
    internal readonly struct FightRing {
        /// <summary>On the ring: the feet from <see cref="Under"/> m under the floor to <see cref="Over"/> m above it (a Jump
        /// is still on the ring). In the arena: from <see cref="ArenaUnder"/> under to <see cref="ArenaOver"/> above.</summary>
        public const float Under = 1f, Over = 8f, ArenaUnder = 6f, ArenaOver = 25f;

        public readonly V3 Centre;
        public readonly float Radius, ArenaRadius;
        public readonly bool Drawn;

        public FightRing(V3 centre, float radius, float arenaRadius, bool drawn) {
            Centre = centre;
            Radius = radius;
            ArenaRadius = arenaRadius;
            Drawn = drawn;
        }

        /// <summary>Every number finite, a ring radius above 0 and an arena radius at least the ring's.</summary>
        public bool Valid => Finite(Centre.X) && Finite(Centre.Y) && Finite(Centre.Z) && Finite(Radius) && Finite(ArenaRadius)
            && Radius > 0f && ArenaRadius >= Radius;

        /// <summary>Spec 3.1: Ring within <see cref="Radius"/> (flat) and from <see cref="Under"/> under the floor to
        /// <see cref="Over"/> above it; else Arena within <see cref="ArenaRadius"/> and from <see cref="ArenaUnder"/> under to
        /// <see cref="ArenaOver"/> above; else Outside. An invalid ring, or feet that are not a number, are Outside.</summary>
        public FightWhere Where(V3 feet) {
            if (!Valid || !Finite(feet.X) || !Finite(feet.Y) || !Finite(feet.Z)) return FightWhere.Outside;
            float dx = feet.X - Centre.X, dz = feet.Z - Centre.Z, flat2 = dx * dx + dz * dz, up = feet.Y - Centre.Y;
            if (flat2 <= Radius * Radius && up >= -Under && up <= Over) return FightWhere.Ring;
            if (flat2 <= ArenaRadius * ArenaRadius && up >= -ArenaUnder && up <= ArenaOver) return FightWhere.Arena;
            return FightWhere.Outside;
        }

        /// <summary>"x, y, z, ring radius, arena radius" ([EarthFight] Ring), numbers in the invariant culture; a valid ring,
        /// drawn. False for anything else (empty included).</summary>
        public static bool TryParse(string? text, out FightRing ring) {
            ring = default;
            if (text == null) return false;
            string[] parts = text.Split(',');
            if (parts.Length != 5) return false;
            var n = new float[5];
            for (int i = 0; i < 5; i++)
                if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out n[i]) || !Finite(n[i])) return false;
            var r = new FightRing(new V3(n[0], n[1], n[2]), n[3], n[4], true);
            if (!r.Valid) return false;
            ring = r;
            return true;
        }

        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "centre {0}, ring {1:0.##} m, arena {2:0.##} m{3}", Centre, Radius, ArenaRadius, Drawn ? "" : " (not drawn)");

        static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
    }

    /// <summary>What a stone's hit did to a player's fight health.</summary>
    internal enum FightHit : byte { NotFighting, Hurt, KnockedOut }

    internal static class FightRules {
        /// <summary>RUMBLE's 20 health ([EarthFight] Points).</summary>
        public const int DefaultPoints = 20;
        public const float DefaultFlingClear = 3f, DefaultFlingMin = 8f, DefaultFlingMax = 16f, DefaultFlingHitstun = 1.5f, LeftSeconds = 0.3f, DefaultHealthPoints = 20f;
        static readonly float Diagonal = (float)Math.Sqrt(0.5);

        /// <summary>Spec 3.6, outside every ring: a stone of <paramref name="power"/> takes power / <paramref name="healthPoints"/>
        /// of <paramref name="maxHealth"/>. 0 for power 0 or less, or any number that is not a positive finite one.</summary>
        public static float PlayerDamage(int power, float maxHealth, float healthPoints) {
            if (power <= 0 || !(maxHealth > 0f) || float.IsInfinity(maxHealth) || !(healthPoints > 0f) || float.IsInfinity(healthPoints)) return 0f;
            return power / healthPoints * maxHealth;
        }

        /// <summary>The flat distance from <paramref name="from"/> along <paramref name="flatDir"/> (its height is not counted)
        /// to the ring's edge. 0 from outside the ring. A zero direction is the way out from the centre through
        /// <paramref name="from"/> (+X from the exact centre).</summary>
        public static float ExitDistance(in FightRing ring, V3 from, V3 flatDir) {
            float fx = from.X - ring.Centre.X, fz = from.Z - ring.Centre.Z;
            if (!Finite(fx) || !Finite(fz)) return 0f;
            Way(fx, fz, flatDir, out float dx, out float dz);
            float r = ring.Radius;
            float b = fx * dx + fz * dz, c = fx * fx + fz * fz - r * r;
            if (c >= 0f) return 0f;   // on or past the edge
            float disc = b * b - c;
            float t = -b + (float)Math.Sqrt(disc);
            return t > 0f && Finite(t) ? t : 0f;
        }

        /// <summary>Spec 3.3: the finishing hit's launch speed, 45 degrees up along <paramref name="flatDir"/>, landing
        /// <paramref name="clear"/> m past the ring's edge at the floor's height: sqrt((ExitDistance + clear) x |gravity|),
        /// held to <paramref name="min"/>..<paramref name="max"/> (the maximum wins when they cross). No gravity (or one that is
        /// not a number) gives the maximum.</summary>
        public static float FlingSpeed(in FightRing ring, V3 feet, V3 flatDir, float gravity, float clear, float min, float max) {
            if (!(max > 0f) || float.IsInfinity(max)) max = DefaultFlingMax;
            if (!(min >= 0f) || float.IsInfinity(min)) min = 0f;
            if (min > max) min = max;
            float g = Math.Abs(gravity);
            if (!(g > 0f) || float.IsInfinity(g)) return max;
            if (!(clear >= 0f) || float.IsInfinity(clear)) clear = 0f;
            float range = ExitDistance(ring, feet, flatDir) + clear;
            float s = (float)Math.Sqrt(range * g);
            if (!Finite(s)) return max;
            return s < min ? min : s > max ? max : s;
        }

        /// <summary>The fling's velocity: <paramref name="speed"/> 45 degrees up along the flat way of
        /// <paramref name="flatDir"/> (the way out through the feet when it has none).</summary>
        public static V3 FlingVelocity(in FightRing ring, V3 feet, V3 flatDir, float speed) {
            float fx = feet.X - ring.Centre.X, fz = feet.Z - ring.Centre.Z;
            if (!Finite(fx) || !Finite(fz)) { fx = 0f; fz = 0f; }
            Way(fx, fz, flatDir, out float dx, out float dz);
            float s = speed * Diagonal;
            return new V3(dx * s, s, dz * s);
        }

        // The flat way: flatDir's own, else out from the centre through (fx, fz), else +X. Length 1.
        static void Way(float fx, float fz, V3 flatDir, out float dx, out float dz) {
            dx = Finite(flatDir.X) ? flatDir.X : 0f;
            dz = Finite(flatDir.Z) ? flatDir.Z : 0f;
            float l = (float)Math.Sqrt(dx * dx + dz * dz);
            if (l > 1e-4f) { dx /= l; dz /= l; return; }
            l = (float)Math.Sqrt(fx * fx + fz * fz);
            if (l > 1e-4f) { dx = fx / l; dz = fz / l; return; }
            dx = 1f;
            dz = 0f;
        }

        static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
    }

    /// <summary>Each player's fight health on the one ring (spec 3.2-3.4), keyed by account. A player seen on the ring with no
    /// entry (and not "out") gets full points; one seen off it for <see cref="FightRules.LeftSeconds"/> loses theirs and every
    /// other fighter refills; the hit that reaches 0 removes the entry, marks the player "out" (no points until they have been
    /// off the ring for LeftSeconds) and refills every other fighter. Every call adds each player whose points changed to the
    /// caller's list once, with their points now (-1 = none). Never saved.</summary>
    internal sealed class FightLedger {
        public readonly struct Change {
            public readonly ulong Player;
            /// <summary>-1 = none.</summary>
            public readonly int Points;

            public Change(ulong player, int points) { Player = player; Points = points; }
        }

        sealed class State {
            public int Points = -1;            // -1: no entry
            public bool Out;                   // knocked out: no entry until off the ring for LeftSeconds
            public double OffSince = double.NaN;   // when this stretch off the ring began (NaN: on it)
        }

        readonly Dictionary<ulong, State> players = new Dictionary<ulong, State>();
        readonly List<ulong> keys = new List<ulong>();

        public FightLedger(int points) => Points = points < 1 ? 1 : points > 250 ? 250 : points;

        /// <summary>Full fight health, 1..250.</summary>
        public int Points { get; }

        /// <summary>The player is on, in or out of the ring at <paramref name="now"/> seconds.</summary>
        public void See(ulong player, FightWhere where, double now, List<Change> changes) {
            players.TryGetValue(player, out State? s);
            if (where == FightWhere.Ring) {
                if (s == null) {
                    s = new State();
                    players[player] = s;
                }
                s.OffSince = double.NaN;
                if (s.Out || s.Points >= 0) return;
                s.Points = Points;
                changes.Add(new Change(player, Points));
                return;
            }
            if (s == null) return;   // off the ring and nothing known: nothing to do
            if (double.IsNaN(s.OffSince)) s.OffSince = now;
            if (!(now - s.OffSince >= FightRules.LeftSeconds - 1e-9)) return;
            // left the ring
            bool had = s.Points >= 0;
            players.Remove(player);   // no entry, and no longer out
            if (!had) return;
            changes.Add(new Change(player, -1));
            RefillOthers(player, changes);
        }

        /// <summary>A stone of <paramref name="power"/> hits the player: NotFighting with no entry (or power 0 or less),
        /// Hurt when points are left, KnockedOut when it reaches 0.</summary>
        public FightHit Hit(ulong player, int power, List<Change> changes) {
            if (power <= 0 || !players.TryGetValue(player, out State? s) || s.Points < 0) return FightHit.NotFighting;
            if (power < s.Points) {
                s.Points -= power;
                changes.Add(new Change(player, s.Points));
                return FightHit.Hurt;
            }
            s.Points = -1;
            s.Out = true;
            changes.Add(new Change(player, -1));
            RefillOthers(player, changes);
            return FightHit.KnockedOut;
        }

        /// <summary>The player's points, -1 for none.</summary>
        public int PointsOf(ulong player) => players.TryGetValue(player, out State? s) ? s.Points : -1;

        /// <summary>Whether the player has fight health now.</summary>
        public bool Fighting(ulong player) => PointsOf(player) >= 0;

        /// <summary>The player is gone (left the server, died, has no body): their entry goes and, if they had points, every
        /// other fighter refills.</summary>
        public void Forget(ulong player, List<Change> changes) {
            if (!players.TryGetValue(player, out State? s)) return;
            players.Remove(player);
            if (s.Points < 0) return;
            changes.Add(new Change(player, -1));
            RefillOthers(player, changes);
        }

        /// <summary>The ring went or moved: every entry -1, and nobody is out.</summary>
        public void Clear(List<Change> changes) {
            foreach (KeyValuePair<ulong, State> kv in players)
                if (kv.Value.Points >= 0) changes.Add(new Change(kv.Key, -1));
            players.Clear();
        }

        /// <summary>Every player with fight health now, with their points (for a resend).</summary>
        public void Fighters(List<Change> into) {
            foreach (KeyValuePair<ulong, State> kv in players)
                if (kv.Value.Points >= 0) into.Add(new Change(kv.Key, kv.Value.Points));
        }

        /// <summary>Every player the ledger knows (fighting, out, or stepping off), for the server's sweep of players gone.</summary>
        public void Known(List<ulong> into) {
            foreach (ulong k in players.Keys) into.Add(k);
        }

        void RefillOthers(ulong except, List<Change> changes) {
            keys.Clear();
            foreach (KeyValuePair<ulong, State> kv in players)
                if (kv.Key != except && kv.Value.Points >= 0 && kv.Value.Points < Points) keys.Add(kv.Key);
            foreach (ulong k in keys) {
                players[k].Points = Points;
                changes.Add(new Change(k, Points));
            }
        }
    }
}
