using System;
using MelonLoader;
using UnityEngine;

namespace Earthbending {
    // Games only: the one card-and-markers display of the two guides (the pose capture's, CaptureGuide.cs, Task 3b; the
    // practice guide's, PracticeGuide.cs, Task 3c). The owner cannot read a chat window in the headset, so a floating card in
    // front of them carries a guide's words, and two markers show where the right and the left hand go: a ring at the hand's
    // place (lying in the palm's plane), a line along the fingers (12 cm) and a shorter one out of the palm (6 cm), from one
    // pose's spec, the A hand on the right. A marker turns nearly white and thicker while its hand is inside the pose's zone
    // round it (the ellipsoid of spec 3.3); directions are not judged.
    //
    // A guide (a driver) calls, each frame it wants the display: Frame (shows it, follows the head), then Write and Pose when
    // its words or its pose changed or Frame said to write again, then Place. One guide has it at a time: the last to call
    // Frame; Hide does nothing for a guide that no longer has it.
    //
    // Everything is placed from the head's place and flat yaw (the body frame run backwards, BodyFrame.ToWorld), eased toward
    // the head with [EarthLooks] GuideFollowSeconds so head jitter does not shake it; it jumps straight to place when it
    // appears (and after a teleport). All of it is the mod's own objects under one DontDestroyOnLoad host, built once at the
    // first use and switched on and off; a frame allocates nothing, the text is written only when a guide says so.
    // ponytail: legacy TextMesh with Unity's built-in Arial, as the archery mod's Tutorial.cs; a play-test tool.
    internal static class GuideDisplay {
        /// <summary>The guides, as <see cref="Frame"/> and <see cref="Hide"/> know them.</summary>
        internal const int Capture = 1, Practice = 2;

        /// <summary>No line of the card is longer than this many letters: the card is then about 35 degrees wide at the
        /// default distance and letter size, which reads without turning the head.</summary>
        internal const int WrapLetters = 34;

        const float DefaultDistance = 1.2f, DefaultDrop = 0.15f, DefaultTextSize = 0.035f, DefaultFollow = 0.3f;
        const string DefaultRight = "#FF8A1E", DefaultLeft = "#3FA9FF", DefaultBack = "#000000B0";

        // the look of a play-test tool, so not settings
        const int FontSize = 64, RingPoints = 24;
        const float RingRadius = 0.04f, FingersMetres = 0.12f, PalmMetres = 0.06f, LineWidth = 0.008f, NearWidth = 0.014f, NearWhite = 0.8f;
        const float LineHeightEm = 1.15f, PlateMarginEm = 0.6f, PlateBehindMetres = 0.01f;
        const float SnapMetres = 2f;   // the head further than this from where the guide follows it (a teleport): jump there
        // nothing writes depth, so the queue is the order: the markers first, then the plate over them (a marker that
        // crosses the card is dimmed by the plate and never covers a letter), then the letters on the plate
        const int MarkerQueue = 2990, PlateQueue = 3000, TextQueue = 3010;

        static MelonPreferences_Entry<float>? distanceEntry, dropEntry, textSizeEntry, followEntry;
        static MelonPreferences_Entry<bool>? markersEntry;
        static MelonPreferences_Entry<string>? rightEntry, leftEntry, backEntry;

        static float distance = DefaultDistance, drop = DefaultDrop, textSize = DefaultTextSize, follow = DefaultFollow;
        static bool plateOn = true;
        static Color rightColour, leftColour, backColour;

        /// <summary>[EarthLooks] GuideMarkers: off, a guide shows the card only and its words name no colours.</summary>
        internal static bool Markers { get; private set; } = true;
        /// <summary>The markers' colours as #RRGGBB, for the hand lines on the card.</summary>
        internal static string RightHex { get; private set; } = DefaultRight;
        internal static string LeftHex { get; private set; } = DefaultLeft;

        sealed class Marker {
            public GameObject Root = null!;
            public Transform Ring = null!;
            public LineRenderer RingLine = null!, Fingers = null!, Palm = null!;
            public LineRenderer[] Lines = null!;
            public Color Colour;
            public bool Near;
        }

        static GameObject? host;
        static Transform card = null!;
        static TextMesh text = null!;
        static Font font = null!;
        static LineRenderer plate = null!;
        static Marker right = null!, left = null!;

        static bool shown, placed, dirty = true, markersShown, listening;
        static int owner;            // the guide that has the display (0: none)
        static Material? textCopy;   // the lettering's own material (null: the font's own is used)
        static PoseSpec pose;
        static Vector3 at;      // the head as the guide follows it
        static float yaw;
        static Transform rightHand = null!, leftHand = null!;   // this frame's, from Frame for Place

        /// <summary>From Main.OnInitializeMelon on games: the guides' [EarthLooks] settings.</summary>
        internal static void Init() {
            MelonPreferences_Category looks = Cfg.Category("EarthLooks");
            distanceEntry = looks.CreateEntry("GuideDistanceMetres", DefaultDistance, description: "Per game. The guides (the pose capture's, [EarthPose] Capture, and the practice guide, [EarthPose] Practice): their card floats this many metres in front of you, along the way your head faces on the ground. Above 0.");
            dropEntry = looks.CreateEntry("GuideDropMetres", DefaultDrop, description: "Per game. The guide's card: its middle is this many metres under eye level. 0 or more.");
            textSizeEntry = looks.CreateEntry("GuideTextSize", DefaultTextSize, description: "Per game. The guide's card: the height of its letters in metres (the default 0.035 is meant for a card 1.2 m away). Above 0.");
            markersEntry = looks.CreateEntry("GuideMarkers", true, description: "Per game. The guide's two hand markers, where the next pose's right and left hand go: a ring at the hand's place, a long line along the fingers and a short one out of the palm; a marker turns nearly white while that hand is at its place. Off: the card only.");
            rightEntry = looks.CreateEntry("GuideRightColour", DefaultRight, description: "Per game. The right hand's marker and its line on the card (#RRGGBB). The card's words call it orange.");
            leftEntry = looks.CreateEntry("GuideLeftColour", DefaultLeft, description: "Per game. The left hand's marker and its line on the card (#RRGGBB). The card's words call it blue.");
            backEntry = looks.CreateEntry("GuideBackColour", DefaultBack, description: "Per game. The dark plate behind the card's text, so it reads against the sky (#RRGGBBAA). Empty: no plate.");
            followEntry = looks.CreateEntry("GuideFollowSeconds", DefaultFollow, description: "Per game. The guide's card and markers follow your head this slowly (the time to go about two thirds of the way), so they do not shake with it. 0: they follow at once.");
            Apply();
            Cfg.Changed += Apply;
        }

        static void Apply() {
            distance = Cfg.Positive(distanceEntry, "EarthLooks", "GuideDistanceMetres", DefaultDistance);
            drop = Cfg.NotNegative(dropEntry, "EarthLooks", "GuideDropMetres", DefaultDrop);
            textSize = Cfg.Positive(textSizeEntry, "EarthLooks", "GuideTextSize", DefaultTextSize);
            follow = Cfg.NotNegative(followEntry, "EarthLooks", "GuideFollowSeconds", DefaultFollow);
            Markers = markersEntry == null || markersEntry.Value;
            rightColour = Colour(rightEntry, "GuideRightColour", DefaultRight);
            leftColour = Colour(leftEntry, "GuideLeftColour", DefaultLeft);
            RightHex = "#" + ColorUtility.ToHtmlStringRGB(rightColour);
            LeftHex = "#" + ColorUtility.ToHtmlStringRGB(leftColour);
            plateOn = (backEntry?.Value ?? DefaultBack).Trim().Length > 0;
            backColour = plateOn ? Colour(backEntry, "GuideBackColour", DefaultBack) : Color.clear;
            dirty = true;
        }

        static Color Colour(MelonPreferences_Entry<string>? e, string key, string fallback) {
            string t = (e?.Value ?? fallback).Trim();
            if (ColorUtility.TryParseHtmlString(t, out Color c)) return c;
            MelonLogger.Warning($"[Earth] Settings: [EarthLooks] {key} '{t}' is not a colour (#RRGGBB); using {fallback}");
            ColorUtility.TryParseHtmlString(fallback, out c);
            return c;
        }

        /// <summary>A guide's frame, after the game has moved the head and hands: the display is shown for
        /// <paramref name="guide"/> (taken from the other guide if that had it) and follows the head. False, and hidden, with
        /// no local player (loading, a scene change). <paramref name="rewrite"/>: the guide must call <see cref="Write"/> and
        /// <see cref="Pose"/> again this frame (just shown, a setting changed, or the font's atlas was rebuilt, which moves
        /// every letter). May throw (the first use builds everything): the guide catches.</summary>
        internal static bool Frame(int guide, string name, out bool rewrite) {
            rewrite = false;
            if (!Hands.Raw(out Vector3 head, out float headYaw, out rightHand, out leftHand)) {
                Hide(guide);
                return false;
            }
            if (host == null) Build();
            if (!shown || owner != guide) {
                host!.SetActive(true);
                shown = true;
                owner = guide;
                placed = false;
                dirty = true;
                MelonLogger.Msg($"[Earth] Guide: the {name} guide is shown (a card in front of the player; the next pose's hand markers)");
            }
            if (!placed || (head - at).sqrMagnitude > SnapMetres * SnapMetres) {
                at = head;
                yaw = headYaw;
                placed = true;
            }
            else {
                float k = PoseGuide.Ease(Time.unscaledDeltaTime, follow);
                at = Vector3.Lerp(at, head, k);
                yaw = Mathf.LerpAngle(yaw, headYaw, k);
            }
            rewrite = dirty;
            return true;
        }

        /// <summary>The card's text and the plate behind it. Only when the words changed or <see cref="Frame"/> said so.</summary>
        internal static void Write(string words) {
            dirty = false;
            float size = textSize / (FontSize * 0.1f);   // a TextMesh letter is fontSize x characterSize x 0.1 metres high
            font.RequestCharactersInTexture(words, FontSize);
            text.characterSize = size;
            text.text = "";   // the same string again would keep its old mesh
            text.text = words;
            if (textCopy != null) textCopy.mainTexture = font.material.mainTexture;   // the atlas, should a rebuild have made a new one

            plate.enabled = plateOn;
            if (!plateOn) return;
            Measure(words, out float widest, out int lines);
            float margin = PlateMarginEm * textSize;
            float w = widest * size * 0.1f + 2f * margin;
            plate.widthMultiplier = lines * LineHeightEm * textSize + 2f * margin;
            plate.SetPosition(0, new Vector3(-w * 0.5f, 0f, PlateBehindMetres));
            plate.SetPosition(1, new Vector3(w * 0.5f, 0f, PlateBehindMetres));
            plate.startColor = backColour;
            plate.endColor = backColour;
        }

        /// <summary>The pose the markers show (A on the right hand), in the settings' colours; <paramref name="on"/> false:
        /// no markers. With [EarthLooks] GuideMarkers off there are none either way. Only when the pose changed or
        /// <see cref="Frame"/> said so.</summary>
        internal static void Pose(bool on, in PoseSpec spec) {
            markersShown = on && Markers;
            right.Root.SetActive(markersShown);
            left.Root.SetActive(markersShown);
            if (!markersShown) return;
            pose = spec;
            Dress(right, in pose.A, rightColour);
            Dress(left, in pose.B, leftColour);
        }

        /// <summary>Every frame after <see cref="Frame"/> said true: the card and the markers at their places for the head
        /// as the guide follows it. The pose's hand places are <paramref name="bodyScale"/> times as far from the head (the
        /// capture: 1, as the recorder writes them; the practice guide: [EarthPose] BodyScale, as the machine judges them).</summary>
        internal static void Place(float bodyScale) {
            V3 origin = new V3(at.x, at.y, at.z);
            card.SetPositionAndRotation(W(BodyFrame.ToWorld(origin, yaw, new V3(0f, -drop, distance))), Quaternion.Euler(0f, yaw, 0f));
            if (!markersShown) return;
            float scale = bodyScale > 0f && !float.IsInfinity(bodyScale) ? bodyScale : 1f;
            Place(right, in pose.A, origin, rightHand.position, scale);
            Place(left, in pose.B, origin, leftHand.position, scale);
        }

        /// <summary>Hides the display if <paramref name="guide"/> has it; nothing otherwise (the other guide took it). Never
        /// builds anything.</summary>
        internal static void Hide(int guide) {
            if (owner != guide) return;
            if (host != null) host.SetActive(false);
            shown = false;
            placed = false;
            owner = 0;
        }

        /// <summary>The widest line of the text in the font's pixels at <see cref="FontSize"/>, and its number of lines; the
        /// colour tags are not letters.</summary>
        static void Measure(string words, out float widest, out int lines) {
            widest = 0f;
            lines = 1;
            float line = 0f;
            bool tag = false;
            foreach (char c in words) {
                if (c == '<') { tag = true; continue; }
                if (tag) { tag = c != '>'; continue; }
                if (c == '\n') { lines++; line = 0f; continue; }
                line += font.GetCharacterInfo(c, out UnityEngine.CharacterInfo info, FontSize) ? info.advance : FontSize * 0.5f;
                if (line > widest) widest = line;
            }
        }

        static void Dress(Marker m, in HandSpec h, Color colour) {
            m.Colour = colour;
            m.Fingers.enabled = PoseMatch.Checked(h.Fingers);
            m.Palm.enabled = PoseMatch.Checked(h.Palm);
            Tint(m, false);
        }

        static void Tint(Marker m, bool near) {
            m.Near = near;
            Color c = near ? Color.Lerp(m.Colour, Color.white, NearWhite) : m.Colour;
            float w = near ? NearWidth : LineWidth;
            foreach (LineRenderer l in m.Lines) {
                l.startColor = c;
                l.endColor = c;
                l.widthMultiplier = w;
            }
        }

        /// <summary>One marker for this frame: the ring at the hand's place in the palm's plane, the fingers' line and the
        /// palm's, and whether the hand itself is inside the pose's zone round it (PoseMatch.Zone, in the guide's own body
        /// frame at the body scale).</summary>
        static void Place(Marker m, in HandSpec h, V3 origin, Vector3 hand, float scale) {
            Vector3 spot = W(BodyFrame.ToWorld(origin, yaw, h.Pos * scale));
            Vector3 palm = W(BodyFrame.DirToWorld(yaw, h.Palm.Normalized)), fingers = W(BodyFrame.DirToWorld(yaw, h.Fingers.Normalized));
            // a pose that checks no palm: the ring faces back at the player
            Vector3 normal = palm.sqrMagnitude > 0.5f ? palm : Quaternion.Euler(0f, yaw, 0f) * Vector3.back;
            Vector3 up = fingers.sqrMagnitude > 0.5f && Mathf.Abs(Vector3.Dot(normal, fingers)) < 0.99f ? fingers
                : Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.forward;
            m.Ring.SetPositionAndRotation(spot, Quaternion.LookRotation(normal, up));
            m.Fingers.SetPosition(0, spot);
            m.Fingers.SetPosition(1, spot + fingers * FingersMetres);
            m.Palm.SetPosition(0, spot);
            m.Palm.SetPosition(1, spot + palm * PalmMetres);
            V3 inBody = BodyFrame.ToBody(origin, yaw, new V3(hand.x, hand.y, hand.z), V3.Zero, V3.Zero, scale).Pos;
            bool near = PoseMatch.Zone(in h, inBody - h.Pos) <= 1f;
            if (near != m.Near) Tint(m, near);
        }

        // ---------------------------------------------------------------- built once

        static void Build() {
            Shader lines = Shader.Find("Sprites/Default");
            if (lines == null) throw new InvalidOperationException("the shader Sprites/Default is not in this game");
            var root = new GameObject("EarthGuide");
            try {
                UnityEngine.Object.DontDestroyOnLoad(root);
                root.SetActive(false);
                Build(root, lines);
                host = root;
            }
            catch {
                UnityEngine.Object.Destroy(root);   // nothing half-built stays; the next use tries again
                throw;
            }
        }

        static void Build(GameObject root, Shader lines) {

            var cardObject = new GameObject("Card");
            card = cardObject.transform;
            card.SetParent(root.transform, false);
            text = cardObject.AddComponent<TextMesh>();
            font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.font = font;
            text.fontSize = FontSize;
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.richText = true;
            text.color = Color.white;
            cardObject.GetComponent<MeshRenderer>().sharedMaterial = TextMaterial(font);
            // another text of this font can make Unity rebuild its atlas, which moves every letter: write ours again then
            if (!listening) {
                listening = true;
                Font.textureRebuilt += rebuilt => { if (rebuilt == font) dirty = true; };
            }

            var plateObject = new GameObject("Plate");
            plateObject.transform.SetParent(card, false);
            plate = Line(plateObject, new Material(lines) { name = "Earth Guide Plate", renderQueue = PlateQueue }, 2, false);
            plate.alignment = LineAlignment.TransformZ;   // flat on the card, its width the card's height
            plate.numCapVertices = 0;

            var marker = new Material(lines) { name = "Earth Guide Marker", renderQueue = MarkerQueue };
            right = NewMarker(root.transform, "Right hand", marker);
            left = NewMarker(root.transform, "Left hand", marker);
        }

        /// <summary>The lettering's material: depth-tested, so a hand or a marker in front of the card hides it. Unity's
        /// UI/Default draws the font's atlas as the game's own UI text does: (texture + _TextureSampleAdd) x the letter's
        /// colour, and with _TextureSampleAdd (1,1,1,0) an alpha-only atlas reads white in the letter's shape; its depth test
        /// is unity_GUIZTestMode (the archery mod's Tablet.cs found both). Without that shader: the font's own material,
        /// which draws over everything.</summary>
        static Material TextMaterial(Font f) {
            Material own = f.material;
            Shader ui = Shader.Find("UI/Default");
            if (ui == null) {
                MelonLogger.Msg("[Earth] Guide: no shader UI/Default; the card's text uses the font's own material and shows through everything");
                return own;
            }
            Texture atlas = own.mainTexture;
            bool alphaOnly = !(atlas is Texture2D t) || t.format == TextureFormat.Alpha8;
            var copy = new Material(ui) { name = "Earth Guide Text", mainTexture = atlas, renderQueue = TextQueue };
            textCopy = copy;
            if (copy.HasProperty("_Color")) copy.SetColor("_Color", Color.white);
            copy.SetVector("_TextureSampleAdd", alphaOnly ? new Vector4(1f, 1f, 1f, 0f) : Vector4.zero);
            copy.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
            return copy;
        }

        static Marker NewMarker(Transform parent, string name, Material material) {
            var m = new Marker { Root = new GameObject(name) };
            m.Root.transform.SetParent(parent, false);
            var ring = new GameObject("Ring");
            ring.transform.SetParent(m.Root.transform, false);
            m.Ring = ring.transform;
            m.RingLine = Line(ring, material, RingPoints, false);
            m.RingLine.loop = true;
            for (int i = 0; i < RingPoints; i++) {
                float a = i * Mathf.PI * 2f / RingPoints;
                m.RingLine.SetPosition(i, new Vector3(Mathf.Cos(a) * RingRadius, Mathf.Sin(a) * RingRadius, 0f));   // its own XY plane: z is the palm
            }
            var fingers = new GameObject("Fingers");
            fingers.transform.SetParent(m.Root.transform, false);
            m.Fingers = Line(fingers, material, 2, true);
            var palm = new GameObject("Palm");
            palm.transform.SetParent(m.Root.transform, false);
            m.Palm = Line(palm, material, 2, true);
            m.Lines = new[] { m.RingLine, m.Fingers, m.Palm };
            return m;
        }

        static LineRenderer Line(GameObject go, Material material, int points, bool world) {
            var l = go.AddComponent<LineRenderer>();
            l.sharedMaterial = material;
            l.useWorldSpace = world;
            l.positionCount = points;
            l.widthMultiplier = LineWidth;
            l.numCapVertices = 2;
            l.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            l.receiveShadows = false;
            return l;
        }

        static Vector3 W(V3 v) => new Vector3(v.X, v.Y, v.Z);
    }
}
