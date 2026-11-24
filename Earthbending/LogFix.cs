using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MelonLoader;

namespace Earthbending {
    // The game and the server froze (17 hangs since April; a stack snapshot of the frozen server, 2026-11-23): a background
    // thread writing a log line was stuck entering MelonLoader's native log writer (VERSION.dll LogMsg) while other threads
    // were starting and exiting under the Windows loader lock; it held the log's managed lock, so the main thread, wanting
    // to log, waited for ever. TavernLib sends every game log line through MelonLogger, many from web-request threads.
    // The fix: a log line from any thread but the main one is queued, and the main thread writes it at its next frame.
    // So only the main thread, which entered the native runtime at start-up, ever calls the native log writer.
    internal static class LogFix {
        const int MaxQueued = 20000;   // a main thread stalled this long drops the oldest lines, and says how many
        static int mainThread;
        static readonly Queue<KeyValuePair<MethodBase, object[]>> queue = new Queue<KeyValuePair<MethodBase, object[]>>();
        static readonly List<KeyValuePair<MethodBase, object[]>> batch = new List<KeyValuePair<MethodBase, object[]>>();
        static int dropped;

        /// <summary>First thing in Main.OnInitializeMelon (the main thread), on the server and on games.</summary>
        internal static void Init(HarmonyLib.Harmony harmony) {
            mainThread = Thread.CurrentThread.ManagedThreadId;
            int n = 0;
            foreach (string name in new[] { "PassLogMsg", "PassLogError" }) {
                try {
                    MethodInfo target = AccessTools.Method(typeof(MelonLogger), name);
                    if (target == null) { MelonLogger.Warning($"[Earth] LogFix: MelonLogger.{name} not found: not patched"); continue; }
                    harmony.Patch(target, prefix: new HarmonyMethod(typeof(LogFix), nameof(OnlyMainThreadWrites)));
                    n++;
                }
                catch (Exception e) { MelonLogger.Error($"[Earth] LogFix: MelonLogger.{name} could not be patched: {e.Message}"); }
            }
            MelonLogger.Msg($"[Earth] LogFix: {n} of 2 log writers patched: lines from background threads are written by the main thread");
        }

        static bool OnlyMainThreadWrites(MethodBase __originalMethod, object[] __args) {
            if (Thread.CurrentThread.ManagedThreadId == mainThread) return true;
            lock (queue) {
                if (queue.Count >= MaxQueued) { queue.Dequeue(); dropped++; }
                queue.Enqueue(new KeyValuePair<MethodBase, object[]>(__originalMethod, (object[])__args.Clone()));
            }
            return false;
        }

        /// <summary>Every frame (first thing in Main.OnUpdate) and at quit, on the main thread.</summary>
        internal static void Drain() {
            int lost;
            lock (queue) {
                if (queue.Count == 0 && dropped == 0) return;
                batch.AddRange(queue);
                queue.Clear();
                lost = dropped;
                dropped = 0;
            }
            foreach (KeyValuePair<MethodBase, object[]> line in batch) {
                try { line.Key.Invoke(null, line.Value); }   // on the main thread the prefix lets it through
                catch { }                                     // a log line never breaks the frame
            }
            batch.Clear();
            if (lost > 0) MelonLogger.Warning($"[Earth] LogFix: {lost} background log line(s) were dropped while the main thread was busy");
        }
    }
}
