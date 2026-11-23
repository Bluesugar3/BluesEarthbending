using System;
using System.Collections.Generic;
using Alta.Networking.Scripts.Player;
using MelonLoader;
using UnityEngine;

namespace Earthbending {
    // Games only: what players see of fight health (docs/superpowers/specs/2026-11-23-earthbending-fight-health-design.md
    // section 3.7). Nothing here decides anything: it draws what the server's Fight and Ring messages say (Net.cs).
    //
    // The stones. One small stone per point of fight health left, each the game's own boulder mesh with its own shared
    // materials (StoneLook.Boulder: read and shared, never changed; no game prefab is instantiated), fitted from the mesh's
    // bounds so its largest side is the stone's size and its middle sits on its place, each turned its own fixed way so a
    // row does not look stamped.
    //   - Another fighter: a row over the head, FightRowHeight above it (under the game's own name board, which hangs 0.5 m
    //     over a head), in groups of five, the first on the looker's left. It turns to the local camera about the upright
    //     axis only, as the game's name boards do, so it stays level. From further than FightRowFarMetres the row grows
    //     with the distance (at most FightRowFarScale times), so a watcher on the rim still sees stones and not a grey line:
    //     at its own size a 3.5 cm stone 15 m away is 8 arc minutes wide, two or three pixels of a headset.
    //     Review I-1: a grown row of 20 in one line is 2.5 m wide and ran into the next fighter's. So a row grown past 1.5
    //     times (FightPips.PerLineAt, with a little slack so it does not flicker at the switching distance) is two lines
    //     of ten, 1.22 m wide at x2.5; and every second fighter's row, by account (FightPips.Rank, the same on every game),
    //     hangs a whole row's lines and a group gap higher (FightPips.RaiseHeight), so two rows that do meet are never at
    //     one height and read as two.
    //   - The local player (who cannot see over their own head): the same count in rows of five across one forearm
    //     (FightBandHand, the left by default), on the side that faces their eyes, the rows going from the wrist toward the
    //     elbow. The plan's ring of stones all the way round was not readable: the arm hides the far side, so 3, 4 and 5
    //     points all showed the same three stones and 2 showed one. Each row is five neighbouring places of a ring round the
    //     arm (FightPips.BandPlace, BandAngle), which the arm never hides. The forearm is the avatar's own: that arm's Elbow
    //     and Wrist bones (Controller.Hand.ArmIKControllers -> ArmController, as the game's climbing reads the Shoulder),
    //     which the game solves every frame in PlayerCharacter.Update (ArmController.UpdateCharacterComponent), so LateUpdate
    //     reads what is drawn; the band starts from the wrist bone, never from the tracked controller (the two part when the
    //     game's hand is held back). "On top" is toward the eyes, so no controller axis is trusted (they differ between
    //     controllers); it turns toward a new side at most 720 degrees a second and keeps the side it had while the eyes
    //     look along the forearm, so it never snaps round the arm. Without the bones the same rows are a small patch facing
    //     the eyes just above the controller. Review I-2: the band is in front of the arm, so it is hidden while its middle
    //     is nearer the eyes than FightBandHideMetres (it cannot be read there and only blocks the view; it is back a quarter
    //     further out), and FightBand switches it off alone.
    //   A fighter's stones follow Net.FightReceived (-1: none) and are dropped when it is not heard again for 12 s (the
    //   server says every fighter's points again every 5 s); a fighter whose body is not on this game shows none.
    //
    // The ring. One made by the server's setting or its console is drawn as 64 pale dashes on the ground at the ring's
    // radius: line renderers on Sprites/Default, facing the camera (a flat strip on the ground is thinner than a pixel from
    // across a 10 m ring), each dash laid on the natural ground under it (a cast down from 1.5 m over the floor to 1.5 m
    // under; no ground there: the floor's own height; cast again at the server's next few resends, then left), 0.03 m up.
    // It goes with the ring, and when the server has not said it again for 12 s (it says the ring, or that there is none,
    // every 5 s).
    //
    // Leaving the server (no live connection, or the main menu's body) forgets every fighter and the ring at once.
    //
    // All of it is the mod's own objects under one DontDestroyOnLoad host, built at the first use and switched on and off;
    // a row's stones are placed only when its count, its lines or a setting changed, so a frame sets one transform per
    // fighter and allocates nothing. With no fighter and no ring a frame is one bool test. Each look fails on its own: no
    // boulder mesh is one warning and no stones; the frame, a fighter, the stones or the ring that throws is hidden, warned
    // of once and tried again 5 s later. Nothing of this runs on the headless server.
    internal static class FightLook {
        const float DropSeconds = 12f, RetrySeconds = 5f;
        const int MaxRows = 64;   // fighters remembered at once; a message for one more is not drawn
        const float DefaultPipSize = 0.035f, DefaultPipGap = 0.012f, DefaultGroupGap = 0.03f, DefaultRowHeight = 0.35f;
        const float DefaultFarMetres = 6f, DefaultFarScale = 2.5f;
        const float DefaultBandPipSize = 0.018f, DefaultBandRadius = 0.055f, DefaultBandUp = 0.12f, DefaultBandHide = 0.2f;
        const string DefaultBandHand = "left";
        const float DefaultRingWidth = 0.04f;
        const string DefaultRingColour = "#EDE6D2C0";

        // the band's frame: not settings (they say where a body is, not how the band looks)
        const float HandLength = 0.08f;                        // FightBandUp counts from the hand: this far before the wrist bone
        const float MinForearm = 0.1f, MaxForearm = 0.6f;      // a wrist-to-elbow distance outside these is not a forearm
        const float ArmRetrySeconds = 2f;
        const float TopShare = 0.2f;                           // the eyes this near the forearm's own line: the band keeps the side it had
        const float TurnDegreesPerSecond = 720f;               // the band turns round the arm toward the eyes no faster

        // the test ring's dashes: the look of a play-test tool
        const int RingDashes = 64, RingRelays = 3;
        const float RingLift = 0.03f, RingCastUp = 1.5f, DashShare = 0.5f;

        static MelonPreferences_Entry<bool>? stonesEntry, bandEntry;
        static MelonPreferences_Entry<float>? pipSizeEntry, pipGapEntry, groupGapEntry, rowHeightEntry, farMetresEntry, farScaleEntry;
        static MelonPreferences_Entry<float>? bandPipSizeEntry, bandRadiusEntry, bandUpEntry, bandHideEntry, ringWidthEntry;
        static MelonPreferences_Entry<string>? bandHandEntry, ringColourEntry;

        static bool stonesOn = true, bandOn = true, bandRight;
        static float pipSize = DefaultPipSize, pipGap = DefaultPipGap, groupGap = DefaultGroupGap, rowHeight = DefaultRowHeight;
        static float farMetres = DefaultFarMetres, farScale = DefaultFarScale;
        static float bandSize = DefaultBandPipSize, bandRadius = DefaultBandRadius, bandUp = DefaultBandUp, bandHide = DefaultBandHide;
        static float bandPitch = DefaultBandPipSize;   // from one stone of the band to the next, across the arm and along it
        static int bandPlaces = FightPips.Group;
        static float ringWidth = DefaultRingWidth;
        static Color ringColour = Color.white;
        static int stamp;   // goes up with every settings load: a row laid out for an older one is laid out again

        /// <summary>One fighter's stones: a row over their head, or the local player's band. A row whose fighter is gone
        /// (<see cref="Points"/> below 0) keeps its objects, hidden, for the next fighter.</summary>
        sealed class Row {
            public int Account, Points = -1;
            public float RefreshedAt, RetryAt;
            public bool Raised;                       // every second fighter by account: this row hangs higher
            public int PerLine = FightPips.PerLine;   // stones to a line now (fewer once the row is grown for a far looker)
            public GameObject? Root;
            public Transform RootT = null!;
            public readonly Transform[] Stones = new Transform[FightPips.MaxShown];
            public int Built, Laid = -1, LaidPer = -1, LaidStamp = -1;
            public bool LaidBand, Visible, Warned;
            public float Scale = 1f;
        }

        static readonly List<Row> rows = new List<Row>();
        static readonly int[] accounts = new int[MaxRows];   // every fighter now, for the order
        static bool active;            // a fighter is known or a ring is drawn: the frame has work
        static bool orderDirty;        // a fighter came or went: who is raised is worked out again
        static int most;               // the most points heard of any fighter (the server's full value, as far as a game knows)
        static GameObject? host;
        static bool hostShown;

        static Mesh? boulderMesh;
        static Material[] boulderMaterials = new Material[0];
        static Vector3 boulderCentre;
        static float boulderMax = 1f;
        static bool boulderAsked;

        static PlayerController? armOf;          // the local body whose arm was looked up, and which arm
        static bool armRight;
        static Transform? elbow, wrist;
        static float armRetryAt;
        static bool armSaid, fallbackSaid;
        static Vector3 lastTop = Vector3.up;     // the side of the forearm the band faced last
        static bool topSet;
        static int topFrame;
        static bool bandFar = true;              // false while the band is hidden for being too near the eyes

        static bool ringHas, ringShown, ringDirty, ringBroken;
        static Vector3 ringCentre;
        static float ringRadius, ringAt, ringRetryAt;
        static int ringMissed, ringLays, ringLaidStamp = -1;
        static GameObject? ringRoot;
        static readonly LineRenderer[] dashes = new LineRenderer[RingDashes];

        static float frameRetryAt, stonesRetryAt;
        static bool frameWarned, stonesWarned, ringWarned, rowsFullWarned;

        /// <summary>From Main.OnInitializeMelon on games: the [EarthLooks] Fight* settings, and the server's Fight and Ring
        /// messages.</summary>
        internal static void Init() {
            MelonPreferences_Category looks = Cfg.Category("EarthLooks");
            stonesEntry = looks.CreateEntry("FightStones", true, description: "Per game. Fight health as small stones: one per point left, in a row over every other fighter's head and in rows of five on your own forearm. Off: no stones (a drawn test ring and the knock-out's blast stay).");
            pipSizeEntry = looks.CreateEntry("FightPipSize", DefaultPipSize, description: "Per game. The size of one stone of a row over a head, in metres (its largest side). Above 0.");
            pipGapEntry = looks.CreateEntry("FightPipGap", DefaultPipGap, description: "Per game. The space between two stones of a row over a head, in metres (the stones on your forearm keep the same share of their own size). 0 or more.");
            groupGapEntry = looks.CreateEntry("FightGroupGap", DefaultGroupGap, description: "Per game. The extra space between two groups of five in a row over a head, in metres. 0 or more.");
            rowHeightEntry = looks.CreateEntry("FightRowHeight", DefaultRowHeight, description: "Per game. A fighter's row floats this many metres over their head (the game's own name board hangs 0.5 m over it). Every second fighter's row, by account, hangs a row's lines higher, so two rows side by side are never at one height. 0 or more.");
            farMetresEntry = looks.CreateEntry("FightRowFarMetres", DefaultFarMetres, description: "Per game. From further away than this many metres a row over a head grows with the distance, so its stones look no smaller than they do from here and still read from the rim (see FightRowFarScale). Grown past 1.5 times it is laid as two lines of ten instead of one of twenty, so it stays narrow. 0: a row never grows.");
            farScaleEntry = looks.CreateEntry("FightRowFarScale", DefaultFarScale, description: "Per game. The most a far row grows: this many times its own size (2.5 is reached 15 m away with FightRowFarMetres 6). 1: a row never grows.");
            bandEntry = looks.CreateEntry("FightBand", true, description: "Per game. Your own fight health as rows of five small stones on your forearm. Off: none on your arm; the rows over the other fighters' heads stay.");
            bandHandEntry = looks.CreateEntry("FightBandHand", DefaultBandHand, description: "Per game. The forearm that carries your own stones: left or right.");
            bandPipSizeEntry = looks.CreateEntry("FightBandPipSize", DefaultBandPipSize, description: "Per game. The size of one stone on your own forearm, in metres (smaller than a head's: it is seen from half a metre). Above 0.");
            bandRadiusEntry = looks.CreateEntry("FightBandRadius", DefaultBandRadius, description: "Per game. Your own stones sit this many metres from the middle line of your forearm, on the side toward your eyes. Above 0.");
            bandUpEntry = looks.CreateEntry("FightBandUp", DefaultBandUp, description: "Per game. Your own stones start this many metres from the hand toward the elbow (the hand is taken as 0.08 m before the wrist, so 0.12 starts 0.04 m above the wrist); each further row of five is one row's place nearer the elbow. 0 or more.");
            bandHideEntry = looks.CreateEntry("FightBandHideMetres", DefaultBandHide, description: "Per game. Your own stones are hidden while their middle is nearer your eyes than this many metres (they cannot be read there and only block the view); they come back a quarter further out (0.2: at 0.25). 0: never hidden.");
            ringWidthEntry = looks.CreateEntry("FightRingWidth", DefaultRingWidth, description: "Per game. A drawn test ring (the server's [EarthFight] Ring, or its console's earth ring): the width of its dashes, in metres. Above 0.");
            ringColourEntry = looks.CreateEntry("FightRingColour", DefaultRingColour, description: "Per game. A drawn test ring's colour (#RRGGBB or #RRGGBBAA).");
            Apply();
            Cfg.Changed += Apply;
            // a Net whose static initialiser failed (Main says so) throws on every touch: nothing is drawn, the rest still starts
            try {
                Net.FightReceived += OnFight;
                Net.RingReceived += OnRing;
            }
            catch (Exception e) { MelonLogger.Warning($"[Earth] Fight: fight health is not drawn (message 19 is not set up: {e.GetType().Name})"); }
        }

        static void Apply() {
            stonesOn = stonesEntry == null || stonesEntry.Value;
            bandOn = bandEntry == null || bandEntry.Value;
            pipSize = Cfg.Positive(pipSizeEntry, "EarthLooks", "FightPipSize", DefaultPipSize);
            pipGap = Cfg.NotNegative(pipGapEntry, "EarthLooks", "FightPipGap", DefaultPipGap);
            groupGap = Cfg.NotNegative(groupGapEntry, "EarthLooks", "FightGroupGap", DefaultGroupGap);
            rowHeight = Cfg.NotNegative(rowHeightEntry, "EarthLooks", "FightRowHeight", DefaultRowHeight);
            farMetres = Cfg.NotNegative(farMetresEntry, "EarthLooks", "FightRowFarMetres", DefaultFarMetres);
            farScale = Cfg.Positive(farScaleEntry, "EarthLooks", "FightRowFarScale", DefaultFarScale);
            bandSize = Cfg.Positive(bandPipSizeEntry, "EarthLooks", "FightBandPipSize", DefaultBandPipSize);
            bandRadius = Cfg.Positive(bandRadiusEntry, "EarthLooks", "FightBandRadius", DefaultBandRadius);
            bandUp = Cfg.NotNegative(bandUpEntry, "EarthLooks", "FightBandUp", DefaultBandUp);
            bandHide = Cfg.NotNegative(bandHideEntry, "EarthLooks", "FightBandHideMetres", DefaultBandHide);
            ringWidth = Cfg.Positive(ringWidthEntry, "EarthLooks", "FightRingWidth", DefaultRingWidth);
            string hand = (bandHandEntry?.Value ?? DefaultBandHand).Trim();
            bandRight = string.Equals(hand, "right", StringComparison.OrdinalIgnoreCase);
            if (!bandRight && !string.Equals(hand, "left", StringComparison.OrdinalIgnoreCase))
                MelonLogger.Warning($"[Earth] Settings: [EarthLooks] FightBandHand '{hand}' is not left or right; using left");
            string text = (ringColourEntry?.Value ?? DefaultRingColour).Trim();
            if (!ColorUtility.TryParseHtmlString(text, out ringColour)) {
                MelonLogger.Warning($"[Earth] Settings: [EarthLooks] FightRingColour '{text}' is not a colour (#RRGGBB or #RRGGBBAA); using {DefaultRingColour}");
                ColorUtility.TryParseHtmlString(DefaultRingColour, out ringColour);
            }
            // the forearm's stones keep the head row's share of space between them
            bandPitch = bandSize + pipGap * bandSize / pipSize;
            bandPlaces = FightPips.BandPlaces(bandRadius, bandPitch);
            stamp++;
        }

        // ---------------------------------------------------------------- what the server says (data only; the frame draws)

        static void OnFight(int account, int points) {
            if (Main.IsServer) return;
            Row? row = null, free = null;
            for (int i = 0; i < rows.Count; i++) {
                Row r = rows[i];
                if (r.Points >= 0 && r.Account == account) { row = r; break; }
                if (r.Points < 0 && free == null) free = r;
            }
            if (points < 0) {
                if (row != null) {   // the frame hides it
                    row.Points = -1;
                    orderDirty = true;
                }
                return;
            }
            if (row == null) {
                row = free;
                if (row == null) {
                    if (rows.Count >= MaxRows) {
                        if (!rowsFullWarned) { rowsFullWarned = true; MelonLogger.Warning($"[Earth] Fight: more than {MaxRows} fighters at once; the rest are not drawn (said once)"); }
                        return;
                    }
                    row = new Row();
                    rows.Add(row);
                }
                row.Account = account;
                row.RetryAt = 0f;
                row.PerLine = FightPips.PerLine;
                orderDirty = true;
            }
            row.Points = points;
            row.RefreshedAt = Time.unscaledTime;
            if (points > most) most = points;
            active = true;
        }

        static void OnRing(bool has, Vector3 centre, float radius, float arena, bool draw) {
            if (Main.IsServer) return;
            if (!has || !draw) {
                if (ringHas) MelonLogger.Msg("[Earth] Fight: the drawn ring is gone");
                ringHas = false;   // the frame hides it
                return;
            }
            if (!ringHas || centre != ringCentre || radius != ringRadius) {
                ringCentre = centre;
                ringRadius = radius;
                ringDirty = true;
                ringLays = 0;
                MelonLogger.Msg($"[Earth] Fight: a ring is drawn on the ground: centre {centre}, {radius:0.##} m ({RingDashes} dashes)");
            }
            // some dashes found no ground (not loaded yet?): laid again at the next few resends, then left flat (a platform,
            // a cliff's edge)
            else if (ringMissed > 0 && ringLays <= RingRelays) ringDirty = true;
            ringHas = true;
            ringAt = Time.unscaledTime;
            active = true;
        }

        // ---------------------------------------------------------------- every frame

        /// <summary>Every frame on games, after the game moved the heads and hands (Main.OnLateUpdate). One bool test while no
        /// fighter is known and no ring is drawn.</summary>
        internal static void Frame() {
            if (!active) return;
            float now = Time.unscaledTime;
            if (now < frameRetryAt) return;
            try {
                PlayerController pc = PlayerController.Current;
                // left the server (the main menu has a body of its own): nothing the old server said is drawn any longer
                if (!Net.Connected || pc is MainMenuPlayerController) {
                    Forget();
                    return;
                }
                Camera? cam = pc != null ? pc.Camera : null;
                if (pc == null || cam == null) {   // loading, a scene change: nothing is drawn, what is known stays
                    if (hostShown && host != null) { host.SetActive(false); hostShown = false; }
                    return;
                }
                if (host == null) {
                    host = new GameObject("EarthFight");
                    UnityEngine.Object.DontDestroyOnLoad(host);
                    hostShown = true;
                }
                else if (!hostShown) { host.SetActive(true); hostShown = true; }
                Vector3 eye = cam.transform.position;
                DrawStones(now, pc, eye);
                DrawRing(now);
                bool fighters = false;
                for (int i = 0; i < rows.Count && !fighters; i++) fighters = rows[i].Points >= 0;
                if (!fighters) most = 0;   // the next fight says its own full value
                active = fighters || ringHas;
            }
            catch (Exception e) {
                frameRetryAt = now + RetrySeconds;
                try {
                    if (hostShown && host != null) { host.SetActive(false); hostShown = false; }
                }
                catch { }
                if (!frameWarned) { frameWarned = true; MelonLogger.Warning($"[Earth] Fight: drawing fight health failed (said once; tried again every {RetrySeconds:0} s): {e}"); }
            }
        }

        /// <summary>Every fighter and the ring are forgotten and hidden at once (this game left the server); the frame has
        /// no more work until a server speaks again.</summary>
        static void Forget() {
            bool any = ringHas;
            for (int i = 0; i < rows.Count; i++) {
                Row row = rows[i];
                any |= row.Points >= 0;
                row.Points = -1;
                Hide(row);
            }
            ringHas = false;
            HideRing();
            most = 0;
            orderDirty = true;
            topSet = false;
            bandFar = true;
            active = false;
            if (any) MelonLogger.Msg("[Earth] Fight: this game left the server; its fight health and ring are no longer drawn");
        }

        // ---------------------------------------------------------------- the stones

        static void DrawStones(float now, PlayerController pc, Vector3 eye) {
            if (now < stonesRetryAt) return;
            try {
                bool draw = stonesOn && Boulder();
                bool hasOwn = OwnAccount(pc, out int own);
                for (int i = 0; i < rows.Count; i++) {
                    Row row = rows[i];
                    if (row.Points >= 0 && now - row.RefreshedAt > DropSeconds) {
                        if (Logs.Debug) MelonLogger.Msg($"[Earth] Fight: fighter {row.Account} was not heard of for {DropSeconds:0} s; their stones go");
                        row.Points = -1;
                        orderDirty = true;
                    }
                }
                if (orderDirty) Order();
                for (int i = 0; i < rows.Count; i++) {
                    Row row = rows[i];
                    if (row.Points < 0 || !draw || now < row.RetryAt) { Hide(row); continue; }
                    try {
                        bool mine = hasOwn && row.Account == own;
                        Player? p = null;
                        if (!mine) {
                            p = Player.Get(row.Account) as Player;
                            mine = p != null && p.IsLocalPlayer;
                        }
                        if (!(mine ? bandOn && PlaceBand(row, pc, eye, now) : PlaceOverHead(row, p, eye))) Hide(row);
                    }
                    catch (Exception e) {
                        row.RetryAt = now + RetrySeconds;
                        Hide(row);
                        if (!row.Warned) { row.Warned = true; MelonLogger.Warning($"[Earth] Fight: fighter {row.Account}'s stones could not be drawn (said once; tried again every {RetrySeconds:0} s): {e}"); }
                    }
                }
            }
            catch (Exception e) {
                stonesRetryAt = now + RetrySeconds;
                for (int i = 0; i < rows.Count; i++) {
                    try { Hide(rows[i]); }
                    catch { }
                }
                if (!stonesWarned) { stonesWarned = true; MelonLogger.Warning($"[Earth] Fight: the fight health stones failed (said once; tried again every {RetrySeconds:0} s): {e}"); }
            }
        }

        /// <summary>Who hangs higher: every second fighter by account, the local player counted too, so every game puts the
        /// same fighter's row up. Worked out when a fighter came or went.</summary>
        static void Order() {
            orderDirty = false;
            int count = 0;
            for (int i = 0; i < rows.Count && count < accounts.Length; i++)
                if (rows[i].Points >= 0) accounts[count++] = rows[i].Account;
            for (int i = 0; i < rows.Count; i++) {
                Row row = rows[i];
                row.Raised = row.Points >= 0 && FightPips.Raised(FightPips.Rank(row.Account, accounts, count));
            }
        }

        /// <summary>The boulder's mesh and materials (StoneLook's, shared) and its bounds, asked for once per game, at the
        /// first frame that draws a fighter. False without them: one warning, no stones.</summary>
        static bool Boulder() {
            if (boulderMesh != null) return true;
            if (boulderAsked) return false;
            boulderAsked = true;
            if (!StoneLook.Boulder(out Mesh? mesh, out Material[] materials) || mesh == null || materials.Length == 0) {
                MelonLogger.Warning("[Earth] Fight: no boulder mesh in this game, so fight health is not drawn as stones (the ring and the knock-out's blast still are)");
                return false;
            }
            Bounds b = mesh.bounds;
            float max = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            if (!(max > 1e-4f)) {
                MelonLogger.Warning($"[Earth] Fight: the boulder mesh '{mesh.name}' has no size, so fight health is not drawn as stones");
                return false;
            }
            boulderMaterials = materials;
            boulderCentre = b.center;
            boulderMax = max;
            boulderMesh = mesh;
            MelonLogger.Msg($"[Earth] Fight: fight health is drawn as small '{mesh.name}' stones ({b.size.x:0.##} x {b.size.y:0.##} x {b.size.z:0.##} m, its middle {b.center.y:0.##} m over its origin): {pipSize} m in a row {rowHeight} m over a fighter's head (two lines of ten from afar; at most {FightPips.MaxShown} stones a fighter), {bandSize} m in rows of five on your own {(bandRight ? "right" : "left")} forearm ({bandPlaces} places round it, {bandRadius} m out{(bandOn ? "" : "; switched off")})");
            return true;
        }

        static bool OwnAccount(PlayerController pc, out int account) {
            Player? p = pc.NetworkPlayer;
            if (p == null) p = Player.Current as Player;
            if (p != null && p.UserInfo != null) { account = p.UserInfo.Identifier; return true; }
            account = 0;
            return false;
        }

        /// <summary>Another fighter's row: over their head, level, turned to the local camera, grown with the distance (and
        /// then in shorter lines), higher for every second fighter. False when their body is not on this game.</summary>
        static bool PlaceOverHead(Row row, Player? p, Vector3 eye) {
            if (p == null || p.UserInfo == null || p.UserInfo.Identifier != row.Account) return false;
            PlayerController body = p.PlayerController;
            if (body == null || body.Entity == null || body.Entity.IsBeingDestroyed) return false;
            Transform head = body.Head;
            if (head == null) return false;
            Vector3 at = head.position;
            at.y += rowHeight;
            Vector3 to = eye - at;
            float scale = FightPips.FarScale(to.magnitude, farMetres, farScale);
            row.PerLine = FightPips.PerLineAt(scale, row.PerLine);
            at.y += FightPips.RaiseHeight(row.Raised, most, row.PerLine, pipSize, pipGap, groupGap) * scale;
            Show(row, Math.Min(row.Points, FightPips.MaxShown), false, row.PerLine);
            to.y = 0f;
            if (to.sqrMagnitude > 1e-6f) row.RootT.SetPositionAndRotation(at, Quaternion.LookRotation(to, Vector3.up));   // its +Z at the looker, so its +X is the looker's left
            else row.RootT.position = at;
            if (scale != row.Scale) {
                row.Scale = scale;
                row.RootT.localScale = new Vector3(scale, scale, scale);
            }
            return true;
        }

        /// <summary>The local player's own stones: rows of five across one forearm, on the side toward the eyes. False
        /// (nothing drawn) without that controller, or while the band is too near the eyes.</summary>
        static bool PlaceBand(Row row, PlayerController pc, Vector3 eye, float now) {
            Controller c = bandRight ? pc.RightController : pc.LeftController;
            if (c == null) return false;
            int n = Math.Min(row.Points, FightPips.MaxShown);
            float half = (n - 1) / FightPips.Group * bandPitch * 0.5f;   // from the first row to the middle of the rows
            Vector3 axis, start, top;
            if (Forearm(pc, now, out Vector3 w, out axis)) {
                // from the wrist bone, never the tracked controller: the game's hand lags it under a heavy item or at a wall
                start = w + axis * Mathf.Max(0f, bandUp - HandLength);
                top = TowardEyes(eye - (start + axis * half), axis);
            }
            else {
                // no forearm to sit on: the same rows as a small patch facing the eyes, FightBandUp above the controller
                Vector3 hand = c.transform.position, look = eye - hand;
                float distance = look.magnitude;
                if (distance < 0.05f) return false;
                look /= distance;
                axis = Vector3.up - look * look.y;   // up, square to the line of sight
                if (axis.sqrMagnitude < 1e-4f) axis = Vector3.forward - look * look.z;
                axis.Normalize();
                top = look;
                start = hand + axis * bandUp - top * bandRadius;
                topSet = false;
                if (!fallbackSaid) { fallbackSaid = true; MelonLogger.Msg($"[Earth] Fight: the {(bandRight ? "right" : "left")} arm's bones are not known (yet), so your own stones float {bandUp} m above that controller, facing you (said once)"); }
            }
            bandFar = FightPips.BandShown((eye - (start + axis * half + top * bandRadius)).magnitude, bandHide, bandFar);
            if (!bandFar) return false;
            Show(row, n, true, FightPips.PerLine);
            row.RootT.SetPositionAndRotation(start, Quaternion.LookRotation(axis, top));   // +Z toward the elbow, +Y at the eyes
            return true;
        }

        /// <summary>The side of the forearm the band sits on: toward the eyes, square to the forearm. It turns toward a new
        /// side at most <see cref="TurnDegreesPerSecond"/>, and while the eyes look along the forearm (no side is "toward
        /// them") it keeps the side it had: no half turn in one frame when the eyes cross the forearm's line.</summary>
        static Vector3 TowardEyes(Vector3 toEye, Vector3 axis) {
            Vector3 want = toEye - axis * Vector3.Dot(toEye, axis);
            bool clear = want.sqrMagnitude > TopShare * TopShare * toEye.sqrMagnitude && want.sqrMagnitude > 1e-8f;
            Vector3 held = lastTop - axis * Vector3.Dot(lastTop, axis);   // the side it had, on the forearm as it lies now
            bool have = held.sqrMagnitude > 1e-4f;
            Vector3 top;
            if (clear && have && topSet && Time.frameCount - topFrame <= 1) {
                top = Vector3.RotateTowards(held.normalized, want.normalized, TurnDegreesPerSecond * Mathf.Deg2Rad * Time.unscaledDeltaTime, 0f);
                top -= axis * Vector3.Dot(top, axis);
                top = top.sqrMagnitude > 1e-6f ? top.normalized : want.normalized;
            }
            else if (clear) top = want.normalized;   // just shown: straight there
            else if (have) top = held.normalized;
            else {
                top = Vector3.up - axis * axis.y;
                if (top.sqrMagnitude < 1e-4f) top = Vector3.forward - axis * axis.z;
                top.Normalize();
            }
            lastTop = top;
            topSet = true;
            topFrame = Time.frameCount;
            return top;
        }

        /// <summary>The band's forearm from the avatar's bones: the wrist's place and the way from it to the elbow (length
        /// 1). False without the bones, or when they are not a forearm's length apart.</summary>
        static bool Forearm(PlayerController pc, float now, out Vector3 at, out Vector3 axis) {
            at = default;
            axis = default;
            if (!Arm(pc, now) || elbow == null || wrist == null) return false;
            at = wrist.position;
            axis = elbow.position - at;
            float length = axis.magnitude;
            if (!(length >= MinForearm && length <= MaxForearm)) return false;
            axis /= length;
            return true;
        }

        /// <summary>The local body's Elbow and Wrist bones of the band's arm, looked up once per body and arm (again every
        /// 2 s while they are not there, or after one was destroyed).</summary>
        static bool Arm(PlayerController pc, float now) {
            bool same = ReferenceEquals(armOf, pc) && armRight == bandRight;
            if (same && elbow != null && wrist != null) return true;
            if (same && now < armRetryAt) return false;
            armOf = pc;
            armRight = bandRight;
            armRetryAt = now + ArmRetrySeconds;
            elbow = null;
            wrist = null;
            try {
                Controller c = bandRight ? pc.RightController : pc.LeftController;
                Alta.Character.Hand? h = c != null ? c.Hand : null;
                Alta.Character.CharacterComponentControllerCollection? arms = h != null ? h.ArmIKControllers : null;
                Alta.Character.ArmController? arm = arms != null ? arms.GetComponent<Alta.Character.ArmController>() : null;
                if (arm != null) {
                    elbow = arm.Elbow;
                    wrist = arm.Wrist;
                }
            }
            catch (Exception e) {
                if (Logs.Debug) MelonLogger.Msg($"[Earth] Fight: the {(bandRight ? "right" : "left")} arm's bones could not be read: {e.Message}");
            }
            bool ok = elbow != null && wrist != null;
            if (ok && !armSaid) { armSaid = true; MelonLogger.Msg($"[Earth] Fight: your own stones sit on the avatar's {(bandRight ? "right" : "left")} forearm (bones '{wrist!.name}' to '{elbow!.name}')"); }
            return ok;
        }

        /// <summary>The row shows <paramref name="n"/> stones, as a row over a head (<paramref name="perLine"/> to a line) or
        /// as the band; it is laid out again only when the count, the kind, the lines or a setting changed.</summary>
        static void Show(Row row, int n, bool band, int perLine) {
            if (row.Root == null) {
                var root = new GameObject("Fighter");
                root.transform.SetParent(host!.transform, false);
                root.SetActive(false);
                row.Root = root;
                row.RootT = root.transform;
                row.Built = 0;
                row.Laid = -1;
                row.Visible = false;
                row.Scale = 1f;
            }
            if (n != row.Laid || band != row.LaidBand || perLine != row.LaidPer || stamp != row.LaidStamp) Lay(row, n, band, perLine);
            if (!row.Visible) {
                row.Root.SetActive(true);
                row.Visible = true;
            }
        }

        static void Lay(Row row, int n, bool band, int perLine) {
            while (row.Built < n) {
                row.Stones[row.Built] = NewStone(row.RootT);
                row.Built++;
            }
            float size = band ? bandSize : pipSize, fit = size / boulderMax;
            var scale = new Vector3(fit, fit, fit);
            Vector3 middle = boulderCentre * fit;
            for (int i = 0; i < row.Built; i++) {
                Transform stone = row.Stones[i];
                bool on = i < n;
                if (stone.gameObject.activeSelf != on) stone.gameObject.SetActive(on);
                if (!on) continue;
                // each stone its own way up, the same for stone i on every game; the mesh's middle (not its origin, which
                // is at the boulder's foot) on the place
                Quaternion turn = Quaternion.Euler(i * 67f % 360f, i * 139f % 360f, i * 23f % 360f);
                stone.localRotation = turn;
                stone.localScale = scale;
                stone.localPosition = (band ? BandSlot(i) : RowSlot(i, n, perLine)) - turn * middle;
            }
            if (band && row.Scale != 1f) {   // the band never grows
                row.Scale = 1f;
                row.RootT.localScale = Vector3.one;
            }
            row.Laid = n;
            row.LaidBand = band;
            row.LaidPer = perLine;
            row.LaidStamp = stamp;
        }

        // The row's own space: +X the looker's left, +Y up, each further line above the one before.
        static Vector3 RowSlot(int i, int n, int perLine) {
            int line = FightPips.LineOf(i, perLine);
            float x = -FightPips.RowOffset(i - line * perLine, FightPips.InLine(line, n, perLine), pipSize, pipGap, groupGap);
            return new Vector3(x, line * (pipSize + pipGap), 0f);
        }

        // The band's own space: +Z toward the elbow, +Y toward the eyes. Row i / 5 along the arm, five places across the top.
        static Vector3 BandSlot(int i) {
            float a = FightPips.BandAngle(FightPips.BandPlace(i % FightPips.Group, bandPlaces), bandPlaces);
            return new Vector3(Mathf.Sin(a) * bandRadius, Mathf.Cos(a) * bandRadius, i / FightPips.Group * bandPitch);
        }

        static Transform NewStone(Transform parent) {
            var go = new GameObject("Pip");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = boulderMesh;
            MeshRenderer r = go.AddComponent<MeshRenderer>();
            r.sharedMaterials = boulderMaterials;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go.transform;
        }

        static void Hide(Row row) {
            if (!row.Visible) return;
            row.Visible = false;
            if (row.Root != null) row.Root.SetActive(false);
        }

        // ---------------------------------------------------------------- the ring

        static void DrawRing(float now) {
            if (ringHas && now - ringAt > DropSeconds) {
                ringHas = false;
                MelonLogger.Msg($"[Earth] Fight: the server has not said the ring for {DropSeconds:0} s; it is no longer drawn");
            }
            if (!ringHas || ringBroken || now < ringRetryAt) { HideRing(); return; }
            try {
                if (ringRoot == null && !BuildRing()) return;
                if (ringDirty || ringLaidStamp != stamp) LayRing();
                if (!ringShown) {
                    ringRoot!.SetActive(true);
                    ringShown = true;
                }
            }
            catch (Exception e) {
                ringRetryAt = now + RetrySeconds;
                try { HideRing(); }
                catch { }
                if (!ringWarned) { ringWarned = true; MelonLogger.Warning($"[Earth] Fight: the ring could not be drawn (said once; tried again every {RetrySeconds:0} s): {e}"); }
            }
        }

        static void HideRing() {
            if (!ringShown) return;
            ringShown = false;
            if (ringRoot != null) ringRoot.SetActive(false);
        }

        static bool BuildRing() {
            Shader lines = Shader.Find("Sprites/Default");
            if (lines == null) {
                ringBroken = true;
                MelonLogger.Warning("[Earth] Fight: the shader Sprites/Default is not in this game, so a test ring is not drawn");
                return false;
            }
            var root = new GameObject("Ring");
            Material? material = null;
            try {
                root.transform.SetParent(host!.transform, false);
                root.SetActive(false);
                material = new Material(lines) { name = "Earth Fight Ring" };
                for (int k = 0; k < RingDashes; k++) {
                    var go = new GameObject("Dash");
                    go.transform.SetParent(root.transform, false);
                    LineRenderer l = go.AddComponent<LineRenderer>();
                    l.sharedMaterial = material;
                    l.useWorldSpace = true;
                    l.positionCount = 3;
                    l.numCapVertices = 0;
                    l.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    l.receiveShadows = false;
                    dashes[k] = l;
                }
            }
            catch {
                // nothing half-built stays; the next try builds it again
                UnityEngine.Object.Destroy(root);
                if (material != null) UnityEngine.Object.Destroy(material);
                throw;
            }
            ringRoot = root;
            ringShown = false;
            ringDirty = true;
            return true;
        }

        /// <summary>Every dash at its place: three points over half of its 64th of the circle, each on the ground under it.</summary>
        static void LayRing() {
            float slot = Mathf.PI * 2f / RingDashes;
            int missed = 0;
            for (int k = 0; k < RingDashes; k++) {
                LineRenderer l = dashes[k];
                for (int j = 0; j < 3; j++) {
                    float a = (k + j * DashShare * 0.5f) * slot;
                    float x = ringCentre.x + Mathf.Cos(a) * ringRadius, z = ringCentre.z + Mathf.Sin(a) * ringRadius, y = ringCentre.y;
                    if (Physics.Raycast(new Vector3(x, y + RingCastUp, z), Vector3.down, out RaycastHit hit, RingCastUp * 2f, Ground.NaturalLayers, QueryTriggerInteraction.Ignore)) y = hit.point.y;
                    else missed++;
                    l.SetPosition(j, new Vector3(x, y + RingLift, z));
                }
                l.startColor = ringColour;
                l.endColor = ringColour;
                l.widthMultiplier = ringWidth;
            }
            ringMissed = missed;
            ringDirty = false;
            ringLaidStamp = stamp;
            if (ringLays <= RingRelays) ringLays++;
            if (Logs.Debug) MelonLogger.Msg($"[Earth] Fight: the ring's {RingDashes} dashes are laid ({RingDashes * 3 - missed} of {RingDashes * 3} points on natural ground, the rest at the floor's height{(missed > 0 && ringLays > RingRelays ? "; they stay there" : "")})");
        }
    }
}
