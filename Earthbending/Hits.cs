using System;
using System.Collections.Generic;
using System.Reflection;
using Alta.Impact;
using Alta.Intelligence;
using Alta.Networking;
using Alta.Networking.Scripts.Player;
using MelonLoader;
using UnityEngine;

namespace Earthbending {
    // The server: what a moving stone does to what it meets (spec 2.2, 2.6, 3.4, 3.7; rulings 24 and 25). The [EarthDamage]
    // settings are the server's.
    //   - Power (StoneRules.Power): the kind's ([EarthStones] <Kind>Power) + 1 while grounded + 1 moving faster than
    //     MovingSpeed, from the stone's velocity BEFORE the physics step (StoneBody.Velocity): a collision callback comes after
    //     the step, which has already slowed the stone against what it struck.
    //   - A creature (a health object that is not a player's, with a cripple stat or an Alta.Intelligence.Agent) is hit
    //     through the stone's own collisions (StoneBody.OnCollisionEnter -> OnContact), judged on the STONE's own speed before
    //     the step (the controller's ruling on Task 8): at least MinHitSpeed, and never by a Rising stone (ruling 25). A
    //     creature that walks into a standing stone is not hit and does not break it. Only a new contact counts: a creature
    //     already touching a stone when it is pushed is not hit by it (accepted for now).
    //   - A player is hit by the server testing each moving stone (its own speed at least MinHitSpeed, not Rising) against
    //     each player's capsule, from the head (PlayerController.Head) down to the feet (RealFeetPosition), radius 0.3 m, in the
    //     stone's own frame (Contact, Stones.cs), every physics step before the step (StoneBody.FixedUpdate -> TestPlayers):
    //     the headless server's player colliders are not known to be where the players are (ruling 24). Only a stone that
    //     comes toward the player hits them (StoneRules.Approaching, this step's velocity or the last one's, so a bounce off
    //     the player's own collider in between still counts): one that slides away from a player touching it, as its owner
    //     who just pushed it, does not. Its owner CAN be hit by it otherwise (ruling 25); the player holding it (Task 13) or
    //     flicking it (Task 14) never is (ruling C16). With HurtPlayers off, stones ignore players entirely: no damage, no knockback, no break
    //     (StoneRules.HitsPlayer; the capsule test does not even run).
    //   - The hit: DamagePerPower x power (Blunt) and CripplePerPower x power, one plain ImpactHit (no tool, so never the
    //     game's PvP gate) through the health object's DetectImpact, as Explosive.Trigger does; the game's own multipliers,
    //     armour, damage protection and spawn invincibility still apply. The wielder is the stone owner's PlayerController
    //     (and its entity) while they are online, so a creature turns on them and a kill names them; else none. A struck
    //     player is also knocked back on their own game (Net.SendKnockback: KnockDir of the stone's velocity x
    //     KnockbackSpeed, hitstun power x 0.7 / 3 s, none from a Disc; Moves pushes and stuns them). A struck creature still
    //     alive is stunned (its Agent's own Stun(float), which Turabada, Gotera, GreaterGotera, Babu and Dragonfly have) with
    //     power x 0.3 s as the strength; the game's Stun ignores the number and plays the creature's own stunned action.
    //   - One stone hits one target at most once per HitCooldownSeconds (HitLedger); with BreakOnHit the stone then breaks,
    //     so it hits once. A stone that has broken hits nothing more, in the same physics step too.
    //   - Two stones that touch clash when neither is Rising and they meet at MinHitSpeed or faster relative to each other:
    //     StoneRules.Clash on both powers, and the loser (both on a tie) breaks; a winner that is not kinematic carries on at
    //     its velocity (and spin) from before the step (StoneBody.CarryOn, spec 3.7), and the loser stops colliding at once
    //     (StoneBody.Break). Both stones get the collision; the one with the lower Identifier decides it. No hitstop freeze
    //     yet (Task 15).
    //   - Per hit and per clash one Debug line ([EarthLog] Debug), with the target's health before and after, for tuning
    //     DamagePerPower. A failure is warned once per kind, then Debug lines; nothing here ever throws into the physics step.
    //   - Ruling 30: a player hit counts only when the game really lowered their health: a hit with damage above 0 that left
    //     it where it was (spawn invincibility, the game ignoring damage) gives no knockback and no break (the hit record is
    //     still charged). A hit with no damage set (DamagePerPower and CripplePerPower 0) still knocks back. A dead creature
    //     (health 0) is not hit at all: no record, no break.
    //   - Task 12 (ruling C12; spec 2.4, 2.6, 3.7): Explode, a marked stone's blast when it breaks (StoneBody.Break), by
    //     StoneRules.ExplodeShare of each thing's distance: players (only with HurtPlayers) knocked back away from it,
    //     living creatures staggered (cripple 1, no damage), loose rigidbodies and our own stones pushed away (grounded stones
    //     set loose first, Rising ones left alone). No damage, and a blast breaks no stone, so blasts never chain. A clash's
    //     winner carries on first, so a marked loser's blast adds to the speed it carries on at.
    internal static class Hits {
        const bool DefaultHurtPlayers = true, DefaultBreakOnHit = true;
        const float DefaultDamagePerPower = 8f, DefaultCripplePerPower = 0.5f, DefaultMinHitSpeed = 2f, DefaultMovingSpeed = 3f;
        const float DefaultKnockbackSpeed = 4f, DefaultHitCooldown = 0.5f;
        const int MemoCap = 1024;

        static MelonPreferences_Entry<bool>? hurtPlayersEntry, breakOnHitEntry;
        static MelonPreferences_Entry<float>? damagePerPowerEntry, cripplePerPowerEntry, minHitSpeedEntry, movingSpeedEntry, knockbackSpeedEntry, hitCooldownEntry;

        /// <summary>[EarthDamage] HurtPlayers: stones hit players (damage, knockback, the break); off, they ignore players entirely.</summary>
        internal static bool HurtPlayers { get; private set; } = DefaultHurtPlayers;
        internal static float DamagePerPower { get; private set; } = DefaultDamagePerPower;
        internal static float CripplePerPower { get; private set; } = DefaultCripplePerPower;
        /// <summary>[EarthDamage] MinHitSpeed: the slowest a stone hits or clashes at, in m/s.</summary>
        internal static float MinHitSpeed { get; private set; } = DefaultMinHitSpeed;
        /// <summary>[EarthDamage] MovingSpeed: a stone faster than this has +1 power, in m/s.</summary>
        internal static float MovingSpeed { get; private set; } = DefaultMovingSpeed;
        internal static float KnockbackSpeed { get; private set; } = DefaultKnockbackSpeed;
        internal static bool BreakOnHit { get; private set; } = DefaultBreakOnHit;
        internal static float HitCooldownSeconds { get; private set; } = DefaultHitCooldown;

        static readonly HitLedger ledger = new HitLedger();
        static readonly Dictionary<Type, MethodInfo?> stunMethods = new Dictionary<Type, MethodInfo?>();
        static readonly Dictionary<int, Agent?> agentMemo = new Dictionary<int, Agent?>();
        static readonly List<Victim> victims = new List<Victim>();   // this physics step's players
        static double victimsAt = double.NaN;
        static bool contactFailed, playersFailed, playerReadFailed, stunFailed, sendFailed, blastFailed, blastSendFailed;

        struct Victim {
            public Player Player;
            public PlayerHealth Health;
            public string Name;
            public Vector3 Head, Feet;
        }

        /// <summary>From Server.Init on the dedicated server: the [EarthDamage] settings.</summary>
        internal static void Init() {
            MelonPreferences_Category cat = Cfg.Category("EarthDamage");
            hurtPlayersEntry = cat.CreateEntry("HurtPlayers", DefaultHurtPlayers, description: "Server. Stones hit players: a moving stone that comes at a player damages them (whatever the server's PvP setting; the game's spawn invincibility still applies), knocks them back and breaks on them; a marked stone's blast knocks them back too. Off: stones and blasts ignore players entirely (no damage, no knockback, no break; they still hit creatures and clash), for a public server: a knockback alone can push someone off a cliff.");
            damagePerPowerEntry = cat.CreateEntry("DamagePerPower", DefaultDamagePerPower, description: "Server. A stone's hit deals this much damage per point of its power (Blunt; power is the stone's own, +1 grounded, +1 moving faster than MovingSpeed: a still Cube is 3, a sliding Pillar pushed by Straight 3). A guess to tune from the Debug lines' health before and after. 0 or more (0: no damage).");
            cripplePerPowerEntry = cat.CreateEntry("CripplePerPower", DefaultCripplePerPower, description: "Server. A stone's hit also deals this much cripple per point of power, which knocks a creature down once its stagger pool runs out. 0 or more.");
            minHitSpeedEntry = cat.CreateEntry("MinHitSpeed", DefaultMinHitSpeed, description: "Server. A stone hits a creature or a player only when the stone itself moves this many m/s or more (toward the player; a creature walking into a standing stone is not hit), and two stones clash only when they meet this fast. A rising stone never hits or clashes. 0 or more.");
            movingSpeedEntry = cat.CreateEntry("MovingSpeed", DefaultMovingSpeed, description: "Server. A stone moving faster than this many m/s has one more power for its hits and clashes. 0 or more.");
            knockbackSpeedEntry = cat.CreateEntry("KnockbackSpeed", DefaultKnockbackSpeed, description: "Server. A struck player is pushed this many m/s along the stone's way (flat, or 45 degrees up when the stone rose that steeply), on their own game, and cannot bend for power x 0.7 / 3 s (never after a Disc). A marked stone's blast ([EarthModifiers] ExplodeRadius) pushes each player in it this many m/s away from it, times their share (all of it within half the radius, a quarter at its edge), with the same stun. 0 or more.");
            breakOnHitEntry = cat.CreateEntry("BreakOnHit", DefaultBreakOnHit, description: "Server. A stone breaks once it has hit a creature or a player (so each stone hits once). Off: it carries on, and may hit the same one again after HitCooldownSeconds.");
            hitCooldownEntry = cat.CreateEntry("HitCooldownSeconds", DefaultHitCooldown, description: "Server. One stone hits one creature or player at most once in this many seconds. 0 or more.");
            Apply();
            Cfg.Changed += Apply;
        }

        static void Apply() {
            HurtPlayers = hurtPlayersEntry?.Value ?? DefaultHurtPlayers;
            BreakOnHit = breakOnHitEntry?.Value ?? DefaultBreakOnHit;
            DamagePerPower = Cfg.NotNegative(damagePerPowerEntry, "EarthDamage", "DamagePerPower", DefaultDamagePerPower);
            CripplePerPower = Cfg.NotNegative(cripplePerPowerEntry, "EarthDamage", "CripplePerPower", DefaultCripplePerPower);
            MinHitSpeed = Cfg.NotNegative(minHitSpeedEntry, "EarthDamage", "MinHitSpeed", DefaultMinHitSpeed);
            MovingSpeed = Cfg.NotNegative(movingSpeedEntry, "EarthDamage", "MovingSpeed", DefaultMovingSpeed);
            KnockbackSpeed = Cfg.NotNegative(knockbackSpeedEntry, "EarthDamage", "KnockbackSpeed", DefaultKnockbackSpeed);
            HitCooldownSeconds = Cfg.NotNegative(hitCooldownEntry, "EarthDamage", "HitCooldownSeconds", DefaultHitCooldown);
            MelonLogger.Msg($"[Earth] Hits: {DamagePerPower:0.##} damage and {CripplePerPower:0.##} cripple per power from {MinHitSpeed:0.##} m/s (+1 power over {MovingSpeed:0.##} m/s); {(HurtPlayers ? $"players hurt and knocked back {KnockbackSpeed:0.##} m/s" : "players ignored ([EarthDamage] HurtPlayers off)")}; a stone {(BreakOnHit ? "breaks on its hit" : "carries on after a hit")}, one hit per target every {HitCooldownSeconds:0.##} s");
        }

        /// <summary>From Server.Forget: a stone that no longer stands is forgotten by the hit record.</summary>
        internal static void Forget(uint stone) => ledger.Forget(stone);

        // ---------------------------------------------------------------- creatures and stones: the stone's collisions

        /// <summary>Server, from StoneBody.OnCollisionEnter: the stone touched something. Another stone: a clash. A creature: a
        /// hit. Anything else (the ground, a player's collider, a tree) does nothing here. Never throws.</summary>
        internal static void OnContact(StoneBody stone, Collision c) {
            try {
                if (stone == null || stone.Broken || c == null || stone.State == StoneState.Rising) return;
                float rel = c.relativeVelocity.magnitude, own = stone.Velocity.magnitude;
                if (!(rel >= MinHitSpeed) && !(own >= MinHitSpeed)) return;   // most contacts (a stone on the ground) end here
                Collider other = c.collider;
                if (other == null) return;
                StoneBody that = other.GetComponentInParent<StoneBody>();
                if (that != null) {
                    if (!ReferenceEquals(that, stone)) Clash(stone, that, rel);
                    return;
                }
                if (!StoneRules.CanHit(stone.State, own, MinHitSpeed)) return;   // the stone's own speed (the ruling on concern 4)
                HealthObject? h = HealthOf(other);
                if (h == null || h is PlayerHealth || !IsCreature(h)) return;   // players: TestPlayers (ruling 24)
                Vector3 point = other.transform.position, normal = Vector3.up;
                if (c.contactCount > 0) {
                    ContactPoint cp = c.GetContact(0);
                    point = cp.point;
                    normal = cp.normal;   // out of the creature, toward the stone
                }
                HitCreature(stone, h, point, normal, rel);
            }
            catch (Exception e) {
                if (!contactFailed) { contactFailed = true; MelonLogger.Warning($"[Earth] Hits: a stone's contact failed (said once): {e}"); }
                else if (Logs.Debug) MelonLogger.Msg($"[Earth] Hits: a stone's contact failed again: {e.Message}");
            }
        }

        static void Clash(StoneBody a, StoneBody b, float rel) {
            if (a.Id > b.Id || b.Broken) return;   // both stones get the collision; the lower Identifier decides
            if (!StoneRules.Clashes(a.State, b.State, rel, MinHitSpeed)) return;
            int pa = a.Power, pb = b.Power;
            ClashResult r = StoneRules.Clash(pa, pb);
            // spec 3.7: the winner carries on at its old speed (the step's solver stopped or bounced it against the loser, which
            // has stopped colliding); a kinematic winner (Still) has none to get back. Before the loser breaks, so a marked
            // loser's blast (Task 12) adds to that speed. No hitstop freeze yet (Task 15).
            StoneBody? winner = r == ClashResult.ABreaks ? b : r == ClashResult.BBreaks ? a : null;
            bool carried = winner != null && winner.CarryOn();
            if (r != ClashResult.BBreaks) a.Break(Logs.Debug ? $"clash with {b.Kind} #{b.Id} (power {pa} against {pb})" : "clash");
            if (r != ClashResult.ABreaks) b.Break(Logs.Debug ? $"clash with {a.Kind} #{a.Id} (power {pb} against {pa})" : "clash");
            if (Logs.Debug)
                MelonLogger.Msg($"[Earth] Hits: {a.Kind} #{a.Id} of {a.OwnerName} ({a.State}, power {pa}, {a.Velocity.magnitude:0.##} m/s) met {b.Kind} #{b.Id} of {b.OwnerName} ({b.State}, power {pb}, {b.Velocity.magnitude:0.##} m/s) at {rel:0.##} m/s: {(winner == null ? "both break" : $"#{(winner == a ? b.Id : a.Id)} breaks, #{winner.Id} {(carried ? $"carries on at {winner.Velocity.magnitude:0.##} m/s" : "stands (kinematic)")}")}");
        }

        static void HitCreature(StoneBody stone, HealthObject h, Vector3 point, Vector3 normal, float rel) {
            int power = stone.Power;
            if (power <= 0) return;   // a slow loose Disc touches without a hit (spec 2.2)
            if (!(h.Health > 0f)) return;   // a dead creature is not hit: no record, no break
            float damage = StoneRules.Damage(power, stone.Velocity.magnitude, DamagePerPower, MinHitSpeed), cripple = StoneRules.Cripple(power, CripplePerPower);
            if (!ledger.TryHit(stone.Id, h.GetInstanceID(), Time.unscaledTimeAsDouble, HitCooldownSeconds)) return;
            string name = h.name;
            Vector3 dir = Direction(stone.Velocity, -normal);
            Sane(ref point, ref normal, h.transform.position);
            PlayerController? wielder = OwnerController(stone);
            float before = h.Health, stun = 0f;
            var hit = new ImpactHit(damage, cripple, 1f, 1f, null, ImpactType.Blunt, point, normal, dir, null, wielder != null ? wielder.Entity : null, wielder);
            h.DetectImpact(hit);
            float after = h != null ? h.Health : 0f;
            if (h != null && after > 0f) stun = Stun(h, StoneRules.StunSeconds(power));
            if (Logs.Debug)
                MelonLogger.Msg($"[Earth] Hits: {stone.Kind} #{stone.Id} of {stone.OwnerName} ({stone.State}, power {power}, {stone.Velocity.magnitude:0.##} m/s, {rel:0.##} m/s into it) hit '{name}': damage {damage:0.##}, cripple {cripple:0.##}; health {before:0.##} -> {after:0.##} of {(h != null ? h.MaxHealth : 0f):0.##}{(after > 0f ? "" : " (killed)")}{(stun > 0f ? $"; stunned ({stun:0.##})" : "")}; {(wielder != null ? "dealt by " + stone.OwnerName : "no wielder (its owner is offline)")}{(BreakOnHit ? "; it breaks" : "")}");
            if (BreakOnHit) stone.Break(Logs.Debug ? $"hit '{name}'" : "hit a creature");
        }

        // ---------------------------------------------------------------- players: the capsule test

        /// <summary>Server, from StoneBody.FixedUpdate (before the physics step, after the stone's own update): a moving stone
        /// (its own speed, this step's or the last one's, at least MinHitSpeed; never Rising) against every player's capsule
        /// but its holder's and its flicker's (ruling C16); nothing at all with HurtPlayers off (StoneRules.HitsPlayer decides).
        /// Never throws.</summary>
        internal static void TestPlayers(StoneBody stone) {
            try {
                if (stone == null || stone.Broken || !HurtPlayers) return;   // HurtPlayers off: stones ignore players entirely
                Vector3 now = stone.Velocity, last = stone.LastVelocity;
                if (!StoneRules.CanHit(stone.State, Mathf.Max(now.magnitude, last.magnitude), MinHitSpeed)) return;
                Collider? shape = stone.Shape;
                if (shape == null) return;
                ReadPlayers();
                Player? holder = stone.Holder, flicker = stone.FlickedBy;   // ruling C16: a stone never hits the player who holds or flicks it
                for (int i = 0; i < victims.Count && !stone.Broken; i++) {
                    Victim v = victims[i];
                    if (holder != null && ReferenceEquals(v.Player, holder)) continue;
                    if (flicker != null && ReferenceEquals(v.Player, flicker)) continue;
                    if (!Touches(shape, v.Head, v.Feet, out Vector3 onPlayer, out Vector3 onStone, out Vector3 centre)) continue;
                    Vector3 toward = onPlayer - onStone;
                    if (toward.sqrMagnitude < 1e-8f) toward = onPlayer - centre;   // the head's line runs through the stone
                    Vector3 vel;
                    if (StoneRules.HitsPlayer(stone.State, V(now), V(toward), MinHitSpeed, HurtPlayers)) vel = now;
                    else if (StoneRules.HitsPlayer(stone.State, V(last), V(toward), MinHitSpeed, HurtPlayers)) vel = last;   // it bounced off them in the last step
                    else continue;
                    HitPlayer(stone, v, vel, onStone, toward);
                }
            }
            catch (Exception e) {
                if (!playersFailed) { playersFailed = true; MelonLogger.Warning($"[Earth] Hits: testing a stone against the players failed (said once): {e}"); }
                else if (Logs.Debug) MelonLogger.Msg($"[Earth] Hits: testing a stone against the players failed again: {e.Message}");
            }
        }

        static void HitPlayer(StoneBody stone, Victim v, Vector3 vel, Vector3 point, Vector3 toward) {
            float speed = vel.magnitude;
            int power = StoneRules.Power(stone.Kind, stone.State, speed, MovingSpeed);
            if (power <= 0) return;   // a slow loose Disc touches without a hit (spec 2.2)
            float damage = StoneRules.Damage(power, speed, DamagePerPower, MinHitSpeed), cripple = StoneRules.Cripple(power, CripplePerPower);
            float hitstun = StoneRules.HitstunSeconds(stone.Kind, power);
            PlayerHealth ph = v.Health;
            if (ph == null || !ledger.TryHit(stone.Id, ph.GetInstanceID(), Time.unscaledTimeAsDouble, HitCooldownSeconds)) return;
            Vector3 normal = -toward;   // out of the player, toward the stone
            Sane(ref point, ref normal, v.Head);
            Vector3 dir = Direction(vel, -normal);
            float before = ph.Health, after = before;
            bool hurt = damage > 0f || cripple > 0f;   // HurtPlayers is on here (TestPlayers returns without it)
            if (hurt) {
                PlayerController? wielder = OwnerController(stone);
                var hit = new ImpactHit(damage, cripple, 1f, 1f, null, ImpactType.Blunt, point, normal, dir, null, wielder != null ? wielder.Entity : null, wielder);
                ph.DetectImpact(hit);
                after = ph != null ? ph.Health : 0f;
            }
            if (damage > 0f && !(after < before)) {   // ruling 30: the game did not lower their health; the record stays charged
                if (Logs.Debug)
                    MelonLogger.Msg($"[Earth] Hits: {stone.Kind} #{stone.Id} of {stone.OwnerName} ({stone.State}, power {power}, {speed:0.##} m/s) hit player {v.Name}: damage {damage:0.##} refused by the game (health {before:0.##} -> {after:0.##}: spawn invincibility or ignoring damage); no knockback, no break");
                return;
            }
            V3 away = new V3(toward.x, toward.y, toward.z);
            V3 d = StoneRules.KnockDir(V(vel), away);
            Vector3 knock = new Vector3(d.X, d.Y, d.Z) * KnockbackSpeed;
            bool sent = false;
            try { sent = Net.SendKnockback(v.Player, knock, hitstun); }
            catch (Exception e) {
                if (!sendFailed) { sendFailed = true; MelonLogger.Warning($"[Earth] Hits: sending a knockback failed (said once): {e.Message}"); }
            }
            if (Logs.Debug) {
                string dealt = hurt ? $"damage {damage:0.##}, cripple {cripple:0.##}" : "not hurt (DamagePerPower and CripplePerPower are 0)";
                MelonLogger.Msg($"[Earth] Hits: {stone.Kind} #{stone.Id} of {stone.OwnerName} ({stone.State}, power {power}, {speed:0.##} m/s) hit player {v.Name}{(stone.Owner != 0UL && stone.Owner == Server.AccountOf(v.Player) ? " (its owner)" : "")}: {dealt}; health {before:0.##} -> {after:0.##} of {(ph != null ? ph.MaxHealth : 0f):0.##}; knockback {knock}, hitstun {hitstun:0.###} s{(sent ? "" : " (NOT sent)")}{(BreakOnHit ? "; it breaks" : "")}");
            }
            if (BreakOnHit) stone.Break(Logs.Debug ? $"hit player {v.Name}" : "hit a player");
        }

        /// <summary>This physics step's players with a body and a connection, read once per step (every moving stone tests
        /// them). Dead ones are left out; a downed one can still be hit (the game decides what damage does then).</summary>
        static void ReadPlayers() {
            double at = Time.fixedTimeAsDouble;
            if (at == victimsAt) return;
            victimsAt = at;
            victims.Clear();
            foreach (IPlayer ip in Player.AllPlayers) {
                if (!(ip is Player p) || p == null) continue;
                try {
                    PlayerController pc = p.PlayerController;
                    if (pc == null || !pc.IsAlive || !(pc.Health is PlayerHealth ph) || ph == null) continue;
                    Transform head = pc.Head;
                    Connection c = p.ConnectionToRemotePlayer;
                    if (head == null || c == null || c.IsDisposed) continue;
                    Vector3 h = head.position, f = pc.RealFeetPosition;
                    if (!Finite(h) || !Finite(f)) continue;
                    victims.Add(new Victim { Player = p, Health = ph, Name = Server.NameOf(p), Head = h, Feet = f });
                }
                catch (Exception e) {   // a player still joining: left out this step
                    if (!playerReadFailed) { playerReadFailed = true; MelonLogger.Warning($"[Earth] Hits: a player could not be read for the stones' hits; left out (said once): {e.Message}"); }
                }
            }
        }

        /// <summary>Whether the capsule from <paramref name="head"/> to <paramref name="feet"/> (StoneRules.PlayerRadius) touches the
        /// stone's collider: a box in its own frame, a sphere by its centre, any other by its world bounds. The nearest points on
        /// the capsule's line and on the stone, and the stone's centre, in world space.</summary>
        static bool Touches(Collider shape, Vector3 head, Vector3 feet, out Vector3 onPlayer, out Vector3 onStone, out Vector3 centre) {
            onPlayer = head;
            onStone = head;
            Transform t = shape.transform;
            Vector3 scale = t.lossyScale;
            scale = new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            if (shape is SphereCollider sphere) {
                centre = t.TransformPoint(sphere.center);
                float radius = sphere.radius * Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z));
                float d = Contact.SegmentPoint(V(head), V(feet), V(centre), out V3 near) - radius;
                if (!(d <= StoneRules.PlayerRadius)) return false;
                onPlayer = U(near);
                Vector3 outward = onPlayer - centre;
                onStone = outward.sqrMagnitude > 1e-8f ? centre + outward.normalized * radius : centre;
                return true;
            }
            Quaternion rot;
            Vector3 half;
            if (shape is BoxCollider box) {
                centre = t.TransformPoint(box.center);
                rot = t.rotation;
                half = Vector3.Scale(box.size, scale) * 0.5f;
            }
            else {
                Bounds b = shape.bounds;
                centre = b.center;
                rot = Quaternion.identity;
                half = b.extents;
            }
            Quaternion inv = Quaternion.Inverse(rot);
            float dist = Contact.SegmentBox(V(inv * (head - centre)), V(inv * (feet - centre)), V(half), out V3 seg, out V3 onBox);
            if (!(dist <= StoneRules.PlayerRadius)) return false;
            onPlayer = centre + rot * U(seg);
            onStone = centre + rot * U(onBox);
            return true;
        }

        // ---------------------------------------------------------------- explosions (Task 12)

        /// <summary>Server (ruling C12; spec 2.4, 2.6, 3.7), from StoneBody.Break: a marked stone (<paramref name="source"/>,
        /// already broken: it is not blasted) broke at <paramref name="centre"/>. Everything within <paramref name="radius"/> m
        /// is blasted by StoneRules.ExplodeShare of the distance from the centre to its nearest point (all of it within half
        /// the radius, a quarter at the edge). No damage, and no stone breaks, so blasts never chain.
        /// <list type="bullet">
        /// <item>Players, only with HurtPlayers (ruling 27): each whose capsule (head to feet, StoneRules.PlayerRadius) comes
        /// within the radius is knocked back on their own game, StoneRules.BlastDir (from the centre to the capsule, flat or
        /// 45 degrees up; the source's heading for one straight above or below it) x KnockbackSpeed x share, with the hitstun a
        /// hit by the source gives (StoneRules.HitstunSeconds of its kind and power). Knockbacks stack (Moves.Push adds).</item>
        /// <item>Living creatures: one plain ImpactHit each (per health object), damage 0 and cripple StoneRules.ExplodeCripple,
        /// the source owner's PlayerController as the wielder (as HitCreature); dead ones are skipped. No hit record.</item>
        /// <item>Loose rigidbodies (not kinematic, not a player's, not one of our stones): one push each, away from the centre
        /// (to its centre of mass; straight up when that is on the centre), <paramref name="force"/> x share in N s, the speed
        /// it adds held to [EarthModifiers] MaxStoneSpeed (a 1 kg log would otherwise fly off at 1000 m/s).</item>
        /// <item>Our own stones (not the source, not broken; a copy of Server.Stones): StoneBody.Blast with the same push
        /// (Rising ones left alone, grounded ones set loose first, held to MaxStoneSpeed).</item>
        /// </list>
        /// The games within 80 m get Applied(Exploded) at the centre (they play ExplodeEffect and ExplodeSound). One Debug line
        /// with what it pushed. Never throws: a failure is warned once, then Debug lines, and the rest of the blast goes on.</summary>
        internal static void Explode(Vector3 centre, float radius, float force, StoneBody? source) {
            try {
                if (!Finite(centre)) return;
                int games = 0;
                try { games = Net.SendApplied(source != null ? source.Id : 0u, Wire.Exploded, centre); }
                catch (Exception e) {
                    if (!blastSendFailed) { blastSendFailed = true; MelonLogger.Warning($"[Earth] Hits: telling the games a stone exploded failed (said once): {e.Message}"); }
                }
                string what = !Logs.Debug ? "" : source != null ? $"{source.Kind} #{source.Id} of {source.OwnerName}" : "a blast";
                if (!(radius > 0f) || float.IsInfinity(radius)) {
                    if (Logs.Debug) MelonLogger.Msg($"[Earth] Hits: {what} exploded at {centre}, blasting nothing ([EarthModifiers] ExplodeRadius {radius:0.##}); the look goes to {games} game(s)");
                    return;
                }
                if (!(force >= 0f) || float.IsInfinity(force)) force = 0f;
                List<string>? said = Logs.Debug ? new List<string>() : null;
                int players = Players(centre, radius, source, said);
                int creatures = 0, bodies = 0, stones = 0, loosened = 0, rising = 0;

                // creatures and loose rigidbodies: every solid collider in the sphere (the game's Explosive.Trigger does the same)
                Collider[] found = Physics.OverlapSphere(centre, radius, ~0, QueryTriggerInteraction.Ignore);
                var healths = new HashSet<int>();
                var nearest = new Dictionary<Rigidbody, float>();
                PlayerController? wielder = source != null ? OwnerController(source) : null;
                foreach (Collider col in found) {
                    if (col == null) continue;
                    try {
                        if (col.GetComponentInParent<StoneView>() != null) continue;   // our stones: from the registry, below
                        Vector3 point = col.bounds.ClosestPoint(centre);
                        float dist = (point - centre).magnitude;
                        Rigidbody rb = col.attachedRigidbody;
                        if (rb != null && !rb.isKinematic && !IsPlayers(rb) && (!nearest.TryGetValue(rb, out float had) || dist < had)) nearest[rb] = dist;
                        HealthObject? h = HealthOf(col);
                        if (h == null || h is PlayerHealth || !(h.Health > 0f) || !IsCreature(h) || !healths.Add(h.GetInstanceID())) continue;
                        Vector3 normal = centre - point;   // out of the creature, toward the blast
                        Vector3 dir = Direction(point - centre, Vector3.up);
                        Sane(ref point, ref normal, h.transform.position);
                        var hit = new ImpactHit(0f, StoneRules.ExplodeCripple, 1f, 1f, null, ImpactType.Blunt, point, normal, dir, null, wielder != null ? wielder.Entity : null, wielder);
                        h.DetectImpact(hit);
                        creatures++;
                        said?.Add($"creature '{h.name}'");
                    }
                    catch (Exception e) { BlastFailed("a collider", e); }
                }
                foreach (KeyValuePair<Rigidbody, float> kv in nearest) {
                    Rigidbody rb = kv.Key;
                    if (rb == null) continue;
                    try {
                        float share = StoneRules.ExplodeShare(kv.Value, radius);
                        Vector3 impulse = Away(centre, rb.worldCenterOfMass) * (force * share);
                        Vector3 dv = U(StoneRules.Pushed(V3.Zero, V(impulse), rb.mass, Server.MaxStoneSpeed));
                        if (dv == Vector3.zero) continue;
                        rb.AddForce(dv, ForceMode.VelocityChange);
                        bodies++;
                        said?.Add($"'{rb.name}' {dv.magnitude:0.##} m/s");
                    }
                    catch (Exception e) { BlastFailed("a loose rigidbody", e); }
                }

                // our own stones: a copy, as nothing here may change the registry under the loop
                foreach (StoneBody s in new List<StoneBody>(Server.Stones.Values)) {
                    if (s == null || ReferenceEquals(s, source) || s.Broken) continue;
                    try {
                        float share = StoneRules.ExplodeShare((s.Box.ClosestPoint(centre) - centre).magnitude, radius);
                        if (!(share > 0f)) continue;
                        if (s.State == StoneState.Rising) { rising++; continue; }   // half in the ground (ruling C6)
                        StoneState was = s.State;
                        if (!s.Blast(Away(centre, s.CentreOfMass) * (force * share), out bool freed)) continue;
                        stones++;
                        if (freed) loosened++;
                        said?.Add($"{s.Kind} #{s.Id} ({was}{(freed ? ", set loose" : "")}) {s.Speed:0.##} m/s");
                    }
                    catch (Exception e) { BlastFailed("a stone", e); }
                }
                if (said != null)
                    MelonLogger.Msg($"[Earth] Hits: {what} exploded at {centre} ({radius:0.##} m, {force:0.#} N s): {players} player(s) knocked back, {creatures} creature(s) staggered, {bodies} loose rigidbody(ies) and {stones} stone(s) pushed ({loosened} set loose{(rising > 0 ? $", {rising} rising left alone" : "")}){(said.Count > 0 ? ": " + string.Join(", ", said.ToArray()) : "")}; the look goes to {games} game(s)");
            }
            catch (Exception e) { BlastFailed("the blast", e); }
        }

        // The blast's players (only with HurtPlayers, ruling 27): this step's capsules (ReadPlayers) within the radius.
        static int Players(Vector3 centre, float radius, StoneBody? source, List<string>? said) {
            if (!HurtPlayers) return 0;
            ReadPlayers();
            float hitstun = source != null ? StoneRules.HitstunSeconds(source.Kind, source.Power) : 0f;
            V3 heading = source != null ? V(source.Velocity) : V3.Zero, c = V(centre);
            int n = 0;
            for (int i = 0; i < victims.Count; i++) {
                Victim v = victims[i];
                try {
                    float dist = Contact.SegmentPoint(V(v.Head), V(v.Feet), c, out V3 near) - StoneRules.PlayerRadius;   // to the capsule
                    float share = StoneRules.ExplodeShare(dist, radius);
                    if (!(share > 0f)) continue;
                    Vector3 knock = U(StoneRules.BlastDir(near - c, heading)) * (KnockbackSpeed * share);
                    if (knock == Vector3.zero && !(hitstun > 0f)) continue;
                    bool sent = false;
                    try { sent = Net.SendKnockback(v.Player, knock, hitstun); }
                    catch (Exception e) {
                        if (!sendFailed) { sendFailed = true; MelonLogger.Warning($"[Earth] Hits: sending a knockback failed (said once): {e.Message}"); }
                    }
                    if (sent) n++;
                    said?.Add($"player {v.Name} {knock} m/s, hitstun {hitstun:0.###} s{(sent ? "" : " (NOT sent)")}");
                }
                catch (Exception e) { BlastFailed("a player", e); }
            }
            return n;
        }

        /// <summary>The way from the blast's centre to <paramref name="to"/>, length 1; straight up when it is on the centre.</summary>
        static Vector3 Away(Vector3 centre, Vector3 to) {
            Vector3 d = to - centre;
            return Finite(d) && d.sqrMagnitude > 1e-8f ? d.normalized : Vector3.up;
        }

        /// <summary>Whether a component is a player's: under a PlayerController, or in an entity whose prefab root has one.</summary>
        static bool IsPlayers(Component c) {
            if (c.GetComponentInParent<PlayerController>() != null) return true;
            NetworkEntity e = c.GetComponentInParent<NetworkEntity>();
            NetworkEntity root = e != null ? e.PrefabRoot : null!;
            return root != null && root.GetComponent<PlayerController>() != null;
        }

        static void BlastFailed(string what, Exception e) {
            if (!blastFailed) { blastFailed = true; MelonLogger.Warning($"[Earth] Hits: {what} in a stone's blast failed (said once): {e}"); }
            else if (Logs.Debug) MelonLogger.Msg($"[Earth] Hits: {what} in a stone's blast failed again: {e.Message}");
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>The health object behind a collider: the game's own (its ImpactDetector's HealthTarget), else its entity's
        /// prefab root's (a player's own health under a PlayerController), as the archery mod's Targets.Resolve. Null for none.</summary>
        static HealthObject? HealthOf(Collider c) {
            ImpactDetector det = c.GetComponentInParent<ImpactDetector>();
            if (det != null && det.HealthTarget != null) return det.HealthTarget;
            NetworkEntity e = c.GetComponentInParent<NetworkEntity>();
            NetworkEntity root = e != null ? e.PrefabRoot : null!;
            if (root == null) return null;
            PlayerController pc = root.GetComponent<PlayerController>();
            if (pc != null) return pc.Health != null ? pc.Health : null;
            HealthObject h = root.GetComponentInChildren<HealthObject>(true);
            return h != null ? h : null;
        }

        /// <summary>A creature (the archery mod's Targets.IsCreature, without its dummies): a health object that is not a
        /// player's, with a stagger pool or a creature's AI. A tree or a loose item is not one.</summary>
        static bool IsCreature(HealthObject h) => !(h is PlayerHealth) && (h.HasCrippleStat || AgentOf(h) != null);

        /// <summary>The creature's AI: an Alta.Intelligence.Agent under its health object's prefab root (none under a player).
        /// Remembered per health object (null too: it has none), at most 1024.</summary>
        static Agent? AgentOf(HealthObject h) {
            int key = h.GetInstanceID();
            if (agentMemo.TryGetValue(key, out Agent? known)) return known != null ? known : null;
            NetworkEntity e = h.Entity;
            NetworkEntity root = e != null ? e.PrefabRoot : null!;
            Agent? a = null;
            if (root != null && root.GetComponent<PlayerController>() == null) {
                Agent found = root.GetComponentInChildren<Agent>(true);
                if (found != null) a = found;
            }
            if (agentMemo.Count >= MemoCap) agentMemo.Clear();
            agentMemo[key] = a;
            return a;
        }

        /// <summary>Stuns a struck creature through its Agent's own Stun(float) (Turabada, Gotera, GreaterGotera, Babu,
        /// Dragonfly; found by name once per type, as the archery mod's Weight.KnockDown), <paramref name="seconds"/> as the
        /// strength (the game's Stun ignores it: the creature's own stunned action plays). Returns the strength given, 0 when the
        /// creature has no Stun or it failed.</summary>
        static float Stun(HealthObject h, float seconds) {
            if (!(seconds > 0f)) return 0f;
            Agent? a = AgentOf(h);
            if (a == null) return 0f;
            Type type = a.GetType();
            if (!stunMethods.TryGetValue(type, out MethodInfo? m)) {
                m = type.GetMethod("Stun", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(float) }, null);
                stunMethods[type] = m;
                if (Logs.Debug) MelonLogger.Msg($"[Earth] Hits: {type.Name} {(m != null ? "can" : "cannot")} be stunned");
            }
            if (m == null) return 0f;
            try {
                m.Invoke(a, new object[] { seconds });
                return seconds;
            }
            catch (Exception e) {
                Exception inner = e is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e;
                if (!stunFailed) { stunFailed = true; MelonLogger.Warning($"[Earth] Hits: stunning a {type.Name} failed (said once): {inner.Message}"); }
                else if (Logs.Debug) MelonLogger.Msg($"[Earth] Hits: stunning a {type.Name} failed again: {inner.Message}");
                return 0f;
            }
        }

        /// <summary>The stone owner's PlayerController while they are online (the hit's wielder), else null.</summary>
        static PlayerController? OwnerController(StoneBody stone) {
            if (stone.Owner == 0UL) return null;
            foreach (IPlayer ip in Player.AllPlayers) {
                if (!(ip is Player p) || p == null || p.UserInfo == null || Server.AccountOf(p) != stone.Owner) continue;
                Connection c = p.ConnectionToRemotePlayer;
                PlayerController pc = p.PlayerController;
                return c != null && !c.IsDisposed && pc != null ? pc : null;
            }
            return null;
        }

        /// <summary>The way a hit goes: along the stone's velocity, or <paramref name="fallback"/> when it has none.</summary>
        static Vector3 Direction(Vector3 velocity, Vector3 fallback) {
            if (Finite(velocity) && velocity.sqrMagnitude > 1e-6f) return velocity.normalized;
            return Finite(fallback) && fallback.sqrMagnitude > 1e-8f ? fallback.normalized : Vector3.forward;
        }

        /// <summary>Never a point or a normal that is not a number, nor a zero normal (the archery mod's Hits.Sane).</summary>
        static void Sane(ref Vector3 point, ref Vector3 normal, Vector3 fallbackPoint) {
            if (!Finite(point)) point = fallbackPoint;
            if (!Finite(normal) || normal.sqrMagnitude < 1e-8f) normal = Vector3.up;
            else normal = normal.normalized;
        }

        static bool Finite(Vector3 v) => !float.IsNaN(v.x) && !float.IsInfinity(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.z);

        static V3 V(Vector3 v) => new V3(v.x, v.y, v.z);
        static Vector3 U(V3 v) => new Vector3(v.X, v.Y, v.Z);
    }
}
