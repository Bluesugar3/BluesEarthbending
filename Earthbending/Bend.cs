using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

namespace Earthbending {
    // Games only: the pose machine in the game (spec 3.2, 3.3, 3.6). Every frame (Main.OnUpdate) Bend.Tick reads the local
    // player's head (Hands.Raw), the hands in the body frame at [EarthPose] BodyScale, a fixed number (Hands.Read; Ruling 35:
    // the size is not measured), whether they may bend (Bender.CanBend, through the two-frame EmptyGate),
    // the ground (Hands.GroundState, through the GroundGrace of [EarthPose] GroundGraceSeconds: ruling 16) and the hitstun,
    // and steps the PoseMachine. A fired move gives one haptic tick on both hands ([EarthLooks] PoseHaptic, PoseHapticSeconds),
    // does what it does (Jump: Moves.Push straight up at [EarthMovement] JumpSpeed, and Moves.Dust at the feet; an armed
    // structure: a Summon claim to the server, Net.SendSummon, only when the server runs this mod; a modifier: the stone in
    // reach most in front (Reach.Pick over StoneView.Live) gets a Modify claim, Net.SendModify, and a Straight pushes the player
    // back, Moves.Pushback; no stone in reach sends nothing), logs one Debug line, then raises Fired. Nothing fires while the
    // pose recorder runs ([EarthPose] Capture), while downed or dead (as in hitstun: a pose held through it does not fire after
    // it), or with no local player (the machine then starts over).
    //
    // The machine is built once, from the poses (PoseLibrary.Defaults, each replaced by UserData\Earthbending\poses.txt's pose
    // of the same move when that file has one: PoseLibrary.Merge; a line of it that does not parse is one warning, and its pose
    // stays the built-in one) and the four [EarthPose] timings, and built again only when one of those changed: at a settings
    // reload, or when poses.txt's write time changed (looked at every 2 s). A rebuild starts every pose and cooldown over.
    // Bend.Tick allocates nothing on a frame without a fired move or a look at poses.txt.

    /// <summary>Who may bend.</summary>
    internal static class Bender {
        /// <summary>Whether <paramref name="pc"/> may bend now: what the pose machine is given as its empty-hands test. Today
        /// both hands empty (<see cref="Hands.Empty(PlayerController)"/>). The one place a future gauntlet (bending with a
        /// worn item, or with something held) changes.</summary>
        internal static bool CanBend(PlayerController pc) => Hands.Empty(pc);
    }

    internal static class Bend {
        const float DefaultGroundGraceSeconds = 0.15f, MaxGroundGraceSeconds = 0.5f, DefaultJumpSpeed = 6f, DefaultPoseHaptic = 0.4f, DefaultPoseHapticSeconds = 0.05f;
        const float PosesLookSeconds = 2f;

        static MelonPreferences_Entry<float>? minHold, rearm, structureWindow, moveCooldown, groundGrace, jumpSpeedEntry, poseHaptic, poseHapticSeconds;
        static MelonPreferences_Entry<float>? structureDeep, structureStillMetres, structureStillSeconds;
        static float jumpSpeed = DefaultJumpSpeed, haptic = DefaultPoseHaptic, hapticSeconds = DefaultPoseHapticSeconds;

        static PoseMachine? machine;
        static PoseSpec[] running = new PoseSpec[0];   // the poses the machine was built with
        static PoseValues wanted, built;          // the timings asked for by the settings, and those the machine was built with
        static string posesPath = "";
        static string[]? posesLines;              // poses.txt's lines the machine was built from (null: no file)
        static DateTime posesStamp;               // their write time (default: no file)
        static float nextPosesLook;
        static bool posesReadWarned;

        static readonly EmptyGate emptyGate = new EmptyGate();
        static readonly GroundGrace grace = new GroundGrace(DefaultGroundGraceSeconds);
        static bool idle = true, failed, buzzFailed;

        /// <summary>A move fired: the move, and whether A (the pose's first hand, e.g. Hold's holding hand) is the right hand.
        /// Raised after the move's own effect (Jump's push and dust). Each handler runs in its own try.</summary>
        internal static event Action<Move, bool>? Fired;

        /// <summary>The score of the last move fired (0 exact, 1 the edge of its zone): a <see cref="Fired"/> handler reads
        /// its move's score here.</summary>
        internal static float LastScore => machine?.LastScore ?? 0f;

        /// <summary>Whether the base has armed the structures now (PoseMachine.Armed, on the machine's own clock).</summary>
        internal static bool Armed => machine != null && machine.Armed(Time.unscaledTimeAsDouble);

        /// <summary>Another number each time the machine was built again (poses.txt or a timing changed).</summary>
        internal static int PosesVersion { get; private set; }

        /// <summary>A move's pose as the machine runs it (poses.txt's over the built-in one). False for a move with no pose.</summary>
        internal static bool Pose(Move move, out PoseSpec spec) {
            foreach (PoseSpec s in running)
                if (s.Move == move) { spec = s; return true; }
            spec = default;
            return false;
        }

        /// <summary>From Main.OnInitializeMelon on games, after Moves.Init and Hands.Init.</summary>
        internal static void Init() {
            PoseValues d = PoseValues.Default;
            MelonPreferences_Category pose = Cfg.Category("EarthPose");
            minHold = pose.CreateEntry("MinHoldSeconds", d.MinHoldSeconds, description: "Per game. A pose counts once the hands have been inside it this long without a break. 0 (the default): the first frame they are inside it, so a pose fires as the hands pass through it (flow, not hold). 0 or more.");
            rearm = pose.CreateEntry("RearmSeconds", d.RearmSeconds, description: "Per game. A pose fires again only after the hands have left it for this long. 0 or more.");
            structureWindow = pose.CreateEntry("StructureWindowSeconds", d.StructureWindowSeconds, description: "Per game. A structure pose fires only when the base pose was held within this many seconds before it. 0 or more.");
            moveCooldown = pose.CreateEntry("MoveCooldownSeconds", d.MoveCooldownSeconds, description: "Per game. After a Jump (or a Dash) fires, the same move waits this long before it can fire again. 0 or more.");
            structureDeep = pose.CreateEntry("StructureDeepScore", d.StructureDeepScore, description: "Per game. A structure (Disc, Ball, Pillar, Cube, Wall) is not fired as the hands enter it but at its deepest point, so the edge of one crossed on the way to another fires nothing: of the structures the hands are in, the best one fires once its score is at most this (0 is the pose's exact place, 1 the edge of its zone; 0.5: half way in), or once the hands stop in it (StructureStillMetres, StructureStillSeconds). 0 or more; 1 or more fires on entry.");
            structureStillMetres = pose.CreateEntry("StructureStillMetres", d.StructureStillMetres, description: "Per game. A structure struck loosely (never as deep as StructureDeepScore) fires once both hands have stayed within this many metres of where they were for StructureStillSeconds, and it has got no deeper meanwhile. Hands moving slower than this over that time count as stopped (0.01 m in 0.06 s: about 0.17 m/s). 0 or more.");
            structureStillSeconds = pose.CreateEntry("StructureStillSeconds", d.StructureStillSeconds, description: "Per game. See StructureStillMetres: how long the hands must be still in a structure struck loosely before it fires. 0 or more.");
            groundGrace = pose.CreateEntry("GroundGraceSeconds", DefaultGroundGraceSeconds, description: "Per game. A pose counts you as standing for this long after the game last had you on the ground, on the ground you stood on then (the game's on-the-ground flag flickers while walking, and a pose judged in a flicker would be used up). 0 to 0.5 (more is used as 0.5: a longer grace would let a Jump fire well after leaving the ground).");
            jumpSpeedEntry = Cfg.Category("EarthMovement").CreateEntry("JumpSpeed", DefaultJumpSpeed, description: "Per game. The Jump pose pushes you up at this many m/s (6: about 2.2 m high). 0 or more.");
            MelonPreferences_Category looks = Cfg.Category("EarthLooks");
            poseHaptic = looks.CreateEntry("PoseHaptic", DefaultPoseHaptic, description: "Per game. Every move a pose fires buzzes both hands this strongly (0 to 1; 0: no buzz).");
            poseHapticSeconds = looks.CreateEntry("PoseHapticSeconds", DefaultPoseHapticSeconds, description: "Per game. How long that buzz lasts, in seconds (0: the game's own shortest tick).");
            posesPath = Path.Combine(MelonEnvironment.UserDataDirectory, "Earthbending", "poses.txt");
            Apply();
            Cfg.Changed += Apply;
        }

        static void Apply() {
            float g = Cfg.NotNegative(groundGrace, "EarthPose", "GroundGraceSeconds", DefaultGroundGraceSeconds);
            if (g > MaxGroundGraceSeconds) {
                MelonLogger.Warning($"[Earth] Settings: [EarthPose] GroundGraceSeconds {g} is more than {MaxGroundGraceSeconds}; using {MaxGroundGraceSeconds}");
                g = MaxGroundGraceSeconds;
            }
            grace.Seconds = g;
            jumpSpeed = Cfg.NotNegative(jumpSpeedEntry, "EarthMovement", "JumpSpeed", DefaultJumpSpeed);
            haptic = Mathf.Min(1f, Cfg.NotNegative(poseHaptic, "EarthLooks", "PoseHaptic", DefaultPoseHaptic));
            hapticSeconds = Cfg.NotNegative(poseHapticSeconds, "EarthLooks", "PoseHapticSeconds", DefaultPoseHapticSeconds);
            PoseValues v = PoseValues.Default;
            v.MinHoldSeconds = Cfg.NotNegative(minHold, "EarthPose", "MinHoldSeconds", v.MinHoldSeconds);
            v.RearmSeconds = Cfg.NotNegative(rearm, "EarthPose", "RearmSeconds", v.RearmSeconds);
            v.StructureWindowSeconds = Cfg.NotNegative(structureWindow, "EarthPose", "StructureWindowSeconds", v.StructureWindowSeconds);
            v.MoveCooldownSeconds = Cfg.NotNegative(moveCooldown, "EarthPose", "MoveCooldownSeconds", v.MoveCooldownSeconds);
            v.StructureDeepScore = Cfg.NotNegative(structureDeep, "EarthPose", "StructureDeepScore", v.StructureDeepScore);
            v.StructureStillMetres = Cfg.NotNegative(structureStillMetres, "EarthPose", "StructureStillMetres", v.StructureStillMetres);
            v.StructureStillSeconds = Cfg.NotNegative(structureStillSeconds, "EarthPose", "StructureStillSeconds", v.StructureStillSeconds);
            wanted = v;
            Build();
        }

        /// <summary>Reads poses.txt and builds the machine again when the poses or the timings differ from what it was built
        /// with. A file that cannot be read keeps the poses as they were (and is read again at the next look). Runs at every
        /// settings reload and every change of poses.txt, each of which lets a failing frame warn once more.</summary>
        static void Build() {
            failed = false;
            string[]? lines = posesLines;
            if (ReadPoses(out string[]? read, out DateTime stamp)) {
                lines = read;
                posesStamp = stamp;
            }
            if (machine != null && SameTimings(wanted, built) && SameLines(lines, posesLines)) return;
            PoseSpec[]? fromFile = null;
            if (lines != null) {
                fromFile = PoseLibrary.Parse(lines, out List<string> errors);
                foreach (string e in errors) MelonLogger.Warning($"[Earth] Poses: poses.txt {e} (skipped: that pose stays the built-in one)");
            }
            PoseSpec[] poses = PoseLibrary.Merge(PoseLibrary.Defaults, fromFile, out int fromFileCount);
            machine = new PoseMachine(poses, wanted);
            running = poses;
            PosesVersion++;
            built = wanted;
            posesLines = lines;
            idle = true;   // the next frame with a player starts from a fresh machine, gates included
            emptyGate.Reset();
            grace.Reset();
            MelonLogger.Msg($"[Earth] Poses: the machine runs {poses.Length} poses ({(lines == null ? "all built in; no " + posesPath : fromFileCount + " from " + posesPath + ", the rest built in")}); "
                + $"hold {wanted.MinHoldSeconds:0.###} s, rearm {wanted.RearmSeconds:0.###} s, a structure within {wanted.StructureWindowSeconds:0.###} s of the base, "
                + $"fired at a score of {wanted.StructureDeepScore:0.###} or with the hands within {wanted.StructureStillMetres:0.###} m for {wanted.StructureStillSeconds:0.###} s, Jump and Dash cooldown {wanted.MoveCooldownSeconds:0.###} s");
        }

        static bool ReadPoses(out string[]? lines, out DateTime stamp) {
            lines = null;
            stamp = default;
            try {
                if (File.Exists(posesPath)) {
                    stamp = File.GetLastWriteTimeUtc(posesPath);
                    lines = File.ReadAllLines(posesPath);
                }
                posesReadWarned = false;
                return true;
            }
            catch (Exception e) {
                if (!posesReadWarned) {
                    posesReadWarned = true;
                    MelonLogger.Warning($"[Earth] Poses: could not read {posesPath} ({e.Message}); the poses stay as they were, and it is read again once it can be");
                }
                return false;
            }
        }

        /// <summary>poses.txt's write time now (default: no file); on an error the last one seen, so nothing is reloaded.</summary>
        static DateTime PosesStamp() {
            try { return File.Exists(posesPath) ? File.GetLastWriteTimeUtc(posesPath) : default; }
            catch { return posesStamp; }
        }

        static bool SameTimings(PoseValues a, PoseValues b) =>
            a.MinHoldSeconds == b.MinHoldSeconds && a.RearmSeconds == b.RearmSeconds && a.StructureWindowSeconds == b.StructureWindowSeconds && a.MoveCooldownSeconds == b.MoveCooldownSeconds
            && a.StructureDeepScore == b.StructureDeepScore && a.StructureStillMetres == b.StructureStillMetres && a.StructureStillSeconds == b.StructureStillSeconds;

        static bool SameLines(string[]? a, string[]? b) {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>Every frame on games (Main.OnUpdate, after Hands.Tick).</summary>
        internal static void Tick() {
            if (machine == null) return;
            try {
                float t = Time.unscaledTime;
                if (t >= nextPosesLook) {
                    nextPosesLook = t + PosesLookSeconds;
                    if (PosesStamp() != posesStamp) Build();
                }
                PoseMachine m = machine;
                PlayerController pc = PlayerController.Current;
                if (pc == null || Hands.Capturing || !Hands.Raw(out Vector3 head, out float yawDeg, out Transform rightHand, out Transform leftHand)) {
                    StartOver();
                    return;
                }
                idle = false;
                double now = Time.unscaledTimeAsDouble;
                Hands.GroundState(out bool grounded, out bool natural);
                Hands.Read(head, yawDeg, rightHand, leftHand, out HandSample right, out HandSample left);
                bool canBend = emptyGate.Step(Bender.CanBend(pc));
                bool standing = grace.Step(now, grounded, natural, out bool onNatural);
                bool stunned = Moves.Stunned || pc.IsDowned || !pc.IsAlive;
                Move move = m.Step(now, right, left, canBend, standing, onNatural, stunned, out bool rightIsA);
                if (move != Move.None) Fire(pc, move, rightIsA, m.LastScore, head, yawDeg);
            }
            catch (Exception e) {
                if (failed) return;
                failed = true;
                MelonLogger.Warning($"[Earth] Bend: the pose machine's frame failed (said once until the next settings reload): {e}");
            }
        }

        /// <summary>No local player, or the recorder is on: every pose, the arming and both gates start over (once).</summary>
        static void StartOver() {
            if (idle) return;
            idle = true;
            machine?.Reset();
            emptyGate.Reset();
            grace.Reset();
        }

        static void Fire(PlayerController pc, Move move, bool rightIsA, float score, Vector3 head, float yawDeg) {
            if (Logs.Debug) MelonLogger.Msg($"[Earth] Bend: {move} fired (A on the {(rightIsA ? "right" : "left")} hand, score {score:0.00})");
            try { Buzz(pc); }
            catch (Exception e) {
                if (!buzzFailed) { buzzFailed = true; MelonLogger.Warning($"[Earth] Bend: the haptic tick failed (said once): {e.Message}"); }
            }
            try {
                if (move == Move.Jump) { if (Moves.Push(new Vector3(0f, jumpSpeed, 0f))) Moves.Dust(pc.RealFeetPosition); }
                else if (PoseMachine.IsStructure(move)) Summon(move, head, yawDeg);
                else if (PoseMachine.IsModifier(move)) Modify(pc, move, rightIsA, head, yawDeg);
            }
            catch (Exception e) {
                MelonLogger.Warning($"[Earth] Bend: {move} failed: {e.Message}");
            }
            Action<Move, bool>? handlers = Fired;
            if (handlers == null) return;
            foreach (Delegate d in handlers.GetInvocationList()) {
                try { ((Action<Move, bool>)d)(move, rightIsA); }
                catch (Exception e) { MelonLogger.Warning($"[Earth] Bend: a Fired handler ({d.Method.DeclaringType?.Name}.{d.Method.Name}) failed: {e}"); }
            }
        }

        /// <summary>An armed structure fired: asks the server for that stone (spec 3.2) with this frame's head and yaw. Only when
        /// the server runs this mod (ruling 12: SyncLib gave our prefabs its hashes); otherwise nothing is sent, one Debug line.
        /// The server checks the claim and raises the stone (Server.cs); a refusal is said only in the server's log.</summary>
        static void Summon(Move move, Vector3 head, float yawDeg) {
            if (!SummonCheck.KindOf(move, out StoneKind kind)) return;
            if (Prefabs.Of(kind) == null) {
                if (Logs.Debug) MelonLogger.Msg($"[Earth] Bend: {move} not sent: this server does not run Blue's Earthbending (no stone prefabs)");
                return;
            }
            bool sent = Net.SendSummon(new SummonClaim(kind, new V3(head.x, head.y, head.z), yawDeg));
            if (Logs.Debug) MelonLogger.Msg($"[Earth] Bend: Summon {kind} {(sent ? "sent" : "NOT sent (no connection to the server)")} (head {head}, yaw {yawDeg:0.#})");
        }

        /// <summary>A modifier fired: picks the stone it goes to (<see cref="Target"/>) and asks the server to apply it (spec
        /// 3.2, 3.8) with this frame's head and yaw and the pose's A hand (0 right, 1 left). No stone in reach: nothing is sent,
        /// and a Straight pushes no one. Only for a stone whose prefab this server registered (ruling 12). A Straight that was
        /// sent pushes the local player back (Moves.Pushback), harder when the stone is anchored as this game sees it (its
        /// rigidbody kinematic); the server may still refuse the claim (its log says why).</summary>
        static void Modify(PlayerController pc, Move move, bool rightIsA, Vector3 head, float yawDeg) {
            if (!ModifyCheck.ModifierOf(move, out Modifier mod)) return;
            StoneView? target = Target(pc, head, yawDeg);
            if (target == null) {
                if (Logs.Debug) MelonLogger.Msg($"[Earth] Bend: {mod} not sent: no stone in reach ({StoneView.Live.Count} stone(s) on this game)");
                return;
            }
            uint id = target.Entity != null ? target.Entity.Identifier : 0u;
            if (Prefabs.Of(target.Kind) == null) {
                if (Logs.Debug) MelonLogger.Msg($"[Earth] Bend: {mod} not sent: this server does not run Blue's Earthbending (no stone prefabs)");
                return;
            }
            bool anchored = target.Body != null && target.Body.isKinematic;
            bool sent = Net.SendModify(new ModifyClaim(mod, (byte)(rightIsA ? 0 : 1), id, new V3(head.x, head.y, head.z), yawDeg));
            if (Logs.Debug) MelonLogger.Msg($"[Earth] Bend: Modify {mod} on {target.Kind} #{id} ({(anchored ? "anchored" : "loose")}) {(sent ? "sent" : "NOT sent (no connection to the server)")} (head {head}, yaw {yawDeg:0.#})");
            if (sent && mod == Modifier.Straight) Moves.Pushback(yawDeg, anchored);
        }

        static readonly List<(uint id, V3 min, V3 max)> candidates = new List<(uint id, V3 min, V3 max)>();
        static readonly List<StoneView> candidateViews = new List<StoneView>();

        /// <summary>The stone a modifier struck now goes to: of the live stones on this game (StoneView.Live, no scene
        /// search), the one Reach.Pick picks from this head, yaw and the local player's feet, with [EarthModifiers] Reach and
        /// MaxHeight (no slack); each stone's box is its collider's world bounds. Null when none is in reach.</summary>
        static StoneView? Target(PlayerController pc, Vector3 head, float yawDeg) {
            candidates.Clear();
            candidateViews.Clear();
            foreach (StoneView v in StoneView.Live) {
                if (v == null || !v.isActiveAndEnabled || v.Shape == null || v.Entity == null || v.Entity.IsBeingDestroyed || v.Entity.Identifier == 0u) continue;
                Bounds b = v.Shape.bounds;
                candidates.Add((v.Entity.Identifier, new V3(b.min.x, b.min.y, b.min.z), new V3(b.max.x, b.max.y, b.max.z)));
                candidateViews.Add(v);
            }
            uint id = Reach.Pick(new V3(head.x, head.y, head.z), yawDeg, candidates, pc.RealFeetPosition.y, ReachSettings.Reach, ReachSettings.MaxHeight);
            StoneView? picked = null;
            if (id != 0u)
                for (int i = 0; i < candidates.Count; i++) if (candidates[i].id == id) { picked = candidateViews[i]; break; }
            candidates.Clear();
            candidateViews.Clear();
            return picked;
        }

        /// <summary>One tick on both hands. TriggerHaptic's second argument is the buzz's length in seconds (PlayerInput.Haptic
        /// sends an impulse every frame for that long; 0 is one 0.01 s impulse, which the game's own one-off ticks use).</summary>
        static void Buzz(PlayerController pc) {
            if (!(haptic > 0f)) return;
            Controller r = pc.RightController, l = pc.LeftController;
            if (r != null) r.TriggerHaptic(haptic, hapticSeconds);
            if (l != null) l.TriggerHaptic(haptic, hapticSeconds);
        }
    }
}
