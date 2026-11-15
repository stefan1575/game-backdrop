using System.Runtime.InteropServices;
using static TestWindow.Native;

namespace TestWindow;

/// <summary>
/// A stand-in game for checking Game Backdrop's real Win32 path without a game. It is a plain
/// Win32 window (no WinForms, which re-applies its own bounds when another process restyles it),
/// so it behaves like a typical game window. List this exe in the settings file, then switch it
/// with keys: W windowed, F borderless fullscreen, R next size, Esc quit.
/// Usage: <c>TestWindow.exe [--mode windowed|fullscreen] [--size WIDTHxHEIGHT]</c>
/// </summary>
internal static class Program
{
    private static readonly (int Width, int Height)[] Sizes = [(800, 600), (1280, 720), (640, 480)];
    private static readonly WndProc Proc = WindowProc; // kept alive for the window's lifetime
    private static (int Width, int Height) _clientSize;
    private static int _nextSize;
    private static nint _brush;

    [STAThread]
    private static int Main(string[] args)
    {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        _clientSize = ParseSize(Option(args, "--size")) ?? Sizes[0];
        _brush = CreateSolidBrush(0x008B3D48); // dark slate blue (BGR)

        var instance = GetModuleHandle(null);
        var windowClass = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
            hInstance = instance,
            hCursor = LoadCursor(0, IDC_ARROW),
            hbrBackground = _brush,
            lpszClassName = "GameBackdropTestWindow",
        };
        if (RegisterClassEx(ref windowClass) == 0) return 1;

        var outer = OuterFor(_clientSize, WS_OVERLAPPEDWINDOW);
        var hwnd = CreateWindowEx(0, windowClass.lpszClassName, "Game Backdrop test window", WS_OVERLAPPEDWINDOW,
            100, 100, outer.Width, outer.Height, 0, 0, instance, 0);
        if (hwnd == 0) return 1;
        if (Option(args, "--mode") == "fullscreen") MakeBorderlessFullscreen(hwnd);
        ShowWindow(hwnd, SW_SHOWNORMAL);

        while (GetMessage(out var msg, 0, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        return 0;
    }

    private static nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_KEYDOWN:
                switch ((int)wParam)
                {
                    case 'W': MakeWindowed(hwnd); break;
                    case 'F': MakeBorderlessFullscreen(hwnd); break;
                    case 'R': NextSize(hwnd); break;
                    case VK_ESCAPE: DestroyWindow(hwnd); break;
                }
                return 0;
            case WM_SIZE:
            case WM_MOVE:
                InvalidateRect(hwnd, 0, true);
                break;
            case WM_PAINT:
                Paint(hwnd);
                return 0;
            case WM_DESTROY:
                PostQuitMessage(0);
                return 0;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static void MakeWindowed(nint hwnd)
    {
        SetWindowLongPtr(hwnd, GWL_STYLE, (nint)(WS_OVERLAPPEDWINDOW | WS_VISIBLE));
        var outer = OuterFor(_clientSize, WS_OVERLAPPEDWINDOW);
        SetWindowPos(hwnd, 0, 100, 100, outer.Width, outer.Height, SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    private static void MakeBorderlessFullscreen(nint hwnd)
    {
        var monitor = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref monitor);
        var visible = IsWindowVisible(hwnd) ? WS_VISIBLE : 0;
        SetWindowLongPtr(hwnd, GWL_STYLE, (nint)(WS_POPUP | visible));
        var b = monitor.rcMonitor;
        SetWindowPos(hwnd, 0, b.Left, b.Top, b.Right - b.Left, b.Bottom - b.Top, SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    /// <summary>Change the client size and keep the current frame, the way a game's resolution change would.</summary>
    private static void NextSize(nint hwnd)
    {
        _clientSize = Sizes[++_nextSize % Sizes.Length];
        var style = (uint)GetWindowLongPtr(hwnd, GWL_STYLE);
        var outer = OuterFor(_clientSize, style);
        SetWindowPos(hwnd, 0, 0, 0, outer.Width, outer.Height, SWP_NOZORDER | SWP_NOMOVE);
    }

    private static (int Width, int Height) OuterFor((int Width, int Height) client, uint style)
    {
        var rect = new RECT { Right = client.Width, Bottom = client.Height };
        AdjustWindowRectExForDpi(ref rect, style, false, 0, GetDpiForSystem());
        return (rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    private static void Paint(nint hwnd)
    {
        var dc = BeginPaint(hwnd, out var paint);
        GetWindowRect(hwnd, out var outer);
        GetClientRect(hwnd, out var client);
        var style = (uint)GetWindowLongPtr(hwnd, GWL_STYLE);
        var text = $"Game Backdrop test window\n\nW windowed   F borderless fullscreen   R next size   Esc quit\n\n" +
                   $"Outer ({outer.Left},{outer.Top})-({outer.Right},{outer.Bottom})\nClient {client.Right}x{client.Bottom}\nStyle 0x{style:X8}";
        SetTextColor(dc, 0x00FFFFFF);
        SetBkMode(dc, TRANSPARENT);
        var area = new RECT { Left = 16, Top = 16, Right = client.Right - 16, Bottom = client.Bottom - 16 };
        DrawText(dc, text, -1, ref area, 0);
        EndPaint(hwnd, ref paint);
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static (int, int)? ParseSize(string? text)
    {
        var parts = text?.Split('x');
        return parts is [var w, var h] && int.TryParse(w, out var width) && int.TryParse(h, out var height)
            ? (width, height)
            : null;
    }
}
