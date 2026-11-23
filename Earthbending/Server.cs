using System;
using System.Collections.Generic;
using ATT.Saving;
using Alta.Chunks;
using Alta.Networking;
using Alta.Networking.Scripts.Player;
using MelonLoader;
using UnityEngine;

namespace Earthbending {
    // The dedicated server: Summon claims in, the checks, the spawn, the registry of standing stones and their life (spec 3.2,
    // 3.4). The pose was judged on the player's own game (Bend.cs); the server checks what it can (Stones.cs's SummonCheck):
    //   1. the claim is one a game of this mod sends (ruling 13), and its kind is in [EarthStones] Enabled;
    //   2. the player's cooldown for that stone (Ledger): the game's own structure cooldown (0.3 s) less
    //      [EarthStones] CooldownSlackSeconds, because two claims fired 0.3 s apart on the game can be read here closer together
    //      (the network's jitter, and the server reads messages once a frame at 15-45 Hz);
    //   3. the claimed head within [EarthStones] HeadSlackMetres of the server's copy of the head (PlayerController.Head);
    //   4. natural ground under the spot SpawnPlan picks (Ground.Under, cast down from the claimed head's height), cast only
    //      for a claim that passed 1-3.
    // The head and yaw the stone is placed from are the claim's (what the player saw), once within the slack. Net raises the
    // claim on the game's thread (the archery mod's message handlers run there too), so the spawn happens in the handler. A
    // refusal is one Debug line naming the reason. Then the caps (Caps.Decide: the player's oldest breaks, or a full server
    // refuses), and the stone is spawned at the plan's start, its top at ground level, kinematic and Rising; StoneBody raises it.
    // Every stone is SaveMode.Ignore. Each frame Tick breaks a stone that lived [EarthStones] LifeSeconds, fell below the world,
    // or whose owner is no longer connected (StoneLife); the server's quit breaks them all (Shutdown).
    internal static class Server {
        const float DefaultLife = 60f, DefaultHeadSlack = 1f, DefaultCooldownSlack = 0.1f;
        const int DefaultMaxPerPlayer = 8, DefaultMaxTotal = 64;
        const string DefaultEnabled = "Pillar";

        /// <summary>Every standing stone the server spawned, by its entity's Identifier.</summary>
        internal static readonly Dictionary<uint, StoneBody> Stones = new Dictionary<uint, StoneBody>();

        static readonly Ledger ledger = new Ledger();
        static readonly PoseValues poseDefaults = PoseValues.Default;   // the game's own cooldowns (PoseValues.Default allocates)
        static readonly HashSet<ulong> online = new HashSet<ulong>();   // reused by Tick
        static readonly List<StoneBody> due = new List<StoneBody>();
        static readonly List<LifeEnd> dueWhy = new List<LifeEnd>();
        static readonly List<uint> gone = new List<uint>();

        static MelonPreferences_Entry<string>? enabledEntry;
        static MelonPreferences_Entry<int>? maxPerPlayerEntry, maxTotalEntry;
        static MelonPreferences_Entry<float>? lifeEntry, headSlackEntry, cooldownSlackEntry;
        static readonly MelonPreferences_Entry<float>[] aheadEntries = new MelonPreferences_Entry<float>[StoneTable.Count];
        static readonly MelonPreferences_Entry<float>[] riseEntries = new MelonPreferences_Entry<float>[StoneTable.Count];

        static bool[] enabled = new bool[StoneTable.Count];
        static int maxPerPlayer = DefaultMaxPerPlayer, maxTotal = DefaultMaxTotal;
        static float life = DefaultLife, headSlack = DefaultHeadSlack, cooldownSlack = DefaultCooldownSlack;
        static bool shutDown, tickFailed, summonFailed;

        /// <summary>From Main.OnInitializeMelon on the dedicated server only, after Prefabs.Init (which made [EarthStones]).</summary>
        internal static void Init() {
            MelonPreferences_Category cat = Cfg.Category("EarthStones");
            enabledEntry = cat.CreateEntry("Enabled", DefaultEnabled, description: "Server. The stones a pose may summon, by name, split by commas: Disc, Ball, Pillar, Cube, Wall. A claim for any other is refused (KindOff).");
            maxPerPlayerEntry = cat.CreateEntry("MaxPerPlayer", DefaultMaxPerPlayer, description: "Server. The most stones one player has standing; summoning one more breaks their oldest. 1 or more.");
            maxTotalEntry = cat.CreateEntry("MaxTotal", DefaultMaxTotal, description: "Server. The most stones standing on the server; a summon past it is refused (unless that player is at MaxPerPlayer: then their oldest breaks). 1 or more.");
            lifeEntry = cat.CreateEntry("LifeSeconds", DefaultLife, description: "Server. A stone breaks this many seconds after it was summoned. Above 0.");
            headSlackEntry = cat.CreateEntry("HeadSlackMetres", DefaultHeadSlack, description: "Server. A summon is refused (HeadFar) when the head its game claims is further than this from the server's copy of that player's head, in metres (the server's copy lags; a fast run needs more). 0 or more.");
            cooldownSlackEntry = cat.CreateEntry("CooldownSlackSeconds", DefaultCooldownSlack, description: "Server. A player's summons of one stone may come this much closer together than the game's own cooldown (0.3 s) before the server refuses one (Cooldown): two claims sent 0.3 s apart can arrive closer. 0 or more.");
            for (int i = 0; i < StoneTable.Count; i++) {
                var k = (StoneKind)i;
                aheadEntries[i] = cat.CreateEntry(k + "AheadMetres", SpawnPlan.DefaultAhead(k), description: $"Server. A summoned {k} rises this many metres in front of the head (spec 3.4). 0 or more.");
                riseEntries[i] = cat.CreateEntry(k + "RiseSeconds", SpawnPlan.DefaultRise(k), description: $"Server. A summoned {k} rises out of the ground over this many seconds (0: it stands at once). 0 or more.");
            }
            Apply();
            Cfg.Changed += Apply;
            // a Net whose static initialiser failed (Main says so) throws on every touch: no summons, the rest still starts
            try { Net.SummonReceived += OnSummon; }
            catch (Exception e) { MelonLogger.Warning($"[Earth] Stones: no summons (message 19 is not set up: {e.GetType().Name})"); }
            Player.OnPlayerRemoved += OnPlayerRemoved;
            // closing the server's window skips every mod's OnApplicationQuit (the archery mod's notes): quit from both
            try { MelonEvents.OnApplicationDefiniteQuit.Subscribe(Shutdown); }
            catch (Exception e) { MelonLogger.Warning($"[Earth] Stones: could not hook the server's quit ({e.Message}); stones are still never saved"); }
        }

        static void Apply() {
            enabled = StoneKinds.Parse(enabledEntry?.Value, out string unknown);
            if (unknown.Length > 0) MelonLogger.Warning($"[Earth] Settings: [EarthStones] Enabled names no stone '{unknown}' (the stones are Disc, Ball, Pillar, Cube, Wall); ignored");
            maxPerPlayer = AtLeastOne(maxPerPlayerEntry, "MaxPerPlayer", DefaultMaxPerPlayer);
            maxTotal = AtLeastOne(maxTotalEntry, "MaxTotal", DefaultMaxTotal);
            life = Cfg.Positive(lifeEntry, "EarthStones", "LifeSeconds", DefaultLife);
            headSlack = Cfg.NotNegative(headSlackEntry, "EarthStones", "HeadSlackMetres", DefaultHeadSlack);
            cooldownSlack = Cfg.NotNegative(cooldownSlackEntry, "EarthStones", "CooldownSlackSeconds", DefaultCooldownSlack);
            for (int i = 0; i < StoneTable.Count; i++) {
                var k = (StoneKind)i;
                SpawnPlan.Set(k, Cfg.NotNegative(aheadEntries[i], "EarthStones", k + "AheadMetres", SpawnPlan.DefaultAhead(k)),
                    Cfg.NotNegative(riseEntries[i], "EarthStones", k + "RiseSeconds", SpawnPlan.DefaultRise(k)));
            }
            var on = new List<string>();
            for (int i = 0; i < StoneTable.Count; i++) if (enabled[i]) on.Add(((StoneKind)i).ToString());
            MelonLogger.Msg($"[Earth] Stones: summons of {(on.Count == 0 ? "no stone" : string.Join(", ", on.ToArray()))}; at most {maxPerPlayer} a player and {maxTotal} in all, each for {life:0.#} s");
        }

        static int AtLeastOne(MelonPreferences_Entry<int>? e, string key, int fallback) {
            if (e == null) return fallback;
            if (e.Value >= 1) return e.Value;
            MelonLogger.Warning($"[Earth] Settings: [EarthStones] {key} {e.Value} is not 1 or more; using {fallback}");
            return fallback;
        }

        // ---------------------------------------------------------------- a Summon claim

        static void OnSummon(Player p, SummonClaim c) {
            if (shutDown || p == null) return;
            try { Summon(p, c); }
            catch (Exception e) {
                if (!summonFailed) { summonFailed = true; MelonLogger.Warning($"[Earth] Stones: a summon failed (said once): {e}"); }
                else if (Logs.Debug) MelonLogger.Msg($"[Earth] Stones: a summon failed again: {e.Message}");
            }
        }

        static void Summon(Player p, SummonClaim c) {
            PlayerController pc = p.PlayerController;
            Transform? headT = pc != null ? pc.Head : null;
            if (p.UserInfo == null || headT == null) { Refused(p, c, SummonRefusal.NoPlayer, null, ""); return; }
            ulong account = AccountOf(p);
            V3 serverHead = V(headT.position);

            // Every check but the ground first; the ground is cast only for a claim that passes them (a sane claim whose head is
            // the server's within the slack). Only a claim for a kind that is on spends its cooldown; one refused after that (its
            // head, its ground) has still spent it, as the game's own fired pose has.
            bool sane = SummonCheck.Sane(in c), kindOn = sane && enabled[(int)c.Kind];
            bool cooldownOk = kindOn && ledger.TrySpend(account, (int)SummonCheck.MoveOf(c.Kind), Time.unscaledTimeAsDouble, CooldownOf(c.Kind));
            SummonRefusal why = SummonCheck.Decide(in c, serverHead, true, kindOn, cooldownOk, headSlack);
            string ground = "";
            (V3 start, V3 end, float riseSeconds, float yawDeg) plan = default;
            if (why == SummonRefusal.None) {
                V3 spot = SpawnPlan.For(c.Kind, c.Head, c.Yaw, 0f).end;   // where it would stand; its height comes from the cast
                bool natural = Ground.Under(new Vector3(spot.X, c.Head.Y, spot.Z), out RaycastHit hit);
                if (hit.collider != null) {
                    plan = SpawnPlan.For(c.Kind, c.Head, c.Yaw, hit.point.y);
                    if (Logs.Debug) ground = $", ground {hit.collider.name} (layer {hit.collider.gameObject.layer}) at {hit.point.y:0.##}";
                }
                else {
                    natural = false;
                    if (Logs.Debug) ground = $", no ground within {Ground.Depth:0.#} m under the spot";
                }
                why = SummonCheck.Decide(in c, serverHead, natural, kindOn, cooldownOk, headSlack);
            }
            if (why != SummonRefusal.None) { Refused(p, c, why, serverHead, ground); return; }

            // the caps: this player's oldest makes room, or a full server refuses
            List<StoneBody> owned = OwnedBy(account);
            for (int guard = 0; ; guard++) {
                CapAction a = Caps.Decide(owned.Count, Stones.Count, maxPerPlayer, maxTotal);
                if (a == CapAction.Spawn) break;
                if (a == CapAction.Refuse || owned.Count == 0 || guard > maxTotal) {
                    if (Logs.Debug) MelonLogger.Msg($"[Earth] Summon: refused {c.Kind} for {NameOf(p)}: the server has {Stones.Count} stones standing ([EarthStones] MaxTotal {maxTotal})");
                    return;
                }
                StoneBody oldest = owned[0];
                owned.RemoveAt(0);
                oldest.Break("its owner summoned one more at [EarthStones] MaxPerPlayer");
            }

            StoneBody? b = Spawn(c.Kind, U(plan.start), Quaternion.Euler(0f, plan.yawDeg, 0f), p, StoneState.Rising, U(plan.end), plan.riseSeconds);
            if (b != null && Logs.Debug)
                MelonLogger.Msg($"[Earth] Summon: {c.Kind} #{b.Id} for {b.OwnerName}, rising from {plan.start} to {plan.end} over {plan.riseSeconds:0.##} s (heads {(c.Head - serverHead).Length:0.##} m apart{ground}); {owned.Count + 1} of theirs, {Stones.Count} in all");
        }

        // One Debug line; nothing is built without [EarthLog] Debug.
        static void Refused(Player p, in SummonClaim c, SummonRefusal why, V3? serverHead, string ground) {
            if (!Logs.Debug) return;
            string kind = StoneTable.Valid(c.Kind) ? c.Kind.ToString() : "kind " + (byte)c.Kind;
            string heads = serverHead is V3 h ? $" (claimed head {c.Head}, the server's {h}, {(c.Head - h).Length:0.##} m apart{ground})" : " (no body on the server)";
            MelonLogger.Msg($"[Earth] Summon: refused {kind} for {NameOf(p)}: {why}{heads}");
        }

        static float CooldownOf(StoneKind k) => Math.Max(0f, poseDefaults.CooldownOf(SummonCheck.MoveOf(k)) - cooldownSlack);

        static void OnPlayerRemoved(Player p) {
            try { if (p != null && p.UserInfo != null) ledger.Forget(AccountOf(p)); }
            catch (Exception e) { if (Logs.Debug) MelonLogger.Msg($"[Earth] Stones: forgetting a leaving player's cooldowns failed: {e.Message}"); }
        }

        // ---------------------------------------------------------------- spawning and the registry

        /// <summary>Server: spawns a stone with its centre at <paramref name="at"/>, never saved (SaveMode.Ignore before it is
        /// chunked), its rigidbody kinematic when the state is (StoneRules.Kinematic), and registers its StoneBody. A Rising stone
        /// is moved to <paramref name="end"/> over <paramref name="riseSeconds"/>. <paramref name="owner"/> may be null (no
        /// owner: it never breaks for one leaving). Null (and a warning) when it cannot. No caps: the caller's.</summary>
        internal static StoneBody? Spawn(StoneKind kind, Vector3 at, Quaternion rot, Player? owner, StoneState state, Vector3 end, float riseSeconds) {
            if (!Main.IsServer) { MelonLogger.Warning($"[Earth] Spawn: refused, {kind} asked for on a game (only the server spawns stones)"); return null; }
            if (shutDown) return null;
            NetworkPrefab? prefab = Prefabs.Of(kind);
            if (prefab == null) { MelonLogger.Warning($"[Earth] Spawn: refused, no registered prefab for {kind}"); return null; }
            LocationChunk chunk = LocationChunkHelper.GetBestContaining(end);   // the end: the start may be under the chunk's ground
            if (chunk == null) { MelonLogger.Warning($"[Earth] Spawn: refused, no chunk holds {end} for the {kind}"); return null; }
            bool ignored = false, kinematic = StoneRules.Kinematic(state);
            NetworkEntity e = SpawnHelper.Spawn(prefab, SpawnData.Default, chunk, at, rot, x => {
                // never in the world save; the copy's own NetworkPrefab, not NetworkEntity.Prefab (see Prefabs.KindOf). Kinematic
                // before it is chunked, so the first state the games get is already the stone's
                NetworkPrefab? np = x != null ? x.GetComponent<NetworkPrefab>() : null;
                if (np != null) { np.SaveMode = SaveMode.Ignore; ignored = true; }
                Rigidbody? rb = x != null ? x.GetComponent<Rigidbody>() : null;
                if (rb != null) rb.isKinematic = kinematic;
            });
            if (e == null) { MelonLogger.Warning($"[Earth] Spawn: the game did not spawn the {kind} at {at}"); return null; }
            if (!ignored) {
                MelonLogger.Warning($"[Earth] Spawn: the {kind} #{e.Identifier} has no NetworkPrefab to mark unsaved; destroyed");
                e.SceneDestroy();
                return null;
            }
            StoneBody b = e.gameObject.AddComponent<StoneBody>();
            b.Init(e, kind, owner != null ? AccountOf(owner) : 0UL, owner != null ? NameOf(owner) : "nobody", state, at, end, riseSeconds, Time.unscaledTimeAsDouble);
            Stones[b.Id] = b;
            if (Logs.Debug) MelonLogger.Msg($"[Earth] Spawn: {kind} #{b.Id} at {at} ({state}) for {b.OwnerName}");
            return b;
        }

        /// <summary>From StoneBody (its Break, or its destroy by anything else): it no longer stands.</summary>
        internal static void Forget(StoneBody b) {
            if (b != null && Stones.TryGetValue(b.Id, out StoneBody known) && ReferenceEquals(known, b)) Stones.Remove(b.Id);
        }

        /// <summary>Every standing stone of this player, oldest first.</summary>
        internal static List<StoneBody> OwnedBy(ulong account) {
            var list = new List<StoneBody>();
            foreach (StoneBody b in Stones.Values) if (b != null && b.Owner == account) list.Add(b);
            list.Sort((x, y) => x.Born != y.Born ? x.Born.CompareTo(y.Born) : x.Id.CompareTo(y.Id));
            return list;
        }

        // ---------------------------------------------------------------- every frame, and the quit

        /// <summary>Every frame on the server (Main.OnUpdate, before the headless return): breaks every stone StoneLife says
        /// should go. A player is online while their Player is in the game's list with a connection that is not disposed.</summary>
        internal static void Tick() {
            if (shutDown || Stones.Count == 0) return;
            try {
                online.Clear();
                foreach (IPlayer ip in Player.AllPlayers) {
                    if (!(ip is Player p) || p == null || p.UserInfo == null) continue;
                    Connection c = p.ConnectionToRemotePlayer;
                    if (c != null && !c.IsDisposed) online.Add(AccountOf(p));
                }
                double now = Time.unscaledTimeAsDouble;
                due.Clear();
                dueWhy.Clear();
                gone.Clear();
                foreach (KeyValuePair<uint, StoneBody> kv in Stones) {
                    StoneBody b = kv.Value;
                    if (b == null) { gone.Add(kv.Key); continue; }   // destroyed without its OnDestroy (should not happen)
                    LifeEnd why = StoneLife.Why((float)(now - b.Born), life, b.transform.position.y, b.Owner == 0UL || online.Contains(b.Owner));
                    if (why == LifeEnd.None) continue;
                    due.Add(b);
                    dueWhy.Add(why);
                }
                foreach (uint id in gone) Stones.Remove(id);
                for (int i = 0; i < due.Count; i++)
                    due[i].Break(dueWhy[i] == LifeEnd.Old ? "it lived [EarthStones] LifeSeconds" : dueWhy[i] == LifeEnd.Void ? "it fell out of the world" : "its owner left");
                due.Clear();
            }
            catch (Exception e) {
                if (!tickFailed) { tickFailed = true; MelonLogger.Warning($"[Earth] Stones: the stones' tick failed (said once): {e}"); }
            }
        }

        /// <summary>At the server's quit (Main.OnApplicationQuit, and MelonLoader's definite quit): every stone breaks, and none
        /// is spawned after. Nothing is ever saved anyway (SaveMode.Ignore).</summary>
        internal static void Shutdown() {
            if (shutDown) return;
            shutDown = true;
            try {
                var all = new List<StoneBody>(Stones.Values);
                foreach (StoneBody b in all) if (b != null) b.Break("the server quits");
                Stones.Clear();
                if (all.Count > 0) MelonLogger.Msg($"[Earth] Stones: broke {all.Count} stone(s) at the server's quit");
            }
            catch (Exception e) { MelonLogger.Warning($"[Earth] Stones: breaking the stones at the quit failed: {e.Message}"); }
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>A player's account (UserInfo.Identifier, an int) as the ledger's and the owners' key; 0 with no user record.</summary>
        internal static ulong AccountOf(Player p) => p != null && p.UserInfo != null ? unchecked((ulong)(uint)p.UserInfo.Identifier) : 0UL;

        static string NameOf(Player p) {
            string t = p != null && p.UserInfo != null ? p.UserInfo.Username ?? "" : "";
            if (t.Length == 0) return "?";
            var chars = new char[Math.Min(t.Length, 40)];
            for (int i = 0; i < chars.Length; i++) chars[i] = t[i] >= ' ' && t[i] < 127 ? t[i] : '?';
            return new string(chars);
        }

        static V3 V(Vector3 v) => new V3(v.x, v.y, v.z);
        static Vector3 U(V3 v) => new Vector3(v.X, v.Y, v.Z);
    }
}
