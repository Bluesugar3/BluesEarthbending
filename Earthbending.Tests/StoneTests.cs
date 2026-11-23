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
