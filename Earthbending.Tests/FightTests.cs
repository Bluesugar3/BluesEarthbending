using System;
using System.Collections.Generic;
using Earthbending;
using static Program;

// Fight health (docs/superpowers/specs/2026-11-23-earthbending-fight-health-design.md): the ring's zones, RUMBLE's damage
// share, the fling's speed and the ledger of each player's points.
internal static class FightTests {
    const ulong A = 101, B = 102, C = 103, X = 999;

    internal static void Run() {
        Zones();
        Parse();
        Damage();
        Fling();
        Landing();
        Ledger();
        Pips();
    }

    static FightRing Ring() => new FightRing(new V3(0f, 10f, 0f), 10f, 16f, true);

    static void Zones() {
        FightRing r = Ring();
        Check("ring: (9.9, 10, 0) is on the ring", r.Where(new V3(9.9f, 10f, 0f)) == FightWhere.Ring);
        Check("ring: (10.1, 10, 0) is in the arena", r.Where(new V3(10.1f, 10f, 0f)) == FightWhere.Arena);
        Check("ring: a Jump (7.9 m up) is still on the ring", r.Where(new V3(0f, 17.9f, 0f)) == FightWhere.Ring);
        Check("ring: 8.1 m up is in the arena", r.Where(new V3(0f, 18.1f, 0f)) == FightWhere.Arena);
        Check("ring: 1.1 m under the floor is in the arena (the pit)", r.Where(new V3(0f, 8.9f, 0f)) == FightWhere.Arena);
        Check("ring: 6.1 m under the floor is outside", r.Where(new V3(0f, 3.9f, 0f)) == FightWhere.Outside);
        Check("ring: past the arena's radius is outside", r.Where(new V3(16.1f, 10f, 0f)) == FightWhere.Outside);
        Check("ring: 25.1 m up is outside", r.Where(new V3(0f, 35.1f, 0f)) == FightWhere.Outside);
        Check("ring: the zones are flat distance (z counts)", r.Where(new V3(0f, 10f, -9.9f)) == FightWhere.Ring && r.Where(new V3(7.1f, 10f, 7.1f)) == FightWhere.Arena);
        Check("ring: valid", r.Valid);
        Check("ring: an invalid ring is outside everywhere", !new FightRing(new V3(0f, 10f, 0f), 0f, 5f, true).Valid
            && new FightRing(new V3(0f, 10f, 0f), 0f, 5f, true).Where(new V3(0f, 10f, 0f)) == FightWhere.Outside
            && new FightRing(new V3(0f, 10f, 0f), 10f, 5f, true).Where(new V3(0f, 10f, 0f)) == FightWhere.Outside
            && new FightRing(new V3(float.NaN, 10f, 0f), 10f, 16f, true).Where(new V3(0f, 10f, 0f)) == FightWhere.Outside);
        Check("ring: feet that are not a number are outside", r.Where(new V3(float.NaN, 10f, 0f)) == FightWhere.Outside);
        Check("ring: default is not valid", !default(FightRing).Valid);
    }

    static void Parse() {
        bool ok = FightRing.TryParse("-697, 128.2, 87, 10, 16", out FightRing r);
        Check("parse: the arena's text", ok && r.Valid && r.Drawn && Near(r.Centre.X, -697f) && Near(r.Centre.Y, 128.2f) && Near(r.Centre.Z, 87f)
            && Near(r.Radius, 10f) && Near(r.ArenaRadius, 16f));
        Check("parse: empty", !FightRing.TryParse("", out _));
        Check("parse: null", !FightRing.TryParse(null, out _));
        Check("parse: three numbers", !FightRing.TryParse("1,2,3", out _));
        Check("parse: letters", !FightRing.TryParse("a,b,c,d,e", out _));
        Check("parse: a ring radius of 0", !FightRing.TryParse("0,0,0,0,5", out _));
        Check("parse: an arena smaller than the ring", !FightRing.TryParse("0,0,0,10,5", out _));
        Check("parse: a semicolon", !FightRing.TryParse("0;5,0,0,10,16", out _));
        Check("parse: six numbers", !FightRing.TryParse("0,0,0,10,16,3", out _));
        Check("parse: infinity", !FightRing.TryParse("0,0,Infinity,10,16", out _));
        Check("parse: arena equal to the ring", FightRing.TryParse(" 1 ,2, 3,10,10 ", out FightRing eq) && eq.Valid);
    }

    static void Damage() {
        Check("damage: a sliding Wall (5) on 2 health takes 0.5", Near(FightRules.PlayerDamage(5, 2f, 20f), 0.5f));
        Check("damage: power 1 on 10 health is 0.5", Near(FightRules.PlayerDamage(1, 10f, 20f), 0.5f));
        Check("damage: power 0 takes nothing", FightRules.PlayerDamage(0, 10f, 20f) == 0f && FightRules.PlayerDamage(-2, 10f, 20f) == 0f);
        Check("damage: health that is not a number", FightRules.PlayerDamage(3, float.NaN, 20f) == 0f);
        Check("damage: 0 health points", FightRules.PlayerDamage(3, 10f, 0f) == 0f);
        Check("damage: bad numbers", FightRules.PlayerDamage(3, -10f, 20f) == 0f && FightRules.PlayerDamage(3, 10f, float.NaN) == 0f
            && FightRules.PlayerDamage(3, float.PositiveInfinity, 20f) == 0f && FightRules.PlayerDamage(3, 10f, float.PositiveInfinity) == 0f);
        Check("damage: 20 power on 100 health is all of it", Near(FightRules.PlayerDamage(20, 100f, 20f), 100f));
        Check("damage: the defaults", FightRules.DefaultPoints == 20 && Near(FightRules.DefaultHealthPoints, 20f) && Near(FightRules.LeftSeconds, 0.3f)
            && Near(FightRules.DefaultFlingClear, 3f) && Near(FightRules.DefaultFlingMin, 8f) && Near(FightRules.DefaultFlingMax, 16f) && Near(FightRules.DefaultFlingHitstun, 1.5f));
    }

    static readonly V3 PlusX = new V3(1f, 0f, 0f);

    static void Fling() {
        FightRing r = Ring();
        const float g = -9.81f;
        Check("fling: from the centre the edge is 10 m away", Near(FightRules.ExitDistance(r, new V3(0f, 10f, 0f), PlusX), 10f));
        Check("fling: from (9, 10, 0) out along +X, 1 m", Near(FightRules.ExitDistance(r, new V3(9f, 10f, 0f), PlusX), 1f));
        Check("fling: from (-9.9, 10, 0) along +X, 19.9 m", Math.Abs(FightRules.ExitDistance(r, new V3(-9.9f, 10f, 0f), PlusX) - 19.9f) < 1e-3f);
        Check("fling: from outside the ring, 0", FightRules.ExitDistance(r, new V3(12f, 10f, 0f), PlusX) == 0f && FightRules.ExitDistance(r, new V3(12f, 10f, 0f), new V3(-1f, 0f, 0f)) == 0f);
        Check("fling: a direction's height does not count", Near(FightRules.ExitDistance(r, new V3(0f, 10f, 0f), new V3(1f, 1f, 0f)), 10f));

        float centre = FightRules.FlingSpeed(r, new V3(0f, 10f, 0f), PlusX, g, 3f, 8f, 16f);
        Check($"fling: from the centre, sqrt(13 x 9.81) = 11.29 ({centre})", Math.Abs(centre - 11.29f) < 0.01f);
        Check("fling: near the edge, the minimum", FightRules.FlingSpeed(r, new V3(9f, 10f, 0f), PlusX, g, 3f, 8f, 16f) == 8f);
        float far = FightRules.FlingSpeed(r, new V3(-9.9f, 10f, 0f), PlusX, g, 3f, 8f, 16f);
        Check($"fling: across the ring, 15.0 ({far})", Math.Abs(far - 15.0f) < 0.05f);
        float zero = FightRules.FlingSpeed(r, new V3(5f, 10f, 0f), V3.Zero, g, 3f, 8f, 16f), alongX = FightRules.FlingSpeed(r, new V3(5f, 10f, 0f), PlusX, g, 3f, 8f, 16f);
        Check("fling: no direction is the way out through the feet", Near(zero, alongX));
        Check("fling: from the exact centre with no direction, +X", Near(FightRules.FlingSpeed(r, new V3(0f, 10f, 0f), V3.Zero, g, 3f, 8f, 16f), centre));
        Check("fling: a straight up direction is the way out through the feet", Near(FightRules.FlingSpeed(r, new V3(5f, 10f, 0f), new V3(0f, 1f, 0f), g, 3f, 8f, 16f), alongX));
        Check("fling: no gravity, the maximum", FightRules.FlingSpeed(r, new V3(0f, 10f, 0f), PlusX, 0f, 3f, 8f, 16f) == 16f);
        Check("fling: gravity that is not a number, the maximum", FightRules.FlingSpeed(r, new V3(0f, 10f, 0f), PlusX, float.NaN, 3f, 8f, 16f) == 16f);
        Check("fling: never past the maximum", FightRules.FlingSpeed(r, new V3(0f, 10f, 0f), PlusX, -100f, 3f, 8f, 16f) == 16f);
        Check("fling: positive gravity counts as its size", Near(FightRules.FlingSpeed(r, new V3(0f, 10f, 0f), PlusX, 9.81f, 3f, 8f, 16f), centre));
        float bad = FightRules.FlingSpeed(r, new V3(float.NaN, 10f, 0f), PlusX, g, 3f, 8f, 16f);
        Check($"fling: feet that are not a number still give a speed in range ({bad})", bad >= 8f && bad <= 16f);
        Check("fling: a minimum over the maximum gives the maximum", FightRules.FlingSpeed(r, new V3(9f, 10f, 0f), PlusX, g, 3f, 20f, 16f) == 16f);
    }

    // From 50 random places on the ring along 8 directions, the fling (45 degrees up, at FlingSpeed, under that gravity)
    // comes back to the floor's height outside the ring's radius, or flies at the maximum.
    static void Landing() {
        FightRing r = Ring();
        const float g = -9.81f, clear = 3f, min = 8f, max = 16f;
        var rng = new Random(23);
        int bad = 0, total = 0;
        for (int i = 0; i < 50; i++) {
            double a = rng.NextDouble() * Math.PI * 2, d = Math.Sqrt(rng.NextDouble()) * r.Radius * 0.999;
            var feet = new V3((float)(Math.Cos(a) * d), r.Centre.Y, (float)(Math.Sin(a) * d));
            for (int k = 0; k < 8; k++) {
                double b = k * Math.PI / 4;
                var dir = new V3((float)Math.Cos(b), 0f, (float)Math.Sin(b));
                float s = FightRules.FlingSpeed(r, feet, dir, g, clear, min, max);
                total++;
                if (s == max) continue;
                // stepped as the game does, 90 frames a second
                float vx = s * (float)Math.Sqrt(0.5), vy = vx, x = feet.X, y = feet.Y, z = feet.Z;
                const float dt = 1f / 90f;
                for (int step = 0; step < 100000; step++) {
                    x += dir.X * vx * dt;
                    z += dir.Z * vx * dt;
                    y += vy * dt;
                    vy += g * dt;
                    if (y < r.Centre.Y && vy < 0f) break;
                }
                float flat = (float)Math.Sqrt(x * x + z * z);
                if (!(flat > r.Radius)) bad++;
            }
        }
        Check($"fling: every fling lands outside the ring ({bad} of {total} did not)", bad == 0);
    }

    static bool Has(List<FightLedger.Change> c, ulong player, int points) {
        foreach (FightLedger.Change x in c) if (x.Player == player && x.Points == points) return true;
        return false;
    }

    static int Count(List<FightLedger.Change> c, ulong player) {
        int n = 0;
        foreach (FightLedger.Change x in c) if (x.Player == player) n++;
        return n;
    }

    static void Ledger() {
        var c = new List<FightLedger.Change>();
        var l = new FightLedger(20);

        l.See(A, FightWhere.Ring, 0.0, c);
        Check("ledger: on the ring, 20", l.PointsOf(A) == 20 && l.Fighting(A) && c.Count == 1 && Has(c, A, 20));
        c.Clear();
        l.See(A, FightWhere.Ring, 0.1, c);
        Check("ledger: seen again, no change", c.Count == 0 && l.PointsOf(A) == 20);

        c.Clear();
        Check("ledger: a hit of 3 hurts", l.Hit(A, 3, c) == FightHit.Hurt && l.PointsOf(A) == 17 && c.Count == 1 && Has(c, A, 17));
        c.Clear();
        Check("ledger: a hit of 0 takes nothing", l.Hit(A, 0, c) == FightHit.NotFighting && c.Count == 0 && l.PointsOf(A) == 17);
        Check("ledger: a hit below 0 takes nothing", l.Hit(A, -4, c) == FightHit.NotFighting && c.Count == 0 && l.PointsOf(A) == 17);

        l.See(B, FightWhere.Ring, 0.2, c);
        c.Clear();
        for (int i = 0; i < 3; i++) l.Hit(B, 5, c);
        Check("ledger: three hits of 5 leave 5", l.PointsOf(B) == 5);
        c.Clear();
        Check("ledger: the fourth knocks out", l.Hit(B, 5, c) == FightHit.KnockedOut);
        Check("ledger: the knocked out player has none, the other refills", l.PointsOf(B) == -1 && !l.Fighting(B) && l.PointsOf(A) == 20
            && Has(c, B, -1) && Has(c, A, 20) && c.Count == 2);
        c.Clear();
        Check("ledger: out, a later hit does nothing", l.Hit(B, 5, c) == FightHit.NotFighting && c.Count == 0);
        l.See(B, FightWhere.Ring, 1.0, c);
        Check("ledger: out and still on the ring, nothing (must leave first)", l.PointsOf(B) == -1 && c.Count == 0);
        l.See(B, FightWhere.Arena, 2.0, c);
        l.See(B, FightWhere.Arena, 2.29, c);
        l.See(B, FightWhere.Ring, 2.295, c);   // back on before 0.3 s off: still out
        Check("ledger: 0.29 s off is not left", l.PointsOf(B) == -1 && c.Count == 0);
        l.See(B, FightWhere.Arena, 3.0, c);
        l.See(B, FightWhere.Outside, 3.31, c);
        Check("ledger: off for 0.31 s, still none while off", l.PointsOf(B) == -1 && c.Count == 0);
        l.See(B, FightWhere.Ring, 3.4, c);
        Check("ledger: then back on, 20", l.PointsOf(B) == 20 && Has(c, B, 20) && c.Count == 1);

        // a brief step off
        c.Clear();
        l.Hit(A, 2, c);
        c.Clear();
        l.See(A, FightWhere.Arena, 4.0, c);
        l.See(A, FightWhere.Ring, 4.2, c);
        l.See(A, FightWhere.Arena, 4.3, c);
        l.See(A, FightWhere.Arena, 4.55, c);   // 0.25 s since this step off began: the first one does not count
        Check("ledger: a brief step off keeps the points, nobody refills", l.PointsOf(A) == 18 && c.Count == 0);
        l.See(A, FightWhere.Ring, 4.6, c);

        // walking off: A at 12 and C at 9
        var w = new FightLedger(20);
        w.See(A, FightWhere.Ring, 0.0, c);
        w.See(C, FightWhere.Ring, 0.0, c);
        w.Hit(A, 8, c);
        w.Hit(C, 11, c);
        Check("ledger: A at 12, C at 9", w.PointsOf(A) == 12 && w.PointsOf(C) == 9);
        c.Clear();
        w.See(A, FightWhere.Arena, 1.0, c);
        Check("ledger: just off, still in", w.PointsOf(A) == 12 && c.Count == 0);
        w.See(A, FightWhere.Arena, 1.31, c);
        Check("ledger: walking off ends the fight: A has none, C refills", w.PointsOf(A) == -1 && w.PointsOf(C) == 20 && Has(c, A, -1) && Has(c, C, 20) && c.Count == 2);
        c.Clear();
        w.See(A, FightWhere.Ring, 1.4, c);
        Check("ledger: walking back on gives 20 (not out: it left)", w.PointsOf(A) == 20 && Has(c, A, 20));

        c.Clear();
        Check("ledger: a player never seen", w.Hit(X, 3, c) == FightHit.NotFighting && c.Count == 0 && w.PointsOf(X) == -1 && !w.Fighting(X));
        w.Hit(A, 4, c);
        w.See(B, FightWhere.Ring, 2.0, c);
        w.Hit(B, 1, c);
        c.Clear();
        w.Forget(C, c);
        Check("ledger: forgetting a fighter refills the others", w.PointsOf(C) == -1 && Has(c, C, -1) && Has(c, A, 20) && Has(c, B, 20) && c.Count == 3);
        c.Clear();
        w.Forget(C, c);
        Check("ledger: forgetting someone with nothing changes nothing", c.Count == 0);

        // Clear: every entry -1, nobody out
        w.Hit(A, 20, c);   // A is out
        Check("ledger: A knocked out", w.PointsOf(A) == -1);
        c.Clear();
        w.Clear(c);
        Check("ledger: clear: every entry -1", w.PointsOf(B) == -1 && Has(c, B, -1) && c.Count == 1);
        c.Clear();
        w.See(A, FightWhere.Ring, 3.0, c);
        Check("ledger: after a clear nobody is out", w.PointsOf(A) == 20 && Has(c, A, 20));

        // two stones in one step at 1 point
        var t = new FightLedger(20);
        t.See(A, FightWhere.Ring, 0.0, c);
        t.Hit(A, 19, c);
        c.Clear();
        Check("ledger: two hits in one step: one knock-out", t.Hit(A, 1, c) == FightHit.KnockedOut && t.Hit(A, 3, c) == FightHit.NotFighting && Count(c, A) == 1);

        // one change per player per call
        var u = new FightLedger(20);
        u.See(A, FightWhere.Ring, 0.0, c);
        c.Clear();
        u.Forget(A, c);
        u.See(A, FightWhere.Ring, 0.0, c);
        Check("ledger: forgotten, then seen, 20 again", u.PointsOf(A) == 20);

        Check("ledger: points held to 1..250", new FightLedger(300).Points == 250 && new FightLedger(0).Points == 1 && new FightLedger(-5).Points == 1 && new FightLedger(20).Points == 20);
        var big = new FightLedger(300);
        big.See(A, FightWhere.Ring, 0.0, c);
        Check("ledger: 250 points", big.PointsOf(A) == 250);
        var small = new FightLedger(0);
        small.See(A, FightWhere.Ring, 0.0, c);
        c.Clear();
        Check("ledger: 1 point: any hit knocks out", small.Hit(A, 1, c) == FightHit.KnockedOut);
        Check("ledger: a huge hit knocks out", new Func<bool>(() => {
            var h = new FightLedger(20);
            var cc = new List<FightLedger.Change>();
            h.See(A, FightWhere.Ring, 0.0, cc);
            return h.Hit(A, int.MaxValue, cc) == FightHit.KnockedOut && h.PointsOf(A) == -1;
        })());
        Check("ledger: the fighters", new Func<bool>(() => {
            var h = new FightLedger(20);
            var cc = new List<FightLedger.Change>();
            h.See(A, FightWhere.Ring, 0.0, cc);
            h.See(B, FightWhere.Arena, 0.0, cc);
            var f = new List<FightLedger.Change>();
            h.Fighters(f);
            return f.Count == 1 && f[0].Player == A && f[0].Points == 20 && !h.Fighting(B);
        })());
    }

    // What players see (spec 3.7): where each small stone of a fighter's row sits over the head, and on the forearm.
    static void Pips() {
        const float size = 0.035f, gap = 0.012f, groupGap = 0.03f, pitch = size + gap;
        // every place mirrors its opposite in a row of whole groups (and in any row of up to one group)
        foreach (int n in new[] { 1, 2, 3, 4, 5, 10, 15, 20 }) {
            bool mirrored = true;
            for (int i = 0; i < n; i++) mirrored &= Near(FightPips.RowOffset(i, n, size, gap, groupGap), -FightPips.RowOffset(n - 1 - i, n, size, gap, groupGap));
            Check($"pips: a row of {n} mirrors itself", mirrored);
        }
        // The plan asked the same of a row of 7. With groups of five counted from one end no layout can give it: the one
        // boundary of 7 lies between stones 4 and 5 and its mirror between 1 and 2 (2 + 4 pitches against 6 pitches and a
        // group gap). What holds for every n: the row's two ends are as far from the middle, so the row is centred.
        foreach (int n in new[] { 6, 7, 8, 9, 13, 19, 20 })
            Check($"pips: a row of {n} is centred (its ends mirror)", Near(FightPips.RowOffset(0, n, size, gap, groupGap), -FightPips.RowOffset(n - 1, n, size, gap, groupGap)));
        Check("pips: a row of 7 keeps its five together (it cannot mirror place by place)",
            !Near(FightPips.RowOffset(2, 7, size, gap, groupGap), -FightPips.RowOffset(4, 7, size, gap, groupGap)));
        Check("pips: one stone sits in the middle", FightPips.RowOffset(0, 1, size, gap, groupGap) == 0f);
        bool steps = true;
        foreach (int n in new[] { 7, 20 })
            for (int i = 0; i + 1 < n; i++) {
                float step = FightPips.RowOffset(i + 1, n, size, gap, groupGap) - FightPips.RowOffset(i, n, size, gap, groupGap);
                steps &= Near(step, (i + 1) % FightPips.Group == 0 ? pitch + groupGap : pitch);
            }
        Check("pips: neighbours are size + gap apart, and a group gap more across a group of five", steps);
        float width = FightPips.RowOffset(19, 20, size, gap, groupGap) - FightPips.RowOffset(0, 20, size, gap, groupGap) + size;
        Check($"pips: a row of 20 is under 1.1 m wide ({width})", width < 1.1f && width > 0.9f);
        Check("pips: the first stone is at the low end", FightPips.RowOffset(0, 20, size, gap, groupGap) < 0f && FightPips.RowOffset(19, 20, size, gap, groupGap) > 0f);
        Check("pips: no stones, or a place that is not in the row, is 0", FightPips.RowOffset(0, 0, size, gap, groupGap) == 0f && FightPips.RowOffset(0, -3, size, gap, groupGap) == 0f
            && FightPips.RowOffset(-1, 5, size, gap, groupGap) == 0f && FightPips.RowOffset(5, 5, size, gap, groupGap) == 0f);
        Check("pips: the constants", FightPips.MaxShown == 40 && FightPips.Group == 5 && FightPips.PerLine == 20 && FightPips.PerLine % FightPips.Group == 0);

        // more than a line's worth goes on in a second line above the first
        Check("pips: 20 stones are one line", FightPips.Lines(20) == 1 && FightPips.Lines(1) == 1 && FightPips.Lines(0) == 0 && FightPips.Lines(-2) == 0);
        Check("pips: 21 to 40 are two lines", FightPips.Lines(21) == 2 && FightPips.Lines(40) == 2);
        Check("pips: stone 19 is on line 0, stone 20 on line 1", FightPips.LineOf(19) == 0 && FightPips.LineOf(20) == 1 && FightPips.LineOf(39) == 1 && FightPips.LineOf(-1) == 0);
        Check("pips: the first line of 27 holds 20, the second 7", FightPips.InLine(0, 27) == 20 && FightPips.InLine(1, 27) == 7 && FightPips.InLine(2, 27) == 0
            && FightPips.InLine(0, 12) == 12 && FightPips.InLine(1, 12) == 0 && FightPips.InLine(-1, 12) == 0 && FightPips.InLine(0, 0) == 0);

        // round the forearm
        Check("band: the first place is on top", FightPips.BandAngle(0, 20) == 0f);
        bool even = true;
        for (int i = 0; i < 20; i++) even &= Near(FightPips.BandAngle(i, 20), (float)(i * 2.0 * Math.PI / 20));
        Check("band: 20 places go round in steps of 2 pi / 20", even);
        Check("band: half-way round is pi", Near(FightPips.BandAngle(7, 14), (float)Math.PI));
        Check("band: no places, or a place that is not one, is 0", FightPips.BandAngle(0, 0) == 0f && FightPips.BandAngle(3, 0) == 0f && FightPips.BandAngle(3, -4) == 0f
            && FightPips.BandAngle(-1, 20) == 0f && FightPips.BandAngle(20, 20) == 0f);
        // a row of five across the top: the middle first, then out to either side in turn
        Check("band: a row fills from the top outward (0, 1, 13, 2, 12 of 14 places)", FightPips.BandPlace(0, 14) == 0 && FightPips.BandPlace(1, 14) == 1
            && FightPips.BandPlace(2, 14) == 13 && FightPips.BandPlace(3, 14) == 2 && FightPips.BandPlace(4, 14) == 12);
        bool distinct = true, near = true;
        for (int n = FightPips.Group; n <= 30; n++) {
            var seen = new HashSet<int>();
            for (int j = 0; j < FightPips.Group; j++) {
                int place = FightPips.BandPlace(j, n);
                distinct &= place >= 0 && place < n && seen.Add(place);
                near &= place <= 2 || place >= n - 2;   // never further round than two places from the top, on either side
            }
        }
        Check("band: the five places of a row are five different ones", distinct);
        Check("band: a row stays within two places of the top", near);
        Check("band: too few places, or a stone that is not one, is place 0", FightPips.BandPlace(1, 0) == 0 && FightPips.BandPlace(2, -1) == 0 && FightPips.BandPlace(-1, 14) == 0
            && FightPips.BandPlace(3, 1) == 0);
        Check("band: places round a forearm of 0.07 m for stones 0.0295 m apart: 14", FightPips.BandPlaces(0.07f, 0.0295f) == 14);
        Check("band: never fewer places than a row of five needs", FightPips.BandPlaces(0.01f, 0.05f) == FightPips.Group && FightPips.BandPlaces(0f, 0.03f) == FightPips.Group
            && FightPips.BandPlaces(0.07f, 0f) == FightPips.Group && FightPips.BandPlaces(float.NaN, 0.03f) == FightPips.Group && FightPips.BandPlaces(0.07f, float.NaN) == FightPips.Group);
        Check("band: never more places than 360", FightPips.BandPlaces(10f, 0.001f) == 360);

        // far away the row grows, so its stones never look smaller than they do at the far distance
        Check("far: within the far distance a row is its own size", FightPips.FarScale(3f, 6f, 2.5f) == 1f && FightPips.FarScale(6f, 6f, 2.5f) == 1f && FightPips.FarScale(0f, 6f, 2.5f) == 1f);
        Check("far: twice the far distance, twice the size", Near(FightPips.FarScale(12f, 6f, 2.5f), 2f));
        Check("far: never past the most", FightPips.FarScale(15f, 6f, 2.5f) == 2.5f && FightPips.FarScale(500f, 6f, 2.5f) == 2.5f && FightPips.FarScale(float.PositiveInfinity, 6f, 2.5f) == 2.5f);
        Check("far: no far distance, or a most of 1 or less, never grows", FightPips.FarScale(50f, 0f, 2.5f) == 1f && FightPips.FarScale(50f, 6f, 1f) == 1f && FightPips.FarScale(50f, 6f, 0.5f) == 1f
            && FightPips.FarScale(50f, -1f, 2.5f) == 1f);
        Check("far: numbers that are not numbers never grow", FightPips.FarScale(float.NaN, 6f, 2.5f) == 1f && FightPips.FarScale(50f, float.NaN, 2.5f) == 1f && FightPips.FarScale(50f, 6f, float.NaN) == 1f);
    }
}
