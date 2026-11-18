using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using GameBackdrop.Core.Startup;

namespace GameBackdrop.Windows;

/// <summary>
/// The app's task in Task Scheduler, through its COM API (<c>Schedule.Service</c>): in the
/// <see cref="Folder"/> folder, for the signed-in user only (an interactive token, no stored
/// password), its action the exe with its arguments. Registered at run level highest it starts the
/// app as administrator, and running it, as the user who owns it, needs no UAC prompt.
/// </summary>
/// <param name="folder">The task's folder, e.g. <c>\Game Backdrop</c>; made when the task is registered.</param>
/// <param name="highest">Run level highest (the app's, which needs administrator to register), or
/// the user's normal run level (tests, which run unelevated).</param>
public sealed class ScheduledTask(string folder, string name, string exePath, string arguments, bool highest = true) : IScheduledTask
{
    public const string Folder = @"\Game Backdrop";

    private const int TaskActionExec = 0;
    private const int TaskTriggerLogon = 9;
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunLevelLua = 0;
    private const int TaskRunLevelHighest = 1;
    private const int TaskInstancesParallel = 0;
    private const int NormalPriority = 4; // the default, 7, starts the app below normal priority

    private const int ErrorFileNotFound = unchecked((int)0x80070002);
    private const int ErrorPathNotFound = unchecked((int)0x80070003);

    /// <summary>
    /// The task's name: one per user, and for a run with its own <c>--settings</c> file one of its
    /// own, derived from that path, so a test run never touches the real task.
    /// </summary>
    public static string NameFor(string? customSettings)
    {
        var user = $"{Environment.UserDomainName} {Environment.UserName}";
        if (customSettings is null) return $"Always as administrator ({user})";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(customSettings.ToUpperInvariant()));
        return $"Always as administrator ({user}) test {Convert.ToHexString(hash, 0, 4)}";
    }

    public string Name => name;

    public ScheduledTaskInfo? Read()
    {
        var service = Connect();
        try
        {
            var task = GetTask(service);
            if (task is null) return null;
            try
            {
                var definition = task.Definition;
                string? path = null;
                foreach (var action in definition.Actions)
                {
                    if ((int)action.Type != TaskActionExec) continue;
                    path = (string)action.Path;
                    break;
                }
                if (path is null) return null;
                var atLogon = false;
                foreach (var trigger in definition.Triggers)
                    atLogon |= (int)trigger.Type == TaskTriggerLogon && (bool)trigger.Enabled;
                return new ScheduledTaskInfo(path, atLogon);
            }
            finally
            {
                Release(task);
            }
        }
        finally
        {
            Release(service);
        }
    }

    public void Register(bool atLogon)
    {
        var service = Connect();
        try
        {
            var user = WindowsIdentity.GetCurrent().Name;
            var definition = service.NewTask(0);
            definition.RegistrationInfo.Description =
                "Starts Game Backdrop as administrator with no UAC prompt. Turn it off in the app's tray menu (Always run as administrator).";

            var principal = definition.Principal;
            principal.UserId = user;
            principal.LogonType = TaskLogonInteractiveToken;
            principal.RunLevel = highest ? TaskRunLevelHighest : TaskRunLevelLua;

            var settings = definition.Settings;
            // A second launch runs the task while the app runs: the new copy must start, to ask
            // the running one to open its list, not be dropped as a second instance of the task.
            settings.MultipleInstances = TaskInstancesParallel;
            settings.DisallowStartIfOnBatteries = false;
            settings.StopIfGoingOnBatteries = false;
            settings.ExecutionTimeLimit = "PT0S"; // never stopped (the default is 72 hours)
            settings.Priority = NormalPriority;

            if (atLogon)
            {
                var trigger = definition.Triggers.Create(TaskTriggerLogon);
                trigger.UserId = user;
            }

            var action = definition.Actions.Create(TaskActionExec);
            action.Path = exePath;
            action.Arguments = arguments;
            action.WorkingDirectory = Path.GetDirectoryName(exePath) ?? "";

            var taskFolder = GetOrCreateFolder(service);
            Release(taskFolder.RegisterTaskDefinition(name, definition, TaskCreateOrUpdate, null, null, TaskLogonInteractiveToken, null));
        }
        finally
        {
            Release(service);
        }
    }

    public void Run()
    {
        var service = Connect();
        try
        {
            var task = GetTask(service) ?? throw new InvalidOperationException($"There is no task {folder}\\{name}.");
            try
            {
                Release(task.Run(null));
            }
            finally
            {
                Release(task);
            }
        }
        finally
        {
            Release(service);
        }
    }

    /// <summary>Delete the task, and its folder once nothing else is in it.</summary>
    public void Delete()
    {
        var service = Connect();
        try
        {
            var taskFolder = GetFolder(service);
            if (taskFolder is null) return;
            try
            {
                taskFolder.DeleteTask(name, 0);
            }
            catch (Exception e) when (IsNotFound(e))
            {
                // Already gone.
            }
            try
            {
                if ((int)taskFolder.GetTasks(1 /* TASK_ENUM_HIDDEN */).Count == 0 && (int)taskFolder.GetFolders(0).Count == 0)
                    service.GetFolder(Parent(folder)).DeleteFolder(Leaf(folder), 0);
            }
            catch (Exception e) when (e is COMException or UnauthorizedAccessException || IsNotFound(e))
            {
                // Another user's task is in it, or it's not ours to delete: it stays.
            }
        }
        finally
        {
            Release(service);
        }
    }

    private static dynamic Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }

    private dynamic? GetFolder(dynamic service)
    {
        try
        {
            return service.GetFolder(folder);
        }
        catch (Exception e) when (IsNotFound(e))
        {
            return null;
        }
    }

    private dynamic GetOrCreateFolder(dynamic service) =>
        GetFolder(service) ?? service.GetFolder(Parent(folder)).CreateFolder(Leaf(folder), null);

    private dynamic? GetTask(dynamic service)
    {
        var taskFolder = GetFolder(service);
        if (taskFolder is null) return null;
        try
        {
            return taskFolder.GetTask(name);
        }
        catch (Exception e) when (IsNotFound(e))
        {
            return null;
        }
    }

    private static string Parent(string path)
    {
        var i = path.TrimEnd('\\').LastIndexOf('\\');
        return i <= 0 ? "\\" : path[..i];
    }

    private static string Leaf(string path) => path.TrimEnd('\\')[(path.TrimEnd('\\').LastIndexOf('\\') + 1)..];

    private static bool IsNotFound(Exception e) =>
        (e is TargetInvocationException { InnerException: { } inner } ? inner : e).HResult is ErrorFileNotFound or ErrorPathNotFound;

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com);
    }
}
