using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using GameBackdrop.Core.Settings;
using static GameBackdrop.Windows.NativeMethods;

namespace GameBackdrop.Windows;

/// <summary>
/// A system-wide hotkey (RegisterHotKey) on a message-only window of this process. Nothing is
/// hooked: Windows posts WM_HOTKEY to that window, so no keyboard input of any other process is
/// read. Create it on a UI thread with a SynchronizationContext; each press is posted there,
/// never run inside the window procedure. Key repeat while it is held down is ignored.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const string WindowClass = "GameBackdrop.Hotkey";
    private const int HotkeyId = 1;
    private static readonly WndProc Proc = OnMessage;
    private static readonly Lazy<string> ClassRegistered = new(RegisterClass);
    private static readonly ConcurrentDictionary<nint, GlobalHotkey> Owners = new();

    private readonly SynchronizationContext _context;
    private readonly Action _pressed;
    private nint _hwnd;

    private GlobalHotkey(SynchronizationContext context, Action pressed, nint hwnd)
    {
        _context = context;
        _pressed = pressed;
        _hwnd = hwnd;
    }

    /// <summary>
    /// Register <paramref name="hotkey"/>, running <paramref name="pressed"/> on each press. Null
    /// if it can't be registered, as when another app already holds it.
    /// </summary>
    public static GlobalHotkey? TryRegister(Hotkey hotkey, Action pressed)
    {
        var context = SynchronizationContext.Current
            ?? throw new InvalidOperationException("Register the hotkey on a UI thread with a SynchronizationContext.");
        var hwnd = CreateWindowEx(0, ClassRegistered.Value, null, 0, 0, 0, 0, 0, HWND_MESSAGE, 0, GetModuleHandle(null), 0);
        if (hwnd == 0) throw new InvalidOperationException($"CreateWindowEx(hotkey) failed: {Marshal.GetLastPInvokeError()}.");

        var registered = new GlobalHotkey(context, pressed, hwnd);
        Owners[hwnd] = registered;
        if (RegisterHotKey(hwnd, HotkeyId, (uint)hotkey.Modifiers | MOD_NOREPEAT, hotkey.VirtualKey)) return registered;
        registered.Dispose();
        return null;
    }

    public void Dispose()
    {
        if (_hwnd == 0) return;
        UnregisterHotKey(_hwnd, HotkeyId);
        Owners.TryRemove(_hwnd, out _);
        DestroyWindow(_hwnd);
        _hwnd = 0;
    }

    private static string RegisterClass()
    {
        var windowClass = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
            hInstance = GetModuleHandle(null),
            lpszClassName = WindowClass,
        };
        if (RegisterClassEx(ref windowClass) == 0 && Marshal.GetLastPInvokeError() != ERROR_CLASS_ALREADY_EXISTS)
            throw new InvalidOperationException($"RegisterClassEx(hotkey) failed: {Marshal.GetLastPInvokeError()}.");
        return WindowClass;
    }

    private static nint OnMessage(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_HOTKEY && wParam == HotkeyId && Owners.TryGetValue(hwnd, out var owner))
        {
            owner._context.Post(_ => { if (owner._hwnd != 0) owner._pressed(); }, null);
            return 0;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
