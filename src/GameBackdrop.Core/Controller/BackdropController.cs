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
/// <param name="ProcessId">With <paramref name="ProcessPath"/> and <paramref name="Style"/>, how
/// the window is told apart from a later one: a window handle can be reused once its window is
/// destroyed, even by a new window of the same game, so the controller checks all three before it
/// changes the window behind a handle.</param>
/// <param name="Style">The styles the window has while it is this tracked window: the ones the
/// controller gave it, or its own when nothing was changed. A new top-level window always has a
/// title bar or WS_POPUP of its own, so it doesn't have the borderless styles the controller set.</param>
/// <param name="Original">The window's frame before the controller first touched it.</param>
/// <param name="HadCaption">Whether the window had a title bar when it was first seen.</param>
public sealed record TrackedWindow(
    WindowHandle Window,
    int ProcessId,
    string? ProcessPath,
    GameEntry Game,
    TrackedState State,
    WindowStyle Style,
    WindowFrame Original,
    bool HadCaption,
    Size ClientSize);

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
        foreach (var window in windows.TopLevelWindows()) Consider(window);
    }

    /// <summary>
    /// Stop watching and put every changed game window back as it was. Returns the windows that
    /// couldn't be put back; they keep their originals, and calling Stop again retries them.
    /// </summary>
    public IReadOnlyList<TrackedWindow> Stop()
    {
        if (_running)
        {
            _running = false;
            windows.WindowEvent -= OnWindowEvent;
        }
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
            if (TryRestore(tracked)) _tracked.Remove(tracked.Window);
            else notRestored.Add(_tracked[tracked.Window] = AfterFailedRestore(tracked));
        }
        return notRestored;
    }

    private void OnWindowEvent(WindowEvent e)
    {
        switch (e.Kind)
        {
            case WindowEventKind.Shown:
                Consider(e.Window);
                break;
            case WindowEventKind.Destroyed:
                // A game that has exited needs no restore. Destroy events arrive queued, so the
                // handle may already belong to a newer window: keep the record only if the window
                // behind the handle is still the tracked one (a newer one tracked since, say).
                if (_tracked.TryGetValue(e.Window, out var tracked) && !IsStillTheSameWindow(tracked, windows.Describe(e.Window)))
                    _tracked.Remove(e.Window);
                break;
        }
    }

    private void Consider(WindowHandle handle)
    {
        var info = windows.Describe(handle);
        if (info is null) return;
        if (_tracked.TryGetValue(handle, out var known))
        {
            if (IsStillTheSameWindow(known, info)) return;
            // The tracked window is gone and another window has its handle: forget the old one.
            _tracked.Remove(handle);
        }
        if (!IsCandidate(info)) return;
        var game = games.FindEnabled(info.ProcessPath);
        if (game is null) return;

        var current = _tracked.Values.FirstOrDefault(t => t.ProcessId == info.ProcessId && t.State != TrackedState.RestorePending);
        if (current is not null)
        {
            if (!Replaces(info, current)) return;
            if (TryRestore(current)) _tracked.Remove(current.Window);
            else _tracked[current.Window] = AfterFailedRestore(current) with { State = TrackedState.RestorePending };
        }

        Track(info, game);
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
        var state = TrackedState.Fullscreen;
        var trackedStyle = info.Style;
        if (WindowClassifier.Classify(info.Client, info.Style.HasCaption, info.Monitor.Bounds) == WindowMode.Windowed)
        {
            // The client area keeps its size; whatever non-client part survives the restyle (a
            // menu bar) is added on, and the whole window is centred.
            var style = info.Style.Borderless();
            var outerSize = windows.OuterSizeFor(info.Handle, style, info.Client.Size);
            var result = windows.SetFrame(info.Handle, new WindowFrame(style, Placement.Centre(outerSize, info.Monitor.Bounds)));
            switch (result)
            {
                case ChangeResult.Ok:
                case ChangeResult.PartlyChanged:
                    // A half-changed window still has to be put back.
                    state = TrackedState.Restyled;
                    var after = windows.Describe(info.Handle);
                    if (result == ChangeResult.Ok && after is not null && after.Client.Size != info.Client.Size)
                        after = CorrectClientSize(info, style, outerSize, after);
                    // Whatever styles the window really has now are the ones that mark it as ours.
                    trackedStyle = after?.Style ?? style;
                    break;
                case ChangeResult.AccessDenied:
                    state = TrackedState.AccessDenied;
                    break;
                case ChangeResult.WindowGone:
                case ChangeResult.Failed:
                    // Nothing was changed; a later Shown event gets another try.
                    return;
            }
        }

        _tracked[info.Handle] = new TrackedWindow(info.Handle, info.ProcessId, info.ProcessPath, game, state, trackedStyle, info.Frame, info.Style.HasCaption, info.Client.Size);
    }

    /// <summary>
    /// The port's outer size can be short: a menu bar that wraps onto more than one row is taller
    /// than the port predicts. Grow the window by what the client area lost, measured, and centre
    /// it again. Returns the window as it is afterwards.
    /// </summary>
    private WindowInfo? CorrectClientSize(WindowInfo before, WindowStyle style, Size outerSize, WindowInfo after)
    {
        var corrected = new Size(
            outerSize.Width + before.Client.Width - after.Client.Width,
            outerSize.Height + before.Client.Height - after.Client.Height);
        windows.SetFrame(before.Handle, new WindowFrame(style, Placement.Centre(corrected, before.Monitor.Bounds)));
        return windows.Describe(before.Handle);
    }

    /// <summary>Put a tracked window back as it was. False when that failed and should be tried again.</summary>
    private bool TryRestore(TrackedWindow tracked)
    {
        if (tracked.State is not (TrackedState.Restyled or TrackedState.RestorePending)) return true;
        // A window that has gone needs nothing back, and its handle may now be another program's window.
        if (!IsStillTheSameWindow(tracked, windows.Describe(tracked.Window))) return true;
        return windows.SetFrame(tracked.Window, tracked.Original) is ChangeResult.Ok or ChangeResult.WindowGone;
    }

    /// <summary>
    /// A restore that failed part-way may have changed the window's styles: those are now the ones
    /// that mark it as ours, so the retry still recognises it.
    /// </summary>
    private TrackedWindow AfterFailedRestore(TrackedWindow tracked) =>
        windows.Describe(tracked.Window) is { } now && now.ProcessId == tracked.ProcessId
            ? tracked with { Style = now.Style }
            : tracked;

    /// <summary>
    /// Whether the window behind a tracked handle is still the one that was tracked: the same
    /// process, and the frame styles it had when it was tracked (state bits such as minimised
    /// don't count). A window whose frame styles changed since is either a new window with the
    /// old handle or a game that restyled itself; neither gets the saved frame put back.
    /// </summary>
    private static bool IsStillTheSameWindow(TrackedWindow tracked, WindowInfo? now) =>
        now is not null && now.ProcessId == tracked.ProcessId
        && string.Equals(now.ProcessPath, tracked.ProcessPath, StringComparison.OrdinalIgnoreCase)
        && now.Style.FrameBits == tracked.Style.FrameBits;
}
