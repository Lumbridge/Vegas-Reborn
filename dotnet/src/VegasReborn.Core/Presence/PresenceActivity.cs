using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using VegasReborn.Core.Titles;

namespace VegasReborn.Core.Presence;

/// <summary>
/// The Rich Presence activity the companion shares. No join secrets or party data: a
/// retail join path is not verified. One link button lets friends get the launcher.
/// </summary>
public sealed record PresenceActivity(string Details, string State, long? StartTimestamp = null)
{
    /// <summary>The label of the presence button that links to the launcher download.</summary>
    public const string DownloadLabel = "Get Vegas Reborn";

    /// <summary>The launcher's latest release page.</summary>
    public const string DownloadUrl = "https://github.com/Lumbridge/Vegas-Reborn/releases/latest";

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Activity for the companion's state. <paramref name="title"/> is the game being played
    /// (Rainbow Six Vegas when not given). <paramref name="started"/> is Unix seconds.
    /// </summary>
    public static PresenceActivity For(
        GameTitle? title = null,
        bool playing = false,
        bool hosting = false,
        bool preparingHost = false,
        long? started = null)
    {
        title ??= TitleCatalog.Vegas1;
        var details = playing ? "Original PC game" : "Vegas Reborn Launcher";
        var state = playing ? "Playing " + title.DisplayName : "Ready to play";
        if (hosting)
        {
            state = playing ? "Playing · Dedicated server running" : "Dedicated server running";
        }
        else if (preparingHost)
        {
            state = playing ? "Playing · Preparing dedicated server" : "Preparing dedicated server";
        }
        return new PresenceActivity(details, state, playing && started is > 0 ? started : null);
    }

    /// <summary>The Discord SET_ACTIVITY activity object, compact, non-ASCII unescaped.</summary>
    public string ToJson()
    {
        var value = new JsonObject { ["details"] = Details, ["state"] = State };
        if (StartTimestamp is { } start)
        {
            value["timestamps"] = new JsonObject { ["start"] = start };
        }
        value["buttons"] = new JsonArray(new JsonObject { ["label"] = DownloadLabel, ["url"] = DownloadUrl });
        return value.ToJsonString(Compact);
    }
}
