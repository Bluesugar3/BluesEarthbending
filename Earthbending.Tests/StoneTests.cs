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
