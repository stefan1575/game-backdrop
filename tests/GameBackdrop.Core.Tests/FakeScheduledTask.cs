using GameBackdrop.Core.Startup;

namespace GameBackdrop.Core.Tests;

/// <summary>Task Scheduler with room for one task: the app's.</summary>
public sealed class FakeScheduledTask(string exePath) : IScheduledTask
{
    public ScheduledTaskInfo? Task { get; set; }
    public int Registers { get; private set; }
    public int Runs { get; private set; }
    public bool FailRun { get; set; }
    public bool FailRead { get; set; }
    public bool FailDelete { get; set; }

    /// <summary>A task left by a copy of the app somewhere else.</summary>
    public void RegisteredBy(string otherExe, bool atLogon = false) => Task = new(otherExe, atLogon);

    public ScheduledTaskInfo? Read() => FailRead ? throw new UnauthorizedAccessException("The task can't be read.") : Task;

    public void Register(bool atLogon)
    {
        Registers++;
        Task = new(exePath, atLogon);
    }

    public void Run()
    {
        if (FailRun) throw new UnauthorizedAccessException("The task can't be run.");
        Runs++;
    }

    public void Delete()
    {
        if (FailDelete) throw new UnauthorizedAccessException("The task can't be deleted.");
        Task = null;
    }
}
