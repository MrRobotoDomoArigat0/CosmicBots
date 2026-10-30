# Building CosmicBots from source

CosmicBots is a small launcher (`CosmicBots.exe`) that starts MARVEL Cosmic Invasion in its own
AppDomain and applies runtime patches with [Harmony](https://github.com/pardeike/Harmony). The game
files are never modified. This repository contains only the mod's own source code. It does **not**
contain any game code, game assemblies or assets.

## Requirements

- Windows 10/11 (x64). The resulting exe targets .NET Framework 4.7 (`net47`), which ships with Windows.
- [.NET SDK 8.0 or newer](https://dotnet.microsoft.com/download), or Visual Studio 2022 with the ".NET desktop development" workload.
  The .NET Framework 4.7 reference assemblies are pulled from NuGet (`Microsoft.NETFramework.ReferenceAssemblies`), so no
  targeting pack needs to be installed.
- Internet access for the first build (NuGet restore: `Lib.Harmony` 2.3.3 and the reference assemblies).
- **Your own legitimate copy of MARVEL Cosmic Invasion** (Steam). The mod compiles against the game's
  `Game.exe`, `ParisEngine.dll`, `FNA.dll`, `NLog.dll` and `Newtonsoft.Json.dll`. These are only
  read at compile time and are never copied to the output. They are not included in this repository.
  The release was built against Steam build 1.0.0.13466. Other builds may fail to compile or may
  produce a different exe.

## Build

Default Steam location:

```
git clone https://github.com/MrRobotoDomoArigat0/CosmicBots.git
cd CosmicBots
dotnet build -c Release
```

The csproj looks for the game in
`C:\Program Files (x86)\Steam\steamapps\common\MARVEL Cosmic Invasion`.
If your game is somewhere else, set `GameDir` to the folder that contains `Game.exe`:

```
dotnet build -c Release -p:GameDir="D:\Games\MARVEL Cosmic Invasion"
```

(In Visual Studio: open `CosmicBots.csproj`, set the `GameDir` property in the csproj or pass it with
`/p:GameDir=...`, choose the Release configuration and build.)

If `Game.exe` cannot be found, the build stops with a message that says so.

## Output

`bin\Release\` contains exactly the three files that make up the mod:

| File | What it is |
| --- | --- |
| `CosmicBots.exe` | the launcher and mod (built from this source) |
| `CosmicBots.exe.config` | generated runtime config (`supportedRuntime` .NET Framework 4.7) |
| `0Harmony.dll` | Harmony 2.3.3 from NuGet (`Lib.Harmony`), unmodified (it already contains MonoMod and Mono.Cecil) |

The release zip also contains `README.txt` and `THIRD-PARTY-NOTICES.txt`; those are plain text files and are not built.

To install, copy those three files into the game folder (next to `Game.exe`) and start the game with `CosmicBots.exe`.
See the README for details.

## Comparing with the release

The build is deterministic (`Deterministic`, `ContinuousIntegrationBuild`, `PathMap`, no pdb), so building
from this source gives a byte-identical `CosmicBots.exe` regardless of where you checked it out, as long as the
same game build is used for the reference assemblies. In PowerShell:

```
Get-FileHash bin\Release\CosmicBots.exe, bin\Release\CosmicBots.exe.config, bin\Release\0Harmony.dll -Algorithm SHA256
```

Expected SHA-256 for release 0.4.0-test1:

```
b18b68e1c6509f893f0e8947f690a1e727d37008df9feed12ef11d78bc36fcdc  CosmicBots.exe
03940fcfcc5c386db34cf8b2f9ff9db8e6c8809b44bfc6d6c085e2919956110e  CosmicBots.exe.config
4c06bb6d9d75785f9b64fd551fc75d54af9d5a5458a43cfb5bf52a5a22b24e6e  0Harmony.dll
```

To compare against the downloaded release, extract the zip and run `Get-FileHash` on the same three files.
If the exe hash differs, the usual cause is a different compiler version or a different game build used
as reference. The `CosmicBots.exe` in the release was produced with the .NET SDK 8 compiler.

## Notes on what the code does (for reviewers)

- `Loader.cs` / `Mod.cs`: launcher, AppDomain setup and starting the game's `Main`.
- `GamePatches.cs`, `GameRefl.cs`: Harmony hooks into the game (looked up by type and method name).
- `Telemetry.cs`: **disables** the game's crash reporting and outbound HTTP (NBug, Discord webhook, web services, analytics). The mod itself makes no network connections.
- `Bot*.cs`, `Menu*.cs`, `Config.cs`: the bot AI and the in-game F7 menu.
- `SteamFix.cs`: sets the Steam app id environment variables so the game starts when launched outside Steam.

## License

The mod's source code is MIT licensed (see `LICENSE`). Harmony and its bundled components are covered by
`THIRD-PARTY-NOTICES.txt`. MARVEL Cosmic Invasion is not part of this project and is not covered by this license.
