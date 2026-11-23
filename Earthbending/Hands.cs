using System;
using System.IO;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

namespace Earthbending {
    // Games only: the local player's head and hands (spec 3.1), and the pose recorder (Task 3). Task 5 adds Hands.Read (the
    // hands in the body frame, through the axis settings below), Hands.Empty and Hands.GroundState here.
    //
    // The recorder, with [EarthPose] Capture on: each time both controllers have stayed within CaptureStillMetres (1 cm) of
    // where they were for CaptureStillSeconds (0.6 s), and either has moved CaptureMoveMetres (0.2 m) since the last snapshot
    // (CaptureGate in Poses.cs), one row goes to UserData\Earthbending\pose-capture-<yyyyMMdd-HHmmss>.csv (PoseCapture.Row:
    // each controller's place and local X, Y and Z axes in the body frame, through BodyFrame.ToBody at body scale 1), both
    // hands buzz twice and the log says "[Earth] Capture: snapshot N". Both hands still more than CaptureRestBelowMetres
    // (0.7 m) under the head is a rest (arms at the sides): no row, no buzz, one log line, and it arms the next snapshot. The
    // owner strikes the 16 poses of spec 3.3 in its order with A on the right hand, resting between them;
    // Earthbending/tools/fit_poses.py turns the file into poses.txt and the four axis settings.
    internal static class Hands {
        // the double buzz of a snapshot: feedback of a one-off tool, so not settings
        const float BuzzStrength = 0.6f, BuzzSeconds = 0.06f, BuzzGapSeconds = 0.15f;
        const float DefaultStillMetres = 0.01f, DefaultStillSeconds = 0.6f, DefaultMoveMetres = 0.2f, DefaultRestBelowMetres = 0.7f;

        static MelonPreferences_Entry<bool>? capture;
        static MelonPreferences_Entry<float>? stillMetres, stillSeconds, moveMetres, restBelowMetres;

        /// <summary>[EarthPose] PalmAxisRight, FingerAxisRight, PalmAxisLeft, FingerAxisLeft: which local axis of each
        /// controller's transform (one of <c>+x -x +y -y +z -z</c>) points out of the palm and along the fingers. Defined
        /// here (Task 3), read by Task 5's Hands.Read.</summary>
        internal static MelonPreferences_Entry<string>? PalmAxisRight, FingerAxisRight, PalmAxisLeft, FingerAxisLeft;

        // the recorder; on = Capture, read on every settings load, so a frame with it off costs one bool test
        static bool on;
        static float still = DefaultStillMetres, holdSeconds = DefaultStillSeconds, moveAway = DefaultMoveMetres, restBelow = DefaultRestBelowMetres;
        static CaptureGate? gate;
        static string? file;
        static int taken;
        static float startedAt = -1f, secondBuzzAt = -1f;
        static bool warned;

        /// <summary>From Main.OnInitializeMelon on games: the [EarthPose] settings of this file.</summary>
        internal static void Init() {
            MelonPreferences_Category cat = Cfg.Category("EarthPose");
            capture = cat.CreateEntry("Capture", false, description: "Per game. On: the pose recorder. Each time both hands are held still (under CaptureStillMetres for CaptureStillSeconds) after either moved CaptureMoveMetres since the last snapshot, one row goes to UserData\\Earthbending\\pose-capture-<time>.csv and both hands buzz twice. Strike the 16 poses in the spec 3.3 table's order (Base, Disc, Ball, Pillar, Cube, Wall, Straight, Kick, Stomp, Uppercut, Parry, Hold, Flick, Explode, Jump, Dash) with A on the right hand, then switch this off and run Earthbending/tools/fit_poses.py on the file. Each switch on starts a new file. Off: nothing runs.");
            stillMetres = cat.CreateEntry("CaptureStillMetres", DefaultStillMetres, description: "Per game. The pose recorder: a hand counts as still while it stays within this many metres of where it was.");
            stillSeconds = cat.CreateEntry("CaptureStillSeconds", DefaultStillSeconds, description: "Per game. The pose recorder: both hands still this many seconds take a snapshot.");
            moveMetres = cat.CreateEntry("CaptureMoveMetres", DefaultMoveMetres, description: "Per game. The pose recorder: after a snapshot, the next one waits until either hand has moved this many metres from where it was, or the hands have rested at the sides. Before the first, from where the hands were when the capture started.");
            restBelowMetres = cat.CreateEntry("CaptureRestBelowMetres", DefaultRestBelowMetres, description: "Per game. The pose recorder: both hands held still more than this many metres under the head is a rest (arms at the sides), never a snapshot; it arms the next one. Every pose keeps a hand at most 0.55 m under the head (Base, Stomp), so 0.7 refuses no pose; never set it to 0.55 or less.");
            PalmAxisRight = cat.CreateEntry("PalmAxisRight", "-x", description: "Per game. The local axis of the right controller (+x -x +y -y +z -z) that points out of the palm. fit_poses.py prints it from the pose capture.");
            FingerAxisRight = cat.CreateEntry("FingerAxisRight", "+z", description: "Per game. The local axis of the right controller (+x -x +y -y +z -z) that points along the fingers. fit_poses.py prints it from the pose capture.");
            PalmAxisLeft = cat.CreateEntry("PalmAxisLeft", "+x", description: "Per game. The local axis of the left controller (+x -x +y -y +z -z) that points out of the palm. fit_poses.py prints it from the pose capture.");
            FingerAxisLeft = cat.CreateEntry("FingerAxisLeft", "+z", description: "Per game. The local axis of the left controller (+x -x +y -y +z -z) that points along the fingers. fit_poses.py prints it from the pose capture.");
            Apply();
            Cfg.Changed += Apply;
        }

        /// <summary>The local player's head (the camera's world place), its yaw (degrees, 0 facing +Z, 90 facing +X, from the
        /// camera's forward flattened to the ground) and both controllers' transforms (the tracked controllers in the world).
        /// False when there is no local player, camera or controller; the outs are then zero and null.</summary>
        internal static bool Raw(out Vector3 head, out float yawDeg, out Transform right, out Transform left) {
            head = default;
            yawDeg = 0f;
            right = null!;
            left = null!;
            PlayerController pc = PlayerController.Current;
            if (pc == null) return false;
            Camera cam = pc.Camera;
            Controller r = pc.RightController, l = pc.LeftController;
            if (cam == null || r == null || l == null) return false;
            Transform h = cam.transform;
            head = h.position;
            yawDeg = Yaw(h);
            right = r.transform;
            left = l.transform;
            return true;
        }

        /// <summary>The head's yaw from its forward on the ground. Looking straight up or down the forward has no ground part,
        /// so the top of the head stands in (it points ahead looking down, back looking up).</summary>
        static float Yaw(Transform head) {
            Vector3 f = head.forward;
            if (f.x * f.x + f.z * f.z < 1e-4f) f = f.y > 0f ? -head.up : head.up;
            return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        }

        static void Apply() {
            float s = Read(stillMetres, "CaptureStillMetres", DefaultStillMetres);
            float h = Read(stillSeconds, "CaptureStillSeconds", DefaultStillSeconds);
            float m = Read(moveMetres, "CaptureMoveMetres", DefaultMoveMetres);
            float r = Read(restBelowMetres, "CaptureRestBelowMetres", DefaultRestBelowMetres);
            bool changed = s != still || h != holdSeconds || m != moveAway || r != restBelow;
            still = s;
            holdSeconds = h;
            moveAway = m;
            restBelow = r;
            bool want = capture != null && capture.Value;
            if (want && !on) Start();
            else if (!want && on) Stop();
            else if (on && changed) {
                gate = new CaptureGate(still, holdSeconds, moveAway, restBelow);
                MelonLogger.Msg($"[Earth] Capture: now still under {still} m for {holdSeconds} s, {moveAway} m between poses, a rest below {restBelow} m under the head: rest, then the next pose");
            }
        }

        static float Read(MelonPreferences_Entry<float>? e, string key, float fallback) {
            if (e == null) return fallback;
            float v = e.Value;
            if (v > 0f && !float.IsInfinity(v)) return v;
            MelonLogger.Warning($"[Earth] Settings: [EarthPose] {key} {v} is not above 0; using {fallback}");
            return fallback;
        }

        static void Start() {
            on = true;
            gate = new CaptureGate(still, holdSeconds, moveAway, restBelow);
            file = null;
            taken = 0;
            startedAt = -1f;
            secondBuzzAt = -1f;
            warned = false;
            MelonLogger.Msg("[Earth] Capture: on. Strike the 16 poses in order (Base, Disc, Ball, Pillar, Cube, Wall, Straight, Kick, Stomp, Uppercut, Parry, Hold, Flick, Explode, Jump, Dash) with A on the right hand, each held still until both hands buzz twice; rest the arms at your sides between poses (a rest takes no snapshot)");
        }

        static void Stop() {
            on = false;
            gate = null;
            MelonLogger.Msg(file == null
                ? "[Earth] Capture: off; no snapshot was taken"
                : $"[Earth] Capture: off after {taken} snapshot(s) in {file}; run: python -I Earthbending/tools/fit_poses.py \"{file}\"");
        }

        /// <summary>Every frame on games (Main.OnUpdate). With Capture off, one bool test.</summary>
        internal static void Tick() {
            if (!on) return;
            try {
                float now = Time.unscaledTime;
                if (startedAt < 0f) startedAt = now;
                if (secondBuzzAt >= 0f && now >= secondBuzzAt) {
                    secondBuzzAt = -1f;
                    Buzz();
                }
                if (!Raw(out Vector3 head, out float yaw, out Transform right, out Transform left)) {
                    gate?.Reset();   // no player (loading, a scene change): the next one starts over, its hands not a pose
                    return;
                }
                if (gate == null) return;
                CaptureEvent e = gate.Step(now, V(head), V(right.position), V(left.position));
                if (e == CaptureEvent.Rest)
                    MelonLogger.Msg(taken < 16 ? $"[Earth] Capture: resting (no snapshot); the next pose in the order is {(Move)(taken + 1)}" : "[Earth] Capture: resting (no snapshot)");
                else if (e == CaptureEvent.Snapshot) Snapshot(now, head, yaw, right, left);
            }
            catch (Exception e) {
                if (warned) return;
                warned = true;
                MelonLogger.Warning($"[Earth] Capture: the recorder failed (said once per capture): {e}");
            }
        }

        static void Snapshot(float now, Vector3 head, float yaw, Transform right, Transform left) {
            V3 h = V(head);
            CaptureHand r = PoseCapture.Hand(h, yaw, V(right.position), V(right.right), V(right.up), V(right.forward));
            CaptureHand l = PoseCapture.Hand(h, yaw, V(left.position), V(left.right), V(left.up), V(left.forward));
            int n = taken + 1;
            try {
                if (file == null) {
                    string dir = Path.Combine(MelonEnvironment.UserDataDirectory, "Earthbending");
                    Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir, "pose-capture-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".csv");
                    File.WriteAllText(path, PoseCapture.Header + Environment.NewLine);
                    file = path;
                    MelonLogger.Msg($"[Earth] Capture: writing to {path}");
                }
                File.AppendAllText(file, PoseCapture.Row(n, now - startedAt, h, yaw, r, l) + Environment.NewLine);
            }
            catch (Exception e) {
                MelonLogger.Warning($"[Earth] Capture: snapshot {n} NOT written (no buzz; move away and strike the pose again): {e.Message}");
                return;
            }
            taken = n;
            Buzz();
            secondBuzzAt = now + BuzzGapSeconds;
            // the name is by count (the order the owner was given), not what the hands did: fit_poses.py says which pose each
            // row looks like when the count is off
            MelonLogger.Msg(n < 16 ? $"[Earth] Capture: snapshot {n} (number {n} in the order is {(Move)n}; next {(Move)(n + 1)})"
                : n == 16 ? $"[Earth] Capture: snapshot 16 (number 16 in the order is Dash, the last); switch [EarthPose] Capture off and run fit_poses.py on {file}"
                : $"[Earth] Capture: snapshot {n} (more than the 16 poses: fit_poses.py on {file} names the pose each row looks like, so the extra rows can be deleted)");
        }

        static void Buzz() {
            PlayerController pc = PlayerController.Current;
            if (pc == null) return;
            if (pc.RightController != null) pc.RightController.TriggerHaptic(BuzzStrength, BuzzSeconds);
            if (pc.LeftController != null) pc.LeftController.TriggerHaptic(BuzzStrength, BuzzSeconds);
        }

        static V3 V(Vector3 v) => new V3(v.x, v.y, v.z);
    }
}
