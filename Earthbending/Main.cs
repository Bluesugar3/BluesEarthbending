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
    //   Cfg.cs        the settings file UserData\Earthbending.cfg and its 2-second reload; [EarthLog] Debug (Logs)
    //   Stones.cs     Unity-free: StoneKind, V3, StoneTable (spec 3.4); Modifier, Wire, the two claims (spec 3.8)
    //   StoneBox.cs   Unity-free: the chamfered stone block (a copy of the archery mod's Lectern.cs types)
    //   Ground.cs     the natural-ground test under a point
    //   Prefabs.cs    the five stones as code-built SyncLib network prefabs, [EarthStones]; Server.Spawn (until Server.cs)
    //   StoneLook.cs  games: the stones' mesh and material (StoneView on every stone)
    //   Net.cs        message 19 both ways (spec 3.8): claims to the server, knockback and Applied events to games
    //   Moves.cs      games: pushing the local player, and the hitstun after a knockback
    //   Poses.cs      Unity-free: the pose engine (spec 3.3), and the pose recorder's rules (CaptureGate, PoseCapture)
    //   Hands.cs      games: the local player's head and hands (Hands.Raw), the [EarthPose] axis settings, the pose recorder
    //   tools/fit_poses.py  the owner's pose capture -> poses.txt and the axis settings (run it with python -I)
    //   Commands.cs   the server's `earth` console module
    public class Main : MelonMod {
        public const string Version = "0.1.1";

        /// <summary>The dedicated (headless) server.</summary>
        internal static bool IsServer => ApplicationManager.IsHeadless;

        float nextGroundLine;

        public override void OnInitializeMelon() {
            Cfg.Init("Earthbending.cfg");
            Logs.Init();
            Prefabs.Init(this);   // [EarthStones], and SyncLib's RegisterPrefabs (it may only be subscribed here)
            Net.Init();           // message 19; its handlers are Harmony postfixes, which MelonLoader applies
            if (!IsServer) {
                Moves.Init();
                Hands.Init();     // [EarthPose]: the axis settings and the pose recorder
            }
            if (IsServer) Commands.Register();
            LoggerInstance.Msg($"[Earth] Blue's Earthbending v{Version} started ({(IsServer ? "server" : "client")})");
        }

        public override void OnUpdate() {
            Cfg.Poll();
            if (IsServer) return;   // the headless server: nothing below draws or reads the local player
            Hands.Tick();           // the pose recorder; one bool test while [EarthPose] Capture is off
            if (Logs.Debug && Time.unscaledTime >= nextGroundLine) {
                nextGroundLine = Time.unscaledTime + 2f;
                PlayerController pc = PlayerController.Current;
                if (pc != null) {
                    try { MelonLogger.Msg(Ground.Describe(pc.RealFeetPosition)); }
                    catch (Exception e) { MelonLogger.Warning($"[Earth] Ground: could not read the ground under the feet: {e.Message}"); }
                }
            }
        }
    }
}
