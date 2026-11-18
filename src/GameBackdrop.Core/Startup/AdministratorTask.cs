namespace GameBackdrop.Core.Startup;

/// <summary>
/// The scheduled task, as far as this exe is concerned: a task that starts another exe (the app
/// moved, or another copy registered it) counts as not set up.
/// </summary>
public sealed class AdministratorTask(IScheduledTask task, string exePath)
{
    /// <summary>The task is there and starts this exe.</summary>
    public bool IsSetUp => Read() is not null;

    /// <summary>The task is set up and starts the app at logon.</summary>
    public bool StartsAtLogon => Read()?.StartsAtLogon == true;

    /// <inheritdoc cref="IScheduledTask.Register"/>
    public void Register(bool atLogon) => task.Register(atLogon);

    /// <inheritdoc cref="IScheduledTask.Run"/>
    public void Run() => task.Run();

    /// <inheritdoc cref="IScheduledTask.Delete"/>
    public void Delete() => task.Delete();

    private ScheduledTaskInfo? Read() =>
        task.Read() is { } info && SamePath(info.ExePath, exePath) ? info : null;

    private static bool SamePath(string a, string b) =>
        string.Equals(Normalise(a), Normalise(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalise(string path) => path.Trim().Trim('"').Replace('/', '\\');
}
