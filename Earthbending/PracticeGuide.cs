using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace Earthbending {
    // Games only: the practice guide (Task 3c; the owner: "whenever I do testing, have a guide"), on the guides' shared
    // display (GuideDisplay.cs). While [EarthPose] Practice is on, the card names the next move of [EarthPose] PracticeList
    // to try and what each hand does, and the markers show its two hands (for a structure Base's hands until the base has
    // armed it, Bend.Armed, then the structure's), from the poses the machine runs (Bend.Pose: poses.txt over the built-in
    // ones) at [EarthPose] BodyScale. When the move fires the card says it worked and moves on; another move is named; a step
    // not fired for PracticeSkipSeconds is skipped; after the last the card shows the counts until Practice is switched off
    // (PracticeRun in Poses.cs is the step machine and the card's words). One log line per step and one at the end, never
    // behind Debug: the controller reads them after a play-test.
    //
    // It only watches: it hears of fired moves from Bend.Fired and changes nothing a move does. With Capture on the capture
    // guide has the display and this one waits; coming back (and every switch on) starts again from step 1. Its clock runs
    // only on frames with a local player, so a loading screen skips nothing. A frame allocates nothing unless a move fired,
    // a step ended or a setting changed; with Practice off a frame costs one bool test.
    internal static class PracticeGuide {
        static MelonPreferences_Entry<bool>? practiceEntry;
        static MelonPreferences_Entry<string>? listEntry;
        static MelonPreferences_Entry<float>? skipEntry, showEntry;

        static bool on, active, failed, restart;
        static string list = PracticeRun.DefaultList;
        static float skip = PracticeRun.DefaultSkipSeconds, show = PracticeRun.DefaultShowSeconds;
        static PracticeRun? run;
        static List<Move> steps = new List<Move>();   // the run's moves, for the log
        static double clock;
        static Move pending, shownMarker;
        static float pendingScore;
        static int shownVersion = -1, shownPoses = -1;
        static Move current;   // the step being tried now (None: none), for the hands trace

        /// <summary>The move the practice guide asks for now: its current step's move while it is tried; None while a result
        /// is shown, once done, and with the guide off or away (the hands trace's practice column).</summary>
        internal static Move Current => active ? current : Move.None;

        /// <summary>From Main.OnInitializeMelon on games, after Bend.Init.</summary>
        internal static void Init() {
            MelonPreferences_Category pose = Cfg.Category("EarthPose");
            practiceEntry = pose.CreateEntry("Practice", false, description: "Per game. On: the practice guide for play-tests. A card in front of you names the next move of PracticeList to try and what each hand does, with the two hand markers; when that move fires the card says so and goes on to the next, another move that fires is named, and at the end it shows the counts. Moves do what they always do. Off and on again starts from the first move. With Capture on, the capture's guide shows instead.");
            listEntry = pose.CreateEntry("PracticeList", PracticeRun.DefaultList, description: "Per game. The practice guide's moves in the order they are tried: names split by commas (Disc, Ball, Pillar, Cube, Wall, Straight, Kick, Stomp, Uppercut, Parry, Hold, Flick, Explode, Jump, Dash; a name twice is tried twice). A name that is no move is skipped with a warning; Base is never a step.");
            skipEntry = pose.CreateEntry("PracticeSkipSeconds", PracticeRun.DefaultSkipSeconds, description: "Per game. The practice guide: a move that has not fired after this many seconds is skipped. Above 0.");
            showEntry = pose.CreateEntry("PracticeShowSeconds", PracticeRun.DefaultShowSeconds, description: "Per game. The practice guide: 'worked', 'skipped' and the name of another move that fired are shown this many seconds. 0 or more.");
            Apply();
            Cfg.Changed += Apply;
            Bend.Fired += OnFired;
        }

        static void Apply() {
            on = practiceEntry != null && practiceEntry.Value;
            skip = Cfg.Positive(skipEntry, "EarthPose", "PracticeSkipSeconds", PracticeRun.DefaultSkipSeconds);
            show = Cfg.NotNegative(showEntry, "EarthPose", "PracticeShowSeconds", PracticeRun.DefaultShowSeconds);
            if (run != null) {
                run.SkipSeconds = skip;
                run.ShowSeconds = show;
            }
            string text = listEntry?.Value ?? PracticeRun.DefaultList;
            if (text == list) return;
            list = text;
            restart = true;   // another list: from its first move, at the next frame the guide shows
        }

        /// <summary>A move fired (Bend.Fire, in Main.OnUpdate): kept for this frame's Tick, with its score.</summary>
        static void OnFired(Move move, bool rightIsA) {
            if (!active) return;
            pending = move;
            pendingScore = Bend.LastScore;
        }

        /// <summary>Every frame on games, after the game has moved the head and hands (Main.OnLateUpdate, after
        /// CaptureGuide.Tick). With Practice off, one bool test. Never throws: a failure is said once and the guide stays
        /// away until Practice is switched on again.</summary>
        internal static void Tick() {
            if (!on || Hands.Capturing) {
                if (active || failed) {
                    GuideDisplay.Hide(GuideDisplay.Practice);
                    active = false;
                    failed = false;
                    pending = Move.None;
                }
                return;
            }
            if (failed) return;
            try { Frame(); }
            catch (Exception e) {
                failed = true;
                active = false;
                pending = Move.None;
                MelonLogger.Warning($"[Earth] Practice: the practice guide failed and is hidden until Practice is switched on again (the moves themselves go on): {e}");
                try { GuideDisplay.Hide(GuideDisplay.Practice); } catch { }
            }
        }

        static void Frame() {
            if (!GuideDisplay.Frame(GuideDisplay.Practice, "practice", out bool rewrite)) {
                pending = Move.None;   // no player: the step's clock stands
                return;
            }
            if (!active || restart) Start();
            clock += Time.unscaledDeltaTime;
            PracticeView v = run!.Step(clock, pending, pendingScore);
            pending = Move.None;
            current = v.Phase == PracticePhase.Try ? v.Move : Move.None;
            if (v.Log != null) MelonLogger.Msg(v.Log);

            if (rewrite || v.Version != shownVersion) {
                shownVersion = v.Version;
                GuideDisplay.Write(PracticeRun.Card(in v, GuideDisplay.RightHex, GuideDisplay.LeftHex, GuideDisplay.Markers, GuideDisplay.WrapLetters));
            }
            Move marker = PracticeRun.Marker(in v, Bend.Armed);
            if (rewrite || marker != shownMarker || Bend.PosesVersion != shownPoses) {
                shownMarker = marker;
                shownPoses = Bend.PosesVersion;
                if (marker != Move.None && Bend.Pose(marker, out PoseSpec spec)) GuideDisplay.Pose(true, in spec);
                else GuideDisplay.Pose(false, default);
            }
            GuideDisplay.Place(Hands.BodyScale);
        }

        /// <summary>From step 1: at every switch on, on coming back from the capture, and when the list changed.</summary>
        static void Start() {
            if (run != null && !restart) run.Restart();   // the same list: the same run from its first step
            else {
                steps = PracticeRun.Parse(list, out List<string> errors);
                if (errors.Count > 0) MelonLogger.Warning($"[Earth] Practice: [EarthPose] PracticeList: {string.Join("; ", errors)} (skipped)");
                run = new PracticeRun(steps, skip, show);
            }
            active = true;
            restart = false;
            clock = 0.0;
            pending = Move.None;
            shownVersion = -1;
            MelonLogger.Msg($"[Earth] Practice: on, {steps.Count} step(s): {string.Join(", ", steps)}; a step is skipped after {skip:0.#} s");
        }
    }
}
