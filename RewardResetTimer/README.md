# Reward Reset Timer

Dalamud plugin that puts a countdown next to the Duty Finder's "Reward already claimed" text, so you can see when
the reward comes back.

* Roulettes count down to the daily reset (15:00 UTC). Other duties (weekly-restricted raid loot and the like) count
  down to the weekly reset (Tuesday 08:00 UTC). Which one applies comes from whether the selected entry is a roulette.
* `/rewardreset` opens settings: both countdowns live, overlay on/off, "Daily/Weekly reset:" prefix, seconds,
  text size, gap and colour. `/rewardreset time` prints both countdowns to chat.
* The text to look for is read from the game's Addon sheet (English rows containing "already claimed", read back in
  the client's language), so it should work on any client language. `/xllog` lists the phrases it matched on load.
* The game's UI isn't edited: the countdown is an ImGui overlay drawn at the text's on-screen position and scaled
  with the Duty Finder window.
* Build: `dotnet build -c Release`, then add `bin\Release\RewardResetTimer.dll` as a dev plugin.

Not yet checked in game: placement beside the text, and that weekly duties pick the weekly timer. A duty with both
a daily and a weekly reward shows only the weekly one.
