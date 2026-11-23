using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace VegasReborn.Core.Profiles.Vegas1;

/// <summary>Unsupported source identity or a malformed original-data contract.</summary>
public sealed class RankSourceException : Exception
{
    public RankSourceException(string code)
        : base(code)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <param name="Threshold">Lowest raw XP for this record.</param>
/// <param name="RecordId">Original record id.</param>
/// <param name="Ordinal">Rank label index (0 recruit .. 17 elite).</param>
public readonly record struct RankRecord(uint Threshold, uint RecordId, uint Ordinal);

/// <summary>An earned rank for a raw XP value.</summary>
public sealed record RetailRank(
    uint Experience,
    int Index,
    uint Ordinal,
    uint RecordId,
    int Subrank,
    string LabelKey,
    string Label,
    uint Threshold,
    uint? NextThreshold,
    uint? ExperienceToNext,
    double? ProgressToNext)
{
    /// <summary>The companion's rank text, for example "Private First Class  /  1/5\n1,100 XP  ·  400 XP to the next step".</summary>
    public string DisplayText
    {
        get
        {
            var title = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Label.ToLowerInvariant());
            var next = ExperienceToNext is { } remaining
                ? remaining.ToString("N0", CultureInfo.InvariantCulture) + " XP to the next step"
                : "Maximum rank";
            return $"{title}  /  {Subrank}/5\n{Experience.ToString("N0", CultureInfo.InvariantCulture)} XP  ·  {next}";
        }
    }
}

/// <summary>
/// Rainbow Six Vegas (1) earned rank, read only, from the original PEC archive and the
/// shipped rank carrier. Thresholds are decoded from <c>KellerGame/pec/PECInfoFile.bin</c>
/// (hash-pinned), never reconstructed or hardcoded; labels come from the English
/// <c>R6Menus.int</c> [Pec] section (hash-pinned). Original server loader 10e6ac40 →
/// deserializer 10e691a0 checks float version 1.2, then reads defaults 10e6c130 and rank
/// array 10e6c5c0; record reader 10e6c370 copies threshold/id/ordinal in that order.
/// Client lookup 10e6d620 selects the last threshold &lt;= unsigned XP. Ordinal getter
/// 10e6d6c0, subrank counter (server 10e6c4b0) and PC service-record UI 109c81a0 establish
/// the label enum and five-pip display. Not valid for Vegas 2.
/// </summary>
public sealed class RankTable
{
    public const string PecInfoSha256 = "ce019057071ea0e7ce8dc61b6d2f0710fd0a55ce0f89ea61b9d968bd1fba6194";
    public const string EnglishMenusSha256 = "73fe8e0a89ecb635ff752a66ae6b6570cc1d107d1438c49f4e86244374cbc892";
    public const int PecInfoBytes = 13652;
    public const string PecInfoPath = "KellerGame/pec/PECInfoFile.bin";
    public const string EnglishMenusPath = "KellerGame/Localization/INT/R6Menus.int";
    private const int MaxMenusBytes = 1024 * 1024;

    // Messages the companion shows on its rank card.
    public const string SignInMessage = "Sign in to see your saved rank and XP.";
    public const string SyncFirstMessage = "Sync your character to see your saved rank.";
    public const string UnavailableMessage = "Rank unavailable. Check your game folder and saved character.";

    /// <summary>Exact ordinal switch in original PC UI 109c81a0, not inferred military order.</summary>
    public static IReadOnlyList<string> RankLabelKeys { get; } =
    [
        "RankRecruit", "RankPrivate2ndclass", "RankPrivate1stclass", "RankSpecialist",
        "RankCorporal", "RankSergeant", "RankStaffSergeant", "RankSergeant1stClass",
        "RankMasterSergeant", "Rank1stSergeant", "RankSergeantMajor", "Rank2ndLieutenant",
        "Rank1stLieutenant", "RankCaptain", "RankMajor", "RankLieutenantColonel",
        "RankColonel", "RankElite",
    ];

    private readonly uint[] _thresholds;

    private RankTable(IReadOnlyList<RankRecord> records, IReadOnlyList<string> labels)
    {
        Records = records;
        Labels = labels;
        _thresholds = [.. records.Select(row => row.Threshold)];
    }

    public IReadOnlyList<RankRecord> Records { get; }

    public IReadOnlyList<string> Labels { get; }

    public RetailRank RankForExperience(uint experience)
    {
        // bisect_right - 1: the last record whose threshold <= experience.
        var index = Array.BinarySearch(_thresholds, experience);
        index = index >= 0 ? index : ~index - 1;
        while (index + 1 < _thresholds.Length && _thresholds[index + 1] <= experience)
        {
            index++;
        }
        if (index < 0)
        {
            throw new RankSourceException("rank_table_has_no_initial_threshold");
        }
        var row = Records[index];
        var subrank = 0;
        for (var prior = 0; prior < index; prior++)
        {
            if (Records[prior].Ordinal == row.Ordinal)
            {
                subrank++;
            }
        }
        // Native progress reads the next record. At maximum use an explicit absent-next
        // value rather than copy the original out-of-range access.
        uint? following = index + 1 < Records.Count ? Records[index + 1].Threshold : null;
        double? progress = following is { } next ? (double)(experience - row.Threshold) / (next - row.Threshold) : null;
        return new RetailRank(experience, index, row.Ordinal, row.RecordId, subrank,
            RankLabelKeys[(int)row.Ordinal], Labels[(int)row.Ordinal], row.Threshold,
            following, following is { } n ? n - experience : null, progress);
    }

    /// <summary>Accepts only the inspected original binary and English localization.</summary>
    public static RankTable Parse(ReadOnlySpan<byte> carrier, ReadOnlySpan<byte> englishMenus)
    {
        if (carrier.Length != PecInfoBytes || Convert.ToHexStringLower(SHA256.HashData(carrier)) != PecInfoSha256)
        {
            throw new RankSourceException("unsupported_original_pec_info");
        }
        if (BinaryPrimitives.ReadSingleLittleEndian(carrier) != 1.2f)
        {
            throw new RankSourceException("unsupported_original_pec_info_version");
        }
        var defaultCount = BinaryPrimitives.ReadUInt32LittleEndian(carrier[4..]);
        if (defaultCount != 12)
        {
            throw new RankSourceException("unsupported_original_pec_defaults");
        }
        var rankCountOffset = 8 + (int)defaultCount * 8;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(carrier[rankCountOffset..]);
        if (count != 102)
        {
            throw new RankSourceException("unsupported_original_rank_count");
        }
        var records = new RankRecord[count];
        for (var i = 0; i < records.Length; i++)
        {
            var at = carrier[(rankCountOffset + 4 + 12 * i)..];
            records[i] = new RankRecord(BinaryPrimitives.ReadUInt32LittleEndian(at),
                BinaryPrimitives.ReadUInt32LittleEndian(at[4..]), BinaryPrimitives.ReadUInt32LittleEndian(at[8..]));
        }
        if (records[0].Threshold != 0 || records.Any(row => row.Ordinal >= RankLabelKeys.Count)
            || records.Zip(records.Skip(1)).Any(pair => pair.First.Threshold >= pair.Second.Threshold))
        {
            throw new RankSourceException("invalid_original_rank_records");
        }
        if (Convert.ToHexStringLower(SHA256.HashData(englishMenus)) != EnglishMenusSha256)
        {
            throw new RankSourceException("unsupported_original_english_labels");
        }
        var section = IniSection.Read(Encoding.Latin1.GetString(englishMenus), "Pec")
            ?? throw new RankSourceException("invalid_original_english_labels");
        var labels = new string[RankLabelKeys.Count];
        for (var i = 0; i < labels.Length; i++)
        {
            if (!section.TryGetValue(RankLabelKeys[i], out var label))
            {
                throw new RankSourceException("invalid_original_english_labels");
            }
            labels[i] = label.Trim();
            if (labels[i].Length == 0)
            {
                throw new RankSourceException("missing_original_rank_label");
            }
        }
        return new RankTable(records, labels);
    }

    /// <summary>Loads from a Vegas 1 installation or isolated reference; writes nothing.</summary>
    public static RankTable Load(string gameRoot)
    {
        var carrier = ReadPrefix(Path.Combine(gameRoot, PecInfoPath), PecInfoBytes + 1);
        var menus = ReadPrefix(Path.Combine(gameRoot, EnglishMenusPath), MaxMenusBytes + 1);
        return Parse(carrier, menus);
    }

    /// <summary>Validates the PEC archive's CRC and version, then derives its earned raw-XP rank.</summary>
    public static RetailRank ReadRank(string pecFile, RankTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return table.RankForExperience(PecArchive.ReadXp(ReadPrefix(pecFile, PecArchive.MaxBytes + 1)));
    }

    /// <inheritdoc cref="ReadRank(string, RankTable)"/>
    public static RetailRank ReadRank(string pecFile, string gameRoot)
    {
        var experience = PecArchive.ReadXp(ReadPrefix(pecFile, PecArchive.MaxBytes + 1));
        return Load(gameRoot).RankForExperience(experience);
    }

    /// <summary>
    /// The companion's rank card text for a saved character: the rank, or
    /// <see cref="SyncFirstMessage"/> when files are missing, or <see cref="UnavailableMessage"/>.
    /// </summary>
    public static string Describe(string? pecFile, string gameRoot)
    {
        if (pecFile is null)
        {
            return SignInMessage;
        }
        try
        {
            return ReadRank(pecFile, gameRoot).DisplayText;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return SyncFirstMessage;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or RankSourceException or ArgumentException)
        {
            return UnavailableMessage;
        }
    }

    private static byte[] ReadPrefix(string path, int count)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[count];
        var total = 0;
        int read;
        while (total < count && (read = stream.Read(buffer, total, count - total)) > 0)
        {
            total += read;
        }
        return buffer.AsSpan(0, total).ToArray();
    }
}

/// <summary>
/// The subset of Python's RawConfigParser(strict=False) used for R6Menus.int:
/// case-sensitive section and key names, '=' or ':' delimiters, full-line '#'/';'
/// comments, indented continuation lines, later duplicates win.
/// </summary>
internal static class IniSection
{
    /// <summary>Returns the section's keys, or null when the text is not parseable or the section is absent.</summary>
    public static Dictionary<string, string>? Read(string text, string sectionName)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        Dictionary<string, string>? current = null;
        string? lastKey = null;
        var failed = false;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var stripped = line.Trim();
            if (stripped.Length == 0)
            {
                lastKey = null;
                continue;
            }
            if (stripped[0] is '#' or ';')
            {
                continue;
            }
            var indented = char.IsWhiteSpace(line[0]);
            if (indented && current is not null && lastKey is not null)
            {
                current[lastKey] = current[lastKey] + "\n" + stripped;
                continue;
            }
            if (stripped[0] == '[' && stripped.IndexOf(']') is var close and > 0)
            {
                var name = stripped[1..close];
                if (!sections.TryGetValue(name, out current))
                {
                    current = new Dictionary<string, string>(StringComparer.Ordinal);
                    sections[name] = current;
                }
                lastKey = null;
                continue;
            }
            if (current is null)
            {
                return null; // MissingSectionHeaderError
            }
            var delimiter = stripped.IndexOfAny(['=', ':']);
            if (delimiter <= 0)
            {
                failed = true; // ParsingError, raised after the whole file is read
                lastKey = null;
                continue;
            }
            lastKey = stripped[..delimiter].TrimEnd();
            current[lastKey] = stripped[(delimiter + 1)..].Trim();
        }
        return failed ? null : sections.GetValueOrDefault(sectionName);
    }
}
