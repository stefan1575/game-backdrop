using System.Runtime.InteropServices;
using GameBackdrop.Core.Controller;
using GameBackdrop.Core.Games;
using GameBackdrop.Core.Settings;
using GameBackdrop.Core.Startup;
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
    private readonly StartAtLogon _startAtLogon;
    private bool _failureReported;
    private bool _crashing;

    public TrayApplication(SettingsStore settings, StartAtLogon startAtLogon)
    {
        // The window system posts its events here; Application.Run would install it too, but
        // the controller starts (and may get events) before that.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        var loaded = settings.Load();
        _windows = new Win32WindowSystem();
        _controller = new BackdropController(_windows, new GameList(loaded.Settings.Games));

        _startAtLogon = startAtLogon;
        var startAtLogonItem = new ToolStripMenuItem("Start at logon");
        startAtLogonItem.Click += (_, _) => ToggleStartAtLogon();

        var menu = new ContextMenuStrip();
        menu.Items.Add(startAtLogonItem);
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

        try
        {
            _startAtLogon.ApplyAtStart();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException)
        {
            _icon.ShowBalloonTip(5000, "Game Backdrop",
                $"Couldn't set up starting at logon: {e.Message}", ToolTipIcon.Warning);
        }

        _controller.Start();
    }

    private void ToggleStartAtLogon()
    {
        try
        {
            _startAtLogon.Set(!_startAtLogon.IsOn);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            _icon.ShowBalloonTip(5000, "Game Backdrop",
                $"Couldn't change starting at logon: {e.Message}", ToolTipIcon.Warning);
        }
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
