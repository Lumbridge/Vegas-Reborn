namespace VegasReborn.Core.Launch;

/// <summary>
/// Vegas 1 per-map player limits in the copy's mission ini files
/// (KellerGame/Content/CookedPc/Maps/*/*.ini, read into R6MissionConfig's game mode list).
/// The original allows 4 players in Terrorist Hunt and 14 in adversarial matches hosted
/// from the game (16 only on dedicated servers). Every file is always rebuilt from the installed
/// original on each launch: only those exact shipped values change to 16, and when asked,
/// listed story missions also gain the versus modes from <see cref="MapModeExtension"/>.
/// </summary>
internal static class MissionPlayerLimits
{
    public const string MapsFolder = "KellerGame/Content/CookedPc/Maps";
    public const int Players = 16;
    private const string TerroristHunt = "GAMEMODE_TERROHUNT";
    private const string MaxPlayers = "m_iMaxPlayers";
    private const string ListenServer = "m_iMaxPlayersListenServer";

    /// <summary>
    /// Rebuild every mission ini under <paramref name="folder"/> in the copy from the original,
    /// writing the files whose text differs. Modes are added only for the Maps folder.
    /// </summary>
    public static void Apply(string copy, string source, bool extendModes, string folder = MapsFolder)
    {
        var maps = Path.Combine(source, folder);
        if (!Directory.Exists(maps))
        {
            return;
        }
        foreach (var original in Directory.EnumerateFiles(maps, "*.ini", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, original);
            var target = Path.Combine(copy, relative);
            if (!PrivateFile.IsWithin(target, copy) || !File.Exists(target))
            {
                continue;
            }
            var text = File.ReadAllText(original);
            var key = Path.GetRelativePath(maps, original).Replace('\\', '/');
            if (extendModes && folder == MapsFolder && MapModeExtension.OrderIds.TryGetValue(key, out var orderId))
            {
                text = MapModeExtension.Extend(text, orderId);
            }
            var expected = Raise(text) ?? text;
            if (File.ReadAllText(target) != expected)
            {
                PrivateFile.WriteText(target, expected);
            }
        }
    }

    /// <summary>
    /// Puts each story-mission ini listed in <see cref="MapModeExtension.OrderIds"/> back to the
    /// installed original, unchanged, where the copy has it and it differs. Used by the server
    /// copy when Extra Maps is not installed.
    /// </summary>
    public static void RestoreStoryModes(string copy, string source)
    {
        foreach (var key in MapModeExtension.OrderIds.Keys)
        {
            var relative = Path.Combine(MapsFolder, key);
            var original = Path.Combine(source, relative);
            var target = Path.Combine(copy, relative);
            if (!File.Exists(original) || !File.Exists(target) || !PrivateFile.IsWithin(target, copy))
            {
                continue;
            }
            var bytes = File.ReadAllBytes(original);
            if (!File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes))
            {
                PrivateFile.Write(target, bytes);
            }
        }
    }

    /// <summary>The raised text, or null when the file has no game mode limits to change.</summary>
    internal static string? Raise(string text)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var changed = false;
        var start = 0;
        for (var i = 0; i <= lines.Count; i++)
        {
            if (i == lines.Count || lines[i].TrimStart().StartsWith('['))
            {
                var inserted = RaiseSection(lines, start, i);
                changed |= inserted >= 0;
                i += Math.Max(inserted, 0);
                start = i;
            }
        }
        return changed ? string.Join(newline, lines) : null;
    }

    /// <summary>Lines inserted into a raised section, or -1 when the section is unchanged.</summary>
    private static int RaiseSection(List<string> lines, int start, int end)
    {
        if (end <= start || !lines[start].Trim().StartsWith("[GameMode", StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }
        string? mode = null;
        int max = -1, listen = -1;
        for (var i = start + 1; i < end; i++)
        {
            var (key, value) = Entry(lines[i]);
            if (key.Equals("m_eGameMode", StringComparison.OrdinalIgnoreCase))
            {
                mode = value;
            }
            else if (key.Equals(MaxPlayers, StringComparison.OrdinalIgnoreCase))
            {
                max = i;
            }
            else if (key.Equals(ListenServer, StringComparison.OrdinalIgnoreCase))
            {
                listen = i;
            }
        }
        if (string.Equals(mode, TerroristHunt, StringComparison.OrdinalIgnoreCase))
        {
            if (max < 0 || Entry(lines[max]).Value != "4" || (listen >= 0 && Entry(lines[listen]).Value != "4"))
            {
                return -1;
            }
            lines[max] = MaxPlayers + "=" + Players;
            if (listen >= 0)
            {
                lines[listen] = ListenServer + "=" + Players;
                return 0;
            }
            lines.Insert(max + 1, ListenServer + "=" + Players);
            return 1;
        }
        if (mode is not null && max >= 0 && listen >= 0 && Entry(lines[max]).Value == "16" && Entry(lines[listen]).Value == "14")
        {
            lines[listen] = ListenServer + "=" + Players;
            return 0;
        }
        return -1;
    }

    private static (string Key, string Value) Entry(string line)
    {
        var separator = line.IndexOf('=');
        return separator < 0 ? ("", "") : (line[..separator].Trim(), line[(separator + 1)..].Trim());
    }
}
