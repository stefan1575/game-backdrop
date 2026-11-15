# Evidence harness

Produced the real-app screenshots and measurements in `pr-10/`. A console app that launches
`tools/TestWindow` and the real tray app (with a temp `--settings` file), records each test
window's style, outer rect and client rect, and saves pictures. It quits the app through its real
tray menu (UI Automation).

```
dotnet build -c Release -o out
out\Evidence.exe <outDir> <main|wrap|round3> <GameBackdrop.exe> <TestWindow.exe> <temp settings.json>
```

Scenarios live in `Program.cs` (`main`: windowed, menus, fullscreen, Quit; `wrap`: wrapped menu;
`round3`: a game changing its own frame, a game minimised at Quit). Add one per PR's checks.

Pictures show only the test window's own pixels (`PrintWindow`) at its real position on an
outline of the monitor, so nothing else on the screen is captured. Once the app draws its own
black backdrop (#3), a picture needs the backdrop too: capture that monitor from the screen
(`Graphics.CopyFromScreen`) while the backdrop covers it, which still shows nothing but the game
and black. Say which way each picture was made.

`side-by-side.ps1` and `grid.ps1` combine pictures for a before/after view.
