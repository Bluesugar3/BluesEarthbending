using Earthbending;
using static Earthbending.StoneKind;
using static Program;

internal static class StoneTests {
    static UvRect[] StoneUvSix() {
        var r = new UvRect[StoneBox.Faces];
        for (int i = 0; i < r.Length; i++) r[i] = StoneUv.Slate(1f, 1f);
        return r;
    }

    static SummonClaim Claim(StoneKind kind, V3 head) => new SummonClaim(kind, head, 0f);

    internal static void Run() {
        // spec 3.4: the table's numbers
        Check("table wall", StoneTable.Of(StoneKind.Wall).Power == 3 && Near(StoneTable.Of(StoneKind.Wall).Mass, 400f));
        Check("table disc", StoneTable.Of(StoneKind.Disc).Power == 0 && Near(StoneTable.Of(StoneKind.Disc).Size.Y, 0.14f));
        // the chamfered block the Pillar, Cube and Wall are drawn with: 6 faces, 12 edge strips, 8 corners
        var box = StoneBox.Build(1f, 1f, 1f, 0.05f, StoneUvSix());   // six StoneUv.Slate rects
        Check("box bevelled", box.VertexCount == StoneBox.BevelledVertices && box.TriangleCount == StoneBox.BevelledTriangles);

        Summons();
        Straights();
        HitRules();
        OtherStones();
        Modifiers();
        ParryAndExplode();
        Holds();
        Flicks();
        DashAndHitstop();
    }

    // Task 15: Dash and hitstop (spec 2.2, 2.5, 3.6, 3.7; rulings C17-C20)
    static void DashAndHitstop() {
        // the brief's checks
        Check("hitstop: powers 3 and 1 freeze 0.14 s", Near(StoneRules.HitstopSeconds(3, 1), 0.14f));
        Check("breakable: a frozen stone is not broken by a contact", !StoneRules.Breakable(true) && StoneRules.Breakable(false));
        Check("dash: yaw 90 is (8, 0.5, 0)", NearV(MoveRules.Dash(90f, 8f, 0.5f), new V3(8f, 0.5f, 0f)));

        // the defaults (the settings': [EarthDamage] HitstopSeconds and HitstopPerPower, [EarthMovement] DashSpeed and DashLift)
        Check("defaults: hitstop 0.06 s + 0.02 s per power", Near(StoneRules.DefaultHitstopSeconds, 0.06f) && Near(StoneRules.DefaultHitstopPerPower, 0.02f)
            && Near(StoneRules.HitstopBase, 0.06f) && Near(StoneRules.HitstopPerPower, 0.02f));
        Check("defaults: dash 8 m/s, lift 0.5 m/s", Near(MoveRules.DefaultDashSpeed, 8f) && Near(MoveRules.DefaultDashLift, 0.5f));

        // spec 3.7: 0.06 + 0.02 x (powerA + powerB)
        Check("hitstop: powers 0 and 0 are the base alone", Near(StoneRules.HitstopSeconds(0, 0), 0.06f));
        Check("hitstop: powers 1 and 1", Near(StoneRules.HitstopSeconds(1, 1), 0.10f));
        Check("hitstop: a pushed wall (5) and a sliding cube (4)", Near(StoneRules.HitstopSeconds(5, 4), 0.24f));
        Check("hitstop: the order does not matter", Near(StoneRules.HitstopSeconds(1, 3), StoneRules.HitstopSeconds(3, 1)));
        Check("hitstop: given numbers", Near(StoneRules.HitstopSeconds(3, 1, 0.1f, 0.05f), 0.3f) && Near(StoneRules.HitstopSeconds(2, 2, 0f, 0.02f), 0.08f)
            && Near(StoneRules.HitstopSeconds(2, 2, 0.06f, 0f), 0.06f));

        // bad inputs: never negative, never a number that is not one
        Check("hitstop: a power below 0 counts as 0", Near(StoneRules.HitstopSeconds(-3, 1), 0.08f) && Near(StoneRules.HitstopSeconds(-5, -5), 0.06f));
        Check("hitstop: a base or a per-power below 0 is 0", Near(StoneRules.HitstopSeconds(3, 1, -1f, 0.02f), 0.08f) && Near(StoneRules.HitstopSeconds(3, 1, 0.06f, -1f), 0.06f)
            && StoneRules.HitstopSeconds(3, 1, -1f, -1f) == 0f);
        Check("hitstop: a base or a per-power that is not a number is 0", Near(StoneRules.HitstopSeconds(3, 1, float.NaN, 0.02f), 0.08f)
            && Near(StoneRules.HitstopSeconds(3, 1, 0.06f, float.NaN), 0.06f) && Near(StoneRules.HitstopSeconds(3, 1, float.PositiveInfinity, 0.02f), 0.08f)
            && Near(StoneRules.HitstopSeconds(3, 1, 0.06f, float.NegativeInfinity), 0.06f));
        float huge = StoneRules.HitstopSeconds(int.MaxValue, int.MaxValue, float.MaxValue, float.MaxValue);
        Check("hitstop: the largest powers and numbers stay a finite number", !float.IsNaN(huge) && !float.IsInfinity(huge) && huge > 0f);
        Check("hitstop: the smallest powers do not wrap round", Near(StoneRules.HitstopSeconds(int.MinValue, int.MinValue), 0.06f));

        // the settings replace both numbers; bad ones are never negative
        StoneRules.SetHitstop(0.1f, 0.05f);
        Check("hitstop: from the settings", Near(StoneRules.HitstopSeconds(3, 1), 0.3f) && Near(StoneRules.HitstopBase, 0.1f) && Near(StoneRules.HitstopPerPower, 0.05f));
        StoneRules.SetHitstop(0f, 0f);
        Check("hitstop: settings 0 and 0 freeze for no time", StoneRules.HitstopSeconds(3, 1) == 0f);
        StoneRules.SetHitstop(-1f, float.NaN);
        Check("hitstop: bad settings are never negative", StoneRules.HitstopSeconds(3, 1) == 0f && StoneRules.HitstopBase == 0f && StoneRules.HitstopPerPower == 0f);
        StoneRules.SetHitstop(StoneRules.DefaultHitstopSeconds, StoneRules.DefaultHitstopPerPower);
        Check("hitstop: defaults back", Near(StoneRules.HitstopSeconds(3, 1), 0.14f));

        // ruling C19: only a contact is refused on a frozen stone (its life, its owner, the quit, clear, a double Straight still break it)
        Check("breakable: the same answer every time", StoneRules.Breakable(false) == StoneRules.Breakable(false) && StoneRules.Breakable(true) == StoneRules.Breakable(true));

        // ruling C20: along the yaw at the speed, y the lift
        Check("dash: yaw 0 is (0, 0.5, 8)", NearV(MoveRules.Dash(0f, 8f, 0.5f), new V3(0f, 0.5f, 8f)));
        Check("dash: yaw -90 is (-8, 0.5, 0)", NearV(MoveRules.Dash(-90f, 8f, 0.5f), new V3(-8f, 0.5f, 0f)));
        Check("dash: yaw 180 is (0, 0.5, -8)", NearV(MoveRules.Dash(180f, 8f, 0.5f), new V3(0f, 0.5f, -8f)));
        V3 d30 = MoveRules.Dash(30f, 6f, 0.2f);
        Check("dash: yaw 30, flat speed 6, lift 0.2", Near((float)System.Math.Sqrt(d30.X * d30.X + d30.Z * d30.Z), 6f) && Near(d30.Y, 0.2f) && Near(d30.X, 3f));
        Check("dash: no speed is the lift alone", NearV(MoveRules.Dash(45f, 0f, 0.5f), new V3(0f, 0.5f, 0f)));
        Check("dash: no lift is flat", NearV(MoveRules.Dash(90f, 8f, 0f), new V3(8f, 0f, 0f)));
        Check("dash: the defaults along yaw 90", NearV(MoveRules.Dash(90f, MoveRules.DefaultDashSpeed, MoveRules.DefaultDashLift), new V3(8f, 0.5f, 0f)));
    }

    // Task 14: Flick (spec 2.3, 2.4, 3.5; rulings C13, C15, C16)
    static void Flicks() {
        // the brief's checks
        Check("flick gains: 120 kg is (1, 0.1, 6)", Pd.FlickGains(120f) == (1f, 0.1f, 6f));
        Check("flick gains: 350 kg is (1, 0.1, 8.5)", Pd.FlickGains(350f) == (1f, 0.1f, 8.5f));
        V3 sum = V3.Zero;
        Pd.Flick(new V3(1f, 2f, -1f), ref sum, V3.Zero, 150f, 0.25f, 1000f, 0.02f);
        bool once = NearV(sum, new V3(0.25f, 0.5f, -0.25f));
        Pd.Flick(new V3(-2f, 0f, 4f), ref sum, V3.Zero, 150f, 0.5f, 1000f, 0.02f);
        Check("pd flick: the integral grows by error x dt", once && NearV(sum, new V3(-0.75f, 0.5f, 1.75f)));
        Check("flick on still is refused", !StoneRules.CanApply(StoneState.Still, Modifier.Flick));
        Check("flick on free is allowed", StoneRules.CanApply(StoneState.Free, Modifier.Flick));
        (V3 forward, V3 up) upright = Pd.FlickTurn(new V3(0.1f, 1.2f, 0.4f), new V3(0.1f, 0.9f, 0.4f), new V3(0f, 0f, 1f));
        Check("flick turn: B straight under A gives up (0, 1, 0), facing the look", NearV(upright.up, new V3(0f, 1f, 0f)) && NearV(upright.forward, new V3(0f, 0f, 1f)));
        Check("flicked: stomp -> stomped (it drops)", StoneRules.After(StoneState.Flicked, Modifier.Stomp) == StoneState.Stomped);
        Check("flicked: straight stays flicked, pushed as a loose stone", StoneRules.After(StoneState.Flicked, Modifier.Straight) == StoneState.Flicked
            && StoneRules.CanApply(StoneState.Flicked, Modifier.Straight) && !StoneRules.IsGrounded(StoneState.Flicked));

        // the defaults (the settings')
        Check("defaults (C30): flick x 1000, turn gain 8, turn damp 4", Near(Pd.DefaultFlickScale, 1000f) && Near(Pd.DefaultFlickTurnGain, 8f) && Near(Pd.DefaultFlickTurnDamp, 4f));

        // ruling C15: FlickGains at every band edge (Hold's bands: up to 150, up to 300, past it)
        Check("flick gains: 0, 100, 150 kg (and below 0) are (1, 0.1, 6)", Pd.FlickGains(0f) == (1f, 0.1f, 6f) && Pd.FlickGains(100f) == (1f, 0.1f, 6f)
            && Pd.FlickGains(150f) == (1f, 0.1f, 6f) && Pd.FlickGains(-5f) == (1f, 0.1f, 6f));
        Check("flick gains: just past 150 to 300 is (1, 0.1, 7)", Pd.FlickGains(150.01f) == (1f, 0.1f, 7f) && Pd.FlickGains(275f) == (1f, 0.1f, 7f) && Pd.FlickGains(300f) == (1f, 0.1f, 7f));
        Check("flick gains: just past 300 is (1, 0.1, 8.5)", Pd.FlickGains(300.01f) == (1f, 0.1f, 8.5f) && Pd.FlickGains(400f) == (1f, 0.1f, 8.5f) && Pd.FlickGains(1e6f) == (1f, 0.1f, 8.5f));
        Check("flick gains: a mass that is not a number is the heaviest band", Pd.FlickGains(float.NaN) == (1f, 0.1f, 8.5f) && Pd.FlickGains(float.PositiveInfinity) == (1f, 0.1f, 8.5f));
        Check("flick gains: the five stones", Pd.FlickGains(StoneTable.Default(Disc).Mass) == (1f, 0.1f, 6f) && Pd.FlickGains(StoneTable.Default(Ball).Mass) == (1f, 0.1f, 6f)
            && Pd.FlickGains(StoneTable.Default(Pillar).Mass) == (1f, 0.1f, 7f) && Pd.FlickGains(StoneTable.Default(Cube).Mass) == (1f, 0.1f, 7f) && Pd.FlickGains(StoneTable.Default(Wall).Mass) == (1f, 0.1f, 8.5f));

        // ruling C13 (as Hold): scale x (p x error + i x integral - d x dStep x velocity), the integral first grown by error x dt
        V3 i0 = V3.Zero;
        Check("pd flick: error (1, 0, 0) from a zero integral: 1000 x (1 + 0.1 x 0.25)", NearF(Pd.Flick(new V3(1f, 0f, 0f), ref i0, V3.Zero, 150f, 0.25f, 1000f, 0.02f), new V3(1025f, 0f, 0f))
            && NearV(i0, new V3(0.25f, 0f, 0f)));
        V3 i1 = V3.Zero;
        Check("pd flick: dt 0 is p alone, and the integral stays", NearF(Pd.Flick(new V3(1f, -2f, 0.5f), ref i1, V3.Zero, 150f, 0f, 1000f, 0.02f), new V3(1000f, -2000f, 500f)) && NearV(i1, V3.Zero));
        V3 i2 = new V3(2f, 0f, -4f);
        Check("pd flick: the integral pulls on its own", NearF(Pd.Flick(V3.Zero, ref i2, V3.Zero, 150f, 0.02f, 1000f, 0.02f), new V3(200f, 0f, -400f)) && NearV(i2, new V3(2f, 0f, -4f)));
        V3 iz = V3.Zero;
        Check("pd flick: a 150 kg stone at 1 m/s is damped by 1000 x 6 x 0.02", NearF(Pd.Flick(V3.Zero, ref iz, new V3(1f, 0f, 0f), 150f, 0.02f, 1000f, 0.02f), new V3(-120f, 0f, 0f)));
        iz = V3.Zero;
        Check("pd flick: 250 kg by 7, every axis", NearF(Pd.Flick(V3.Zero, ref iz, new V3(1f, 2f, -1f), 250f, 0.02f, 1000f, 0.02f), new V3(-140f, -280f, 140f)));
        iz = V3.Zero;
        Check("pd flick: 400 kg by 8.5", NearF(Pd.Flick(V3.Zero, ref iz, new V3(1f, 0f, 0f), 400f, 0.02f, 1000f, 0.02f), new V3(-170f, 0f, 0f)));
        iz = V3.Zero;
        Check("pd flick: PdStepSeconds 1 is the per-second reading", NearF(Pd.Flick(V3.Zero, ref iz, new V3(1f, 0f, 0f), 150f, 0.02f, 1000f, 1f), new V3(-6000f, 0f, 0f)));
        iz = V3.Zero;
        Check("pd flick: PdStepSeconds 0 does not damp", NearF(Pd.Flick(new V3(1f, 0f, 0f), ref iz, new V3(5f, 5f, 5f), 150f, 0f, 1000f, 0f), new V3(1000f, 0f, 0f)));
        V3 i3 = new V3(1f, 0f, -1f);
        Check("pd flick: p, i and d together (350 kg)", NearF(Pd.Flick(new V3(0.5f, -0.2f, 1f), ref i3, new V3(2f, 0f, 0f), 350f, 0.5f, 1000f, 0.02f), new V3(285f, -210f, 950f))
            && NearV(i3, new V3(1.25f, -0.1f, -0.5f)));
        iz = V3.Zero;
        Check("pd flick: the scale from the settings", NearF(Pd.Flick(new V3(1f, 1f, 0f), ref iz, V3.Zero, 150f, 0f, 500f, 0.02f), new V3(500f, 500f, 0f)));
        // nothing that is not a number gets out, the force or the integral
        V3 keep = new V3(1f, 1f, 1f);
        Check("pd flick: an error that is not a number pulls nothing and leaves the integral",
            NearV(Pd.Flick(new V3(float.NaN, 0f, 0f), ref keep, V3.Zero, 150f, 0.02f, 1000f, 0.02f), V3.Zero) && NearV(keep, new V3(1f, 1f, 1f))
            && NearV(Pd.Flick(new V3(0f, float.PositiveInfinity, 0f), ref keep, new V3(1f, 0f, 0f), 150f, 0.02f, 1000f, 0.02f), V3.Zero) && NearV(keep, new V3(1f, 1f, 1f)));
        iz = V3.Zero;
        Check("pd flick: a velocity that is not a number counts as none", NearF(Pd.Flick(new V3(1f, 0f, 0f), ref iz, new V3(float.NaN, 0f, 0f), 150f, 0f, 1000f, 0.02f), new V3(1000f, 0f, 0f)));
        V3 bad = new V3(float.NaN, 3f, 0f);
        Check("pd flick: an integral that is not a number starts again from zero", NearF(Pd.Flick(new V3(1f, 0f, 0f), ref bad, V3.Zero, 150f, 0.25f, 1000f, 0.02f), new V3(1025f, 0f, 0f))
            && NearV(bad, new V3(0.25f, 0f, 0f)));
        bool dts = true;
        foreach (float dt in new[] { float.NaN, -0.02f, float.PositiveInfinity }) {
            V3 i = V3.Zero;
            dts &= NearF(Pd.Flick(new V3(1f, 0f, 0f), ref i, V3.Zero, 150f, dt, 1000f, 0.02f), new V3(1000f, 0f, 0f)) && NearV(i, V3.Zero);
        }
        Check("pd flick: a dt that is not above 0 or not a number does not grow the integral", dts);
        bool scales = true;
        foreach (float sc in new[] { float.NaN, 0f, -1f, float.PositiveInfinity }) {
            V3 i = new V3(1f, 0f, 0f);
            scales &= NearV(Pd.Flick(new V3(1f, 1f, 1f), ref i, V3.Zero, 150f, 0.02f, sc, 0.02f), V3.Zero) && NearV(i, new V3(1f, 0f, 0f));
        }
        Check("pd flick: a scale not above 0 or not a number pulls nothing and leaves the integral", scales);
        iz = V3.Zero;
        Check("pd flick: a step that is not a number does not damp", NearF(Pd.Flick(new V3(1f, 0f, 0f), ref iz, new V3(1f, 0f, 0f), 150f, 0f, 1000f, float.NaN), new V3(1000f, 0f, 0f)));
        iz = V3.Zero;
        Check("pd flick: a mass that is not a number damps as the heaviest", NearF(Pd.Flick(V3.Zero, ref iz, new V3(1f, 0f, 0f), float.NaN, 0f, 1000f, 0.02f), new V3(-170f, 0f, 0f)));
        V3 hugeI = new V3(3e38f, -3e38f, 3e38f);
        V3 hugeF = Pd.Flick(new V3(3e38f, -3e38f, 3e38f), ref hugeI, new V3(-3e38f, 3e38f, 0f), 150f, 3e38f, 3e38f, 3e38f);
        Check("pd flick: huge numbers stay finite, the force and the integral", FiniteV(hugeF) && FiniteV(hugeI));

        // the target: the midpoint of the two hands plus the head's flat forward x HoldDistance
        var a = new V3(0.1f, 1.2f, 0.4f);
        var b = new V3(0.1f, 0.9f, 0.4f);
        var mid = new V3(0.1f, 1.05f, 0.4f);
        Check("flick target: between the hands, 1 m along the look", NearV(Pd.FlickTarget(a, b, new V3(0f, 0f, 1f), 1f), new V3(0.1f, 1.05f, 1.4f)));
        Check("flick target: the look made flat and length 1", NearV(Pd.FlickTarget(a, b, new V3(0f, -3f, 4f), 1f), new V3(0.1f, 1.05f, 1.4f)));
        Check("flick target: HoldDistance along another look", NearV(Pd.FlickTarget(a, b, new V3(1f, 0f, 0f), 2f), new V3(2.1f, 1.05f, 0.4f)));
        Check("flick target: a distance of 0, below 0 or not a number is the midpoint", NearV(Pd.FlickTarget(a, b, new V3(0f, 0f, 1f), 0f), mid)
            && NearV(Pd.FlickTarget(a, b, new V3(0f, 0f, 1f), -1f), mid) && NearV(Pd.FlickTarget(a, b, new V3(0f, 0f, 1f), float.NaN), mid));
        Check("flick target: a look straight down or not a number is the midpoint", NearV(Pd.FlickTarget(a, b, new V3(0f, -1f, 0f), 1f), mid)
            && NearV(Pd.FlickTarget(a, b, new V3(float.NaN, 0f, 1f), 1f), mid));

        // the turn: up from B to A, forward the look made square to it
        float r = (float)System.Math.Sqrt(0.5);
        var z = new V3(0f, 0f, 1f);
        Check("flick turn: upright, looking east", NearV(Pd.FlickTurn(a, b, new V3(1f, 0f, 0f)).forward, new V3(1f, 0f, 0f)) && NearV(Pd.FlickTurn(a, b, new V3(1f, 0f, 0f)).up, new V3(0f, 1f, 0f)));
        Check("flick turn: a look that is not flat or not length 1", NearV(Pd.FlickTurn(a, b, new V3(0f, -3f, 4f)).forward, z));
        (V3 forward, V3 up) side = Pd.FlickTurn(new V3(0.3f, 0.3f, 0f), V3.Zero, z);
        Check("flick turn: the hands tilted 45 degrees to the side", NearV(side.up, new V3(r, r, 0f)) && NearV(side.forward, z));
        (V3 forward, V3 up) ahead = Pd.FlickTurn(new V3(0f, 0.3f, 0.3f), V3.Zero, z);
        Check("flick turn: tilted 45 degrees forward faces down and ahead", NearV(ahead.up, new V3(0f, r, r)) && NearV(ahead.forward, new V3(0f, -r, r)));
        (V3 forward, V3 up) back = Pd.FlickTurn(new V3(0f, 0.3f, -0.3f), V3.Zero, z);
        Check("flick turn: tilted 45 degrees back faces up and ahead", NearV(back.up, new V3(0f, r, -r)) && NearV(back.forward, new V3(0f, r, r)));
        (V3 forward, V3 up) flipped = Pd.FlickTurn(b, a, z);
        Check("flick turn: A under B turns it upside down, still facing the look", NearV(flipped.up, new V3(0f, -1f, 0f)) && NearV(flipped.forward, z));
        bool square = true;
        uint seed = 12345u;
        for (int k = 0; k < 200; k++) {
            V3 ha = Rand(ref seed), hb = Rand(ref seed), look = Rand(ref seed);
            (V3 forward, V3 up) t = Pd.FlickTurn(ha, hb, look);
            square &= Near2(t.forward.Length, 1f, 1e-4f) && Near2(t.up.Length, 1f, 1e-4f) && System.Math.Abs(V3.Dot(t.forward, t.up)) < 1e-4f;
        }
        Check("flick turn: forward and up are always length 1 and square to each other", square);
        // the fallback: up (nearly) along the look, where the look made square has no length; it takes the way the forward
        // goes as the hands tilt there from upright: down when A is ahead of B, up when A is behind
        (V3 forward, V3 up) pointing = Pd.FlickTurn(new V3(0f, 0f, 0.3f), V3.Zero, z);
        Check("flick turn: A straight ahead of B faces down", NearV(pointing.up, z) && NearV(pointing.forward, new V3(0f, -1f, 0f)));
        (V3 forward, V3 up) behind = Pd.FlickTurn(new V3(0f, 0f, -0.3f), V3.Zero, z);
        Check("flick turn: A straight behind B faces up", NearV(behind.up, new V3(0f, 0f, -1f)) && NearV(behind.forward, new V3(0f, 1f, 0f)));
        (V3 forward, V3 up) nearly = Pd.FlickTurn(new V3(0f, 0.05f, 1f), V3.Zero, z);
        Check("flick turn: nearly along the look, square and length 1", nearly.forward.Y < -0.99f && System.Math.Abs(V3.Dot(nearly.forward, nearly.up)) < 1e-4f && Near2(nearly.forward.Length, 1f, 1e-4f));
        (V3 forward, V3 up) sideways = Pd.FlickTurn(new V3(0.02f, 0f, 1f), V3.Zero, z);
        Check("flick turn: nearly along the look from the side, square and length 1", System.Math.Abs(V3.Dot(sideways.forward, sideways.up)) < 1e-4f && Near2(sideways.forward.Length, 1f, 1e-4f));
        bool smooth = true;
        V3 prevF = Pd.FlickTurn(Tilt(60), V3.Zero, z).forward;
        for (int deg = 61; deg <= 95; deg++) {
            V3 f = Pd.FlickTurn(Tilt(deg), V3.Zero, z).forward;
            smooth &= V3.Dot(f, prevF) > (float)System.Math.Cos(3.0 * System.Math.PI / 180.0);
            prevF = f;
        }
        Check("flick turn: tilting the hands forward from 60 to 95 degrees turns it smoothly (through the fallback)", smooth);
        Check("flick turn: past the fallback it faces the look again", Pd.FlickTurn(Tilt(120), V3.Zero, z).forward.Z > 0.49f);
        Check("flick turn: hands at one place, or not a number, stand it upright", NearV(Pd.FlickTurn(a, a, z).up, new V3(0f, 1f, 0f)) && NearV(Pd.FlickTurn(a, a, z).forward, z)
            && NearV(Pd.FlickTurn(new V3(float.NaN, 0f, 0f), b, z).up, new V3(0f, 1f, 0f)));
        Check("flick turn: no flat look faces +z", NearV(Pd.FlickTurn(a, b, new V3(0f, -1f, 0f)).forward, z) && NearV(Pd.FlickTurn(a, b, new V3(float.NaN, 0f, 0f)).forward, z));

        // the rotation from the stone's turn to the wanted one, as axis x angle in radians, the short way
        float s45 = (float)System.Math.Sin(System.Math.PI / 4.0), s135 = (float)System.Math.Sin(3.0 * System.Math.PI / 4.0), c135 = (float)System.Math.Cos(3.0 * System.Math.PI / 4.0);
        float halfPi = (float)(System.Math.PI / 2.0);
        Check("turn vector: none for no turn", NearV(Pd.TurnVector(0f, 0f, 0f, 1f), V3.Zero) && NearV(Pd.TurnVector(0f, 0f, 0f, -1f), V3.Zero));
        Check("turn vector: 90 degrees about y", NearV(Pd.TurnVector(0f, s45, 0f, s45), new V3(0f, halfPi, 0f)));
        Check("turn vector: 270 degrees about y is 90 the other way", NearV(Pd.TurnVector(0f, s135, 0f, c135), new V3(0f, -halfPi, 0f)));
        Check("turn vector: the same turn written negated", NearV(Pd.TurnVector(0f, -s45, 0f, -s45), new V3(0f, halfPi, 0f)));
        Check("turn vector: 180 degrees about x", NearV(Pd.TurnVector(1f, 0f, 0f, 0f), new V3((float)System.Math.PI, 0f, 0f)));
        Check("turn vector: a quaternion not of length 1", NearV(Pd.TurnVector(0f, 2f * s45, 0f, 2f * s45), new V3(0f, halfPi, 0f)));
        Check("turn vector: not a number is none", NearV(Pd.TurnVector(float.NaN, 0f, 0f, 1f), V3.Zero) && NearV(Pd.TurnVector(0f, 0f, float.PositiveInfinity, 1f), V3.Zero));
        Check("turn vector: huge numbers stay finite", FiniteV(Pd.TurnVector(3e38f, -3e38f, 3e38f, 1f)));

        // the turn's torque (an acceleration, ForceMode.Acceleration): FlickTurnGain x the turn - FlickTurnDamp x the spin
        Check("flick torque: gain x turn", NearV(Pd.FlickTorque(new V3(0f, 1f, 0f), V3.Zero, 8f, 1f), new V3(0f, 8f, 0f)));
        Check("flick torque: damp x spin", NearV(Pd.FlickTorque(V3.Zero, new V3(0f, 2f, -1f), 8f, 1f), new V3(0f, -2f, 1f)));
        Check("flick torque: both", NearF(Pd.FlickTorque(new V3(halfPi, 0f, 0f), new V3(1f, 0f, 0f), 8f, 1f), new V3(8f * halfPi - 1f, 0f, 0f)));
        Check("flick torque: from the settings", NearV(Pd.FlickTorque(new V3(1f, 0f, 0f), new V3(1f, 0f, 0f), 3f, 2f), new V3(1f, 0f, 0f)));
        Check("flick torque: a turn that is not a number only damps", NearV(Pd.FlickTorque(new V3(float.NaN, 0f, 0f), new V3(1f, 0f, 0f), 8f, 1f), new V3(-1f, 0f, 0f)));
        Check("flick torque: a spin that is not a number counts as none", NearV(Pd.FlickTorque(new V3(1f, 0f, 0f), new V3(0f, float.NaN, 0f), 8f, 1f), new V3(8f, 0f, 0f)));
        Check("flick torque: a gain or damp below 0 or not a number is 0", NearV(Pd.FlickTorque(new V3(1f, 0f, 0f), new V3(1f, 0f, 0f), float.NaN, -1f), V3.Zero)
            && NearV(Pd.FlickTorque(new V3(1f, 0f, 0f), new V3(1f, 0f, 0f), -8f, float.PositiveInfinity), V3.Zero));
        Check("flick torque: huge numbers stay finite", FiniteV(Pd.FlickTorque(new V3(3e38f, 3e38f, 3e38f), new V3(-3e38f, -3e38f, -3e38f), 3e38f, 3e38f)));

        // models, stepped as PhysX steps a body (semi-implicit Euler) at the defaults: the pull on every stone settles on a
        // still target and follows a moving one at the server's 15-50 Hz without running away; the turn settles. The pull is
        // slow and lightly damped at these gains (a Ball swings with a period of about 2.4 s, a Wall 4 s; a Wall 1 m off is
        // still about 0.2 m off after 10 s), so the windows are long
        bool settles = true, follows = true, turns = true;
        foreach (float hz in new[] { 15f, 30f, 45f, 50f }) {
            foreach (StoneKind k in new[] { Disc, Ball, Pillar, Cube, Wall }) {
                float end = Flicked(StoneTable.Default(k).Mass, hz, _ => 0.0, -1.0, 40.0, out float worst);
                settles &= end < 0.01f && worst <= 1f + 1e-4f;
            }
            float trail = Flicked(150f, hz, Ramp(2.0, 1.0), 0.0, 40.0, out float sweepWorst);
            follows &= trail < 0.01f && sweepWorst < 1.2f;   // it trails a 2 m/s sweep by up to about 1 m
            turns &= Turned(hz, 3.0, 20.0) < 0.01f && Turned(hz, 4.0, 20.0) < 0.01f && Turned(hz, -2.0, 20.0) < 0.01f;
        }
        Check("model: every flicked stone 1 m off a still target settles within 1 cm in 40 s, never further than it started (15-50 Hz)", settles);
        Check("model: a flicked Ball follows a 2 m sweep of the hands in 1 s, never 1.2 m behind, and settles after it (15-50 Hz)", follows);
        Check("model: a stone turned 3 rad, 4 rad (the short way) or -2 rad off settles on the wanted turn (15-50 Hz)", turns);
        // ruling C30: FlickTurnDamp 4 (damping 0.71 with gain 8): a quick 90 degree turn of the hands swings past by a few
        // degrees and settles within a degree in 3 s; the plan's 1 (damping 0.18) swung past by about 50 degrees
        double quarter = System.Math.PI / 2.0, degree = System.Math.PI / 180.0;
        bool crisp = true, swung = true;
        foreach (float hz in new[] { 15f, 30f, 45f, 50f }) {
            crisp &= Turned(hz, quarter, 3.0, Pd.DefaultFlickTurnDamp, out float past) < 1.0 * degree && past < 10.0 * degree;
            Turned(hz, quarter, 3.0, 1f, out float oldPast);
            swung &= oldPast > 40.0 * degree && oldPast < 60.0 * degree;
        }
        Check("model (C30): a quick 90 degree turn swings past by under 10 degrees and settles within 1 degree in 3 s (15-50 Hz)", crisp);
        Check("model (C30): with damp 1 the same turn swung past by 40 to 60 degrees", swung);
        float pdTrail = 6f * Pd.DefaultPdStepSeconds * 1f;   // a PD alone (no i) trails a 1 m/s target by d x dStep / p
        float iTrail = Flicked(150f, 45f, t => t, 0.0, 40.0, out _);
        Check("model: the integral takes up the trail behind a steadily moving hand", iTrail < pdTrail * 0.5f);

        // ruling C15: Flick in every state: loose ones (Free, Floating, Held, Flicked) are Flicked; grounded ones refused
        StoneState[] after = { StoneState.Rising, StoneState.Still, StoneState.Sliding, StoneState.Flicked, StoneState.Stomped, StoneState.Flicked, StoneState.Flicked, StoneState.Flicked };
        bool[] can = { false, false, false, true, false, true, true, true };
        for (int i = 0; i < AllStates.Length; i++)
            Check($"flick on {AllStates[i]}: {(can[i] ? after[i].ToString() : "refused")}", StoneRules.CanApply(AllStates[i], Modifier.Flick) == can[i] && StoneRules.After(AllStates[i], Modifier.Flick) == after[i]);
        // the Flicked row: Stomp drops it (Stomped); Straight, Kick and Uppercut push it and it stays; Parry and Hold are
        // refused; Explode marks it; Flick again (by anyone) takes it over, still Flicked
        StoneState[] row = { StoneState.Flicked, StoneState.Flicked, StoneState.Stomped, StoneState.Flicked, StoneState.Flicked, StoneState.Flicked, StoneState.Flicked, StoneState.Flicked };
        bool[] takes = { true, true, true, true, false, false, true, true };
        for (int m = 0; m < ModifyCheck.ModifierCount; m++)
            Check($"flicked: {(Modifier)m} {(takes[m] ? "-> " + row[m] : "refused")}", StoneRules.CanApply(StoneState.Flicked, (Modifier)m) == takes[m] && StoneRules.After(StoneState.Flicked, (Modifier)m) == row[m]);
        Check("flicked: kick and uppercut push it as a loose stone", NearV(StoneRules.Push(Modifier.Kick, StoneRules.IsGrounded(StoneState.Flicked), z).impulse, new V3(0f, 1500f, 0f))
            && NearV(StoneRules.Push(Modifier.Uppercut, StoneRules.IsGrounded(StoneState.Flicked), z).impulse, new V3(0f, 900f, 900f)));
        Check("flicked: stomp pushes it down as a loose stone", NearV(StoneRules.Push(Modifier.Stomp, StoneRules.IsGrounded(StoneState.Flicked), z).impulse, new V3(0f, -1500f, 0f)));
        Check("flicked is loose: not grounded, not kinematic, discrete, no landing", !StoneRules.IsGrounded(StoneState.Flicked) && !StoneRules.Kinematic(StoneState.Flicked)
            && !StoneRules.Continuous(StoneState.Flicked) && StoneRules.Landed(StoneState.Flicked) == StoneState.Flicked);
        Check("flicked: a stone summoned where it is leaves it be", StoneRules.SummonUnder(StoneState.Flicked) == (StoneState.Flicked, 0f));
        Check("flicked: a ball's power is not grounded", StoneRules.Power(Ball, StoneState.Flicked, 0f, 3f) == 1);
        Check("flick pushes nothing", StoneRules.Impulse(Modifier.Flick, false) == (0f, 0f, 0f) && StoneRules.Impulse(Modifier.Flick, true) == (0f, 0f, 0f));
        // Task 11's CARRY: the server refuses Flick on a grounded stone (NotAllowed)
        var boxMin = new V3(-0.5f, -1.6f, 1.5f);
        var boxMax = new V3(0.5f, -0.6f, 2.5f);
        foreach (StoneState s in new[] { StoneState.Still, StoneState.Sliding, StoneState.Stomped, StoneState.Rising })
            Check($"modify: flick on a {s} stone is not allowed", ModifyCheck.Decide(Mod(Modifier.Flick, 9u, V3.Zero), true, s, boxMin, boxMax, V3.Zero, -1.6f, 2.5f, 2.4f, 0.5f) == ModifyRefusal.NotAllowed);
        foreach (StoneState s in new[] { StoneState.Free, StoneState.Floating, StoneState.Held, StoneState.Flicked })
            Check($"modify: flick on a {s} stone is allowed", ModifyCheck.Decide(Mod(Modifier.Flick, 9u, V3.Zero), true, s, boxMin, boxMax, V3.Zero, -1.6f, 2.5f, 2.4f, 0.5f) == ModifyRefusal.None);
        Check("modify: parry and hold on a flicked stone are not allowed", ModifyCheck.Decide(Mod(Modifier.Parry, 9u, V3.Zero), true, StoneState.Flicked, boxMin, boxMax, V3.Zero, -1.6f, 2.5f, 2.4f, 0.5f) == ModifyRefusal.NotAllowed
            && ModifyCheck.Decide(Mod(Modifier.Hold, 9u, V3.Zero), true, StoneState.Flicked, boxMin, boxMax, V3.Zero, -1.6f, 2.5f, 2.4f, 0.5f) == ModifyRefusal.NotAllowed);
        Check("modify: a byte past the modifiers is never applied", !StoneRules.CanApply(StoneState.Free, (Modifier)8) && !StoneRules.CanApply(StoneState.Flicked, (Modifier)200));
    }

    static bool NearF(V3 a, V3 b) => Near2(a.X, b.X, 1e-3f) && Near2(a.Y, b.Y, 1e-3f) && Near2(a.Z, b.Z, 1e-3f);

    // A pair of hands tilted `deg` degrees forward from upright (A above B at 0, A ahead of B at 90), seen from B.
    static V3 Tilt(int deg) {
        double t = deg * System.Math.PI / 180.0;
        return new V3(0f, (float)System.Math.Cos(t), (float)System.Math.Sin(t));
    }

    // A made-up place in a 4 m box round the origin (a fixed sequence: the same every run).
    static V3 Rand(ref uint seed) {
        float Next(ref uint s) {
            s = s * 1664525u + 1013904223u;
            return (s >> 8) / (float)(1 << 24) * 4f - 2f;
        }
        return new V3(Next(ref seed), Next(ref seed), Next(ref seed));
    }

    // A flicked stone of this mass along x, pulled by Pd.Flick at the defaults (FlickScale, PdStepSeconds; the integral from
    // zero as a flick begins) toward `target(t)`, starting at rest at x0, stepped as PhysX steps a body (semi-implicit Euler:
    // the velocity first, then the place) every 1/hz s for `seconds`: how far it is from the target at the end, and `worst`
    // its furthest. NaN when it runs away.
    static float Flicked(float mass, float hz, System.Func<double, double> target, double x0, double seconds, out float worst) {
        double x = x0, v = 0.0, dt = 1.0 / hz, t = 0.0;
        V3 integral = V3.Zero;
        worst = 0f;
        for (; t < seconds; t += dt) {
            var error = new V3((float)(target(t) - x), 0f, 0f);
            worst = System.Math.Max(worst, System.Math.Abs(error.X));
            V3 f = Pd.Flick(error, ref integral, new V3((float)v, 0f, 0f), mass, (float)dt, Pd.DefaultFlickScale, Pd.DefaultPdStepSeconds);
            v += f.X / mass * dt;
            x += v * dt;
            if (double.IsNaN(x) || System.Math.Abs(x) > 1e6) { worst = float.NaN; return float.NaN; }
        }
        return (float)System.Math.Abs(target(t) - x);
    }

    // A flicked stone's turn about x, `wanted` radians from where it starts at rest, driven each 1/hz s step by
    // Pd.FlickTorque (the defaults: an acceleration, so its inertia does not count) on the short way to it (Pd.TurnVector):
    // how far off the wanted turn it is after `seconds`, in radians (the short way). NaN when it runs away.
    static float Turned(float hz, double wanted, double seconds) => Turned(hz, wanted, seconds, Pd.DefaultFlickTurnDamp, out _);

    // As above with FlickTurnDamp `damp`; `past` is how far it swung past the wanted turn (wanted within half a turn, so the
    // short way is its own way), in radians.
    static float Turned(float hz, double wanted, double seconds, float damp, out float past) {
        double angle = 0.0, spin = 0.0, dt = 1.0 / hz, most = 0.0;
        for (double t = 0.0; t < seconds; t += dt) {
            double off = (wanted - angle) * 0.5;
            V3 turn = Pd.TurnVector((float)System.Math.Sin(off), 0f, 0f, (float)System.Math.Cos(off));
            V3 torque = Pd.FlickTorque(turn, new V3((float)spin, 0f, 0f), Pd.DefaultFlickTurnGain, damp);
            spin += torque.X * dt;
            angle += spin * dt;
            most = System.Math.Max(most, (angle - wanted) * System.Math.Sign(wanted));
            if (double.IsNaN(angle) || System.Math.Abs(angle) > 1e6) { past = float.NaN; return float.NaN; }
        }
        past = (float)most;
        return (float)System.Math.Abs(System.Math.IEEERemainder(wanted - angle, 2.0 * System.Math.PI));   // the short way, worked out here
    }

    // Task 13: Hold (spec 2.3, 2.4, 3.2, 3.5; rulings C13, C14, C16)
    static void Holds() {
        // the brief's checks
        Check("hold gains: 150 kg is (1, 8, 8)", Pd.HoldGains(150f) == (1f, 8f, 8f));
        Check("hold gains: 350 kg is (1, 11, 13)", Pd.HoldGains(350f) == (1f, 11f, 13f));
        Check("hold gains: 450 kg is (1, 15, 18)", Pd.HoldGains(450f) == (1f, 15f, 18f));
        Check("pd hold: no error, no velocity pulls nothing", NearV(Pd.Hold(V3.Zero, V3.Zero, 150f, false, 2500f, 1500f, 0.02f), V3.Zero));
        Check("pd hold: error (1, 0, 0), still, free -> (2500, 0, 0)", NearV(Pd.Hold(new V3(1f, 0f, 0f), V3.Zero, 150f, false, 2500f, 1500f, 0.02f), new V3(2500f, 0f, 0f)));
        Check("let go: past 8 m/s, or grounded changed, not at 7.9", StoneRules.HoldLetGo(8.1f, false, 8f) && StoneRules.HoldLetGo(1f, true, 8f) && !StoneRules.HoldLetGo(7.9f, false, 8f));
        Check("hold again with the hand that holds it lets go", StoneRules.HoldAgain(0, 0) && StoneRules.HoldAgain(1, 1));

        // the defaults (the settings')
        Check("defaults: hold 1 m ahead, x 2500 free, x 1500 grounded, d per 0.02 s", Near(Pd.DefaultHoldDistance, 1f) && Near(Pd.DefaultHoldFreeScale, 2500f)
            && Near(Pd.DefaultHoldGroundScale, 1500f) && Near(Pd.DefaultPdStepSeconds, 0.02f));
        Check("defaults (C28): let go past 5 m/s, armed within 0.5 m", Near(Pd.DefaultHoldLetGoSpeed, 5f) && Near(Pd.DefaultHoldArmMetres, 0.5f));

        // HoldGains at every band edge (spec 2.4: up to 150, 150-300, 300-400, past 400 kg)
        Check("hold gains: 0 and 100 kg are the lightest band", Pd.HoldGains(0f) == (1f, 8f, 8f) && Pd.HoldGains(100f) == (1f, 8f, 8f) && Pd.HoldGains(-5f) == (1f, 8f, 8f));
        Check("hold gains: just past 150 to 300 is (1, 9, 9)", Pd.HoldGains(150.01f) == (1f, 9f, 9f) && Pd.HoldGains(300f) == (1f, 9f, 9f));
        Check("hold gains: just past 300 to 400 is (1, 11, 13)", Pd.HoldGains(300.01f) == (1f, 11f, 13f) && Pd.HoldGains(400f) == (1f, 11f, 13f));
        Check("hold gains: just past 400 is (1, 15, 18)", Pd.HoldGains(400.01f) == (1f, 15f, 18f) && Pd.HoldGains(1e6f) == (1f, 15f, 18f));
        Check("hold gains: a mass that is not a number is the heaviest band", Pd.HoldGains(float.NaN) == (1f, 15f, 18f) && Pd.HoldGains(float.PositiveInfinity) == (1f, 15f, 18f));
        Check("hold gains: the five stones", Pd.HoldGains(StoneTable.Default(Disc).Mass) == (1f, 8f, 8f) && Pd.HoldGains(StoneTable.Default(Ball).Mass) == (1f, 8f, 8f)
            && Pd.HoldGains(StoneTable.Default(Pillar).Mass) == (1f, 9f, 9f) && Pd.HoldGains(StoneTable.Default(Cube).Mass) == (1f, 9f, 9f) && Pd.HoldGains(StoneTable.Default(Wall).Mass) == (1f, 11f, 13f));

        // ruling C13: d x PdStepSeconds x velocity (the error's change per 50 Hz physics step), dxz on x and z, dy on y
        Check("pd hold: a 150 kg stone at 1 m/s along x is damped by 2500 x 8 x 0.02", NearV(Pd.Hold(V3.Zero, new V3(1f, 0f, 0f), 150f, false, 2500f, 1500f, 0.02f), new V3(-400f, 0f, 0f)));
        Check("pd hold: dy on y, dxz on x and z (450 kg)", NearV(Pd.Hold(V3.Zero, new V3(1f, 2f, -1f), 450f, false, 2500f, 1500f, 0.02f), new V3(-750f, -1800f, 750f)));
        Check("pd hold: p and d together (350 kg)", NearV(Pd.Hold(new V3(0.5f, -0.2f, 1f), new V3(2f, 0f, 0f), 350f, false, 2500f, 1500f, 0.02f), new V3(150f, -500f, 2500f)));
        Check("pd hold: PdStepSeconds 1 is the per-second reading", NearV(Pd.Hold(V3.Zero, new V3(1f, 0f, 0f), 150f, false, 2500f, 1500f, 1f), new V3(-20000f, 0f, 0f)));
        Check("pd hold: PdStepSeconds 0 does not damp", NearV(Pd.Hold(new V3(1f, 0f, 0f), new V3(5f, 5f, 5f), 150f, false, 2500f, 1500f, 0f), new V3(2500f, 0f, 0f)));
        Check("pd hold: a ball trailing a 1 m/s hand by 0.16 m is pulled no more", NearV(Pd.Hold(new V3(0.16f, 0f, 0f), new V3(1f, 0f, 0f), 150f, false, 2500f, 1500f, 0.02f), V3.Zero));
        // ruling C14: a grounded hold pulls along x and z only, x HoldGroundScale
        Check("pd hold: grounded is x 1500 and has no y", NearV(Pd.Hold(new V3(1f, 1f, -2f), V3.Zero, 275f, true, 2500f, 1500f, 0.02f), new V3(1500f, 0f, -3000f)));
        Check("pd hold: grounded damps x and z only", NearV(Pd.Hold(V3.Zero, new V3(1f, -5f, 1f), 150f, true, 2500f, 1500f, 0.02f), new V3(-240f, 0f, -240f)));
        Check("pd hold: the scales from the settings", NearV(Pd.Hold(new V3(1f, 1f, 0f), V3.Zero, 150f, false, 1000f, 500f, 0.02f), new V3(1000f, 1000f, 0f))
            && NearV(Pd.Hold(new V3(1f, 1f, 0f), V3.Zero, 150f, true, 1000f, 500f, 0.02f), new V3(500f, 0f, 0f)));
        // never a force that is not a number
        Check("pd hold: an error that is not a number pulls nothing", NearV(Pd.Hold(new V3(float.NaN, 0f, 0f), V3.Zero, 150f, false, 2500f, 1500f, 0.02f), V3.Zero)
            && NearV(Pd.Hold(new V3(0f, float.PositiveInfinity, 0f), new V3(1f, 0f, 0f), 150f, false, 2500f, 1500f, 0.02f), V3.Zero));
        Check("pd hold: a velocity that is not a number counts as none", NearV(Pd.Hold(new V3(1f, 0f, 0f), new V3(float.NaN, 0f, 0f), 150f, false, 2500f, 1500f, 0.02f), new V3(2500f, 0f, 0f)));
        Check("pd hold: a mass that is not a number damps as the heaviest", NearV(Pd.Hold(V3.Zero, new V3(1f, 0f, 0f), float.NaN, false, 2500f, 1500f, 0.02f), new V3(-750f, 0f, 0f)));
        Check("pd hold: a scale not above 0 or not a number pulls nothing", NearV(Pd.Hold(new V3(1f, 1f, 1f), V3.Zero, 150f, false, float.NaN, 1500f, 0.02f), V3.Zero)
            && NearV(Pd.Hold(new V3(1f, 1f, 1f), V3.Zero, 150f, true, 2500f, -1f, 0.02f), V3.Zero) && NearV(Pd.Hold(new V3(1f, 1f, 1f), V3.Zero, 150f, false, float.PositiveInfinity, 1500f, 0.02f), V3.Zero));
        Check("pd hold: a step that is not a number does not damp", NearV(Pd.Hold(new V3(1f, 0f, 0f), new V3(1f, 0f, 0f), 150f, false, 2500f, 1500f, float.NaN), new V3(2500f, 0f, 0f)));
        Check("pd hold: huge numbers stay finite", FiniteV(Pd.Hold(new V3(3e38f, -3e38f, 3e38f), new V3(-3e38f, 3e38f, 0f), 150f, false, 3e38f, 3e38f, 3e38f)));
        // ruling C13: stable at the server's 15-45 Hz with every stone, and the trail behind a 1 m/s hand settles at 0.16 m
        Check("pd hold: a ball follows a 1 m/s hand 0.16 m behind (45 Hz)", Near2(Follow(150f, false, 1f / 45f, 1f, 20f), 0.16f, 0.005f));
        bool stable = true;
        foreach (float hz in new[] { 15f, 30f, 45f, 50f })
            foreach (StoneKind k in new[] { Disc, Ball, Pillar, Cube, Wall })
                foreach (bool grounded in new[] { false, true })
                    stable &= Settles(StoneTable.Default(k).Mass, grounded, 1f / hz);
        Check("pd hold: every stone settles on a still target at 15, 30, 45 and 50 Hz, free and grounded", stable);
        // ruling C28's model: a Ball (150 kg) held by Pd.Hold at the defaults, stepped at 45 Hz through the arming and let-go
        // rules: an arm swing throws it, a body turn does not, and a far pick-up is not thrown on its pull-in
        Check("model (C28): a swing (2.5 m in 0.25 s) throws a Ball", Held(150f, false, 45f, Ramp(2.5, 0.25), 0.0, 3.0, out _, out _, out _));
        Check("model (C28): a body turn (1.5 m in 0.5 s) does not", !Held(150f, false, 45f, Ramp(1.5, 0.5), 0.0, 3.0, out float turnPeak, out bool turnArmed, out _)
            && turnArmed && turnPeak < Pd.DefaultHoldLetGoSpeed);
        Check("model (C28): a far pick-up (at rest 2.7 m off) is not thrown: it arms near the target and settles",
            !Held(150f, false, 45f, _ => 0.0, -2.7, 10.0, out float pickPeak, out bool pickArmed, out float pickLeft) && pickArmed && pickLeft < 0.01f && pickPeak > Pd.DefaultHoldLetGoSpeed);
        Check("model (C28): a far pick-up of a Disc (100 kg) is not thrown either", !Held(100f, false, 45f, _ => 0.0, -2.7, 10.0, out _, out _, out _));
        bool rates = true;
        foreach (float hz in new[] { 15f, 30f, 50f })
            rates &= Held(150f, false, hz, Ramp(2.5, 0.25), 0.0, 3.0, out _, out _, out _) && !Held(150f, false, hz, Ramp(1.5, 0.5), 0.0, 3.0, out _, out _, out _)
                && !Held(150f, false, hz, _ => 0.0, -2.7, 10.0, out _, out _, out _);
        Check("model (C28): the same three at the server's 15, 30 and 50 Hz", rates);

        // the target: the hand plus its own flat forward x HoldDistance; the head's flat forward when the hand points within
        // 45 degrees of straight up or down (the Hold pose's fingers point up)
        var hand = new V3(0f, 1.5f, 0.4f);
        var east = new V3(1f, 0f, 0f);
        Check("hold target: a hand pointing ahead", NearV(Pd.HoldTarget(hand, new V3(0f, 0f, 1f), east, 1f), new V3(0f, 1.5f, 1.4f)));
        Check("hold target: pointing down and ahead goes flat ahead", NearV(Pd.HoldTarget(hand, new V3(0f, -0.6f, 0.8f), east, 1f), new V3(0f, 1.5f, 1.4f)));
        Check("hold target: fingers straight up take the head's flat forward", NearV(Pd.HoldTarget(hand, new V3(0f, 1f, 0f), east, 1f), new V3(1f, 1.5f, 0.4f)));
        Check("hold target: fingers tilted back 37 degrees still take the head's", NearV(Pd.HoldTarget(hand, new V3(0f, 0.8f, -0.6f), new V3(0f, 0f, 1f), 1f), new V3(0f, 1.5f, 1.4f))
            && NearV(Pd.HoldTarget(hand, new V3(0.6f, -0.8f, 0f), new V3(0f, 0f, 1f), 1f), new V3(0f, 1.5f, 1.4f)));
        Check("hold target: past 45 degrees from up the hand's own way", NearV(Pd.HoldTarget(hand, new V3(0.75f, 0.6614f, 0f), new V3(0f, 0f, 1f), 1f), new V3(1f, 1.5f, 0.4f)));
        Check("hold target: the head's forward is made flat and length 1", NearV(Pd.HoldTarget(hand, new V3(0f, 1f, 0f), new V3(0f, -3f, 4f), 1f), new V3(0f, 1.5f, 1.4f)));
        Check("hold target: a forward of any length", NearV(Pd.HoldTarget(hand, new V3(0f, 0f, 5f), east, 1f), new V3(0f, 1.5f, 1.4f)));
        Check("hold target: HoldDistance", NearV(Pd.HoldTarget(hand, new V3(0f, 0f, 1f), east, 2.5f), new V3(0f, 1.5f, 2.9f)) && NearV(Pd.HoldTarget(hand, new V3(0f, 0f, 1f), east, 0f), hand));
        Check("hold target: a distance not above 0 or not a number is the hand", NearV(Pd.HoldTarget(hand, new V3(0f, 0f, 1f), east, float.NaN), hand) && NearV(Pd.HoldTarget(hand, new V3(0f, 0f, 1f), east, -1f), hand));
        Check("hold target: a forward that is not a number takes the head's", NearV(Pd.HoldTarget(hand, new V3(float.NaN, 0f, 1f), east, 1f), new V3(1f, 1.5f, 0.4f)));
        Check("hold target: nothing to go by is the hand itself", NearV(Pd.HoldTarget(hand, new V3(0f, 1f, 0f), new V3(0f, -1f, 0f), 1f), hand)
            && NearV(Pd.HoldTarget(hand, V3.Zero, new V3(float.NaN, 0f, 0f), 1f), hand));

        // letting go (ruling: past HoldLetGoSpeed, or grounded changed)
        Check("let go: at 8 m/s it holds on", !StoneRules.HoldLetGo(8f, false, 8f));
        Check("let go: a speed that is not a number lets go", StoneRules.HoldLetGo(float.NaN, false, 8f));
        // rulings C27, C28: the speed test counts only once the held stone has been, in one step, both within HoldArmMetres of
        // its target and at HoldLetGoSpeed or under (a stone caught flying, or pulled in from afar, settles first; a swing
        // then throws it)
        Check("arm (C28): near and slow arms", StoneRules.HoldArmed(false, 0.4f, 3f, 0.5f, 5f) && StoneRules.HoldArmed(false, 0f, 0f, 0.5f, 5f));
        Check("arm (C28): slow but far does not (a far pick-up)", !StoneRules.HoldArmed(false, 2.7f, 0f, 0.5f, 5f) && !StoneRules.HoldArmed(false, 0.6f, 1f, 0.5f, 5f));
        Check("arm (C28): near but fast does not (a stone caught flying, or rushing past its place)", !StoneRules.HoldArmed(false, 0.1f, 12f, 0.5f, 5f) && !StoneRules.HoldArmed(false, 0.4f, 6f, 0.5f, 5f));
        Check("arm (C28): at both edges it arms, just past either it does not", StoneRules.HoldArmed(false, 0.5f, 5f, 0.5f, 5f)
            && !StoneRules.HoldArmed(false, 0.51f, 5f, 0.5f, 5f) && !StoneRules.HoldArmed(false, 0.5f, 5.01f, 0.5f, 5f));
        Check("arm (C28): armed stays armed however far or fast", StoneRules.HoldArmed(true, 9f, 20f, 0.5f, 5f) && StoneRules.HoldArmed(true, float.NaN, float.NaN, 0.5f, 5f));
        Check("arm (C28): a distance or speed that is not a number does not arm", !StoneRules.HoldArmed(false, float.NaN, 1f, 0.5f, 5f) && !StoneRules.HoldArmed(false, 0.1f, float.NaN, 0.5f, 5f)
            && !StoneRules.HoldArmed(false, float.PositiveInfinity, 1f, 0.5f, 5f) && !StoneRules.HoldArmed(false, 0.1f, float.PositiveInfinity, 0.5f, 5f));
        // the trail: how far the stone is from its target, flat for a grounded hold (it slides at its own height under a target
        // at hand height, and Pd.Hold pulls it along x and z only)
        Check("trail: a free hold's is the whole distance", Near(Pd.Trail(new V3(3f, 4f, 0f), false), 5f) && Near(Pd.Trail(V3.Zero, false), 0f));
        Check("trail: a grounded hold's is flat", Near(Pd.Trail(new V3(3f, 4f, 0f), true), 3f) && Near(Pd.Trail(new V3(0f, 0.9f, 0f), true), 0f) && Near(Pd.Trail(new V3(0.3f, -2f, 0.4f), true), 0.5f));
        Check("trail: an error that is not a number is infinitely far", float.IsPositiveInfinity(Pd.Trail(new V3(float.NaN, 0f, 0f), false))
            && float.IsPositiveInfinity(Pd.Trail(new V3(0f, 0f, float.PositiveInfinity), true)));
        // armed or not (HoldLetGo's own checks, with the let-go speed given)
        Check("let go (C27): unarmed, 12 m/s holds on; armed, 8.1 lets go and 7.9 does not", !StoneRules.HoldLetGo(12f, false, 8f, false)
            && StoneRules.HoldLetGo(8.1f, false, 8f, true) && !StoneRules.HoldLetGo(7.9f, false, 8f, true));
        Check("let go (C27): grounded changed lets go unarmed", StoneRules.HoldLetGo(12f, true, 8f, false) && StoneRules.HoldLetGo(1f, true, 8f, false));
        Check("let go (C27): a speed that is not a number lets go unarmed too", StoneRules.HoldLetGo(float.NaN, false, 8f, false) && StoneRules.HoldLetGo(float.PositiveInfinity, false, 8f, false));
        Check("let go (C27): the brief's checks are the armed case", StoneRules.HoldLetGo(8.1f, false, 8f, true) && StoneRules.HoldLetGo(1f, true, 8f, true) && !StoneRules.HoldLetGo(7.9f, false, 8f, true));
        Check("let go: from the settings", StoneRules.HoldLetGo(5.1f, false, 5f) && !StoneRules.HoldLetGo(9f, false, 12f));
        Check("hold again: another hand, or not held, does not let go", !StoneRules.HoldAgain(0, 1) && !StoneRules.HoldAgain(1, 0) && !StoneRules.HoldAgain(-1, 0) && !StoneRules.HoldAgain(-1, 1) && !StoneRules.HoldAgain(-1, -1));

        // ruling C14: Hold in every state (CanApply, After)
        StoneState[] after = { StoneState.Sliding, StoneState.Sliding, StoneState.Sliding, StoneState.Held, StoneState.Stomped, StoneState.Held, StoneState.Held, StoneState.Flicked };
        bool[] can = { true, true, true, true, false, true, true, false };
        for (int i = 0; i < AllStates.Length; i++)
            Check($"hold on {AllStates[i]}: {(can[i] ? after[i].ToString() : "refused")}", StoneRules.CanApply(AllStates[i], Modifier.Hold) == can[i] && StoneRules.After(AllStates[i], Modifier.Hold) == after[i]);
        Check("modify: hold on a stomped stone is not allowed (it lands first)", ModifyCheck.Decide(Mod(Modifier.Hold, 9u, V3.Zero), true, StoneState.Stomped, new V3(-0.5f, -1.6f, 1.5f), new V3(0.5f, -0.6f, 2.5f),
            V3.Zero, -1.6f, 2.5f, 2.4f, 0.5f) == ModifyRefusal.NotAllowed);
        Check("modify: hold on a free stone is allowed", ModifyCheck.Decide(Mod(Modifier.Hold, 9u, V3.Zero), true, StoneState.Free, new V3(-0.5f, -1.6f, 1.5f), new V3(0.5f, -0.6f, 2.5f),
            V3.Zero, -1.6f, 2.5f, 2.4f, 0.5f) == ModifyRefusal.None);
        Check("hold pushes nothing", StoneRules.Impulse(Modifier.Hold, false) == (0f, 0f, 0f) && StoneRules.Impulse(Modifier.Hold, true) == (0f, 0f, 0f));
        // the Held row: Straight and Explode keep the hold; Kick, Uppercut, Stomp and Parry change its state (they let go)
        Check("held: straight stays held", StoneRules.After(StoneState.Held, Modifier.Straight) == StoneState.Held);
        Check("held: explode stays held", StoneRules.After(StoneState.Held, Modifier.Explode) == StoneState.Held);
        Check("held: kick -> free", StoneRules.After(StoneState.Held, Modifier.Kick) == StoneState.Free);
        Check("held: uppercut -> free", StoneRules.After(StoneState.Held, Modifier.Uppercut) == StoneState.Free);
        Check("held: stomp -> stomped", StoneRules.After(StoneState.Held, Modifier.Stomp) == StoneState.Stomped);
        Check("held: parry -> floating", StoneRules.After(StoneState.Held, Modifier.Parry) == StoneState.Floating);
        bool heldTakes = true;
        foreach (Modifier m in new[] { Modifier.Straight, Modifier.Kick, Modifier.Stomp, Modifier.Uppercut, Modifier.Parry, Modifier.Hold, Modifier.Flick, Modifier.Explode })
            heldTakes &= StoneRules.CanApply(StoneState.Held, m);
        Check("held: every modifier applies (Flick takes it out of the hand: Task 14)", heldTakes && StoneRules.After(StoneState.Held, Modifier.Flick) == StoneState.Flicked);
        // a held sliding stone: Straight keeps it sliding (held); Stomp does nothing to it (held); Kick, Uppercut, Parry change it
        Check("held sliding: straight and stomp keep it sliding", StoneRules.After(StoneState.Sliding, Modifier.Straight) == StoneState.Sliding && StoneRules.After(StoneState.Sliding, Modifier.Stomp) == StoneState.Sliding);
        Check("held sliding: kick, uppercut, parry change it", StoneRules.After(StoneState.Sliding, Modifier.Kick) == StoneState.Free
            && StoneRules.After(StoneState.Sliding, Modifier.Uppercut) == StoneState.Free && StoneRules.After(StoneState.Sliding, Modifier.Parry) == StoneState.Floating);
        Check("held is loose: not grounded, not kinematic, discrete", !StoneRules.IsGrounded(StoneState.Held) && !StoneRules.Kinematic(StoneState.Held) && !StoneRules.Continuous(StoneState.Held));
    }

    static bool FiniteV(V3 v) => !float.IsNaN(v.X) && !float.IsInfinity(v.X) && !float.IsNaN(v.Y) && !float.IsInfinity(v.Y) && !float.IsNaN(v.Z) && !float.IsInfinity(v.Z);

    static bool Near2(float a, float b, float tol) => System.Math.Abs(a - b) <= tol;

    // A free stone of this mass held by Pd.Hold (defaults) behind a target moving along x at `speed`, stepped as PhysX steps a
    // body (semi-implicit Euler: the velocity first, then the place) every `dt`: how far it trails after `seconds`. NaN when it
    // runs away.
    static float Follow(float mass, bool grounded, float dt, float speed, float seconds) {
        double x = 0.0, v = 0.0, t = 0.0;
        for (; t < seconds; t += dt) {
            double target = speed * t;
            V3 f = Pd.Hold(new V3((float)(target - x), 0f, 0f), new V3((float)v, 0f, 0f), mass, grounded, Pd.DefaultHoldFreeScale, Pd.DefaultHoldGroundScale, Pd.DefaultPdStepSeconds);
            v += f.X / mass * dt;
            x += v * dt;
            if (double.IsNaN(x) || System.Math.Abs(x) > 1e6) return float.NaN;
        }
        return (float)(speed * t - x);
    }

    // Ruling C28's model: one held stone along x, stepped as PhysX steps a body (semi-implicit Euler) every 1/hz s with the
    // default settings, through the arming (Pd.Trail, StoneRules.HoldArmed) and let-go (StoneRules.HoldLetGo) rules in the order
    // StoneHold.DriveHold runs them, starting at rest at x0 under the target `target(t)`. True when it is let go (thrown);
    // `peak` its fastest, `armed` whether it armed, `left` how far from the target it is at the end (or the let-go).
    static bool Held(float mass, bool grounded, float hz, System.Func<double, double> target, double x0, double seconds, out float peak, out bool armed, out float left) {
        double x = x0, v = 0.0, dt = 1.0 / hz;
        peak = 0f;
        armed = false;
        for (double t = 0.0; t < seconds; t += dt) {
            var error = new V3((float)(target(t) - x), 0f, 0f);
            float speed = (float)System.Math.Abs(v);
            armed = StoneRules.HoldArmed(armed, Pd.Trail(error, grounded), speed, Pd.DefaultHoldArmMetres, Pd.DefaultHoldLetGoSpeed);
            if (StoneRules.HoldLetGo(speed, false, Pd.DefaultHoldLetGoSpeed, armed)) { left = error.Length; return true; }
            V3 f = Pd.Hold(error, new V3((float)v, 0f, 0f), mass, grounded, Pd.DefaultHoldFreeScale, Pd.DefaultHoldGroundScale, Pd.DefaultPdStepSeconds);
            v += f.X / mass * dt;
            x += v * dt;
            peak = System.Math.Max(peak, (float)System.Math.Abs(v));
        }
        left = (float)System.Math.Abs(target(seconds) - x);
        return false;
    }

    // The target moving `metres` along x at an even speed over `seconds`, then still.
    static System.Func<double, double> Ramp(double metres, double seconds) => t => metres * System.Math.Min(System.Math.Max(t / seconds, 0.0), 1.0);

    // A stone of this mass 1 m off a still target settles within 1 cm of it in 20 s (it neither runs away nor rings forever).
    static bool Settles(float mass, bool grounded, float dt) {
        double x = -1.0, v = 0.0, peak = 0.0;
        for (double t = 0.0; t < 20.0; t += dt) {
            V3 f = Pd.Hold(new V3((float)(-x), 0f, 0f), new V3((float)v, 0f, 0f), mass, grounded, Pd.DefaultHoldFreeScale, Pd.DefaultHoldGroundScale, Pd.DefaultPdStepSeconds);
            v += f.X / mass * dt;
            x += v * dt;
            if (double.IsNaN(x) || System.Math.Abs(x) > 1e3) return false;
            peak = System.Math.Max(peak, System.Math.Abs(x));
        }
        return System.Math.Abs(x) < 0.01 && peak <= 1.0 + 1e-6;
    }

    // Task 12: Parry, Explode and the double Straight (spec 2.3, 2.4, 2.6, 3.5, 3.7; rulings C8, C11, C12, C26)
    static void ParryAndExplode() {
        // the brief's checks
        Check("parry on still -> floating", StoneRules.After(StoneState.Still, Modifier.Parry) == StoneState.Floating);
        Check("parry on free -> floating", StoneRules.After(StoneState.Free, Modifier.Parry) == StoneState.Floating);
        Check("parry ends: 3.1 s, 0.2 m/s, not 0.05 m/s", StoneRules.ParryEnds(3.1f, 0f, 3f, 0.1f) && StoneRules.ParryEnds(1f, 0.2f, 3f, 0.1f) && !StoneRules.ParryEnds(1f, 0.05f, 3f, 0.1f));
        Check("explode share: 1 m of 3, 3 m of 3, 3.1 m of 3", StoneRules.ExplodeShare(1f, 3f) == 1f && StoneRules.ExplodeShare(3f, 3f) == 0.25f && StoneRules.ExplodeShare(3.1f, 3f) == 0f);
        Check("double straight inside DoubleStraightSeconds", StoneRules.DoubleStraight(10.0, 10.3, StoneRules.DefaultDoubleStraightSeconds));

        // the defaults (the settings'; ruling C26: the spec's 0.4)
        Check("defaults: parry 3 s, 0.1 m/s", Near(StoneRules.DefaultParrySeconds, 3f) && Near(StoneRules.DefaultParryBreakSpeed, 0.1f));
        Check("defaults: explode 3 m, 1000 N s, cripple 1", Near(StoneRules.DefaultExplodeRadius, 3f) && Near(StoneRules.DefaultExplodeForce, 1000f) && Near(StoneRules.ExplodeCripple, 1f));
        Check("defaults: double straight 0.4 s (ruling C26)", Near(StoneRules.DefaultDoubleStraightSeconds, 0.4f));

        // Parry: Floating from Free, Still, Sliding, Stomped, and Rising once it has risen (C6); Floating again restarts it;
        // Flicked refuses it (Task 14, ruling C15: Flicks); Held is Task 13's (Holds: it lets go and floats)
        Check("parry on sliding -> floating", StoneRules.After(StoneState.Sliding, Modifier.Parry) == StoneState.Floating);
        Check("parry on stomped -> floating", StoneRules.After(StoneState.Stomped, Modifier.Parry) == StoneState.Floating);
        Check("parry on rising -> floating once risen", StoneRules.After(StoneState.Rising, Modifier.Parry) == StoneState.Floating);
        Check("parry on floating -> floating", StoneRules.After(StoneState.Floating, Modifier.Parry) == StoneState.Floating);
        Check("parry on flicked: refused, unchanged", StoneRules.After(StoneState.Flicked, Modifier.Parry) == StoneState.Flicked && !StoneRules.CanApply(StoneState.Flicked, Modifier.Parry));
        // the Floating row: Straight keeps it floating with the push (its speed then ends the float), Kick and Uppercut set it
        // loose, Stomp stomps it, Explode only marks it
        Check("floating: straight stays floating", StoneRules.After(StoneState.Floating, Modifier.Straight) == StoneState.Floating);
        Check("floating: kick -> free", StoneRules.After(StoneState.Floating, Modifier.Kick) == StoneState.Free);
        Check("floating: uppercut -> free", StoneRules.After(StoneState.Floating, Modifier.Uppercut) == StoneState.Free);
        Check("floating: stomp -> stomped", StoneRules.After(StoneState.Floating, Modifier.Stomp) == StoneState.Stomped);
        Check("floating: explode leaves it floating", StoneRules.After(StoneState.Floating, Modifier.Explode) == StoneState.Floating);
        Check("floating is loose: free numbers", !StoneRules.IsGrounded(StoneState.Floating) && !StoneRules.Kinematic(StoneState.Floating) && !StoneRules.Continuous(StoneState.Floating));
        // ruling C11: Explode marks the stone and leaves its state, in every state
        bool marks = true;
        foreach (StoneState s in AllStates) marks &= StoneRules.After(s, Modifier.Explode) == s;
        Check("explode leaves every state", marks);

        // Parry applies in every state but Flicked (ruling C15), Explode in every state (Hold is Task 13's: Holds; Flick
        // Task 14's: Flicks)
        bool parryAll = true, explodeAll = true;
        foreach (StoneState s in AllStates) {
            parryAll &= StoneRules.CanApply(s, Modifier.Parry) == (s != StoneState.Flicked);
            explodeAll &= StoneRules.CanApply(s, Modifier.Explode);
        }
        Check("parry applies in every state but Flicked", parryAll);
        Check("explode applies in every state", explodeAll);
        Check("modify: explode on a still stone is allowed", ModifyCheck.Decide(Mod(Modifier.Explode, 9u, V3.Zero), true, StoneState.Still, new V3(-0.5f, -1.6f, 1.5f), new V3(0.5f, -0.6f, 2.5f),
            V3.Zero, -1.6f, 2.5f, 2.4f, 0.5f) == ModifyRefusal.None);
        Check("modify: parry on a rising stone is allowed (held until risen)", ModifyCheck.Decide(Mod(Modifier.Parry, 9u, V3.Zero), true, StoneState.Rising, new V3(-0.5f, -1.6f, 1.5f), new V3(0.5f, -0.6f, 2.5f),
            V3.Zero, -1.6f, 2.5f, 2.4f, 0.5f) == ModifyRefusal.None);
        Check("parry pushes nothing", StoneRules.Impulse(Modifier.Parry, false) == (0f, 0f, 0f) && StoneRules.Impulse(Modifier.Explode, true) == (0f, 0f, 0f));

        // ParryEnds at its edges: older than ParrySeconds, or faster than ParryBreakSpeed (at either is not past it)
        Check("parry ends: at 3 s it still floats", !StoneRules.ParryEnds(3f, 0f, 3f, 0.1f) && StoneRules.ParryEnds(3.001f, 0f, 3f, 0.1f));
        Check("parry ends: at 0.1 m/s it still floats", !StoneRules.ParryEnds(1f, 0.1f, 3f, 0.1f) && StoneRules.ParryEnds(1f, 0.1001f, 3f, 0.1f));
        Check("parry ends: just parried", !StoneRules.ParryEnds(0f, 0f, 3f, 0.1f));
        Check("parry ends: not a number ends it", StoneRules.ParryEnds(float.NaN, 0f, 3f, 0.1f) && StoneRules.ParryEnds(1f, float.NaN, 3f, 0.1f));
        Check("parry ends: from the settings", !StoneRules.ParryEnds(4f, 0.4f, 5f, 0.5f) && StoneRules.ParryEnds(5.1f, 0f, 5f, 0.5f) && StoneRules.ParryEnds(1f, 0.6f, 5f, 0.5f));
        Check("parry ends: 0 s ends it at the first step", StoneRules.ParryEnds(0.02f, 0f, 0f, 0.1f));

        // ExplodeShare: 1 within half the radius, falling linearly to 0.25 at the radius, 0 beyond or with nothing to go by
        Check("explode share: at half the radius", StoneRules.ExplodeShare(1.5f, 3f) == 1f && StoneRules.ExplodeShare(0f, 3f) == 1f);
        Check("explode share: three quarters of the radius", Near(StoneRules.ExplodeShare(2.25f, 3f), 0.625f));
        Check("explode share: another radius", Near(StoneRules.ExplodeShare(4.5f, 6f), 0.625f) && StoneRules.ExplodeShare(6f, 6f) == 0.25f && StoneRules.ExplodeShare(2.9f, 6f) == 1f);
        bool falls = true;
        float prev = 1f;
        for (int i = 0; i <= 30; i++) {
            float sh = StoneRules.ExplodeShare(1.5f + 0.05f * i, 3f);
            falls &= sh <= prev + 1e-6f && sh >= 0.25f - 1e-5f && sh <= 1f;
            prev = sh;
        }
        Check("explode share: falls from 1 to 0.25 between half the radius and the radius", falls);
        Check("explode share: a distance that is not a number", StoneRules.ExplodeShare(float.NaN, 3f) == 0f && StoneRules.ExplodeShare(float.PositiveInfinity, 3f) == 0f);
        Check("explode share: a radius not above 0 or not a number", StoneRules.ExplodeShare(0f, 0f) == 0f && StoneRules.ExplodeShare(1f, -3f) == 0f
            && StoneRules.ExplodeShare(1f, float.NaN) == 0f && StoneRules.ExplodeShare(1f, float.PositiveInfinity) == 0f);
        Check("explode share: a distance below 0 is at the centre", StoneRules.ExplodeShare(-1f, 3f) == 1f);

        // the double Straight (rulings C8, C26): the last Straight claim within the window, by anyone
        Check("double straight: never straighted", !StoneRules.DoubleStraight(double.NegativeInfinity, 10.0, 0.4f));
        Check("double straight: a last that is not a number", !StoneRules.DoubleStraight(double.NaN, 10.0, 0.4f));
        Check("double straight: at the window", StoneRules.DoubleStraight(10.0, 10.5, 0.5f));
        Check("double straight: past the window", !StoneRules.DoubleStraight(10.0, 10.5001, 0.5f) && !StoneRules.DoubleStraight(10.0, 11.0, 0.4f));
        Check("double straight: a now that is not a number", !StoneRules.DoubleStraight(10.0, double.NaN, 0.4f));
        // ruling C26: one player's own two Straights are at least the game's 0.5 s cooldown apart, so never within the default;
        // a second player's Straight answering the first at once is
        Check("double straight: one player's two at the game's cooldown are not", !StoneRules.DoubleStraight(20.0, 20.5, StoneRules.DefaultDoubleStraightSeconds));
        Check("double straight: another player's answer 0.3 s later is", StoneRules.DoubleStraight(20.0, 20.3, StoneRules.DefaultDoubleStraightSeconds));

        // the blast's way on a player (spec 2.6, ruling C12): away from the centre, flat or 45 degrees up
        float r = (float)System.Math.Sqrt(0.5);
        Check("blast: beside the centre is flat", NearV(StoneRules.BlastDir(new V3(2f, 0.3f, 0f), V3.Zero), new V3(1f, 0f, 0f)));
        Check("blast: above the side is 45 degrees", NearV(StoneRules.BlastDir(new V3(0f, 1f, -1f), V3.Zero), new V3(0f, r, -r)));
        Check("blast: below the side is flat", NearV(StoneRules.BlastDir(new V3(0f, -1f, 2f), V3.Zero), new V3(0f, 0f, 1f)));
        Check("blast: straight above takes the stone's heading, 45 degrees up", NearV(StoneRules.BlastDir(new V3(0f, 1f, 0f), new V3(0f, 0f, 6f)), new V3(0f, r, r)));
        Check("blast: straight above with no heading goes straight up", NearV(StoneRules.BlastDir(new V3(0f, 1f, 0f), V3.Zero), new V3(0f, 1f, 0f))
            && NearV(StoneRules.BlastDir(V3.Zero, V3.Zero), new V3(0f, 1f, 0f)));
        Check("blast: at the centre takes the stone's heading, flat", NearV(StoneRules.BlastDir(V3.Zero, new V3(-3f, 0f, 0f)), new V3(-1f, 0f, 0f)));
        Check("blast: straight below with no heading: none", NearV(StoneRules.BlastDir(new V3(0f, -1f, 0f), V3.Zero), V3.Zero));
        Check("blast: not a number: none", NearV(StoneRules.BlastDir(new V3(float.NaN, 1f, 0f), V3.Zero), V3.Zero));
        Check("blast: unit length", Near(StoneRules.BlastDir(new V3(3f, 2f, -4f), V3.Zero).Length, 1f));
    }

    static readonly StoneState[] AllStates = {
        StoneState.Rising, StoneState.Still, StoneState.Sliding, StoneState.Free, StoneState.Stomped, StoneState.Floating, StoneState.Held, StoneState.Flicked,
    };

    // Task 11: Kick, Stomp, Uppercut (spec 2.3, 3.5; rulings C5, C6, C7, C10 and Task 9's two CARRY minors)
    static void Modifiers() {
        // the brief's checks: the states
        Check("kick on still -> free", StoneRules.After(StoneState.Still, Modifier.Kick) == StoneState.Free);
        Check("kick on free -> free", StoneRules.After(StoneState.Free, Modifier.Kick) == StoneState.Free);
        Check("stomp on free -> stomped", StoneRules.After(StoneState.Free, Modifier.Stomp) == StoneState.Stomped);
        Check("stomp on still -> still", StoneRules.After(StoneState.Still, Modifier.Stomp) == StoneState.Still);
        Check("uppercut on still -> free", StoneRules.After(StoneState.Still, Modifier.Uppercut) == StoneState.Free);
        Check("a stomped stone lands still", StoneRules.Landed(StoneState.Stomped) == StoneState.Still);
        Check("only a stomped stone lands", StoneRules.Landed(StoneState.Free) == StoneState.Free && StoneRules.Landed(StoneState.Sliding) == StoneState.Sliding
            && StoneRules.Landed(StoneState.Still) == StoneState.Still && StoneRules.Landed(StoneState.Rising) == StoneState.Rising);
        // fix round 1: a stomped stone is driven down at up to 20 m/s, so only it uses continuous collision detection, and no
        // kinematic state ever does (Unity refuses ContinuousDynamic on a kinematic body)
        bool onlyStomped = true, neverKinematic = true;
        foreach (StoneState s in AllStates) {
            onlyStomped &= StoneRules.Continuous(s) == (s == StoneState.Stomped);
            neverKinematic &= !(StoneRules.Continuous(s) && StoneRules.Kinematic(s));
        }
        Check("continuous: only a stomped stone", onlyStomped && StoneRules.Continuous(StoneState.Stomped));
        Check("continuous: never a kinematic state", neverKinematic);
        Check("continuous: a landed stone is not", !StoneRules.Continuous(StoneRules.Landed(StoneState.Stomped)));

        // Kick and Uppercut free every grounded stone; Stomp leaves a grounded one as it is
        Check("kick on sliding -> free", StoneRules.After(StoneState.Sliding, Modifier.Kick) == StoneState.Free);
        Check("kick on stomped -> free", StoneRules.After(StoneState.Stomped, Modifier.Kick) == StoneState.Free);
        Check("uppercut on sliding -> free", StoneRules.After(StoneState.Sliding, Modifier.Uppercut) == StoneState.Free);
        Check("uppercut on stomped -> free", StoneRules.After(StoneState.Stomped, Modifier.Uppercut) == StoneState.Free);
        Check("uppercut on free -> free", StoneRules.After(StoneState.Free, Modifier.Uppercut) == StoneState.Free);
        Check("stomp on sliding slides on", StoneRules.After(StoneState.Sliding, Modifier.Stomp) == StoneState.Sliding);
        Check("stomp on stomped stays stomped", StoneRules.After(StoneState.Stomped, Modifier.Stomp) == StoneState.Stomped);
        // ruling C6: a modifier taken while Rising waits for the rise; After is what it does to the risen (Still) stone
        Check("kick on rising -> free once risen", StoneRules.After(StoneState.Rising, Modifier.Kick) == StoneState.Free);
        Check("uppercut on rising -> free once risen", StoneRules.After(StoneState.Rising, Modifier.Uppercut) == StoneState.Free);
        Check("stomp on rising -> still once risen", StoneRules.After(StoneState.Rising, Modifier.Stomp) == StoneState.Still);
        // Flicked's row is Task 14's (Flicks: Stomp drops it; Kick and Uppercut push it and it stays in the hands); Floating's
        // is Task 12's (ParryAndExplode); Held's Task 13's (Holds)
        Check("flicked: kick and uppercut keep it flicked", StoneRules.After(StoneState.Flicked, Modifier.Kick) == StoneState.Flicked
            && StoneRules.After(StoneState.Flicked, Modifier.Uppercut) == StoneState.Flicked);

        // they apply in every state (Hold: Holds; Flick: Flicks)
        bool every = true;
        foreach (StoneState s in AllStates)
            foreach (Modifier m in new[] { Modifier.Straight, Modifier.Kick, Modifier.Stomp, Modifier.Uppercut })
                every &= StoneRules.CanApply(s, m);
        Check("straight, kick, stomp, uppercut apply in every state", every);
        Check("modify: kick on a still stone is allowed", ModifyCheck.Decide(Mod(Modifier.Kick, 9u, V3.Zero), true, StoneState.Still, new V3(-0.5f, -1.6f, 1.5f), new V3(0.5f, -0.6f, 2.5f),
            V3.Zero, -1.6f, 2.5f, 2.4f, 0.5f) == ModifyRefusal.None);

        // ruling C5: the impulses (forward along the yaw N s, up N s, spin rad/s about the flat right axis)
        Check("impulse: kick 1500 up, free", StoneRules.Impulse(Modifier.Kick, false) == (0f, 1500f, 0f));
        Check("impulse: kick 1500 up, grounded", StoneRules.Impulse(Modifier.Kick, true) == (0f, 1500f, 0f));
        Check("impulse: uppercut free 900 + 900", StoneRules.Impulse(Modifier.Uppercut, false) == (900f, 900f, 0f));
        Check("impulse: uppercut grounded 1300 + 1300, spin 5", StoneRules.Impulse(Modifier.Uppercut, true) == (1300f, 1300f, 5f));
        Check("impulse: stomp free 1500 down", StoneRules.Impulse(Modifier.Stomp, false) == (0f, -1500f, 0f));
        Check("impulse: stomp grounded none (it stays grounded)", StoneRules.Impulse(Modifier.Stomp, true) == (0f, 0f, 0f));
        Check("impulse: straight and the rest are not here", StoneRules.Impulse(Modifier.Straight, true) == (0f, 0f, 0f)
            && StoneRules.Impulse(Modifier.Parry, false) == (0f, 0f, 0f) && StoneRules.Impulse((Modifier)9, false) == (0f, 0f, 0f));
        StoneRules.SetImpulses(1000f, 2000f, 500f, 700f, 3f);
        Check("impulse: from the settings", StoneRules.Impulse(Modifier.Kick, true) == (0f, 1000f, 0f) && StoneRules.Impulse(Modifier.Stomp, false) == (0f, -2000f, 0f)
            && StoneRules.Impulse(Modifier.Uppercut, false) == (500f, 500f, 0f) && StoneRules.Impulse(Modifier.Uppercut, true) == (700f, 700f, 3f));
        StoneRules.SetImpulses(StoneRules.DefaultKickUp, StoneRules.DefaultStompDown, StoneRules.DefaultUppercutFree, StoneRules.DefaultUppercutGrounded, StoneRules.DefaultUppercutSpin);
        Check("impulse: defaults back", StoneRules.Impulse(Modifier.Uppercut, true) == (1300f, 1300f, 5f) && StoneRules.Impulse(Modifier.Kick, false) == (0f, 1500f, 0f));

        // the push in world terms: forward along the claimed flat yaw, the spin about its flat right axis (the top goes forward)
        Check("right of yaw 0 is +x", NearV(StoneRules.Right(SpawnPlan.Forward(0f)), new V3(1f, 0f, 0f)));
        Check("right of yaw 90 is -z", NearV(StoneRules.Right(SpawnPlan.Forward(90f)), new V3(0f, 0f, -1f)));
        var up0 = StoneRules.Push(Modifier.Uppercut, true, SpawnPlan.Forward(0f));
        Check("push: grounded uppercut along yaw 0", NearV(up0.impulse, new V3(0f, 1300f, 1300f)) && NearV(up0.spin, new V3(5f, 0f, 0f)));
        var up90 = StoneRules.Push(Modifier.Uppercut, true, SpawnPlan.Forward(90f));
        Check("push: grounded uppercut along yaw 90", NearV(up90.impulse, new V3(1300f, 1300f, 0f)) && NearV(up90.spin, new V3(0f, 0f, -5f)));
        var upFree = StoneRules.Push(Modifier.Uppercut, false, SpawnPlan.Forward(180f));
        Check("push: free uppercut along yaw 180, no spin", NearV(upFree.impulse, new V3(0f, 900f, -900f)) && NearV(upFree.spin, V3.Zero));
        Check("push: kick straight up whatever the yaw", NearV(StoneRules.Push(Modifier.Kick, false, SpawnPlan.Forward(37f)).impulse, new V3(0f, 1500f, 0f)));
        Check("push: stomp straight down", NearV(StoneRules.Push(Modifier.Stomp, false, SpawnPlan.Forward(37f)).impulse, new V3(0f, -1500f, 0f))
            && NearV(StoneRules.Push(Modifier.Stomp, true, SpawnPlan.Forward(37f)).impulse, V3.Zero));
        Check("push: a forward that is not flat or not length 1", NearV(StoneRules.Push(Modifier.Uppercut, false, new V3(0f, 5f, 2f)).impulse, new V3(0f, 900f, 900f)));
        Check("push: a forward that is not a number pushes only up", NearV(StoneRules.Push(Modifier.Uppercut, true, new V3(float.NaN, 0f, 1f)).impulse, new V3(0f, 1300f, 0f))
            && NearV(StoneRules.Push(Modifier.Uppercut, true, new V3(float.NaN, 0f, 1f)).spin, V3.Zero));

        // ruling C10: every modifier's push is velocity + impulse / mass, held to MaxStoneSpeed
        Check("pushed: a kick on a still 150 kg ball", NearV(StoneRules.Pushed(V3.Zero, new V3(0f, 1500f, 0f), 150f, 20f), new V3(0f, 10f, 0f)));
        Check("pushed: adds to its velocity", NearV(StoneRules.Pushed(new V3(3f, 0f, 0f), new V3(0f, 1500f, 0f), 300f, 20f), new V3(3f, 5f, 0f)));
        Check("pushed: held to the cap", NearV(StoneRules.Pushed(V3.Zero, new V3(0f, 0f, 5000f), 100f, 20f), new V3(0f, 0f, 20f)));
        Check("pushed: a second straight on a flying stone stops at the cap", NearV(StoneRules.Pushed(new V3(0f, 0f, 15f), new V3(0f, 0f, 1800f), 150f, 20f), new V3(0f, 0f, 20f)));
        V3 slanted = StoneRules.Pushed(new V3(10f, 0f, 0f), new V3(0f, 3000f, 0f), 100f, 20f);
        Check("pushed: the cap keeps the way", Near(slanted.Length, 20f) && Near(slanted.Y / slanted.X, 3f));
        Check("pushed: at the cap is not cut", NearV(StoneRules.Pushed(new V3(0f, 12f, 0f), new V3(0f, 0f, 1600f), 100f, 20f), new V3(0f, 12f, 16f)));
        Check("pushed: a mass that is not above 0 pushes nothing", NearV(StoneRules.Pushed(new V3(1f, 2f, 3f), new V3(0f, 1500f, 0f), 0f, 20f), new V3(1f, 2f, 3f))
            && NearV(StoneRules.Pushed(new V3(1f, 2f, 3f), new V3(0f, 1500f, 0f), -5f, 20f), new V3(1f, 2f, 3f)));
        Check("pushed: a mass that is not a number pushes nothing", NearV(StoneRules.Pushed(new V3(1f, 2f, 3f), new V3(0f, 1500f, 0f), float.NaN, 20f), new V3(1f, 2f, 3f))
            && NearV(StoneRules.Pushed(new V3(1f, 2f, 3f), new V3(0f, 1500f, 0f), float.PositiveInfinity, 20f), new V3(1f, 2f, 3f)));
        Check("pushed: a cap that is not above 0 is the default 20", Near(StoneRules.Pushed(V3.Zero, new V3(0f, 0f, 5000f), 100f, 0f).Length, StoneRules.DefaultMaxStoneSpeed)
            && Near(StoneRules.Pushed(V3.Zero, new V3(0f, 0f, 5000f), 100f, -3f).Length, 20f));
        Check("pushed: a cap that is not a number is the default 20", Near(StoneRules.Pushed(V3.Zero, new V3(0f, 0f, 5000f), 100f, float.NaN).Length, 20f)
            && Near(StoneRules.Pushed(V3.Zero, new V3(0f, 0f, 5000f), 100f, float.PositiveInfinity).Length, 20f));
        Check("pushed: a velocity that is not a number counts as none", NearV(StoneRules.Pushed(new V3(float.NaN, 0f, 0f), new V3(0f, 1500f, 0f), 150f, 20f), new V3(0f, 10f, 0f))
            && NearV(StoneRules.Pushed(new V3(0f, float.PositiveInfinity, 0f), V3.Zero, 150f, 20f), V3.Zero));
        Check("pushed: an impulse that is not a number pushes nothing", NearV(StoneRules.Pushed(new V3(1f, 0f, 0f), new V3(float.NaN, 1f, 1f), 150f, 20f), new V3(1f, 0f, 0f))
            && NearV(StoneRules.Pushed(new V3(1f, 0f, 0f), new V3(0f, float.NegativeInfinity, 0f), 150f, 20f), new V3(1f, 0f, 0f)));
        V3 huge = StoneRules.Pushed(new V3(3e38f, -3e38f, 3e38f), new V3(3e38f, 3e38f, -3e38f), 1e-38f, 20f);
        Check("pushed: huge numbers stay finite and capped", !float.IsNaN(huge.X) && !float.IsInfinity(huge.Y) && Near(huge.Length, 20f));
        Check("pushed: default cap 20 m/s", Near(StoneRules.DefaultMaxStoneSpeed, 20f));

        // Task 9's CARRY (a): the Ball is loose once it has risen, whatever its launch; the Pillar, Cube and Wall stand still
        Check("risen: the ball is loose", StoneRules.Risen(Ball) == StoneState.Free);
        Check("risen: pillar, cube, wall stand still", StoneRules.Risen(Pillar) == StoneState.Still && StoneRules.Risen(Cube) == StoneState.Still && StoneRules.Risen(Wall) == StoneState.Still);
        Check("risen: the disc is loose (it never rises)", StoneRules.Risen(Disc) == StoneState.Free);
        SpawnPlan.SetLaunch(SpawnPlan.DefaultDiscLift, SpawnPlan.DefaultDiscPush, SpawnPlan.DefaultDiscPushDelay, SpawnPlan.DefaultDiscHeight, 0f, 0f);
        Check("risen: the ball is loose with BallLaunchSpeed 0", StoneRules.Risen(Ball) == StoneState.Free && IsZero(SpawnPlan.Launch(Ball)));
        // Task 9's CARRY (b): SummonUnderLift 0 still sets a standing stone loose (no throw)
        Check("summon under: lift 0 still frees", StoneRules.SummonUnder(StoneState.Still) == (StoneState.Free, 0f) && StoneRules.SummonUnder(StoneState.Stomped) == (StoneState.Free, 0f));
        SpawnPlan.SetLaunch(SpawnPlan.DefaultDiscLift, SpawnPlan.DefaultDiscPush, SpawnPlan.DefaultDiscPushDelay, SpawnPlan.DefaultDiscHeight,
            SpawnPlan.DefaultBallLaunchSpeed, SpawnPlan.DefaultSummonUnderLift);
        Check("risen: launch defaults back", Near(SpawnPlan.Launch(Ball).first.Y, 750f) && Near(StoneRules.SummonUnder(StoneState.Still).lift, 4f));
    }

    static bool NearV(V3 a, V3 b) => Near(a.X, b.X) && Near(a.Y, b.Y) && Near(a.Z, b.Z);

    static bool IsZero((V3 first, float delay, V3 second) l) => NearV(l.first, V3.Zero) && l.delay == 0f && NearV(l.second, V3.Zero);

    // Task 9: the Disc, Ball, Cube and Wall (spec 3.4; rulings C1-C4)
    static void OtherStones() {
        // the Wall: 1.8 m ahead, turned to the yaw, rises like the Pillar
        var wall = SpawnPlan.For(Wall, new V3(1f, 1.7f, 2f), 30f, 0.2f);
        Check("plan: wall turned to the yaw", wall.yawDeg == 30f);
        Check("plan: wall 1.8 m ahead along yaw 30", Near(wall.end.X, 1f + 0.9f) && Near(wall.end.Z, 2f + 1.8f * (float)System.Math.Cos(System.Math.PI / 6.0))
            && Near(wall.end.Y, 0.2f + 1f) && Near(wall.start.Y, 0.2f - 1f) && Near(wall.riseSeconds, 0.45f));
        var cube = SpawnPlan.For(Cube, V3.Zero, 0f, -1.6f);
        Check("plan: cube rises like the pillar", SpawnPlan.Rises(Cube) && SpawnPlan.Rises(Pillar) && SpawnPlan.Rises(Wall)
            && Near(cube.riseSeconds, 0.35f) && Near(cube.start.Y, -2.1f) && Near(cube.end.Y, -1.1f) && Near(cube.end.Z, 1.5f));

        // ruling C1: the Disc appears loose in the air, 1.2 m ahead at ground + DiscHeightMetres 0.5, and does not rise
        var disc = SpawnPlan.For(Disc, new V3(2f, 1.7f, 3f), 0f, 0.25f);
        Check("plan: disc 1.2 m ahead at ground + 0.5", Near(disc.end.X, 2f) && Near(disc.end.Z, 3f + 1.2f) && Near(disc.end.Y, 0.25f + 0.5f));
        Check("plan: disc does not rise", !SpawnPlan.Rises(Disc) && NearV(disc.start, disc.end) && disc.riseSeconds == 0f);
        // ruling C1: the Ball rises from its top at ground level (ruling 5) over BallRiseSeconds 0.12 (0.6 m at 5 m/s)
        var ball = SpawnPlan.For(Ball, V3.Zero, 90f, -1.6f);
        float ballR = StoneTable.Of(Ball).Size.Y * 0.5f;
        Check("plan: ball starts with its top under the ground", ball.start.Y + ballR <= -1.6f + 1e-4f && Near(ball.start.Y + ballR, -1.6f));
        Check("plan: ball rises 1.2 m ahead in 0.12 s", SpawnPlan.Rises(Ball) && Near(ball.end.X, 1.2f) && Near(ball.end.Y, -1.6f + ballR) && Near(ball.riseSeconds, 0.12f));

        // ruling C2: impulses in N s in the stone's yaw frame (x right, y up, z ahead)
        var dl = SpawnPlan.Launch(Disc);
        Check("launch: disc delay 0.12 s", dl.delay == 0.12f);
        Check("launch: disc lift 300 up, then push 1400 ahead", NearV(dl.first, new V3(0f, 300f, 0f)) && NearV(dl.second, new V3(0f, 0f, 1400f)));
        var bl = SpawnPlan.Launch(Ball);
        Check("launch: ball 5 m/s up on its 150 kg", Near(bl.first.Y, 750f) && Near(bl.first.X, 0f) && Near(bl.first.Z, 0f) && bl.delay == 0f && NearV(bl.second, V3.Zero));
        Check("launch: cube none", IsZero(SpawnPlan.Launch(Cube)));
        Check("launch: pillar, wall, not a kind none", IsZero(SpawnPlan.Launch(Pillar)) && IsZero(SpawnPlan.Launch(Wall)) && IsZero(SpawnPlan.Launch((StoneKind)9)));
        SpawnPlan.SetLaunch(200f, 1000f, 0.2f, 0.8f, 4f, 3f);
        Check("launch: from the settings", NearV(SpawnPlan.Launch(Disc).first, new V3(0f, 200f, 0f)) && SpawnPlan.Launch(Disc).delay == 0.2f
            && NearV(SpawnPlan.Launch(Disc).second, new V3(0f, 0f, 1000f)) && Near(SpawnPlan.Launch(Ball).first.Y, 600f)
            && Near(SpawnPlan.For(Disc, V3.Zero, 0f, 1f).end.Y, 1.8f) && Near(StoneRules.SummonUnder(StoneState.Still).lift, 3f));
        SpawnPlan.SetLaunch(SpawnPlan.DefaultDiscLift, SpawnPlan.DefaultDiscPush, SpawnPlan.DefaultDiscPushDelay, SpawnPlan.DefaultDiscHeight,
            SpawnPlan.DefaultBallLaunchSpeed, SpawnPlan.DefaultSummonUnderLift);
        Check("launch: defaults back", SpawnPlan.Launch(Disc).delay == 0.12f && Near(SpawnPlan.For(Disc, V3.Zero, 0f, 0f).end.Y, 0.5f));

        // ruling C4: a stone summoned where one stands lifts it off when it is Still, Sliding or Stomped (SummonUnderLift 4 m/s)
        foreach (StoneState s in new[] { StoneState.Still, StoneState.Sliding, StoneState.Stomped }) {
            var u = StoneRules.SummonUnder(s);
            Check("summon under: " + s + " is lifted off, 4 m/s", u.state == StoneState.Free && Near(u.lift, 4f));
        }
        var free = StoneRules.SummonUnder(StoneState.Free);
        Check("summon under: free stays, no lift", free.state == StoneState.Free && free.lift == 0f);
        var rising = StoneRules.SummonUnder(StoneState.Rising);
        Check("summon under: rising stays, no lift", rising.state == StoneState.Rising && rising.lift == 0f);
        Check("summon under: floating, held, flicked stay", StoneRules.SummonUnder(StoneState.Floating) == (StoneState.Floating, 0f)
            && StoneRules.SummonUnder(StoneState.Held) == (StoneState.Held, 0f) && StoneRules.SummonUnder(StoneState.Flicked) == (StoneState.Flicked, 0f));

        // [EarthStones] Enabled defaults to all five
        bool[] on = StoneKinds.Parse(StoneKinds.DefaultEnabled, out string unknown);
        Check("enabled: all five by default", on[(int)Disc] && on[(int)Ball] && on[(int)Pillar] && on[(int)Cube] && on[(int)Wall] && unknown.Length == 0);
    }

    // Task 8: hits, damage, knockback, clashes (spec 2.2, 2.6, 3.4, 3.7; rulings 24 and 25)
    static void HitRules() {
        const float moving = 3f;
        // spec 3.4: base + 1 grounded + 1 moving faster than MovingSpeed
        Check("power: a still cube is 3", StoneRules.Power(Cube, StoneState.Still, 0f, moving) == 3);
        Check("power: a fast free disc is 1", StoneRules.Power(Disc, StoneState.Free, 5f, moving) == 1);
        Check("power: a slow free disc is 0", StoneRules.Power(Disc, StoneState.Free, 1f, moving) == 0);
        Check("power: a fast sliding pillar is 3", StoneRules.Power(Pillar, StoneState.Sliding, 8.4f, moving) == 3);
        Check("power: a fast free wall is 4", StoneRules.Power(Wall, StoneState.Free, 4f, moving) == 4);
        Check("power: faster than, not at", StoneRules.Power(Ball, StoneState.Free, 3f, moving) == 1 && StoneRules.Power(Ball, StoneState.Free, 3.01f, moving) == 2);
        Check("power: a speed that is not a number is not moving", StoneRules.Power(Ball, StoneState.Free, float.NaN, moving) == 1);
        Check("power: a byte that is not a kind", StoneRules.Power((StoneKind)9, StoneState.Free, 0f, moving) == 0);

        // spec 2.2: equal power both break, else the weaker
        Check("clash: equal, both break", StoneRules.Clash(2, 2) == ClashResult.BothBreak);
        Check("clash: the weaker B breaks", StoneRules.Clash(3, 1) == ClashResult.BBreaks);
        Check("clash: the weaker A breaks", StoneRules.Clash(1, 3) == ClashResult.ABreaks);
        Check("clash: zero against zero", StoneRules.Clash(0, 0) == ClashResult.BothBreak);

        // spec 3.7: DamagePerPower x power once the relative speed is at least MinHitSpeed
        Check("damage: too slow", StoneRules.Damage(2, 1.5f, 8f, 2f) == 0f);
        Check("damage: 2 x 8", StoneRules.Damage(2, 5f, 8f, 2f) == 16f);
        Check("damage: at MinHitSpeed counts", StoneRules.Damage(2, 2f, 8f, 2f) == 16f);
        Check("damage: power 0 deals nothing", StoneRules.Damage(0, 9f, 8f, 2f) == 0f);
        Check("damage: a speed that is not a number", StoneRules.Damage(3, float.NaN, 8f, 2f) == 0f);
        Check("damage: no damage per power", StoneRules.Damage(3, 5f, 0f, 2f) == 0f && StoneRules.Damage(3, 5f, float.NaN, 2f) == 0f && StoneRules.Damage(3, 5f, -1f, 2f) == 0f);
        Check("cripple: 0.5 x power", Near(StoneRules.Cripple(3, 0.5f), 1.5f) && StoneRules.Cripple(0, 0.5f) == 0f && StoneRules.Cripple(2, float.NaN) == 0f);
        Check("stun: power x 0.3 s", Near(StoneRules.StunSeconds(3), 0.9f) && StoneRules.StunSeconds(0) == 0f && StoneRules.StunSeconds(-1) == 0f);

        // spec 2.6: flat or 45 degrees up, whichever is nearer
        float r = (float)System.Math.Sqrt(0.5);
        Check("knock: a little up is flat", NearV(StoneRules.KnockDir(new V3(1f, 0.1f, 0f)), new V3(1f, 0f, 0f)));
        Check("knock: well up is 45 degrees", NearV(StoneRules.KnockDir(new V3(1f, 0.8f, 0f)), new V3(r, r, 0f)));
        Check("knock: down is flat", NearV(StoneRules.KnockDir(new V3(0f, -3f, 4f)), new V3(0f, 0f, 1f)));
        Check("knock: just over 22.5 degrees is 45", NearV(StoneRules.KnockDir(new V3(0f, 0.42f, -1f)), new V3(0f, r, -r))
            && NearV(StoneRules.KnockDir(new V3(0f, 0.41f, -1f)), new V3(0f, 0f, -1f)));
        Check("knock: unit length", Near(StoneRules.KnockDir(new V3(3f, 2f, -4f)).Length, 1f));
        Check("knock: straight down takes the way away from the stone", NearV(StoneRules.KnockDir(new V3(0f, -5f, 0f), new V3(2f, 1f, 0f)), new V3(1f, 0f, 0f)));
        Check("knock: straight up is 45 degrees away", NearV(StoneRules.KnockDir(new V3(0f, 5f, 0f), new V3(0f, -0.3f, 3f)), new V3(0f, r, r)));
        Check("knock: nothing to go by", NearV(StoneRules.KnockDir(V3.Zero), V3.Zero) && NearV(StoneRules.KnockDir(new V3(float.NaN, 0f, 1f)), V3.Zero)
            && NearV(StoneRules.KnockDir(new V3(0f, -5f, 0f), V3.Zero), V3.Zero));

        // spec 2.6: hitstun power x 0.7 / 3 s, never from a disc
        Check("hitstun: a disc never stuns", StoneRules.HitstunSeconds(Disc, 1) == 0f && StoneRules.HitstunSeconds(Disc, 3) == 0f);
        Check("hitstun: a power-3 wall 0.7 s", Near(StoneRules.HitstunSeconds(Wall, 3), 0.7f));
        Check("hitstun: a power-2 pillar", Near(StoneRules.HitstunSeconds(Pillar, 2), 1.4f / 3f) && StoneRules.HitstunSeconds(Pillar, 0) == 0f);

        // ruling 25: a rising stone never hits or clashes; ruling 24: a hit needs MinHitSpeed
        Check("can hit: moving", StoneRules.CanHit(StoneState.Sliding, 2f, 2f) && StoneRules.CanHit(StoneState.Free, 9f, 2f) && StoneRules.CanHit(StoneState.Still, 3f, 2f));
        Check("can hit: too slow", !StoneRules.CanHit(StoneState.Sliding, 1.9f, 2f) && !StoneRules.CanHit(StoneState.Free, float.NaN, 2f));
        Check("can hit: never rising", !StoneRules.CanHit(StoneState.Rising, 9f, 2f));
        Check("clashes: one fast enough", StoneRules.Clashes(StoneState.Sliding, StoneState.Still, 8f, 2f) && StoneRules.Clashes(StoneState.Free, StoneState.Free, 2f, 2f));
        Check("clashes: too slow", !StoneRules.Clashes(StoneState.Sliding, StoneState.Still, 1f, 2f) && !StoneRules.Clashes(StoneState.Sliding, StoneState.Still, float.NaN, 2f));
        Check("clashes: never with a rising stone", !StoneRules.Clashes(StoneState.Rising, StoneState.Sliding, 8f, 2f) && !StoneRules.Clashes(StoneState.Sliding, StoneState.Rising, 8f, 2f));
        Check("approaching: toward the player", StoneRules.Approaching(new V3(0f, 0f, 5f), new V3(0.2f, 0f, 1f)));
        Check("approaching: away from the player (its owner pushing it)", !StoneRules.Approaching(new V3(0f, 0f, 5f), new V3(0f, 0f, -0.2f)));
        Check("approaching: sliding past beside", !StoneRules.Approaching(new V3(0f, 0f, 5f), new V3(0.25f, 0f, 0f)));
        Check("approaching: nothing to go by", !StoneRules.Approaching(V3.Zero, new V3(0f, 0f, 1f)) && !StoneRules.Approaching(new V3(0f, 0f, 5f), V3.Zero)
            && !StoneRules.Approaching(new V3(float.NaN, 0f, 5f), new V3(0f, 0f, 1f)));
        Check("player radius 0.3 m", Near(StoneRules.PlayerRadius, 0.3f));
        // the controller's rulings on Task 8: a creature is hit on the STONE's own speed (a creature walking into a standing
        // stone is not hit and does not break it); a player only by a stone coming toward them, and not at all with HurtPlayers off
        Check("creature: a standing stone is never a hit", !StoneRules.CanHit(StoneState.Still, 0f, 2f) && !StoneRules.CanHit(StoneState.Sliding, 1.5f, 2f));
        Check("creature: a stone moving at MinHitSpeed hits", StoneRules.CanHit(StoneState.Sliding, 2f, 2f));
        var toward = new V3(0f, 0f, 1f);
        Check("player: a fast stone coming toward them", StoneRules.HitsPlayer(StoneState.Sliding, new V3(0f, 0f, 8f), toward, 2f, true));
        Check("player: HurtPlayers off ignores players", !StoneRules.HitsPlayer(StoneState.Sliding, new V3(0f, 0f, 8f), toward, 2f, false)
            && !StoneRules.HitsPlayer(StoneState.Free, new V3(0f, 0f, 20f), toward, 0f, false));
        Check("player: going away is no hit", !StoneRules.HitsPlayer(StoneState.Sliding, new V3(0f, 0f, -8f), toward, 2f, true));
        Check("player: too slow is no hit", !StoneRules.HitsPlayer(StoneState.Sliding, new V3(0f, 0f, 1.9f), toward, 2f, true));
        Check("player: never by a rising stone", !StoneRules.HitsPlayer(StoneState.Rising, new V3(0f, 8f, 0f), new V3(0f, 1f, 0f), 2f, true));
        Check("player: a velocity that is not a number", !StoneRules.HitsPlayer(StoneState.Free, new V3(float.NaN, 0f, 8f), toward, 2f, true));

        // ruling 24: a player is a capsule from the head down to the feet, radius 0.3, against the stone's box (in its own frame)
        var half = new V3(0.5f, 0.5f, 0.5f);   // a 1 m cube centred at the origin
        Check("segment: through the box", Near(Contact.SegmentBox(new V3(0f, 2f, 0f), new V3(0f, -2f, 0f), half, out _, out _), 0f));
        Check("segment: beside a face", Near(Contact.SegmentBox(new V3(0.8f, 1f, 0f), new V3(0.8f, -1f, 0f), half, out V3 onSeg, out V3 onBox), 0.3f)
            && Near(onSeg.X, 0.8f) && Near(onBox.X, 0.5f) && onSeg.Y <= 0.5f + 1e-4f && onSeg.Y >= -0.5f - 1e-4f);
        Check("segment: beside an edge", Near(Contact.SegmentBox(new V3(0.8f, 1f, 0.8f), new V3(0.8f, -1f, 0.8f), half, out _, out _), (float)System.Math.Sqrt(0.18)));
        Check("segment: above, its end nearest", Near(Contact.SegmentBox(new V3(0f, 3f, 0f), new V3(0f, 1.2f, 0f), half, out V3 end, out _), 0.7f) && Near(end.Y, 1.2f));
        Check("segment: slanted past a corner", Near(Contact.SegmentBox(new V3(1.5f, 0.5f, -2f), new V3(1.5f, 0.5f, 2f), half, out _, out _), 1f)
            && Near(Contact.SegmentBox(new V3(-2f, 2f, 0f), new V3(2f, -2f, 2.2f), new V3(0.5f, 0.5f, 0.5f), out _, out _), Brute(new V3(-2f, 2f, 0f), new V3(2f, -2f, 2.2f), half)));
        Check("segment: a point", Near(Contact.SegmentBox(new V3(0f, 0f, 1.5f), new V3(0f, 0f, 1.5f), half, out _, out _), 1f));
        Check("segment: not a number", float.IsPositiveInfinity(Contact.SegmentBox(new V3(float.NaN, 0f, 0f), new V3(0f, 0f, 0f), half, out _, out _))
            && float.IsPositiveInfinity(Contact.SegmentBox(V3.Zero, new V3(0f, 1f, 0f), new V3(float.NaN, 1f, 1f), out _, out _)));
        Check("capsule: within the radius touches", Contact.CapsuleTouchesBox(new V3(0.79f, 1.7f, 0f), new V3(0.79f, 0f, 0f), StoneRules.PlayerRadius, half)
            && !Contact.CapsuleTouchesBox(new V3(0.81f, 1.7f, 0f), new V3(0.81f, 0f, 0f), StoneRules.PlayerRadius, half));
        Check("capsule: a pillar sliding along the ground reaches the feet", Contact.CapsuleTouchesBox(new V3(0f, 1.7f, -0.5f), new V3(0f, -0.9f, -0.5f), 0.3f, new V3(0.275f, 0.9f, 0.275f)));
        Check("capsule: the feet's end over the top", Contact.CapsuleTouchesBox(new V3(0f, 2.5f, 0f), new V3(0f, 0.79f, 0f), 0.3f, half) && !Contact.CapsuleTouchesBox(new V3(0f, 2.5f, 0f), new V3(0f, 0.81f, 0f), 0.3f, half));
        Check("capsule: not a number never touches", !Contact.CapsuleTouchesBox(new V3(float.NaN, 1f, 0f), V3.Zero, 0.3f, half) && !Contact.CapsuleTouchesBox(V3.Zero, V3.Zero, float.NaN, half));
        Check("segment and point (the Ball)", Near(Contact.SegmentPoint(new V3(1f, 2f, 0f), new V3(1f, -2f, 0f), V3.Zero, out V3 sp), 1f) && Near(sp.Y, 0f)
            && Near(Contact.SegmentPoint(new V3(0f, 2f, 0f), new V3(0f, 1f, 0f), V3.Zero, out _), 1f) && Near(Contact.SegmentPoint(V3.Zero, V3.Zero, new V3(0f, 3f, 4f), out _), 5f)
            && float.IsPositiveInfinity(Contact.SegmentPoint(new V3(float.NaN, 0f, 0f), V3.Zero, V3.Zero, out _)));
        // a random sweep against the brute force: every case within 1 mm
        var rng = new System.Random(8);
        bool sweep = true;
        for (int i = 0; i < 300 && sweep; i++) {
            V3 a = new V3(R(rng), R(rng), R(rng)), b = new V3(R(rng), R(rng), R(rng)), h = new V3(0.1f + (float)rng.NextDouble(), 0.1f + (float)rng.NextDouble(), 0.1f + (float)rng.NextDouble());
            float got = Contact.SegmentBox(a, b, h, out V3 s, out V3 q), want = Brute(a, b, h);
            sweep = System.Math.Abs(got - want) < 1e-3f && System.Math.Abs((s - q).Length - got) < 1e-3f;
            if (!sweep) System.Console.WriteLine($"  segment {a} {b} half {h}: {got} (brute {want})");
        }
        Check("segment: 300 random cases match the brute force", sweep);

        // one hit per target per stone, at most once per HitCooldownSeconds
        var hits = new HitLedger();
        Check("ledger: the first hit", hits.TryHit(5u, 100, 10.0, 0.5f));
        Check("ledger: the same pair within the cooldown", !hits.TryHit(5u, 100, 10.3, 0.5f));
        Check("ledger: another target, another stone", hits.TryHit(5u, 101, 10.3, 0.5f) && hits.TryHit(6u, 100, 10.3, 0.5f));
        Check("ledger: after the cooldown", hits.TryHit(5u, 100, 10.5, 0.5f));
        hits.Forget(5u);
        Check("ledger: a broken stone is forgotten", hits.Count == 1 && hits.TryHit(5u, 100, 10.6, 0.5f));
        Check("ledger: no time", !hits.TryHit(7u, 100, double.NaN, 0.5f));
        var many = new HitLedger();
        for (int i = 0; i < HitLedger.MaxEntries + 10; i++) many.TryHit((uint)i + 1u, 1, i * 0.001, 0.5f);
        many.TryHit(999999u, 1, 100.0, 0.5f);
        Check("ledger: old entries go once it is full", many.Count <= HitLedger.MaxEntries && many.Count >= 1);
    }

    static float R(System.Random rng) => (float)(rng.NextDouble() * 6.0 - 3.0);

    // the segment-to-box distance by sampling 20001 points and refining round the best (the test's own, slow way)
    static float Brute(V3 a, V3 b, V3 h) {
        double best = double.MaxValue, bestT = 0;
        for (int i = 0; i <= 20000; i++) {
            double t = i / 20000.0, d = BoxDist(a, b, h, t);
            if (d < best) { best = d; bestT = t; }
        }
        for (int i = -1000; i <= 1000; i++) {
            double t = bestT + i / 2e7;
            if (t < 0 || t > 1) continue;
            double d = BoxDist(a, b, h, t);
            if (d < best) best = d;
        }
        return (float)best;
    }

    static double BoxDist(V3 a, V3 b, V3 h, double t) {
        double x = a.X + (b.X - a.X) * t, y = a.Y + (b.Y - a.Y) * t, z = a.Z + (b.Z - a.Z) * t;
        double dx = System.Math.Max(System.Math.Abs(x) - h.X, 0), dy = System.Math.Max(System.Math.Abs(y) - h.Y, 0), dz = System.Math.Max(System.Math.Abs(z) - h.Z, 0);
        return System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    // A cube of side `size` centred at (x, y, z), as (min, max).
    static (V3 min, V3 max) Box(float x, float y, float z, float size) {
        float h = size * 0.5f;
        return (new V3(x - h, y - h, z - h), new V3(x + h, y + h, z + h));
    }

    static (uint id, V3 min, V3 max) Stone(uint id, float x, float y, float z, float size) {
        (V3 min, V3 max) b = Box(x, y, z, size);
        return (id, b.min, b.max);
    }

    static ModifyClaim Mod(Modifier m, uint target, V3 head) => new ModifyClaim(m, 0, target, head, 0f);

    // Task 7: Straight (the reach rule, the target pick, the server's check of a Modify claim, the states, the pushback)
    static void Straights() {
        // the head at the origin, the feet 1.6 m under it: a 1 m cube on the ground has its centre at -1.1
        var head = V3.Zero;
        const float feet = -1.6f;

        // spec 2.4: the reach rule
        Check("reach: cube 2 m ahead", Reach.InReach(head, 0f, Box(0, -1.1f, 2f, 1f), feet, 2.5f, 2.4f, 0f));
        Check("reach: behind", !Reach.InReach(head, 0f, Box(0, -1.1f, -1.5f, 1f), feet, 2.5f, 2.4f, 0f));
        Check("reach: 3.2 m", !Reach.InReach(head, 0f, Box(0, -1.1f, 3.7f, 1f), feet, 2.5f, 2.4f, 0f));
        Check("reach: slack lets 2.9 m in", Reach.InReach(head, 0f, Box(0, -1.1f, 3.4f, 1f), feet, 2.5f, 2.4f, 0.5f));
        Check("reach: too high", !Reach.InReach(head, 0f, Box(0, 1.2f, 1f, 1f), feet, 2.5f, 2.4f, 0f));
        Check("reach: slack lifts the height too", Reach.InReach(head, 0f, Box(0, 1.2f, 1f, 1f), feet, 2.5f, 2.4f, 0.5f));
        Check("reach: yaw 90 faces +x", Reach.InReach(head, 90f, Box(2f, -1.1f, 0f, 1f), feet, 2.5f, 2.4f, 0f)
            && !Reach.InReach(head, 90f, Box(-2f, -1.1f, 0f, 1f), feet, 2.5f, 2.4f, 0f)
            && !Reach.InReach(head, 90f, Box(-0.01f, -1.1f, 1f, 1f), feet, 2.5f, 2.4f, 0f));   // beside, its centre just behind the plane
        Check("reach: the nearest point counts, not the centre", Reach.InReach(head, 0f, Box(0, -1.1f, 2.9f, 2f), feet, 2.5f, 2.4f, 0f));
        Check("reach: separate corners", Reach.InReach(head, 0f, new V3(-0.5f, -1.6f, 1.5f), new V3(0.5f, -0.6f, 2.5f), feet, 2.5f, 2.4f, 0f));
        Check("reach: a head that is not a number", !Reach.InReach(new V3(float.NaN, 0f, 0f), 0f, Box(0, -1.1f, 2f, 1f), feet, 2.5f, 2.4f, 0.5f));
        Check("reach: a yaw that is not a number", !Reach.InReach(head, float.NaN, Box(0, -1.1f, 2f, 1f), feet, 2.5f, 2.4f, 0.5f));

        // the client's pick: the smallest angle from the flat forward, ties to the nearer
        double l = 40.0 * System.Math.PI / 180.0;
        var left40 = Stone(1u, (float)(-1.5 * System.Math.Sin(l)), -1.1f, (float)(1.5 * System.Math.Cos(l)), 0.5f);
        Check("pick: the one most in front", Reach.Pick(head, 0f, new[] { left40, Stone(2u, 0f, -1.1f, 2f, 1f) }, feet, 2.5f, 2.4f) == 2u);
        Check("pick: ties to the nearer", Reach.Pick(head, 0f, new[] { Stone(3u, 0f, -1.1f, 2.4f, 1f), Stone(4u, 0f, -1.1f, 1.5f, 1f) }, feet, 2.5f, 2.4f) == 4u
            && Reach.Pick(head, 0f, new[] { Stone(4u, 0f, -1.1f, 1.5f, 1f), Stone(3u, 0f, -1.1f, 2.4f, 1f) }, feet, 2.5f, 2.4f) == 4u);
        Check("pick: one out of reach is never picked", Reach.Pick(head, 0f, new[] { Stone(5u, 0f, -1.1f, 3.7f, 1f), left40 }, feet, 2.5f, 2.4f) == 1u);
        Check("pick: none in reach", Reach.Pick(head, 0f, new[] { Stone(5u, 0f, -1.1f, 3.7f, 1f), Stone(6u, 0f, -1.1f, -2f, 1f) }, feet, 2.5f, 2.4f) == 0u);
        Check("pick: no stones", Reach.Pick(head, 0f, new (uint, V3, V3)[0], feet, 2.5f, 2.4f) == 0u && Reach.Pick(head, 0f, null, feet, 2.5f, 2.4f) == 0u);
        Check("pick: id 0 is no stone", Reach.Pick(head, 0f, new[] { Stone(0u, 0f, -1.1f, 2f, 1f) }, feet, 2.5f, 2.4f) == 0u);
        Check("pick: along the yaw", Reach.Pick(head, 90f, new[] { Stone(7u, 0f, -1.1f, 2f, 1f), Stone(8u, 2f, -1.1f, 0.6f, 1f) }, feet, 2.5f, 2.4f) == 8u);

        // states
        Check("straight on still -> sliding", StoneRules.After(StoneState.Still, Modifier.Straight) == StoneState.Sliding);
        Check("straight on free -> free", StoneRules.After(StoneState.Free, Modifier.Straight) == StoneState.Free);
        Check("straight on rising -> sliding", StoneRules.After(StoneState.Rising, Modifier.Straight) == StoneState.Sliding);
        Check("straight on sliding -> sliding", StoneRules.After(StoneState.Sliding, Modifier.Straight) == StoneState.Sliding);
        Check("straight on flicked -> flicked", StoneRules.After(StoneState.Flicked, Modifier.Straight) == StoneState.Flicked);
        Check("explode leaves the state", StoneRules.After(StoneState.Free, Modifier.Explode) == StoneState.Free);
        Check("straight applies to every state", StoneRules.CanApply(StoneState.Still, Modifier.Straight) && StoneRules.CanApply(StoneState.Free, Modifier.Straight)
            && StoneRules.CanApply(StoneState.Rising, Modifier.Straight) && StoneRules.CanApply(StoneState.Sliding, Modifier.Straight));
        Check("flick never on a still stone", !StoneRules.CanApply(StoneState.Still, Modifier.Flick));
        Check("no modifier past Explode", !StoneRules.CanApply(StoneState.Free, (Modifier)8));

        // the slide: slowed by its deceleration, still again under 0.1 m/s
        Check("slide: slows", Near(StoneRules.SlideSpeed(8f, 4f, 0.5f), 6f) && Near(StoneRules.SlideSpeed(1f, 4f, 0.5f), 0f));
        Check("slide: 0 deceleration keeps it", Near(StoneRules.SlideSpeed(8f, 0f, 0.5f), 8f));
        Check("slide: a speed that is not a number stops", Near(StoneRules.SlideSpeed(float.NaN, 4f, 0.02f), 0f));
        Check("slide: stops under 0.1 m/s", StoneRules.SlideStops(0.09f) && !StoneRules.SlideStops(0.1f) && !StoneRules.SlideStops(3f) && StoneRules.SlideStops(float.NaN));
        // ruling 22: each physics step of a slide, from whether it touched ground in the last step, how long since it last did
        // (or was pushed), its flat speed and whether the push has landed (the step after the push)
        Check("slide step: slow and touching -> still", StoneRules.Slide(true, 0.02, 0.05f, true) == SlideStep.Still);
        Check("slide step: fast and touching slides", StoneRules.Slide(true, 0.02, 3f, true) == SlideStep.Slides);
        Check("slide step: before the push lands it slides", StoneRules.Slide(true, 0.02, 0f, false) == SlideStep.Slides);
        Check("slide step: a moment off the ground slides on", StoneRules.Slide(false, 0.19, 3f, true) == SlideStep.Slides);
        Check("slide step: slow in the air is never still", StoneRules.Slide(false, 0.1, 0.05f, true) == SlideStep.Slides);
        Check("slide step: 0.2 s with no ground -> free", StoneRules.Slide(false, 0.2, 3f, true) == SlideStep.Free && StoneRules.Slide(false, 1.0, 0.05f, true) == SlideStep.Free);
        Check("slide step: no time -> free", StoneRules.Slide(false, double.NaN, 3f, true) == SlideStep.Free);
        Check("slide step: touching never frees", StoneRules.Slide(true, 5.0, 3f, true) == SlideStep.Slides);
        Check("slide step: 0.2 s and 60 degrees", Near(StoneRules.SlideAirSeconds, 0.2f) && Near(StoneRules.GroundNormalY, 0.5f));
        Check("ground contact: up-facing normals", StoneRules.IsGroundContact(1f) && StoneRules.IsGroundContact(0.5f) && !StoneRules.IsGroundContact(0.49f) && !StoneRules.IsGroundContact(-1f) && !StoneRules.IsGroundContact(float.NaN));

        // the server's check of a Modify claim (its own head and feet, the claimed yaw)
        var still = (StoneState?)StoneState.Still;
        (V3 min, V3 max) ahead = Box(0, -1.1f, 2f, 1f), far = Box(0f, 0f, 3.8f, 1f);   // far: its nearest point 3.3 m from the head
        Check("modify: ok", ModifyCheck.Decide(Mod(Modifier.Straight, 9u, head), true, still, ahead.min, ahead.max, head, feet, 2.5f, 2.4f, 0.5f) == ModifyRefusal.None);
        Check("modify: not a stone", ModifyCheck.Decide(Mod(Modifier.Straight, 9u, head), true, null, ahead.min, ahead.max, head, feet, 2.5f, 2.4f, 0.5f) == ModifyRefusal.NoStone);
        Check("modify: out of reach with slack", ModifyCheck.Decide(Mod(Modifier.Straight, 9u, head), true, still, far.min, far.max, head, feet, 2.5f, 2.4f, 0.5f) == ModifyRefusal.OutOfReach);
        Check("modify: cooldown", ModifyCheck.Decide(Mod(Modifier.Straight, 9u, head), false, still, ahead.min, ahead.max, head, feet, 2.5f, 2.4f, 0.5f) == ModifyRefusal.Cooldown);
        Check("modify: not allowed", ModifyCheck.Decide(Mod(Modifier.Flick, 9u, head), true, still, ahead.min, ahead.max, head, feet, 2.5f, 2.4f, 0.5f) == ModifyRefusal.NotAllowed);
        Check("modify: the server's head, not the claimed one", ModifyCheck.Decide(Mod(Modifier.Straight, 9u, new V3(0f, 0f, 3f)), true, still, far.min, far.max, head, feet, 2.5f, 2.4f, 0.5f) == ModifyRefusal.OutOfReach
            && ModifyCheck.Decide(Mod(Modifier.Straight, 9u, new V3(0f, 0f, 3f)), true, still, ahead.min, ahead.max, head, feet, 2.5f, 2.4f, 0.5f) == ModifyRefusal.None);
        Check("modify: the server's head unknown", ModifyCheck.Decide(Mod(Modifier.Straight, 9u, head), true, still, ahead.min, ahead.max, new V3(float.NaN, 0f, 0f), feet, 2.5f, 2.4f, 0.5f) == ModifyRefusal.OutOfReach);
        Check("modify: the claimed yaw", ModifyCheck.Decide(new ModifyClaim(Modifier.Straight, 0, 9u, head, 180f), true, still, ahead.min, ahead.max, head, feet, 2.5f, 2.4f, 0.5f) == ModifyRefusal.OutOfReach);
        // the order: bad, cooldown, the stone, its reach, the state
        Check("modify: cooldown before the stone", ModifyCheck.Decide(Mod(Modifier.Straight, 9u, head), false, null, ahead.min, ahead.max, head, feet, 2.5f, 2.4f, 0.5f) == ModifyRefusal.Cooldown);
        Check("modify: the stone before its reach", ModifyCheck.Decide(Mod(Modifier.Straight, 9u, head), true, null, far.min, far.max, head, feet, 2.5f, 2.4f, 0.5f) == ModifyRefusal.NoStone);
        Check("modify: reach before the state", ModifyCheck.Decide(Mod(Modifier.Flick, 9u, head), true, still, far.min, far.max, head, feet, 2.5f, 2.4f, 0.5f) == ModifyRefusal.OutOfReach);
        // ruling 13: a claim no game of this mod sends
        Check("modify bad: modifier byte out of range", ModifyCheck.Decide(Mod((Modifier)8, 9u, head), true, still, ahead.min, ahead.max, head, feet, 2.5f, 2.4f, 0.5f) == ModifyRefusal.Bad);
        Check("modify bad: hand byte out of range", ModifyCheck.Decide(new ModifyClaim(Modifier.Straight, 2, 9u, head, 0f), true, still, ahead.min, ahead.max, head, feet, 2.5f, 2.4f, 0.5f) == ModifyRefusal.Bad
            && ModifyCheck.Decide(new ModifyClaim(Modifier.Straight, 1, 9u, head, 0f), true, still, ahead.min, ahead.max, head, feet, 2.5f, 2.4f, 0.5f) == ModifyRefusal.None);
        Check("modify bad: NaN head", ModifyCheck.Decide(Mod(Modifier.Straight, 9u, new V3(0f, float.NaN, 0f)), true, still, ahead.min, ahead.max, head, feet, 2.5f, 2.4f, 0.5f) == ModifyRefusal.Bad);
        Check("modify bad: infinite head", ModifyCheck.Decide(Mod(Modifier.Straight, 9u, new V3(float.PositiveInfinity, 0f, 0f)), true, still, ahead.min, ahead.max, head, feet, 2.5f, 2.4f, 0.5f) == ModifyRefusal.Bad);
        Check("modify bad: NaN or infinite yaw", ModifyCheck.Decide(new ModifyClaim(Modifier.Straight, 0, 9u, head, float.NaN), true, still, ahead.min, ahead.max, head, feet, 2.5f, 2.4f, 0.5f) == ModifyRefusal.Bad
            && ModifyCheck.Decide(new ModifyClaim(Modifier.Straight, 0, 9u, head, float.NegativeInfinity), true, still, ahead.min, ahead.max, head, feet, 2.5f, 2.4f, 0.5f) == ModifyRefusal.Bad);
        Check("modify bad: yaw past a turn", ModifyCheck.Decide(new ModifyClaim(Modifier.Straight, 0, 9u, head, 1e30f), true, still, ahead.min, ahead.max, head, feet, 2.5f, 2.4f, 0.5f) == ModifyRefusal.Bad);
        Check("modify bad before cooldown", ModifyCheck.Decide(Mod((Modifier)200, 9u, head), false, null, ahead.min, ahead.max, head, feet, 2.5f, 2.4f, 0.5f) == ModifyRefusal.Bad);
        Check("modify sane", ModifyCheck.Sane(Mod(Modifier.Explode, 1u, head)) && !ModifyCheck.Sane(Mod((Modifier)8, 1u, head)));
        Check("move of a modifier", ModifyCheck.MoveOf(Modifier.Straight) == Move.Straight && ModifyCheck.MoveOf(Modifier.Explode) == Move.Explode && ModifyCheck.MoveOf(Modifier.Hold) == Move.Hold);
        Check("modifier of a move", ModifyCheck.ModifierOf(Move.Kick, out Modifier km) && km == Modifier.Kick && ModifyCheck.ModifierOf(Move.Explode, out Modifier em) && em == Modifier.Explode
            && !ModifyCheck.ModifierOf(Move.Wall, out _) && !ModifyCheck.ModifierOf(Move.Jump, out _) && !ModifyCheck.ModifierOf(Move.None, out _));

        // the player's own pushback (spec 3.5): back along the yaw, a little up, half in the air
        Check("pushback grounded, yaw 0", Near(MoveRules.Pushback(0f, true, false, 2.5f, 1.7f, 0.25f).Z, -2.5f) && Near(MoveRules.Pushback(0f, true, false, 2.5f, 1.7f, 0.25f).Y, 0.25f));
        Check("pushback airborne halves", Near(MoveRules.Pushback(0f, false, true, 2.5f, 1.7f, 0.25f).Z, -0.85f));
        Check("pushback free, on the ground", Near(MoveRules.Pushback(0f, false, false, 2.5f, 1.7f, 0.25f).Z, -1.7f) && Near(MoveRules.Pushback(0f, false, false, 2.5f, 1.7f, 0.25f).X, 0f));
        Check("pushback yaw 90 goes to -x", Near(MoveRules.Pushback(90f, true, false, 2.5f, 1.7f, 0.25f).X, -2.5f) && Near(MoveRules.Pushback(90f, true, false, 2.5f, 1.7f, 0.25f).Z, 0f));
        Check("pushback airborne halves the lift", Near(MoveRules.Pushback(0f, true, true, 2.5f, 1.7f, 0.25f).Y, 0.125f) && Near(MoveRules.Pushback(0f, true, true, 2.5f, 1.7f, 0.25f).Z, -1.25f));
    }

    // Task 6: summoning (the server's checks, the cooldown ledger, where a stone rises, when it breaks, the caps)
    static void Summons() {
        var head = new V3(10f, 1.7f, 5f);
        var X = new V3(1f, 0f, 0f);
        var ledger = new Ledger();

        Check("summon ok", SummonCheck.Decide(Claim(Pillar, head), head, true, true, true, 1f) == SummonRefusal.None);
        Check("head claimed 3 m off", SummonCheck.Decide(Claim(Pillar, head + 3*X), head, true, true, true, 1f) == SummonRefusal.HeadFar);
        Check("not natural", SummonCheck.Decide(Claim(Pillar, head), head, false, true, true, 1f) == SummonRefusal.NotNatural);
        Check("kind off", SummonCheck.Decide(Claim(Wall, head), head, true, false, true, 1f) == SummonRefusal.KindOff);
        Check("cooldown 0.3", ledger.TrySpend(7, (int)Move.Pillar, 10.0, 0.3f) && !ledger.TrySpend(7, (int)Move.Pillar, 10.2, 0.3f) && ledger.TrySpend(7, (int)Move.Pillar, 10.31, 0.3f));
        Check("cooldown per player", ledger.TrySpend(8, (int)Move.Pillar, 10.2, 0.3f));
        Check("plan: cube 1.5 m ahead along yaw 90", Near(SpawnPlan.For(Cube, V3.Zero, 90f, -1.6f).end.X, 1.5f) && Near(SpawnPlan.For(Cube, V3.Zero, 90f, -1.6f).end.Y, -1.1f));
        Check("plan: starts underground", Near(SpawnPlan.For(Pillar, V3.Zero, 0f, -1.6f).start.Y, -2.5f));   // ruling 5: its top at ground level
        Check("life: old", StoneLife.ShouldBreak(61f, 60f, 0f, true));
        Check("life: void", StoneLife.ShouldBreak(1f, 60f, -101f, true));
        Check("life: owner gone", StoneLife.ShouldBreak(1f, 60f, 0f, false));
        Check("life: fine", !StoneLife.ShouldBreak(1f, 60f, 0f, true));
        Check("cap: room", Caps.Decide(3, 10, 8, 64) == CapAction.Spawn);
        Check("cap: ninth breaks oldest", Caps.Decide(8, 10, 8, 64) == CapAction.BreakOldest);
        Check("cap: server full", Caps.Decide(3, 64, 8, 64) == CapAction.Refuse);
        Check("cap: server full but player at max breaks own", Caps.Decide(8, 64, 8, 64) == CapAction.BreakOldest);

        // ruling 13: a claim the game could not have sent
        Check("bad: kind byte out of range", SummonCheck.Decide(Claim((StoneKind)9, head), head, true, true, true, 1f) == SummonRefusal.Bad);
        Check("bad: NaN head", SummonCheck.Decide(Claim(Pillar, new V3(float.NaN, 1.7f, 5f)), head, true, true, true, 1f) == SummonRefusal.Bad);
        Check("bad: infinite yaw", SummonCheck.Decide(new SummonClaim(Pillar, head, float.PositiveInfinity), head, true, true, true, 1f) == SummonRefusal.Bad);
        Check("bad: yaw past a turn", SummonCheck.Decide(new SummonClaim(Pillar, head, 1e30f), head, true, true, true, 1f) == SummonRefusal.Bad
            && SummonCheck.Decide(new SummonClaim(Pillar, head, -179.9f), head, true, true, true, 1f) == SummonRefusal.None);
        Check("bad before kind off",SummonCheck.Decide(Claim((StoneKind)200, head), head, true, false, true, 1f) == SummonRefusal.Bad);
        Check("sane claim", SummonCheck.Sane(Claim(Pillar, head)) && !SummonCheck.Sane(Claim((StoneKind)5, head)));
        // the rest of the order: kind off, cooldown, head, ground
        Check("cooldown refused", SummonCheck.Decide(Claim(Pillar, head), head, true, true, false, 1f) == SummonRefusal.Cooldown);
        Check("kind off before cooldown", SummonCheck.Decide(Claim(Pillar, head), head, true, false, false, 1f) == SummonRefusal.KindOff);
        Check("cooldown before head", SummonCheck.Decide(Claim(Pillar, head + 3*X), head, false, true, false, 1f) == SummonRefusal.Cooldown);
        Check("head before ground", SummonCheck.Decide(Claim(Pillar, head + 3*X), head, false, true, true, 1f) == SummonRefusal.HeadFar);
        Check("head within the slack", SummonCheck.Decide(Claim(Pillar, head + 0.9f*X), head, true, true, true, 1f) == SummonRefusal.None);
        Check("server head unknown", SummonCheck.Decide(Claim(Pillar, head), new V3(float.NaN, 0f, 0f), true, true, true, 1f) == SummonRefusal.HeadFar);
        Check("move of a stone", SummonCheck.MoveOf(Pillar) == Move.Pillar && SummonCheck.MoveOf(Disc) == Move.Disc && SummonCheck.MoveOf(Wall) == Move.Wall);
        Check("stone of a move", SummonCheck.KindOf(Move.Cube, out StoneKind k) && k == Cube && !SummonCheck.KindOf(Move.Jump, out _) && !SummonCheck.KindOf(Move.Base, out _));

        // the ledger
        var forget = new Ledger();
        Check("ledger: another move is its own", forget.TrySpend(3, (int)Move.Pillar, 5.0, 0.3f) && forget.TrySpend(3, (int)Move.Cube, 5.0, 0.3f));
        forget.Forget(3);
        Check("ledger: forgotten", forget.TrySpend(3, (int)Move.Pillar, 5.1, 0.3f));
        Check("ledger: no time", !forget.TrySpend(4, (int)Move.Pillar, double.NaN, 0.3f));

        // the plan
        var plan = SpawnPlan.For(Pillar, new V3(2f, 1.7f, 3f), 0f, 0.25f);
        Check("plan: pillar 1.3 m ahead along yaw 0", Near(plan.end.X, 2f) && Near(plan.end.Z, 3f + 1.3f) && Near(plan.end.Y, 0.25f + 0.9f));
        Check("plan: rises straight up", Near(plan.start.X, plan.end.X) && Near(plan.start.Z, plan.end.Z) && Near(plan.end.Y - plan.start.Y, 1.8f));
        Check("plan: pillar rises in 0.35 s, turned to the yaw", Near(plan.riseSeconds, 0.35f) && Near(SpawnPlan.For(Wall, V3.Zero, 30f, 0f).yawDeg, 30f));
        Check("plan: wall 1.8 m ahead along yaw 180", Near(SpawnPlan.For(Wall, V3.Zero, 180f, 0f).end.Z, -1.8f) && Near(SpawnPlan.For(Wall, V3.Zero, 180f, 0f).riseSeconds, 0.45f));
        SpawnPlan.Set(Pillar, 2f, 0.5f);
        Check("plan: from the settings", Near(SpawnPlan.For(Pillar, V3.Zero, 0f, 0f).end.Z, 2f) && Near(SpawnPlan.For(Pillar, V3.Zero, 0f, 0f).riseSeconds, 0.5f));
        SpawnPlan.Set(Pillar, SpawnPlan.DefaultAhead(Pillar), SpawnPlan.DefaultRise(Pillar));
        Check("plan: defaults back", Near(SpawnPlan.For(Pillar, V3.Zero, 0f, 0f).end.Z, 1.3f));

        // life
        Check("life: why old", StoneLife.Why(61f, 60f, 0f, true) == LifeEnd.Old && StoneLife.Why(1f, 60f, -101f, true) == LifeEnd.Void && StoneLife.Why(1f, 60f, 0f, false) == LifeEnd.OwnerGone);
        Check("life: NaN place breaks", StoneLife.ShouldBreak(1f, 60f, float.NaN, true));
        Check("life: just under the void line stands", !StoneLife.ShouldBreak(1f, 60f, -99f, true));

        // caps
        Check("cap: over the max after a lower setting breaks oldest", Caps.Decide(10, 20, 8, 64) == CapAction.BreakOldest);
        Check("cap: max 0 refuses", Caps.Decide(0, 0, 0, 64) == CapAction.Refuse);
        Check("cap: total 0 refuses", Caps.Decide(0, 0, 8, 0) == CapAction.Refuse);

        // states
        Check("grounded states", StoneRules.IsGrounded(StoneState.Rising) && StoneRules.IsGrounded(StoneState.Still) && StoneRules.IsGrounded(StoneState.Sliding) && StoneRules.IsGrounded(StoneState.Stomped));
        Check("free states", !StoneRules.IsGrounded(StoneState.Free) && !StoneRules.IsGrounded(StoneState.Floating) && !StoneRules.IsGrounded(StoneState.Held) && !StoneRules.IsGrounded(StoneState.Flicked));
        Check("kinematic states", StoneRules.Kinematic(StoneState.Rising) && StoneRules.Kinematic(StoneState.Still) && !StoneRules.Kinematic(StoneState.Free) && !StoneRules.Kinematic(StoneState.Sliding));

        // [EarthStones] Enabled
        bool[] on = StoneKinds.Parse("Pillar", out string unknown);
        Check("enabled: pillar only", on.Length == StoneTable.Count && on[(int)Pillar] && !on[(int)Disc] && !on[(int)Wall] && unknown.Length == 0);
        on = StoneKinds.Parse(" pillar, CUBE;wall ", out unknown);
        Check("enabled: a list in any case", on[(int)Pillar] && on[(int)Cube] && on[(int)Wall] && !on[(int)Ball] && unknown.Length == 0);
        on = StoneKinds.Parse("Pillar, Rock", out unknown);
        Check("enabled: an unknown name", on[(int)Pillar] && unknown == "Rock");
        on = StoneKinds.Parse(null, out unknown);
        Check("enabled: none", !on[(int)Pillar] && unknown.Length == 0);

        // spec 3.8: the Applied event bytes
        Check("wire: Broke 20, Hitstop 21, Exploded 22", Wire.Broke == 20 && Wire.Hitstop == 21 && Wire.Exploded == 22);
    }
}
