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
// Scenario "live": PR #14's live changes, run on main and on the PR. Scenario "backdrop": PR #12's black backdrop checks, then one fgprobe2 round. "fgprobe", "fgprobe2": PR #12 diagnosis.
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
        // Review 3's cases on the real app: a game that changes its own frame after the
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
    else if (scenario == "fsprobe")
    {
        // A fullscreen game opened while the app runs, five times: every distinct state of its
        // window over 4 s (style, rect), and whether the app gave it a backdrop.
        for (var round = 1; round <= 5; round++)
        {
            var app = Launch(appExe, $"--settings \"{settingsPath}\"");
            Thread.Sleep(1500);
            var proc = Launch(testExe, "--mode fullscreen");
            var sw = Stopwatch.StartNew();
            nint h = 0;
            while (h == 0 && sw.ElapsedMilliseconds < 5000) { h = W.TestWindowOf(proc.Id); Thread.Sleep(5); }
            string last = "";
            var states = new List<string>();
            while (sw.ElapsedMilliseconds < 4000)
            {
                var d = W.Describe(h);
                var now = $"style 0x{d.Style:X8} outer {d.Outer} client {d.Client}";
                if (now != last) { states.Add($"{sw.ElapsedMilliseconds} ms: {now}"); last = now; }
                Thread.Sleep(5);
            }
            var r = new Dictionary<string, object?> { ["name"] = "fsprobe", ["round"] = round, ["states"] = states, ["backdrops"] = W.Backdrops(app.Id).Count(W.IsVisible) };
            log.Add(r); Console.WriteLine(JsonSerializer.Serialize(r));
            Quit(app);
            proc.Kill(); proc.WaitForExit(); Thread.Sleep(500);
        }
    }
    else if (scenario == "live")
    {
        // PR #14 (#4): a game changing its own window while the app runs. Run once on main (before)
        // and once on the PR's build (after); each picture is the test window's own pixels on an
        // outline of the monitor, its caption saying whether a backdrop covers the monitor.
        string Backdrop(int pid) => W.Backdrops(pid).Any(b => W.IsVisible(b) && W.Describe(b).Outer == new W.R(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height)) ? "black backdrop covers the monitor" : "no backdrop";
        void Key(nint h, char k) { W.PostMessage(h, 0x0100, k, 0); Thread.Sleep(1200); } // WM_KEYDOWN

        // 1-4. A windowed game goes fullscreen, windowed again, changes resolution; then Quit.
        // Each part closes its test window, so the next app run sees only its own game.
        void Close(Process p) { p.Kill(); p.WaitForExit(); Thread.Sleep(600); }
        // Where a backdrop covers the monitor, also the monitor itself (CopyFromScreen, with the
        // game in front so its fullscreen mark drops the taskbar behind the black); see ScreenShot.
        void Both(string name, string scene, nint game, int pid)
        {
            Shot(name, scene, game);
            if (Backdrop(pid) == "no backdrop") return;
            // A real screen capture (ScreenShot: only while the black covers the monitor, any other
            // program's window masked), with the game in front so its fullscreen mark drops the
            // taskbar behind the black. Activation is retried until the game is the foreground.
            var activated = W.EnsureForeground(game);
            for (var i = 0; i < 5 && W.Foreground() != game; i++) { Thread.Sleep(500); activated = W.EnsureForeground(game); }
            Thread.Sleep(800);
            ScreenShot(name + "-screen", scene, game, pid, activated);
        }
        // The monitor as the two windows make it up: the backdrop's own pixels over its rect, then
        // the game's over its rect, each from PrintWindow, in their real Z order. Built from the
        // windows, not the screen, so another program's window above them (a topmost ShareX
        // overlay on this PC) is neither captured nor in the way. The taskbar's strip shows where
        // it is, drawn (not captured) as above or below the black from the measured Z order.
        void Monitor(string name, string scene, nint game, int pid, string activated)
        {
            var m = Measure(game, pid, 0);
            var mon = screen.Bounds; var work = screen.WorkingArea;
            var bd = W.Backdrops(pid).First(b => W.IsVisible(b));
            var bo = W.Describe(bd).Outer; var go = W.Describe(game).Outer;
            using var canvas = new Bitmap(mon.Width, mon.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(canvas))
            {
                g.Clear(Color.FromArgb(28, 28, 30));
                var gameAbove = W.ZIndex(game) < W.ZIndex(bd);
                using (var bp = W.Print(bd, bo)) g.DrawImage(bp, bo.X - mon.Left, bo.Y - mon.Top);
                if (gameAbove) using (var gp = W.Print(game, go)) g.DrawImage(gp, go.X - mon.Left, go.Y - mon.Top);
                m["blackShareOutsideGame"] = W.BlackShare(canvas, (x, y) => !new Rectangle(go.X - mon.Left, go.Y - mon.Top, go.Width, go.Height).Contains(x, y));
                var strip = new Rectangle(0, work.Bottom - mon.Top, mon.Width, mon.Bottom - work.Bottom);
                var taskbarAbove = W.ZIndex(W.FindTray()) < W.ZIndex(bd);
                using var font = new Font("Segoe UI", 20, FontStyle.Bold);
                using var small = new Font("Consolas", 15);
                if (taskbarAbove && strip.Height > 0)
                {
                    using var tb = new SolidBrush(Color.FromArgb(70, 70, 76));
                    g.FillRectangle(tb, strip);
                    g.DrawString("taskbar ABOVE the black (drawn, not captured)", small, Brushes.White, 20, strip.Top + 8);
                }
                else if (strip.Height > 0)
                    g.DrawString("taskbar strip: the taskbar is BELOW the black here (measured Z order), so this is the backdrop's black", small, Brushes.DimGray, 20, strip.Top + 8);
                var y0 = go.Y - mon.Top > 160 ? 24 : Math.Min(go.Y - mon.Top + go.Height + 30, work.Bottom - mon.Top - 110);
                g.DrawString(scene, font, Brushes.Gray, 30, y0);
                g.DrawString("The backdrop's and the game's own pixels (PrintWindow) at their real rects and Z order; nothing else on the screen is captured.", small, Brushes.DimGray, 30, y0 + 40);
                g.DrawString($"backdrop {bo}   game {go}   pure black outside the game: {m["blackShareOutsideGame"]}   taskbar {(taskbarAbove ? "above" : "below")} the black   foreground: {m["foreground"]}", small, Brushes.DimGray, 30, y0 + 66);
            }
            m["name"] = name; m["scene"] = scene; m["activatedByHarness"] = activated;
            m["picture"] = $"{name}.png: the backdrop's and the game's own pixels (PrintWindow) at their real rects and Z order";
            canvas.Save(Path.Combine(outDir, name + ".png"), ImageFormat.Png);
            log.Add(m);
            Console.WriteLine($"{name}: black outside game {m["blackShareOutsideGame"]}, taskbar {m["taskbar"]}, below game {m["firstVisibleBelowGame"]}");
        }
        var aProc = Launch(testExe, "--size 800x600");
        var a = WaitWindow(aProc);
        var sa = W.Describe(a).Style;
        var app = Launch(appExe, $"--settings \"{settingsPath}\"");
        WaitChanged(a, sa);
        WaitBackdrop(app.Id);
        Key(a, 'F');
        Both("1-game-goes-fullscreen", $"1. Restyled game switches itself to borderless fullscreen (F): {Backdrop(app.Id)}", a, app.Id);
        Key(a, 'W');
        Both("2-game-goes-windowed-again", $"2. Then back to windowed (W): {Backdrop(app.Id)}", a, app.Id);
        Key(a, 'R');
        Both("3-game-changes-resolution", $"3. Then a resolution change to 1280x720 (R): {Backdrop(app.Id)}", a, app.Id);
        Quit(app);
        Shot("4-after-quit", "4. App quit from its tray menu", a);
        Close(aProc);

        // 5-6. A maximised frameless game, stripped; it goes topmost and fills the monitor itself.
        var bProc = Launch(testExe, "--size 800x600");
        var b = WaitWindow(bProc);
        var work = screen.WorkingArea;
        W.SetStyle(b, 0x80000000 | 0x10000000 | 0x01000000); // WS_POPUP | WS_VISIBLE | WS_MAXIMIZE
        W.SetRect(b, work.X, work.Y, work.Width, work.Height);
        Thread.Sleep(600);
        Shot("5-maximised-frameless-before-app", "5. Frameless maximised game covering the work area, app not running", b);
        var sb = W.Describe(b).Style;
        var app2 = Launch(appExe, $"--settings \"{settingsPath}\"");
        WaitChanged(b, sb);
        WaitBackdrop(app2.Id);
        W.SetTopmost(b, true);
        var mon = screen.Bounds;
        W.SetRect(b, mon.X, mon.Y, mon.Width, mon.Height);
        Thread.Sleep(1500);
        Both("6-maximised-game-fills-monitor", $"6. App stripped it (WS_MAXIMIZE off); it went topmost and filled the monitor itself: {Backdrop(app2.Id)}", b, app2.Id);
        Quit(app2);
        Shot("7-maximised-after-quit", "7. App quit from its tray menu", b);
        W.SetTopmost(b, false);
        Close(bProc);

        // 10-12. A frameless game with a resize border, stripped, fills its monitor itself: its border goes back.
        var tProc = Launch(testExe, "--size 800x600");
        var t = WaitWindow(tProc);
        W.SetStyle(t, 0x80000000 | 0x10000000 | 0x00040000); // WS_POPUP | WS_VISIBLE | WS_THICKFRAME
        W.SetRect(t, 100, 100, 816, 616);
        Thread.Sleep(600);
        Shot("10-resize-border-before-app", "10. Frameless game with a resize border, app not running", t);
        var st = W.Describe(t).Style;
        var app5 = Launch(appExe, $"--settings \"{settingsPath}\"");
        WaitChanged(t, st);
        WaitBackdrop(app5.Id);
        Both("11-resize-border-stripped", $"11. App started: resize border stripped, centred: {Backdrop(app5.Id)}", t, app5.Id);
        W.SetRect(t, mon.X, mon.Y, mon.Width, mon.Height);
        Thread.Sleep(1500);
        Both("12-resize-border-game-fills-monitor", $"12. The game filled its monitor itself, still stripped (WS_THICKFRAME 0x00040000 back?): {Backdrop(app5.Id)}", t, app5.Id);
        Quit(app5);
        Shot("13-resize-border-after-quit", "13. App quit from its tray menu", t);
        Close(tProc);

        // 8. A game a modal dialog disabled while the app ran, at Quit.
        var cProc = Launch(testExe, "--size 800x600");
        var c = WaitWindow(cProc);
        var sc = W.Describe(c).Style;
        var app3 = Launch(appExe, $"--settings \"{settingsPath}\"");
        WaitChanged(c, sc);
        WaitBackdrop(app3.Id);
        W.Enable(c, false); // WS_DISABLED, as under a modal dialog
        Thread.Sleep(800);
        Quit(app3);
        Shot("8-disabled-game-after-quit", "8. Game disabled (as under a modal dialog) when the app quit: title bar back; is it still disabled?", c);
        W.Enable(c, true);
        Close(cProc);

        // 9. A fullscreen game opened while the app runs is left alone.
        var app4 = Launch(appExe, $"--settings \"{settingsPath}\"");
        Thread.Sleep(1500);
        var full = WaitWindow(Launch(testExe, "--mode fullscreen"));
        Thread.Sleep(1500);
        Shot("9-fullscreen-left-alone", $"9. Borderless-fullscreen game opened while the app runs: {Backdrop(app4.Id)}", full);
        Quit(app4);
    }
    else if (scenario == "always-admin")
    {
        // PR for #31 (always run as administrator): the tray menu, with no game running as
        // administrator. On main the one-off "Restart as administrator" item stays hidden until a
        // notice; on the PR the "Always run as administrator" check item is always there. The
        // menu's own rect, from the screen. A sixth argument names the pictures' prefix.
        var prefix = args.Length > 5 ? args[5] : "shot";
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(new { games = Array.Empty<object>(), startAtLogon = false }));
        var app = Launch(appExe, $"--settings \"{settingsPath}\"");
        Thread.Sleep(2500);
        var menu = U.OpenTrayMenu(app);
        var items = U.MenuItems(menu);
        log.Add(new { name = "tray-menu", items });
        Console.WriteLine($"tray menu: {string.Join(", ", items)}");
        U.SaveCaptioned(Path.Combine(outDir, $"{prefix}-tray-menu.png"), U.FromScreen(menu), "Tray icon's menu (right-click), no game running as administrator");
        W.PostMessage(menu, 0x0100, 0x1B, 0); // Escape closes the menu
        Thread.Sleep(500);
        Quit(app);
    }
    else if (scenario == "list")
    {
        // PR #18 (the game list window): the tray menu, the list, the "Add from open windows"
        // picker, the list with the picked game on it, and a second launch. Run on main for the
        // before- pictures and on the PR for the after- ones. Pictures are each app window's own
        // pixels (PrintWindow; the tray menu, a topmost window, from the screen over its own rect
        // only). In the picker, rows of other programs' windows are painted over before saving.
        var prefix = args.Length > 5 ? args[5] : "shot";
        const string testTitle = "Game Backdrop test window";
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(new
        {
            games = new[] { new { exePath = @"C:\Games\Example\example.exe", displayName = "Example game", enabled = false, keepMouseInside = false } },
        }));
        var game = WaitWindow(Launch(testExe, "--size 800x600"));
        var app = Launch(appExe, $"--settings \"{settingsPath}\"");
        Thread.Sleep(2500);

        var menu = U.OpenTrayMenu(app);
        U.SaveCaptioned(Path.Combine(outDir, $"{prefix}-01-tray-menu.png"), U.FromScreen(menu), "Tray icon's menu (right-click)");
        var items = U.MenuItems(menu);
        log.Add(new { name = "tray-menu", items });
        Console.WriteLine($"tray menu: {string.Join(", ", items)}");

        var openList = U.Find(menu, "Open list", ControlType.MenuItem);
        if (openList is null)
        {
            Console.WriteLine("no Open list item: this build has no list window");
            log.Add(new { name = "list-window", present = false });
            W.PostMessage(menu, 0x0100, 0x1B, 0); // Escape closes the menu
            Thread.Sleep(500);
        }
        else
        {
            U.InvokeAsync(openList);
            var list = U.WaitWindow(app.Id, "Game Backdrop: games");
            Thread.Sleep(800);
            U.SaveCaptioned(Path.Combine(outDir, $"{prefix}-02-list.png"), W.Print(list, W.Describe(list).Outer), "\"Open list\": the game list window");

            U.Click(U.Find(list, "Add from open windows…", ControlType.Button)!);
            var picker = U.WaitWindow(app.Id, "Add from open windows");
            Thread.Sleep(1200);
            var row = U.Rows(picker).FirstOrDefault(r => r.Current.Name == testTitle) ?? throw new Exception("test window not in the picker");
            ((SelectionItemPattern)row.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            Thread.Sleep(500);
            var po = W.Describe(picker).Outer;
            using (var shot = W.Print(picker, po))
            {
                var hidden = 0;
                using (var g = Graphics.FromImage(shot))
                using (var font = new Font("Segoe UI", 9))
                {
                    foreach (var r in U.Rows(picker).Where(r => r.Current.Name != testTitle))
                    {
                        var b = r.Current.BoundingRectangle;
                        if (b.IsEmpty) continue;
                        var rect = new Rectangle((int)b.X - po.X, (int)b.Y - po.Y, (int)b.Width, (int)b.Height);
                        g.FillRectangle(Brushes.Gainsboro, rect);
                        g.DrawString("another program's window (hidden in this picture)", font, Brushes.DimGray, rect.X + 6, rect.Y + 2);
                        hidden++;
                    }
                }
                log.Add(new { name = "picker", rows = U.Rows(picker).Count, hiddenRows = hidden });
                U.SaveCaptioned(Path.Combine(outDir, $"{prefix}-03-picker.png"), new Bitmap(shot), "\"Add from open windows…\": the test window picked (other programs' rows painted over)");
            }

            U.Click(U.Find(picker, "Add", ControlType.Button)!);
            Thread.Sleep(2500);
            U.SaveCaptioned(Path.Combine(outDir, $"{prefix}-04-list-with-game.png"), W.Print(list, W.Describe(list).Outer), "After \"Add\": the game is on the list, switched on, and backdropped at once");
            var d = W.Describe(game);
            log.Add(new { name = "game-after-add", style = $"0x{d.Style:X8}", outer = d.Outer.ToString(), visibleBackdrops = W.Backdrops(app.Id).Count(W.IsVisible) });
            log.Add(new { name = "settings-after-add", json = File.ReadAllText(settingsPath) });
            Console.WriteLine(File.ReadAllText(settingsPath));
            U.CloseWindow(list);
            Thread.Sleep(800);
        }

        // A second launch: exits at once; on the PR it opens the running copy's list.
        var second = Launch(appExe, $"--settings \"{settingsPath}\"");
        var exited = second.WaitForExit(10000);
        Thread.Sleep(1500);
        var listAgain = U.FindWindow(app.Id, "Game Backdrop: games");
        log.Add(new { name = "second-launch", exited, exitCode = exited ? second.ExitCode : (int?)null, listOpened = listAgain != 0, listInFront = listAgain != 0 && W.Foreground() == listAgain });
        Console.WriteLine($"second launch: exited {exited}, list opened {listAgain != 0}");
        if (listAgain != 0) U.CloseWindow(listAgain);
        Thread.Sleep(500);
        Quit(app);
    }
    else if (scenario == "admin")
    {
        // PR #20: a game running as administrator. Three UAC prompts, answered by a person:
        // 1 accept (TestWindow elevated), 2 decline (from the notice), 3 accept (from the menu).
        var signals = Path.Combine(outDir, "signals");
        if (Directory.Exists(signals)) Directory.Delete(signals, true);
        Directory.CreateDirectory(signals);
        var helper = Path.Combine(AppContext.BaseDirectory, "elevated-game.ps1");

        var app = Launch(appExe, $"--settings \"{settingsPath}\"");
        Thread.Sleep(2000);
        log.Add(new { step = "app started", pid = app.Id });

        Console.WriteLine(">>> UAC 1 of 3: ACCEPT (starts the test window as administrator)");
        Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{helper}\" \"{testExe}\" \"{appExe}\" \"{signals}\"")
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
        nint game = 0;
        for (var i = 0; i < 600 && game == 0; i++) { game = W.WindowsOfClass("GameBackdropTestWindow").FirstOrDefault(W.IsVisible); Thread.Sleep(200); }
        if (game == 0) throw new Exception("elevated test window never appeared");
        Thread.Sleep(3000);
        var d1 = W.Describe(game);
        log.Add(new { step = "elevated game, normal app", style = $"0x{d1.Style:X8}", outer = d1.Outer.ToString(), client = d1.Client.ToString(),
            backdrops = W.Backdrops(app.Id).Count(W.IsVisible), above = W.VisibleAbove(game).Select(W.ClassOf).ToList() });
        Console.WriteLine($"game: style 0x{d1.Style:X8} outer {d1.Outer} (untouched: has WS_CAPTION {(d1.Style & 0x00C00000) == 0x00C00000})");
        U.SaveCaptioned(Path.Combine(outDir, "after-1-game-untouched.png"), A.VisibleFrameFromScreen(game),
            $"Elevated test window, normal app running: left untouched (style 0x{d1.Style:X8}, outer {d1.Outer}). The window's own rect, from the screen.");

        var toast = A.WaitToast(15000);
        if (toast is null) { Console.WriteLine("no notice appeared"); log.Add(new { step = "notice", found = false }); }
        else
        {
            var texts = A.Texts(toast);
            log.Add(new { step = "notice", found = true, texts });
            Console.WriteLine("notice: " + string.Join(" | ", texts));
            U.SaveCaptioned(Path.Combine(outDir, "after-2-notice.png"), A.FromScreen(toast), "The notice, once for this run of the game. The notice's own rect, from the screen.");
        }

        if (toast is not null)
        {
            Console.WriteLine(">>> UAC 2 of 3: DECLINE (clicking the notice now)");
            var r = toast.Current.BoundingRectangle;
            var cursor = W.CursorPos();
            W.Click((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height * 0.4));
            W.SetCursor(cursor);
            var answered = A.WaitConsent(180000);
            log.Add(new { step = "notice clicked", uacShown = answered.shown, uacGone = answered.gone });
            Thread.Sleep(3000);
            var others = Process.GetProcessesByName("GameBackdrop").Where(p => p.Id != app.Id).Select(p => p.Id).ToList();
            var d2 = W.Describe(game);
            log.Add(new { step = "after decline", appRunning = !app.HasExited, otherCopies = others, style = $"0x{d2.Style:X8}", outer = d2.Outer.ToString() });
            Console.WriteLine($"after decline: normal copy running {!app.HasExited}, other copies {others.Count}, game style 0x{d2.Style:X8}");
        }

        var menu = U.OpenTrayMenu(app);
        var items = U.MenuItems(menu);
        log.Add(new { step = "tray menu", items });
        Console.WriteLine("menu: " + string.Join(", ", items));
        U.SaveCaptioned(Path.Combine(outDir, "after-3-menu.png"), U.FromScreen(menu), "The tray menu after the notice: \"Restart as administrator\". The menu's own rect, from the screen.");
        var restart = U.Find(menu, "Restart as administrator", ControlType.MenuItem) ?? throw new Exception("no Restart as administrator item");
        Console.WriteLine(">>> UAC 3 of 3: ACCEPT (Restart as administrator from the menu)");
        U.InvokeAsync(restart);
        var accepted = A.WaitConsent(180000);
        log.Add(new { step = "menu restart", uacShown = accepted.shown, uacGone = accepted.gone });
        Process? elevated = null;
        for (var i = 0; i < 150 && elevated is null; i++)
        {
            elevated = Process.GetProcessesByName("GameBackdrop").FirstOrDefault(p => p.Id != app.Id);
            Thread.Sleep(200);
        }
        var normalExited = app.WaitForExit(20000);
        if (elevated is null) throw new Exception("no elevated copy started");
        WaitChanged(game, d1.Style);
        WaitBackdrop(elevated.Id);
        A.WaitNoToast(15000);
        Thread.Sleep(800);
        var d3 = W.Describe(game);
        var mon = Screen.FromHandle(game).Bounds;
        log.Add(new { step = "elevated copy took over", normalExited, elevatedPid = elevated.Id, style = $"0x{d3.Style:X8}", outer = d3.Outer.ToString(), client = d3.Client.ToString(),
            backdrops = W.Backdrops(elevated.Id).Where(W.IsVisible).Select(h => W.Describe(h).Outer.ToString()).ToList() });
        Console.WriteLine($"took over: normal exited {normalExited}, game style 0x{d3.Style:X8} outer {d3.Outer} client {d3.Client}");
        // The whole monitor only with the game in front and nothing else over that monitor:
        // otherwise the picture would show other programs (first run: the active window, above
        // the backdrop, because the elevated game wasn't in front).
        var activated = W.EnsureForeground(game);
        Thread.Sleep(800);
        var over = W.VisibleAbove(game).Where(h => W.Intersects(h, mon)).Select(W.ClassOf).ToList();
        log.Add(new { step = "before the monitor picture", activated, windowsOverTheMonitor = over });
        if (over.Count > 0) Console.WriteLine("monitor picture skipped: other windows over the monitor: " + string.Join(", ", over));
        else using (var bmp = new Bitmap(mon.Width, mon.Height))
        {
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(mon.X, mon.Y, 0, 0, mon.Size);
            var black = W.BlackShare(bmp, (x, y) => !new Rectangle(d3.Outer.X - mon.X, d3.Outer.Y - mon.Y, d3.Outer.Width, d3.Outer.Height).Contains(x, y));
            log.Add(new { step = "monitor black outside the game", black });
            U.SaveCaptioned(Path.Combine(outDir, "after-4-elevated-restyled.png"), (Bitmap)bmp.Clone(),
                $"Elevated copy: game borderless and centred (outer {d3.Outer}), the rest of the monitor black ({black}). Whole monitor from the screen, under the backdrop.");
        }
        var names = A.TrayNames();
        log.Add(new { step = "tray icon names", names });
        Console.WriteLine("tray icon names: " + string.Join(" | ", names));

        // Close the game (elevated helper), so the taskbar shows; then hover the icon for its tooltip.
        File.WriteAllText(Path.Combine(signals, "close-game"), "");
        for (var i = 0; i < 100 && !File.Exists(Path.Combine(signals, "game-closed")); i++) Thread.Sleep(100);
        Thread.Sleep(1500);
        var icon = A.TrayIcon("Game Backdrop");
        if (icon is null) log.Add(new { step = "tooltip", found = false, note = "icon not on the taskbar (likely in the hidden-icons overflow)" });
        else
        {
            var ir = icon.Current.BoundingRectangle;
            var cursor = W.CursorPos();
            W.SetCursor($"({(int)(ir.Left + ir.Width / 2)},{(int)(ir.Top + ir.Height / 2)})");
            Thread.Sleep(2000);
            var tip = W.WindowsOfClass("tooltips_class32").FirstOrDefault(h => W.IsVisible(h) && W.TitleOf(h).StartsWith("Game Backdrop"));
            log.Add(new { step = "tooltip", found = true, iconName = icon.Current.Name, tooltipWindow = tip != 0 ? W.TitleOf(tip) : null });
            if (tip != 0) U.SaveCaptioned(Path.Combine(outDir, "after-5-tooltip.png"), U.FromScreen(tip), "The tray icon's tooltip in the elevated copy. The tooltip's own rect, from the screen.");
            W.SetCursor(cursor);
            Console.WriteLine($"icon name \"{icon.Current.Name}\", tooltip window {(tip != 0 ? W.TitleOf(tip) : "not found")}");
        }
        File.WriteAllText(Path.Combine(signals, "stop"), "");
        for (var i = 0; i < 100 && !File.Exists(Path.Combine(signals, "stopped")); i++) Thread.Sleep(100);
        Console.WriteLine("elevated helper stopped the test window and the elevated copy");
    }
    else if (scenario == "desktops")
    {
        // #40 (PR #41): a windowed game with its backdrop on one virtual desktop; the harness makes a
        // new, empty desktop (Ctrl+Win+D), captures its monitor, closes it (Ctrl+Win+F4) and goes
        // back to the game's desktop. Also logs the game's cloak events (EVENT_OBJECT_CLOAKED).
        var prefix = args.Length > 5 ? args[5] : "shot";
        var game = WaitWindow(Launch(testExe, "--size 1280x720"));
        var s0 = W.Describe(game).Style;
        var app = Launch(appExe, $"--settings \"{settingsPath}\"");
        WaitChanged(game, s0);
        Console.WriteLine($"game activated: {W.EnsureForeground(game)}");
        WaitBackdrop(app.Id);
        Console.WriteLine($"game after restyle: style 0x{W.Describe(game).Style:X8} {W.Describe(game).Outer}");
        var backdrop = W.Backdrops(app.Id).First(W.IsVisible);
        V.Watch(game, backdrop);
        void Desk(string when)
        {
            var m = new Dictionary<string, object?>
            {
                ["name"] = "desktops", ["when"] = when,
                ["game"] = V.State(game), ["backdrop"] = V.State(backdrop),
            };
            log.Add(m); Console.WriteLine($"{when}: {JsonSerializer.Serialize(m)}");
        }
        Desk("on the game's desktop");
        V.Chord(0x44); // Ctrl+Win+D: a new desktop, and switch to it
        for (var i = 0; i < 50 && V.OnCurrent(game) != false; i++) Thread.Sleep(100);
        Thread.Sleep(1500); // the switch animation
        Desk("on a new, empty desktop");
        var mon = screen.Bounds; var work = screen.WorkingArea;
        using (var shot = new Bitmap(mon.Width, mon.Height, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(mon.Left, mon.Top, 0, 0, mon.Size);
            var strip = new Rectangle(0, work.Bottom - mon.Top, mon.Width, mon.Bottom - work.Bottom);
            var m = new Dictionary<string, object?>
            {
                ["name"] = prefix + "-empty-desktop",
                ["blackShareMonitor"] = W.BlackShare(shot, (_, _) => true),
                ["blackShareAboveTaskbar"] = W.BlackShare(shot, (x, y) => !strip.Contains(x, y)),
                ["blackShareTaskbarStrip"] = W.BlackShare(shot, (x, y) => strip.Contains(x, y)),
            };
            // What else is on screen: visible, uncloaked windows over this monitor that sit above the
            // backdrop (all of them when the backdrop isn't shown here), the desktop itself excepted.
            var backdropShown = V.Cloak(backdrop) == 0 && W.IsVisible(backdrop);
            var shell = new[] { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };
            var foreign = V.OnScreen().Where(h => h != backdrop && !shell.Contains(W.ClassOf(h)) && W.Intersects(h, mon)
                && (!backdropShown || W.ZIndex(h) < W.ZIndex(backdrop))).ToList();
            // A click-through overlay over the whole monitor (WS_EX_LAYERED, as screen-recording and
            // GPU overlays are) draws nothing of its own here; masking it would hide the whole picture.
            bool Overlay(nint h) { var d = W.Describe(h); return (d.Ex & 0x80000) != 0 && d.Outer.Width >= mon.Width - 1 && d.Outer.Height >= mon.Height; }
            m["overlaysNotMasked"] = foreign.Where(Overlay).Select(h => W.ClassOf(h)).ToList();
            m["otherWindowsMaskedClasses"] = foreign.Where(h => !Overlay(h)).Select(h => W.ClassOf(h)).ToList();
            foreign = foreign.Where(h => !Overlay(h)).ToList();
            var tray = W.FindTray();
            var taskbarShown = !backdropShown || W.ZIndex(tray) < W.ZIndex(backdrop);
            m["backdropShownHere"] = backdropShown;
            m["taskbarAboveBackdrop"] = taskbarShown;
            m["otherWindowsMasked"] = foreign.Count;
            V.Pixelate(shot, 40); // wallpaper and desktop icons unreadable; pure black stays pure black
            using (var g = Graphics.FromImage(shot))
            {
                using var label = new Font("Segoe UI", 16);
                using var maskBrush = new SolidBrush(Color.FromArgb(70, 70, 76));
                if (taskbarShown)
                {
                    g.FillRectangle(maskBrush, strip);
                    g.DrawString("taskbar (masked)", label, Brushes.White, 20, strip.Top + 10);
                }
                foreach (var h in foreign)
                {
                    var r = W.Describe(h).Outer;
                    g.FillRectangle(maskBrush, r.X - mon.Left, r.Y - mon.Top, r.Width, r.Height);
                    g.DrawString("other window (masked)", label, Brushes.White, r.X - mon.Left + 8, r.Y - mon.Top + 8);
                }
                using var font = new Font("Segoe UI", 20, FontStyle.Bold);
                using var small = new Font("Consolas", 15);
                var caption = prefix == "before" ? "Before (main): a new, empty virtual desktop while a windowed game runs on another"
                    : "After (PR #41): a new, empty virtual desktop while a windowed game runs on another";
                g.FillRectangle(Brushes.Black, 20, 16, mon.Width - 40, 110);
                g.DrawString(caption, font, Brushes.White, 30, 24);
                g.DrawString($"Screen capture (CopyFromScreen), then pixelated (40 px blocks); taskbar{(foreign.Count > 0 ? " and other windows" : "")} masked. Captions drawn after measuring.", small, Brushes.LightGray, 30, 64);
                g.DrawString($"pure black: above the taskbar {m["blackShareAboveTaskbar"]}   backdrop shown here: {backdropShown}", small, Brushes.LightGray, 30, 90);
            }
            m["picture"] = $"{prefix}-empty-desktop.png: screen capture (CopyFromScreen) of the new desktop's monitor, pixelated, taskbar" + (foreign.Count > 0 ? " and other windows" : "") + " masked";
            shot.Save(Path.Combine(outDir, prefix + "-empty-desktop.png"), ImageFormat.Png);
            log.Add(m); Console.WriteLine($"{m["name"]}: {JsonSerializer.Serialize(m)}");
        }
        V.Chord(0x73); // Ctrl+Win+F4: close the new desktop
        Thread.Sleep(1200);
        for (var i = 0; i < 10 && V.OnCurrent(game) == false; i++) { V.Chord(0x25); Thread.Sleep(900); } // Ctrl+Win+Left
        Thread.Sleep(800);
        Desk("back on the game's desktop");
        log.Add(new { name = "cloak events", events = V.Events() });
        Console.WriteLine($"cloak events: {string.Join("; ", V.Events())}");
        Quit(app);
    }
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

    public static void SetStyle(nint h, uint style)
    {
        SetWindowLongPtr(h, -16, (nint)style);
        SetWindowPos(h, 0, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
    }
    public static void SetRect(nint h, int x, int y, int w, int hgt) => SetWindowPos(h, 0, x, y, w, hgt, 0x0004 | 0x0010); // NOZORDER NOACTIVATE
    public static void SetTopmost(nint h, bool on) => SetWindowPos(h, on ? -1 : -2, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // HWND_TOPMOST / NOTOPMOST
    [DllImport("user32.dll")] static extern bool EnableWindow(nint h, bool enable);
    public static void Enable(nint h, bool on) => EnableWindow(h, on);

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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(nint h, StringBuilder s, int n);
    public static string TitleOf(nint h) { var sb = new StringBuilder(512); GetWindowText(h, sb, 512); return sb.ToString(); }
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

// The "admin" scenario (PR #20): notices, the UAC prompt, the tray icon, through UI Automation.
static class A
{
    static List<AutomationElement> Toasts() =>
        AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.NameProperty, "New notification"))
        .Cast<AutomationElement>().Where(e => !e.Current.BoundingRectangle.IsEmpty && e.Current.BoundingRectangle.Width > 0).ToList();

    public static AutomationElement? WaitToast(int ms)
    {
        for (var t = 0; t < ms; t += 200) { var x = Toasts().FirstOrDefault(); if (x is not null) { Thread.Sleep(1200); return x; } Thread.Sleep(200); }
        return null;
    }

    public static void WaitNoToast(int ms) { for (var t = 0; t < ms && Toasts().Count > 0; t += 200) Thread.Sleep(200); }

    public static List<string> Texts(AutomationElement e) =>
        e.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
        .Cast<AutomationElement>().Select(x => x.Current.Name).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

    public static Bitmap FromScreen(AutomationElement e)
    {
        var r = e.Current.BoundingRectangle;
        var bmp = new Bitmap((int)r.Width, (int)r.Height);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen((int)r.Left, (int)r.Top, 0, 0, bmp.Size);
        return bmp;
    }

    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint h, int attr, out RECT r, int size);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    // A window's visible frame (DWMWA_EXTENDED_FRAME_BOUNDS), from the screen: GetWindowRect
    // includes the invisible resize borders, which show whatever is behind.
    public static Bitmap VisibleFrameFromScreen(nint h)
    {
        DwmGetWindowAttribute(h, 9, out var r, Marshal.SizeOf<RECT>());
        var bmp = new Bitmap(r.R - r.L, r.B - r.T);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.L, r.T, 0, 0, bmp.Size);
        return bmp;
    }

    // The UAC prompt runs as consent.exe: wait for it to show, then to go (answered).
    public static (bool shown, bool gone) WaitConsent(int ms)
    {
        var shown = false;
        for (var t = 0; t < ms; t += 200)
        {
            var up = Process.GetProcessesByName("consent").Length > 0;
            if (up) shown = true;
            else if (shown) { Console.WriteLine("UAC prompt answered"); return (true, true); }
            else if (t > 20000) return (false, false); // never showed
            Thread.Sleep(200);
        }
        return (shown, false);
    }

    static IEnumerable<AutomationElement> TrayButtons()
    {
        var tray = W.FindTray();
        if (tray == 0) return [];
        return AutomationElement.FromHandle(tray).FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>();
    }

    public static List<string> TrayNames() => TrayButtons().Select(b => b.Current.Name).Where(n => n.StartsWith("Game Backdrop")).ToList();

    public static AutomationElement? TrayIcon(string prefix) =>
        TrayButtons().FirstOrDefault(b => b.Current.Name.StartsWith(prefix) && !b.Current.BoundingRectangle.IsEmpty);
}

// UI Automation and pictures for the "list" scenario (PR #18).
static class U
{
    public static AutomationElement? Find(int pid, string name, ControlType type) =>
        AutomationElement.RootElement.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, pid),
            new PropertyCondition(AutomationElement.NameProperty, name),
            new PropertyCondition(AutomationElement.ControlTypeProperty, type)));

    public static AutomationElement? Find(nint window, string name, ControlType type) =>
        AutomationElement.FromHandle(window).FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.NameProperty, name),
            new PropertyCondition(AutomationElement.ControlTypeProperty, type)));

    public static List<string> MenuItems(nint menu) =>
        AutomationElement.FromHandle(menu).FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem))
        .Cast<AutomationElement>().Select(e => e.Current.Name).ToList();

    public static List<AutomationElement> Rows(nint window) =>
        AutomationElement.FromHandle(window).FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Cast<AutomationElement>().ToList();

    // Invoking a button that opens a modal dialog can block until the dialog closes.
    public static void InvokeAsync(AutomationElement e) =>
        new Thread(() => { try { ((InvokePattern)e.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); } catch { } }) { IsBackground = true }.Start();

    // A button that opens a modal dialog: posted BM_CLICK, so no UI Automation call is left
    // waiting inside the dialog's message loop (later UIA calls into the app would time out).
    public static void Click(AutomationElement button) => W.PostMessage((nint)button.Current.NativeWindowHandle, 0x00F5, 0, 0);

    public static nint FindWindow(int pid, string title)
    {
        // The app's own visible top-level windows (an owned dialog is top-level too), by title.
        foreach (var h in W.WindowsOf(pid).Where(W.IsVisible))
            if (W.TitleOf(h) == title) return h;
        return 0;
    }

    public static nint WaitWindow(int pid, string title)
    {
        for (var i = 0; i < 80; i++)
        {
            var h = FindWindow(pid, title);
            if (h != 0) return h;
            Thread.Sleep(100);
        }
        throw new Exception($"window \"{title}\" never appeared");
    }

    public static nint OpenTrayMenu(Process app)
    {
        foreach (var h in W.WindowsOf(app.Id)) W.PostMessage(h, 0x0400 + 1024, 0, 0x0205);
        for (var i = 0; i < 50; i++)
        {
            var menu = W.WindowsOf(app.Id).FirstOrDefault(W.IsVisible);
            if (menu != 0) { Thread.Sleep(600); return menu; }
            Thread.Sleep(100);
        }
        throw new Exception("tray menu never appeared");
    }

    public static void CloseWindow(nint h) => W.PostMessage(h, 0x0010, 0, 0); // WM_CLOSE

    // A topmost menu's own rect, from the screen: nothing but the menu's pixels.
    public static Bitmap FromScreen(nint h)
    {
        var o = W.Describe(h).Outer;
        var bmp = new Bitmap(o.Width, o.Height);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(o.X, o.Y, 0, 0, new Size(o.Width, o.Height));
        return bmp;
    }

    public static void SaveCaptioned(string path, Bitmap window, string caption)
    {
        using var font = new Font("Segoe UI", 11, FontStyle.Bold);
        const int pad = 16, top = 40;
        var width = Math.Max(window.Width + 2 * pad, 760);
        using var canvas = new Bitmap(width, window.Height + top + pad);
        using var g = Graphics.FromImage(canvas);
        g.Clear(Color.FromArgb(28, 28, 30));
        g.DrawString(caption, font, Brushes.White, pad, 10);
        g.DrawImage(window, pad, top, window.Width, window.Height);
        canvas.Save(path, ImageFormat.Png);
        window.Dispose();
        Console.WriteLine($"saved {path}");
    }
}

// Virtual desktops (#40): the shell's public IVirtualDesktopManager, desktop-switch keys, and a
// WinEvent hook that logs when the game and its backdrop are cloaked or uncloaked.
static class V
{
    [ComImport, Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")] class VirtualDesktopManager;
    [ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(nint hwnd, [MarshalAs(UnmanagedType.Bool)] out bool on);
        [PreserveSig] int GetWindowDesktopId(nint hwnd, out Guid id);
        [PreserveSig] int MoveWindowToDesktop(nint hwnd, in Guid id);
    }
    static readonly IVirtualDesktopManager Vdm = (IVirtualDesktopManager)new VirtualDesktopManager();

    public static bool? OnCurrent(nint h) => Vdm.IsWindowOnCurrentVirtualDesktop(h, out var on) == 0 ? on : null;
    public static string DesktopOf(nint h)
    {
        var hr = Vdm.GetWindowDesktopId(h, out var id);
        return hr != 0 ? $"none (0x{hr:X8})" : id == Guid.Empty ? "none (empty GUID)" : id.ToString();
    }
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint h, int attr, out int v, int size);
    public static int Cloak(nint h) => DwmGetWindowAttribute(h, 14, out var v, 4) == 0 ? v : -1; // DWMWA_CLOAKED
    public static object State(nint h) => new { desktop = DesktopOf(h), onCurrentDesktop = OnCurrent(h), cloaked = Cloak(h), visible = W.IsVisible(h) };

    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, nint extra);
    /// <summary>Ctrl+Win+key.</summary>
    public static void Chord(byte key)
    {
        keybd_event(0x5B, 0, 1, 0); keybd_event(0x11, 0, 0, 0); // LWIN (extended), CTRL
        keybd_event(key, 0, key is 0x25 ? 1u : 0u, 0); Thread.Sleep(40); keybd_event(key, 0, key is 0x25 ? 3u : 2u, 0);
        keybd_event(0x11, 0, 2, 0); keybd_event(0x5B, 0, 3, 0);
    }

    delegate bool EnumProc(nint h, nint l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, nint l);
    public static List<nint> OnScreen()
    {
        var list = new List<nint>();
        EnumWindows((h, _) => { if (W.IsVisible(h) && Cloak(h) == 0) list.Add(h); return true; }, 0);
        return list;
    }

    delegate void WinEventProc(nint hook, uint ev, nint hwnd, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] static extern nint SetWinEventHook(uint min, uint max, nint mod, WinEventProc proc, uint pid, uint thread, uint flags);
    static WinEventProc? _proc;
    static readonly List<string> _events = [];
    static readonly Stopwatch _clock = Stopwatch.StartNew();
    public static List<string> Events() { lock (_events) return [.. _events]; }
    /// <summary>Logs EVENT_OBJECT_CLOAKED / UNCLOAKED for the two windows, from a thread pumping messages.</summary>
    public static void Watch(nint game, nint backdrop)
    {
        var ready = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            _proc = (_, ev, hwnd, obj, _, _, _) =>
            {
                if (obj != 0 || (hwnd != game && hwnd != backdrop)) return;
                lock (_events) _events.Add($"{_clock.ElapsedMilliseconds} ms: {(hwnd == game ? "game" : "backdrop")} {(ev == 0x8017 ? "cloaked" : "uncloaked")}");
            };
            SetWinEventHook(0x8017, 0x8018, 0, _proc, 0, 0, 0); // OUTOFCONTEXT
            ready.Set();
            Application.Run();
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA); t.Start(); ready.Wait(5000);
        lock (_events) _events.Add($"{_clock.ElapsedMilliseconds} ms: watching");
    }

    /// <summary>Each block becomes its average colour, except that a block that is all pure black stays so.</summary>
    public static void Pixelate(Bitmap bmp, int block)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        var px = new int[bmp.Width * bmp.Height];
        Marshal.Copy(data.Scan0, px, 0, px.Length);
        for (var by = 0; by < bmp.Height; by += block)
            for (var bx = 0; bx < bmp.Width; bx += block)
            {
                long r = 0, g = 0, b = 0, n = 0;
                for (var y = by; y < Math.Min(by + block, bmp.Height); y++)
                    for (var x = bx; x < Math.Min(bx + block, bmp.Width); x++)
                    { var p = px[y * bmp.Width + x]; r += (p >> 16) & 255; g += (p >> 8) & 255; b += p & 255; n++; }
                var avg = unchecked((int)0xFF000000) | (int)(r / n) << 16 | (int)(g / n) << 8 | (int)(b / n);
                for (var y = by; y < Math.Min(by + block, bmp.Height); y++)
                    for (var x = bx; x < Math.Min(bx + block, bmp.Width); x++) px[y * bmp.Width + x] = avg;
            }
        Marshal.Copy(px, 0, data.Scan0, px.Length);
        bmp.UnlockBits(data);
    }
}
