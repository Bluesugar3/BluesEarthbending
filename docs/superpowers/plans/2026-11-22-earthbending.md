# Blue's Earthbending Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A new PC mod in which a player strikes empty-hand poses on natural ground to raise five kinds of stone, move them with eight modifiers and throw themselves with Jump and Dash. It plays on a dedicated server.

**Architecture:** The player's own game detects poses (Unity-free `PoseMachine`) and sends claims on message type 19. The server checks every claim and owns every stone: five SyncLib network prefabs, rules in the Unity-free `Stones.cs`, forces, hits, damage. Clients only draw. Movement (Jump, Dash, pushback, knockback) is applied by each player's own game to its own `SmoothLocomotion.velocity`.

**Tech Stack:** C# net472 (Unity 2020.3 Mono), MelonLoader 0.7.3, Harmony, SyncLib 1.1.1-custom (installed DLL), the game's `Alta.*` / `Root.Township` assemblies, and a console test runner in the archery mod's `Check(...)` style. Python 3 (stdlib) for the pose fit.

**Spec:** `docs/superpowers/specs/2026-11-22-earthbending-design.md` (read it first: the numbers in every task come from its tables, sections 3.3-3.8).

## Global Constraints

- New folder `Earthbending\` (assembly, root namespace and folder `Earthbending`), csproj exactly `<Project Sdk="Microsoft.NET.Sdk" />`; tests in `Earthbending.Tests\` compiling only the Unity-free files `Earthbending\Poses.cs`, `Earthbending\Stones.cs`, `Earthbending\StoneBox.cs` (only `System.*` in them).
- `[assembly: MelonInfo(typeof(Earthbending.Main), "Blue's Earthbending", Main.Version, "Blue")]` + `[assembly: MelonGame("Alta", "A Township Tale")]`. Never rename Name or Author after the first server use (SyncLib's ModId).
- SyncLib CustomPrefabIds: `EarthDisc`, `EarthBall`, `EarthPillar`, `EarthCube`, `EarthWall`. Register only inside `NetworkPrefabRegistry.RegisterPrefabs` (subscribed in `OnInitializeMelon`); a null return on a client is normal. Never name a method `Register` in the `MelonMod` subclass.
- One network message, type 19: first byte the kind, second byte `Wire.Version` (1). Kinds and fields exactly as spec 3.8.
- The headless server never plays a sound or a pooled effect (they throw there). Every stone is `SaveMode.Ignore`.
- Every number and asset name is a setting in `UserData\Earthbending.cfg` (MelonPreferences on its own file), re-read within 2 s by one `LoadFromFile`. Server-only sections: `[Stones]`, `[Modifiers]`, `[Damage]`. Per game: `[Pose]`, `[Movement]`, `[Looks]`, `[Log]`.
- Log lines start `[Earth] `. Per-frame, per-hit and per-claim lines go behind `[Log] Debug` (false), with the check before the string is built.
- Art: only the game's own meshes, materials, effects and sounds, found by name at runtime and shared, never modified. Never put ripped files in the mod.
- Version starts `0.1.0`, bumped on every installed build (`Main.Version`).
- Commits: authored by the git user only. **No `Co-Authored-By` or "Generated with" lines** (owner's rule for this repo).
- Work in a git worktree: `.claude/worktrees/earthbending`, branch `earthbending` (other sessions commit in the shared checkout).
- Install only with the game and the server closed (`tasklist | grep -i "township"` empty): `dotnet build Earthbending -c Release -p:Deploy=true -p:DeployServer=true`. The archery mod's auto-install rule does not cover this mod: ask the owner before the first install (Task 1 moves `SyncLib.dll` from the server's `Mods\disabled` to `Mods`, which locks Quest players out of that server).
- Play-tests go to the owner as numbered tests of four short lines: **Do**, **You should see**, **Wrong if** (plus the build's version). At most about 7, no cfg edits by the owner, no log lines. Reading `Latest.log` is the agent's job afterwards.

## Review Focus

1. **Everyday hand movement with empty hands** (climbing a ladder, swimming, waving, reaching): a hand passing through a pose for a frame must fire nothing, and a structure pose without the base pose first must fire nothing. Tests in Task 4.
2. **Standing on the wrong ground** (a wooden floor in town, a cave's stone, one of your own stones, water): no structure and no movement; modifiers refused while standing there, allowed in the air. Machine test in Task 4; the server's spot check in Task 6.
3. **Left-handed players and other heights**: the mirrored pose fires the same move with the hands' roles swapped (Hold attaches to the top hand), and `BodyScale` moves every target. Tests in Task 4.
4. **A forged or lagging client**: spam, a summon claimed far from where the server sees the head, a target that is not a stone or is out of reach, a tenth stone. Each is refused or capped by the server rule, never trusted. Tests in Tasks 6 and 7.
5. **Stones outliving their reason**: the owner leaves, the stone falls out of the world, it lives past `LifeSeconds`, or the server quits. It breaks, and nothing is ever saved. Tests in Task 6 (`StoneLife`), quit and leave in its play check.

## File Structure

| File | Owns |
|---|---|
| `Earthbending/Earthbending.csproj` | `<Project Sdk="Microsoft.NET.Sdk" />` (everything else from `Directory.Build.props`). |
| `Earthbending/Main.cs` | `MelonMod`: version, start-up line, settings init, SyncLib hook, `OnUpdate`/`OnFixedUpdate` routing (server work before the headless return), quit clean-up. |
| `Earthbending/Cfg.cs` | The settings file, its 2-second poll, `Changed` event. |
| `Earthbending/Poses.cs` | Unity-free: `HandSample`, `HandSpec`, `PoseSpec`, `Move`, `BodyFrame`, `PoseMatch`, `PoseLibrary` (defaults + `poses.txt` parse), `PoseValues`, `PoseMachine`. |
| `Earthbending/Stones.cs` | Unity-free: `V3`, `StoneKind`, `StoneState`, `Modifier`, `StoneTable`, `StoneRules` (power, transitions, clash, hitstop, damage, knockback), `MoveRules` (pushback, dash), `Reach`, `Ledger`, `Caps`, `SummonCheck`, `ModifyCheck`, `StoneLife`, `SpawnPlan`, `Pd`, `Wire`, claim structs. |
| `Earthbending/StoneBox.cs` | Unity-free copy of `UvRect`, `StoneUv`, `StoneMesh`, `StoneBox` from `BluesArcheryOverhaul/Lectern.cs:1-185` (namespace changed; the archery file is not touched). |
| `Earthbending/Ground.cs` | The natural-ground test under a point. |
| `Earthbending/Prefabs.cs` | Code-built templates of the five stones, SyncLib registration, kind lookup by prefab hash. |
| `Earthbending/Net.cs` | Message 19 both ways (handlers, send helpers, events). |
| `Earthbending/Commands.cs` | Server console module `earth` (spawn, clear, push, list). |
| `Earthbending/Moves.cs` | Client: push the local player, hitstun, dust. |
| `Earthbending/Hands.cs` | Client: head, hands, body frame, empty hands, ground state; the pose capture recorder. |
| `Earthbending/Bend.cs` | Client: runs `PoseMachine` each frame, turns moves into pushes and claims, haptic tick, target pick. |
| `Earthbending/Server.cs` | Server: claims in, checks, spawns, the stone registry, tick. |
| `Earthbending/StoneBody.cs` | Server: one per stone instance: state, forces, modifiers, Hold/Flick drive, life. |
| `Earthbending/Hits.cs` | Server: contacts, damage, knock-down, knockback sends, clashes, hitstop, explode. |
| `Earthbending/StoneLook.cs` | Client: mesh and material on each stone, rise, break, modifier and hitstop looks. |
| `Earthbending/tools/fit_poses.py` | Turns a capture CSV into `poses.txt` and the four hand-axis settings. |
| `Earthbending.Tests/Earthbending.Tests.csproj`, `Program.cs`, `PoseTests.cs`, `StoneTests.cs` | Console runner: `ALL PASS (N)`. |

---

## Phase A: foundations (each task is a spike with its own check)

### Task 1: Mod skeleton, settings, ground probe, code-built stone prefabs, `earth spawn`

**Files:**
- Create: `Earthbending/Earthbending.csproj`, `Main.cs`, `Cfg.cs`, `Ground.cs`, `Prefabs.cs`, `Commands.cs`, `StoneLook.cs` (dress only), `StoneBox.cs`, `Stones.cs` (only `StoneKind`, `StoneTable` for now)
- Create: `Earthbending.Tests/Earthbending.Tests.csproj`, `Program.cs`, `StoneTests.cs`

**Interfaces:**
- Produces: `Main.Version` (`const string`, "0.1.0"); `Main.IsServer` (`bool`, dedicated server); `Cfg.Init(string fileName)`, `Cfg.Poll()`, `event Action Cfg.Changed`, `Cfg.Category(string section) -> MelonPreferences_Category`; `Ground.Under(Vector3 from, out RaycastHit hit) -> bool natural`; `enum StoneKind : byte { Disc, Ball, Pillar, Cube, Wall }`; `struct V3` in `Stones.cs` for every Unity-free file (`X, Y, Z`; `+ - *`, `Dot`, `Length`, `Normalized`, `MirrorX()`, `Zero`); `StoneTable.Of(StoneKind) -> StoneStats` (`Power` int, `Mass` float, `Size` V3, spec 3.4); `Prefabs.Of(StoneKind) -> NetworkPrefab?`, `Prefabs.KindOf(NetworkEntity) -> StoneKind?`; `Server.Spawn(StoneKind, Vector3 pos, Quaternion rot, Player? owner, Action<NetworkEntity>? done)` (a stub in `Prefabs.cs` until Task 6 creates `Server.cs`); `StoneLook.Dress(GameObject root, StoneKind kind)`.

- [ ] **Step 1: Write the failing tests** in `StoneTests.cs`, called from `Program.Main` with the archery runner's `Check(name, bool)` and `Near(a, b)`:

```csharp
Check("table wall", StoneTable.Of(StoneKind.Wall).Power == 3 && Near(StoneTable.Of(StoneKind.Wall).Mass, 400f));
Check("table disc", StoneTable.Of(StoneKind.Disc).Power == 0 && Near(StoneTable.Of(StoneKind.Disc).Size.Y, 0.14f));
var box = StoneBox.Build(1f, 1f, 1f, 0.05f, StoneUvSix());   // six StoneUv.Slate rects
Check("box bevelled", box.VertexCount == StoneBox.BevelledVertices && box.TriangleCount == StoneBox.BevelledTriangles);
```
`Program.Main` ends with `Console.WriteLine(failed == 0 ? $"ALL PASS ({total})" : $"FAILED {failed} of {total}")`, returns `failed`.

- [ ] **Step 2: Run to see it fail.** `dotnet run --project Earthbending.Tests -c Release` → build errors (types missing).

- [ ] **Step 3: Implement.**
  - `StoneBox.cs`: a verbatim copy of the four types, namespace `Earthbending`.
  - `StoneTable`: values from spec 3.4. Later tasks make them settings: `Cfg` entries `[Stones] <Kind>Power/Mass/Size` override the table at load.
  - `Cfg`: one MelonPreferences file `UserData\Earthbending.cfg`; the poll compares the file's write time every 2 s and calls the file's single `LoadFromFile`, then `Changed`.
  - `Ground.Under`: a raycast down 3 m on layers 8, 10 and 11. Natural = a hit whose collider has no `attachedRigidbody`.
  - `Prefabs`: for each kind, a template `GameObject` under an inactive, `DontDestroyOnLoad` holder (so `Instantiate` copies come out active). It carries a `Rigidbody` (mass from the table, `interpolation = Interpolate`), one collider (box, or a sphere for Ball) and an empty child `Model`. `this.RegisterCustomPrefab(template, "Earth<Kind>")` inside `RegisterPrefabs`.
  - `Server.Spawn` stub: `SpawnHelper.Spawn` as in `BluesArcheryTestKit/TestTools.cs:420-430`, with `SaveMode.Ignore` in the callback.
  - `StoneLook.Dress` (client, on every instance as it appears): a `MeshFilter` + `MeshRenderer` on `Model`.
    - Ball: the mesh and material of the network prefab "Stone Boulder 01" (found through `PrefabManager`).
    - Disc: the same mesh, squashed to the collider.
    - Pillar, Cube, Wall: `StoneBox.Build` at the collider's size, with that boulder's material for now (Task 10 picks the real look).
  - `Commands`: module `earth`, the `SkyfallCommands.cs` pattern. Subcommands: `spawn <kind> <player>` (1.5 m in front of that player's head, on the ground), `clear` (destroys every stone), `list`.
  - `Main`: the start-up line `[Earth] Blue's Earthbending v0.1.0 started (server|client)`. With `[Log] Debug`, every 2 s a line `[Earth] Ground: layer <n>, <collider name>, material <name>, natural <yes|no>` for the local player's feet.

- [ ] **Step 4: Run tests.** Same command → `ALL PASS (3)`. Then `dotnet build Earthbending -c Release` → 0 warnings, 0 errors.

- [ ] **Step 5: Install (owner's OK first, see Global Constraints) and check in the game.** Move `SyncLib.dll` from `<server>\Mods\disabled` to `<server>\Mods`; deploy; start the server and the game.
  - Agent check: both logs have the start-up line and SyncLib's registration of 5 prefabs.
  - Owner test 1. **Do:** stand in a field, then ask Claude to run `earth spawn cube <you>` (or type it in the server console). **You should see:** a grey stone cube appear about 1.5 m in front of you and sit on the ground. **Wrong if:** nothing appears, it is invisible, it falls through the ground, or the game closes.
  - Owner test 2. **Do:** throw a stick at it. **You should see:** it moves, and a friend sees it in the same place. **Wrong if:** it teleports back, or the two of you see it in different places.
  - Agent check after the owner walks a wooden floor and a stone floor: read the `Ground:` lines; if a floor reads `natural yes`, add a collider-name or material-name test to `Ground.Under`.
  - **If `RegisterCustomPrefab` rejects the code-built template:** build the same five templates as a bundle in `C:\ATTUnity\ExportedProject\Assets\Mods\Earthbending\` (colliders, rigidbody and an empty `Model` only), with **Build All Assets**. Copy the bundle file to `Earthbending/Resources/earthstones` and load it as `Template/Main.cs` does.

- [ ] **Step 6: Commit.** `git add Earthbending Earthbending.Tests && git commit -m "Earthbending 0.1.0: stone prefabs spawn from the server console"`

### Task 2: Message 19 both ways, and pushing the local player

**Files:**
- Create: `Earthbending/Net.cs`, `Earthbending/Moves.cs`
- Modify: `Earthbending/Commands.cs` (add `push`), `Earthbending/Stones.cs` (claim structs)

**Interfaces:**
- Consumes: `Cfg`, `Main.IsServer`.
- Produces:
  - `Wire.Version` (`const byte` 1) and `Wire.Kind` bytes `Summon 1, Modify 2, Knockback 10, Applied 11`, in `Stones.cs`.
  - `struct SummonClaim { StoneKind Kind; V3 Head; float Yaw; }`, `struct ModifyClaim { Modifier Mod; byte Hand; uint Target; V3 Head; float Yaw; }`, in `Stones.cs`. `enum Modifier : byte { Straight, Kick, Stomp, Uppercut, Parry, Hold, Flick, Explode }`.
  - `Net.Init()`, client senders `Net.SendSummon(SummonClaim)` and `Net.SendModify(ModifyClaim)`, server senders `Net.SendKnockback(Player victim, Vector3 velocity, float hitstun)` and `Net.SendApplied(uint entity, byte evt, Vector3 at)` (to every player within 80 m).
  - Server events `Net.SummonReceived(Player, SummonClaim)` and `Net.ModifyReceived(Player, ModifyClaim)`; client events `Net.KnockbackReceived(Vector3, float)` and `Net.AppliedReceived(uint, byte, Vector3)`.
  - `Moves.Push(Vector3 velocity)` (sets `SmoothLocomotion.velocity`, keeps `y >= 0.11`, calls `TreatAsGrounded()`), `Moves.Stun(float seconds)`, `Moves.Stunned` (bool).

- [ ] **Step 1: Implement `Net`.**
  - Server handler: a Harmony postfix on `ServerHandler.ConnectionCreated` sets `connection.SetHandler((MessageType)19, Read)`.
  - Client handler: a postfix on `JoinServerPipeline.ConnectionApprovedByServer`, which also keeps the `Connection`.
  - Pattern: `BluesArcheryOverhaul/JoinGate.cs:99-105, 209-215`; send with `connection.Send(null, (MessageType)19, (c, s) => …)`.
  - A wrong version or a short message is dropped, with one warning per connection.
  - The server maps the connection to its player with `connection.Player`. A claim from a connection with no player is dropped.
- [ ] **Step 2: Implement `Moves`** (spec 3.1, "Push the local player"). Nothing while downed.
- [ ] **Step 3: Add `earth push <player> <up m/s>`.** It sends `Knockback(velocity (0, up, 0), hitstun 0)`. The client's `KnockbackReceived` calls `Moves.Push` and `Moves.Stun`.
- [ ] **Step 4: Build.** `dotnet build Earthbending -c Release` → 0 warnings. Run the tests → still `ALL PASS (3)`.
- [ ] **Step 5: Install, check in the game.**
  - Owner test. **Do:** stand still, ask for `earth push <you> 6`. **You should see:** you are thrown about 2 m straight up and land with no damage. **Wrong if:** nothing happens, you are hurt on landing, or you stick in the air.
  - Agent check: no "Unsupported message type 19" in either log.
- [ ] **Step 6: Commit** `"Earthbending 0.1.1: its own message, and the server can throw a player"`.

### Task 3: Pose capture and fit (the owner's own poses, the hands' axes)

**Files:**
- Create: `Earthbending/Hands.cs` (capture part), `Earthbending/tools/fit_poses.py`

**Interfaces:**
- Produces:
  - Settings `[Pose] Capture` (false) and `[Pose] PalmAxisRight`, `FingerAxisRight`, `PalmAxisLeft`, `FingerAxisLeft`. Each is one of `+x -x +y -y +z -z`, the `Controller` transform's local axis; defaults come from the capture.
  - The file `UserData\Earthbending\poses.txt`: one line per pose:
    `<Name> | ax ay az  anx any anz  afx afy afz | bx by bz  bnx bny bnz  bfx bfy bfz | posTol palmDeg fingerDeg`.
    A is the right hand; `0 0 0` means the direction is not checked; `#` starts a comment.
- The capture CSV `UserData\Earthbending\pose-capture-<yyyyMMdd-HHmmss>.csv`. Columns: `n, t, headX, headY, headZ, yaw`, then for each of `right` and `left`: body-frame position (3), and the controller's local X, Y and Z axes in the body frame (9).

- [ ] **Step 1: Write the fit script's self-check first.** `python -I Earthbending/tools/fit_poses.py --selftest`.
  - It builds a synthetic capture of the 16 spec poses (spec 3.3), with the palm on the controller's -Y and the fingers on +Z (right), then mirrored (left), and random 3 cm / 8 degree noise.
  - It asserts that the fit recovers `-y`/`+z` for both hands and every pose position within 0.05 m.
  - Run it → fails (no fit yet).
- [ ] **Step 2: Implement `fit_poses.py`.** `fit_poses.py <capture.csv> [--out UserData-folder]`.
  - The snapshots are taken in the spec table's order: Base, Disc, Ball, Pillar, Cube, Wall, Straight, Kick, Stomp, Uppercut, Parry, Hold, Flick, Explode, Jump, Dash. A capture with a count other than 16 is refused, naming the count.
  - For each hand it picks the signed local axis whose agreement with the spec's palm, and separately its fingers, summed over all poses, is best. It prints the agreement in degrees.
  - It writes `poses.txt` from the measured positions and directions (default tolerances from spec 3.3) and prints the four axis settings.
  - Self-check → `SELFTEST PASS`.
- [ ] **Step 3: Implement the recorder in `Hands.cs`** (client, only with `Capture` on).
  - A snapshot is taken when both controllers have moved less than 1 cm over 0.6 s, and only after either hand moved 0.2 m since the last snapshot.
  - Each snapshot is one CSV row, two haptic pulses on both hands, and a line `[Earth] Capture: snapshot N`.
- [ ] **Step 4: Build, install, capture.** Claude sets `[Pose] Capture = true` in the owner's cfg first.
  - Owner test. **Do:** stand on grass. Make each of the 16 poses on the list Claude gives you, in order, holding each still until both hands buzz twice. **You should see:** a double buzz after about half a second of stillness, once per pose. **Wrong if:** no buzz, or buzzes while you are moving.
  - Agent: run the fit on the CSV. Put the axis settings in as defaults in `Hands.cs`. Copy `poses.txt` into the mod as `PoseLibrary`'s defaults (Task 4) and keep the file. Switch `Capture` off. Record the fit's agreement numbers in the commit message.
- [ ] **Step 5: Commit** `"Earthbending: pose capture and fit (axes palm <a>/<b>, fingers <c>/<d>)"`.

## Phase B: first playable (Pillar, Straight, Jump)

### Task 4: The pose engine (Unity-free)

**Files:**
- Create: `Earthbending/Poses.cs`, `Earthbending.Tests/PoseTests.cs`
- Modify: `Earthbending.Tests/Earthbending.Tests.csproj` (compile `Poses.cs`)

**Interfaces:**
- Consumes: `V3` (`Stones.cs`, Task 1).
- Produces:
  - `struct HandSample { V3 Pos, Palm, Fingers; }` (body frame).
  - `struct HandSpec { V3 Pos, Palm, Fingers; float PosTol, PalmDeg, FingerDeg; }` (a zero `Palm`/`Fingers` = not checked).
  - `enum Move : byte { None, Base, Disc, Ball, Pillar, Cube, Wall, Straight, Kick, Stomp, Uppercut, Parry, Hold, Flick, Explode, Jump, Dash }`.
  - `struct PoseSpec { Move Move; HandSpec A, B; }`.
  - `BodyFrame.ToBody(V3 head, float yawDeg, V3 worldPos, V3 worldPalm, V3 worldFingers, float bodyScale) -> HandSample`.
  - `PoseMatch.Match(in PoseSpec s, in HandSample right, in HandSample left, out bool rightIsA, out float score) -> bool`. It tries A = right as written and A = left with the spec mirrored; score = the largest of (position error / tolerance, angle / cone) over every checked part, lower is better.
  - `PoseLibrary.Defaults` (`PoseSpec[]`, from Task 3's `poses.txt`, or the spec 3.3 table if the capture was not done), `PoseLibrary.Parse(string[] lines) -> PoseSpec[]` (a bad line is skipped and named in `out List<string> errors`).
  - `struct PoseValues { MinHoldSeconds 0.05, RearmSeconds 0.1, StructureWindowSeconds 1.2, MoveCooldownSeconds 0.6, float[] Cooldown (by Move, spec 2.2 and 2.4) }`.
  - `PoseMachine(PoseSpec[], PoseValues)` with `Move Step(double now, in HandSample right, in HandSample left, bool handsEmpty, bool grounded, bool onNatural, bool stunned, out bool rightIsA)`. Kinds: `PoseMachine.IsStructure(Move)`, `IsModifier`, `IsMovement`.

- [ ] **Step 1: Write the failing tests** (`PoseTests.cs`). `At(move, mirrored)` builds the exact `HandSample` pair from a spec; `Run(m, samples, seconds)` steps at 90 Hz and returns the moves fired.

```csharp
Check("base matches", PoseMatch.Match(Spec(Move.Base), R(Move.Base), L(Move.Base), out _, out float s) && s < 0.01f);
Check("mirrored straight is straight, left is A", PoseMatch.Match(Spec(Move.Straight), At(Move.Straight, true).r, At(Move.Straight, true).l, out bool rA, out _) && !rA);
Check("palm 60 off fails", !PoseMatch.Match(Spec(Move.Parry), Turned(R(Move.Parry), 60f), L(Move.Parry), out _, out _));
Check("pos 0.25 off fails", !PoseMatch.Match(Spec(Move.Cube), Shifted(R(Move.Cube), 0.25f), L(Move.Cube), out _, out _));
Check("frame turns with yaw", Same(BodyFrame.ToBody(head, 90f, RotY(p, 90f), RotY(n, 90f), RotY(f, 90f), 1f), BodyFrame.ToBody(head, 0f, p, n, f, 1f)));   // a pose struck facing east reads as the same pose facing north
Check("body scale", Near(BodyFrame.ToBody(V3.Zero, 0f, new V3(0, -0.9f, 0), up, fwd, 0.9f).Pos.Y, -1f));
Check("one frame sweep fires nothing", Run(machine, OneFrame(Move.Jump), 0.5).Count == 0);
Check("held jump fires once", Run(machine, Hold(Move.Jump, 0.3), 0.3).SequenceEqual(new[] { Move.Jump }));
Check("structure needs base", Run(machine, Hold(Move.Pillar, 0.3), 0.3).Count == 0);
Check("base then pillar", Run(machine, Seq(Hold(Move.Base, 0.2), Hold(Move.Pillar, 0.2)), 0.4).SequenceEqual(new[] { Move.Pillar }));
Check("base too long ago", Run(machine, Seq(Hold(Move.Base, 0.2), Idle(1.3), Hold(Move.Pillar, 0.2)), 1.7).Count == 0);
Check("pillar twice needs base twice", /* base, pillar, pillar -> one Pillar */);
Check("rearm: hold straight 1 s fires once; leave 0.15 s, re-enter fires again after cooldown");
Check("cooldown: straight, leave, straight within 0.5 s fires once");
Check("hands full fires nothing", /* handsEmpty false */);
Check("other ground: no movement, no modifier", /* grounded true, onNatural false: Jump and Straight fire nothing */);
Check("in the air: modifier yes, movement no, structure no", /* grounded false */);
Check("stunned fires nothing", /* stunned true */);
Check("priority: armed structure beats a modifier matching the same frame");
Check("parse: round-trip of Defaults through text equals Defaults; a bad line is skipped and named");
```
Each commented `Check` is one test the implementer writes in the same style. The comment gives its inputs and the expected moves.

- [ ] **Step 2: Run** → build errors.
- [ ] **Step 3: Implement `Poses.cs`** to the interface above, with rules from spec 3.3. The pose's "started matching" time is kept per pose. A structure fires only if `Base` matched within `StructureWindowSeconds` before it started matching.
- [ ] **Step 4: Run** → `ALL PASS (N)` (N = 3 + the new checks).
- [ ] **Step 5: Commit** `"Earthbending: the pose engine"`.

### Task 5: The game runs the poses; Jump

**Files:**
- Modify: `Earthbending/Hands.cs` (read), `Earthbending/Main.cs` (client tick), `Earthbending/Moves.cs` (dust)
- Create: `Earthbending/Bend.cs`

**Interfaces:**
- Consumes: `PoseMachine`, `PoseLibrary`, `Moves.Push`, `Moves.Stunned`, `Ground.Under`.
- Produces:
  - `Hands.Read(out HandSample right, out HandSample left, out Vector3 head, out float yawDeg) -> bool`, using the axis settings from Task 3.
  - `Hands.Empty() -> bool` (both `!Controller.Interactor.IsInteracting`).
  - `Bender.CanBend(PlayerController pc) -> bool`, today `Hands.Empty()`: the one place the future gauntlet changes (in `Bend.cs`; `Bend.Tick` passes it as `handsEmpty`).
  - `Hands.GroundState(out bool grounded, out bool natural)` (`SmoothLocomotion.IsGrounded`, plus `Ground.Under` at the feet).
  - `Bend.Tick()` (each frame, client), `event Action<Move, bool> Bend.Fired` (move, rightIsA).
  - `Moves.Dust(Vector3 at)` (`[Looks] DustEffect` "GroundPuffSmall").

- [ ] **Step 1: Implement.**
  - `Bend.Tick` reads the hands and the ground, steps the machine, and raises `Fired`.
  - On Jump it calls `Moves.Push((0, [Movement] JumpSpeed 6, 0))` and `Moves.Dust`.
  - On every fired move, one haptic pulse (`[Looks] PoseHaptic` 0.4, 0.05 s) on both hands.
  - With `[Log] Debug`, one line per fired move: move, rightIsA, score.
  - `poses.txt` in `UserData\Earthbending\` overrides the defaults when present, reloaded on `Cfg.Changed`.
- [ ] **Step 2: Build** → 0 warnings; tests `ALL PASS`.
- [ ] **Step 3: Install, play.**
  - Owner test 1. **Do:** on grass, empty hands, make the Jump pose and hold it. **You should see:** a buzz, a puff of dust, and you jump about your own height. **Wrong if:** no jump, several jumps from one pose, or a jump while just walking or climbing.
  - Owner test 2. **Do:** the same on a wooden floor in town. **You should see:** nothing. **Wrong if:** you jump.
- [ ] **Step 4: Commit** `"Earthbending 0.1.x: poses run in the game; Jump"`.

### Task 6: Summoning stones on the server (Pillar first)

**Files:**
- Modify: `Earthbending/Stones.cs`, `Earthbending.Tests/StoneTests.cs`, `Earthbending/Bend.cs`, `Earthbending/StoneLook.cs`, `Earthbending/Main.cs`
- Create: `Earthbending/Server.cs`, `Earthbending/StoneBody.cs` (move `Server.Spawn` out of `Prefabs.cs`)

**Interfaces:**
- Consumes: `Net.SummonReceived`, `Net.SendSummon`, `Prefabs.Of`, `StoneTable`, `Ground.Under`.
- Produces:
  - `enum StoneState : byte { Rising, Still, Sliding, Free, Stomped, Floating, Held, Flicked }`.
  - `StoneRules.IsGrounded(StoneState) -> bool` (Rising, Still, Sliding, Stomped).
  - `Ledger` (per player: `bool TrySpend(ulong account, int move, double now, float cooldown)`, `void Forget(ulong account)`).
  - `enum SummonRefusal { None, Cooldown, HeadFar, NotNatural, KindOff, NoPlayer }`, `SummonCheck.Decide(in SummonClaim c, V3 serverHead, bool spotNatural, bool kindEnabled, bool cooldownOk, float headSlack) -> SummonRefusal`.
  - `SpawnPlan.For(StoneKind k, V3 head, float yawDeg, float groundY) -> (V3 start, V3 end, float riseSeconds, float yawDeg)` (spec 3.4 distances; start = end lowered by the kind's height).
  - `StoneLife.ShouldBreak(float age, float life, float y, bool ownerOnline) -> bool`.
  - `enum CapAction { Spawn, BreakOldest, Refuse }`, `Caps.Decide(int ownedByPlayer, int total, int maxPerPlayer, int maxTotal) -> CapAction`.
  - `StoneBody` (MonoBehaviour, server): `Kind`, `Owner` (ulong account), `State`, `Born` (double), `Break(string why)` (sends `Applied(Broke)`, then `SceneDestroy`).
  - `Server.Stones` (`Dictionary<uint, StoneBody>` by entity `Identifier`), `Server.Tick()`, `Server.OwnedBy(ulong) -> List<StoneBody>` (oldest first).
  - Settings: `[Stones] Enabled` ("Pillar"; Task 9 widens it), `MaxPerPlayer` 8, `MaxTotal` 64, `LifeSeconds` 60, `HeadSlackMetres` 1.0.

- [ ] **Step 1: Failing tests** (`StoneTests.cs`):

```csharp
Check("summon ok", SummonCheck.Decide(Claim(Pillar, head), head, true, true, true, 1f) == SummonRefusal.None);
Check("head claimed 3 m off", SummonCheck.Decide(Claim(Pillar, head + 3*X), head, true, true, true, 1f) == SummonRefusal.HeadFar);
Check("not natural", SummonCheck.Decide(Claim(Pillar, head), head, false, true, true, 1f) == SummonRefusal.NotNatural);
Check("kind off", SummonCheck.Decide(Claim(Wall, head), head, true, false, true, 1f) == SummonRefusal.KindOff);
Check("cooldown 0.3", ledger.TrySpend(7, (int)Move.Pillar, 10.0, 0.3f) && !ledger.TrySpend(7, (int)Move.Pillar, 10.2, 0.3f) && ledger.TrySpend(7, (int)Move.Pillar, 10.31, 0.3f));
Check("cooldown per player", ledger.TrySpend(8, (int)Move.Pillar, 10.2, 0.3f));
Check("plan: cube 1.5 m ahead along yaw 90", Near(SpawnPlan.For(Cube, V3.Zero, 90f, -1.6f).end.X, 1.5f) && Near(SpawnPlan.For(Cube, V3.Zero, 90f, -1.6f).end.Y, -1.1f));
Check("plan: starts underground", SpawnPlan.For(Pillar, V3.Zero, 0f, -1.6f).start.Y < -1.6f - 0.9f);
Check("life: old", StoneLife.ShouldBreak(61f, 60f, 0f, true));
Check("life: void", StoneLife.ShouldBreak(1f, 60f, -101f, true));
Check("life: owner gone", StoneLife.ShouldBreak(1f, 60f, 0f, false));
Check("life: fine", !StoneLife.ShouldBreak(1f, 60f, 0f, true));
Check("cap: room", Caps.Decide(3, 10, 8, 64) == CapAction.Spawn);
Check("cap: ninth breaks oldest", Caps.Decide(8, 10, 8, 64) == CapAction.BreakOldest);
Check("cap: server full", Caps.Decide(3, 64, 8, 64) == CapAction.Refuse);
Check("cap: server full but player at max breaks own", Caps.Decide(8, 64, 8, 64) == CapAction.BreakOldest);
```
- [ ] **Step 2: Run** → fails.
- [ ] **Step 3: Implement the rules**, then the game side:
  - Client: `Bend` sends `Net.SendSummon` on an armed structure move.
  - Server: `Net.SummonReceived` → ledger, ground cast under `SpawnPlan`'s end, `SummonCheck`. A refusal is one Debug line naming the reason.
  - `Caps.Decide` before spawning: `BreakOldest` breaks that player's oldest (`Server.OwnedBy`), `Refuse` is a Debug line.
  - `Server.Spawn` at the start point, kinematic and `State = Rising`. `StoneBody.FixedUpdate` moves it to the end over `riseSeconds` (`MovePosition`), then sets `Still`.
  - `Server.Tick` breaks by `StoneLife` (owner online = their `Player` is still connected). Quit breaks everything.
- [ ] **Step 4: Client looks** (`StoneLook`).
  - When a stone appears: `[Looks] RiseEffect` ("Digging Dirt Spray Effect;Digging Dirt Smoke Puff Effect") at its end point's ground, and `RiseSound` ("Digging DirtFall Sound Set").
  - On `Applied(Broke)`: `BreakEffect` ("Destroyed Rock") and `BreakSound` ("Rock Destroyed Sounds;Boulder Breaking Sounds") at the given position.
  - An effect or sound name that is not found: one warning, nothing drawn.
- [ ] **Step 5: Tests `ALL PASS`, build 0 warnings, install, play.**
  - Owner test 1. **Do:** on grass, the base pose, then Pillar. **You should see:** dirt sprays and a stone pillar rises in front of you in about a third of a second, and stays. **Wrong if:** nothing rises, it rises under you, it falls over, or it appears without the dirt.
  - Owner test 2. **Do:** make 10 pillars. **You should see:** the first one crumbles when the ninth appears. **Wrong if:** more than 8 stand at once.
  - Owner test 3. **Do:** leave the server and rejoin. **You should see:** your pillars are gone. **Wrong if:** any are still there.
  - Agent checks in the server log: no refusal lines except `HeadFar` while moving fast; no exceptions; after a server restart, nothing saved (no stone in the world).
- [ ] **Step 6: Commit** `"Earthbending 0.2.0: Pillar rises from the ground"`.

### Task 7: Straight (target, claim, check, push, pushback)

**Files:**
- Modify: `Earthbending/Stones.cs`, `StoneTests.cs`, `Bend.cs`, `Server.cs`, `StoneBody.cs`

**Interfaces:**
- Consumes: `Net.SendModify`, `Net.ModifyReceived`, `Server.Stones`, `Moves.Push`.
- Produces:
  - `Reach.InReach(V3 head, float yawDeg, V3 boxMin, V3 boxMax, float feetY, float reach, float maxHeight, float slack) -> bool` (spec 2.4).
  - `Reach.Pick(V3 head, float yawDeg, IList<(uint id, V3 min, V3 max)> stones, float feetY, float reach, float maxHeight) -> uint` (0 = none; the smallest angle from the flat forward, ties to the nearer).
  - `enum ModifyRefusal { None, Cooldown, NoStone, OutOfReach, NotAllowed }`, `ModifyCheck.Decide(...)`.
  - `StoneRules.After(StoneState s, Modifier m) -> StoneState`, `StoneRules.CanApply(StoneState s, Modifier m) -> bool`.
  - `StoneBody.Apply(Modifier m, Player by, int hand, Vector3 flatForward)`.
  - `MoveRules.Pushback(float yawDeg, bool targetGrounded, bool airborne, float grounded, float free, float lift) -> V3` (backward along the yaw; half when airborne).
  - Settings `[Modifiers] StraightGrounded` 2300, `StraightFree` 1800, `Reach` 2.5, `MaxHeight` 2.4, `RangeSlackMetres` 0.5, `DoubleStraightSeconds` 0.4; `[Movement] PushbackGrounded` 2.5, `PushbackFree` 1.7, `PushbackLift` 0.25.

- [ ] **Step 1: Failing tests:**

```csharp
Check("reach: cube 2 m ahead", Reach.InReach(head, 0f, Box(0, -1.1f, 2f, 1f), feet, 2.5f, 2.4f, 0f));
Check("reach: behind", !Reach.InReach(head, 0f, Box(0, -1.1f, -1.5f, 1f), feet, 2.5f, 2.4f, 0f));
Check("reach: 3.2 m", !Reach.InReach(head, 0f, Box(0, -1.1f, 3.7f, 1f), feet, 2.5f, 2.4f, 0f));
Check("reach: slack lets 2.9 m in", Reach.InReach(head, 0f, Box(0, -1.1f, 3.4f, 1f), feet, 2.5f, 2.4f, 0.5f));
Check("reach: too high", !Reach.InReach(head, 0f, Box(0, 1.2f, 1f, 1f), feet, 2.5f, 2.4f, 0f));
Check("pick: the one most in front", Reach.Pick(head, 0f, new[] { (1u, ...left 40 deg), (2u, ...ahead) }, feet, 2.5f, 2.4f) == 2u);
Check("straight on still -> sliding", StoneRules.After(StoneState.Still, Modifier.Straight) == StoneState.Sliding);
Check("straight on free -> free", StoneRules.After(StoneState.Free, Modifier.Straight) == StoneState.Free);
Check("modify: not a stone", ModifyCheck.Decide(/* target missing */) == ModifyRefusal.NoStone);
Check("modify: out of reach with slack", ModifyCheck.Decide(/* 3.3 m */) == ModifyRefusal.OutOfReach);
Check("pushback grounded, yaw 0", Near(MoveRules.Pushback(0f, true, false, 2.5f, 1.7f, 0.25f).Z, -2.5f) && Near(MoveRules.Pushback(0f, true, false, 2.5f, 1.7f, 0.25f).Y, 0.25f));
Check("pushback airborne halves", Near(MoveRules.Pushback(0f, false, true, 2.5f, 1.7f, 0.25f).Z, -0.85f));
```
- [ ] **Step 2: Run** → fails. **Step 3: Implement.**
  - Client: on a modifier move, `Reach.Pick` over the stones this game sees (`Prefabs.KindOf` on live entities within 4 m). No target: nothing is sent and there is no pushback.
  - Otherwise send `Modify` and, for Straight, push yourself (`Moves.Push(MoveRules.Pushback(...))`, with `targetGrounded` from the stone's kinematic flag as this game sees it).
  - Server: ledger, then `ModifyCheck` with its own head, then `StoneBody.Apply`.
  - Straight: `AddForce(flatForward * impulse, ForceMode.Impulse)`. On a grounded stone, `isKinematic = false`, constraints `FreezePositionY | FreezeRotation`, `Sliding`; back to `Still` (kinematic) under 0.1 m/s.
  - Record the time of the Straight; the double Straight is Task 12.
- [ ] **Step 4: `ALL PASS`, install, play.**
  - Owner test 1. **Do:** raise a pillar, then Straight at it. **You should see:** it slides away along the ground, and you are pushed back a little. **Wrong if:** it does not move, it tips over, it flies up, or you are not pushed.
  - Owner test 2. **Do:** Straight with no stone in front of you. **You should see:** nothing. **Wrong if:** you are pushed back.
- [ ] **Step 5: Commit** `"Earthbending 0.3.0: Straight"`.

### Task 8: Hits, damage, knockback, stone on stone (Phase B play-test)

**Files:**
- Create: `Earthbending/Hits.cs`
- Modify: `Earthbending/Stones.cs`, `StoneTests.cs`, `StoneBody.cs`, `Bend.cs` (knockback in, stun)

**Interfaces:**
- Produces:
  - `StoneRules.Power(StoneKind k, StoneState s, float speed, float movingSpeed) -> int` (spec 3.4).
  - `enum ClashResult { BothBreak, ABreaks, BBreaks }`, `StoneRules.Clash(int a, int b) -> ClashResult`.
  - `StoneRules.Damage(int power, float relSpeed, float perPower, float minSpeed) -> float` (0 below `minSpeed` or at power 0).
  - `StoneRules.KnockDir(V3 v) -> V3` (unit; flat or 45 degrees up, whichever is nearer).
  - `StoneRules.HitstunSeconds(StoneKind k, int power) -> float` (Disc 0, else power x 0.7 / 3).
  - `Hits.OnContact(StoneBody stone, Collision c)` (server).
  - Settings `[Damage] HurtPlayers` true, `DamagePerPower` 8, `CripplePerPower` 0.5, `MinHitSpeed` 2, `MovingSpeed` 3, `KnockbackSpeed` 4, `BreakOnHit` true, `HitCooldownSeconds` 0.5.

- [ ] **Step 1: Failing tests:** `Power(Cube, Still, 0) == 3`, `Power(Disc, Free, 5) == 1`, `Power(Disc, Free, 1) == 0`, `Clash(2,2) == BothBreak`, `Clash(3,1) == BBreaks`, `Damage(2, 1.5f, 8, 2) == 0`, `Damage(2, 5f, 8, 2) == 16`, `KnockDir((1,0.1,0))` flat, `KnockDir((1,0.8,0))` 45 degrees, `HitstunSeconds(Disc, 1) == 0`, `Near(HitstunSeconds(Wall, 3), 0.7f)`.
- [ ] **Step 2: Run** → fails. **Step 3: Implement `Hits`.**
  - `StoneBody.OnCollisionEnter` (server) → `Hits.OnContact`.
  - Find the `HealthObject` the way `BluesArcheryOverhaul/Targets.cs:61` does (the entity's `PrefabRoot`). A player is a `HealthObject` under a `PlayerController`.
  - Damage when relative speed ≥ `MinHitSpeed`: `new ImpactHit(damage, cripple, 1, 1, null, ImpactType.Blunt, point, normal, …)` and `health.DetectImpact(hit)`, as `Explosive.Trigger` (`_decomp/Explosive.cs:142-205`). Players only with `HurtPlayers`. Creatures that have `Agent.Stun` get stunned for `power x 0.3` s.
  - A player victim: `Net.SendKnockback(victim, KnockDir(stone velocity) * KnockbackSpeed, HitstunSeconds)`.
  - Then `Break("hit")` if `BreakOnHit`.
  - A stone on a stone: `Clash` on both powers; the loser or losers `Break("clash")`.
  - One hit per target per `HitCooldownSeconds`.
  - Client: `KnockbackReceived` → `Moves.Push` + `Moves.Stun`; `Bend` passes `Moves.Stunned` to the machine.
  - Debug line per hit: stone, target, power, speed, damage.
- [ ] **Step 4: `ALL PASS`, install. Play-test 1** (send the owner these, numbered):
  1. **Do:** Pillar, then Straight it into a Turabada or Gotera. **You should see:** it is hit and knocked down; the pillar crumbles. **Wrong if:** no damage, or the pillar survives.
  2. **Do:** a friend (or your second account) stands 2 m ahead; Pillar + Straight at them. **You should see:** they take damage and are pushed away along the path. **Wrong if:** no damage, or no push.
  3. **Do:** your friend Straights a pillar into one of yours. **You should see:** both crumble. **Wrong if:** one survives.
  4. **Do:** stand still, a friend Straights a pillar into you; try a pose at once. **You should see:** no pose works for a moment. **Wrong if:** you can jump straight away.
  - Agent: from the Debug lines, read a player's and a Turabada's health before and after a hit; tune `DamagePerPower` so a cube (power 3 grounded) takes about a third of a player's health; record it in the commit.
- [ ] **Step 5: Commit** `"Earthbending 0.4.0: stones hurt, knock back and clash"`.

## Phase C: the other structures, and their look

### Task 9: Disc, Ball, Cube, Wall

**Files:** Modify `Stones.cs`, `StoneTests.cs`, `Server.cs`, `StoneBody.cs`.

**Interfaces:**
- Produces: `SpawnPlan.For` for every kind (spec 3.4), plus `SpawnPlan.Launch(StoneKind) -> (V3 first, float delay, V3 second)` (Disc: up 300, then after 0.12 s 1400 along the yaw; Ball: up 5 m/s as velocity; others none). `[Stones] Enabled` defaults to all five.

- [ ] **Step 1: Failing tests:** `For(Wall, …, yaw 30).yawDeg == 30`; `Wall` ends 1.8 m ahead; `Disc` ends 1.2 m ahead at ground y + 0.5; `Launch(Disc).delay == 0.12f`; `Launch(Cube)` none; the Ball starts with its top under the ground; summoning a Cube where a stone already stands: the old stone, if grounded, becomes `Free` and gets `[Stones] SummonUnderLift` 4 m/s up (`StoneRules.SummonUnder(StoneState) -> (StoneState, float lift)`).
- [ ] **Step 2: Run** → fails. **Step 3: Implement** (Disc and Ball spawn `Free`, non-kinematic; Cube and Wall rise like Pillar; Wall turned to the yaw). **Step 4: `ALL PASS`, install.**
  - Owner test. **Do:** base pose then each of Disc, Ball, Cube, Wall. **You should see:** a flat stone lifts and flies forward; a round stone pops up to chest height and drops; a cube and a wide wall rise and stay. **Wrong if:** the wrong one appears, or a pose fires another move.
- [ ] **Step 5: Commit** `"Earthbending 0.5.0: Disc, Ball, Cube, Wall"`.

### Task 10: The stones' look (art; Fable)

**Files:** Modify `StoneLook.cs`; create `Earthbending/Art/` (previews only, not shipped).

**Interfaces:** `StoneLook.Dress` unchanged; new settings `[Looks] <Kind>Mesh` (a network prefab name whose mesh is used, or `block`), `<Kind>Material`, `BlockBevel` 0.06, `BlockUv` (`slate|bricks|meander|whole`).

- [ ] **Step 1: Find what is loaded at runtime.** In a game, log every candidate material from spec 3.9 found by `Resources.FindObjectsOfTypeAll<Material>()`: at the archery shrine, in a field far from ruins, and in a cave. Only materials found in all three may be defaults (the rest stay possible settings).
- [ ] **Step 2: Previews.** In the Unity editor (`ub open`, `ub call …`), build the five stones three ways from the game's assets:
  - (a) chamfered blocks on the ruins atlas (`StoneUv` bands, the tablet's material);
  - (b) chamfered blocks on a rock material (`rockStone_m` / `largeBoulder_02_m`);
  - (c) the game's boulder meshes scaled to each collider.
  Render a sheet with the game's own boulders and ruins beside them. Show the owner; they pick per stone. (Private reference material: the sheet is for the owner's eyes only.)
- [ ] **Step 3: Implement the pick** as `[Looks]` defaults. A material or mesh not found falls back to the boulder's, with one warning.
- [ ] **Step 4: Install.** Owner test: **Do:** raise all five. **You should see:** stones that look like they belong to the game (the look you picked). **Wrong if:** flat white, pink, or stretched textures.
- [ ] **Step 5: Commit** `"Earthbending 0.5.x: the stones' look"`.

## Phase D: modifiers

### Task 11: Kick, Stomp, Uppercut

**Files:** Modify `Stones.cs`, `StoneTests.cs`, `StoneBody.cs`, `StoneLook.cs`.

- [ ] **Step 1: Failing tests** on `StoneRules.After`:
  - `(Still, Kick) == Free`, `(Free, Kick) == Free`;
  - `(Free, Stomp) == Stomped`, `(Still, Stomp) == Still`;
  - `(Still, Uppercut) == Free`;
  - `StoneRules.Landed(Stomped) == Still`;
  - `StoneRules.Impulse(Modifier, bool grounded) -> (V3 flat, float up, float spin)` gives Kick `(0, 1500, 0)`, Uppercut free `(900, 900, 0)`, Uppercut grounded `(1300, 1300, 5)`, Stomp `(0, -1500, 0)`.
  - Settings `[Modifiers] KickUp`, `StompDown`, `UppercutFree`, `UppercutGrounded`, `UppercutSpin`.
- [ ] **Step 2: Run** → fails. **Step 3: Implement.**
  - Stomp: a `Stomped` stone keeps only vertical movement (`FreezePositionX | FreezePositionZ | FreezeRotation`). When it touches natural ground it becomes `Still` (kinematic, velocity zeroed) and the server sends `Applied(Stomp)` then (at the landing, not at the pose).
  - Uppercut's spin is about the flat right axis.
  - Client: `Applied(Stomp)` plays `[Looks] StompEffect` ("Turabada Ground Slam Effect") and `StompSound` ("Heavy Turabada Step Sounds").
- [ ] **Step 4: `ALL PASS`, install.** Owner tests:
  1. **Do:** Cube, then Kick. **You should see:** it hops up and falls.
  2. **Do:** Ball, then Stomp while it is in the air. **You should see:** it slams down and sits there with a thud and dust.
  3. **Do:** Pillar, then Uppercut. **You should see:** it flies up and forward, tumbling.
  - **Wrong if:** any of them move the wrong way.
- [ ] **Step 5: Commit** `"Earthbending 0.6.0: Kick, Stomp, Uppercut"`.

### Task 12: Parry and Explode

**Files:** Modify `Stones.cs`, `StoneTests.cs`, `StoneBody.cs`, `Hits.cs`, `StoneLook.cs`.

**Interfaces:**
- Produces: `StoneRules.ParryEnds(float age, float speed) -> bool` (age > 3 or speed > 0.1); `StoneBody.Exploding` (bool, permanent); `Hits.Explode(Vector3 centre, float radius, float force, StoneBody? source)`; `StoneRules.ExplodeShare(float dist, float radius) -> float` (1 inside half the radius, falling to 0.25 at the edge, 0 outside); settings `[Modifiers] ParrySeconds` 3, `ParryBreakSpeed` 0.1, `ExplodeRadius` 3, `ExplodeForce` 1000.

- [ ] **Step 1: Failing tests:**
  - `After(Still, Parry) == Floating`, `After(Free, Parry) == Floating`;
  - `ParryEnds(3.1f, 0) && ParryEnds(1f, 0.2f) && !ParryEnds(1f, 0.05f)`;
  - `ExplodeShare(1f, 3f) == 1`, `ExplodeShare(3f, 3f) == 0.25f`, `ExplodeShare(3.1f, 3f) == 0`;
  - a double Straight inside `DoubleStraightSeconds` → `StoneRules.DoubleStraight(double last, double now, float window) == true`.
- [ ] **Step 2: Run** → fails. **Step 3: Implement.**
  - Parry: zero velocity, `useGravity = false`, non-kinematic, `Floating`; at `ParryEnds` back to `Free` with gravity.
  - Explode marks the stone (`Applied(Explode)`: clients draw `[Looks] ExplodeMarkEffect` cracks, one look per stone). When a marked stone breaks for any reason, `Hits.Explode` runs:
    - players in the radius get knockback away from the centre at `KnockbackSpeed x share`;
    - creatures get cripple 1;
    - loose rigidbodies get `AddExplosionForce`;
    - our grounded stones in the radius go `Free` with the push.
    - `Applied(Exploded)`: clients play `ExplodeEffect` ("Shockwave Impact") and `ExplodeSound` ("Shockwave Sounds").
  - A double Straight explodes the stone at once at `ExplodeForce`.
- [ ] **Step 4: `ALL PASS`, install.** Owner tests:
  1. **Do:** Ball, Straight it at a friend, then Parry it mid-air. **You should see:** it stops dead and hangs there, then drops after about 3 s.
  2. **Do:** Cube, Explode, then Straight it into a wall of yours. **You should see:** both break with a blast that pushes you and anyone near back.
  3. **Do:** Straight the same pillar twice fast. **You should see:** it bursts.
  - **Wrong if:** a parried stone drifts, or the blast hurts but does not push.
- [ ] **Step 5: Commit** `"Earthbending 0.7.0: Parry and Explode"`.

## Phase E: Hold and Flick

### Task 13: Hold

**Files:** Modify `Stones.cs`, `StoneTests.cs`, `StoneBody.cs`.

**Interfaces:**
- Produces: `Pd.HoldGains(float mass) -> (float p, float dxz, float dy)` (spec 2.4 table); `Pd.Hold(V3 error, V3 velocity, float mass, bool grounded, float freeScale, float groundScale) -> V3` (force; plus the gravity cancel added by the caller); `StoneRules.HoldLetGo(float speed, bool groundedChanged, float letGoSpeed) -> bool`; settings `[Modifiers] HoldDistance` 1.0, `HoldFreeScale` 2500, `HoldGroundScale` 1500, `HoldLetGoSpeed` 8.

- [ ] **Step 1: Failing tests:** `HoldGains(150) == (1, 8, 8)`, `HoldGains(350) == (1, 11, 13)`, `HoldGains(450) == (1, 15, 18)`; `Pd.Hold` with zero error and zero velocity is zero; with error (1, 0, 0), zero velocity, free → (2500, 0, 0); `HoldLetGo(8.1f, false, 8) && HoldLetGo(1, true, 8) && !HoldLetGo(7.9f, false, 8)`; a held stone of the same hand side held again lets go (`StoneRules.HoldAgain(int heldBy, int hand) -> bool`).
- [ ] **Step 2: Run** → fails. **Step 3: Implement.**
  - The server drives `Held` stones each `FixedUpdate`. The target is that player's controller (`PlayerController.LeftController/RightController.transform` on the server) plus the controller's flat forward x `HoldDistance`. Add `-Physics.gravity * mass`.
  - A grounded stone stays `Sliding` while held. Let go by `HoldLetGo`; the stone keeps its velocity (that is the throw).
  - With `[Log] Debug`, log once a second the distance between the server's controller and the claimed head offset, to measure the lag (spec section 4, unknown 3).
- [ ] **Step 4: `ALL PASS`, install.** Owner tests:
  1. **Do:** Ball, Hold, walk around. **You should see:** the ball floats about an arm's length ahead of your top hand and follows, a little behind.
  2. **Do:** swing your arm hard. **You should see:** it flies off the way you swung.
  - **Wrong if:** it shakes, falls, or stays where it was.
  - Agent: if the measured lag is over 0.25 s, note the upgrade (give the holder's game authority) as a follow-up, not in this task.
- [ ] **Step 5: Commit** `"Earthbending 0.8.0: Hold"`.

### Task 14: Flick

**Files:** Modify `Stones.cs`, `StoneTests.cs`, `StoneBody.cs`.

**Interfaces:**
- Produces: `Pd.FlickGains(float mass) -> (float p, float i, float d)` (spec 2.4); `Pd.Flick(V3 error, ref V3 integral, V3 velocity, float mass, float dt, float scale) -> V3`; `Pd.FlickTurn(V3 handA, V3 handB, V3 flatForward) -> (V3 forward, V3 up)` (up = from B to A; forward = the flat forward made square to it); settings `[Modifiers] FlickScale` 1000, `FlickTurnGain` 8, `FlickTurnDamp` 1.

- [ ] **Step 1: Failing tests:** `FlickGains(120) == (1, 0.1, 6)`, `FlickGains(350) == (1, 0.1, 8.5)`; the integral grows by `error x dt`; `CanApply(Still, Flick) == false`, `CanApply(Free, Flick) == true`; `FlickTurn` with B straight under A gives up (0,1,0); the stone stays `Flicked` until Stomp or a break (`After(Flicked, Stomp) == Stomped`, `After(Flicked, Straight) == Flicked`, with the push applied).
- [ ] **Step 2: Run** → fails. **Step 3: Implement** on the server. The target is the midpoint of both controllers plus the flat forward x `HoldDistance`. The turn torque is `FlickTurnGain x` (the angle to the target, as an axis) `- FlickTurnDamp x angularVelocity`. Gravity is cancelled. **Step 4: `ALL PASS`, install.**
  - Owner test. **Do:** Ball, Kick it up, Flick, then turn your hands. **You should see:** it hangs between your hands and turns as you turn them; Stomp drops it. **Wrong if:** it spins wildly or drops by itself.
- [ ] **Step 5: Commit** `"Earthbending 0.9.0: Flick"`.

## Phase F: Dash and the finish

### Task 15: Dash, hitstop, final play-test

**Files:** Modify `Bend.cs`, `Moves.cs`, `Stones.cs`, `StoneTests.cs`, `Hits.cs`, `StoneLook.cs`.

**Interfaces:**
- Produces: `StoneRules.HitstopSeconds(int a, int b) -> float` (0.06 + 0.02 x (a + b)); `StoneBody.Freeze(float seconds)` (kinematic; restores its state and velocity after); `Applied(Hitstop)` (clients shake `Model` by `[Looks] ShakeMetres` 0.02 for that time); settings `[Movement] DashSpeed` 8, `DashLift` 0.5.

- [ ] **Step 1: Failing tests:** `Near(HitstopSeconds(3, 1), 0.14f)`; a frozen stone cannot be broken by a second contact while frozen (`StoneRules.Breakable(bool frozen) == !frozen`); `MoveRules.Dash(float yawDeg, float speed, float lift) -> V3` points along the yaw with `y = lift` (`Dash(90f, 8f, 0.5f)` is `(8, 0.5, 0)`).
- [ ] **Step 2: Run** → fails. **Step 3: Implement.**
  - Dash: `Moves.Push(MoveRules.Dash(...))` + dust.
  - Clash and hit now freeze both stones for `HitstopSeconds` before the loser breaks; the winner gets its old velocity back.
- [ ] **Step 4: `ALL PASS`, install. Final play-test** (the owner and a friend), numbered:
  1. **Do:** Dash. **You should see:** you shoot forward a few metres with dust behind you. **Wrong if:** it fires while you walk.
  2. **Do:** your Wall against their Ball. **You should see:** both freeze and shake for a blink, then the ball breaks and the wall stands.
  3. **Do:** a full combo: Disc, Straight; Ball, Hold, throw; Cube, Explode, Straight into them. **You should see:** each works as before, chained without waiting. **Wrong if:** any move stops working after another.
  4. **Do:** play 10 minutes in a field together. **You should see:** no stutter. **Wrong if:** frame drops when stones are about.
  - Agent: both logs free of exceptions; `MaxTotal` never hit; nothing saved after a server restart.
- [ ] **Step 5: Commit** `"Earthbending 1.0.0: Dash and hitstop"`.

### Task 16: Notes for the next person

**Files:** Create `Earthbending/README.md`. Send the CLAUDE.md section to the documentation session (it owns `CLAUDE.md`); do not edit `CLAUDE.md` here.

- [ ] **Step 1: Write `Earthbending/README.md`:**
  - what it is;
  - the 16 poses, in game terms and in the owner's own captured numbers (from `poses.txt`);
  - install (SyncLib on both sides, never on the Quest server);
  - the settings sections;
  - message 19's layout;
  - the gauntlet seam: `Bend` checks `Bender.CanBend(PlayerController)`, today `Hands.Empty()`.
- [ ] **Step 2: Draft a CLAUDE.md section of about 20 lines** (commands, file table, the five "learned the hard way" facts from the play-tests) and hand it to the documentation session.
- [ ] **Step 3: Commit** `"Earthbending: notes"`.
