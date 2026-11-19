using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
    /// Remove everything the app puts outside its own folder (the Startup shortcut, the task),
    /// after asking the running copy to quit, and exit: 0 when done, 1 otherwise.
    /// </summary>
    public const string UninstallFlag = "--uninstall";

    /// <summary>
    /// How long a takeover waits (and an uninstall, for the running copy to quit). The copy quitting has put its games back before it starts this
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
    /// window and exits.
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

        // Before the task is run: an uninstall never starts the app.
        if (args.Contains(UninstallFlag)) return RunUninstall(exe, customSettings, shortcut, scheduledTask);

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
        using var uninstalls = instance.OnQuit(SynchronizationContext.Current!, tray.ExitThread);
        Application.Run(tray);
        return 0;
    }

    /// <summary>
    /// <c>--uninstall</c>. The one UAC prompt comes only when the task is there or the running copy
    /// is elevated; the elevated copy is this exe with the same arguments. Why it failed goes to the
    /// console it was run from, or else a message box.
    /// </summary>
    private static int RunUninstall(string exe, string? customSettings, StartupShortcut shortcut, ScheduledTask task)
    {
        var result = new Uninstall(RunningCopy.ForApp(TakeoverWait), shortcut, task)
            .Run(Environment.IsPrivilegedProcess, () => RunElevated(exe, customSettings));
        if (result.Done) return 0;
        Report(result.Message!, dialog: !result.ShownByElevatedCopy);
        return 1;
    }

    /// <summary>This exe's uninstall as administrator, waited for: its exit code, or null if it didn't start (the UAC prompt was declined).</summary>
    private static int? RunElevated(string exe, string? customSettings)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
        start.ArgumentList.Add(UninstallFlag);
        if (customSettings is not null)
        {
            start.ArgumentList.Add("--settings");
            start.ArgumentList.Add(customSettings);
        }
        try
        {
            using var elevated = Process.Start(start);
            if (elevated is null) return null;
            elevated.WaitForExit();
            return elevated.ExitCode;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Say why a command-line run failed: on its redirected error output, or the console of the
    /// shell that started it (this is a Windows app, so it has none of its own), or else, when
    /// <paramref name="dialog"/>, in a message box.
    /// </summary>
    private static void Report(string message, bool dialog)
    {
        var error = GetStdHandle(StdErrorHandle);
        if ((error != 0 && error != -1) || AttachConsole(AttachParentProcess))
        {
            Console.Error.WriteLine(message);
            return;
        }
        if (dialog) MessageBox.Show(message, "Game Backdrop", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private const int StdErrorHandle = -12;
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int handle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

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
