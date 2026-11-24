"""Draws Earthbending/pose-guide.html from the pose table in Poses.cs, so the guide always matches the game.
Run: python -I Earthbending/tools/pose_guide.py   (from the repository root)"""
import math, os, re

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "..", "Poses.cs")
OUT = os.path.join(HERE, "..", "pose-guide.html")

DIRS = {"PX": (1, 0, 0), "NX": (-1, 0, 0), "PY": (0, 1, 0), "NY": (0, -1, 0), "PZ": (0, 0, 1), "NZ": (0, 0, -1),
        "StompPalm": (0, -0.71, -0.71)}
WHAT = {
    "Base": "Arms a stone. Do this first, then go straight into a stone's pose.",
    "Disc": "Stone: a flat disc that flies off.", "Ball": "Stone: a ball that rolls away.",
    "Pillar": "Stone: a tall pillar.", "Cube": "Stone: a cube.", "Wall": "Stone: a wide wall.",
    "Straight": "Punches a stone straight ahead.", "Kick": "Kicks a stone along the ground.",
    "Stomp": "Drives a stone down.", "Uppercut": "Launches a stone up.", "Parry": "Stops a stone coming at you.",
    "Hold": "Picks a stone up; swing to throw it.", "Flick": "Holds a stone between your hands and turns it.",
    "Explode": "Marks a stone to blast when it breaks.", "Jump": "Throws you up.", "Dash": "Throws you forward.",
}
GROUPS = [("Start here", ["Base"]), ("Stones (Base first, then one of these)", ["Disc", "Ball", "Pillar", "Cube", "Wall"]),
          ("Moves on a stone", ["Straight", "Kick", "Stomp", "Uppercut", "Parry", "Hold", "Flick", "Explode"]),
          ("Moving yourself", ["Jump", "Dash"])]


def vec(text):
    text = text.strip()
    if text in DIRS:
        return DIRS[text]
    m = re.match(r"new V3\(([^)]*)\)", text)
    return tuple(float(x.strip().rstrip("f")) for x in m.group(1).split(","))


def split_args(s):
    out, depth, cur = [], 0, ""
    for ch in s:
        if ch == "(": depth += 1
        if ch == ")": depth -= 1
        if ch == "," and depth == 0:
            out.append(cur); cur = ""
        else:
            cur += ch
    out.append(cur)
    return [x.strip() for x in out]


def table():
    src = open(SRC, encoding="utf-8-sig").read()
    tol = re.search(r"DefaultTolX = ([\d.]+)f, DefaultTolY = ([\d.]+)f, DefaultTolZ = ([\d.]+)f", src)
    poses = {}
    for m in re.finditer(r"^\s*P\(Move\.(\w+),\s*(.*)\),\s*$", src, re.M):
        a = split_args(m.group(2))
        poses[m.group(1)] = [vec(x) for x in a]   # A pos, palm, fingers, B pos, palm, fingers
    assert len(poses) == 16, len(poses)
    return poses, tuple(float(x) for x in tol.groups())


def word(v):
    names = [("right", "left"), ("up", "down"), ("forward", "back toward you")]
    parts = []
    for i, (pos, neg) in enumerate(names):
        if abs(v[i]) >= 0.25:
            parts.append(pos if v[i] > 0 else neg)
    return " and ".join(parts) if parts else "any way"


def height(y):
    # metres below the eyes -> a body landmark
    for limit, name in [(-0.60, "hip"), (-0.42, "belly"), (-0.18, "chest"), (0.06, "eye level"), (9, "above your head")]:
        if y <= limit:
            return name
    return "chest"


S = 190  # pixels per metre


def figure(view, a, b, tol):
    """view 0: from behind, as you see your own hands (x across, y up). view 1: from your right side (z across = forward to the right)."""
    w, h, ox, oy = 150, 232, 75 if view == 0 else 40, 66
    def pt(p):
        return (ox + (p[0] if view == 0 else p[2]) * S, oy - p[1] * S)
    rx, ry = (tol[0] if view == 0 else tol[2]) * S, tol[1] * S
    o = [f'<svg viewBox="0 0 {w} {h}" role="img" aria-label="{"behind" if view == 0 else "side"} view">']
    if view == 0:   # head, shoulders, torso
        o.append(f'<circle cx="{ox}" cy="{oy}" r="15" class="body"/><path d="M{ox-34} {oy+28} Q{ox} {oy+14} {ox+34} {oy+28} L{ox+26} {oy+128} L{ox-26} {oy+128} Z" class="body"/>')
    else:           # head with a nose pointing forward, torso from the side
        o.append(f'<circle cx="{ox}" cy="{oy}" r="15" class="body"/><path d="M{ox+14} {oy-3} l8 5 l-8 4" class="body"/><path d="M{ox-12} {oy+24} L{ox+12} {oy+24} L{ox+10} {oy+128} L{ox-12} {oy+128} Z" class="body"/>')
    for p, cls, label in ((a, "ha", "1"), (b, "hb", "2")):
        x, y = pt(p)
        o.append(f'<ellipse cx="{x:.0f}" cy="{y:.0f}" rx="{rx:.0f}" ry="{ry:.0f}" class="zone {cls}"/>')
    for p, cls, label in ((a, "ha", "1"), (b, "hb", "2")):
        x, y = pt(p)
        o.append(f'<circle cx="{x:.0f}" cy="{y:.0f}" r="9" class="hand {cls}"/><text x="{x:.0f}" y="{y+4:.0f}" class="num">{label}</text>')
    o.append(f'<text x="{w/2}" y="12" class="cap">{"from behind (your right is right)" if view == 0 else "from your right side"}</text></svg>')
    return "".join(o)


def card(name, p, tol):
    ap, an, af, bp, bn, bf = p
    def line(n, pos, palm, fingers):
        side = "right" if pos[0] > 0.02 else "left" if pos[0] < -0.02 else "middle"
        return (f'<li><b class="h{n}">Hand {n}</b>: {height(pos[1])}, {side}, {int(round(pos[2]*100))} cm out. '
                f'Palm {word(palm)}, fingers {word(fingers)}.</li>')
    return (f'<article><h3>{name}</h3><p class="what">{WHAT[name]}</p><div class="figs">{figure(0, ap, bp, tol)}{figure(1, ap, bp, tol)}</div>'
            f'<ul>{line(1, ap, an, af)}{line(2, bp, bn, bf)}</ul></article>')


def main():
    poses, tol = table()
    body = []
    for title, names in GROUPS:
        body.append(f"<h2>{title}</h2><section>" + "".join(card(n, poses[n], tol) for n in names) + "</section>")
    html = f"""<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Earthbending Pose Guide</title>
<style>
:root {{ --bg:#f6f3ec; --card:#fffdf8; --ink:#2b2620; --soft:#6f665a; --line:#d9d1c2; --body:#cfc6b4; --a:#b4542a; --b:#2f6f8f; }}
@media (prefers-color-scheme: dark) {{ :root {{ --bg:#1d1a16; --card:#27231d; --ink:#ece5d8; --soft:#a89e8e; --line:#3d372e; --body:#4a4338; --a:#e08a5c; --b:#6fb3d2; }} }}
body {{ margin:0; padding:20px 16px 40px; background:var(--bg); color:var(--ink); font:16px/1.45 system-ui, sans-serif; }}
main {{ max-width:1100px; margin:auto; }}
h1 {{ font-size:1.6rem; margin:0 0 6px; }} h2 {{ font-size:1.1rem; margin:26px 0 10px; }}
p.lead, li {{ color:var(--soft); }} p.lead b, li b {{ color:var(--ink); }}
section {{ display:grid; grid-template-columns:repeat(auto-fill, minmax(300px, 1fr)); gap:12px; }}
article {{ background:var(--card); border:1px solid var(--line); border-radius:10px; padding:12px 14px; }}
h3 {{ margin:0; font-size:1.05rem; }} .what {{ margin:2px 0 6px; color:var(--soft); font-size:.92rem; }}
.figs {{ display:flex; gap:6px; }} svg {{ width:50%; height:auto; }}
.body {{ fill:var(--body); stroke:none; }} .zone {{ fill-opacity:.16; stroke-width:1; stroke-dasharray:3 3; }}
.ha {{ fill:var(--a); stroke:var(--a); }} .hb {{ fill:var(--b); stroke:var(--b); }}
.hand {{ stroke:var(--card); stroke-width:2; }} .num {{ fill:#fff; font:bold 11px system-ui; text-anchor:middle; }}
.cap {{ fill:var(--soft); font:10px system-ui; text-anchor:middle; }}
ul {{ margin:6px 0 0; padding-left:18px; font-size:.9rem; }} .h1 {{ color:var(--a); }} .h2 {{ color:var(--b); }}
</style></head><body><main>
<h1>Earthbending pose guide</h1>
<p class="lead">Empty hands, standing on natural ground. A pose fires as your hands <b>arrive</b> in it; you don't have to hold it.
The dotted shapes are how far off a hand can be ({int(tol[0]*100)} cm sideways, {int(tol[1]*100)} cm up or down, {int(tol[2]*100)} cm in reach).
<b>Hand 1 and hand 2 can be either hand</b>: every pose also works mirrored.
For a stone, strike <b>Base</b> first, then go into the stone's pose within about a second.</p>
{''.join(body)}
<p class="lead">In the game: the server command <b>earth practice &lt;your name&gt;</b> shows the next move on a card with two hand markers.</p>
</main></body></html>
"""
    open(OUT, "w", encoding="utf-8", newline="\n").write(html)
    print(f"wrote {os.path.normpath(OUT)} ({len(html)} bytes), zones {tol}")


if __name__ == "__main__":
    main()
