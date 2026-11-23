"""Blue's Earthbending, Task 3: the owner's pose capture -> the mod's poses.txt and the four hand-axis settings.

Stdlib only. Run it with python -I:

  python -I Earthbending/tools/fit_poses.py <pose-capture-....csv> [--out <UserData folder>]
  python -I Earthbending/tools/fit_poses.py --selftest [--out <folder>]

See --help for the details.
"""

import argparse
import csv
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
# The fit only uses the palm and finger directions here, to decide which local axis of each controller is the palm and
# which the fingers; poses.txt gets the owner's own measured places and directions. This is NOT Poses.cs's table, which
# holds the owner's capture once Task 3 is done (the selftest checks it against the design doc instead).

PX, NX, PY, NY, PZ, NZ = (1.0, 0.0, 0.0), (-1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, -1.0, 0.0), (0.0, 0.0, 1.0), (0.0, 0.0, -1.0)
STOMP_PALM, STOMP_FINGERS = (0.0, -0.71, -0.71), (0.0, -0.71, 0.71)

SPEC = [
    ("Base",     ((0.20, -0.55, 0.30), PY, PZ),                   ((-0.20, -0.55, 0.30), PY, PZ)),
    ("Disc",     ((0.05, -0.30, 0.60), NX, PZ),                   ((-0.05, -0.30, 0.35), NZ, PX)),
    ("Ball",     ((0.15, -0.30, 0.25), NX, PZ),                   ((-0.10, -0.30, 0.60), PX, PZ)),
    ("Pillar",   ((0.20, -0.55, 0.30), NX, PZ),                   ((-0.20, -0.05, 0.25), PX, PY)),
    ("Cube",     ((0.20, -0.05, 0.25), NX, PY),                   ((-0.20, -0.05, 0.25), PX, PY)),
    ("Wall",     ((0.20, 0.10, 0.30), PY, PZ),                    ((-0.20, 0.10, 0.30), PY, PZ)),
    ("Straight", ((0.20, -0.25, 0.25), NX, PY),                   ((-0.15, -0.25, 0.60), NY, PZ)),
    ("Kick",     ((0.20, -0.25, 0.25), NX, PY),                   ((-0.05, -0.70, 0.20), NZ, NY)),
    ("Stomp",    ((0.20, -0.55, 0.40), STOMP_PALM, STOMP_FINGERS), ((-0.20, -0.55, 0.40), STOMP_PALM, STOMP_FINGERS)),
    ("Uppercut", ((0.20, -0.30, 0.25), NX, (0.0, 0.97, 0.26)),    ((-0.05, -0.05, 0.35), NZ, PY)),
    ("Parry",    ((0.20, -0.25, 0.50), PZ, PY),                   ((-0.20, -0.25, 0.50), PZ, PY)),
    ("Hold",     ((0.05, -0.25, 0.50), PZ, PY),                   ((0.05, -0.40, 0.45), PY, PZ)),
    ("Flick",    ((0.10, -0.25, 0.50), PX, PZ),                   ((0.10, -0.50, 0.45), PX, PZ)),
    ("Explode",  ((0.06, -0.50, 0.30), NZ, NX),                   ((-0.06, -0.50, 0.30), NZ, PX)),
    ("Jump",     ((0.20, -0.75, 0.20), PY, PZ),                   ((-0.20, 0.10, 0.10), PX, PY)),
    ("Dash",     ((0.20, -0.75, 0.20), PY, PZ),                   ((-0.10, -0.30, 0.60), PX, PZ)),
]
NAMES = [s[0] for s in SPEC]

# the default tolerances of spec 3.3 (PoseLibrary.DefaultPosTol, DefaultPalmDeg, DefaultFingerDeg)
POS_TOL, PALM_DEG, FINGER_DEG = 0.18, 40.0, 45.0

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


def axis_dir(hand, axis):
    """The direction a signed local axis ("-y") of one snapshot's controller points in the body frame."""
    v = hand[axis[1]]
    return scale(v, -1.0) if axis[0] == "-" else v


def choose(snaps, hand, part):
    """The signed local axis of one hand's controller that agrees best with the spec's palm (part 1) or fingers (part 2)
    over all the poses: the smallest mean angle. Ties go to the earlier axis of AXES."""
    side = 1 if hand == "right" else 2
    ranking = []
    for axis in AXES:
        angles = {}
        for snap, spec in zip(snaps, SPEC):
            ref = spec[side][part]
            if checked(ref):
                angles[spec[0]] = angle_deg(axis_dir(snap[hand], axis), ref)
        worst = max(angles, key=angles.get)
        ranking.append({"axis": axis, "mean": sum(angles.values()) / len(angles), "worst": angles[worst], "worst_pose": worst, "angles": angles})
    ranking.sort(key=lambda r: r["mean"])
    best = dict(ranking[0])
    best["next_axis"], best["next_mean"] = ranking[1]["axis"], ranking[1]["mean"]
    return best


def fit(snaps):
    """The axes of both hands and the 16 measured poses. Refuses a capture of any count but 16, and one whose palm and
    fingers come out on the same axis of a controller."""
    if len(snaps) != len(SPEC):
        raise FitError("the capture has %d snapshot%s; the fit wants exactly %d, one per pose in this order: %s"
                       % (len(snaps), "" if len(snaps) == 1 else "s", len(SPEC), ", ".join(NAMES)))
    result = {}
    for h in HANDS:
        palm, fingers = choose(snaps, h, 1), choose(snaps, h, 2)
        if palm["axis"][1] == fingers["axis"][1]:
            raise FitError("the %s hand's palm (%s) and fingers (%s) came out on the same axis of the controller: the snapshots"
                           " do not look like the 16 poses in order with A on the right hand" % (h, palm["axis"], fingers["axis"]))
        result[h] = {"palm": palm, "fingers": fingers}
    result["poses"] = [
        dict({"name": name, "n": snap["n"]}, **{h: {"pos": snap[h]["pos"],
                                                     "palm": axis_dir(snap[h], result[h]["palm"]["axis"]),
                                                     "fingers": axis_dir(snap[h], result[h]["fingers"]["axis"])} for h in HANDS})
        for snap, (name, _, _) in zip(snaps, SPEC)]
    return result


def n3(x):
    s = "%.3f" % x
    return "0.000" if s == "-0.000" else s


def format_poses(result, source):
    """poses.txt's lines, in PoseLibrary.Parse's format: <Name> | A | B | posTol palmDeg fingerDeg, A the right hand."""
    def hand(h):
        return "  ".join(" ".join(n3(c) for c in v) for v in (h["pos"], h["palm"], h["fingers"]))

    axes = "; ".join("%s hand palm %s, fingers %s" % (h, result[h]["palm"]["axis"], result[h]["fingers"]["axis"]) for h in HANDS)
    lines = [
        "# Blue's Earthbending poses: the owner's own, fitted by fit_poses.py from %s on %s (%s)."
        % (source, datetime.now().strftime("%Y-%m-%d %H:%M"), axes),
        "# <Name> | A: x y z  palm x y z  fingers x y z | B: the same | posTol palmDeg fingerDeg",
        "# Body frame: metres from the head as captured (BodyScale 1), +Z ahead (head yaw only), +Y up, +X right; A is the right hand; 0 0 0 = not checked.",
    ]
    tol = "%g %g %g" % (POS_TOL, PALM_DEG, FINGER_DEG)
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
    """The agreement of the axes and each pose's distance from the design's, for the person running the fit."""
    print("Axes (mean angle to spec 3.3's directions over the 16 poses; the worst pose; the next best axis):")
    for h in HANDS:
        for part in ("palm", "fingers"):
            r = result[h][part]
            print("  %-5s %-7s %s  %5.1f deg  (worst %5.1f, %s; next %s at %5.1f)"
                  % (h, part, r["axis"], r["mean"], r["worst"], r["worst_pose"], r["next_axis"], r["next_mean"]))
    print("Per pose, how far the owner's is from the design's (palm deg, fingers deg, place m; ! = outside 40 / 45 / 0.18):")
    for k, (name, a, b) in enumerate(SPEC):
        cells = []
        for h, spec_hand in (("right", a), ("left", b)):
            p = result["poses"][k][h]
            d = (angle_deg(p["palm"], spec_hand[1]), angle_deg(p["fingers"], spec_hand[2]), length(sub(p["pos"], spec_hand[0])))
            marks = ["!" if v > lim else " " for v, lim in zip(d, (PALM_DEG, FINGER_DEG, POS_TOL))]
            cells.append("%-5s %5.1f%s %5.1f%s %5.2f%s" % (h, d[0], marks[0], d[1], marks[1], d[2], marks[2]))
        print("  %2s %-9s %s   %s" % (result["poses"][k]["n"], name, cells[0], cells[1]))
    worst = max(result[h][part]["mean"] for h in HANDS for part in ("palm", "fingers"))
    if worst > 25.0:
        print("WARNING: an axis agrees with the design only to %.1f degrees on average: check that the 16 snapshots are the"
              " poses in order, with A on the right hand." % worst)


# ----- the selftest ----------------------------------------------------------------------------------------------------

def controller_axes(palm, fingers, palm_axis, finger_axis):
    """The local X, Y and Z axes (as {'x':..,'y':..,'z':..}) of a controller whose palm is on palm_axis and fingers on
    finger_axis (e.g. "-y", "+z"), turned so that its palm points along palm and its fingers along fingers. A Unity
    transform's axes are a turn: right = up x forward, up = forward x right, forward = right x up."""
    n = normalized(palm)
    f = normalized(sub(fingers, scale(n, dot(fingers, n))))
    cols = {palm_axis[1]: scale(n, -1.0 if palm_axis[0] == "-" else 1.0),
            finger_axis[1]: scale(f, -1.0 if finger_axis[0] == "-" else 1.0)}
    nxt = {"x": "y", "y": "z", "z": "x"}
    third = ({"x", "y", "z"} - set(cols)).pop()
    cols[third] = cross(cols[nxt[third]], cols[nxt[nxt[third]]])
    return cols


def random_unit(rng):
    while True:
        v = (rng.uniform(-1, 1), rng.uniform(-1, 1), rng.uniform(-1, 1))
        if 1e-3 < length(v) <= 1.0:
            return normalized(v)


def synthetic_capture(seed, axes, pos_noise=0.03, turn_noise_deg=8.0, count=16):
    """Rows of a capture of spec 3.3's poses (A on the right hand), as the recorder writes them: each hand's place moved
    up to pos_noise metres and the whole controller turned up to turn_noise_deg degrees about a random axis. axes is
    {"right": (palm axis, finger axis), "left": (...)}. count other than 16 repeats or drops poses at the end."""
    rng = random.Random(seed)
    head, yaw = (rng.uniform(-500, 500), rng.uniform(150, 250), rng.uniform(-500, 500)), rng.uniform(-180, 180)
    rows, t = [], 0.0
    for i in range(count):
        _, a, b = SPEC[i % len(SPEC)]
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


def parse_poses(lines):
    """poses.txt read as PoseLibrary.Parse reads it (a check of what this script writes): {name: (A 9 numbers, B 9, tol 3)}."""
    poses = {}
    for line in lines:
        text = line.split("#", 1)[0].strip()
        if not text:
            continue
        parts = text.split("|")
        if len(parts) != 4:
            raise ValueError("%d parts in: %s" % (len(parts), line))
        nums = [[float(w) for w in p.split()] for p in parts[1:]]
        if [len(n) for n in nums] != [9, 9, 3]:
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
                check("%s, seed %d: axes %s" % (label, seed, got), got == axes)
                far = [(name, h) for (name, a, b), pose in zip(SPEC, result["poses"])
                       for h, spec_hand, fitted in (("right", a, pose["right"]), ("left", b, pose["left"]))
                       if not near_v(fitted["pos"], spec_hand[0], 0.05)]
                check("%s, seed %d: every pose's place within 0.05 m of the spec's (far: %s)" % (label, seed, far), not far)
                agree = all(result[h][part]["mean"] < 8.5 and result[h][part]["next_mean"] > 45.0 for h in HANDS for part in ("palm", "fingers"))
                check("%s, seed %d: agreement under 8.5 degrees, the next axis over 45" % (label, seed), agree)

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
        check("poses.txt: default tolerances 0.18 40 45", all(p[2] == [POS_TOL, PALM_DEG, FINGER_DEG] for p in poses.values()))
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
describe, on the right). The fit assumes both: a capture with any other count is refused, naming the count.

For each hand it picks the signed local axis of the controller (+x -x +y -y +z -z) that agrees best with the spec's
palm over all 16 poses, and separately the one that agrees best with its fingers, and prints the agreement in degrees.
It writes poses.txt from the measured places and directions (tolerances 0.18 m, 40 and 45 degrees) and prints the four
settings PalmAxisRight, FingerAxisRight, PalmAxisLeft and FingerAxisLeft.

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
        result = fit(read_capture(args.capture))
    except FitError as e:
        print("REFUSED: " + str(e))
        return 1
    except OSError as e:
        print("REFUSED: cannot read %s: %s" % (args.capture, e))
        return 1
    report(result)
    folder = os.path.join(args.out, "Earthbending") if args.out else os.path.dirname(os.path.abspath(args.capture))
    path = write_poses(result, folder, os.path.basename(args.capture))
    print("Wrote " + path)
    print("The four settings, [EarthPose] in UserData\\Earthbending.cfg:")
    for h, key in (("right", "Right"), ("left", "Left")):
        print('  PalmAxis%s = "%s"' % (key, result[h]["palm"]["axis"]))
        print('  FingerAxis%s = "%s"' % (key, result[h]["fingers"]["axis"]))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
