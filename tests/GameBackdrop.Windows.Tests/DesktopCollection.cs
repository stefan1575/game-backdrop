namespace GameBackdrop.Windows.Tests;

/// <summary>
/// The tests that use the real desktop (foreground, Z order, simulated key presses) run one at a
/// time: one test's key press or foreground change would upset another's.
/// </summary>
[CollectionDefinition(Name)]
public sealed class DesktopCollection
{
    public const string Name = "Desktop";
}
