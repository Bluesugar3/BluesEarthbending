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
        /// into this file. So every section of this mod starts with "Earth" ([EarthLog], [EarthStones], ...), and a name some
        /// other mod made first is refused (the start-up stops with an error).</summary>
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

        /// <summary>A number setting that must be above 0: its value, or <paramref name="fallback"/> (no entry yet, or a value
        /// that is 0, negative, infinite or not a number, which is said once per load).</summary>
        internal static float Positive(MelonPreferences_Entry<float>? e, string section, string key, float fallback) => Number(e, section, key, fallback, false);

        /// <summary>As <see cref="Positive"/>, with 0 allowed.</summary>
        internal static float NotNegative(MelonPreferences_Entry<float>? e, string section, string key, float fallback) => Number(e, section, key, fallback, true);

        static float Number(MelonPreferences_Entry<float>? e, string section, string key, float fallback, bool zeroOk) {
            if (e == null) return fallback;
            float v = e.Value;
            if ((zeroOk ? v >= 0f : v > 0f) && !float.IsInfinity(v)) return v;
            MelonLogger.Warning($"[Earth] Settings: [{section}] {key} {v} is not {(zeroOk ? "0 or more" : "above 0")}; using {fallback}");
            return fallback;
        }
    }

    /// <summary>[EarthModifiers] Reach and MaxHeight (spec 2.4), on the server AND on every game: a game picks the stone its
    /// modifier goes to with its own values (Bend.cs), the server checks the claim with its own plus RangeSlackMetres
    /// (Server.cs). Keep them the same on both; the server's are the ones that count.</summary>
    internal static class ReachSettings {
        const float DefaultReach = 2.5f, DefaultMaxHeight = 2.4f;

        static MelonPreferences_Entry<float>? reachEntry, maxHeightEntry;

        /// <summary>Metres from the head to the nearest point of a stone's box.</summary>
        internal static float Reach { get; private set; } = DefaultReach;
        /// <summary>Metres a stone's centre may be above the feet.</summary>
        internal static float MaxHeight { get; private set; } = DefaultMaxHeight;

        /// <summary>From Main.OnInitializeMelon on every machine.</summary>
        internal static void Init() {
            MelonPreferences_Category cat = Cfg.Category("EarthModifiers");
            reachEntry = cat.CreateEntry("Reach", DefaultReach, description: "Server and game. A modifier reaches a stone whose nearest point is within this many metres of the head, whose centre is in front of the head, and whose centre is at most MaxHeight above the feet (spec 2.4). The game picks the stone most in front with its own value; the server checks it with its own plus RangeSlackMetres, so keep both the same. Above 0.");
            maxHeightEntry = cat.CreateEntry("MaxHeight", DefaultMaxHeight, description: "Server and game. A modifier reaches a stone only when its centre is at most this many metres above the feet (see Reach). 0 or more.");
            Apply();
            Cfg.Changed += Apply;
        }

        static void Apply() {
            Reach = Cfg.Positive(reachEntry, "EarthModifiers", "Reach", DefaultReach);
            MaxHeight = Cfg.NotNegative(maxHeightEntry, "EarthModifiers", "MaxHeight", DefaultMaxHeight);
        }
    }

    /// <summary>[EarthLog] Debug (per game, off): the per-frame, per-hit and per-claim lines. Written
    /// <c>if (Logs.Debug) MelonLogger.Msg(...)</c>, so the string is never built while it is off.</summary>
    internal static class Logs {
        static MelonPreferences_Entry<bool>? debug;

        internal static bool Debug => debug != null && debug.Value;

        internal static void Init() {
            debug = Cfg.Category("EarthLog").CreateEntry("Debug", false, description: "Per game. On: the detail lines for play-tests (every 2 s the ground under the local player's feet; each stone dressed and spawned; each message 19 sent and received; each push and hitstun). Off: start-up lines, warnings and errors only.");
        }
    }
}
