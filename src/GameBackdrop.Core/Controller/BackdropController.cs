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
/// <param name="Original">The window's frame before the controller first touched it.</param>
/// <param name="HadCaption">Whether the window had a title bar when it was first seen.</param>
public sealed record TrackedWindow(
    WindowHandle Window,
    int ProcessId,
    GameEntry Game,
    TrackedState State,
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
                // A game that has exited needs no restore.
                _tracked.Remove(e.Window);
                break;
        }
    }

    private void Consider(WindowHandle handle)
    {
        if (_tracked.ContainsKey(handle)) return;
        var info = windows.Describe(handle);
        if (info is null || !IsCandidate(info)) return;
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
            var target = new WindowFrame(info.Style.Borderless(), Placement.Centre(info.Client.Size, info.Monitor.Bounds));
            switch (windows.SetFrame(info.Handle, target))
            {
                case ChangeResult.Ok:
                case ChangeResult.PartlyChanged:
                    // A half-changed window still has to be put back.
                    state = TrackedState.Restyled;
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

        _tracked[info.Handle] = new TrackedWindow(info.Handle, info.ProcessId, game, state, info.Frame, info.Style.HasCaption, info.Client.Size);
    }

    /// <summary>Put a tracked window back as it was. False when that failed and should be tried again.</summary>
    private bool TryRestore(TrackedWindow tracked)
    {
        if (tracked.State is not (TrackedState.Restyled or TrackedState.RestorePending)) return true;
        return windows.SetFrame(tracked.Window, tracked.Original) is ChangeResult.Ok or ChangeResult.WindowGone;
    }
}
