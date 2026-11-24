using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VegasReborn.Core.Launch;
using VegasReborn.Core.Titles;

namespace VegasReborn.Core.Dedicated;

/// <summary>How one title's original dedicated server is prepared, started and checked.</summary>
public interface IDedicatedAdapter
{
    GameTitle Title { get; }

    /// <summary>The native module injected into the server, loaded via <see cref="AppPaths.NativeAdapter"/>.</summary>
    string ModuleName { get; }

    /// <summary>Copy-relative files (server executable included) that must match these SHA-256 values.</summary>
    IReadOnlyDictionary<string, string> PinnedFiles { get; }

    /// <summary>The server log the original writes in the copy, relative to it.</summary>
    string NativeLog { get; }

    /// <summary>Writes the server's own options files into the copy. The installation is only read.</summary>
    void PrepareCopy(string source, string game);

    /// <summary>The server's command-line arguments after the executable.</summary>
    IReadOnlyList<string> Arguments(DedicatedSettings settings, string? onlineAlias, bool localTest);

    /// <summary>Throws when the module's compat-status.json does not report a ready server.</summary>
    void ValidateReady(JsonObject status);
}

public static class DedicatedAdapters
{
    public static IDedicatedAdapter For(GameTitle title) => title.Key switch
    {
        "vegas1" => new Vegas1DedicatedAdapter(),
        _ => throw new DedicatedException($"Dedicated hosting is not available for {title.DisplayName} yet"),
    };
}

/// <summary>
/// Vegas 1's <c>R6VegasServer.exe TEXTSERVER</c>, as in <c>src/vegas_reborn/dedicated.py</c>.
/// </summary>
public sealed partial class Vegas1DedicatedAdapter : IDedicatedAdapter
{
    /// <summary>A separate options file keeps the shipped R6VegasServerConfig.ini as supplied.</summary>
    public const string OptionsName = "R6VegasDedicatedLAN";
    public const string ShippedOptions = "KellerGame/Config/PC/R6VegasServerConfig.ini";

    public GameTitle Title => TitleCatalog.Vegas1;

    public string ModuleName => "VegasDedicated-4.dll";

    public IReadOnlyDictionary<string, string> PinnedFiles { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [TitleCatalog.Vegas1.ServerExecutable!] = TitleCatalog.Vegas1.ServerSha256!,
        ["Binaries/PhysXLoader.dll"] = "313e6065f14a033147ee08d6ad3992d7511bca4bb43568d881101a0e0734cc16",
        ["Binaries/PhysXCore.dll"] = "9467d747076f0312682f572e654acdfc7dc65a72cf8d7be75bc97a064116621d",
    };

    public string NativeLog => "Binaries/Log_" + OptionsName + ".log";

    public void PrepareCopy(string source, string game)
    {
        var original = Path.Combine(source, ShippedOptions);
        string text;
        try
        {
            text = Encoding.Latin1.GetString(File.ReadAllBytes(original));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new DedicatedException("The installed game's server options file could not be read", error);
        }
        // PunkBuster's service no longer exists.
        text = PunkBusterLine().Replace(text, "m_bPunkBusterSv=False");
        PrivateFile.Write(Path.Combine(game, "KellerGame/Config/PC", OptionsName + ".ini"), Encoding.Latin1.GetBytes(text));
    }

    public IReadOnlyList<string> Arguments(DedicatedSettings settings, string? onlineAlias, bool localTest)
    {
        settings.Validate();
        var login = onlineAlias is null ? "AgO=0?AgU=?AgP=" : "AgO=1?AgU=" + RequireAlias(onlineAlias) + "?AgP=UseDiscord";
        // The original shipped TEXTSERVER template with PunkBuster off, the separate options
        // file and the configurable match options substituted.
        var url = settings.Map + "?" + login + "?SrvOptionFile=" + OptionsName
            + "?RG=0?RC=3?TBR=0?PB=0?GAME=" + settings.GameType
            + "?TKPM=5?FF=" + (settings.FriendlyFire ? "1" : "0")
            + "?TL=180?MP=" + settings.MaxPlayers.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "?AR=1?ML=3?TR=0?GPL=0?LT=0?TKP=1?TB=5?GD=2?PW=" + settings.Password;
        // Loopback is only for a local test; online hosting binds the routed interface.
        return localTest ? ["TEXTSERVER", url, "MULTIHOME=127.0.0.3"] : ["TEXTSERVER", url];
    }

    public void ValidateReady(JsonObject status)
    {
        if (!Flag(status, "ready") || status["nativeVersion"]?.GetValueKind() != System.Text.Json.JsonValueKind.Number
            || status["nativeVersion"]!.GetValue<int>() != 4
            || !Flag(status, "nativeStatisticsVerified") || !Flag(status, "nativeRankedHostVerified")
            || !Flag(status, "nativeSoloHuntVerified"))
        {
            throw new DedicatedException("The server's compatibility module did not verify its setup");
        }
    }

    private static bool Flag(JsonObject status, string name) =>
        status[name] is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.True;

    private static string RequireAlias(string alias) =>
        HostAlias().IsMatch(alias) ? alias : throw new DedicatedException("The account service returned an invalid host name");

    [GeneratedRegex(@"(?m)^m_bPunkBusterSv=[^\r\n]*")]
    private static partial Regex PunkBusterLine();

    [GeneratedRegex(@"\Ah[0-9a-f]{14}\z")]
    private static partial Regex HostAlias();
}
