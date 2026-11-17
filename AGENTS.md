# Game backdrop

A Windows tray app: when a game on Stefan's list runs **windowed**, it strips the game's title
bar and border, centres it at its own size, and blacks out the rest of that monitor, taskbar
included, for full immersion. A game that fills its screen (fullscreen or borderless) is left to
its own native mode. The spec is the GitHub issue labelled `spec`; its tickets are sub-issues.

Windows only (Stefan, 2026-11-15): Linux was considered and dropped, because Wayland doesn't let
an outside app move another app's window, so a shared backend wouldn't come for free.

**On GitHub as the private `stefan1575/game-backdrop`.** Commit after every completed step, with
a message that says what changed and why. The queue runner on Stefan's PC reviews every PR
(`~/Code/Personal/queue-runner/docs/reviews.md`), and Stefan merges.

## Build and test

C# on .NET 10 (WinForms), SDK from scoop (`dotnet-sdk`).

```
dotnet build
dotnet test
dotnet publish src/GameBackdrop -c Release   # single-file exe
```

Checking the real Win32 path by hand: `tools/TestWindow` is a stand-in game (keys W windowed,
F borderless fullscreen, R next size; or `--mode fullscreen`, `--size 1280x720`). List its exe
in a settings file and start the app with `--settings <path>` to leave the real
`%APPDATA%\GameBackdrop\settings.json` alone.

Layout: `src/GameBackdrop.Core` (no Win32: settings, game list, classifier, placement, and the
`BackdropController` over the `IWindowSystem` port), `src/GameBackdrop.Windows` (the Win32
port), `src/GameBackdrop` (tray app), `tests/GameBackdrop.Core.Tests` (a `FakeWindowSystem`
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

## Agent skills

### Issue tracker

Issues and the spec live in this repo's GitHub Issues (`gh`). See `docs/agents/issue-tracker.md`.

### Triage labels

The five default labels: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`,
`wontfix`. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: `GLOSSARY.md` and `docs/adr/` at the root, created when needed. See
`docs/agents/domain.md`.
