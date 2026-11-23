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
    //   4. natural ground under the spot SpawnPlan picks (Ground.UnderPastStones, cast down from the claimed head's height,
    //      looking past our own stones: ruling C3), cast only for a claim that passed 1-3.
    // The head and yaw the stone is placed from are the claim's (what the player saw), once within the slack. Net raises the
    // claim on the game's thread (the archery mod's message handlers run there too), so the spawn happens in the handler. A
    // refusal is one Debug line naming the reason. Then the caps (Caps.Decide: the player's oldest breaks, or a full server
    // refuses); every standing stone the new one's end box overlaps that StoneRules.SummonUnder lifts (Still, Sliding,
    // Stomped) is set loose and thrown up (ruling C4); and the stone is spawned at the plan's start, its top at ground level,
    // kinematic and Rising (StoneBody raises it), or for the Disc loose in the air where it ends (ruling C1). Then its launch
    // (SpawnPlan.Launch turned to the yaw, ruling C2): the Disc's lift at once and its push after the delay, the Ball's throw
    // up once it has risen; the others have none.
    // Every stone is SaveMode.Ignore. Each frame Tick breaks a stone that lived [EarthStones] LifeSeconds, fell below the world,
    // or whose owner is no longer connected (StoneLife); the server's quit breaks them all (Shutdown).
    //
    // Modify claims (Task 7, spec 2.4, 3.5): the game picked the stone (Bend.cs). The server spends the player's cooldown for
    // that modifier (the game's own, less CooldownSlackSeconds) for any claim a game of this mod could send, then checks it
    // (Stones.cs's ModifyCheck): the target is a standing stone of this registry, its box (the collider's bounds here) is in
    // reach of the SERVER's copy of the head and feet looking along the claimed yaw, with [EarthModifiers] Reach, MaxHeight and
    // RangeSlackMetres, and the modifier applies in the stone's state. Then StoneBody.Apply, pushed along the claimed yaw. A
    // refusal is one Debug line naming the reason. Task 11: Kick, Stomp and Uppercut too; their [EarthModifiers] numbers go to
    // StoneRules (SetImpulses), and MaxStoneSpeed holds every modifier's push (ruling C10). Task 12: Parry (a stone floats for
    // ParrySeconds, or until it moves faster than ParryBreakSpeed) and Explode (it marks the stone; a marked stone that breaks
    // blasts everything within ExplodeRadius with ExplodeForce: Hits.Explode), and a second Straight claim on one stone within
    // DoubleStraightSeconds of the last (by anyone: a two-player move) bursts it at once (rulings C8, C26). The server's quit
    // breaks every stone with no blast. Task 13: Hold (StoneHold.cs) with HoldDistance, HoldFreeScale, HoldGroundScale,
    // HoldLetGoSpeed, HoldArmMetres (rulings C27, C28) and PdStepSeconds (ruling C13: spec 2.4's d read per physics step of
    // that many seconds). Task 14: Flick (StoneFlick.cs) with FlickScale, FlickTurnGain and FlickTurnDamp (ruling C15), and
    // Hold's HoldDistance and PdStepSeconds.
    internal static class Server {
        const float DefaultLife = 60f, DefaultHeadSlack = 1f, DefaultCooldownSlack = 0.1f;
        const float DefaultStraightGrounded = 2300f, DefaultStraightFree = 1800f, DefaultRangeSlack = 0.5f, DefaultSlideDeceleration = 4f;
        const int DefaultMaxPerPlayer = 8, DefaultMaxTotal = 64;
        const int StoneLayers = 1 << 0;   // our stones sit on layer 0 (Default: Prefabs never sets one)

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
        static MelonPreferences_Entry<float>? discLiftEntry, discPushEntry, discPushDelayEntry, discHeightEntry, ballLaunchEntry, summonUnderEntry;
        static readonly MelonPreferences_Entry<float>[] aheadEntries = new MelonPreferences_Entry<float>[StoneTable.Count];
        static readonly MelonPreferences_Entry<float>?[] riseEntries = new MelonPreferences_Entry<float>?[StoneTable.Count];   // none for the Disc: it never rises

        static MelonPreferences_Entry<float>? straightGroundedEntry, straightFreeEntry, rangeSlackEntry, doubleStraightEntry, slideDecelerationEntry;
        static MelonPreferences_Entry<float>? kickUpEntry, stompDownEntry, uppercutFreeEntry, uppercutGroundedEntry, uppercutSpinEntry, maxStoneSpeedEntry;
        static MelonPreferences_Entry<float>? parrySecondsEntry, parryBreakSpeedEntry, explodeRadiusEntry, explodeForceEntry;
        static MelonPreferences_Entry<float>? holdDistanceEntry, holdFreeScaleEntry, holdGroundScaleEntry, holdLetGoSpeedEntry, holdArmEntry, pdStepEntry;
        static MelonPreferences_Entry<float>? flickScaleEntry, flickTurnGainEntry, flickTurnDampEntry;

        static bool[] enabled = new bool[StoneTable.Count];
        static int maxPerPlayer = DefaultMaxPerPlayer, maxTotal = DefaultMaxTotal;
        static float life = DefaultLife, headSlack = DefaultHeadSlack, cooldownSlack = DefaultCooldownSlack, rangeSlack = DefaultRangeSlack;
        static bool shutDown, tickFailed, summonFailed, modifyFailed, unspawnableWarned;

        /// <summary>[EarthModifiers] StraightGrounded: Straight's impulse in N s on an anchored stone (Rising, Still,
        /// Sliding), which then slides.</summary>
        internal static float StraightGrounded { get; private set; } = DefaultStraightGrounded;
        /// <summary>[EarthModifiers] StraightFree: Straight's impulse in N s on every other stone.</summary>
        internal static float StraightFree { get; private set; } = DefaultStraightFree;
        /// <summary>[EarthModifiers] DoubleStraightSeconds (rulings C8, C26): a Straight claim on a stone within this many
        /// seconds of its last one by anyone (StoneBody.LastStraightAt) bursts it at once (StoneRules.DoubleStraight).</summary>
        internal static float DoubleStraightSeconds { get; private set; } = StoneRules.DefaultDoubleStraightSeconds;
        /// <summary>[EarthModifiers] SlideDeceleration: how fast a sliding stone slows, in m/s every second.</summary>
        internal static float SlideDeceleration { get; private set; } = DefaultSlideDeceleration;
        /// <summary>[EarthModifiers] MaxStoneSpeed (ruling C10): no modifier leaves a stone faster than this, in m/s
        /// (StoneRules.Pushed).</summary>
        internal static float MaxStoneSpeed { get; private set; } = StoneRules.DefaultMaxStoneSpeed;
        /// <summary>[EarthModifiers] ParrySeconds: a parried (Floating) stone falls again after this many seconds.</summary>
        internal static float ParrySeconds { get; private set; } = StoneRules.DefaultParrySeconds;
        /// <summary>[EarthModifiers] ParryBreakSpeed: a parried stone falls again once it moves faster than this, in m/s.</summary>
        internal static float ParryBreakSpeed { get; private set; } = StoneRules.DefaultParryBreakSpeed;
        /// <summary>[EarthModifiers] ExplodeRadius: a marked stone's blast reaches this many metres from its centre.</summary>
        internal static float ExplodeRadius { get; private set; } = StoneRules.DefaultExplodeRadius;
        /// <summary>[EarthModifiers] ExplodeForce: the blast's push on stones and loose rigidbodies, in N s (x its share).</summary>
        internal static float ExplodeForce { get; private set; } = StoneRules.DefaultExplodeForce;
        /// <summary>[EarthModifiers] HoldDistance: a held stone is pulled to this many metres ahead of the holding hand (Pd.HoldTarget).</summary>
        internal static float HoldDistance { get; private set; } = Pd.DefaultHoldDistance;
        /// <summary>[EarthModifiers] HoldFreeScale: the hold's PD output times this on a loose (Held) stone, in N per metre off.</summary>
        internal static float HoldFreeScale { get; private set; } = Pd.DefaultHoldFreeScale;
        /// <summary>[EarthModifiers] HoldGroundScale: the same on a held slide (along the ground only).</summary>
        internal static float HoldGroundScale { get; private set; } = Pd.DefaultHoldGroundScale;
        /// <summary>[EarthModifiers] HoldLetGoSpeed: a held stone faster than this many m/s is let go (thrown), once armed.</summary>
        internal static float HoldLetGoSpeed { get; private set; } = Pd.DefaultHoldLetGoSpeed;
        /// <summary>[EarthModifiers] HoldArmMetres (ruling C28): a hold's throw counts once the stone has been this near its
        /// target at HoldLetGoSpeed or slower (StoneRules.HoldArmed).</summary>
        internal static float HoldArmMetres { get; private set; } = Pd.DefaultHoldArmMetres;
        /// <summary>[EarthModifiers] PdStepSeconds (ruling C13): spec 2.4's d is the error's change per physics step of this
        /// many seconds (Pd.Hold).</summary>
        internal static float PdStepSeconds { get; private set; } = Pd.DefaultPdStepSeconds;
        /// <summary>[EarthModifiers] FlickScale (ruling C15): the flick's PID output times this, in N per metre off (Pd.Flick).</summary>
        internal static float FlickScale { get; private set; } = Pd.DefaultFlickScale;
        /// <summary>[EarthModifiers] FlickTurnGain: a flicked stone is turned this many rad/s every second per radian off the
        /// hands' turn (Pd.FlickTorque).</summary>
        internal static float FlickTurnGain { get; private set; } = Pd.DefaultFlickTurnGain;
        /// <summary>[EarthModifiers] FlickTurnDamp: the turn is slowed this many rad/s every second per rad/s of the stone's
        /// spin.</summary>
        internal static float FlickTurnDamp { get; private set; } = Pd.DefaultFlickTurnDamp;

        /// <summary>From Main.OnInitializeMelon on the dedicated server only, after Prefabs.Init (which made [EarthStones]).</summary>
        internal static void Init() {
            MelonPreferences_Category cat = Cfg.Category("EarthStones");
            enabledEntry = cat.CreateEntry("Enabled", StoneKinds.DefaultEnabled, description: "Server. The stones a pose may summon, by name, split by commas: Disc, Ball, Pillar, Cube, Wall. A claim for any other is refused (KindOff).");
            maxPerPlayerEntry = cat.CreateEntry("MaxPerPlayer", DefaultMaxPerPlayer, description: "Server. The most stones one player has standing; summoning one more breaks their oldest. 1 or more.");
            maxTotalEntry = cat.CreateEntry("MaxTotal", DefaultMaxTotal, description: "Server. The most stones standing on the server; a summon past it is refused (unless that player is at MaxPerPlayer: then their oldest breaks). 1 or more.");
            lifeEntry = cat.CreateEntry("LifeSeconds", DefaultLife, description: "Server. A stone breaks this many seconds after it was summoned. Above 0.");
            headSlackEntry = cat.CreateEntry("HeadSlackMetres", DefaultHeadSlack, description: "Server. A summon is refused (HeadFar) when the head its game claims is further than this from the server's copy of that player's head, in metres (the server's copy lags; a fast run needs more). 0 or more.");
            cooldownSlackEntry = cat.CreateEntry("CooldownSlackSeconds", DefaultCooldownSlack, description: "Server. A player's summons of one stone, or uses of one modifier, may come this much closer together than the game's own cooldown (0.3 s for a stone, 0.3 to 0.8 s for a modifier) before the server refuses one (Cooldown): two claims sent a cooldown apart can arrive closer. 0 or more.");
            for (int i = 0; i < StoneTable.Count; i++) {
                var k = (StoneKind)i;
                aheadEntries[i] = cat.CreateEntry(k + "AheadMetres", SpawnPlan.DefaultAhead(k), description: $"Server. A summoned {k} {(SpawnPlan.Rises(k) ? "rises" : "appears")} this many metres in front of the head (spec 3.4). 0 or more.");
                if (SpawnPlan.Rises(k))
                    riseEntries[i] = cat.CreateEntry(k + "RiseSeconds", SpawnPlan.DefaultRise(k), description: $"Server. A summoned {k} rises out of the ground over this many seconds (0: it is up at once). 0 or more.");
            }
            discHeightEntry = cat.CreateEntry("DiscHeightMetres", SpawnPlan.DefaultDiscHeight, description: "Server. A summoned Disc does not rise: it appears loose with its centre this many metres above the ground, and is launched at once (DiscLift, then DiscPush). 0 or more; under 0.07 (half its thickness) it starts partly in the ground.");
            discLiftEntry = cat.CreateEntry("DiscLift", SpawnPlan.DefaultDiscLift, description: "Server. A summoned Disc is pushed straight up with this impulse as it appears, in N s (a 100 kg Disc: 300 is 3 m/s). 0 or more.");
            discPushEntry = cat.CreateEntry("DiscPush", SpawnPlan.DefaultDiscPush, description: "Server. DiscPushDelaySeconds after its lift, a summoned Disc is pushed along the look with this impulse, in N s (a 100 kg Disc: 1400 is 14 m/s). A modifier on the Disc, or its break, before then cancels this push. 0 or more.");
            discPushDelayEntry = cat.CreateEntry("DiscPushDelaySeconds", SpawnPlan.DefaultDiscPushDelay, description: "Server. The time from a summoned Disc's lift to its push, in seconds (counted in the server's physics steps: the push lands in the first step at least this long after the lift). 0 or more.");
            ballLaunchEntry = cat.CreateEntry("BallLaunchSpeed", SpawnPlan.DefaultBallLaunchSpeed, description: "Server. A summoned Ball rises out of the ground over BallRiseSeconds, then is loose and thrown straight up at this speed, in m/s (5: about 1.6 m higher with the game's gravity). 0: it is loose where it rose, not thrown.");
            summonUnderEntry = cat.CreateEntry("SummonUnderLift", SpawnPlan.DefaultSummonUnderLift, description: "Server. A stone summoned where another stands (still, sliding or stomped) sets that one loose and throws it up at this speed, in m/s. 0: it is set loose but not thrown.");
            MelonPreferences_Category mods = Cfg.Category("EarthModifiers");   // Reach and MaxHeight are ReachSettings' (both sides)
            straightGroundedEntry = mods.CreateEntry("StraightGrounded", DefaultStraightGrounded, description: "Server. Straight pushes a stone anchored to the ground (standing still, rising or sliding) along the look with this impulse, in N s (a 275 kg Pillar: 2300 is 8.4 m/s); it slides along the ground until it stops. 0 or more.");
            straightFreeEntry = mods.CreateEntry("StraightFree", DefaultStraightFree, description: "Server. Straight pushes any other stone (loose, in the air) along the look with this impulse, in N s. 0 or more.");
            rangeSlackEntry = mods.CreateEntry("RangeSlackMetres", DefaultRangeSlack, description: "Server. A modifier's stone is checked against Reach and MaxHeight from the server's copy of the head and feet, which lags the player: both get this many metres more on the server. 0 or more.");
            doubleStraightEntry = mods.CreateEntry("DoubleStraightSeconds", StoneRules.DefaultDoubleStraightSeconds, description: "Server. Two Straights on one stone within this many seconds (by anyone) burst it: the second one breaks it at once instead of pushing it, and it blasts as a stone marked by Explode does (ExplodeRadius, ExplodeForce), even while it rises. One player's own Straight cooldown (0.5 s) is longer than this, so it takes two players: a friend Straights a stone at you and you Straight it straight back. 0 or more (0: only two Straights in the same server frame).");
            slideDecelerationEntry = mods.CreateEntry("SlideDeceleration", DefaultSlideDeceleration, description: "Server. A stone sliding along the ground after a Straight slows by this many m/s every second while it touches ground (it slides on a frictionless material, so the ground itself does not slow it), and stands still again under 0.1 m/s (8.4 m/s with 4: about 9 m on level ground; uphill shorter, downhill longer). 0: only a rise or what it hits stops it.");
            kickUpEntry = mods.CreateEntry("KickUp", StoneRules.DefaultKickUp, description: "Server. Kick pushes a stone straight up with this impulse, in N s (a 300 kg Cube: 1500 is 5 m/s); a stone anchored to the ground (standing still, sliding or stomped) is set loose first. 0 or more.");
            stompDownEntry = mods.CreateEntry("StompDown", StoneRules.DefaultStompDown, description: "Server. Stomp on a loose stone: from then on it moves only up and down (no sideways, no turning) and is pushed down with this impulse, in N s (a 150 kg Ball: 1500 is 10 m/s); where it lands (on the ground, a floor or another stone's top) it stands still, with a slam. Stomp on a stone anchored to the ground does nothing. 0 or more.");
            uppercutFreeEntry = mods.CreateEntry("UppercutFree", StoneRules.DefaultUppercutFree, description: "Server. Uppercut on a loose stone pushes it with this impulse forward along the look and as much up, in N s (a 150 kg Ball: 900 is 6 m/s each way). 0 or more.");
            uppercutGroundedEntry = mods.CreateEntry("UppercutGrounded", StoneRules.DefaultUppercutGrounded, description: "Server. Uppercut on a stone anchored to the ground (standing still, sliding or stomped) sets it loose and pushes it with this impulse forward along the look and as much up, in N s, and spins it (UppercutSpin) (a 275 kg Pillar: 1300 is 4.7 m/s each way). 0 or more.");
            uppercutSpinEntry = mods.CreateEntry("UppercutSpin", StoneRules.DefaultUppercutSpin, description: "Server. Uppercut on a stone anchored to the ground also spins it this many radians a second about the axis across the look, its top tipping forward (5: about 0.8 turns a second). 0 or more.");
            maxStoneSpeedEntry = mods.CreateEntry("MaxStoneSpeed", StoneRules.DefaultMaxStoneSpeed, description: "Server. No modifier (Straight, Kick, Stomp, Uppercut) and no blast leaves a stone faster than this, in m/s: each push adds its impulse over the stone's mass to the speed it already has, and the sum is held to this (a flying Disc Straighted again stops at 20). A blast adds no more than this to a loose object's speed either. Above 0.");
            parrySecondsEntry = mods.CreateEntry("ParrySeconds", StoneRules.DefaultParrySeconds, description: "Server. Parry stops a stone dead where it is and makes it float, with no gravity (a stone anchored to the ground or stomped is set loose first). It falls again after this many seconds, or as soon as anything moves it faster than ParryBreakSpeed (a Straight on a floating stone sends it flying; Kick and Uppercut set it loose; Stomp stomps it). Parry again starts the time over. 0 or more.");
            parryBreakSpeedEntry = mods.CreateEntry("ParryBreakSpeed", StoneRules.DefaultParryBreakSpeed, description: "Server. A floating (parried) stone falls again once it moves faster than this, in m/s. 0 or more.");
            explodeRadiusEntry = mods.CreateEntry("ExplodeRadius", StoneRules.DefaultExplodeRadius, description: "Server. Explode marks a stone. When a marked stone breaks (a hit, a clash, its life running out, its owner leaving, a ninth stone; not at the server's quit or by earth clear) it blasts everything within this many metres of its centre, all of it within half this distance and falling to a quarter at its edge: players are knocked back away from it ([EarthDamage] KnockbackSpeed, only with HurtPlayers), creatures are staggered (cripple 1, no damage), loose objects and stones are pushed away (ExplodeForce; anchored stones are set loose first, rising ones are left alone). A blast never breaks a stone. 0 or more (0: no blast).");
            explodeForceEntry = mods.CreateEntry("ExplodeForce", StoneRules.DefaultExplodeForce, description: "Server. A blast pushes each stone and loose object in it away from its centre with this impulse, in N s, times its share (a 300 kg Cube in the middle: 1000 is 3.3 m/s); never past MaxStoneSpeed. 0 or more.");
            holdDistanceEntry = mods.CreateEntry("HoldDistance", Pd.DefaultHoldDistance, description: "Server. Hold makes a stone follow the holding hand (the top hand of the pose) this many metres ahead of it: along the way the hand points, made flat, or along the look when the hand points within 45 degrees of straight up or down (as the Hold pose's fingers do). Flick hangs its stone this many metres ahead of the point between the two hands, along the look made flat. Both follow the server's copy of the hands, which lags the real ones a little. 0: at the hand (between the hands) itself. 0 or more.");
            holdFreeScaleEntry = mods.CreateEntry("HoldFreeScale", Pd.DefaultHoldFreeScale, description: "Server. How hard Hold pulls a loose stone toward its place ahead of the hand: the pull is this many N for each metre it is off, less a damping by its weight (spec 2.4's d: 8 for a Ball, up to 18 for the heaviest), and its gravity is off while it is held. Higher follows tighter and swings more. 0: no pull (it floats where it is). 0 or more.");
            holdGroundScaleEntry = mods.CreateEntry("HoldGroundScale", Pd.DefaultHoldGroundScale, description: "Server. The same for a stone standing on the ground when it is held: it slides along the ground after the hand (pulled only sideways, gravity on), and while held it neither slows down nor stands still. 0 or more.");
            holdLetGoSpeedEntry = mods.CreateEntry("HoldLetGoSpeed", Pd.DefaultHoldLetGoSpeed, description: "Server. A held stone is let go, keeping its speed (a throw), once it moves faster than this many m/s (never past MaxStoneSpeed), but only after it has settled near the hand: once, it was within HoldArmMetres of its place ahead of the hand and this slow or slower at the same moment. So a stone pulled in from afar or caught flying is not thrown by its own rush to the hand; then a swing of the holding arm throws it. At 5, a quick swing (a quarter second) throws a loose Ball with about 2 m of hand travel, a Pillar or Cube 2.5 m, a Wall 3 m; a stone held sliding on the ground needs more (a Ball 2.3 m, a Pillar or Cube 3 m, a Wall 3.5 m), more than an arm swings: strike Hold again to let it slide on instead. Lower throws more easily (and lets go on a quick turn of the body sooner). It is also let go at once when a held sliding stone leaves the ground, when Kick, Uppercut, Stomp or Parry is used on it (Straight and Explode keep it held), when its holder dies, is downed or leaves, when that hand holds another stone, and on Hold with the same hand again. Above 0.");
            holdArmEntry = mods.CreateEntry("HoldArmMetres", Pd.DefaultHoldArmMetres, description: "Server. A held stone can be thrown (HoldLetGoSpeed) only once it has settled near the hand: within this many metres of its place ahead of the hand (measured flat for a stone sliding on the ground) while no faster than HoldLetGoSpeed, at the same moment. Until then it is only pulled in. Above 0.");
            pdStepEntry = mods.CreateEntry("PdStepSeconds", Pd.DefaultPdStepSeconds, description: "Server. How Hold and Flick read spec 2.4's damping d: as the stone's change over one physics step of this many seconds (0.02, RUMBLE's 50 steps a second). A held Ball trails a hand moving 1 m/s by about 8 x this (0.16 m). Higher damps more and trails further; 1 (d per second) makes the server's physics run away. 0: no damping (it swings until it is thrown). 0 or more.");
            flickScaleEntry = mods.CreateEntry("FlickScale", Pd.DefaultFlickScale, description: "Server. How hard Flick pulls a loose stone to its place between the hands (HoldDistance ahead of the point between the two hands, along the look): this many N for each metre it is off, plus a pull that grows the longer it stays off (spec 2.4's i, 0.1 a second), less a damping by its weight (spec 2.4's d: 6 for a Disc or Ball, 7 for a Pillar or Cube, 8.5 for a Wall, read per PdStepSeconds); its gravity is off while flicked. At 1000 a Ball swings to its place about every 2.4 s and a Wall every 4 s, and a quick sweep of the hands leaves it behind; higher follows tighter and quicker. Only Stomp or a break drops a flicked stone. 0: no pull (it floats where it is). 0 or more.");
            flickTurnGainEntry = mods.CreateEntry("FlickTurnGain", Pd.DefaultFlickTurnGain, description: "Server. How hard Flick turns its stone to match the hands: the stone's up runs from the lower hand of the pose to the upper one and it faces along the look, so tilting the hands tilts it. It is turned this many rad/s every second for each radian it is off, the same on every stone whatever its weight. Higher turns quicker and swings past more. 0: no turn. 0 or more.");
            flickTurnDampEntry = mods.CreateEntry("FlickTurnDamp", Pd.DefaultFlickTurnDamp, description: "Server. How much Flick's turn is slowed by the stone's own spin, in rad/s every second for each rad/s. Higher settles the turn with less swinging past: with FlickTurnGain 8, 4 follows a quick quarter turn of the hands in about a second, swings past it by about 3 degrees and settles within 2 s; 1 swings past by about half (some 50 degrees) and wobbles for seconds; about 5.7 or more never swings past but follows more slowly. 0: it swings on. 0 or more.");
            Apply();
            Cfg.Changed += Apply;
            Hits.Init();   // [EarthDamage]: what a moving stone does to creatures, players and other stones (Task 8)
            // a Net whose static initialiser failed (Main says so) throws on every touch: no summons, the rest still starts
            try {
                Net.SummonReceived += OnSummon;
                Net.ModifyReceived += OnModify;
            }
            catch (Exception e) { MelonLogger.Warning($"[Earth] Stones: no summons or modifiers (message 19 is not set up: {e.GetType().Name})"); }
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
            StraightGrounded = Cfg.NotNegative(straightGroundedEntry, "EarthModifiers", "StraightGrounded", DefaultStraightGrounded);
            StraightFree = Cfg.NotNegative(straightFreeEntry, "EarthModifiers", "StraightFree", DefaultStraightFree);
            rangeSlack = Cfg.NotNegative(rangeSlackEntry, "EarthModifiers", "RangeSlackMetres", DefaultRangeSlack);
            DoubleStraightSeconds = Cfg.NotNegative(doubleStraightEntry, "EarthModifiers", "DoubleStraightSeconds", StoneRules.DefaultDoubleStraightSeconds);
            SlideDeceleration = Cfg.NotNegative(slideDecelerationEntry, "EarthModifiers", "SlideDeceleration", DefaultSlideDeceleration);
            StoneRules.SetImpulses(
                kickUp: Cfg.NotNegative(kickUpEntry, "EarthModifiers", "KickUp", StoneRules.DefaultKickUp),
                stompDown: Cfg.NotNegative(stompDownEntry, "EarthModifiers", "StompDown", StoneRules.DefaultStompDown),
                uppercutFree: Cfg.NotNegative(uppercutFreeEntry, "EarthModifiers", "UppercutFree", StoneRules.DefaultUppercutFree),
                uppercutGrounded: Cfg.NotNegative(uppercutGroundedEntry, "EarthModifiers", "UppercutGrounded", StoneRules.DefaultUppercutGrounded),
                uppercutSpin: Cfg.NotNegative(uppercutSpinEntry, "EarthModifiers", "UppercutSpin", StoneRules.DefaultUppercutSpin));
            MaxStoneSpeed = Cfg.Positive(maxStoneSpeedEntry, "EarthModifiers", "MaxStoneSpeed", StoneRules.DefaultMaxStoneSpeed);
            ParrySeconds = Cfg.NotNegative(parrySecondsEntry, "EarthModifiers", "ParrySeconds", StoneRules.DefaultParrySeconds);
            ParryBreakSpeed = Cfg.NotNegative(parryBreakSpeedEntry, "EarthModifiers", "ParryBreakSpeed", StoneRules.DefaultParryBreakSpeed);
            ExplodeRadius = Cfg.NotNegative(explodeRadiusEntry, "EarthModifiers", "ExplodeRadius", StoneRules.DefaultExplodeRadius);
            ExplodeForce = Cfg.NotNegative(explodeForceEntry, "EarthModifiers", "ExplodeForce", StoneRules.DefaultExplodeForce);
            HoldDistance = Cfg.NotNegative(holdDistanceEntry, "EarthModifiers", "HoldDistance", Pd.DefaultHoldDistance);
            HoldFreeScale = Cfg.NotNegative(holdFreeScaleEntry, "EarthModifiers", "HoldFreeScale", Pd.DefaultHoldFreeScale);
            HoldGroundScale = Cfg.NotNegative(holdGroundScaleEntry, "EarthModifiers", "HoldGroundScale", Pd.DefaultHoldGroundScale);
            HoldLetGoSpeed = Cfg.Positive(holdLetGoSpeedEntry, "EarthModifiers", "HoldLetGoSpeed", Pd.DefaultHoldLetGoSpeed);
            HoldArmMetres = Cfg.Positive(holdArmEntry, "EarthModifiers", "HoldArmMetres", Pd.DefaultHoldArmMetres);
            PdStepSeconds = Cfg.NotNegative(pdStepEntry, "EarthModifiers", "PdStepSeconds", Pd.DefaultPdStepSeconds);
            FlickScale = Cfg.NotNegative(flickScaleEntry, "EarthModifiers", "FlickScale", Pd.DefaultFlickScale);
            FlickTurnGain = Cfg.NotNegative(flickTurnGainEntry, "EarthModifiers", "FlickTurnGain", Pd.DefaultFlickTurnGain);
            FlickTurnDamp = Cfg.NotNegative(flickTurnDampEntry, "EarthModifiers", "FlickTurnDamp", Pd.DefaultFlickTurnDamp);
            for (int i = 0; i < StoneTable.Count; i++) {
                var k = (StoneKind)i;
                SpawnPlan.Set(k, Cfg.NotNegative(aheadEntries[i], "EarthStones", k + "AheadMetres", SpawnPlan.DefaultAhead(k)),
                    Cfg.NotNegative(riseEntries[i], "EarthStones", k + "RiseSeconds", SpawnPlan.DefaultRise(k)));
            }
            SpawnPlan.SetLaunch(
                discLift: Cfg.NotNegative(discLiftEntry, "EarthStones", "DiscLift", SpawnPlan.DefaultDiscLift),
                discPush: Cfg.NotNegative(discPushEntry, "EarthStones", "DiscPush", SpawnPlan.DefaultDiscPush),
                discPushDelaySeconds: Cfg.NotNegative(discPushDelayEntry, "EarthStones", "DiscPushDelaySeconds", SpawnPlan.DefaultDiscPushDelay),
                discHeightMetres: Cfg.NotNegative(discHeightEntry, "EarthStones", "DiscHeightMetres", SpawnPlan.DefaultDiscHeight),
                ballLaunchSpeed: Cfg.NotNegative(ballLaunchEntry, "EarthStones", "BallLaunchSpeed", SpawnPlan.DefaultBallLaunchSpeed),
                summonUnderLift: Cfg.NotNegative(summonUnderEntry, "EarthStones", "SummonUnderLift", SpawnPlan.DefaultSummonUnderLift));
            var on = new List<string>();
            for (int i = 0; i < StoneTable.Count; i++) if (enabled[i]) on.Add(((StoneKind)i).ToString());
            MelonLogger.Msg($"[Earth] Stones: summons of {(on.Count == 0 ? "no stone" : string.Join(", ", on.ToArray()))}; at most {maxPerPlayer} a player and {maxTotal} in all, each for {life:0.#} s");
            MelonLogger.Msg($"[Earth] Modifiers: Straight {StraightGrounded:0.#} N s on an anchored stone (it slides, slowing {SlideDeceleration:0.##} m/s every second), {StraightFree:0.#} on a loose one; reach {ReachSettings.Reach:0.##} m, at most {ReachSettings.MaxHeight:0.##} m above the feet, {rangeSlack:0.##} m of slack on the server");
            MelonLogger.Msg($"[Earth] Modifiers: Kick {StoneRules.KickUp:0.#} N s up; Stomp {StoneRules.StompDown:0.#} down on a loose stone; Uppercut {StoneRules.UppercutFree:0.#} forward and up on a loose stone, {StoneRules.UppercutGrounded:0.#} each way and {StoneRules.UppercutSpin:0.##} rad/s of spin on an anchored one; no push leaves a stone faster than {MaxStoneSpeed:0.##} m/s");
            MelonLogger.Msg($"[Earth] Modifiers: Parry floats a stone {ParrySeconds:0.##} s (or until it moves faster than {ParryBreakSpeed:0.##} m/s); a stone marked by Explode, or Straighted twice within {DoubleStraightSeconds:0.##} s, blasts {ExplodeRadius:0.##} m round it with {ExplodeForce:0.#} N s when it breaks");
            MelonLogger.Msg($"[Earth] Modifiers: Hold pulls a stone {HoldDistance:0.##} m ahead of the holding hand (x {HoldFreeScale:0.#} loose, x {HoldGroundScale:0.#} along the ground, d per {PdStepSeconds:0.###} s) and lets it go past {HoldLetGoSpeed:0.##} m/s once it has been within {HoldArmMetres:0.##} m of its place at that speed or slower");
            MelonLogger.Msg($"[Earth] Modifiers: Flick hangs a loose stone {HoldDistance:0.##} m ahead of the hands (x {FlickScale:0.#}, d per {PdStepSeconds:0.###} s) and turns it with them (x {FlickTurnGain:0.##}, damped {FlickTurnDamp:0.##}); only Stomp or a break drops it");
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
            bool cooldownOk = kindOn && ledger.TrySpend(account, (int)SummonCheck.MoveOf(c.Kind), Time.unscaledTimeAsDouble, CooldownOf(SummonCheck.MoveOf(c.Kind)));
            SummonRefusal why = SummonCheck.Decide(in c, serverHead, true, kindOn, cooldownOk, headSlack);
            string ground = "";
            (V3 start, V3 end, float riseSeconds, float yawDeg) plan = default;
            if (why == SummonRefusal.None) {
                V3 spot = SpawnPlan.For(c.Kind, c.Head, c.Yaw, 0f).end;   // where it would stand; its height comes from the cast
                bool natural = Ground.UnderPastStones(new Vector3(spot.X, c.Head.Y, spot.Z), out RaycastHit hit);   // ruling C3
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

            // what Spawn needs (a registered prefab, a chunk at the end point) BEFORE the caps break anyone's stone for it
            if (!Spawnable(c.Kind, U(plan.end), out _, out _, out string cannot)) {
                if (!unspawnableWarned) { unspawnableWarned = true; MelonLogger.Warning($"[Earth] Summon: refused {c.Kind} for {NameOf(p)}: {cannot} (said once; later ones are Debug lines)"); }
                else if (Logs.Debug) MelonLogger.Msg($"[Earth] Summon: refused {c.Kind} for {NameOf(p)}: {cannot}");
                return;
            }

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

            Quaternion yaw = Quaternion.Euler(0f, plan.yawDeg, 0f);
            int lifted = LiftOff(c.Kind, U(plan.end), yaw);   // ruling C4: after the caps, so a refused summon lifts nothing
            bool rises = SpawnPlan.Rises(c.Kind);
            StoneBody? b = Spawn(c.Kind, U(plan.start), yaw, p, rises ? StoneState.Rising : StoneState.Free, U(plan.end), plan.riseSeconds);
            if (b == null) return;
            (V3 first, float delay, V3 second) launch = SpawnPlan.Launch(c.Kind);   // ruling C2: in the yaw frame
            b.Launch(yaw * U(launch.first), launch.delay, yaw * U(launch.second));
            if (Logs.Debug)
                MelonLogger.Msg($"[Earth] Summon: {c.Kind} #{b.Id} for {b.OwnerName}, {(rises ? $"rising from {plan.start} to {plan.end} over {plan.riseSeconds:0.##} s" : $"loose at {plan.end}")} (heads {(c.Head - serverHead).Length:0.##} m apart{ground}{(lifted > 0 ? $"; lifted {lifted} standing stone(s) off the spot" : "")}); {owned.Count + 1} of theirs, {Stones.Count} in all");
        }

        /// <summary>Ruling C4: every standing stone the new <paramref name="kind"/>'s collider at <paramref name="end"/>, turned
        /// by <paramref name="rot"/>, overlaps is lifted off when StoneRules.SummonUnder says so (StoneBody.LiftOff). How many were.</summary>
        static int LiftOff(StoneKind kind, Vector3 end, Quaternion rot) {
            Vector3 size = U(StoneTable.Of(kind).Size);
            Collider[] under = kind == StoneKind.Ball
                ? Physics.OverlapSphere(end, size.x * 0.5f, StoneLayers, QueryTriggerInteraction.Ignore)
                : Physics.OverlapBox(end, size * 0.5f, rot, StoneLayers, QueryTriggerInteraction.Ignore);
            int lifted = 0;
            foreach (Collider col in under) {
                StoneBody? s = col != null ? col.GetComponentInParent<StoneBody>() : null;
                if (s != null && s.LiftOff()) lifted++;
            }
            return lifted;
        }

        // One Debug line; nothing is built without [EarthLog] Debug.
        static void Refused(Player p, in SummonClaim c, SummonRefusal why, V3? serverHead, string ground) {
            if (!Logs.Debug) return;
            string kind = StoneTable.Valid(c.Kind) ? c.Kind.ToString() : "kind " + (byte)c.Kind;
            string heads = serverHead is V3 h ? $" (claimed head {c.Head}, the server's {h}, {(c.Head - h).Length:0.##} m apart{ground})" : " (no body on the server)";
            MelonLogger.Msg($"[Earth] Summon: refused {kind} for {NameOf(p)}: {why}{heads}");
        }

        /// <summary>The server's cooldown for a move: the game's own (PoseValues.Default) less CooldownSlackSeconds.</summary>
        static float CooldownOf(Move m) => Math.Max(0f, poseDefaults.CooldownOf(m) - cooldownSlack);

        // ---------------------------------------------------------------- a Modify claim

        static void OnModify(Player p, ModifyClaim c) {
            if (shutDown || p == null) return;
            try { Modify(p, c); }
            catch (Exception e) {
                if (!modifyFailed) { modifyFailed = true; MelonLogger.Warning($"[Earth] Modifiers: a modifier failed (said once): {e}"); }
                else if (Logs.Debug) MelonLogger.Msg($"[Earth] Modifiers: a modifier failed again: {e.Message}");
            }
        }

        static void Modify(Player p, ModifyClaim c) {
            PlayerController pc = p.PlayerController;
            Transform? headT = pc != null ? pc.Head : null;
            if (p.UserInfo == null || headT == null) { RefusedModify(p, c, ModifyRefusal.OutOfReach, null, null, 0f); return; }
            ulong account = AccountOf(p);
            V3 serverHead = V(headT.position);
            float feetY = pc!.RealFeetPosition.y;

            // the ledger first (any claim a game of this mod could send spends that modifier's cooldown, as the game's own
            // fired pose has), then the check against the server's own head and feet
            bool sane = ModifyCheck.Sane(in c);
            bool cooldownOk = sane && ledger.TrySpend(account, (int)ModifyCheck.MoveOf(c.Mod), Time.unscaledTimeAsDouble, CooldownOf(ModifyCheck.MoveOf(c.Mod)));
            StoneBody? b = sane && Stones.TryGetValue(c.Target, out StoneBody found) && found != null && !found.Broken ? found : null;
            Bounds box = b != null ? b.Box : default;
            ModifyRefusal why = ModifyCheck.Decide(in c, cooldownOk, b != null ? b.State : (StoneState?)null, V(box.min), V(box.max), serverHead, feetY,
                ReachSettings.Reach, ReachSettings.MaxHeight, rangeSlack);
            if (why != ModifyRefusal.None || b == null) { RefusedModify(p, c, why, b, serverHead, feetY); return; }

            StoneState was = b.State;
            b.Apply(c.Mod, p, c.Hand, U(SpawnPlan.Forward(c.Yaw)));
            if (Logs.Debug)
                MelonLogger.Msg($"[Earth] Modify: {c.Mod} on {b.Kind} #{b.Id} of {b.OwnerName} by {NameOf(p)}: {was} -> {(b.Broken ? "burst (a double Straight)" : b.State.ToString())}{(b.Queued != null && !b.Broken ? " (held until it has risen)" : "")}{(c.Mod == Modifier.Explode ? " (marked)" : "")} along yaw {c.Yaw:0.#} (its box {Reach.Nearest(serverHead, V(box.min), V(box.max)):0.##} m from the server's head; heads {(c.Head - serverHead).Length:0.##} m apart)");
        }

        // One Debug line; nothing is built without [EarthLog] Debug.
        static void RefusedModify(Player p, in ModifyClaim c, ModifyRefusal why, StoneBody? b, V3? serverHead, float feetY) {
            if (!Logs.Debug) return;
            string mod = (int)c.Mod < ModifyCheck.ModifierCount ? c.Mod.ToString() : "modifier " + (byte)c.Mod;
            string stone = b != null ? $"{b.Kind} #{b.Id} ({b.State})" : $"#{c.Target}";
            string where = serverHead is V3 h
                ? $" (hand {c.Hand}, yaw {c.Yaw:0.#}, claimed head {c.Head}, the server's {h} and feet {feetY:0.##}{(b != null ? $", the stone's box {V(b.Box.min)} to {V(b.Box.max)}" : "")})"
                : " (no body on the server)";
            MelonLogger.Msg($"[Earth] Modify: refused {mod} on {stone} for {NameOf(p)}: {why}{where}");
        }

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
            if (!Spawnable(kind, end, out NetworkPrefab? prefab, out LocationChunk? chunk, out string cannot) || prefab == null || chunk == null) {
                MelonLogger.Warning($"[Earth] Spawn: refused, {cannot}");
                return null;
            }
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

        /// <summary>What <see cref="Spawn"/> needs for a stone that ends at <paramref name="end"/>: the kind's registered prefab
        /// and the chunk that holds the end (not the start: that may be under the chunk's ground). False, with why, when either
        /// is missing.</summary>
        static bool Spawnable(StoneKind kind, Vector3 end, out NetworkPrefab? prefab, out LocationChunk? chunk, out string why) {
            chunk = null;
            prefab = Prefabs.Of(kind);
            if (prefab == null) { why = $"no registered prefab for {kind}"; return false; }
            chunk = LocationChunkHelper.GetBestContaining(end);
            if (chunk == null) { why = $"no chunk holds {end} for the {kind}"; return false; }
            why = "";
            return true;
        }

        /// <summary>From StoneBody (its Break, or its destroy by anything else): it no longer stands.</summary>
        internal static void Forget(StoneBody b) {
            if (b == null) return;
            Hits.Forget(b.Id);   // its hit record (Task 8)
            if (Stones.TryGetValue(b.Id, out StoneBody known) && ReferenceEquals(known, b)) Stones.Remove(b.Id);
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
                else if (Logs.Debug) MelonLogger.Msg($"[Earth] Stones: the stones' tick failed again: {e.Message}");
            }
        }

        /// <summary>At the server's quit (Main.OnApplicationQuit, and MelonLoader's definite quit): every stone breaks, with no
        /// blast from a marked one (ruling C12), and none is spawned after. Nothing is ever saved anyway (SaveMode.Ignore).</summary>
        internal static void Shutdown() {
            if (shutDown) return;
            shutDown = true;
            try {
                var all = new List<StoneBody>(Stones.Values);
                foreach (StoneBody b in all) if (b != null) b.Break("the server quits", explode: false);
                Stones.Clear();
                if (all.Count > 0) MelonLogger.Msg($"[Earth] Stones: broke {all.Count} stone(s) at the server's quit");
            }
            catch (Exception e) { MelonLogger.Warning($"[Earth] Stones: breaking the stones at the quit failed: {e.Message}"); }
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>A player's account (UserInfo.Identifier, an int) as the ledger's and the owners' key; 0 with no user record.</summary>
        internal static ulong AccountOf(Player p) => p != null && p.UserInfo != null ? unchecked((ulong)(uint)p.UserInfo.Identifier) : 0UL;

        /// <summary>A player's name made safe for one log line (at most 40 printable ASCII characters; "?" with none).</summary>
        internal static string NameOf(Player p) {
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
