using System;
using System.IO;
using HarmonyLib;
using MelonLoader;
using MelonLoader.Utils;

namespace Earthbending {
    // The freeze fix. The game and the server froze (18 hangs since April), on every Earthbending version and with none.
    // Two stack snapshots of a frozen process (2026-11-23, .superpowers/sdd/2026-11-22-earthbending/freeze-server-stacks-1713.txt
    // and watch-client-stacks-173728.txt) show the same thing: a thread inside MelonLoader 0.7.3's native log writer
    // (VERSION.dll LogMsg, a NativeAOT runtime of its own) waits for ever on that runtime (in the game: the MAIN thread,
    // LogMsg -> a garbage collection of that runtime -> waiting on the other threads), while Mono threads that are exiting
    // wait on the same runtime in their thread clean-up and a new thread waits in VERSION's DllMain. TavernLib sends every
    // line the game logs through MelonLogger, thousands a minute since the game's web service died, and each is a call
    // into that runtime.
    // So after this mod starts, no log line goes into the native writer at all: MelonLogger's two pass-through methods are
    // replaced by a plain managed file, MelonLoader\Latest.managed.log (the last run's is Previous.managed.log). The console
    // window and MelonLoader\Latest.log stop after the "[Earth] LogFix:" line; everything later is in the managed file.
    // 1.1.3 only moved background threads' lines to the main thread, and the main thread then froze the same way.
    internal static class LogFix {
        static readonly object gate = new object();
        static StreamWriter? file;

        /// <summary>The managed log's path ("" until Init opened it).</summary>
        internal static string Path { get; private set; } = "";

        /// <summary>First thing in Main.OnInitializeMelon, on the server and on games.</summary>
        internal static void Init(HarmonyLib.Harmony harmony) {
            try {
                string dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(MelonEnvironment.UserDataDirectory.TrimEnd('\\', '/')) ?? ".", "MelonLoader");
                string path = System.IO.Path.Combine(dir, "Latest.managed.log"), prev = System.IO.Path.Combine(dir, "Previous.managed.log");
                try { if (File.Exists(path)) { File.Delete(prev); File.Move(path, prev); } } catch { }   // a held file: overwritten below
                file = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { AutoFlush = true };
                Path = path;
                MelonLogger.Msg($"[Earth] LogFix: from here every log line goes to {path} (not to this console or Latest.log): MelonLoader's own log writer can freeze the game");
                harmony.Patch(AccessTools.Method(typeof(MelonLogger), "PassLogMsg"), prefix: new HarmonyMethod(typeof(LogFix), nameof(Msg)));
                harmony.Patch(AccessTools.Method(typeof(MelonLogger), "PassLogError"), prefix: new HarmonyMethod(typeof(LogFix), nameof(Err)));
            }
            catch (Exception e) {
                // not patched (or half): lines keep going to MelonLoader's writer, as without the fix
                try { MelonLogger.Error($"[Earth] LogFix: could not take over the log, so the freeze fix is OFF: {e}"); } catch { }
            }
        }

        static bool Msg(string msg, string section, string strippedMsg) => Write(null, section, strippedMsg ?? msg);

        static bool Err(string msg, string section, bool warning) => Write(warning ? "WARNING" : "ERROR", section, msg);

        // Any thread. False: MelonLoader's native writer is not called.
        static bool Write(string? level, string section, string text) {
            StreamWriter? f = file;
            if (f == null) return true;
            try {
                lock (gate) {
                    f.Write('[');
                    f.Write(DateTime.Now.ToString("HH:mm:ss.fff"));
                    f.Write("] ");
                    if (level != null) { f.Write('['); f.Write(level); f.Write("] "); }
                    if (!string.IsNullOrEmpty(section)) { f.Write('['); f.Write(section); f.Write("] "); }
                    f.WriteLine(text);
                }
            }
            catch { }   // a full disk or a closed file: the line is lost, never the frame
            return false;
        }
    }
}
