namespace GameBackdrop.Core.Startup;

/// <summary>Whether a copy of the app runs in this session, and whether this process can reach it.</summary>
public enum RunningCopyState
{
    /// <summary>No copy runs.</summary>
    None,

    /// <summary>A copy runs, and this process can ask it to quit.</summary>
    Running,

    /// <summary>A copy runs as administrator, so only a process running as administrator can ask it to quit.</summary>
    OutOfReach,
}

/// <summary>The copy of the app that runs in this session, as another process sees it.</summary>
public interface IRunningCopy
{
    RunningCopyState Find();

    /// <summary>
    /// Ask the running copy to quit, and wait (a while) until it has: it puts every game back
    /// before it lets go. True once no copy runs; false if it didn't quit in time or can't be asked.
    /// </summary>
    bool Quit();
}
