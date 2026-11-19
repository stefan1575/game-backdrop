# Game Backdrop

A Windows tray app: when a game on your list runs **windowed**, it strips the game's title bar
and border, centres it at its own size, and blacks out the rest of that monitor, taskbar
included. A game that fills its screen (fullscreen or borderless) is left to its own native
mode. It only moves and restyles the game's window from the outside, like Borderless Gaming:
nothing is injected into the game.

## Install

Each release is one self-contained `GameBackdrop.exe` on
[GitHub Releases](https://github.com/stefan1575/game-backdrop/releases): no installer, and no
.NET runtime to install. A package manager downloads it without the browser's Mark of the Web,
so Windows SmartScreen has nothing to warn about.

### With Scoop

```
scoop bucket add game-backdrop https://github.com/stefan1575/game-backdrop
scoop install game-backdrop
```

This repo is its own bucket (`bucket/game-backdrop.json`). Start it from the Start menu
(**Game Backdrop**), or run `GameBackdrop` in a shell.

- **Update:** quit it from the tray, then `scoop update game-backdrop`. Start at logon and
  Always run as administrator carry on: they point at Scoop's `current` folder, which follows
  the update.
- **Uninstall:** `scoop uninstall game-backdrop`. It first runs `GameBackdrop --uninstall`
  (below); if that fails, the uninstall stops and says why.

### With winget

Coming once the package is in winget's community repository:

```
winget install stefan1575.GameBackdrop
```

- **Update:** quit it from the tray, then `winget upgrade stefan1575.GameBackdrop`.
- **Uninstall:** winget can't run anything as it uninstalls, so first run
  `GameBackdrop --uninstall`, then `winget uninstall stefan1575.GameBackdrop`.

### Direct download

1. Download `GameBackdrop.exe` from the newest release and put it somewhere it will stay, e.g.
   `%LOCALAPPDATA%\Programs\GameBackdrop\GameBackdrop.exe`.
2. The browser marks the file as from the internet, and the exe isn't signed, so the first run
   brings up SmartScreen's **Windows protected your PC**: click **More info**, then **Run
   anyway**. (Or, before running it, right-click the file, **Properties**, tick **Unblock**.)
   It asks only once.
3. **Update:** quit it from the tray and replace the exe with the new one.
4. **Uninstall:** run `GameBackdrop.exe --uninstall`, then delete the exe.

### First run

It sits in the tray and, on this first run, ticks **Start at logon**: a `Game Backdrop.lnk`
shortcut in your Startup folder (`shell:startup`) starts it each time you sign in. Untick it in
the tray menu to stop that; the choice is kept in the settings file, so it stays off.

If you move the exe, run it once from its new place: with Start at logon on, the shortcut is
rewritten to point at the exe that's running.

A game that runs as administrator can only be changed by an app that does too. Tick **Always
run as administrator** in the tray menu, or click the notice such a game brings up: after one UAC
prompt, the app sets up a Task Scheduler task (`\Game Backdrop`, run level highest, for you
only) and every start goes through it, elevated and with no prompt. While the task is there it
also starts the app at logon, in place of the Startup shortcut. Untick it to delete the task; the
next start is a normal one. A task left pointing at an exe that moved is ignored: tick it again
from the new place.

The game list lives in `%APPDATA%\GameBackdrop\settings.json`:

```json
{
  "games": [
    { "exePath": "C:\\Program Files (x86)\\Steam\\steamapps\\common\\dota 2 beta\\game\\bin\\win64\\dota2.exe", "displayName": "Dota 2" }
  ],
  "startAtLogon": true
}
```

## Uninstall

`GameBackdrop.exe --uninstall` removes everything the app puts outside its own folder, for you:
it asks the running copy to quit (which puts every game back first) and waits for it, then
removes the Start at logon shortcut and the Always run as administrator task. The task, or a
copy running as administrator, needs administrator, so only then does it ask (one UAC prompt).
It exits with 0 when done, or 1, with the reason, when it couldn't; nothing running and nothing
set up is done at once. Scoop runs it for you; for winget and the direct download, run it
yourself before removing the exe.

The settings file stays: delete `%APPDATA%\GameBackdrop` too to remove your game list.

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
pushes to `main` (`gh run download <run id> --repo stefan1575/game-backdrop --name GameBackdrop`).

Pushing a `v<major>.<minor>.<patch>` tag (e.g. `git tag v0.1.0`, `git push origin v0.1.0`) builds,
tests and publishes the exe with that version, attaches it to a GitHub Release, and points the
Scoop manifest on `main` at it (its version, URL and hash). Until the first tag the manifest is a
`0.0.0` placeholder.

`GameBackdrop.exe --settings <path>` runs against another settings file and keeps its Start at
logon shortcut in a `Startup` folder beside that file, and its Always run as administrator task
under a name of its own, leaving your real setup alone.
