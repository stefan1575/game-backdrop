using GameBackdrop.Core.Settings;

namespace GameBackdrop;

internal static class Program
{
    /// <summary>
    /// Usage: <c>GameBackdrop.exe [--settings &lt;path&gt;]</c>. The settings file defaults to
    /// <c>%APPDATA%\GameBackdrop\settings.json</c>. A second launch opens the running copy's
    /// game list window and exits.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        using var instance = SingleInstance.Claim();
        if (instance is null) return 0;

        ApplicationConfiguration.Initialize();
        var settingsPath = SettingsPathFrom(args) ?? SettingsStore.DefaultPath;

        using var tray = new TrayApplication(new SettingsStore(settingsPath));
        // The tray app installed the UI thread's context; a second launch is answered on it.
        // Declared after the tray, so it stops (and waits out a signal in flight) before the tray
        // and its context are disposed.
        using var secondLaunches = instance.OnSecondLaunch(SynchronizationContext.Current!, tray.ShowList);
        Application.Run(tray);
        return 0;
    }

    private static string? SettingsPathFrom(string[] args)
    {
        var i = Array.IndexOf(args, "--settings");
        return i >= 0 && i + 1 < args.Length ? Path.GetFullPath(args[i + 1]) : null;
    }
}
