# Blue's Earthbending: design and research

Date: 2026-11-22. Status: research done, owner's decisions taken, plan in `docs/superpowers/plans/2026-11-22-earthbending.md`.

A new MelonLoader mod for A Township Tale (PC, server and game): stand on natural ground, strike two-hand poses with empty hands, and stone rises out of the earth in front of you; further poses throw, stop, lift, hold and blow it up, and two poses throw you. The reference is RUMBLE's pose system (https://rumble.wiki.gg/, read 2026-11-22). We take its *mechanics* (pose rules, the move list, the published numbers as starting values) and nothing of its art, sounds, names of places or UI: every look and sound is the game's own (house rule, see the art memory).

## 1. The owner's decisions (2026-11-22)

| Question | Answer |
|---|---|
| Who bends | Everyone with the mod, both hands empty. Later: only while wearing a gauntlet (keep one seam, `Bender.CanBend`, so that is one change). |
| Damage | Rocks always hurt players too, whatever the server's PvP setting, and hurt creatures. |
| Platform | PC only. SyncLib is fine (real network prefabs for the five stones). Never on Quest. |
| Scope | Everything in one plan, in playable phases, each ending in a play-test. |

Consequences the owner should know:
- **SyncLib must be on the server and every client.** The local server has `SyncLib.dll` in `Mods\disabled` today (the Quest cut-down). A server running this mod cannot take Quest players (they have no SyncLib), so run it as a separate PC server profile, or accept that Quest players are locked out while it is on.
- **"Always hurt players" means anyone with the mod can kill anyone near them, anywhere, town included.** It ships as the server setting `[Damage] HurtPlayers = true` so a public server can switch it off without a new build.
- Player movement is client-authoritative in ATT and has no anti-cheat: a player can raise their own jump or dash in their own file. Accepted (as every movement mod is).

## 2. How RUMBLE does it (reference, summarised)

### 2.1 Poses
- A move is a **static pose**: both hands at a place and turned a way. The path to the pose, and speed, do not matter. Hands need not be closed.
- Place and turn are judged in a frame that **turns with the head's yaw** (left/right) but not its pitch.
- Every pose is **ambidextrous**: the mirror image counts the same.
- A pose fires on entry. To fire the same pose again you must leave it first (move or turn a hand out of it).
- **Ground rule**: every pose needs the player's last grounded spot to be on dirt. Structures need the player grounded now; modifiers and movement also work in the air. Standing on non-dirt blocks modifiers.
- **Structures are two-part**: the base pose first, then the structure's pose. Players fail repeats by not returning fully to the base pose.
- **Base pose**: both hands palm up, fingers forward, a little above the waist, shoulder width apart.

### 2.2 Structures (power, weight; every structure's cooldown 0.3 s)

| Structure | Power | Weight | Spawns as | Second pose (after base) |
|---|---|---|---|---|
| Disc | 0 | 100 | free; a lift then a forward push launches it | A: arm fully out, fingers forward, palm to the side. B: palm toward the body, fingers to the side, across A's forearm ("punching my own forearm"). |
| Ball | 1 | 150 | free; pops straight up out of the ground | Both palms to the side, fingers forward, raised to just under shoulder height; A near the body, B fully extended ("drawing a bow"). |
| Pillar | 1 | 275 | rises, then stands grounded | A: palm to the side, fingers forward (low, where the base pose was). B: palm to the side, fingers up, at face height. |
| Cube (~1 m) | 2 | 300 | rises, then grounded | Both hands palm to the side, fingers up, at face height. |
| Wall | 3 | 400 | rises, then grounded; widest, a bit taller than a pillar | Both palms up the whole time, lifted straight up. |

- **Power** decides collisions: equal power, both break; more power breaks less. Both freeze briefly first (hitstop: frozen, unbreakable, shaking; longer for more total power), then the winner carries on at its old speed.
- **Grounded** adds +1 power (and darkens the stone's bottom). A power-0 disc only hurts when grounded and/or moving.

### 2.3 States of a structure
- **Free** (default): normal physics. Disc and ball start here.
- **Rising** (pillar, cube, wall): pushed up out of the ground, cannot move sideways; destroyed if it stays too long or sinks back fully. Ends grounded.
- **Grounded, still**: held in place by an anchor that strongly resists movement.
- **Grounded, sliding** (after Straight or during Hold): slides along the ground, cannot leave it; back to still under 0.1 m/s.
- **Stomped**: only vertical movement, pushed down; on touching the ground, grounded and still (any Hold or Flick breaks).
- **Floating** (Parry): no velocity, no gravity; ends after 3 s or once it moves faster than 0.1 m/s.

### 2.4 Modifiers
Range (judged per structure): its centre in the half-space in front of the head; its nearest bounding-box point within 2.5 m of the head; its centre no higher than 2.4 m.

| Modifier | Cooldown | Pose | Effect |
|---|---|---|---|
| Straight | 0.5 s | A: palm to the side, fingers up, in front of the shoulder. B: fingers forward, palm down, fully extended. | Horizontal push along the look direction: impulse 2300 on a grounded structure (it slides), 1800 on a free one. Pushes the player back (400 back + 40 up grounded, 275 back free; half in the air). Two Straights on one structure in quick succession explode it (force 1000). |
| Kick | 0.3 s | A as Straight. B: fingers down, palm toward the body, at the belly button. | Push up; frees a grounded structure. |
| Stomp | 0.3 s | Both hands shoulder width apart, fingers forward and down about 45 degrees. | Push down; grounds a free structure and kills its momentum when it lands. |
| Uppercut | 0.5 s | A as Straight, tilted a little forward. B: fingers up, palm toward the face, half extended in front of the face. | Up and forward, weaker than Straight or Kick each way; on a grounded structure stronger and with a forward spin. |
| Parry | 0.3 s | Both hands at shoulder height and width, extended, palms forward, fingers up. | Floating state (above). Lifts a grounded one off its anchor. |
| Hold | 0.8 s | A (the holding hand): shoulder height, extended, palm forward, fingers up. B: just under A, fingers forward, palm up. | The structure follows A at a set distance, loosely: a PD controller on position (p 1, no i; d by weight: 0-150 → 8, 150-300 → 9, 300-400 → 11 (13 up), 400+ → 15 (18 up)), output x 2500 free, x 1500 grounded, plus a force that cancels gravity. Lets go at 8 m/s (that is a throw) or on changing between grounded and free. Both hands can each hold one. |
| Flick | 0.8 s | A: shoulder height, extended, palm toward its own arm's side, fingers forward. B: a few fists under A, fingers forward, palm the same way as A. | Follows both hands, the hands' relative place sets its turn: PID on position (p 1, i 0.1, d 6 / 7 / 8.5 by weight), x 1000, and one on rotation; cancels gravity. Only free structures; dropped only by Stomp or breaking. |
| Explode | 0.3 s | Both palms toward the stomach, fingers pointing to the side, knuckles together ("fist-bumping yourself"). | Marks the structure: when it breaks, everything in a sphere around it is knocked back (stacks) and grounded structures there are freed. No extra damage. |

### 2.5 Movement
- **Jump**: A palm up, fingers forward, in front of the hip ("one-handed base pose"); B fingers up, palm to the side, raised above the shoulder. Upward push with a dust plume. Only from the ground.
- **Dash**: A as Jump; B fingers forward, palm to the side, extended in front. Forward push with a dust trail. Only from the ground.
- Both run on the player's own machine (no ping).

### 2.6 Knockback on players
- A structure hit pushes the player along the structure's velocity; an explosion pushes away from its centre. The direction is snapped to flat or 45 degrees up, whichever is nearer.
- While knocked back the player cannot summon or use movement, for `power x 0.7 / 3` s.
- A Disc hit never stuns.

## 3. Our version in ATT

### 3.1 What the game gives us (read in `_decomp/`, 2026-11-22)

| Need | Game API |
|---|---|
| Head (client) | `PlayerController.Current.Camera.transform` (the archery mod's `Fx.View()`). |
| Hands (client) | `PlayerController.Current.LeftController` / `.RightController` (`Controller`); their `transform.position/rotation` is the tracked controller, written in `OpenXRInputController.UpdatePosition`. Velocity: `controller.PlayerInput.RawInput.Velocity` (local only). |
| Hand empty | `!controller.Interactor.IsInteracting` (covers pickups, climbing holds, bow strings). `Controller.TryGetHeldPickup` is narrower. |
| Hands and head on the server | Each `Controller` has a `NetworkTransform`; the server snaps to the latest copy; sent every 2nd frame (about 22 ms at 90 fps) plus ping. |
| Grounded | `SmoothLocomotion.IsGrounded` (from the `CharacterController`), reached by `(PlayerController.Current as SmoothLocomotionPlayerController).SmoothLocomotion`. |
| Natural ground ("dirt") | The spade's own test: layer in Terrain 8, Ground 10 or GroundOverlay 11, and the collider has no `attachedRigidbody`. Terrain also holds combined static meshes, so a stone floor can pass; check the collider's `sharedMaterial` name in UnityExplorer during Task 1 and add a name check if a floor passes. |
| Push the local player | Write `SmoothLocomotion.velocity` (public field). Gravity is -8 x `GravityMultiplier`; friction decays it. **While grounded with `velocity.y <= 0.1` it is zeroed**, so every push keeps `y > 0.1` (a dash gets a small lift). Fall damage counts from the apex: call `SmoothLocomotion.TreatAsGrounded()` on the push. No movement while downed. |
| Push another player | Explosions never push players in vanilla. Movement is client-side, so the server sends that player's game a knockback message. |
| Spawn / destroy | `SpawnHelper.Spawn(PrefabManager.GetPrefab(hash), …)` (working call in `BluesArcheryTestKit/TestTools.cs:426`), `SaveMode.Ignore` in the callback; destroy with `NetworkEntity.SceneDestroy()` (server). `NetworkRigidbody` syncs position, velocity, angular velocity and `isKinematic` (clients lerp at 0.3). Scale is not synced after spawn. |
| Damage | Server-built `new ImpactHit(health, cripple, 1, effectiveness, null, ImpactType.Blunt, …)` then `HealthObject.DetectImpact(hit)`, as `Explosive.Trigger` does. A plain `ImpactHit` never meets the PvP gate (archery notes), which is what "always hurt players" needs. Knock-down = cripple; `Agent.Stun(seconds)` on Turabada, Gotera, GreaterGotera, Babu, Dragonfly. |
| Messages | Server: postfix on `ServerHandler.ConnectionCreated` → `connection.SetHandler((MessageType)N, …)`. Client: postfix on `JoinServerPipeline.ConnectionApprovedByServer`. Send: `connection.Send(null, (MessageType)N, (c, s) => …)`. Pattern: `BluesArcheryOverhaul/JoinGate.cs`. Free numbers: 3, 5, 8, 14, 16, 19, 31 (vanilla 0-30 minus those; mods 18 SyncLib, 27 archery, 32 TavernLib, 137 ArmorRestored). **We take 19.** |
| Haptics | `Controller.TriggerHaptic(strength, duration)`. |
| Effects, sounds | `PooledObjectDefinition` by name, `def.Get(pos, rot)`; `SoundEffectSet.PlayAtPosition`. **Never on the headless server** (sound calls throw there). |
| Physics | Fixed step 0.0222 s (45 Hz) on clients; the server varies 15-45 Hz with its frame rate. Gravity -8. |

### 3.2 Architecture

```
game (client)                                   server
-------------                                   ------
Hands.Read  head + hands -> BodyFrame
PoseMachine (Unity-free) -> Move
  Jump / Dash -> Moves.Push (own velocity)
  Straight's pushback -> Moves.Push
  structure  -> Net.Summon(kind, head, yaw) ---> Server.OnSummon: checks -> SpawnHelper.Spawn
  modifier   -> Net.Modify(mod, hand, target,   Server.OnModify: checks -> StoneBody.Apply
                head, yaw)                ----> StoneBody.FixedUpdate: state, Hold/Flick PD
                                                 hits -> damage, clash, explode
Net.OnKnockback -> Moves.Push + hitstun   <---- Net.Knockback(victim)
StoneLook (dress, rise, break, effects)   <---- spawn / NetworkRigidbody / Net.Applied (event)
```

- The **pose is detected on the shooter's own game** (precise, no lag) and sent as a claim. The server never re-detects poses. It checks what it can: cooldowns, caps, where the head is, natural ground at the spot, and the target in range. This is the archery mod's claim-and-check pattern.
- The **server owns every stone**: spawn, state, forces, hits, damage, breaking. Hold and Flick follow the server's copy of the hand (`Controller.transform` there), which is about 22 ms plus ping behind. Hold is loose by design, so this is accepted; the upgrade path is to give the holder's game authority.
- **Clients only draw**: the stone's mesh and material go on each machine when the entity appears; rise, break and modifier effects play from the spawn, the destroy and the server's `Applied` events.

### 3.3 Poses: the numbers

Frame: the origin is the head (camera) position. +Z is the head's forward flattened to the ground, +Y is world up, +X is right. Metres for a 1.75 m player, all multiplied by `[EarthPose] BodyScale` (1.0, a fixed number). (Ruling 35, play-test of 0.4.2: the size is NOT measured. The game raises every player's view to the avatar's eye height, 2.06 m above the terrain in the owner's log, while the hands track at real distances from the head, so an eye-height scale stretched every pose about 22% too far.)

Hand axes (Ruling 36): a controller's palm and finger directions are any direction in the controller's own space, not only its six axes (`[EarthPose] PalmAxisRight` and the others take `-y` or `0,-0.93,0.36`). The owner's controllers hold the fingers about 21 degrees from the controller's -y, tilted toward +z, on both hands. `fit_poses.py` fits these directions from a capture: the mean of each pose's design direction taken into the controller's space, with the fingers made square to the palm. Each hand is: position `p`, palm normal `n` (out of the palm) and finger direction `f`. The table gives A as the right hand. The mirror (A is the left hand) flips x of `p`, `n` and `f`. `-` means the axis is not checked.

**Flow, not hold (the owner, 2026-11-22, after the first capture):** "it's less about going to the exact pose and holding it than moving fluidly from one to the other", with "a little bit more tolerance", "not exactly fixed points", and no pose "far out from my body". So each hand's place is a roomy ZONE, an ellipsoid round `p` with half-sizes sideways 0.25, up/down 0.20 and reach (forward/back) 0.30 m, and the directions allow 60 degrees (palm) and 60 degrees (fingers). No pose needs a straight arm: "arm out" is about half reach (0.42-0.45 m ahead of the head). The table is the second design; `poses.txt` from a natural capture (the guide's words only, no markers) re-centres each zone on the player's own pose.

| Pose | A p | A n | A f | B p | B n | B f |
|---|---|---|---|---|---|---|
| Base | (0.20, -0.55, 0.30) | +Y | +Z | (-0.20, -0.55, 0.30) | +Y | +Z |
| Disc | (0.05, -0.30, 0.45) | -X | +Z | (-0.05, -0.30, 0.28) | -Z | +X |
| Ball | (0.15, -0.30, 0.22) | -X | +Z | (-0.10, -0.30, 0.45) | +X | +Z |
| Pillar | (0.20, -0.55, 0.30) | -X | +Z | (-0.20, -0.05, 0.25) | +X | +Y |
| Cube | (0.20, -0.05, 0.25) | -X | +Y | (-0.20, -0.05, 0.25) | +X | +Y |
| Wall | (0.20, 0.00, 0.30) | +Y | +Z | (-0.20, 0.00, 0.30) | +Y | +Z |
| Straight | (0.20, -0.25, 0.25) | -X | +Y | (-0.15, -0.25, 0.45) | -Y | +Z |
| Kick | (0.20, -0.25, 0.25) | -X | +Y | (-0.05, -0.65, 0.22) | -Z | -Y |
| Stomp | (0.20, -0.55, 0.35) | (0, -0.71, -0.71) | (0, -0.71, 0.71) | (-0.20, -0.55, 0.35) | (0, -0.71, -0.71) | (0, -0.71, 0.71) |
| Uppercut | (0.20, -0.30, 0.25) | -X | (0, 0.97, 0.26) | (-0.05, -0.10, 0.32) | -Z | +Y |
| Parry | (0.20, -0.25, 0.42) | +Z | +Y | (-0.20, -0.25, 0.42) | +Z | +Y |
| Hold | (0.05, -0.25, 0.42) | +Z | +Y | (0.05, -0.40, 0.38) | +Y | +Z |
| Flick | (0.10, -0.25, 0.42) | +X | +Z | (0.10, -0.48, 0.38) | +X | +Z |
| Explode | (0.06, -0.50, 0.28) | -Z | -X | (-0.06, -0.50, 0.28) | -Z | +X |
| Jump | (0.20, -0.72, 0.20) | +Y | +Z | (-0.20, 0.05, 0.10) | +X | +Y |
| Dash | (0.20, -0.72, 0.20) | +Y | +Z | (-0.10, -0.30, 0.45) | +X | +Z |

Rules of the pose machine (all settings, `[EarthPose]`):
- A pose fires the first frame the hands are inside it (`MinHoldSeconds` 0: passing through counts). It fires again only after it has stopped matching for `RearmSeconds` 0.1.
- While a structure is armed (the base pose within `StructureWindowSeconds`), Jump and Dash do not fire: moving from the base pose into a structure must not dash on the way (RUMBLE's own wiki warns of that accident). Modifiers still fire.
- (Ruling 33) An armed structure fires at its deepest point, not on entry: the hands moving from Base to Cube cross the edge of Ball's zone first, and Ball must not fire. While armed, the structure zones the hands are in are followed. A structure fires once the hands are deep inside it, or once they stop going deeper into it while it is the best-scored structure. Passing the edge of one structure on the way to another fires only the other. The rule is tested by straight-line hand paths from Base to each structure: each fires exactly that structure, with either hand as A.
- (Ruling 34) The base pose arms only from a frame where no structure matches (a clean base), so one loose pose in a zone that both Base and a structure reach never summons on its own.
- Structure poses count only while armed: the base pose matched within the last `StructureWindowSeconds` 1.2, and the player is grounded on natural ground now. Firing a structure disarms; the base pose must be struck again.
- Movement needs grounded on natural ground. Modifiers work grounded on natural ground or in the air; never while standing on other ground.
- Both hands empty, or nothing counts and the machine resets.
- During hitstun (knockback) nothing fires.
- Several poses in one frame: an armed structure first, then modifiers, then movement; within a group the best-scored (smallest normalised error).
- Per-move cooldowns as RUMBLE's (2.2, 2.4); Jump and Dash `MoveCooldownSeconds` 0.6.
- Hold's holding hand is A (the top hand) of the matched assignment.

### 3.4 Structures: our numbers (all `[Stones]`, server)

| Kind | Power | Mass (kg) | Collider (m) | Spawn | Look |
|---|---|---|---|---|---|
| Disc | 0 | 100 | box 0.70 x 0.14 x 0.70 | 1.2 m ahead, at knee height, free; lift 300 up, then after 0.12 s 1400 forward along the yaw | the boulder mesh squashed flat |
| Ball | 1 | 150 | sphere r 0.30 | 1.2 m ahead, from under the ground, free, 5 m/s up | the game's sandstone boulder mesh |
| Pillar | 1 | 275 | box 0.55 x 1.80 x 0.55 | 1.3 m ahead, rises over 0.35 s | chamfered stone block |
| Cube | 2 | 300 | box 1.00 x 1.00 x 1.00 | 1.5 m ahead, rises over 0.35 s | chamfered stone block |
| Wall | 3 | 400 | box 2.40 x 2.00 x 0.50 | 1.8 m ahead, turned to the yaw, rises over 0.45 s | chamfered stone block |

- Power for hits and clashes = base + 1 if grounded (still or sliding) + 1 if moving faster than `MovingSpeed` 3 m/s.
- `MaxPerPlayer` 8 (spawning a ninth breaks that player's oldest), `MaxTotal` 64, `LifeSeconds` 60. A stone breaks below the world (y < -100 m) and when its owner leaves.
- Every stone is `SaveMode.Ignore`, so nothing is ever saved. Server quit destroys them all.

Grounded is done in Unity with rigidbody constraints. Still: `isKinematic` (synced). Sliding: non-kinematic, `FreezeRotation` only, gravity on, a frictionless physic material on its collider while it slides, slowed by `[EarthModifiers] SlideDeceleration`; it follows slopes, and it becomes Free if it has touched no ground for 0.2 s (it slid off a drop). (Ruling 22: a frozen height jammed uphill and floated off drops.) Rising: kinematic, moved up by the server each tick. Floating: `useGravity = false`, zero velocity.

### 3.5 Modifiers: our numbers (all `[Modifiers]`, server; impulses in N·s on the masses above)

| Modifier | On free | On grounded |
|---|---|---|
| Straight | 1800 along the yaw | 2300 along the yaw, slides |
| Kick | 1500 up | frees, then 1500 up |
| Stomp | 1500 down; landing: grounded, still | stays grounded |
| Uppercut | 900 forward + 900 up | frees, 1300 + 1300, spin 5 rad/s about the right axis |
| Parry | floating (3 s, or broken at 0.1 m/s) | frees, then floating |
| Hold | PD to the hand (2.4 table), let go at 8 m/s | slides while held (x 1500) |
| Flick | PID to both hands + turn | refused (grounded cannot be flicked) |
| Explode | marked: on breaking, radius `ExplodeRadius` 3 m, force 1000 | same |

- Range: as RUMBLE (2.4), plus server slack `RangeSlackMetres` 0.5 for lag.
- The player's own pushback from Straight (on their own game, `[Movement]`): `PushbackGrounded` 2.5 m/s back + 0.25 up, `PushbackFree` 1.7 back + 0.2 up, half in the air.
- Two Straights on one stone within `DoubleStraightSeconds` 0.4 explode it at once.

### 3.6 Movement (on the player's own game, `[Movement]`)
- Jump: `JumpSpeed` 6 m/s up (about 2.2 m with gravity -8).
- Dash: `DashSpeed` 8 m/s along the yaw + `DashLift` 0.5 up (the lift keeps the game from zeroing it).
- Dust: the game's `GroundPuffSmall` / `Ground Hit Puff` at the feet.

### 3.7 Hits, damage, knockback (server; `[Damage]`)
- A moving stone (relative speed at least `MinHitSpeed` 2 m/s) touching a creature or a player deals `DamagePerPower` 8 x power, Blunt, with cripple `CripplePerPower` 0.5 x power. Players are hurt only with `HurtPlayers` (true).
- Then the stone breaks (`BreakOnHit` true; one hit per stone). Each target is hit by one stone at most once per `HitCooldownSeconds` 0.5.
- The victim's game gets a knockback: speed `KnockbackSpeed` 4 m/s along the stone's velocity (an explosion: away from its centre), snapped to flat or 45 degrees, and hitstun `power x 0.7 / 3` s (a disc: 0).
- **The numbers 8 and 0.5 are guesses**: read a player's and a Turabada's max health in the first play-test and tune.
- Stone on stone: compare power. Equal, both break; otherwise the lesser breaks. Both are frozen for hitstop `0.06 + 0.02 x (powerA + powerB)` s first (kinematic), then the winner gets its old velocity back. Clients shake the stone's model while frozen.
- Explode breaking: players in the radius get knockback away from the centre (it stacks across explosions); creatures are staggered (cripple 1); loose rigidbodies get `AddExplosionForce`; our grounded stones are freed and pushed.

### 3.8 Network (message type 19, our only message)
The first byte is the kind, the second the wire version (`Wire.Version`, 1). Messages from another version are ignored with one warning. Floats are the game's `SerializeFloat`.

| Kind | Direction | Fields |
|---|---|---|
| 1 Summon | game → server | stone kind (byte), head position (3 floats), yaw degrees (float) |
| 2 Modify | game → server | modifier (byte), holding hand (byte: 0 right, 1 left), target entity `Identifier` (uint), head position (3 floats), yaw (float) |
| 10 Knockback | server → one game | velocity (3 floats), hitstun seconds (float) |
| 11 Applied | server → games within 80 m | entity `Identifier` (uint), event (byte: modifier id, or Broke 20, Hitstop 21, Exploded 22), position (3 floats) |

The client picks the modifier's target (what the player sees); the server re-checks it with its own copy of the head. A game without the mod never gets this far, because SyncLib refuses it at the join (missing prefabs).

### 3.9 Assets (all the game's own; survey of `C:\ATTUnity\ExportedProject`, 2026-11-22)
- **Network prefabs to borrow meshes and materials from** (always loaded at runtime, because `PrefabManager` holds every network prefab): Stone Boulder 01-03 (meshes `SandstoneBoulder_v01..03_LOD0`, about 0.66 x 0.82 x 0.56 m, material `Wooden_Atlas_m` on the `_Atlased` meshes), Copper/Iron ore boulders (about 1 m), Ruins Weight Orb 10kg (a stone ball), Gravestone Parts, Explosion Boulder parts (debris).
- **Materials for the chamfered blocks**: the ruins atlas the shrine tablet already uses (`Ruins_02_glow_m` / `ruins_02_diffuse`, bands in `StoneUv`), or the rock materials `rockStone_m`, `largeBoulder_02_m`, `caveRock_m`, `Cobblestone_01_m`. Whether each is loaded away from where it is placed must be checked at runtime (the material must be found by name).
- **Rise**: `Digging Dirt Spray Effect`, `Digging Dirt Smoke Puff Effect`, `Large Smoke Puff`; sound `Digging DirtFall Sound Set`, `Turabada Ground Tap Sounds`.
- **Break**: `Destroyed Rock` (`Rock Destroyed Visual`); sounds `Rock Destroyed Sounds`, `Boulder Breaking Sounds`.
- **Impacts**: `Mining Successful`, `Ground Hit Puff`, `Turabada Ground Slam Effect` (Stomp's landing); sounds `Turabada Rock Hit Sounds`, `Heavy Turabada Step Sounds`.
- **Explode**: `Shockwave Impact`, `Small Explosion`; sounds `Shockwave Sounds`, `Small Explosion`.
- **Movement dust**: `GroundPuffSmall`, `Ground Hit Puff`, `Climb Grab Dust`; whoosh `Power Hit Strike Sounds`.
- There is no rock-grinding loop sound and no terrain deformation. The rise is the stone moving up plus the dirt spray.
- Never copy ripped game files into the mod. Meshes and materials are found by name at runtime and shared, never changed.

## 4. Unknowns, each closed by a task
1. Does SyncLib 1.1.1-custom accept a prefab built in code, with no AssetBundle? (Task 1; fallback: a bundle built in `C:\ATTUnity\ExportedProject\Assets\Mods\Earthbending\` with only colliders and a rigidbody.)
2. Which local axes of the `Controller` transform are the palm normal and the fingers, per hand? (Task 3's capture.)
3. Is the server's `Controller.transform` current enough for Hold? (Task 13 logs its lag against the client's.)
4. Does a stone floor or a building's floor pass the natural-ground test? (Task 1 logs the ground under the player.)
5. Players' and creatures' health, to tune damage. (Task 8's play-test.)
