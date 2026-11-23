using System;
using System.Globalization;
using Earthbending;
using static Program;

// Task 17b (Ruling 38): the Model's smooth, predicted place between the game's syncs of a body (Motion.cs). The server sends
// a body's place every second frame of its own, so the gap depends on the server's frame rate: the moving cases run at three
// cadences, a slow server (a sync every 0.1 s, the hard case), a fast one (every 0.022 s, 45 a second) and an irregular one
// (gaps alternating 0.02 and 0.07 s). Every case is a body whose true place is known at every moment; the "game" sees it only
// as its syncs, each stamped with the time of the first frame at or after the moment it was taken (as NetworkRigidbody's
// lastSyncTime is), and draws at 90 frames a second. Each case prints what it measured (the report quotes these lines).
internal static class MotionTests {
    const float Frame = 1f / 90f, SyncEvery = 0.1f, MinSpeed = 2f;   // SyncEvery: a slow server, for the spin and the rise
    static readonly float[] SlowServer = { 0.1f }, FastServer = { 0.022f }, Irregular = { 0.02f, 0.07f };

    delegate void Truth(float t, out V3 place, out V3 velocity);

    internal static void Run() {
        Values();
        Damping();
        Turns();

        Moving("slow server, every 0.1 s", SlowServer);
        Moving("fast server, every 0.022 s", FastServer);
        Moving("irregular, 0.02 / 0.07 s", Irregular);

        KinematicRise();
        Stops();
        Silence();
        Snaps();
        BadSteps();
        StartsOnBody();
    }

    // the four moving bodies at one cadence (gaps between syncs, used in turn)
    static void Moving(string cadence, float[] gaps) {
        // a body moving at a steady 10 m/s
        Case(cadence, gaps, "steady 10 m/s", (float t, out V3 p, out V3 v) => { v = new V3(10f, 0f, 0f); p = new V3(10f * t, 1f, 0f); },
            kinematic: false, from: 0.3f, to: 2f, maxError: 0.15f, stepFrom: 0.3f, stepTo: 2f, maxStepRatio: 0.3f);
        // a slide that slows 4 m/s every second from 8 m/s (stops at 2 s). For a body whose speed changes the "mean step" is the
        // true step that frame, judged only while it moves 2 m/s or more (a step near a stop is a few millimetres)
        Case(cadence, gaps, "slide 8 m/s slowing 4 m/s/s", (float t, out V3 p, out V3 v) => {
                float tt = Math.Min(t, 2f);
                v = new V3(8f - 4f * tt, 0f, 0f);
                p = new V3(8f * tt - 2f * tt * tt, 0.5f, 0f);
            },
            kinematic: false, from: 0.3f, to: 2.5f, maxError: 0.2f, stepFrom: 0.3f, stepTo: 2.5f, maxStepRatio: 0.3f);
        // a body thrown up at 5 m/s under gravity 9.81 (the game's target has no gravity: live + velocity x age)
        Case(cadence, gaps, "thrown up 5 m/s", (float t, out V3 p, out V3 v) => {
                v = new V3(0f, 5f - 9.81f * t, 0f);
                p = new V3(0f, 5f * t - 0.5f * 9.81f * t * t, 0f);
            },
            kinematic: false, from: 0.3f, to: 1f, maxError: 0.2f, stepFrom: 0.3f, stepTo: 1f, maxStepRatio: 0.3f);
        // the same thrown up at 5 m/s while sliding sideways at 6 m/s: the step judged against the true step
        Case(cadence, gaps, "thrown 5 m/s up, 6 m/s across", (float t, out V3 p, out V3 v) => {
                v = new V3(6f, 5f - 9.81f * t, 0f);
                p = new V3(6f * t, 5f * t - 0.5f * 9.81f * t * t, 0f);
            },
            kinematic: false, from: 0.3f, to: 1f, maxError: 0.2f, stepFrom: 0.3f, stepTo: 1f, maxStepRatio: 0.3f);
    }

    static string F(float x) => x.ToString("0.0000", CultureInfo.InvariantCulture);

    static void Values() {
        var d = MotionValues.Default;
        Check("motion: defaults 0.25 s, 0.05 s, 3 m", Near(d.PredictMaxSeconds, 0.25f) && Near(d.SmoothSeconds, 0.05f) && Near(d.SnapMetres, 3f));
        var bad = new MotionValues(float.NaN, -1f, float.PositiveInfinity).Clamped();
        Check("motion: a NaN, negative or endless value is the default", Near(bad.PredictMaxSeconds, 0.25f) && Near(bad.SmoothSeconds, 0.05f) && Near(bad.SnapMetres, 3f));
        var zero = new MotionValues(0f, 0f, 0f).Clamped();
        Check("motion: 0 stays 0", zero.PredictMaxSeconds == 0f && zero.SmoothSeconds == 0f && zero.SnapMetres == 0f);

        // the target from a sync
        var s = new MotionSync(new V3(1f, 2f, 3f), Q.Identity, new V3(4f, 0f, 0f), V3.Zero, 10f, false);
        Check("target: live + velocity x age", NearV(Motion.Target(s, 10.1f, 0.25f), new V3(1.4f, 2f, 3f)));
        Check("target: the age counts in full up to PredictMaxSeconds", NearV(Motion.Target(s, 10.25f, 0.25f), new V3(2f, 2f, 3f)));
        Check("target: past PredictMaxSeconds the prediction fades back to the synced place", NearV(Motion.Target(s, 11f, 0.25f), new V3(1f, 2f, 3f))
            && NearV(Motion.Target(s, 10.5f, 0.25f), new V3(1f, 2f, 3f)) && Motion.Target(s, 10.4f, 0.25f).X > 1f && Motion.Target(s, 10.4f, 0.25f).X < 2f);
        float most = 0f;
        for (int i = 0; i <= 1000; i++) most = Math.Max(most, Motion.PredictedAge(i * 0.001f, 0.25f));
        Check("target: the fade coasts on at most 6% of PredictMaxSeconds", most <= 0.25f * 1.06f && most >= 0.25f);
        Check("target: the free distance holds the predicted length", NearV(Motion.Target(s, 10.2f, 0.25f, 0.3f), new V3(1.3f, 2f, 3f))
            && NearV(Motion.Target(s, 10.2f, 0.25f, 0f), new V3(1f, 2f, 3f)) && NearV(Motion.Target(s, 10.05f, 0.25f, 0.3f), new V3(1.2f, 2f, 3f))
            && NearV(Motion.Target(s, 10.2f, 0.25f, float.NaN), new V3(1.8f, 2f, 3f)));
        var stopped = new MotionSync(new V3(1f, 2f, 3f), Q.Identity, V3.Zero, V3.Zero, 10.1f, false);
        var back = new MotionSync(new V3(1f, 2f, 3f), Q.Identity, new V3(-2f, 0f, 0f), V3.Zero, 10.1f, false);
        var slower = new MotionSync(new V3(1f, 2f, 3f), Q.Identity, new V3(3f, 0f, 0f), V3.Zero, 10.1f, false);
        var frozen = new MotionSync(new V3(1f, 2f, 3f), Q.Identity, V3.Zero, V3.Zero, 10.1f, true);
        Check("against: stopped, going back and frozen are against the motion, slowing is not", Motion.Against(s, stopped) && Motion.Against(s, back)
            && Motion.Against(s, frozen) && !Motion.Against(s, slower) && !Motion.Against(stopped, s) && !Motion.Against(frozen, s));
        Check("target: an age below 0 is 0", NearV(Motion.Target(s, 9f, 0.25f), new V3(1f, 2f, 3f)));
        var k = new MotionSync(new V3(1f, 2f, 3f), Q.Identity, new V3(4f, 0f, 0f), V3.Zero, 10f, true);
        Check("target: kinematic is live, no prediction", NearV(Motion.Target(k, 10.2f, 0.25f), new V3(1f, 2f, 3f)));
    }

    static void Damping() {
        // critically damped: from rest an offset falls as (1 + t/T) e^(-t/T), never crossing 0
        V3 x = new V3(1f, 0f, 0f), r = V3.Zero;
        bool crossed = false;
        for (int i = 0; i < 90; i++) { Motion.Damp(ref x, ref r, Frame, 0.05f); if (x.X < 0f) crossed = true; }
        float want = (float)((1.0 + 1.0 / 0.05) * Math.Exp(-1.0 / 0.05));
        Check("damp: one second of T 0.05 s is (1 + 20) e^-20", Math.Abs(x.X - want) < 1e-5f && !crossed);
        V3 y = new V3(1f, 0f, 0f), q = V3.Zero;
        Motion.Damp(ref y, ref q, 0.05f, 0.05f);
        Check("damp: one T is 2/e", Math.Abs(y.X - (float)(2.0 / Math.E)) < 1e-4f);
        V3 z = new V3(1f, 1f, 1f), zr = new V3(3f, 3f, 3f);
        Motion.Damp(ref z, ref zr, Frame, 0f);
        Check("damp: T 0 is no easing", NearV(z, V3.Zero) && NearV(zr, V3.Zero));
        V3 big = new V3(1f, 0f, 0f), br = V3.Zero;
        Motion.Damp(ref big, ref br, 1000f, 0.05f);
        Check("damp: a huge step is stable", Math.Abs(big.X) < 1e-6f && !float.IsNaN(big.X) && !float.IsNaN(br.X));
    }

    static void Turns() {
        Q a = Q.FromRotVec(new V3(0f, 0.5f, 0f));
        Check("q: a turn vector there and back", NearV(a.ToRotVec(), new V3(0f, 0.5f, 0f)));
        Check("q: a turn times its inverse is none", Q.Angle(a * a.Inverse, Q.Identity) < 1e-4f);
        Check("q: the short way round", NearV(Q.FromRotVec(new V3(0f, 4f, 0f)).ToRotVec(), new V3(0f, 4f - 2f * (float)Math.PI, 0f)));
        Q b = Q.FromRotVec(new V3(0.3f, 0f, 0f));
        Check("q: angle between two turns", Math.Abs(Q.Angle(a, b) - Q.Angle(b, a)) < 1e-5f && Q.Angle(a, a) < 1e-4f);
        // y spin of 0.5 rad then x of 0.3: Unity's order, the left turns last (world axes)
        V3 p = (b * a).Rotate(new V3(0f, 0f, 1f));
        V3 want = b.Rotate(a.Rotate(new V3(0f, 0f, 1f)));
        Check("q: (b * a) turns by a then b", NearV(p, want));
        Check("q: rotate 90 about y takes z to x", NearV(Q.FromRotVec(new V3(0f, (float)Math.PI / 2f, 0f)).Rotate(new V3(0f, 0f, 1f)), new V3(1f, 0f, 0f)));

        // a body spinning at 3 rad/s about y, synced every 0.1 s (a slow server): the shown turn within 2 degrees from 0.3 s on
        var f = new MotionFollow();
        var m = MotionValues.Default;
        float worst = 0f;
        V3 w = new V3(0f, 3f, 0f);
        MotionSync sync = default;
        float nextSync = 0.005f;
        bool have = false;
        for (int i = 0; i <= 180; i++) {
            float now = i * Frame;
            if (now >= nextSync) {
                Q truthAtSync = Q.FromRotVec(w * nextSync);
                sync = new MotionSync(V3.Zero, truthAtSync, V3.Zero, w, now, false);
                have = true;
                nextSync += SyncEvery;
            }
            if (!have) continue;
            f.Step(sync, now, Frame, V3.Zero, Q.FromRotVec(w * now), m);
            if (now >= 0.3f) worst = Math.Max(worst, Q.Angle(f.ShownTurn, Q.FromRotVec(w * now)));
        }
        float deg = worst * 180f / (float)Math.PI;
        Console.WriteLine($"MOTION spin 3 rad/s: worst turn error {F(deg)} degrees");
        Check("motion: a steady spin is followed within 2 degrees", deg < 2f);
    }

    static bool NearV(V3 a, V3 b) => Near(a.X, b.X) && Near(a.Y, b.Y) && Near(a.Z, b.Z);

    // One body: the worst distance from the true place in [from, to], the worst |shown step - true step| / true step in
    // [stepFrom, stepTo] while the body moves at least MinSpeed.
    static void Case(string cadence, float[] gaps, string name, Truth truth, bool kinematic, float from, float to, float maxError, float stepFrom, float stepTo, float maxStepRatio) {
        var f = new MotionFollow();
        var m = MotionValues.Default;
        float worstError = 0f, worstRatio = 0f;
        MotionSync sync = default;
        float nextSync = 0.005f;   // half a frame off the frames' clock, so float sums never flip which frame stamps a sync
        int gap = 0;
        bool have = false;
        V3 prevShown = V3.Zero, prevTrue = V3.Zero;
        bool havePrev = false;
        int frames = (int)Math.Ceiling(to / Frame);
        for (int i = 0; i <= frames; i++) {
            float now = i * Frame;
            truth(now, out V3 truePlace, out V3 trueVelocity);
            if (now >= nextSync) {
                truth(nextSync, out V3 at, out V3 vel);
                sync = new MotionSync(at, Q.Identity, kinematic ? V3.Zero : vel, V3.Zero, now, kinematic);
                have = true;
                nextSync += gaps[gap++ % gaps.Length];
            }
            V3 shown;
            if (!have) { f.Reset(); shown = truePlace; }   // before the first sync: on the rigidbody (here, the true place)
            else {
                f.Step(sync, now, Frame, truePlace, Q.Identity, m);
                shown = f.Shown;
            }
            if (now >= from && now <= to) worstError = Math.Max(worstError, (shown - truePlace).Length);
            if (havePrev && now >= stepFrom && now <= stepTo && trueVelocity.Length >= MinSpeed) {
                float trueStep = (truePlace - prevTrue).Length, shownStep = (shown - prevShown).Length;
                if (trueStep > 1e-6f) worstRatio = Math.Max(worstRatio, Math.Abs(shownStep - trueStep) / trueStep);
            }
            prevShown = shown; prevTrue = truePlace; havePrev = true;
        }
        name = $"{name} ({cadence})";
        Console.WriteLine($"MOTION {name}: worst error {F(worstError)} m, worst step ratio {F(worstRatio)}");
        Check($"motion: {name} within {maxError} m of the true place", worstError <= maxError);
        Check($"motion: {name} steps within {maxStepRatio:0%} of the true step", worstRatio <= maxStepRatio);
    }

    // a kinematic rise of 1.8 m in 0.3 s (6 m/s; a kinematic sync carries no velocity), synced at a fast server's gap and a
    // slow one's, the stone standing at the top after it (its syncs go on)
    static void KinematicRise() {
        KinematicRise(0.022f, maxLag: 0.25f, maxLate: 0.05f);
        KinematicRise(0.1f, maxLag: float.MaxValue, maxLate: float.MaxValue);
    }

    static void KinematicRise(float gap, float maxLag, float maxLate) {
        var f = new MotionFollow();
        var m = MotionValues.Default;
        const float top = 1.8f, rise = 0.3f;
        float over = 0f, lag = 0f, outAt = 0f, atEnd = float.MaxValue;
        float nextSync = 0.005f;
        MotionSync sync = default;
        bool have = false;
        for (int i = 0; i <= 90; i++) {
            float now = i * Frame;
            float trueY = Math.Min(now / rise, 1f) * top;
            if (now >= nextSync) {
                sync = new MotionSync(new V3(0f, Math.Min(nextSync / rise, 1f) * top, 0f), Q.Identity, V3.Zero, V3.Zero, now, true);
                have = true;
                nextSync += gap;
            }
            if (!have) continue;
            f.Step(sync, now, Frame, new V3(0f, trueY, 0f), Q.Identity, m);
            over = Math.Max(over, f.Shown.Y - top);
            lag = Math.Max(lag, trueY - f.Shown.Y);
            if (Math.Abs(f.Shown.Y - top) > 0.05f) outAt = now + Frame;   // the model is within 5 cm of the top from outAt on
            if (now >= rise + 0.3f && atEnd == float.MaxValue) atEnd = Math.Abs(f.Shown.Y - top);
        }
        float late = outAt - rise;
        Console.WriteLine($"MOTION kinematic rise 1.8 m in 0.3 s (every {gap} s): worst lag {F(lag)} m, at the top {F(late)} s after the true stone, worst overshoot {F(Math.Max(over, 0f))} m, off the top 0.3 s after it {F(atEnd)} m");
        Check($"motion: a kinematic rise (every {gap} s) never passes the top by more than 0.05 m", over <= 0.05f);
        Check($"motion: a kinematic rise (every {gap} s) is within 0.05 m of the top 0.3 s after the true one", atEnd <= 0.05f);
        if (maxLag < float.MaxValue) Check($"motion: a kinematic rise (every {gap} s) lags at most {maxLag} m", lag <= maxLag);
        if (maxLate < float.MaxValue) Check($"motion: a kinematic rise (every {gap} s) is at the top within {maxLate} s of the true one", late <= maxLate);
    }

    enum StopKind { Stop, Reverse, Freeze }

    // I-1: 8 m/s into a wall at ts, then stopped (Stop), going back at 4 m/s (Reverse) or frozen by a clash (Freeze: a
    // kinematic sync, velocity 0), at three gaps and ten phases of ts against the syncs. With the free distance given (the
    // game's sweep finds the wall) the model never goes more than 5 cm past the wall; without it (no wall known, as a hitstop
    // in open air) it overshoots and is back within 5 cm of the true place soon after.
    static void Stops() {
        foreach (float gap in new[] { 0.022f, 0.1f, 0.15f })
            foreach (StopKind kind in new[] { StopKind.Stop, StopKind.Reverse, StopKind.Freeze }) {
                float pastWall = 0f, past = 0f, back = 0f;
                for (int k = 0; k < 10; k++) {
                    float ts = 0.4f + gap * k / 10f;
                    StopRun(kind, gap, ts, true, out float pw, out _);
                    StopRun(kind, gap, ts, false, out float p, out float b);
                    pastWall = Math.Max(pastWall, pw);
                    past = Math.Max(past, p);
                    back = Math.Max(back, b);
                }
                float limit = gap < 0.05f ? 0.15f : gap < 0.12f ? 0.25f : float.MaxValue;
                string name = $"{kind} at 8 m/s (every {gap} s)";
                Console.WriteLine($"MOTION {name}: past the wall {F(pastWall)} m with the free distance; without it {F(past)} m past, back within 5 cm {F(back)} s after the stop");
                Check($"motion: {name} with the free distance is never more than 0.05 m past the wall", pastWall <= 0.05f);
                if (limit < float.MaxValue) Check($"motion: {name} without it is back within 0.05 m in under {limit} s", back < limit);
            }
    }

    static void StopRun(StopKind kind, float gap, float ts, bool withFree, out float past, out float back) {
        var f = new MotionFollow();
        var m = MotionValues.Default;
        float wall = 8f * ts;
        past = 0f;
        float outAt = ts;
        float nextSync = 0.005f;
        MotionSync sync = default;
        bool have = false;
        float free = float.PositiveInfinity;
        for (int i = 0; i * Frame <= ts + 1f; i++) {
            float now = i * Frame;
            StopTruth(kind, ts, now, out float x, out _);
            if (now >= nextSync) {
                StopTruth(kind, ts, nextSync, out float sx, out float sv);
                bool kin = kind == StopKind.Freeze && nextSync >= ts;
                sync = new MotionSync(new V3(sx, 0f, 0f), Q.Identity, new V3(kin ? 0f : sv, 0f, 0f), V3.Zero, now, kin);
                free = withFree && !kin && sv > 0f ? Math.Max(0f, wall - sx) : float.PositiveInfinity;   // the sweep along the velocity
                have = true;
                nextSync += gap;
            }
            if (!have) continue;
            f.Step(sync, now, Frame, new V3(x, 0f, 0f), Q.Identity, m, free);
            past = Math.Max(past, f.Shown.X - wall);
            if (now >= ts && Math.Abs(f.Shown.X - x) > 0.05f) outAt = now + Frame;
        }
        back = outAt - ts;
    }

    static void StopTruth(StopKind kind, float ts, float t, out float x, out float v) {
        if (t < ts) { x = 8f * t; v = 8f; return; }
        if (kind == StopKind.Reverse) { x = 8f * ts - 4f * (t - ts); v = -4f; return; }
        x = 8f * ts; v = 0f;
    }

    // M-1: the syncs stop while the last one carried 8 m/s: the model comes back to the synced place, smoothly
    static void Silence() {
        var f = new MotionFollow();
        var m = MotionValues.Default;
        var s = new MotionSync(new V3(0f, 0f, 0f), Q.Identity, new V3(8f, 0f, 0f), V3.Zero, 1f, false);
        float worstStep = 0f, furthest = 0f;
        V3 prev = V3.Zero;
        for (int i = 0; i <= 180; i++) {
            float now = 1f + i * Frame;
            f.Step(s, now, Frame, V3.Zero, Q.Identity, m);
            if (i > 0) worstStep = Math.Max(worstStep, (f.Shown - prev).Length);
            furthest = Math.Max(furthest, f.Shown.X);
            prev = f.Shown;
        }
        Console.WriteLine($"MOTION syncs stop at 8 m/s: furthest {F(furthest)} m, after 2 s {F(f.Shown.Length)} m from the synced place, worst step {F(worstStep)} m a frame");
        Check("motion: when the syncs stop the model comes back to its synced place", f.Shown.Length < 0.01f);
        Check("motion: and never coasts more than 6% past PredictMaxSeconds' reach", furthest <= 8f * 0.25f * 1.06f);
        Check("motion: and never steps more than twice its speed in a frame", worstStep <= 2f * 8f * Frame);
    }

    // a jump of 5 m between two syncs is taken at once, one of 1 m is eased
    static void Snaps() {
        var m = MotionValues.Default;
        foreach (float jump in new[] { 5f, 1f }) {
            var f = new MotionFollow();
            var a = new MotionSync(V3.Zero, Q.Identity, V3.Zero, V3.Zero, 1f, false);
            f.Step(a, 1f, Frame, V3.Zero, Q.Identity, m);
            f.Step(a, 1f + Frame, Frame, V3.Zero, Q.Identity, m);
            var b = new MotionSync(new V3(jump, 0f, 0f), Q.FromRotVec(new V3(0f, 1f, 0f)), V3.Zero, V3.Zero, 1f + 2f * Frame, false);
            f.Step(b, 1f + 2f * Frame, Frame, V3.Zero, Q.Identity, m);
            if (jump > 3f) Check("motion: a 5 m jump is taken at once (place and turn)", NearV(f.Shown, new V3(5f, 0f, 0f)) && Q.Angle(f.ShownTurn, b.LiveTurn) < 1e-4f);
            else Check("motion: a 1 m jump is eased (moved toward it, not there)", f.Shown.X > 0.01f && f.Shown.X < 0.5f && Q.Angle(f.ShownTurn, b.LiveTurn) > 0.05f);
            if (jump <= 3f) {
                for (int i = 3; i < 60; i++) f.Step(b, 1f + i * Frame, Frame, V3.Zero, Q.Identity, m);
                Check("motion: the eased 1 m jump arrives", Math.Abs(f.Shown.X - 1f) < 1e-3f && Q.Angle(f.ShownTurn, b.LiveTurn) < 1e-3f);
            }
        }
        // SnapMetres 0: every change of target is taken at once
        var none = new MotionFollow();
        var m0 = new MotionValues(0.25f, 0.05f, 0f);
        none.Step(new MotionSync(V3.Zero, Q.Identity, V3.Zero, V3.Zero, 1f, false), 1f, Frame, V3.Zero, Q.Identity, m0);
        none.Step(new MotionSync(new V3(0.2f, 0f, 0f), Q.Identity, V3.Zero, V3.Zero, 1.01f, false), 1.01f, Frame, V3.Zero, Q.Identity, m0);
        Check("motion: SnapMetres 0 takes every jump at once", NearV(none.Shown, new V3(0.2f, 0f, 0f)));
    }

    // NaN and zero time steps change nothing
    static void BadSteps() {
        var m = MotionValues.Default;
        var f = new MotionFollow();
        var a = new MotionSync(V3.Zero, Q.Identity, new V3(5f, 0f, 0f), V3.Zero, 1f, false);
        f.Step(a, 1f, Frame, new V3(0.3f, 0f, 0f), Q.Identity, m);
        f.Step(a, 1f + Frame, Frame, new V3(0.3f, 0f, 0f), Q.Identity, m);
        V3 before = f.Shown;
        Q turn = f.ShownTurn;
        var b = new MotionSync(new V3(1f, 0f, 0f), Q.FromRotVec(new V3(0f, 0.4f, 0f)), V3.Zero, V3.Zero, 1.05f, false);
        bool same = true;
        foreach (float dt in new[] { 0f, float.NaN, -0.01f, float.PositiveInfinity }) {
            f.Step(b, 1.05f, dt, new V3(9f, 9f, 9f), Q.Identity, m);
            same &= NearV(f.Shown, before) && Q.Angle(f.ShownTurn, turn) < 1e-6f;
        }
        f.Step(b, float.NaN, Frame, new V3(9f, 9f, 9f), Q.Identity, m);
        same &= NearV(f.Shown, before);
        Check("motion: a zero, NaN, negative or endless step changes nothing", same);
        // and the next good step goes on from where it was (the sync seen then, eased, not jumped)
        f.Step(b, 1.05f + Frame, Frame, new V3(9f, 9f, 9f), Q.Identity, m);
        Check("motion: after a bad step the next good one eases on", f.Shown.X > before.X - 1e-4f && f.Shown.X < 1f && !float.IsNaN(f.Shown.X));
        // a sync that is not a number is refused: false, and nothing changes
        var nan = new MotionSync(new V3(float.NaN, 0f, 0f), Q.Identity, V3.Zero, V3.Zero, 1.2f, false);
        V3 keep = f.Shown;
        bool ok = f.Step(nan, 1.2f, Frame, V3.Zero, Q.Identity, m);
        Check("motion: a sync that is not a number is refused", !ok && NearV(f.Shown, keep));
    }

    // the first frame sits on the rigidbody, and eases from there
    static void StartsOnBody() {
        var m = MotionValues.Default;
        var f = new MotionFollow();
        var s = new MotionSync(new V3(1f, 0f, 0f), Q.Identity, V3.Zero, V3.Zero, 1f, false);
        Q bodyTurn = Q.FromRotVec(new V3(0f, 0.3f, 0f));
        f.Step(s, 1f, Frame, new V3(0.5f, 0f, 0f), bodyTurn, m);
        Check("motion: the first frame sits on the rigidbody", NearV(f.Shown, new V3(0.5f, 0f, 0f)) && Q.Angle(f.ShownTurn, bodyTurn) < 1e-5f);
        f.Step(s, 1f + Frame, Frame, new V3(0.5f, 0f, 0f), bodyTurn, m);
        Check("motion: then eases toward the target", f.Shown.X > 0.5f && f.Shown.X < 1f);
        f.Reset();
        f.Step(s, 2f, Frame, new V3(-7f, 0f, 0f), Q.Identity, m);
        Check("motion: after Reset the first frame sits on the rigidbody again, even far away", NearV(f.Shown, new V3(-7f, 0f, 0f)));
        f.Step(s, 2f + Frame, Frame, new V3(-7f, 0f, 0f), Q.Identity, m);
        Check("motion: a start further than SnapMetres is put on the target at once", NearV(f.Shown, new V3(1f, 0f, 0f)));
    }
}
