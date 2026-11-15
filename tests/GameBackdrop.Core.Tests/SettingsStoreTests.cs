using System.Text.Json.Nodes;
using GameBackdrop.Core.Settings;

namespace GameBackdrop.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "GameBackdropTests", Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_folder, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void A_missing_file_starts_with_an_empty_game_list()
    {
        var result = new SettingsStore(FilePath).Load();

        Assert.Empty(result.Settings.Games);
        Assert.Equal(SettingsLoadStatus.Missing, result.Status);
    }

    [Fact]
    public void Saved_games_load_back_the_same()
    {
        var store = new SettingsStore(FilePath);
        var settings = new AppSettings();
        settings.Games.Add(new GameEntry { ExePath = @"C:\Games\Dota\dota2.exe", DisplayName = "Dota 2", Enabled = true, KeepMouseInside = false });
        settings.Games.Add(new GameEntry { ExePath = @"D:\VN\novel.exe", DisplayName = "Novel", Enabled = false });

        store.Save(settings);
        var loaded = store.Load();

        Assert.Equal(SettingsLoadStatus.Loaded, loaded.Status);
        Assert.Collection(loaded.Settings.Games,
            g =>
            {
                Assert.Equal(@"C:\Games\Dota\dota2.exe", g.ExePath);
                Assert.Equal("Dota 2", g.DisplayName);
                Assert.True(g.Enabled);
                Assert.False(g.KeepMouseInside);
            },
            g =>
            {
                Assert.Equal("Novel", g.DisplayName);
                Assert.False(g.Enabled);
                Assert.True(g.KeepMouseInside);
            });
    }

    [Fact]
    public void A_hand_written_file_with_only_exe_paths_gets_the_defaults()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, """{ "games": [ { "exePath": "C:\\Games\\a.exe" } ] }""");

        var game = Assert.Single(new SettingsStore(FilePath).Load().Settings.Games);

        Assert.Equal(@"C:\Games\a.exe", game.ExePath);

        Assert.True(game.Enabled);
        Assert.True(game.KeepMouseInside);
    }

    [Fact]
    public void Unknown_fields_are_kept_through_a_save()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, """
            {
              "futureSetting": { "nested": [1, 2] },
              "games": [ { "exePath": "C:\\Games\\a.exe", "displayName": "A", "colour": "black" } ]
            }
            """);
        var store = new SettingsStore(FilePath);

        var settings = store.Load().Settings;
        settings.Games[0].DisplayName = "Renamed";
        store.Save(settings);

        var json = JsonNode.Parse(File.ReadAllText(FilePath))!;
        Assert.Equal(2, json["futureSetting"]!["nested"]!.AsArray().Count);
        Assert.Equal("black", (string?)json["games"]![0]!["colour"]);
        Assert.Equal("Renamed", (string?)json["games"]![0]!["displayName"]);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("[1, 2, 3]")]
    [InlineData("""{ "games": "nope" }""")]
    [InlineData("null")]
    [InlineData("""{ "games": [null] }""")]
    [InlineData("""{ "games": [{ "exePath": null }] }""")]
    [InlineData("""{ "games": [{ "displayName": "No path" }] }""")]
    public void A_corrupt_file_starts_empty_and_is_left_alone_until_the_next_save(string content)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, content);
        var store = new SettingsStore(FilePath);

        var result = store.Load();

        Assert.Equal(SettingsLoadStatus.Corrupt, result.Status);
        Assert.Empty(result.Settings.Games);
        Assert.Equal(content, File.ReadAllText(FilePath));

        result.Settings.Games.Add(new GameEntry { ExePath = @"C:\Games\a.exe" });
        store.Save(result.Settings);
        Assert.Single(store.Load().Settings.Games);
    }

    [Fact]
    public void Saving_creates_the_folder_and_leaves_no_temp_file_behind()
    {
        new SettingsStore(FilePath).Save(new AppSettings());

        Assert.Equal(["settings.json"], Directory.GetFiles(_folder).Select(Path.GetFileName));
    }
}
