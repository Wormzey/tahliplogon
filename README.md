# Tahli Plogons

Dalamud plugins for FFXIV.

## Install

1. In game, open `/xlsettings` and go to the **Experimental** tab.
2. Under **Custom Plugin Repositories**, paste this link, tick the box and press **Save and Close**:

   ```
   https://raw.githubusercontent.com/Wormzey/tahliplogon/master/repo.json
   ```

3. Open `/xlplugins` and search for **[TPlog]** to see them all.

## Plugins

| Plugin | What it does | Command |
|--------|--------------|---------|
| [[TPlog] Stray Apocalypse Paths](StrayApocalypse) | Dancing Mad (Ultimate): draws the full path of each Stray Apocalypse line of explosions as soon as it starts casting, with countdowns. Includes a practice mode (`/stray test`). | `/stray` |
| [[TPlog] Dancing Mad Helper](DancingMadHelper) | Dancing Mad (Ultimate) phase 4: real/fake callouts for Grand Cross debuffs, Mana Release and Chaos's Fire/Water. | `/dmuhelper` |
| [[TPlog] Max Melee Ring](MaxMeleeRing) | Draws each enemy's maximum melee range on the ground. | `/maxmelee` |
| [[TPlog] Materia Sweep](MateriaSweep) | Extracts materia from fully spiritbonded gear (and optionally retrieves melds) across your inventory and armoury chest. | `/materiasweep` |

The Dancing Mad plugins were decoded from combat logs and are still being checked in game, so treat their
callouts as a helper, not gospel. Each plugin's folder has a README with the details.

## Building

Needs the .NET 10 SDK and Dalamud installed through XIVLauncher.

```
dotnet build tahliplogon.slnx -c Release
```

Each plugin's `bin/Release/<Name>/latest.zip` is what goes in `dist/<Name>/` for the repo.
