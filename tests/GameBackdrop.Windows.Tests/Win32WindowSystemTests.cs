using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using GameBackdrop.Core.Geometry;
using GameBackdrop.Core.Layout;
using GameBackdrop.Core.WindowSystem;
using Xunit.Abstractions;

namespace GameBackdrop.Windows.Tests;

/// <summary>
/// The Win32 port against real windows: each test starts its own <c>TestWindow.exe</c> (another
/// process, because the port skips its own process's window events) and kills it afterwards.
/// A test that can't set up what it checks here (the foreground, a shell taskbar) passes without
/// checking and says so in its output (<c>dotnet test --logger "console;verbosity=detailed"</c>).
/// </summary>
[Collection(DesktopCollection.Name)]
public sealed class Win32WindowSystemTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly Process _testWindow;
    private readonly nint _hwnd;
    private readonly Win32WindowSystem _windows;
    private readonly List<WindowEvent> _events = [];

    public Win32WindowSystemTests(ITestOutputHelper output)
    {
        _output = output;
        // The port posts its events to the current SynchronizationContext; this one runs them at
        // once, on this thread, which also owns the hooks.
        SynchronizationContext.SetSynchronizationContext(new RunAtOnce());
        _windows = new Win32WindowSystem();
        _windows.WindowEvent += _events.Add;

        _testWindow = StartTestWindow("--size 800x600");
        _hwnd = WaitFor(() => TestWindowOf(_testWindow.Id), "the test window to open");
    }

    public void Dispose()
    {
        _windows.Dispose();
        if (!_testWindow.HasExited) _testWindow.Kill();
        _testWindow.Dispose();
    }

    [Fact]
    public void A_window_minimised_before_it_is_put_back_reopens_at_its_original_size_and_position()
    {
        var handle = new WindowHandle(_hwnd);
        var original = _windows.Describe(handle)!;
        var borderless = original.Style.Borderless();
        Assert.Equal(ChangeResult.Ok, _windows.SetFrame(handle,
            new WindowFrame(borderless, Placement.Centre(original.Client.Size, original.Monitor.Bounds))));
        ShowWindow(_hwnd, SW_MINIMIZE);
        WaitFor(() => IsIconic(_hwnd) ? 1 : 0, "the window to minimise");

        Assert.Equal(ChangeResult.Ok, _windows.SetFrame(handle, original.Frame));
        Assert.True(IsIconic(_hwnd), "putting it back leaves it minimised");
        ShowWindow(_hwnd, SW_RESTORE);
        WaitFor(() => IsIconic(_hwnd) ? 0 : 1, "the window to reopen");

        var reopened = _windows.Describe(handle)!;
        Assert.Equal(original.Outer, reopened.Outer);
        Assert.Equal(original.Client.Size, reopened.Client.Size);
        Assert.True(reopened.Style.HasCaption);
    }

    [Fact]
    public void An_open_window_is_listed_once_with_its_title_and_exe_and_the_app_s_own_windows_are_not()
    {
        using var second = StartTestWindow("--size 640x480");
        WaitFor(() => TestWindowOf(second.Id), "the second test window to open");
        var ownWindow = CreateWindowEx(0, "STATIC", "Game Backdrop own window", WS_POPUP | WS_VISIBLE, 0, 0, 16, 16, 0, 0, 0, 0);
        try
        {
            var listed = OpenWindows.List();

            var exe = _testWindow.MainModule!.FileName;
            var testWindow = Assert.Single(listed, w => string.Equals(w.ExePath, exe, StringComparison.OrdinalIgnoreCase));
            Assert.Equal("Game Backdrop test window", testWindow.Title);
            Assert.DoesNotContain(listed, w => w.Title == "Game Backdrop own window");
            Assert.All(listed, w => Assert.False(string.IsNullOrWhiteSpace(w.Title)));
        }
        finally
        {
            DestroyWindow(ownWindow);
            second.Kill();
        }
    }

    [Fact]
    public void The_foreground_window_is_the_one_the_user_is_in()
    {
        if (!TryMakeForeground(_hwnd))
        {
            _output.WriteLine("Not checked: Windows wouldn't give the test window the foreground.");
            return;
        }

        Assert.Equal(new WindowHandle(_hwnd), _windows.ForegroundWindow());
    }

    [Fact]
    public void A_window_is_described_with_its_process_s_id_path_and_start_time()
    {
        var info = _windows.Describe(new WindowHandle(_hwnd))!;

        Assert.Equal(_testWindow.Id, info.ProcessId);
        Assert.Equal(_testWindow.MainModule!.FileName, info.ProcessPath, ignoreCase: true);
        Assert.Equal(_testWindow.StartTime.ToFileTimeUtc(), info.ProcessStarted);
    }

    [Fact]
    public void A_window_keeps_its_lifetime_when_it_changes_itself()
    {
        var handle = new WindowHandle(_hwnd);
        var before = _windows.Describe(handle)!;

        _windows.SetFrame(handle, new WindowFrame(before.Style.Borderless(), Rect.FromPosition(10, 10, before.Client.Size)));

        Assert.Equal(before.Lifetime, _windows.Describe(handle)!.Lifetime);
    }

    [Fact]
    public void Describing_takes_in_a_window_s_destruction_first()
    {
        var handle = new WindowHandle(_hwnd);
        var lifetime = _windows.Describe(handle)!.Lifetime;

        _testWindow.Kill();
        _testWindow.WaitForExit();
        WaitFor(() => IsWindow(_hwnd) ? 0 : 1, "the window to be destroyed");

        Assert.Null(_windows.Describe(handle));
        Assert.Contains(new WindowEvent(WindowEventKind.Destroyed, handle, lifetime), _events);
    }

    [Fact]
    public void Describing_any_window_takes_in_destructions_queued_behind_a_posted_message()
    {
        // The tray app's thread nearly always has posted messages waiting (WinForms posts its
        // own, and the controller's events are posted): every destruction queued behind one must
        // be taken in by the next Describe, of any window, and the posted message left for the loop.
        using var second = StartTestWindow("--size 640x480");
        var secondHwnd = WaitFor(() => TestWindowOf(second.Id), "the second test window to open");
        var first = new WindowHandle(_hwnd);
        var other = new WindowHandle(secondHwnd);
        var firstLifetime = _windows.Describe(first)!.Lifetime;
        var otherLifetime = _windows.Describe(other)!.Lifetime;
        Assert.True(PostThreadMessage(GetCurrentThreadId(), WM_APP_PROBE, 0, 0));

        _testWindow.Kill();
        second.Kill();
        _testWindow.WaitForExit();
        second.WaitForExit();
        WaitFor(() => IsWindow(_hwnd) || IsWindow(secondHwnd) ? 0 : 1, "both windows to be destroyed");
        Thread.Sleep(200);
        Assert.DoesNotContain(_events, e => e.Kind == WindowEventKind.Destroyed && (e.Window == first || e.Window == other)); // nothing has pumped
        _windows.Describe(new WindowHandle(GetDesktopWindow()));

        Assert.Contains(new WindowEvent(WindowEventKind.Destroyed, first, firstLifetime), _events);
        Assert.Contains(new WindowEvent(WindowEventKind.Destroyed, other, otherLifetime), _events);
        Assert.True(PeekMessage(out _, 0, WM_APP_PROBE, WM_APP_PROBE, PM_REMOVE), "the posted message is still queued");
    }

    [Fact]
    public void A_backdrop_covers_the_bounds_it_is_given_directly_behind_the_window()
    {
        var bounds = _windows.Describe(new WindowHandle(_hwnd))!.Monitor.Bounds;
        var backdrop = _windows.CreateBackdrop();

        _windows.ShowBackdrop(backdrop, bounds, new WindowHandle(_hwnd));

        var hwnd = backdrop.Value;
        Assert.True(GetWindowRect(hwnd, out var rect));
        Assert.Equal(bounds, new Rect(rect.Left, rect.Top, rect.Right, rect.Bottom));
        Assert.True(IsWindowVisible(hwnd));
        Assert.Equal(hwnd, NextVisibleBelow(_hwnd));
        var exStyle = (uint)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        var owner = GetWindow(hwnd, GW_OWNER);
        Assert.True(owner != 0 && !IsWindowVisible(owner), "owned by a hidden window: not in alt-tab or the taskbar");
        Assert.True((exStyle & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) == 0, "neither style, which would put it on every virtual desktop");
        Assert.True((exStyle & WS_EX_TOPMOST) == 0, "never topmost");
    }

    [Fact]
    public void A_backdrop_is_on_the_virtual_desktop_of_the_window_it_is_behind_and_no_other()
    {
        // A window no virtual desktop holds is shown on every one: another desktop would be
        // blacked out wherever the backdrop is above its windows.
        var bounds = _windows.Describe(new WindowHandle(_hwnd))!.Monitor.Bounds;
        var backdrop = _windows.CreateBackdrop();

        _windows.ShowBackdrop(backdrop, bounds, new WindowHandle(_hwnd));

        Pump();
        var desktops = (IVirtualDesktopManager)new VirtualDesktopManager();
        Assert.Equal(0, desktops.GetWindowDesktopId(_hwnd, out var windowDesktop));
        Assert.Equal(0, desktops.GetWindowDesktopId(backdrop.Value, out var backdropDesktop));
        Assert.NotEqual(Guid.Empty, backdropDesktop);
        Assert.Equal(windowDesktop, backdropDesktop);
    }

    [Fact]
    public void A_backdrop_shown_behind_a_window_on_another_virtual_desktop_goes_to_that_desktop()
    {
        // Windows puts a window on the desktop current when it is first shown. Only this process's
        // windows can be moved between desktops, so the game here is one of the test's own.
        var desktops = (IVirtualDesktopManager)new VirtualDesktopManager();
        var game = CreateWindowEx(0, "STATIC", "Game Backdrop stand-in game", WS_POPUP | WS_VISIBLE, 100, 100, 320, 240, 0, 0, 0, 0);
        try
        {
            Pump();
            Assert.Equal(0, desktops.GetWindowDesktopId(game, out var current));
            var other = VirtualDesktopIds().FirstOrDefault(d => d != current);
            if (other == Guid.Empty)
            {
                NotChecked("this session has only one virtual desktop");
                return;
            }
            Assert.Equal(0, desktops.MoveWindowToDesktop(game, other));
            Pump();
            var backdrop = _windows.CreateBackdrop();

            _windows.ShowBackdrop(backdrop, new Rect(0, 0, 640, 480), new WindowHandle(game));

            Pump();
            Assert.Equal(0, desktops.GetWindowDesktopId(backdrop.Value, out var backdropDesktop));
            Assert.Equal(other, backdropDesktop);
            Assert.Equal(0, desktops.IsWindowOnCurrentVirtualDesktop(backdrop.Value, out var onCurrent));
            Assert.False(onCurrent, "not shown on the desktop the user is on");
        }
        finally
        {
            DestroyWindow(game);
        }
    }

    /// <summary>The session's virtual desktops, from Explorer's own record of them.</summary>
    private static List<Guid> VirtualDesktopIds()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops");
        if (key?.GetValue("VirtualDesktopIDs") is not byte[] ids) return [];
        return Enumerable.Range(0, ids.Length / 16).Select(i => new Guid(ids.AsSpan(i * 16, 16))).ToList();
    }

    /// <summary>Run this thread's messages for a moment: the shell places a newly shown window on a desktop then.</summary>
    private static void Pump()
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 300)
        {
            while (PeekMessage(out var msg, 0, 0, 0, PM_REMOVE)) DispatchMessage(ref msg);
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void A_backdrop_shown_behind_the_foreground_window_raises_that_window_to_the_top_first()
    {
        // Windows makes a window the foreground (and raises EVENT_SYSTEM_FOREGROUND) a moment
        // before the window's own thread raises it: a backdrop ordered behind it in that gap would
        // be left under whatever the window is about to jump over. This sets up that moment: the
        // test window is the foreground, and another window sits above it. (Windows won't raise
        // another window above the foreground one, so the foreground window is ordered behind it.)
        using var otherProcess = StartTestWindow("--size 640x480");
        var other = WaitFor(() => TestWindowOf(otherProcess.Id), "the other test window to open");
        try
        {
            // A test process in the background may not take the foreground: then there is nothing to check.
            if (!TryMakeForeground(_hwnd)) { NotChecked("the test window could not take the foreground"); return; }
            SetWindowPos(_hwnd, other, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            if (GetForegroundWindow() != _hwnd || ZIndex(other) > ZIndex(_hwnd)) { NotChecked("the foreground or the order changed under the test"); return; }
            var bounds = _windows.Describe(new WindowHandle(_hwnd))!.Monitor.Bounds;
            var backdrop = _windows.CreateBackdrop();

            _windows.ShowBackdrop(backdrop, bounds, new WindowHandle(_hwnd));

            Assert.True(ZIndex(_hwnd) < ZIndex(other), "the foreground window is above the other window");
            Assert.Equal(backdrop.Value, NextVisibleBelow(_hwnd));
            Assert.Equal(_hwnd, GetForegroundWindow());
        }
        finally
        {
            otherProcess.Kill();
        }
    }

    [Fact]
    public void A_backdrop_shown_behind_a_window_that_is_not_the_foreground_does_not_raise_it()
    {
        // A third window is the foreground (Windows never raises a window above that one, so a
        // raise would only show against a window that isn't), and the other window sits above
        // the test window. Measured on Windows 11: from a process without the foreground, an
        // HWND_TOP on a window that isn't the foreground is ignored anyway, so this pins the
        // outcome (nothing raised) rather than the port's own check.
        using var otherProcess = StartTestWindow("--size 640x480");
        using var foregroundProcess = StartTestWindow("--size 320x240");
        var other = WaitFor(() => TestWindowOf(otherProcess.Id), "the other test window to open");
        var foreground = WaitFor(() => TestWindowOf(foregroundProcess.Id), "the foreground test window to open");
        try
        {
            if (!TryMakeForeground(foreground)) { NotChecked("the third window could not take the foreground"); return; }
            SetWindowPos(_hwnd, other, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            if (GetForegroundWindow() != foreground || ZIndex(other) > ZIndex(_hwnd)) { NotChecked("the foreground or the order changed under the test"); return; }
            var bounds = _windows.Describe(new WindowHandle(_hwnd))!.Monitor.Bounds;
            var backdrop = _windows.CreateBackdrop();

            _windows.ShowBackdrop(backdrop, bounds, new WindowHandle(_hwnd));

            Assert.True(ZIndex(other) < ZIndex(_hwnd), "the other window stays above the test window");
            Assert.Equal(backdrop.Value, NextVisibleBelow(_hwnd));
        }
        finally
        {
            otherProcess.Kill();
            foregroundProcess.Kill();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_backdrop_shown_behind_a_topmost_window_stays_out_of_the_topmost_band(bool targetIsForeground)
    {
        // A windowed game can be topmost (Borderless keeps the bit). Ordered directly behind it,
        // the backdrop would sit above any topmost window below the game, and Windows makes a
        // window ordered there topmost too. Here two test windows are topmost, the target above
        // the other. The backdrop is 16x16, at the target's corner, to keep this unobtrusive.
        // The foreground goes to the target, or else to the other topmost window (where Windows
        // would honour a raise of the backdrop over the ordinary windows). It is taken the way the
        // other foreground tests take it (TryMakeForeground); when Windows still refuses there is
        // nothing to check.
        using var firstProcess = StartTestWindow("--size 320x240");
        var first = WaitFor(() => TestWindowOf(firstProcess.Id), "the first topmost window to open");
        using var lastProcess = StartTestWindow("--size 320x240");
        var last = WaitFor(() => TestWindowOf(lastProcess.Id), "the last topmost window to open");
        var (target, anchor) = targetIsForeground ? (last, first) : (first, last);
        try
        {
            Assert.True(SetWindowPos(anchor, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE));
            Assert.True(SetWindowPos(target, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE));
            Assert.True(IsTopmost(target) && IsTopmost(anchor), "both test windows are topmost");
            Assert.True(ZIndex(target) < ZIndex(anchor), "the target is above the other topmost window");
            TryMakeForeground(targetIsForeground ? target : anchor);
            Thread.Sleep(200);
            if ((GetForegroundWindow() == target) != targetIsForeground)
            {
                NotChecked(targetIsForeground ? "the target did not get the foreground" : "the target got the foreground");
                return;
            }
            Assert.True(GetWindowRect(target, out var corner));
            var bounds = new Rect(corner.Left, corner.Top, corner.Left + 16, corner.Top + 16);
            var backdrop = _windows.CreateBackdrop();
            // First directly behind an ordinary window, so the test sees whether it is raised.
            _windows.ShowBackdrop(backdrop, bounds, new WindowHandle(_hwnd));
            Assert.True(ZIndex(backdrop.Value) > ZIndex(_hwnd));

            _windows.ShowBackdrop(backdrop, bounds, new WindowHandle(target));

            var exStyle = (uint)GetWindowLongPtr(backdrop.Value, GWL_EXSTYLE);
            Assert.True((exStyle & WS_EX_TOPMOST) == 0, $"never topmost (ex-style 0x{exStyle:X8})");
            Assert.True(IsWindowVisible(backdrop.Value));
            Assert.True(ZIndex(backdrop.Value) > ZIndex(anchor), "below both topmost windows");
            if (targetIsForeground)
                Assert.True(ZIndex(backdrop.Value) < ZIndex(_hwnd), "the game in front: raised over the ordinary windows");
            else
                Assert.True(ZIndex(backdrop.Value) > ZIndex(_hwnd), "the game not in front: not raised over the ordinary windows");
        }
        finally
        {
            SetWindowPos(target, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            SetWindowPos(anchor, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            firstProcess.Kill();
            lastProcess.Kill();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_new_backdrop_behind_a_topmost_window_in_the_background_stays_below_the_active_app(bool appHoldsTheForeground)
    {
        // The game is topmost, but the user is in an ordinary app: the tray app started, or the
        // game was shown, while they were elsewhere. A fresh backdrop has no safe place to keep.
        // Where Windows creates it depends on who holds the foreground (measured on Windows 11):
        // made by a process in the background, a new window goes directly below the foreground
        // window; made while the app's own process holds the foreground (as just after it is
        // launched), it goes to the top of the ordinary windows, above the active app. The first
        // case is the ordinary test window as the active app; the second, a small window of this
        // process, with the ordinary test window also open. The backdrop is 16x16, at the topmost
        // target's corner, to keep this unobtrusive.
        using var targetProcess = StartTestWindow("--size 320x240");
        var target = WaitFor(() => TestWindowOf(targetProcess.Id), "the topmost window to open");
        nint ownWindow = 0;
        try
        {
            Assert.True(SetWindowPos(target, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE));
            if (appHoldsTheForeground)
            {
                ownWindow = CreateWindowEx(0, "STATIC", "Game Backdrop test", WS_POPUP | WS_VISIBLE, 0, 0, 16, 16, 0, 0, 0, 0);
                Assert.NotEqual(0, ownWindow);
            }
            var active = appHoldsTheForeground ? ownWindow : _hwnd;
            if (!TryMakeForeground(active)) { NotChecked("the active window could not take the foreground"); return; }
            if (!IsTopmost(target) || GetForegroundWindow() != active) { NotChecked("the foreground or the target changed under the test"); return; }
            Assert.True(GetWindowRect(target, out var corner));
            var bounds = new Rect(corner.Left, corner.Top, corner.Left + 16, corner.Top + 16);
            var backdrop = _windows.CreateBackdrop();
            string Order() => $"target Z {ZIndex(target)}, backdrop Z {ZIndex(backdrop.Value)}, active app Z {ZIndex(active)}, ordinary window Z {ZIndex(_hwnd)}";
            _output.WriteLine($"created: {Order()}");

            _windows.ShowBackdrop(backdrop, bounds, new WindowHandle(target));

            _output.WriteLine($"shown: {Order()}");
            var exStyle = (uint)GetWindowLongPtr(backdrop.Value, GWL_EXSTYLE);
            Assert.True((exStyle & WS_EX_TOPMOST) == 0, $"never topmost (ex-style 0x{exStyle:X8})");
            Assert.True(IsWindowVisible(backdrop.Value));
            Assert.True(ZIndex(backdrop.Value) > ZIndex(active), $"below the active app ({Order()})");
            Assert.True(ZIndex(backdrop.Value) > ZIndex(_hwnd), $"below the ordinary window ({Order()})");
            Assert.Equal(active, GetForegroundWindow());
        }
        finally
        {
            SetWindowPos(target, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            targetProcess.Kill();
            if (ownWindow != 0) DestroyWindow(ownWindow);
        }
    }

    private static bool IsTopmost(nint hwnd) => ((uint)GetWindowLongPtr(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;

    [Fact]
    public void Showing_a_backdrop_does_not_take_the_foreground()
    {
        var bounds = _windows.Describe(new WindowHandle(_hwnd))!.Monitor.Bounds;
        var before = GetForegroundWindow();

        _windows.ShowBackdrop(_windows.CreateBackdrop(), bounds, new WindowHandle(_hwnd));

        Assert.Equal(before, GetForegroundWindow());
    }

    [Fact]
    public void A_hidden_backdrop_is_not_visible_and_a_destroyed_one_is_gone()
    {
        var bounds = _windows.Describe(new WindowHandle(_hwnd))!.Monitor.Bounds;
        var backdrop = _windows.CreateBackdrop();
        Assert.False(IsWindowVisible(backdrop.Value), "created hidden");
        _windows.ShowBackdrop(backdrop, bounds, new WindowHandle(_hwnd));

        _windows.HideBackdrop(backdrop);
        Assert.True(IsWindow(backdrop.Value));
        Assert.False(IsWindowVisible(backdrop.Value));

        _windows.DestroyBackdrop(backdrop);
        Assert.False(IsWindow(backdrop.Value));
        _windows.DestroyBackdrop(backdrop); // already gone: no-op
    }

    [Theory]
    [InlineData(WM_LBUTTONDOWN)]
    [InlineData(WM_RBUTTONDOWN)]
    [InlineData(WM_MBUTTONDOWN)]
    public void A_click_on_a_backdrop_raises_BackdropClicked_and_does_not_activate_it(uint button)
    {
        var bounds = _windows.Describe(new WindowHandle(_hwnd))!.Monitor.Bounds;
        var backdrop = _windows.CreateBackdrop();
        _windows.ShowBackdrop(backdrop, bounds, new WindowHandle(_hwnd));
        var clicks = new List<BackdropHandle>();
        _windows.BackdropClicked += clicks.Add;

        Assert.Equal(MA_NOACTIVATE, SendMessage(backdrop.Value, WM_MOUSEACTIVATE, backdrop.Value, (nint)(HTCLIENT | (button << 16))));
        Assert.True(PostMessage(backdrop.Value, button, 0, 0));
        while (PeekMessage(out var msg, backdrop.Value, 0, 0, PM_REMOVE)) DispatchMessage(ref msg);

        Assert.Equal([backdrop], clicks);
    }

    [Fact]
    public void Hiding_a_window_raises_Hidden_with_its_lifetime()
    {
        var handle = new WindowHandle(_hwnd);
        var lifetime = _windows.Describe(handle)!.Lifetime;

        ShowWindow(_hwnd, SW_HIDE);
        WaitFor(() => IsWindowVisible(_hwnd) ? 0 : 1, "the window to hide");

        WaitFor(() =>
        {
            _windows.Describe(handle); // takes in the queued window events
            return _events.Contains(new WindowEvent(WindowEventKind.Hidden, handle, lifetime)) ? 1 : 0;
        }, "the Hidden event");
    }

    [Fact]
    public void Marking_a_window_fullscreen_and_back_succeeds()
    {
        // xUnit 2 has no runtime skip: with no shell taskbar (a CI runner without Explorer) there
        // is nothing to tell, so the test passes without checking.
        if (FindWindow("Shell_TrayWnd", null) == 0) { NotChecked("no shell taskbar"); return; }
        var handle = new WindowHandle(_hwnd);

        Assert.Equal(ChangeResult.Ok, _windows.MarkFullscreen(handle, true));
        Assert.Equal(ChangeResult.Ok, _windows.MarkFullscreen(handle, false));
    }

    [Fact]
    public void Marking_or_activating_a_window_that_is_gone_says_so()
    {
        var handle = new WindowHandle(_hwnd);
        _testWindow.Kill();
        _testWindow.WaitForExit();
        WaitFor(() => IsWindow(_hwnd) ? 0 : 1, "the window to be destroyed");

        Assert.Equal(ChangeResult.WindowGone, _windows.MarkFullscreen(handle, true));
        Assert.Equal(ChangeResult.WindowGone, _windows.Activate(handle));
    }

    [Fact]
    public void Clipping_keeps_the_cursor_inside_the_game_s_client_area_and_releasing_frees_it()
    {
        var area = _windows.Describe(new WindowHandle(_hwnd))!.Client;
        if (!CursorCanBeClipped(out var free)) return;
        try
        {
            _windows.ClipCursor(area);

            Assert.Equal(area, CurrentClip());

            _windows.ReleaseCursor();

            Assert.Equal(free, CurrentClip());
        }
        finally
        {
            ClipCursor(0);
        }
    }

    [Fact]
    public void Releasing_leaves_a_clip_another_program_set_since_alone()
    {
        var area = _windows.Describe(new WindowHandle(_hwnd))!.Client;
        if (!CursorCanBeClipped(out _)) return;
        var theirs = new RECT { Left = area.Left + 10, Top = area.Top + 10, Right = area.Right - 10, Bottom = area.Bottom - 10 };
        try
        {
            _windows.ClipCursor(area);
            ClipCursor(ref theirs);

            _windows.ReleaseCursor();

            Assert.Equal(new Rect(theirs.Left, theirs.Top, theirs.Right, theirs.Bottom), CurrentClip());
        }
        finally
        {
            ClipCursor(0);
        }
    }

    /// <summary>
    /// Whether this desktop lets the cursor be clipped (a test runner's may not), tried by hand and
    /// freed again; <paramref name="free"/> is the clip of a free cursor (the whole virtual screen).
    /// </summary>
    private bool CursorCanBeClipped(out Rect free)
    {
        free = CurrentClip();
        var probe = new RECT { Left = free.Left, Top = free.Top, Right = free.Left + 100, Bottom = free.Top + 100 };
        var clipped = ClipCursor(ref probe) && CurrentClip() == new Rect(probe.Left, probe.Top, probe.Right, probe.Bottom);
        ClipCursor(0);
        if (!clipped) NotChecked("this desktop doesn't let the cursor be clipped");
        return clipped;
    }

    private static Rect CurrentClip()
    {
        Assert.True(GetClipCursor(out var r));
        return new Rect(r.Left, r.Top, r.Right, r.Bottom);
    }

    /// <summary>The test passes without checking: say why, so a run shows which tests checked nothing.</summary>
    private void NotChecked(string why) => _output.WriteLine($"NOT CHECKED: {why}.");

    /// <summary>
    /// Make <paramref name="hwnd"/> the foreground window. A process in the background may not
    /// take the foreground; a tap of ALT (down and up, no other key) lets it.
    /// </summary>
    private static bool TryMakeForeground(nint hwnd)
    {
        for (var attempt = 0; attempt < 5 && GetForegroundWindow() != hwnd; attempt++)
        {
            keybd_event(VK_MENU, 0, 0, 0);
            SetForegroundWindow(hwnd);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0);
            Thread.Sleep(200);
        }
        return GetForegroundWindow() == hwnd;
    }

    /// <summary>The window's position in the desktop's Z order, front first.</summary>
    private static int ZIndex(nint hwnd)
    {
        var index = 0;
        for (var next = GetTopWindow(0); next != 0; next = GetWindow(next, GW_HWNDNEXT), index++)
            if (next == hwnd) return index;
        return int.MaxValue;
    }

    /// <summary>The first visible window below <paramref name="hwnd"/> in the Z order.</summary>
    private static nint NextVisibleBelow(nint hwnd)
    {
        for (var next = GetWindow(hwnd, GW_HWNDNEXT); next != 0; next = GetWindow(next, GW_HWNDNEXT))
            if (IsWindowVisible(next)) return next;
        return 0;
    }

    private static Process StartTestWindow(string arguments)
    {
        var exe = typeof(Win32WindowSystemTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "TestWindowExe").Value!;
        return Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false })!;
    }

    private static nint WaitFor(Func<nint> probe, string what)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            var result = probe();
            if (result != 0) return result;
            Thread.Sleep(50);
        }
        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    private static nint TestWindowOf(int processId)
    {
        nint found = 0;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            var name = new StringBuilder(64);
            GetClassName(hwnd, name, name.Capacity);
            if (pid == processId && name.ToString() == "GameBackdropTestWindow" && IsWindowVisible(hwnd)) found = hwnd;
            return found == 0;
        }, 0);
        return found;
    }

    private sealed class RunAtOnce : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }

    private const int SW_HIDE = 0, SW_MINIMIZE = 6, SW_RESTORE = 9;
    private const int GWL_EXSTYLE = -20;
    private const uint GW_HWNDNEXT = 2, GW_OWNER = 4;
    private const uint WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    private const uint WM_MOUSEACTIVATE = 0x0021, WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDOWN = 0x0207;
    private const uint HTCLIENT = 1;
    private const nint MA_NOACTIVATE = 3;
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClipCursor(ref RECT rect);
    [DllImport("user32.dll")] private static extern bool ClipCursor(nint none);
    [DllImport("user32.dll")] private static extern bool GetClipCursor(out RECT rect);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetTopWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, nint extraInfo);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
    private static extern nint CreateWindowEx(uint exStyle, string className, string? windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    private const uint WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000;
    private const nint HWND_TOP = 0, HWND_TOPMOST = -1, HWND_NOTOPMOST = -2;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x2;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string className, string? windowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessage(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DispatchMessage(ref MSG msg);
    private const uint WM_APP_PROBE = 0x8000 + 1, PM_REMOVE = 1;
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public int x, y; public uint priv; }
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG msg, nint hwnd, uint min, uint max, uint remove);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);

    [ComImport, Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")]
    private class VirtualDesktopManager;

    [ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(nint hwnd, [MarshalAs(UnmanagedType.Bool)] out bool onCurrent);
        [PreserveSig] int GetWindowDesktopId(nint hwnd, out Guid desktop);
        [PreserveSig] int MoveWindowToDesktop(nint hwnd, in Guid desktop);
    }
}
