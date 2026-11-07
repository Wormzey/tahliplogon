# Stray Apocalypse Paths

Dalamud plugin for Dancing Mad (Ultimate). As soon as each Stray Apocalypse starter begins casting, it draws all
six follow-up explosions on the ground, with a countdown on each. Sets alternate colour (orange, blue, orange…),
the next circle to go off in each line gets a red outline, and each circle disappears once it has exploded.

* `/stray` opens settings (radius, colours, fill, countdown, duty-only toggle).
* A green circle marks where to stand, given the lines announced so far. It plans from where you are: it assumes
  you react after your reaction time (0.7 s by default, set in `/stray`) and then run straight there at normal run
  speed (6 y/s, no sprint), and checks every announced explosion against where you'd be when it goes off, so the
  route is safe as well as the spot. Among safe spots it prefers short runs, with a little pull towards the centre,
  and it only switches when another spot is clearly better. Simulated over all 36 set orders with a player
  following the circle (0.5-1.0 s reaction), it was never hit at 6 y or 7 y radius: about 5 moves per Stray
  Apocalypse, averaging ~5-6 y each (longest ~22 y), each shown ~5.6 s before the old spot would be hit. No spot is
  safe for the whole mechanic, so you do have to move.
  It also tries to keep a safety buffer (2 y by default) from the explosions' edges when that doesn't mean a much
  longer run. At 7 y the explosions cover the arena so densely that a few yalms often isn't possible: in the
  simulation the typical closest edge went from ~0.7 y to ~1.0 y at 7 y radius (~1.6 y at 6 y), for about 5 moves
  instead of 4.7. Toggle, colour, reaction time and buffer in `/stray`.
* `/stray test` loops the whole mechanic for practice: six sets 2.5 s apart, alternating NW and NE, each of the
  three NW and three NE starting pairs once in a random order (as in every logged pull). You stand where the arena
  centre would be, and the layout is re-anchored on you every 23 s. A white ring shows the 20 y arena edge (no
  player in the logs got further than 19.1 y from the centre), and a purple circle with a countdown marks each
  starter outside it until it goes off, standing in for the game's own marker.
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
