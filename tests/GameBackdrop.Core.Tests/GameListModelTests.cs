using GameBackdrop.Core.Settings;

namespace GameBackdrop.Core.Tests;

public sealed class GameListModelTests : IDisposable
{
    private const string Dota = @"C:\Games\dota 2 beta\game\bin\win64\dota2.exe";
    private const string Novel = @"D:\VN\Novel\novel.exe";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "GameBackdropTests", Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_folder, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private GameListModel Open() => GameListModel.Load(new SettingsStore(FilePath));

    /// <summary>The list as the settings file holds it now, read by a fresh store.</summary>
    private List<GameEntry> Saved() => new SettingsStore(FilePath).Load().Settings.Games;

    [Fact]
    public void An_added_game_is_saved_at_once_switched_on_with_the_mouse_kept_inside()
    {
        var model = Open();

        Assert.Equal(AddResult.Added, model.Add(Dota, "Dota 2"));

        var saved = Assert.Single(Saved());
        Assert.Equal(Dota, saved.ExePath);
        Assert.Equal("Dota 2", saved.DisplayName);
        Assert.True(saved.Enabled);
        Assert.True(saved.KeepMouseInside);
        Assert.Equal(Dota, Assert.Single(model.Games).ExePath);
    }

    [Theory]
    [InlineData(Dota)]
    [InlineData(@"c:\games\DOTA 2 BETA\game\bin\win64\Dota2.EXE")]
    [InlineData("C:/Games/dota 2 beta/game/bin/win64/dota2.exe")]
    public void Adding_a_game_whose_exe_is_already_listed_is_refused_and_changes_nothing(string samePath)
    {
        var model = Open();
        model.Add(Dota, "Dota 2");
        var before = File.ReadAllText(FilePath);

        Assert.Equal(AddResult.AlreadyListed, model.Add(samePath, "Again"));

        Assert.Equal("Dota 2", Assert.Single(model.Games).DisplayName);
        Assert.Equal(before, File.ReadAllText(FilePath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("dota2.exe")]
    [InlineData(@"games\dota2.exe")]
    public void A_game_without_a_full_exe_path_is_refused(string path)
    {
        var model = Open();

        Assert.Equal(AddResult.NotAFullPath, model.Add(path, null));

        Assert.Empty(model.Games);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void A_renamed_game_is_saved_under_its_new_name()
    {
        var model = Open();
        model.Add(Dota, "dota2");

        Assert.True(model.Rename(Dota.ToUpperInvariant(), "  Dota 2  "));

        Assert.Equal("Dota 2", Assert.Single(Saved()).DisplayName);
        Assert.Equal("Dota 2", Assert.Single(model.Games).Name);
    }

    [Fact]
    public void A_game_renamed_to_nothing_goes_back_to_its_exe_name()
    {
        var model = Open();
        model.Add(Dota, "Dota 2");

        model.Rename(Dota, " ");

        Assert.Null(Assert.Single(Saved()).DisplayName);
        Assert.Equal("dota2", Assert.Single(model.Games).Name);
    }

    [Fact]
    public void A_game_switched_off_and_on_again_is_saved_each_time()
    {
        var model = Open();
        model.Add(Dota, "Dota 2");

        Assert.True(model.SetEnabled(Dota, false));
        Assert.False(Assert.Single(Saved()).Enabled);
        Assert.False(Assert.Single(model.Games).Enabled);

        model.SetEnabled(Dota, true);
        Assert.True(Assert.Single(Saved()).Enabled);
    }

    [Fact]
    public void Keep_mouse_inside_switched_off_is_saved()
    {
        var model = Open();
        model.Add(Dota, "Dota 2");

        Assert.True(model.SetKeepMouseInside(Dota, false));

        Assert.False(Assert.Single(Saved()).KeepMouseInside);
        Assert.True(Assert.Single(Saved()).Enabled);
    }

    [Fact]
    public void A_removed_game_is_gone_from_the_file_and_the_others_stay()
    {
        var model = Open();
        model.Add(Dota, "Dota 2");
        model.Add(Novel, "Novel");

        Assert.True(model.Remove(Dota));

        Assert.Equal(Novel, Assert.Single(Saved()).ExePath);
        Assert.Equal(Novel, Assert.Single(model.Games).ExePath);
    }

    [Fact]
    public void A_change_to_a_game_not_on_the_list_is_refused()
    {
        var model = Open();
        model.Add(Dota, "Dota 2");

        Assert.False(model.Rename(Novel, "Novel"));
        Assert.False(model.SetEnabled(Novel, false));
        Assert.False(model.SetKeepMouseInside(Novel, false));
        Assert.False(model.Remove(Novel));

        Assert.Equal(Dota, Assert.Single(Saved()).ExePath);
    }

    [Fact]
    public void The_list_reloads_as_it_was_saved_with_the_games_in_order()
    {
        var model = Open();
        model.Add(Dota, "Dota 2");
        model.Add(Novel, null);
        model.SetEnabled(Novel, false);

        var reloaded = Open();

        Assert.Equal([("Dota 2", true), ("novel", false)], reloaded.Games.Select(g => (g.Name, g.Enabled)));
    }

    [Fact]
    public void A_change_that_cannot_be_saved_throws_and_leaves_the_list_as_it_was()
    {
        var model = Open();
        model.Add(Dota, "Dota 2");
        using (File.Open(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<SettingsSaveException>(() => model.SetEnabled(Dota, false));
            Assert.Throws<SettingsSaveException>(() => model.Add(Novel, "Novel"));
        }

        Assert.True(Assert.Single(model.Games).Enabled);
        Assert.True(Assert.Single(Saved()).Enabled);
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
    }

    [Fact]
    public void A_change_leaves_the_entries_read_before_it_as_they_were()
    {
        var model = Open();
        model.Add(Dota, "Dota 2");
        var before = model.Games[0];

        model.SetEnabled(Dota, false);
        model.Rename(Dota, "Renamed");

        Assert.True(before.Enabled);
        Assert.Equal("Dota 2", before.DisplayName);
    }

    [Fact]
    public void Each_saved_change_is_announced_once_and_a_refused_one_is_not()
    {
        var model = Open();
        var announced = 0;
        model.Changed += () => announced++;

        model.Add(Dota, "Dota 2");
        model.Add(Dota, "Again");
        model.Rename(Dota, "Dota");
        model.Remove(Novel);

        Assert.Equal(2, announced);
    }

    [Fact]
    public void A_corrupt_file_is_copied_aside_before_the_first_change_overwrites_it()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, "{ not json");
        var model = Open();
        Assert.Equal(SettingsLoadStatus.Corrupt, model.LoadStatus);
        Assert.Equal("{ not json", File.ReadAllText(FilePath));

        model.Add(Dota, "Dota 2");
        model.Add(Novel, "Novel");

        var copy = Assert.Single(Directory.GetFiles(_folder, "settings.corrupt-*.json"));
        Assert.Equal("{ not json", File.ReadAllText(copy));
        Assert.Equal(2, Saved().Count);
    }
}
