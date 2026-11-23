using System;
using MelonLoader;

namespace Earthbending {
    // Games only: the guide for the pose capture (Task 3b), on the guides' shared display (GuideDisplay.cs). While
    // [EarthPose] Capture is on the card names the next pose to strike and says what each hand does (PoseGuide in Poses.cs,
    // by the recorder's snapshot count), and the two markers show where the right and the left hand go, from that pose's
    // built-in spec (PoseLibrary.Defaults, never poses.txt: the capture is what makes it) at body scale 1 as the recorder
    // writes it. After the sixteenth snapshot the card says so and the markers go.
    //
    // A frame allocates nothing: the text is written only when the count or a setting changes. With Capture off a frame costs
    // one bool test.
    internal static class CaptureGuide {
        static readonly PoseSpec[] poses = PoseLibrary.Defaults;
        static bool active, failed;
        static int shownTaken = -1;

        /// <summary>Every frame on games, after the game has moved the head and hands (Main.OnLateUpdate). With Capture off,
        /// one bool test. Never throws: a failure is said once per capture and the guide stays away until the next one.</summary>
        internal static void Tick() {
            if (!Hands.Capturing) {
                if (active || failed) {
                    GuideDisplay.Hide(GuideDisplay.Capture);
                    active = false;
                    failed = false;
                }
                return;
            }
            if (failed) return;
            try {
                active = true;
                if (!GuideDisplay.Frame(GuideDisplay.Capture, "capture", out bool rewrite)) return;
                int taken = Hands.CaptureTaken;
                if (rewrite || taken != shownTaken) Write(taken);
                GuideDisplay.Place(1f);
            }
            catch (Exception e) {
                failed = true;
                MelonLogger.Warning($"[Earth] Guide: the capture guide failed and is hidden until the capture is switched on again (the recorder itself goes on): {e}");
                try { GuideDisplay.Hide(GuideDisplay.Capture); } catch { }
            }
        }

        /// <summary>The card's text and the markers' pose for a count of snapshots.</summary>
        static void Write(int taken) {
            shownTaken = taken;
            GuideDisplay.Write(PoseGuide.Card(taken, GuideDisplay.RightHex, GuideDisplay.LeftHex, GuideDisplay.Markers, GuideDisplay.WrapLetters));
            int index = Array.FindIndex(poses, p => p.Move == PoseGuide.Next(taken));
            if (index >= 0) GuideDisplay.Pose(true, in poses[index]);
            else GuideDisplay.Pose(false, default);
        }
    }
}
