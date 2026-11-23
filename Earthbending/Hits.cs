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
    //     who just pushed it, does not. Its owner CAN be hit by it otherwise (ruling 25). With HurtPlayers off, stones ignore
    //     players entirely: no damage, no knockback, no break (StoneRules.HitsPlayer; the capsule test does not even run).
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
        static bool contactFailed, playersFailed, playerReadFailed, stunFailed, sendFailed;

        struct Victim {
            public Player Player;
            public PlayerHealth Health;
            public string Name;
            public Vector3 Head, Feet;
        }

        /// <summary>From Server.Init on the dedicated server: the [EarthDamage] settings.</summary>
        internal static void Init() {
            MelonPreferences_Category cat = Cfg.Category("EarthDamage");
            hurtPlayersEntry = cat.CreateEntry("HurtPlayers", DefaultHurtPlayers, description: "Server. Stones hit players: a moving stone that comes at a player damages them (whatever the server's PvP setting; the game's spawn invincibility still applies), knocks them back and breaks on them. Off: stones ignore players entirely (no damage, no knockback, no break; they still hit creatures and clash), for a public server: a knockback alone can push someone off a cliff.");
            damagePerPowerEntry = cat.CreateEntry("DamagePerPower", DefaultDamagePerPower, description: "Server. A stone's hit deals this much damage per point of its power (Blunt; power is the stone's own, +1 grounded, +1 moving faster than MovingSpeed: a still Cube is 3, a sliding Pillar pushed by Straight 3). A guess to tune from the Debug lines' health before and after. 0 or more (0: no damage).");
            cripplePerPowerEntry = cat.CreateEntry("CripplePerPower", DefaultCripplePerPower, description: "Server. A stone's hit also deals this much cripple per point of power, which knocks a creature down once its stagger pool runs out. 0 or more.");
            minHitSpeedEntry = cat.CreateEntry("MinHitSpeed", DefaultMinHitSpeed, description: "Server. A stone hits a creature or a player only when the stone itself moves this many m/s or more (toward the player; a creature walking into a standing stone is not hit), and two stones clash only when they meet this fast. A rising stone never hits or clashes. 0 or more.");
            movingSpeedEntry = cat.CreateEntry("MovingSpeed", DefaultMovingSpeed, description: "Server. A stone moving faster than this many m/s has one more power for its hits and clashes. 0 or more.");
            knockbackSpeedEntry = cat.CreateEntry("KnockbackSpeed", DefaultKnockbackSpeed, description: "Server. A struck player is pushed this many m/s along the stone's way (flat, or 45 degrees up when the stone rose that steeply), on their own game, and cannot bend for power x 0.7 / 3 s (never after a Disc). 0 or more.");
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
            if (r != ClashResult.BBreaks) a.Break(Logs.Debug ? $"clash with {b.Kind} #{b.Id} (power {pa} against {pb})" : "clash");
            if (r != ClashResult.ABreaks) b.Break(Logs.Debug ? $"clash with {a.Kind} #{a.Id} (power {pb} against {pa})" : "clash");
            // spec 3.7: the winner carries on at its old speed (the step's solver stopped or bounced it against the loser, which
            // has stopped colliding); a kinematic winner (Still) has none to get back. No hitstop freeze yet (Task 15).
            StoneBody? winner = r == ClashResult.ABreaks ? b : r == ClashResult.BBreaks ? a : null;
            bool carried = winner != null && winner.CarryOn();
            if (Logs.Debug)
                MelonLogger.Msg($"[Earth] Hits: {a.Kind} #{a.Id} of {a.OwnerName} ({a.State}, power {pa}, {a.Velocity.magnitude:0.##} m/s) met {b.Kind} #{b.Id} of {b.OwnerName} ({b.State}, power {pb}, {b.Velocity.magnitude:0.##} m/s) at {rel:0.##} m/s: {(winner == null ? "both break" : $"#{(winner == a ? b.Id : a.Id)} breaks, #{winner.Id} {(carried ? $"carries on at {winner.Velocity.magnitude:0.##} m/s" : "stands (kinematic)")}")}");
        }

        static void HitCreature(StoneBody stone, HealthObject h, Vector3 point, Vector3 normal, float rel) {
            int power = stone.Power;
            if (power <= 0) return;   // a slow loose Disc touches without a hit (spec 2.2)
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
        /// (its own speed, this step's or the last one's, at least MinHitSpeed; never Rising) against every player's capsule;
        /// nothing at all with HurtPlayers off (StoneRules.HitsPlayer decides). Never throws.</summary>
        internal static void TestPlayers(StoneBody stone) {
            try {
                if (stone == null || stone.Broken || !HurtPlayers) return;   // HurtPlayers off: stones ignore players entirely
                Vector3 now = stone.Velocity, last = stone.LastVelocity;
                if (!StoneRules.CanHit(stone.State, Mathf.Max(now.magnitude, last.magnitude), MinHitSpeed)) return;
                Collider? shape = stone.Shape;
                if (shape == null) return;
                ReadPlayers();
                for (int i = 0; i < victims.Count && !stone.Broken; i++) {
                    Victim v = victims[i];
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
