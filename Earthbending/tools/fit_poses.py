"""Blue's Earthbending, Task 3: the owner's pose capture -> the mod's poses.txt and the four hand-axis settings.

Stdlib only. Run it with python -I:

  python -I Earthbending/tools/fit_poses.py <pose-capture-....csv> [--out <UserData folder>]
  python -I Earthbending/tools/fit_poses.py --selftest [--out <folder>]

See --help for the details.
"""

import argparse
import contextlib
import csv
import io
import math
import os
import random
import re
import shutil
import sys
import tempfile
from datetime import datetime

# ----- the reference: spec 3.3's table -----------------------------------------------------------------------------
#
# The design's starting poses (docs/superpowers/specs/2026-11-22-earthbending-design.md, section 3.3), in the order the
# owner strikes them. Body frame: metres from the head, +Z ahead (head yaw only), +Y up, +X right. A is the right hand.
# The fit only uses the palm and finger directions here, to find which direction in each controller's own space is the
# palm and which the fingers (Ruling 36: any direction, not only the six signed axes); poses.txt gets the owner's own
# measured places and directions. This is NOT Poses.cs's table, which
# holds the owner's capture once Task 3 is done (the selftest checks it against the design doc instead).

PX, NX, PY, NY, PZ, NZ = (1.0, 0.0, 0.0), (-1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, -1.0, 0.0), (0.0, 0.0, 1.0), (0.0, 0.0, -1.0)
STOMP_PALM, STOMP_FINGERS = (0.0, -0.71, -0.71), (0.0, -0.71, 0.71)

SPEC = [
    ("Base",     ((0.24, -0.57, 0.22), PY, PZ),                   ((-0.24, -0.57, 0.22), PY, PZ)),
    ("Disc",     ((0.05, -0.30, 0.45), NX, PZ),                   ((-0.05, -0.30, 0.28), NZ, PX)),
    ("Ball",     ((0.15, -0.30, 0.22), NX, PZ),                   ((-0.10, -0.30, 0.45), PX, PZ)),
    ("Pillar",   ((0.20, -0.55, 0.30), NX, PZ),                   ((-0.20, -0.05, 0.25), PX, PY)),
    ("Cube",     ((0.20, -0.05, 0.25), NX, PY),                   ((-0.20, -0.05, 0.25), PX, PY)),
    ("Wall",     ((0.20, 0.00, 0.30), PY, PZ),                    ((-0.20, 0.00, 0.30), PY, PZ)),
    ("Straight", ((0.20, -0.25, 0.25), NX, PY),                   ((-0.15, -0.25, 0.45), NY, PZ)),
    ("Kick",     ((0.20, -0.25, 0.25), NX, PY),                   ((-0.05, -0.65, 0.22), NZ, NY)),
    ("Stomp",    ((0.20, -0.55, 0.35), STOMP_PALM, STOMP_FINGERS), ((-0.20, -0.55, 0.35), STOMP_PALM, STOMP_FINGERS)),
    ("Uppercut", ((0.20, -0.30, 0.25), NX, (0.0, 0.97, 0.26)),    ((-0.05, -0.10, 0.32), NZ, PY)),
    ("Parry",    ((0.20, -0.25, 0.42), PZ, PY),                   ((-0.20, -0.25, 0.42), PZ, PY)),
    ("Hold",     ((0.05, -0.25, 0.42), PZ, PY),                   ((0.05, -0.40, 0.38), PY, PZ)),
    ("Flick",    ((0.10, -0.25, 0.42), PX, PZ),                   ((0.10, -0.48, 0.38), PX, PZ)),
    ("Explode",  ((0.08, -0.38, 0.18), NZ, NX),                   ((-0.08, -0.38, 0.18), NZ, PX)),
    ("Jump",     ((0.20, -0.72, 0.20), PY, PZ),                   ((-0.20, 0.05, 0.10), PX, PY)),
    ("Dash",     ((0.20, -0.72, 0.20), PY, PZ),                   ((-0.10, -0.30, 0.45), PX, PZ)),
]
NAMES = [s[0] for s in SPEC]

# the default tolerances of spec 3.3 (PoseLibrary.DefaultTolX/Y/Z, DefaultPalmDeg, DefaultFingerDeg): each hand's place is a
# zone, an ellipsoid with these half-sizes sideways, up/down and in reach (metres), and the directions are cones (degrees)
ZONE, PALM_DEG, FINGER_DEG = (0.30, 0.24, 0.33), 65.0, 65.0   # Ruling 45 (were 0.25 0.20 0.30, 60, 60)

AXES = ("+x", "-x", "+y", "-y", "+z", "-z")
HANDS = ("right", "left")

# the recorder's columns (Hands.cs, PoseCapture.Header in Poses.cs): n, t, the head in the world and its yaw, then for each
# hand its place in the body frame and the controller's local X, Y and Z axes in the body frame
CAPTURE_COLUMNS = ["n", "t", "headX", "headY", "headZ", "yaw"] + [
    h + c for h in HANDS for c in (["X", "Y", "Z"] + [a + "axis" + k for a in "XYZ" for k in "XYZ"])]


class FitError(Exception):
    """A capture the fit refuses (the message says why)."""


# ----- vectors ---------------------------------------------------------------------------------------------------------

def add(a, b): return (a[0] + b[0], a[1] + b[1], a[2] + b[2])
def sub(a, b): return (a[0] - b[0], a[1] - b[1], a[2] - b[2])
def scale(a, s): return (a[0] * s, a[1] * s, a[2] * s)
def dot(a, b): return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
def length(a): return math.sqrt(dot(a, a))
def cross(a, b): return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def normalized(a):
    n = length(a)
    return scale(a, 1.0 / n) if n > 1e-9 else (0.0, 0.0, 0.0)


def angle_deg(a, b):
    """Degrees between two directions; 180 when either has none."""
    na, nb = normalized(a), normalized(b)
    if dot(na, na) == 0.0 or dot(nb, nb) == 0.0:
        return 180.0
    return math.degrees(math.acos(max(-1.0, min(1.0, dot(na, nb)))))


def rotated(v, axis, deg):
    """Rodrigues: v turned by deg about the unit axis."""
    k, r = normalized(axis), math.radians(deg)
    c, s = math.cos(r), math.sin(r)
    return add(add(scale(v, c), scale(cross(k, v), s)), scale(k, dot(k, v) * (1.0 - c)))


def checked(v):
    """A direction of a pose is checked unless it is zero (PoseMatch.Checked)."""
    return length(v) > 1e-6


def zone(offset):
    """How deep an offset from a pose's place is in its zone (PoseMatch.Zone): the ellipsoid distance, 1 on the edge."""
    return math.sqrt(sum((d / t) ** 2 for d, t in zip(offset, ZONE)))


def mirror_hand(h):
    """A hand of a pose (place, palm, fingers) on the other side: x flipped in all three (HandSpec.Mirrored)."""
    return tuple((-v[0], v[1], v[2]) for v in h)


# ----- the fit ---------------------------------------------------------------------------------------------------------

def read_capture(path):
    """The capture's snapshots in file order: [{"n", "line", "right": {"pos", "x", "y", "z"}, "left": {...}}], every
    axis made length 1. Columns are found by name; a missing column, a short row or a value that is not a number is refused."""
    with open(path, newline="", encoding="utf-8-sig") as f:
        reader = csv.reader(f)
        header = [h.strip() for h in next(reader, [])]
        missing = [c for c in CAPTURE_COLUMNS if c not in header]
        if missing:
            raise FitError("%s has no column%s %s: it is not a capture the game's recorder wrote"
                           % (os.path.basename(path), "s" if len(missing) > 1 else "", ", ".join(missing)))
        at = {c: header.index(c) for c in CAPTURE_COLUMNS}
        snaps = []
        for line, row in enumerate(reader, start=2):
            if not any(c.strip() for c in row):
                continue
            if len(row) < len(header):
                raise FitError("line %d has %d values, the header %d" % (line, len(row), len(header)))

            def num(column):
                text = row[at[column]].strip()
                try:
                    v = float(text)
                except ValueError:
                    raise FitError("line %d, column %s: '%s' is not a number" % (line, column, text))
                if not math.isfinite(v):
                    raise FitError("line %d, column %s: '%s' is not a number" % (line, column, text))
                return v

            snap = {"n": row[at["n"]].strip(), "line": line}
            for h in HANDS:
                hand = {"pos": tuple(num(h + c) for c in "XYZ")}
                for a in "XYZ":
                    v = tuple(num(h + a + "axis" + k) for k in "XYZ")
                    if abs(length(v) - 1.0) > 0.05:
                        raise FitError("line %d: the %s controller's local %s axis has length %.3f, want 1" % (line, h, a, length(v)))
                    hand[a.lower()] = normalized(v)
                snap[h] = hand
            snaps.append(snap)
    return snaps


def axis_vector(axis):
    """A signed local axis ("-y") as a direction in the controller's own space; a direction (three numbers) stays itself."""
    if not isinstance(axis, str):
        return axis
    v = {"x": PX, "y": PY, "z": PZ}[axis[1]]
    return scale(v, -1.0) if axis[0] == "-" else v


def to_body(hand, local):
    """A direction in one snapshot's controller's own space, in the body frame (what Transform.TransformDirection gives
    the game, then the body frame): the controller's local X, Y and Z axes weighed by its three numbers."""
    return add(add(scale(hand["x"], local[0]), scale(hand["y"], local[1])), scale(hand["z"], local[2]))


def to_local(hand, d):
    """A body-frame direction in one snapshot's controller's own space: its part along each local axis."""
    return (dot(d, hand["x"]), dot(d, hand["y"]), dot(d, hand["z"]))


def axis_dir(hand, axis):
    """The direction a signed local axis ("-y"), or a direction in the controller's space, points in the body frame."""
    return to_body(hand, axis_vector(axis))


def agreement(snaps, hand, part, local):
    """How well one direction in a hand's controller space agrees with the spec's palm (part 1) or fingers (part 2) over
    the poses: the mean angle, the worst and its pose."""
    side = 1 if hand == "right" else 2
    angles = {}
    for snap, spec in zip(snaps, SPEC):
        ref = spec[side][part]
        if checked(ref):
            angles[spec[0]] = angle_deg(to_body(snap[hand], local), ref)
    worst = max(angles, key=angles.get)
    return {"mean": sum(angles.values()) / len(angles), "worst": angles[worst], "worst_pose": worst}


def mean_direction(snaps, hand, part):
    """Ruling 36: one hand's palm (part 1) or fingers (part 2) as a direction in its controller's own space: each pose's
    design direction taken into that row's controller space, averaged over the poses and made length 1 (zero when the
    rows cancel out)."""
    side = 1 if hand == "right" else 2
    total = (0.0, 0.0, 0.0)
    for snap, spec in zip(snaps, SPEC):
        ref = spec[side][part]
        if checked(ref):
            total = add(total, to_local(snap[hand], normalized(ref)))
    return normalized(total)


# the game's rule for a hand's two directions (HandAxes.Check in Poses.cs): within this many degrees of each other, or of
# opposite, is no hand
MIN_APART_DEG = 45.0


def square_fingers(hand, palm, fingers):
    """The fingers made square to the palm (the palm's part taken out, then length 1). Refused, as the game refuses the
    settings, when either has no direction or the two are within 45 degrees of each other or of opposite."""
    if not checked(palm) or not checked(fingers):
        raise FitError("the %s hand's %s came out with no direction: the snapshots do not look like the 16 poses in order with"
                       " A on the right hand" % (hand, "palm" if not checked(palm) else "fingers"))
    apart = angle_deg(palm, fingers)
    if not (MIN_APART_DEG < apart < 180.0 - MIN_APART_DEG):
        raise FitError("the %s hand's palm and fingers came out %.0f degrees apart (they are square to each other, so 45 to 135"
                       " is wanted): the snapshots do not look like the 16 poses in order with A on the right hand" % (hand, apart))
    return normalized(sub(fingers, scale(palm, dot(fingers, palm))))


def nearest_axis(local):
    """The signed local axis closest to a direction in the controller's space (ties to the earlier of AXES)."""
    return min(AXES, key=lambda a: angle_deg(axis_vector(a), local))


def setting_text(local):
    """A direction as the game's setting takes it: three numbers with 2 decimals (no -0.00)."""
    return ",".join("0.00" if c == "-0.00" else c for c in ("%.2f" % v for v in local))


def fit(snaps):
    """The palm and finger directions of both hands' controllers and the 16 measured poses. Refuses a capture of any count
    but 16, and one whose palm and fingers come out with no direction or within 45 degrees of each other (or of opposite).
    Each part's "dir" is the fit, "setting" the text the game is given (2 decimals) and "game" what the game reads from
    it; the agreement ("mean", "worst"), the nearest signed axis ("axis", with its own "axis_mean") and poses.txt use the
    game's."""
    if len(snaps) != len(SPEC):
        raise FitError("the capture has %d snapshot%s; the fit wants exactly %d, one per pose in this order: %s"
                       % (len(snaps), "" if len(snaps) == 1 else "s", len(SPEC), ", ".join(NAMES)))
    result = {}
    for h in HANDS:
        palm = mean_direction(snaps, h, 1)
        fingers = square_fingers(h, palm, mean_direction(snaps, h, 2))
        result[h] = {}
        for k, (part, d) in enumerate((("palm", palm), ("fingers", fingers))):
            text = setting_text(d)
            game = normalized(tuple(float(c) for c in text.split(",")))
            axis = nearest_axis(game)
            r = {"dir": d, "setting": text, "game": game, "axis": axis, "axis_mean": agreement(snaps, h, k + 1, axis_vector(axis))["mean"]}
            r.update(agreement(snaps, h, k + 1, game))
            result[h][part] = r
    result["poses"] = [
        dict({"name": name, "n": snap["n"]}, **{h: {"pos": snap[h]["pos"],
                                                     "palm": normalized(to_body(snap[h], result[h]["palm"]["game"])),
                                                     "fingers": normalized(to_body(snap[h], result[h]["fingers"]["game"]))} for h in HANDS})
        for snap, (name, _, _) in zip(snaps, SPEC)]
    return result


def n3(x):
    s = "%.3f" % x
    return "0.000" if s == "-0.000" else s


def format_poses(result, source):
    """poses.txt's lines, in PoseLibrary.Parse's format: <Name> | A | B | tx ty tz palmDeg fingerDeg, A the right hand."""
    def hand(h):
        return "  ".join(" ".join(n3(c) for c in v) for v in (h["pos"], h["palm"], h["fingers"]))

    axes = "; ".join("%s hand palm %s, fingers %s" % (h, result[h]["palm"]["setting"], result[h]["fingers"]["setting"]) for h in HANDS)
    lines = [
        "# Blue's Earthbending poses: the owner's own, fitted by fit_poses.py from %s on %s (%s)."
        % (source, datetime.now().strftime("%Y-%m-%d %H:%M"), axes),
        "# <Name> | A: x y z  palm x y z  fingers x y z | B: the same | tx ty tz palmDeg fingerDeg",
        "# Body frame: metres from the head as captured (BodyScale 1), +Z ahead (head yaw only), +Y up, +X right; A is the right hand; 0 0 0 = not checked; tx ty tz = the zone's half-sizes sideways, up/down, reach.",
    ]
    tol = "%g %g %g %g %g" % (ZONE + (PALM_DEG, FINGER_DEG))
    for p in result["poses"]:
        lines.append("%s | %s | %s | %s" % (p["name"], hand(p["right"]), hand(p["left"]), tol))
    return lines


def write_poses(result, folder, source):
    """Writes folder\\poses.txt (an older one is kept as poses.txt.bak) and returns its path."""
    os.makedirs(folder, exist_ok=True)
    path = os.path.join(folder, "poses.txt")
    if os.path.exists(path):
        shutil.copy2(path, path + ".bak")
    with open(path, "w", newline="\n", encoding="utf-8") as f:
        f.write("\n".join(format_poses(result, source)) + "\n")
    return path


def report(result):
    """The fitted directions with their agreement, and each pose's distance from the design's, for the person running the
    fit."""
    print("Directions in each controller's own space (the mean and worst angle to spec 3.3's directions over the 16 poses;"
          " the nearest signed axis for reference, with its mean):")
    for h in HANDS:
        for part in ("palm", "fingers"):
            r = result[h][part]
            print("  %-5s %-7s (%s)  mean %5.1f deg, worst %5.1f (%s); nearest signed axis %s, mean %5.1f"
                  % (h, part, ", ".join("%5.2f" % c for c in r["game"]), r["mean"], r["worst"], r["worst_pose"], r["axis"], r["axis_mean"]))
    print("Per pose, how far the owner's is from the design's with those directions (palm deg, fingers deg, place m; ! = outside the %g / %g degree"
          " cones or the place's zone, %g sideways, %g up/down, %g reach):" % ((PALM_DEG, FINGER_DEG) + ZONE))
    for k, (name, a, b) in enumerate(SPEC):
        cells = []
        for h, spec_hand in (("right", a), ("left", b)):
            p = result["poses"][k][h]
            off = sub(p["pos"], spec_hand[0])
            d = (angle_deg(p["palm"], spec_hand[1]), angle_deg(p["fingers"], spec_hand[2]), length(off))
            marks = ["!" if out else " " for out in (d[0] > PALM_DEG, d[1] > FINGER_DEG, zone(off) > 1.0)]
            cells.append("%-5s %5.1f%s %5.1f%s %5.2f%s" % (h, d[0], marks[0], d[1], marks[1], d[2], marks[2]))
        print("  %2s %-9s %s   %s" % (result["poses"][k]["n"], name, cells[0], cells[1]))
    worst = max(result[h][part]["mean"] for h in HANDS for part in ("palm", "fingers"))
    if worst > 25.0:
        print("WARNING: a fitted direction agrees with the design only to %.1f degrees on average: check that the 16 snapshots are the"
              " poses in order, with A on the right hand." % worst)
    return worst


# ----- the guess: what each row looks like, when the rows are not the 16 poses in order ----------------------------

# every way a controller can carry a palm and fingers on two different local axes
PAIRS = [(p, f) for p in AXES for f in AXES if p[1] != f[1]]


def hand_score(spec_hand, pos, palm, fingers):
    """One hand against one hand of a pose, as the game scores it (PoseMatch.Score): the largest of the place's zone
    distance and each checked direction's angle over its cone; 1 is the edge of the pose, lower is closer."""
    ref_pos, ref_palm, ref_fingers = spec_hand
    r = zone(sub(pos, ref_pos))
    if checked(ref_palm):
        r = max(r, angle_deg(palm, ref_palm) / PALM_DEG)
    if checked(ref_fingers):
        r = max(r, angle_deg(fingers, ref_fingers) / FINGER_DEG)
    return r


def pose_score(spec, right, left):
    """Both hands against a pose, as written (A on the right) or mirrored (A on the left), whichever is closer:
    (score, mirrored) (PoseMatch.Match)."""
    _, a, b = spec
    written = max(hand_score(a, *right), hand_score(b, *left))
    mirrored = max(hand_score(mirror_hand(a), *left), hand_score(mirror_hand(b), *right))
    return (written, False) if written <= mirrored else (mirrored, True)


def hand_sample(snap, hand, pair):
    h = snap[hand]
    return h["pos"], axis_dir(h, pair[0]), axis_dir(h, pair[1])


def guess_axes(snaps, hand):
    """The palm and finger axes of one hand's controller, found without the order: the pair under which the rows come
    closest to some hand of some pose (its own side's as written, the other side's mirrored), each row's score capped at
    2 so that rows which are no pose at all do not decide it."""
    own = 1 if hand == "right" else 2
    hands = [spec[own] for spec in SPEC] + [mirror_hand(spec[3 - own]) for spec in SPEC]
    best = None
    for pair in PAIRS:
        total = sum(min(2.0, min(hand_score(c, *hand_sample(snap, hand, pair)) for c in hands)) for snap in snaps)
        if best is None or total < best[0]:
            best = (total, pair)
    return best[1]


def guess(snaps):
    """For a capture that is not the 16 poses in order: the axes found from the rows themselves, each row's nearest pose of
    spec 3.3 (mirroring allowed) and its score, the rows that look like no pose (score over 1), the poses no row looks
    like, and the poses two or more rows look like."""
    axes = {h: guess_axes(snaps, h) for h in HANDS}
    rows = []
    for i, snap in enumerate(snaps):
        right, left = hand_sample(snap, "right", axes["right"]), hand_sample(snap, "left", axes["left"])
        score, mirrored, name = min(pose_score(spec, right, left) + (spec[0],) for spec in SPEC)
        rows.append({"row": i + 1, "n": snap["n"], "pose": name, "mirrored": mirrored, "score": score})
    seen = {}
    for r in rows:
        if r["score"] <= 1.0:
            seen.setdefault(r["pose"], []).append(r["row"])
    return {"axes": axes, "rows": rows,
            "junk": [r["row"] for r in rows if r["score"] > 1.0],
            "missing": [name for name in NAMES if name not in seen],
            "twice": {name: rs for name, rs in seen.items() if len(rs) > 1}}


def print_guess(g):
    print("What each row looks like (axes found from the rows: %s; the game's score, 1 is the edge of a pose; mirrored ="
          " A on the left hand):" % "; ".join("%s palm %s, fingers %s" % (h, g["axes"][h][0], g["axes"][h][1]) for h in HANDS))
    for r in g["rows"]:
        name = r["pose"] + (" mirrored" if r["mirrored"] else "")
        if r["score"] <= 1.0:
            print("  row %2d (n %2s): %-17s %.2f" % (r["row"], r["n"], name, r["score"]))
        else:
            print("  row %2d (n %2s): matches no pose well (nearest %s, %.2f)" % (r["row"], r["n"], name, r["score"]))
    print("Rows that look like no pose (a rest, a slip, or a pose struck far from the design's: check them against the order):"
          " %s" % (", ".join(str(x) for x in g["junk"]) or "none"))
    print("Poses no row looks like (strike them again, in their place in the order): %s" % (", ".join(g["missing"]) or "none"))
    print("Poses two or more rows look like (keep the better one): %s"
          % ("; ".join("%s (rows %s)" % (k, ", ".join(str(x) for x in v)) for k, v in g["twice"].items()) or "none"))
    print("The fit wants the 16 poses in the table's order, one row each: delete or reorder rows (keep the header line) and run it again.")


# ----- the selftest ----------------------------------------------------------------------------------------------------

def controller_axes(palm, fingers, palm_axis, finger_axis):
    """The local X, Y and Z axes (as {'x':..,'y':..,'z':..}) of a controller whose palm is along palm_axis and fingers along
    finger_axis in its own space (a signed axis such as "-y", or three numbers), turned so that its palm points along palm
    and its fingers along fingers (each pair made square). The turn maps the local frame (palm, fingers, palm x fingers)
    onto the body one, so the axes are a turn as a Unity transform's are: right = up x forward."""
    pl = normalized(axis_vector(palm_axis))
    fl = normalized(sub(axis_vector(finger_axis), scale(pl, dot(axis_vector(finger_axis), pl))))
    tl = cross(pl, fl)
    n = normalized(palm)
    f = normalized(sub(fingers, scale(n, dot(fingers, n))))
    t = cross(n, f)
    return {c: add(add(scale(n, pl[j]), scale(f, fl[j])), scale(t, tl[j])) for j, c in enumerate("xyz")}


def random_unit(rng):
    while True:
        v = (rng.uniform(-1, 1), rng.uniform(-1, 1), rng.uniform(-1, 1))
        if 1e-3 < length(v) <= 1.0:
            return normalized(v)


# hands hanging at the sides, palms in, fingers down (the C# tests' IdleR / IdleL): what a rest looks like
REST = ("rest", ((0.25, -0.95, 0.05), NX, NY), ((-0.25, -0.95, 0.05), PX, NY))


def synthetic_capture(seed, axes, pos_noise=0.03, turn_noise_deg=8.0, count=16, poses=None):
    """Rows of a capture of spec 3.3's poses (A on the right hand), as the recorder writes them: each hand's place moved
    up to pos_noise metres and the whole controller turned up to turn_noise_deg degrees about a random axis. axes is
    {"right": (palm axis, finger axis), "left": (...)}, each a signed axis or a direction in the controller's space. count
    other than 16 repeats or drops poses at the end; poses (a list of SPEC-like entries) replaces the table."""
    rng = random.Random(seed)
    head, yaw = (rng.uniform(-500, 500), rng.uniform(150, 250), rng.uniform(-500, 500)), rng.uniform(-180, 180)
    poses = poses or [SPEC[i % len(SPEC)] for i in range(count)]
    rows, t = [], 0.0
    for i, (_, a, b) in enumerate(poses):
        t += rng.uniform(2.0, 6.0)
        row = {"n": i + 1, "t": round(t, 3), "headX": head[0], "headY": head[1], "headZ": head[2], "yaw": yaw}
        for hand, (pos, palm, fingers) in (("right", a), ("left", b)):
            cols = controller_axes(palm, fingers, *axes[hand])
            k, deg = random_unit(rng), rng.uniform(0.0, turn_noise_deg)
            cols = {c: rotated(v, k, deg) for c, v in cols.items()}
            p = add(pos, scale(random_unit(rng), rng.uniform(0.0, pos_noise)))
            row[hand + "X"], row[hand + "Y"], row[hand + "Z"] = p
            for c in "xyz":
                for j, comp in enumerate("XYZ"):
                    row[hand + c.upper() + "axis" + comp] = cols[c][j]
        rows.append(row)
    return rows


def write_capture(rows, path, columns=None):
    columns = columns or CAPTURE_COLUMNS
    with open(path, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(columns)
        for r in rows:
            w.writerow([("%.5f" % r[c]) if isinstance(r[c], float) else r[c] for c in columns])
    return path


def parse_poses(lines):
    """poses.txt read as PoseLibrary.Parse reads it (a check of what this script writes): {name: (A 9 numbers, B 9, tol 5)}."""
    poses = {}
    for line in lines:
        text = line.split("#", 1)[0].strip()
        if not text:
            continue
        parts = text.split("|")
        if len(parts) != 4:
            raise ValueError("%d parts in: %s" % (len(parts), line))
        nums = [[float(w) for w in p.split()] for p in parts[1:]]
        if [len(n) for n in nums] != [9, 9, 5]:
            raise ValueError("wrong counts in: " + line)
        poses[parts[0].strip()] = tuple(nums)
    return poses


def read_spec_doc(path):
    """Spec 3.3's table as written in the design doc: {name: ((A p, n, f), (B p, n, f))}."""
    def vec(cell):
        cell = cell.strip()
        signed = {"+X": PX, "-X": NX, "+Y": PY, "-Y": NY, "+Z": PZ, "-Z": NZ}
        if cell in signed:
            return signed[cell]
        m = re.fullmatch(r"\(\s*([-0-9.]+)\s*,\s*([-0-9.]+)\s*,\s*([-0-9.]+)\s*\)", cell)
        if not m:
            raise ValueError("cannot read '%s'" % cell)
        return tuple(float(g) for g in m.groups())

    table, inside = {}, False
    with open(path, encoding="utf-8") as f:
        for line in f:
            if line.startswith("### 3.3"):
                inside = True
                continue
            if inside and line.startswith("### "):
                break
            if not inside or not line.startswith("|"):
                continue
            cells = [c for c in line.strip().strip("|").split("|")]
            if len(cells) != 7 or cells[0].strip() not in NAMES:
                continue
            v = [vec(c) for c in cells[1:]]
            table[cells[0].strip()] = ((v[0], v[1], v[2]), (v[3], v[4], v[5]))
    return table


def selftest(out_dir):
    failures = []

    def check(name, ok):
        if not ok:
            failures.append(name)
            print("FAIL " + name)

    def near_v(a, b, eps):
        return length(sub(a, b)) <= eps

    # the reference table is spec 3.3's as the design doc has it now
    doc = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "docs", "superpowers", "specs", "2026-11-22-earthbending-design.md")
    if os.path.exists(doc):
        table = read_spec_doc(doc)
        same = len(table) == 16 and all(
            name in table and all(near_v(x, y, 1e-9) for x, y in zip(a + b, table[name][0] + table[name][1])) for name, a, b in SPEC)
        check("the script's table is spec 3.3's in the design doc (%d rows read)" % len(table), same)
    else:
        print("selftest: the design doc is not beside this script: its table check is skipped")

    # the recorder's columns
    check("capture columns: 6 + 2 x 12", len(CAPTURE_COLUMNS) == 30 and CAPTURE_COLUMNS[6] == "rightX" and CAPTURE_COLUMNS[9] == "rightXaxisX"
          and CAPTURE_COLUMNS[-1] == "leftZaxisZ")

    # the synthetic capture itself is a capture of spec 3.3 (a check of the generator)
    rows = synthetic_capture(0, {"right": ("-y", "+z"), "left": ("-y", "+z")}, pos_noise=0.0, turn_noise_deg=0.0)
    gen_ok = True
    for r, (name, a, b) in zip(rows, SPEC):
        for hand, (pos, palm, fingers) in (("right", a), ("left", b)):
            x = tuple(r[hand + "Xaxis" + k] for k in "XYZ")
            y = tuple(r[hand + "Yaxis" + k] for k in "XYZ")
            z = tuple(r[hand + "Zaxis" + k] for k in "XYZ")
            gen_ok &= angle_deg(scale(y, -1.0), palm) < 1e-6 and angle_deg(z, fingers) < 1e-6 and near_v(cross(y, z), x, 1e-9)
    check("synthetic capture without noise: palm on -Y, fingers on +Z, a turn", gen_ok)

    scenarios = [
        ("palm -y, fingers +z on both hands (the brief's)", {"right": ("-y", "+z"), "left": ("-y", "+z")}),
        ("palm -x right / +x left, fingers +z (ruling 3's guess, the mirror flips x)", {"right": ("-x", "+z"), "left": ("+x", "+z")}),
    ]
    tmp = tempfile.mkdtemp(prefix="fit_poses_selftest_")
    try:
        for which, (label, axes) in enumerate(scenarios):
            for seed in range(20):
                csv_path = os.path.join(tmp, "capture-%d-%d.csv" % (which, seed))
                write_capture(synthetic_capture(seed, axes), csv_path)
                try:
                    result = fit(read_capture(csv_path))
                except Exception as e:  # a failure of the fit, not of the test
                    check("%s, seed %d: the fit ran (%s: %s)" % (label, seed, type(e).__name__, e), False)
                    continue
                got = {h: (result[h]["palm"]["axis"], result[h]["fingers"]["axis"]) for h in HANDS}
                check("%s, seed %d: nearest signed axes %s" % (label, seed, got), got == axes)
                off = max(angle_deg(result[h][part]["dir"], axis_vector(axes[h][k])) for h in HANDS for k, part in enumerate(("palm", "fingers")))
                check("%s, seed %d: every fitted direction within 4 degrees of its axis (worst %.2f)" % (label, seed, off), off < 4.0)
                square = max(abs(dot(result[h]["palm"]["dir"], result[h]["fingers"]["dir"])) for h in HANDS)
                check("%s, seed %d: the fingers square to the palm" % (label, seed), square < 1e-9)
                far = [(name, h) for (name, a, b), pose in zip(SPEC, result["poses"])
                       for h, spec_hand, fitted in (("right", a, pose["right"]), ("left", b, pose["left"]))
                       if not near_v(fitted["pos"], spec_hand[0], 0.05)]
                check("%s, seed %d: every pose's place within 0.05 m of the spec's (far: %s)" % (label, seed, far), not far)
                agree = all(result[h][part]["mean"] < 8.5 and result[h][part]["axis_mean"] < 8.5 for h in HANDS for part in ("palm", "fingers"))
                check("%s, seed %d: agreement under 8.5 degrees, the fitted direction's and the nearest axis's" % (label, seed), agree)

        # Ruling 36: fingers 21 degrees off the controller's -y, tilted toward +z, on both hands (the owner's): the fit finds
        # the tilt within 2 degrees (each row's controller turned up to 4 degrees at random: at the usual 8 the mean of 16
        # rows itself wanders up to about 2.4 degrees; at 4 the worst of 100 seeds was 1.2), the nearest signed axis is
        # still -y, and the settings are printed as three numbers with 2 decimals that read back as the tilt
        tilt = (0.0, -math.cos(math.radians(21.0)), math.sin(math.radians(21.0)))
        tilted = {"right": ((-1.0, 0.0, 0.0), tilt), "left": ((1.0, 0.0, 0.0), tilt)}
        worst = 0.0
        for seed in range(20):
            csv_path = os.path.join(tmp, "tilt-%d.csv" % seed)
            write_capture(synthetic_capture(100 + seed, tilted, turn_noise_deg=4.0), csv_path)
            result = fit(read_capture(csv_path))
            worst = max([worst] + [angle_deg(result[h][part]["dir"], tilted[h][k]) for h in HANDS for k, part in enumerate(("palm", "fingers"))])
            check("21-degree tilt, seed %d: the nearest signed axes are still -x/+x palm, -y fingers" % seed,
                  all(result[h]["fingers"]["axis"] == "-y" for h in HANDS) and result["right"]["palm"]["axis"] == "-x" and result["left"]["palm"]["axis"] == "+x")
            check("21-degree tilt, seed %d: the fitted directions beat the signed axes (%.1f against %.1f degrees on the fingers)"
                  % (seed, result["right"]["fingers"]["mean"], result["right"]["fingers"]["axis_mean"]),
                  all(result[h]["fingers"]["mean"] < result[h]["fingers"]["axis_mean"] for h in HANDS))
        check("21-degree tilt: every fitted direction within 2 degrees of the tilt over 20 seeds (worst %.2f)" % worst, worst < 2.0)
        exact = fit(read_capture(write_capture(synthetic_capture(7, tilted, pos_noise=0.0, turn_noise_deg=0.0), os.path.join(tmp, "tilt-exact.csv"))))
        check("21-degree tilt without noise: the fit is exact (to the capture's 5 decimals)", all(angle_deg(exact[h][part]["dir"], tilted[h][k]) < 0.01 for h in HANDS for k, part in enumerate(("palm", "fingers"))))
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = main([os.path.join(tmp, "tilt-exact.csv"), "--out", os.path.join(tmp, "tilt-out")])
        settings = dict(re.findall(r'^  (\w+) = "([^"]*)"$', out.getvalue(), re.M))
        three = re.compile(r"^-?\d\.\d\d,-?\d\.\d\d,-?\d\.\d\d$")
        check("21-degree tilt: the command line prints the four settings as three numbers with 2 decimals (%s)" % settings,
              code == 0 and sorted(settings) == ["FingerAxisLeft", "FingerAxisRight", "PalmAxisLeft", "PalmAxisRight"] and all(three.match(v) for v in settings.values()))
        check("21-degree tilt: the printed fingers read back within 1 degree of the tilt (%s)" % settings.get("FingerAxisRight"),
              all(angle_deg(tuple(float(c) for c in settings.get(k, "0,0,0").split(",")), tilt) < 1.0 for k in ("FingerAxisRight", "FingerAxisLeft")))
        check("21-degree tilt: the printed palms read back as the palms (-x right, +x left)",
              angle_deg(tuple(float(c) for c in settings.get("PalmAxisRight", "0,0,0").split(",")), (-1.0, 0.0, 0.0)) < 1.0
              and angle_deg(tuple(float(c) for c in settings.get("PalmAxisLeft", "0,0,0").split(",")), (1.0, 0.0, 0.0)) < 1.0)
        check("21-degree tilt: the report names the nearest signed axis for reference", "nearest signed axis -y" in out.getvalue())

        # the game's rule for a hand's palm and fingers (HandAxes.Check): within 45 degrees of each other, or of opposite, is
        # refused; otherwise the fingers are made square to the palm
        for label, palm, fingers in (("30 degrees apart", (0.0, 0.0, 1.0), (0.0, 0.5, 0.866)), ("opposite", (0.0, 1.0, 0.0), (0.0, -1.0, 0.0)),
                                     ("the same", (1.0, 0.0, 0.0), (1.0, 0.0, 0.0)), ("no fingers", (1.0, 0.0, 0.0), (0.0, 0.0, 0.0))):
            try:
                square_fingers("right", palm, fingers)
                check("a palm and fingers %s are refused" % label, False)
            except FitError as e:
                check("a palm and fingers %s are refused, naming the hand (%s)" % (label, e), "right" in str(e))
        sq = square_fingers("left", (0.0, 0.0, 1.0), (0.0, 0.77, 0.64))
        check("a palm and fingers 50 degrees apart: the fingers made square to the palm", abs(dot(sq, (0.0, 0.0, 1.0))) < 1e-9 and angle_deg(sq, (0.0, 1.0, 0.0)) < 1e-6)

        # poses.txt (of the brief's scenario): what is written reads back as written, in PoseLibrary.Parse's format
        result = fit(read_capture(os.path.join(tmp, "capture-0-0.csv")))
        path = write_poses(result, os.path.join(out_dir, "Earthbending"), "a made-up capture (selftest, seed 0)")
        with open(path, encoding="utf-8") as f:
            lines = f.read().splitlines()
        poses = parse_poses(lines)
        check("poses.txt: 16 poses, in the table's order", list(poses) == NAMES
              and [l.split("|")[0].strip() for l in lines if l.split("#")[0].strip()] == NAMES)
        exact = all(near_v(tuple(poses[name][0][3 * i:3 * i + 3]), result["poses"][k]["right"][part], 9e-4)
                    and near_v(tuple(poses[name][1][3 * i:3 * i + 3]), result["poses"][k]["left"][part], 9e-4)
                    for k, name in enumerate(NAMES) for i, part in enumerate(("pos", "palm", "fingers")))
        check("poses.txt: the numbers are the fit's to 3 decimals", exact)
        check("poses.txt: default tolerances 0.3 0.24 0.33 65 65", all(p[2] == list(ZONE) + [PALM_DEG, FINGER_DEG] for p in poses.values())
              and all(l.endswith("| 0.3 0.24 0.33 65 65") for l in lines if l.split("#")[0].strip()))
        check("poses.txt: directions length 1", all(abs(length(tuple(p[h][3 * i:3 * i + 3])) - 1.0) < 2e-3 for p in poses.values() for h in (0, 1) for i in (1, 2)))
        print("selftest: wrote " + path)

        # refusals: a count other than 16, a missing column
        for count in (15, 17):
            bad = os.path.join(tmp, "count-%d.csv" % count)
            write_capture(synthetic_capture(1, scenarios[0][1], count=count), bad)
            try:
                fit(read_capture(bad))
                check("a capture of %d snapshots is refused" % count, False)
            except FitError as e:
                check("a capture of %d snapshots is refused, naming the count (%s)" % (count, e), str(count) in str(e))
        # a count other than 16: the guess names what each row looks like (mirroring allowed), with axes found from the rows
        for which, (label, axes) in enumerate(scenarios):
            for seed in range(5):
                extra = os.path.join(tmp, "extra-%d-%d.csv" % (which, seed))
                write_capture(synthetic_capture(10 + seed, axes, poses=SPEC[:6] + [REST] + SPEC[6:]), extra)
                g = guess(read_capture(extra))
                rows = g["rows"]
                own = all(r["pose"] == name and r["score"] <= 1.0 for r, (name, _, _) in zip(rows[:6] + rows[7:], SPEC))
                check("%s, seed %d: 17 rows with a rest as row 7: axes %s found from the rows" % (label, seed, g["axes"]), g["axes"] == {h: axes[h] for h in HANDS})
                check("%s, seed %d: the rest (row 7) matches no pose well (nearest %s, %.2f), every other row its own pose (%s)"
                      % (label, seed, rows[6]["pose"], rows[6]["score"], [(r["row"], r["pose"], round(r["score"], 2)) for r in rows if r["row"] != 7]),
                      len(rows) == 17 and rows[6]["score"] > 1.0 and own and g["junk"] == [7] and not g["missing"] and not g["twice"])
        short = os.path.join(tmp, "short.csv")
        write_capture(synthetic_capture(3, scenarios[0][1], poses=[p for p in SPEC if p[0] != "Kick"]), short)
        g = guess(read_capture(short))
        check("15 rows without Kick: the guess says no row looks like Kick (%s)" % g["missing"], g["missing"] == ["Kick"] and not g["junk"])
        mirrored = [(n, mirror_hand(b), mirror_hand(a)) if n == "Kick" else (n, a, b) for n, a, b in SPEC]
        doubled = os.path.join(tmp, "doubled.csv")
        write_capture(synthetic_capture(4, scenarios[0][1], poses=mirrored + [SPEC[5]]), doubled)
        g = guess(read_capture(doubled))
        kick = g["rows"][7]
        check("17 rows, Kick struck mirrored and Wall twice: Kick is named mirrored, Wall twice (%s, %s)" % (kick, g["twice"]),
              kick["pose"] == "Kick" and kick["mirrored"] and g["twice"] == {"Wall": [6, 17]} and not g["missing"] and not g["junk"])
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = main([os.path.join(tmp, "extra-0-0.csv")])
        text = out.getvalue()
        check("the command line refuses 17 rows and prints the guess (exit %d)" % code,
              code == 1 and "REFUSED" in text and "17 snapshots" in text and "row  7" in text and "matches no pose well" in text)

        missing = os.path.join(tmp, "missing.csv")
        write_capture(synthetic_capture(2, scenarios[0][1]), missing, [c for c in CAPTURE_COLUMNS if c != "leftYaxisZ"])
        try:
            read_capture(missing)
            check("a capture without a column is refused", False)
        except FitError as e:
            check("a capture without a column is refused, naming it (%s)" % e, "leftYaxisZ" in str(e))
    except Exception as e:
        check("the selftest ran to its end (%s: %s)" % (type(e).__name__, e), False)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)

    print("SELFTEST PASS" if not failures else "SELFTEST FAILED (%d)" % len(failures))
    return 0 if not failures else 1


# ----- the command line ------------------------------------------------------------------------------------------------

HELP = """\
Turns the owner's pose capture into the mod's poses.txt and the four [EarthPose] hand-axis settings.

The capture is the CSV the game writes with [EarthPose] Capture = true
(UserData\\Earthbending\\pose-capture-<yyyyMMdd-HHmmss>.csv). It must hold exactly 16 snapshots, one per pose,
taken in the spec 3.3 table's order:
  %s
and A is the RIGHT hand in every snapshot (the owner strikes each pose with A, the hand the table's first columns
describe, on the right). The fit assumes both: a capture with any other count is refused, naming the count, and
then each row is shown with the pose of the table it looks most like and the game's score for it (mirrored allowed;
the axes found from the rows themselves), so the rows that are rests or slips, and the poses missing or struck twice,
can be found. The same list follows the fit's warning when an axis agrees badly (the poses out of order).

For each hand it fits the palm and the fingers as directions in the controller's own space (Ruling 36: any
direction, not only +x -x +y -y +z -z): each pose's design direction taken into that snapshot's controller space,
averaged over the 16 poses and made length 1, the fingers then made square to the palm. It prints each direction's
mean and worst angle to the spec's over the poses, and the nearest signed axis for reference. It writes poses.txt
from the measured places and the fitted directions (spec 3.3's tolerances: each place a zone of 0.30 m sideways,
0.24 m up/down and 0.33 m in reach; the palm and fingers within 65 degrees) and prints the four settings
PalmAxisRight, FingerAxisRight, PalmAxisLeft and FingerAxisLeft as three numbers with 2 decimals.

poses.txt goes to <UserData folder>\\Earthbending\\poses.txt with --out, else beside the capture. An older poses.txt
there is kept as poses.txt.bak.
""" % ", ".join(NAMES)


def main(argv):
    ap = argparse.ArgumentParser(prog="fit_poses.py", description=HELP, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("capture", nargs="?", help="the capture CSV")
    ap.add_argument("--out", help="the UserData folder: poses.txt goes to its Earthbending folder (with --selftest: any folder)")
    ap.add_argument("--selftest", action="store_true", help="fit made-up captures of the 16 spec poses and check the result; prints SELFTEST PASS")
    args = ap.parse_args(argv)
    if args.selftest:
        out = args.out or tempfile.mkdtemp(prefix="fit_poses_out_")
        return selftest(out)
    if not args.capture:
        ap.print_usage()
        return 2
    try:
        snaps = read_capture(args.capture)
    except FitError as e:
        print("REFUSED: " + str(e))
        return 1
    except OSError as e:
        print("REFUSED: cannot read %s: %s" % (args.capture, e))
        return 1
    try:
        result = fit(snaps)
    except FitError as e:
        print("REFUSED: " + str(e))
        if snaps:
            print_guess(guess(snaps))
        return 1
    if report(result) > 25.0:
        print_guess(guess(snaps))
    folder = os.path.join(args.out, "Earthbending") if args.out else os.path.dirname(os.path.abspath(args.capture))
    path = write_poses(result, folder, os.path.basename(args.capture))
    print("Wrote " + path)
    print("The four settings, [EarthPose] in UserData\\Earthbending.cfg (directions in each controller's own space):")
    for h, key in (("right", "Right"), ("left", "Left")):
        print('  PalmAxis%s = "%s"' % (key, result[h]["palm"]["setting"]))
        print('  FingerAxis%s = "%s"' % (key, result[h]["fingers"]["setting"]))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
