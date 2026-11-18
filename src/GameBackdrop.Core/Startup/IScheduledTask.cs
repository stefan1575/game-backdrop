namespace GameBackdrop.Core.Startup;

/// <summary>What the app's scheduled task starts, and whether it also starts it at logon.</summary>
public sealed record ScheduledTaskInfo(string ExePath, bool StartsAtLogon);

/// <summary>
/// The app's task in Task Scheduler, which runs it as administrator with no UAC prompt: run level
/// highest, for the current user only (interactive, no stored password), its action the app's exe
/// with the run's arguments.
/// </summary>
public interface IScheduledTask
{
    /// <summary>The task as registered, or null when there's none.</summary>
    ScheduledTaskInfo? Read();

    /// <summary>
    /// Create the task, or replace it so it starts the running exe, with a logon trigger when
    /// <paramref name="atLogon"/>. Needs administrator.
    /// </summary>
    void Register(bool atLogon);

    /// <summary>Start the task now. Running a task of one's own needs no prompt.</summary>
    void Run();

    /// <summary>Delete the task if it's there.</summary>
    void Delete();
}
