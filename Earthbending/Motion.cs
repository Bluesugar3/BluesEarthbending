using System;

// Unity-free, compiled by Earthbending.Tests, so only System.* in here. Task 17b (Ruling 38): where a game draws a stone's
// "Model" between the game's syncs of its rigidbody. The server sends a body's place every second frame of its own
// (NetworkScene.Update, serializationInterval 2), so the gap between syncs depends on the server's frame rate (at most half
// of it; a slow server far fewer). Between syncs the game pulls its copy of the body toward the LAST synced place
// (NetworkRigidbody.ClientUpdate), so a sliding stone stalls and jumps; the Model is instead drawn at a
// predicted place (MotionFollow), and StoneView (StoneLook.cs) puts it there every LateUpdate. Tested in MotionTests.cs.
//
//   the target   from the last sync: not kinematic, live + velocity x age, its turn turned on by angular velocity x age;
//                kinematic, live (a kinematic sync carries no velocity). The age counts in full up to PredictMaxSeconds,
//                then fades back to 0 over as long again, so a stone whose syncs stopped comes back to its synced place.
//                The predicted length is held to the free distance the caller gives (the game sweeps the stone's own
//                shape through the world along it): a stone is never predicted into a wall or a standing stone.
//   the follow   shown = target + offset. The offset is all the follow keeps: when a new sync moves the target, the offset
//                takes up the difference (so nothing jumps), and it falls to 0 by a critically damped spring. A steady
//                motion is therefore followed with no lag (the target itself moves), and a correction is eased in over a
//                few frames. The spring's time constant is SmoothSeconds; a third of it for a correction against the way
//                the stone was moving (the sync says stopped, frozen or going back), so an overshoot is short; and for a
//                kinematic stone (a rise) at most half the gap between its last two syncs, so a rise is on time. The turn is
//                the same, its offset a turn vector.
//   the snap     an offset longer than SnapMetres (a teleport) is dropped: the stone is put on its target at once.
namespace Earthbending {
    /// <summary>A turn (Unity's x, y, z, w order and conventions: <c>a * b</c> turns by b, then by a).</summary>
    internal readonly struct Q {
        public readonly float X, Y, Z, W;

        public Q(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }

        public static readonly Q Identity = new Q(0f, 0f, 0f, 1f);

        public static Q operator *(Q a, Q b) => new Q(
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

        /// <summary>The turn back (for a turn of length 1).</summary>
        public Q Inverse => new Q(-X, -Y, -Z, W);

        public Q Normalized {
            get {
                double l = Math.Sqrt((double)X * X + (double)Y * Y + (double)Z * Z + (double)W * W);
                return l > 1e-9 && !double.IsInfinity(l) ? new Q((float)(X / l), (float)(Y / l), (float)(Z / l), (float)(W / l)) : Identity;
            }
        }

        public bool Finite => IsFinite(X) && IsFinite(Y) && IsFinite(Z) && IsFinite(W);

        /// <summary>The turn about <paramref name="r"/>'s direction by its length in radians.</summary>
        public static Q FromRotVec(V3 r) {
            double a = Math.Sqrt((double)r.X * r.X + (double)r.Y * r.Y + (double)r.Z * r.Z);
            if (a < 1e-9) return Identity;
            double s = Math.Sin(a * 0.5) / a;
            return new Q((float)(r.X * s), (float)(r.Y * s), (float)(r.Z * s), (float)Math.Cos(a * 0.5));
        }

        /// <summary>The turn as a vector: its axis times its angle in radians, the short way round (at most pi).</summary>
        public V3 ToRotVec() {
            Q q = Normalized;
            if (q.W < 0f) q = new Q(-q.X, -q.Y, -q.Z, -q.W);
            double s = Math.Sqrt((double)q.X * q.X + (double)q.Y * q.Y + (double)q.Z * q.Z);
            if (s < 1e-9) return new V3(2f * q.X, 2f * q.Y, 2f * q.Z);
            double k = 2.0 * Math.Atan2(s, q.W) / s;
            return new V3((float)(q.X * k), (float)(q.Y * k), (float)(q.Z * k));
        }

        /// <summary><paramref name="v"/> turned by this turn.</summary>
        public V3 Rotate(V3 v) {
            // v + 2 w (u x v) + 2 u x (u x v), u the vector part
            float cx = Y * v.Z - Z * v.Y, cy = Z * v.X - X * v.Z, cz = X * v.Y - Y * v.X;
            float dx = Y * cz - Z * cy, dy = Z * cx - X * cz, dz = X * cy - Y * cx;
            return new V3(v.X + 2f * (W * cx + dx), v.Y + 2f * (W * cy + dy), v.Z + 2f * (W * cz + dz));
        }

        /// <summary>The angle between two turns in radians (0 to pi).</summary>
        public static float Angle(Q a, Q b) {
            // the turn from a to b (a^-1 b), measured by atan2: acos of a dot near 1 loses the small angles in floats
            Q r = a.Normalized.Inverse * b.Normalized;
            double s = Math.Sqrt((double)r.X * r.X + (double)r.Y * r.Y + (double)r.Z * r.Z);
            return (float)(2.0 * Math.Atan2(s, Math.Abs((double)r.W)));
        }

        internal static bool IsFinite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
    }

    /// <summary>The [EarthLooks] numbers of the follow.</summary>
    internal readonly struct MotionValues {
        public const float DefaultPredictMaxSeconds = 0.25f, DefaultSmoothSeconds = 0.05f, DefaultSnapMetres = 3f;

        public readonly float PredictMaxSeconds, SmoothSeconds, SnapMetres;

        public MotionValues(float predictMaxSeconds, float smoothSeconds, float snapMetres) {
            PredictMaxSeconds = predictMaxSeconds; SmoothSeconds = smoothSeconds; SnapMetres = snapMetres;
        }

        public static MotionValues Default => new MotionValues(DefaultPredictMaxSeconds, DefaultSmoothSeconds, DefaultSnapMetres);

        /// <summary>A value that is not a number, endless or below 0 is its default; 0 stays 0 (no prediction, no easing,
        /// every jump taken at once).</summary>
        public MotionValues Clamped() => new MotionValues(
            Clean(PredictMaxSeconds, DefaultPredictMaxSeconds), Clean(SmoothSeconds, DefaultSmoothSeconds), Clean(SnapMetres, DefaultSnapMetres));

        static float Clean(float v, float fallback) => Q.IsFinite(v) && v >= 0f ? v : fallback;
    }

    /// <summary>What the game last heard of a body: its place and turn (the game's <c>LivePosition</c>, <c>LiveRotation</c>),
    /// velocity and angular velocity (world, radians a second), the time it heard it (<c>lastSyncTime</c>, on the same clock as
    /// the frames), and whether it was kinematic.</summary>
    internal readonly struct MotionSync {
        public readonly V3 Live, Velocity, AngularVelocity;
        public readonly Q LiveTurn;
        public readonly float Time;
        public readonly bool Kinematic;

        public MotionSync(V3 live, Q liveTurn, V3 velocity, V3 angularVelocity, float time, bool kinematic) {
            Live = live; LiveTurn = liveTurn; Velocity = velocity; AngularVelocity = angularVelocity; Time = time; Kinematic = kinematic;
        }

        public bool Finite => Fin(Live) && Fin(Velocity) && Fin(AngularVelocity) && LiveTurn.Finite && Q.IsFinite(Time);

        static bool Fin(V3 v) => Q.IsFinite(v.X) && Q.IsFinite(v.Y) && Q.IsFinite(v.Z);

        public bool Same(in MotionSync o) => Time == o.Time && Kinematic == o.Kinematic && Eq(Live, o.Live) && Eq(Velocity, o.Velocity)
            && Eq(AngularVelocity, o.AngularVelocity) && LiveTurn.X == o.LiveTurn.X && LiveTurn.Y == o.LiveTurn.Y && LiveTurn.Z == o.LiveTurn.Z
            && LiveTurn.W == o.LiveTurn.W;

        static bool Eq(V3 a, V3 b) => a.X == b.X && a.Y == b.Y && a.Z == b.Z;
    }

    internal static class Motion {
        /// <summary>A speed under this (m/s) is a stone at rest.</summary>
        public const float RestSpeed = 0.05f;
        /// <summary>A correction against the way the stone was moving is eased this many times faster.</summary>
        public const float AgainstFaster = 3f;

        /// <summary>How long the prediction runs for a sync heard <paramref name="now"/> - its time ago: the age itself up to
        /// <paramref name="predictMax"/>, then back down to 0 over as long again (syncs that stopped), 0 below 0.</summary>
        public static float Age(in MotionSync s, float now, float predictMax) => PredictedAge(now - s.Time, predictMax);

        public static float PredictedAge(float age, float predictMax) {
            if (!(age > 0f) || !(predictMax > 0f)) return 0f;
            if (age <= predictMax) return age;
            if (age >= 2f * predictMax) return 0f;
            // a cubic from (P, slope 1) to (2P, slope 0) at 0: the stone coasts on a little (at most 5% of P), then comes
            // back to its synced place smoothly (at most 1.8 times its speed), never with a turn back in one frame
            float u = (age - predictMax) / predictMax;
            return predictMax * (1f + u - 5f * u * u + 3f * u * u * u);
        }

        /// <summary>Where the body should be now: kinematic, the synced place; else the synced place plus velocity x age, that
        /// length held to <paramref name="free"/> metres (the free way ahead; endless or not a number: no cap).</summary>
        public static V3 Target(in MotionSync s, float now, float predictMax, float free = float.PositiveInfinity) {
            if (s.Kinematic) return s.Live;
            V3 ahead = s.Velocity * Age(s, now, predictMax);
            if (free >= 0f && !float.IsPositiveInfinity(free)) {
                float l = ahead.Length;
                if (l > free) ahead = l > 1e-9f ? ahead * (free / l) : V3.Zero;
            }
            else if (free < 0f) ahead = V3.Zero;
            return s.Live + ahead;
        }

        /// <summary>How the body should be turned now: the synced turn, turned on by angular velocity x age (not kinematic).</summary>
        public static Q TargetTurn(in MotionSync s, float now, float predictMax) =>
            s.Kinematic ? s.LiveTurn : (Q.FromRotVec(s.AngularVelocity * Age(s, now, predictMax)) * s.LiveTurn).Normalized;

        /// <summary>True when <paramref name="next"/> says the body stopped, froze or goes back, against the way
        /// <paramref name="was"/> had it moving (it moved at least <see cref="RestSpeed"/>).</summary>
        public static bool Against(in MotionSync was, in MotionSync next) {
            if (was.Kinematic) return false;
            float speed = was.Velocity.Length;
            if (!(speed >= RestSpeed)) return false;
            V3 along = next.Kinematic ? V3.Zero : next.Velocity;
            return V3.Dot(along, was.Velocity) / speed < RestSpeed;
        }

        /// <summary>A critically damped spring pulling <paramref name="x"/> to 0 with time constant <paramref name="smooth"/>
        /// (exact for any step, so never unstable): x(t) = (x0 + (r0 + x0/T) t) e^(-t/T). A time constant of 0 is no easing.</summary>
        public static void Damp(ref V3 x, ref V3 rate, float dt, float smooth) {
            if (!(smooth > 0f)) { x = V3.Zero; rate = V3.Zero; return; }
            double w = 1.0 / smooth, e = Math.Exp(-w * dt);
            V3 x0 = x, r0 = rate;
            x = new V3(Pos(x0.X, r0.X, w, dt, e), Pos(x0.Y, r0.Y, w, dt, e), Pos(x0.Z, r0.Z, w, dt, e));
            rate = new V3(Rate(x0.X, r0.X, w, dt, e), Rate(x0.Y, r0.Y, w, dt, e), Rate(x0.Z, r0.Z, w, dt, e));
        }

        // x(t) = (x0 + c t) e^(-wt), r(t) = (r0 - w c t) e^(-wt), c = r0 + w x0
        static float Pos(float x0, float r0, double w, double dt, double e) => (float)((x0 + (r0 + w * x0) * dt) * e);
        static float Rate(float x0, float r0, double w, double dt, double e) => (float)((r0 - w * (r0 + w * x0) * dt) * e);
    }

    /// <summary>One stone's follow on a game. No allocation per step.</summary>
    internal sealed class MotionFollow {
        /// <summary>Where and how turned the Model is drawn (valid once <see cref="Step"/> returned true).</summary>
        public V3 Shown { get; private set; }
        public Q ShownTurn { get; private set; } = Q.Identity;

        bool started;
        MotionSync last;
        V3 offset, offsetRate, turnOffset, turnRate;
        float lastFree = float.PositiveInfinity;   // the free distance the last step used (the old target at a new sync)
        float smoothNow = -1f;                     // the spring's time constant since the last sync (below 0: SmoothSeconds)
        float kinematicGap;                        // the gap between the last two kinematic syncs (0: none known)

        /// <summary>The spring's time constant in use now (seconds).</summary>
        public float SmoothNow(MotionValues values) => smoothNow >= 0f ? smoothNow : values.Clamped().SmoothSeconds;

        /// <summary>Forget everything: the next step starts on the rigidbody again (before the first sync, a stone that came back).</summary>
        public void Reset() { started = false; smoothNow = -1f; kinematicGap = 0f; lastFree = float.PositiveInfinity; }

        /// <summary>One frame: <paramref name="s"/> is what the game last heard, <paramref name="now"/> the frame's time on the
        /// sync's clock, <paramref name="dt"/> the frame's length, <paramref name="body"/> and <paramref name="bodyTurn"/> where
        /// the rigidbody is (where the first frame is drawn). A step that is not above 0 or not a number, or a frame time that is
        /// not a number, changes nothing. False, and nothing changes, for a sync that is not a number (draw the rigidbody).
        /// <paramref name="free"/>: the metres the stone can move from its synced place along its velocity before it meets the
        /// world (endless: nothing known); the prediction never goes further.</summary>
        public bool Step(in MotionSync s, float now, float dt, V3 body, Q bodyTurn, MotionValues values, float free = float.PositiveInfinity) {
            if (!s.Finite) return false;
            MotionValues m = values.Clamped();
            if (!started) {
                if (!Q.IsFinite(now)) return false;
                V3 target = Motion.Target(s, now, m.PredictMaxSeconds, free);
                Q targetTurn = Motion.TargetTurn(s, now, m.PredictMaxSeconds);
                Q bt = bodyTurn.Finite ? bodyTurn.Normalized : targetTurn;
                V3 b = Q.IsFinite(body.X) && Q.IsFinite(body.Y) && Q.IsFinite(body.Z) ? body : target;
                offset = b - target;
                turnOffset = (bt * targetTurn.Inverse).ToRotVec();
                offsetRate = V3.Zero;
                turnRate = V3.Zero;
                last = s;
                lastFree = free;
                started = true;
                Shown = b;
                ShownTurn = bt;
                return true;
            }
            if (!(dt > 0f) || float.IsInfinity(dt) || !Q.IsFinite(now)) return true;

            if (!s.Same(last)) {
                // the target moved under the stone: the offset takes the difference up, so the drawn place does not move now
                offset = offset + (Motion.Target(last, now, m.PredictMaxSeconds, lastFree) - Motion.Target(s, now, m.PredictMaxSeconds, free));
                Q was = Q.FromRotVec(turnOffset) * Motion.TargetTurn(last, now, m.PredictMaxSeconds);
                turnOffset = (was * Motion.TargetTurn(s, now, m.PredictMaxSeconds).Inverse).ToRotVec();
                // the spring until the next sync: a kinematic stone at most half its sync gap (a rise is not drawn late), a
                // correction against the way the stone moved three times faster (an overshoot is short)
                if (s.Kinematic && last.Kinematic) { if (s.Time > last.Time) kinematicGap = s.Time - last.Time; }
                else kinematicGap = 0f;
                float t = m.SmoothSeconds;
                if (s.Kinematic && kinematicGap > 0f) t = Math.Min(t, kinematicGap * 0.5f);
                if (Motion.Against(last, s)) t = Math.Min(t, m.SmoothSeconds / Motion.AgainstFaster);
                smoothNow = t;
                last = s;
            }
            if (offset.Length > m.SnapMetres) {
                offset = V3.Zero; offsetRate = V3.Zero; turnOffset = V3.Zero; turnRate = V3.Zero;
            }
            float smooth = smoothNow >= 0f ? smoothNow : m.SmoothSeconds;
            Motion.Damp(ref offset, ref offsetRate, dt, smooth);
            Motion.Damp(ref turnOffset, ref turnRate, dt, smooth);
            Shown = Motion.Target(s, now, m.PredictMaxSeconds, free) + offset;
            lastFree = free;
            ShownTurn = (Q.FromRotVec(turnOffset) * Motion.TargetTurn(s, now, m.PredictMaxSeconds)).Normalized;
            return true;
        }
    }
}
