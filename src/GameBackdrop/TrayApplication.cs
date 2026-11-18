using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using GameBackdrop.Core.Controller;
using GameBackdrop.Core.Games;
using GameBackdrop.Core.Settings;
using GameBackdrop.Core.Startup;
using GameBackdrop.Windows;

namespace GameBackdrop;

/// <summary>
/// The tray icon and its menu, and the wiring: settings → game list → controller over the Win32
/// window system, and the hotkey that toggles the backdrop for the game in front. Every changed
/// game window is put back when the app quits, however it quits. A game running as administrator
/// gets a notice that offers to restart the app as administrator.
/// </summary>
internal sealed class TrayApplication : ApplicationContext
{
    private const int ErrorCancelled = 1223; // ShellExecute: the UAC prompt was declined
    private const int RestoreRetries = 3;
    private const int RestoreRetryDelay = 200; // ms

    private static readonly bool Elevated = Environment.IsPrivilegedProcess;

    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _restartItem;
    private readonly Win32WindowSystem _windows;
    private readonly BackdropController _controller;
    private readonly StartAtLogon _startAtLogon;
    private readonly GameListModel _games;
    private GameListForm? _list;
    private readonly GlobalHotkey? _hotkey;
    private bool _failureReported;
    private readonly string? _settingsArgument;
    private bool _crashing;
    private bool _balloonOffersRestart;
    private bool _restarting;

    /// <param name="settingsArgument">The <c>--settings</c> path the app was started with, if
    /// any, so a restart as administrator uses the same file.</param>
    public TrayApplication(SettingsStore settings, StartAtLogon startAtLogon, string? settingsArgument)
    {
        _settingsArgument = settingsArgument;
        // The window system posts its events here; Application.Run would install it too, but
        // the controller starts (and may get events) before that.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        _games = GameListModel.Load(settings);
        _windows = new Win32WindowSystem();
        _controller = new BackdropController(_windows, _games.ToGameList());
        // Each saved change reaches the running controller at once: a game switched off or
        // removed is put back, one switched on or added is handled if it's running.
        _games.Changed += () => _controller.UpdateGames(_games.ToGameList());
        _controller.GameRunsAsAdministrator += ShowAdministratorNotice;

        _startAtLogon = startAtLogon;
        var startAtLogonItem = new ToolStripMenuItem("Start at logon");
        startAtLogonItem.Click += (_, _) => ToggleStartAtLogon();

        // Shown once a game running as administrator has been seen, for when its notice is gone.
        _restartItem = new ToolStripMenuItem("Restart as administrator", null, (_, _) => RestartAsAdministrator()) { Visible = false };
        var menu = new ContextMenuStrip();
        var openList = new ToolStripMenuItem("Open list", null, (_, _) => ShowList());
        openList.Font = new Font(openList.Font, FontStyle.Bold);
        menu.Items.Add(openList);
        menu.Items.Add(startAtLogonItem);
        menu.Items.Add(_restartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => ExitThread());
        // Read fresh each time: the shortcut or the settings file may have changed outside the app.
        menu.Opening += (_, _) =>
        {
            startAtLogonItem.Checked = _startAtLogon.IsOn;
            startAtLogonItem.Enabled = _startAtLogon.CanRecord;
        };
        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = Elevated ? "Game Backdrop (administrator)" : "Game Backdrop",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowList();
        };
        _icon.BalloonTipClicked += (_, _) =>
        {
            if (_balloonOffersRestart) RestartAsAdministrator();
        };

        Application.ThreadException += (_, e) => Crash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash(e.ExceptionObject as Exception);
        Application.ApplicationExit += (_, _) => StopController();

        // One balloon at a time shows, so every startup notice goes in the same one (Windows cuts
        // its text at 255 characters; both together only come with a corrupt file).
        var notices = new List<string>();
        if (_games.LoadStatus == SettingsLoadStatus.Corrupt)
            notices.Add($"The settings file couldn't be read, so no games are listed. It is left as it is until the list " +
                $"is changed, and then kept beside it as a .corrupt copy: {settings.FilePath}");

        try
        {
            _startAtLogon.ApplyAtStart();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException)
        {
            notices.Add($"Couldn't set up starting at logon: {e.Message}");
        }

        _controller.Start();

        var hotkey = Hotkey.FromSettings(_games.Hotkey, out var understood);
        if (!understood)
            notices.Add($"The hotkey \"{_games.Hotkey}\" in the settings isn't one the app can read, so {hotkey} is used.");
        // Another app holding the keys means no toggle, and a notice: never a crash.
        _hotkey = GlobalHotkey.TryRegister(hotkey, _controller.ToggleBackdrop);
        if (_hotkey is null)
            notices.Add($"{hotkey} is taken by another app, so it won't toggle the backdrop. Set another \"hotkey\" in the settings file.");

        if (notices.Count > 0)
            ShowBalloon(5000, "Game Backdrop", string.Join("\n\n", notices), ToolTipIcon.Warning);
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

    private void ToggleStartAtLogon()
    {
        try
        {
            _startAtLogon.Set(!_startAtLogon.IsOn);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            ShowBalloon(5000, "Game Backdrop", $"Couldn't change starting at logon: {e.Message}", ToolTipIcon.Warning);
        }
    }

    /// <summary>
    /// A listed game runs as administrator, so its window refused to change. Raised once per run
    /// of the game. Unless the app already runs as administrator, clicking the notice (or the tray
    /// menu item) restarts it as administrator.
    /// </summary>
    private void ShowAdministratorNotice(GameEntry game)
    {
        if (Elevated)
        {
            ShowBalloon(5000, $"{game.Name} can't be changed",
                "Windows refused to let Game Backdrop change the game's window, even running as administrator.",
                ToolTipIcon.Warning);
            return;
        }
        _restartItem.Visible = true;
        ShowBalloon(10000, $"{game.Name} runs as administrator",
            "Game Backdrop can't change the game's window unless it runs as administrator too. Click here to restart Game Backdrop as administrator.",
            ToolTipIcon.Info, offersRestart: true);
    }

    /// <summary>Show a tray notice. A click on it restarts the app as administrator only if this one offered that.</summary>
    private void ShowBalloon(int timeout, string title, string text, ToolTipIcon icon, bool offersRestart = false)
    {
        _balloonOffersRestart = offersRestart;
        _icon.ShowBalloonTip(timeout, title, text, icon);
    }

    /// <summary>
    /// Put every game back, then start an elevated copy (the UAC prompt) and quit. The new copy
    /// saves each window as it finds it as the original, so it is started only once every game is
    /// back; it is told to take over, and waits for this one to release the single-instance
    /// guard, so the two never both act on a window. A game that can't be put back, or a declined
    /// prompt, leaves this copy running, with its games changed again.
    /// </summary>
    private void RestartAsAdministrator()
    {
        if (_restarting || Elevated) return;
        _restarting = true;
        var result = _controller.HandOver(StartElevatedCopy, RestoreRetries, () => Thread.Sleep(RestoreRetryDelay));
        if (result.HandedOver)
        {
            ExitThread();
            return;
        }
        _restarting = false;
        if (result.NotRestored.Count > 0)
        {
            ShowBalloon(10000, "Couldn't restart as administrator",
                $"These game windows couldn't be put back first, so Game Backdrop carries on as it was:\n{Names(result.NotRestored)}",
                ToolTipIcon.Warning);
        }
    }

    /// <summary>Start the elevated copy that takes over. False if it didn't start (the UAC prompt was declined, or it failed).</summary>
    private bool StartElevatedCopy()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        start.ArgumentList.Add(Program.TakeoverFlag);
        if (_settingsArgument is not null)
        {
            start.ArgumentList.Add("--settings");
            start.ArgumentList.Add(_settingsArgument);
        }
        try
        {
            using var elevated = Process.Start(start);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
        catch (Win32Exception e)
        {
            ShowBalloon(5000, "Game Backdrop", $"Couldn't restart as administrator: {e.Message}", ToolTipIcon.Warning);
            return false;
        }
        return true;
    }

    protected override void ExitThreadCore()
    {
        _hotkey?.Dispose();
        StopController();
        _icon.Visible = false;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _list?.Dispose();
            _hotkey?.Dispose();
            StopController();
            _icon.Dispose();
            _windows.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Stop the controller and put every game back. A restore that fails is retried a few times;
    /// any game still not put back is named to the user once, rather than forgotten silently. The
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
        for (var attempt = 0; retry && attempt < RestoreRetries && notRestored.Count > 0; attempt++)
        {
            Thread.Sleep(RestoreRetryDelay);
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
