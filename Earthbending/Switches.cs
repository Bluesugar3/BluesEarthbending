using System;
using System.Collections.Generic;
using Alta.Networking;
using Alta.Networking.Scripts.Player;
using MelonLoader;
using UnityEngine;

namespace Earthbending {
    // The server console's two switches (Commands.cs: `earth on|off`, `earth practice <player> on|off`):
    //   - Bending, for everyone: saved as [EarthSwitch] Bending in the server's cfg. Off: the server drops every Summon and
    //     Modify claim, and every game's pose machine stops (no move fires, Jump and Dash included).
    //   - The practice card, per player: not saved (a server restart turns it off for everyone).
    // The server sends each connected game its own Switch (one byte) when a switch changes and every 2 s (a game that joins
    // catches up within 2 s). A game that has heard one uses it; before that, and off a server, its own cfg decides
    // ([EarthPose] Practice; bending on).
    internal static class Switches {
        const float ResendSeconds = 2f;
        internal const byte BendingBit = 1, PracticeBit = 2;

        // ---------------------------------------------------------------- server
        static MelonPreferences_Entry<bool>? bendingEntry;
        static readonly HashSet<ulong> practising = new HashSet<ulong>();
        static float resendAt;
        static bool sendFailed;

        internal static bool Bending => bendingEntry == null || bendingEntry.Value;

        /// <summary>Server, from Main.OnInitializeMelon.</summary>
        internal static void InitServer() {
            bendingEntry = Cfg.Category("EarthSwitch").CreateEntry("Bending", true, description: "Server. Earthbending for everyone: false drops every summon and modifier and stops every game's poses. Set by the console's `earth on` / `earth off`.");
        }

        internal static void SetBending(bool on) {
            if (bendingEntry == null) return;
            bendingEntry.Value = on;
            try { bendingEntry.Category.SaveToFile(false); }
            catch (Exception e) { MelonLogger.Warning($"[Earth] Switch: could not save [EarthSwitch] Bending: {e.Message}"); }
            resendAt = 0f;
        }

        internal static bool Practising(ulong account) => practising.Contains(account);

        internal static void SetPractice(ulong account, bool on) {
            if (on) practising.Add(account); else practising.Remove(account);
            resendAt = 0f;
        }

        /// <summary>Server, every frame (Main.OnUpdate, before the headless return).</summary>
        internal static void Tick() {
            if (Time.unscaledTime < resendAt) return;
            resendAt = Time.unscaledTime + ResendSeconds;
            try {
                foreach (IPlayer ip in Player.AllPlayers) {
                    if (!(ip is Player p) || p == null || p.UserInfo == null) continue;
                    byte flags = (byte)((Bending ? BendingBit : 0) | (practising.Contains(Server.AccountOf(p)) ? PracticeBit : 0));
                    Net.SendSwitch(p, flags);
                }
            }
            catch (Exception e) {
                if (!sendFailed) { sendFailed = true; MelonLogger.Warning($"[Earth] Switch: sending the switches failed (said once): {e.Message}"); }
            }
        }

        // ---------------------------------------------------------------- game
        static bool heard, serverBending = true, serverPractice;

        /// <summary>Game: the server's Switch (Net.cs).</summary>
        internal static void Heard(byte flags) {
            bool b = (flags & BendingBit) != 0, pr = (flags & PracticeBit) != 0;
            if (!heard || b != serverBending || pr != serverPractice)
                MelonLogger.Msg($"[Earth] Switch: the server says bending {(b ? "on" : "off")}, practice card {(pr ? "on" : "off")}");
            heard = true;
            serverBending = b;
            serverPractice = pr;
        }

        /// <summary>Game: a new connection to a server (Net.cs): nothing heard from it yet.</summary>
        internal static void Forget() => heard = false;

        /// <summary>Game: whether the pose machine may run.</summary>
        internal static bool BendingOn => !(heard && Net.Connected) || serverBending;

        /// <summary>Game: the practice card, the server's word while connected, else [EarthPose] Practice.</summary>
        internal static bool PracticeOn(bool own) => heard && Net.Connected ? serverPractice : own;
    }
}
