using System;
using System.Collections.Generic;
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
    // NetworkPrefab, NetworkEntity and NetworkRigidbody itself. The [EarthStones] settings (power and mass, server only) replace
    // StoneTable's numbers at start-up and on every reload, and the templates' mass follows, so the next stone spawned has it.
    // Sizes are not settings: every machine builds the colliders, so they are StoneTable's constants.
    internal static class Prefabs {
        static MelonMod? mod;
        static GameObject? holder;
        static readonly GameObject?[] templates = new GameObject?[StoneTable.Count];
        static readonly NetworkPrefab?[] registered = new NetworkPrefab?[StoneTable.Count];
        static readonly MelonPreferences_Entry<int>[] power = new MelonPreferences_Entry<int>[StoneTable.Count];
        static readonly MelonPreferences_Entry<float>[] mass = new MelonPreferences_Entry<float>[StoneTable.Count];

        internal static string Id(StoneKind k) => "Earth" + k;

        /// <summary>From Main.OnInitializeMelon (the only time SyncLib's event may be subscribed).</summary>
        internal static void Init(MelonMod owner) {
            mod = owner;
            MelonPreferences_Category cat = Cfg.Category("EarthStones");
            for (int i = 0; i < StoneTable.Count; i++) {
                var k = (StoneKind)i;
                StoneStats d = StoneTable.Default(k);
                power[i] = cat.CreateEntry(k + "Power", d.Power, description: $"Server. The {k}'s power for hits and clashes (spec 3.4; still or sliding on the ground, and moving, each add 1).");
                mass[i] = cat.CreateEntry(k + "Mass", d.Mass, description: $"Server. The {k}'s mass in kg.");
            }
            Apply();
            Cfg.Changed += Apply;
            NetworkPrefabRegistry.RegisterPrefabs += OnRegisterPrefabs;
        }

        /// <summary>The registered prefab, or null before registration or on a server without this mod.</summary>
        internal static NetworkPrefab? Of(StoneKind k) => StoneTable.Valid(k) ? registered[(int)k] : null;

        /// <summary>Which stone an entity is, from its StoneView (the kind is a serialized field set on the template, so every
        /// copy carries it); null when it has none. Never through <c>NetworkEntity.Prefab</c>: that field is filled by the
        /// game's prefab-apply step, which may not have run on a component SyncLib adds at run time.</summary>
        internal static StoneKind? KindOf(NetworkEntity? e) {
            StoneView? v = e != null ? e.GetComponent<StoneView>() : null;
            return v != null ? v.Kind : (StoneKind?)null;
        }

        // ---------------------------------------------------------------- settings

        static void Apply() {
            for (int i = 0; i < StoneTable.Count; i++) {
                var k = (StoneKind)i;
                StoneStats d = StoneTable.Default(k);
                int p = power[i].Value;
                if (p < 0) { MelonLogger.Warning($"[Earth] Settings: [EarthStones] {k}Power {p} is below 0; using {d.Power}"); p = d.Power; }
                float m = mass[i].Value;
                if (!(m > 0f) || float.IsInfinity(m)) { MelonLogger.Warning($"[Earth] Settings: [EarthStones] {k}Mass {m} is not a mass; using {d.Mass}"); m = d.Mass; }
                StoneTable.Set(k, p, m);
            }
            Fit();
        }

        // ---------------------------------------------------------------- templates and registration

        static void OnRegisterPrefabs() {
            // every registration is one server's: a game that joins a second server never keeps the first one's prefabs (or
            // their hashes), so on a server without this mod Of stays null and nothing is claimed
            Array.Clear(registered, 0, registered.Length);
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
                t.AddComponent<StoneView>().Kind = k;   // copied onto every Instantiate copy with the template's other serialized fields
                templates[i] = t;
            }
            Fit();
        }

        // The templates' mass (a setting) and collider (a constant) from the table, once they exist; stones already standing keep theirs.
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
}
