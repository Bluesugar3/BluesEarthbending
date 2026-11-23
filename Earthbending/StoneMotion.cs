using System;
using System.Collections.Generic;
using System.Reflection;
using Alta.Networking;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace Earthbending {
    // Games only (Task 17b, Ruling 38): what a stone's NetworkRigidbody last heard from the server, for the Model's smooth,
    // predicted place (Motion.cs; StoneView.LateUpdate draws it). The game keeps it in private fields of NetworkRigidbody
    // (_decomp/Alta.Networking/NetworkRigidbody.cs): positionSerializer (its LivePosition and LiveRotation, PositionSerializer.cs),
    // velocity, angularVelocity, lastSyncTime (Time.timeSinceLevelLoad when the last sync was read; 0 before the first) and
    // syncKinematic. They are found once by name and type; if any is missing or of another type, one warning, and every stone's
    // Model rides its rigidbody as before 17b. [EarthLooks] SmoothMotion, PredictMaxSeconds, SmoothSeconds, SnapMetres (per game).
    internal static class StoneMotion {
        static MelonPreferences_Entry<bool>? smoothEntry;
        static MelonPreferences_Entry<float>? predictEntry, smoothSecondsEntry, snapEntry;

        static bool resolved, usable;
        static AccessTools.FieldRef<NetworkRigidbody, PositionSerializer<NetworkRigidbody>>? serializerRef;
        static AccessTools.FieldRef<NetworkRigidbody, Vector3>? velocityRef, angularRef;
        static AccessTools.FieldRef<NetworkRigidbody, float>? lastSyncRef;
        static AccessTools.FieldRef<NetworkRigidbody, bool>? kinematicRef;

        /// <summary>[EarthLooks] SmoothMotion: false, every Model rides its rigidbody (as before 17b).</summary>
        internal static bool On { get; private set; } = true;
        internal static MotionValues Values { get; private set; } = MotionValues.Default;

        /// <summary>From StoneLook.Init (games only).</summary>
        internal static void Init() {
            MelonPreferences_Category looks = Cfg.Category("EarthLooks");
            smoothEntry = looks.CreateEntry("SmoothMotion", true, description: "Per game. On: a stone is drawn where it should be now, predicted from the server's last sync of it (the server sends a body's place every second frame of its own, so the gap depends on the server's frame rate, and between syncs the game pulls its copy toward the last synced place, so it stalls and jumps), and eased there. Off: the stone is drawn on the game's own copy of its body.");
            predictEntry = looks.CreateEntry("PredictMaxSeconds", MotionValues.DefaultPredictMaxSeconds, description: "Per game. With SmoothMotion: how far ahead of the last sync a moving stone is predicted (its synced velocity times the time since), at most this many seconds. 0 or more (0: drawn at the synced place, eased).");
            smoothSecondsEntry = looks.CreateEntry("SmoothSeconds", MotionValues.DefaultSmoothSeconds, description: "Per game. With SmoothMotion: the time constant in seconds of the easing that takes up each sync's correction (critically damped: most of it is gone after 3 of these). 0 or more (0: no easing, every correction shows as a jump).");
            snapEntry = looks.CreateEntry("SnapMetres", MotionValues.DefaultSnapMetres, description: "Per game. With SmoothMotion: a stone drawn further than this from where it should be (a teleport) is put there at once instead of eased. 0 or more.");
            Apply();
            Cfg.Changed += Apply;
        }

        static void Apply() {
            On = smoothEntry == null || smoothEntry.Value;
            Values = new MotionValues(
                Cfg.NotNegative(predictEntry, "EarthLooks", "PredictMaxSeconds", MotionValues.DefaultPredictMaxSeconds),
                Cfg.NotNegative(smoothSecondsEntry, "EarthLooks", "SmoothSeconds", MotionValues.DefaultSmoothSeconds),
                Cfg.NotNegative(snapEntry, "EarthLooks", "SnapMetres", MotionValues.DefaultSnapMetres)).Clamped();
        }

        /// <summary>True when the five fields were found (once per game; a miss is one warning).</summary>
        internal static bool Usable {
            get {
                if (!resolved) Resolve();
                return usable;
            }
        }

        static void Resolve() {
            resolved = true;
            try {
                var missing = new List<string>();
                Want("positionSerializer", typeof(PositionSerializer<NetworkRigidbody>), missing);
                Want("velocity", typeof(Vector3), missing);
                Want("angularVelocity", typeof(Vector3), missing);
                Want("lastSyncTime", typeof(float), missing);
                Want("syncKinematic", typeof(bool), missing);
                if (missing.Count > 0) {
                    MelonLogger.Warning($"[Earth] Looks: NetworkRigidbody has no {string.Join(", ", missing.ToArray())} as this mod knows it; stones are drawn on their bodies (no smooth motion)");
                    return;
                }
                serializerRef = AccessTools.FieldRefAccess<NetworkRigidbody, PositionSerializer<NetworkRigidbody>>("positionSerializer");
                velocityRef = AccessTools.FieldRefAccess<NetworkRigidbody, Vector3>("velocity");
                angularRef = AccessTools.FieldRefAccess<NetworkRigidbody, Vector3>("angularVelocity");
                lastSyncRef = AccessTools.FieldRefAccess<NetworkRigidbody, float>("lastSyncTime");
                kinematicRef = AccessTools.FieldRefAccess<NetworkRigidbody, bool>("syncKinematic");
                usable = true;
                MelonLogger.Msg("[Earth] Looks: stones move smoothly on this game (drawn ahead of the game's syncs, [EarthLooks] SmoothMotion)");
            }
            catch (Exception e) {
                usable = false;
                MelonLogger.Warning($"[Earth] Looks: NetworkRigidbody's sync fields could not be read ({e.GetType().Name}: {e.Message}); stones are drawn on their bodies (no smooth motion)");
            }
        }

        static void Want(string name, Type type, List<string> missing) {
            FieldInfo f = typeof(NetworkRigidbody).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f == null || f.FieldType != type) missing.Add($"{type.Name} {name}");
        }

        // ---------------------------------------------------------------- the measured sync gap ([EarthLog] Debug only)
        // The server sends a body's place every second frame of its own, so the real gap is measured, not assumed: every 5 s, while
        // any stone moves (not kinematic) and at least 5 syncs of moving stones came, one line with the gaps and how far the Model
        // is drawn from the game's own copy of the body. A sync is a change of lastSyncTime.
        const float ReportSeconds = 5f;
        const int ReportMinSyncs = 5;
        static float windowStart = -1f, gapMin, gapMax, offsetMax;
        static double gapSum, offsetSum;
        static int window, syncs, gaps, offsets, moving;

        /// <summary>Debug only, once per stone per frame (StoneView.LateUpdate): <paramref name="seenSync"/> and
        /// <paramref name="seenWindow"/> are that stone's own memory; <paramref name="offset"/> the Model's distance from the
        /// body this frame (0 while it rides the body).</summary>
        internal static void Measure(NetworkRigidbody? body, ref float seenSync, ref int seenWindow, float offset) {
            float now = Time.unscaledTime;
            if (windowStart < 0f) Restart(now);
            else if (now - windowStart >= ReportSeconds) {
                if (syncs >= ReportMinSyncs && moving > 0)
                    MelonLogger.Msg($"[Earth] Motion: {syncs} sync(s) in {now - windowStart:0.0} s over {moving} moving stone(s): gap min {(gaps > 0 ? gapMin * 1000f : 0f):0} / mean {(gaps > 0 ? gapSum / gaps * 1000.0 : 0.0):0} / max {gapMax * 1000f:0} ms; the model's offset from the game's own copy of the body: mean {(offsets > 0 ? offsetSum / offsets : 0.0):0.###} / max {offsetMax:0.###} m");
                Restart(now);
            }
            if (body == null || !Read(body, out MotionSync s) || s.Kinematic) return;
            if (seenWindow != window) { seenWindow = window; moving++; }
            if (Q.IsFinite(offset)) {
                offsetSum += offset;
                offsets++;
                if (offset > offsetMax) offsetMax = offset;
            }
            if (s.Time == seenSync) return;
            if (seenSync != 0f) {
                float gap = s.Time - seenSync;
                if (gap > 0f) {
                    if (gaps == 0 || gap < gapMin) gapMin = gap;
                    if (gap > gapMax) gapMax = gap;
                    gapSum += gap;
                    gaps++;
                }
            }
            seenSync = s.Time;
            syncs++;
        }

        static void Restart(float now) {
            windowStart = now;
            window++;
            syncs = gaps = offsets = moving = 0;
            gapMin = gapMax = offsetMax = 0f;
            gapSum = offsetSum = 0.0;
        }

        /// <summary>What <paramref name="body"/> last heard from the server; false before its first sync (or with no serializer
        /// yet), and when the fields cannot be read.</summary>
        internal static bool Read(NetworkRigidbody body, out MotionSync sync) {
            sync = default;
            if (!Usable) return false;
            float time = lastSyncRef!(body);
            if (time == 0f) return false;   // the game's own test for "never synced"
            PositionSerializer<NetworkRigidbody> ps = serializerRef!(body);
            if (ps == null) return false;
            Vector3 p = ps.LivePosition, v = velocityRef!(body), w = angularRef!(body);
            Quaternion r = ps.LiveRotation;
            sync = new MotionSync(new V3(p.x, p.y, p.z), new Q(r.x, r.y, r.z, r.w), new V3(v.x, v.y, v.z), new V3(w.x, w.y, w.z), time, kinematicRef!(body));
            return true;
        }
    }
}
