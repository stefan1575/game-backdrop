using GameBackdrop.Core.Startup;

namespace GameBackdrop.Windows;

/// <summary>
/// The running copy, through the single-instance guard: asked to quit with its quit event, and
/// gone once its mutex can be taken. Use it on one thread: the hold is a mutex, which belongs to
/// the thread that took it.
/// </summary>
/// <param name="wait">How long to wait for the running copy to put its games back and quit.</param>
public sealed class RunningCopy(TimeSpan wait, string mutexName = SingleInstance.MutexName, string openListEventName = SingleInstance.OpenListEventName)
    : IRunningCopy
{
    public QuitRequest AskToQuit() => SingleInstance.AskToQuit(mutexName);

    public IDisposable? WaitUntilGone() => SingleInstance.Claim(mutexName, openListEventName, wait);
}
