using GameBackdrop.Core.Settings;
using GameBackdrop.Windows;

namespace GameBackdrop;

internal static class Program
{
    private const string SingleInstanceMutex = @"Local\GameBackdrop.SingleInstance";

    /// <summary>
    /// Passed by a copy that restarts the app as administrator: wait for that copy to quit (and
    /// put its games back) instead of exiting because it still runs.
    /// </summary>
    public const string TakeoverFlag = "--takeover";

    /// <summary>
    /// How long a takeover waits. The copy quitting retries a failed restore for under a second,
    /// but then shows a dialog naming the games it couldn't put back, which waits for Stefan.
    /// </summary>
    private static readonly TimeSpan TakeoverWait = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Usage: <c>GameBackdrop.exe [--settings &lt;path&gt;] [--takeover]</c>. The settings file
    /// defaults to <c>%APPDATA%\GameBackdrop\settings.json</c>.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var takeover = args.Contains(TakeoverFlag);
        // Disposed last, after the tray has put every game back: only then may a copy taking over start.
        using var instance = SingleInstance.TryAcquire(SingleInstanceMutex, takeover ? TakeoverWait : TimeSpan.Zero);
        if (instance is null)
        {
            if (takeover)
            {
                MessageBox.Show("Game Backdrop couldn't restart as administrator: the copy already running didn't quit.",
                    "Game Backdrop", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return 0; // Another copy is running; for now a second launch just exits.
        }

        var settingsPath = SettingsPathFrom(args);
        using var tray = new TrayApplication(new SettingsStore(settingsPath ?? SettingsStore.DefaultPath), settingsPath);
        Application.Run(tray);
        return 0;
    }

    private static string? SettingsPathFrom(string[] args)
    {
        var i = Array.IndexOf(args, "--settings");
        return i >= 0 && i + 1 < args.Length ? Path.GetFullPath(args[i + 1]) : null;
    }
}
