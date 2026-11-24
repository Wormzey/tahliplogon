# Reward Reset Timer

Dalamud plugin that puts a countdown next to the "Reward already received" text in the Duty Finder's duty detail
pane, so you can see when the reward comes back.

* Roulettes count down to the daily reset (15:00 UTC). Other duties (weekly-restricted raid loot and the like) count
  down to the weekly reset (Tuesday 08:00 UTC). Which one applies comes from whether the selected entry is a roulette.
* `/rewardreset` opens settings: both countdowns live, overlay on/off, "Daily/Weekly reset:" prefix, seconds,
  position (left/right/above/below the text, left by default since a chest icon sits to its right), text size,
  gap and colour. `/rewardreset time` prints both countdowns to chat.
* The text's colour codes are stripped, then any short text containing "Reward already received" / "Reward already
  claimed" (or the client-language version from the game's Addon sheet) matches. While the Duty Finder is open,
  visible windows are searched for it, and `/xllog` notes which window it was found in.
* `/rewardreset debug` logs every visible text mentioning "reward" or "already" (window, node, text, raw bytes) to
  `/xllog`, for when the countdown doesn't appear.
* The game's UI isn't edited: the countdown is an ImGui overlay drawn at the text's on-screen position and scaled
  with the window.
* Build: `dotnet build -c Release`, then add `bin\Release\RewardResetTimer.dll` as a dev plugin.

A duty with both a daily and a weekly reward shows only the weekly one.
