# CosmicBots 0.5.1

**AI teammates for MARVEL Cosmic Invasion (offline local play)**

Copyright © 2026 MrRobotoDomoArigato · [YouTube](https://www.youtube.com/@MrRobotoDomoArigato) · [Nexus Mods page](https://www.nexusmods.com/marvelcosmicinvasion/mods/20)

Feel free to reach out to report any issues or suggest features you'd like to see for the bots.

---

## ⚠️ Important – please read

CosmicBots is an **unofficial fan mod**. It is not affiliated with, endorsed by, or supported by Marvel, Tribute Games, Dotemu or Valve. You need your own legitimate copy of MARVEL Cosmic Invasion on Steam. This download contains **no game code and no game assets**, only the mod itself.

Please report anything that goes wrong (see [Troubleshooting](#troubleshooting)).

## What's new in 0.5.1

- **Siege of Castle Doom bot support is added, but experimental: it needs more testing in that mode, so please report any problems** (see [Troubleshooting](#troubleshooting)). Bots join at character select, press Ready on the Reed's Lab vote screens without voting (your votes decide), pick their own perks automatically, and fight with you. You choose doors, pick up equipment and decide when to take the elevator (bots don't take it while a human is still standing). Bots try to stay out of Castle Doom traps (Tesla coils, pendulum blades, sentry lasers). The F7 menu shows "Castle Doom bots: experimental" in that mode.
- Includes the Castle Doom crash fix from 0.5.0 (starting a Castle Doom run through CosmicBots crashed after the Reed's Lab screens).

## What's new in 0.5.0

- **Doctor Doom and Invisible Woman bots.** Both can be bot heroes and partners (only if you own the DLC and the hero is unlocked on your save).
- **Bot colors.** New "Color" and "Partner color" rows in the F7 menu for every bot, shown as "Auto" or e.g. "3 of 8". The value follows the hero picked for that bot and never goes past that hero's real number of colors; locked colors are skipped. On the character select screen the bot sits on its hero without locking in, so you can change its color in F7 and see it on the select screen. Bots lock in when every human is ready, or right away with "Lock in bots now". With Hero on Auto, Color stays Auto.
- **Siege of Castle Doom:** bots were switched off in that mode (back in 0.5.1, as experimental).
- **Fix:** a crash when starting a Castle Doom run through CosmicBots (after the Reed's Lab screens). It came from CosmicBots switching off Epic online services, not from the bots.
- **Steam Input fallback.** If nobody has joined after a few seconds on the title screen (for example Steam Input does not see your controller), CosmicBots switches to plain keyboard/gamepad input. Set with `InputFallbackSeconds` in `CosmicBots.ini` (default 5, 0 = off).
- **Menu.** The F7 menu shows "By MrRobotoDomoArigato" under the title and fits on screen with all three bots on (on very small screens it scrolls with the selected line).
- Updated for game build 1.0.0.14793.

## What it does

Adds computer-controlled teammates ("bots") for players 2, 3 and 4 in **offline local play**, so you can play a 2–4 player game on your own. The bots fight, block, parry, dodge, sprint, use assists and team specials, escape enemy rushes, follow your lead on the path-select screen, and stay with you through the level.

## Download

Get the zip from the [Releases page](../../releases) or from [Nexus Mods](https://www.nexusmods.com/marvelcosmicinvasion/mods/20).

## Install

1. Close the game.
2. In Steam, right-click **MARVEL Cosmic Invasion → Manage → Browse local files**. An Explorer window opens on the game folder (the one that contains `Game.exe`).
3. Unzip the contents of this zip into **that** folder, so these files sit **right next to `Game.exe`**:
   - `CosmicBots.exe`
   - `CosmicBots.exe.config`
   - `0Harmony.dll`
4. Make sure Steam is open and you are logged in.
5. Start the game by double-clicking **`CosmicBots.exe`** (instead of pressing Play in Steam). CosmicBots starts the normal game for you.

**Tip:** right-click `CosmicBots.exe` → **Send to → Desktop (create shortcut)** for an easy launcher.

### Uninstall

Close the game and delete `CosmicBots.exe`, `CosmicBots.exe.config`, `0Harmony.dll` and, if you like, `CosmicBots.ini`, `CosmicBots.log` and `CosmicBots.prev.log`. Nothing else was changed. Starting the game from Steam as usual always gives you the unmodded game.

## Using the bots – the F7 menu

Press **F7** (keyboard) at any time to open or close the CosmicBots menu. While it is open the game ignores the keyboard, so you will not move by accident.

| Key | Action |
|---|---|
| Up / Down | Move through the menu |
| Left / Right | Change a value |
| Enter / Space | Switch an item on or off, or press a button |
| Esc / Backspace / F7 | Close the menu |

| Menu item | What it does |
|---|---|
| **P2 bot / P3 bot / P4 bot** | Switch a bot on or off for that player slot. A bot joins at the character select screen (or right away if you are already in a level) and only takes a slot that no human is using. |
| **Hero / Partner** | Shown for slots that are on. Pick which hero and partner hero that bot plays, or leave it on "automatic". |
| **Color / Partner color** | Pick the bot's hero color, or leave it on "Auto" (the game's default color). Shown as "2 of 5": color 2 of the 5 that hero has. Needs a Hero / Partner picked first. |
| **Lock in bots now** | On the character select screen, makes the bots confirm their heroes. By default they also lock in automatically when every human player is ready. |
| **Aggression** | How eager the bots are to charge in (0.50–2.00, default 1.00). |
| **Bot skill** | Easy / Normal / Hard (default Hard): reaction time and quality of decisions. |
| **Close** | Closes the menu. |

Your choices are saved automatically in `CosmicBots.ini` in the game folder.

## Notes

- **Known limitations in Castle Doom (experimental):** bots don't open doors and don't collect Battery Cells or equipment, so you do that. If all human players are down, the run can stall at a door because the bots won't open it. New bots can't join once the run is locked (same as for human players).
- **Offline / local play only.** Bots are switched off in online sessions, and CosmicBots does not start the game's online services while it runs.
- While running through CosmicBots, the game's crash reporting and analytics reporting are switched off, and the mod never sends anything anywhere. This applies only to the copy of the game started by `CosmicBots.exe`.
- CosmicBots does not modify any game file. It starts the game and patches it in memory only while it runs. Your saves are not touched.
- CosmicBots tells the game its Steam App ID (2753970) for that one launch, as Steam itself would if you pressed Play. Steam must be running.
- Built for Steam build **1.0.0.14793**. A later game update may break the mod until it is updated.

## Troubleshooting

<details>
<summary><b>"Game.exe not found", nothing happens, or a message in CosmicBots.log</b></summary>

The three mod files must be in the same folder as `Game.exe`. Use Steam → right-click the game → Manage → Browse local files, and unzip there.
</details>

<details>
<summary><b>The game opens but there is no Steam, or the game behaves oddly</b></summary>

Make sure the Steam client is running and logged into the account that owns the game, then start `CosmicBots.exe` again. `CosmicBots.log` says whether Steam started (`Steam: SteamAPI initialised`).
</details>

<details>
<summary><b>Windows SmartScreen or your antivirus flags or deletes CosmicBots.exe</b></summary>

`CosmicBots.exe` is not code-signed, and it uses a runtime-patching library (Harmony) to change the game in memory, the same technique game trainers use, so some security tools are suspicious of it. That is the only reason.

You can check the file is the one published here. Its SHA256 is:

```
95f1a2df3d667a3e056411932b0a76a4c8b7ed5c7dda5c456791b250005da80e
```

In PowerShell: `Get-FileHash CosmicBots.exe -Algorithm SHA256`

To allow it:
- **SmartScreen:** click "More info", then "Run anyway".
- **Defender / other antivirus:** restore the file from quarantine and add the game folder (or `CosmicBots.exe`) to the exclusions list.

If you are not comfortable with that, please do not use the mod.
</details>

<details>
<summary><b>".NET Framework" error on start</b></summary>

CosmicBots needs Microsoft .NET Framework 4.x (4.7 or newer). It is already part of Windows 10 and 11. Older systems can install it from Microsoft.
</details>

<details>
<summary><b>The game updated and now crashes, or the bots misbehave</b></summary>

Start the game from Steam (Play) to play unmodded, and wait for a CosmicBots update. Please tell me which game build you have.
</details>

<details>
<summary><b>You cannot press Start on the title screen</b></summary>

Wait about 5 seconds, then press a key or a button again. CosmicBots switches to plain keyboard/gamepad input (`CosmicBots.log` says "Input fallback"). If you set `InputFallbackSeconds=0`, set it back to 5.
</details>

<details>
<summary><b>Settings seem broken</b></summary>

Close the game and delete `CosmicBots.ini`. A fresh one with the defaults is created next time.
</details>

### Asking for help

Please open an [issue](../../issues) or post on Nexus with:

1. `CosmicBots.log` (in the game folder; the previous run is in `CosmicBots.prev.log`)
2. The game's own log: `Documents\Tribute Games\MARVELCosmicInvasion\log.txt`
3. What you were doing, and your Windows version

Both logs are plain text. They may contain your Windows user name in file paths, so look them over first if that matters to you.

## License / warranty

CosmicBots is Copyright © 2026 MrRobotoDomoArigato. It is provided free of charge, **as is**, with no warranty of any kind. Use it at your own risk. MARVEL Cosmic Invasion and all related names and assets belong to their respective owners. The mod uses the Harmony library (MIT license); see `THIRD-PARTY-NOTICES.txt`.

## Building from source

See [BUILDING.md](BUILDING.md). The build is deterministic, so you can check that it gives the same `CosmicBots.exe` as the release.
