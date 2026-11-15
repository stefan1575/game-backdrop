using System.IO;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

// Usage: Evidence <outDir> <scenario> <appExe> <testWindowExe> <settingsPath>
// scenario "main": the PR's manual checks on the given app. Scenario "wrap": only the wrapped-menu window.
// Scenario "backdrop": PR #12's black backdrop checks, then one fgprobe2 round. "fgprobe", "fgprobe2": PR #12 diagnosis.
W.PerMonitorDpiAware(); // rects and screen captures in physical pixels
var (outDir, scenario, appExe, testExe, settingsPath) = (args[0], args[1], args[2], args[3], args[4]);
Directory.CreateDirectory(outDir);
File.WriteAllText(settingsPath, JsonSerializer.Serialize(new { games = new[] { new { exePath = testExe.ToUpperInvariant(), displayName = "Test window" } } }));
var log = new List<object>();
var started = new List<Process>();
var screen = Screen.PrimaryScreen!;

Process Launch(string exe, string arguments)
{
    var p = Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false })!;
    started.Add(p);
    return p;
}

nint WaitWindow(Process p)
{
    for (var i = 0; i < 100; i++)
    {
        var h = W.TestWindowOf(p.Id);
        if (h != 0) { Thread.Sleep(400); return h; }
        Thread.Sleep(100);
    }
    throw new Exception("test window never appeared");
}

void Shot(string name, string scene, nint hwnd)
{
    var d = W.Describe(hwnd);
    log.Add(new { name, scene, style = $"0x{d.Style:X8}", ex = $"0x{d.Ex:X8}", outer = d.Outer.ToString(), client = d.Client.ToString() });
    using var win = W.Print(hwnd, d.Outer);
    var mon = screen.Bounds; var work = screen.WorkingArea;
    using var canvas = new Bitmap(mon.Width, mon.Height);
    using var g = Graphics.FromImage(canvas);
    g.Clear(Color.FromArgb(28, 28, 30));
    if (work.Bottom < mon.Bottom)
    {
        using var tb = new SolidBrush(Color.FromArgb(48, 48, 52));
        g.FillRectangle(tb, mon.Left, work.Bottom, mon.Width, mon.Bottom - work.Bottom);
        using var f2 = new Font("Segoe UI", 16);
        g.DrawString("taskbar area (not captured)", f2, Brushes.Gray, 20, work.Bottom + 10);
    }
    g.DrawImage(win, d.Outer.X - mon.Left, d.Outer.Y - mon.Top);
    using var pen = new Pen(Color.FromArgb(90, 90, 96), 4);
    g.DrawRectangle(pen, 2, 2, mon.Width - 4, mon.Height - 4);
    using var font = new Font("Segoe UI", 22, FontStyle.Bold);
    using var small = new Font("Consolas", 18);
    // Captions at the top, or under the window when it reaches up there.
    var cy = d.Outer.Y - mon.Top < 140 ? Math.Min(d.Outer.Y - mon.Top + d.Outer.Height + 30, work.Bottom - mon.Top - 130) : 24;
    g.DrawString(scene, font, Brushes.White, 30, cy);
    g.DrawString($"style 0x{d.Style:X8}   outer {d.Outer}   client {d.Client}   monitor {mon.Width}x{mon.Height}", small, Brushes.LightGray, 30, cy + 40);
    g.DrawString("Only the test window's own pixels (PrintWindow) at its real screen position; the rest of the screen isn't captured.", small, Brushes.Gray, 30, cy + 72);
    canvas.Save(Path.Combine(outDir, name + ".png"), ImageFormat.Png);
    Console.WriteLine($"{name}: style 0x{d.Style:X8} outer {d.Outer} client {d.Client}");
}

void Quit(Process app)
{
    // The tray icon's own menu: WinForms' NotifyIcon opens its ContextMenuStrip on a right-button-up
    // callback message (WM_USER + 1024); then UI Automation invokes "Quit".
    foreach (var h in W.WindowsOf(app.Id)) W.PostMessage(h, 0x0400 + 1024, 0, 0x0205);
    for (var i = 0; i < 50; i++)
    {
        var quit = AutomationElement.RootElement.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, app.Id),
            new PropertyCondition(AutomationElement.NameProperty, "Quit")));
        if (quit is not null)
        {
            ((InvokePattern)quit.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            if (!app.WaitForExit(10000)) throw new Exception("app didn't exit after Quit");
            Console.WriteLine($"app quit through its tray menu, exit code {app.ExitCode}");
            Thread.Sleep(600);
            return;
        }
        Thread.Sleep(100);
    }
    throw new Exception("tray menu's Quit item never appeared");
}

void WaitChanged(nint hwnd, uint styleBefore)
{
    for (var i = 0; i < 50 && W.Describe(hwnd).Style == styleBefore; i++) Thread.Sleep(100);
    Thread.Sleep(500);
}

void WaitBackdrop(int appPid)
{
    for (var i = 0; i < 50; i++)
    {
        if (W.Backdrops(appPid).Any(W.IsVisible)) { Thread.Sleep(300); return; }
        Thread.Sleep(100);
    }
    Console.WriteLine("warning: no visible backdrop after 5 s");
}

// Every measurement PR #12's checks ask for, for one step. appPid 0: the app isn't running, so any
// GameBackdrop.Backdrop window on the desktop is listed.
Dictionary<string, object?> Measure(nint game, int appPid, nint other)
{
    var d = W.Describe(game);
    var backdrops = appPid == 0 ? W.WindowsOfClass("GameBackdrop.Backdrop") : W.Backdrops(appPid);
    var tray = W.FindTray();
    var fg = W.Foreground();
    var below = W.NextVisibleBelow(game);
    string Name(nint h) => h == 0 ? "none" : h == game ? "game" : h == other ? "Another app" : backdrops.Contains(h) ? "backdrop" : h == tray ? "Shell_TrayWnd" : $"other window (class {W.ClassOf(h)})";
    var m = new Dictionary<string, object?>
    {
        ["game"] = new { style = $"0x{d.Style:X8}", ex = $"0x{d.Ex:X8}", outer = d.Outer.ToString(), client = d.Client.ToString(), visible = W.IsVisible(game), minimised = W.Minimised(game) },
        ["backdrops"] = backdrops.Select(b =>
        {
            var bd = W.Describe(b);
            return new
            {
                rect = bd.Outer.ToString(), visible = W.IsVisible(b), ex = $"0x{bd.Ex:X8}",
                noActivate = (bd.Ex & 0x08000000) != 0, toolWindow = (bd.Ex & 0x80) != 0, topmost = (bd.Ex & 0x8) != 0,
            };
        }).ToList(),
        ["firstVisibleBelowGame"] = Name(below),
        ["firstVisibleBelowGameInfo"] = below == 0 || backdrops.Contains(below) ? null : W.Info(below, game),
        ["backdropDirectlyBehindGame"] = backdrops.Contains(below),
        ["foreground"] = Name(fg),
        ["taskbar"] = backdrops.Count == 0 ? "no backdrop" : W.ZIndex(tray) < W.ZIndex(backdrops[0]) ? "above the backdrop" : "below the backdrop",
    };
    if (other != 0 && W.IsVisible(other)) m["anotherAppAboveGame"] = W.ZIndex(other) < W.ZIndex(game);
    return m;
}

void Step(string name, string scene, nint game, int appPid, nint other, string picture, string? note = null)
{
    var m = Measure(game, appPid, other);
    m["name"] = name; m["scene"] = scene; m["picture"] = picture;
    if (note is not null) m["note"] = note;
    log.Add(m);
    Console.WriteLine($"{name}: {JsonSerializer.Serialize(m)}");
}

// A screen capture of the monitor, taken only while a visible backdrop covers all of it. Black shares
// are counted before anything is painted over. The taskbar strip is masked if the taskbar is above
// the backdrop; any other visible window above the backdrop that the harness didn't open is masked too.
void ScreenShot(string name, string scene, nint game, int appPid, string activated, nint other = 0, string? note = null)
{
    var m = Measure(game, appPid, other);
    m["name"] = name; m["scene"] = scene; m["activatedByHarness"] = activated;
    if (note is not null) m["note"] = note;
    var mon = screen.Bounds; var work = screen.WorkingArea;
    var cover = W.Backdrops(appPid).FirstOrDefault(b => W.IsVisible(b) && W.Describe(b).Outer == new W.R(mon.X, mon.Y, mon.Width, mon.Height));
    if (cover == 0)
    {
        m["picture"] = "none: no visible backdrop covers the monitor, so the screen wasn't captured";
        log.Add(m); Console.WriteLine($"{name}: NO COVERING BACKDROP, no capture. {JsonSerializer.Serialize(m)}");
        return;
    }
    var tray = W.FindTray();
    var foreign = W.VisibleAbove(cover).Where(h => h != game && h != other && h != tray && W.Intersects(h, mon)).ToList();
    using var shot = new Bitmap(mon.Width, mon.Height, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(mon.Left, mon.Top, 0, 0, mon.Size);
    var gd = W.Describe(game);
    var gameRect = new Rectangle(gd.Outer.X - mon.Left, gd.Outer.Y - mon.Top, gd.Outer.Width, gd.Outer.Height);
    var o = other != 0 && W.IsVisible(other) ? W.Describe(other).Outer : default;
    var otherRect = new Rectangle(o.X - mon.Left, o.Y - mon.Top, o.Width, o.Height);
    var strip = new Rectangle(0, work.Bottom - mon.Top, mon.Width, mon.Bottom - work.Bottom);
    m["blackShareTaskbarStrip"] = W.BlackShare(shot, (x, y) => strip.Contains(x, y));
    m["blackShareOutsideGame"] = W.BlackShare(shot, (x, y) => !gameRect.Contains(x, y) && !otherRect.Contains(x, y));
    m["blackShareOutsideGameArea"] = o.Width > 0 ? "monitor minus the game's outer rect and the Another app window's rect" : "monitor minus the game's outer rect";
    var taskbarAbove = W.ZIndex(tray) < W.ZIndex(cover);
    using (var g = Graphics.FromImage(shot))
    {
        using var label = new Font("Segoe UI", 16);
        using var maskBrush = new SolidBrush(Color.FromArgb(70, 70, 76));
        if (taskbarAbove)
        {
            g.FillRectangle(maskBrush, strip);
            g.DrawString("taskbar (masked; measured above the backdrop)", label, Brushes.White, 20, strip.Top + 10);
        }
        foreach (var h in foreign)
        {
            var r = W.Describe(h).Outer;
            // Masked except where the game or the harness's window is in front of it.
            using var region = new Region(new Rectangle(r.X - mon.Left, r.Y - mon.Top, r.Width, r.Height));
            if (W.ZIndex(game) < W.ZIndex(h)) region.Exclude(gameRect);
            if (o.Width > 0 && W.ZIndex(other) < W.ZIndex(h)) region.Exclude(otherRect);
            g.FillRegion(maskBrush, region);
            g.DrawString("other window (masked)", label, Brushes.White, r.X - mon.Left + 8, r.Y - mon.Top + 8);
        }
        using var font = new Font("Segoe UI", 20, FontStyle.Bold);
        using var small = new Font("Consolas", 15);
        // Captions sit at the top, or under the game when the harness's window is up there, clear of every window.
        var y0 = o.Width > 0 ? Math.Min(gameRect.Bottom + 30, strip.Top - 110) : 24;
        g.DrawString(scene, font, Brushes.Gray, 30, y0);
        g.DrawString($"Screen capture (CopyFromScreen) while the backdrop covers the monitor{(taskbarAbove ? "; taskbar strip masked" : "")}. Captions drawn after measuring.", small, Brushes.DimGray, 30, y0 + 40);
        g.DrawString($"game {gd.Outer}   pure black: taskbar strip {m["blackShareTaskbarStrip"]}, outside game {m["blackShareOutsideGame"]}   foreground: {m["foreground"]}", small, Brushes.DimGray, 30, y0 + 66);
    }
    m["picture"] = $"{name}.png: screen capture (CopyFromScreen) while the backdrop covers the monitor" + (taskbarAbove ? "; taskbar strip masked" : "") + (foreign.Count > 0 ? $"; {foreign.Count} other window(s) masked" : "");
    m["maskedOtherWindows"] = foreign.Select(h => W.Info(h, game)).ToList();
    shot.Save(Path.Combine(outDir, name + ".png"), ImageFormat.Png);
    log.Add(m);
    Console.WriteLine($"{name}: {JsonSerializer.Serialize(m)}");
}

// Defect A's check (PR #12): another app (a harness-owned window) is the foreground when the game is
// restyled; then the game is activated, and its backdrop must be directly behind it. No pictures.
void FgProbe2(int rounds)
{
    // Diagnosis for PR #12 with only harness-owned windows: another app is the foreground when the
    // game is restyled; then the game is activated. Also: does the taskbar stay behind after hide+show?
    var f = new Form { Text = "Another app", StartPosition = FormStartPosition.Manual, Location = new Point(100, 100), Size = new Size(2000, 1200) };
    nint oh = 0; var ready = new ManualResetEventSlim();
    var t = new Thread(() => { f.Shown += (_, _) => { oh = f.Handle; ready.Set(); }; Application.Run(f); }) { IsBackground = true };
    t.SetApartmentState(ApartmentState.STA); t.Start(); ready.Wait(10000);
    var gameProc = Launch(testExe, "--size 800x600");
    var game = WaitWindow(gameProc);
    nint app0 = 0; Process? app = null;
    void Z(string when)
    {
        var b = W.Backdrops(app!.Id).FirstOrDefault();
        var below = W.NextVisibleBelow(game);
        var fg = W.Foreground();
        var r = new Dictionary<string, object?> { ["name"] = "fgprobe2", ["when"] = when, ["foreground"] = fg == game ? "game" : fg == oh ? "Another app" : W.Info(fg, game), ["zGame"] = W.ZIndex(game), ["zBackdrop"] = W.ZIndex(b), ["zAnotherApp"] = W.ZIndex(oh), ["belowGame"] = below == b ? "backdrop" : below == oh ? "Another app" : W.Info(below, game), ["taskbarAboveBackdrop"] = W.ZIndex(W.FindTray()) < W.ZIndex(b) };
        log.Add(r); Console.WriteLine(JsonSerializer.Serialize(r));
    }
    for (var round = 1; round <= rounds; round++)
    {
        Console.WriteLine($"-- round {round}: {W.EnsureForeground(oh)} Another app");
        Thread.Sleep(500);
        var s0 = W.Describe(game).Style;
        app = Launch(appExe, $"--settings \"{settingsPath}\"");
        WaitChanged(game, s0);
        WaitBackdrop(app.Id);
        Z($"r{round} restyled while Another app is foreground");
        var a = W.EnsureForeground(game);
        Thread.Sleep(100);
        Z($"r{round} 100 ms after activating the game ({a})");
        Thread.Sleep(1000);
        Z($"r{round} 1.1 s after activating the game");
        W.Show(game, 0); Thread.Sleep(800); W.Show(game, 5); Thread.Sleep(300);
        var a2 = W.EnsureForeground(game); Thread.Sleep(1000);
        Z($"r{round} after hide + show + activate ({a2})");
        W.EnsureForeground(oh); Thread.Sleep(800); var a3 = W.EnsureForeground(game); Thread.Sleep(1000);
        Z($"r{round} after hide + show, Another app, then the game again ({a3})");
        Quit(app);
    }
    f.Invoke(() => f.Close());
    t.Join(3000);
    gameProc.Kill(); gameProc.WaitForExit();
}

try
{
    if (scenario == "main")
    {
        var plain = WaitWindow(Launch(testExe, "--size 800x600"));
        Shot("01-windowed-before", "1. Windowed test window, app not running", plain);
        var menu = WaitWindow(Launch(testExe, "--size 800x600 --menu"));
        Shot("04-menu-before", "4. Test window with a one-row menu bar, app not running", menu);
        var wrap = WaitWindow(Launch(testExe, "--size 400x600 --menu-items 14"));
        Shot("06-wrapped-menu-before", "6. Narrow test window whose 14-item menu wraps, app not running", wrap);

        var s1 = W.Describe(plain).Style;
        var app = Launch(appExe, $"--settings \"{settingsPath}\"");
        WaitChanged(plain, s1);
        Shot("02-windowed-restyled", "2. App started: border stripped, centred at its own size", plain);
        Shot("05-menu-restyled", "5. Menu bar kept, client area still 800x600, centred", menu);
        Shot("07-wrapped-menu-restyled", "7. Wrapped menu: client area kept (yellow border intact)", wrap);

        var fullProc = Launch(testExe, "--mode fullscreen");
        var full = WaitWindow(fullProc);
        Thread.Sleep(800);
        Shot("08-fullscreen-untouched", "8. Borderless-fullscreen test window opened while the app runs: left alone", full);

        Quit(app);
        Shot("03-windowed-restored", "3. App quit from its tray menu: original frame back", plain);
        Shot("05b-menu-restored", "5b. After quit: menu window back as it was", menu);
        Shot("07b-wrapped-menu-restored", "7b. After quit: wrapped-menu window back as it was", wrap);
        Shot("08b-fullscreen-after-quit", "8b. After quit: fullscreen window still untouched", full);
    }
    else if (scenario == "round3")
    {
        // Sol's round-3 cases on the real app: a game that changes its own frame after the
        // restyle (S1), and a game that is minimised when the app quits (S6).
        var self = WaitWindow(Launch(testExe, "--size 800x600"));
        var mini = WaitWindow(Launch(testExe, "--size 640x480"));
        Shot("r3-01-self-before", "S1: test window A, app not running", self);
        Shot("r3-04-min-before", "S6: test window B, app not running", mini);
        var s1 = W.Describe(self).Style;
        var app = Launch(appExe, $"--settings \"{settingsPath}\"");
        WaitChanged(self, s1);
        Thread.Sleep(300);
        W.AddExStyle(self, 0x200); // WS_EX_CLIENTEDGE: the game changes its own frame
        Thread.Sleep(400);
        Shot("r3-02-self-changed", "S1: app restyled A, then A added WS_EX_CLIENTEDGE itself", self);
        W.Show(mini, 6); // SW_MINIMIZE
        Thread.Sleep(600);
        Quit(app);
        Shot("r3-03-self-after-quit", "S1: after Quit, A should have its original frame back", self);
        W.Show(mini, 9); // SW_RESTORE
        Thread.Sleep(800);
        Shot("r3-05-min-reopened", "S6: B was minimised at Quit; reopened afterwards", mini);
    }
    else if (scenario == "backdrop")
    {
        // PR #12 (#3): the black backdrop bound to the game. Screen pictures are captured only while
        // a backdrop covers the whole monitor; other states are logged only.
        nint other = 0;
        var otherReady = new ManualResetEventSlim();
        Form? otherForm = null;

        var gameProc = Launch(testExe, "--size 800x600");
        var game = WaitWindow(gameProc);
        var original = W.Describe(game);
        Shot("b01-before", "1. Windowed 800x600 test window, app not running", game);
        Step("b01-before", "1. App not running", game, 0, 0, "b01-before.png: PrintWindow on an outline of the monitor");

        var app = Launch(appExe, $"--settings \"{settingsPath}\"");
        WaitChanged(game, original.Style);
        WaitBackdrop(app.Id);
        var activated = W.EnsureForeground(game);
        Thread.Sleep(1000);
        ScreenShot("b02-restyled", "2. App started: game centred, rest of the monitor black, taskbar behind", game, app.Id, activated);

        var t = new Thread(() =>
        {
            otherForm = new Form { Text = "Another app", StartPosition = FormStartPosition.Manual, Location = new Point(100, 100), Size = new Size(600, 400), BackColor = Color.WhiteSmoke };
            otherForm.Controls.Add(new Label { Text = "Another app (opened by the evidence harness)", AutoSize = true, Location = new Point(20, 20), Font = new Font("Segoe UI", 14) });
            otherForm.Shown += (_, _) => { other = otherForm.Handle; otherReady.Set(); };
            Application.Run(otherForm);
        });
        t.SetApartmentState(ApartmentState.STA); t.IsBackground = true; t.Start();
        otherReady.Wait(10000);
        var a3 = W.EnsureForeground(other);
        Thread.Sleep(1000);
        ScreenShot("b03-other-app", "3. Another app in front: above the game, the game above the backdrop", game, app.Id, a3, other);

        // Click only if the backdrop is what's under the point, so no other program gets the click.
        var hit = W.TopLevelAt(2400, 100);
        if (W.Backdrops(app.Id).Contains(hit))
        {
            var cursor = W.CursorPos();
            W.Click(2400, 100);
            W.SetCursor(cursor);
            Thread.Sleep(1000);
            ScreenShot("b04-click-black", "4. Real click on the black at (2400,100): focus back to the game", game, app.Id, "no (SendInput click only)", other, $"cursor put back at {cursor}");
        }
        else Step("b04-click-black", "4. Click on the black at (2400,100): NOT clicked", game, app.Id, other, "none",
            $"the top-level window at (2400,100) is not the backdrop but {W.Info(hit, game)}; the click was skipped so no other program receives it");

        W.Show(game, 6); // SW_MINIMIZE
        Thread.Sleep(1000);
        Step("b05-minimised", "5. Game minimised (SW_MINIMIZE)", game, app.Id, other, "log only (backdrop hidden: no screen capture)");

        W.Show(game, 9); // SW_RESTORE
        Thread.Sleep(300);
        var a6 = W.EnsureForeground(game);
        Thread.Sleep(1000);
        ScreenShot("b06-restored", "6. Game restored (SW_RESTORE): backdrop back, directly behind it", game, app.Id, a6, other);

        W.Show(game, 0); // SW_HIDE
        Thread.Sleep(1000);
        Step("b07a-hidden", "7a. Game hidden (SW_HIDE)", game, app.Id, other, "log only (backdrop hidden: no screen capture)");
        W.Show(game, 5); // SW_SHOW
        Thread.Sleep(300);
        var a7 = W.EnsureForeground(game);
        Thread.Sleep(1000);
        ScreenShot("b07b-shown", "7b. Game shown again (SW_SHOW): backdrop back, directly behind it", game, app.Id, a7, other);

        Quit(app);
        var after = W.Describe(game);
        Step("b08-after-quit", "8. App quit from its tray menu", game, 0, other, "b08-after-quit.png: PrintWindow on an outline of the monitor",
            $"original style 0x{original.Style:X8} outer {original.Outer} client {original.Client}; now style 0x{after.Style:X8} outer {after.Outer} client {after.Client}; equal: {after == original}");
        Shot("b08-after-quit", "8. App quit from its tray menu: original frame back, no backdrop", game);

        // 9. Game exit: a fresh test window under a fresh app, then the test window is killed.
        gameProc.Kill(); gameProc.WaitForExit();
        Thread.Sleep(500);
        var game9Proc = Launch(testExe, "--size 800x600");
        var game9 = WaitWindow(game9Proc);
        var s9 = W.Describe(game9).Style;
        var app9 = Launch(appExe, $"--settings \"{settingsPath}\"");
        WaitChanged(game9, s9);
        WaitBackdrop(app9.Id);
        var before9 = W.Backdrops(app9.Id).Count;
        var sw = Stopwatch.StartNew();
        game9Proc.Kill();
        long goneMs = -1;
        while (sw.ElapsedMilliseconds < 3000) { if (W.Backdrops(app9.Id).Count == 0) { goneMs = sw.ElapsedMilliseconds; break; } Thread.Sleep(10); }
        var r9 = new Dictionary<string, object?> { ["name"] = "b09-game-exit", ["scene"] = "9. Test window killed while the app runs", ["picture"] = "log only", ["backdropsBeforeKill"] = before9, ["backdropsAfter"] = W.Backdrops(app9.Id).Count, ["msUntilNoBackdrop"] = goneMs };
        log.Add(r9);
        Console.WriteLine($"b09-game-exit: {JsonSerializer.Serialize(r9)}");
        Quit(app9);

        // 10. Borderless fullscreen opened while a fresh app runs.
        var app10 = Launch(appExe, $"--settings \"{settingsPath}\"");
        Thread.Sleep(1500);
        var fullProc = Launch(testExe, "--mode fullscreen");
        var full = WaitWindow(fullProc);
        var f0 = W.Describe(full);
        Thread.Sleep(1500);
        var f1 = W.Describe(full);
        Step("b10-fullscreen", "10. Borderless-fullscreen test window opened while the app runs", full, app10.Id, other, "log only",
            $"at open style 0x{f0.Style:X8} outer {f0.Outer}; 1.5 s later style 0x{f1.Style:X8} outer {f1.Outer}; untouched: {f0 == f1}");
        Quit(app10);

        otherForm?.Invoke(() => otherForm.Close());
        t.Join(3000);
        fullProc.Kill(); fullProc.WaitForExit();
        Thread.Sleep(500);

        // Defect A's own check: one round of fgprobe2 after the scenario.
        FgProbe2(1);
    }
    else if (scenario == "fgprobe")
    {
        // Diagnosis for PR #12: does the game's Foreground event put the backdrop directly behind it?
        // No pictures, no clicks: Z positions of the game, its backdrop, and what's directly below the game.
        var game = WaitWindow(Launch(testExe, "--size 800x600"));
        var s0 = W.Describe(game).Style;
        var app = Launch(appExe, $"--settings \"{settingsPath}\"");
        WaitChanged(game, s0);
        WaitBackdrop(app.Id);
        void Z(string when)
        {
            var b = W.Backdrops(app.Id).FirstOrDefault();
            var below = W.NextVisibleBelow(game);
            var r = new Dictionary<string, object?> { ["name"] = "fgprobe", ["when"] = when, ["fgIsGame"] = W.Foreground() == game, ["zGame"] = W.ZIndex(game), ["zBackdrop"] = W.ZIndex(b), ["backdropVisible"] = W.IsVisible(b), ["belowGame"] = below == b ? "backdrop" : W.Info(below, game), ["taskbarAboveBackdrop"] = W.ZIndex(W.FindTray()) < W.ZIndex(b) };
            log.Add(r); Console.WriteLine(JsonSerializer.Serialize(r));
        }
        Z("after restyle, before activation");
        var a = W.EnsureForeground(game);
        Z($"100+ ms after activating the game ({a})");
        Thread.Sleep(2000);
        Z("2 s later");
        W.Show(game, 6); Thread.Sleep(800); W.Show(game, 9); Thread.Sleep(300); W.EnsureForeground(game); Thread.Sleep(1000);
        Z("after minimise + restore");
        var f = new Form { Text = "Another app", StartPosition = FormStartPosition.Manual, Location = new Point(100, 100), Size = new Size(600, 400) };
        nint oh = 0; var ready = new ManualResetEventSlim();
        var t = new Thread(() => { f.Shown += (_, _) => { oh = f.Handle; ready.Set(); }; Application.Run(f); }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA); t.Start(); ready.Wait(10000);
        W.EnsureForeground(oh); Thread.Sleep(1000);
        Z("Another app activated");
        var a2 = W.EnsureForeground(game); Thread.Sleep(1000);
        Z($"game activated again ({a2})");
        W.Show(game, 0); Thread.Sleep(800); W.Show(game, 5); Thread.Sleep(300); W.EnsureForeground(game); Thread.Sleep(1000);
        Z("after hide + show");
        W.EnsureForeground(oh); Thread.Sleep(800); W.EnsureForeground(game); Thread.Sleep(1000);
        Z("after hide + show, then Another app, then game again");
        f.Invoke(() => f.Close());
        Quit(app);
    }
    else if (scenario == "fgprobe2") FgProbe2(3);
    else if (scenario == "wrap")
    {
        var wrap = WaitWindow(Launch(testExe, "--size 400x600 --menu-items 14"));
        Shot("s5-before", "S5 before: wrapped-menu test window, app not running", wrap);
        var s = W.Describe(wrap).Style;
        var app = Launch(appExe, $"--settings \"{settingsPath}\"");
        WaitChanged(wrap, s);
        Shot("s5-old-build-restyled", "S5 on the old build (6a1d44f): bottom of the client area cut off", wrap);
        Quit(app);
    }
}
finally
{
    File.WriteAllText(Path.Combine(outDir, scenario + "-log.json"), JsonSerializer.Serialize(log, new JsonSerializerOptions { WriteIndented = true }));
    foreach (var p in started) { try { if (!p.HasExited) p.Kill(); } catch { } }
}

static class W
{
    public record struct R(int X, int Y, int Width, int Height) { public override string ToString() => $"({X},{Y}) {Width}x{Height}"; }
    public record D(uint Style, uint Ex, R Outer, R Client);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, Rt, B; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    delegate bool EnumProc(nint h, nint l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, nint l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(nint h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint h, out RECT r);
    [DllImport("user32.dll")] static extern bool GetClientRect(nint h, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(nint h, ref POINT p);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern nint GetWindowLongPtr(nint h, int i);
    [DllImport("user32.dll")] static extern bool PrintWindow(nint h, nint dc, uint flags);
    [DllImport("user32.dll")] public static extern bool PostMessage(nint h, uint m, nint w, nint l);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern nint SetWindowLongPtr(nint h, int i, nint v);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(nint h, int cmd);

    public static void AddExStyle(nint h, uint bits)
    {
        SetWindowLongPtr(h, -20, (nint)((uint)GetWindowLongPtr(h, -20) | bits));
        SetWindowPos(h, 0, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020); // NOSIZE NOMOVE NOZORDER NOACTIVATE FRAMECHANGED
    }

    public static void Show(nint h, int cmd) => ShowWindow(h, cmd);

    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(nint c);
    public static void PerMonitorDpiAware() => SetProcessDpiAwarenessContext(-4); // PER_MONITOR_AWARE_V2
    [DllImport("user32.dll")] static extern nint GetWindow(nint h, uint cmd);
    [DllImport("user32.dll")] static extern nint GetTopWindow(nint h);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint h);
    [DllImport("user32.dll")] static extern bool IsIconic(nint h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint FindWindow(string? cls, string? title);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, nint extra);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint h, int attr, out int v, int size);
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public nint extra; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public MOUSEINPUT mi; }

    public static object Info(nint h, nint game)
    {
        var d = Describe(h);
        GetWindowThreadProcessId(h, out var pid);
        string proc; try { proc = Process.GetProcessById((int)pid).ProcessName; } catch { proc = "?"; }
        return new { cls = ClassOf(h), process = proc, rect = d.Outer.ToString(), ex = $"0x{d.Ex:X8}", topmost = (d.Ex & 8) != 0, layered = (d.Ex & 0x80000) != 0, transparent = (d.Ex & 0x20) != 0, belowGame = ZIndex(game) < ZIndex(h) };
    }
    [DllImport("user32.dll")] static extern nint WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern nint GetAncestor(nint h, uint flags);
    public static nint TopLevelAt(int x, int y) => GetAncestor(WindowFromPoint(new POINT { X = x, Y = y }), 2); // GA_ROOT
    public static bool IsVisible(nint h) => IsWindowVisible(h);
    public static bool Minimised(nint h) => IsIconic(h);
    public static nint Foreground() => GetForegroundWindow();
    public static nint FindTray() => FindWindow("Shell_TrayWnd", null);
    public static string ClassOf(nint h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
    public static List<nint> Backdrops(int pid) => WindowsOf(pid).Where(h => ClassOf(h) == "GameBackdrop.Backdrop").ToList();
    public static List<nint> WindowsOfClass(string cls)
    {
        var list = new List<nint>();
        EnumWindows((h, _) => { if (ClassOf(h) == cls) list.Add(h); return true; }, 0);
        return list;
    }
    static bool Cloaked(nint h) => DwmGetWindowAttribute(h, 14, out var v, 4) == 0 && v != 0; // DWMWA_CLOAKED
    public static nint NextVisibleBelow(nint h)
    {
        for (var n = GetWindow(h, 2); n != 0; n = GetWindow(n, 2)) if (IsWindowVisible(n)) return n; // GW_HWNDNEXT
        return 0;
    }
    public static int ZIndex(nint target)
    {
        var i = 0;
        for (var n = GetTopWindow(0); n != 0; n = GetWindow(n, 2), i++) if (n == target) return i;
        return int.MaxValue;
    }
    public static List<nint> VisibleAbove(nint target)
    {
        var list = new List<nint>();
        for (var n = GetTopWindow(0); n != 0 && n != target; n = GetWindow(n, 2))
            if (IsWindowVisible(n) && !Cloaked(n)) list.Add(n);
        return list;
    }
    public static bool Intersects(nint h, Rectangle mon)
    {
        var r = Describe(h).Outer;
        return r.Width > 0 && r.Height > 0 && new Rectangle(r.X, r.Y, r.Width, r.Height).IntersectsWith(mon);
    }
    // A background process may not take the foreground; a harmless ALT tap around SetForegroundWindow lets it.
    public static string EnsureForeground(nint h)
    {
        if (GetForegroundWindow() == h) return "no (already foreground)";
        for (var i = 0; i < 5; i++)
        {
            keybd_event(0x12, 0, 0, 0);
            SetForegroundWindow(h);
            keybd_event(0x12, 0, 2, 0);
            Thread.Sleep(200);
            if (GetForegroundWindow() == h) return $"yes (ALT tap + SetForegroundWindow, attempt {i + 1})";
        }
        return "FAILED to activate";
    }
    public static string CursorPos() { GetCursorPos(out var p); return $"({p.X},{p.Y})"; }
    public static void SetCursor(string pos) { var xy = pos.Trim('(', ')').Split(','); SetCursorPos(int.Parse(xy[0]), int.Parse(xy[1])); }
    // A real left click at (x, y) through SendInput (absolute move, then down and up).
    public static void Click(int x, int y)
    {
        int w = GetSystemMetrics(0), hgt = GetSystemMetrics(1);
        var move = new INPUT { type = 0, mi = new MOUSEINPUT { dx = (x * 65536 + w / 2) / w, dy = (y * 65536 + hgt / 2) / hgt, flags = 0x0001 | 0x8000 } };
        var down = new INPUT { type = 0, mi = new MOUSEINPUT { flags = 0x0002 } };
        var up = new INPUT { type = 0, mi = new MOUSEINPUT { flags = 0x0004 } };
        SendInput(1, [move], Marshal.SizeOf<INPUT>()); Thread.Sleep(150);
        SendInput(1, [down], Marshal.SizeOf<INPUT>()); Thread.Sleep(60);
        SendInput(1, [up], Marshal.SizeOf<INPUT>()); Thread.Sleep(150);
    }
    public static string BlackShare(Bitmap bmp, Func<int, int, bool> inArea)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var px = new int[bmp.Width * bmp.Height];
        Marshal.Copy(data.Scan0, px, 0, px.Length);
        bmp.UnlockBits(data);
        long total = 0, black = 0;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
                if (inArea(x, y)) { total++; if ((px[y * bmp.Width + x] & 0xFFFFFF) == 0) black++; }
        return total == 0 ? "n/a (empty area)" : $"{100.0 * black / total:F2}% of {total} px";
    }

    public static List<nint> WindowsOf(int pid)
    {
        var list = new List<nint>();
        EnumWindows((h, _) => { GetWindowThreadProcessId(h, out var p); if (p == pid) list.Add(h); return true; }, 0);
        return list;
    }

    public static nint TestWindowOf(int pid)
    {
        foreach (var h in WindowsOf(pid))
        {
            var sb = new StringBuilder(256); GetClassName(h, sb, 256);
            if (sb.ToString() == "GameBackdropTestWindow" && IsWindowVisible(h)) return h;
        }
        return 0;
    }

    public static D Describe(nint h)
    {
        GetWindowRect(h, out var o); GetClientRect(h, out var c);
        var p = new POINT(); ClientToScreen(h, ref p);
        return new D((uint)GetWindowLongPtr(h, -16), (uint)GetWindowLongPtr(h, -20),
            new R(o.L, o.T, o.Rt - o.L, o.B - o.T), new R(p.X, p.Y, c.Rt, c.B));
    }

    public static Bitmap Print(nint h, R outer)
    {
        var bmp = new Bitmap(outer.Width, outer.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        var dc = g.GetHdc();
        PrintWindow(h, dc, 2); // PW_RENDERFULLCONTENT
        g.ReleaseHdc(dc);
        return bmp;
    }
}
