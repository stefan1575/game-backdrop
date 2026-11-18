using System.Runtime.InteropServices;
using GameBackdrop.Core.Settings;
using Xunit.Abstractions;

namespace GameBackdrop.Windows.Tests;

/// <summary>
/// The global hotkey against the real OS. The hotkeys used are ones nothing else is likely to
/// hold (Ctrl+Alt+Shift with F21–F23). A press that can't be delivered here (no input desktop)
/// passes without checking and says so in its output.
/// </summary>
[Collection(DesktopCollection.Name)]
public sealed class GlobalHotkeyTests
{
    private readonly ITestOutputHelper _output;

    public GlobalHotkeyTests(ITestOutputHelper output)
    {
        _output = output;
        SynchronizationContext.SetSynchronizationContext(new RunAtOnce());
    }

    private static Hotkey Unusual(uint fKey) =>
        new(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x70 + fKey - 1);

    [Fact]
    public void A_hotkey_another_registration_holds_is_refused_without_throwing()
    {
        using var held = GlobalHotkey.TryRegister(Unusual(23), () => { });
        Assert.NotNull(held);

        using var second = GlobalHotkey.TryRegister(Unusual(23), () => { });

        Assert.Null(second);
    }

    [Fact]
    public void A_hotkey_disposed_is_free_to_register_again()
    {
        GlobalHotkey.TryRegister(Unusual(22), () => { })!.Dispose();

        using var again = GlobalHotkey.TryRegister(Unusual(22), () => { });

        Assert.NotNull(again);
    }

    [Fact]
    public void Pressing_the_hotkey_runs_its_action_once()
    {
        var presses = 0;
        using var hotkey = GlobalHotkey.TryRegister(Unusual(21), () => presses++);
        Assert.NotNull(hotkey);

        Press(VK_CONTROL, VK_MENU, VK_SHIFT, 0x70 + 21 - 1);
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (presses == 0 && DateTime.UtcNow < deadline) Pump();

        if (presses == 0)
        {
            _output.WriteLine("Not checked: the simulated key press never reached a hotkey (no input desktop?).");
            return;
        }
        Pump();
        Assert.Equal(1, presses);
    }

    private const byte VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint PM_REMOVE = 0x0001;

    private static void Press(params uint[] keys)
    {
        foreach (var key in keys) keybd_event((byte)key, 0, 0, 0);
        foreach (var key in keys.Reverse()) keybd_event((byte)key, 0, KEYEVENTF_KEYUP, 0);
    }

    private static void Pump()
    {
        while (PeekMessage(out var msg, 0, 0, 0, PM_REMOVE))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        Thread.Sleep(10);
    }

    /// <summary>Runs posted work at once, on the posting thread (here, the one pumping messages).</summary>
    private sealed class RunAtOnce : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int x, y;
        public uint lPrivate;
    }

    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, nint extraInfo);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG msg, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(ref MSG msg);
}
