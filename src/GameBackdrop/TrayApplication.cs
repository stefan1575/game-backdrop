using GameBackdrop.Core.Controller;
using GameBackdrop.Core.Games;
using GameBackdrop.Core.Settings;
using GameBackdrop.Windows;

namespace GameBackdrop;

/// <summary>
/// The tray icon and its menu, and the wiring: settings → game list → controller over the Win32
/// window system. Every changed game window is put back when the app quits, however it quits.
/// </summary>
internal sealed class TrayApplication : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly Win32WindowSystem _windows;
    private readonly BackdropController _controller;
    private readonly GameListModel _games;
    private GameListForm? _list;
    private bool _failureReported;
    private bool _crashing;

    public TrayApplication(SettingsStore settings)
    {
        // The window system posts its events here; Application.Run would install it too, but
        // the controller starts (and may get events) before that.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        _games = GameListModel.Load(settings);
        _windows = new Win32WindowSystem();
        _controller = new BackdropController(_windows, _games.ToGameList());
        // Each saved change reaches the running controller at once: a game switched off or
        // removed is put back, one switched on or added is handled if it's running.
        _games.Changed += () => _controller.UpdateGames(_games.ToGameList());

        var menu = new ContextMenuStrip();
        var openList = new ToolStripMenuItem("Open list", null, (_, _) => ShowList());
        openList.Font = new Font(openList.Font, FontStyle.Bold);
        menu.Items.Add(openList);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => ExitThread());
        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Game Backdrop",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowList();
        };

        Application.ThreadException += (_, e) => Crash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash(e.ExceptionObject as Exception);
        Application.ApplicationExit += (_, _) => StopController();

        if (_games.LoadStatus == SettingsLoadStatus.Corrupt)
        {
            _icon.ShowBalloonTip(5000, "Game Backdrop",
                $"The settings file couldn't be read, so no games are listed. It is left as it is until the list " +
                $"is changed, and then kept beside it as a .corrupt copy: {settings.FilePath}",
                ToolTipIcon.Warning);
        }

        _controller.Start();
    }

    /// <summary>Open the game list window, or bring it to the front if it's open.</summary>
    public void ShowList()
    {
        if (_list is null || _list.IsDisposed)
        {
            _list = new GameListForm(_games);
            _list.FormClosed += (_, _) => _list = null;
        }
        _list.Show();
        if (_list.WindowState == FormWindowState.Minimized) _list.WindowState = FormWindowState.Normal;
        _list.Activate();
    }

    protected override void ExitThreadCore()
    {
        StopController();
        _icon.Visible = false;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _list?.Dispose();
            StopController();
            _icon.Dispose();
            _windows.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Stop the controller and put every game back. A restore that fails is retried a few times;
    /// any game still not put back is named to Stefan once, rather than forgotten silently. The
    /// controller is stopped (no longer handling window events) before any dialog opens, so the
    /// dialog's message loop can't restyle a game again.
    /// </summary>
    private void StopController()
    {
        var notRestored = StopAndRestore(retry: !_failureReported);
        if (notRestored.Count == 0 || _failureReported) return;

        _failureReported = true;
        MessageBox.Show($"Game Backdrop couldn't put these game windows back as they were:\n\n{Names(notRestored)}",
            "Game Backdrop", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private IReadOnlyList<TrackedWindow> StopAndRestore(bool retry)
    {
        var notRestored = _controller.Stop();
        for (var attempt = 0; retry && attempt < 3 && notRestored.Count > 0; attempt++)
        {
            Thread.Sleep(200);
            notRestored = _controller.Stop();
        }
        return notRestored;
    }

    private static string Names(IEnumerable<TrackedWindow> windows) =>
        string.Join("\n", windows.Select(t => t.Game.Name));

    private void Crash(Exception? exception)
    {
        // A second error while the dialog below is open: cleanup has already run.
        if (_crashing) Environment.Exit(1);
        _crashing = true;

        IReadOnlyList<TrackedWindow> notRestored = [];
        try
        {
            // Stop first: the controller stops handling window events, so nothing the dialog's
            // message loop dispatches can change a game after this cleanup.
            notRestored = StopAndRestore(retry: true);
        }
        catch
        {
            // Restoring is best effort once something has already gone wrong.
        }
        _failureReported = true;
        var outcome = notRestored.Count == 0
            ? "put its games back"
            : $"couldn't put these game windows back as they were:\n{Names(notRestored)}";
        MessageBox.Show($"Game Backdrop stopped after an error and {outcome}\n\n{exception}",
            "Game Backdrop", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Environment.Exit(1);
    }
}
