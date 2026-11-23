using System;
using System.Collections.Generic;
using System.Reflection;
using Alta.Networking;
using Alta.Networking.Scripts.Player;
using Alta.Networking.Servers;
using Alta.Serialization;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace Earthbending {
    // Message 19, the mod's only network message, both ways (spec 3.8). The first byte is the kind (Wire.Kind), the second the
    // wire version (Wire.Version), then the kind's fields in the order of spec 3.8's table, floats through the game's own
    // SerializeFloat (32 bits each).
    //   game -> server: Summon (SummonClaim), Modify (ModifyClaim)
    //   server -> one game: Knockback (velocity, hitstun seconds)
    //   server -> every game whose player's head is within 80 m: Applied (entity Identifier, event byte, position)
    // Each kind's fields are ONE method (Fields / Knockback / Applied) that both writes and reads, because the game's Stream
    // does either through the same calls: the layout cannot drift between the two sides. The size check of a received message
    // comes from that same method, counted once with the game's StreamCounter.
    //
    // How it sits in the game (read in _decomp, 2026-11-22; the pattern is BluesArcheryOverhaul/JoinGate.cs):
    //   - Server: a postfix on ServerHandler.ConnectionCreated puts our handler on every new connection. A claim is the
    //     connection's player's (Connection.Player); a claim from a connection with no player yet is dropped.
    //   - Game: a postfix on JoinServerPipeline.ConnectionApprovedByServer puts our handler on the connection to the server and
    //     keeps that connection for sending.
    //   - The game hands a handler a StreamReader just past the message type (5 bits, 8 with TavernLib), sized to the whole
    //     message in bytes (MessageProcessor.ProcessSingleMessageFromData), so the bits left are known before reading; reading
    //     past the end does not throw, it reads garbage, so every length is checked first.
    //   - A message of another version, of a kind this side does not take, or too short is dropped, with ONE warning per
    //     connection (later ones are dropped quietly, or with a Debug line). The handler never throws.
    //   - Every event below runs each subscriber in its own try: a subscriber that throws never stops the others.
    internal static class Net {
        const MessageType Message = (MessageType)19;
        const int HeaderBits = 16;
        /// <summary>Spec 3.8: an Applied event reaches every player whose head is within this distance of it.</summary>
        const float AppliedMetres = 80f;

        // Each kind's field bits after the header, counted from the same method that writes and reads them.
        static readonly int SummonBits = Count(s => { var c = default(SummonClaim); Fields(s, ref c); });
        static readonly int ModifyBits = Count(s => { var c = default(ModifyClaim); Fields(s, ref c); });
        static readonly int KnockbackBits = Count(s => { Vector3 v = default; float t = 0f; Knockback(s, ref v, ref t); });
        static readonly int AppliedBits = Count(s => { uint e = 0; byte b = 0; Vector3 at = default; Applied(s, ref e, ref b, ref at); });

        static Connection? server;                                       // game: the connection to the server
        static readonly HashSet<Connection> warned = new HashSet<Connection>();
        static readonly HashSet<MethodInfo> failedSubscribers = new HashSet<MethodInfo>();

        /// <summary>Server: a game asked for a stone. The player is the sending connection's.</summary>
        internal static event Action<Player, SummonClaim>? SummonReceived;
        /// <summary>Server: a game asked for a modifier on a stone.</summary>
        internal static event Action<Player, ModifyClaim>? ModifyReceived;
        /// <summary>Game: the server knocks the local player back (velocity in m/s, hitstun in seconds).</summary>
        internal static event Action<Vector3, float>? KnockbackReceived;
        /// <summary>Game: something happened to a stone (entity Identifier, event byte: a Modifier, or Broke 20, Hitstop 21,
        /// Exploded 22; where).</summary>
        internal static event Action<uint, byte, Vector3>? AppliedReceived;

        /// <summary>From Main.OnInitializeMelon. The handlers themselves come from the Harmony postfixes below (MelonLoader
        /// applies them); this logs the layout once.</summary>
        internal static void Init() {
            MelonLogger.Msg($"[Earth] Net: message {(int)Message}, wire {Wire.Version} (bits after the header: Summon {SummonBits}, Modify {ModifyBits}, Knockback {KnockbackBits}, Applied {AppliedBits})");
        }

        // ---------------------------------------------------------------- the layout (one method per kind, both ways)

        static void Header(Stream s, ref byte kind, ref byte version) {
            s.SerializeByte(ref kind);
            s.SerializeByte(ref version);
        }

        static void Write(Stream s, Wire.Kind kind) {
            byte k = (byte)kind, v = Wire.Version;
            Header(s, ref k, ref v);
        }

        // Summon: stone kind (byte), head position (3 floats), yaw degrees (float)
        static void Fields(Stream s, ref SummonClaim c) {
            byte kind = (byte)c.Kind;
            s.SerializeByte(ref kind);
            c.Kind = (StoneKind)kind;
            Vec(s, ref c.Head);
            s.SerializeFloat(ref c.Yaw);
        }

        // Modify: modifier (byte), holding hand (byte), target entity Identifier (uint), head position (3 floats), yaw (float)
        static void Fields(Stream s, ref ModifyClaim c) {
            byte mod = (byte)c.Mod;
            s.SerializeByte(ref mod);
            c.Mod = (Modifier)mod;
            s.SerializeByte(ref c.Hand);
            s.SerializeUnsignedInt(ref c.Target);
            Vec(s, ref c.Head);
            s.SerializeFloat(ref c.Yaw);
        }

        // Knockback: velocity (3 floats), hitstun seconds (float)
        static void Knockback(Stream s, ref Vector3 velocity, ref float hitstun) {
            s.SerializeVector3(ref velocity);   // three SerializeFloat calls, x, y, z
            s.SerializeFloat(ref hitstun);
        }

        // Applied: entity Identifier (uint), event (byte), position (3 floats)
        static void Applied(Stream s, ref uint entity, ref byte evt, ref Vector3 at) {
            s.SerializeUnsignedInt(ref entity);
            s.SerializeByte(ref evt);
            s.SerializeVector3(ref at);
        }

        static void Vec(Stream s, ref V3 v) {
            float x = v.X, y = v.Y, z = v.Z;
            s.SerializeFloat(ref x);
            s.SerializeFloat(ref y);
            s.SerializeFloat(ref z);
            v = new V3(x, y, z);
        }

        static int Count(Action<Stream> fields) {
            var counter = new StreamCounter(0);
            fields(counter);
            return (int)counter.BitIndex;
        }

        // ---------------------------------------------------------------- sending

        /// <summary>Game: asks the server for a stone. False when there is no server connection.</summary>
        internal static bool SendSummon(SummonClaim claim) {
            Connection? c = ToServer("a summon");
            return c != null && c.Send(null, Message, (cc, s) => { Write(s, Wire.Kind.Summon); Fields(s, ref claim); });
        }

        /// <summary>Game: asks the server for a modifier on a stone. False when there is no server connection.</summary>
        internal static bool SendModify(ModifyClaim claim) {
            Connection? c = ToServer("a modify");
            return c != null && c.Send(null, Message, (cc, s) => { Write(s, Wire.Kind.Modify); Fields(s, ref claim); });
        }

        /// <summary>Server: knocks one player back on their own game (spec 3.1: movement is the player's game's). False when
        /// that player has no connection.</summary>
        internal static bool SendKnockback(Player victim, Vector3 velocity, float hitstun) {
            if (!Main.IsServer) { MelonLogger.Warning("[Earth] Net: a knockback can only be sent by the server"); return false; }
            if (victim == null) return false;
            Connection c = victim.ConnectionToRemotePlayer;
            if (c == null || c.IsDisposed) {
                if (Logs.Debug) MelonLogger.Msg($"[Earth] Net: no knockback for {NameOf(victim)}: no connection");
                return false;
            }
            bool sent = c.Send(null, Message, (cc, s) => { Write(s, Wire.Kind.Knockback); Knockback(s, ref velocity, ref hitstun); });
            if (Logs.Debug) MelonLogger.Msg($"[Earth] Net: knockback {velocity}, hitstun {hitstun:0.###} s to {NameOf(victim)}{(sent ? "" : ": NOT sent")}");
            return sent;
        }

        /// <summary>Server: tells every game whose player's head is within 80 m of <paramref name="at"/> what happened to a
        /// stone. Returns how many games it went to.</summary>
        internal static int SendApplied(uint entity, byte evt, Vector3 at) {
            if (!Main.IsServer) { MelonLogger.Warning("[Earth] Net: an Applied event can only be sent by the server"); return 0; }
            const float reach = AppliedMetres * AppliedMetres;
            SerializeConnectionMethod body = (cc, s) => { Write(s, Wire.Kind.Applied); Applied(s, ref entity, ref evt, ref at); };
            int n = 0;
            foreach (IPlayer ip in Player.AllPlayers) {
                if (!(ip is Player p) || p == null) continue;
                PlayerController pc = p.PlayerController;
                Transform? head = pc != null ? pc.Head : null;
                if (head == null || (head.position - at).sqrMagnitude > reach) continue;
                Connection c = p.ConnectionToRemotePlayer;
                if (c == null || c.IsDisposed) continue;
                if (c.Send(null, Message, body)) n++;
            }
            if (Logs.Debug) MelonLogger.Msg($"[Earth] Net: Applied {evt} on #{entity} at {at} to {n} game(s)");
            return n;
        }

        static Connection? ToServer(string what) {
            if (Main.IsServer) { MelonLogger.Warning($"[Earth] Net: {what} can only be sent by a game"); return null; }
            Connection? c = server;
            if (c != null && !c.IsDisposed) return c;
            if (Logs.Debug) MelonLogger.Msg($"[Earth] Net: {what} not sent: not connected to a server");
            return null;
        }

        // ---------------------------------------------------------------- receiving

        [HarmonyPatch(typeof(ServerHandler), "ConnectionCreated")]
        static class ServerListen {
            static void Postfix(Connection connection) {
                try { connection?.SetHandler(Message, ServerRead); }
                catch (Exception e) { MelonLogger.Error($"[Earth] Net: could not listen on a new connection: {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(JoinServerPipeline), "ConnectionApprovedByServer")]
        static class ClientListen {
            static void Postfix(Connection connection) {
                try {
                    if (connection == null) return;
                    connection.SetHandler(Message, ClientRead);
                    server = connection;
                    lock (warned) warned.Clear();
                }
                catch (Exception e) { MelonLogger.Error($"[Earth] Net: could not listen to the server: {e.Message}"); }
            }
        }

        static void ServerRead(Connection connection, Stream stream) => Read(connection, stream, onServer: true);

        static void ClientRead(Connection connection, Stream stream) => Read(connection, stream, onServer: false);

        static void Read(Connection connection, Stream s, bool onServer) {
            if (connection == null) return;
            try {
                int left = BitsLeft(s);
                if (left < HeaderBits) { Drop(connection, $"{left} bits, too short for the header"); return; }
                byte kind = 0, version = 0;
                Header(s, ref kind, ref version);
                left -= HeaderBits;
                if (version != Wire.Version) { Drop(connection, $"wire version {version} (this build: {Wire.Version})"); return; }
                switch ((Wire.Kind)kind) {
                    case Wire.Kind.Summon when onServer: {
                        if (!Enough(connection, "Summon", left, SummonBits)) return;
                        var claim = default(SummonClaim);
                        Fields(s, ref claim);
                        Player? p = PlayerOf(connection, "Summon");
                        if (p == null) return;
                        if (Logs.Debug) MelonLogger.Msg($"[Earth] Net: Summon {claim.Kind} from {NameOf(p)} (head {claim.Head}, yaw {claim.Yaw:0.#})");
                        Raise(SummonReceived, p, claim);
                        return;
                    }
                    case Wire.Kind.Modify when onServer: {
                        if (!Enough(connection, "Modify", left, ModifyBits)) return;
                        var claim = default(ModifyClaim);
                        Fields(s, ref claim);
                        Player? p = PlayerOf(connection, "Modify");
                        if (p == null) return;
                        if (Logs.Debug) MelonLogger.Msg($"[Earth] Net: Modify {claim.Mod} on #{claim.Target} from {NameOf(p)} (hand {claim.Hand}, head {claim.Head}, yaw {claim.Yaw:0.#})");
                        Raise(ModifyReceived, p, claim);
                        return;
                    }
                    case Wire.Kind.Knockback when !onServer: {
                        if (!Enough(connection, "Knockback", left, KnockbackBits)) return;
                        Vector3 velocity = default;
                        float hitstun = 0f;
                        Knockback(s, ref velocity, ref hitstun);
                        if (Logs.Debug) MelonLogger.Msg($"[Earth] Net: Knockback {velocity}, hitstun {hitstun:0.###} s");
                        Raise(KnockbackReceived, velocity, hitstun);
                        return;
                    }
                    case Wire.Kind.Applied when !onServer: {
                        if (!Enough(connection, "Applied", left, AppliedBits)) return;
                        uint entity = 0;
                        byte evt = 0;
                        Vector3 at = default;
                        Applied(s, ref entity, ref evt, ref at);
                        if (Logs.Debug) MelonLogger.Msg($"[Earth] Net: Applied {evt} on #{entity} at {at}");
                        Raise(AppliedReceived, entity, evt, at);
                        return;
                    }
                    default:
                        Drop(connection, $"kind {kind}, which the {(onServer ? "server" : "game")} does not take");
                        return;
                }
            }
            catch (Exception e) {
                try { Drop(connection, $"reading it failed ({e.GetType().Name}: {e.Message})"); }
                catch { }   // the handler never throws
            }
        }

        // Clamped: the game's reader keeps counting past the end of the message.
        static int BitsLeft(Stream s) => s is StreamReader r ? Math.Max(0, (int)(r.TotalByteCount * 8) - (int)r.BitIndex) : 0;

        static bool Enough(Connection c, string kind, int left, int need) {
            if (left >= need) return true;
            Drop(c, $"a {kind} of {left} bits (it needs {need})");
            return false;
        }

        static Player? PlayerOf(Connection c, string kind) {
            Player? p = c.Player as Player;
            if (p != null) return p;
            if (Logs.Debug) MelonLogger.Msg($"[Earth] Net: a {kind} from connection {c.Identifier}, which has no player yet: dropped");
            return null;
        }

        /// <summary>A message that is dropped: one warning per connection, then Debug lines only.</summary>
        static void Drop(Connection c, string why) {
            bool first;
            lock (warned) {
                if (warned.Count > 64) warned.RemoveWhere(x => x == null || x.IsDisposed);
                first = warned.Add(c);
            }
            string from = Main.IsServer ? $"{Ascii(c.UserInfo?.Username ?? c.PlayerName)} (connection {c.Identifier})" : "the server";
            if (first) MelonLogger.Warning($"[Earth] Net: dropped a message {(int)Message} from {from}: {why}. Another build of Blue's Earthbending? Later bad messages on this connection are dropped without a warning");
            else if (Logs.Debug) MelonLogger.Msg($"[Earth] Net: dropped a message {(int)Message} from {from}: {why}");
        }

        static void Raise<A, B>(Action<A, B>? ev, A a, B b) {
            if (ev == null) return;
            foreach (Delegate d in ev.GetInvocationList()) {
                try { ((Action<A, B>)d)(a, b); }
                catch (Exception e) { Failed(d, e); }
            }
        }

        static void Raise<A, B, C>(Action<A, B, C>? ev, A a, B b, C c) {
            if (ev == null) return;
            foreach (Delegate d in ev.GetInvocationList()) {
                try { ((Action<A, B, C>)d)(a, b, c); }
                catch (Exception e) { Failed(d, e); }
            }
        }

        // A subscriber that throws is warned once (with its stack), then only in Debug lines: it runs on every message.
        static void Failed(Delegate d, Exception e) {
            string who = $"{d.Method.DeclaringType?.Name}.{d.Method.Name}";
            bool first;
            lock (failedSubscribers) first = failedSubscribers.Add(d.Method);
            if (first) MelonLogger.Warning($"[Earth] Net: a message handler ({who}) failed: {e}");
            else if (Logs.Debug) MelonLogger.Msg($"[Earth] Net: a message handler ({who}) failed again: {e.Message}");
        }

        static string NameOf(Player p) => p != null && p.UserInfo != null ? Ascii(p.UserInfo.Username) : "?";

        // A name from the network, made safe for one log line.
        static string Ascii(string? text) {
            string t = text ?? "";
            if (t.Length == 0) return "?";
            var chars = new char[Math.Min(t.Length, 40)];
            for (int i = 0; i < chars.Length; i++) chars[i] = t[i] >= ' ' && t[i] < 127 ? t[i] : '?';
            return new string(chars);
        }
    }
}
