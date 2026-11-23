using System;
using System.Collections.Generic;
using Alta.Networking;
using Alta.Networking.Scripts.Player;
using MelonLoader;
using UnityEngine;

namespace Earthbending {
    // The server: fight health (docs/superpowers/specs/2026-11-23-earthbending-fight-health-design.md). The rules are Fight.cs's
    // (Unity-free, tested); this keeps the one ring, feeds the ledger every frame and on every stone hit (Hits.HitPlayer), and
    // tells every game the ring and each fighter's points (message 19's Fight and Ring kinds). [EarthFight] is the server's.
    //   - The ring: another mod's (Arena.SetRing) wins over the console's (`earth ring`), which wins over [EarthFight] Ring.
    //     [EarthFight] Enabled off is no ring at all. Any change of the ring that counts (a new one, a moved one, none) sends Ring
    //     to every game, clears the ledger and sends every cleared player's -1 (spec, Review Focus 3): from the next physics step
    //     hits follow the rule of the new ring (or the real-health rule with none).
    //   - Tick (every frame, Main.OnUpdate before the headless return): every player with a connection, a user record and a
    //     living, standing body (not downed) is seen where their feet are (PlayerController.RealFeetPosition); anyone the
    //     ledger knows who is not (left the server, died, downed, no body) is forgotten, which refills the others. Every change
    //     goes to every game at once. Every 5 s, and at once when a player joins, the ring and every fighter's points go to
    //     every game again (a game that joined or missed a message catches up within 5 s).
    //   - Nothing is saved. Log lines: one at start-up and per ring change, a Summary line per finishing hit (Hits), Debug
    //     lines for each change of points.
    internal static class FightServer {
        const float ResendSeconds = 5f;

        static MelonPreferences_Entry<bool>? enabledEntry;
        static MelonPreferences_Entry<int>? pointsEntry;
        static MelonPreferences_Entry<string>? ringEntry;
        static MelonPreferences_Entry<float>? flingClearEntry, flingMinEntry, flingMaxEntry, flingHitstunEntry;

        static bool enabled = true, ready;
        static FightRing? arenaRing, consoleRing, settingRing;
        static string settingText = "";
        static FightRing ring;            // the ring that counts (valid while hasRing)
        static bool hasRing;
        static string ringFrom = "none";
        static FightLedger ledger = new FightLedger(FightRules.DefaultPoints);
        static readonly List<FightLedger.Change> changes = new List<FightLedger.Change>();
        static readonly List<ulong> known = new List<ulong>();
        static readonly HashSet<ulong> seen = new HashSet<ulong>(), seenBefore = new HashSet<ulong>();
        static float resendAt;
        static bool tickFailed, sendFailed;

        /// <summary>[EarthFight] FlingClearMetres: the finishing hit lands its player this far past the ring's edge.</summary>
        internal static float FlingClear { get; private set; } = FightRules.DefaultFlingClear;
        internal static float FlingMin { get; private set; } = FightRules.DefaultFlingMin;
        internal static float FlingMax { get; private set; } = FightRules.DefaultFlingMax;
        /// <summary>[EarthFight] FlingHitstunSeconds: no bending this long after the finishing hit.</summary>
        internal static float FlingHitstun { get; private set; } = FightRules.DefaultFlingHitstun;

        /// <summary>Whether there is a ring now.</summary>
        internal static bool HasRing => hasRing;
        /// <summary>The ring now (valid only while <see cref="HasRing"/>).</summary>
        internal static FightRing Ring => ring;
        /// <summary>Where the ring now came from: "the arena mod", "the console", "[EarthFight] Ring" or "none".</summary>
        internal static string RingFrom => ringFrom;
        internal static int Points => ledger.Points;

        /// <summary>From Main.OnInitializeMelon on the dedicated server, after Server.Init.</summary>
        internal static void Init() {
            MelonPreferences_Category cat = Cfg.Category("EarthFight");
            enabledEntry = cat.CreateEntry("Enabled", true, description: "Server. Fight health: on a ring (set by the arena mod, the console's `earth ring`, or Ring below) a stone takes its power off the player's 20 points of fight health instead of their real health, and the hit that reaches 0 flings them out of the ring. Off: there is no ring, and every stone hit takes a share of real health ([EarthDamage] PlayerHealthPoints).");
            pointsEntry = cat.CreateEntry("Points", FightRules.DefaultPoints, description: "Server. The fight health a player gets on stepping onto the ring (RUMBLE's 20). A stone takes its power off it: Disc 0, Ball 1, Pillar 1, Cube 2, Wall 3, +1 still or sliding on the ground, +1 moving fast (a sliding Wall is 5). 1 to 250. A change ends every fight now.");
            ringEntry = cat.CreateEntry("Ring", "", description: "Server. A ring for testing on open ground: \"x, y, z, ring radius, arena radius\" in metres (y is the floor; the arena radius takes in the pit and the rim round the ring, at least the ring radius), e.g. \"-697, 128.2, 87, 10, 16\". Empty: none. The arena mod's ring, then the console's (`earth ring`), win over this one. Drawn on every game as a dotted circle.");
            flingClearEntry = cat.CreateEntry("FlingClearMetres", FightRules.DefaultFlingClear, description: "Server. The hit that takes a player's fight health to 0 flings them along the stone's way, 45 degrees up, fast enough to land this many metres past the ring's edge (held to FlingMinSpeed..FlingMaxSpeed). 0 or more.");
            flingMinEntry = cat.CreateEntry("FlingMinSpeed", FightRules.DefaultFlingMin, description: "Server. The finishing hit's fling is at least this fast, in m/s (from near the edge). 0 or more.");
            flingMaxEntry = cat.CreateEntry("FlingMaxSpeed", FightRules.DefaultFlingMax, description: "Server. The finishing hit's fling is at most this fast, in m/s ([EarthMovement] MaxPushSpeed on each game, 20, caps it again). Above 0.");
            flingHitstunEntry = cat.CreateEntry("FlingHitstunSeconds", FightRules.DefaultFlingHitstun, description: "Server. The flung player cannot bend for this many seconds. 0 or more.");
            ready = true;
            Apply();
            Cfg.Changed += Apply;
            MelonLogger.Msg($"[Earth] Fight: fight health {(enabled ? "on" : "off ([EarthFight] Enabled)")}: {ledger.Points} points on the ring, a stone takes its power; the finishing hit flings {FlingMin:0.##} to {FlingMax:0.##} m/s to land {FlingClear:0.##} m past the edge, {FlingHitstun:0.##} s of hitstun; ring: {Describe()}");
        }

        static void Apply() {
            enabled = enabledEntry?.Value ?? true;
            int points = pointsEntry?.Value ?? FightRules.DefaultPoints;
            if (points < 1 || points > 250) {
                MelonLogger.Warning($"[Earth] Settings: [EarthFight] Points {points} is not 1 to 250; using {Math.Max(1, Math.Min(250, points))}");
                points = Math.Max(1, Math.Min(250, points));
            }
            FlingClear = Cfg.NotNegative(flingClearEntry, "EarthFight", "FlingClearMetres", FightRules.DefaultFlingClear);
            FlingMin = Cfg.NotNegative(flingMinEntry, "EarthFight", "FlingMinSpeed", FightRules.DefaultFlingMin);
            FlingMax = Cfg.Positive(flingMaxEntry, "EarthFight", "FlingMaxSpeed", FightRules.DefaultFlingMax);
            FlingHitstun = Cfg.NotNegative(flingHitstunEntry, "EarthFight", "FlingHitstunSeconds", FightRules.DefaultFlingHitstun);
            string text = (ringEntry?.Value ?? "").Trim();
            if (text != settingText) {
                settingText = text;
                if (text.Length == 0) settingRing = null;
                else if (FightRing.TryParse(text, out FightRing r)) settingRing = r;
                else {
                    settingRing = null;
                    MelonLogger.Warning($"[Earth] Settings: [EarthFight] Ring \"{text}\" is not \"x, y, z, ring radius, arena radius\" (a ring radius above 0, an arena radius at least that): no ring from the setting");
                }
            }
            if (points != ledger.Points) {
                EndAll($"[EarthFight] Points is now {points}");
                ledger = new FightLedger(points);
            }
            Recompute();
        }

        // ---------------------------------------------------------------- the ring

        /// <summary>Arena.SetRing / ClearRing: another mod's ring (null: none), not drawn.</summary>
        internal static void SetArena(FightRing? r) {
            arenaRing = r;
            if (ready) Recompute();
        }

        /// <summary>The console's ring (null: none), drawn. Never saved.</summary>
        internal static void SetConsole(FightRing? r) {
            consoleRing = r;
            if (ready) Recompute();
        }

        // The ring that counts now; when it is not the one before (another place or size, or none), every fight ends.
        static void Recompute() {
            FightRing? want = null;
            string from = "none";
            if (enabled) {
                if (arenaRing.HasValue) { want = arenaRing; from = "the arena mod"; }
                else if (consoleRing.HasValue) { want = consoleRing; from = "the console"; }
                else if (settingRing.HasValue) { want = settingRing; from = "[EarthFight] Ring"; }
            }
            bool has = want.HasValue && want.Value.Valid;
            FightRing next = has ? want!.Value : default;
            if (has == hasRing && (!has || Same(next, ring))) { ringFrom = has ? from : "none"; return; }
            EndAll(has ? (hasRing ? "the ring moved" : "a ring was set") : "the ring was removed");
            hasRing = has;
            ring = next;
            ringFrom = has ? from : "none";
            MelonLogger.Msg($"[Earth] Fight: {(has ? $"the ring is {ring} (from {ringFrom})" : enabled ? "no ring: stone hits take a share of real health everywhere" : "no ring ([EarthFight] Enabled is off)")}");
            SendRing();
        }

        static bool Same(in FightRing a, in FightRing b) =>
            a.Centre.X == b.Centre.X && a.Centre.Y == b.Centre.Y && a.Centre.Z == b.Centre.Z && a.Radius == b.Radius && a.ArenaRadius == b.ArenaRadius && a.Drawn == b.Drawn;

        // Every fighter's points go (sent as -1 to every game), and nobody is out.
        static void EndAll(string why) {
            changes.Clear();
            ledger.Clear(changes);
            if (changes.Count > 0) MelonLogger.Msg($"[Earth] Fight: {why}: every fight ends ({changes.Count} fighter(s))");
            Send(changes, why);
        }

        internal static string Describe() => hasRing ? $"{ring} (from {ringFrom})" : enabled ? "none" : "none ([EarthFight] Enabled is off)";

        // ---------------------------------------------------------------- what Hits asks

        /// <summary>Where <paramref name="feet"/> are for the ring now (Outside with no ring).</summary>
        internal static FightWhere Where(Vector3 feet) => hasRing ? ring.Where(new V3(feet.x, feet.y, feet.z)) : FightWhere.Outside;

        /// <summary>Where a player's feet are (<paramref name="feet"/>, as Hits read them) for the ring now.</summary>
        internal static FightWhere Where(Player p, Vector3 feet) => p == null ? FightWhere.Outside : Where(feet);

        /// <summary>Whether the player has fight health now (always false with no ring).</summary>
        internal static bool Fighting(Player p) => hasRing && p != null && ledger.Fighting(Server.AccountOf(p));

        /// <summary>The player's fight health now, -1 for none.</summary>
        internal static int PointsOf(ulong account) => hasRing ? ledger.PointsOf(account) : -1;

        /// <summary>A stone of <paramref name="power"/> hits a fighter: takes it off their fight health and sends every change
        /// (the struck player's first) to every game before the caller sends the knockback.</summary>
        internal static FightHit Hit(Player p, int power) {
            if (!hasRing || p == null) return FightHit.NotFighting;
            changes.Clear();
            FightHit r = ledger.Hit(Server.AccountOf(p), power, changes);
            Send(changes, r == FightHit.KnockedOut ? "knocked out" : "hit");
            return r;
        }

        /// <summary>The finishing hit's knockback: 45 degrees up along the flat way of <paramref name="stoneVelocity"/> (the
        /// flat way of <paramref name="away"/>, from the stone to the player, when it has none; out from the ring's centre
        /// through the feet when neither has one), at FightRules.FlingSpeed for this server's gravity.</summary>
        internal static Vector3 Fling(Vector3 feet, Vector3 stoneVelocity, Vector3 away, out float speed) {
            var f = new V3(feet.x, feet.y, feet.z);
            V3 dir = Flat(stoneVelocity);
            if (dir.X == 0f && dir.Z == 0f) dir = Flat(away);
            speed = FightRules.FlingSpeed(ring, f, dir, Physics.gravity.y, FlingClear, FlingMin, FlingMax);
            V3 v = FightRules.FlingVelocity(ring, f, dir, speed);
            return new Vector3(v.X, v.Y, v.Z);
        }

        static V3 Flat(Vector3 v) {
            if (float.IsNaN(v.x) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.z)) return V3.Zero;
            return v.x * v.x + v.z * v.z > 1e-6f ? new V3(v.x, 0f, v.z) : V3.Zero;
        }

        // ---------------------------------------------------------------- every frame

        /// <summary>Every frame on the server (Main.OnUpdate, before the headless return). See the header.</summary>
        internal static void Tick() {
            if (!ready || !hasRing) {
                if (ready && Time.unscaledTime >= resendAt) {   // no ring: a game that missed the "none" hears it again
                    resendAt = Time.unscaledTime + ResendSeconds;
                    SendRing();
                }
                return;
            }
            try {
                double now = Time.unscaledTimeAsDouble;
                changes.Clear();
                seen.Clear();
                bool joined = false;
                foreach (IPlayer ip in Player.AllPlayers) {
                    if (!(ip is Player p) || p == null || p.UserInfo == null) continue;
                    Connection c = p.ConnectionToRemotePlayer;
                    if (c == null || c.IsDisposed) continue;
                    ulong account = Server.AccountOf(p);
                    if (account == 0UL) continue;
                    if (!seenBefore.Contains(account)) joined = true;
                    PlayerController pc = p.PlayerController;
                    if (pc == null || !pc.IsAlive || pc.IsDowned) continue;   // dead, downed or no body: forgotten below
                    Vector3 feet = pc.RealFeetPosition;
                    if (float.IsNaN(feet.x) || float.IsNaN(feet.y) || float.IsNaN(feet.z)) continue;
                    seen.Add(account);
                    ledger.See(account, ring.Where(new V3(feet.x, feet.y, feet.z)), now, changes);
                }
                known.Clear();
                ledger.Known(known);
                foreach (ulong k in known) if (!seen.Contains(k)) ledger.Forget(k, changes);
                if (changes.Count > 0) Send(changes, "seen");
                // seenBefore: every connected account this frame (alive or not), to notice a join
                seenBefore.Clear();
                foreach (IPlayer ip in Player.AllPlayers)
                    if (ip is Player p && p != null && p.UserInfo != null && p.ConnectionToRemotePlayer != null && !p.ConnectionToRemotePlayer.IsDisposed) seenBefore.Add(Server.AccountOf(p));
                if (joined || Time.unscaledTime >= resendAt) {
                    resendAt = Time.unscaledTime + ResendSeconds;
                    SendRing();
                    changes.Clear();
                    ledger.Fighters(changes);
                    foreach (FightLedger.Change ch in changes) SendOne(ch.Player, ch.Points);
                }
            }
            catch (Exception e) {
                if (!tickFailed) { tickFailed = true; MelonLogger.Warning($"[Earth] Fight: the fight tick failed (said once): {e}"); }
                else if (Logs.Debug) MelonLogger.Msg($"[Earth] Fight: the fight tick failed again: {e.Message}");
            }
        }

        /// <summary>Every fighter now, with their points (the console's `earth ring`).</summary>
        internal static void Fighters(List<FightLedger.Change> into) {
            if (hasRing) ledger.Fighters(into);
        }

        // ---------------------------------------------------------------- sending

        static void Send(List<FightLedger.Change> list, string why) {
            foreach (FightLedger.Change ch in list) {
                if (Logs.Debug) MelonLogger.Msg($"[Earth] Fight: {NameOf(ch.Player)} {(ch.Points < 0 ? "has no fight health" : $"has {ch.Points} of {ledger.Points}")} ({why})");
                SendOne(ch.Player, ch.Points);
            }
        }

        static void SendOne(ulong account, int points) {
            try { Net.SendFight(unchecked((int)(uint)account), points); }
            catch (Exception e) {
                if (!sendFailed) { sendFailed = true; MelonLogger.Warning($"[Earth] Fight: sending fight health failed (said once): {e.Message}"); }
            }
        }

        static void SendRing() {
            try { Net.SendRing(hasRing, ring); }
            catch (Exception e) {
                if (!sendFailed) { sendFailed = true; MelonLogger.Warning($"[Earth] Fight: sending the ring failed (said once): {e.Message}"); }
            }
        }

        /// <summary>An online player's name by account, for a log line ("account N" when not online).</summary>
        internal static string NameOf(ulong account) {
            foreach (IPlayer ip in Player.AllPlayers)
                if (ip is Player p && p != null && p.UserInfo != null && Server.AccountOf(p) == account) return Server.NameOf(p);
            return $"account {account}";
        }
    }
}
