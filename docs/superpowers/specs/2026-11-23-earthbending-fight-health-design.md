# Blue's Earthbending: fight health (design)

Approved by the owner on 2026-11-23 ("Yes, build it"). It adds to `2026-11-22-earthbending-design.md` (sections 3.7 and 3.8) and reverses one line of the arena design (`earthbending-arena/2026-11-22-earthbending-arena-design.md`: "No rounds, scores or rules in code").

## 1. What the owner asked for

- "Lower the damage to the stats given by rumble."
- "When you are in the area have you not regenerate and the damage is permanent until you get knocked out the arena or 'killed' but don't actually kill you."
- "When they hit 0 have the thing that did the hit that made it 0 do a ton of knockback instantly flinging them out the arena."

Why: the play-test of 1.0.0 had the owner's character at 2 health, and their own kicked Wall (damage 24) killed it. A duel should have stakes without anyone really dying.

## 2. RUMBLE's numbers (its wiki, read 2026-11-23)

- Every player has 20 health. Nothing gives it back during a fight.
- A structure's damage is its power: Disc 0, Ball 1, Pillar 1, Cube 2, Wall 3, +1 if it is grounded, +1 if it is moving. The hardest hit is a sliding Wall: 5.
- Falling into the ring's pit costs all 20.

Our power rule (spec 3.4) is already this one.

## 3. The design

### 3.1 The ring

A ring is a place on the server: a centre, the ring's radius (the fighting floor), the arena's radius (everything round it: the pit and the rim) and the height of the floor. A player is:

- **on the ring:** within the ring's radius of the centre, and their feet between 1 m under the floor and 8 m above it (a Jump is still on the ring);
- **in the arena:** not on the ring, within the arena's radius, between 6 m under the floor and 25 m above it;
- **outside:** anywhere else.

A player counts as having left the ring once they have been off it for 0.3 s.

There is at most one ring. It comes from, in this order:

1. another mod (the arena) through `Earthbending.Arena.SetRing(...)`, a small public class; `ClearRing()` removes it;
2. `[EarthFight] Ring` on the server: "x, y, z, ring radius, arena radius" (empty: no ring), for testing on open ground;
3. the server console: `earth ring <player> [radius]` puts a test ring round where that player stands (arena radius = radius + 6), `earth ring off` removes it, `earth ring` says where it is. Never saved.

With no ring, everything in 3.2 to 3.5 is off and 3.6 applies everywhere.

### 3.2 Fight health

- A player who steps onto the ring gets `[EarthFight] Points` (20) of fight health.
- A stone that hits a player on the ring takes its power off their fight health and does not touch their real health (no `ImpactHit`). The knockback, the hitstun and the stone's break are as today.
- Fight health never comes back while its player stays on the ring.
- A hit of power 0 (a still, loose Disc) takes nothing, as today.

### 3.3 The finishing hit

The hit that takes a player's fight health to 0:

- flings them: their knockback is along the stone's way, 45 degrees up, at the speed that lands them `FlingClearMetres` (3) past the ring's edge from where they stand, for the gravity the player's game falls with, held between `FlingMinSpeed` (8) and `FlingMaxSpeed` (16) m/s;
- stuns them (`FlingHitstunSeconds` 1.5): no bending until it ends;
- plays a shockwave and a boom at them on every game (the Explode blast's look and sound);
- never really hurts them.

The fling has to carry the whole flight: the player's game must not cut the push short in the air.

### 3.4 The end of a fight

When any player leaves the ring (flung, fallen, pushed or walked off), every player still on the ring goes back to full fight health. The player who left has none until they step onto the ring again, which gives them 20.

### 3.5 In the arena but off the ring

Stones ignore a player who is in the arena but not on the ring: no damage, no knockback, and the stone does not break on them. (Watchers on the rim and whoever is climbing out of the pit.)

### 3.6 Outside every ring

A stone's hit takes the same share of the player's REAL health: `power / PlayerHealthPoints` (20) of their maximum health. It can really kill. Creatures are unchanged (`DamagePerPower`), until a play-test shows a creature's numbers.

### 3.7 What players see

- Over each fighter's head: a row of small stones, one per point of fight health left, in groups of five, turned toward whoever looks. Readable from the ring and the rim.
- Your own: a band of the same small stones round your left forearm (you cannot see over your own head). *(The controller's call: the owner approved the row over the head; this is the same look for the one fighter who cannot see it.)*
- A lost point: its stone is gone. All stones come back at once at a refill.
- A ring made by the setting or the console is drawn on every game as a thin dotted circle on the ground; a ring set by another mod is not drawn.
- The stones are the game's own boulder mesh and material, small. No new art.

### 3.8 The network (message 19, two more kinds; the wire version stays 1)

- **Fight** (server to every game): a player's identifier and their fight health (0 to 250; 255 = none). Sent when it changes, and to a game that joins.
- **Ring** (server to every game): the centre, the two radii and whether to draw it; or "none". Sent when it changes, and to a game that joins.
- The finishing hit's look rides the existing Applied event with a new event byte.

## 4. Approaches

1. **In Earthbending, the ring given to it (chosen).** Earthbending already owns the hits, the knockback message and message 19, so fight health is one branch where a stone hits a player. The arena mod only says where its ring is. Testable on open ground before the arena exists.
2. **In the arena mod.** It would need a hook on every stone hit and its own message, and would put combat rules in a mod about a place. Rejected.
3. **Everywhere, no ring.** Against "when you are in the area". Rejected.
4. **The game's real health with regeneration switched off.** The owner's character had 2 health, the game regenerates by its own rules and really downs a player at 0, and real health would carry in and out of the ring. Rejected for a separate 20 points.

## 5. Goals, non-goals, anti-goals

- **Goals:** a duel has a clear end without a death; RUMBLE's damage numbers; the finishing hit is a moment.
- **Non-goals:** rounds, scores, teams, timers, a winner's announcement, saved results, any change to creatures' damage.
- **Anti-goals:** a player really dying on the ring from a stone; a watcher hurt or shoved by a stray stone; fight health that a player cannot read; a fling that leaves the loser standing on the ring.

## 6. Left to the builder

The exact sizes and places of the stones over the head and round the forearm, the dotted circle's look, how the fling is kept through the flight on the player's game, and the names of the new Unity-free types. All numbers are settings.
