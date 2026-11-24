using System;
using MelonLoader;
using UnityEngine;

// Name + Author form SyncLib's ModId ("Blue's Earthbending.Blue"): never rename either after a server has used the mod.
[assembly: MelonInfo(typeof(Earthbending.Main), "Blue's Earthbending", Earthbending.Main.Version, "Blue")]
[assembly: MelonGame("Alta", "A Township Tale")]

namespace Earthbending {
    // Blue's Earthbending: empty-hand poses on natural ground raise stones, move them and throw the player. One DLL, loaded by
    // the dedicated server and by every game.
    //   Main.cs       start-up, the per-frame routing (server work before the headless return)
    //   Cfg.cs        the settings file UserData\Earthbending.cfg and its 2-second reload; [EarthLog] Debug (Logs);
    //                 [EarthModifiers] Reach and MaxHeight, read on both sides (ReachSettings)
    //   Stones.cs     Unity-free: StoneKind, V3, StoneTable (spec 3.4); Modifier, Wire, the two claims (spec 3.8); the
    //                 summoning rules (StoneState, Ledger, SummonCheck, SpawnPlan, StoneLife, Caps, StoneKinds); the
    //                 modifiers' rules (StoneRules.After, CanApply, Impulse, Pushed and the slide, Reach, ModifyCheck,
    //                 MoveRules.Pushback);
    //                 the hits' rules (StoneRules.Power, Clash, Damage, KnockDir, HitstunSeconds; Contact, the players'
    //                 capsule against a stone's box; HitLedger)
    //   StoneBox.cs   Unity-free: the chamfered stone block (a copy of the archery mod's Lectern.cs types)
    //   Ground.cs     the natural-ground test under a point
    //   Prefabs.cs    the five stones as code-built SyncLib network prefabs, [EarthStones] power and mass
    //   Server.cs     the server: Summon and Modify claims checked, stones spawned, the registry and every stone's life,
    //                 the quit; [EarthModifiers] the modifiers' numbers
    //   StoneBody.cs  the server's side of one stone: kind, owner, state, the rise, the modifiers (Straight, Kick, Stomp,
    //                 Uppercut, Parry, Explode's mark), the slide, a stomped stone's landing, Break
    //   StoneHold.cs  the server: Hold (the stone follows the holder's hand, a swing throws it)
    //   StoneFlick.cs the server: Flick (the stone hangs between the hands and turns with them)
    //   StoneFreeze.cs the server: the hitstop of two stones that clash
    //   Hits.cs       the server: a moving stone hits creatures (its collisions) and players (their capsules), knocks
    //                 players back, stuns creatures, two stones clash, and Explode's blast; [EarthDamage]
    //   Fight.cs      Unity-free: fight health's rules (FightRing: the ring's zones; FightRules: RUMBLE's damage share,
    //                 the finishing hit's fling; FightLedger: each player's points)
    //   FightServer.cs the server: the one ring (the arena mod's, the console's, [EarthFight] Ring), who is on it, fight
    //                 health on every stone hit, the Fight and Ring messages; [EarthFight]
    //   Arena.cs      public: Arena.SetRing / ClearRing, for the arena mod to say where its ring is
    //   FightLook.cs  games: fight health as small stones (a row over each fighter's head, rows of five on your own left
    //                 forearm), a drawn test ring's dashes; [EarthLooks] Fight*
    //   StoneLook.cs  games: the stones' mesh and material (StoneView on every stone, StoneView.Live), the rise, break and
    //                 stomp looks; the Model drawn at its smooth, predicted place in StoneView.LateUpdate
    //   Motion.cs     Unity-free: the Model's follow between the game's syncs (the predicted target, the critically damped
    //                 easing, the snap), and the turn maths it needs (Q)
    //   StoneMotion.cs games: NetworkRigidbody's last sync read by reflection; [EarthLooks] SmoothMotion and its numbers
    //   Net.cs        message 19 both ways (spec 3.8): claims to the server, knockback, Applied events, fight health and
    //                 the ring to games
    //   Moves.cs      games: pushing the local player (Straight's pushback too), the hitstun after a knockback, the
    //                 movement moves' dust, the game's grounded flag, a fight's fling flying without air friction
    //   Poses.cs      Unity-free: the pose engine (spec 3.3: zones, pass-through), the rules of feeding it (the axis
    //                 settings as directions, empty hands, ground grace, push), and the pose recorder's rules (CaptureGate,
    //                 PoseCapture)
    //   PoseTraceFile.cs games: [EarthPose] Trace, the hands written to pose-trace-*.csv (PoseTrace in Poses.cs; replayed by
    //                 Earthbending.Tests -- --replay)
    //   Hands.cs      games: the local player's head and hands (Hands.Raw, Hands.Read), empty hands, the ground under the
    //                 feet, the [EarthPose] axis settings and BodyScale (fixed: Ruling 35), the pose recorder
    //   GuideDisplay.cs  games: the guides' shared display, a floating card and two hand markers; [EarthLooks] Guide*
    //   CaptureGuide.cs  games: the pose recorder's guide, naming the next pose to strike
    //   PracticeGuide.cs games: the practice guide for play-tests ([EarthPose] Practice): the next move to try, whether it
    //                 fired, the counts (PracticeRun in Poses.cs)
    //   Bend.cs       games: the pose machine every frame (Bend.Tick), the fired moves (Jump, a structure's Summon claim, a
    //                 modifier's target and Modify claim), Bender.CanBend, poses.txt
    //   tools/fit_poses.py  the owner's pose capture -> poses.txt and the axis settings as directions (run it with python -I)
    //   Commands.cs   the server's `earth` console module (`earth ring`: fight health's test ring; `earth on|off`,
    //                 `earth practice <player> on|off`)
    //   Switches.cs   the console's switches: bending for everyone ([EarthSwitch] Bending) and each player's practice card,
    //                 sent to every game every 2 s
    //   LogFix.cs     the freeze fix: every log line goes to MelonLoader\Latest.managed.log, never into MelonLoader's native log writer (it deadlocks)
    public class Main : MelonMod {
        public const string Version = "1.1.5";

        /// <summary>The dedicated (headless) server.</summary>
        internal static bool IsServer => ApplicationManager.IsHeadless;

        float nextGroundLine;

        public override void OnInitializeMelon() {
            LogFix.Init();   // first: log lines go to a managed file, never MelonLoader's native writer (LogFix.cs)
            Cfg.Init("Earthbending.cfg");
            Logs.Init();
            ReachSettings.Init();   // [EarthModifiers] Reach and MaxHeight: games pick with them, the server checks with them
            Prefabs.Init(this);   // [EarthStones], and SyncLib's RegisterPrefabs (it may only be subscribed here)
            // message 19; its handlers are Harmony postfixes, which MelonLoader applies. Its field sizes are counted in a static
            // initialiser, whose failure would otherwise stop the whole start-up here
            try { Net.Init(); }
            catch (Exception e) { LoggerInstance.Error($"[Earth] Net: message 19 could not be set up, so no stone or knockback works: {e}"); }
            if (!IsServer) {
                Moves.Init();
                Hands.Init();     // [EarthPose]: the axis settings, BodyScale and the pose recorder
                GuideDisplay.Init(); // [EarthLooks] Guide*: the card and hand markers of the capture's and the practice guide
                Bend.Init();      // [EarthPose] timings, JumpSpeed, the haptic tick, poses.txt: the pose machine
                PracticeGuide.Init(); // [EarthPose] Practice*: the practice guide, which hears of Bend's fired moves
                StoneLook.Init(); // [EarthLooks] the rise and break looks, and the server's Applied events
                FightLook.Init(); // [EarthLooks] Fight*: fight health's stones and the drawn ring, from the server's Fight and Ring
            }
            if (IsServer) {
                Server.Init();    // [EarthStones] summons, caps and life; [EarthModifiers]; Summon and Modify claims; the quit
                FightServer.Init(); // [EarthFight]: the ring and fight health
                Switches.InitServer(); // [EarthSwitch] Bending: `earth on|off`
                Commands.Register();
            }
            LoggerInstance.Msg($"[Earth] Blue's Earthbending v{Version} started ({(IsServer ? "server" : "client")})");
        }

        public override void OnUpdate() {
            Cfg.Poll();
            if (IsServer) {
                Server.Tick();      // every stone that lived its time, fell out of the world or lost its owner breaks
                FightServer.Tick(); // who is on the ring; fight health in and out; the ring and the points to every game
                Switches.Tick();    // bending on/off and each player's practice card to their game, every 2 s
                return;             // the headless server: nothing below draws or reads the local player
            }
            Hands.Tick();           // the pose recorder; one bool test while [EarthPose] Capture is off
            Bend.Tick();            // the pose machine (nothing fires while the recorder is on)
            if (Logs.Debug && Time.unscaledTime >= nextGroundLine) {
                nextGroundLine = Time.unscaledTime + 2f;
                PlayerController pc = PlayerController.Current;
                if (pc != null) {
                    try { MelonLogger.Msg(Ground.Describe(pc.RealFeetPosition)); }
                    catch (Exception e) { MelonLogger.Warning($"[Earth] Ground: could not read the ground under the feet: {e.Message}"); }
                }
            }
        }

        public override void OnLateUpdate() {
            if (IsServer) return;
            // after the game moved the head and hands; each one bool test while its setting is off
            CaptureGuide.Tick();    // [EarthPose] Capture
            PracticeGuide.Tick();   // [EarthPose] Practice (waits while the capture is on)
            FightLook.Frame();      // fight health's stones and the drawn ring; one bool test with no fighter and no ring
        }

        public override void OnApplicationQuit() {
            if (IsServer) Server.Shutdown();   // also on MelonLoader's definite quit (Server.Init), which closing the window raises alone
        }
    }
}
