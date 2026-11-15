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
    g.DrawString(scene, font, Brushes.White, 30, 24);
    g.DrawString($"style 0x{d.Style:X8}   outer {d.Outer}   client {d.Client}   monitor {mon.Width}x{mon.Height}", small, Brushes.LightGray, 30, 64);
    g.DrawString("Only the test window's own pixels (PrintWindow) at its real screen position; the rest of the screen isn't captured.", small, Brushes.Gray, 30, 96);
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

        var full = WaitWindow(Launch(testExe, "--mode fullscreen"));
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
