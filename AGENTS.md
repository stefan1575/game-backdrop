# Game backdrop

A Windows tray app: when a game on the user's list runs **windowed**, it strips the game's title
bar and border, centres it at its own size, and blacks out the rest of that monitor, taskbar
included, for full immersion. A game that fills its screen (fullscreen or borderless) is left to
its own native mode. The spec is the GitHub issue labelled `spec`; its tickets are sub-issues.

Windows only (2026-11-15): Linux was considered and dropped, because Wayland doesn't let
an outside app move another app's window, so a shared backend wouldn't come for free.

Commit after every completed step, with a message that says what changed and why.

## Build and test

C# on .NET 10 (WinForms), SDK from scoop (`dotnet-sdk`).
If `dotnet` reports no SDKs, put Scoop's SDK first on PATH for this shell:
`$env:PATH = "$env:USERPROFILE\scoop\apps\dotnet-sdk\current;$env:PATH"`.

```
dotnet build
dotnet test
dotnet publish src/GameBackdrop -c Release   # single-file exe
```

Checking the real Win32 path by hand: `tools/TestWindow` is a stand-in game (keys W windowed,
F borderless fullscreen, R next size; or `--mode fullscreen`, `--size 1280x720`). List its exe
in a settings file and start the app with `--settings <path>` to leave the real
`%APPDATA%\GameBackdrop\settings.json` alone; such a run also keeps its Start at logon shortcut in
a `Startup` folder beside that settings file, never in the real Startup folder.

A game running as administrator can be stood in for without a UAC prompt: run a copy of the
app at Low integrity (`icacls <copy>\GameBackdrop.exe /setintegritylevel low`, then start it)
beside a normal TestWindow. Windows refuses its restyle with the same access denied an elevated
game gives a normal app, though a Low app's tray notice may not show. Start it detached
(`cmd /c start`): a shell call that starts it directly can hang.

Screenshots (a visible change needs a before and after): use the evidence harness on the
`evidence` branch (`harness/README.md`). Add a scenario for the PR, run it on `main` for `before-`
and on the PR for `after-`. Never capture the whole desktop: it shows other programs.
A state a script can't reach (a UAC prompt) is `Screenshot owed: <why>`.

Layout: `src/GameBackdrop.Core` (no Win32: settings, game list, classifier, placement, and the
`BackdropController` over the `IWindowSystem` port, and `StartAtLogon` over the
`IStartupShortcut` port), `src/GameBackdrop.Windows` (the Win32
side of both ports), `src/GameBackdrop` (tray app), `tests/GameBackdrop.Core.Tests` (a `FakeWindowSystem`
drives the controller), `tests/GameBackdrop.Windows.Tests` (the Win32 port against real
`TestWindow` windows; `dotnet test` briefly opens a few of them, and they need the solution
built first in the same configuration).

## Ground rules

- **Never inject into a game.** The app only moves, restyles and orders a game's window from the
  outside (Win32 calls on its handle), the same as Borderless Gaming does. Anti-cheat (Dota 2's
  VAC) must have nothing to object to: no DLL injection, no hooks inside the game's process, no
  memory reads.
- **Always put a game back.** Whatever the app changed on a game window (style, position) is
  restored when the game, the app, or the backdrop for that game stops.
- The decisions (fullscreen or windowed, where to centre, which monitor) live in a core with no
  Win32 calls, so they're unit-tested; the Win32 layer stays thin.

## Agent docs

### Issue tracker

Issues and the spec live in this repo's GitHub Issues (`gh`). See `docs/agents/issue-tracker.md`.

### Triage labels

The five default labels: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`,
`wontfix`. See `docs/agents/triage-labels.md`.

### Domain docs

Before exploring code, read `docs/agents/domain.md` for the repo's optional glossary and ADRs.
