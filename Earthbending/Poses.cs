using System;
using System.Collections.Generic;
using System.Globalization;

// Unity-free, compiled by Earthbending.Tests, so only System.* in here: the pose engine (spec 3.3). A hand is a place, a palm
// direction and a finger direction in the body frame (BodyFrame: origin the head, turned with the head's yaw only); a pose is
// two hands (PoseSpec: A is the right hand as written, the mirror image counts with A on the left); PoseMatch judges one pose;
// PoseLibrary holds the sixteen poses and reads and writes poses.txt; PoseMachine turns every frame's matches into moves (the
// hold, the entry, the rearm, the cooldowns, the base pose arming a structure, the ground rules and the priority). Bend.cs
// feeds it the hands every frame, through the rules at the end of this file (Task 5): HandAxis and HandAxes (the axis
// settings: a signed axis or any direction in the controller's own space, Ruling 36), EmptyGate (the two-frame empty
// test), GroundGrace (ruling 16), PoseLibrary.Merge (poses.txt over the defaults) and PushRule (a push's velocity,
// ruling 14). The pose recorder's rules (Task 3) are here too: CaptureGate (when a snapshot
// is taken) and PoseCapture (what its CSV row holds); Hands.cs runs them, Earthbending/tools/fit_poses.py reads the file.
// The capture guide's words and rules (Task 3b) as well: PoseGuide (each pose's hand lines, the next pose for a count, the
// card's text, the easing) and BodyFrame.ToWorld (the body frame run backwards); CaptureGuide.cs draws them. The practice
// guide's step machine and card (Task 3c): PracticeRun; PracticeGuide.cs draws it.
namespace Earthbending {
    /// <summary>What a pose does. <see cref="Base"/> is never fired: it arms the structures. The order is the spec 3.3 table's.</summary>
    internal enum Move : byte { None, Base, Disc, Ball, Pillar, Cube, Wall, Straight, Kick, Stomp, Uppercut, Parry, Hold, Flick, Explode, Jump, Dash }

    /// <summary>One hand as the game reads it, in the body frame: <see cref="Pos"/> in metres from the head (already divided by
    /// <c>[EarthPose] BodyScale</c>, a fixed number: Ruling 35), <see cref="Palm"/> out of the palm and <see cref="Fingers"/>
    /// along the fingers, both length 1.</summary>
    internal struct HandSample {
        public V3 Pos, Palm, Fingers;

        public HandSample(V3 pos, V3 palm, V3 fingers) { Pos = pos; Palm = palm; Fingers = fingers; }
    }

    /// <summary>One hand of a pose: where it is and how it is turned, and how far off each may be (spec 3.3, "flow, not
    /// hold"). The place is a ZONE: an ellipsoid round <see cref="Pos"/> with the half-sizes <see cref="TolX"/> sideways,
    /// <see cref="TolY"/> up/down and <see cref="TolZ"/> in reach (forward/back), in body-frame metres. The directions are
    /// cones of <see cref="PalmDeg"/> and <see cref="FingerDeg"/>. A zero <see cref="Palm"/> or <see cref="Fingers"/> is not
    /// checked. The directions need not be length 1.</summary>
    internal struct HandSpec {
        public V3 Pos, Palm, Fingers;
        public float TolX, TolY, TolZ, PalmDeg, FingerDeg;

        public HandSpec(V3 pos, V3 palm, V3 fingers, float tolX, float tolY, float tolZ, float palmDeg, float fingerDeg) {
            Pos = pos; Palm = palm; Fingers = fingers; TolX = tolX; TolY = tolY; TolZ = tolZ; PalmDeg = palmDeg; FingerDeg = fingerDeg;
        }

        /// <summary>The same hand on the other side (x flipped in the place and both directions); the zone, square to the
        /// body frame's axes, is the same.</summary>
        public readonly HandSpec Mirrored() => new HandSpec(Pos.MirrorX(), Palm.MirrorX(), Fingers.MirrorX(), TolX, TolY, TolZ, PalmDeg, FingerDeg);
    }

    /// <summary>A two-hand pose: <see cref="A"/> on the right hand and <see cref="B"/> on the left as written.</summary>
    internal struct PoseSpec {
        public Move Move;
        public HandSpec A, B;

        public PoseSpec(Move move, HandSpec a, HandSpec b) { Move = move; A = a; B = b; }

        /// <summary>The mirror image: <see cref="A"/> is then the left hand's and <see cref="B"/> the right hand's.</summary>
        public readonly PoseSpec Mirrored() => new PoseSpec(Move, A.Mirrored(), B.Mirrored());
    }

    /// <summary>The frame poses are judged in (spec 3.3): the origin is the head, +Z the head's forward flattened to the
    /// ground, +Y world up, +X right. It turns with the head's yaw and never its pitch or roll. Yaw is Unity's: 0 faces +Z,
    /// 90 faces +X (clockwise seen from above).</summary>
    internal static class BodyFrame {
        /// <summary>A hand in the world as the body frame sees it. The position is relative to the head and divided by
        /// <paramref name="bodyScale"/> (a 1.75 m player is 1; zero, negative or not a number reads as 1); the directions are
        /// turned and made length 1, never scaled.</summary>
        public static HandSample ToBody(V3 head, float yawDeg, V3 worldPos, V3 worldPalm, V3 worldFingers, float bodyScale) {
            double r = yawDeg * Math.PI / 180.0;
            float c = (float)Math.Cos(r), s = (float)Math.Sin(r);
            float scale = bodyScale > 0f && !float.IsInfinity(bodyScale) ? bodyScale : 1f;
            return new HandSample(Unturn(worldPos - head, c, s) * (1f / scale), Unturn(worldPalm, c, s).Normalized, Unturn(worldFingers, c, s).Normalized);
        }

        /// <summary>Turned by -yaw about +Y (c, s the yaw's cosine and sine): what was ahead of the head is +Z.</summary>
        static V3 Unturn(V3 v, float c, float s) => new V3(v.X * c - v.Z * s, v.Y, v.X * s + v.Z * c);

        /// <summary><see cref="ToBody"/> run backwards at body scale 1: where a place of the body frame is in the world, for
        /// a head at <paramref name="head"/> turned <paramref name="yawDeg"/> (the capture guide's markers, Task 3b).</summary>
        public static V3 ToWorld(V3 head, float yawDeg, V3 bodyPos) => head + DirToWorld(yawDeg, bodyPos);

        /// <summary>A direction of the body frame in the world: turned by +yaw about +Y, its length kept.</summary>
        public static V3 DirToWorld(float yawDeg, V3 bodyDir) {
            double r = yawDeg * Math.PI / 180.0;
            float c = (float)Math.Cos(r), s = (float)Math.Sin(r);
            return new V3(bodyDir.X * c + bodyDir.Z * s, bodyDir.Y, -bodyDir.X * s + bodyDir.Z * c);
        }
    }

    /// <summary>What limits one hand in a pose (<see cref="PoseMatch.Parts"/>): the place's zone distance (0 the centre, 1 the
    /// edge), and the palm's and the fingers' angles to the pose's in degrees (NaN: not checked).</summary>
    internal struct HandParts {
        public float Place, PalmDeg, FingerDeg;

        /// <summary><c>place 0.31 palm 12 fingers 35 deg</c> (a part not checked is <c>-</c>).</summary>
        public readonly override string ToString() => "place " + Place.ToString("0.00", CultureInfo.InvariantCulture) + " palm " + Deg(PalmDeg) + " fingers " + Deg(FingerDeg) + " deg";

        static string Deg(float d) => float.IsNaN(d) ? "-" : d.ToString("0", CultureInfo.InvariantCulture);
    }

    internal static class PoseMatch {
        /// <summary>Whether the hands strike the pose, as written (A = right) or mirrored (A = left), whichever is closer.
        /// <paramref name="score"/> is the largest of (the place's <see cref="Zone"/> distance, angle / cone) over every checked
        /// part of both hands: 0 is exact, 1 is the edge, lower is better. A tie counts as written.</summary>
        public static bool Match(in PoseSpec s, in HandSample right, in HandSample left, out bool rightIsA, out float score) {
            float asWritten = Math.Max(Score(in s.A, in right), Score(in s.B, in left));
            PoseSpec m = s.Mirrored();
            float mirrored = Math.Max(Score(in m.A, in left), Score(in m.B, in right));
            rightIsA = !(mirrored < asWritten);
            score = rightIsA ? asWritten : mirrored;
            return score <= 1f;
        }

        /// <summary>One hand's score against one hand of a pose (see <see cref="Match"/>); never NaN: a hand that cannot be
        /// judged (NaN, no direction where one is checked, a tolerance or half-size of 0) scores infinity.</summary>
        public static float Score(in HandSpec h, in HandSample x) {
            float r = Zone(in h, x.Pos - h.Pos);
            if (Checked(h.Palm)) r = Math.Max(r, Ratio(Angle(x.Palm, h.Palm), h.PalmDeg));
            if (Checked(h.Fingers)) r = Math.Max(r, Ratio(Angle(x.Fingers, h.Fingers), h.FingerDeg));
            return r;
        }

        /// <summary>How deep <paramref name="offset"/> (a hand's place less the pose's, body frame) is in the hand's zone: the
        /// ellipsoid distance sqrt((dx/tx)^2 + (dy/ty)^2 + (dz/tz)^2), 0 at the centre and 1 on the edge. Infinity, never NaN,
        /// when it cannot be judged (a half-size that is 0, negative or not a number, or an offset that is not a number).</summary>
        public static float Zone(in HandSpec h, V3 offset) {
            if (!(h.TolX > 0f && h.TolY > 0f && h.TolZ > 0f)) return float.PositiveInfinity;
            double x = offset.X / (double)h.TolX, y = offset.Y / (double)h.TolY, z = offset.Z / (double)h.TolZ;
            double r = Math.Sqrt(x * x + y * y + z * z);
            return double.IsNaN(r) ? float.PositiveInfinity : (float)r;
        }

        /// <summary>A direction of a pose is checked unless it is zero (a NaN one is checked, and matches nothing).</summary>
        public static bool Checked(V3 v) => !(v.Length <= 1e-6f);

        /// <summary>The angle between two directions in degrees; 180 when either has no direction.</summary>
        public static float Angle(V3 a, V3 b) {
            V3 na = a.Normalized, nb = b.Normalized;
            if (V3.Dot(na, na) == 0f || V3.Dot(nb, nb) == 0f) return 180f;
            double d = Math.Max(-1.0, Math.Min(1.0, V3.Dot(na, nb)));
            return (float)(Math.Acos(d) * 180.0 / Math.PI);
        }

        /// <summary>One hand's parts against one hand of a pose (the fire line and the trace's replay): the place's
        /// <see cref="Zone"/> distance and the palm's and the fingers' angles in degrees; an angle the pose does not check is NaN.</summary>
        public static HandParts Parts(in HandSpec h, in HandSample x) => new HandParts {
            Place = Zone(in h, x.Pos - h.Pos),
            PalmDeg = Checked(h.Palm) ? Angle(x.Palm, h.Palm) : float.NaN,
            FingerDeg = Checked(h.Fingers) ? Angle(x.Fingers, h.Fingers) : float.NaN,
        };

        /// <summary>Each hand's <see cref="Parts"/> against the pose, with A on the right hand (<paramref name="rightIsA"/>) or
        /// on the left (the mirror), as <see cref="Match"/> assigned it.</summary>
        public static void Explain(in PoseSpec s, bool rightIsA, in HandSample right, in HandSample left, out HandParts r, out HandParts l) {
            PoseSpec p = rightIsA ? s : s.Mirrored();
            r = Parts(rightIsA ? p.A : p.B, in right);
            l = Parts(rightIsA ? p.B : p.A, in left);
        }

        static float Ratio(float error, float tolerance) {
            if (!(tolerance > 0f)) return float.PositiveInfinity;
            float r = error / tolerance;
            return float.IsNaN(r) ? float.PositiveInfinity : r;
        }
    }

    /// <summary>The sixteen poses, and the text of <c>UserData\Earthbending\poses.txt</c>: one line per pose,
    /// <c>&lt;Name&gt; | ax ay az  anx any anz  afx afy afz | bx by bz  bnx bny bnz  bfx bfy bfz | tx ty tz palmDeg fingerDeg</c>
    /// (A the right hand, <c>0 0 0</c> a direction not checked, one set of tolerances for both hands: the zone's half-sizes
    /// sideways, up/down and in reach, then the two cones; <c>#</c> starts a comment, numbers in the invariant culture). The
    /// tail of a file from before 0.4.2, <c>posTol palmDeg fingerDeg</c>, still reads: <c>posTol</c> is all three half-sizes.</summary>
    internal static class PoseLibrary {
        // spec 3.3, "flow, not hold": roomy zones and wide cones, so a pose fires as the hands pass through it
        public const float DefaultTolX = 0.25f, DefaultTolY = 0.20f, DefaultTolZ = 0.30f, DefaultPalmDeg = 60f, DefaultFingerDeg = 60f;

        static readonly V3 PX = new V3(1f, 0f, 0f), NX = new V3(-1f, 0f, 0f), PY = new V3(0f, 1f, 0f), NY = new V3(0f, -1f, 0f),
            PZ = new V3(0f, 0f, 1f), NZ = new V3(0f, 0f, -1f);

        // Stomp pushes down: palms down and back, square to its fingers. Unchecked (as the table had it first), its fingers 45
        // degrees below forward overlapped Base, and Base's own hands struck Stomp (Task 4's ruling, option A).
        static readonly V3 StompPalm = new V3(0f, -0.71f, -0.71f);

        // the spec 3.3 table, second design (2026-11-22, after the first capture): near the body, no straight arm ("arm out"
        // is about half reach, 0.42-0.45 m ahead of the head); a natural capture's poses.txt re-centres each zone
        static readonly PoseSpec[] Table = {
            P(Move.Base,     new V3(0.20f, -0.55f, 0.30f), PY, PZ, new V3(-0.20f, -0.55f, 0.30f), PY, PZ),
            P(Move.Disc,     new V3(0.05f, -0.30f, 0.45f), NX, PZ, new V3(-0.05f, -0.30f, 0.28f), NZ, PX),
            P(Move.Ball,     new V3(0.15f, -0.30f, 0.22f), NX, PZ, new V3(-0.10f, -0.30f, 0.45f), PX, PZ),
            P(Move.Pillar,   new V3(0.20f, -0.55f, 0.30f), NX, PZ, new V3(-0.20f, -0.05f, 0.25f), PX, PY),
            P(Move.Cube,     new V3(0.20f, -0.05f, 0.25f), NX, PY, new V3(-0.20f, -0.05f, 0.25f), PX, PY),
            P(Move.Wall,     new V3(0.20f, 0.00f, 0.30f), PY, PZ, new V3(-0.20f, 0.00f, 0.30f), PY, PZ),
            P(Move.Straight, new V3(0.20f, -0.25f, 0.25f), NX, PY, new V3(-0.15f, -0.25f, 0.45f), NY, PZ),
            P(Move.Kick,     new V3(0.20f, -0.25f, 0.25f), NX, PY, new V3(-0.05f, -0.65f, 0.22f), NZ, NY),
            P(Move.Stomp,    new V3(0.20f, -0.55f, 0.35f), StompPalm, new V3(0f, -0.71f, 0.71f), new V3(-0.20f, -0.55f, 0.35f), StompPalm, new V3(0f, -0.71f, 0.71f)),
            P(Move.Uppercut, new V3(0.20f, -0.30f, 0.25f), NX, new V3(0f, 0.97f, 0.26f), new V3(-0.05f, -0.10f, 0.32f), NZ, PY),
            P(Move.Parry,    new V3(0.20f, -0.25f, 0.42f), PZ, PY, new V3(-0.20f, -0.25f, 0.42f), PZ, PY),
            P(Move.Hold,     new V3(0.05f, -0.25f, 0.42f), PZ, PY, new V3(0.05f, -0.40f, 0.38f), PY, PZ),
            P(Move.Flick,    new V3(0.10f, -0.25f, 0.42f), PX, PZ, new V3(0.10f, -0.48f, 0.38f), PX, PZ),
            P(Move.Explode,  new V3(0.06f, -0.50f, 0.28f), NZ, NX, new V3(-0.06f, -0.50f, 0.28f), NZ, PX),
            P(Move.Jump,     new V3(0.20f, -0.72f, 0.20f), PY, PZ, new V3(-0.20f, 0.05f, 0.10f), PX, PY),
            P(Move.Dash,     new V3(0.20f, -0.72f, 0.20f), PY, PZ, new V3(-0.10f, -0.30f, 0.45f), PX, PZ),
        };

        static PoseSpec P(Move m, V3 ap, V3 an, V3 af, V3 bp, V3 bn, V3 bf) => new PoseSpec(m,
            new HandSpec(ap, an, af, DefaultTolX, DefaultTolY, DefaultTolZ, DefaultPalmDeg, DefaultFingerDeg),
            new HandSpec(bp, bn, bf, DefaultTolX, DefaultTolY, DefaultTolZ, DefaultPalmDeg, DefaultFingerDeg));

        /// <summary>The built-in poses (a fresh copy each time), in the table's order.</summary>
        public static PoseSpec[] Defaults => (PoseSpec[])Table.Clone();

        /// <summary>The poses the game runs (Task 5): <paramref name="defaults"/> in their order, each replaced by
        /// <paramref name="fromFile"/>'s pose of the same move when it has one (a pose of a move the defaults lack goes at the
        /// end). A pose the file leaves out, or whose line <see cref="Parse"/> skipped, stays the default. A new array; neither
        /// argument is changed. <paramref name="replaced"/> counts the file's poses used.</summary>
        public static PoseSpec[] Merge(PoseSpec[] defaults, PoseSpec[]? fromFile, out int replaced) {
            var all = new List<PoseSpec>(defaults ?? new PoseSpec[0]);
            replaced = 0;
            foreach (PoseSpec s in fromFile ?? new PoseSpec[0]) {
                int at = all.FindIndex(p => p.Move == s.Move);
                if (at >= 0) all[at] = s;
                else all.Add(s);
                replaced++;
            }
            return all.ToArray();
        }

        /// <summary>Reads poses.txt's lines. Blank lines and comments are skipped; a line that does not parse, or names a pose
        /// already read above it, is skipped and named in <paramref name="errors"/> (its line number, why, and the line).</summary>
        public static PoseSpec[] Parse(string[] lines, out List<string> errors) {
            errors = new List<string>();
            var poses = new List<PoseSpec>();
            if (lines == null) return poses.ToArray();
            for (int i = 0; i < lines.Length; i++) {
                string raw = lines[i] ?? "";
                int hash = raw.IndexOf('#');
                string text = (hash >= 0 ? raw.Substring(0, hash) : raw).Trim();
                if (text.Length == 0) continue;
                if (!TryLine(text, out PoseSpec spec, out string why)) {
                    errors.Add("line " + (i + 1) + ": " + why + ": " + raw.Trim());
                    continue;
                }
                if (poses.Exists(p => p.Move == spec.Move)) {
                    errors.Add("line " + (i + 1) + ": " + spec.Move + " is already on an earlier line, this one is skipped: " + raw.Trim());
                    continue;
                }
                poses.Add(spec);
            }
            return poses.ToArray();
        }

        /// <summary>The poses as poses.txt's lines (with two comment lines on top), which <see cref="Parse"/> reads back
        /// exactly. Each line carries A's tolerances for both hands, always in the 5-number form.</summary>
        public static string[] Format(PoseSpec[] poses) {
            var lines = new List<string> {
                "# <Name> | A: x y z  palm x y z  fingers x y z | B: the same | tx ty tz palmDeg fingerDeg",
                "# Body frame: metres from the head for a 1.75 m player, +Z ahead (head yaw only), +Y up, +X right; A is the right hand; 0 0 0 = not checked; tx ty tz = the zone's half-sizes sideways, up/down, reach.",
            };
            foreach (PoseSpec s in poses ?? new PoseSpec[0])
                lines.Add(s.Move + " | " + Hand(s.A) + " | " + Hand(s.B) + " | " + N(s.A.TolX) + " " + N(s.A.TolY) + " " + N(s.A.TolZ) + " " + N(s.A.PalmDeg) + " " + N(s.A.FingerDeg));
            return lines.ToArray();
        }

        static string Hand(in HandSpec h) => V(h.Pos) + "  " + V(h.Palm) + "  " + V(h.Fingers);
        static string V(V3 v) => N(v.X) + " " + N(v.Y) + " " + N(v.Z);
        static string N(float f) => f.ToString("R", CultureInfo.InvariantCulture);

        static bool TryLine(string text, out PoseSpec spec, out string why) {
            spec = default;
            string[] parts = text.Split('|');
            if (parts.Length != 4) { why = parts.Length + " parts split by |, want 4 (name | A | B | tolerances)"; return false; }
            string name = parts[0].Trim();
            if (!TryMove(name, out Move move)) { why = "no pose named '" + name + "'"; return false; }
            if (!TryNumbers(parts[1], 9, out float[] a, out why)) { why = "hand A: " + why; return false; }
            if (!TryNumbers(parts[2], 9, out float[] b, out why)) { why = "hand B: " + why; return false; }
            if (!TryTolerances(parts[3], out float[] t, out why)) { why = "tolerances: " + why; return false; }
            spec = new PoseSpec(move, Hand(a, t), Hand(b, t));
            return true;
        }

        /// <summary>The tail as <c>tx ty tz palmDeg fingerDeg</c>; the old <c>posTol palmDeg fingerDeg</c> gives posTol to all
        /// three half-sizes. Every number above 0.</summary>
        static bool TryTolerances(string text, out float[] t, out string why) {
            int count = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            if (count != 5 && count != 3) {
                t = new float[5];
                why = count + " numbers, want 5 (tx ty tz palmDeg fingerDeg) or the old 3 (posTol palmDeg fingerDeg)";
                return false;
            }
            if (!TryNumbers(text, count, out float[] n, out why)) { t = new float[5]; return false; }
            t = count == 5 ? n : new[] { n[0], n[0], n[0], n[1], n[2] };
            foreach (float f in t)
                if (!(f > 0f)) { why = "tolerances must be above 0"; return false; }
            return true;
        }

        static HandSpec Hand(float[] v, float[] t) =>
            new HandSpec(new V3(v[0], v[1], v[2]), new V3(v[3], v[4], v[5]), new V3(v[6], v[7], v[8]), t[0], t[1], t[2], t[3], t[4]);

        /// <summary>A move by its name, any case; never <see cref="Move.None"/>.</summary>
        public static bool TryMove(string name, out Move move) {
            foreach (Move m in (Move[])Enum.GetValues(typeof(Move)))
                if (m != Move.None && string.Equals(m.ToString(), name, StringComparison.OrdinalIgnoreCase)) { move = m; return true; }
            move = Move.None;
            return false;
        }

        static bool TryNumbers(string text, int count, out float[] numbers, out string why) {
            numbers = new float[count];
            string[] words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length != count) { why = words.Length + " numbers, want " + count; return false; }
            for (int i = 0; i < count; i++) {
                if (!float.TryParse(words[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float f) || float.IsNaN(f) || float.IsInfinity(f)) {
                    why = "'" + words[i] + "' is not a number";
                    return false;
                }
                numbers[i] = f;
            }
            why = "";
            return true;
        }
    }

    /// <summary>The pose machine's numbers (spec 3.3, <c>[EarthPose]</c>). <see cref="Cooldown"/> is by <see cref="Move"/>
    /// (spec 2.2 and 2.4); Jump and Dash use <see cref="MoveCooldownSeconds"/> instead of their entries.</summary>
    internal struct PoseValues {
        public const int MoveCount = (int)Move.Dash + 1;

        public float MinHoldSeconds, RearmSeconds, StructureWindowSeconds, MoveCooldownSeconds;
        public float StructureDeepScore, StructureStillMetres, StructureStillSeconds;
        /// <summary>A modifier or a movement fires once its score is at most this (or the hands rest in it), as a structure
        /// does at <see cref="StructureDeepScore"/> (Ruling 37).</summary>
        public float MoveDeepScore;
        public float[]? Cooldown;

        public static PoseValues Default {
            get {
                var c = new float[MoveCount];
                foreach (Move m in new[] { Move.Disc, Move.Ball, Move.Pillar, Move.Cube, Move.Wall }) c[(int)m] = 0.3f;
                c[(int)Move.Straight] = 0.5f;
                c[(int)Move.Kick] = 0.3f;
                c[(int)Move.Stomp] = 0.3f;
                c[(int)Move.Uppercut] = 0.5f;
                c[(int)Move.Parry] = 0.3f;
                c[(int)Move.Hold] = 0.8f;
                c[(int)Move.Flick] = 0.8f;
                c[(int)Move.Explode] = 0.3f;
                // MinHoldSeconds 0: a pose counts the first frame the hands are inside it; it fires once deep or at rest
                // there (Ruling 37, MoveDeepScore 0.6: the path tests pass from 0.4 to 0.7)
                return new PoseValues { MinHoldSeconds = 0f, RearmSeconds = 0.1f, StructureWindowSeconds = 1.2f, MoveCooldownSeconds = 0.6f,
                    StructureDeepScore = 0.5f, StructureStillMetres = 0.01f, StructureStillSeconds = 0.06f, MoveDeepScore = 0.6f, Cooldown = c };
            }
        }

        /// <summary>Seconds after a move fires before it can fire again; 0 for <see cref="Move.None"/> and <see cref="Move.Base"/>.</summary>
        public readonly float CooldownOf(Move m) {
            if (m == Move.Jump || m == Move.Dash) return MoveCooldownSeconds;
            return Cooldown != null && (int)m < Cooldown.Length ? Cooldown[(int)m] : 0f;
        }

        /// <summary>A copy that is safe to run on: every number that is NaN, infinite or negative, and every cooldown missing
        /// from a short or absent array, is the default's.</summary>
        public readonly PoseValues Clamped() {
            PoseValues d = Default;
            var c = new float[MoveCount];
            for (int i = 0; i < MoveCount; i++) c[i] = Ok(Cooldown != null && i < Cooldown.Length ? Cooldown[i] : float.NaN, d.Cooldown![i]);
            return new PoseValues {
                MinHoldSeconds = Ok(MinHoldSeconds, d.MinHoldSeconds),
                RearmSeconds = Ok(RearmSeconds, d.RearmSeconds),
                StructureWindowSeconds = Ok(StructureWindowSeconds, d.StructureWindowSeconds),
                MoveCooldownSeconds = Ok(MoveCooldownSeconds, d.MoveCooldownSeconds),
                StructureDeepScore = Ok(StructureDeepScore, d.StructureDeepScore),
                StructureStillMetres = Ok(StructureStillMetres, d.StructureStillMetres),
                StructureStillSeconds = Ok(StructureStillSeconds, d.StructureStillSeconds),
                MoveDeepScore = Ok(MoveDeepScore, d.MoveDeepScore),
                Cooldown = c,
            };
        }

        static float Ok(float v, float fallback) => v >= 0f && !float.IsInfinity(v) ? v : fallback;
    }

    /// <summary>Turns the hands of every frame into moves (spec 3.3). Each pose keeps its own state:
    /// <list type="bullet">
    /// <item>it counts once it has matched for <see cref="PoseValues.MinHoldSeconds"/> without a break (the default 0: the
    /// first frame the hands are inside it); once it fires, or is used up, it fires again only after it has stopped matching
    /// for <see cref="PoseValues.RearmSeconds"/>;</item>
    /// <item>every move is followed while the hands are in it, never fired on a brush (Rulings 33 and 37): it fires once its
    /// score is at most its deep score (<see cref="PoseValues.StructureDeepScore"/> for a structure,
    /// <see cref="PoseValues.MoveDeepScore"/> for a modifier or a movement), or once the hands have stopped going deeper into
    /// it (both hands within <see cref="PoseValues.StructureStillMetres"/> of where they were, and its score no better, for
    /// <see cref="PoseValues.StructureStillSeconds"/>). So the edge of one zone crossed on the way to another fires
    /// nothing, and a pose struck loosely fires when the hands stop in it;</item>
    /// <item>a candidate that may not fire now (stunned, its cooldown, a structure not armed, the ground rules, movement while
    /// armed) is used up; when a move fires, every other pose the hands are in that frame is used up too (no second fire a
    /// frame later where two zones overlap);</item>
    /// <item><see cref="Move.Base"/> never fires: while it counts on a frame where no structure matches (a clean base,
    /// Ruling 34) it arms the structures, which fire only when the base counted within
    /// <see cref="PoseValues.StructureWindowSeconds"/> before the structure's pose started matching, with the player grounded
    /// on natural ground; firing a structure disarms. While armed (the base counted within that window of now) Jump and Dash
    /// do not fire, so moving from the base into a structure never dashes on the way;</item>
    /// <item>modifiers need natural ground under a grounded player, or the air; movement needs natural ground under a
    /// grounded player; each move waits out its own cooldown;</item>
    /// <item>with anything in a hand nothing counts and every pose and the arming start over (the cooldowns stay); while
    /// stunned nothing fires (the base still arms);</item>
    /// <item>three groups in order, an armed structure, then modifiers, then movement: of each the best-scored candidate
    /// leads, and the first group whose lead is ready fires; a lead that is not ready holds back no group after it. One move
    /// a frame.</item>
    /// </list></summary>
    internal sealed class PoseMachine {
        struct State {
            public bool Matching, Used, RightIsA, Counts;
            public double Since, LeftAt;
            public float Score;
            public float Deepest;       // the best score since it started matching (to follow a move) ...
            public double DeepestAt;    // ... and when that was
        }

        const double Eps = 1e-6;   // seconds: frame times that land on a threshold count as reaching it
        // a followed structure's score counts as deeper only when it is this much better than its best so far (a score's
        // noise from a hand's tracking jitter is well under this at the default zones)
        const float DeeperBy = 0.01f;

        readonly PoseSpec[] specs;
        readonly State[] states;
        readonly PoseValues values;
        readonly double[] lastFired = new double[PoseValues.MoveCount];
        const int Groups = 3;   // an armed structure, a modifier, movement (Rank)
        readonly int[] lead = new int[Groups];
        readonly float[] leadScore = new float[Groups];
        double baseAt = double.NegativeInfinity;

        // the hands' stillness: where both were when one last moved more than StructureStillMetres, and since when
        V3 stillRight, stillLeft;
        bool stillKnown;
        double stillSince = double.PositiveInfinity;

        /// <summary>The score of the last move fired (for the log).</summary>
        public float LastScore { get; private set; }

        public PoseMachine(PoseSpec[] poses, PoseValues v) {
            var kept = new List<PoseSpec>();
            foreach (PoseSpec s in poses ?? new PoseSpec[0])
                if (s.Move > Move.None && s.Move <= Move.Dash) kept.Add(s);
            specs = kept.ToArray();
            states = new State[specs.Length];
            values = v.Clamped();
            for (int i = 0; i < lastFired.Length; i++) lastFired[i] = double.NegativeInfinity;
            Reset();
        }

        /// <summary>Whether the base has armed the structures at <paramref name="now"/>: it counted, clean, within
        /// <see cref="PoseValues.StructureWindowSeconds"/> and no structure has fired since (the practice guide's markers).</summary>
        public bool Armed(double now) => now - baseAt <= values.StructureWindowSeconds + Eps;

        public static bool IsStructure(Move m) => m >= Move.Disc && m <= Move.Wall;
        public static bool IsModifier(Move m) => m >= Move.Straight && m <= Move.Explode;
        public static bool IsMovement(Move m) => m == Move.Jump || m == Move.Dash;

        /// <summary>One frame: the hands in the body frame and the player's state. Returns the move that fires now, or
        /// <see cref="Move.None"/>; <paramref name="rightIsA"/> is the fired pose's assignment (true when nothing fired).</summary>
        public Move Step(double now, in HandSample right, in HandSample left, bool handsEmpty, bool grounded, bool onNatural, bool stunned, out bool rightIsA) {
            rightIsA = true;
            if (!handsEmpty) {
                Reset();
                return Move.None;
            }

            Still(now, in right, in left);

            // every pose's state first, so the base's arming does not depend on the order of the list
            bool baseCounts = false, structureMatches = false;
            for (int i = 0; i < specs.Length; i++) {
                ref State st = ref states[i];
                bool matches = PoseMatch.Match(in specs[i], in right, in left, out bool rA, out float score);
                if (!matches) {
                    if (st.Matching) { st.Matching = false; st.LeftAt = now; }
                    st.Counts = false;
                    continue;
                }
                if (!st.Matching) {
                    st.Matching = true;
                    st.Since = now;
                    st.Deepest = score;
                    st.DeepestAt = now;
                    if (st.Used && now - st.LeftAt >= values.RearmSeconds - Eps) st.Used = false;
                }
                else if (score < st.Deepest - DeeperBy) {
                    st.Deepest = score;
                    st.DeepestAt = now;
                }
                st.RightIsA = rA;
                st.Score = score;
                st.Counts = now - st.Since >= values.MinHoldSeconds - Eps;
                if (IsStructure(specs[i].Move)) structureMatches = true;
                if (st.Counts && specs[i].Move == Move.Base) baseCounts = true;
            }
            if (baseCounts && !structureMatches) baseAt = now;   // Ruling 34: only a clean base arms

            // every move is followed (Rulings 33, 37): a candidate is used up only when it may not fire now (stunned, its
            // cooldown, not armed, the ground rules, movement while armed) or when a move fires. Of each group (0 an armed
            // structure, 1 a modifier, 2 movement) the best-scored candidate leads; the first group in that order whose lead
            // is ready (deep enough, or the hands at rest in it) fires. A lead that is not ready holds back nothing after it
            for (int g = 0; g < Groups; g++) { lead[g] = -1; leadScore[g] = float.PositiveInfinity; }
            for (int i = 0; i < specs.Length; i++) {
                ref State st = ref states[i];
                Move move = specs[i].Move;
                if (!st.Counts || st.Used || move == Move.Base) continue;
                if (stunned) { st.Used = true; continue; }
                int rank = Rank(move, st.Since, now, grounded, onNatural);
                if (rank < 0) { st.Used = true; continue; }
                if (st.Score < leadScore[rank]) { lead[rank] = i; leadScore[rank] = st.Score; }
            }
            int best = -1;
            for (int g = 0; g < Groups && best < 0; g++)
                if (lead[g] >= 0 && Ready(in states[lead[g]], now, g == 0 ? values.StructureDeepScore : values.MoveDeepScore)) best = lead[g];
            if (best < 0) return Move.None;
            // every pose the hands are in now is used up with it: it must stop matching for RearmSeconds before it can fire,
            // or hands resting where two zones overlap would fire the second a frame later (the base is no move: it still arms)
            for (int i = 0; i < specs.Length; i++)
                if (states[i].Matching && specs[i].Move != Move.Base) states[i].Used = true;

            Move fired = specs[best].Move;
            lastFired[(int)fired] = now;
            if (IsStructure(fired)) baseAt = double.NegativeInfinity;
            rightIsA = states[best].RightIsA;
            LastScore = states[best].Score;
            return fired;
        }

        /// <summary>0 an armed structure, 1 a modifier, 2 movement; -1 when it may not fire now (movement also while a
        /// structure is armed).</summary>
        int Rank(Move m, double since, double now, bool grounded, bool onNatural) {
            if (now - lastFired[(int)m] < values.CooldownOf(m) - Eps) return -1;
            bool onEarth = grounded && onNatural;
            if (IsStructure(m)) return onEarth && since - baseAt <= values.StructureWindowSeconds + Eps ? 0 : -1;
            if (IsModifier(m)) return onEarth || !grounded ? 1 : -1;
            if (IsMovement(m)) return onEarth && !(now - baseAt <= values.StructureWindowSeconds + Eps) ? 2 : -1;
            return -1;
        }

        /// <summary>A followed move fires now: deep inside it (its score at most <paramref name="deep"/>: StructureDeepScore
        /// for a structure, MoveDeepScore for the others), or the hands have stopped going deeper into it (both hands still,
        /// and its score no better, for StructureStillSeconds).</summary>
        bool Ready(in State st, double now, float deep) {
            if (st.Score <= deep) return true;
            double wait = values.StructureStillSeconds - Eps;
            return now - stillSince >= wait && now - st.DeepestAt >= wait;
        }

        /// <summary>Follows the hands' stillness: both within StructureStillMetres of where they were at
        /// <see cref="stillSince"/>; a hand that moved further, or a place that is not a number, starts it over.</summary>
        void Still(double now, in HandSample right, in HandSample left) {
            if (!Finite(right.Pos) || !Finite(left.Pos)) {
                stillKnown = false;
                stillSince = double.PositiveInfinity;
                return;
            }
            if (stillKnown && (right.Pos - stillRight).Length <= values.StructureStillMetres && (left.Pos - stillLeft).Length <= values.StructureStillMetres) return;
            stillRight = right.Pos;
            stillLeft = left.Pos;
            stillKnown = true;
            stillSince = now;
        }

        static bool Finite(V3 v) => !float.IsNaN(v.X) && !float.IsInfinity(v.X) && !float.IsNaN(v.Y) && !float.IsInfinity(v.Y) && !float.IsNaN(v.Z) && !float.IsInfinity(v.Z);

        /// <summary>Every pose, the base's arming and the hands' stillness start over; the cooldowns stay. Also what a frame
        /// with a hand full does.</summary>
        public void Reset() {
            for (int i = 0; i < states.Length; i++) states[i] = new State { LeftAt = double.NegativeInfinity };
            baseAt = double.NegativeInfinity;
            stillKnown = false;
            stillSince = double.PositiveInfinity;
        }
    }

    /// <summary>What one frame of the pose recorder brings: nothing, a snapshot, or a rest (once per still rest).</summary>
    internal enum CaptureEvent : byte { None, Snapshot, Rest }

    /// <summary>The pose recorder's rule (Task 3, <c>[EarthPose] Capture</c>): a snapshot is due once both hands have stayed
    /// within <c>stillMetres</c> of where they were for <c>stillSeconds</c>, and only after either hand has moved
    /// <c>moveMetres</c> from where it was at the last snapshot. Before the first snapshot that is where it was first seen, so
    /// hands resting when the capture starts are not taken for a pose. A still moment with BOTH hands more than
    /// <c>restBelowMetres</c> under the head is a rest (arms hanging at the sides): never a snapshot, but it arms the next one
    /// (it counts as the move away). Every pose of the table keeps a hand well above that line (the lowest pair, Base and
    /// Stomp, has its higher hand 0.55 m under the head). The places are in the world; a hand's height under the head is the
    /// body frame's y (the body frame turns only about the vertical, and the capture's body scale is 1).</summary>
    internal sealed class CaptureGate {
        const double Eps = 1e-6;   // seconds: a frame that lands on the hold time counts as reaching it

        readonly float still, move, restBelow;
        readonly double seconds;
        bool started, anchored, armed, restTold;
        V3 lastR, lastL, anchorR, anchorL;
        double sinceR, sinceL;

        public CaptureGate(float stillMetres, float stillSeconds, float moveMetres, float restBelowMetres) {
            still = stillMetres;
            seconds = stillSeconds;
            move = moveMetres;
            restBelow = restBelowMetres;
        }

        /// <summary>One frame: the head and both hands in the world. <see cref="CaptureEvent.Snapshot"/> when a snapshot is
        /// due now (the hands' places are then the last snapshot's); <see cref="CaptureEvent.Rest"/> on the first frame of a
        /// still rest. A place that is not a number (a hand lost) brings nothing and starts the stillness over; a move already
        /// made still counts.</summary>
        public CaptureEvent Step(double now, V3 head, V3 right, V3 left) {
            if (!Finite(head) || !Finite(right) || !Finite(left) || double.IsNaN(now) || double.IsInfinity(now)) {
                anchored = false;
                restTold = false;
                return CaptureEvent.None;
            }
            if (!started) {
                started = true;
                lastR = right;
                lastL = left;
            }
            if (!anchored || (right - anchorR).Length > still) { anchorR = right; sinceR = now; }
            if (!anchored || (left - anchorL).Length > still) { anchorL = left; sinceL = now; }
            anchored = true;
            if (!armed && ((right - lastR).Length >= move || (left - lastL).Length >= move)) armed = true;
            bool stillNow = now - sinceR >= seconds - Eps && now - sinceL >= seconds - Eps;
            if (stillNow && right.Y - head.Y < -restBelow && left.Y - head.Y < -restBelow) {
                armed = true;
                if (restTold) return CaptureEvent.None;
                restTold = true;
                return CaptureEvent.Rest;
            }
            restTold = false;
            if (!armed || !stillNow) return CaptureEvent.None;
            armed = false;
            lastR = right;
            lastL = left;
            return CaptureEvent.Snapshot;
        }

        /// <summary>Starts over: the next place seen is where the hands first were.</summary>
        public void Reset() {
            started = false;
            anchored = false;
            armed = false;
            restTold = false;
        }

        static bool Finite(V3 v) => !float.IsNaN(v.X) && !float.IsInfinity(v.X) && !float.IsNaN(v.Y) && !float.IsInfinity(v.Y) && !float.IsNaN(v.Z) && !float.IsInfinity(v.Z);
    }

    /// <summary>One hand of a capture snapshot in the body frame: its place, and its controller's local X, Y and Z axes.</summary>
    internal struct CaptureHand {
        public V3 Pos, X, Y, Z;
    }

    /// <summary>The pose recorder's file (Task 3): <c>UserData\Earthbending\pose-capture-&lt;yyyyMMdd-HHmmss&gt;.csv</c>, the
    /// <see cref="Header"/> line, then one <see cref="Row"/> per snapshot. Earthbending/tools/fit_poses.py reads it by column
    /// name: n, t (seconds since the capture started), the head in the world and its yaw, then for the right hand and then the
    /// left its place in the body frame and its controller's local X, Y and Z axes in the body frame.</summary>
    internal static class PoseCapture {
        public static readonly string Header = Columns();

        /// <summary>A hand as the capture records it: the controller's world place and its local axes (transform.right, up and
        /// forward) through <see cref="BodyFrame.ToBody"/> at body scale 1, the axes as directions.</summary>
        public static CaptureHand Hand(V3 head, float yawDeg, V3 pos, V3 right, V3 up, V3 forward) {
            HandSample xy = BodyFrame.ToBody(head, yawDeg, pos, right, up, 1f);
            HandSample z = BodyFrame.ToBody(head, yawDeg, pos, forward, forward, 1f);
            return new CaptureHand { Pos = xy.Pos, X = xy.Palm, Y = xy.Fingers, Z = z.Palm };
        }

        /// <summary>One snapshot's line, numbers in the invariant culture.</summary>
        public static string Row(int n, double t, V3 head, float yawDeg, in CaptureHand right, in CaptureHand left) {
            var cells = new List<string>(30) {
                n.ToString(CultureInfo.InvariantCulture), t.ToString("0.###", CultureInfo.InvariantCulture),
                N(head.X), N(head.Y), N(head.Z), yawDeg.ToString("0.##", CultureInfo.InvariantCulture),
            };
            foreach (CaptureHand h in new[] { right, left })
                foreach (V3 v in new[] { h.Pos, h.X, h.Y, h.Z }) { cells.Add(N(v.X)); cells.Add(N(v.Y)); cells.Add(N(v.Z)); }
            return string.Join(",", cells);
        }

        static string N(float f) => f.ToString("0.#####", CultureInfo.InvariantCulture);

        static string Columns() {
            var c = new List<string> { "n", "t", "headX", "headY", "headZ", "yaw" };
            foreach (string hand in new[] { "right", "left" }) {
                foreach (char k in "XYZ") c.Add(hand + k);
                foreach (char axis in "XYZ") foreach (char k in "XYZ") c.Add(hand + axis + "axis" + k);
            }
            return string.Join(",", c);
        }
    }

    /// <summary>One frame of the hands trace (<see cref="PoseTrace"/>): the time, the practice guide's step (None: none), what
    /// the machine was given (hands empty, grounded, on natural ground, stunned), whether the structures were armed after the
    /// frame, the head's yaw, and both hands in the body frame the machine sees.</summary>
    internal struct TraceFrame {
        public double T;
        public Move Practice;
        public bool Empty, Grounded, Natural, Stunned, Armed;
        public float Yaw;
        public HandSample Right, Left;
    }

    /// <summary>A move fired, as the trace records it: the time, the move, its score, and whether A was the right hand.</summary>
    internal struct TraceFire {
        public double T;
        public Move Move;
        public float Score;
        public bool RightIsA;
    }

    /// <summary>The hands trace (task 17a, <c>[EarthPose] Trace</c>): <c>UserData\Earthbending\pose-trace-&lt;yyyyMMdd-HHmmss&gt;.csv</c>,
    /// the <see cref="Header"/>, then one row per frame (at most one every <see cref="RowSeconds"/>, <see cref="Due"/>) and
    /// an event row whenever a move fires: <c>t,fired,Move,score,right|left</c> (A's hand). Numbers in the invariant culture,
    /// 3 decimals. PoseTraceFile.cs writes it; Earthbending.Tests' <c>--replay</c> reads it with <see cref="Parse"/>.</summary>
    internal static class PoseTrace {
        /// <summary>At most 45 frame rows a second.</summary>
        public const double RowSeconds = 1.0 / 45.0;
        /// <summary>The file stops growing at 20 MB.</summary>
        public const long MaxBytes = 20L * 1024 * 1024;
        public const string FiredWord = "fired";
        const int Columns = 26;
        const double Eps = 1e-6;

        public static readonly string Header = MakeHeader();

        static string MakeHeader() {
            var c = new List<string> { "t", "practice", "empty", "grounded", "natural", "stunned", "armed", "yaw" };
            foreach (string hand in new[] { "right", "left" })
                foreach (string part in new[] { "", "Palm", "Fingers" })
                    foreach (char k in "XYZ") c.Add(hand + part + k);
            return string.Join(",", c);
        }

        /// <summary>The file's name for a trace started at <paramref name="at"/> (local time).</summary>
        public static string FileName(DateTime at) => "pose-trace-" + at.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv";

        /// <summary>Whether a frame row is due at <paramref name="now"/>, the last one written at <paramref name="lastRow"/>.</summary>
        public static bool Due(double now, double lastRow) => now - lastRow >= RowSeconds - Eps;

        /// <summary>One frame's row (no line break).</summary>
        public static string Row(in TraceFrame f) {
            var sb = new System.Text.StringBuilder(200);
            AppendRow(sb, in f);
            return sb.ToString();
        }

        public static void AppendRow(System.Text.StringBuilder sb, in TraceFrame f) {
            sb.Append(N(f.T)).Append(',').Append(f.Practice == Move.None ? "" : f.Practice.ToString())
                .Append(',').Append(B(f.Empty)).Append(',').Append(B(f.Grounded)).Append(',').Append(B(f.Natural))
                .Append(',').Append(B(f.Stunned)).Append(',').Append(B(f.Armed)).Append(',').Append(N(f.Yaw));
            foreach (HandSample h in new[] { f.Right, f.Left })
                foreach (V3 v in new[] { h.Pos, h.Palm, h.Fingers })
                    sb.Append(',').Append(N(v.X)).Append(',').Append(N(v.Y)).Append(',').Append(N(v.Z));
        }

        /// <summary>A fire's event row (no line break).</summary>
        public static string Fire(in TraceFire f) => N(f.T) + "," + FiredWord + "," + f.Move + "," + N(f.Score) + "," + (f.RightIsA ? "right" : "left");

        static string B(bool b) => b ? "1" : "0";
        static string N(double d) => d.ToString("0.000", CultureInfo.InvariantCulture);

        /// <summary>A trace's lines: its frames and its fires, in the file's order. The header (and any line starting
        /// <c>t,</c>) and blank lines are skipped; a line that does not read is skipped and named in <paramref name="errors"/>.</summary>
        public static void Parse(string[] lines, out List<TraceFrame> frames, out List<TraceFire> fires, out List<string> errors) {
            frames = new List<TraceFrame>();
            fires = new List<TraceFire>();
            errors = new List<string>();
            for (int i = 0; i < (lines?.Length ?? 0); i++) {
                string line = (lines![i] ?? "").Trim();
                if (line.Length == 0 || line.StartsWith("t,", StringComparison.Ordinal)) continue;
                string[] c = line.Split(',');
                if (c.Length == 5 && c[1] == FiredWord) {
                    if (D(c[0], out double t) && PoseLibrary.TryMove(c[2], out Move m) && F(c[3], out float score) && (c[4] == "right" || c[4] == "left"))
                        fires.Add(new TraceFire { T = t, Move = m, Score = score, RightIsA = c[4] == "right" });
                    else errors.Add("line " + (i + 1) + ": not a fired row: " + line);
                    continue;
                }
                if (c.Length != Columns) { errors.Add("line " + (i + 1) + ": " + c.Length + " cells, want " + Columns); continue; }
                var f = new TraceFrame();
                Move practice = Move.None;
                bool ok = D(c[0], out f.T) && (c[1].Length == 0 || PoseLibrary.TryMove(c[1], out practice))
                    && Flag(c[2], out f.Empty) && Flag(c[3], out f.Grounded) && Flag(c[4], out f.Natural) && Flag(c[5], out f.Stunned) && Flag(c[6], out f.Armed)
                    && F(c[7], out f.Yaw) && Hand(c, 8, out f.Right) && Hand(c, 17, out f.Left);
                f.Practice = practice;
                if (ok) frames.Add(f);
                else errors.Add("line " + (i + 1) + ": a cell that does not read: " + line);
            }
        }

        static bool Hand(string[] c, int at, out HandSample h) {
            h = default;
            var n = new float[9];
            for (int i = 0; i < 9; i++) if (!F(c[at + i], out n[i])) return false;
            h = new HandSample(new V3(n[0], n[1], n[2]), new V3(n[3], n[4], n[5]), new V3(n[6], n[7], n[8]));
            return true;
        }

        static bool Flag(string s, out bool b) {
            b = s == "1";
            return s == "0" || s == "1";
        }

        static bool D(string s, out double d) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && !double.IsNaN(d) && !double.IsInfinity(d);
        static bool F(string s, out float f) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f);
    }

    /// <summary>The capture guide's words and rules (Task 3b; CaptureGuide.cs draws them): what each hand does in each of
    /// the sixteen poses, in the order the recorder counts its snapshots (the <see cref="Move"/> enum's, the spec 3.3
    /// table's), the next pose for a count of snapshots, the card's text, and how far one frame eases toward a new place.</summary>
    internal static class PoseGuide {
        public const int Count = 16;
        public const string Hint = "Hold still until both hands buzz twice. Rest your arms at your sides between poses.";
        public const string AllDone = "All 16 done. Tell Claude.";
        /// <summary>The markers' colours as the card names them: the defaults of [EarthLooks] GuideRightColour and GuideLeftColour.</summary>
        public const string Legend = "orange = right, blue = left";

        struct Entry {
            public Move Move;
            public string Right, Left;
        }

        static Entry E(Move m, string right, string left) => new Entry { Move = m, Right = right, Left = left };

        // the owner's words (the task's table); the right hand is the pose's A hand
        static readonly Entry[] Table = {
            E(Move.Base,     "palm up, fingers forward, just above the waist", "the same, shoulder width apart"),
            E(Move.Disc,     "arm half out in front, fingers forward, palm facing left", "across your right forearm, palm toward you, fingers pointing right"),
            E(Move.Ball,     "near your body, just under the shoulder, palm facing left, fingers forward", "arm half out, just under the shoulder, palm facing right, fingers forward"),
            E(Move.Pillar,   "low, where Base was, palm facing left, fingers forward", "at face height, palm facing right, fingers up"),
            E(Move.Cube,     "at face height, fingers up, palm facing left", "at face height, fingers up, palm facing right"),
            E(Move.Wall,     "palm up, lifted to your forehead", "the same"),
            E(Move.Straight, "in front of your right shoulder, palm facing left, fingers up", "arm half out in front, palm down, fingers forward"),
            E(Move.Kick,     "in front of your right shoulder, palm facing left, fingers up", "at your belly button, palm toward you, fingers down"),
            E(Move.Stomp,    "in front at waist height, fingers forward and down, palm down", "the same"),
            E(Move.Uppercut, "in front of your right shoulder, fingers up, tilted a little forward", "half out in front of your face, palm toward your face, fingers up"),
            E(Move.Parry,    "arm out at shoulder height, palm forward, fingers up", "the same"),
            E(Move.Hold,     "arm out at shoulder height near the middle, palm forward, fingers up", "just under your right hand, palm up, fingers forward"),
            E(Move.Flick,    "arm out at shoulder height, palm facing right, fingers forward", "a few fists under your right hand, palm facing right, fingers forward"),
            E(Move.Explode,  "at your belly, palm toward you, fingers pointing left, knuckles touching the left", "at your belly, palm toward you, fingers pointing right"),
            E(Move.Jump,     "at your hip, palm up, fingers forward", "above your shoulder, palm facing right, fingers up"),
            E(Move.Dash,     "at your hip, palm up, fingers forward", "arm half out in front, palm facing right, fingers forward"),
        };

        static readonly Move[] Order = Array.ConvertAll(Table, e => e.Move);

        /// <summary>The sixteen poses in the order they are struck.</summary>
        public static IReadOnlyList<Move> Moves => Order;

        /// <summary>What the right hand does in the pose; empty for a move that is no pose.</summary>
        public static string Right(Move m) => Find(m, out Entry e) ? e.Right : "";

        /// <summary>What the left hand does in the pose; empty for a move that is no pose.</summary>
        public static string Left(Move m) => Find(m, out Entry e) ? e.Left : "";

        static bool Find(Move m, out Entry entry) {
            foreach (Entry e in Table)
                if (e.Move == m) { entry = e; return true; }
            entry = default;
            return false;
        }

        /// <summary>Every pose has its snapshot (extra snapshots are still done).</summary>
        public static bool Done(int taken) => taken >= Count;

        /// <summary>The pose to strike after <paramref name="taken"/> snapshots; <see cref="Move.None"/> once done.</summary>
        public static Move Next(int taken) => Done(taken) ? Move.None : Table[taken < 0 ? 0 : taken].Move;

        /// <summary><c>Pose N of 16: Name</c> for the next pose; empty once done.</summary>
        public static string Title(int taken) {
            Move m = Next(taken);
            return m == Move.None ? "" : "Pose " + ((int)m).ToString(CultureInfo.InvariantCulture) + " of " + Count.ToString(CultureInfo.InvariantCulture) + ": " + m;
        }

        /// <summary>The card's whole text, one line break between its lines and none longer than <paramref name="wrap"/>
        /// letters: the title, the right hand's line, the left hand's, the hint and, with <paramref name="markers"/>, the
        /// colours; once done only <see cref="AllDone"/>. With markers each hand's lines are in its marker's colour
        /// (<paramref name="rightHex"/>, <paramref name="leftHex"/>: #RRGGBB; every line carries its own rich-text tag).</summary>
        public static string Card(int taken, string rightHex, string leftHex, bool markers, int wrap) {
            Move m = Next(taken);
            if (m == Move.None) return AllDone;
            var lines = new List<string> { Title(taken), "" };
            Add(lines, "RIGHT: " + Right(m), markers ? rightHex : "", wrap);
            Add(lines, "LEFT: " + Left(m), markers ? leftHex : "", wrap);
            lines.Add("");
            Add(lines, Hint, "", wrap);
            if (markers) Add(lines, Legend, "", wrap);
            return string.Join("\n", lines);
        }

        /// <summary>Adds the text's wrapped lines, each in the colour <paramref name="hex"/> when one is given.</summary>
        public static void Add(List<string> lines, string text, string hex, int wrap) {
            foreach (string l in Wrap(text, wrap)) lines.Add(string.IsNullOrEmpty(hex) ? l : "<color=" + hex + ">" + l + "</color>");
        }

        /// <summary>The text broken at spaces into lines of at most <paramref name="width"/> letters; a word longer than that
        /// stays whole on its own line. A width under 1 breaks nothing; an empty text has no line.</summary>
        public static List<string> Wrap(string text, int width) {
            var lines = new List<string>();
            string line = "";
            foreach (string word in (text ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) {
                if (line.Length == 0) line = word;
                else if (width < 1 || line.Length + 1 + word.Length <= width) line += " " + word;
                else { lines.Add(line); line = word; }
            }
            if (line.Length > 0) lines.Add(line);
            return lines;
        }

        /// <summary>How much of the way to its new place something goes in one frame of <paramref name="dt"/> seconds, eased
        /// with the time constant <paramref name="seconds"/> ([EarthLooks] GuideFollowSeconds): 1 - e^(-dt / seconds), the
        /// same after a second however many frames it was. 0 to 1; a follow time that is not above 0, or a number that is
        /// not one, goes all the way.</summary>
        public static float Ease(float dt, float seconds) {
            if (float.IsNaN(dt) || !(seconds > 0f) || float.IsInfinity(seconds)) return 1f;
            if (dt <= 0f) return 0f;
            return 1f - (float)Math.Exp(-dt / seconds);
        }
    }

    /// <summary>Where a practice step stands: the move is being tried, it fired (shown for the show time), it was skipped
    /// (shown for the show time), or every step is over.</summary>
    internal enum PracticePhase : byte { Try, Worked, Skipped, Done }

    /// <summary>What the practice guide's card shows after one frame (<see cref="PracticeRun.Step"/>).</summary>
    internal struct PracticeView {
        public PracticePhase Phase;
        /// <summary>The step, from 0, of <see cref="Count"/>; <see cref="Move"/> is its move (None once done).</summary>
        public int Index, Count;
        public Move Move;
        /// <summary>The score the step's move fired with (<see cref="PracticePhase.Worked"/>).</summary>
        public float Score;
        /// <summary>Another move that fired during the step, still being said (None: nothing to say), and its score.</summary>
        public Move Other;
        public float OtherScore;
        /// <summary>The counts so far: steps that worked with no other move fired first, steps that worked after one, steps skipped.</summary>
        public int First, Tries, Skipped;
        /// <summary>Another number whenever anything above changed: the card's text is written again only then.</summary>
        public int Version;
        /// <summary>The log's line, only on the frame a step ends (worked or skipped) and on the frame the run is done.</summary>
        public string? Log;
    }

    /// <summary>The practice guide's step machine (Task 3c, <c>[EarthPose] Practice</c>; PracticeGuide.cs draws it): a list
    /// of moves tried one after the other. A step ends when its move fires (said for the show time, then the next step) or
    /// when it has not fired for the skip time (said for the show time too). Another move fired during a step is said for
    /// the show time and counted; the step stays. A move fired while a result is shown is not counted. After the last step
    /// the run is done, with its counts, until <see cref="Restart"/>. It only watches: it is told the moves that fired.
    /// A frame without an event allocates nothing.</summary>
    internal sealed class PracticeRun {
        public const string DefaultList = "Jump, Pillar, Straight, Wall, Cube, Ball, Disc, Kick, Stomp, Uppercut, Parry, Hold, Flick, Explode, Dash";
        public const float DefaultSkipSeconds = 25f, DefaultShowSeconds = 1.5f;
        public const string BaseFirst = "First Base (both palms up at your waist), then ";
        const double Eps = 1e-6;   // seconds: a frame that lands on a time counts as reaching it

        readonly Move[] steps;
        readonly List<Move> others = new List<Move>();   // the other moves fired during this step, in order
        PracticeView view;
        bool started, told;
        double stepAt, shownAt, otherAt;

        /// <summary>A step not fired after this many seconds is skipped (not above 0, or not a number: the default).</summary>
        public float SkipSeconds { get => skip; set => skip = value > 0f && !float.IsInfinity(value) ? value : DefaultSkipSeconds; }
        /// <summary>A result, and another move's line, are shown this many seconds (negative or not a number: the default).</summary>
        public float ShowSeconds { get => show; set => show = value >= 0f && !float.IsInfinity(value) ? value : DefaultShowSeconds; }
        float skip = DefaultSkipSeconds, show = DefaultShowSeconds;

        public PracticeRun(IList<Move> moves, float skipSeconds, float showSeconds) {
            var kept = new List<Move>();
            foreach (Move m in moves ?? new Move[0])
                if (m > Move.Base && m <= Move.Dash) kept.Add(m);
            steps = kept.ToArray();
            SkipSeconds = skipSeconds;
            ShowSeconds = showSeconds;
            Restart();
        }

        /// <summary>The list's moves in its order (<c>[EarthPose] PracticeList</c>: names split by commas, any case, a name
        /// twice is two steps). A name that is no move, and Base (never a step: it arms the structures), is skipped and
        /// named in <paramref name="errors"/>.</summary>
        public static List<Move> Parse(string? list, out List<string> errors) {
            errors = new List<string>();
            var moves = new List<Move>();
            foreach (string part in (list ?? "").Split(',')) {
                string name = part.Trim();
                if (name.Length == 0) continue;
                if (!PoseLibrary.TryMove(name, out Move m)) errors.Add("no move named '" + name + "'");
                else if (m == Move.Base) errors.Add("Base is never a step (it arms the structures)");
                else moves.Add(m);
            }
            return moves;
        }

        /// <summary>From step 1 again, nothing counted (Practice switched off and on).</summary>
        public void Restart() {
            int version = view.Version + 1;
            view = new PracticeView { Count = steps.Length, Version = version };
            others.Clear();
            started = false;
            told = false;
            Enter(0);
        }

        void Enter(int index) {
            view.Index = index;
            view.Other = Move.None;
            view.Score = 0f;
            others.Clear();
            started = false;   // the step's time starts at its first frame
            bool done = index >= steps.Length;
            view.Phase = done ? PracticePhase.Done : PracticePhase.Try;
            view.Move = done ? Move.None : steps[index];
            view.Version++;
        }

        /// <summary>One frame: the time (any clock that only goes forward), and the move that fired this frame with its score
        /// (<see cref="Move.None"/>: none). Returns what the card shows now.</summary>
        public PracticeView Step(double now, Move fired, float score) {
            view.Log = null;
            if (view.Phase == PracticePhase.Worked || view.Phase == PracticePhase.Skipped) {
                if (now - shownAt < show - Eps) return view;
                Enter(view.Index + 1);
                fired = Move.None;
            }
            if (view.Phase == PracticePhase.Done) {
                if (!told) {
                    told = true;
                    view.Log = "[Earth] Practice: done: " + Counts(in view);
                }
                return view;
            }
            if (!started) {
                started = true;
                stepAt = now;
            }
            if (view.Other != Move.None && now - otherAt >= show - Eps) {
                view.Other = Move.None;
                view.Version++;
            }
            if (fired == view.Move) {
                view.Phase = PracticePhase.Worked;
                view.Score = score;
                view.Other = Move.None;
                view.Version++;
                shownAt = now;
                if (others.Count == 0) view.First++;
                else view.Tries++;
                view.Log = "[Earth] Practice: " + view.Move + " worked (score " + N(score, "0.00") + ") after " + N(now - stepAt, "0.0") + " s; first fired: " + Others();
            }
            else if (fired != Move.None && fired != Move.Base) {
                others.Add(fired);
                view.Other = fired;
                view.OtherScore = score;
                view.Version++;
                otherAt = now;
            }
            else if (now - stepAt >= skip - Eps) {
                view.Phase = PracticePhase.Skipped;
                view.Other = Move.None;
                view.Skipped++;
                view.Version++;
                shownAt = now;
                view.Log = "[Earth] Practice: " + view.Move + " skipped after " + N(skip, "0.#") + " s; fired instead: " + Others();
            }
            return view;
        }

        /// <summary>The other moves fired during the step, in the order each first fired, with its count when more than
        /// once (<c>Cube x2, Dash</c>); <c>none</c> without any.</summary>
        string Others() {
            if (others.Count == 0) return "none";
            var names = new List<string>();
            for (int i = 0; i < others.Count; i++) {
                Move m = others[i];
                if (others.IndexOf(m) != i) continue;
                int n = others.FindAll(o => o == m).Count;
                names.Add(n > 1 ? m + " x" + n.ToString(CultureInfo.InvariantCulture) : m.ToString());
            }
            return string.Join(", ", names);
        }

        static string Counts(in PracticeView v) => v.First.ToString(CultureInfo.InvariantCulture) + " of " + v.Count.ToString(CultureInfo.InvariantCulture)
            + " worked first time, " + v.Tries.ToString(CultureInfo.InvariantCulture) + " needed tries, " + v.Skipped.ToString(CultureInfo.InvariantCulture) + " skipped";

        static string N(double d, string format) => d.ToString(format, CultureInfo.InvariantCulture);

        /// <summary>The pose the hand markers show for a view: the step's move while it is tried, but Base's hands for a
        /// structure the base has not armed (<paramref name="armed"/>: <see cref="PoseMachine.Armed"/>);
        /// <see cref="Move.None"/> (no markers) while a result is shown and once done.</summary>
        public static Move Marker(in PracticeView v, bool armed) =>
            v.Phase != PracticePhase.Try ? Move.None : PoseMachine.IsStructure(v.Move) && !armed ? Move.Base : v.Move;

        /// <summary>The card's whole text for a view, no line longer than <paramref name="wrap"/> letters. While a move is
        /// tried: <c>Try N of M: Move</c>, the hand lines (<see cref="PoseGuide"/>; with <paramref name="markers"/> each in
        /// its marker's colour), for a structure the base-first line, another move that just fired, and with markers the
        /// colours. Otherwise one sentence: the move worked, it was skipped, or the summary.</summary>
        public static string Card(in PracticeView v, string rightHex, string leftHex, bool markers, int wrap) {
            var lines = new List<string>();
            if (v.Phase == PracticePhase.Worked) PoseGuide.Add(lines, v.Move + " worked (score " + N(v.Score, "0.00") + ")", "", wrap);
            else if (v.Phase == PracticePhase.Skipped) PoseGuide.Add(lines, "Skipped " + v.Move, "", wrap);
            else if (v.Phase == PracticePhase.Done) PoseGuide.Add(lines, "Practice done: " + Counts(in v), "", wrap);
            else {
                lines.Add("Try " + (v.Index + 1).ToString(CultureInfo.InvariantCulture) + " of " + v.Count.ToString(CultureInfo.InvariantCulture) + ": " + v.Move);
                lines.Add("");
                PoseGuide.Add(lines, "RIGHT: " + PoseGuide.Right(v.Move), markers ? rightHex : "", wrap);
                PoseGuide.Add(lines, "LEFT: " + PoseGuide.Left(v.Move), markers ? leftHex : "", wrap);
                if (PoseMachine.IsStructure(v.Move)) {
                    lines.Add("");
                    PoseGuide.Add(lines, BaseFirst + v.Move, "", wrap);
                }
                if (v.Other != Move.None) {
                    lines.Add("");
                    PoseGuide.Add(lines, "That was " + v.Other + " (score " + N(v.OtherScore, "0.00") + "), not " + v.Move, "", wrap);
                }
                if (markers) {
                    lines.Add("");
                    PoseGuide.Add(lines, PoseGuide.Legend, "", wrap);
                }
            }
            return string.Join("\n", lines);
        }
    }

    // ---------------------------------------------------------------- the game's side of the machine (Task 5)
    // Hands.cs and Bend.cs read the game every frame and feed the machine; these are the rules of that feeding that need no
    // Unity (the axis settings, the two-frame empty test, the ground grace) and Moves.Push's velocity.

    /// <summary>One signed local axis of a controller's transform, as an <c>[EarthPose]</c> axis setting may name it: one of
    /// <c>+x -x +y -y +z -z</c> (x the transform's right, y its up, z its forward).</summary>
    internal readonly struct HandAxis {
        /// <summary>0 x, 1 y, 2 z.</summary>
        public readonly byte Index;
        public readonly bool Negative;

        public HandAxis(int index, bool negative) { Index = (byte)(index < 0 ? 0 : index > 2 ? 2 : index); Negative = negative; }

        /// <summary>One of the six names; case and the spaces round it do not matter. False for anything else.</summary>
        public static bool TryParse(string? text, out HandAxis axis) {
            axis = default;
            string t = (text ?? "").Trim();
            if (t.Length != 2 || (t[0] != '+' && t[0] != '-')) return false;
            int i = "xyz".IndexOf(char.ToLowerInvariant(t[1]));
            if (i < 0) return false;
            axis = new HandAxis(i, t[0] == '-');
            return true;
        }

        /// <summary>The axis as a direction in the controller's own space: (1, 0, 0) for +x, (0, -1, 0) for -y, ...</summary>
        public V3 Direction {
            get {
                float s = Negative ? -1f : 1f;
                return Index == 0 ? new V3(s, 0f, 0f) : Index == 1 ? new V3(0f, s, 0f) : new V3(0f, 0f, s);
            }
        }

        public override string ToString() => (Negative ? "-" : "+") + "xyz"[Index];
    }

    /// <summary>The four axis settings (Ruling 36): each is a direction in its controller's own space, which Hands.Read turns
    /// into the world with <c>Transform.TransformDirection</c>. A setting is a signed axis (<c>-y</c>, <see cref="HandAxis"/>)
    /// or three numbers <c>a,b,c</c> (the invariant culture: a dot decimal, a comma between the numbers, spaces allowed),
    /// made length 1. fit_poses.py prints the owner's from a capture (their fingers sit about 21 degrees off -y, toward +z).</summary>
    internal static class HandAxes {
        /// <summary>A hand's palm and fingers within this many degrees of each other, or of opposite, are no hand: the two are
        /// square to each other (the rule before 0.4.3, one axis for both, refused +y with +y and +y with -y alike).</summary>
        public const float MinApartDegrees = 45f;

        // Ruling 3, until the owner's capture says otherwise: the OpenXR grip pose as Unity sees it (the palm faces the other
        // hand, the fingers point ahead of the grip). The owner's fitted directions go in their cfg, not here
        public static readonly V3 PalmRight = new V3(-1f, 0f, 0f), FingersRight = new V3(0f, 0f, 1f),
            PalmLeft = new V3(1f, 0f, 0f), FingersLeft = new V3(0f, 0f, 1f);

        /// <summary>A setting's text as a direction of length 1: a signed axis, or three numbers. False, with
        /// <paramref name="why"/>, for anything else: neither form (a comma decimal makes more than three numbers), a number
        /// that is not finite, or no direction (zero length).</summary>
        public static bool TryParse(string? text, out V3 direction, out string why) {
            direction = V3.Zero;
            why = "";
            if (HandAxis.TryParse(text, out HandAxis axis)) {
                direction = axis.Direction;
                return true;
            }
            string[] parts = (text ?? "").Split(',');
            var n = new float[3];
            bool numbers = parts.Length == 3;
            for (int i = 0; numbers && i < 3; i++)
                numbers = float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out n[i]) && !float.IsNaN(n[i]) && !float.IsInfinity(n[i]);
            if (!numbers) {
                why = "is neither a signed axis (+x -x +y -y +z -z) nor three numbers a,b,c with a dot decimal";
                return false;
            }
            direction = new V3(n[0], n[1], n[2]).Normalized;
            if (V3.Dot(direction, direction) > 0f) return true;
            why = "has no direction (zero length)";
            return false;
        }

        /// <summary>A direction as a setting's text: the signed axis it is exactly, else three numbers (invariant culture).</summary>
        public static string Describe(V3 v) {
            for (int i = 0; i < 6; i++) {
                var a = new HandAxis(i / 2, i % 2 == 1);
                V3 d = a.Direction;
                if (v.X == d.X && v.Y == d.Y && v.Z == d.Z) return a.ToString();
            }
            return string.Format(CultureInfo.InvariantCulture, "{0:0.###},{1:0.###},{2:0.###}", v.X, v.Y, v.Z);
        }

        /// <summary>One hand's two settings as the game uses them, each length 1. A setting that <see cref="TryParse"/>
        /// refuses, or a palm and fingers within <see cref="MinApartDegrees"/> of each other or of opposite, gives the hand
        /// both its defaults and adds one line to <paramref name="warnings"/>, naming each setting at fault, what it said and
        /// what is used. The defaults must pass.</summary>
        public static void Check(string palmKey, string? palmText, string fingerKey, string? fingerText, V3 palmDefault, V3 fingerDefault,
            out V3 palm, out V3 fingers, List<string> warnings) {
            bool palmOk = TryParse(palmText, out palm, out string palmWhy), fingersOk = TryParse(fingerText, out fingers, out string fingersWhy);
            string fault;
            if (!palmOk || !fingersOk) {
                var bad = new List<string>(2);
                if (!palmOk) bad.Add(palmKey + " '" + palmText + "' " + palmWhy);
                if (!fingersOk) bad.Add(fingerKey + " '" + fingerText + "' " + fingersWhy);
                fault = string.Join("; ", bad);
            }
            else {
                float apart = PoseMatch.Angle(palm, fingers);
                if (apart > MinApartDegrees && apart < 180f - MinApartDegrees) return;
                fault = palmKey + " " + Describe(palm) + " and " + fingerKey + " " + Describe(fingers) + " are " + apart.ToString("0", CultureInfo.InvariantCulture)
                    + " degrees apart (the palm and the fingers are square to each other: " + MinApartDegrees.ToString("0", CultureInfo.InvariantCulture)
                    + " to " + (180f - MinApartDegrees).ToString("0", CultureInfo.InvariantCulture) + " wanted)";
            }
            palm = palmDefault;
            fingers = fingerDefault;
            warnings.Add(fault + "; this hand uses its defaults, " + palmKey + " " + Describe(palmDefault) + " and " + fingerKey + " " + Describe(fingerDefault));
        }
    }

    /// <summary>The empty-hands test as the machine is fed it. The machine starts every pose over on any frame with a hand
    /// full, so a hand that reads full for a single frame (a pickup handed over, a grab and its release in one frame) would let
    /// a held pose fire again once its cooldown is over. Full counts only from the <see cref="FullFrames"/>th full frame in a
    /// row; an empty frame reads empty at once.</summary>
    internal sealed class EmptyGate {
        public const int FullFrames = 2;
        int full;

        /// <summary>One frame: whether the hands are empty now, as the game says. Returns what the machine is given.</summary>
        public bool Step(bool emptyNow) {
            if (emptyNow) { full = 0; return true; }
            if (full < FullFrames) full++;
            return full < FullFrames;
        }

        public void Reset() => full = 0;
    }

    /// <summary>Ruling 16: the game's grounded flag (the character controller's last move touching below) flickers, and a
    /// pose judged on a frame in the air is used up, so a flicker would eat a Jump. The machine's "grounded" is grounded within
    /// the last <see cref="Seconds"/>, and its ground is the ground that was under the feet on that last grounded frame (as
    /// RUMBLE uses the last grounded spot).</summary>
    internal sealed class GroundGrace {
        const double Eps = 1e-6;   // seconds: a frame that lands on the grace's end still counts
        double lastGrounded = double.NegativeInfinity;
        bool lastNatural;

        /// <summary>The grace in seconds (0: only a grounded frame is grounded).</summary>
        public float Seconds;

        public GroundGrace(float seconds) { Seconds = seconds; }

        /// <summary>One frame: the game's grounded flag and the ground under the feet now. Returns whether the machine is
        /// grounded; <paramref name="onNatural"/> is the last grounded frame's ground (false before any).</summary>
        public bool Step(double now, bool grounded, bool natural, out bool onNatural) {
            if (grounded) { lastGrounded = now; lastNatural = natural; }
            onNatural = lastNatural;
            return now - lastGrounded <= Seconds + Eps;
        }

        public void Reset() {
            lastGrounded = double.NegativeInfinity;
            lastNatural = false;
        }
    }

    /// <summary>A push's new velocity (Moves.Push, ruling 14): the push is added to the velocity the player has (a velocity
    /// that is not a number is replaced), so pushes stack; the sum is held to <c>maxSpeed</c> along its own direction (a max
    /// that is not above 0 holds nothing); then, only on the ground, y is at least <see cref="MinLift"/> so the game does not
    /// zero it. In the air a push only adds: a fall goes on, and so does its fall-damage count.</summary>
    internal static class PushRule {
        /// <summary>The game zeroes a grounded player's velocity while its y is at most 0.1 m/s.</summary>
        public const float MinLift = 0.11f;

        public static V3 Next(V3 was, V3 push, bool grounded, float maxSpeed) {
            V3 v = Finite(was) ? was + push : push;
            float l = v.Length;
            if (maxSpeed > 0f && !float.IsInfinity(maxSpeed) && l > maxSpeed) v = v * (maxSpeed / l);
            if (grounded && v.Y < MinLift) v = new V3(v.X, MinLift, v.Z);
            return v;
        }

        static bool Finite(V3 v) => !float.IsNaN(v.X) && !float.IsInfinity(v.X) && !float.IsNaN(v.Y) && !float.IsInfinity(v.Y) && !float.IsNaN(v.Z) && !float.IsInfinity(v.Z);
    }
}
