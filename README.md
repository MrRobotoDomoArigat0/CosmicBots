# CosmicBots
COSMICBOTS v0.4.0 - PUBLIC STEAM TEST

AI teammates for MARVEL Cosmic Invasion STEAM (offline local play)

CosmicBots v0.4.1 Xbox Public Test (Xbox app / PC Game Pass, experimental)

AI bot teammates (P2–P4) for MARVEL Cosmic Invasion, now for the Xbox app / PC Game Pass version. Offline local play only. Experimental: tested on game build 1.0.0.13466 (Xbox package 26.6.8.0).



Copyright (c) 2026 MrRobotoDomoArigato
https://www.youtube.com/@MrRobotoDomoArigato
Feel free to reach out to me to report any issues or features with the bot you would like to see.
==================================================================
The Xbox app / PC Game Pass version is now supported and separated by a 2nd release.


IMPORTANT - PLEASE READ
-----------------------
CosmicBots is an UNOFFICIAL FAN MOD. It is not affiliated with, endorsed by,
or supported by Marvel, Tribute Games, Dotemu or Valve. You need your own
legitimate copy of MARVEL Cosmic Invasion on Steam. This download contains
no game code and no game assets - only the mod itself.

This is a first public test. Expect rough edges, and please report what you
find (see TROUBLESHOOTING).


WHAT IT DOES
------------
Adds computer-controlled teammates ("bots") for players 2, 3 and 4 in
OFFLINE LOCAL play, so you can play a 2-4 player game on your own. The bots
fight, block, parry, dodge, sprint, use assists and team specials, escape
enemy rushes, follow your lead on the path-select screen, and stay with you
through the level.


STEAM INSTALL
-------
1. Close the game.
2. In Steam, right-click MARVEL Cosmic Invasion > Manage > Browse local files.
   An Explorer window opens on the game folder (the one that contains Game.exe).
3. Unzip the contents of the 0.4.0 zip into THAT folder, so these files sit
   right next to Game.exe:
       CosmicBots.exe
       CosmicBots.exe.config
       0Harmony.dll
4. Make sure Steam is open and you are logged in.
5. Start the game by double-clicking CosmicBots.exe (instead of pressing
   Play in Steam). CosmicBots starts the normal game for you.

To uninstall: close the game and delete CosmicBots.exe, CosmicBots.exe.config,
0Harmony.dll and, if you like, CosmicBots.ini, CosmicBots.log and
CosmicBots.prev.log. Nothing else was changed. Starting the game from Steam
as usual always gives you the unmodded game.

Tip: right-click CosmicBots.exe > Send to > Desktop (create shortcut) for an
easy launcher.

XBOX APP / PC GAME PASS VERSION Install
------
Extract the 0.4.1 zip somewhere (like your Desktop).

1. Go to your game folder, usually: C:\XboxGames\MARVEL Cosmic Invasion\Content ( Tip: Turn on *View > File name extensions* in File Explorer so you don't end up with Game.exe.exe ), Another way to reach the game folder (Xbox app: click the 3 dots (...) next to the game, then Manage, then Files, then Browse, then in explorer click into MARVEL Cosmic Invasion\Content)

2. Rename the game's **Game.exe** to exactly **Game-original.exe**

3. Unzip the mod if you haven't done so already, copy CosmicBots.exe into the folder and rename it to Game.exe, then copy 0Harmony.dll in too

4. Launch the game normally from the Xbox app (Play Button)

Press F7 in-game, use arrow keys to turn bots on or off

- Game updates replace Game.exe, so redo steps 2 and 3 after each update

- To uninstall, delete the mod's Game.exe and rename Game-original.exe back to Game.exe, or use the 3 dots (...) > Manage > Files > Verify and repair

- It works offline only, and blocks online multiplayer, crossplay, invites, Epic Online Services, telemetry and crash reporting while it runs. Xbox sign-in, achievements and cloud saves are left alone

- Tested on game build 1.0.0.13466.

USING THE BOTS - THE F7 MENU
----------------------------
Press F7 (keyboard) at any time to open or close the CosmicBots menu.
While it is open the game ignores the keyboard, so you will not move by accident.

  Up / Down        move through the menu
  Left / Right     change a value
  Enter / Space    switch an item on or off / press a button
  Esc or Backspace close the menu (so does F7)

Menu items:
  P2 bot / P3 bot / P4 bot   Switch a bot on or off for that player slot.
                             A bot joins at the character select screen (or
                             right away if you are already in a level) and
                             only takes a slot that no human is using.
     Hero / Partner          (shown for slots that are on) pick which hero
                             and partner hero that bot plays, or leave on
                             "automatic".
  Lock in bots now           On the character select screen, makes the bots
                             confirm their heroes. By default they also lock in
                             automatically when every human player is ready.
  Aggression                 How eager the bots are to charge in (0.50 - 2.00,
                             default 1.00).
  Bot skill                  Easy / Normal / Hard (default Hard): reaction time
                             and quality of decisions.
  Close                      Closes the menu.

Your choices are saved automatically in CosmicBots.ini (game folder).


NOTES
-----
* OFFLINE / LOCAL PLAY ONLY. Bots are switched off in online sessions and
  CosmicBots does not start the game's online services while it runs.
* While running through CosmicBots, the game's crash reporting and analytics
  reporting is switched off, and the mod never sends anything anywhere. (This
  applies only to the copy of the game started by CosmicBots.exe.)
* CosmicBots does not modify any game file. It starts the game and patches it
  in memory only while it runs. Your saves are not touched.
* CosmicBots tells the game its Steam App ID (2753970) for that one launch,
  as Steam itself would if you pressed Play. Steam must be running.
* The mod is tested on the Steam build 1.0.0.13466. A later game update may
  change things and break the mod until it is updated.


TROUBLESHOOTING
---------------
"Game.exe not found" / nothing happens / a message in CosmicBots.log:
  The three mod files must be in the same folder as Game.exe. Use Steam >
  right-click the game > Manage > Browse local files, and unzip there.

The game opens but there is no Steam / the game behaves oddly:
  Make sure the Steam client is running and logged into the account that owns
  the game, then start CosmicBots.exe again. CosmicBots.log says whether Steam
  started ("Steam: SteamAPI initialised").

Windows SmartScreen ("Windows protected your PC") or your antivirus flags
CosmicBots.exe, or deletes it:
  CosmicBots.exe is not code-signed, and it uses a runtime-patching library
  (Harmony) to change the game in memory - the same technique game trainers
  use - so some security tools are suspicious of it. That is the only reason.
  You can check the file is the one published here: its SHA256 is
      b18b68e1c6509f893f0e8947f690a1e727d37008df9feed12ef11d78bc36fcdc
  (PowerShell:  Get-FileHash CosmicBots.exe -Algorithm SHA256 )
  To allow it:
    - SmartScreen: click "More info" and then "Run anyway".
    - Defender / other antivirus: restore the file from quarantine and add the
      game folder (or CosmicBots.exe) to the exclusions list.
  If you are not comfortable with that, please do not use the mod.

".NET Framework" error on start:
  CosmicBots needs the Microsoft .NET Framework 4.x (4.7 or newer). It is
  already part of Windows 10 and 11. Older systems can install it from Microsoft.

The game updated and now crashes or the bots misbehave:
  Start the game from Steam (Play) to play unmodded, and wait for a CosmicBots
  update. Please tell me which game build you have.

Settings seem broken:
  Close the game and delete CosmicBots.ini; a fresh one with the defaults is
  created next time.

Asking for help - please send:
  1. CosmicBots.log (in the game folder; the previous run is in
     CosmicBots.prev.log)
  2. The game's own log:
     Documents\Tribute Games\MARVELCosmicInvasion\log.txt
  3. What you were doing, and your Windows version.
  Both logs are plain text. They may contain your Windows user name in file
  paths, so look them over first if that matters to you.


LICENSE / WARRANTY
------------------
CosmicBots is Copyright (c) 2026 MrRobotoDomoArigato. It is provided
free of charge, AS IS, with no warranty of any kind. Use it at your own risk.
MARVEL Cosmic Invasion and all related names and assets belong to their
respective owners. The mod uses the Harmony library (MIT license) - see
THIRD-PARTY-NOTICES.txt.
