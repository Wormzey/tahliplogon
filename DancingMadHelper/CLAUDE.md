# Dancing Mad Helper: project context

Dalamud plugin (C#) for FFXIV that shows real/fake callouts in Dancing Mad (Ultimate) phase 4 (Kefka Says).
Goal: tell the player whether their Compressed Water / Forked Lightning is a stack or spread, and whether their
Acceleration Bomb needs stillness or motion, ideally on or near the status-bar icon.

## Current state

* Builds cleanly (0 warnings, 0 errors) with .NET SDK 10.0.403 against `Dalamud.NET.Sdk/15.0.0` and local Dalamud
  15.0.3.9, API level 15: `dotnet build -c Release`.
* **Run in game (2026-11-07, `dalamud.log` vs `Network_30304_20261107.log`):** in the 20:16 and 20:31 pulls it read the
  Neo flag, Chaos flag, both charged spells and both Mana Release markers (so the ActorControl hook and p1 assumption
  work). Every Fire/Water call and both Mana Release callouts matched what was then cast.
* That build threw `NullReferenceException` from Dalamud's `BattleChara.IsCasting` on some frames (20:12-20:52), which
  aborted `OnUpdate`. Now all NPC reads happen once per frame in `ScanNpcs`, and any object that throws is skipped.
* Settings window (`/dmuhelper config` or the plugin's Settings button): per-callout toggles, font scale, fit-to-text or
  fixed width/height, and the party chat toggle. Saved in `pluginConfigs/DancingMadHelper.json`.
* The window only shows from Kefka Says (`C2DC` cast start) until a wipe (`IDutyState.DutyWiped`) or the second Ultima
  Upsurge (`C24A`) after it; `/dmuhelper` shows or hides it manually for positioning. Nothing is tracked outside that.
* A debuff gained before the flag is readable can only fill it in within 2 s of gain; after that it stays `?`.

## Plugin design (what Plugin.cs does)

* Gate on territory `0x553`; run on `IFramework.Update`.
* Find Neo Exdeath in the object table by NPC NameId `6055`, read its status `0x808`, and take the status Param
  (`0x461` or `0x462`) as the flag.
* When the local player gains `15A8` / `15A9` / `15AA`, latch the flag at that moment (keep a per-status dictionary),
  so debuffs from Grand Cross 1 and 2 each use their own cast's flag. Drop the entry when the status expires.
* Draw a movable ImGui window (`WindowSystem`) with lines like `Water: STACK 12s`, `Bomb: STAND STILL 30s`.
  Show `?` if the flag was never seen; never guess. `/dmuhelper` toggles a preview for positioning.
* Log each flag and each debuff gain with its latched flag, so the rules can be validated in game via `/xllog`.

## Decoded mechanics (from a 610k-line ACT network log, 2026-10-31, 7 pulls, 14 Grand Cross sets)

Territory: ACT zone `0x553` (1363). Neo Exdeath NPC NameId: `6055` (Chaos 7691, Exdeath 6052, Kefka 7131).

Neo Exdeath gains hidden status `0x808` ("Unknown_808", no source) ~90 ms before each Grand Cross (`BB14`) cast
starts. The status's param (the "count" field in log line 26) is the flag. It is removed ~1.4 s after the debuffs land.
Debuffs land ~9 s after cast start (cast time 8.7 s).

| Flag param | Cast | Forked Lightning `15A8` | Compressed Water `15A9` | Acceleration Bomb `15AA` |
|------------|------|-------------------------|-------------------------|--------------------------|
| `0x462`    | real | spread                  | stack                   | stand still (inferred)   |
| `0x461`    | fake | stack                   | spread                  | move (inferred)          |

* **Cursed Shriek `15A7`** uses the same latched flag: real `0x462` -> look away, fake `0x461` -> look at (user's rule).
  The flag predicts the Death Shriek ID at expiry in 79/80 cases across the 10-31, 11-06 and 11-07 logs (`0x462` ->
  `BB16`, `0x461` -> `BB17`). Facing isn't in the log, so which ID is look-at comes from the user, not the data.

Other IDs: Neo-sourced White Wound `15A5` / Black Wound `15A6`.
Chaos has a similar flag pair `0x45F` / `0x460` (undecoded). Grand Cross 3 and a later Neo cast also carry the flag
(undecoded).

Grand Cross 1 applies debuffs with timers 51s/76s, Grand Cross 2 with 36s/61s.

### Evidence and confidence

* **Stack/spread: verified, high confidence.** At debuff expiry: flag `0x462` -> single-target Death Bolt (`BB18`) on
  Lightning holders and a six-hit Death Wave (`BB1A`) stack on Water; flag `0x461` -> single-target Death Wave (`BB1B`)
  on Water holders and a six-hit Death Bolt (`BB19`) stack on Lightning. 14/14 sets match (one noisy set still
  fit). Matches the Icy Veins and Materia guides.
* **Acceleration Bomb: inferred, medium confidence.** The debuff has no type info (param always 0). Death Bomb (`BB15`)
  fires only on single players right at expiry (5 times in the log) and looks like the failure state. Under `0x462`,
  no passing bomb had an ability use in the last second, and all 3 failures had ability use or movement. Under `0x461`,
  11 of 20 bombs had ability use just before expiry and both failures stood still doing nothing. Position data in the
  log is sparse, so a few fake-flag passes show no clear movement. Must be validated in game.

### Kefka Mana Charge / Mana Release (same log, 7 pulls)

Casts before Grand Cross 3 don't matter. Mana Charge (`BAA4`, ~67s after Kefka Says) gives Kefka status `5CA`. He then
charges Thunder at ~73s (status `5CD`, own cast `C5DE`) and Blizzard at ~91s (status `5CC`, own cast `BA95`), during the
first Shriek and second Lightning/Water. Mana Release (`BAA5`) starts ~102s, 6.7s cast; released spells cast at ~109s
and land at ~114s. Helper actors cast the real ability, and their names vary, so match by action ID:

| Spell    | True (deals damage) | False (flag `1B`, no damage)  | Marker true/false |
|----------|---------------------|-------------------------------|-------------------|
| Thunder  | `BA9F`              | `BAA0` + `BAA1`               | `02A6` / `02A5`   |
| Blizzard | `BA98`              | `BA9B` + `BA9E`               | `02A4` / `02A3`   |

* **Rule (from the user), high confidence:** Mana Release shows one marker per spell, ~0.2s before its cast. The
  false-ID marker (`02A5`/`02A3`) is the question mark around the VFX and inverts that charged spell; the true-ID marker
  leaves it alone. Released = charged XOR question mark. Stand on released spells that are false.
* Matches all 6 pulls where the released spells were cast (12/12 spells); pull 3 wiped first. Log combos (Thunder,
  Blizzard): false/false x3 (BOTH), true/true x1 (NEITHER), false/true x2 (THUNDER). STAND ON BLIZZARD is unseen.
* Line 27 markers reach the client as ActorControl category 34, assumed with the ID in p1. `Plugin.cs` hooks
  `PacketDispatcher.HandleActorControlPacket` for this. If the hook or markers fail, the callout stays `?` until the
  released casts start at ~109s, about 5s before they land; a disagreement between the two logs a warning.
* A `1B` flag's "damage" value in the log is the action ID, not damage.

### Chaos Inferno / Tsunami (same log, 7 pulls, 14 casts)

Casts start ~15s and ~30s after Kefka Says, in either order. Chaos gains status `0x808` ~90 ms before each, lost about
0.8s after the cast ends. Inferno is `BB1E` + `BB20`, Tsunami is `BB1F` + `BB21`; helper names vary.

| Flag param | Cast | Inferno spawns (`BB22`/`BB23`) | Tsunami spawns (`BB24`/`BB25`) | Chat call                     |
|------------|------|--------------------------------|--------------------------------|-------------------------------|
| `0x460`    | true | `BB22` twister                 | `BB24` donut                   | Fire TWISTER, Water DONUT     |
| `0x45F`    | fake | `BB23` donut                   | `BB25` twister                 | Fire DONUT, Water TWISTER     |

* The flag predicts the spawn ID in 14/14 casts.
* Water shapes are confirmed by hit distance: `BB24` hits players ~14-20y from its source, `BB25` hits near it.
* Fire shapes rest on the user's rule. No player was hit by `BB22`/`BB23` in the phase, so the log can't confirm them.
* Players' debuffs: Inferno gives Entropy `15AB`, Tsunami gives Dynamic Fluid `15AC`, ~10s after the cast. The overlay
  shows `Fire:`/`Water:` TWISTER or DONUT with that debuff's timer, using the result of the chat call.
* The call is sent 3s after the cast starts: to `/e` (own chat only) by default, or to `/p` once `/dmuhelper chat` turns
  party calls on. It is always logged.

### Log format notes (for writing replay/validation scripts)

ACT network log, `|`-separated. Types: `01` zone change, `03` AddCombatant (p[9]=NameId, p[10]=BaseId), `20` start
cast, `21`/`22` ability (p[4]=id, p[5]=name, p[7]=target name), `26` status gain (p[2]=id, p[4]=duration,
p[8]=target, p[9]=count/param), `30` status loss, `38` status list (packed `param<<16|statusId`), `24` HoT/DoT tick
(position at p[13..16]), `37`/`38` positions at p[11..14]. The source log is `Network_30304_20261031.log`
(uploaded by the user to the earlier chat sandbox at `/mnt/user-data/uploads/`).

## Open tasks (suggested order)

1. Check the overlay can be dragged with `/dmuhelper`.
2. Confirm Dalamud can read status `0x808` and its `Param` off the untargetable Neo Exdeath actor. If not,
   fall back to an action/network hook.
3. Validate the bomb rule and the Mana Release callout in game using the plugin's log lines (`/xllog`). Also confirm
   `IsCasting`/`CastActionId` can be read off the helper actors and that `Mana Release marker` lines appear.
4. Retexture or overlay the real status-bar icon: inspect the status addons with Dalamud's Addon Inspector (`/xldata`),
   then hook via `IAddonLifecycle`. Prefer drawing an ImGui overlay at the node position over editing game nodes.
5. Decode Cursed Shriek (probably the same flag) and Grand Cross 3.

## Constraints

* Overlay/readout only; do not automate player input. The one exception, requested by the user, is the optional
  party chat call for Chaos's Fire/Water.
* Don't present inferred rules (the bomb) as verified.
