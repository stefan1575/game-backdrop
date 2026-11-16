using System.ComponentModel;
using System.Diagnostics;
using GameBackdrop.Core.Controller;
using GameBackdrop.Core.Games;
using GameBackdrop.Core.Settings;
using GameBackdrop.Windows;

namespace GameBackdrop;

/// <summary>
/// The tray icon and its menu, and the wiring: settings → game list → controller over the Win32
/// window system. Every changed game window is put back when the app quits, however it quits.
/// A game running as administrator gets a notice that offers to restart the app as administrator.
/// </summary>
internal sealed class TrayApplication : ApplicationContext
{
    private const int ErrorCancelled = 1223; // ShellExecute: the UAC prompt was declined

    private static readonly bool Elevated = Environment.IsPrivilegedProcess;

    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _restartItem;
    private readonly Win32WindowSystem _windows;
    private readonly BackdropController _controller;
    private readonly string? _settingsArgument;
    private bool _failureReported;
    private bool _crashing;
    private bool _balloonOffersRestart;
    private bool _restarting;

    /// <param name="settingsArgument">The <c>--settings</c> path the app was started with, if
    /// any, so a restart as administrator uses the same file.</param>
    public TrayApplication(SettingsStore settings, string? settingsArgument)
    {
        _settingsArgument = settingsArgument;
        // The window system posts its events here; Application.Run would install it too, but
        // the controller starts (and may get events) before that.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        var loaded = settings.Load();
        _windows = new Win32WindowSystem();
        _controller = new BackdropController(_windows, new GameList(loaded.Settings.Games));
        _controller.GameRunsAsAdministrator += ShowAdministratorNotice;

        // Shown once a game running as administrator has been seen, for when its notice is gone.
        _restartItem = new ToolStripMenuItem("Restart as administrator", null, (_, _) => RestartAsAdministrator()) { Visible = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add(_restartItem);
        menu.Items.Add("Quit", null, (_, _) => ExitThread());
        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = Elevated ? "Game Backdrop (administrator)" : "Game Backdrop",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.BalloonTipClicked += (_, _) =>
        {
            if (_balloonOffersRestart) RestartAsAdministrator();
        };

        Application.ThreadException += (_, e) => Crash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash(e.ExceptionObject as Exception);
        Application.ApplicationExit += (_, _) => StopController();

        if (loaded.Status == SettingsLoadStatus.Corrupt)
        {
            ShowBalloon(5000, "Game Backdrop",
                $"The settings file couldn't be read, so no games are listed. It is left as it is: {settings.FilePath}",
                ToolTipIcon.Warning);
        }

        _controller.Start();
    }

    /// <summary>
    /// A listed game runs as administrator, so its window refused to change. Raised once per
    /// launch of the game. Unless the app already runs as administrator, clicking the notice (or
    /// the tray menu item) restarts it as administrator.
    /// </summary>
    private void ShowAdministratorNotice(GameEntry game)
    {
        var name = game.DisplayName ?? Path.GetFileNameWithoutExtension(game.ExePath);
        if (Elevated)
        {
            ShowBalloon(5000, $"{name} can't be changed",
                "Windows refused to let Game Backdrop change the game's window, even running as administrator.",
                ToolTipIcon.Warning);
            return;
        }
        _restartItem.Visible = true;
        ShowBalloon(10000, $"{name} runs as administrator",
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
    /// Start an elevated copy (the UAC prompt) and quit, putting every game back. The new copy is
    /// told to take over: it waits for this one to release the single-instance guard, which this
    /// one does only after its games are back, so the two never both act on a window. A declined
    /// prompt leaves this copy running as it was.
    /// </summary>
    private void RestartAsAdministrator()
    {
        if (_restarting || Elevated) return;
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
            return;
        }
        catch (Win32Exception e)
        {
            ShowBalloon(5000, "Game Backdrop", $"Couldn't restart as administrator: {e.Message}", ToolTipIcon.Warning);
            return;
        }
        _restarting = true;
        ExitThread();
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
        string.Join("\n", windows.Select(t => t.Game.DisplayName ?? t.Game.ExePath));

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
