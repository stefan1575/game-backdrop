using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using GameBackdrop.Core.Geometry;
using GameBackdrop.Core.Layout;
using GameBackdrop.Core.WindowSystem;

namespace GameBackdrop.Windows.Tests;

/// <summary>
/// The Win32 port against real windows: each test starts its own <c>TestWindow.exe</c> (another
/// process, because the port skips its own process's window events) and kills it afterwards.
/// </summary>
public sealed class Win32WindowSystemTests : IDisposable
{
    private readonly Process _testWindow;
    private readonly nint _hwnd;
    private readonly Win32WindowSystem _windows;
    private readonly List<WindowEvent> _events = [];

    public Win32WindowSystemTests()
    {
        // The port posts its events to the current SynchronizationContext; this one runs them at
        // once, on this thread, which also owns the hooks.
        SynchronizationContext.SetSynchronizationContext(new RunAtOnce());
        _windows = new Win32WindowSystem();
        _windows.WindowEvent += _events.Add;

        var exe = typeof(Win32WindowSystemTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "TestWindowExe").Value!;
        _testWindow = Process.Start(new ProcessStartInfo(exe, "--size 800x600") { UseShellExecute = false })!;
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
    public void A_window_keeps_its_lifetime_when_it_changes_itself()
    {
        var handle = new WindowHandle(_hwnd);
        var before = _windows.Describe(handle)!;

        _windows.SetFrame(handle, new WindowFrame(before.Style.Borderless(), Rect.FromPosition(10, 10, before.Client.Size)));
        _windows.CatchUp();

        Assert.Equal(before.Lifetime, _windows.Describe(handle)!.Lifetime);
    }

    [Fact]
    public void Catching_up_takes_in_a_window_s_destruction_and_its_handle_has_no_lifetime_left()
    {
        var handle = new WindowHandle(_hwnd);
        var lifetime = _windows.Describe(handle)!.Lifetime;

        _testWindow.Kill();
        _testWindow.WaitForExit();
        WaitFor(() => IsWindow(_hwnd) ? 0 : 1, "the window to be destroyed");
        _windows.CatchUp();

        Assert.Contains(new WindowEvent(WindowEventKind.Destroyed, handle, lifetime), _events);
        Assert.Null(_windows.Describe(handle));
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

    private const int SW_MINIMIZE = 6, SW_RESTORE = 9;
    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
}
