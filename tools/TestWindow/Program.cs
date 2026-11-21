using System.Runtime.InteropServices;
using static TestWindow.Native;

namespace TestWindow;

/// <summary>
/// A stand-in game for checking Game Backdrop's real Win32 path without a game. It is a plain
/// Win32 window (no WinForms, which re-applies its own bounds when another process restyles it),
/// so it behaves like a typical game window. List this exe in the settings file, then switch it
/// with keys: W windowed, F borderless fullscreen, R next size, C cloak or uncloak, Esc quit.
/// C stands in for a switch to another virtual desktop, which cloaks a window (only a window's
/// own process may cloak it, so the test window does it itself). <c>--menu</c> gives it
/// a menu bar, like many visual novels have; <c>--menu-items N</c> gives it N menu items, so a
/// narrow window's menu bar wraps onto more than one row. Like a game, it draws at its own
/// resolution: the client size it last set itself, outlined in yellow, so a screenshot shows at
/// once when another program left it less room than that.
/// Usage: <c>TestWindow.exe [--mode windowed|fullscreen] [--size WIDTHxHEIGHT] [--menu] [--menu-items N]</c>
/// </summary>
internal static class Program
{
    private static readonly (int Width, int Height)[] Sizes = [(800, 600), (1280, 720), (640, 480)];
    private static readonly WndProc Proc = WindowProc; // kept alive for the window's lifetime
    private static (int Width, int Height) _clientSize;
    private static int _nextSize;
    private static nint _brush;
    private static bool _hasMenu;
    private static bool _cloaked;
    private static RECT _rendered; // the client area the window last sized itself to: what it "renders"

    [STAThread]
    private static int Main(string[] args)
    {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        _clientSize = ParseSize(Option(args, "--size")) ?? Sizes[0];
        var menuItems = int.TryParse(Option(args, "--menu-items"), out var items) ? items : args.Contains("--menu") ? 1 : 0;
        _hasMenu = menuItems > 0;
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
        nint menu = 0;
        if (_hasMenu)
        {
            menu = CreateMenu();
            string[] names = ["&File", "&Edit", "&View", "&Game", "&Options", "&Window", "&Tools", "&Help"];
            for (var i = 0; i < menuItems; i++)
                AppendMenu(menu, MF_STRING, (nuint)(i + 1), i < names.Length ? names[i] : $"Item &{i + 1}");
        }
        var hwnd = CreateWindowEx(0, windowClass.lpszClassName, "Game Backdrop test window", WS_OVERLAPPEDWINDOW,
            100, 100, outer.Width, outer.Height, 0, menu, instance, 0);
        if (hwnd == 0) return 1;
        if (Option(args, "--mode") == "fullscreen") MakeBorderlessFullscreen(hwnd);
        GetClientRect(hwnd, out _rendered);
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
                    case 'C': ToggleCloak(hwnd); break;
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
        GetClientRect(hwnd, out _rendered);
    }

    private static void MakeBorderlessFullscreen(nint hwnd)
    {
        var monitor = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref monitor);
        var visible = IsWindowVisible(hwnd) ? WS_VISIBLE : 0;
        SetWindowLongPtr(hwnd, GWL_STYLE, (nint)(WS_POPUP | visible));
        var b = monitor.rcMonitor;
        SetWindowPos(hwnd, 0, b.Left, b.Top, b.Right - b.Left, b.Bottom - b.Top, SWP_NOZORDER | SWP_FRAMECHANGED);
        GetClientRect(hwnd, out _rendered);
    }

    private static void ToggleCloak(nint hwnd)
    {
        _cloaked = !_cloaked;
        var cloak = _cloaked ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref cloak, sizeof(int));
    }

    /// <summary>Change the client size and keep the current frame, the way a game's resolution change would.</summary>
    private static void NextSize(nint hwnd)
    {
        _clientSize = Sizes[++_nextSize % Sizes.Length];
        var style = (uint)GetWindowLongPtr(hwnd, GWL_STYLE);
        var outer = OuterFor(_clientSize, style);
        SetWindowPos(hwnd, 0, 0, 0, outer.Width, outer.Height, SWP_NOZORDER | SWP_NOMOVE);
        GetClientRect(hwnd, out _rendered);
    }

    private static (int Width, int Height) OuterFor((int Width, int Height) client, uint style)
    {
        var rect = new RECT { Right = client.Width, Bottom = client.Height };
        AdjustWindowRectExForDpi(ref rect, style, _hasMenu, 0, GetDpiForSystem());
        return (rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    private static void Paint(nint hwnd)
    {
        var dc = BeginPaint(hwnd, out var paint);
        GetWindowRect(hwnd, out var outer);
        GetClientRect(hwnd, out var client);
        var style = (uint)GetWindowLongPtr(hwnd, GWL_STYLE);
        var text = $"Game Backdrop test window\n\nW windowed   F borderless fullscreen   R next size   C cloak   Esc quit\n\n" +
                   $"Outer ({outer.Left},{outer.Top})-({outer.Right},{outer.Bottom})\nClient {client.Right}x{client.Bottom}\nStyle 0x{style:X8}\n\n" +
                   $"Renders at {_rendered.Right}x{_rendered.Bottom} (the yellow frame)";
        var border = CreateSolidBrush(0x0000D7FF); // yellow (BGR)
        for (var i = 0; i < 4; i++)
        {
            var edge = new RECT { Left = i, Top = i, Right = _rendered.Right - i, Bottom = _rendered.Bottom - i };
            FrameRect(dc, ref edge, border);
        }
        DeleteObject(border);
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
