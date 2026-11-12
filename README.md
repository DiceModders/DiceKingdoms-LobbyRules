# DiceKingdoms Lobby Rules (`DiceModders.LobbyRules.dll`)

Gameplay rules and game values the host sets in the lobby, for **Dice Kingdoms** (BepInEx 6 IL2CPP).

## What it does

The host sets the rules in **Settings > Lobby > Rules** before the match; everybody else sees them read-only. They change the simulation, so they are only used when **every** player has this mod in the same version. Otherwise the match is played with the game's own values for everybody, and ModCore warns in the lobby first.

| Rule | Effect |
|---|---|
| **Island grid size** (*Islands*) | tiles per side of every island, from 49 (the game's) to 201, odd sizes. Up to 119 plays normally; 121 to 201 is experimental, and the host is asked first with the game's own dialog (Cancel goes back to 119). The land and the space between the islands grow with it, and so do the number of hills, forests, rock fields and fish (with the land area); each hill or forest keeps the game's size, and the fish are spread evenly around the whole coast. Above 119 the game's own island generation crashes; ModCore fixes that. The default camera view stays at the normal distance (centred on your island) and the zoom limits stay the game's; only the area the camera can pan over grows, because the islands lie further apart. On bigger islands the interface is drawn farther from the camera so it does not shimmer while the camera moves |
| **Land size %** (*Islands*) | how much of the grid is land, 70 to 110 %; 100 = like the game's islands. The game's land falls off towards the edge of the grid, so the rule moves that falloff by the same factor as the land limit |

The island rules are fixed when the islands start to spawn (a little before the match starts) on every computer. A player who leaves a running match and comes back without the same mods is refused by the host (ModCore), so they cannot play with other rules than everybody else.

### Game values window

**Settings > Lobby > Lobby Rules > Game values** opens a window where the host changes the game's numbers before the match:

* **Units**: one section per civilization (and *Every civilization*): attack period and delay, aggro distance, range, parry, arrow block and miss chance (in %) of every unit type.
* **Buildings**: health, tower range and period, and the cost of every building.
* **Technologies**: the cost of every technology.

Click a name to open its section. **-** / **+** change a value, **Game** puts it back, **Reset all** clears every change; a changed value shows the value it replaced. Everybody else sees the host's values read-only. Only the changed values are sent, in one lobby value, so every player gets exactly the host's numbers; they are used only when every player has this mod (ModCore's unit stats and balance, with their own switches *Edited unit values* / *Edited building values* in the lobby rules).

The whole mod can be switched off in the mod list.

## Code map

| File | Job |
|---|---|
| `Plugin.cs` | the plugin and the mod switch |
| `GameValues.cs` | the host's changed values: one lobby value, applied through ModCore's unit stats and balance |
| `ValuesWindow.cs` | the Game values window and its Lobby settings button |
| `IslandSize.cs` | the island grid size and land size rules, the experimental-size warning, the amounts and fish on bigger islands, the camera, sea and interface for bigger islands |

## Requirements

* Dice Kingdoms with **BepInEx 6 IL2CPP (x64)**: see the wiki's [Mods Installation](https://dicemodders.github.io/DiceKingdoms-Modding-Wiki/guides/mod-installation/) guide. Start the game once so BepInEx creates its folders.
* The DiceModders libraries in `BepInEx\plugins`: [ModCore](https://github.com/DiceModders/DiceKingdoms-ModCore) (`DiceModders.ModCore.dll`), and [UiKit](https://github.com/DiceModders/UiKit) with [ModBridge](https://github.com/DiceModders/DiceKingdoms-ModBridge) for the rows the host edits the rules with. This mod does not contain copies of them.

## Install (players)

Copy `DiceModders.LobbyRules.dll` into `BepInEx\plugins`, next to the libraries. Every player of the lobby needs it.

## Build (developers)

Needs the .NET SDK (6 or newer) and PowerShell. Game files are never committed: the script links `BepInEx\interop` from your game folder and copies the libraries from `BepInEx\plugins` into `libs`.

```powershell
.\scripts\build.ps1 -GameDir "<your Dice-Kingdoms folder>"   # first time: remembers the folder
.\scripts\build.ps1                                           # build + install (Release, console and log OFF)
.\scripts\build.ps1 -Dev -Run                                 # Debug build, console and log ON, then start the game
```

Close the game before building: it locks the DLLs. If PowerShell refuses to run scripts: `powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1`.

## License

MIT, see [LICENSE](LICENSE).
