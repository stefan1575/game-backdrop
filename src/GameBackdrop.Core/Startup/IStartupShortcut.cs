namespace GameBackdrop.Core.Startup;

/// <summary>The app's shortcut in the user's Startup folder, which starts it at logon.</summary>
public interface IStartupShortcut
{
    bool Exists { get; }

    /// <summary>Create the shortcut, or replace it so it points at the running app.</summary>
    void Create();

    /// <summary>Remove the shortcut if it's there.</summary>
    void Remove();
}
