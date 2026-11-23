using System;
using System.Collections.Generic;
using System.Globalization;
using ATT.Saving;
using Alta.Chunks;
using Alta.Networking;
using MelonLoader;
using SyncLib.Items;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Earthbending {
    // The five stones as network prefabs, built in code (no AssetBundle): each kind's template is a GameObject with a Rigidbody,
    // one collider (a box, a sphere for the Ball), an empty child "Model" that StoneLook dresses on games, and a StoneView.
    // The templates sit under one inactive DontDestroyOnLoad holder, so they never simulate or render themselves, while the
    // copies the game makes of them (Instantiate, with no parent) come out active. SyncLib registers each one as
    // "Earth<Kind>" (its CustomPrefabId: never rename one after a server has used it) inside RegisterPrefabs only; it adds
    // NetworkPrefab, NetworkEntity and NetworkRigidbody itself. The [Stones] settings (power, mass, size) replace StoneTable's
    // numbers at start-up and on every reload, and the templates follow, so the next stone spawned has them.
    internal static class Prefabs {
        static MelonMod? mod;
        static GameObject? holder;
        static readonly GameObject?[] templates = new GameObject?[StoneTable.Count];
        static readonly NetworkPrefab?[] registered = new NetworkPrefab?[StoneTable.Count];
        static readonly Dictionary<uint, StoneKind> kindByHash = new Dictionary<uint, StoneKind>();
        static readonly MelonPreferences_Entry<int>[] power = new MelonPreferences_Entry<int>[StoneTable.Count];
        static readonly MelonPreferences_Entry<float>[] mass = new MelonPreferences_Entry<float>[StoneTable.Count];
        static readonly MelonPreferences_Entry<string>[] size = new MelonPreferences_Entry<string>[StoneTable.Count];

        const float MinSize = 0.05f, MaxSize = 20f;

        internal static string Id(StoneKind k) => "Earth" + k;

        /// <summary>From Main.OnInitializeMelon (the only time SyncLib's event may be subscribed).</summary>
        internal static void Init(MelonMod owner) {
            mod = owner;
            MelonPreferences_Category cat = Cfg.Category("Stones");
            for (int i = 0; i < StoneTable.Count; i++) {
                var k = (StoneKind)i;
                StoneStats d = StoneTable.Default(k);
                power[i] = cat.CreateEntry(k + "Power", d.Power, description: $"Server. The {k}'s power for hits and clashes (spec 3.4; grounded and moving add to it).");
                mass[i] = cat.CreateEntry(k + "Mass", d.Mass, description: $"Server. The {k}'s mass in kg.");
                size[i] = cat.CreateEntry(k + "Size", SizeText(d.Size), description: $"The {k}'s collider in metres, \"x, y, z\" ({(k == StoneKind.Ball ? "the ball's diameter, on x" : "across, up, ahead")}). Every game builds the stone from its own value, so keep it the same on the server and every game.");
            }
            Apply();
            Cfg.Changed += Apply;
            NetworkPrefabRegistry.RegisterPrefabs += OnRegisterPrefabs;
        }

        /// <summary>The registered prefab, or null before registration or on a server without this mod.</summary>
        internal static NetworkPrefab? Of(StoneKind k) => StoneTable.Valid(k) ? registered[(int)k] : null;

        /// <summary>Which stone an entity is, by its prefab's hash; null when it is not one.</summary>
        internal static StoneKind? KindOf(NetworkEntity? e) {
            NetworkPrefab? p = e != null ? e.Prefab : null;
            return p != null && kindByHash.TryGetValue(p.Hash, out StoneKind k) ? k : (StoneKind?)null;
        }

        // ---------------------------------------------------------------- settings

        static void Apply() {
            for (int i = 0; i < StoneTable.Count; i++) {
                var k = (StoneKind)i;
                StoneStats d = StoneTable.Default(k), s = d;
                if (power[i].Value >= 0) s.Power = power[i].Value;
                else MelonLogger.Warning($"[Earth] Settings: [Stones] {k}Power {power[i].Value} is below 0; using {d.Power}");
                float m = mass[i].Value;
                if (m > 0f && !float.IsInfinity(m)) s.Mass = m;
                else MelonLogger.Warning($"[Earth] Settings: [Stones] {k}Mass {m} is not a mass; using {d.Mass}");
                if (TryParseSize(size[i].Value, out V3 v)) s.Size = k == StoneKind.Ball ? new V3(v.X, v.X, v.X) : v;
                else MelonLogger.Warning($"[Earth] Settings: [Stones] {k}Size '{size[i].Value}' is not three sizes from {MinSize} to {MaxSize} m (\"x, y, z\"); using {SizeText(d.Size)}");
                StoneTable.Set(k, s);
            }
            Fit();
        }

        static string SizeText(V3 v) => string.Format(CultureInfo.InvariantCulture, "{0:0.###}, {1:0.###}, {2:0.###}", v.X, v.Y, v.Z);

        static bool TryParseSize(string? text, out V3 v) {
            v = V3.Zero;
            string[] parts = (text ?? "").Split(',');
            if (parts.Length != 3) return false;
            var f = new float[3];
            for (int i = 0; i < 3; i++)
                if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out f[i]) || !(f[i] >= MinSize && f[i] <= MaxSize)) return false;
            v = new V3(f[0], f[1], f[2]);
            return true;
        }

        // ---------------------------------------------------------------- templates and registration

        static void OnRegisterPrefabs() {
            if (mod == null) return;
            BuildTemplates();
            int done = 0;
            var names = new List<string>(StoneTable.Count);
            for (int i = 0; i < StoneTable.Count; i++) {
                var k = (StoneKind)i;
                GameObject? t = templates[i];
                if (t == null) continue;
                NetworkPrefab? p = mod.RegisterCustomPrefab(t, Id(k));   // null on a game whose server does not run this mod
                if (p == null) continue;
                registered[i] = p;
                kindByHash[p.Hash] = k;
                names.Add($"{Id(k)} {p.Hash}");
                done++;
            }
            if (done == StoneTable.Count) MelonLogger.Msg($"[Earth] Stones: {done} stone prefabs registered with SyncLib ({string.Join(", ", names.ToArray())})");
            else if (done == 0 && !Main.IsServer) MelonLogger.Msg("[Earth] Stones: this server does not run Blue's Earthbending, so there are no stones here");
            else MelonLogger.Warning($"[Earth] Stones: only {done} of {StoneTable.Count} stone prefabs registered ({string.Join(", ", names.ToArray())}); read SyncLib's lines above");
        }

        static void BuildTemplates() {
            if (holder != null) return;
            holder = new GameObject("Earthbending stone templates");
            holder.SetActive(false);
            Object.DontDestroyOnLoad(holder);
            for (int i = 0; i < StoneTable.Count; i++) {
                var k = (StoneKind)i;
                var t = new GameObject(Id(k));
                t.transform.SetParent(holder.transform, false);   // under the inactive holder before any component is added
                Rigidbody rb = t.AddComponent<Rigidbody>();
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                if (k == StoneKind.Ball) t.AddComponent<SphereCollider>();
                else t.AddComponent<BoxCollider>();
                new GameObject("Model").transform.SetParent(t.transform, false);
                t.AddComponent<StoneView>();
                templates[i] = t;
            }
            Fit();
        }

        // The templates' mass and collider from the table (only once they exist; stones already standing keep theirs).
        static void Fit() {
            for (int i = 0; i < StoneTable.Count; i++) {
                GameObject? t = templates[i];
                if (t == null) continue;
                StoneStats s = StoneTable.Of((StoneKind)i);
                t.GetComponent<Rigidbody>().mass = s.Mass;
                SphereCollider sphere = t.GetComponent<SphereCollider>();
                if (sphere != null) sphere.radius = s.Size.X * 0.5f;
                BoxCollider box = t.GetComponent<BoxCollider>();
                if (box != null) box.size = new Vector3(s.Size.X, s.Size.Y, s.Size.Z);
            }
        }
    }

    // Server: spawning a stone. A stub for the console's `earth spawn` until Task 6 moves it to Server.cs with the registry.
    internal static class Server {
        /// <summary>Spawns a stone at <paramref name="pos"/> (its centre), never saved (SaveMode.Ignore before it is chunked).
        /// Calls <paramref name="done"/> with the new entity and returns it; null (and a warning) when it cannot.</summary>
        internal static NetworkEntity? Spawn(StoneKind kind, Vector3 pos, Quaternion rot, Player? owner, Action<NetworkEntity>? done) {
            if (!Main.IsServer) { MelonLogger.Warning($"[Earth] Spawn: refused, {kind} asked for on a game (only the server spawns stones)"); return null; }
            NetworkPrefab? prefab = Prefabs.Of(kind);
            if (prefab == null) { MelonLogger.Warning($"[Earth] Spawn: refused, no registered prefab for {kind}"); return null; }
            LocationChunk chunk = LocationChunkHelper.GetBestContaining(pos);
            if (chunk == null) { MelonLogger.Warning($"[Earth] Spawn: refused, no chunk holds {pos} for the {kind}"); return null; }
            NetworkEntity e = SpawnHelper.Spawn(prefab, SpawnData.Default, chunk, pos, rot,
                x => { if (x != null && x.Prefab != null) x.Prefab.SaveMode = SaveMode.Ignore; });   // never in the world save
            if (e == null) { MelonLogger.Warning($"[Earth] Spawn: the game did not spawn the {kind} at {pos}"); return null; }
            if (Logs.Debug) MelonLogger.Msg($"[Earth] Spawn: {kind} #{e.Identifier} at {pos} for {(owner?.UserInfo != null ? owner.UserInfo.Username : "nobody")}");
            done?.Invoke(e);
            return e;
        }
    }
}
