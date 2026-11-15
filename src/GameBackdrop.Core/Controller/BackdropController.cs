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
/// by a new window of the same game, so the controller checks the lifetime before it changes the
/// window behind a handle. A window that changes its own frame keeps its lifetime, and its
/// original.</param>
/// <param name="Original">The window's frame before the controller first touched it.</param>
/// <param name="HadCaption">Whether the window had a title bar when it was first seen.</param>
public sealed record TrackedWindow(
    WindowHandle Window,
    int ProcessId,
    string? ProcessPath,
    GameEntry Game,
    TrackedState State,
    long Lifetime,
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
            else notRestored.Add(tracked);
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
                // handle may already belong to a newer window: forget only the window that ended.
                if (_tracked.TryGetValue(e.Window, out var tracked) && tracked.Lifetime == e.Lifetime)
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
            if (IsStillTheSameWindow(known.Lifetime, info)) return;
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
            else _tracked[current.Window] = current with { State = TrackedState.RestorePending };
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
                    if (result == ChangeResult.Ok && after is not null && IsStillTheSameWindow(info.Lifetime, after)
                        && after.Client.Size != info.Client.Size)
                        CorrectClientSize(info, style, outerSize, after);
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

        _tracked[info.Handle] = new TrackedWindow(info.Handle, info.ProcessId, info.ProcessPath, game, state, info.Lifetime, info.Frame, info.Style.HasCaption, info.Client.Size);
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

    /// <summary>Put a tracked window back as it was. False when that failed and should be tried again.</summary>
    private bool TryRestore(TrackedWindow tracked)
    {
        if (tracked.State is not (TrackedState.Restyled or TrackedState.RestorePending)) return true;
        // A window that has gone needs nothing back, and its handle may now be another window
        // (another program's, or a new one of the same game): Describe's lifetime is current.
        if (!IsStillTheSameWindow(tracked.Lifetime, windows.Describe(tracked.Window))) return true;
        return windows.SetFrame(tracked.Window, tracked.Original) is ChangeResult.Ok or ChangeResult.WindowGone;
    }

    /// <summary>Whether the window behind a handle is still the one with this lifetime.</summary>
    private static bool IsStillTheSameWindow(long lifetime, WindowInfo? now) => now is not null && now.Lifetime == lifetime;
}
