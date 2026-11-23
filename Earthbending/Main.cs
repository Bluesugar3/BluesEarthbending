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
    //                 modifiers' rules (StoneRules.After, CanApply and the slide, Reach, ModifyCheck, MoveRules.Pushback);
    //                 the hits' rules (StoneRules.Power, Clash, Damage, KnockDir, HitstunSeconds; Contact, the players'
    //                 capsule against a stone's box; HitLedger)
    //   StoneBox.cs   Unity-free: the chamfered stone block (a copy of the archery mod's Lectern.cs types)
    //   Ground.cs     the natural-ground test under a point
    //   Prefabs.cs    the five stones as code-built SyncLib network prefabs, [EarthStones] power and mass
    //   Server.cs     the server: Summon and Modify claims checked, stones spawned, the registry and every stone's life,
    //                 the quit; [EarthModifiers] Straight's numbers
    //   StoneBody.cs  the server's side of one stone: kind, owner, state, the rise, the modifiers (Straight), the slide, Break
    //   Hits.cs       the server: a moving stone hits creatures (its collisions) and players (their capsules), knocks
    //                 players back, stuns creatures, and two stones clash; [EarthDamage]
    //   StoneLook.cs  games: the stones' mesh and material (StoneView on every stone, StoneView.Live), the rise and break looks
    //   Net.cs        message 19 both ways (spec 3.8): claims to the server, knockback and Applied events to games
    //   Moves.cs      games: pushing the local player (Straight's pushback too), the hitstun after a knockback, the
    //                 movement moves' dust, the game's grounded flag
    //   Poses.cs      Unity-free: the pose engine (spec 3.3: zones, pass-through), the measured body scale
    //                 (BodyScaleEstimate), the rules of feeding it (axes, empty hands, ground grace, push),
    //                 and the pose recorder's rules (CaptureGate, PoseCapture)
    //   Hands.cs      games: the local player's head and hands (Hands.Raw, Hands.Read), empty hands, the ground under the
    //                 feet, the [EarthPose] axis settings and BodyScale, the pose recorder
    //   CaptureGuide.cs  games: the pose recorder's guide, a floating card naming the next pose and two hand markers
    //   Bend.cs       games: the pose machine every frame (Bend.Tick), the measured body scale, the fired moves (Jump, a
    //                 structure's Summon claim, a modifier's target and Modify claim), Bender.CanBend, poses.txt
    //   tools/fit_poses.py  the owner's pose capture -> poses.txt and the axis settings (run it with python -I)
    //   Commands.cs   the server's `earth` console module
    public class Main : MelonMod {
        public const string Version = "0.4.2";

        /// <summary>The dedicated (headless) server.</summary>
        internal static bool IsServer => ApplicationManager.IsHeadless;

        float nextGroundLine;

        public override void OnInitializeMelon() {
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
                CaptureGuide.Init(); // [EarthLooks] Guide*: the card and hand markers shown while the recorder is on
                Bend.Init();      // [EarthPose] timings, JumpSpeed, the haptic tick, poses.txt: the pose machine
                StoneLook.Init(); // [EarthLooks] the rise and break looks, and the server's Applied events
            }
            if (IsServer) {
                Server.Init();    // [EarthStones] summons, caps and life; [EarthModifiers]; Summon and Modify claims; the quit
                Commands.Register();
            }
            LoggerInstance.Msg($"[Earth] Blue's Earthbending v{Version} started ({(IsServer ? "server" : "client")})");
        }

        public override void OnUpdate() {
            Cfg.Poll();
            if (IsServer) {
                Server.Tick();      // every stone that lived its time, fell out of the world or lost its owner breaks
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
            if (!IsServer) CaptureGuide.Tick();   // after the game moved the head and hands; one bool test while [EarthPose] Capture is off
        }

        public override void OnApplicationQuit() {
            if (IsServer) Server.Shutdown();   // also on MelonLoader's definite quit (Server.Init), which closing the window raises alone
        }
    }
}
