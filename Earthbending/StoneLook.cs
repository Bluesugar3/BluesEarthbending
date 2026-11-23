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
    //
    // Task 15 (spec 2.2, 3.7; ruling C18): the server's Applied(Hitstop), a stone frozen by a clash (it carries no time), shakes
    // that stone's "Model" child for ShakeSeconds: each frame a random offset of at most ShakeMetres round where Dress put it
    // (StoneView.Shake, unscaled time), and back exactly there when the shake ends or the stone goes. Nothing on the server.
    //
    // Task 17b. Ruling 38: on a game the Model is no longer drawn on the game's copy of the body, which the game moves toward
    // the last synced place at each sync (the server sends it every second frame of its own, so the gap depends on the
    // server's frame rate; a sliding stone stalled and jumped), but at a place predicted from that sync and
    // eased there (Motion.cs, StoneMotion.cs; StoneView.LateUpdate), the shake on top; [EarthLooks] SmoothMotion off: as before.
    // Ruling 39: the blocks carry the boulder's atlas rectangle in their fourth UV set (the game's atlas shader reads it there),
    // their UV0 scaled by each face's size. A break and a blast try more of the game's effects (the archery mod's).
    internal static class StoneLook {
        const string BoulderPrefab = "Stone Boulder 01";
        const float BlockBevel = 0.05f;
        const string DefaultRiseEffect = "Digging Dirt Spray Effect;Digging Dirt Smoke Puff Effect", DefaultRiseSound = "Digging DirtFall Sound Set";
        const string DefaultBreakEffect = "Destroyed Rock;Charged Axe Shatter Effect;Axe Shatter Damage Effect;Large Smoke Puff", DefaultBreakSound = "Rock Destroyed Sounds;Boulder Breaking Sounds";
        const string DefaultStompEffect = "Turabada Ground Slam Effect", DefaultStompSound = "Heavy Turabada Step Sounds";
        const string DefaultExplodeMarkEffect = "Mining Successful", DefaultExplodeEffect = "Shockwave Impact;Explosion;Charged Axe Shatter Effect", DefaultExplodeSound = "Shockwave Sounds";
        const float DefaultShakeMetres = 0.02f, DefaultShakeSeconds = 0.15f;

        static bool searched, warnedModel, riseFailed, breakFailed, stompFailed, markFailed, explodeFailed, shakeFailed;
        static Mesh? boulderMesh;
        static Material[] boulderMaterials = new Material[0];
        // Ruling 39: the game's atlas shader takes the part of its texture a mesh uses from the mesh's FOURTH UV set
        // (TEXCOORD3) as (x, y, width, height), with UV0 inside 0..1 across that part. These are the numbers on every vertex of
        // the boulder's own mesh (SandstoneBoulder_v01_LOD0_Atlased_2, 131 vertices, read from the exported game assets); a
        // game cannot read them at run time, the mesh is not readable.
        const float AtlasX = 0.501220703125f, AtlasY = 0.251220703125f, AtlasWidth = 0.12255859375f, AtlasHeight = 0.12255859375f;
        // the metres of block face one copy of that part covers: the brush strokes are the same size on every block, and the
        // Wall's 2.4 m front uses the whole of it
        const float TileMetres = 2.4f;
        static readonly Dictionary<Vector3, Mesh> blocks = new Dictionary<Vector3, Mesh>();

        static MelonPreferences_Entry<string>? riseEffectEntry, riseSoundEntry, breakEffectEntry, breakSoundEntry, stompEffectEntry, stompSoundEntry;
        static MelonPreferences_Entry<string>? explodeMarkEffectEntry, explodeEffectEntry, explodeSoundEntry;
        static MelonPreferences_Entry<float>? shakeMetresEntry, shakeSecondsEntry;
        static float shakeMetres = DefaultShakeMetres, shakeSeconds = DefaultShakeSeconds;
        static readonly LookAsset<PooledObjectDefinition> riseEffect = new LookAsset<PooledObjectDefinition>("RiseEffect", "effect", EffectNamed);
        static readonly LookAsset<PooledObjectDefinition> breakEffect = new LookAsset<PooledObjectDefinition>("BreakEffect", "effect", EffectNamed);
        static readonly LookAsset<PooledObjectDefinition> stompEffect = new LookAsset<PooledObjectDefinition>("StompEffect", "effect", EffectNamed);
        static readonly LookAsset<PooledObjectDefinition> explodeMarkEffect = new LookAsset<PooledObjectDefinition>("ExplodeMarkEffect", "effect", EffectNamed);
        static readonly LookAsset<PooledObjectDefinition> explodeEffect = new LookAsset<PooledObjectDefinition>("ExplodeEffect", "effect", EffectNamed);
        static readonly LookAsset<SoundEffectSet> riseSound = new LookAsset<SoundEffectSet>("RiseSound", "sound set", SoundNamed);
        static readonly LookAsset<SoundEffectSet> breakSound = new LookAsset<SoundEffectSet>("BreakSound", "sound set", SoundNamed);
        static readonly LookAsset<SoundEffectSet> stompSound = new LookAsset<SoundEffectSet>("StompSound", "sound set", SoundNamed);
        static readonly LookAsset<SoundEffectSet> explodeSound = new LookAsset<SoundEffectSet>("ExplodeSound", "sound set", SoundNamed);

        // a pooled effect is named as its definition or as its prefab, the same test as the archery mod's Fx.FindEffect (which
        // finds 'Charged Axe Shatter Effect', 'Axe Shatter Damage Effect', 'Explosion' and 'Large Smoke Puff' on this game);
        // a sound set as itself
        static bool EffectNamed(PooledObjectDefinition d, string n) => d.name == n || (d.Prefab != null && d.Prefab.name == n);
        static bool SoundNamed(SoundEffectSet s, string n) => s.name == n;

        /// <summary>From Main.OnInitializeMelon on games: the [EarthLooks] rise, break, stomp, explode and shake settings, and
        /// the server's Applied events.</summary>
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
            shakeMetresEntry = looks.CreateEntry("ShakeMetres", DefaultShakeMetres, description: "Per game. Two stones that clash freeze for a blink first (the server's [EarthDamage] HitstopSeconds); meanwhile each shakes this many metres at most round its place, a new way every frame. 0 or more (0: no shake).");
            shakeSecondsEntry = looks.CreateEntry("ShakeSeconds", DefaultShakeSeconds, description: "Per game. How long a clashing stone shakes (see ShakeMetres), in seconds. 0 or more (0: no shake).");
            ApplyShake();
            Cfg.Changed += ApplyShake;
            // [EarthLooks] SmoothMotion, PredictMaxSeconds, SmoothSeconds, SnapMetres; a game whose NetworkRigidbody types changed
            // must not stop the break and explode looks below (StoneView's own try then draws every stone on its body)
            try { StoneMotion.Init(); }
            catch (Exception e) { MelonLogger.Warning($"[Earth] Looks: no smooth motion (its settings could not be set up: {e.GetType().Name}: {e.Message}); stones are drawn on their bodies"); }
            // a Net whose static initialiser failed (Main says so) throws on every touch: no break, stomp or explode looks, the rest still starts
            try { Net.AppliedReceived += OnApplied; }
            catch (Exception e) { MelonLogger.Warning($"[Earth] Looks: no break, stomp or explode looks (message 19 is not set up: {e.GetType().Name})"); }
        }

        static void ApplyShake() {
            shakeMetres = Cfg.NotNegative(shakeMetresEntry, "EarthLooks", "ShakeMetres", DefaultShakeMetres);
            shakeSeconds = Cfg.NotNegative(shakeSecondsEntry, "EarthLooks", "ShakeSeconds", DefaultShakeSeconds);
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
            else if (evt == Wire.Hitstop) {   // ruling C18: no time on the wire; the shake is ShakeSeconds
                try {
                    StoneView? v = StoneView.Find(entity);
                    bool shakes = v != null && v.Shake(shakeMetres, shakeSeconds);
                    if (Logs.Debug) MelonLogger.Msg($"[Earth] Looks: stone #{entity} froze in a clash; {(shakes ? $"its model shakes {shakeMetres:0.###} m for {shakeSeconds:0.##} s" : v == null ? "it is not on this game" : "no shake ([EarthLooks] ShakeMetres or ShakeSeconds 0, or it has no Model)")}");
                }
                catch (Exception e) {
                    if (!shakeFailed) { shakeFailed = true; MelonLogger.Warning($"[Earth] Looks: the hitstop shake failed (said once): {e}"); }
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
            MelonLogger.Msg($"[Earth] Looks: stones wear '{boulderMesh.name}' ({boulderMesh.vertexCount} vertices) and '{boulderMaterials[0].name}' from '{prefab.name}'; the blocks use the boulder's atlas rectangle ({AtlasX}, {AtlasY}, {AtlasWidth} x {AtlasHeight}), one copy per {TileMetres} m of face");
            return true;
        }

        // One mesh per size, kept for the whole game (a mesh made per stone would never be freed).
        static Mesh Block(Vector3 size) {
            if (blocks.TryGetValue(size, out Mesh cached) && cached != null) return cached;
            // UV0 per face, in StoneBox's order +X, -X, +Y, -Y, +Z, -Z: u along z and v along y on the X faces, u along x and
            // v along z on the Y faces, u along x and v along y on the Z faces
            var uv = new UvRect[StoneBox.Faces];
            for (int i = 0; i < uv.Length; i++) {
                int axis = i / 2;
                float w = axis == 0 ? size.z : size.x, h = axis == 1 ? size.z : size.y;
                uv[i] = new UvRect(0f, 0f, Tile(w), Tile(h));
            }
            Mesh m = ToMesh(StoneBox.Build(size.x, size.y, size.z, BlockBevel, uv), $"Earth block {size.x:0.##} x {size.y:0.##} x {size.z:0.##}");
            blocks[size] = m;
            return m;
        }

        // a face's metres as a share of one copy of the atlas part, inside 0..1
        static float Tile(float metres) => metres > 0f && !float.IsInfinity(metres) ? Mathf.Min(metres / TileMetres, 1f) : 1f;

        static Mesh ToMesh(StoneMesh s, string name) {
            int n = s.VertexCount;
            var v = new Vector3[n];
            var nr = new Vector3[n];
            var t = new Vector4[n];
            var uv = new Vector2[n];
            var atlas = new List<Vector4>(n);
            var rect = new Vector4(AtlasX, AtlasY, AtlasWidth, AtlasHeight);
            for (int i = 0; i < n; i++) {
                atlas.Add(rect);
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
            m.SetUVs(3, atlas);   // TEXCOORD3: the atlas rectangle, the same on every vertex as on the boulder
            m.triangles = s.Triangles;
            m.RecalculateBounds();
            return m;
        }
    }

    /// <summary>On every stone prefab (Prefabs.cs puts it on the templates and sets <see cref="Kind"/>; Instantiate copies both).
    /// It says which stone this is (<c>Prefabs.KindOf</c>). On a game it dresses the copy once it appears; on the headless server
    /// it does nothing more (no renderers or effects there). Every copy that has started is in <see cref="Live"/> until it is
    /// destroyed (the templates, under their inactive holder, never start), so a game finds the stones near it without a scene
    /// search (Bend.cs picks a modifier's target from it). Task 15: on a game it shakes its "Model" child while the stone is
    /// frozen in a clash (<see cref="Shake"/>).</summary>
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

        static bool warned, shakeWarned, followWarned, measureWarned;

        Transform? model;    // the dressed "Model" child (games only), and where Dress put it
        Vector3 home;
        Quaternion homeTurn = Quaternion.identity;
        float shakeUntil, shakeMetres;
        bool shaking;

        // Task 17b (Ruling 38): the Model drawn at a smooth, predicted place (games only)
        NetworkRigidbody? net;
        MotionFollow? follow;
        bool followed, followBroken;
        float drawnOffset;            // the Model's distance from the body this frame, while followed
        float seenSync; int seenWindow;   // [EarthLog] Debug's sync-gap measure (StoneMotion.Measure)

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
            Transform found = transform.Find("Model");
            if (found != null) {
                model = found;
                home = found.localPosition;
                homeTurn = found.localRotation;
                net = GetComponent<NetworkRigidbody>();
                follow = new MotionFollow();
            }
            StoneLook.Rise(gameObject, Kind);   // never throws
        }

        /// <summary>The live stone whose entity has this Identifier on this machine, or null.</summary>
        internal static StoneView? Find(uint entity) {
            if (entity == 0u) return null;
            for (int i = 0; i < Live.Count; i++) {
                StoneView v = Live[i];
                if (v != null && v.Entity != null && v.Entity.Identifier == entity) return v;
            }
            return null;
        }

        /// <summary>Games (ruling C18): shakes the "Model" child for <paramref name="seconds"/> (unscaled), at most
        /// <paramref name="metres"/> round where Dress put it, a new random offset each frame; a shake already running starts
        /// over. False, and nothing happens, without a dressed Model, or for a time or a size not above 0.</summary>
        internal bool Shake(float metres, float seconds) {
            if (model == null || !(metres > 0f) || !(seconds > 0f) || float.IsInfinity(metres) || float.IsInfinity(seconds)) return false;
            shakeMetres = metres;
            shakeUntil = Time.unscaledTime + seconds;
            shaking = true;
            return true;
        }

        // Games: where the Model is drawn this frame (after every Update and the game's own moves of the body), then the
        // hitstop's shake on top. The headless server has no model, so it returns at once.
        void LateUpdate() {
            if (model == null) return;
            bool placed = false;
            if (follow != null && !followBroken) {
                try { placed = StoneMotion.On && Follow(model, follow); }
                catch (Exception e) {
                    followBroken = true;   // this stone rides its rigidbody from now on
                    if (!followWarned) { followWarned = true; MelonLogger.Warning($"[Earth] Looks: a stone's smooth motion failed (said once; that stone is drawn on its body from now on): {e}"); }
                }
            }
            if (!placed && followed) {
                // back on the rigidbody exactly where Dress put it (SmoothMotion switched off, no sync to read, a failure)
                followed = false;
                follow?.Reset();
                model.localPosition = home;
                model.localRotation = homeTurn;
            }
            if (Logs.Debug) {
                try { StoneMotion.Measure(net, ref seenSync, ref seenWindow, followed ? drawnOffset : 0f); }
                catch (Exception e) {
                    if (!measureWarned) { measureWarned = true; MelonLogger.Warning($"[Earth] Motion: the sync gap could not be measured (said once): {e.Message}"); }
                }
            }
            if (!shaking) return;
            try {
                if (Time.unscaledTime >= shakeUntil) { StopShake(); return; }
                Vector3 jolt = UnityEngine.Random.insideUnitSphere * shakeMetres;
                if (followed) model.position += jolt;   // on the smooth place set just above, which the next frame sets afresh
                else model.localPosition = home + jolt;
            }
            catch (Exception e) {
                shaking = false;
                if (!shakeWarned) { shakeWarned = true; MelonLogger.Warning($"[Earth] Looks: a stone's shake failed (said once): {e.Message}"); }
            }
        }

        // The Model at the follow's place: the shown place plus the shown turn times Dress's offset, turned by the shown turn
        // times Dress's turn (the stone's root is never scaled). False: draw it on the rigidbody (before the first sync too).
        bool Follow(Transform m, MotionFollow f) {
            if (net == null || !StoneMotion.Read(net, out MotionSync sync)) { f.Reset(); return false; }
            Transform t = transform;
            Vector3 bp = t.position;
            Quaternion br = t.rotation;
            float now = Time.timeSinceLevelLoad;
            float free = FreeAhead(sync, now);
            if (!f.Step(sync, now, Time.deltaTime, new V3(bp.x, bp.y, bp.z), new Q(br.x, br.y, br.z, br.w), StoneMotion.Values, free)) {
                f.Reset();
                return false;
            }
            V3 s = f.Shown;
            drawnOffset = (s - new V3(bp.x, bp.y, bp.z)).Length;
            Q q = f.ShownTurn;
            var turn = new Quaternion(q.X, q.Y, q.Z, q.W);
            m.SetPositionAndRotation(new Vector3(s.X, s.Y, s.Z) + turn * home, turn * homeTurn);
            followed = true;
            return true;
        }

        // I-1 of the review: how far this stone can go from its synced place along its synced velocity before it meets the world,
        // so the prediction never draws it into a wall or a standing stone. One sweep of the stone's own shape (shrunk by Skin,
        // so a stone sliding on the ground does not meet the ground), only for a stone that moves and only as far as the
        // prediction reaches now. Not counted: the stone itself, triggers, players' bodies (Ground.SolidMask), anything loose or
        // moving (a body that is not kinematic: other moving stones, items), a contact already there at the start, and a
        // surface the sweep only grazes (its normal not against the way). Counted: the solid world and standing (kinematic)
        // stones. Endless when nothing is in the way.
        static readonly RaycastHit[] sweepHits = new RaycastHit[16];
        const float Skin = 0.02f, GrazeCos = -0.2f;

        float FreeAhead(in MotionSync s, float now) {
            if (s.Kinematic || Shape == null) return float.PositiveInfinity;
            var v = new Vector3(s.Velocity.X, s.Velocity.Y, s.Velocity.Z);
            float speed = v.magnitude;
            if (!(speed >= Motion.RestSpeed)) return float.PositiveInfinity;
            float reach = speed * Motion.Age(s, now, StoneMotion.Values.PredictMaxSeconds);
            if (!(reach > 1e-3f)) return float.PositiveInfinity;
            Vector3 dir = v / speed;
            var at = new Vector3(s.Live.X, s.Live.Y, s.Live.Z);
            var turn = new Quaternion(s.LiveTurn.X, s.LiveTurn.Y, s.LiveTurn.Z, s.LiveTurn.W);
            int n;
            if (Shape is BoxCollider box) {
                Vector3 half = box.size * 0.5f - new Vector3(Skin, Skin, Skin);
                half = Vector3.Max(half, new Vector3(0.01f, 0.01f, 0.01f));
                n = Physics.BoxCastNonAlloc(at + turn * box.center, half, dir, sweepHits, turn, reach, Ground.SolidMask, QueryTriggerInteraction.Ignore);
            }
            else if (Shape is SphereCollider sphere) {
                n = Physics.SphereCastNonAlloc(at + turn * sphere.center, Mathf.Max(sphere.radius - Skin, 0.01f), dir, sweepHits, reach, Ground.SolidMask, QueryTriggerInteraction.Ignore);
            }
            else return float.PositiveInfinity;
            float free = float.PositiveInfinity;
            for (int i = 0; i < n; i++) {
                RaycastHit h = sweepHits[i];
                Collider c = h.collider;
                if (c == null || !(h.distance > 0f) || h.distance >= free) continue;
                Rigidbody rb = c.attachedRigidbody;
                if (rb != null && (rb == Body || !rb.isKinematic)) continue;
                if (Vector3.Dot(h.normal, dir) > GrazeCos) continue;
                free = h.distance;
            }
            // the shrunk shape meets a thing Skin later than the stone itself would
            return float.IsPositiveInfinity(free) ? free : Mathf.Max(0f, free - Skin);
        }

        // back exactly where Dress put it; while followed the smooth place set this frame stays (no jolt was added to it)
        void StopShake() {
            shaking = false;
            if (model != null && !followed) model.localPosition = home;
        }

        void OnDisable() {
            if (shaking) StopShake();
        }

        void OnDestroy() {
            if (shaking) StopShake();
            Live.Remove(this);
        }
    }

    /// <summary>Games: one of the game's assets ([EarthLooks]) found by name among everything loaded: the names split by ;
    /// are tried in order and the first one loaded is kept (until the setting changes, or it is unloaded). None loaded: one
    /// warning per setting value, nothing played, and it is looked for again every 10 s (each search walks every loaded
    /// asset of the type). A later name kept because an earlier one was not loaded: the earlier names are looked for again
    /// every 10 s, without a warning, and the first of them that has loaded since takes over.</summary>
    internal sealed class LookAsset<T> where T : UnityEngine.Object {
        const float RetrySeconds = 10f;

        readonly string key, what;
        readonly Func<T, string, bool> named;
        string names = "";
        string[] list = new string[0];
        T? found;
        int foundAt;   // the index in list of the name found
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
                list = Split(w);
                found = null;
                retryAt = 0f;
                warned = false;
            }
            // Unity's null: one unloaded since is looked for again at once
            if (found == null && !ReferenceEquals(found, null)) { found = null; retryAt = 0f; }
            if (found != null && (foundAt == 0 || Time.unscaledTime < retryAt)) return found;
            if (list.Length == 0 || Time.unscaledTime < retryAt) return found;
            retryAt = Time.unscaledTime + RetrySeconds;
            int upTo = found != null ? foundAt : list.Length;   // a fallback kept: only the names before it
            T[] all = Resources.FindObjectsOfTypeAll<T>();
            for (int i = 0; i < upTo; i++) {
                string n = list[i];
                foreach (T a in all)
                    if (a != null && named(a, n)) {
                        found = a;
                        foundAt = i;
                        if (Logs.Debug) MelonLogger.Msg($"[Earth] Looks: [EarthLooks] {key} plays the {what} '{n}'");
                        return a;
                    }
            }
            if (found != null) return found;
            if (!warned) {
                warned = true;
                MelonLogger.Warning($"[Earth] Looks: none of the {what}s '{string.Join("', '", list)}' is loaded ([EarthLooks] {key}): nothing is played; looked for again every {RetrySeconds:0} s");
            }
            return null;
        }

        static string[] Split(string names) {
            var r = new List<string>();
            foreach (string raw in names.Split(';')) {
                string n = raw.Trim();
                if (n.Length > 0) r.Add(n);
            }
            return r.ToArray();
        }
    }
}
