using GameBackdrop.Core.Settings;

namespace GameBackdrop;

internal static class Program
{
    private const string SingleInstanceMutex = @"Local\GameBackdrop.SingleInstance";

    /// <summary>
    /// Usage: <c>GameBackdrop.exe [--settings &lt;path&gt;]</c>. The settings file defaults to
    /// <c>%APPDATA%\GameBackdrop\settings.json</c>.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutex, out var createdNew);
        if (!createdNew) return 0; // Another copy is running; for now a second launch just exits.

        ApplicationConfiguration.Initialize();
        var settingsPath = SettingsPathFrom(args) ?? SettingsStore.DefaultPath;

        using var tray = new TrayApplication(new SettingsStore(settingsPath));
        Application.Run(tray);
        GC.KeepAlive(mutex);
        return 0;
    }

    private static string? SettingsPathFrom(string[] args)
    {
        var i = Array.IndexOf(args, "--settings");
        return i >= 0 && i + 1 < args.Length ? Path.GetFullPath(args[i + 1]) : null;
    }
}
