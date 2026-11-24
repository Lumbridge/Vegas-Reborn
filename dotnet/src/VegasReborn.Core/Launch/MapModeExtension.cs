namespace VegasReborn.Core.Launch;

/// <summary>
/// Adds Terrorist Hunt, Deathmatch, Team Deathmatch and Attack and Defend game modes to
/// story-mission ini files in the launcher's private game copy.
/// </summary>
internal static class MapModeExtension
{
    /// <summary>Mission order id per file, keyed by forward-slash path relative to the Maps folder.</summary>
    public static readonly IReadOnlyDictionary<string, int> OrderIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["01_Mexico/01_Mexico_04.ini"] = 30,
        ["01_Mexico/01_Mexico_05.ini"] = 31,
        ["01_Mexico/01_Mexico_06.ini"] = 32,
        ["02_Casino/02_Casino_06.ini"] = 35,
        ["03_Fremont/03_Fremont_02.ini"] = 36,
        ["03_Fremont/03_Fremont_03B.ini"] = 37,
        ["04_Tower/04_Tower_02.ini"] = 38,
        ["05_Dantes/05_Dantes_04.ini"] = 30,
        ["05_Dantes/05_Dantes_06.ini"] = 39,
        ["06_HooverDam/06_Dam_02.ini"] = 40,
        ["06_HooverDam/06_Dam_04.ini"] = 30,
        ["06_HooverDam/06_Dam_08.ini"] = 41,
        ["99_HeliRides/99_HeliRides_01In.ini"] = 30,
        ["99_HeliRides/99_HeliRides_02InNear.ini"] = 42,
        ["99_HeliRides/99_HeliRides_03InNear.ini"] = 43,
        ["99_HeliRides/99_HeliRides_04InNear.ini"] = 44,
        ["99_HeliRides/99_HeliRides_05InNear.ini"] = 45,
        ["99_HeliRides/99_HeliRides_06In.ini"] = 46,
    };

    private static readonly (string Mode, string? MissionType)[] Modes =
    [
        ("GAMEMODE_TERROHUNT", null),
        ("GAMEMODE_DEATHMATCH", null),
        ("GAMEMODE_TEAMDEATHMATCH", null),
        ("GAMEMODE_ATTACKANDDEFEND", "R6ObjectiveExtractionItem"),
    ];

    /// <summary>
    /// The text with four game mode sections added after the last [GameModeN] section.
    /// Returns the text unchanged when it has no game mode section.
    /// </summary>
    internal static string Extend(string text, int orderId)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var last = -1;
        var highest = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var header = lines[i].Trim();
            if (header.StartsWith("[GameMode", StringComparison.OrdinalIgnoreCase) && header.EndsWith(']')
                && int.TryParse(header["[GameMode".Length..^1], out var number) && number >= highest)
            {
                highest = number;
                last = i;
            }
        }
        if (last < 0)
        {
            return text;
        }
        var end = last + 1;
        while (end < lines.Count && !lines[end].TrimStart().StartsWith('['))
        {
            end++;
        }
        while (end - 1 > last && string.IsNullOrWhiteSpace(lines[end - 1]))
        {
            end--;
        }
        var added = new List<string>();
        for (var i = 0; i < Modes.Length; i++)
        {
            added.Add("");
            added.Add($"[GameMode{highest + 1 + i}]");
            added.Add("m_eGameMode=" + Modes[i].Mode);
            if (Modes[i].MissionType is { } type)
            {
                added.Add("m_szMissionType=" + type);
            }
            added.Add("m_iMissionOrderId=" + orderId);
            added.Add("m_iMaxPlayers=16");
            added.Add("m_iMaxPlayersListenServer=16");
        }
        lines.InsertRange(end, added);
        return string.Join(newline, lines);
    }
}
