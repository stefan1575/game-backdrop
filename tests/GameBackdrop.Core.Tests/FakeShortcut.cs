using GameBackdrop.Core.Startup;

namespace GameBackdrop.Core.Tests;

/// <summary>The Startup folder's shortcut, in memory.</summary>
public sealed class FakeShortcut : IStartupShortcut
{
    public bool Exists { get; private set; }
    public int Creates { get; private set; }
    public bool FailNextCreate { get; set; }

    public void Create()
    {
        if (FailNextCreate)
        {
            FailNextCreate = false;
            throw new IOException("The Startup folder can't be written.");
        }
        Creates++;
        Exists = true;
    }

    /// <summary>Runs after the next removal, standing in for something that happens meanwhile.</summary>
    public Action? OnRemove { get; set; }

    public void Remove()
    {
        Exists = false;
        OnRemove?.Invoke();
    }

    public void PlaceByHand() => Exists = true;
}
