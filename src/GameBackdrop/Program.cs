using System.ComponentModel;
using System.Diagnostics;
using GameBackdrop.Core.Settings;
using GameBackdrop.Core.Startup;
using GameBackdrop.Windows;

namespace GameBackdrop;

internal static class Program
{
    /// <summary>
    /// Passed by a copy that hands over to an elevated copy: wait for that copy to quit (and
    /// put its games back) instead of exiting because it still runs.
    /// </summary>
    public const string TakeoverFlag = "--takeover";

    /// <summary>Passed with <see cref="TakeoverFlag"/> by a copy turning on "Always run as administrator": set the task up.</summary>
    public const string SetUpTaskFlag = "--set-up-task";

    /// <summary>
    /// In the task's own arguments: this copy was started by the task, so it never runs the task
    /// again (it runs unelevated for a user who isn't an administrator).
    /// </summary>
    public const string StartedByTaskFlag = "--started-by-task";

    /// <summary>
    /// Run by a package manager before it deletes the exe: quit the running copy (which puts its
    /// games back), remove the Startup shortcut and the task, and exit 0, or 1 with a message.
    /// </summary>
    public const string UninstallFlag = "--uninstall";

    private const int ErrorCancelled = 1223;

    /// <summary>
    /// How long a takeover waits. The copy quitting has put its games back before it starts this
    /// one, so it normally lets go at once; the margin is for a slow quit on a busy PC.
    /// </summary>
    private static readonly TimeSpan TakeoverWait = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Usage: <c>GameBackdrop.exe [--settings &lt;path&gt;] [--takeover | --uninstall]</c>. The settings file
    /// defaults to <c>%APPDATA%\GameBackdrop\settings.json</c>, and the Start at logon shortcut
    /// goes in the user's Startup folder. A run with its own <c>--settings</c> file keeps its
    /// shortcut in a <c>Startup</c> folder beside that file instead, and has a scheduled task of
    /// its own, so a test run leaves the real ones alone. A launch that finds the "Always run as
    /// administrator" task runs it and exits. A second launch opens the running copy's game list
    /// window and exits. <c>--uninstall</c> tidies up for a package manager's uninstall.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var takeover = args.Contains(TakeoverFlag);
        var customSettings = SettingsPathFrom(args);
        var store = new SettingsStore(customSettings ?? SettingsStore.DefaultPath);
        var exe = Environment.ProcessPath ?? Application.ExecutablePath;
        var scheduledTask = new ScheduledTask(ScheduledTask.Folder, ScheduledTask.NameFor(customSettings), exe, TaskArguments(customSettings));
        var shortcut = StartupShortcutFor(exe, customSettings);
        if (args.Contains(UninstallFlag))
            return Uninstall(new Uninstall(new RunningCopy(TakeoverWait), shortcut, scheduledTask), exe, customSettings);

        var task = new AdministratorTask(scheduledTask, exe);
        var startAtLogon = new StartAtLogon(store, shortcut, task);
        var always = new AlwaysAsAdministrator(task, startAtLogon);

        // The task starts the elevated copy, which takes it from here (a second launch included).
        if (!takeover && always.LaunchThroughTask(Environment.IsPrivilegedProcess, args.Contains(StartedByTaskFlag)))
            return 0;

        // Disposed last, after the tray has put every game back: only then may a copy taking over start.
        using var instance = SingleInstance.Claim(takeover ? TakeoverWait : TimeSpan.Zero);
        if (instance is null)
        {
            if (takeover)
            {
                MessageBox.Show("Game Backdrop couldn't start as administrator: the copy already running didn't quit.",
                    "Game Backdrop", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return 0;
        }

        using var tray = new TrayApplication(store, startAtLogon, always, customSettings, setUpTask: args.Contains(SetUpTaskFlag));
        // The tray app installed the UI thread's context; a second launch is answered on it.
        // Declared after the tray, so it stops (and waits out a signal in flight) before the tray
        // and its context are disposed.
        using var secondLaunches = instance.OnSecondLaunch(SynchronizationContext.Current!, tray.ShowList);
        using var quitRequests = instance.OnQuitRequest(SynchronizationContext.Current!, tray.ExitThread);
        Application.Run(tray);
        return 0;
    }

    /// <summary>
    /// <c>--uninstall</c>: tidy up, as administrator if the task or the running copy needs that (an
    /// elevated copy of this run, so one UAC prompt). Silent when it's done.
    /// </summary>
    private static int Uninstall(Uninstall uninstall, string exe, string? customSettings)
    {
        UninstallResult result;
        try
        {
            result = uninstall.Run(Environment.IsPrivilegedProcess);
        }
        catch (Exception e) when (AlwaysAsAdministrator.IsTaskSchedulerError(e))
        {
            return Failed($"Couldn't remove what starts Game Backdrop: {e.Message}");
        }
        switch (result)
        {
            case UninstallResult.Done:
                return 0;
            case UninstallResult.StillRunning:
                return Failed("Game Backdrop is still running: it didn't quit. Quit it from its tray menu, then uninstall again.");
        }

        var start = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
        start.ArgumentList.Add(UninstallFlag);
        // The full path: the elevated copy may start in another folder.
        if (customSettings is not null)
        {
            start.ArgumentList.Add("--settings");
            start.ArgumentList.Add(customSettings);
        }
        try
        {
            using var elevated = Process.Start(start)!;
            elevated.WaitForExit();
            return elevated.ExitCode;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            return Failed("Uninstalling needs administrator, to remove the Always run as administrator task or quit a copy running as administrator. Nothing was removed.");
        }
        catch (Win32Exception e)
        {
            return Failed($"Couldn't start the uninstall as administrator: {e.Message}");
        }
    }

    private static int Failed(string message)
    {
        MessageBox.Show(message, "Game Backdrop", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return 1;
    }

    private static StartupShortcut StartupShortcutFor(string exe, string? customSettings)
    {
        return customSettings is null
            ? new StartupShortcut(StartupShortcut.UserStartupFolder, exe, "")
            : new StartupShortcut(Path.Combine(Path.GetDirectoryName(customSettings)!, "Startup"), exe,
                $"--settings \"{customSettings}\"");
    }

    /// <summary>The task's action: this exe, with the same <c>--settings</c> argument, if any.</summary>
    private static string TaskArguments(string? customSettings) =>
        customSettings is null ? StartedByTaskFlag : $"--settings \"{customSettings}\" {StartedByTaskFlag}";

    private static string? SettingsPathFrom(string[] args)
    {
        var i = Array.IndexOf(args, "--settings");
        return i >= 0 && i + 1 < args.Length ? Path.GetFullPath(args[i + 1]) : null;
    }
}
