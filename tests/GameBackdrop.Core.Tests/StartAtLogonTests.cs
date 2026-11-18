using GameBackdrop.Core.Settings;
using GameBackdrop.Core.Startup;

namespace GameBackdrop.Core.Tests;

public sealed class StartAtLogonTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "GameBackdropTests", Guid.NewGuid().ToString("N"));
    private readonly FakeShortcut _shortcut = new();
    private SettingsStore Store => new(Path.Combine(_folder, "settings.json"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private StartAtLogon NewRun() => new(Store, _shortcut);

    [Fact]
    public void The_first_run_creates_the_shortcut_and_records_it_on()
    {
        NewRun().ApplyAtStart();

        Assert.True(_shortcut.Exists);
        Assert.Equal(1, _shortcut.Creates);
        Assert.True(Store.Load().Settings.StartAtLogon);
    }

    [Fact]
    public void Turning_it_off_removes_the_shortcut_and_survives_a_restart()
    {
        NewRun().ApplyAtStart();

        NewRun().Set(false);
        Assert.False(_shortcut.Exists);

        var restarted = NewRun();
        restarted.ApplyAtStart();
        Assert.False(_shortcut.Exists);
        Assert.False(restarted.IsOn);
        Assert.Equal(1, _shortcut.Creates);
    }

    [Fact]
    public void An_unreadable_settings_file_is_not_taken_for_a_first_run_and_is_never_overwritten()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Store.FilePath, "{ not json");

        var run = NewRun();
        Assert.False(run.CanRecord);
        run.ApplyAtStart();
        Assert.False(_shortcut.Exists);

        Assert.Throws<InvalidOperationException>(() => run.Set(true));
        Assert.False(_shortcut.Exists);
        Assert.Equal("{ not json", File.ReadAllText(Store.FilePath));
    }

    [Fact]
    public void A_settings_file_another_program_holds_open_cant_be_recorded_rather_than_throwing()
    {
        NewRun().ApplyAtStart();
        var run = NewRun();

        using (new FileStream(Store.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(run.CanRecord);
        }
        Assert.True(run.CanRecord);
    }

    [Fact]
    public void A_settings_path_that_cant_be_read_for_access_reasons_cant_be_recorded_rather_than_throwing()
    {
        // Reading a folder as a file is refused as access denied, as an ACL denying read would be.
        Directory.CreateDirectory(Store.FilePath);

        Assert.False(NewRun().CanRecord);
    }

    [Fact]
    public void Turning_it_back_on_creates_the_shortcut_and_keeps_a_game_added_by_hand_meanwhile()
    {
        var run = NewRun();
        run.ApplyAtStart();
        run.Set(false);
        var edited = Store.Load().Settings;
        edited.Games.Add(new GameEntry { ExePath = @"C:\Games\a.exe" });
        Store.Save(edited);

        run.Set(true);

        Assert.True(run.IsOn);
        var settings = Store.Load().Settings;
        Assert.True(settings.StartAtLogon);
        Assert.Equal(@"C:\Games\a.exe", Assert.Single(settings.Games).ExePath);
    }

    [Fact]
    public void A_first_run_that_cant_create_the_shortcut_records_nothing_so_the_next_start_tries_again()
    {
        _shortcut.FailNextCreate = true;
        Assert.Throws<IOException>(() => NewRun().ApplyAtStart());
        Assert.Null(Store.Load().Settings.StartAtLogon);

        NewRun().ApplyAtStart();
        Assert.True(_shortcut.Exists);
        Assert.True(Store.Load().Settings.StartAtLogon);
    }

    [Fact]
    public void Recorded_off_leaves_a_shortcut_placed_by_hand_alone()
    {
        NewRun().ApplyAtStart();
        NewRun().Set(false);
        _shortcut.PlaceByHand();

        var run = NewRun();
        run.ApplyAtStart();

        Assert.True(run.IsOn);
        Assert.False(Store.Load().Settings.StartAtLogon);
    }
}
