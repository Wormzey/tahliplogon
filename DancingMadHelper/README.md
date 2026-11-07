# Dancing Mad Helper (Dalamud plugin)

Phase 4 (Kefka Says) callouts, driven by Neo Exdeath's hidden real/fake flag.

## Rules the plugin applies

Neo Exdeath gains status `0x808` ~90 ms before each Grand Cross cast. Its param:

| Param   | Cast | Forked Lightning (`15A8`) | Compressed Water (`15A9`) | Acceleration Bomb (`15AA`) |
|---------|------|---------------------------|---------------------------|----------------------------|
| `0x462` | real | spread                    | stack                     | stand still                |
| `0x461` | fake | stack                     | spread                    | move                       |

The plugin latches the flag at the moment *you* gain each debuff, so a Water from Grand Cross 1 and a
Bomb from Grand Cross 2 each use their own cast's flag.

## Setup

1. Install it from the custom repo (see the main README), or build with `dotnet build -c Release`.
2. `/dmuhelper` toggles a preview so you can drag the overlay into place.

## Evidence (network log, 2026-10-31, 7 pulls, 14 Grand Cross sets)

* **Stack/spread:** every set matched. Flag `0x462` -> single-target Death Bolt (`BB18`) on Lightning holders and
  a six-hit Death Wave (`BB1A`) stack; flag `0x461` -> single-target Death Wave (`BB1B`) on Water holders and a
  six-hit Death Bolt (`BB19`) stack. (One set was noisy from extra simultaneous hits but followed the same pattern.)
* **Acceleration Bomb:** the debuff carries no type information (param is always 0), so this half is inferred.
  Under `0x462`, none of the non-failing bombs had a player action in the last second, and all three failures
  had either an ability use (Blazing Shot, First Generation) or movement right before expiry. Under `0x461`,
  11 of 20 bombs had an ability use just before expiry, and both failures were players who stood still with no
  actions. The log's position data is sparse, so a few fake-flag bombs that passed show no clear movement.
  Treat "fake = move" as likely, not proven.

## Verify in game

The plugin logs each flag it sees and each debuff gain with the flag it latched (`/xllog`). Compare against
what actually happens on your first few pulls before trusting the bomb call.

## Not covered

* Cursed Shriek (`15A7`, gaze). It probably follows the same flag, but I haven't verified it.
* Grand Cross 3 (Black/White Wound) and Chaos's own flag (`0x45F`/`0x460`) are not decoded.
* Swapping the actual status-bar icon: this overlay is a separate window. Retexturing the real icon needs the
  status addon's node layout, which you can inspect with Dalamud's Addon Inspector (`/xldata`).
