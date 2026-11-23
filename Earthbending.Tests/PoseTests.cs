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
        BodyScaleChecks();

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

        // the machine: entry (the first frame inside a pose counts: flow, not hold), rearm
        Check("a hand passing through a pose for one frame fires it once", Run(M(), OneFrame(Move.Jump), 0.5).SequenceEqual(new[] { Move.Jump }));
        Check("held jump fires once", Run(M(), Hold(Move.Jump, 0.3), 0.3).SequenceEqual(new[] { Move.Jump }));
        var held = new PoseValues { MinHoldSeconds = 0.05f, RearmSeconds = 0.1f, StructureWindowSeconds = 1.2f, MoveCooldownSeconds = 0.6f, Cooldown = PoseValues.Default.Cooldown };
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
        Check("values: the spec's numbers (no hold: passing through counts)", pv.MinHoldSeconds == 0f && Near(pv.RearmSeconds, 0.1f) && Near(pv.StructureWindowSeconds, 1.2f) && Near(pv.MoveCooldownSeconds, 0.6f));
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

    // the measured body scale ([EarthPose] BodyScale 0): standing eye height over 1.63 m, held to 0.7..1.3
    static void BodyScaleChecks() {
        const float frame = 1f / 90f;
        static void Feed(BodyScaleEstimate e, double seconds, float height, bool grounded = true) {
            for (int i = 0, n = (int)Math.Round(seconds * 90.0); i < n; i++) e.Step(1f / 90f, height, grounded);
        }
        var est = new BodyScaleEstimate();
        Check("body scale: it starts at 1 (the design player's 1.63 m)", est.Scale == 1f && est.Height == 1.63f);
        Feed(est, 30, 1.80f);
        Check("body scale: standing at 1.80 m settles at about 1.10", Math.Abs(est.Scale - 1.80f / 1.63f) < 0.002f && Math.Abs(est.Scale - 1.10f) < 0.01f);
        float settled = est.Scale;
        Feed(est, 10, 1.20f);
        Check("body scale: a crouch (under 85% of the estimate) does not lower it", est.Scale == settled);
        Feed(est, 10, 2.6f, grounded: false);
        Check("body scale: a sample in the air is ignored", est.Scale == settled);
        for (int i = 0; i < 90; i++) { est.Step(frame, float.NaN, true); est.Step(frame, float.PositiveInfinity, true); est.Step(float.NaN, 2f, true); est.Step(-1f, 2f, true); }
        Check("body scale: a NaN or infinite sample, or a broken frame time, is ignored", est.Scale == settled && !float.IsNaN(est.Scale));

        var fresh = new BodyScaleEstimate();
        fresh.Step(frame, 1.9f, true);
        Check("body scale: one frame of a bad sample hardly moves it", Math.Abs(fresh.Scale - 1f) < 0.001f);
        fresh.Step(30f, 1.9f, true);
        Check("body scale: a long frame (a hitch) moves it no more than a short one would", Math.Abs(fresh.Scale - 1f) < 0.01f);
        var climb = new BodyScaleEstimate();
        Feed(climb, 1, 2.2f);
        Check("body scale: it moves at most a few cm per second", climb.Height - 1.63f <= 0.05f && climb.Height > 1.63f);

        var tall = new BodyScaleEstimate();
        Feed(tall, 60, 2.6f);
        Check("body scale: clamped at 1.3", tall.Scale == 1.3f);
        var small = new BodyScaleEstimate();
        for (int i = 0; i < 90 * 60; i++) small.Step(frame, Math.Max(0.9f, 1.63f - 0.02f * i * frame), true);   // getting smaller at 2 cm/s, each sample within 85%
        Check("body scale: clamped at 0.7", small.Scale == 0.7f);
        Feed(small, 60, 1.63f);
        Check("body scale: from the floor of the clamp it comes back up to a standing 1.63 m", Math.Abs(small.Scale - 1f) < 0.002f);
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
        Check("axis: Pick takes the named axis, turned round for -", NearV(new HandAxis(1, true).Pick(x, y, z), -y) && NearV(new HandAxis(2, false).Pick(x, y, z), z) && NearV(new HandAxis(0, true).Pick(x, y, z), -x));
        Check("axis: the defaults are the OpenXR grip (right palm -x, left palm +x, fingers +z), each hand on two axes",
            HandAxes.PalmRight.ToString() == "-x" && HandAxes.FingersRight.ToString() == "+z" && HandAxes.PalmLeft.ToString() == "+x" && HandAxes.FingersLeft.ToString() == "+z"
            && HandAxes.PalmRight.Index != HandAxes.FingersRight.Index && HandAxes.PalmLeft.Index != HandAxes.FingersLeft.Index);
        var warnings = new List<string>();
        HandAxes.Check("PalmAxisRight", "+y", "FingerAxisRight", "-z", HandAxes.PalmRight, HandAxes.FingersRight, out HandAxis p1, out HandAxis f1, warnings);
        Check("axis check: a good pair is kept, no warning", p1.ToString() == "+y" && f1.ToString() == "-z" && warnings.Count == 0);
        warnings.Clear();
        HandAxes.Check("PalmAxisRight", "+q", "FingerAxisRight", "+y", HandAxes.PalmRight, HandAxes.FingersRight, out HandAxis p2, out HandAxis f2, warnings);
        Check("axis check: a bad name falls back to its default with one warning naming it", p2.ToString() == "-x" && f2.ToString() == "+y" && warnings.Count == 1
            && warnings[0].Contains("PalmAxisRight") && warnings[0].Contains("+q") && warnings[0].Contains("-x"));
        warnings.Clear();
        HandAxes.Check("PalmAxisLeft", "+y", "FingerAxisLeft", "-y", HandAxes.PalmLeft, HandAxes.FingersLeft, out HandAxis p3, out HandAxis f3, warnings);
        Check("axis check: palm and fingers on the same axis: the hand falls back to both defaults, one warning", p3.ToString() == "+x" && f3.ToString() == "+z" && warnings.Count == 1
            && warnings[0].Contains("PalmAxisLeft") && warnings[0].Contains("FingerAxisLeft"));
        warnings.Clear();
        HandAxes.Check("PalmAxisRight", "", "FingerAxisRight", "+x", HandAxes.PalmRight, HandAxes.FingersRight, out HandAxis p4, out HandAxis f4, warnings);
        Check("axis check: a bad palm whose default meets the fingers' axis: both defaults, a warning for each", p4.ToString() == "-x" && f4.ToString() == "+z" && warnings.Count == 2);
        warnings.Clear();
        HandAxes.Check("PalmAxisRight", null, "FingerAxisRight", "sideways", HandAxes.PalmRight, HandAxes.FingersRight, out HandAxis p5, out HandAxis f5, warnings);
        Check("axis check: both bad: both defaults, two warnings", p5.ToString() == "-x" && f5.ToString() == "+z" && warnings.Count == 2);

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
