using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

namespace Earthbending {
    // The mod's settings: one MelonPreferences file of its own, UserData\Earthbending.cfg, re-read while the game runs. Save
    // the file and the new values are live within two seconds (Poll compares the file's write time, then one LoadFromFile).
    internal static class Cfg {
        static readonly List<MelonPreferences_Category> categories = new List<MelonPreferences_Category>();
        static string path = "";
        static DateTime stamp;
        static float nextPoll;
        static bool written;

        /// <summary>Raised after the file changed on disk and its new values are loaded. Each handler runs in its own try, so
        /// one that throws never stops the others.</summary>
        internal static event Action? Changed;

        internal static void Init(string fileName) => path = Path.Combine(MelonEnvironment.UserDataDirectory, fileName);

        /// <summary>The file's [section]. MelonLoader keeps ONE list of categories by name for every mod, and a category
        /// moves to whichever file the last caller gave it: a name another mod already uses would take that mod's settings
        /// into this file. So a name some other mod made first is refused (the start-up stops with an error), and the
        /// mod's own sections must not share a name with another mod's (the archery mod has [Log]: ours is [EarthLog]).</summary>
        internal static MelonPreferences_Category Category(string section) {
            if (path.Length == 0) throw new InvalidOperationException("[Earth] Settings: Cfg.Init must run before Cfg.Category");
            foreach (MelonPreferences_Category known in categories) if (known.Identifier == section) return known;
            if (MelonPreferences.GetCategory(section) != null)
                throw new InvalidOperationException($"[Earth] Settings: another mod already has a settings section [{section}]; Earthbending needs a section name of its own");
            MelonPreferences_Category c = MelonPreferences.CreateCategory(section);
            c.SetFilePath(path, true, false);
            categories.Add(c);
            return c;
        }

        /// <summary>Every frame on every machine. The first call writes the file whole (every entry exists by then, so all of
        /// them show in it); later calls look at the file's write time every 2 s and, when it changed, load it once and raise
        /// <see cref="Changed"/>. All categories share one MelonLoader file, and one LoadFromFile refreshes every entry of it.</summary>
        internal static void Poll() {
            if (path.Length == 0 || categories.Count == 0 || Time.unscaledTime < nextPoll) return;
            nextPoll = Time.unscaledTime + 2f;
            try {
                if (!written) {
                    written = true;
                    foreach (MelonPreferences_Category c in categories) c.SaveToFile(false);
                    stamp = Stamp();
                    return;
                }
                DateTime now = Stamp();
                if (now == stamp) return;
                stamp = now;
                categories[0].LoadFromFile(false);
                MelonLogger.Msg("[Earth] Settings reloaded from " + Path.GetFileName(path));
            }
            catch (Exception e) {
                MelonLogger.Warning($"[Earth] Settings: could not read or write {path}: {e.Message}");
                return;
            }
            Action? handlers = Changed;
            if (handlers == null) return;
            foreach (Delegate d in handlers.GetInvocationList()) {
                try { ((Action)d)(); }
                catch (Exception e) { MelonLogger.Warning($"[Earth] Settings: a reload handler ({d.Method.DeclaringType?.Name}.{d.Method.Name}) failed: {e}"); }
            }
        }

        static DateTime Stamp() {
            try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default; }
            catch { return default; }
        }
    }

    /// <summary>[EarthLog] Debug (per game, off): the per-frame, per-hit and per-claim lines. Written
    /// <c>if (Logs.Debug) MelonLogger.Msg(...)</c>, so the string is never built while it is off.</summary>
    internal static class Logs {
        static MelonPreferences_Entry<bool>? debug;

        internal static bool Debug => debug != null && debug.Value;

        internal static void Init() {
            debug = Cfg.Category("EarthLog").CreateEntry("Debug", false, description: "Per game. On: the detail lines for play-tests (every 2 s the ground under the local player's feet; each stone dressed and spawned). Off: start-up lines, warnings and errors only.");
        }
    }
}
