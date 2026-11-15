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
    private bool _failureReported;

    public TrayApplication(SettingsStore settings)
    {
        // The window system posts its events here; Application.Run would install it too, but
        // the controller starts (and may get events) before that.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        var loaded = settings.Load();
        _windows = new Win32WindowSystem();
        _controller = new BackdropController(_windows, new GameList(loaded.Settings.Games));

        var menu = new ContextMenuStrip();
        menu.Items.Add("Quit", null, (_, _) => ExitThread());
        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Game Backdrop",
            ContextMenuStrip = menu,
            Visible = true,
        };

        Application.ThreadException += (_, e) => Crash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash(e.ExceptionObject as Exception);
        Application.ApplicationExit += (_, _) => StopController();

        if (loaded.Status == SettingsLoadStatus.Corrupt)
        {
            _icon.ShowBalloonTip(5000, "Game Backdrop",
                $"The settings file couldn't be read, so no games are listed. It is left as it is: {settings.FilePath}",
                ToolTipIcon.Warning);
        }

        _controller.Start();
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
    /// any game still not put back is named to Stefan once, rather than forgotten silently.
    /// </summary>
    private void StopController()
    {
        var notRestored = _controller.Stop();
        if (_failureReported) return;
        for (var attempt = 0; attempt < 3 && notRestored.Count > 0; attempt++)
        {
            Thread.Sleep(200);
            notRestored = _controller.Stop();
        }
        if (notRestored.Count == 0) return;

        _failureReported = true;
        var names = string.Join("\n", notRestored.Select(t => t.Game.DisplayName ?? t.Game.ExePath));
        MessageBox.Show($"Game Backdrop couldn't put these game windows back as they were:\n\n{names}",
            "Game Backdrop", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void Crash(Exception? exception)
    {
        try
        {
            _controller.RestoreAll();
        }
        catch
        {
            // Restoring is best effort once something has already gone wrong.
        }
        MessageBox.Show($"Game Backdrop stopped after an error and put its games back.\n\n{exception}",
            "Game Backdrop", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Environment.Exit(1);
    }
}
