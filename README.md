# Game Backdrop

A Windows tray app: when a game on your list runs **windowed**, it strips the game's title bar
and border, centres it at its own size, and blacks out the rest of that monitor, taskbar
included. A game that fills its screen (fullscreen or borderless) is left to its own native
mode. It only moves and restyles the game's window from the outside, like Borderless Gaming:
nothing is injected into the game.

## Install

With [Scoop](https://scoop.sh):

```
scoop bucket add game-backdrop https://github.com/stefan1575/game-backdrop
scoop install game-backdrop
```

That adds **Game Backdrop** to the Start menu. `scoop update game-backdrop` gets a new release;
quit the app from its tray first, as Scoop won't replace a running exe.

Or download `GameBackdrop.exe` from the newest
[release](https://github.com/stefan1575/game-backdrop/releases/latest) and put it somewhere it
will stay, e.g. `%LOCALAPPDATA%\Programs\GameBackdrop\`. It's one self-contained file: no .NET
runtime needs installing. The exe isn't code-signed, so the first time you run a downloaded copy
Windows shows **Windows protected your PC**: click **More info**, then **Run anyway**. (Scoop
doesn't trigger that: it downloads outside the browser.)

Run it once. It sits in the tray and, on this first run, ticks **Start at logon**: a
`Game Backdrop.lnk` shortcut in your Startup folder (`shell:startup`) starts it each time you
sign in. Untick it in the tray menu to stop that; the choice is kept in the settings file, so it
stays off.

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

With Scoop: `scoop uninstall game-backdrop`.

A copy you downloaded: run `GameBackdrop.exe --uninstall`, then delete the exe. A copy from
before the first release (a CI artifact) can't be asked to quit: quit it from its tray first.

Either way, `--uninstall` (Scoop runs it for you) quits a running copy, which puts every game
back, and removes the Start at logon shortcut and the Always run as administrator task. With the
task set up, or the app running as administrator, it asks for administrator once. Your game list
stays in `%APPDATA%\GameBackdrop`; delete that folder to remove it too.

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

Pushing a version tag (`git tag v0.1.0`, `git push origin v0.1.0`) makes a release: the
`Release` workflow builds and tests that version, attaches the exe to a GitHub Release, and
points the Scoop manifest (`bucket/game-backdrop.json`) on `main` at it, through
`tools/update-scoop-manifest.ps1`.

`GameBackdrop.exe --settings <path>` runs against another settings file and keeps its Start at
logon shortcut in a `Startup` folder beside that file, and its Always run as administrator task
under a name of its own, leaving your real setup alone.

## License

MIT: see [LICENSE](LICENSE).
