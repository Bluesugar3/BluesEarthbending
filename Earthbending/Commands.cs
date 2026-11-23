using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Alta.Console;
using Alta.Networking;
using Alta.Networking.Scripts.Player;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Earthbending {
    // The `earth` console module on the dedicated server (the TavernLauncher Server window's Console, or the game's remote
    // console): `earth spawn <kind> <player>`, `earth push <player> <up m/s>`, `earth list`, `earth clear`. Registered from
    // Main.OnInitializeMelon on the dedicated server only. Every command answers with a string and never throws: a failure is
    // warned with its stack.
    [PlayOnly, ServerOnly, Module("earth", "Blue's Earthbending: spawn, push, list and clear stones")]
    internal static class Commands {
        const float AheadMetres = 1.5f, GroundBelowMetres = 6f, Lift = 0.02f;
        const string Usage = "Usage: earth spawn <disc|ball|pillar|cube|wall> <player>";
        const string PushUsage = "Usage: earth push <player> <up m/s>";

        internal static void Register() {
            try {
                CommandService.CommandCollection.ProcessModule(typeof(Commands), typeof(Commands).Assembly);
                MelonLogger.Msg("[Earth] Console commands added (earth spawn, earth push, earth list, earth clear)");
            }
            catch (Exception e) { MelonLogger.Warning($"[Earth] The earth console commands could not be added: {e.Message}"); }
        }

        // A parameter with a default makes the word optional, so a bare `earth spawn` gets the usage text, not "Command not found".

        [Command("spawn", "Spawns a stone 1.5 m in front of a player's head, on the ground: earth spawn <disc|ball|pillar|cube|wall> <player>")]
        static string Spawn(string kind = "", string player = "") {
            try {
                string k = (kind ?? "").Trim(), who = (player ?? "").Trim();
                if (k.Length == 0 || who.Length == 0) return Usage;
                if (!TryKind(k, out StoneKind stone)) return $"There is no stone '{k}'. {Usage}";
                Player? p = Find(who, out string refusal);
                if (p == null) return refusal;
                string name = p.UserInfo.Username;
                Transform? head = p.PlayerController != null ? p.PlayerController.Head : null;
                if (head == null) return $"{name} has no body right now: nothing spawned";
                Vector3 forward = head.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude < 1e-4f) { forward = head.up; forward.y = 0f; }   // looking straight down: the top of the head points ahead
                if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
                forward.Normalize();
                Vector3 spot = head.position + forward * AheadMetres;
                if (!Physics.Raycast(spot, Vector3.down, out RaycastHit hit, GroundBelowMetres, Ground.SolidMask, QueryTriggerInteraction.Ignore))
                    return $"No ground within {GroundBelowMetres:0} m under the spot {AheadMetres} m in front of {name}: nothing spawned";
                bool natural = Ground.IsNatural(hit.collider);
                Vector3 at = hit.point + Vector3.up * (StoneTable.Of(stone).Size.Y * 0.5f + Lift);
                // free (it settles on the ground under physics), owned by that player: it breaks as a summoned one does
                // (LifeSeconds, out of the world, the player leaving), counts toward their caps, but skips them
                StoneBody? b = Server.Spawn(stone, at, Quaternion.LookRotation(forward, Vector3.up), p, StoneState.Free, at, 0f);
                if (b == null) return $"The {stone} could not be spawned (see the log)";
                MelonLogger.Msg($"[Earth] Console: spawned {stone} #{b.Id} in front of {name} at {at} (ground {hit.collider.name}, layer {hit.collider.gameObject.layer}, natural {(natural ? "yes" : "no")})");
                return $"Spawned {stone} #{b.Id} in front of {name} at {at}";
            }
            catch (Exception e) { return Failed(e); }
        }

        [Command("push", "Throws a player straight up on their own game (a knockback with no hitstun): earth push <player> <up m/s>")]
        static string Push(string player = "", string up = "") {
            try {
                string who = (player ?? "").Trim(), speedText = (up ?? "").Trim();
                if (who.Length == 0 || speedText.Length == 0) return PushUsage;
                if (!float.TryParse(speedText, NumberStyles.Float, CultureInfo.InvariantCulture, out float speed) || float.IsNaN(speed) || float.IsInfinity(speed))
                    return $"'{speedText}' is not a speed in m/s. {PushUsage}";
                Player? p = Find(who, out string refusal);
                if (p == null) return refusal;
                string name = p.UserInfo.Username;
                if (!Net.SendKnockback(p, new Vector3(0f, speed, 0f), 0f)) return $"The push could not be sent to {name} (no connection)";
                MelonLogger.Msg($"[Earth] Console: pushed {name} up at {speed.ToString("0.##", CultureInfo.InvariantCulture)} m/s");
                return $"Sent {name} a push of {speed.ToString("0.##", CultureInfo.InvariantCulture)} m/s up";
            }
            catch (Exception e) { return Failed(e); }
        }

        [Command("list", "Lists every stone")]
        static string List() {
            try {
                var text = new StringBuilder();
                int n = 0;
                foreach (StoneView v in Object.FindObjectsOfType<StoneView>()) {
                    NetworkEntity e = v.GetComponent<NetworkEntity>();
                    if (e == null || e.IsBeingDestroyed) continue;
                    n++;
                    text.Append($"\n{Prefabs.KindOf(e)?.ToString() ?? "?"} #{e.Identifier} at {v.transform.position}");
                }
                return n == 0 ? "No stones" : $"{n} stone(s):{text}";
            }
            catch (Exception e) { return Failed(e); }
        }

        [Command("clear", "Destroys every stone")]
        static string Clear() {
            try {
                int n = 0;
                foreach (StoneView v in Object.FindObjectsOfType<StoneView>()) {
                    NetworkEntity e = v.GetComponent<NetworkEntity>();
                    if (e == null || e.IsBeingDestroyed) continue;
                    StoneBody b = v.GetComponent<StoneBody>();
                    if (b != null) b.Break("cleared from the console");   // the games play its break
                    else e.SceneDestroy();
                    n++;
                }
                MelonLogger.Msg($"[Earth] Console: cleared {n} stone(s)");
                return $"Destroyed {n} stone(s)";
            }
            catch (Exception e) { return Failed(e); }
        }

        // A kind by name, any case ("cube"); a number is not a name.
        static bool TryKind(string text, out StoneKind kind) {
            foreach (StoneKind k in (StoneKind[])Enum.GetValues(typeof(StoneKind)))
                if (string.Equals(k.ToString(), text, StringComparison.OrdinalIgnoreCase)) { kind = k; return true; }
            kind = default;
            return false;
        }

        // An online player by exact name (any case) or account id; players still joining (no user record) are skipped.
        static Player? Find(string who, out string refusal) {
            var names = new List<string>();
            foreach (IPlayer ip in Player.AllPlayers) {
                if (!(ip is Player p) || p == null || p.UserInfo == null) continue;
                if (string.Equals(p.UserInfo.Username, who, StringComparison.OrdinalIgnoreCase) || p.UserInfo.Identifier.ToString() == who) { refusal = ""; return p; }
                names.Add(p.UserInfo.Username);
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            refusal = names.Count == 0 ? $"No player '{who}': nobody is online" : $"No player '{who}' online. Online: {string.Join(", ", names.ToArray())}";
            return null;
        }

        static string Failed(Exception e) {
            MelonLogger.Warning($"[Earth] Console command failed: {e}");
            return "Failed: " + e.Message;
        }
    }
}
