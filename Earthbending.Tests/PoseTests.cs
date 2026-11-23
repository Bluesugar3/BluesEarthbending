using System;
using System.Collections.Generic;
using System.Linq;
using Earthbending;
using static Program;

// The pose engine (Poses.cs, spec 3.3): matching, the body frame, the machine's rules, poses.txt, and V3.
internal static class PoseTests {
    const double Dt = 1.0 / 90.0;   // the machine is stepped at 90 Hz, a headset's frame rate

    /// <summary>One stretch of a scripted run: a pose held (or <see cref="Move.None"/> for idle hands) for <see cref="Dur"/> seconds.</summary>
    struct Seg {
        public double Dur;
        public Move Pose;
        public bool Mirrored, Full, Stunned;
    }

    // hands hanging at the sides, palms in, fingers down: no pose of the table
    static readonly HandSample IdleR = new HandSample(new V3(0.25f, -0.95f, 0.05f), new V3(-1f, 0f, 0f), new V3(0f, -1f, 0f));
    static readonly HandSample IdleL = new HandSample(new V3(-0.25f, -0.95f, 0.05f), new V3(1f, 0f, 0f), new V3(0f, -1f, 0f));

    static PoseSpec Spec(Move m) => PoseLibrary.Defaults.First(s => s.Move == m);

    static HandSample Exact(in HandSpec h) => new HandSample(h.Pos, h.Palm, h.Fingers);

    /// <summary>The exact hands of a default pose: A on the right as written, or (mirrored) A on the left.</summary>
    static (HandSample r, HandSample l) At(Move m, bool mirrored = false) => At(Spec(m), mirrored);

    static (HandSample r, HandSample l) At(PoseSpec s, bool mirrored) {
        if (!mirrored) return (Exact(s.A), Exact(s.B));
        PoseSpec x = s.Mirrored();
        return (Exact(x.B), Exact(x.A));
    }

    static HandSample R(Move m) => At(m).r;
    static HandSample L(Move m) => At(m).l;

    static V3 Cross(V3 a, V3 b) => new V3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    /// <summary>Rodrigues: v turned by deg about the unit axis k.</summary>
    static V3 Rot(V3 v, V3 k, float deg) {
        double r = deg * Math.PI / 180.0;
        float c = (float)Math.Cos(r), s = (float)Math.Sin(r);
        k = k.Normalized;
        return v * c + Cross(k, v) * s + k * (V3.Dot(k, v) * (1f - c));
    }

    /// <summary>The hand turned about its own fingers: the palm moves by deg, the fingers stay.</summary>
    static HandSample Turned(HandSample h, float deg) => new HandSample(h.Pos, Rot(h.Palm, h.Fingers, deg), h.Fingers);

    static HandSample Shifted(HandSample h, float metres) => Moved(h, new V3(metres, 0f, 0f));

    static HandSample Moved(HandSample h, V3 by) => new HandSample(h.Pos + by, h.Palm, h.Fingers);

    /// <summary>Part of the way from one hand to another: the place along the straight line, the directions blended and
    /// made length 1 (the reviewer's sweep).</summary>
    static HandSample Toward(HandSample a, HandSample b, float u) =>
        new HandSample(a.Pos + (b.Pos - a.Pos) * u, (a.Palm + (b.Palm - a.Palm) * u).Normalized, (a.Fingers + (b.Fingers - a.Fingers) * u).Normalized);

    /// <summary>Steps the machine at 90 Hz: the hands held at <paramref name="from"/> for <paramref name="hold"/> s, moved
    /// along straight lines to <paramref name="to"/> over <paramref name="move"/> s, then held there for <paramref name="after"/>
    /// s. Returns each move fired and when.</summary>
    static List<(Move move, double at)> RunPath(PoseMachine m, (HandSample r, HandSample l) from, (HandSample r, HandSample l) to, double hold, double move, double after) {
        var fired = new List<(Move, double)>();
        int frames = (int)Math.Round((hold + move + after) / Dt);
        for (int n = 0; n < frames; n++) {
            double t = n * Dt;
            float u = t < hold ? 0f : t < hold + move ? (float)((t - hold) / move) : 1f;
            Move mv = m.Step(t, Toward(from.r, to.r, u), Toward(from.l, to.l, u), true, true, true, false, out _);
            if (mv != Move.None) fired.Add((mv, t));
        }
        return fired;
    }

    /// <summary>Two hands inside both poses at once (each hand inside both poses' hands as the two are written, or the
    /// second mirrored), from the overlap search's witness; false when there is none.</summary>
    static bool Between(PoseSpec p, PoseSpec q, bool mirrored, out (HandSample r, HandSample l) hands) {
        hands = default;
        PoseSpec o = mirrored ? q.Mirrored() : q;
        HandSpec qr = mirrored ? o.B : o.A, ql = mirrored ? o.A : o.B;
        var orients = Orientations(600, 24);
        if (!Witness(p.A, qr, orients, out HandSample r) || !Witness(p.B, ql, orients, out HandSample l)) return false;
        hands = (r, l);
        return PoseMatch.Match(p, r, l, out _, out _) && PoseMatch.Match(q, r, l, out _, out _);
    }

    static readonly Move[] Structures = { Move.Disc, Move.Ball, Move.Pillar, Move.Cube, Move.Wall };

    /// <summary>A poses.txt line with its tolerances (the part after the last |) replaced.</summary>
    static string Tail(string line, string tolerances) => line.Substring(0, line.LastIndexOf('|') + 1) + " " + tolerances;

    /// <summary>Unity's turn about +Y: positive yaw is clockwise seen from above, so yaw 90 takes +Z to +X.</summary>
    static V3 RotY(V3 v, float deg) {
        double r = deg * Math.PI / 180.0;
        float c = (float)Math.Cos(r), s = (float)Math.Sin(r);
        return new V3(v.X * c + v.Z * s, v.Y, -v.X * s + v.Z * c);
    }

    static bool NearV(V3 a, V3 b) => Near(a.X, b.X) && Near(a.Y, b.Y) && Near(a.Z, b.Z);
    static bool Same(HandSample a, HandSample b) => NearV(a.Pos, b.Pos) && NearV(a.Palm, b.Palm) && NearV(a.Fingers, b.Fingers);

    static Seg[] Hold(Move m, double seconds, bool mirrored = false) => new[] { new Seg { Dur = seconds, Pose = m, Mirrored = mirrored } };
    static Seg[] Idle(double seconds) => Hold(Move.None, seconds);
    static Seg[] Full(double seconds) => new[] { new Seg { Dur = seconds, Pose = Move.None, Full = true } };
    static Seg[] OneFrame(Move m) => Hold(m, Dt);
    static Seg[] Seq(params Seg[][] parts) => parts.SelectMany(p => p).ToArray();
    static Seg[] Stunned(Seg[] segs) => segs.Select(s => { s.Stunned = true; return s; }).ToArray();
    static Seg[] Holding(Seg[] segs) => segs.Select(s => { s.Full = true; return s; }).ToArray();   // something in a hand

    static PoseMachine M() => new PoseMachine(PoseLibrary.Defaults, PoseValues.Default);

    /// <summary>Steps the machine at 90 Hz for <paramref name="seconds"/> along the script (idle empty hands after its end)
    /// and returns the moves it fired.</summary>
    static List<Move> Run(PoseMachine m, Seg[] script, double seconds, bool grounded = true, bool natural = true) => Run(m, script, seconds, grounded, natural, out _);

    static List<Move> Run(PoseMachine m, Seg[] script, double seconds, bool grounded, bool natural, out List<bool> rightIsA) {
        var fired = new List<Move>();
        rightIsA = new List<bool>();
        int frames = (int)Math.Round(seconds / Dt);
        for (int n = 0; n < frames; n++) {
            double t = n * Dt;
            Seg seg = new Seg { Pose = Move.None };
            double start = 0;
            foreach (Seg s in script) {
                if (t < start + s.Dur) { seg = s; break; }
                start += s.Dur;
            }
            var (r, l) = seg.Pose == Move.None ? (IdleR, IdleL) : At(seg.Pose, seg.Mirrored);
            Move mv = m.Step(t, r, l, !seg.Full, grounded, natural, seg.Stunned, out bool rA);
            if (mv == Move.None) continue;
            fired.Add(mv);
            rightIsA.Add(rA);
        }
        return fired;
    }

    static bool Equal(in HandSpec a, in HandSpec b) =>
        a.Pos.X == b.Pos.X && a.Pos.Y == b.Pos.Y && a.Pos.Z == b.Pos.Z &&
        a.Palm.X == b.Palm.X && a.Palm.Y == b.Palm.Y && a.Palm.Z == b.Palm.Z &&
        a.Fingers.X == b.Fingers.X && a.Fingers.Y == b.Fingers.Y && a.Fingers.Z == b.Fingers.Z &&
        a.TolX == b.TolX && a.TolY == b.TolY && a.TolZ == b.TolZ && a.PalmDeg == b.PalmDeg && a.FingerDeg == b.FingerDeg;

    static bool Equal(PoseSpec[] a, PoseSpec[] b) {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i].Move != b[i].Move || !Equal(a[i].A, b[i].A) || !Equal(a[i].B, b[i].B)) return false;
        return true;
    }

    internal static void Run() {
        V3Checks();
        CaptureChecks();
        GameSideChecks();
        GuideChecks();
        PracticeChecks();

        // the table (spec 3.3): sixteen poses in its order, each matching its own exact hands both ways round
        PoseSpec[] defaults = PoseLibrary.Defaults;
        Check("defaults: the sixteen poses in the table's order", defaults.Length == 16 && defaults.Select(s => (int)s.Move).SequenceEqual(Enumerable.Range((int)Move.Base, 16)));
        Check("defaults: zones 0.25 sideways, 0.20 up/down, 0.30 reach, palm 60 and fingers 60 degrees, on both hands",
            defaults.All(s => new[] { s.A, s.B }.All(h => h.TolX == 0.25f && h.TolY == 0.20f && h.TolZ == 0.30f && h.PalmDeg == 60f && h.FingerDeg == 60f)));
        Check("defaults: no pose needs a straight arm (no hand more than 0.45 m ahead of the head)", defaults.All(s => s.A.Pos.Z <= 0.45f && s.B.Pos.Z <= 0.45f));
        Check("a mirrored hand keeps its zone", new[] { Spec(Move.Disc).A.Mirrored() }.All(h => h.TolX == 0.25f && h.TolY == 0.20f && h.TolZ == 0.30f && h.PalmDeg == 60f && h.FingerDeg == 60f && h.Pos.X == -0.05f));
        Check("defaults: stomp checks its palms, down and back", NearV(Spec(Move.Stomp).A.Palm, new V3(0f, -0.71f, -0.71f)) && NearV(Spec(Move.Stomp).B.Palm, new V3(0f, -0.71f, -0.71f))
            && Near(V3.Dot(Spec(Move.Stomp).A.Palm, Spec(Move.Stomp).A.Fingers), 0f));
        bool apart = true;
        foreach (PoseSpec s in defaults)
            for (int mirrored = 0; mirrored < 2; mirrored++) {
                var (r, l) = At(s, mirrored == 1);
                apart &= defaults.All(o => o.Move == s.Move || !PoseMatch.Match(o, r, l, out _, out _));
            }
        Check("no default pose's own hands strike another pose", apart);   // the region search is `-- --overlaps`
        bool own = true;
        foreach (PoseSpec s in defaults) {
            var (r, l) = At(s, false);
            var (mr, ml) = At(s, true);
            own &= PoseMatch.Match(s, r, l, out bool a1, out float s1) && a1 && s1 < 0.01f;
            own &= PoseMatch.Match(s, mr, ml, out _, out float s2) && s2 < 0.01f;
        }
        Check("every pose matches its own hands, both ways", own);

        // matching
        Check("base matches", PoseMatch.Match(Spec(Move.Base), R(Move.Base), L(Move.Base), out _, out float sBase) && sBase < 0.01f);
        Check("mirrored straight is straight, left is A", PoseMatch.Match(Spec(Move.Straight), At(Move.Straight, true).r, At(Move.Straight, true).l, out bool rA, out _) && !rA);
        Check("straight as written, right is A", PoseMatch.Match(Spec(Move.Straight), R(Move.Straight), L(Move.Straight), out bool rA2, out _) && rA2);
        Check("palm 70 off fails", !PoseMatch.Match(Spec(Move.Parry), Turned(R(Move.Parry), 70f), L(Move.Parry), out _, out _));
        Check("palm 50 off still matches", PoseMatch.Match(Spec(Move.Parry), Turned(R(Move.Parry), 50f), L(Move.Parry), out _, out float sPalm) && Near(sPalm, 50f / 60f));
        Check("fingers 70 off fails, 50 off still matches", !PoseMatch.Match(Spec(Move.Parry), new HandSample(R(Move.Parry).Pos, R(Move.Parry).Palm, Rot(R(Move.Parry).Fingers, R(Move.Parry).Palm, 70f)), L(Move.Parry), out _, out _)
            && PoseMatch.Match(Spec(Move.Parry), new HandSample(R(Move.Parry).Pos, R(Move.Parry).Palm, Rot(R(Move.Parry).Fingers, R(Move.Parry).Palm, 50f)), L(Move.Parry), out _, out float sFingers) && Near(sFingers, 50f / 60f));

        // the zones: an ellipsoid round each hand's place, 0.25 m sideways, 0.20 m up/down, 0.30 m in reach
        PoseSpec cube = Spec(Move.Cube);
        bool CubeWith(V3 by, out float sc) => PoseMatch.Match(cube, Moved(R(Move.Cube), by), L(Move.Cube), out _, out sc);
        Check("zone: 0.24 m to the side matches, 0.26 m does not", CubeWith(new V3(0.24f, 0f, 0f), out float sSide) && Near(sSide, 0.24f / 0.25f) && !CubeWith(new V3(0.26f, 0f, 0f), out _));
        Check("zone: 0.29 m in reach matches, 0.31 m does not", CubeWith(new V3(0f, 0f, 0.29f), out float sReach) && Near(sReach, 0.29f / 0.30f) && !CubeWith(new V3(0f, 0f, 0.31f), out _)
            && CubeWith(new V3(0f, 0f, -0.29f), out _) && !CubeWith(new V3(0f, 0f, -0.31f), out _));
        Check("zone: 0.19 m up matches, 0.21 m does not", CubeWith(new V3(0f, 0.19f, 0f), out _) && !CubeWith(new V3(0f, 0.21f, 0f), out _) && !CubeWith(new V3(0f, -0.21f, 0f), out _));
        Check("zone: an ellipsoid: 0.2 m to the side and 0.15 m up each match alone, together they do not (0.8^2 + 0.75^2 > 1)",
            CubeWith(new V3(0.2f, 0f, 0f), out _) && CubeWith(new V3(0f, 0.15f, 0f), out _) && !CubeWith(new V3(0.2f, 0.15f, 0f), out float sBoth)
            && Near(sBoth, (float)Math.Sqrt(0.8 * 0.8 + 0.75 * 0.75)));
        Check("zone: the place's score is the ellipsoid distance, 1 at the edge", Near(PoseMatch.Zone(cube.A, new V3(0.125f, 0.1f, 0.15f)), (float)Math.Sqrt(0.75)) && PoseMatch.Zone(cube.A, V3.Zero) == 0f);
        HandSpec flat = cube.A, broken = cube.A;
        flat.TolY = 0f;
        broken.TolZ = float.NaN;
        float sFlat = PoseMatch.Score(flat, R(Move.Cube)), sBroken = PoseMatch.Score(broken, R(Move.Cube));
        Check("zone: a half-size of 0 or NaN never matches and never scores NaN, even at the exact place",
            float.IsPositiveInfinity(sFlat) && float.IsPositiveInfinity(sBroken) && float.IsPositiveInfinity(PoseMatch.Zone(flat, V3.Zero)));

        PoseSpec stomp = Spec(Move.Stomp);
        var noPalm = new PoseSpec(Move.Stomp, new HandSpec(stomp.A.Pos, V3.Zero, stomp.A.Fingers, 0.25f, 0.2f, 0.3f, 60f, 60f), new HandSpec(stomp.B.Pos, V3.Zero, stomp.B.Fingers, 0.25f, 0.2f, 0.3f, 60f, 60f));
        var palmUp = new HandSample(R(Move.Stomp).Pos, new V3(0f, 1f, 0f), R(Move.Stomp).Fingers);
        Check("an unchecked palm is not checked", PoseMatch.Match(noPalm, palmUp, L(Move.Stomp), out _, out _) && !PoseMatch.Match(stomp, palmUp, L(Move.Stomp), out _, out _));
        Check("a sample with no palm fails a checked palm", !PoseMatch.Match(Spec(Move.Parry), new HandSample(R(Move.Parry).Pos, V3.Zero, R(Move.Parry).Fingers), L(Move.Parry), out _, out _));
        Check("a NaN hand matches nothing", !PoseMatch.Match(Spec(Move.Base), new HandSample(new V3(float.NaN, 0f, 0f), R(Move.Base).Palm, R(Move.Base).Fingers), L(Move.Base), out _, out float sNaN) && !float.IsNaN(sNaN));
        bool idleNone = defaults.All(s => !PoseMatch.Match(s, IdleR, IdleL, out _, out _));
        Check("idle hands match nothing", idleNone);

        // the body frame: origin the head, turned with the head's yaw only, positions divided by the body scale
        V3 head = new V3(3f, 1.7f, -2f);
        V3 rel = new V3(0.2f, -0.4f, 0.35f), n = new V3(0f, 1f, 0f), f = new V3(0.3f, -0.2f, 0.9f).Normalized;
        Check("frame turns with yaw", Same(BodyFrame.ToBody(head, 90f, head + RotY(rel, 90f), RotY(n, 90f), RotY(f, 90f), 1f), BodyFrame.ToBody(head, 0f, head + rel, n, f, 1f)));   // a pose struck facing east reads as the same pose facing north
        Check("frame turns with yaw, any yaw", Same(BodyFrame.ToBody(head, -137f, head + RotY(rel, -137f), RotY(n, -137f), RotY(f, -137f), 1f), BodyFrame.ToBody(head, 0f, head + rel, n, f, 1f)));
        HandSample east = BodyFrame.ToBody(V3.Zero, 90f, new V3(1f, 0f, 0f), new V3(1f, 0f, 0f), new V3(0f, 0f, 1f), 1f);
        Check("yaw 90: east is ahead, north is left", NearV(east.Pos, new V3(0f, 0f, 1f)) && NearV(east.Palm, new V3(0f, 0f, 1f)) && NearV(east.Fingers, new V3(-1f, 0f, 0f)));
        V3 up = new V3(0f, 1f, 0f), fwd = new V3(0f, 0f, 1f);
        Check("body scale", Near(BodyFrame.ToBody(V3.Zero, 0f, new V3(0, -0.9f, 0), up, fwd, 0.9f).Pos.Y, -1f));
        Check("body scale leaves directions", NearV(BodyFrame.ToBody(V3.Zero, 0f, new V3(0, -0.9f, 0), up * 3f, fwd, 0.5f).Palm, up));
        Check("body scale 0 or NaN reads as 1", Near(BodyFrame.ToBody(V3.Zero, 0f, new V3(0, -0.9f, 0), up, fwd, 0f).Pos.Y, -0.9f) && Near(BodyFrame.ToBody(V3.Zero, 0f, new V3(0, -0.9f, 0), up, fwd, float.NaN).Pos.Y, -0.9f));

        // the machine: a pose struck at its centre is deep at once (Ruling 37: the first frame there fires), rearm
        Check("hands at a pose's centre for one frame fire it once (deep at once)", Run(M(), OneFrame(Move.Jump), 0.5).SequenceEqual(new[] { Move.Jump }));
        Check("held jump fires once", Run(M(), Hold(Move.Jump, 0.3), 0.3).SequenceEqual(new[] { Move.Jump }));
        PoseValues held = PoseValues.Default;
        held.MinHoldSeconds = 0.05f;
        Check("MinHoldSeconds above 0 still asks for a hold: one frame fires nothing, a held pose fires",
            Run(new PoseMachine(PoseLibrary.Defaults, held), OneFrame(Move.Jump), 0.5).Count == 0 && Run(new PoseMachine(PoseLibrary.Defaults, held), Hold(Move.Jump, 0.3), 0.3).SequenceEqual(new[] { Move.Jump }));
        Check("held mirrored jump fires once, left is A", Run(M(), Hold(Move.Jump, 0.3, mirrored: true), 0.3, true, true, out var jumpA).SequenceEqual(new[] { Move.Jump }) && !jumpA[0]);
        Check("base fires nothing", Run(M(), Hold(Move.Base, 1.0), 1.0).Count == 0);

        // structures need the base pose first
        Check("structure needs base", Run(M(), Hold(Move.Pillar, 0.3), 0.3).Count == 0);
        Check("base then pillar", Run(M(), Seq(Hold(Move.Base, 0.2), Hold(Move.Pillar, 0.2)), 0.4).SequenceEqual(new[] { Move.Pillar }));
        Check("base then mirrored pillar", Run(M(), Seq(Hold(Move.Base, 0.2), Hold(Move.Pillar, 0.2, mirrored: true)), 0.4).SequenceEqual(new[] { Move.Pillar }));
        Check("base too long ago", Run(M(), Seq(Hold(Move.Base, 0.2), Idle(1.3), Hold(Move.Pillar, 0.2)), 1.7).Count == 0);
        Check("base just in time", Run(M(), Seq(Hold(Move.Base, 0.2), Idle(1.0), Hold(Move.Pillar, 0.2)), 1.4).SequenceEqual(new[] { Move.Pillar }));
        Check("a base passed through for one frame arms", Run(M(), Seq(OneFrame(Move.Base), Hold(Move.Pillar, 0.3)), 0.4).SequenceEqual(new[] { Move.Pillar }));
        Check("pillar twice needs base twice", Run(M(), Seq(Hold(Move.Base, 0.2), Hold(Move.Pillar, 0.2), Idle(0.3), Hold(Move.Pillar, 0.2)), 0.9).SequenceEqual(new[] { Move.Pillar }));
        Check("base, pillar, base, pillar: two pillars", Run(M(), Seq(Hold(Move.Base, 0.2), Hold(Move.Pillar, 0.2), Idle(0.3), Hold(Move.Base, 0.2), Hold(Move.Pillar, 0.2)), 1.1).SequenceEqual(new[] { Move.Pillar, Move.Pillar }));

        // armed (the base within the structure window): Jump and Dash do not fire, so moving from the base into a structure
        // never dashes on the way; a blocked entry is used up; modifiers still fire
        Check("armed: no jump", Run(M(), Seq(Hold(Move.Base, 0.2), Hold(Move.Jump, 0.3)), 0.5).Count == 0);
        Check("armed: no dash", Run(M(), Seq(Hold(Move.Base, 0.2), Hold(Move.Dash, 0.3)), 0.5).Count == 0);
        Check("armed: a straight still fires", Run(M(), Seq(Hold(Move.Base, 0.2), Hold(Move.Straight, 0.3)), 0.5).SequenceEqual(new[] { Move.Straight }));
        Check("armed: a jump blocked by the arming is used up (held on past the window, it still does not fire)", Run(M(), Seq(Hold(Move.Base, 0.2), Hold(Move.Jump, 2.0)), 2.2).Count == 0);
        Check("armed: once the window is over a jump fires", Run(M(), Seq(Hold(Move.Base, 0.2), Idle(1.3), Hold(Move.Jump, 0.3)), 1.8).SequenceEqual(new[] { Move.Jump }));
        Check("armed: a structure fired disarms, so a jump after it fires", Run(M(), Seq(Hold(Move.Base, 0.2), Hold(Move.Pillar, 0.2), Hold(Move.Jump, 0.3)), 0.7).SequenceEqual(new[] { Move.Pillar, Move.Jump }));

        StructureFlowChecks();
        MovePathChecks();
        ReviewChecks();
        TraceChecks();

        // rearm and cooldown
        Check("rearm: hold straight 1 s fires once; leave 0.15 s, re-enter fires again after cooldown", Run(M(), Seq(Hold(Move.Straight, 1.0), Idle(0.15), Hold(Move.Straight, 0.3)), 1.45).SequenceEqual(new[] { Move.Straight, Move.Straight }));
        Check("rearm: leave 0.05 s, re-enter does not fire", Run(M(), Seq(Hold(Move.Straight, 1.0), Idle(0.05), Hold(Move.Straight, 0.3)), 1.35).SequenceEqual(new[] { Move.Straight }));
        Check("cooldown: straight, leave, straight within 0.5 s fires once", Run(M(), Seq(Hold(Move.Straight, 0.15), Idle(0.15), Hold(Move.Straight, 0.15)), 0.45).SequenceEqual(new[] { Move.Straight }));
        Check("cooldown over: straight, leave 0.5 s, straight fires twice", Run(M(), Seq(Hold(Move.Straight, 0.15), Idle(0.5), Hold(Move.Straight, 0.15)), 0.8).SequenceEqual(new[] { Move.Straight, Move.Straight }));
        Check("cooldown: a pose struck in its cooldown is used up, it does not fire when the cooldown ends", Run(M(), Seq(Hold(Move.Straight, 0.15), Idle(0.15), Hold(Move.Straight, 1.0)), 1.3).SequenceEqual(new[] { Move.Straight }));
        Check("cooldowns are per move: straight then kick at once", Run(M(), Seq(Hold(Move.Straight, 0.15), Hold(Move.Kick, 0.15)), 0.3).SequenceEqual(new[] { Move.Straight, Move.Kick }));

        // hands, ground, stun
        Check("hands full fires nothing", Run(M(), Holding(Hold(Move.Jump, 0.3)), 0.3).Count == 0);
        Check("hands full resets the base", Run(M(), Seq(Hold(Move.Base, 0.2), Full(0.05), Hold(Move.Pillar, 0.2)), 0.45).Count == 0
            && Run(M(), Seq(Hold(Move.Base, 0.2), Idle(0.05), Hold(Move.Pillar, 0.2)), 0.45).SequenceEqual(new[] { Move.Pillar }));
        Check("other ground: no movement, no modifier", Run(M(), Hold(Move.Jump, 0.3), 0.3, grounded: true, natural: false).Count == 0
            && Run(M(), Hold(Move.Straight, 0.3), 0.3, grounded: true, natural: false).Count == 0
            && Run(M(), Seq(Hold(Move.Base, 0.2), Hold(Move.Pillar, 0.2)), 0.4, grounded: true, natural: false).Count == 0);
        Check("in the air: modifier yes, movement no, structure no", Run(M(), Hold(Move.Straight, 0.3), 0.3, grounded: false, natural: false).SequenceEqual(new[] { Move.Straight })
            && Run(M(), Hold(Move.Jump, 0.3), 0.3, grounded: false, natural: true).Count == 0
            && Run(M(), Seq(Hold(Move.Base, 0.2), Hold(Move.Pillar, 0.2)), 0.4, grounded: false, natural: true).Count == 0);
        Check("stunned fires nothing", Run(M(), Stunned(Hold(Move.Jump, 0.3)), 0.3).Count == 0
            && Run(M(), Stunned(Hold(Move.Straight, 0.3)), 0.3).Count == 0
            && Run(M(), Stunned(Seq(Hold(Move.Base, 0.2), Hold(Move.Pillar, 0.2))), 0.4).Count == 0);
        Check("stunned: a pose held through the stun does not fire after it", Run(M(), Seq(Stunned(Hold(Move.Jump, 0.2)), Hold(Move.Jump, 0.3)), 0.5).Count == 0);
        Check("stunned: the base still arms", Run(M(), Seq(Stunned(Hold(Move.Base, 0.2)), Hold(Move.Pillar, 0.2)), 0.4).SequenceEqual(new[] { Move.Pillar }));

        // priority: an armed structure, then modifiers, then movement; within a group the lower score
        PoseSpec pillar = Spec(Move.Pillar);
        var twin = new PoseSpec(Move.Straight, pillar.A, pillar.B);   // a modifier struck with exactly the pillar's hands, listed first
        var both = new PoseMachine(new[] { twin, Spec(Move.Base), pillar }, PoseValues.Default);
        Check("priority: armed structure beats a modifier matching the same frame", Run(both, Seq(Hold(Move.Base, 0.2), Hold(Move.Pillar, 0.5)), 0.7).SequenceEqual(new[] { Move.Pillar }));
        Check("priority: unarmed, the modifier fires", Run(new PoseMachine(new[] { twin, Spec(Move.Base), pillar }, PoseValues.Default), Hold(Move.Pillar, 0.3), 0.3).SequenceEqual(new[] { Move.Straight }));
        var farther = new PoseSpec(Move.Kick, new HandSpec(pillar.A.Pos + new V3(0.05f, 0f, 0f), pillar.A.Palm, pillar.A.Fingers, 0.25f, 0.2f, 0.3f, 60f, 60f), pillar.B);
        Check("priority: within a group the lower score", Run(new PoseMachine(new[] { farther, twin }, PoseValues.Default), Hold(Move.Pillar, 0.3), 0.3).SequenceEqual(new[] { Move.Straight }));
        var jumpTwin = new PoseSpec(Move.Jump, pillar.A, pillar.B);
        Check("priority: a modifier beats movement", Run(new PoseMachine(new[] { jumpTwin, twin }, PoseValues.Default), Hold(Move.Pillar, 0.3), 0.3).SequenceEqual(new[] { Move.Straight }));

        // kinds and numbers
        Check("kinds", PoseMachine.IsStructure(Move.Disc) && PoseMachine.IsStructure(Move.Wall) && !PoseMachine.IsStructure(Move.Base) && !PoseMachine.IsStructure(Move.Straight)
            && PoseMachine.IsModifier(Move.Straight) && PoseMachine.IsModifier(Move.Explode) && !PoseMachine.IsModifier(Move.Jump) && !PoseMachine.IsModifier(Move.Wall)
            && PoseMachine.IsMovement(Move.Jump) && PoseMachine.IsMovement(Move.Dash) && !PoseMachine.IsMovement(Move.Base) && !PoseMachine.IsMovement(Move.None));
        PoseValues pv = PoseValues.Default;
        Check("values: the spec's numbers (no hold)", pv.MinHoldSeconds == 0f && Near(pv.RearmSeconds, 0.1f) && Near(pv.StructureWindowSeconds, 1.2f) && Near(pv.MoveCooldownSeconds, 0.6f));
        Check("cooldowns by move", new[] { Move.Disc, Move.Ball, Move.Pillar, Move.Cube, Move.Wall }.All(m => Near(pv.CooldownOf(m), 0.3f))
            && Near(pv.CooldownOf(Move.Straight), 0.5f) && Near(pv.CooldownOf(Move.Kick), 0.3f) && Near(pv.CooldownOf(Move.Stomp), 0.3f) && Near(pv.CooldownOf(Move.Uppercut), 0.5f)
            && Near(pv.CooldownOf(Move.Parry), 0.3f) && Near(pv.CooldownOf(Move.Hold), 0.8f) && Near(pv.CooldownOf(Move.Flick), 0.8f) && Near(pv.CooldownOf(Move.Explode), 0.3f)
            && Near(pv.CooldownOf(Move.Jump), 0.6f) && Near(pv.CooldownOf(Move.Dash), 0.6f) && pv.CooldownOf(Move.Base) == 0f);
        var bad = new PoseValues { MinHoldSeconds = float.NaN, RearmSeconds = -1f, StructureWindowSeconds = float.PositiveInfinity, MoveCooldownSeconds = 0.25f, Cooldown = new float[] { 0f, 0f, float.NaN } };
        PoseValues ok = bad.Clamped();
        Check("values: NaN, negative and infinite fall back to the defaults", ok.MinHoldSeconds == 0f && Near(ok.RearmSeconds, 0.1f) && Near(ok.StructureWindowSeconds, 1.2f) && Near(ok.MoveCooldownSeconds, 0.25f)
            && Near(ok.CooldownOf(Move.Disc), 0.3f) && Near(ok.CooldownOf(Move.Hold), 0.8f) && Near(ok.CooldownOf(Move.Jump), 0.25f) && new PoseValues().Clamped().Cooldown!.Length == (int)Move.Dash + 1);

        // poses.txt
        string[] text = PoseLibrary.Format(defaults);
        PoseSpec[] back = PoseLibrary.Parse(text, out List<string> roundErrors);
        string wallLine = text.First(t => t.StartsWith("Wall "));
        PoseSpec[] some = PoseLibrary.Parse(new[] {
            "# my poses",
            "",
            wallLine + "   # mine",
            "Nope | 0 0 0  0 0 0  0 0 0 | 0 0 0  0 0 0  0 0 0 | 0.25 0.2 0.3 60 60",
            "Base | 1 2 3 | 0 0 0  0 0 0  0 0 0 | 0.25 0.2 0.3 60 60",
            "Jump | 0 0 0  0 0 0  0 0 x | 0 0 0  0 0 0  0 0 0 | 0.25 0.2 0.3 60 60",
            "Dash | 0 0 0  0 0 0  0 0 0 | 0 0 0  0 0 0  0 0 0 | 0.25 0 0.3 60 60",
            "Cube | 0 0 0  0 0 0  0 0 0 | 0 0 0  0 0 0  0 0 0",
            "Disc | 0 0 0  0 0 0  0 0 0 | 0 0 0  0 0 0  0 0 0 | 0.25 0.2 0.3 60",
            "Ball | 0 0 0  0 0 0  0 0 0 | 0 0 0  0 0 0  0 0 0 | 0 40 45",
        }, out List<string> errors);
        Check("parse: round-trip of Defaults through text equals Defaults", roundErrors.Count == 0 && Equal(back, defaults));
        Check("parse: every default pose alone round-trips through Format and Parse", defaults.All(s => {
            PoseSpec[] one = PoseLibrary.Parse(PoseLibrary.Format(new[] { s }), out List<string> e1);
            return e1.Count == 0 && one.Length == 1 && one[0].Move == s.Move && Equal(one[0].A, s.A) && Equal(one[0].B, s.B);
        }));
        Check("format: the tail is the zone's three half-sizes, then the palm's and the fingers' cones", wallLine.EndsWith("| 0.25 0.2 0.3 60 60"));
        Check("parse: a bad line is skipped and named", some.Length == 1 && some[0].Move == Move.Wall && Equal(some[0].A, Spec(Move.Wall).A)
            && errors.Count == 7 && errors[0].Contains("line 4") && errors[1].Contains("line 5") && errors[2].Contains("line 6") && errors[3].Contains("line 7") && errors[4].Contains("line 8")
            && errors[5].Contains("line 9") && errors[6].Contains("line 10"));
        PoseSpec[] twice = PoseLibrary.Parse(new[] { wallLine, Tail(wallLine, "0.3 0.3 0.3 40 45") }, out List<string> twiceErrors);
        Check("parse: a repeated pose keeps the first and names the repeat", twice.Length == 1 && twice[0].A.TolX == 0.25f && twiceErrors.Count == 1 && twiceErrors[0].Contains("line 2"));
        Check("parse: names in any case, numbers with a dot", PoseLibrary.Parse(new[] { "wALL | 0.2 0.1 0.3  0 1 0  0 0 1 | -0.2 0.1 0.3  0 1 0  0 0 1 | 0.25 0.2 0.3 60 60" }, out _).Length == 1);

        // the old tail (posTol palmDeg fingerDeg: one round tolerance) still reads, as all three half-sizes; it is written back
        // in the new form, which reads back the same
        PoseSpec[] old = PoseLibrary.Parse(new[] { Tail(wallLine, "0.18 40 45") }, out List<string> oldErrors);
        bool oldOk = oldErrors.Count == 0 && old.Length == 1 && new[] { old[0].A, old[0].B }.All(h => h.TolX == 0.18f && h.TolY == 0.18f && h.TolZ == 0.18f && h.PalmDeg == 40f && h.FingerDeg == 45f)
            && NearV(old[0].A.Pos, Spec(Move.Wall).A.Pos) && NearV(old[0].B.Palm, Spec(Move.Wall).B.Palm);
        string[] oldWritten = PoseLibrary.Format(old);
        PoseSpec[] oldBack = PoseLibrary.Parse(oldWritten, out List<string> oldBackErrors);
        Check("parse: the old 3-number tail reads as a round zone", oldOk);
        Check("parse: an old pose is written with the new tail and reads back the same", oldWritten[2].EndsWith("| 0.18 0.18 0.18 40 45") && oldBackErrors.Count == 0 && Equal(oldBack, old));
        PoseSpec[] mixed = PoseLibrary.Parse(new[] { Tail(wallLine, "0.18 40 45"), Tail(PoseLibrary.Format(new[] { Spec(Move.Base) })[2], "0.3 0.25 0.4 50 55") }, out List<string> mixedErrors);
        Check("parse: old and new tails in one file", mixedErrors.Count == 0 && mixed.Length == 2 && mixed[0].A.TolY == 0.18f
            && mixed[1].A.TolX == 0.3f && mixed[1].B.TolY == 0.25f && mixed[1].A.TolZ == 0.4f && mixed[1].B.PalmDeg == 50f && mixed[1].A.FingerDeg == 55f);
    }

    // Rulings 33 and 34 (fix round 1): an armed structure fires at its deepest point, not on entry, so the edge of one
    // structure on the way to another fires nothing; and the base arms only from a frame where no structure matches
    static void StructureFlowChecks() {
        var (baseR, baseL) = At(Move.Base);
        PoseValues pv = PoseValues.Default;
        Check("structures: the numbers (deep at a score of 0.5, or both hands within 1 cm for 0.06 s)",
            Near(pv.StructureDeepScore, 0.5f) && Near(pv.StructureStillMetres, 0.01f) && Near(pv.StructureStillSeconds, 0.06f));

        // the reviewer's acceptance: a straight line from Base's hands to each structure's, both ways round, at three speeds
        foreach (double seconds in new[] { 0.15, 0.3, 0.6 }) {
            var wrong = new List<string>();
            foreach (Move s in Structures)
                for (int mirrored = 0; mirrored < 2; mirrored++) {
                    List<(Move move, double at)> got = RunPath(M(), (baseR, baseL), At(s, mirrored == 1), 0.2, seconds, 0.3);
                    bool ok = got.Count == 1 && got[0].move == s && got[0].at <= 0.2 + seconds + 2 * Dt;
                    if (!ok) wrong.Add($"{s}{(mirrored == 1 ? " mirrored" : "")}: [{string.Join(", ", got.Select(g => $"{g.move} at {g.at - 0.2:0.000} s"))}]");
                }
            Check($"structures: Base to each structure in a straight line over {seconds} s fires exactly that structure, by the time the hands reach it, either hand as A"
                + (wrong.Count > 0 ? " (wrong: " + string.Join("; ", wrong) + ")" : ""), wrong.Count == 0);
        }
        List<(Move move, double at)> cube = RunPath(M(), (baseR, baseL), At(Move.Cube), 0.2, 0.3, 0.3);
        Check("structures: Base to Cube crosses Ball's edge first (score 0.75 at best) and fires only Cube", cube.Count == 1 && cube[0].move == Move.Cube);
        // the reviewer's rest: arms at the sides, 1 m under the head
        var rest = (new HandSample(new V3(0.22f, -1.0f, 0.05f), new V3(-1f, 0f, 0f), new V3(0f, -1f, 0f)), new HandSample(new V3(-0.22f, -1.0f, 0.05f), new V3(1f, 0f, 0f), new V3(0f, -1f, 0f)));
        // (review I-3: Base arms only on arrival, so the hands stop at Base on the way; the raise in one motion, which passes
        // Base at 0.75 at best, arms nothing and so summons nothing)
        var wallWrong = new List<string>();
        foreach (double speed in new[] { 0.5, 1.0, 2.5 }) {
            List<PathFire> wall = Feed(M(), PathHolds(speed, (rest, 0.2), (At(Move.Base), 0.1), (At(Move.Wall), 0.3)));
            if (!(wall.Count == 1 && wall[0].Move == Move.Wall)) wallWrong.Add($"{Inv(speed)} m/s: {Fires(wall)}");
        }
        Check("structures: raising the hands from rest to Wall, stopping at Base on the way (then Ball's edge), fires Wall, never Ball, at 0.5 to 2.5 m/s"
            + (wallWrong.Count > 0 ? " (wrong: " + string.Join("; ", wallWrong) + ")" : ""), wallWrong.Count == 0);
        var oneMotion = new List<string>();
        float baseBest = float.PositiveInfinity;
        foreach (double seconds in new[] { 0.3, 0.6, 1.0, 1.5 }) {
            baseBest = Math.Min(baseBest, Enumerable.Range(0, 200).Select(k => Toward(rest.Item1, At(Move.Wall).r, k / 199f)).Zip(Enumerable.Range(0, 200).Select(k => Toward(rest.Item2, At(Move.Wall).l, k / 199f)), (r, l) => PoseMatch.Match(Spec(Move.Base), r, l, out _, out float sc) ? sc : float.PositiveInfinity).Min());
            List<(Move move, double at)> wall = RunPath(M(), rest, At(Move.Wall), 0.2, seconds, 0.3);
            if (wall.Count != 0) oneMotion.Add($"{seconds} s: [{string.Join(", ", wall.Select(g => g.move))}]");
        }
        Check($"structures (I-3): raising the hands from rest to Wall in one motion passes Base at {baseBest:0.00} at best, which arms nothing: nothing fires, at 0.3 to 1.5 s"
            + (oneMotion.Count > 0 ? " (fired: " + string.Join("; ", oneMotion) + ")" : ""), baseBest > PoseValues.Default.StructureDeepScore && oneMotion.Count == 0);
        List<(Move move, double at)> direct = RunPath(M(), (baseR, baseL), At(Move.Disc), 0.2, 1e-9, 0.3);
        double firstThere = Enumerable.Range(0, 100).Select(n => n * Dt).First(t => !(t < 0.2 + 1e-9));
        Check("structures: struck straight at its centre, a structure fires on the first frame there", direct.Count == 1 && direct[0].move == Move.Disc && direct[0].at == firstThere);

        // a structure struck loosely (never deeper than 0.5) fires once the hands stop in it
        (HandSample r, HandSample l) disc = At(Move.Disc), halfway = (Toward(baseR, disc.r, 0.5f), Toward(baseL, disc.l, 0.5f));
        PoseMatch.Match(Spec(Move.Disc), halfway.r, halfway.l, out _, out float halfScore);
        List<(Move move, double at)> loose = RunPath(M(), (baseR, baseL), halfway, 0.2, 0.15, 0.5);
        Check($"structures: half way to Disc (score {halfScore:0.00}, not deep) and held still, Disc fires once the hands have stopped, within 0.06 s and a frame or two",
            halfScore > 0.5f && halfScore <= 1f && loose.Count == 1 && loose[0].move == Move.Disc && loose[0].at >= 0.35 + 0.06 - 1e-6 && loose[0].at <= 0.35 + 0.06 + 3 * Dt);
        List<(Move move, double at)> slow = RunPath(M(), (baseR, baseL), disc, 0.2, 1.5, 0.3);
        Check("structures: a slow, steady move into Disc fires Disc once", slow.Count == 1 && slow[0].move == Move.Disc);

        // Ruling 34: a pose in a place both Base and a structure reach is no base, so one loose pose there never summons
        foreach (Move s in new[] { Move.Disc, Move.Ball })
            for (int mirrored = 0; mirrored < 2; mirrored++) {
                string name = s + (mirrored == 1 ? " mirrored" : "");
                bool found = Between(Spec(Move.Base), Spec(s), mirrored == 1, out (HandSample r, HandSample l) both);
                var idleHands = (IdleR, IdleL);
                List<(Move move, double at)> alone = found ? RunPath(M(), idleHands, both, 0.2, Dt, 1.0) : new List<(Move, double)>();
                Check($"base: held still for 1 s in a place inside both Base and {name} summons nothing (found the place: {found})",
                    found && !alone.Any(g => PoseMachine.IsStructure(g.move)));
                // the same place after a clean base is a loose structure, struck and held: the best-scored structure there fires
                Move bestThere = Structures.OrderBy(x => PoseMatch.Match(Spec(x), both.r, both.l, out _, out float sc) ? sc : float.PositiveInfinity).First();
                List<(Move move, double at)> after = found ? RunPath(M(), (baseR, baseL), both, 0.2, 0.15, 1.0) : new List<(Move, double)>();
                Check($"base: after a clean base, the same place fires the structure best struck there ({bestThere}) once",
                    found && after.Count(g => PoseMachine.IsStructure(g.move)) == 1 && after.First(g => PoseMachine.IsStructure(g.move)).move == bestThere);
            }
    }

    // ----- Ruling 37 (task 17a): every move fires on arrival (deep, or with the hands at rest in it), never on a brush -----

    /// <summary>One frame of a hand path: its time and both hands.</summary>
    internal struct PathFrame {
        public double T;
        public HandSample R, L;
    }

    /// <summary>A move the machine fired on a path.</summary>
    internal struct PathFire {
        public Move Move;
        public double At;
        public float Score;
        public bool RightIsA;
    }

    /// <summary>The seconds a path holds its hands still at each of its points.</summary>
    const double PathHold = 0.3;

    // the hands at rest: hanging, the right at (0.25, -0.75, 0.05), palm toward the body (-X), fingers down; the left its mirror
    static readonly (HandSample r, HandSample l) RestHands = (
        new HandSample(new V3(0.25f, -0.75f, 0.05f), new V3(-1f, 0f, 0f), new V3(0f, -1f, 0f)),
        new HandSample(new V3(-0.25f, -0.75f, 0.05f), new V3(1f, 0f, 0f), new V3(0f, -1f, 0f)));

    // the eight modifiers and the two movements
    static readonly Move[] MoveList = { Move.Straight, Move.Kick, Move.Stomp, Move.Uppercut, Move.Parry, Move.Hold, Move.Flick, Move.Explode, Move.Jump, Move.Dash };

    /// <summary>The hands along straight lines through <paramref name="points"/> at 90 frames a second: still at each point for
    /// <see cref="PathHold"/> s (the first frame of the hold is the point itself), and between two points the place moved along
    /// the straight line and the hand turned evenly (one turn about one axis), so that the hand that goes further moves at
    /// <paramref name="speed"/> m/s and both arrive together.</summary>
    internal static List<PathFrame> PathFrames(double speed, params (HandSample r, HandSample l)[] points) =>
        PathHolds(speed, points.Select(p => (p, PathHold)).ToArray());

    /// <summary>As <see cref="PathFrames"/>, with each point's own hold in seconds (0: the hands pass through it without
    /// stopping; the point itself is still one frame of the path).</summary>
    internal static List<PathFrame> PathHolds(double speed, params ((HandSample r, HandSample l) at, double hold)[] points) {
        var frames = new List<PathFrame>();
        void Add(HandSample r, HandSample l) => frames.Add(new PathFrame { T = frames.Count * Dt, R = r, L = l });
        for (int i = 0; i < points.Length; i++) {
            if (i > 0) {
                var (a, b) = (points[i - 1].at, points[i].at);
                double far = Math.Max((b.r.Pos - a.r.Pos).Length, (b.l.Pos - a.l.Pos).Length);
                int n = Math.Max(1, (int)Math.Ceiling(far / speed / Dt));
                for (int k = 1; k < n; k++) Add(Turn(a.r, b.r, (float)k / n), Turn(a.l, b.l, (float)k / n));
            }
            int hold = Math.Max(1, (int)Math.Round(points[i].hold / Dt));
            for (int k = 0; k < hold; k++) Add(points[i].at.r, points[i].at.l);
        }
        return frames;
    }

    /// <summary>Steps a machine through a path (hands empty, grounded on natural ground, never stunned) and returns every fire.</summary>
    internal static List<PathFire> Feed(PoseMachine m, List<PathFrame> frames, bool grounded = true, bool natural = true) {
        var fired = new List<PathFire>();
        foreach (PathFrame f in frames) {
            Move mv = m.Step(f.T, f.R, f.L, true, grounded, natural, false, out bool rA);
            if (mv != Move.None) fired.Add(new PathFire { Move = mv, At = f.T, Score = m.LastScore, RightIsA = rA });
        }
        return fired;
    }

    /// <summary>Part of the way from one hand to another: the place along the straight line, and the hand turned by that part
    /// of the one turn that takes a's palm and fingers to b's.</summary>
    static HandSample Turn(HandSample a, HandSample b, float u) {
        V3 pos = a.Pos + (b.Pos - a.Pos) * u;
        double[,] fa = FrameOf(a), fb = FrameOf(b), r = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                for (int k = 0; k < 3; k++) r[i, j] += fb[i, k] * fa[j, k];   // R = Fb Fa^T
        if (!AxisAngle(r, out V3 axis, out double angle)) return new HandSample(pos, a.Palm, a.Fingers);
        float deg = (float)(angle * u * 180.0 / Math.PI);
        return new HandSample(pos, Rot(a.Palm, axis, deg).Normalized, Rot(a.Fingers, axis, deg).Normalized);
    }

    /// <summary>A hand's frame as columns: the palm, the fingers made square to it, and their cross product.</summary>
    static double[,] FrameOf(HandSample h) {
        V3 n = h.Palm.Normalized, f = (h.Fingers - n * V3.Dot(h.Fingers, n)).Normalized, s = Cross(n, f);
        return new double[,] { { n.X, f.X, s.X }, { n.Y, f.Y, s.Y }, { n.Z, f.Z, s.Z } };
    }

    /// <summary>The axis and angle (radians) of a rotation matrix; false for no turn.</summary>
    static bool AxisAngle(double[,] r, out V3 axis, out double angle) {
        axis = V3.Zero;
        double c = Math.Max(-1.0, Math.Min(1.0, (r[0, 0] + r[1, 1] + r[2, 2] - 1.0) / 2.0));
        angle = Math.Acos(c);
        if (angle < 1e-7) return false;
        if (Math.PI - angle > 1e-4) {
            double s2 = 2.0 * Math.Sin(angle);
            axis = new V3((float)((r[2, 1] - r[1, 2]) / s2), (float)((r[0, 2] - r[2, 0]) / s2), (float)((r[1, 0] - r[0, 1]) / s2)).Normalized;
            return true;
        }
        // half a turn: R = 2kk^T - I, read from the largest diagonal
        int i = r[0, 0] >= r[1, 1] && r[0, 0] >= r[2, 2] ? 0 : r[1, 1] >= r[2, 2] ? 1 : 2;
        var k = new double[3];
        k[i] = Math.Sqrt(Math.Max(0.0, (r[i, i] + 1.0) / 2.0));
        for (int j = 0; j < 3; j++) if (j != i) k[j] = (r[i, j] + r[j, i]) / (4.0 * k[i]);
        axis = new V3((float)k[0], (float)k[1], (float)k[2]).Normalized;
        return true;
    }

    static string Hand(bool mirrored) => mirrored ? "A left" : "A right";
    static string Inv(double d) => d.ToString(System.Globalization.CultureInfo.InvariantCulture);
    static string Fires(List<PathFire> got) => "[" + string.Join(", ", got.Select(g => $"{g.Move} {g.Score:0.00}")) + "]";

    /// <summary>Every path of the task 17a tables that did not fire exactly what it should: its key and what it fired. Rest to
    /// each modifier and movement (either hand as A, 1 and 2.5 m/s): exactly that move, once. Rest to Base to each structure:
    /// exactly that structure, once. From each modifier or movement, at rest in it, to every other one (each with either hand
    /// as A, at 1 and 2.5 m/s; a path starts at rest in the first): exactly the two, in that order.</summary>
    internal static List<(string key, string got)> PathTable() {
        var wrong = new List<(string, string)>();
        foreach (double speed in new[] { 1.0, 2.5 }) {
            foreach (Move m in MoveList)
                for (int mi = 0; mi < 2; mi++) {
                    List<PathFire> got = Feed(M(), PathFrames(speed, RestHands, At(m, mi == 1)));
                    if (!(got.Count == 1 && got[0].Move == m))
                        wrong.Add(($"rest -> {m} ({Hand(mi == 1)}) at {Inv(speed)} m/s", Fires(got)));
                }
            foreach (Move s in Structures)
                for (int mi = 0; mi < 2; mi++) {
                    List<PathFire> got = Feed(M(), PathFrames(speed, RestHands, At(Move.Base), At(s, mi == 1)));
                    if (!(got.Count == 1 && got[0].Move == s))
                        wrong.Add(($"rest -> Base -> {s} ({Hand(mi == 1)}) at {Inv(speed)} m/s", Fires(got)));
                }
            foreach (Move a in MoveList)
                foreach (Move b in MoveList) {
                    if (a == b) continue;
                    for (int ma = 0; ma < 2; ma++)
                        for (int mb = 0; mb < 2; mb++) {
                            List<PathFire> got = Feed(M(), PathFrames(speed, At(a, ma == 1), At(b, mb == 1)));
                            if (!(got.Count == 2 && got[0].Move == a && got[1].Move == b))
                                wrong.Add(($"{a} ({Hand(ma == 1)}) -> {b} ({Hand(mb == 1)}) at {Inv(speed)} m/s", Fires(got)));
                        }
                }
        }
        return wrong;
    }

    /// <summary>The paths of <see cref="PathTable"/> that fail with the default poses because their straight line runs deep
    /// through a third pose (task 17a: listed, not hidden). A change that fixes one or adds one shows up in the test.</summary>
    // None left. Until the review fixes (I-3) these were Jump to Dash and Dash to Jump with the other hand as A, at 1 and 2.5
    // m/s: crossing sides, the hands brushed Base's edge (0.96) clean for 1-3 frames, which armed the structures and used up
    // the second move. Base now arms only deep or at rest.
    static readonly string[] KnownPathFailures = {
    };

    static HandSample H(float px, float py, float pz, float nx, float ny, float nz, float fx, float fy, float fz) =>
        new HandSample(new V3(px, py, pz), new V3(nx, ny, nz).Normalized, new V3(fx, fy, fz).Normalized);

    /// <summary>The best score of a move's pose for these hands (infinity: no match).</summary>
    static float ScoreOf(Move m, (HandSample r, HandSample l) h) => PoseMatch.Match(Spec(m), h.r, h.l, out _, out float sc) ? sc : float.PositiveInfinity;

    /// <summary>The hands part of the way from one pose's exact hands to another's (place along the line, turned evenly).</summary>
    static (HandSample r, HandSample l) Between((HandSample r, HandSample l) a, (HandSample r, HandSample l) b, float u) => (Turn(a.r, b.r, u), Turn(a.l, b.l, u));

    // the review of task 17a: I-1 (no second fire at an edge once the hands settle), I-2 (a modifier never eats an armed
    // structure), I-3 (Base arms on arrival: deep or at rest), M-6 (a line through every move's centre fires it)
    static void ReviewChecks() {
        PoseValues pv = PoseValues.Default;

        // I-1, the review's case: from rest at 1 m/s into Straight (0.59 there) where Uppercut's zone reaches, held 0.6 s
        var i1 = (H(0.104f, -0.220f, 0.275f, -0.838f, 0.002f, -0.546f, 0.003f, 1f, -0.001f), H(-0.201f, -0.279f, 0.395f, 0.503f, -0.827f, 0.251f, 0.255f, 0.42f, 0.871f));
        List<PathFire> i1Fires = Feed(M(), PathHolds(1.0, (RestHands, 0.2), (i1, 0.6)));
        Check($"I-1: from rest into Straight where Uppercut reaches, held: Straight fires, Uppercut never ({Fires(i1Fires)})", i1Fires.Count == 1 && i1Fires[0].Move == Move.Straight);

        // I-1's sweep: every point between two moves' centres (either hand as A) where one is deep and the other matches, and
        // the same around each move's centre jittered up to 0.14 m / 30 degrees: from rest, held 0.6 s, never two fires
        int points = 0;
        var doubles = new List<string>();
        void Try((HandSample r, HandSample l) at, string name) {
            var deep = MoveList.Where(m => ScoreOf(m, at) <= pv.MoveDeepScore).ToList();
            if (deep.Count == 0 || !MoveList.Any(m => !deep.Contains(m) && ScoreOf(m, at) <= 1f)) return;
            points++;
            foreach (double speed in new[] { 1.0, 2.5 }) {
                List<PathFire> got = Feed(M(), PathHolds(speed, (RestHands, 0.2), (at, 0.6)));
                if (got.Count > 1) doubles.Add($"{name} at {Inv(speed)} m/s {Fires(got)}");
            }
        }
        foreach (Move a in MoveList)
            foreach (Move b in MoveList) {
                if (a == b) continue;
                for (int ma = 0; ma < 2; ma++)
                    for (int mb = 0; mb < 2; mb++)
                        for (int k = 1; k < 40; k++) Try(Between(At(a, ma == 1), At(b, mb == 1), k / 40f), $"{a}->{b} {k}/40");
            }
        var rng = new Random(17);
        V3 Jitter(V3 v, float metres) => v + new V3((float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1)).Normalized * (float)(rng.NextDouble() * metres);
        HandSample Loose(HandSample h) {
            V3 axis = new V3((float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1));
            float deg = (float)(rng.NextDouble() * 30.0);
            return new HandSample(Jitter(h.Pos, 0.14f), Rot(h.Palm, axis, deg).Normalized, Rot(h.Fingers, axis, deg).Normalized);
        }
        foreach (Move m in MoveList)
            for (int mi = 0; mi < 2; mi++)
                for (int n = 0; n < 300; n++) {
                    var (r, l) = At(m, mi == 1);
                    Try((Loose(r), Loose(l)), $"{m} loose #{n}");
                }
        Check($"I-1: {points} rest points deep in one move and inside another, from rest at 1 and 2.5 m/s and held: never two fires"
            + (doubles.Count > 0 ? " (" + doubles.Count + ": " + string.Join("; ", doubles.Take(6)) + ")" : ""), points >= 100 && doubles.Count == 0);

        // I-1: two at one rest, both in their outer part (0.71 and 0.75): the better one fires alone
        PoseSpec kick = Spec(Move.Kick);
        var far = new PoseSpec(Move.Straight, new HandSpec(kick.A.Pos + new V3(0.365f, 0f, 0f), kick.A.Palm, kick.A.Fingers, 0.25f, 0.2f, 0.3f, 60f, 60f),
            new HandSpec(kick.B.Pos + new V3(0.365f, 0f, 0f), kick.B.Palm, kick.B.Fingers, 0.25f, 0.2f, 0.3f, 60f, 60f));
        var outer = (Moved(R(Move.Kick), new V3(0.1775f, 0f, 0f)), Moved(L(Move.Kick), new V3(0.1775f, 0f, 0f)));
        PoseMatch.Match(kick, outer.Item1, outer.Item2, out _, out float o1);
        PoseMatch.Match(far, outer.Item1, outer.Item2, out _, out float o2);
        List<PathFire> outerFires = Feed(new PoseMachine(new[] { kick, far }, pv), PathHolds(1.0, (RestHands, 0.2), (outer, 1.0)));
        Check($"I-1: two at one rest in their outer parts ({o1:0.00}, {o2:0.00}): the better fires alone", o1 > 0.7f && o2 > o1 && o2 < 0.8f && outerFires.Count == 1 && outerFires[0].Move == Move.Kick);
        // I-1: a pose entered after the fire, still in the one that fired, fires only when deep or better than that one
        var later = new PoseMachine(new[] { kick, far }, pv);
        List<PathFire> slide = Feed(later, PathHolds(1.0, (At(Move.Kick), 0.3), (outer, 1.0)));
        Check("I-1: sliding from Kick's centre to where a neighbour's edge reaches (0.75 there, Kick 0.71): only Kick", slide.Count == 1 && slide[0].Move == Move.Kick);

        // I-2, the review's case: Base, then at 1.5 m/s through a point where Straight is 0.60 and Ball 0.99, on to Ball's
        // centre: Ball fires
        var i2 = (H(0.190f, -0.225f, 0.242f, -0.996f, -0.07f, -0.054f, -0.087f, 0.855f, 0.511f), H(-0.124f, -0.300f, 0.413f, 0.577f, -0.812f, 0.092f, 0.004f, 0.115f, 0.993f));
        List<PathFire> i2Fires = Feed(M(), PathHolds(1.5, (RestHands, 0.2), (At(Move.Base), 0.2), (i2, 0.0), (At(Move.Ball), 0.4)));
        Check($"I-2: Base, through Straight's deep part and Ball's edge, to Ball: Ball fires ({Fires(i2Fires)})", i2Fires.Count(f => f.Move == Move.Ball) == 1 && i2Fires[i2Fires.Count - 1].Move == Move.Ball);
        // I-2's sweep: Base, then through every point between a modifier's centre and a structure's where the modifier is deep
        // and the structure matches, to the structure's centre: the structure fires once, last
        int i2Points = 0;
        var lost = new List<string>();
        foreach (Move s in Structures)
            foreach (Move mod in MoveList.Where(PoseMachine.IsModifier))
                for (int ms = 0; ms < 2; ms++)
                    for (int mm = 0; mm < 2; mm++)
                        for (int k = 1; k < 20; k++) {
                            var at = Between(At(mod, mm == 1), At(s, ms == 1), k / 20f);
                            if (!(ScoreOf(mod, at) <= pv.MoveDeepScore && ScoreOf(s, at) <= 1f)) continue;
                            i2Points++;
                            foreach (double speed in new[] { 1.0, 2.5 }) {
                                List<PathFire> got = Feed(M(), PathHolds(speed, (At(Move.Base), 0.2), (at, 0.0), (At(s, ms == 1), 0.4)));
                                if (!(got.Count(f => f.Move == s) == 1 && got[got.Count - 1].Move == s)) lost.Add($"{mod} then {s} {k}/20 at {Inv(speed)} m/s {Fires(got)}");
                            }
                        }
        Check($"I-2: {i2Points} points where a modifier is deep and a structure matches, passed on the way from Base to the structure: it always fires"
            + (lost.Count > 0 ? " (" + lost.Count + ": " + string.Join("; ", lost.Take(6)) + ")" : ""), i2Points > 20 && lost.Count == 0);

        // I-3: Base arms on arrival: a line through its centre at 2.5 m/s without stopping arms (deep); a brush of its edge
        // (0.96 at best) arms nothing
        var (br, bl) = At(Move.Base);
        V3 back = new V3(0f, 0f, -0.45f), ahead = new V3(0f, 0f, 0.45f), edge = new V3(0f, 0.192f, 0f);
        (HandSample, HandSample) BaseOff(V3 by) => (Moved(br, by), Moved(bl, by));
        bool ArmedAfter(List<PathFrame> path, out float best) {
            var m = M();
            best = path.Min(f => PoseMatch.Match(Spec(Move.Base), f.R, f.L, out _, out float sc) ? sc : float.PositiveInfinity);
            Feed(m, path);
            return m.Armed(path[path.Count - 1].T);
        }
        bool sweepArms = ArmedAfter(PathHolds(2.5, (BaseOff(back), 0.0), (BaseOff(ahead), 0.1)), out float sweepBest);
        bool brushArms = ArmedAfter(PathHolds(2.5, (BaseOff(edge + back), 0.0), (BaseOff(edge + ahead), 0.1)), out float brushBest);
        Check($"I-3: a sweep through Base's centre at 2.5 m/s arms (best {sweepBest:0.00})", sweepArms && sweepBest <= pv.StructureDeepScore);
        Check($"I-3: a brush of Base's edge at 2.5 m/s (best {brushBest:0.00}) arms nothing", !brushArms && brushBest > 0.9f && brushBest <= 1f);
        bool restArms = ArmedAfter(PathHolds(1.0, (BaseOff(edge + back), 0.0), (BaseOff(edge), 0.3)), out _);
        Check("I-3: hands that stop in Base's edge arm (at rest)", restArms);
        Check("I-3: the review's Jump to Dash with the other hand as A now fires both", Feed(M(), PathFrames(2.5, At(Move.Jump), At(Move.Dash, true))).Select(f => f.Move).SequenceEqual(new[] { Move.Jump, Move.Dash }));

        // M-6: a line through every move's centre, every pose running, both ways round, along each axis, at 1, 2.5 and 5 m/s,
        // fires exactly that move
        var missed = new List<string>();
        foreach (Move m in MoveList)
            for (int mi = 0; mi < 2; mi++)
                foreach (V3 axis in new[] { new V3(0.6f, 0f, 0f), new V3(0f, 0.45f, 0f), new V3(0f, 0f, 0.65f) })
                    foreach (double speed in new[] { 1.0, 2.5, 5.0 }) {
                        var (r, l) = At(m, mi == 1);
                        List<PathFire> got = Feed(M(), PathHolds(speed, ((Moved(r, -axis), Moved(l, -axis)), 0.0), ((r, l), 0.0), ((Moved(r, axis), Moved(l, axis)), 0.2)));
                        if (!(got.Count(f => f.Move == m) == 1)) missed.Add($"{m}{(mi == 1 ? " mirrored" : "")} along {axis} at {Inv(speed)} m/s {Fires(got)}");
                    }
        Check("M-6: a line through each modifier's and movement's centre (every pose running, either hand as A, three axes, 1 to 5 m/s) fires it once"
            + (missed.Count > 0 ? " (" + missed.Count + ": " + string.Join("; ", missed.Take(6)) + ")" : ""), missed.Count == 0);
    }

    static void MovePathChecks() {
        PoseValues pv = PoseValues.Default;
        Check("moves: MoveDeepScore 0.6 by default, structures keep 0.5", Near(pv.MoveDeepScore, 0.6f) && Near(pv.StructureDeepScore, 0.5f));

        // the path helper: it ends exactly where it was sent, turned evenly
        List<PathFrame> sample = PathFrames(1.0, RestHands, At(Move.Uppercut, true));
        Check("path helper: ends at the pose, held 0.3 s, the faster hand at 1 m/s", Same(sample[sample.Count - 1].R, At(Move.Uppercut, true).r) && Same(sample[sample.Count - 1].L, At(Move.Uppercut, true).l)
            && Same(sample[0].R, RestHands.r) && Enumerable.Range(1, sample.Count - 1).All(i => (sample[i].R.Pos - sample[i - 1].R.Pos).Length <= 1.0 * Dt + 1e-4 && (sample[i].L.Pos - sample[i - 1].L.Pos).Length <= 1.0 * Dt + 1e-4));
        HandSample halfTurn = Turn(new HandSample(V3.Zero, new V3(1f, 0f, 0f), new V3(0f, 1f, 0f)), new HandSample(V3.Zero, new V3(-1f, 0f, 0f), new V3(0f, 1f, 0f)), 0.5f);
        Check("path helper: half of a half turn is a quarter turn, the fingers kept", Near(V3.Dot(halfTurn.Palm, new V3(1f, 0f, 0f)), 0f) && NearV(halfTurn.Fingers, new V3(0f, 1f, 0f)));

        // the tables: what fails is exactly the known list
        List<(string key, string got)> table;
        var cultureNow = System.Globalization.CultureInfo.CurrentCulture;
        try {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");   // a decimal comma: the keys must not change
            table = PathTable();
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = cultureNow; }
        var known = new HashSet<string>(KnownPathFailures);
        var unexpected = table.Where(w => !known.Contains(w.key)).ToList();
        var fixedNow = KnownPathFailures.Where(k => !table.Any(w => w.key == k)).ToList();
        Check($"paths (run in de-DE): rest to each modifier and movement, rest to Base to each structure, and each move to every other fire exactly what they should, but the {KnownPathFailures.Length} known ({table.Count} fail)"
            + (unexpected.Count > 0 ? "; NEW: " + string.Join("; ", unexpected.Take(12).Select(w => w.key + " " + w.got)) + (unexpected.Count > 12 ? $" and {unexpected.Count - 12} more (-- --paths)" : "") : "")
            + (fixedNow.Count > 0 ? "; now passing: " + string.Join("; ", fixedNow) : ""), unexpected.Count == 0 && fixedNow.Count == 0);

        // a brush: a hand that crosses only the outer part of a zone (score 0.8 at best) without stopping fires nothing; a
        // hand that stops there fires once it has rested StructureStillSeconds
        PoseSpec kick = Spec(Move.Kick);
        V3 side = new V3(0.2f, 0f, 0f), back = new V3(0f, 0f, -0.4f), ahead = new V3(0f, 0f, 0.4f);
        var (kr, kl) = At(Move.Kick);
        (HandSample, HandSample) Off(V3 by) => (Moved(kr, by), Moved(kl, by));
        foreach (double speed in new[] { 0.5, 1.0, 2.5 }) {
            List<PathFrame> brush = PathFrames(speed, Off(side + back), Off(side + ahead));
            float best = brush.Min(f => PoseMatch.Match(kick, f.R, f.L, out _, out float sc) ? sc : float.PositiveInfinity);
            List<PathFire> got = Feed(new PoseMachine(new[] { kick }, PoseValues.Default), brush);
            Check($"brush: crossing Kick's outer part at {speed} m/s (best score {best:0.00}) fires nothing", best > 0.6f && best <= 1f && got.Count == 0);
            List<PathFrame> stop = PathFrames(speed, Off(side + back), Off(side));
            double stoppedAt = stop[stop.Count - (int)Math.Round(PathHold / Dt)].T;
            List<PathFire> rested = Feed(new PoseMachine(new[] { kick }, PoseValues.Default), stop);
            Check($"brush: stopping in Kick's outer part at {speed} m/s fires it once, StructureStillSeconds after the stop",
                rested.Count == 1 && rested[0].Move == Move.Kick && rested[0].At >= stoppedAt + pv.StructureStillSeconds - 1e-6 && rested[0].At <= stoppedAt + pv.StructureStillSeconds + 3 * Dt);
        }
        // with all sixteen poses, the same brush fires nothing either
        Check("brush: crossing Kick's outer part with every pose running fires nothing", Feed(M(), PathFrames(1.0, Off(side + back), Off(side + ahead))).Count == 0);

        // a deep pass fires on the way: the same line through the zone's middle
        List<PathFire> through = Feed(new PoseMachine(new[] { kick }, PoseValues.Default), PathFrames(1.0, Off(back), Off(ahead)));
        Check("deep: a line through Kick's middle fires it on the way, once", through.Count == 1 && through[0].Move == Move.Kick && through[0].Score <= pv.MoveDeepScore);

        // two poses at one rest: the better-scored fires; the other never fires until the hands have left it
        var nearTwin = new PoseSpec(Move.Straight, new HandSpec(kick.A.Pos + new V3(0.1f, 0f, 0f), kick.A.Palm, kick.A.Fingers, 0.25f, 0.2f, 0.3f, 60f, 60f),
            new HandSpec(kick.B.Pos + new V3(0.1f, 0f, 0f), kick.B.Palm, kick.B.Fingers, 0.25f, 0.2f, 0.3f, 60f, 60f));
        var twinHands = Off(new V3(0.03f, 0f, 0f));
        var straightHands = Off(new V3(0.1f, 0f, 0f));
        List<PathFire> two = Feed(new PoseMachine(new[] { kick, nearTwin }, PoseValues.Default), PathFrames(1.0, RestHands, twinHands, twinHands, twinHands, twinHands));
        Check("two at one rest: the better-scored fires, the other not while the hands stay", two.Count == 1 && two[0].Move == Move.Kick);
        var restAfter = PathFrames(1.0, RestHands, twinHands, twinHands, twinHands, RestHands, straightHands);
        List<PathFire> again = Feed(new PoseMachine(new[] { kick, nearTwin }, PoseValues.Default), restAfter);
        Check("two at one rest: once the hands have left and come back to the other's middle, it fires", again.Count == 2 && again[0].Move == Move.Kick && again[1].Move == Move.Straight);
        // groups: a modifier struck loosely (0.8, not ready) does not hold back a movement struck deep in the same frame; and
        // once the movement fired, the modifier matched then is used up (no second fire a moment later)
        var jumpTwin = new PoseSpec(Move.Jump, nearTwin.A, nearTwin.B);
        jumpTwin.A.Pos = kick.A.Pos + side;
        jumpTwin.B.Pos = kick.B.Pos + side;
        List<PathFire> grouped = Feed(new PoseMachine(new[] { kick, jumpTwin }, PoseValues.Default), PathFrames(1.0, Off(side)));
        Check("groups: a modifier that is not ready does not hold back a ready movement, and is used up when it fires", grouped.Count == 1 && grouped[0].Move == Move.Jump && grouped[0].At == 0.0);
    }

    // ----- the hands trace (task 17a): its rows, and its replay through the machine -----

    /// <summary>A gap between two rows longer than this is a stop of the game's machine (review M-3).</summary>
    const double ReplayGapSeconds = 0.25;

    /// <summary>A trace's frames through a fresh machine of <paramref name="poses"/>, with each frame's recorded flags.</summary>
    internal static List<PathFire> Replay(List<TraceFrame> frames, PoseSpec[] poses, PoseValues values) {
        var m = new PoseMachine(poses, values);
        var fired = new List<PathFire>();
        double last = double.NaN;
        foreach (TraceFrame f in frames) {
            if (f.T - last > ReplayGapSeconds) m.Reset();   // the game stopped running the machine (no player, the recorder): it started over
            last = f.T;
            Move mv = m.Step(f.T, f.Right, f.Left, f.Empty, f.Grounded, f.Natural, f.Stunned, out bool rA);
            if (mv != Move.None) fired.Add(new PathFire { Move = mv, At = f.T, Score = m.LastScore, RightIsA = rA });
        }
        return fired;
    }

    /// <summary>A path written as the game writes a trace: every frame a row (as the machine was given it, the armed flag
    /// after the step, the practice step <paramref name="practice"/>), and a fired row after the frame a move fired.</summary>
    static List<string> TraceOf(List<PathFrame> frames, Move practice, out List<PathFire> fired) {
        var m = M();
        fired = new List<PathFire>();
        var lines = new List<string> { PoseTrace.ValuesLine(PoseValues.Default), PoseTrace.Header };
        double slot = double.NegativeInfinity;
        foreach (PathFrame p in frames) {
            Move mv = m.Step(p.T, p.R, p.L, true, true, true, false, out bool rA);
            var f = new TraceFrame { T = p.T, Practice = practice, Empty = true, Grounded = true, Natural = true, Armed = m.Armed(p.T), Yaw = 12.5f, Right = p.R, Left = p.L };
            bool due = PoseTrace.Due(p.T, slot);   // as the game writes: about 45 rows a second, and the frame of every fire
            if (due) slot = PoseTrace.NextSlot(slot, p.T);
            if (due || mv != Move.None) lines.Add(PoseTrace.Row(in f));
            if (mv == Move.None) continue;
            fired.Add(new PathFire { Move = mv, At = p.T, Score = m.LastScore, RightIsA = rA });
            lines.Add(PoseTrace.Fire(new TraceFire { T = p.T, Move = mv, Score = m.LastScore, RightIsA = rA }));
        }
        return lines;
    }

    static void TraceChecks() {
        // I-5: the values line, written and read back; a replay's key=value
        var tuned = PoseValues.Default;
        tuned.MoveDeepScore = 0.7f; tuned.StructureDeepScore = 0.45f; tuned.StructureStillMetres = 0.02f; tuned.StructureStillSeconds = 0.08f;
        tuned.RearmSeconds = 0.15f; tuned.StructureWindowSeconds = 1.5f; tuned.MinHoldSeconds = 0.01f;
        string valuesLine;
        var cultureWas = System.Globalization.CultureInfo.CurrentCulture;
        try {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            valuesLine = PoseTrace.ValuesLine(tuned);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = cultureWas; }
        PoseValues back = PoseTrace.ValuesOf(new[] { valuesLine, PoseTrace.Header, PoseTrace.ValuesLine(PoseValues.Default) }, PoseValues.Default, out bool foundValues, out int laterValues, out List<string> valueErrors);
        Check("values line: '# values' and every key, the invariant culture", valuesLine.StartsWith("# values MoveDeepScore=0.7 StructureDeepScore=0.45 ") && valuesLine.Contains("MinHoldSeconds=0.01") && !valuesLine.Contains(","));
        Check("values line: reads back exactly, the first line counts, a later one is counted", foundValues && laterValues == 1 && valueErrors.Count == 0
            && back.MoveDeepScore == 0.7f && back.StructureDeepScore == 0.45f && back.StructureStillMetres == 0.02f && back.StructureStillSeconds == 0.08f
            && back.RearmSeconds == 0.15f && back.StructureWindowSeconds == 1.5f && back.MinHoldSeconds == 0.01f && back.MoveCooldownSeconds == PoseValues.Default.MoveCooldownSeconds);
        PoseValues none = PoseTrace.ValuesOf(new[] { PoseTrace.Header }, tuned, out bool noneFound, out _, out _);
        Check("values line: none in the file keeps the fallback", !noneFound && none.MoveDeepScore == 0.7f);
        PoseValues kv = PoseValues.Default;
        Check("values: key=value sets one number, any case; a bad key or value is refused and changes nothing",
            PoseTrace.TrySet(ref kv, "movedeepscore=0.65", out _) && kv.MoveDeepScore == 0.65f
            && !PoseTrace.TrySet(ref kv, "Nope=1", out string whyKey) && whyKey.Length > 0 && !PoseTrace.TrySet(ref kv, "MoveDeepScore=-1", out _)
            && !PoseTrace.TrySet(ref kv, "MoveDeepScore=0,7", out _) && !PoseTrace.TrySet(ref kv, "MoveDeepScore", out _) && kv.MoveDeepScore == 0.65f);
        PoseTrace.Parse(new[] { valuesLine, "# a comment", PoseTrace.Header }, out List<TraceFrame> noFrames, out _, out List<string> hashErrors);
        Check("trace parse: # lines are skipped", noFrames.Count == 0 && hashErrors.Count == 0);
        // M-2: the row slots: about 45 rows a second from a 90 Hz clock that jitters by up to 1 ms, never more
        var jitter = new Random(5);
        double rowSlot = double.NegativeInfinity, clock = 0;
        int rowCount = 0;
        for (int n = 0; n < 900; n++) {
            clock += Dt + (jitter.NextDouble() * 2 - 1) * 0.001;
            if (!PoseTrace.Due(clock, rowSlot)) continue;
            rowSlot = PoseTrace.NextSlot(rowSlot, clock);
            rowCount++;
        }
        Check($"trace slots: a jittering 90 Hz clock gives about 45 rows a second ({rowCount / clock:0.0})", rowCount / clock > 43.5 && rowCount <= (int)(clock / PoseTrace.RowSeconds) + 2);

        // the fire line's parts: each hand's place ratio and its two angles to the fired pose, with the assignment it fired with
        PoseSpec kickSpec = Spec(Move.Kick);
        var (er, el) = At(Move.Kick, true);
        PoseMatch.Explain(kickSpec, false, er, el, out HandParts mr, out HandParts ml);
        Check("parts: a mirrored pose's own hands, explained with A on the left, are all 0", Near(mr.Place, 0f) && Near(mr.PalmDeg, 0f) && Near(mr.FingerDeg, 0f) && Near(ml.Place, 0f) && Near(ml.PalmDeg, 0f) && Near(ml.FingerDeg, 0f));
        PoseMatch.Explain(kickSpec, true, Turned(Moved(R(Move.Kick), new V3(0.125f, 0f, 0f)), 30f), L(Move.Kick), out HandParts sr, out HandParts sl);
        Check("parts: 0.125 m to the side is half way out, a palm turned 30 degrees reads 30, the fingers 0", Near(sr.Place, 0.5f) && Math.Abs(sr.PalmDeg - 30f) < 0.01f && Near(sr.FingerDeg, 0f) && Near(sl.Place, 0f));
        Check("parts: the line's words", sr.ToString() == "place 0.50 palm 30 fingers 0 deg");
        var noFingers = new HandSpec(kickSpec.A.Pos, kickSpec.A.Palm, V3.Zero, 0.25f, 0.2f, 0.3f, 60f, 60f);
        HandParts unchecked_ = PoseMatch.Parts(noFingers, R(Move.Kick));
        Check("parts: a direction the pose does not check is NaN, said as -", float.IsNaN(unchecked_.FingerDeg) && unchecked_.ToString() == "place 0.00 palm 0 fingers - deg");

        // one row and one fire, through the text and back, in a culture with a decimal comma
        var frame = new TraceFrame { T = 12.3456, Practice = Move.Kick, Empty = true, Grounded = false, Natural = true, Stunned = false, Armed = true, Yaw = -66.25f,
            Right = new HandSample(new V3(0.25f, -0.75f, 0.05f), new V3(-1f, 0f, 0f), new V3(0f, -1f, 0f)),
            Left = new HandSample(new V3(-0.1234f, 0.5f, 0.3337f), new V3(0f, 0.6f, 0.8f), new V3(0f, -0.8f, 0.6f)) };
        var fire = new TraceFire { T = 12.3456, Move = Move.Uppercut, Score = 0.5812f, RightIsA = false };
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        string row, fireRow;
        try {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            row = PoseTrace.Row(in frame);
            fireRow = PoseTrace.Fire(in fire);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
        string[] header = PoseTrace.Header.Split(',');
        Check("trace header: time, practice, five flags, yaw, then place, palm and fingers of the right hand and the left",
            header.Length == 26 && string.Join(",", header.Take(8)) == "t,practice,empty,grounded,natural,stunned,armed,yaw"
            && string.Join(",", header.Skip(8).Take(9)) == "rightX,rightY,rightZ,rightPalmX,rightPalmY,rightPalmZ,rightFingersX,rightFingersY,rightFingersZ" && header[17] == "leftX" && header[25] == "leftFingersZ");
        Check("trace row: 26 cells, 3 decimals, the invariant culture", row.Split(',').Length == 26 && row.StartsWith("12.346,Kick,1,0,1,0,1,-66.250,0.250,-0.750,0.050,-1.000,") && row.EndsWith(",-0.800,0.600"));
        Check("trace fire row", fireRow == "12.346,fired,Uppercut,0.581,left");
        PoseTrace.Parse(new[] { PoseTrace.Header, row, fireRow, "", "garbage,1,2", "1.0,fired,Nope,0.5,right", "1.0,,1,1,1,0,0" }, out List<TraceFrame> frames, out List<TraceFire> fires, out List<string> errors);
        bool sameFrame = frames.Count == 1 && Math.Abs(frames[0].T - 12.346) < 1e-9 && frames[0].Practice == Move.Kick && frames[0].Empty && !frames[0].Grounded && frames[0].Natural && !frames[0].Stunned && frames[0].Armed
            && Math.Abs(frames[0].Yaw - -66.25f) < 1e-4f && Same(frames[0].Right, frame.Right) && Math.Abs(frames[0].Left.Pos.X - -0.123f) < 1e-6f && Math.Abs(frames[0].Left.Pos.Z - 0.334f) < 1e-6f;
        Check("trace parse: the row reads back (to 3 decimals)", sameFrame);
        Check("trace parse: the fire reads back", fires.Count == 1 && fires[0].Move == Move.Uppercut && Math.Abs(fires[0].Score - 0.581f) < 1e-6f && !fires[0].RightIsA && Math.Abs(fires[0].T - 12.346) < 1e-9);
        Check("trace parse: bad lines are skipped and named", errors.Count == 3 && errors[0].Contains("line 5") && errors[1].Contains("line 6") && errors[2].Contains("line 7"));
        var nanFrame = frame;
        nanFrame.Right = new HandSample(new V3(float.NaN, 0f, 0f), frame.Right.Palm, frame.Right.Fingers);
        nanFrame.Practice = Move.None;
        PoseTrace.Parse(new[] { PoseTrace.Row(in nanFrame) }, out List<TraceFrame> nanBack, out _, out List<string> nanErrors);
        Check("trace parse: a lost hand (NaN) and no practice step read back", nanErrors.Count == 0 && nanBack.Count == 1 && float.IsNaN(nanBack[0].Right.Pos.X) && nanBack[0].Practice == Move.None);
        Check("trace: at most 45 frame rows a second", PoseTrace.Due(1.0, double.NegativeInfinity) && !PoseTrace.Due(1.0 + Dt, 1.0) && PoseTrace.Due(1.0 + 2 * Dt, 1.0) && Near((float)PoseTrace.RowSeconds, 1f / 45f));
        Check("trace: the file's name", PoseTrace.FileName(new DateTime(2026, 11, 23, 9, 5, 7)) == "pose-trace-20261123-090507.csv" && PoseTrace.MaxBytes == 20L * 1024 * 1024);

        // a path written as a trace, read back and replayed: the same fires (each within a frame of its time: 3 decimals)
        var (kr, kl) = At(Move.Kick);
        List<PathFrame> path = PathFrames(1.0, RestHands, At(Move.Straight), At(Move.Kick, true), (Moved(kr, new V3(0.2f, 0f, 0f)), Moved(kl, new V3(0.2f, 0f, 0f))), RestHands, At(Move.Base), At(Move.Cube));
        List<string> lines = TraceOf(path, Move.Kick, out List<PathFire> direct);
        PoseTrace.Parse(lines.ToArray(), out List<TraceFrame> read, out List<TraceFire> recorded, out List<string> readErrors);
        List<PathFire> replayed = Replay(read, PoseLibrary.Defaults, PoseValues.Default);
        Check($"replay: a path's trace replays to the same fires ({string.Join(", ", direct.Select(d => d.Move))} / {string.Join(", ", replayed.Select(d => d.Move))})",
            readErrors.Count == 0 && read.Count < path.Count * 0.6 && direct.Count >= 3 && replayed.Count == direct.Count && recorded.Count == direct.Count
            && Enumerable.Range(0, direct.Count).All(i => replayed[i].Move == direct[i].Move && recorded[i].Move == direct[i].Move && Math.Abs(replayed[i].At - direct[i].At) <= Dt + 1e-3));
        Check("replay: the path fired Straight, Kick with A on the left, Kick at rest in its outer part, then Cube from Base",
            direct.Select(d => d.Move).SequenceEqual(new[] { Move.Straight, Move.Kick, Move.Kick, Move.Cube }) && !direct[1].RightIsA && direct[2].RightIsA && direct[2].Score > PoseValues.Default.MoveDeepScore);
    }

    // ----- not a test: `-- --paths`, the table of task 17a's paths that fire what they should not -----

    internal static int PrintPaths() {
        List<(string key, string got)> table = PathTable();
        var known = new HashSet<string>(KnownPathFailures);
        Console.WriteLine("Paths that do not fire exactly what they should (rest -> each modifier and movement; rest -> Base -> each structure; each modifier or movement -> every other; either hand as A, 1 and 2.5 m/s):");
        foreach (var (key, got) in table) Console.WriteLine($"{(known.Contains(key) ? "KNOWN" : "NEW  ")} {key}: fired {got}");
        foreach (string k in KnownPathFailures.Where(k => !table.Any(w => w.key == k))) Console.WriteLine($"FIXED {k} (known, now passes)");
        Console.WriteLine($"{table.Count} path(s) fail, {table.Count(w => known.Contains(w.key))} of them known (MoveDeepScore {PoseValues.Default.MoveDeepScore}, StructureDeepScore {PoseValues.Default.StructureDeepScore})");
        return table.Any(w => !known.Contains(w.key)) ? 1 : 0;
    }

    // ----- not a test: `-- --replay <trace.csv> [poses.txt]`, a hands trace from the game through the machine -----

    internal static int ReplayTrace(string? path, string[] rest) {
        if (path == null) { Console.WriteLine("usage: --replay <pose-trace.csv> [poses.txt] [Key=value ...] (keys: " + string.Join(", ", PoseTrace.ValueKeys) + ")"); return 2; }
        string? posesPath = rest.FirstOrDefault(a => !a.Contains("="));
        List<string> settings = rest.Where(a => a.Contains("=")).ToList();
        string[] lines;
        try { lines = System.IO.File.ReadAllLines(path); }
        catch (Exception e) { Console.WriteLine($"cannot read {path}: {e.Message}"); return 1; }
        PoseTrace.Parse(lines, out List<TraceFrame> frames, out List<TraceFire> recorded, out List<string> errors);
        foreach (string e in errors) Console.WriteLine("SKIPPED " + e);
        PoseSpec[] poses = PoseLibrary.Defaults;
        if (posesPath != null) {
            string[] poseLines;
            try { poseLines = System.IO.File.ReadAllLines(posesPath); }
            catch (Exception e) { Console.WriteLine($"cannot read {posesPath}: {e.Message}"); return 1; }
            PoseSpec[] fromFile = PoseLibrary.Parse(poseLines, out List<string> poseErrors);
            foreach (string e in poseErrors) Console.WriteLine("POSES SKIPPED " + e);
            poses = PoseLibrary.Merge(PoseLibrary.Defaults, fromFile, out int replaced);
            Console.WriteLine($"poses: {replaced} from {posesPath}, the rest built in");
        }
        // the numbers: the trace's values line (the game's), else the built-in ones; then each Key=value given
        PoseValues values = PoseTrace.ValuesOf(lines, PoseValues.Default, out bool valuesInFile, out int later, out List<string> valueErrors);
        foreach (string e in valueErrors) Console.WriteLine("VALUES SKIPPED " + e);
        foreach (string kv in settings)
            if (!PoseTrace.TrySet(ref values, kv, out string why)) { Console.WriteLine("ERROR " + why); return 2; }
        Console.WriteLine($"{frames.Count} frame(s), {recorded.Count} recorded fire(s) in {path}");
        Console.WriteLine($"the machine runs {(valuesInFile ? "the trace's values" : "the built-in values (the trace has no values line)")}{(settings.Count > 0 ? ", with " + string.Join(" ", settings) : "")}: "
            + PoseTrace.ValuesLine(values).Substring(PoseTrace.ValuesPrefix.Length).Trim());
        if (later > 0) Console.WriteLine($"NOTE: the game's values changed {later} time(s) during the trace (more '# values' lines); the replay runs the first throughout");
        if (frames.Count == 0) return 1;

        List<PathFire> replayed = Replay(frames, poses, values);
        Console.WriteLine();
        Console.WriteLine("Fires (time, move, score, A's hand):");
        var all = replayed.Select(r => (t: r.At, line: $"  replay {r.At,9:0.000} {r.Move,-9} {r.Score:0.00} {(r.RightIsA ? "right" : "left")}"))
            .Concat(recorded.Select(r => (t: r.T, line: $"  file   {r.T,9:0.000} {r.Move,-9} {r.Score:0.00} {(r.RightIsA ? "right" : "left")}")))
            .OrderBy(x => x.t).ThenBy(x => x.line);
        foreach (var x in all) Console.WriteLine(x.line);
        Console.WriteLine($"  {replayed.Count} replayed, {recorded.Count} in the file");

        // each practice step: the target's best score during it, and each hand's parts at that moment
        Console.WriteLine();
        Console.WriteLine("Practice steps (the target's best score during the step; at that moment each hand's place ratio, palm and finger angles):");
        int steps = 0;
        for (int i = 0; i < frames.Count;) {
            Move target = frames[i].Practice;
            int j = i;
            while (j < frames.Count && frames[j].Practice == target) j++;
            if (target != Move.None) {
                steps++;
                PoseSpec spec = poses.FirstOrDefault(p => p.Move == target);
                float best = float.PositiveInfinity;
                int at = -1;
                bool bestA = true;
                for (int k = i; k < j; k++) {
                    PoseMatch.Match(spec, frames[k].Right, frames[k].Left, out bool rA, out float sc);
                    if (sc < best) { best = sc; at = k; bestA = rA; }
                }
                string line = $"  {target,-9} {frames[i].T,9:0.000}-{frames[j - 1].T:0.000} s: ";
                if (at < 0) line += "never judged";
                else {
                    PoseMatch.Explain(spec, bestA, frames[at].Right, frames[at].Left, out HandParts pr, out HandParts pl);
                    line += $"best {best:0.00} at {frames[at].T:0.000} s (A on the {(bestA ? "right" : "left")}); right: {pr}; left: {pl}";
                }
                Console.WriteLine(line);
            }
            i = j;
        }
        if (steps == 0) Console.WriteLine("  (no practice step in this trace)");

        // each pose: how long the hands were inside it (a gap over 0.1 s between rows counts 0.1 s)
        Console.WriteLine();
        Console.WriteLine("Time inside each pose (seconds):");
        foreach (PoseSpec s in poses) {
            double inside = 0;
            for (int k = 0; k < frames.Count; k++) {
                if (!PoseMatch.Match(s, frames[k].Right, frames[k].Left, out _, out _)) continue;
                double dt = k + 1 < frames.Count ? frames[k + 1].T - frames[k].T : 0.0;
                inside += Math.Max(0.0, Math.Min(0.1, dt));
            }
            Console.WriteLine($"  {s.Move,-9} {inside:0.00}");
        }
        return 0;
    }

    // the capture guide (Task 3b): the hand lines in the recorder's order, the next pose for a count, the card's text, the
    // body frame run backwards, and the easing
    static void GuideChecks() {
        PoseSpec[] defaults = PoseLibrary.Defaults;
        Check("guide: sixteen entries", PoseGuide.Count == 16 && PoseGuide.Moves.Count == 16);
        Check("guide: the recorder's order (the Move enum's, as Hands.cs names snapshot n)", Enumerable.Range(0, 16).All(i => PoseGuide.Moves[i] == (Move)(i + 1)));
        Check("guide: the default poses' order exactly", PoseGuide.Moves.SequenceEqual(defaults.Select(s => s.Move)));
        Check("guide: every name a Move, every hand line there", PoseGuide.Moves.All(m => m != Move.None && Enum.IsDefined(typeof(Move), m)
            && PoseGuide.Right(m).Length > 0 && PoseGuide.Left(m).Length > 0));
        Check("guide: no line for a move that is no pose", PoseGuide.Right(Move.None) == "" && PoseGuide.Left((Move)99) == "");
        Check("guide: Base's lines", PoseGuide.Right(Move.Base) == "palm up, fingers forward, just above the waist" && PoseGuide.Left(Move.Base) == "the same, shoulder width apart");
        Check("guide: Disc's lines", PoseGuide.Right(Move.Disc) == "arm half out in front, fingers forward, palm facing left" && PoseGuide.Left(Move.Disc) == "across your right forearm, palm toward you, fingers pointing right");
        Check("guide: Ball's lines", PoseGuide.Right(Move.Ball) == "near your body, just under the shoulder, palm facing left, fingers forward" && PoseGuide.Left(Move.Ball) == "arm half out, just under the shoulder, palm facing right, fingers forward");
        Check("guide: Straight's lines", PoseGuide.Right(Move.Straight) == "in front of your right shoulder, palm facing left, fingers up" && PoseGuide.Left(Move.Straight) == "arm half out in front, palm down, fingers forward");
        Check("guide: Wall's lines", PoseGuide.Right(Move.Wall) == "palm up, lifted to your forehead" && PoseGuide.Left(Move.Wall) == "the same");
        Check("guide: no line asks for an arm fully out", PoseGuide.Moves.All(m => !PoseGuide.Right(m).Contains("fully") && !PoseGuide.Left(m).Contains("fully")));
        Check("guide: Uppercut's lines", PoseGuide.Right(Move.Uppercut) == "in front of your right shoulder, fingers up, tilted a little forward" && PoseGuide.Left(Move.Uppercut) == "half out in front of your face, palm toward your face, fingers up");
        Check("guide: Explode's lines", PoseGuide.Right(Move.Explode) == "at your belly, palm toward you, fingers pointing left, knuckles touching the left" && PoseGuide.Left(Move.Explode) == "at your belly, palm toward you, fingers pointing right");
        Check("guide: Dash's lines", PoseGuide.Right(Move.Dash) == "at your hip, palm up, fingers forward" && PoseGuide.Left(Move.Dash) == "arm half out in front, palm facing right, fingers forward");

        // the next pose for a count of snapshots
        Check("guide: done from 16 on", !PoseGuide.Done(0) && !PoseGuide.Done(15) && PoseGuide.Done(16) && PoseGuide.Done(40));
        Check("guide: the next pose", PoseGuide.Next(0) == Move.Base && PoseGuide.Next(1) == Move.Disc && PoseGuide.Next(15) == Move.Dash && PoseGuide.Next(16) == Move.None && PoseGuide.Next(-3) == Move.Base);
        Check("guide: the title", PoseGuide.Title(0) == "Pose 1 of 16: Base" && PoseGuide.Title(6) == "Pose 7 of 16: Straight" && PoseGuide.Title(15) == "Pose 16 of 16: Dash");

        // the card
        string card = PoseGuide.Card(1, "#FF8A1E", "#3FA9FF", true, 46);
        string[] lines = card.Split('\n');
        string flat = string.Join(" ", lines.Select(Untagged));
        Check("guide: the card's first line is the title", lines[0] == "Pose 2 of 16: Disc");
        Check("guide: the card says what each hand does, the hint and the colours", flat.Contains("RIGHT: arm half out in front, fingers forward, palm facing left")
            && flat.Contains("LEFT: across your right forearm, palm toward you, fingers pointing right")
            && flat.Contains("Hold still until both hands buzz twice. Rest your arms at your sides between poses.") && flat.Contains("orange = right, blue = left"));
        Check("guide: each hand's lines are in its marker's colour, tag by tag on every line", lines.Where(l => Untagged(l).StartsWith("RIGHT:")).All(l => l.StartsWith("<color=#FF8A1E>") && l.EndsWith("</color>"))
            && lines.Count(l => l.StartsWith("<color=#FF8A1E>") && l.EndsWith("</color>")) >= 2 && lines.Count(l => l.StartsWith("<color=#3FA9FF>") && l.EndsWith("</color>")) >= 2
            && lines.All(l => l.Contains("<color=") == l.EndsWith("</color>")));
        Check("guide: no card line longer than the wrap", Enumerable.Range(0, 16).All(n => PoseGuide.Card(n, "#FF8A1E", "#3FA9FF", true, 46).Split('\n').All(l => Untagged(l).Length <= 46)));
        Check("guide: nor at the display's 34 letters", Enumerable.Range(0, 16).All(n => PoseGuide.Card(n, "#FF8A1E", "#3FA9FF", true, 34).Split('\n').All(l => Untagged(l).Length <= 34)));
        Check("guide: without markers the card names no colours and tags nothing", !PoseGuide.Card(1, "#FF8A1E", "#3FA9FF", false, 46).Contains("orange") && !PoseGuide.Card(1, "#FF8A1E", "#3FA9FF", false, 46).Contains("<color"));
        Check("guide: after the sixteenth, only the last words", PoseGuide.Card(16, "#FF8A1E", "#3FA9FF", true, 46) == "All 16 done. Tell Claude." && PoseGuide.Card(23, "", "", false, 46) == "All 16 done. Tell Claude.");
        List<string> wrapped = PoseGuide.Wrap("one two three four five six seven", 9);
        Check("guide: wrap breaks at spaces and loses nothing", wrapped.SequenceEqual(new[] { "one two", "three", "four five", "six seven" }));
        Check("guide: wrap keeps a word longer than the width whole, and a width under 1 wraps nothing", PoseGuide.Wrap("a extraordinarily b", 5).SequenceEqual(new[] { "a", "extraordinarily", "b" })
            && PoseGuide.Wrap("a b c", 0).SequenceEqual(new[] { "a b c" }) && PoseGuide.Wrap("", 10).Count == 0);

        // the body frame run backwards (the markers' places in the world), at body scale 1
        V3 head = new V3(3f, 1.7f, -2f);
        Check("to world: yaw 0 is the body frame moved to the head", NearV(BodyFrame.ToWorld(head, 0f, new V3(0.2f, -0.4f, 0.35f)), new V3(3.2f, 1.3f, -1.65f)));
        Check("to world: yaw 90, ahead is +X and right is -Z", NearV(BodyFrame.ToWorld(V3.Zero, 90f, new V3(0f, 0f, 1f)), new V3(1f, 0f, 0f)) && NearV(BodyFrame.DirToWorld(90f, new V3(1f, 0f, 0f)), new V3(0f, 0f, -1f)));
        bool round = true;
        foreach (float yaw in new[] { 0f, 37f, 90f, -137f, 180f, 271.5f })
            foreach (PoseSpec s in defaults)
                foreach (HandSpec h in new[] { s.A, s.B }) {
                    HandSample back = BodyFrame.ToBody(head, yaw, BodyFrame.ToWorld(head, yaw, h.Pos), BodyFrame.DirToWorld(yaw, h.Palm), BodyFrame.DirToWorld(yaw, h.Fingers), 1f);
                    round &= NearV(back.Pos, h.Pos) && NearV(back.Palm, h.Palm.Normalized) && NearV(back.Fingers, h.Fingers.Normalized);
                }
        Check("to world: ToBody reads every default hand back where it was put, at any yaw", round);
        Check("to world: a direction keeps its length", Near(BodyFrame.DirToWorld(63f, new V3(0.3f, -0.2f, 0.9f)).Length, new V3(0.3f, -0.2f, 0.9f).Length));

        // the easing (GuideFollowSeconds): how much of the way to the new place one frame goes
        Check("ease: one time constant is 1 - 1/e of the way", Near(PoseGuide.Ease(0.3f, 0.3f), 1f - (float)Math.Exp(-1.0)));
        Check("ease: no time is no move, no follow time is all the way", PoseGuide.Ease(0f, 0.3f) == 0f && PoseGuide.Ease(0.011f, 0f) == 1f && PoseGuide.Ease(0.011f, -1f) == 1f);
        Check("ease: never NaN, never outside 0..1", PoseGuide.Ease(float.NaN, 0.3f) == 1f && PoseGuide.Ease(0.011f, float.NaN) == 1f && PoseGuide.Ease(-0.5f, 0.3f) == 0f && PoseGuide.Ease(100f, 0.3f) <= 1f);
        float gone = 0f;
        for (int i = 0; i < 90; i++) gone += (1f - gone) * PoseGuide.Ease((float)Dt, 0.3f);
        Check("ease: frame by frame is the same as in one go", Math.Abs(gone - PoseGuide.Ease(1f, 0.3f)) < 1e-3f);
    }

    // the practice guide (Task 3c): the list, the step machine (worked, another move, the skip, the summary, the restart),
    // the card's text and which pose the markers show
    static void PracticeChecks() {
        const string Orange = "#FF8A1E", Blue = "#3FA9FF";
        List<Move> steps = PracticeRun.Parse("Jump, pillar ,Sprint, Base, Wall,,Jump", out List<string> errors);
        Check("practice: the list's moves in order, any case, a name twice kept twice", steps.SequenceEqual(new[] { Move.Jump, Move.Pillar, Move.Wall, Move.Jump }));
        Check("practice: an unknown name and Base are skipped and named", errors.Count == 2 && errors[0].Contains("'Sprint'") && errors[1].Contains("Base"));
        Check("practice: None is no step, an empty list has none", PracticeRun.Parse("None", out errors).Count == 0 && errors.Count == 1 && PracticeRun.Parse("", out errors).Count == 0 && errors.Count == 0
            && PracticeRun.Parse(null, out errors).Count == 0);
        Check("practice: the default list is the fifteen moves but Base", PracticeRun.Parse(PracticeRun.DefaultList, out errors).Count == 15 && errors.Count == 0
            && PracticeRun.Parse(PracticeRun.DefaultList, out _).Distinct().Count() == 15 && PracticeRun.Parse(PracticeRun.DefaultList, out _)[0] == Move.Jump);

        // the target fires: the card says so for the show time, then the next step
        var run = new PracticeRun(new[] { Move.Jump, Move.Pillar, Move.Kick }, 25f, 1.5f);
        PracticeView v = run.Step(0.0, Move.None, 0f);
        Check("practice: starts at step 1, trying", v.Phase == PracticePhase.Try && v.Index == 0 && v.Count == 3 && v.Move == Move.Jump && v.Other == Move.None && v.Log == null);
        int version = v.Version;
        v = run.Step(3.0, Move.None, 0f);
        Check("practice: a quiet frame changes nothing", v.Phase == PracticePhase.Try && v.Version == version && v.Log == null);
        v = run.Step(4.2, Move.Jump, 0.31f);
        Check("practice: the step's move fired: worked, with its score", v.Phase == PracticePhase.Worked && v.Move == Move.Jump && Near(v.Score, 0.31f) && v.Version != version);
        Check("practice: the worked line of the log", v.Log == "[Earth] Practice: Jump worked (score 0.31) after 4.2 s; first fired: none");
        v = run.Step(5.6, Move.Kick, 0.2f);
        Check("practice: still shown inside the show time, a move fired meanwhile is not counted", v.Phase == PracticePhase.Worked && v.Index == 0 && v.Log == null);
        v = run.Step(5.7, Move.None, 0f);
        Check("practice: after the show time the next step", v.Phase == PracticePhase.Try && v.Index == 1 && v.Move == Move.Pillar && v.Other == Move.None);

        // another move fires first: said for the show time, counted, the step stays
        version = v.Version;
        v = run.Step(7.0, Move.Cube, 0.44f);
        Check("practice: another move fired: named, the step stays", v.Phase == PracticePhase.Try && v.Index == 1 && v.Move == Move.Pillar && v.Other == Move.Cube && Near(v.OtherScore, 0.44f) && v.Version != version && v.Log == null);
        v = run.Step(8.4, Move.None, 0f);
        Check("practice: its line stays for the show time", v.Other == Move.Cube);
        version = v.Version;
        v = run.Step(8.5, Move.None, 0f);
        Check("practice: then goes", v.Other == Move.None && v.Phase == PracticePhase.Try && v.Index == 1 && v.Version != version);
        run.Step(9.0, Move.Cube, 0.4f);
        run.Step(9.5, Move.Dash, 0.6f);
        v = run.Step(12.7, Move.Pillar, 0.25f);
        Check("practice: worked after other moves, each named with its count", v.Phase == PracticePhase.Worked && v.Log == "[Earth] Practice: Pillar worked (score 0.25) after 7.0 s; first fired: Cube x2, Dash");
        v = run.Step(14.2, Move.None, 0f);
        Check("practice: step 3", v.Phase == PracticePhase.Try && v.Index == 2 && v.Move == Move.Kick);

        // the skip: the step's own time, from when it started
        run.Step(20.0, Move.Stomp, 0.5f);
        v = run.Step(39.1, Move.None, 0f);
        Check("practice: not skipped before the skip time", v.Phase == PracticePhase.Try && v.Index == 2);
        v = run.Step(39.2, Move.None, 0f);
        Check("practice: skipped after 25 s", v.Phase == PracticePhase.Skipped && v.Move == Move.Kick && v.Log == "[Earth] Practice: Kick skipped after 25 s; fired instead: Stomp");
        v = run.Step(40.6, Move.None, 0f);
        Check("practice: the skip is shown for the show time", v.Phase == PracticePhase.Skipped && v.Log == null);
        v = run.Step(40.7, Move.None, 0f);
        Check("practice: after the last step, done with the counts", v.Phase == PracticePhase.Done && v.Count == 3 && v.First == 1 && v.Tries == 1 && v.Skipped == 1 && v.Move == Move.None);
        Check("practice: the summary line of the log, once", v.Log == "[Earth] Practice: done: 1 of 3 worked first time, 1 needed tries, 1 skipped" && run.Step(41.0, Move.None, 0f).Log == null);
        v = run.Step(500.0, Move.Jump, 0.1f);
        Check("practice: done stays done", v.Phase == PracticePhase.Done && v.First == 1 && v.Log == null);

        // off and on: from step 1, nothing counted
        run.Restart();
        v = run.Step(600.0, Move.None, 0f);
        Check("practice: a restart is step 1 again with no counts", v.Phase == PracticePhase.Try && v.Index == 0 && v.Move == Move.Jump && v.First == 0 && v.Tries == 0 && v.Skipped == 0 && v.Other == Move.None);
        v = run.Step(624.9, Move.None, 0f);
        Check("practice: and its skip time starts over", v.Phase == PracticePhase.Try);
        v = run.Step(625.0, Move.None, 0f);
        Check("practice: a skip with nothing fired says none", v.Log == "[Earth] Practice: Jump skipped after 25 s; fired instead: none");

        var none = new PracticeRun(new Move[0], 25f, 1.5f);
        v = none.Step(0.0, Move.None, 0f);
        Check("practice: no steps is done at once", v.Phase == PracticePhase.Done && v.Count == 0 && v.Log == "[Earth] Practice: done: 0 of 0 worked first time, 0 needed tries, 0 skipped");
        var quick = new PracticeRun(new[] { Move.Jump }, float.NaN, -1f);
        Check("practice: times that are no times are the defaults", quick.Step(0.0, Move.None, 0f).Phase == PracticePhase.Try && quick.Step(24.9, Move.None, 0f).Phase == PracticePhase.Try
            && quick.Step(25.0, Move.None, 0f).Phase == PracticePhase.Skipped && quick.Step(26.4, Move.None, 0f).Phase == PracticePhase.Skipped && quick.Step(26.5, Move.None, 0f).Phase == PracticePhase.Done);

        // the card
        run = new PracticeRun(new[] { Move.Jump, Move.Pillar }, 25f, 1.5f);
        v = run.Step(0.0, Move.None, 0f);
        string[] lines = PracticeRun.Card(in v, Orange, Blue, true, 34).Split('\n');
        string flat = string.Join(" ", lines.Select(Untagged));
        Check("practice card: the title", lines[0] == "Try 1 of 2: Jump");
        Check("practice card: the hand lines and the colours, no base line for a move that is no structure", flat.Contains("RIGHT: " + PoseGuide.Right(Move.Jump)) && flat.Contains("LEFT: " + PoseGuide.Left(Move.Jump))
            && flat.EndsWith("orange = right, blue = left") && !flat.Contains("First Base") && !flat.Contains("That was"));
        Check("practice card: each hand's lines in its colour", lines.Where(l => Untagged(l).StartsWith("RIGHT:")).All(l => l.StartsWith("<color=#FF8A1E>")) && lines.Where(l => Untagged(l).StartsWith("LEFT:")).All(l => l.StartsWith("<color=#3FA9FF>")));
        v = run.Step(1.0, Move.Dash, 0.52f);
        flat = string.Join(" ", PracticeRun.Card(in v, Orange, Blue, true, 34).Split('\n').Select(Untagged));
        Check("practice card: another move fired", flat.Contains("That was Dash (score 0.52), not Jump") && flat.Contains("Try 1 of 2: Jump"));
        v = run.Step(2.0, Move.Jump, 0.31f);
        Check("practice card: worked", PracticeRun.Card(in v, Orange, Blue, true, 34) == "Jump worked (score 0.31)");
        v = run.Step(3.5, Move.None, 0f);
        flat = string.Join(" ", PracticeRun.Card(in v, "", "", false, 34).Split('\n').Select(Untagged));
        Check("practice card: a structure says the base first; without markers no colours", flat.StartsWith("Try 2 of 2: Pillar") && flat.Contains("First Base (both palms up at your waist), then Pillar")
            && !flat.Contains("orange") && !PracticeRun.Card(in v, "", "", false, 34).Contains("<color"));
        v = run.Step(28.5, Move.None, 0f);
        Check("practice card: skipped", PracticeRun.Card(in v, Orange, Blue, true, 34) == "Skipped Pillar");
        v = run.Step(30.0, Move.None, 0f);
        flat = string.Join(" ", PracticeRun.Card(in v, Orange, Blue, true, 34).Split('\n'));
        Check("practice card: the summary", flat == "Practice done: 0 of 2 worked first time, 1 needed tries, 1 skipped");
        bool fits = true;
        foreach (Move m in PracticeRun.Parse(PracticeRun.DefaultList, out _)) {
            var one = new PracticeRun(new[] { m }, 25f, 1.5f);
            one.Step(0.0, Move.None, 0f);
            PracticeView w = one.Step(1.0, m == Move.Explode ? Move.Straight : Move.Explode, 0.99f);
            fits &= PracticeRun.Card(in w, Orange, Blue, true, 34).Split('\n').All(l => Untagged(l).Length <= 34);
        }
        Check("practice card: no line longer than the wrap", fits);

        // the markers: Base's hands until the base has armed a structure
        v = new PracticeRun(new[] { Move.Cube }, 25f, 1.5f).Step(0.0, Move.None, 0f);
        Check("practice markers: a structure shows Base until armed, then itself", PracticeRun.Marker(in v, false) == Move.Base && PracticeRun.Marker(in v, true) == Move.Cube);
        v = new PracticeRun(new[] { Move.Kick }, 25f, 1.5f).Step(0.0, Move.None, 0f);
        Check("practice markers: another move shows itself", PracticeRun.Marker(in v, false) == Move.Kick && PracticeRun.Marker(in v, true) == Move.Kick);
        var shown = new PracticeRun(new[] { Move.Kick }, 25f, 1.5f);
        shown.Step(0.0, Move.None, 0f);
        v = shown.Step(1.0, Move.Kick, 0.2f);
        PracticeView after = shown.Step(3.0, Move.None, 0f);
        Check("practice markers: none while a result is shown or once done", PracticeRun.Marker(in v, false) == Move.None && after.Phase == PracticePhase.Done && PracticeRun.Marker(in after, true) == Move.None);

        // the machine says whether the base has armed the structures
        var machine = new PoseMachine(PoseLibrary.Defaults, PoseValues.Default);
        Check("armed: not before a base", !machine.Armed(0.0));
        var (br, bl) = At(Move.Base);
        machine.Step(1.0, br, bl, true, true, true, false, out _);
        Check("armed: from the base for the structure window", machine.Armed(1.0) && machine.Armed(2.2) && !machine.Armed(2.3));
        machine.Step(1.1, IdleR, IdleL, true, true, true, false, out _);
        var (cr, cl) = At(Move.Cube);
        Check("armed: not after the structure fired", machine.Step(1.2, cr, cl, true, true, true, false, out _) == Move.Cube && !machine.Armed(1.2));
    }

    static string Untagged(string line) => System.Text.RegularExpressions.Regex.Replace(line, "</?color[^>]*>", "");

    // the game's side of the machine (Task 5): the axis settings, the two-frame empty test, the ground grace, poses.txt over
    // the defaults, and a push's velocity
    static void GameSideChecks() {
        V3 x = new V3(1f, 0f, 0f), y = new V3(0f, 1f, 0f), z = new V3(0f, 0f, 1f);

        // [EarthPose] PalmAxis* / FingerAxis*
        string[] six = { "+x", "-x", "+y", "-y", "+z", "-z" };
        bool all = true;
        for (int i = 0; i < six.Length; i++)
            all &= HandAxis.TryParse(six[i], out HandAxis a) && a.Index == i / 2 && a.Negative == (i % 2 == 1) && a.ToString() == six[i];
        Check("axis: the six names parse, and print back", all);
        Check("axis: case and spaces do not matter", HandAxis.TryParse(" -Y ", out HandAxis my) && my.Index == 1 && my.Negative);
        Check("axis: anything else is refused", new[] { "x", "+w", "", "  ", "++x", "+x+", "1", "-", "x+" }.All(t => !HandAxis.TryParse(t, out _)) && !HandAxis.TryParse(null, out _));
        Check("axis: Direction is the named axis in the controller's own space, turned round for -",
            NearV(new HandAxis(1, true).Direction, -y) && NearV(new HandAxis(2, false).Direction, z) && NearV(new HandAxis(0, true).Direction, -x));

        // Ruling 36: an axis setting is a signed axis or any direction as three numbers, made length 1
        static bool Dir(string? text, out V3 v) => HandAxes.TryParse(text, out v, out _);
        Check("axis direction: -y and 0,-1,0 are the same direction", Dir("-y", out V3 ny) && Dir("0,-1,0", out V3 ny3) && NearV(ny, ny3) && NearV(ny, -y));
        Check("axis direction: every signed axis reads as its axis", Dir("+x", out V3 dx) && NearV(dx, x) && Dir("-z", out V3 dz) && NearV(dz, -z) && Dir(" +Y ", out V3 dy) && NearV(dy, y));
        V3 tilt = new V3(0f, -0.93f, 0.36f);
        Check("axis direction: 0,-0.93,0.36 comes out length 1, the same way", Dir("0,-0.93,0.36", out V3 t1) && Near(t1.Length, 1f) && NearV(t1, tilt * (1f / tilt.Length)));
        Check("axis direction: spaces round the numbers are allowed, and any length", Dir(" 0 , -0.93 ,0.36 ", out V3 t2) && NearV(t2, t1) && Dir("0,-9.3,3.6", out V3 t3) && NearV(t3, t1)
            && Dir("1e-1, 0, 0", out V3 t4) && NearV(t4, x));
        Check("axis direction: garbage, no direction, a count other than three, a number that is none are refused",
            new[] { "garbage", "0,0,0", "0, 0 ,0", "", "  ", "1,2", "1,2,3,4", "1,,2", "NaN,0,1", "0,Infinity,0", "1e40,0,0", "x,y,z", "(0,-1,0)", "-y,0,0" }.All(t => !Dir(t, out _)) && !Dir(null, out _));
        Check("axis direction: a comma decimal is refused", !Dir("0,-0,93,0,36", out _) && !Dir("0 -0,93 0,36", out _) && !Dir("0;-0,93;0,36", out _));
        Check("axis direction: a refusal says why", !HandAxes.TryParse("0,0,0", out _, out string whyZero) && whyZero.Length > 0 && !HandAxes.TryParse("garbage", out _, out string whyBad) && whyBad.Length > 0);
        var cultureNow = System.Globalization.CultureInfo.CurrentCulture;
        bool sameInGerman;
        try {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            sameInGerman = Dir("0,-0.93,0.36", out V3 tg) && NearV(tg, t1) && !Dir("0,-0,93,0,36", out _);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = cultureNow; }
        Check("axis direction: read in the invariant culture whatever the game's culture", sameInGerman);
        Check("axis direction: Describe names a signed axis, else three numbers that read back",
            HandAxes.Describe(-x) == "-x" && HandAxes.Describe(z) == "+z" && Dir(HandAxes.Describe(t1), out V3 back) && Math.Abs(back.X - t1.X) < 1e-3f && Math.Abs(back.Y - t1.Y) < 1e-3f && Math.Abs(back.Z - t1.Z) < 1e-3f);

        Check("axis: the defaults are the OpenXR grip (right palm -x, left palm +x, fingers +z)",
            NearV(HandAxes.PalmRight, -x) && NearV(HandAxes.FingersRight, z) && NearV(HandAxes.PalmLeft, x) && NearV(HandAxes.FingersLeft, z)
            && HandAxes.Describe(HandAxes.PalmRight) == "-x" && HandAxes.Describe(HandAxes.FingersRight) == "+z" && HandAxes.Describe(HandAxes.PalmLeft) == "+x" && HandAxes.Describe(HandAxes.FingersLeft) == "+z");
        var warnings = new List<string>();
        void Hand(string? palm, string? fingers, out V3 p, out V3 f, bool left = false) {
            warnings.Clear();
            if (left) HandAxes.Check("PalmAxisLeft", palm, "FingerAxisLeft", fingers, HandAxes.PalmLeft, HandAxes.FingersLeft, out p, out f, warnings);
            else HandAxes.Check("PalmAxisRight", palm, "FingerAxisRight", fingers, HandAxes.PalmRight, HandAxes.FingersRight, out p, out f, warnings);
        }
        bool Defaults(V3 p, V3 f, bool left = false) => NearV(p, left ? HandAxes.PalmLeft : HandAxes.PalmRight) && NearV(f, left ? HandAxes.FingersLeft : HandAxes.FingersRight);
        Hand("-x", "+z", out V3 p0, out V3 f0);
        Hand("+x", "+z", out V3 p0l, out V3 f0l, left: true);
        Check("axis check: the defaults pass", Defaults(p0, f0) && Defaults(p0l, f0l, left: true) && warnings.Count == 0);
        Hand("+y", "-z", out V3 p1, out V3 f1);
        Check("axis check: a good pair of signed axes is kept, no warning", NearV(p1, y) && NearV(f1, -z) && warnings.Count == 0);
        Hand("-1.00,0.07,0.05", "-0.05,-0.93,0.36", out V3 p2, out V3 f2);
        Check("axis check: the owner's fitted directions are kept, made length 1, no warning", warnings.Count == 0 && Near(p2.Length, 1f) && Near(f2.Length, 1f)
            && NearV(p2, new V3(-1f, 0.07f, 0.05f).Normalized) && NearV(f2, new V3(-0.05f, -0.93f, 0.36f).Normalized));
        Hand("+x", "0,-0.94,0.34", out V3 p3, out V3 f3, left: true);
        Check("axis check: a signed axis and three numbers together", warnings.Count == 0 && NearV(p3, x) && NearV(f3, new V3(0f, -0.94f, 0.34f).Normalized));
        Hand("garbage", "0,-0.93,0.36", out V3 p4, out V3 f4);
        Check("axis check: an unreadable palm: the hand falls back to its defaults, one warning naming the setting, what it said and what is used",
            Defaults(p4, f4) && warnings.Count == 1 && warnings[0].Contains("PalmAxisRight") && warnings[0].Contains("garbage") && warnings[0].Contains("-x") && warnings[0].Contains("+z"));
        Hand("+x", "0,0,0", out V3 p5, out V3 f5, left: true);
        Check("axis check: fingers of zero length: the hand falls back to its defaults, one warning", Defaults(p5, f5, left: true) && warnings.Count == 1 && warnings[0].Contains("FingerAxisLeft") && warnings[0].Contains("0,0,0"));
        Hand("0,0,1", "0,0.5,0.866", out V3 p6, out V3 f6);
        Check("axis check: a palm 30 degrees from its fingers: the hand falls back to its defaults, one warning naming both", Defaults(p6, f6) && warnings.Count == 1
            && warnings[0].Contains("PalmAxisRight") && warnings[0].Contains("FingerAxisRight") && warnings[0].Contains("30"));
        Hand("+y", "+y", out V3 p7, out V3 f7, left: true);
        Check("axis check: palm and fingers the same: the hand falls back to its defaults, one warning", Defaults(p7, f7, left: true) && warnings.Count == 1);
        Hand("+y", "-y", out V3 p8, out V3 f8, left: true);
        Check("axis check: palm and fingers opposite (on one axis, as before 0.4.3): the hand falls back to its defaults, one warning", Defaults(p8, f8, left: true) && warnings.Count == 1);
        Hand("0,0,1", "0,0.77,0.64", out V3 p9, out V3 f9);   // 50 degrees apart
        Check("axis check: 50 degrees apart is kept (only within 45 falls back)", warnings.Count == 0 && NearV(p9, z) && NearV(f9, new V3(0f, 0.77f, 0.64f).Normalized));
        Hand(null, "sideways", out V3 p10, out V3 f10);
        Check("axis check: both unreadable: the hand falls back to its defaults, one warning naming both", Defaults(p10, f10) && warnings.Count == 1
            && warnings[0].Contains("PalmAxisRight") && warnings[0].Contains("FingerAxisRight") && warnings[0].Contains("sideways"));

        // the two-frame empty test: one full frame is not "full" (it would start every pose over), two in a row are
        var gate = new EmptyGate();
        bool[] seen = new[] { true, false, true, false, false, false, true }.Select(e => gate.Step(e)).ToArray();
        Check("empty gate: one full frame reads empty, the second full frame in a row reads full, an empty frame empty at once",
            seen.SequenceEqual(new[] { true, true, true, true, false, false, true }));
        gate.Step(false);
        gate.Reset();
        Check("empty gate: Reset forgets a full frame", gate.Step(false));
        // a Jump held 1.5 s, with something in a hand for one frame at 0.7 s: through the gate it fires once; fed raw, the blip
        // starts the pose over and it fires again once its 0.6 s cooldown is over
        Seg[] blip = Seq(Hold(Move.Jump, 0.7 + Dt / 2), Holding(Hold(Move.Jump, Dt)), Hold(Move.Jump, 0.8));   // the blip is frame 64 alone
        Check("empty gate: a one-frame blip does not fire a held jump again", RunGated(M(), blip, 1.5, new EmptyGate(), null).SequenceEqual(new[] { Move.Jump })
            && Run(M(), blip, 1.5).SequenceEqual(new[] { Move.Jump, Move.Jump }));
        Check("empty gate: two full frames still start over", RunGated(M(), Seq(Hold(Move.Jump, 0.7 + Dt / 2), Holding(Hold(Move.Jump, 2 * Dt)), Hold(Move.Jump, 0.8)), 1.5, new EmptyGate(), null)
            .SequenceEqual(new[] { Move.Jump, Move.Jump }));

        // the ground grace (ruling 16): grounded within the last 0.15 s, on the ground that was under the feet then
        var grace = new GroundGrace(0.15f);
        Check("grace: never grounded is not grounded", !grace.Step(0.0, false, false, out _));
        Check("grace: grounded on natural ground", grace.Step(1.0, true, true, out bool n1) && n1);
        Check("grace: 0.15 s after the last grounded frame still grounded, on that frame's ground", grace.Step(1.15, false, false, out bool n2) && n2);
        Check("grace: past 0.15 s not grounded", !grace.Step(1.16, false, false, out _));
        grace.Step(2.0, true, false, out _);
        Check("grace: the ground is the last grounded frame's (wood, then the air over grass)", grace.Step(2.05, false, true, out bool n3) && !n3);
        grace.Reset();
        Check("grace: Reset forgets", !grace.Step(2.06, false, false, out _));
        grace.Seconds = 0f;
        grace.Step(3.0, true, true, out _);
        Check("grace 0: only a grounded frame is grounded", grace.Step(3.0, false, false, out _) && !grace.Step(3.0 + Dt, false, false, out _));
        // the grounded flag of the game flickers; every 5th frame off, and idle hands until frame 10, put the frame the Jump is
        // judged on (its first, frame 10) in the air
        Func<int, bool> flicker = f => f % 5 != 0;
        Seg[] lateJump = Seq(Idle(0.1 + Dt / 2), Hold(Move.Jump, 0.5));
        Check("grace: a jump struck on a flicker of the ground fires once; without the grace the flicker eats it",
            RunGated(M(), lateJump, 0.6, null, new GroundGrace(0.15f), flicker).SequenceEqual(new[] { Move.Jump })
            && RunGated(M(), lateJump, 0.6, null, null, flicker).Count == 0);

        // poses.txt over the defaults: the file's pose replaces the default of the same move, in the defaults' order; a pose
        // the file leaves out, or whose line did not parse, stays the default
        PoseSpec[] d = PoseLibrary.Defaults;
        PoseSpec[] none = PoseLibrary.Merge(d, null, out int r0);
        Check("merge: no file is the defaults", r0 == 0 && Equal(none, d));
        string wall = Tail(PoseLibrary.Format(new[] { Spec(Move.Wall) })[2], "0.3 0.2 0.3 60 60");
        string jumpBad = Tail(PoseLibrary.Format(new[] { Spec(Move.Jump) })[2], "0.18 40");
        PoseSpec[] file = PoseLibrary.Parse(new[] { "# mine", jumpBad, wall }, out List<string> fileErrors);
        PoseSpec[] merged = PoseLibrary.Merge(d, file, out int r1);
        Check("merge: a file pose replaces its default in place; a bad line keeps the default", r1 == 1 && fileErrors.Count == 1 && merged.Length == 16
            && merged.Select(s => s.Move).SequenceEqual(d.Select(s => s.Move)) && merged[(int)Move.Wall - 1].A.TolX == 0.3f
            && Equal(merged[(int)Move.Jump - 1].A, Spec(Move.Jump).A) && merged.Where(s => s.Move != Move.Wall).All(s => Equal(s.A, Spec(s.Move).A) && Equal(s.B, Spec(s.Move).B)));
        Check("merge: the defaults are not changed", Equal(d, PoseLibrary.Defaults));

        // a push (ruling 14): adds to the velocity; on the ground y at least 0.11 and the fall count starts there; in the air it
        // only adds; never faster than the cap
        Check("push: a jump on the ground", NearV(PushRule.Next(V3.Zero, new V3(0f, 6f, 0f), true, 20f), new V3(0f, 6f, 0f)));
        Check("push: a flat push on the ground lifts 0.11", NearV(PushRule.Next(V3.Zero, new V3(8f, 0f, 0f), true, 20f), new V3(8f, 0.11f, 0f)));
        Check("push: in the air it only adds (a fall goes on)", NearV(PushRule.Next(new V3(0f, -5f, 0f), new V3(2f, 0f, 0f), false, 20f), new V3(2f, -5f, 0f)));
        Check("push: pushes stack", NearV(PushRule.Next(new V3(1f, 2f, 0f), new V3(0f, 6f, 3f), true, 20f), new V3(1f, 8f, 3f)));
        Check("push: capped at the max speed, the direction kept", Near(PushRule.Next(new V3(0f, 15f, 0f), new V3(0f, 15f, 0f), false, 20f).Length, 20f)
            && NearV(PushRule.Next(new V3(0f, 15f, 0f), new V3(0f, 15f, 0f), false, 20f), new V3(0f, 20f, 0f)));
        V3 fast = PushRule.Next(V3.Zero, new V3(30f, 0f, 0f), true, 20f);
        Check("push: capped, then lifted on the ground", Near(fast.X, 20f) && Near(fast.Y, 0.11f));
        Check("push: a broken velocity is replaced by the push", NearV(PushRule.Next(new V3(float.NaN, 0f, 0f), new V3(0f, 6f, 0f), false, 20f), new V3(0f, 6f, 0f)));
        Check("push: a cap that is not above 0 caps nothing", Near(PushRule.Next(V3.Zero, new V3(0f, 30f, 0f), false, 0f).Y, 30f) && Near(PushRule.Next(V3.Zero, new V3(0f, 30f, 0f), false, float.NaN).Y, 30f));
    }

    /// <summary>As <see cref="Run(PoseMachine, Seg[], double, bool, bool)"/>, with the game's side in between: the hands-full
    /// flag through <paramref name="gate"/> and the ground (grounded per frame number from <paramref name="groundedAt"/>,
    /// always natural) through <paramref name="grace"/> (null: fed raw).</summary>
    static List<Move> RunGated(PoseMachine m, Seg[] script, double seconds, EmptyGate? gate, GroundGrace? grace, Func<int, bool>? groundedAt = null) {
        var fired = new List<Move>();
        int frames = (int)Math.Round(seconds / Dt);
        for (int n = 0; n < frames; n++) {
            double t = n * Dt;
            Seg seg = new Seg { Pose = Move.None };
            double start = 0;
            foreach (Seg s in script) {
                if (t < start + s.Dur) { seg = s; break; }
                start += s.Dur;
            }
            var (r, l) = seg.Pose == Move.None ? (IdleR, IdleL) : At(seg.Pose, seg.Mirrored);
            bool empty = gate != null ? gate.Step(!seg.Full) : !seg.Full;
            bool rawGrounded = groundedAt == null || groundedAt(n);
            bool natural = true;
            bool grounded = grace != null ? grace.Step(t, rawGrounded, true, out natural) : rawGrounded;
            Move mv = m.Step(t, r, l, empty, grounded, natural, seg.Stunned, out _);
            if (mv != Move.None) fired.Add(mv);
        }
        return fired;
    }

    static void V3Checks() {
        V3 zero = V3.Zero.Normalized;
        Check("V3: zero Normalized is zero, not NaN", zero.X == 0f && zero.Y == 0f && zero.Z == 0f);
        V3 nan = new V3(float.NaN, 1f, 0f).Normalized;
        Check("V3: NaN Normalized is zero", nan.X == 0f && nan.Y == 0f && nan.Z == 0f);
        Check("V3: Normalized has length 1", Near(new V3(3f, 0f, 4f).Normalized.Length, 1f) && Near(new V3(3f, 0f, 4f).Normalized.X, 0.6f));
        V3 m = new V3(1f, 2f, 3f).MirrorX();
        Check("V3: MirrorX flips x only", m.X == -1f && m.Y == 2f && m.Z == 3f);
        V3 a = new V3(1f, 2f, 3f), b = new V3(4f, -5f, 6f);
        Check("V3: operators", NearV(a + b, new V3(5f, -3f, 9f)) && NearV(a - b, new V3(-3f, 7f, -3f)) && NearV(-a, new V3(-1f, -2f, -3f))
            && NearV(a * 2f, new V3(2f, 4f, 6f)) && NearV(2f * a, new V3(2f, 4f, 6f)) && Near(V3.Dot(a, b), 12f) && Near(new V3(3f, 4f, 0f).Length, 5f));
    }

    // the pose recorder (Task 3): when a snapshot is taken, and what its row holds
    static void CaptureChecks() {
        // the head stands still at H; the hands r0 / l0 are 0.6 m under it (low, but no rest: that is 0.7 m under the head)
        V3 H = new V3(1f, 1.4f, -3f);
        // steps a gate at 90 Hz: each hand at a place given by the time; returns the times snapshots were taken (and, into
        // rests, the times a rest was seen)
        List<double> Snaps(CaptureGate g, double seconds, Func<double, V3> right, Func<double, V3> left, double from = 0.0, List<double>? rests = null) {
            var at = new List<double>();
            int frames = (int)Math.Round(seconds / Dt);
            for (int i = 0; i <= frames; i++) {
                double t = from + i * Dt;
                CaptureEvent e = g.Step(t, H, right(t), left(t));
                if (e == CaptureEvent.Snapshot) at.Add(t);
                if (e == CaptureEvent.Rest) rests?.Add(t);
            }
            return at;
        }
        static CaptureGate G() => new CaptureGate(0.01f, 0.6f, 0.2f, 0.7f);
        V3 r0 = new V3(1.25f, 0.8f, -3.05f), l0 = new V3(0.75f, 0.8f, -3.05f), r1 = r0 + new V3(0f, 0.45f, 0.3f);
        Func<double, V3> restR = _ => r0, restL = _ => l0;
        Func<double, V3> raiseR = t => t < 0.3 ? r0 + (r1 - r0) * (float)(t / 0.3) : r1;   // the right hand raised 0.54 m in 0.3 s, then still

        Check("capture: hands still from the start take no snapshot (they must move into a pose first)", Snaps(G(), 3.0, restR, restL).Count == 0);
        List<double> one = Snaps(G(), 3.0, raiseR, restL);
        Check("capture: a hand moved into a pose and held still: one snapshot 0.6 s after it stopped", one.Count == 1 && one[0] >= 0.9 - 1e-6 && one[0] < 0.9 + 2 * Dt);
        CaptureGate g = G();
        Snaps(g, 3.0, raiseR, restL);
        Check("capture: still after the snapshot, no second one", Snaps(g, 5.0, _ => r1, restL, 3.0 + Dt).Count == 0);
        Check("capture: moved 0.25 m away (for 0.5 s, too short to count) and back, a second one", Snaps(g, 3.0, t => t < 8.5 ? r1 + new V3(0f, -0.25f, 0f) : r1, restL, 8.0 + Dt).Count == 1);
        Check("capture: either hand: the left alone moved", Snaps(G(), 3.0, restR, t => t < 0.3 ? l0 + new V3(0f, (float)t, 0f) : l0 + new V3(0f, 0.3f, 0f)).Count == 1);
        Check("capture: a move of 0.15 m is not a new pose", Snaps(G(), 3.0, t => t < 0.3 ? r0 + new V3(0f, 0.5f * (float)t, 0f) : r0 + new V3(0f, 0.15f, 0f), restL).Count == 0);
        Check("capture: a hand creeping 2 cm/s is never still", Snaps(G(), 4.0, t => t < 0.3 ? r0 + (r1 - r0) * (float)(t / 0.3) : r1 + new V3(0.02f * (float)(t - 0.3), 0f, 0f), restL).Count == 0);
        var rng = new Random(7);
        Func<double, V3> shaky = t => (t < 0.3 ? r0 + (r1 - r0) * (float)(t / 0.3) : r1) + new V3((float)(rng.NextDouble() - 0.5) * 0.004f, (float)(rng.NextDouble() - 0.5) * 0.004f, (float)(rng.NextDouble() - 0.5) * 0.004f);
        Check("capture: tremor of 2 mm each way is still", Snaps(G(), 3.0, shaky, restL).Count == 1);
        Check("capture: the other hand moving holds it off", Snaps(G(), 3.0, raiseR, t => l0 + new V3(0f, 0f, 0.1f * (float)Math.Sin(t * 6.0))).Count == 0);
        CaptureGate lost = G();
        var lostAt = Snaps(lost, 0.6, raiseR, restL);
        bool nanSnap = lost.Step(0.6 + Dt, H, new V3(float.NaN, 0f, 0f), l0) != CaptureEvent.None;
        List<double> after = Snaps(lost, 2.0, raiseR, restL, 0.6 + 2 * Dt);
        Check("capture: a lost hand (NaN) takes no snapshot and restarts the stillness, not the arming", lostAt.Count == 0 && !nanSnap && after.Count == 1 && after[0] >= 1.2 + 2 * Dt - 1e-6);
        CaptureGate reset = G();
        Snaps(reset, 0.6, raiseR, restL);
        reset.Reset();
        Check("capture: Reset forgets the move: the hands must move again", Snaps(reset, 3.0, _ => r1, restL, 0.6 + Dt).Count == 0);

        // rests (fix round 1): hands hanging at the sides are never a pose, and they arm the next one
        V3 hangR = H + new V3(0.25f, -1.0f, 0.05f), hangL = H + new V3(-0.25f, -1.0f, 0.05f), poseR = H + new V3(0.2f, -0.3f, 0.4f), poseL = H + new V3(-0.2f, -0.3f, 0.4f);
        Func<double, V3> Glide(V3 from, V3 to, double start) => t => t < start ? from : t < start + 0.3 ? from + (to - from) * (float)((t - start) / 0.3) : to;
        var rested = new List<double>();
        Check("capture rest: hands lowered from a pose and hanging 1.0 m under the head for 2 s: no snapshot, one rest",
            Snaps(G(), 2.3, Glide(poseR, hangR, 0.0), Glide(poseL, hangL, 0.0), 0.0, rested).Count == 0 && rested.Count == 1);
        Check("capture rest: hanging from the start: no snapshot", Snaps(G(), 3.0, _ => hangR, _ => hangL).Count == 0);
        var cycle = new List<double>();
        CaptureGate back = G();
        List<double> twice = Snaps(back, 6.0, t => t < 2.0 ? Glide(hangR, poseR, 0.0)(t) : t < 4.0 ? Glide(poseR, hangR, 2.0)(t) : Glide(hangR, poseR + new V3(0.05f, 0f, 0f), 4.0)(t),
            t => t < 2.0 ? Glide(hangL, poseL, 0.0)(t) : t < 4.0 ? Glide(poseL, hangL, 2.0)(t) : Glide(hangL, poseL + new V3(0f, -0.05f, 0f), 4.0)(t), 0.0, cycle);
        Check("capture rest: a pose, a rest, then the next pose 5 cm from the first: two snapshots and the rest between",
            twice.Count == 2 && twice[0] < 2.0 && twice[1] > 4.0 && cycle.Count == 1 && cycle[0] > 2.0 && cycle[0] < 4.0);
        PoseSpec jump = Spec(Move.Jump);
        Check("capture rest: Jump's default hands (the low one 0.72 m under the head) snapshot",
            Snaps(G(), 2.0, Glide(hangR, H + jump.A.Pos, 0.0), Glide(hangL, H + jump.B.Pos, 0.0)).Count == 1);
        Check("capture rest: one hand hanging and the other raised is no rest",
            Snaps(G(), 2.0, _ => hangR, Glide(hangL, H + new V3(-0.2f, 0.1f, 0.1f), 0.0)).Count == 1);
        PoseSpec stomp = Spec(Move.Stomp);
        Check("capture rest: Stomp's default hands (both 0.55 m under the head, the lowest pair) snapshot",
            Snaps(G(), 2.0, Glide(hangR, H + stomp.A.Pos, 0.0), Glide(hangL, H + stomp.B.Pos, 0.0)).Count == 1);
        Check("capture rest: every default pose keeps a hand 0.1 m or more above the rest line, written or mirrored",
            PoseLibrary.Defaults.All(s => Math.Max(s.A.Pos.Y, s.B.Pos.Y) >= -0.7f + 0.1f));
        Check("capture: a head that is not a number takes nothing", G().Step(0.0, new V3(0f, float.NaN, 0f), r0, l0) == CaptureEvent.None);

        // a hand's record: the controller's place and axes through the body frame. Facing +X (yaw 90), a controller turned
        // the same way as the head, 0.5 m ahead and 0.3 m down, is the body frame's own axes
        V3 head = new V3(1f, 2f, 3f);
        CaptureHand h = PoseCapture.Hand(head, 90f, head + new V3(0.5f, -0.3f, 0f), new V3(0f, 0f, -1f), new V3(0f, 1f, 0f), new V3(1f, 0f, 0f));
        Check("capture hand: place and axes in the body frame", NearV(h.Pos, new V3(0f, -0.3f, 0.5f)) && NearV(h.X, new V3(1f, 0f, 0f)) && NearV(h.Y, new V3(0f, 1f, 0f)) && NearV(h.Z, new V3(0f, 0f, 1f)));
        CaptureHand tilted = PoseCapture.Hand(V3.Zero, 0f, new V3(0.2f, -0.5f, 0.3f), new V3(2f, 0f, 0f), new V3(0f, 0f, -3f), new V3(0f, 0.5f, 0f));
        Check("capture hand: axes made length 1, the place not scaled", NearV(tilted.Pos, new V3(0.2f, -0.5f, 0.3f)) && NearV(tilted.X, new V3(1f, 0f, 0f)) && NearV(tilted.Y, new V3(0f, 0f, -1f)) && NearV(tilted.Z, new V3(0f, 1f, 0f)));

        string[] columns = PoseCapture.Header.Split(',');
        Check("capture header: n, t, the head, yaw, then 12 per hand", columns.Length == 30
            && string.Join(",", columns.Take(9)) == "n,t,headX,headY,headZ,yaw,rightX,rightY,rightZ"
            && string.Join(",", columns.Skip(9).Take(9)) == "rightXaxisX,rightXaxisY,rightXaxisZ,rightYaxisX,rightYaxisY,rightYaxisZ,rightZaxisX,rightZaxisY,rightZaxisZ"
            && string.Join(",", columns.Skip(18)) == "leftX,leftY,leftZ,leftXaxisX,leftXaxisY,leftXaxisZ,leftYaxisX,leftYaxisY,leftYaxisZ,leftZaxisX,leftZaxisY,leftZaxisZ");
        var ra = new CaptureHand { Pos = new V3(0.25f, -0.5f, 0.125f), X = new V3(1f, 0f, 0f), Y = new V3(0f, 0.6f, 0.8f), Z = new V3(0f, -0.8f, 0.6f) };
        var la = new CaptureHand { Pos = new V3(-0.25f, -0.5f, 0.375f), X = new V3(0f, 0f, 1f), Y = new V3(0f, 1f, 0f), Z = new V3(-1f, 0f, 0f) };
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        string row;
        try {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");   // a decimal comma would split numbers
            row = PoseCapture.Row(3, 12.3456, new V3(-835.5f, 214.25f, 386.75f), -66.5f, ra, la);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
        string[] cells = row.Split(',');
        Check("capture row: 30 numbers in the invariant culture, each in its column", cells.Length == 30 && cells[0] == "3" && cells[1] == "12.346" && cells[2] == "-835.5" && cells[5] == "-66.5"
            && cells[6] == "0.25" && cells[8] == "0.125" && cells[13] == "0.6" && cells[14] == "0.8" && cells[16] == "-0.8" && cells[18] == "-0.25" && cells[20] == "0.375"
            && cells[23] == "1" && cells[26] == "0" && cells[27] == "-1" && cells[29] == "0");
    }

    // ----- not a test: `dotnet run --project Earthbending.Tests -c Release -- --overlaps [poses.txt]` -----

    /// <summary>Prints every pair of poses that one pair of hands can strike together (each hand inside both poses'
    /// tolerances, as written or mirrored), with a witness pair of hands checked through <see cref="PoseMatch.Match"/>, and
    /// every pose whose own exact hands another pose also matches. The default poses, or those of a poses.txt (the owner's
    /// capture: run it on fit_poses.py's file).</summary>
    internal static int PrintOverlaps(string? path) {
        PoseSpec[] specs = PoseLibrary.Defaults;
        if (path != null) {
            if (!ReadPoses(path, out specs, out List<string> problems)) {
                foreach (string p in problems) Console.WriteLine("ERROR " + p);
                return 1;
            }
            Console.WriteLine($"{specs.Length} poses from {path}");
        }
        var orients = Orientations(2000, 36);
        int regions = 0, exact = 0;
        for (int i = 0; i < specs.Length; i++)
            for (int j = i + 1; j < specs.Length; j++)
                for (int mirrored = 0; mirrored < (Symmetric(specs[j]) ? 1 : 2); mirrored++) {
                    PoseSpec q = mirrored == 1 ? specs[j].Mirrored() : specs[j];
                    HandSpec qr = mirrored == 1 ? q.B : q.A, ql = mirrored == 1 ? q.A : q.B;   // mirrored: q's A is on the left
                    if (!Witness(specs[i].A, qr, orients, out HandSample wr) || !Witness(specs[i].B, ql, orients, out HandSample wl)) continue;
                    bool p = PoseMatch.Match(specs[i], wr, wl, out _, out float sp), o = PoseMatch.Match(specs[j], wr, wl, out _, out float so);
                    regions++;
                    Console.WriteLine($"OVERLAP {specs[i].Move} / {specs[j].Move}{(mirrored == 1 ? " mirrored" : "")}: witness scores {sp:0.00} / {so:0.00} (both match: {p && o}); right {wr.Pos} palm {wr.Palm} fingers {wr.Fingers}, left {wl.Pos} palm {wl.Palm} fingers {wl.Fingers}");
                }
        foreach (PoseSpec s in specs)
            for (int mirrored = 0; mirrored < (Symmetric(s) ? 1 : 2); mirrored++) {
                var (r, l) = At(s, mirrored == 1);
                foreach (PoseSpec o in specs) {
                    if (o.Move == s.Move || !PoseMatch.Match(o, r, l, out _, out float sc)) continue;
                    exact++;
                    Console.WriteLine($"EXACT {s.Move}{(mirrored == 1 ? " mirrored" : "")}'s own hands also match {o.Move} (score {sc.ToString("R", System.Globalization.CultureInfo.InvariantCulture)})");
                }
            }
        Console.WriteLine($"{regions} overlapping pair(s), {exact} exact hit(s) among {specs.Length} poses");
        return 0;
    }

    // ----- not a test: `-- --check-poses <poses.txt>`, the check that fit_poses.py writes what PoseLibrary.Parse reads -----

    /// <summary>Reads a poses.txt with <see cref="PoseLibrary.Parse"/>: <c>POSES OK (16)</c> and 0 when it holds the sixteen
    /// poses and no line was skipped, else each problem and 1.</summary>
    internal static int CheckPoses(string? path) {
        if (path == null) { Console.WriteLine("usage: --check-poses <poses.txt>"); return 2; }
        if (ReadPoses(path, out PoseSpec[] poses, out List<string> problems)) {
            Console.WriteLine($"POSES OK ({poses.Length})");
            return 0;
        }
        foreach (string p in problems) Console.WriteLine("ERROR " + p);
        Console.WriteLine($"POSES FAILED ({problems.Count} problem(s), {poses.Length} pose(s) read from {path})");
        return 1;
    }

    /// <summary>A poses.txt through <see cref="PoseLibrary.Parse"/>; true when no line was skipped and all sixteen poses are there.</summary>
    static bool ReadPoses(string path, out PoseSpec[] poses, out List<string> problems) {
        string[] lines;
        try { lines = System.IO.File.ReadAllLines(path); }
        catch (Exception e) {
            poses = new PoseSpec[0];
            problems = new List<string> { $"cannot read {path}: {e.Message}" };
            return false;
        }
        poses = PoseLibrary.Parse(lines, out problems);
        PoseSpec[] read = poses;
        var missing = Enumerable.Range((int)Move.Base, 16).Select(i => (Move)i).Where(m => !read.Any(p => p.Move == m)).ToList();
        if (missing.Count > 0) problems.Add("no line for " + string.Join(", ", missing));
        return problems.Count == 0;
    }

    // ----- not a test: `-- --capture-sample <file.csv>`, a capture as the recorder writes it, for fit_poses.py -----

    /// <summary>Writes a made-up capture of the sixteen default poses through <see cref="PoseCapture"/>, as Hands.cs's recorder
    /// writes one: A on the right hand, each controller's palm on its local -Y and its fingers on +Z, struck facing yaw 30 at
    /// a head far from the origin (so the body frame is not the world's). fit_poses.py on it must find -y / +z and the poses.</summary>
    internal static int WriteCaptureSample(string? path) {
        if (path == null) { Console.WriteLine("usage: --capture-sample <file.csv>"); return 2; }
        V3 head = new V3(-835.6f, 216.0f, 386.9f);
        const float yaw = 30f;
        CaptureHand Record(in HandSpec h) {
            V3 n = h.Palm.Normalized, f = (h.Fingers - n * V3.Dot(h.Fingers, n)).Normalized;
            V3 y = -n, z = f, x = Cross(y, z);   // a turn: right = up x forward
            return PoseCapture.Hand(head, yaw, head + RotY(h.Pos, yaw), RotY(x, yaw), RotY(y, yaw), RotY(z, yaw));
        }
        var lines = new List<string> { PoseCapture.Header };
        PoseSpec[] defaults = PoseLibrary.Defaults;
        for (int i = 0; i < defaults.Length; i++)
            lines.Add(PoseCapture.Row(i + 1, 3.0 * (i + 1), head, yaw, Record(defaults[i].A), Record(defaults[i].B)));
        try { System.IO.File.WriteAllLines(path, lines); }
        catch (Exception e) { Console.WriteLine($"cannot write {path}: {e.Message}"); return 1; }
        Console.WriteLine($"wrote {defaults.Length} snapshots to {path}");
        return 0;
    }

    /// <summary>A pose that is its own mirror image (Base, Cube, Parry ...): its mirrored case is the same case.</summary>
    static bool Symmetric(PoseSpec s) {
        PoseSpec m = s.Mirrored();
        return Equal(m.A, s.B) && Equal(m.B, s.A);
    }

    /// <summary>Palm and finger directions, square to each other, spread evenly over every way a hand can turn.</summary>
    static List<(V3 n, V3 f)> Orientations(int fingers, int turns) {
        var list = new List<(V3, V3)>(fingers * turns);
        double golden = Math.PI * (3.0 - Math.Sqrt(5.0));
        for (int i = 0; i < fingers; i++) {
            double y = 1.0 - 2.0 * (i + 0.5) / fingers, r = Math.Sqrt(1.0 - y * y), t = golden * i;
            V3 f = new V3((float)(r * Math.Cos(t)), (float)y, (float)(r * Math.Sin(t)));
            V3 side = Math.Abs(f.Y) < 0.9f ? new V3(0f, 1f, 0f) : new V3(1f, 0f, 0f);
            V3 n0 = Cross(f, side).Normalized;
            for (int k = 0; k < turns; k++) list.Add((Rot(n0, f, 360f * k / turns), f));
        }
        return list;
    }

    /// <summary>A hand inside both specs, if there is one: the place on the line between the two centres where it is as deep
    /// in one zone as in the other (exactly the deepest place when the two zones have the same half-sizes, as the defaults
    /// and fit_poses.py's poses do), and the way of turning it that is deepest inside both.</summary>
    static bool Witness(HandSpec a, HandSpec b, List<(V3 n, V3 f)> orients, out HandSample w) {
        w = default;
        float depth = float.PositiveInfinity;
        V3 d = b.Pos - a.Pos;
        float na = PoseMatch.Zone(a, d), nb = PoseMatch.Zone(b, d);   // each zone's distance of the whole way between the centres
        V3 pos = a.Pos;
        if (d.Length > 0f) {
            if (!(na * nb / (na + nb) <= 1f)) return false;            // the deepest place on the line is outside a zone
            pos = a.Pos + d * (nb / (na + nb));                         // a's distance t * na equals b's (1 - t) * nb
        }
        foreach (var (n, f) in orients) {
            var x = new HandSample(pos, n, f);
            float s = Math.Max(PoseMatch.Score(a, x), PoseMatch.Score(b, x));
            if (s < depth) { depth = s; w = x; }
        }
        return depth <= 1f;
    }
}
