using System.Text.Json;
using System.Text.Json.Nodes;
using VegasReborn.Core.Display;
using VegasReborn.Core.Launch;
using VegasReborn.Core.Titles;

namespace VegasReborn.Core.Settings;

/// <summary>
/// Local preferences (local/companion/preferences.json), separate from account credentials and
/// cloud saves. The file and the 0.4.x field names are shared with the Python companion, which
/// ignores the added <c>selected_title</c> and <c>titles</c> fields.
/// </summary>
/// <remarks>
/// <c>game_folder</c> stays the Rainbow Six Vegas folder. Other titles' folders and every
/// title's fix switches are under <c>titles.&lt;key&gt;</c>:
/// <c>{"game_folder": "...", "fixes": {"report-xp": false}}</c>. A missing or unknown fix
/// state means the fix's default; AlwaysOn fixes are never stored and always enabled.
/// </remarks>
public sealed class Preferences
{
    private readonly Dictionary<string, string> _gameFolders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, bool>> _fixes = new(StringComparer.Ordinal);

    public string GameFolder { get; set; } = DefaultFolder(TitleCatalog.Vegas1);
    public DisplayMode DisplayMode { get; set; } = DisplayMode.Borderless;
    public string MonitorDevice { get; set; } = "";
    public bool MinimizeOnPlay { get; set; } = true;
    public bool DiscordPresence { get; set; } = true;
    public bool CheckUpdates { get; set; } = true;
    public bool SkipIntros { get; set; } = true;
    /// <summary>The title last chosen in the companion (a <see cref="GameTitle.Key"/>).</summary>
    public string SelectedTitle { get; set; } = TitleCatalog.Vegas1.Key;

    public GameTitle SelectedGame => TitleCatalog.All.FirstOrDefault(title => title.Key == SelectedTitle) ?? TitleCatalog.Vegas1;

    /// <summary>Windows form of the title's default Steam folder (as Python's str(Path(...))).</summary>
    public static string DefaultFolder(GameTitle title) => title.DefaultInstallFolder.Replace('/', '\\');

    public string GameFolderFor(GameTitle title) => title.Key == TitleCatalog.Vegas1.Key
        ? GameFolder
        : _gameFolders.GetValueOrDefault(title.Key) ?? DefaultFolder(title);

    public void SetGameFolder(GameTitle title, string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (title.Key == TitleCatalog.Vegas1.Key)
        {
            GameFolder = folder;
        }
        else
        {
            _gameFolders[title.Key] = folder;
        }
    }

    /// <summary>Whether a fix is on. AlwaysOn fixes are always on; unknown ids are off.</summary>
    public bool IsFixEnabled(GameTitle title, string fixId)
    {
        var fix = title.Fix(fixId);
        if (fix is null)
        {
            return false;
        }
        if (fix.Kind == FixKind.AlwaysOn)
        {
            return true;
        }
        return _fixes.TryGetValue(title.Key, out var states) && states.TryGetValue(fixId, out var enabled)
            ? enabled
            : fix.DefaultEnabled;
    }

    /// <summary>Turn a Toggle fix on or off. Throws for AlwaysOn or unknown fixes.</summary>
    public void SetFixEnabled(GameTitle title, string fixId, bool enabled)
    {
        var fix = title.Fix(fixId) ?? throw new ArgumentException($"Unknown fix '{fixId}' for {title.DisplayName}", nameof(fixId));
        if (fix.Kind != FixKind.Toggle)
        {
            throw new InvalidOperationException($"{fix.Title} is always on");
        }
        if (!_fixes.TryGetValue(title.Key, out var states))
        {
            _fixes[title.Key] = states = new Dictionary<string, bool>(StringComparer.Ordinal);
        }
        states[fixId] = enabled;
    }

    /// <summary>Toggle fix ids that are off for a title (for <see cref="LaunchRequest.DisabledFixes"/>).</summary>
    public IReadOnlySet<string> DisabledFixes(GameTitle title) =>
        title.Fixes.Where(fix => fix.Kind == FixKind.Toggle && !IsFixEnabled(title, fix.Id)).Select(fix => fix.Id).ToHashSet(StringComparer.Ordinal);

    /// <summary>A launch request for a title from these preferences.</summary>
    public LaunchRequest LaunchRequestFor(GameTitle title, Action? onStarted = null) => new()
    {
        Title = title,
        GameFolder = GameFolderFor(title),
        DisplayMode = DisplayMode,
        MonitorDevice = MonitorDevice,
        SkipIntros = SkipIntros,
        DisabledFixes = DisabledFixes(title),
        OnStarted = onStarted,
    };

    public static Preferences Load(AppPaths paths) => Load(paths.Preferences);

    /// <summary>Loads preferences; any invalid 0.4.x field (or unreadable file) gives the defaults.</summary>
    public static Preferences Load(string path)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllBytes(path)) is not JsonObject value)
            {
                return new Preferences();
            }
            var result = new Preferences();
            if (!ReadString(value, "game_folder", folder => result.GameFolder = folder)
                || !ReadString(value, "display_mode", mode =>
                {
                    if (!DisplayModes.TryParse(mode, out var parsed))
                    {
                        throw new FormatException();
                    }
                    result.DisplayMode = parsed;
                })
                || !ReadString(value, "monitor_device", device => result.MonitorDevice = device)
                || !ReadBool(value, "minimize_on_play", flag => result.MinimizeOnPlay = flag)
                || !ReadBool(value, "discord_presence", flag => result.DiscordPresence = flag)
                || !ReadBool(value, "check_updates", flag => result.CheckUpdates = flag)
                || !ReadBool(value, "skip_intros", flag => result.SkipIntros = flag))
            {
                return new Preferences();
            }
            // Added fields are read leniently so a newer file never resets older settings.
            if (value["selected_title"] is JsonValue selected && selected.TryGetValue<string>(out var key)
                && TitleCatalog.All.Any(title => title.Key == key))
            {
                result.SelectedTitle = key;
            }
            if (value["titles"] is JsonObject titles)
            {
                foreach (var title in TitleCatalog.All)
                {
                    if (titles[title.Key] is not JsonObject entry)
                    {
                        continue;
                    }
                    if (title.Key != TitleCatalog.Vegas1.Key && entry["game_folder"] is JsonValue folder
                        && folder.TryGetValue<string>(out var path2))
                    {
                        result._gameFolders[title.Key] = path2;
                    }
                    if (entry["fixes"] is JsonObject fixes)
                    {
                        foreach (var (id, state) in fixes)
                        {
                            if (title.Fix(id)?.Kind == FixKind.Toggle && state is JsonValue flag
                                && flag.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
                            {
                                result.SetFixEnabled(title, id, flag.GetValue<bool>());
                            }
                        }
                    }
                }
            }
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException)
        {
            return new Preferences();
        }
    }

    public void Save(AppPaths paths) => Save(paths.Preferences);

    public void Save(string path)
    {
        var titles = new JsonObject();
        foreach (var title in TitleCatalog.All)
        {
            var entry = new JsonObject();
            if (title.Key != TitleCatalog.Vegas1.Key && _gameFolders.TryGetValue(title.Key, out var folder))
            {
                entry["game_folder"] = folder;
            }
            if (_fixes.TryGetValue(title.Key, out var states) && states.Count > 0)
            {
                var fixes = new JsonObject();
                foreach (var (id, enabled) in states.OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    fixes[id] = enabled;
                }
                entry["fixes"] = fixes;
            }
            if (entry.Count > 0)
            {
                titles[title.Key] = entry;
            }
        }
        var value = new JsonObject
        {
            ["game_folder"] = GameFolder,
            ["display_mode"] = DisplayModes.Name(DisplayMode),
            ["monitor_device"] = MonitorDevice,
            ["minimize_on_play"] = MinimizeOnPlay,
            ["discord_presence"] = DiscordPresence,
            ["check_updates"] = CheckUpdates,
            ["skip_intros"] = SkipIntros,
            ["selected_title"] = SelectedTitle,
            ["titles"] = titles,
        };
        var text = value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        PrivateFile.WriteText(path, text);
    }

    private static bool ReadString(JsonObject value, string name, Action<string> apply)
    {
        if (!value.TryGetPropertyValue(name, out var node))
        {
            return true;
        }
        if (node is not JsonValue item || item.GetValueKind() != JsonValueKind.String)
        {
            return false;
        }
        apply(item.GetValue<string>());
        return true;
    }

    private static bool ReadBool(JsonObject value, string name, Action<bool> apply)
    {
        if (!value.TryGetPropertyValue(name, out var node))
        {
            return true;
        }
        if (node is not JsonValue item || item.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }
        apply(item.GetValue<bool>());
        return true;
    }
}
