using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VegasReborn.Core.Launch;
using VegasReborn.Core.Titles;

namespace VegasReborn.Core.Dedicated;

/// <summary>
/// A dedicated server's <c>server.json</c>. Only the match options below are configurable;
/// every other server URL option comes from the original shipped command template.
/// </summary>
public sealed partial record DedicatedSettings
{
    public const int Version = 1;

    public required GameTitle Title { get; init; }

    /// <summary>The installed game folder. It is only read.</summary>
    public required string GameFolder { get; init; }

    public string Map { get; init; } = "MP_Streets_01";

    public string GameType { get; init; } = "R6Game.R6TeamDeathmatchGame";

    public int MaxPlayers { get; init; } = 16;

    public bool FriendlyFire { get; init; }

    /// <summary>Empty for an open server.</summary>
    public string Password { get; init; } = "";

    /// <summary>Parses and validates server.json. Messages are plain and name the field.</summary>
    public static DedicatedSettings Parse(ReadOnlySpan<byte> json)
    {
        JsonObject value;
        try
        {
            value = JsonNode.Parse(json) as JsonObject ?? throw new DedicatedException("server.json must contain a JSON object");
        }
        catch (JsonException)
        {
            throw new DedicatedException("server.json is not valid JSON");
        }
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            "version", "game", "gameFolder", "map", "gameType", "maxPlayers", "friendlyFire", "password",
        };
        foreach (var (name, _) in value)
        {
            if (!known.Contains(name))
            {
                throw new DedicatedException($"server.json has an unknown setting: {name}");
            }
        }
        if (Integer(value, "version") != Version)
        {
            throw new DedicatedException($"server.json version must be {Version}");
        }
        var key = Text(value, "game") ?? throw new DedicatedException("server.json must set game to vegas1 or vegas2");
        var title = TitleCatalog.All.FirstOrDefault(item => item.Key == key)
            ?? throw new DedicatedException("server.json must set game to vegas1 or vegas2");
        var folder = Text(value, "gameFolder");
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
        {
            throw new DedicatedException("server.json must set gameFolder to the full path of the installed game");
        }
        var settings = new DedicatedSettings { Title = title, GameFolder = folder };
        if (value.ContainsKey("map"))
        {
            settings = settings with { Map = Text(value, "map") ?? "" };
        }
        if (value.ContainsKey("gameType"))
        {
            settings = settings with { GameType = Text(value, "gameType") ?? "" };
        }
        if (value.ContainsKey("maxPlayers"))
        {
            settings = settings with { MaxPlayers = Integer(value, "maxPlayers") ?? 0 };
        }
        if (value.ContainsKey("friendlyFire"))
        {
            settings = settings with
            {
                FriendlyFire = value["friendlyFire"] is JsonValue flag && flag.GetValueKind() is JsonValueKind.True or JsonValueKind.False
                    ? flag.GetValue<bool>()
                    : throw new DedicatedException("server.json friendlyFire must be true or false"),
            };
        }
        if (value.ContainsKey("password"))
        {
            settings = settings with { Password = Text(value, "password") ?? throw new DedicatedException("server.json password must be text") };
        }
        settings.Validate();
        return settings;
    }

    public static DedicatedSettings Load(string path)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new DedicatedException($"No server settings at {path}. Run VegasServer init first.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new DedicatedException($"Could not read {path}");
        }
        return Parse(data);
    }

    /// <summary>The default server.json written by <c>init</c>.</summary>
    public static string Template(GameTitle title, string gameFolder)
    {
        var value = new JsonObject
        {
            ["version"] = Version,
            ["game"] = title.Key,
            ["gameFolder"] = Path.GetFullPath(gameFolder),
            ["map"] = "MP_Streets_01",
            ["gameType"] = "R6Game.R6TeamDeathmatchGame",
            ["maxPlayers"] = 16,
            ["friendlyFire"] = false,
            ["password"] = "",
        };
        return value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    /// <summary>
    /// Values go into the original server URL, where '?' and '=' separate options, so each
    /// is limited to characters that cannot start another option.
    /// </summary>
    public void Validate()
    {
        if (!MapPattern().IsMatch(Map))
        {
            throw new DedicatedException("map must be a map name such as MP_Streets_01 (letters, digits and _)");
        }
        if (!GameTypePattern().IsMatch(GameType))
        {
            throw new DedicatedException("gameType must be a game class such as R6Game.R6TeamDeathmatchGame");
        }
        if (MaxPlayers is < 2 or > 16)
        {
            throw new DedicatedException("maxPlayers must be between 2 and 16");
        }
        if (!PasswordPattern().IsMatch(Password))
        {
            throw new DedicatedException("password may only use letters and digits, up to 16 characters");
        }
    }

    private static string? Text(JsonObject value, string name) =>
        value[name] is JsonValue item && item.GetValueKind() == JsonValueKind.String ? item.GetValue<string>() : null;

    private static int? Integer(JsonObject value, string name)
    {
        if (value[name] is JsonValue item && item.GetValueKind() == JsonValueKind.Number && item.TryGetValue(out int number))
        {
            return number;
        }
        if (value.ContainsKey(name))
        {
            throw new DedicatedException($"server.json {name} must be a whole number");
        }
        return null;
    }

    [GeneratedRegex(@"\A[A-Za-z0-9_]{1,64}\z")]
    private static partial Regex MapPattern();

    [GeneratedRegex(@"\A[A-Za-z][A-Za-z0-9_]{0,31}\.[A-Za-z][A-Za-z0-9_]{0,63}\z")]
    private static partial Regex GameTypePattern();

    [GeneratedRegex(@"\A[A-Za-z0-9]{0,16}\z")]
    private static partial Regex PasswordPattern();
}

/// <summary>A dedicated hosting failure whose message is plain user-facing text without secrets.</summary>
public class DedicatedException : LaunchException
{
    public DedicatedException(string message) : base(message)
    {
    }

    public DedicatedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
