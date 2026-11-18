using System.Text;
using GameBackdrop.Core.WindowSystem;
using static GameBackdrop.Windows.NativeMethods;

namespace GameBackdrop.Windows;

/// <summary>A window open now, as the list window's "add from open windows" picker shows it.</summary>
public sealed record OpenWindow(WindowHandle Handle, string Title, string ExePath);

/// <summary>The windows open right now that could be a game's, for picking one to add to the list.</summary>
public static class OpenWindows
{
    /// <summary>
    /// The visible, titled, unowned top-level windows of other processes, the ones alt-tab would
    /// show, front first, one per exe (its frontmost window). Reads only window handles and each
    /// process's exe path (with PROCESS_QUERY_LIMITED_INFORMATION, as the port does); a process
    /// whose path can't be read (an elevated one) is left out.
    /// </summary>
    public static IReadOnlyList<OpenWindow> List()
    {
        var ownProcess = (uint)Environment.ProcessId;
        var windows = new List<OpenWindow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        EnumWindows((hwnd, _) =>
        {
            if (Candidate(hwnd, ownProcess) is { } window && seen.Add(window.ExePath)) windows.Add(window);
            return true;
        }, 0);
        return windows;
    }

    private static OpenWindow? Candidate(nint hwnd, uint ownProcess)
    {
        if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != 0) return null;
        if (((uint)GetWindowLongPtr(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return null;
        // Cloaked windows (suspended Store apps, other virtual desktops) are "visible" but not on screen.
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return null;
        GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == ownProcess) return null;

        var title = Title(hwnd);
        if (string.IsNullOrWhiteSpace(title)) return null;
        var exe = Win32WindowSystem.ProcessDetails(processId).Path;
        return exe is null ? null : new OpenWindow(new WindowHandle(hwnd), title, exe);
    }

    private static string Title(nint hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0) return "";
        var text = new StringBuilder(length + 1);
        GetWindowText(hwnd, text, text.Capacity);
        return text.ToString();
    }
}
