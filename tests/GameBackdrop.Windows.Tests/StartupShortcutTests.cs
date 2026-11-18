namespace GameBackdrop.Windows.Tests;

/// <summary>The Startup shortcut as a real <c>.lnk</c>, in a temp folder standing in for the Startup folder.</summary>
public sealed class StartupShortcutTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "GameBackdropTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Create_writes_a_shortcut_that_starts_the_app_with_its_arguments()
    {
        var shortcut = new StartupShortcut(_folder, @"C:\Tools\GameBackdrop.exe", "--settings \"D:\\My Settings\\s.json\"");

        shortcut.Create();

        Assert.True(shortcut.Exists);
        Assert.Equal(["Game Backdrop.lnk"], Directory.GetFiles(_folder).Select(Path.GetFileName));
        var target = shortcut.Read()!;
        Assert.Equal(@"C:\Tools\GameBackdrop.exe", target.ExePath);
        Assert.Equal("--settings \"D:\\My Settings\\s.json\"", target.Arguments);
    }

    [Fact]
    public void Create_replaces_a_shortcut_to_an_exe_that_moved()
    {
        new StartupShortcut(_folder, @"C:\Old\GameBackdrop.exe", "").Create();

        var moved = new StartupShortcut(_folder, @"C:\New\GameBackdrop.exe", "");
        moved.Create();

        Assert.Equal(@"C:\New\GameBackdrop.exe", moved.Read()!.ExePath);
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public void Remove_deletes_the_shortcut_and_is_harmless_when_it_is_gone()
    {
        var shortcut = new StartupShortcut(_folder, @"C:\Tools\GameBackdrop.exe", "");
        shortcut.Create();

        shortcut.Remove();
        shortcut.Remove();

        Assert.False(shortcut.Exists);
        Assert.Null(shortcut.Read());
    }
}
