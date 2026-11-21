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
    /// <summary>
    /// The window fills its monitor: the game's own mode wins. Either nothing was changed, or the
    /// game was restyled and then switched to fullscreen itself, which released it: its backdrop
    /// is gone and its frame is the game's own (with what the app took off put back if it still
    /// had the app's strip, <see cref="TrackedWindow.Owed"/>), but
    /// <see cref="TrackedWindow.Original"/> is kept for when it goes windowed again.
    /// </summary>
    Fullscreen,
    /// <summary>
    /// The window was made borderless and centred (or half-changed by a failed attempt);
    /// <see cref="TrackedWindow.Original"/>'s place and frame put it back, over whatever else the
    /// game has set on its window since.
    /// </summary>
    Restyled,
    /// <summary>The port refused the change (an elevated game): nothing was changed, and it isn't retried.</summary>
    AccessDenied,
    /// <summary>
    /// A replaced splash window that couldn't be put back yet. It is no longer the game's window,
    /// but its original is kept so <see cref="BackdropController.RestoreAll"/> tries again. One
    /// replaced while released only gets back the style or mark it was owed, not its frame.
    /// </summary>
    RestorePending,
    /// <summary>
    /// The user turned the backdrop off with the hotkey (<see cref="BackdropController.ToggleBackdrop"/>):
    /// the window is back as it was, has no backdrop and isn't marked. The choice is the game
    /// run's (<see cref="GameRun"/>), not the window's: it stays through every event and every
    /// later window of the same run (a main window after its splash, even once the splash has
    /// closed), until toggled on or the game is relaunched. Nothing is left to put back.
    /// </summary>
    ToggledOff,
}

/// <summary>
/// One run of a game process: its id and when it started. Windows reuses process ids, so a
/// relaunch can get the id of the run before it; the start time tells them apart.
/// </summary>
public readonly record struct GameRun(int ProcessId, long Started)
{
    public static GameRun Of(WindowInfo info) => new(info.ProcessId, info.ProcessStarted);
}

/// <summary>A game window the controller is tracking: one per game process.</summary>
/// <param name="Lifetime">Which window behind <paramref name="Window"/> this is
/// (<see cref="WindowInfo.Lifetime"/>): a handle can be reused once its window is destroyed, even
/// by a new window of the same game, so the controller checks the lifetime before every call on
/// the handle. A window that changes its own frame keeps its lifetime, and its original.</param>
/// <param name="Original">The window's frame before the controller first touched it (until then,
/// the frame it had when first seen).</param>
/// <param name="HadCaption">Whether the window had a title bar when it was first seen.</param>
/// <param name="ClientSize">The window's client size as the controller last left it or found it: a
/// different one means the game changed its window (a resolution change).</param>
/// <param name="Monitor">The monitor the game is on, as the controller last placed it.</param>
/// <param name="Backdrop">The black window behind a restyled game; null once it is destroyed.</param>
/// <param name="MarkedFullscreen">The shell has been told the game is fullscreen (and not yet told
/// otherwise), so the taskbar drops behind it while it is in front.</param>
/// <param name="FrameRestored">No frame is owed any more: the original frame is back, or (a window
/// replaced while released) its frame is the game's own and was never to be put back. Only a
/// style or an unmark is left to retry.</param>
/// <param name="KnownStyle">The window's styles as the controller last left them or found them,
/// with <paramref name="ClientSize"/>: different ones mean the game changed its window.</param>
/// <param name="EverRestyled">The controller has changed the window's frame at least once, so
/// <paramref name="Original"/> is what it puts back.</param>
/// <param name="LastPlaced">When the controller last set the window's frame.</param>
/// <param name="Fights">How many times in a row the game changed its window again soon after the
/// controller placed it, and the controller placed it once more.</param>
/// <param name="Owed">The style bits the controller took off the window and hasn't put back:
/// exactly what <see cref="WindowStyle.Borderless"/> removed, and nothing else. A change the game
/// makes outside its frame (WS_EX_TOPMOST, WS_DISABLED under a modal dialog) leaves them owed; a
/// frame the game sets itself (<see cref="WindowStyle.HasSameFrameAs"/> against
/// <paramref name="KnownStyle"/>) is the game's own, and clears them. The game's own view of its
/// window is what it has now with these on it: whether it has a title bar, and what goes back on
/// it when it goes fullscreen while it still has the controller's strip, or on quit after that.</param>
/// <param name="LeftByStop">A restyled window, with its backdrop, that a <see cref="BackdropController.Stop"/>
/// couldn't finish putting back (a failed hand-over). Once the controller starts again it is
/// changed again from its first original (<c>Resume</c>), not followed as a restyled window: it
/// has no backdrop, and a restore can have stopped part-way.</param>
public sealed record TrackedWindow(
    WindowHandle Window,
    int ProcessId,
    string? ProcessPath,
    long ProcessStarted,
    GameEntry Game,
    TrackedState State,
    long Lifetime,
    WindowFrame Original,
    bool HadCaption,
    Size ClientSize,
    MonitorInfo Monitor,
    BackdropHandle? Backdrop = null,
    bool MarkedFullscreen = false,
    bool FrameRestored = false,
    WindowStyle KnownStyle = default,
    bool EverRestyled = false,
    DateTimeOffset LastPlaced = default,
    int Fights = 0,
    WindowStyle Owed = default,
    bool LeftByStop = false)
{
    public GameRun Run => new(ProcessId, ProcessStarted);

    /// <summary>
    /// The window has the controller's strip and place on it now. A restyled record whose frame
    /// is back (a restore that left only its unmark to retry) is being put back, not restyled:
    /// nothing may strip, centre, follow or mark it again.
    /// </summary>
    public bool IsRestyledNow => State == TrackedState.Restyled && !FrameRestored;
}

/// <summary>What <see cref="BackdropController.HandOver"/> did.</summary>
/// <param name="HandedOver">Every game was put back and the replacement started: this copy is to quit now.</param>
/// <param name="NotRestored">The windows that couldn't be put back, which stopped the hand-over.</param>
public sealed record HandOverResult(bool HandedOver, IReadOnlyList<TrackedWindow> NotRestored);

/// <summary>
/// Turns window events into actions on the window system: a state machine per tracked game
/// window. Single-threaded: call it, and raise the port's events, from one thread.
/// </summary>
/// <remarks>
/// The rule that keeps it off other programs' windows: a game can close during any port call,
/// and its handle can go to another window at once. So every call that changes a game's window
/// (<see cref="IWindowSystem.SetFrame"/>, <see cref="IWindowSystem.ShowBackdrop"/> with the game
/// as <c>behind</c>, <see cref="IWindowSystem.MarkFullscreen"/>, <see cref="IWindowSystem.Activate"/>)
/// comes right after a fresh <see cref="IWindowSystem.Describe"/> of that handle that confirms the
/// lifetime the controller means to act on, with no other port call in between.
/// <see cref="IWindowSystem.ClipCursor"/> changes no window, but its area comes from a Describe
/// of the game in front made just before it, for the same reason.
/// </remarks>
public sealed class BackdropController(IWindowSystem windows, GameList games, TimeProvider? time = null)
{
    /// <summary>
    /// A change the game makes to its window this soon after the controller placed it counts as
    /// the game answering the controller (<see cref="TrackedWindow.Fights"/>).
    /// </summary>
    public static readonly TimeSpan FightWindow = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How many times in a row the controller places a window the game keeps changing back. After
    /// that the game wins: its window stays as the game set it, until it changes again after a
    /// quiet <see cref="FightWindow"/>.
    /// </summary>
    public const int MaxFights = 3;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<WindowHandle, TrackedWindow> _tracked = [];
    private GameList _games = games;
    private bool _running;
    // Where the controller has the cursor kept, and for which game window; null while it leaves
    // the cursor alone.
    private CursorClip? _clip;

    /// <summary>The cursor is kept inside <paramref name="Area"/>, the client area of a game window.</summary>
    private readonly record struct CursorClip(WindowHandle Window, long Lifetime, Rect Area);
    // The game runs that have been in front, least recent first: the toggle acts on the most
    // recent one still with a window when no game is in front. By run, not window: a main window
    // replacing its splash takes the splash's place. A run whose windows are all gone keeps its
    // place, since it can open another window, until its process id turns up on another run.
    private readonly List<GameRun> _inFront = [];
    // The game runs the user toggled off: every window of theirs is left as it is. Kept by run,
    // past the window that was toggled, until toggled on or the process id turns up on another run.
    private readonly HashSet<GameRun> _toggledOff = [];
    // The game runs the administrator notice has been raised for.
    private readonly HashSet<GameRun> _noticed = [];

    public IReadOnlyCollection<TrackedWindow> TrackedWindows => _tracked.Values;

    /// <summary>
    /// A listed game's window refused to be restyled (access denied): the game runs as
    /// administrator and the app doesn't. Raised once per run of the game (<see cref="GameRun"/>),
    /// however many windows it opens, from inside the controller's own handling; the window is
    /// left as it is and not tried again. A handler must not call back into the controller.
    /// </summary>
    public event Action<GameEntry>? GameRunsAsAdministrator;

    /// <summary>
    /// Start watching, and handle the listed games that are already running. After a
    /// <see cref="Stop"/> that couldn't put a game back, starting again changes that game again,
    /// keeping its first original (<see cref="TrackedWindow.LeftByStop"/>).
    /// </summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        windows.WindowEvent += OnWindowEvent;
        windows.BackdropClicked += OnBackdropClicked;
        foreach (var window in windows.TopLevelWindows()) Consider(window);
        // A game already in front gets no Foreground event until the user leaves it and comes back.
        if (windows.ForegroundWindow() is { } foreground && _tracked.TryGetValue(foreground, out var inFront)) NoteInFront(inFront);
        UpdateCursorClip();
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
        ReleaseCursor();
        // A window restyled with its backdrop now is changed again if the controller starts
        // again before it is put back (a toggle that put back its frame but not its unmark has
        // no backdrop, and stays as the user left it).
        var restyled = _tracked.Values.Where(t => t.State == TrackedState.Restyled && (t.Backdrop is not null || t.LeftByStop))
            .Select(t => t.Window).ToHashSet();
        // Nothing moves a backdrop once events stop, so none stays, even behind a game that
        // couldn't be put back.
        foreach (var tracked in _tracked.Values.ToList()) DropBackdrop(tracked);
        var notRestored = RestoreAll();
        foreach (var left in notRestored.Where(t => t.State == TrackedState.Restyled && restyled.Contains(t.Window)))
            Save(left with { LeftByStop = true });
        return notRestored.Select(t => _tracked[t.Window]).ToList();
    }

    /// <summary>
    /// Hand the games over to a copy that takes over from this one (the restart as
    /// administrator): stop and put every game back, retrying a failed restore up to
    /// <paramref name="retries"/> times, and only then start the replacement. The replacement
    /// saves each window as it finds it as the original, so a game still changed when it starts
    /// would never be put back. If a game can't be put back, or the replacement doesn't start
    /// (<paramref name="startReplacement"/> returns false), nothing is handed over: this
    /// controller starts again, changing the games again, and the caller keeps running.
    /// </summary>
    public HandOverResult HandOver(Func<bool> startReplacement, int retries = 0, Action? beforeRetry = null)
    {
        var notRestored = Stop();
        for (var attempt = 0; attempt < retries && notRestored.Count > 0; attempt++)
        {
            beforeRetry?.Invoke();
            notRestored = Stop();
        }
        if (notRestored.Count == 0 && startReplacement()) return new HandOverResult(true, []);
        Start();
        return new HandOverResult(false, notRestored);
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

    /// <summary>
    /// Take a changed game list (from the list window). A tracked game that is off the list or
    /// switched off loses its backdrop and is put back as it was; one that can't be put back yet
    /// keeps its original, and is tried again by the next list change and on quit. A game still
    /// on the list keeps its backdrop and takes its entry's new settings (the mouse kept inside or
    /// freed at once). Then, while running, the listed games already open and not yet tracked are
    /// handled, as at <see cref="Start"/>.
    /// </summary>
    public void UpdateGames(GameList games)
    {
        _games = games;
        foreach (var tracked in _tracked.Values.ToList())
        {
            var entry = tracked.State == TrackedState.RestorePending ? null : games.FindEnabled(tracked.ProcessPath);
            if (entry is null) PutBack(tracked);
            else Save(tracked with { Game = entry });
        }
        if (_running)
        {
            // A game that kept its window through this change keeps that window: none of its other
            // windows takes over, as a later window would (a list change is no new window). A game
            // tracked anew here (added, or switched back on) chooses among its windows as at Start,
            // so its main window wins over a splash listed before it.
            var kept = _tracked.Values.Where(t => t.State != TrackedState.RestorePending).Select(t => t.Run).ToHashSet();
            foreach (var window in windows.TopLevelWindows())
            {
                // A window already tracked keeps what it has.
                if (!_tracked.ContainsKey(window)) Consider(window, keptRuns: kept);
            }
            // As at Start: a game tracked anew while in front gets no Foreground event until the user comes back to it.
            if (windows.ForegroundWindow() is { } foreground && _tracked.TryGetValue(foreground, out var inFront)) NoteInFront(inFront);
        }
        UpdateCursorClip();
    }

    /// <summary>
    /// Stop backdropping a window that left the list: destroy its backdrop and put it back. One
    /// that can't be put back yet stays as <see cref="TrackedState.RestorePending"/>, for a later
    /// try; a released one (fullscreen by its own choice) is only owed its style or unmark then,
    /// not its original frame.
    /// </summary>
    private void PutBack(TrackedWindow tracked)
    {
        var left = TryRestore(DropBackdrop(tracked));
        if (left is null) Forget(tracked);
        else Save(AsRestorePending(left));
    }

    /// <summary>
    /// A window that couldn't be put back yet, kept for a later try. Whether its frame is still
    /// owed comes from the state it had when it first left: a restyled one is owed its original
    /// frame until a try puts it back; a released one (fullscreen by its own choice) never was. A
    /// record already pending keeps what it owes, since its state no longer says which it was.
    /// </summary>
    private static TrackedWindow AsRestorePending(TrackedWindow left) => left with
    {
        State = TrackedState.RestorePending,
        LeftByStop = false,
        FrameRestored = left.FrameRestored || left.State is not (TrackedState.Restyled or TrackedState.RestorePending),
    };

    /// <summary>
    /// The hotkey: turn the backdrop off for the game in front (its window put back as it was, the
    /// black gone, the taskbar back), or on again if it is off. The game in front is the tracked
    /// game that is the foreground window, else the one most recently in front. A game in its own
    /// fullscreen mode, one the app may not touch, or no game at all: nothing changes.
    /// </summary>
    public void ToggleBackdrop()
    {
        if (!_running || !TryGetGameInFront(out var tracked, out var info)) return;
        switch (tracked.State)
        {
            case TrackedState.Restyled:
                ToggleOff(tracked);
                break;
            case TrackedState.ToggledOff:
                // A window that isn't on screen has nowhere to be placed; the next press tries again.
                if (!IsCandidate(info)) break;
                Track(info, tracked.Game);
                // On, unless nothing could be changed and the toggled-off record stays.
                if (!(_tracked.TryGetValue(tracked.Window, out var now) && now.State == TrackedState.ToggledOff))
                    _toggledOff.Remove(tracked.Run);
                break;
        }
    }

    private bool TryGetGameInFront([NotNullWhen(true)] out TrackedWindow? tracked, [NotNullWhen(true)] out WindowInfo? info)
    {
        // The foreground game wins even when it has nothing to toggle: the user is in it.
        if (windows.ForegroundWindow() is { } foreground && TryGetLive(foreground, out tracked, out info)
            && tracked.State != TrackedState.RestorePending)
            return true;
        for (var i = _inFront.Count - 1; i >= 0; i--)
        {
            var run = _inFront[i];
            var window = _tracked.Values.FirstOrDefault(t => t.Run == run && t.State != TrackedState.RestorePending);
            if (window is not null && TryGetLive(window.Window, out tracked, out info)) return true;
        }
        tracked = null;
        info = null;
        return false;
    }

    /// <summary>
    /// Put the game back and take its backdrop away. A restore that fails part-way keeps what it
    /// did, and the black while the frame isn't back, so the next press finishes it.
    /// </summary>
    private void ToggleOff(TrackedWindow tracked)
    {
        if (TryRestore(tracked) is { } left)
        {
            Save(left.FrameRestored ? DropBackdrop(left) : left);
            return;
        }
        // Put back, or gone (StillLive forgets a gone window, its backdrop with it).
        if (StillLive(tracked) is null) return;
        Save(DropBackdrop(tracked) with { State = TrackedState.ToggledOff, FrameRestored = false, MarkedFullscreen = false, Owed = default, LeftByStop = false });
        _toggledOff.Add(tracked.Run);
    }

    private void OnWindowEvent(WindowEvent e)
    {
        HandleWindowEvent(e);
        // Whatever changed which window is in front, or a game's state, place or size, can change
        // where the cursor belongs. Other windows' events (moves, mostly) can't.
        if (e.Kind == WindowEventKind.Foreground || _tracked.ContainsKey(e.Window) || _clip?.Window == e.Window)
            UpdateCursorClip();
    }

    private void HandleWindowEvent(WindowEvent e)
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
        if (e.Kind == WindowEventKind.Foreground) NoteInFront(tracked);
        if (tracked.LeftByStop)
        {
            // Not changed again yet (it was minimised, or the change failed): coming back on
            // screen is the next chance. Until then it isn't followed: it has no backdrop.
            if (e.Kind is WindowEventKind.Foreground or WindowEventKind.MinimizeEnd && !Resume(tracked, info))
                Consider(e.Window); // fully back and forgotten: decided on afresh
            return;
        }
        if (e.Kind == WindowEventKind.LocationChanged)
        {
            Reassess(tracked, info);
            return;
        }
        if (tracked.Backdrop is not { } backdrop) return;
        switch (e.Kind)
        {
            case WindowEventKind.Foreground:
                ShowBackdrop(tracked, info);
                MarkAgain(tracked);
                break;
            case WindowEventKind.MinimizeEnd:
            case WindowEventKind.Uncloaked:
                ShowBackdrop(tracked, info);
                break;
            case WindowEventKind.MinimizeStart:
            case WindowEventKind.Hidden:
            // Switching to another virtual desktop cloaks the game. The backdrop, a tool window,
            // is on every desktop, so it would black out the one switched to.
            case WindowEventKind.Cloaked:
                windows.HideBackdrop(backdrop);
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

    /// <param name="keptRuns">Game runs whose tracked window this window may not take over from;
    /// any other run's tracked window (a launcher or splash screen) it may, by
    /// <see cref="Replaces"/>. Null: none are kept.</param>
    private void Consider(WindowHandle handle, IReadOnlySet<GameRun>? keptRuns = null)
    {
        if (TryGetLive(handle, out var known, out var live))
        {
            if (known.LeftByStop)
            {
                if (Resume(known, live)) return;
                // It was back as it was, and now its unmark is done too: unchanged and untracked,
                // so it is decided on afresh below.
            }
            else
            {
                // The same window shown again: it keeps its backdrop, and its first original. A
                // game that hides its window while it switches mode is shown in its new one, which
                // its LocationChanged events, ignored while it was hidden, didn't get to follow.
                Reassess(known, live);
                // Reassess can have made port calls: the window is checked again before the next.
                if (!_tracked.TryGetValue(handle, out known) || StillLive(known) is not { } now) return;
                ShowBackdrop(known, now);
                MarkAgain(known);
                return;
            }
        }
        // Not tracked, or the tracked window is gone (and forgotten): whatever has the handle now is new.
        var info = windows.Describe(handle);
        if (info is null) return;
        var run = GameRun.Of(info);
        ForgetEndedRuns(run);

        if (!IsCandidate(info)) return;
        var game = _games.FindEnabled(info.ProcessPath);
        if (game is null) return;

        var current = _tracked.Values.FirstOrDefault(t => t.Run == run && t.State != TrackedState.RestorePending);
        if (current is not null && (keptRuns?.Contains(run) == true || !Replaces(info, current))) return;
        if (_toggledOff.Contains(run))
        {
            // Toggled off is the run's, not its splash's or any one window's: the new window is
            // left as it is, and stays off. A toggled-off record has nothing to put back.
            if (current is not null) _tracked.Remove(current.Window);
            Save(NewRecord(info, game) with { State = TrackedState.ToggledOff });
            return;
        }
        if (current is not null)
        {
            current = DropBackdrop(current);
            var left = TryRestore(current);
            if (left is null) _tracked.Remove(current.Window);
            // A retry puts back what this try owed: a released splash keeps the frame it set itself.
            else _tracked[current.Window] = AsRestorePending(left);
            // Putting the splash back took port calls: meanwhile the candidate can have changed,
            // or closed and its handle gone to another window. Decide on it as it is now.
            if (DescribeIfStill(info.Handle, info.Lifetime) is not { } now || !IsCandidate(now)) return;
            info = now;
        }

        Track(info, game);
    }

    /// <summary>
    /// Carry on with a window a <see cref="Stop"/> left part-way back (<see cref="TrackedWindow.LeftByStop"/>).
    /// The restore can have done anything from nothing to all but the unmark, and a half-done one
    /// (<see cref="ChangeResult.PartlyChanged"/>) can leave the title bar back on the borderless
    /// rectangle. So the window gets its whole changed frame again, worked out from its first
    /// original and its client size, and its backdrop; it keeps that original, and any unmark
    /// still owed. A window whose frame is back is unmarked again first: if that works nothing is
    /// owed, and this returns false for the caller to decide on it afresh. A window that is off
    /// screen, or now fills its screen of its own accord, or refuses the change, keeps its record
    /// as it is: Stop still finishes putting it back, and its next Shown, Foreground or MinimizeEnd
    /// tries again. Returns whether the window is still tracked by this record.
    /// <paramref name="live"/> is a Describe of it with no port call since.
    /// </summary>
    private bool Resume(TrackedWindow tracked, WindowInfo live)
    {
        if (tracked.FrameRestored)
        {
            var left = TryRestore(tracked);
            if (left is null)
            {
                Forget(tracked);
                return false;
            }
            tracked = Save(left);
            if (StillLive(tracked) is not { } now) return true;
            live = now;
            if (IsCandidate(live) && Classify(live) != WindowMode.Windowed) return true;
        }
        if (!IsCandidate(live)) return true;

        // The original's frame, stripped, over the styles the window has now (as a restore puts
        // it back): what the game set itself meanwhile stays.
        var unstripped = live.Style.WithFrameOf(tracked.Original.Style);
        var style = unstripped.Borderless();
        var outerSize = windows.OuterSizeFor(tracked.Window, style, tracked.ClientSize);
        if (StillLive(tracked) is not { } before) return true;
        var result = windows.SetFrame(tracked.Window, new WindowFrame(style, Placement.Centre(outerSize, before.Monitor.Bounds)));
        // Anything else changed nothing: the window stays as the restore left it.
        if (result is not (ChangeResult.Ok or ChangeResult.PartlyChanged)) return true;
        tracked = Save(tracked with
        {
            FrameRestored = false, LeftByStop = false, LastPlaced = _time.GetUtcNow(), Fights = 0,
            Owed = tracked.Owed.With(unstripped.TakenOffByBorderless()),
        });
        if (StillLive(tracked) is not { } after) return true;
        if (result == ChangeResult.Ok && after.Client.Size != tracked.ClientSize)
        {
            CorrectClientSize(tracked.Window, tracked.ClientSize, before.Monitor.Bounds, style, outerSize, after);
            if (StillLive(tracked) is not { } corrected) return true;
            after = corrected;
        }
        tracked = Save(Known(tracked, after));
        GiveBackdrop(tracked);
        return true;
    }

    /// <summary>
    /// Whether <paramref name="handle"/> is tracked and still has the tracked window behind it
    /// (<see cref="StillLive"/>).
    /// </summary>
    private bool TryGetLive(WindowHandle handle, [NotNullWhen(true)] out TrackedWindow? tracked, [NotNullWhen(true)] out WindowInfo? info)
    {
        info = null;
        if (!_tracked.TryGetValue(handle, out tracked)) return false;
        info = StillLive(tracked);
        if (info is not null) return true;
        tracked = null;
        return false;
    }

    /// <summary>
    /// The check before each call on a game's handle: the window as it is now if the handle still
    /// has the tracked window behind it, else null, with the record retired. Any port call can
    /// outlive the game (it closes, and its handle goes to a new window, while the call runs), so
    /// this runs again before every next call, not once per event. A retired record is forgotten:
    /// its backdrop destroyed, nothing put back or unmarked. The window it belonged to is gone,
    /// and the handle may be another window's now.
    /// </summary>
    private WindowInfo? StillLive(TrackedWindow tracked)
    {
        var info = windows.Describe(tracked.Window);
        if (IsStillTheSameWindow(tracked.Lifetime, info)) return info;
        Forget(tracked);
        return null;
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

    /// <summary>A record of the window as it is now, nothing changed yet.</summary>
    private static TrackedWindow NewRecord(WindowInfo info, GameEntry game) =>
        new(info.Handle, info.ProcessId, info.ProcessPath, info.ProcessStarted, game, TrackedState.Fullscreen,
            info.Lifetime, info.Frame, info.Style.HasCaption, info.Client.Size, info.Monitor, KnownStyle: info.Style);

    /// <summary>
    /// Decide on a game window and, if it is windowed, restyle, centre and back it. For a
    /// toggled-off game being toggled on, the window is taken as it is now (where the user left it
    /// is its new original); if nothing could be changed, its toggled-off record stays.
    /// </summary>
    private void Track(WindowInfo info, GameEntry game)
    {
        var tracked = NewRecord(info, game);
        if (Classify(info) != WindowMode.Windowed)
        {
            Save(tracked);
            return;
        }
        Restyle(tracked, info);
    }

    private static WindowMode Classify(WindowInfo info) =>
        WindowClassifier.Classify(info.Client, info.Style.HasCaption, info.Monitor.Bounds);

    /// <summary>
    /// Make a windowed game borderless, centre it on its monitor and give it its backdrop: a new
    /// window, or a tracked one that left fullscreen. <paramref name="info"/> is a Describe of it
    /// with no port call since. <paramref name="fights"/> counts this placement
    /// (<see cref="TrackedWindow.Fights"/>).
    /// </summary>
    private void Restyle(TrackedWindow tracked, WindowInfo info, int fights = 0)
    {
        // A window changed for the first time now: what it looks like now is what goes back. One
        // changed before (and released when it went fullscreen) keeps the frame from before the
        // first change.
        if (!tracked.EverRestyled) tracked = tracked with { Original = info.Frame };
        // Bits still owed from an earlier strip stay owed (they aren't on the window, and this
        // strip doesn't put them back); the ones this strip takes off are added below.
        tracked = Observe(tracked, info.Style);

        // The client area keeps its size; whatever non-client part survives the restyle (a
        // menu bar) is added on, and the whole window is centred.
        var style = info.Style.Borderless();
        var outerSize = windows.OuterSizeFor(info.Handle, style, info.Client.Size);
        // Nothing is changed yet, so a window gone during OuterSizeFor is simply not tracked.
        if (StillLive(tracked) is null) return;
        var result = windows.SetFrame(info.Handle, new WindowFrame(style, Placement.Centre(outerSize, info.Monitor.Bounds)));
        switch (result)
        {
            case ChangeResult.AccessDenied:
                // Not retried; quitting still puts back a style or a mark it owed from before.
                Save(tracked with { State = TrackedState.AccessDenied });
                if (_noticed.Add(tracked.Run)) GameRunsAsAdministrator?.Invoke(tracked.Game);
                return;
            case ChangeResult.WindowGone:
            case ChangeResult.Failed:
                // Nothing was changed; a later Shown event (or, for a tracked window, its next
                // change; for a toggled-off game, the next press) gets another try.
                return;
        }

        // Changed, or half-changed, so it has to be put back: the record is kept from here on,
        // and brought up to date after each step, so that if a later step throws, the crash
        // handler's Stop still puts the window back and destroys whatever backdrop was made. Both
        // Ok and PartlyChanged applied the styles, so what they took off is owed.
        tracked = Save(tracked with
        {
            State = TrackedState.Restyled, EverRestyled = true, LastPlaced = _time.GetUtcNow(), Fights = fights,
            Owed = tracked.Owed.With(info.Style.TakenOffByBorderless()),
        });
        // Each StillLive returning null means the window is gone: nothing to put back.
        if (StillLive(tracked) is not { } after) return;
        if (result == ChangeResult.Ok && after.Client.Size != info.Client.Size)
        {
            CorrectClientSize(info, style, outerSize, after); // same monitor: it centres on the one before
            if (StillLive(tracked) is not { } corrected) return;
            after = corrected;
        }
        // The record holds the window as the controller left it, so the events about this
        // change find nothing to do.
        tracked = Save(Known(tracked, after));
        GiveBackdrop(tracked);
    }

    /// <summary>The record with the window's frame as it is now: what the controller left, or found.</summary>
    private static TrackedWindow Known(TrackedWindow tracked, WindowInfo now) =>
        tracked with { ClientSize = now.Client.Size, KnownStyle = now.Style, Monitor = now.Monitor };

    /// <summary>
    /// The record with <see cref="TrackedWindow.Owed"/> up to date for the window's styles now,
    /// <paramref name="now"/>. A change that leaves the frame as the controller last left or found
    /// it (<see cref="TrackedWindow.KnownStyle"/>), such as WS_EX_TOPMOST or WS_DISABLED under a
    /// modal dialog, is the game's on top of the controller's strip: what was taken off is still
    /// owed. A new frame (its title bar back, a popup for fullscreen, its border put back itself)
    /// is one the game set as a whole: nothing of the controller's is left in it, so nothing is
    /// owed any more. Only the frame decides it: no snapshot of the game's style is kept to go stale.
    /// </summary>
    private static TrackedWindow Observe(TrackedWindow tracked, WindowStyle now) =>
        tracked.Owed.IsEmpty || now.HasSameFrameAs(tracked.KnownStyle) ? tracked : tracked with { Owed = default };

    /// <summary>The window's styles as the game sees them: what it has now, with what the controller owes it on.</summary>
    private static WindowStyle AsTheGameSeesIt(TrackedWindow tracked, WindowStyle now) => now.With(tracked.Owed);

    /// <summary>
    /// Give a restyled game its backdrop over its monitor, directly behind it, and tell the shell
    /// it is fullscreen so the taskbar drops behind it while it is in front.
    /// </summary>
    private void GiveBackdrop(TrackedWindow tracked)
    {
        tracked = Save(tracked with { Backdrop = windows.CreateBackdrop() });
        if (StillLive(tracked) is not { } info) return;
        ShowBackdrop(tracked, info);
        if (StillLive(tracked) is null) return;
        if (windows.MarkFullscreen(tracked.Window, true) == ChangeResult.Ok) Save(tracked with { MarkedFullscreen = true });
        // A game given its backdrop while it is in front keeps the mouse inside at once.
        UpdateCursorClip();
    }

    /// <summary>
    /// Keep the cursor inside the game in front, or free it. It is kept inside a game's client
    /// area while that game is the foreground window, on screen, restyled and backdropped, with
    /// <see cref="GameEntry.KeepMouseInside"/> on; it follows the client area when that moves or
    /// resizes. Anything else frees it: another app in front, the game minimised, hidden or cloaked, its
    /// backdrop gone, the game closed. Decided from the desktop as it is now, not from the event
    /// that led here, so a late event never puts the clip on a game the user has left.
    /// </summary>
    private void UpdateCursorClip()
    {
        var wanted = WantedCursorClip();
        if (wanted == _clip) return;
        if (wanted is not { } clip)
        {
            ReleaseCursor();
            return;
        }
        windows.ClipCursor(clip.Area);
        _clip = clip;
    }

    private CursorClip? WantedCursorClip()
    {
        if (!_running || windows.ForegroundWindow() is not { } foreground) return null;
        if (!TryGetLive(foreground, out var tracked, out var info)) return null;
        if (!tracked.IsRestyledNow || tracked.Backdrop is null || !tracked.Game.KeepMouseInside) return null;
        if (!OnScreen(info)) return null;
        return new CursorClip(tracked.Window, tracked.Lifetime, info.Client);
    }

    /// <summary>Free the cursor, if the controller has it kept anywhere.</summary>
    private void ReleaseCursor()
    {
        if (_clip is null) return;
        windows.ReleaseCursor();
        _clip = null;
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
    /// cheap and idempotent. A failed re-mark changes nothing: the next one tries again. It
    /// follows the backdrop's ShowBackdrop, so it checks the handle again first.
    /// </summary>
    private void MarkAgain(TrackedWindow tracked)
    {
        if (!tracked.IsRestyledNow || tracked.Backdrop is null) return;
        if (StillLive(tracked) is null) return;
        if (windows.MarkFullscreen(tracked.Window, true) == ChangeResult.Ok && !tracked.MarkedFullscreen)
            _tracked[tracked.Window] = tracked with { MarkedFullscreen = true };
    }

    /// <summary>
    /// Show the game's backdrop over its monitor, directly behind it, if the game is on screen. A
    /// cloaked game (on another virtual desktop) isn't: its Uncloaked event shows the backdrop.
    /// </summary>
    private void ShowBackdrop(TrackedWindow tracked, WindowInfo info)
    {
        if (tracked.Backdrop is { } backdrop && OnScreen(info))
            windows.ShowBackdrop(backdrop, tracked.Monitor.Bounds, tracked.Window);
    }

    /// <summary>Shown, not minimised and not cloaked: the game is drawn on the desktop the user is on.</summary>
    private static bool OnScreen(WindowInfo info) => info.IsVisible && !info.IsMinimized && !info.IsCloaked;

    /// <summary>
    /// The window moved or changed its size or style: follow what the game did. A tracked game
    /// that went windowed is restyled; a restyled one is centred again when the game changed its
    /// window, followed to another monitor, or released when it went fullscreen. Each of these
    /// that sets the window's frame counts towards <see cref="MaxFights"/>, so a game that answers
    /// every one of them with a change of its own wins after a few.
    /// </summary>
    private void Reassess(TrackedWindow tracked, WindowInfo info)
    {
        if (!info.IsVisible || info.IsMinimized) return;
        switch (tracked.State)
        {
            case TrackedState.Fullscreen:
                // A released game still owed bits: a frame it sets now is its own, and the next
                // change is judged against this one, so it can't later look like the app's strip.
                if (!tracked.Owed.IsEmpty) tracked = Save(Observe(tracked, info.Style) with { KnownStyle = info.Style });
                if (Classify(info) != WindowMode.Windowed) return;
                var fights = FightsIfPlaced(tracked);
                // A game that keeps going windowed in answer stays as it set itself, without a
                // backdrop, until it changes again after a quiet FightWindow.
                if (fights > MaxFights) return;
                Restyle(tracked, info, fights);
                return;
            // A put-back frame is the game's again (see IsRestyledNow): the next press or quit
            // finishes its restore, and following it would strip and centre it with no backdrop.
            case TrackedState.Restyled when tracked.IsRestyledNow:
                FollowRestyled(tracked, info);
                return;
        }
    }

    /// <summary>
    /// How many fights in a row a placement now makes: one more than before if the game changed
    /// its window within <see cref="FightWindow"/> of the controller's last placement, else one.
    /// </summary>
    private int FightsIfPlaced(TrackedWindow tracked) =>
        _time.GetUtcNow() - tracked.LastPlaced < FightWindow ? tracked.Fights + 1 : 1;

    /// <summary>
    /// A restyled game's window changed. With the styles and client size the controller left it
    /// with, it only follows the game to another monitor: a move within its monitor (the
    /// controller's own placement included) changes nothing, so the controller's own changes
    /// never set off another. Changed by the game, it is released if it now fills its monitor,
    /// else stripped and centred again at its new client size. Unless the game keeps undoing
    /// that: then the game wins, and only the backdrop follows it.
    /// </summary>
    private void FollowRestyled(TrackedWindow tracked, WindowInfo info)
    {
        var changedByGame = info.Style != tracked.KnownStyle || info.Client.Size != tracked.ClientSize;
        tracked = Observe(tracked, info.Style);
        if (!changedByGame && info.Monitor == tracked.Monitor) return;
        // Whether it fills its monitor goes by the style the game gave it: a game that only
        // resized still has its title bar as far as it knows, and a captioned game whose client
        // area is its monitor's size is windowed (stripped, it fills the monitor and keeps its
        // backdrop).
        if (changedByGame && WindowClassifier.Classify(info.Client, AsTheGameSeesIt(tracked, info.Style).HasCaption, info.Monitor.Bounds) == WindowMode.Fullscreen)
        {
            Release(tracked, info);
            return;
        }
        var fights = FightsIfPlaced(tracked);
        if (fights > MaxFights)
        {
            // The game keeps changing its window back: it wins, and is left as it set itself.
            Settle(tracked, info);
            return;
        }
        Place(tracked, info, fights);
    }

    /// <summary>
    /// Take the window as it is now into the record, with no change to it; the backdrop follows
    /// it to the monitor it is on. <paramref name="info"/> is a Describe of it with no port call
    /// since, so the backdrop can be shown behind it at once.
    /// </summary>
    private void Settle(TrackedWindow tracked, WindowInfo info)
    {
        var arrived = info.Monitor != tracked.Monitor;
        tracked = Save(Known(tracked, info));
        if (arrived) ShowBackdrop(tracked, info);
    }

    /// <summary>
    /// Strip the window again if the game gave it back a border, and centre it at its own size on
    /// the monitor it is on now; the backdrop follows it there. <paramref name="info"/> is a
    /// Describe of it with no port call since. <paramref name="fights"/> counts this placement.
    /// </summary>
    private void Place(TrackedWindow tracked, WindowInfo info, int fights)
    {
        var style = info.Style.Borderless();
        var outerSize = style == info.Style ? info.Outer.Size : windows.OuterSizeFor(info.Handle, style, info.Client.Size);
        var target = Placement.Centre(outerSize, info.Monitor.Bounds);
        if (style == info.Style && target == info.Outer)
        {
            // Already where it goes (a game that resized about its centre). No port call has run
            // since info's Describe.
            Settle(tracked, info);
            return;
        }
        if (StillLive(tracked) is null) return;
        var result = windows.SetFrame(info.Handle, new WindowFrame(style, target));
        // A border the game put back is owed once this takes it off again (Ok and PartlyChanged
        // both applied the styles).
        var takenOff = result is ChangeResult.Ok or ChangeResult.PartlyChanged ? info.Style.TakenOffByBorderless() : default;
        tracked = Save(tracked with { LastPlaced = _time.GetUtcNow(), Fights = fights, Owed = tracked.Owed.With(takenOff) });
        if (StillLive(tracked) is not { } after) return;
        if (result == ChangeResult.Ok && style != info.Style && after.Client.Size != info.Client.Size)
        {
            CorrectClientSize(info, style, outerSize, after);
            if (StillLive(tracked) is not { } corrected) return;
            after = corrected;
        }
        // Whatever happened, the record now holds the window as it is, so the events about this
        // change find nothing to do.
        Settle(tracked, after);
    }

    /// <summary>
    /// A restyled game switched itself to fullscreen (exclusive or borderless): the game's own
    /// mode wins. Its backdrop goes, and the fullscreen mark the app gave it is taken back. A game
    /// that set a style of its own keeps the frame it set: putting the saved title bar back would
    /// knock it out of fullscreen, and its next change would restyle it again. One that still has
    /// the controller's strip gets back what the strip took off, with its client area where it is,
    /// so it keeps filling its monitor. Its original is kept for when it goes windowed again.
    /// <paramref name="tracked"/> is <see cref="Observe">observed</see> for <paramref name="info"/>.
    /// </summary>
    private void Release(TrackedWindow tracked, WindowInfo info)
    {
        tracked = DropBackdrop(tracked);
        tracked = Save(Known(tracked, info) with { State = TrackedState.Fullscreen });
        var fights = FightsIfPlaced(tracked);
        // A game that keeps answering this wins too; quitting still puts back what is owed.
        if (!tracked.Owed.IsEmpty && fights <= MaxFights)
        {
            var (result, style) = PutOwedBack(tracked);
            if (result == ChangeResult.WindowGone)
            {
                StillLive(tracked); // forgets it: nothing left to do on it
                return;
            }
            tracked = Save(tracked with { LastPlaced = _time.GetUtcNow(), Fights = fights });
            // What was put back is the game's again, and the frame the app left is now this one:
            // a later frame of the game's own is judged against it, not against the strip.
            if (result == ChangeResult.Ok) tracked = Save(tracked with { Owed = default, KnownStyle = style });
        }
        if (!tracked.MarkedFullscreen || StillLive(tracked) is null) return;
        if (windows.MarkFullscreen(tracked.Window, false) is ChangeResult.Ok or ChangeResult.WindowGone)
            Save(tracked with { MarkedFullscreen = false });
    }

    /// <summary>
    /// Put back on a released window what the controller's strip took off it
    /// (<see cref="TrackedWindow.Owed"/>), over whatever else the game has set since, keeping its
    /// client area where it is (a frame that doesn't fit on the monitor runs off it, as a maximised
    /// window's does). Ok when nothing of the controller's is left on it, a game that has set a
    /// frame of its own since included; WindowGone when the window is gone. Returns the styles the
    /// window has then. Doesn't change the record.
    /// </summary>
    private (ChangeResult Result, WindowStyle Style) PutOwedBack(TrackedWindow tracked)
    {
        if (DescribeIfStill(tracked.Window, tracked.Lifetime) is not { } info) return (ChangeResult.WindowGone, default);
        var style = AsTheGameSeesIt(Observe(tracked, info.Style), info.Style);
        if (style == info.Style) return (ChangeResult.Ok, style);
        var client = info.Client;
        var outer = windows.OuterSizeFor(info.Handle, style, client.Size);
        // The frame is first taken to be the same all round; a menu bar makes it deeper at the
        // top, which the measured client area shows and a second move makes up.
        var frame = Rect.FromPosition(client.Left - (outer.Width - client.Width) / 2, client.Top - (outer.Height - client.Height) / 2, outer);
        if (DescribeIfStill(tracked.Window, tracked.Lifetime) is null) return (ChangeResult.WindowGone, default);
        var result = windows.SetFrame(info.Handle, new WindowFrame(style, frame));
        if (result != ChangeResult.Ok) return (result, default);
        if (DescribeIfStill(tracked.Window, tracked.Lifetime) is not { } after) return (ChangeResult.WindowGone, default);
        if (after.Client == client) return (ChangeResult.Ok, style);
        var corrected = new Rect(
            frame.Left + client.Left - after.Client.Left, frame.Top + client.Top - after.Client.Top,
            frame.Right + client.Right - after.Client.Right, frame.Bottom + client.Bottom - after.Client.Bottom);
        // The style is back either way: a failed correction only leaves the client area off by its frame.
        windows.SetFrame(info.Handle, new WindowFrame(style, corrected));
        return (ChangeResult.Ok, style);
    }

    /// <summary>
    /// The port's outer size can be short: a menu bar that wraps onto more than one row is taller
    /// than the port predicts. Grow the window by what the client area lost, measured, and centre
    /// it again. If that fails the window stays restyled and is still put back on quit.
    /// </summary>
    private void CorrectClientSize(WindowInfo before, WindowStyle style, Size outerSize, WindowInfo after) =>
        CorrectClientSize(before.Handle, before.Client.Size, before.Monitor.Bounds, style, outerSize, after);

    /// <inheritdoc cref="CorrectClientSize(WindowInfo, WindowStyle, Size, WindowInfo)"/>
    /// <param name="client">The client size the window is to keep.</param>
    /// <param name="monitorBounds">The monitor it is centred on.</param>
    private void CorrectClientSize(WindowHandle window, Size client, Rect monitorBounds, WindowStyle style, Size outerSize, WindowInfo after)
    {
        var corrected = new Size(
            outerSize.Width + client.Width - after.Client.Width,
            outerSize.Height + client.Height - after.Client.Height);
        windows.SetFrame(window, new WindowFrame(style, Placement.Centre(corrected, monitorBounds)));
    }

    /// <summary>
    /// Put a tracked window back as it was and unmark it. Returns null when nothing is left to do,
    /// else the record with what is done so far, for a later try that doesn't repeat it.
    /// </summary>
    private TrackedWindow? TryRestore(TrackedWindow tracked)
    {
        // A restyled window gets its original place and frame back. A released game (fullscreen by
        // its own choice) keeps the frame the game gave it, as does one denied a restyle; only what
        // the app took off and couldn't put back yet, or a mark it couldn't take back, is retried.
        var restoresFrame = (tracked.State is TrackedState.Restyled or TrackedState.RestorePending) && !tracked.FrameRestored;
        if (!restoresFrame && tracked.Owed.IsEmpty && !tracked.MarkedFullscreen) return null;
        // A window that has gone needs nothing back, and its handle may now be another window
        // (another program's, or a new one of the same game): Describe's lifetime is current.
        // The window can also go while SetFrame runs, so the check runs again before the unmark.
        // (Not StillLive: the callers decide what becomes of the record.)
        if (DescribeIfStill(tracked.Window, tracked.Lifetime) is not { } now) return null;
        if (restoresFrame)
        {
            // The original's frame over the styles the window has now: what the game set itself
            // meanwhile (WS_EX_TOPMOST, WS_DISABLED under a modal dialog) stays.
            var original = tracked.Original with { Style = now.Style.WithFrameOf(tracked.Original.Style) };
            var result = windows.SetFrame(tracked.Window, original);
            if (result == ChangeResult.WindowGone) return null; // gone, so nothing to unmark either
            // The original frame carries everything the app took off: nothing is owed apart from it.
            if (result == ChangeResult.Ok) tracked = tracked with { FrameRestored = true, Owed = default };
        }
        else if (!tracked.Owed.IsEmpty)
        {
            var (result, _) = PutOwedBack(tracked);
            if (result == ChangeResult.WindowGone) return null;
            if (result == ChangeResult.Ok) tracked = tracked with { Owed = default };
        }
        if (tracked.MarkedFullscreen)
        {
            if (!IsLive(tracked)) return null;
            if (windows.MarkFullscreen(tracked.Window, false) is ChangeResult.Ok or ChangeResult.WindowGone)
                tracked = tracked with { MarkedFullscreen = false };
        }
        return (!restoresFrame || tracked.FrameRestored) && tracked.Owed.IsEmpty && !tracked.MarkedFullscreen ? null : tracked;
    }

    /// <summary>Destroy the window's backdrop, if it has one, and keep the record without it.</summary>
    private TrackedWindow DropBackdrop(TrackedWindow tracked)
    {
        if (tracked.Backdrop is not { } backdrop) return tracked;
        // The cursor is only ever kept inside a game while its backdrop is on.
        if (_clip?.Window == tracked.Window) ReleaseCursor();
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

    /// <summary>
    /// A window of <paramref name="run"/> exists, so any earlier run with its process id has ended
    /// (two live processes never share an id): drop what was kept for it.
    /// </summary>
    private void ForgetEndedRuns(GameRun run)
    {
        _toggledOff.RemoveWhere(r => r.ProcessId == run.ProcessId && r != run);
        _noticed.RemoveWhere(r => r.ProcessId == run.ProcessId && r != run);
        _inFront.RemoveAll(r => r.ProcessId == run.ProcessId && r != run);
    }

    /// <summary>The game came to the foreground: it is now the most recent in <see cref="_inFront"/>.</summary>
    private void NoteInFront(TrackedWindow tracked)
    {
        // A replaced splash is no longer the game's window.
        if (tracked.State == TrackedState.RestorePending) return;
        _inFront.Remove(tracked.Run);
        _inFront.Add(tracked.Run);
    }

    /// <summary>Whether the tracked window is still the one behind its handle, by a fresh Describe.</summary>
    private bool IsLive(TrackedWindow tracked) => DescribeIfStill(tracked.Window, tracked.Lifetime) is not null;

    /// <summary>The window behind <paramref name="handle"/> as it is now, if it is still the one with this lifetime; else null.</summary>
    private WindowInfo? DescribeIfStill(WindowHandle handle, long lifetime)
    {
        var now = windows.Describe(handle);
        return IsStillTheSameWindow(lifetime, now) ? now : null;
    }

    /// <summary>Whether the window behind a handle is still the one with this lifetime.</summary>
    private static bool IsStillTheSameWindow(long lifetime, [NotNullWhen(true)] WindowInfo? now) => now is not null && now.Lifetime == lifetime;
}
