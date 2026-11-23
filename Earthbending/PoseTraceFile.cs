using System;
using System.IO;
using System.Text;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

namespace Earthbending {
    // Games only: the hands trace (task 17a, [EarthPose] Trace, off by default). While it is on and the pose machine runs,
    // Bend.Tick hands every frame here (Frame: written at most 45 times a second, PoseTrace.Due) and every fired move (Fired),
    // and they go to UserData\Earthbending\pose-trace-<yyyyMMdd-HHmmss>.csv in the format of PoseTrace (Poses.cs), which
    // Earthbending.Tests' `--replay` reads. Lines are collected in one StringBuilder and appended to the file once a second, and
    // when the trace stops (switched off, the game quits). A new file at every switch on; the file stops growing at 20 MB
    // (one line). A write that fails is one warning and the trace stops until it is switched off and on again. One line when
    // it starts (the path) and when it stops (the rows written). A diagnostic the owner switches on on purpose: while it is on
    // its formatting allocates; with it off, Bend.Tick pays one bool test (On).
    internal static class PoseTraceFile {
        const float FlushSeconds = 1f;

        static MelonPreferences_Entry<bool>? traceEntry;
        static readonly StringBuilder pending = new StringBuilder(64 * 1024);
        static string path = "";
        static long bytes;
        static int rows, pendingRows;
        static double lastRow = double.NegativeInfinity;
        static float nextFlush;
        static bool wanted;

        /// <summary>The trace is being written now: Bend.Tick's one test per frame.</summary>
        internal static bool On { get; private set; }

        /// <summary>From Bend.Init.</summary>
        internal static void Init() {
            traceEntry = Cfg.Category("EarthPose").CreateEntry("Trace", false, description: "Per game. On: every frame the pose machine runs (at most 45 a second) is written to UserData\\Earthbending\\pose-trace-<date-time>.csv: the time, the practice guide's step, whether your hands are empty, you are on the ground, on natural ground, stunned, the structures armed, your head's turn, and both hands' place, palm and fingers as the machine sees them; and a line for every move that fires. A new file at every switch on; it stops at 20 MB. For finding out why a pose does or does not fire (Earthbending.Tests --replay reads it). Off: costs nothing.");
            Apply();
            Cfg.Changed += Apply;
            try { MelonEvents.OnApplicationDefiniteQuit.Subscribe(Quit); }
            catch (Exception e) { MelonLogger.Warning($"[Earth] Trace: could not hear of the game's quit ({e.Message}); a trace still on at quit loses its last second"); }
        }

        static void Apply() {
            bool want = traceEntry != null && traceEntry.Value;
            if (want == wanted) return;
            wanted = want;
            if (want) Start();
            else if (On) Stop("switched off");
        }

        static void Start() {
            try {
                string dir = Path.Combine(MelonEnvironment.UserDataDirectory, "Earthbending");
                Directory.CreateDirectory(dir);
                DateTime now = DateTime.Now;
                string p = Path.Combine(dir, PoseTrace.FileName(now));
                for (int n = 2; File.Exists(p); n++) p = Path.Combine(dir, Path.GetFileNameWithoutExtension(PoseTrace.FileName(now)) + "-" + n + ".csv");
                string header = PoseTrace.Header + "\n";
                File.WriteAllText(p, header);
                path = p;
                bytes = Encoding.UTF8.GetByteCount(header);
            }
            catch (Exception e) {
                MelonLogger.Warning($"[Earth] Trace: could not start a trace file ({e.Message}); no trace until [EarthPose] Trace is switched off and on again");
                return;
            }
            pending.Length = 0;
            rows = 0;
            pendingRows = 0;
            lastRow = double.NegativeInfinity;
            nextFlush = Time.unscaledTime + FlushSeconds;
            On = true;
            MelonLogger.Msg($"[Earth] Trace: writing the hands to {path} (at most 45 rows a second, up to 20 MB)");
        }

        /// <summary>One frame of the machine (Bend.Tick, only while <see cref="On"/>): a row when one is due.</summary>
        internal static void Frame(in TraceFrame f) {
            if (!On) return;
            if (PoseTrace.Due(f.T, lastRow)) {
                lastRow = f.T;
                PoseTrace.AppendRow(pending, in f);
                pending.Append('\n');
                pendingRows++;
            }
            if (Time.unscaledTime >= nextFlush) Flush();
        }

        /// <summary>A move fired (Bend.Tick, after its frame).</summary>
        internal static void Fired(in TraceFire f) {
            if (!On) return;
            pending.Append(PoseTrace.Fire(in f)).Append('\n');
        }

        /// <summary>Appends what is collected. At 20 MB the trace stops (one line); a failed write is one warning and stops it.</summary>
        static void Flush() {
            nextFlush = Time.unscaledTime + FlushSeconds;
            if (pending.Length == 0) return;
            string text = pending.ToString();
            pending.Length = 0;
            int count = pendingRows;
            pendingRows = 0;
            long size = Encoding.UTF8.GetByteCount(text);
            if (bytes + size > PoseTrace.MaxBytes) {
                On = false;
                MelonLogger.Msg($"[Earth] Trace: {path} reached 20 MB; the trace stops there ({rows} rows written; switch [EarthPose] Trace off and on for a new file)");
                return;
            }
            try { File.AppendAllText(path, text); }
            catch (Exception e) {
                On = false;
                MelonLogger.Warning($"[Earth] Trace: could not write to {path} ({e.Message}); the trace stops ({rows} rows written)");
                return;
            }
            bytes += size;
            rows += count;
        }

        static void Stop(string why) {
            Flush();
            if (!On) return;   // the last flush stopped it already, and said so
            On = false;
            MelonLogger.Msg($"[Earth] Trace: stopped ({why}): {rows} rows in {path}");
        }

        static void Quit() {
            try { if (On) Stop("the game quits"); }
            catch (Exception e) { MelonLogger.Warning($"[Earth] Trace: the last write at quit failed: {e.Message}"); }
        }
    }
}
