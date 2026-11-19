using GameBackdrop.Core.Startup;

namespace GameBackdrop.Windows;

/// <summary>
/// The running copy, through the single-instance guard: asked to quit with its settings scope's
/// quit signal, and gone once the guard's mutex can be taken. Use it on one thread: the hold is a
/// mutex, which belongs to the thread that took it.
/// </summary>
/// <param name="slice">How long one <see cref="TryHold"/> waits.</param>
/// <param name="quitEventName">The scope's quit signal, from <see cref="SingleInstance.QuitEventNameFor"/>.</param>
public sealed class RunningCopy(TimeSpan slice, string quitEventName, string mutexName = SingleInstance.MutexName,
    string openListEventName = SingleInstance.OpenListEventName) : IRunningCopy
{
    public QuitRequest AskToQuit() => SingleInstance.AskToQuit(quitEventName);

    public IDisposable? TryHold() => SingleInstance.Claim(mutexName, openListEventName, quitEventName, slice);
}
