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
