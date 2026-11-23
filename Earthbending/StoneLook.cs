using System;
using System.Collections.Generic;
using Alta.Networking;
using MelonLoader;
using UnityEngine;

namespace Earthbending {
    // Games only: how a stone looks. Each copy of a stone prefab carries a StoneView, which dresses the copy's empty "Model"
    // child once it appears: a MeshFilter and a MeshRenderer with the game's own mesh and material, shared and never changed.
    // The Ball and the Disc wear the network prefab "Stone Boulder 01"'s boulder mesh, fitted to their collider (the Disc
    // squashed flat); the Pillar, Cube and Wall a chamfered block (StoneBox) of their collider's size in that boulder's
    // material, mapped onto the part of the texture the boulder uses. Task 10 picks the real looks and makes them settings.
    //
    // The rise and the break (Task 6, [EarthLooks], per game): a stone that appears (StoneView.Start) plays RiseEffect and
    // RiseSound at its top, which a summoned stone has at ground level as it appears (SpawnPlan: it starts lowered by its own
    // height), so the dirt sprays where it comes out (a game that joins later sees the spray at an old stone's top: accepted);
    // the server's Applied(Broke) plays BreakEffect and BreakSound where the stone broke, and (Task 11) its Applied(Stomp), a
    // stomped stone landing, plays StompEffect and StompSound where it landed. Each is the game's own pooled effect or sound
    // set, by name (several names split by ; are tried in order, the first loaded one is used). One that is not loaded is one
    // warning and nothing is played; it is looked for again every 10 s without a warning.
    //
    // Task 12 (rulings C11, C12): the server's Applied(Explode), a stone marked by Explode, plays ExplodeMarkEffect once at the
    // stone (no sound; a lasting crack look is Task 10's), and its Applied(Exploded), a marked stone's blast, plays
    // ExplodeEffect and ExplodeSound at the blast's centre (as well as the Broke look the same stone's break sends).
    internal static class StoneLook {
        const string BoulderPrefab = "Stone Boulder 01";
        const float BlockBevel = 0.05f;
        const string DefaultRiseEffect = "Digging Dirt Spray Effect;Digging Dirt Smoke Puff Effect", DefaultRiseSound = "Digging DirtFall Sound Set";
        const string DefaultBreakEffect = "Destroyed Rock", DefaultBreakSound = "Rock Destroyed Sounds;Boulder Breaking Sounds";
        const string DefaultStompEffect = "Turabada Ground Slam Effect", DefaultStompSound = "Heavy Turabada Step Sounds";
        const string DefaultExplodeMarkEffect = "Mining Successful", DefaultExplodeEffect = "Shockwave Impact", DefaultExplodeSound = "Shockwave Sounds";

        static bool searched, warnedModel, riseFailed, breakFailed, stompFailed, markFailed, explodeFailed;
        static Mesh? boulderMesh;
        static Material[] boulderMaterials = new Material[0];
        static UvRect boulderUv = new UvRect(0f, 0f, 1f, 1f);
        static readonly Dictionary<Vector3, Mesh> blocks = new Dictionary<Vector3, Mesh>();

        static MelonPreferences_Entry<string>? riseEffectEntry, riseSoundEntry, breakEffectEntry, breakSoundEntry, stompEffectEntry, stompSoundEntry;
        static MelonPreferences_Entry<string>? explodeMarkEffectEntry, explodeEffectEntry, explodeSoundEntry;
        static readonly LookAsset<PooledObjectDefinition> riseEffect = new LookAsset<PooledObjectDefinition>("RiseEffect", "effect", EffectNamed);
        static readonly LookAsset<PooledObjectDefinition> breakEffect = new LookAsset<PooledObjectDefinition>("BreakEffect", "effect", EffectNamed);
        static readonly LookAsset<PooledObjectDefinition> stompEffect = new LookAsset<PooledObjectDefinition>("StompEffect", "effect", EffectNamed);
        static readonly LookAsset<PooledObjectDefinition> explodeMarkEffect = new LookAsset<PooledObjectDefinition>("ExplodeMarkEffect", "effect", EffectNamed);
        static readonly LookAsset<PooledObjectDefinition> explodeEffect = new LookAsset<PooledObjectDefinition>("ExplodeEffect", "effect", EffectNamed);
        static readonly LookAsset<SoundEffectSet> riseSound = new LookAsset<SoundEffectSet>("RiseSound", "sound set", SoundNamed);
        static readonly LookAsset<SoundEffectSet> breakSound = new LookAsset<SoundEffectSet>("BreakSound", "sound set", SoundNamed);
        static readonly LookAsset<SoundEffectSet> stompSound = new LookAsset<SoundEffectSet>("StompSound", "sound set", SoundNamed);
        static readonly LookAsset<SoundEffectSet> explodeSound = new LookAsset<SoundEffectSet>("ExplodeSound", "sound set", SoundNamed);

        // a pooled effect is named as its definition or as its prefab; a sound set as itself
        static bool EffectNamed(PooledObjectDefinition d, string n) => d.name == n || (d.Prefab != null && d.Prefab.name == n);
        static bool SoundNamed(SoundEffectSet s, string n) => s.name == n;

        /// <summary>From Main.OnInitializeMelon on games: the [EarthLooks] rise, break, stomp and explode settings, and the
        /// server's Applied events.</summary>
        internal static void Init() {
            MelonPreferences_Category looks = Cfg.Category("EarthLooks");
            riseEffectEntry = looks.CreateEntry("RiseEffect", DefaultRiseEffect, description: "Per game. The game's pooled effect played where a stone comes out of the ground (its name; several names split by ; are tried in order, the first loaded one is used; empty: none).");
            riseSoundEntry = looks.CreateEntry("RiseSound", DefaultRiseSound, description: "Per game. The game's sound set played where a stone comes out of the ground (names as RiseEffect).");
            breakEffectEntry = looks.CreateEntry("BreakEffect", DefaultBreakEffect, description: "Per game. The game's pooled effect played where a stone breaks (names as RiseEffect).");
            breakSoundEntry = looks.CreateEntry("BreakSound", DefaultBreakSound, description: "Per game. The game's sound set played where a stone breaks (names as RiseEffect).");
            stompEffectEntry = looks.CreateEntry("StompEffect", DefaultStompEffect, description: "Per game. The game's pooled effect played where a stomped stone lands (names as RiseEffect).");
            stompSoundEntry = looks.CreateEntry("StompSound", DefaultStompSound, description: "Per game. The game's sound set played where a stomped stone lands (names as RiseEffect).");
            explodeMarkEffectEntry = looks.CreateEntry("ExplodeMarkEffect", DefaultExplodeMarkEffect, description: "Per game. The game's pooled effect played once at a stone when Explode marks it (names as RiseEffect). No sound.");
            explodeEffectEntry = looks.CreateEntry("ExplodeEffect", DefaultExplodeEffect, description: "Per game. The game's pooled effect played where a marked stone blasts as it breaks (names as RiseEffect).");
            explodeSoundEntry = looks.CreateEntry("ExplodeSound", DefaultExplodeSound, description: "Per game. The game's sound set played where a marked stone blasts as it breaks (names as RiseEffect).");
            // a Net whose static initialiser failed (Main says so) throws on every touch: no break, stomp or explode looks, the rest still starts
            try { Net.AppliedReceived += OnApplied; }
            catch (Exception e) { MelonLogger.Warning($"[Earth] Looks: no break, stomp or explode looks (message 19 is not set up: {e.GetType().Name})"); }
        }

        /// <summary>Games: a stone appeared; the dirt and the sound at its top.</summary>
        internal static void Rise(GameObject root, StoneKind kind) {
            try {
                Transform t = root.transform;
                Vector3 top = t.position + t.up * (ColliderSize(root, kind).y * 0.5f);
                Play(riseEffect, riseEffectEntry, riseSound, riseSoundEntry, top);
                if (Logs.Debug) MelonLogger.Msg($"[Earth] Looks: {root.name} ({kind}) appeared; its rise plays at {top}");
            }
            catch (Exception e) {
                if (!riseFailed) { riseFailed = true; MelonLogger.Warning($"[Earth] Looks: the rise look failed (said once): {e}"); }
            }
        }

        static void OnApplied(uint entity, byte evt, Vector3 at) {
            if (Main.IsServer) return;
            if (evt == Wire.Broke) {
                try {
                    Play(breakEffect, breakEffectEntry, breakSound, breakSoundEntry, at);
                    if (Logs.Debug) MelonLogger.Msg($"[Earth] Looks: stone #{entity} broke; its break plays at {at}");
                }
                catch (Exception e) {
                    if (!breakFailed) { breakFailed = true; MelonLogger.Warning($"[Earth] Looks: the break look failed (said once): {e}"); }
                }
            }
            else if (evt == (byte)Modifier.Stomp) {   // a stomped stone landed there (ruling C7), not where the pose was struck
                try {
                    Play(stompEffect, stompEffectEntry, stompSound, stompSoundEntry, at);
                    if (Logs.Debug) MelonLogger.Msg($"[Earth] Looks: stone #{entity} landed from a Stomp; its slam plays at {at}");
                }
                catch (Exception e) {
                    if (!stompFailed) { stompFailed = true; MelonLogger.Warning($"[Earth] Looks: the stomp look failed (said once): {e}"); }
                }
            }
            else if (evt == (byte)Modifier.Explode) {   // the server sends a stone's mark once (ruling C11)
                try {
                    Play(explodeMarkEffect, explodeMarkEffectEntry, null, null, at);
                    if (Logs.Debug) MelonLogger.Msg($"[Earth] Looks: stone #{entity} was marked by Explode; its mark plays at {at}");
                }
                catch (Exception e) {
                    if (!markFailed) { markFailed = true; MelonLogger.Warning($"[Earth] Looks: the explode mark look failed (said once): {e}"); }
                }
            }
            else if (evt == Wire.Exploded) {
                try {
                    Play(explodeEffect, explodeEffectEntry, explodeSound, explodeSoundEntry, at);
                    if (Logs.Debug) MelonLogger.Msg($"[Earth] Looks: stone #{entity} exploded; its blast plays at {at}");
                }
                catch (Exception e) {
                    if (!explodeFailed) { explodeFailed = true; MelonLogger.Warning($"[Earth] Looks: the explode look failed (said once): {e}"); }
                }
            }
        }

        // The effect, then the sound (none: no sound), at a place.
        static void Play(LookAsset<PooledObjectDefinition> effect, MelonPreferences_Entry<string>? effectNames, LookAsset<SoundEffectSet>? sound, MelonPreferences_Entry<string>? soundNames, Vector3 at) {
            PooledObjectDefinition? fx = effect.Get(effectNames?.Value);
            if (fx != null) fx.Get(at);   // no turn: the game plays its own dig effect at a place only (DiggingImpactorSettings.DigEffect)
            if (sound == null) return;
            SoundEffectSet? s = sound.Get(soundNames?.Value);
            if (s != null) s.PlayAtPosition(at);
        }

        internal static void Dress(GameObject root, StoneKind kind) {
            Transform model = root.transform.Find("Model");
            if (model == null) {
                if (!warnedModel) { warnedModel = true; MelonLogger.Warning($"[Earth] Looks: {root.name} has no Model child; the stones stay invisible"); }
                return;
            }
            if (!FindBoulder() || boulderMesh == null) return;
            Vector3 size = ColliderSize(root, kind);
            MeshFilter mf = model.GetComponent<MeshFilter>();
            if (mf == null) mf = model.gameObject.AddComponent<MeshFilter>();
            MeshRenderer mr = model.GetComponent<MeshRenderer>();
            if (mr == null) mr = model.gameObject.AddComponent<MeshRenderer>();
            model.localRotation = Quaternion.identity;
            if (kind == StoneKind.Ball || kind == StoneKind.Disc) {
                // the boulder's bounds stretched onto the collider's, its middle on the stone's middle
                Bounds b = boulderMesh.bounds;
                var scale = new Vector3(Stretch(size.x, b.size.x), Stretch(size.y, b.size.y), Stretch(size.z, b.size.z));
                mf.sharedMesh = boulderMesh;
                mr.sharedMaterials = boulderMaterials;
                model.localScale = scale;
                model.localPosition = -Vector3.Scale(b.center, scale);
            }
            else {
                mf.sharedMesh = Block(size);
                mr.sharedMaterial = boulderMaterials[0];
                model.localScale = Vector3.one;
                model.localPosition = Vector3.zero;
            }
            if (Logs.Debug) MelonLogger.Msg($"[Earth] Looks: dressed {root.name} ({kind}, {size.x:0.##} x {size.y:0.##} x {size.z:0.##} m) at {root.transform.position}");
        }

        static float Stretch(float want, float have) => have > 1e-4f ? want / have : 1f;

        static Vector3 ColliderSize(GameObject root, StoneKind kind) {
            BoxCollider box = root.GetComponent<BoxCollider>();
            if (box != null) return box.size;
            SphereCollider sphere = root.GetComponent<SphereCollider>();
            if (sphere != null) return Vector3.one * (sphere.radius * 2f);
            V3 s = StoneTable.Of(kind).Size;
            return new Vector3(s.X, s.Y, s.Z);
        }

        // Once per game: the boulder prefab's most detailed mesh (a LOD0 first, else the most vertices) and its materials, and
        // the part of the texture its UVs cover (only when the mesh can be read: a game mesh may not be).
        static bool FindBoulder() {
            if (searched) return boulderMesh != null;
            searched = true;
            NetworkPrefab prefab = PrefabManager.GetPrefab(BoulderPrefab.ToLowerInvariant());   // the game compares lower case
            if (prefab == null) { MelonLogger.Warning($"[Earth] Looks: no network prefab '{BoulderPrefab}' in this game; the stones stay invisible"); return false; }
            MeshFilter? best = null;
            foreach (MeshFilter f in prefab.GetComponentsInChildren<MeshFilter>(true)) {
                MeshRenderer r = f.GetComponent<MeshRenderer>();
                if (f.sharedMesh == null || r == null || r.sharedMaterials.Length == 0 || r.sharedMaterials[0] == null) continue;
                if (best == null) { best = f; continue; }
                bool lod0 = f.name.Contains("LOD0"), bestLod0 = best.name.Contains("LOD0");
                if ((lod0 && !bestLod0) || (lod0 == bestLod0 && f.sharedMesh.vertexCount > best.sharedMesh.vertexCount)) best = f;
            }
            if (best == null) { MelonLogger.Warning($"[Earth] Looks: '{prefab.name}' has no mesh with a material; the stones stay invisible"); return false; }
            boulderMesh = best.sharedMesh;
            boulderMaterials = best.GetComponent<MeshRenderer>().sharedMaterials;
            bool readable = boulderMesh.isReadable;
            if (readable) boulderUv = UvBounds(boulderMesh.uv, boulderUv);
            MelonLogger.Msg($"[Earth] Looks: stones wear '{boulderMesh.name}' ({boulderMesh.vertexCount} vertices) and '{boulderMaterials[0].name}' from '{prefab.name}'; the blocks' texture area {(readable ? $"u {boulderUv.U0:0.###}-{boulderUv.U1:0.###}, v {boulderUv.V0:0.###}-{boulderUv.V1:0.###}" : "is the whole texture (the mesh cannot be read)")}");
            return true;
        }

        static UvRect UvBounds(Vector2[] uv, UvRect fallback) {
            if (uv == null || uv.Length == 0) return fallback;
            float u0 = float.MaxValue, v0 = float.MaxValue, u1 = float.MinValue, v1 = float.MinValue;
            foreach (Vector2 p in uv) {
                u0 = Mathf.Min(u0, p.x); v0 = Mathf.Min(v0, p.y);
                u1 = Mathf.Max(u1, p.x); v1 = Mathf.Max(v1, p.y);
            }
            return u1 - u0 > 1e-4f && v1 - v0 > 1e-4f ? new UvRect(u0, v0, u1, v1) : fallback;
        }

        // One mesh per size, kept for the whole game (a mesh made per stone would never be freed).
        static Mesh Block(Vector3 size) {
            if (blocks.TryGetValue(size, out Mesh cached) && cached != null) return cached;
            var uv = new UvRect[StoneBox.Faces];
            for (int i = 0; i < uv.Length; i++) uv[i] = boulderUv;
            Mesh m = ToMesh(StoneBox.Build(size.x, size.y, size.z, BlockBevel, uv), $"Earth block {size.x:0.##} x {size.y:0.##} x {size.z:0.##}");
            blocks[size] = m;
            return m;
        }

        static Mesh ToMesh(StoneMesh s, string name) {
            int n = s.VertexCount;
            var v = new Vector3[n];
            var nr = new Vector3[n];
            var t = new Vector4[n];
            var uv = new Vector2[n];
            for (int i = 0; i < n; i++) {
                v[i] = new Vector3(s.Vertices[i * 3], s.Vertices[i * 3 + 1], s.Vertices[i * 3 + 2]);
                nr[i] = new Vector3(s.Normals[i * 3], s.Normals[i * 3 + 1], s.Normals[i * 3 + 2]);
                t[i] = new Vector4(s.Tangents[i * 4], s.Tangents[i * 4 + 1], s.Tangents[i * 4 + 2], s.Tangents[i * 4 + 3]);
                uv[i] = new Vector2(s.Uvs[i * 2], s.Uvs[i * 2 + 1]);
            }
            var m = new Mesh { name = name, hideFlags = HideFlags.DontUnloadUnusedAsset };
            m.vertices = v;
            m.normals = nr;
            m.tangents = t;
            m.uv = uv;
            m.triangles = s.Triangles;
            m.RecalculateBounds();
            return m;
        }
    }

    /// <summary>On every stone prefab (Prefabs.cs puts it on the templates and sets <see cref="Kind"/>; Instantiate copies both).
    /// It says which stone this is (<c>Prefabs.KindOf</c>). On a game it dresses the copy once it appears; on the headless server
    /// it does nothing more (no renderers or effects there). Every copy that has started is in <see cref="Live"/> until it is
    /// destroyed (the templates, under their inactive holder, never start), so a game finds the stones near it without a scene
    /// search (Bend.cs picks a modifier's target from it).</summary>
    internal sealed class StoneView : MonoBehaviour {
        /// <summary>Public, so Unity serializes it and every copy of a template keeps its kind.</summary>
        public StoneKind Kind;

        /// <summary>Every stone that has started and is not yet destroyed, on this machine. An entry may be inactive or being
        /// destroyed: check before use.</summary>
        internal static readonly List<StoneView> Live = new List<StoneView>();

        /// <summary>This copy's entity, collider and rigidbody (from Start; null when it has none).</summary>
        internal NetworkEntity? Entity { get; private set; }
        internal Collider? Shape { get; private set; }
        internal Rigidbody? Body { get; private set; }

        static bool warned;

        void Start() {
            Entity = GetComponent<NetworkEntity>();
            Shape = GetComponent<Collider>();
            Body = GetComponent<Rigidbody>();
            Live.Add(this);
            if (Main.IsServer) return;
            try { StoneLook.Dress(gameObject, Kind); }
            catch (Exception e) {
                if (!warned) { warned = true; MelonLogger.Warning($"[Earth] Looks: could not dress {name}: {e}"); }
            }
            StoneLook.Rise(gameObject, Kind);   // never throws
        }

        void OnDestroy() => Live.Remove(this);
    }

    /// <summary>Games: one of the game's assets ([EarthLooks]) found by name among everything loaded: the names split by ;
    /// are tried in order and the first one loaded is kept (until the setting changes, or it is unloaded). None loaded: one
    /// warning per setting value, nothing played, and it is looked for again every 10 s (each search walks every loaded
    /// asset of the type).</summary>
    internal sealed class LookAsset<T> where T : UnityEngine.Object {
        const float RetrySeconds = 10f;

        readonly string key, what;
        readonly Func<T, string, bool> named;
        string names = "";
        T? found;
        float retryAt;
        bool warned;

        /// <summary><paramref name="key"/> is the [EarthLooks] entry (for the warning), <paramref name="what"/> the kind of
        /// asset, <paramref name="named"/> whether an asset has a name.</summary>
        internal LookAsset(string key, string what, Func<T, string, bool> named) { this.key = key; this.what = what; this.named = named; }

        /// <summary>The asset <paramref name="wanted"/> names (the setting's value now), or null.</summary>
        internal T? Get(string? wanted) {
            string w = (wanted ?? "").Trim();
            if (w != names) {
                names = w;
                found = null;
                retryAt = 0f;
                warned = false;
            }
            if (found != null) return found;   // Unity's null: one unloaded since is looked for again
            if (names.Length == 0 || Time.unscaledTime < retryAt) return null;
            retryAt = Time.unscaledTime + RetrySeconds;
            T[] all = Resources.FindObjectsOfTypeAll<T>();
            foreach (string raw in names.Split(';')) {
                string n = raw.Trim();
                if (n.Length == 0) continue;
                foreach (T a in all)
                    if (a != null && named(a, n)) {
                        found = a;
                        if (Logs.Debug) MelonLogger.Msg($"[Earth] Looks: [EarthLooks] {key} plays the {what} '{n}'");
                        return a;
                    }
            }
            if (!warned) {
                warned = true;
                MelonLogger.Warning($"[Earth] Looks: no loaded {what} named '{names}' ([EarthLooks] {key}): nothing is played; looked for again every {RetrySeconds:0} s");
            }
            return null;
        }
    }
}
