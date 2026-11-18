# Game Backdrop

A Windows tray app: when a game on your list runs **windowed**, it strips the game's title bar
and border, centres it at its own size, and blacks out the rest of that monitor, taskbar
included. A game that fills its screen (fullscreen or borderless) is left to its own native
mode. It only moves and restyles the game's window from the outside, like Borderless Gaming:
nothing is injected into the game.

## Install

1. Get `GameBackdrop.exe`: publish it yourself (below), or download the `GameBackdrop`
   artifact from the newest CI run on `main`:

   ```
   gh run list --repo stefan1575/game-backdrop --branch main --event push --status success --limit 1 --json databaseId --jq ".[0].databaseId"
   gh run download <that id> --repo stefan1575/game-backdrop --name GameBackdrop
   ```

2. Put it somewhere it will stay, e.g. `%LOCALAPPDATA%\Programs\GameBackdrop\GameBackdrop.exe`.
   It's one self-contained file: no .NET runtime needs installing.
3. Run it once. It sits in the tray and, on this first run, ticks **Start at logon**: a
   `Game Backdrop.lnk` shortcut in your Startup folder (`shell:startup`) starts it each time
   you sign in. Untick it in the tray menu to stop that; the choice is kept in the settings
   file, so it stays off.

If you move the exe, run it once from its new place: with Start at logon on, the shortcut is
rewritten to point at the exe that's running.

The game list lives in `%APPDATA%\GameBackdrop\settings.json`:

```json
{
  "games": [
    { "exePath": "C:\\Program Files (x86)\\Steam\\steamapps\\common\\dota 2 beta\\game\\bin\\win64\\dota2.exe", "displayName": "Dota 2" }
  ],
  "startAtLogon": true
}
```

To uninstall: untick **Start at logon**, quit from the tray, then delete the exe and
`%APPDATA%\GameBackdrop`.

## Build, test and publish

Needs the .NET 10 SDK or newer (`scoop install dotnet-sdk`).

```
dotnet build
dotnet test
dotnet publish src/GameBackdrop -c Release
```

The publish leaves one exe, self-contained for win-x64, at
`src/GameBackdrop/bin/Release/net10.0-windows/win-x64/publish/GameBackdrop.exe`. CI builds,
tests and publishes on every push and PR, and keeps the exe as the `GameBackdrop` artifact on
pushes to `main`.

`GameBackdrop.exe --settings <path>` runs against another settings file and keeps its Start at
logon shortcut in a `Startup` folder beside that file, leaving your real setup alone.
