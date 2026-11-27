using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VegasReborn.Core.Profiles.Vegas1;

namespace VegasReborn.Core.Profiles.Vegas2;

/// <summary>One A.C.E.S. category: earned points, the level they give and progress to the next level.</summary>
/// <param name="Key">Icon and enum suffix: Marksmanship, CQB or Assault.</param>
/// <param name="Label">English label from R6Menus.int [Aces].</param>
public sealed record AcesCategory(string Key, string Label, uint Points, int Level, int MaxLevel, uint? NextRequired, double Progress)
{
    public string Title => Label.Length <= 3 ? Label : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Label.ToLowerInvariant());

    /// <summary>For example "Marksman: level 2, 90 of 120 points".</summary>
    public string DisplayText => NextRequired is { } next
        ? $"{Title}: level {Level}, {Points.ToString("N0", CultureInfo.InvariantCulture)} of {next.ToString("N0", CultureInfo.InvariantCulture)} points"
        : $"{Title}: level {Level}, maximum level";
}

/// <summary>
/// Rainbow Six Vegas 2 A.C.E.S. levels, read only. The level table is the
/// <c>AcesLevelConfig</c> default of <c>R6Game.R6AcesManager</c> in the cooked
/// <c>KellerGame/CookedPC/R6Game.uppc</c> (hash-pinned): 20 levels whose cumulative
/// <c>ExperienceRequired</c> applies to every category. Categories follow
/// <c>EACESTYPE</c> (MARKSMAN, CQB, ASSAULT), which is also the order of the per-level
/// rewards. The save holds the three point totals as u32 after raw XP
/// (<see cref="PecSave.AcesOffset"/>). Labels come from the hash-pinned R6Menus.int [Aces].
/// </summary>
public sealed class AcesTable
{
    public const string PackagePath = "KellerGame/CookedPC/R6Game.uppc";
    public const string PackageSha256 = "9cf5425508e70369e698b1d6b827301fe7ac9add2b86a815e64b809cdb39e07a";
    public const int PackageBytes = 3150875;
    public const int LevelCount = 20;

    // Default__R6AcesManager serial data starts at 0x17e565; AcesLevelConfig's array tag is
    // at +0x9df and each FAcesLevel element is 0x68 bytes with ExperienceRequired first.
    private const int ArrayTag = 0x17ef44;
    private const int FirstLevel = 0x17ef58;
    private const int LevelStride = 0x68;
    private const int NameAcesLevelConfig = 4979;
    private const int NameArrayProperty = 9;
    private const int NameExperienceRequired = 432;
    private const int NameIntProperty = 2;

    /// <summary>Category keys in EACESTYPE order with their R6Menus.int [Aces] label keys.</summary>
    public static IReadOnlyList<(string Key, string LabelKey)> Categories { get; } =
    [
        ("Marksmanship", "Marksman"), ("CQB", "CloseQuarters"), ("Assault", "Assault"),
    ];

    private static readonly ConcurrentDictionary<string, AcesTable> Loaded = new(StringComparer.OrdinalIgnoreCase);

    private AcesTable(IReadOnlyList<uint> required, IReadOnlyList<string> labels)
    {
        Required = required;
        Labels = labels;
    }

    /// <summary>Cumulative points for levels 1 to 20.</summary>
    public IReadOnlyList<uint> Required { get; }

    public IReadOnlyList<string> Labels { get; }

    /// <summary>Accepts only the inspected cooked package and English menus.</summary>
    public static AcesTable Parse(ReadOnlySpan<byte> package, ReadOnlySpan<byte> englishMenus)
    {
        if (package.Length != PackageBytes || Convert.ToHexStringLower(SHA256.HashData(package)) != PackageSha256)
        {
            throw new RankSourceException("unsupported_original_aces_package");
        }
        if (BinaryPrimitives.ReadInt32LittleEndian(package[ArrayTag..]) != NameAcesLevelConfig
            || BinaryPrimitives.ReadInt32LittleEndian(package[(ArrayTag + 4)..]) != NameArrayProperty
            || BinaryPrimitives.ReadInt32LittleEndian(package[(ArrayTag + 16)..]) != LevelCount)
        {
            throw new RankSourceException("invalid_original_aces_levels");
        }
        var required = new uint[LevelCount];
        for (var level = 0; level < LevelCount; level++)
        {
            var tag = package[(FirstLevel + level * LevelStride)..];
            if (BinaryPrimitives.ReadInt32LittleEndian(tag) != NameExperienceRequired
                || BinaryPrimitives.ReadInt32LittleEndian(tag[4..]) != NameIntProperty
                || BinaryPrimitives.ReadInt32LittleEndian(tag[8..]) != 4)
            {
                throw new RankSourceException("invalid_original_aces_levels");
            }
            var value = BinaryPrimitives.ReadInt32LittleEndian(tag[16..]);
            if (value <= 0 || (level > 0 && value <= required[level - 1]))
            {
                throw new RankSourceException("invalid_original_aces_levels");
            }
            required[level] = (uint)value;
        }
        if (Convert.ToHexStringLower(SHA256.HashData(englishMenus)) != RankTable.Vegas2.MenusSha256)
        {
            throw new RankSourceException("unsupported_original_english_labels");
        }
        var section = IniSection.Read(Encoding.Latin1.GetString(englishMenus), "Aces")
            ?? throw new RankSourceException("invalid_original_english_labels");
        var labels = new string[Categories.Count];
        for (var i = 0; i < labels.Length; i++)
        {
            if (!section.TryGetValue(Categories[i].LabelKey, out var label) || label.Trim().Length == 0)
            {
                throw new RankSourceException("invalid_original_english_labels");
            }
            labels[i] = label.Trim();
        }
        return new AcesTable(required, labels);
    }

    public static AcesTable Load(string gameRoot) =>
        Parse(File.ReadAllBytes(CheckedPath(Path.Combine(gameRoot, PackagePath), PackageBytes)),
            File.ReadAllBytes(CheckedPath(Path.Combine(gameRoot, RankTable.Vegas2.MenusPath), 1024 * 1024)));

    /// <summary>Level, next requirement and progress for a point total.</summary>
    public AcesCategory CategoryFor(int category, uint points)
    {
        var level = 0;
        while (level < Required.Count && points >= Required[level])
        {
            level++;
        }
        uint? next = level < Required.Count ? Required[level] : null;
        var previous = level == 0 ? 0u : Required[level - 1];
        var progress = next is { } n ? Math.Clamp((double)(points - previous) / (n - previous), 0.0, 1.0) : 1.0;
        return new AcesCategory(Categories[category].Key, Labels[category], points, level, Required.Count, next, progress);
    }

    /// <summary>
    /// The three categories for a Vegas 2 character save, or null when there is no save, the
    /// game folder lacks the pinned files, or either is unreadable. Writes nothing.
    /// </summary>
    public static IReadOnlyList<AcesCategory>? TryRead(string? saveFile, string? gameRoot)
    {
        if (string.IsNullOrEmpty(saveFile) || string.IsNullOrEmpty(gameRoot) || !File.Exists(saveFile))
        {
            return null;
        }
        try
        {
            var root = Path.GetFullPath(gameRoot);
            if (!Loaded.TryGetValue(root, out var table))
            {
                table = Load(root);
                Loaded[root] = table;
            }
            var points = PecSave.ReadAces(File.ReadAllBytes(CheckedPath(saveFile, PecSave.MaxBytes)));
            return [.. points.Select((value, index) => table.CategoryFor(index, value))];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or RankSourceException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The 64x64 category icon <c>Ic_ACES_&lt;Key&gt;.tga</c> beside the rank insignia, or null when absent or not the shipped format.</summary>
    public static RankImage? TryLoadIcon(string? gameRoot, string key)
    {
        if (string.IsNullOrEmpty(gameRoot) || !Categories.Any(category => category.Key == key))
        {
            return null;
        }
        try
        {
            var info = new FileInfo(IconPath(gameRoot, key));
            if (!info.Exists || info.Length > 64 * 1024)
            {
                return null;
            }
            return RankInsignia.Decode(File.ReadAllBytes(info.FullName));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static string IconPath(string gameRoot, string key) =>
        Path.Combine(gameRoot, RankTable.Vegas2.InsigniaFolder, "Ic_ACES_" + key + ".tga");

    private static string CheckedPath(string path, long maxBytes)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("missing", path);
        }
        if (info.Length > maxBytes)
        {
            throw new InvalidDataException("file larger than supported");
        }
        return info.FullName;
    }
}
