# Stray Apocalypse Paths

Dalamud plugin for Dancing Mad (Ultimate). As soon as each Stray Apocalypse starter begins casting, it draws all
six follow-up explosions on the ground, with a countdown on each. Sets alternate colour (orange, blue, orange…),
the next circle to go off in each line gets a red outline, and each circle disappears once it has exploded.

* `/stray` opens settings (radius, colours, fill, countdown, duty-only toggle).
* `/stray test` loops the whole mechanic for practice: six sets 2.5 s apart, alternating NW and NE, each of the
  three NW and three NE starting pairs once in a random order (as in every logged pull). You stand where the arena
  centre would be, and the layout is re-anchored on you every 23 s.
* Build: `dotnet build -c Release`, then add `bin\Release\StrayApocalypse.dll` as a dev plugin.

## How the mechanic was decoded (ACT logs 2026-11-06 and 2026-11-07, 5 pulls, 60 starters)

* Kefka casts `BB3B`. Pairs of helpers cast `BB3C` (3.7 s) from the arena edge, a new pair every ~2.5 s, six pairs
  in all. Edges used: NW starters face SE (rotation +0.79), NE starters face SW (rotation -0.79).
* Each starter blows up at its own spot (~4.0 s), then fires `BB3D` six times, each one 5 y further along both axes
  in the direction it faces, at 4.60 / 5.11 / 5.62 / 6.13 / 6.65 / 7.17 s after its cast starts.
  Steps 1-6 matched 39-48 of 52 checkable starters each; the misses were logged helper positions that hadn't
  updated yet (helpers are reused), not different paths.
* Radius: players were hit up to ~6.4 y from the logged centre, so the default is 7 y for a safety margin. **Not confirmed in game.**

## Not yet verified in game

* That `IsCasting` / `CastActionId` / `Rotation` read correctly off the helpers (`/xllog` logs one line per lane).
* The exact radius. Adjust it in settings if the circles are visibly off.
* The coloured glyph pillars seen in the video don't show up in the ACT log, so the plugin doesn't use them; it
  predicts from the starter casts alone.
