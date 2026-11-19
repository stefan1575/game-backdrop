using GameBackdrop.Core.Startup;

namespace GameBackdrop.Core.Tests;

/// <summary>The copy of the app that may be running, in memory.</summary>
public sealed class FakeRunningCopy : IRunningCopy
{
    public RunningCopyState State { get; set; } = RunningCopyState.None;
    public bool RefusesToQuit { get; set; }
    public int Quits { get; private set; }

    /// <summary>Called as the copy quits, before it lets go: what it does on the way out.</summary>
    public Action? OnQuit { get; set; }

    public RunningCopyState Find() => State;

    public bool Quit()
    {
        Quits++;
        if (RefusesToQuit || State == RunningCopyState.OutOfReach) return false;
        OnQuit?.Invoke();
        State = RunningCopyState.None;
        return true;
    }
}
