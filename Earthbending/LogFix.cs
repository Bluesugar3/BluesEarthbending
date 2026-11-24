using System;
using System.IO;
using System.Reflection;
using MelonLoader;
using MelonLoader.Logging;
using MelonLoader.Utils;

namespace Earthbending {
    // The freeze fix. The game and the server froze (19 hangs since April), on every Earthbending version and with none.
    // Three stack snapshots of a frozen process (2026-11-23, .superpowers/sdd/2026-11-22-earthbending/freeze-server-stacks-1713.txt,
    // watch-client-stacks-173728.txt and -174058.txt) show the same thing: a thread inside MelonLoader 0.7.3's native log
    // writer (VERSION.dll LogMsg, a NativeAOT runtime of its own) waits for ever on that runtime (in the game: the MAIN
    // thread, LogMsg -> a garbage collection of that runtime -> waiting on the other threads), while Mono threads that are
    // exiting wait on the same runtime in their thread clean-up and a new thread waits in VERSION's DllMain. TavernLib sends
    // every line the game logs through MelonLogger, thousands a minute since the game's web service died, and each is a call
    // into that runtime.
    // So after this mod starts, no log line goes into the native writer: the two delegates MelonLoader calls it through
    // (BootstrapInterop.Library.LogMsg / LogError) are replaced by managed ones that write a plain file,
    // MelonLoader\Latest.managed.log (the last run's is Previous.managed.log). The console window and MelonLoader\Latest.log
    // stop after the "[Earth] LogFix:" line; everything later is in the managed file.
    // 1.1.3 moved only background threads' lines to the main thread (the main thread then froze the same way); 1.1.3 and
    // 1.1.4 used Harmony prefixes on MelonLogger.PassLogMsg / PassLogError, which patched without an error and never ran.
    internal static unsafe class LogFix {
        static readonly object gate = new object();
        static StreamWriter? file;

        /// <summary>First thing in Main.OnInitializeMelon, on the server and on games.</summary>
        internal static void Init() {
            string path = "";
            try {
                string dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(MelonEnvironment.UserDataDirectory.TrimEnd('\\', '/')) ?? ".", "MelonLoader");
                path = System.IO.Path.Combine(dir, "Latest.managed.log");
                string prev = System.IO.Path.Combine(dir, "Previous.managed.log");
                try { if (File.Exists(path)) { File.Delete(prev); File.Move(path, prev); } } catch { }   // a held file: overwritten below
                var f = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { AutoFlush = true };

                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
                Type interop = typeof(MelonLogger).Assembly.GetType("MelonLoader.InternalUtils.BootstrapInterop", true);
                object library = interop.GetProperty("Library", any)!.GetValue(null, null) ?? throw new InvalidOperationException("BootstrapInterop.Library is null");
                PropertyInfo msg = library.GetType().GetProperty("LogMsg", any)!, err = library.GetType().GetProperty("LogError", any)!;
                Delegate newMsg = Delegate.CreateDelegate(msg.PropertyType, typeof(LogFix).GetMethod(nameof(Msg), any)!);
                Delegate newErr = Delegate.CreateDelegate(err.PropertyType, typeof(LogFix).GetMethod(nameof(Err), any)!);

                MelonLogger.Msg($"[Earth] LogFix: from here every log line goes to {path} (not to this console or Latest.log): MelonLoader's own log writer can freeze the game");
                file = f;
                msg.GetSetMethod(true)!.Invoke(library, new object[] { newMsg });
                err.GetSetMethod(true)!.Invoke(library, new object[] { newErr });
            }
            catch (Exception e) {
                file = null;
                try { MelonLogger.Error($"[Earth] LogFix: could not take over the log, so the freeze fix is OFF: {e}"); } catch { }
                return;
            }
            // the check: this line must land in the managed file
            MelonLogger.Msg("[Earth] LogFix: the log is taken over");
            long length = 0;
            try { length = new FileInfo(path).Length; } catch { }
            if (length == 0) MelonLogger.Error("[Earth] LogFix: the take-over did not work (nothing reached the managed file): the freeze fix is OFF");
        }

        // The signatures of MelonLoader.Bootstrap.LogMsgFn and LogErrorFn.
        static void Msg(ColorARGB* msgColor, string msg, int msgLength, ColorARGB* sectionColor, string section, int sectionLength, string strippedMsg, int strippedMsgLength)
            => Write(null, section, strippedMsg ?? msg);

        static void Err(string msg, int msgLength, string section, int sectionLength, bool warning) => Write(warning ? "WARNING" : "ERROR", section, msg);

        // Any thread.
        static void Write(string? level, string? section, string? text) {
            StreamWriter? f = file;
            if (f == null) return;
            try {
                lock (gate) {
                    f.Write('[');
                    f.Write(DateTime.Now.ToString("HH:mm:ss.fff"));
                    f.Write("] ");
                    if (level != null) { f.Write('['); f.Write(level); f.Write("] "); }
                    if (!string.IsNullOrEmpty(section)) { f.Write('['); f.Write(section); f.Write("] "); }
                    f.WriteLine(text ?? "");
                }
            }
            catch { }   // a full disk or a closed file: the line is lost, never the frame
        }
    }
}
