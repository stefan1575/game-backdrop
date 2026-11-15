using System.Diagnostics.CodeAnalysis;
using GameBackdrop.Core.Games;
using GameBackdrop.Core.Geometry;
using GameBackdrop.Core.Layout;
using GameBackdrop.Core.Settings;
using GameBackdrop.Core.WindowSystem;

namespace GameBackdrop.Core.Controller;

/// <summary>What the controller has done to a tracked game window.</summary>
public enum TrackedState
{
    /// <summary>The window fills its monitor: the game's own mode wins and nothing was changed.</summary>
    Fullscreen,
    /// <summary>
    /// The window was made borderless and centred (or half-changed by a failed attempt);
    /// <see cref="TrackedWindow.Original"/> puts it back.
    /// </summary>
    Restyled,
    /// <summary>The port refused the change (an elevated game): nothing was changed, and it isn't retried.</summary>
    AccessDenied,
    /// <summary>
    /// A replaced splash window that couldn't be put back yet. It is no longer the game's window,
    /// but its original is kept so <see cref="BackdropController.RestoreAll"/> tries again.
    /// </summary>
    RestorePending,
}


/// <summary>A game window the controller is tracking: one per game process.</summary>
/// <param name="Lifetime">Which window behind <paramref name="Window"/> this is
/// (<see cref="WindowInfo.Lifetime"/>): a handle can be reused once its window is destroyed, even
/// by a new window of the same game, so the controller checks the lifetime before every call on
/// the handle. A window that changes its own frame keeps its lifetime, and its original.</param>
/// <param name="Original">The window's frame before the controller first touched it.</param>
/// <param name="HadCaption">Whether the window had a title bar when it was first seen.</param>
/// <param name="Monitor">The monitor the game is on, as the controller last placed it.</param>
/// <param name="Backdrop">The black window behind a restyled game; null once it is destroyed.</param>
/// <param name="MarkedFullscreen">The shell has been told the game is fullscreen (and not yet told
/// otherwise), so the taskbar drops behind it while it is in front.</param>
/// <param name="FrameRestored">The original frame is back; only the unmark is left to retry.</param>
public sealed record TrackedWindow(
    WindowHandle Window,
    int ProcessId,
    string? ProcessPath,
    GameEntry Game,
    TrackedState State,
    long Lifetime,
    WindowFrame Original,
    bool HadCaption,
    Size ClientSize,
    MonitorInfo Monitor,
    BackdropHandle? Backdrop = null,
    bool MarkedFullscreen = false,
    bool FrameRestored = false);

/// <summary>
/// Turns window events into actions on the window system: a state machine per tracked game
/// window. Single-threaded: call it, and raise the port's events, from one thread.
/// </summary>
public sealed class BackdropController(IWindowSystem windows, GameList games)
{
    private readonly Dictionary<WindowHandle, TrackedWindow> _tracked = [];
    private bool _running;

    public IReadOnlyCollection<TrackedWindow> TrackedWindows => _tracked.Values;

    /// <summary>Start watching, and handle the listed games that are already running.</summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        windows.WindowEvent += OnWindowEvent;
        windows.BackdropClicked += OnBackdropClicked;
        foreach (var window in windows.TopLevelWindows()) Consider(window);
    }

    /// <summary>
    /// Stop watching, remove every backdrop and put every changed game window back as it was.
    /// Returns the windows that couldn't be put back; they keep their originals, and calling Stop
    /// again retries them.
    /// </summary>
    public IReadOnlyList<TrackedWindow> Stop()
    {
        if (_running)
        {
            _running = false;
            windows.WindowEvent -= OnWindowEvent;
            windows.BackdropClicked -= OnBackdropClicked;
        }
        // Nothing moves a backdrop once events stop, so none stays, even behind a game that
        // couldn't be put back.
        foreach (var tracked in _tracked.Values.ToList()) DropBackdrop(tracked);
        return RestoreAll();
    }

    /// <summary>
    /// Put every changed game window back. Returns the windows that couldn't be put back: they
    /// stay tracked with their originals, so a later call tries again. Safe to call more than once,
    /// and from a crash handler.
    /// </summary>
    public IReadOnlyList<TrackedWindow> RestoreAll()
    {
        var notRestored = new List<TrackedWindow>();
        foreach (var tracked in _tracked.Values.ToList())
        {
            var left = TryRestore(tracked);
            if (left is null)
            {
                Forget(tracked);
                continue;
            }
            // A window put back has no use for its backdrop, even while its unmark is retried.
            _tracked[left.Window] = left.FrameRestored ? DropBackdrop(left) : left;
            notRestored.Add(_tracked[left.Window]);
        }
        return notRestored;
    }

    private void OnWindowEvent(WindowEvent e)
    {
        switch (e.Kind)
        {
            case WindowEventKind.Shown:
                Consider(e.Window);
                return;
            case WindowEventKind.Destroyed:
                // A game that has exited needs no restore. Destroy events arrive queued, so the
                // handle may already belong to a newer window: forget only the window that ended.
                if (_tracked.TryGetValue(e.Window, out var ended) && ended.Lifetime == e.Lifetime) Forget(ended);
                return;
        }

        if (!TryGetLive(e.Window, out var tracked, out var info)) return;
        // An event about an earlier window behind this handle, posted before it was destroyed.
        if (e.Lifetime != 0 && e.Lifetime != tracked.Lifetime) return;
        if (tracked.Backdrop is not { } backdrop) return;
        switch (e.Kind)
        {
            case WindowEventKind.Foreground:
                ShowBackdrop(tracked, info);
                MarkAgain(tracked);
                break;
            case WindowEventKind.MinimizeEnd:
                ShowBackdrop(tracked, info);
                break;
            case WindowEventKind.MinimizeStart:
            case WindowEventKind.Hidden:
                windows.HideBackdrop(backdrop);
                break;
            case WindowEventKind.LocationChanged:
                FollowToMonitor(tracked, info);
                break;
        }
    }

    /// <summary>A click on the black hands focus back to its game; its Foreground event re-orders the backdrop.</summary>
    private void OnBackdropClicked(BackdropHandle backdrop)
    {
        var owner = _tracked.Values.FirstOrDefault(t => t.Backdrop == backdrop);
        if (owner is null || !TryGetLive(owner.Window, out var tracked, out _)) return;
        windows.Activate(tracked.Window);
    }

    private void Consider(WindowHandle handle)
    {
        if (TryGetLive(handle, out var known, out var live))
        {
            // The same window shown again: it keeps its backdrop, and its first original.
            ShowBackdrop(known, live);
            MarkAgain(known);
            return;
        }
        // Not tracked, or the tracked window is gone (and forgotten): whatever has the handle now is new.
        var info = windows.Describe(handle);
        if (info is null) return;

        if (!IsCandidate(info)) return;
        var game = games.FindEnabled(info.ProcessPath);
        if (game is null) return;

        var current = _tracked.Values.FirstOrDefault(t => t.ProcessId == info.ProcessId && t.State != TrackedState.RestorePending);
        if (current is not null)
        {
            if (!Replaces(info, current)) return;
            current = DropBackdrop(current);
            var left = TryRestore(current);
            if (left is null) _tracked.Remove(current.Window);
            else _tracked[current.Window] = left with { State = TrackedState.RestorePending };
        }

        Track(info, game);
    }

    /// <summary>
    /// Whether <paramref name="handle"/> is tracked and still has the tracked window behind it:
    /// the check before any call on a game's handle. A tracked handle that no longer does is
    /// forgotten (its backdrop destroyed, nothing put back or unmarked: the window it belonged to
    /// is gone, and the handle may be another window's now).
    /// </summary>
    private bool TryGetLive(WindowHandle handle, [NotNullWhen(true)] out TrackedWindow? tracked, [NotNullWhen(true)] out WindowInfo? info)
    {
        info = null;
        if (!_tracked.TryGetValue(handle, out tracked)) return false;
        info = windows.Describe(handle);
        if (IsStillTheSameWindow(tracked.Lifetime, info)) return true;
        Forget(tracked);
        tracked = null;
        info = null;
        return false;
    }

    /// <summary>A top-level window that could be a game's main window.</summary>
    private static bool IsCandidate(WindowInfo info) =>
        info.IsVisible && !info.IsMinimized && !info.IsOwned && !info.Style.IsToolWindow
        && info.Client.Width > 0 && info.Client.Height > 0;

    /// <summary>
    /// A later window from the same process replaces a launcher or splash window when it has a
    /// title bar or a larger client area.
    /// </summary>
    private static bool Replaces(WindowInfo candidate, TrackedWindow current) =>
        candidate.Style.HasCaption || candidate.Client.Size.Area > current.ClientSize.Area;

    private void Track(WindowInfo info, GameEntry game)
    {
        var tracked = new TrackedWindow(info.Handle, info.ProcessId, info.ProcessPath, game, TrackedState.Fullscreen,
            info.Lifetime, info.Frame, info.Style.HasCaption, info.Client.Size, info.Monitor);
        if (WindowClassifier.Classify(info.Client, info.Style.HasCaption, info.Monitor.Bounds) != WindowMode.Windowed)
        {
            Save(tracked);
            return;
        }

        // The client area keeps its size; whatever non-client part survives the restyle (a
        // menu bar) is added on, and the whole window is centred.
        var style = info.Style.Borderless();
        var outerSize = windows.OuterSizeFor(info.Handle, style, info.Client.Size);
        var result = windows.SetFrame(info.Handle, new WindowFrame(style, Placement.Centre(outerSize, info.Monitor.Bounds)));
        switch (result)
        {
            case ChangeResult.AccessDenied:
                Save(tracked with { State = TrackedState.AccessDenied });
                return;
            case ChangeResult.WindowGone:
            case ChangeResult.Failed:
                // Nothing was changed; a later Shown event gets another try.
                return;
        }

        // Changed, or half-changed, so it has to be put back: the record is kept from here on,
        // and brought up to date after each step, so that if a later step throws, the crash
        // handler's Stop still puts the window back and destroys whatever backdrop was made.
        tracked = Save(tracked with { State = TrackedState.Restyled });
        var after = windows.Describe(info.Handle);
        if (!IsStillTheSameWindow(info.Lifetime, after))
        {
            Forget(tracked); // gone already: nothing to put back
            return;
        }
        if (result == ChangeResult.Ok && after.Client.Size != info.Client.Size)
            CorrectClientSize(info, style, outerSize, after);
        tracked = Save(tracked with { Monitor = after.Monitor });
        GiveBackdrop(tracked, after);
    }

    /// <summary>
    /// Give a restyled game its backdrop over its monitor, directly behind it, and tell the shell
    /// it is fullscreen so the taskbar drops behind it while it is in front.
    /// </summary>
    private void GiveBackdrop(TrackedWindow tracked, WindowInfo info)
    {
        tracked = Save(tracked with { Backdrop = windows.CreateBackdrop() });
        ShowBackdrop(tracked, info);
        if (windows.MarkFullscreen(tracked.Window, true) == ChangeResult.Ok) Save(tracked with { MarkedFullscreen = true });
    }

    /// <summary>Store the record as the window's current one.</summary>
    private TrackedWindow Save(TrackedWindow tracked)
    {
        _tracked[tracked.Window] = tracked;
        return tracked;
    }

    /// <summary>
    /// Tell the shell again that a restyled game with a backdrop is fullscreen. Explorer forgets
    /// the mark when the window is hidden (it drops the taskbar tab) and when Explorer restarts,
    /// so this runs each time the game is shown again or comes to the foreground; the call is
    /// cheap and idempotent. A failed re-mark changes nothing: the next one tries again. Callers
    /// have just checked the handle (TryGetLive).
    /// </summary>
    private void MarkAgain(TrackedWindow tracked)
    {
        if (tracked.State != TrackedState.Restyled || tracked.Backdrop is null) return;
        if (windows.MarkFullscreen(tracked.Window, true) == ChangeResult.Ok && !tracked.MarkedFullscreen)
            _tracked[tracked.Window] = tracked with { MarkedFullscreen = true };
    }

    /// <summary>Show the game's backdrop over its monitor, directly behind it, if the game is on screen.</summary>
    private void ShowBackdrop(TrackedWindow tracked, WindowInfo info)
    {
        if (tracked.Backdrop is { } backdrop && info.IsVisible && !info.IsMinimized)
            windows.ShowBackdrop(backdrop, tracked.Monitor.Bounds, tracked.Window);
    }

    /// <summary>
    /// The game moved to another monitor: centre it there and move its backdrop with it. A move
    /// within its monitor (the controller's own placement included) changes nothing.
    /// </summary>
    private void FollowToMonitor(TrackedWindow tracked, WindowInfo info)
    {
        if (!info.IsVisible || info.IsMinimized || info.Monitor == tracked.Monitor) return;
        windows.SetFrame(tracked.Window, new WindowFrame(info.Style, Placement.Centre(info.Outer.Size, info.Monitor.Bounds)));
        tracked = tracked with { Monitor = info.Monitor };
        _tracked[tracked.Window] = tracked;
        ShowBackdrop(tracked, info);
    }

    /// <summary>
    /// The port's outer size can be short: a menu bar that wraps onto more than one row is taller
    /// than the port predicts. Grow the window by what the client area lost, measured, and centre
    /// it again. If that fails the window stays restyled and is still put back on quit.
    /// </summary>
    private void CorrectClientSize(WindowInfo before, WindowStyle style, Size outerSize, WindowInfo after)
    {
        var corrected = new Size(
            outerSize.Width + before.Client.Width - after.Client.Width,
            outerSize.Height + before.Client.Height - after.Client.Height);
        windows.SetFrame(before.Handle, new WindowFrame(style, Placement.Centre(corrected, before.Monitor.Bounds)));
    }

    /// <summary>
    /// Put a tracked window back as it was and unmark it. Returns null when nothing is left to do,
    /// else the record with what is done so far, for a later try that doesn't repeat it.
    /// </summary>
    private TrackedWindow? TryRestore(TrackedWindow tracked)
    {
        if (tracked.State is not (TrackedState.Restyled or TrackedState.RestorePending)) return null;
        // A window that has gone needs nothing back, and its handle may now be another window
        // (another program's, or a new one of the same game): Describe's lifetime is current.
        if (!IsStillTheSameWindow(tracked.Lifetime, windows.Describe(tracked.Window))) return null;
        if (!tracked.FrameRestored && windows.SetFrame(tracked.Window, tracked.Original) is ChangeResult.Ok or ChangeResult.WindowGone)
            tracked = tracked with { FrameRestored = true };
        if (tracked.MarkedFullscreen && windows.MarkFullscreen(tracked.Window, false) is ChangeResult.Ok or ChangeResult.WindowGone)
            tracked = tracked with { MarkedFullscreen = false };
        return tracked.FrameRestored && !tracked.MarkedFullscreen ? null : tracked;
    }

    /// <summary>Destroy the window's backdrop, if it has one, and keep the record without it.</summary>
    private TrackedWindow DropBackdrop(TrackedWindow tracked)
    {
        if (tracked.Backdrop is not { } backdrop) return tracked;
        windows.DestroyBackdrop(backdrop);
        var dropped = tracked with { Backdrop = null };
        if (_tracked.ContainsKey(tracked.Window)) _tracked[tracked.Window] = dropped;
        return dropped;
    }

    /// <summary>Stop tracking a window, destroying its backdrop. Nothing is put back.</summary>
    private void Forget(TrackedWindow tracked)
    {
        DropBackdrop(tracked);
        _tracked.Remove(tracked.Window);
    }

    /// <summary>Whether the window behind a handle is still the one with this lifetime.</summary>
    private static bool IsStillTheSameWindow(long lifetime, [NotNullWhen(true)] WindowInfo? now) => now is not null && now.Lifetime == lifetime;
}
