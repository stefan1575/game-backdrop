# Evidence harness

Produced the real-app screenshots and measurements in `pr-10/`. A console app that launches
`tools/TestWindow` and the real tray app (with a temp `--settings` file), records each test
window's style, outer rect and client rect, and saves pictures. It quits the app through its real
tray menu (UI Automation).

```
dotnet build -c Release -o out
out\Evidence.exe <outDir> <main|wrap|round3|backdrop|fgprobe|fgprobe2|live|list|always-admin> <GameBackdrop.exe> <TestWindow.exe> <temp settings.json>
```

Scenarios live in `Program.cs` (`main`: windowed, menus, fullscreen, Quit; `wrap`: wrapped menu;
`round3`: a game changing its own frame, a game minimised at Quit). Add one per PR's checks.

- `backdrop` (PR #12, `pr-12/`): the black backdrop through restyle, another app in front, a real
  click on the black (only after `WindowFromPoint` says the backdrop is there; the cursor is put
  back), minimise and restore, hide and show, Quit, game exit, and a borderless-fullscreen game;
  then one `fgprobe2` round. Each step logs the backdrop's rect, visibility and ex-style, what is
  directly below the game, the foreground, whether the taskbar is above the backdrop, and the
  pure-black shares of the taskbar strip and the rest of the monitor.
- `fgprobe` (PR #12 diagnosis): Z positions of the game and its backdrop through activation,
  minimise and restore, another app, hide and show. Log only.
- `live` (PR #14, `pr-14/`): a game switching itself to fullscreen, back to windowed and to a
  new resolution; a maximised frameless game that goes topmost and fills its monitor; a game
  disabled (as under a modal dialog) at Quit; a fullscreen game opened while the app runs. Run on
  main for the `before-` pictures and on the PR for the `after-` ones.
- `list` (PR #18, `pr-18/`): the tray menu, "Open list", the "Add from open windows" picker
  with the test window picked, the list after "Add", and a second launch (does the running copy's
  list open?). The settings start with one switched-off game. A sixth argument names the
  pictures' prefix (`before` on main, `after` on the PR). Window pictures are the app's own
  windows (`PrintWindow`), the tray menu from the screen over its own rect; in the picker, rows
  of other programs' windows are painted over before the picture is saved. Buttons that open a
  modal dialog are clicked with a posted `BM_CLICK`, not UI Automation's Invoke, which would
  leave later UIA calls into the app timing out.
- `admin` (PR #20, `pr-20/`): a game running as administrator. `elevated-game.ps1` (copied
  next to `Evidence.exe`) starts TestWindow elevated and closes it, and the elevated app copy,
  on signal files, since the unelevated harness can't. A person answers the UAC prompts (accept
  for the game, decline from the notice, accept from the menu). Logs the untouched game, the
  notice's text, the tray menu, the takeover and the elevated copy's tray icon name. The whole
  monitor is captured only with the game in front and no other window over that monitor.
- `fgprobe2` (PR #12 diagnosis, defect A): a harness window is the foreground while the game is
  restyled, then the game is activated; three rounds. Log only.

The harness is per-monitor DPI aware, so rects and captures are in physical pixels.

Pictures show only the test window's own pixels (`PrintWindow`) at its real position on an
outline of the monitor, so nothing else on the screen is captured. Once the app draws its own
black backdrop (#3), a picture needs the backdrop too: capture that monitor from the screen
(`Graphics.CopyFromScreen`) while the backdrop covers it, which still shows nothing but the game
and black. Say which way each picture was made.

`side-by-side.ps1` and `grid.ps1` combine pictures for a before/after view.
- `always-admin` (#31): the tray menu with no game running as administrator, from the screen over
  the menu's own rect. A sixth argument names the prefix (`before` on main, `after` on the PR).
- `list`, reused for #33 (`pr-34/`): the list window's title-bar icon, `before` on main and
  `after` on the PR. `exe-icon.png` is each exe's own icon as Explorer gets it
  (`Icon.ExtractAssociatedIcon`, drawn at 32 px and at 64 px).
- `pr-34/after-uninstall-still-running.png` (#33): `harness/pr-34/stubborn-copy.ps1` holds the
  single-instance mutex and a test scope's quit event and never quits, standing in for a copy
  with a game it can't put back; `GameBackdrop.exe --settings <that scope> --uninstall` waits its
  minute and shows the dialog, which `capture-dialog.ps1` captures from its own pixels
  (`PrintWindow`) and closes. `after-start-menu-entry.png`: after a `scoop install` from a local
  manifest, the Start menu shortcut's own name and icon (`Icon.ExtractAssociatedIcon` on the
  `.lnk`), drawn rather than captured, since the Start menu shows other programs. Neither has a
  `before`: main has no `--uninstall` and no Scoop package.
- `pr-34/before-tray-icon.png`, `after-tray-icon.png` (#33): `harness/pr-34/tray-icon.ps1 -Exe
  <published exe>` starts the app with a scratch `--settings` file, promotes only that exe's own
  `HKCU\Control Panel\NotifyIconSettings` entry (Windows 11 puts a new icon in the overflow),
  asks the shell for the icon's rect (`Shell_NotifyIconGetRect`, uID 1), copies just that rect
  from the screen, then stops the app and deletes the entry. Run on main's exe and the PR's.
- `desktops` (#40, `pr-41/`): a windowed game with its backdrop on the current virtual desktop;
  the harness makes a new, empty desktop with Ctrl+Win+D, captures that monitor, closes the desktop
  with Ctrl+Win+F4 and steps back with Ctrl+Win+Left until the game's desktop is current. It logs
  each window's desktop (`IVirtualDesktopManager`), its cloak state, and the game's and backdrop's
  `EVENT_OBJECT_CLOAKED` / `UNCLOAKED` events. The capture shows the wallpaper, so it is pixelated
  (40 px blocks) after the black shares are measured; the taskbar and other windows over the
  monitor are masked, except full-monitor `WS_EX_LAYERED` overlays (GPU and cursor overlays), which
  draw nothing there and would hide the whole picture. A sixth argument names the prefix (`before`
  on main, `after` on the PR). It switches the signed-in user's desktop, and the test copy of the
  app can't run beside a running copy (the single-instance mutex isn't scoped by `--settings`), so
  quit the real one first.
