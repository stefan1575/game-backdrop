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
    private const int RestoreRetries = 3;
    private const int RestoreRetryDelay = 200; // ms

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
        for (var attempt = 0; retry && attempt < RestoreRetries && notRestored.Count > 0; attempt++)
        {
            Thread.Sleep(RestoreRetryDelay);
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
