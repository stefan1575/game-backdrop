using GameBackdrop.Core.Settings;
using GameBackdrop.Core.Startup;
using GameBackdrop.Windows;

namespace GameBackdrop;

internal static class Program
{
    /// <summary>
    /// Usage: <c>GameBackdrop.exe [--settings &lt;path&gt;]</c>. The settings file defaults to
    /// <c>%APPDATA%\GameBackdrop\settings.json</c>, and the Start at logon shortcut goes in the
    /// user's Startup folder. A run with its own <c>--settings</c> file keeps its shortcut in a
    /// <c>Startup</c> folder beside that file instead, so a test run leaves the real one alone.
    /// A second launch opens the running copy's game list window and exits.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        using var instance = SingleInstance.Claim();
        if (instance is null) return 0;

        ApplicationConfiguration.Initialize();
        var customSettings = SettingsPathFrom(args);
        var store = new SettingsStore(customSettings ?? SettingsStore.DefaultPath);

        using var tray = new TrayApplication(store, new StartAtLogon(store, StartupShortcutFor(customSettings)));
        // The tray app installed the UI thread's context; a second launch is answered on it.
        // Declared after the tray, so it stops (and waits out a signal in flight) before the tray
        // and its context are disposed.
        using var secondLaunches = instance.OnSecondLaunch(SynchronizationContext.Current!, tray.ShowList);
        Application.Run(tray);
        return 0;
    }

    private static StartupShortcut StartupShortcutFor(string? customSettings)
    {
        var exe = Environment.ProcessPath ?? Application.ExecutablePath;
        return customSettings is null
            ? new StartupShortcut(StartupShortcut.UserStartupFolder, exe, "")
            : new StartupShortcut(Path.Combine(Path.GetDirectoryName(customSettings)!, "Startup"), exe,
                $"--settings \"{customSettings}\"");
    }

    private static string? SettingsPathFrom(string[] args)
    {
        var i = Array.IndexOf(args, "--settings");
        return i >= 0 && i + 1 < args.Length ? Path.GetFullPath(args[i + 1]) : null;
    }
}
