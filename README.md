# Blue's Earthbending

Earthbending for **A Township Tale** (PC, MelonLoader, a dedicated server and its players' games). Stand on open ground with both hands empty and strike two-hand poses, after RUMBLE's pose system: stone rises out of the earth in front of you, more poses push, stop, lift, hold and blow it up, and two throw you.

In development: the numbers below are the design's starting values, most not yet tuned in a play-test.

## Poses

16 poses, each a place and a turn of both hands. A pose fires as the hands pass through it: no holding, no speed needed. Every pose also works mirrored: **A** is the right hand as written, or the left when mirrored, and **B** the other.

| Pose | Hands | Does |
|---|---|---|
| **Base** | Both palms up, fingers forward, a little above the waist, shoulder width apart. | Arms a stone for 1.2 s. |
| **Disc** | A out in front, palm in, fingers forward. B nearer, palm toward you, fingers across, against A's forearm ("punching your own forearm"). | A flat stone, thrown up, then forward. |
| **Ball** | Both palms in, fingers forward, just under shoulder height; A near the body, B out in front ("drawing a bow"). | A ball pops up out of the ground. |
| **Pillar** | A stays low where the base pose was, palm in, fingers forward. B at face height, palm in, fingers up. | A pillar rises. |
| **Cube** | Both hands at face height, palms in, fingers up. | A 1 m cube rises. |
| **Wall** | Both palms up, lifted straight up from the base pose to eye level. | A wall rises, broadside to you. |
| **Straight** | A in front of the shoulder, palm in, fingers up. B out in front, palm down, fingers forward. | Pushes the stone the way you look, and you back. |
| **Kick** | A as Straight. B low at the belly button, palm toward you, fingers down. | Pushes it up; frees a standing one. |
| **Stomp** | Both hands at the waist, shoulder width apart, fingers forward and 45° down, palms down and back. | Drives it down; it lands standing. |
| **Uppercut** | A as Straight, fingers tipped a little forward. B in front of the face, palm toward you, fingers up. | Up and forward; a standing one also spins. |
| **Parry** | Both hands out in front at shoulder height and width, palms forward, fingers up. | Stops it dead, floating. |
| **Hold** | A (the holding hand) out in front at shoulder height, palm forward, fingers up. B just under A, palm up, fingers forward. | It follows A; swing to throw it. |
| **Flick** | A out in front at shoulder height, palm toward A's own side, fingers forward. B a few fists under A, turned the same way. | It hangs ahead of your hands and turns with them. |
| **Explode** | Both palms toward the stomach, fingers sideways, knuckles together ("fist-bumping yourself"). | Marks it: it blasts when it breaks. |
| **Jump** | A palm up, fingers forward, in front of the hip (a one-handed base pose). B above the shoulder, palm in, fingers up. | Throws you up. |
| **Dash** | A as Jump. B out in front, palm in, fingers forward. | Throws you forward. |

"Out in front" is about half reach (0.42 to 0.45 m ahead of the eyes): no pose needs a straight arm.

### The numbers

Places are metres from your eyes for a 1.75 m player, times `[EarthPose] BodyScale` (1). The frame turns with your head left and right, never up and down.

```
              +y up
               |    +z ahead (the way you look, kept level)
               |   /
               |  /
               | /
   -x  --------o-------- +x right
              eyes

   y  0.00  eye level                Wall
   y -0.05  face                     Cube, Pillar's B hand
   y -0.25  shoulder                 Straight, Parry, Hold, Flick
   y -0.38  upper belly              Explode
   y -0.55  a little above the waist Base, Stomp
   y -0.65  belly button             Kick's B hand
   y -0.72  hip                      Jump and Dash's A hand
```

| Pose | A (right): x, y, z / palm / fingers | B (left): x, y, z / palm / fingers |
|---|---|---|
| Base | 0.24, -0.57, 0.22 / up / ahead | -0.24, -0.57, 0.22 / up / ahead |
| Disc | 0.05, -0.30, 0.45 / left / ahead | -0.05, -0.30, 0.28 / back / right |
| Ball | 0.15, -0.30, 0.22 / left / ahead | -0.10, -0.30, 0.45 / right / ahead |
| Pillar | 0.20, -0.55, 0.30 / left / ahead | -0.20, -0.05, 0.25 / right / up |
| Cube | 0.20, -0.05, 0.25 / left / up | -0.20, -0.05, 0.25 / right / up |
| Wall | 0.20, 0.00, 0.30 / up / ahead | -0.20, 0.00, 0.30 / up / ahead |
| Straight | 0.20, -0.25, 0.25 / left / up | -0.15, -0.25, 0.45 / down / ahead |
| Kick | 0.20, -0.25, 0.25 / left / up | -0.05, -0.65, 0.22 / back / down |
| Stomp | 0.20, -0.55, 0.35 / down-back / down-ahead | -0.20, -0.55, 0.35 / down-back / down-ahead |
| Uppercut | 0.20, -0.30, 0.25 / left / up, 15° ahead | -0.05, -0.10, 0.32 / back / up |
| Parry | 0.20, -0.25, 0.42 / ahead / up | -0.20, -0.25, 0.42 / ahead / up |
| Hold | 0.05, -0.25, 0.42 / ahead / up | 0.05, -0.40, 0.38 / up / ahead |
| Flick | 0.10, -0.25, 0.42 / right / ahead | 0.10, -0.48, 0.38 / right / ahead |
| Explode | 0.08, -0.38, 0.18 / back / left | -0.08, -0.38, 0.18 / back / right |
| Jump | 0.20, -0.72, 0.20 / up / ahead | -0.20, 0.05, 0.10 / right / up |
| Dash | 0.20, -0.72, 0.20 / up / ahead | -0.10, -0.30, 0.45 / right / ahead |

"Back" means toward you; down-back and down-ahead are 45° between the two. Mirrored, swap left and right and the sign of x.

- **Zones:** a hand counts within 0.30 m to either side, 0.24 m up or down and 0.33 m forward or back of its place, with the palm and the fingers each within 65° of their way (1.1.1, fitted from the owner's practice; were 0.25, 0.20, 0.30 m and 60°).
- **Your own poses:** `UserData\Earthbending\poses.txt` replaces any pose it lists. To make one: `[EarthPose] Capture = true` and `[EarthLooks] GuideMarkers = false`, strike the 16 poses in the table's order with A on the right hand (hold each still 0.6 s, arms down between them; both hands buzz twice per pose), switch Capture off, then run `python -I Earthbending/tools/fit_poses.py <the pose-capture-....csv in UserData\Earthbending>` (Python 3, this repository). It writes `poses.txt` beside the capture and prints the four hand-axis settings for `[EarthPose]`.

### Rules

- **Stones** (Disc to Wall) fire only within 1.2 s of a base pose, standing on natural ground. The base pose arms only where no stone pose also matches. An armed stone fires at its deepest point (the hands within half its zone of its centre, or stopped in it), so crossing the edge of one stone's zone on the way to another fires only the other. Firing one disarms.
- **Modifiers** (Straight to Explode) work on natural ground or in the air, never standing on other ground. They go to the stone in reach most in front: its nearest point within 3 m of your eyes, its centre in front of you and at most 2.4 m above your feet.
- **Jump and Dash** work only from natural ground, and not while a stone is armed.
- **Natural ground** is ground the game's spade can dig (the open terrain). Standing on one of your stones does not count.
- Nothing fires with anything in either hand, during the stun after a knockback, or while downed.
- A pose fires again only after the hands have left it for 0.1 s. Cooldowns: each stone 0.3 s; Straight 0.5, Kick 0.3, Stomp 0.3, Uppercut 0.5, Parry 0.3, Hold 0.8, Flick 0.8, Explode 0.3; Jump and Dash 0.6 each.

## Stones

| Stone | Power | Mass | Size | Appears |
|---|---|---|---|---|
| **Disc** | 0 | 100 kg | 0.70 x 0.14 x 0.70 m | 1.2 m ahead, 0.5 m up, thrown up at 3.75 m/s, then forward at 17.5 m/s 0.12 s later |
| **Ball** | 1 | 150 kg | 0.60 m across | rises 1.2 m ahead in 0.12 s, then thrown up at 6.25 m/s |
| **Pillar** | 1 | 275 kg | 0.55 x 1.80 x 0.55 m | rises 1.3 m ahead in 0.25 s |
| **Cube** | 2 | 300 kg | 1.00 x 1.00 x 1.00 m | rises 1.5 m ahead in 0.25 s |
| **Wall** | 3 | 400 kg | 2.40 wide x 2.00 high x 0.50 m | rises 1.8 m ahead in 0.32 s |

- The spot where it rises must be natural ground too (the server checks it). A stone summoned where another stands throws that one up at 5 m/s.
- 8 stones per player (a ninth breaks your oldest), 64 on the server. A stone breaks after 60 s, below the world, or when its owner leaves. Nothing is ever saved: a server restart leaves none.

## Modifiers

Pushes are in N s: the speed is the push over the mass (2875 on a 275 kg Pillar is 10.5 m/s). Standing means on the ground, still or sliding. A rising stone takes a modifier once it has risen.

| Modifier | Loose stone | Standing stone |
|---|---|---|
| **Straight** | 2250 the way you look | 2875; it slides, slowing 4 m/s every second, and stands again under 0.1 m/s |
| **Kick** | 1875 up | set loose, then 1875 up |
| **Stomp** | 1875 down; it moves only up and down until it lands, then stands | nothing |
| **Uppercut** | 1125 forward and 1125 up | set loose, 1625 forward and 1625 up, spinning 5 rad/s top forward |
| **Parry** | stops dead and floats 3 s, or until something moves it faster than 0.1 m/s | set loose, then floats |
| **Hold** | follows A firmly, about 1 m ahead of it plus half the stone's size, along where you look; thrown by a swing of A (faster than 3.5 m/s relative to your head for at least 0.1 s, after the stone has first settled within 0.5 m of its place), let go at the swing's peak at 1.5 times the hand's fastest, along the swing; a swing back toward you throws nothing; walking, dashing, turning your head or a jump of the hand's copy never throws it | slides along the ground after A; thrown along the ground |
| **Flick** | hangs 1 m plus half its size ahead of the point between your hands and turns with them; only Stomp or a break drops it | refused |
| **Explode** | marked | marked |

- Every push above is `[EarthModifiers] PushScale` (1.25) times its setting (the settings below are the numbers before it). No modifier leaves a stone faster than 25 m/s.
- **Straight** pushes you back 2.5 m/s from a stone standing still (or rising), 1.7 m/s from any other, and 0.25 m/s up (half of each in the air). **Two Straights** on one stone within 0.4 s burst it with a blast. Your own Straight's cooldown is 0.5 s, so it takes two players.
- **Hold** also lets go (no throw) on Parry, on Hold again with that hand, and when you are downed, die or leave; Straight, Kick, Uppercut and Stomp let it go and then push it as usual. Each hand holds one stone. For 0.5 s after any let-go, a throw included, the stone does not hit you. A throw is judged on the server's copies of your hand and head over the last 0.1 s, and a single jump of the hand's copy (a late packet, a hitch, lost tracking, a snap turn) does not count: a swing of the hand 0.8 to 1 m or more in a quarter second throws on a normal server (at about 6 to 10 m/s, about 0.1 s after the hand was fastest), a short hard flick does not; turning the whole body with the arm out (1.9 m/s at 180 degrees a second, 2.8 at 270) does not.
- **The blast** (a marked stone breaking, or a double Straight) reaches 3 m, in full within 1.5 m and a quarter at the edge: players are knocked back (with `HurtPlayers`), creatures staggered (no damage), loose objects and stones pushed (1000 N s), standing stones set loose. It never breaks a stone. A marked stone does not blast when the server quits or `earth clear` breaks it.

## Movement and hits

- **Jump:** 6 m/s up (about 2.2 m). **Dash:** 8 m/s the way you look, with 0.5 m/s up. Both on your own game, with a dust puff.
- **Power** = the stone's own, +1 still or sliding on the ground (not rising or stomped), +1 moving faster than 3 m/s.
- **Hits:** a stone moving at 2 m/s or more itself hits creatures, and players it is closing on, then it breaks. A creature takes 8 damage and 0.5 cripple per point of power (untuned guesses). A player off every ring takes power / 20 of their maximum health as it is now (RUMBLE's 20 health: a sliding Wall, power 5, takes a quarter) and 0.5 cripple per power; on a ring, fight health instead (below). A rising stone never hits. Your own stones can hit you, but not one you hold or flick.
- **Knockback:** a struck player is pushed 4 m/s along the stone's way (level, or 45° up) and cannot bend for power x 0.7 / 3 s (never after a Disc).
- **Clashes:** more power breaks less; equal, both break. Both first freeze and shake for 0.06 + 0.02 x (both powers) s, then the winner carries on at its old speed.

## Fight health

A duel with stakes and no death. On a ring:

- Stepping onto the ring gives you **20 points** of fight health. A stone that hits you takes its power off them (Disc 0, Ball 1, Pillar 1, Cube 2, Wall 3, +1 still or sliding on the ground, +1 moving fast: a sliding Wall is 5) and never touches your real health. The knockback and the stun are as anywhere. Nothing gives points back while you stay on the ring.
- **The hit that takes you to 0 flings you out**: along the stone's way, 45 degrees up, fast enough to land 3 m past the ring's edge (8 to 16 m/s), with 1.5 s of no bending, and a shockwave and a boom at you on every game. The fling starts from rest (whatever you were doing), and it never really hurts you: not even the fall when you land. If you still come down on the ring you cannot bend there; after 5 s on it you are back in with 20 points.
- **A fight ends when anyone leaves the ring** (flung, fallen, pushed or walked off, after 0.3 s off it): everyone still on it goes back to 20. Whoever left has none until they step back on, which gives them 20 again. Leaving the server, dying or being downed counts as leaving.
- **Watchers are safe:** stones ignore anyone in the arena round the ring (the rim, the pit), and anyone on the ring with no points (the one just flung, still over it): no damage, no knockback, the stone does not break on them. Blasts leave them alone too.
- **Off every ring** a stone takes power / 20 of your real health, and can really kill.
- **What you see:** each fighter's points as a row of small stones over their head, one per point, in groups of five, turned toward you. From far off the row grows, so it still reads from the rim, and is then two lines of ten; every second fighter's row hangs higher, so two rows never run together. At most 40 stones are drawn for a fighter. Your own: the same count in rows of five on your forearm (the left, or `FightBandHand`), on the side facing your eyes; it hides while it is closer than 0.2 m to your eyes, and `FightBand = false` removes it alone. A lost point's stone is gone; a refill brings them all back. A test ring is drawn on the ground as a dotted circle; the arena's own ring is not.

A **ring** is a centre on the floor, the ring's radius (the fighting floor: up to 1 m under it and 8 m over it, so a Jump is still on the ring) and the arena's radius round it (6 m under the floor to 25 m over). There is at most one. It comes from the arena mod (`Arena.SetRing`), else the console (`earth ring`), else `[EarthFight] Ring`. A ring set, moved or removed ends every fight at once.

## Install

You need MelonLoader **0.7.3** and **SyncLib** (the custom 1.1.1 build). SyncLib and this mod go on **the server and every player's game**.

1. Close the game and stop the server.
2. Put `Earthbending.dll` and `SyncLib.dll` in the `Mods` folder of the server and of every game.
3. Start the server, then the game.

It loaded if `MelonLoader\Latest.log` has `[Earth] Blue's Earthbending v... started (server)` on the server and `(client)` on the game.

- **Never on a Quest server.** Quest has no SyncLib, so a server running it locks Quest players out. Run it as a separate PC server.
- **A game without the mod** is turned away at the join by SyncLib (missing prefabs).
- **If no pose fires**, the default hand axes (fingers along the controller's +z) do not fit your controllers: on the owner's, the fingers sit about 21° off -y toward +z, beyond the 65° a pose allows. Run the capture (see Your own poses) and put the four axis settings `fit_poses.py` prints (`PalmAxisRight`, `FingerAxisRight`, `PalmAxisLeft`, `FingerAxisLeft`) in that game's `[EarthPose]`.
- **Stones hurt players everywhere, town included, whatever the server's PvP setting** (the game's spawn invincibility still applies). On a public server set `[EarthDamage] HurtPlayers = false`.

## Settings

Everything is in `UserData\Earthbending.cfg`, written in full at the first start; edits apply within 2 s. The server's sections count for the stones: `[EarthStones]`, `[EarthModifiers]`, `[EarthDamage]`. Each game reads its own `[EarthPose]`, `[EarthMovement]`, `[EarthLooks]`. `[EarthLog]` is each machine's own. A game's file also lists `[EarthStones]` power and mass and `[EarthModifiers]` Reach and MaxHeight: a game picks a modifier's stone with its own Reach and MaxHeight, so keep those the same as the server's.

### [EarthStones] (server)

| Key | Default | What |
|---|---|---|
| `Enabled` | `Disc, Ball, Pillar, Cube, Wall` | The stones a pose may summon. |
| `DiscPower` `BallPower` `PillarPower` `CubePower` `WallPower` | 0, 1, 1, 2, 3 | Power for hits and clashes. |
| `DiscMass` `BallMass` `PillarMass` `CubeMass` `WallMass` | 100, 150, 275, 300, 400 | kg. |
| `DiscAheadMetres` `BallAheadMetres` `PillarAheadMetres` `CubeAheadMetres` `WallAheadMetres` | 1.2, 1.2, 1.3, 1.5, 1.8 | How far in front of the head it appears. |
| `BallRiseSeconds` `PillarRiseSeconds` `CubeRiseSeconds` `WallRiseSeconds` | 0.12, 0.25, 0.25, 0.32 | How long the rise takes (0: up at once). |
| `DiscHeightMetres` | 0.5 | The Disc appears loose this high above the ground. |
| `DiscLift` | 300 | Its push straight up as it appears, N s (x PushScale). |
| `DiscPush` | 1400 | Its push the way you look, N s (x PushScale); a modifier or a break first cancels it. |
| `DiscPushDelaySeconds` | 0.12 | From the lift to the push. |
| `BallLaunchSpeed` | 5 | The Ball is thrown up at this m/s (x PushScale) once risen (0: not thrown). |
| `SummonUnderLift` | 4 | A standing stone where a new one rises is set loose and thrown up at this m/s (x PushScale). |
| `MaxPerPlayer` | 8 | Stones per player; one more breaks their oldest. |
| `MaxTotal` | 64 | Stones on the server; past it a summon is refused. |
| `LifeSeconds` | 60 | A stone breaks this long after it was summoned. |
| `HeadSlackMetres` | 1 | A summon is refused when the head its game claims is further than this from the server's copy. |
| `CooldownSlackSeconds` | 0.1 | Claims may arrive this much closer together than the game's cooldown. |

### [EarthModifiers] (server; Reach and MaxHeight also each game)

| Key | Default | What |
|---|---|---|
| `Reach` | 3 | A modifier reaches a stone whose nearest point is this close to the head, m (3 keeps a held Wall in reach). |
| `MaxHeight` | 2.4 | ... and whose centre is at most this high above the feet, m. |
| `RangeSlackMetres` | 0.5 | Extra reach on the server, for lag. |
| `StraightGrounded` | 2300 | Straight on a standing stone, N s (x PushScale); it slides. |
| `StraightFree` | 1800 | Straight on a loose stone, N s (x PushScale). |
| `SlideDeceleration` | 4 | A sliding stone slows this many m/s every second (0: only what it hits stops it). |
| `DoubleStraightSeconds` | 0.4 | Two Straights on one stone within this, by anyone, burst it. |
| `KickUp` | 1500 | N s up (x PushScale). |
| `StompDown` | 1500 | N s down, on a loose stone (x PushScale). |
| `UppercutFree` | 900 | N s forward and as much up, on a loose stone (x PushScale). |
| `UppercutGrounded` | 1300 | The same on a standing stone, set loose first (x PushScale). |
| `UppercutSpin` | 5 | Its spin on a standing stone, rad/s (not scaled). |
| `MaxStoneSpeed` | 25 | No modifier or blast leaves a stone faster, m/s. |
| `PushScale` | 1.25 | Every push a modifier gives a stone (Straight, Kick, Stomp, Uppercut), the Disc's two pushes, the Ball's launch and `SummonUnderLift` are this many times their numbers. Not a blast, a player's knockback or pushback, Hold or Flick. |
| `ParrySeconds` | 3 | A parried stone floats this long ... |
| `ParryBreakSpeed` | 0.1 | ... or until moved faster than this, m/s. |
| `ExplodeRadius` | 3 | The blast's reach, m. |
| `ExplodeForce` | 1000 | Its push on stones and loose objects, N s, times their share. |
| `HoldDistance` | 1 | A held stone keeps about this far ahead of your top hand, along where you look (a flicked one, of the point between the hands), plus half its size, m: its centre is this plus a quarter of its width and depth (a Ball: its radius): a Disc 1.35, a Ball 1.30, a Pillar 1.275, a Cube 1.50, a Wall 1.725. |
| `HoldFollowRate` | 12 | How quickly a held stone follows its place, rad/s, times its weight factor (Disc 1.05, Ball 1, Pillar 0.875, Cube 0.85, Wall 0.75). A Ball 2 m off is within 10 cm in about 0.4 s, a Wall 0.55 s; a Ball trails a hand moving 1 m/s by 0.17 m, a Wall 0.22. |
| `HoldDamping` | 1 | 1 closes on its place as fast as it can without passing it; less swings past, more creeps in. |
| `HoldLetGoSpeed` | 3.5 | A held stone is thrown by a swing of the hand faster than this relative to your head, m/s ... (walking, a dash, a knock-back or a head turn never throw) |
| `HoldArmMetres` | 0.5 | ... but only after the stone has once been this close to its place while the hand was that slow, m. |
| `HoldThrowScale` | 1.5 | A thrown stone flies at your head's velocity plus the swing's fastest times this, when faster than it already moves (never past `MaxStoneSpeed`; a slide-hold only along the ground). 0: it keeps its own speed. |
| `HoldSwingSeconds` | 0.1 | The hand must stay that fast this long before a swing throws. A single jump of the hand's copy never throws (the hand reading leaves out a lone jump). A swing of about 0.8 m or more in a quarter second throws; a short hard flick (0.6 m in 0.15 s) only sometimes; on a server as slow as 15 steps a second a 1 m swing about 7 times in 10. |
| `HoldSwingMaxSeconds` | 0.2 | A swing throws at its peak, or this long after it began, whichever is first. |
| `FlickFollowRate` | 10 | How quickly a flicked stone follows its place, rad/s, times the same weight factor. A Ball 2 m off is within 10 cm in about 0.5 s, a Wall 0.65 s. |
| `FlickDamping` | 1 | As `HoldDamping`, for Flick. |
| `FlickTurnRate` | 10 | How quickly Flick turns the stone to match the hands, rad/s: a quarter turn within 5 degrees in about half a second. |
| `FlickTurnDamping` | 1 | 1 turns as fast as it can without swinging past; less swings past and wobbles. |

### [EarthDamage] (server)

| Key | Default | What |
|---|---|---|
| `HurtPlayers` | true | Stones and blasts hit players. Off: players are ignored (no damage, no knockback, no break). |
| `DamagePerPower` | 8 | Damage per point of power to a creature (Blunt). |
| `PlayerHealthPoints` | 20 | Off every ring, a stone takes power / this of a player's maximum health. 0: never. |
| `CripplePerPower` | 0.5 | Cripple per point of power. |
| `MinHitSpeed` | 2 | A stone hits only while moving this fast itself, and two stones clash only meeting this fast, m/s. |
| `MovingSpeed` | 3 | Faster than this, +1 power, m/s. |
| `KnockbackSpeed` | 4 | A struck player is pushed this many m/s (a blast: times their share). |
| `BreakOnHit` | true | A stone breaks once it has hit a creature or a player. |
| `HitCooldownSeconds` | 0.5 | One stone hits one target at most once this often. |
| `HitstopSeconds` | 0.06 | Two clashing stones freeze this long ... |
| `HitstopPerPower` | 0.02 | ... plus this for each point of their power together. |

### [EarthFight] (server)

| Key | Default | What |
|---|---|---|
| `Enabled` | true | Fight health on a ring. Off: no ring; every hit takes real health (`PlayerHealthPoints`). |
| `Points` | 20 | Fight health on stepping onto the ring, 1 to 250. A change ends every fight. |
| `Ring` | (empty) | A test ring: `x, y, z, ring radius, arena radius` (y the floor), e.g. `-697, 128.2, 87, 10, 16`. Drawn on the games. |
| `FlingClearMetres` | 3 | The finishing hit lands its player this far past the ring's edge ... |
| `FlingMinSpeed` | 8 | ... flung at least this fast, m/s ... |
| `FlingMaxSpeed` | 16 | ... and at most this fast, m/s. |
| `FlingHitstunSeconds` | 1.5 | No bending this long after the finishing hit. |
| `OutSeconds` | 5 | A knocked-out player still on the ring cannot bend; after this long there they are back in with full points. |

At the game's gravity (-8) a 16 m/s fling clears a ring up to 14.5 m in radius from its far side; the server warns (start-up, `earth ring`) about a wider one.

### [EarthPose] (each game)

| Key | Default | What |
|---|---|---|
| `MinHoldSeconds` | 0 | How long the hands must stay in a pose (0: passing through fires it). |
| `RearmSeconds` | 0.1 | A pose fires again only after the hands have left it this long. |
| `StructureWindowSeconds` | 1.2 | A stone fires only this soon after the base pose. |
| `StructureDeepScore` | 0.5 | An armed stone fires once the hands are this deep in its zone (0 its centre, 1 its edge) ... |
| `StructureStillMetres` | 0.01 | ... or once both hands stay within this many metres ... |
| `StructureStillSeconds` | 0.06 | ... for this long. |
| `MoveCooldownSeconds` | 0.6 | Jump's and Dash's cooldown. |
| `GroundGraceSeconds` | 0.15 | You count as standing this long after the game last had you on the ground (at most 0.5). |
| `BodyScale` | 1 | Your size against a 1.75 m player: every hand place is this many times as far from the head. Set by hand, never measured. |
| `PalmAxisRight` `FingerAxisRight` | `-x`, `+z` | The ways out of the right controller's palm and along its fingers: a signed axis or `a,b,c`. `fit_poses.py` fits them. |
| `PalmAxisLeft` `FingerAxisLeft` | `+x`, `+z` | The same for the left controller. |
| `Capture` | false | The pose recorder (see Poses). |
| `CaptureStillMetres` | 0.01 | A hand counts as still within this many metres ... |
| `CaptureStillSeconds` | 0.6 | ... and both still this long take a snapshot. |
| `CaptureMoveMetres` | 0.2 | The next snapshot waits until a hand has moved this far. |
| `CaptureRestBelowMetres` | 0.7 | Both hands this far under the head is a rest, never a snapshot. |
| `Practice` | false | A card in front of you names the next move to try, shows where the hands go, and counts what fired. |
| `PracticeList` | `Jump, Pillar, Straight, Wall, Cube, Ball, Disc, Kick, Stomp, Uppercut, Parry, Hold, Flick, Explode, Dash` | Its moves, in order. |
| `PracticeSkipSeconds` | 25 | A move not fired in this long is skipped. |
| `PracticeShowSeconds` | 1.5 | How long "worked" or "skipped" shows. |

### [EarthMovement] (each game)

| Key | Default | What |
|---|---|---|
| `JumpSpeed` | 6 | m/s up. |
| `DashSpeed` | 8 | m/s the way you look, level. |
| `DashLift` | 0.5 | m/s up with a dash, so the game does not stop it on the ground. |
| `PushbackGrounded` | 2.5 | Straight on a stone standing still (or rising) pushes you back this many m/s (half in the air). |
| `PushbackFree` | 1.7 | ... on any other. |
| `PushbackLift` | 0.25 | ... and lifts you this many m/s. |
| `PushEndSpeed` | 0.3 | A push slides you along the ground until slower than this, m/s ... |
| `PushMaxSeconds` | 1.5 | ... or for at most this long. |
| `MaxPushSpeed` | 20 | Stacked pushes never leave you faster, m/s. |

### [EarthLooks] (each game)

Effects and sounds are the game's own, by name; several names split by `;` are tried in order; empty plays nothing.

| Key | Default | What |
|---|---|---|
| `PoseHaptic` | 0.4 | Buzz on both hands when a pose fires (0 to 1). |
| `PoseHapticSeconds` | 0.05 | Its length. |
| `DustEffect` | `GroundPuffSmall` | At your feet on a jump or dash. |
| `RiseEffect`, `RiseSound` | `Digging Dirt Spray Effect;Digging Dirt Smoke Puff Effect`, `Digging DirtFall Sound Set` | Where a stone comes out of the ground. |
| `BreakEffect`, `BreakSound` | `Destroyed Rock`, `Rock Destroyed Sounds;Boulder Breaking Sounds` | Where a stone breaks. |
| `StompEffect`, `StompSound` | `Turabada Ground Slam Effect`, `Heavy Turabada Step Sounds` | Where a stomped stone lands. |
| `ExplodeMarkEffect` | `Mining Successful` | Once, when Explode marks a stone. |
| `ExplodeEffect`, `ExplodeSound` | `Shockwave Impact`, `Shockwave Sounds` | The blast. |
| `ShakeMetres` | 0.02 | A clashing stone shakes this far ... |
| `ShakeSeconds` | 0.15 | ... for this long. |
| `GuideDistanceMetres` | 1.2 | The guides' card floats this far in front of you. |
| `GuideDropMetres` | 0.15 | ... this far under eye level. |
| `GuideTextSize` | 0.035 | Its letters' height, m. |
| `GuideMarkers` | true | The two hand markers (off: the card only). |
| `GuideRightColour`, `GuideLeftColour` | `#FF8A1E`, `#3FA9FF` | The right (orange) and left (blue) markers. |
| `GuideBackColour` | `#000000B0` | The plate behind the card's text (empty: none). |
| `GuideFollowSeconds` | 0.3 | How slowly the card follows your head. |
| `FightStones` | true | Fight health as small stones (off: none; a drawn ring and the knock-out's blast stay). |
| `FightPipSize` | 0.035 | One stone of a row over a head, m. |
| `FightPipGap`, `FightGroupGap` | 0.012, 0.03 | The space between two stones, and the extra space between two groups of five, m. |
| `FightRowHeight` | 0.35 | A row floats this far over its fighter's head, m. Every second fighter's (by account) hangs a row's lines higher. |
| `FightRowFarMetres`, `FightRowFarScale` | 6, 2.5 | Further away than the first, a row grows with the distance, at most the second times its size (0 or 1: never). Grown past 1.5 times it is two lines of ten. |
| `FightBand` | true | Your own stones on your forearm (off: none there; the rows over other heads stay). |
| `FightBandHand` | `left` | The forearm that carries them: `left` or `right`. |
| `FightBandPipSize` | 0.018 | One stone on your own forearm, m. |
| `FightBandRadius` | 0.055 | Your own stones sit this far from the forearm's middle line, m. |
| `FightBandUp` | 0.12 | ... starting this far from the hand toward the elbow, m (0.04 m above the wrist). |
| `FightBandHideMetres` | 0.2 | They hide while nearer your eyes than this, and are back a quarter further out (0: never). |
| `FightRingWidth`, `FightRingColour` | 0.04, `#EDE6D2C0` | A drawn test ring's dashes. |

### [EarthLog] (each machine)

| Key | Default | What |
|---|---|---|
| `Debug` | false | Detail lines in `MelonLoader\Latest.log` for play-tests. |

## Server console

From the server launcher's Console. A player is their exact name (any case) or account id.

```
earth spawn <disc|ball|pillar|cube|wall> <player>   a loose stone on the ground, 1.5 m in front of that player (theirs)
earth push <player> <up m/s>                        throws that player straight up (no stun)
earth list                                          every stone, its number and place
earth clear                                         breaks every stone (a marked one does not blast)
earth ring <player> [radius]                        a test ring round where that player stands (10 m; the arena 6 m wider), drawn; the floor is their feet, so on a slope anyone over 1 m under it is in the arena, not on the ring
earth ring off                                      removes the console's test ring
earth ring                                          where the ring is, and who fights on it with how many points
```

The console's ring is never saved, and the arena mod's ring wins over it.

## For modders

### Message 19

The mod's only network message. The first byte is the kind, the second the wire version (`Wire.Version`, 1); a message of another version is dropped with one warning per connection. Floats are the game's `SerializeFloat`. `Net.cs` writes and reads each kind with one method, and logs each kind's size in bits at start-up.

| Kind | From | To | Fields after the two header bytes |
|---|---|---|---|
| 1 Summon | a game | the server | stone (byte: 0 Disc, 1 Ball, 2 Pillar, 3 Cube, 4 Wall), head position (3 floats), yaw in degrees (float) |
| 2 Modify | a game | the server | modifier (byte: 0 Straight, 1 Kick, 2 Stomp, 3 Uppercut, 4 Parry, 5 Hold, 6 Flick, 7 Explode), holding hand (byte: 0 right, 1 left; the pose's A), target entity `Identifier` (uint), head position (3 floats), yaw (float) |
| 10 Knockback | the server | one game | velocity (3 floats), stun seconds (float) |
| 11 Applied | the server | every game whose player's head is within 80 m | entity `Identifier` (uint), event (byte: a modifier as above, or 20 Broke, 21 Hitstop, 22 Exploded, 23 KnockedOut: entity 0, at the player's head), position (3 floats) |
| 12 Fight | the server | every game | the player's account (`UserInfo.Identifier`, 32 bits), fight health (byte: 0 to 250, 255 none). Sent when it changes, and every 5 s for every fighter |
| 13 Ring | the server | every game | has a ring (bool), centre (3 floats, y the floor), ring radius (float), arena radius (float), drawn (bool). Sent when it changes, and every 5 s |

- The game judges the pose and sends a claim, only when the server runs the mod. The server never judges poses; it checks the claim. A Summon: the stone on, its cooldown, the claimed head within `HeadSlackMetres` of the server's copy, natural ground at the spot. A Modify: its cooldown, a stone that still exists, in reach of the server's copy of the head and feet (plus `RangeSlackMetres`), allowed in the stone's state. Bytes out of range and numbers that are not finite are refused.
- Applied events sent today: 2 Stomp (a stomped stone landed), 7 Explode (a stone was marked), 20, 21, 22 and 23.
- The ring: `Earthbending.Arena.SetRing(Vector3 centre, float ringRadius, float arenaRadius)` and `Arena.ClearRing()`, on the dedicated server (ignored on a game). Not saved: set it after every server start.
- Hooks: the server listens from a postfix on `ServerHandler.ConnectionCreated`, a game from one on `JoinServerPipeline.ConnectionApprovedByServer`. Message numbers already taken by mods: 18 SyncLib, 19 this mod, 27 Blue's Archery Overhaul, 32 TavernLib, 137 ArmorRestored.
- SyncLib prefab ids: `EarthDisc`, `EarthBall`, `EarthPillar`, `EarthCube`, `EarthWall`, under the mod's name and author. Never rename any of them after a server has used them.

### The gauntlet seam

Who may bend is one method: `Bender.CanBend(PlayerController)` in `Bend.cs`. Today it is `Hands.Empty(pc)`: neither hand holds a pickup, a climbing hold or a bow string. The pose machine reads it every frame (through a two-frame gate), and false starts every pose over. Bending only while wearing a gauntlet, or with something held, is a change to that method alone.

## License

MIT, see [LICENSE](LICENSE).
