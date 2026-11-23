# Earthbending Fight Health Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** On a ring, stones take RUMBLE's damage off 20 points of fight health instead of real health, and the hit that reaches 0 flings the player out; outside a ring a stone takes the same share of real health.

**Architecture:** The rules are Unity-free in a new `Earthbending/Fight.cs` (the ring's zones, the damage share, the fling's speed, a ledger of each player's points). The server tracks who is on the ring every frame and branches where a stone hits a player (`Hits.HitPlayer`). Two new kinds on message 19 tell every game the ring and each fighter's points; games draw small stones over heads and round the local player's forearm.

**Tech Stack:** C# net472, MelonLoader 0.7.3, Harmony, Unity 2020.3 Mono, the game's `Alta.*` assemblies. Tests: the console project `Earthbending.Tests` (`dotnet run --project Earthbending.Tests -c Release`).

**Spec:** `docs/superpowers/specs/2026-11-23-earthbending-fight-health-design.md` (read it first; this plan argues from it). The mod's own design: `docs/superpowers/specs/2026-11-22-earthbending-design.md` sections 3.4, 3.7, 3.8.

## Global Constraints

- Unity-free files (`Poses.cs`, `Stones.cs`, `StoneBox.cs`, and the new `Fight.cs`) hold only `System.*` and are the only files the tests compile.
- Every settings section is prefixed `Earth`; every number is a setting, re-read within 2 s. New server section: `[EarthFight]`.
- One network message, type 19: first byte the kind, second `Wire.Version` (stays 1).
- The headless server never plays a sound or a pooled effect. Nothing here is ever saved.
- Log lines start `[Earth] `; per-frame, per-hit lines go behind `[EarthLog] Debug` with the check before the string is built.
- Art: only the game's own meshes, materials, effects and sounds, shared and never modified.
- `dotnet build Earthbending -c Release`: 0 warnings, 0 errors. No deploy; never touch the game's or the server's folders.
- Never change `Main.Version`. Commits authored by the git user, with no Co-Authored-By or "Generated with" line.
- Harmony and reflection names are checked only at run time: verify each in `C:\Users\dmand\OneDrive\Desktop\attmod\_decomp\` and fail soft.
- Work in the worktree `C:\Users\dmand\OneDrive\Desktop\attmod\.claude\worktrees\eb-fight` (branch `earthbending-fight`). Other branches change `Stones.cs`, `Server.cs`, `StoneLook.cs`, `Poses.cs`, `Bend.cs` and the tests' `Program.cs`/csproj: keep edits to those files to the few lines named here.

## Review Focus

1. A player leaves the server, dies, or is teleported away while on the ring: their points go, the others on the ring refill, and no game keeps drawing their stones.
2. Two stones reach a player with 1 point in the same physics step: one finishing hit and one fling; the second stone ignores them.
3. The ring is removed or moved while a fight is on (`earth ring off`, the setting changed, `Arena.ClearRing`): all fight health goes at once on the server and on every game, and hits use the real-health rule from the next step.
4. A player who was flung is still above the ring: stones ignore them, and they get 20 only after they have been off the ring and step on again.
5. A game that joins mid-fight, or misses a message: it shows the ring and every fighter's points within 5 s, and drops a fighter's stones that were not refreshed for 12 s.

---

### Task 1: The rules, the server, the network and the console

**Files:**
- Create: `Earthbending/Fight.cs` (Unity-free), `Earthbending/FightServer.cs`, `Earthbending/Arena.cs`, `Earthbending.Tests/FightTests.cs`
- Modify: `Earthbending/Hits.cs` (`TestPlayers`, `HitPlayer`, one setting), `Earthbending/Net.cs` (two kinds), `Earthbending/Stones.cs` (only the `Wire` class), `Earthbending/Commands.cs` (`earth ring`), `Earthbending/Main.cs` (init, tick, the file table), `Earthbending/Moves.cs` (only if the fling needs it), `Earthbending.Tests/Earthbending.Tests.csproj` and `Program.cs` (the new file and one call), `Earthbending/README.md`

**Interfaces:**
- Consumes: `V3`, `StoneRules.Power`, `StoneRules.KnockDir`, `StoneRules.HitstunSeconds` (Stones.cs); `Hits.Victim` (`Player`, `Health`, `Head`, `Feet`); `Net.SendKnockback(Player, Vector3 velocity, float hitstun)`; `Net.SendApplied(uint entity, byte evt, Vector3 at)`.
- Produces (Task 2 relies on these exact names):
  - `Wire.Kind.Fight = 12`, `Wire.Kind.Ring = 13`, `Wire.KnockedOut = 23` (an Applied event byte; its entity is 0 and `at` is the player's head).
  - `Net.FightReceived : Action<int, int>` (the player's account identifier, their points; -1 = none) and `Net.RingReceived : Action<bool, Vector3, float, float, bool>` (has a ring, centre with y = the floor, ring radius, arena radius, draw it), raised on games.
  - `FightRules.DefaultPoints` (20).

**In `Fight.cs`:**

```csharp
internal enum FightWhere : byte { Outside, Arena, Ring }

internal readonly struct FightRing {
    public const float Under = 1f, Over = 8f, ArenaUnder = 6f, ArenaOver = 25f;
    public readonly V3 Centre;            // Y is the floor
    public readonly float Radius, ArenaRadius;
    public readonly bool Drawn;
    public FightRing(V3 centre, float radius, float arenaRadius, bool drawn);
    public bool Valid { get; }            // finite, Radius > 0, ArenaRadius >= Radius
    public FightWhere Where(V3 feet);
    public static bool TryParse(string? text, out FightRing ring);   // "x, y, z, ring radius, arena radius", invariant culture, Drawn true
}

internal enum FightHit : byte { NotFighting, Hurt, KnockedOut }

internal static class FightRules {
    public const int DefaultPoints = 20;
    public const float DefaultFlingClear = 3f, DefaultFlingMin = 8f, DefaultFlingMax = 16f, DefaultFlingHitstun = 1.5f, LeftSeconds = 0.3f, DefaultHealthPoints = 20f;
    public static float PlayerDamage(int power, float maxHealth, float healthPoints);
    public static float ExitDistance(in FightRing ring, V3 from, V3 flatDir);
    public static float FlingSpeed(in FightRing ring, V3 feet, V3 flatDir, float gravity, float clear, float min, float max);
}

internal sealed class FightLedger {
    public readonly struct Change { public readonly ulong Player; public readonly int Points; }   // -1 = none
    public FightLedger(int points);
    public void See(ulong player, FightWhere where, double now, List<Change> changes);
    public FightHit Hit(ulong player, int power, List<Change> changes);
    public int PointsOf(ulong player);    // -1 = none
    public bool Fighting(ulong player);
    public void Forget(ulong player, List<Change> changes);
    public void Clear(List<Change> changes);
}
```

Rules the signatures do not say:
- `Where`: Ring when the flat distance from `Centre` is at most `Radius` and the feet are from `Under` below the floor to `Over` above it; else Arena when the flat distance is at most `ArenaRadius` and the feet are from `ArenaUnder` below to `ArenaOver` above; else Outside. An invalid ring is Outside everywhere.
- `PlayerDamage` = `power / healthPoints x maxHealth`; 0 for power 0 or less, or a bad number.
- `FlingSpeed`: the launch is 45 degrees up along `flatDir`; the range wanted is `ExitDistance + clear`; speed = `sqrt(range x |gravity|)`, held to `min..max`. A zero `flatDir` uses the way from the centre out through `feet` (from the exact centre: +X). A zero gravity gives `max`.
- The ledger: a player seen on the Ring with no entry, who is not "out", gets full points. A player with an entry seen off the Ring for `LeftSeconds` loses the entry, and every other entry goes back to full. `Hit` on a player with an entry takes `power` (0 takes nothing and returns `Hurt` only when power > 0, else `NotFighting`); reaching 0 removes the entry, marks the player "out", refills every other entry and returns `KnockedOut`. An "out" player gets no entry until they have been seen off the Ring for `LeftSeconds`. `Hit` on a player with no entry returns `NotFighting`. Every change of any player's points is added to `changes` once per call.

- [ ] **Step 1: Write the failing tests** in `Earthbending.Tests/FightTests.cs` (the project's own assert helper, as `StoneTests.cs` uses), and call them from `Program.cs`:

```csharp
// the ring's zones (centre (0, 10, 0), radius 10, arena radius 16)
Where(feet (9.9, 10, 0)) == Ring;      Where(feet (10.1, 10, 0)) == Arena;
Where(feet (0, 17.9, 0)) == Ring;      Where(feet (0, 18.1, 0)) == Arena;     // a Jump is on the ring
Where(feet (0, 8.9, 0)) == Arena;      Where(feet (0, 3.9, 0)) == Outside;
Where(feet (16.1, 10, 0)) == Outside;  Where(feet (0, 35.1, 0)) == Outside;
TryParse("-697, 128.2, 87, 10, 16") gives that ring, Drawn; TryParse("") , ("1,2,3"), ("a,b,c,d,e"), ("0,0,0,0,5"), ("0,0,0,10,5"), ("0;5,0,0,10,16") all false
// RUMBLE's damage
PlayerDamage(5, 2f, 20f) == 0.5f;  PlayerDamage(1, 10f, 20f) == 0.5f;  PlayerDamage(0, 10f, 20f) == 0f;  PlayerDamage(3, NaN, 20f) == 0f;  PlayerDamage(3, 10f, 0f) == 0f
// the fling: from the centre the edge is 10 m away, + 3 clear = 13 m; sqrt(13 x 9.81) = 11.29
FlingSpeed(ring, feet centre, dir +X, -9.81, 3, 8, 16) within 0.01 of 11.29
FlingSpeed(ring, feet (9, 10, 0), dir +X, ...) == 8 (the minimum);  FlingSpeed(ring, feet (-9.9, 10, 0), dir +X, ...) == 15.0 within 0.05 (range 22.9)
FlingSpeed with dir (0,0,0) at feet (5, 10, 0) equals the +X result;  with gravity 0 == 16
// landing check: stepping a point from each of 50 random places on the ring along 8 directions at FlingSpeed, 45 degrees up, under that gravity, it comes back to the floor's height outside the ring's radius (or the speed is the maximum)
// the ledger (20 points)
See(A, Ring) -> PointsOf(A) == 20, one change (A, 20);  See(A, Ring) again -> no change
Hit(A, 3) == Hurt, PointsOf(A) == 17, change (A, 17);   Hit(A, 0) == NotFighting, no change
See(B, Ring); Hit(B, 5) x 3 -> 5;  Hit(B, 5) == KnockedOut: PointsOf(B) == -1, PointsOf(A) == 20, changes hold (B, -1) and (A, 20)
Hit(B, 5) again == NotFighting (out);  See(B, Ring, later) -> still -1 (must leave first)
See(B, Arena, t), See(B, Arena, t + 0.29) -> still out;  See(B, Arena, t + 0.31); See(B, Ring) -> 20
a brief step off: See(A, Arena, t); See(A, Ring, t + 0.2) -> A keeps its points, nobody refills
walking off: A at 12 and C at 9 on the ring; See(A, Arena, t), See(A, Arena, t + 0.31) -> PointsOf(A) == -1, PointsOf(C) == 20
Hit(X never seen, 3) == NotFighting;  Forget(C) -> (C, -1) and the others refill;  Clear -> every entry -1 and nobody is "out"
two hits in one step at 1 point: Hit(A, 1) == KnockedOut, then Hit(A, 3) == NotFighting
points over 250 or under 1 in the constructor are held to 1..250
```

- [ ] **Step 2: Run them and see them fail** (`dotnet run --project Earthbending.Tests -c Release`): the build fails on the missing types.
- [ ] **Step 3: Write `Fight.cs`** to the signatures and rules above; add it to the tests' csproj.
- [ ] **Step 4: Run the tests: ALL PASS.** Commit: `Earthbending: fight health's rules (the ring's zones, RUMBLE's damage share, the fling, the ledger)`.

- [ ] **Step 5: The network** (`Stones.cs` `Wire`, `Net.cs`): the two kinds and the event byte from "Produces". Fight carries the account identifier (an int: verify `UserInfo.Identifier`'s type in `_decomp`) and one byte of points (255 = none). Ring carries one bool (has a ring), the centre, the two radii and the draw flag. `Net.SendFight(int account, int points)` and `Net.SendRing(bool has, in FightRing ring)` go to every connection, as `SendApplied` does; games raise the two events. A message of either kind on the server, or shorter than its fields, is dropped as the others are.
- [ ] **Step 6: `FightServer.cs`** (server only):
  - Settings `[EarthFight]`: `Enabled` (true), `Points` (20), `Ring` (""), `FlingClearMetres` (3), `FlingMinSpeed` (8), `FlingMaxSpeed` (16), `FlingHitstunSeconds` (1.5). `[EarthDamage] PlayerHealthPoints` (20) lives with Hits' settings.
  - The ring: another mod's (`Arena.SetRing`) wins over the console's, which wins over the setting's. Any change sends Ring, clears the ledger and sends every cleared player's -1. `Enabled` false is no ring.
  - `Tick()` from `Main.OnUpdate` before the headless return: for every player with a body (`pc.RealFeetPosition`, as `Hits.ReadPlayers` reads them), `ledger.See`; a player who is gone, dead or has no body is `Forget`-ed; every change is sent. Every 5 s it sends the ring and every fighter's points again.
  - `internal static FightWhere Where(Player p, Vector3 feet)`, `internal static bool Fighting(Player p)`, `internal static FightHit Hit(Player p, int power)` (sends the changes), and the fling's numbers for Hits.
  - One start-up line; a Summary line (not Debug) per finishing hit: `[Earth] Fight: <name> is knocked out by <kind> #<id> of <owner> (power p): flung at v m/s`; Debug lines for enter, leave, refill and each hit's points.
  - `Arena.cs`: `public static class Arena { public static void SetRing(Vector3 centre, float ringRadius, float arenaRadius); public static void ClearRing(); }` with a summary comment saying it is for the arena mod and that calls are ignored on a game.
- [ ] **Step 7: `Hits.cs`:**
  - `TestPlayers`: a victim who is in the Arena, or on the Ring and not `Fighting`, is skipped entirely (no hit, no knockback, no break).
  - `HitPlayer`, after the power and the hit ledger: a fighting victim takes `FightServer.Hit(player, power)` and no `ImpactHit`; `Hurt` gives today's knockback and hitstun; `KnockedOut` gives the fling (the stone's flat velocity direction, 45 degrees up, `FlingSpeed` with `Physics.gravity.y`; hitstun `FlingHitstunSeconds`) and `Net.SendApplied(0, Wire.KnockedOut, head)`. The stone breaks as today.
  - A victim Outside takes `FightRules.PlayerDamage(power, their maximum health, PlayerHealthPoints)` as the `ImpactHit`'s damage (under `MinHitSpeed`: none, as `StoneRules.Damage` does today); cripple, knockback and ruling 30 unchanged. Creatures keep `DamagePerPower`.
  - The start-up line says both rules.
- [ ] **Step 8: The fling carries.** Read `Moves.OnKnockback` and `Moves.Push`: a knockback of 16 m/s at 45 degrees must reach the ground beyond the ring on the player's game (the push's speed cap, the hold, the stun). Change `Moves.cs` only if it would cut the flight short, and say what you found in the report either way.
- [ ] **Step 9: The console** (`Commands.cs`): `earth ring <player> [radius]` (default 10; the ring round that player's feet at their height, arena radius = radius + 6, drawn), `earth ring off`, `earth ring` (where it is, who is on it with how many points). Every reply is a string.
- [ ] **Step 10: `Main.cs`** (init, the tick, the file table's new lines) and the README (a "Fight health" part: the rules in the owner's words, the settings, the console).
- [ ] **Step 11: Run the tests (ALL PASS) and the build (0 warnings).** Commit: `Earthbending: fight health on the server (20 points on the ring, RUMBLE's damage, the finishing hit flings; a share of real health outside)`.

### Task 2: What players see

**Files:**
- Create: `Earthbending/FightLook.cs`
- Modify: `Earthbending/Fight.cs` and `Earthbending.Tests/FightTests.cs` (the layout maths), `Earthbending/StoneLook.cs` (only: one accessor for the boulder mesh and materials, and the KnockedOut look), `Earthbending/Main.cs` (init, one call, the file table)

**Interfaces:**
- Consumes: `Net.FightReceived`, `Net.RingReceived`, `Wire.KnockedOut`, `FightRules.DefaultPoints` (Task 1); the boulder mesh and materials `StoneLook` already finds; `PlayerController.Current`, a player's `PlayerController.Head`, the local left controller's transform (as `Hands.cs` reads it).
- Produces: nothing other code uses.

**In `Fight.cs`:**

```csharp
internal static class FightPips {
    public const int MaxShown = 40, Group = 5;
    // the i-th of n stones in a row centred on 0: stones `size + gap` apart, an extra `groupGap` between groups of five
    public static float RowOffset(int i, int n, float size, float gap, float groupGap);
    // the i-th of n places round a forearm, in radians, starting on top and going round evenly
    public static float BandAngle(int i, int n);
}
```

- [ ] **Step 1: Write the failing tests:** `RowOffset` is symmetric (`RowOffset(i, n) == -RowOffset(n - 1 - i, n)` for n = 1, 5, 7, 20), neighbours inside a group are `size + gap` apart and across a group boundary `size + gap + groupGap`, a row of 20 at size 0.035, gap 0.012, group gap 0.03 is under 1.1 m wide, n of 0 or less and i out of range give 0; `BandAngle(0, 20) == 0`, steps of 2 pi / 20, n of 0 gives 0.
- [ ] **Step 2: Run them and see them fail. Step 3: write `FightPips`. Step 4: ALL PASS.**
- [ ] **Step 5: `FightLook.cs`** (games only; nothing on the server):
  - Stones: the game's boulder mesh and its own shared materials, at `[EarthLooks] FightPipSize` (0.035 m), pooled under one `DontDestroyOnLoad` host, never more than `FightPips.MaxShown` a fighter.
  - Another fighter: their row floats `FightRowHeight` (0.35 m) over their head, centred, turned to face the local camera each frame.
  - The local player: the same count as a band round the left forearm (`FightBandRadius` 0.07 m, `FightBandUp` 0.12 m from the controller toward the elbow; the elbow's way is from the controller toward the point 0.25 m under the head).
  - A fighter's stones follow `Net.FightReceived` (-1 or a player who is gone: none) and are dropped when not refreshed for 12 s.
  - A drawn ring: a dotted circle (64 dashes, `Sprites/Default`, pale, 0.03 m above the floor) at the ring's radius; gone with the ring.
  - `Applied(KnockedOut)`: `StoneLook` plays the Explode blast's effect and sound at the place.
  - `[EarthLooks] FightStones` (true): false draws no stones (the ring and the knock-out look stay).
  - No per-frame allocation once built; one try round the frame's work, warned once; each look fails soft on its own (no boulder mesh: one warning, no stones).
- [ ] **Step 6: Run the tests (ALL PASS) and the build (0 warnings).** Commit: `Earthbending: fight health is seen (small stones over each fighter's head and round your own forearm, the test ring's edge, the knock-out)`.
