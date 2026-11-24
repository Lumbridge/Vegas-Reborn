using System.Text.Json.Nodes;
using VegasReborn.Core.Display;
using VegasReborn.Core.Settings;
using VegasReborn.Core.Tests.Launch;
using VegasReborn.Core.Titles;

namespace VegasReborn.Core.Tests.Settings;

public sealed class PreferencesTests
{
    private static string Json(Preferences preferences)
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "preferences.json");
        preferences.Save(path);
        return File.ReadAllText(path);
    }

    [Fact]
    public void NewPreferencesAndExplicitChoices()
    {
        var defaults = new Preferences();
        Assert.Equal(DisplayMode.Borderless, defaults.DisplayMode);
        Assert.True(defaults.SkipIntros);
        Assert.Equal(@"E:\SteamLibrary\steamapps\common\Rainbow Six Vegas", defaults.GameFolder);
        using var directory = new TestDirectory();
        var path = directory.File("preferences.json", "{\"display_mode\":\"Windowed\",\"skip_intros\":false}");
        var saved = Preferences.Load(path);
        Assert.Equal(DisplayMode.Windowed, saved.DisplayMode);
        Assert.False(saved.SkipIntros);
        File.WriteAllText(path, "{}");
        Assert.True(Preferences.Load(path).SkipIntros);
    }

    [Fact]
    public void RoundtripAndCorruptFallback()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        var preferences = new Preferences { GameFolder = "D:/Owned Vegas", DisplayMode = DisplayMode.Windowed, MonitorDevice = "other", MinimizeOnPlay = false };
        preferences.Save(path);
        Assert.Equal(Json(preferences), Json(Preferences.Load(path)));
        foreach (var value in new[] { "{\"display_mode\":\"invalid\"}", "{\"minimize_on_play\":\"false\"}", "{\"skip_intros\":\"false\"}", "null", "[]", "{" })
        {
            File.WriteAllText(path, value);
            Assert.Equal(Json(new Preferences()), Json(Preferences.Load(path)));
        }
        Assert.Equal(Json(new Preferences()), Json(Preferences.Load(Path.Combine(directory.Path, "missing.json"))));
    }

    [Fact]
    public void FileKeepsPythonFieldNamesAndAddsTitles()
    {
        var preferences = new Preferences { SelectedTitle = "vegas2" };
        preferences.SetGameFolder(TitleCatalog.Vegas2, @"D:\Games\Vegas 2");
        var value = JsonNode.Parse(Json(preferences))!.AsObject();
        foreach (var key in new[] { "game_folder", "display_mode", "monitor_device", "minimize_on_play", "discord_presence", "check_updates", "skip_intros" })
        {
            Assert.True(value.ContainsKey(key), key);
        }
        Assert.Equal("Borderless", (string)value["display_mode"]!);
        Assert.Equal(@"D:\Games\Vegas 2", (string)value["titles"]!["vegas2"]!["game_folder"]!);
        Assert.Null(value["titles"]!["vegas1"]?["game_folder"]);
        Assert.EndsWith("\n", Json(preferences));
    }

    [Fact]
    public void FieldOfViewPerTitle()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "preferences.json");
        var preferences = new Preferences();
        Assert.Equal(0, preferences.FieldOfViewFor(TitleCatalog.Vegas1));
        Assert.Equal(0, preferences.LaunchRequestFor(TitleCatalog.Vegas2).FieldOfView);
        preferences.SetFieldOfView(TitleCatalog.Vegas1, 75);
        preferences.Save(path);
        var titles = JsonNode.Parse(File.ReadAllText(path))!["titles"]!.AsObject();
        Assert.Equal(75, titles["vegas1"]!["field_of_view"]!.GetValue<int>());
        Assert.False(titles.ContainsKey("vegas2"));
        var loaded = Preferences.Load(path);
        Assert.Equal(75, loaded.FieldOfViewFor(TitleCatalog.Vegas1));
        Assert.Equal(0, loaded.FieldOfViewFor(TitleCatalog.Vegas2));
        Assert.Equal(75, loaded.LaunchRequestFor(TitleCatalog.Vegas1).FieldOfView);
        loaded.SetFieldOfView(TitleCatalog.Vegas1, 0);
        Assert.Equal(0, loaded.FieldOfViewFor(TitleCatalog.Vegas1));
        Assert.Throws<ArgumentOutOfRangeException>(() => loaded.SetFieldOfView(TitleCatalog.Vegas2, 120));
        // Unsupported stored values keep the game's default without resetting other settings.
        foreach (var value in new[] { "120", "\"75\"", "75.5", "true" })
        {
            File.WriteAllText(path, "{\"skip_intros\":false,\"titles\":{\"vegas2\":{\"field_of_view\":" + value + "}}}");
            var stored = Preferences.Load(path);
            Assert.Equal(0, stored.FieldOfViewFor(TitleCatalog.Vegas2));
            Assert.False(stored.SkipIntros);
        }
    }

    [Fact]
    public void GameFolderPerTitle()
    {
        var preferences = new Preferences();
        Assert.Equal(@"E:\SteamLibrary\steamapps\common\Rainbow Six Vegas 2", preferences.GameFolderFor(TitleCatalog.Vegas2));
        preferences.SetGameFolder(TitleCatalog.Vegas1, "C:/One");
        preferences.SetGameFolder(TitleCatalog.Vegas2, "C:/Two");
        Assert.Equal("C:/One", preferences.GameFolder);
        Assert.Equal("C:/Two", preferences.GameFolderFor(TitleCatalog.Vegas2));
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "p.json");
        preferences.Save(path);
        var loaded = Preferences.Load(path);
        Assert.Equal("C:/One", loaded.GameFolderFor(TitleCatalog.Vegas1));
        Assert.Equal("C:/Two", loaded.GameFolderFor(TitleCatalog.Vegas2));
    }

    [Fact]
    public void EveryFixIsAlwaysOnAndCannotBeDisabled()
    {
        var vegas1 = TitleCatalog.Vegas1;
        var preferences = new Preferences();
        Assert.All(TitleCatalog.All.SelectMany(title => title.Fixes), fix => Assert.Equal(FixKind.AlwaysOn, fix.Kind));
        Assert.All(vegas1.Fixes, fix => Assert.True(preferences.IsFixEnabled(vegas1, fix.Id)));
        Assert.Empty(preferences.DisabledFixes(vegas1));
        Assert.Throws<InvalidOperationException>(() => preferences.SetFixEnabled(vegas1, TitleCatalog.SoloHunt, false));
        Assert.Throws<InvalidOperationException>(() => preferences.SetFixEnabled(vegas1, TitleCatalog.OnlineRoute, false));
        Assert.Throws<ArgumentException>(() => preferences.SetFixEnabled(vegas1, "not-a-fix", false));
        Assert.True(preferences.IsFixEnabled(TitleCatalog.Vegas2, TitleCatalog.OcclusionQueries));
        Assert.False(preferences.IsFixEnabled(TitleCatalog.Vegas2, TitleCatalog.SoloHunt));
        var request = preferences.LaunchRequestFor(vegas1);
        Assert.Empty(request.DisabledFixes);
        Assert.Equal(preferences.GameFolder, request.GameFolder);
    }

    [Fact]
    public void StoredStatesForAlwaysOnUnknownOrInvalidValuesAreIgnored()
    {
        using var directory = new TestDirectory();
        var path = directory.File("p.json",
            "{\"display_mode\":\"Fullscreen\",\"selected_title\":\"vegas9\",\"titles\":{\"vegas1\":{\"fixes\":{\"online-route\":false,\"future-fix\":false,\"report-xp\":\"no\",\"player-rank\":false}},\"vegas2\":[]}}");
        var loaded = Preferences.Load(path);
        Assert.Equal(DisplayMode.Fullscreen, loaded.DisplayMode);
        Assert.Equal("vegas1", loaded.SelectedTitle);
        Assert.True(loaded.IsFixEnabled(TitleCatalog.Vegas1, TitleCatalog.OnlineRoute));
        Assert.True(loaded.IsFixEnabled(TitleCatalog.Vegas1, TitleCatalog.ReportXp));
        // A 0.4.x "off" for a fix that is now always on is ignored and not saved again.
        Assert.True(loaded.IsFixEnabled(TitleCatalog.Vegas1, TitleCatalog.PlayerRank));
        var saved = JsonNode.Parse(Json(loaded))!["titles"]?["vegas1"]?["fixes"] as JsonObject;
        Assert.True(saved is null || saved.Count == 0);
    }
}
