using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using VegasReborn.Core.Launch;

namespace VegasReborn.Core.Titles;

/// <summary>Where an installation was found.</summary>
public enum InstallSource
{
    Steam,
    Gog,
    Epic,
    /// <summary>A Windows installed-programs entry (retail disc and other installers).</summary>
    InstalledPrograms,
    /// <summary>A search of folders on the PC's drives.</summary>
    FolderSearch,
}

/// <summary>A folder that contains a title's client executable.</summary>
/// <param name="Supported">The executable is the build the compatibility module supports.</param>
public sealed record InstallCandidate(string Folder, InstallSource Source, bool Supported)
{
    public string SourceName => Source switch
    {
        InstallSource.Steam => "Steam",
        InstallSource.Gog => "GOG",
        InstallSource.Epic => "Epic Games",
        InstallSource.InstalledPrograms => "installed programs",
        _ => "folder search",
    };
}

/// <summary>Registry reads used by <see cref="GameInstallLocator"/>, replaceable in tests.</summary>
internal interface IInstallRegistry
{
    /// <summary>A string value, or null. Keys start with HKEY_LOCAL_MACHINE or HKEY_CURRENT_USER.</summary>
    string? GetString(string key, string name);

    IReadOnlyList<string> SubKeyNames(string key);
}

/// <summary>
/// Finds installations of a title through the stores and installers that record them, then by
/// searching folders on the PC's fixed drives. A folder counts only when it contains the
/// title's client executable; it is <see cref="InstallCandidate.Supported"/> only when that
/// executable has the supported SHA-256.
/// </summary>
public sealed class GameInstallLocator
{
    private const string Hklm = "HKEY_LOCAL_MACHINE";
    private const string Hkcu = "HKEY_CURRENT_USER";
    private const int DefaultSearchDepth = 6;

    private static readonly string[] UninstallKeys =
    [
        Hklm + @"\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        Hklm + @"\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        Hkcu + @"\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    // Folder names never worth descending into while searching drives.
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "$Recycle.Bin", "$WinREAgent", "System Volume Information", "Recovery", "Config.Msi",
        "AppData", "WinSxS", "node_modules", ".git", "Microsoft", "WindowsApps", "Packages",
    };

    private readonly IInstallRegistry _registry;
    private readonly Func<IReadOnlyList<string>> _driveRoots;
    private readonly string _programData;
    private readonly IReadOnlyList<string> _excluded;

    /// <param name="excludedFolders">Folders never reported or searched, such as the launcher's own game copies.</param>
    public GameInstallLocator(IEnumerable<string>? excludedFolders = null)
        : this(new WindowsInstallRegistry(), FixedDriveRoots,
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), excludedFolders)
    {
    }

    internal GameInstallLocator(IInstallRegistry registry, Func<IReadOnlyList<string>> driveRoots, string programData,
        IEnumerable<string>? excludedFolders = null)
    {
        _registry = registry;
        _driveRoots = driveRoots;
        _programData = programData;
        _excluded = [.. excludedFolders ?? []];
    }

    /// <summary>Installations recorded by Steam, GOG, Epic and Windows installers.</summary>
    public IReadOnlyList<InstallCandidate> FindRecorded(GameTitle title)
    {
        var found = new List<(string Folder, InstallSource Source)>();
        void Add(string? folder, InstallSource source)
        {
            if (!string.IsNullOrWhiteSpace(folder) && ContainsClient(folder, title))
            {
                found.Add((folder, source));
            }
        }

        foreach (var library in SteamLibraries())
        {
            Add(SteamAppFolder(library, title.SteamAppId), InstallSource.Steam);
            Add(Path.Combine(library, "steamapps", "common", Path.GetFileName(title.DefaultInstallFolder)), InstallSource.Steam);
        }
        foreach (var root in new[] { Hklm + @"\SOFTWARE\WOW6432Node\GOG.com\Games", Hklm + @"\SOFTWARE\GOG.com\Games" })
        {
            foreach (var id in _registry.SubKeyNames(root))
            {
                Add(_registry.GetString(root + "\\" + id, "path"), InstallSource.Gog);
            }
        }
        foreach (var folder in EpicFolders())
        {
            Add(folder, InstallSource.Epic);
        }
        foreach (var root in UninstallKeys)
        {
            foreach (var name in _registry.SubKeyNames(root))
            {
                var source = name.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase) ? InstallSource.Steam
                    : InstallSource.InstalledPrograms;
                Add(_registry.GetString(root + "\\" + name, "InstallLocation"), source);
            }
        }
        return Rank(title, found);
    }

    /// <summary>
    /// Searches folders on every fixed drive (or under <paramref name="roots"/>) for the titles'
    /// client executables, shallowest folders first. Junctions and system folders are skipped.
    /// </summary>
    public IReadOnlyDictionary<GameTitle, IReadOnlyList<InstallCandidate>> Search(
        IReadOnlyList<GameTitle> titles,
        IReadOnlyList<string>? roots = null,
        int maxDepth = DefaultSearchDepth,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var found = titles.ToDictionary(title => title, _ => new List<(string, InstallSource)>());
        var queue = new Queue<(string Folder, int Depth)>((roots ?? _driveRoots()).Select(root => (root, 0)));
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (folder, depth) = queue.Dequeue();
            if (IsExcluded(folder))
            {
                continue;
            }
            foreach (var title in titles)
            {
                if (ContainsClient(folder, title))
                {
                    found[title].Add((folder, InstallSource.FolderSearch));
                }
            }
            if (depth >= maxDepth)
            {
                continue;
            }
            if (depth <= 1)
            {
                progress?.Report(folder);
            }
            foreach (var child in ChildFolders(folder))
            {
                queue.Enqueue((child, depth + 1));
            }
        }
        return found.ToDictionary(item => item.Key, item => (IReadOnlyList<InstallCandidate>)Rank(item.Key, item.Value));
    }

    /// <summary>True when the folder contains the title's client executable.</summary>
    public static bool ContainsClient(string folder, GameTitle title)
    {
        try
        {
            return File.Exists(Path.Combine(folder, title.ClientExecutable));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    /// <summary>True when the folder's client executable is the supported build.</summary>
    public static bool IsSupported(string folder, GameTitle title)
    {
        try
        {
            return PrivateFile.Sha256(Path.Combine(folder, title.ClientExecutable)) == title.ClientSha256;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    // Supported builds first, then the order the sources were consulted; one entry per folder.
    private List<InstallCandidate> Rank(GameTitle title, IEnumerable<(string Folder, InstallSource Source)> found)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<InstallCandidate>();
        foreach (var (folder, source) in found)
        {
            string full;
            try
            {
                full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            if (!IsExcluded(full) && seen.Add(full))
            {
                result.Add(new InstallCandidate(full, source, IsSupported(full, title)));
            }
        }
        return [.. result.OrderBy(candidate => candidate.Supported ? 0 : 1)];
    }

    private bool IsExcluded(string folder)
    {
        try
        {
            return _excluded.Any(excluded => PrivateFile.IsWithin(folder, excluded));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }
    }

    private IEnumerable<string> SteamLibraries()
    {
        var roots = new[]
        {
            _registry.GetString(Hkcu + @"\Software\Valve\Steam", "SteamPath"),
            _registry.GetString(Hklm + @"\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            _registry.GetString(Hklm + @"\SOFTWARE\Valve\Steam", "InstallPath"),
        };
        var libraries = new List<string>();
        foreach (var root in roots.Where(root => !string.IsNullOrWhiteSpace(root)).Cast<string>())
        {
            libraries.Add(root);
            foreach (var file in new[] { Path.Combine(root, "steamapps", "libraryfolders.vdf"), Path.Combine(root, "config", "libraryfolders.vdf") })
            {
                libraries.AddRange(SteamLibraryPaths(ReadText(file)));
            }
        }
        return libraries.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Library folders in a libraryfolders.vdf (current "path" entries and the older numbered form).</summary>
    internal static IEnumerable<string> SteamLibraryPaths(string? vdf)
    {
        if (vdf is null)
        {
            yield break;
        }
        foreach (Match match in Regex.Matches(vdf, "^\\s*\"(?:path|\\d+)\"\\s+\"([^\"]+)\"", RegexOptions.Multiline))
        {
            var value = VdfUnescape(match.Groups[1].Value);
            if (value.Contains('\\') || value.Contains('/'))
            {
                yield return value;
            }
        }
    }

    /// <summary>The installdir of an appmanifest_&lt;id&gt;.acf as a full folder, or null.</summary>
    internal static string? SteamAppFolder(string library, int appId)
    {
        var manifest = ReadText(Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf"));
        var match = manifest is null ? null : Regex.Match(manifest, "\"installdir\"\\s+\"([^\"]+)\"");
        return match is { Success: true } ? Path.Combine(library, "steamapps", "common", VdfUnescape(match.Groups[1].Value)) : null;
    }

    private IEnumerable<string> EpicFolders()
    {
        var manifests = Path.Combine(_programData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
        string[] files;
        try
        {
            files = Directory.Exists(manifests) ? Directory.GetFiles(manifests, "*.item") : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var file in files)
        {
            string? folder = null;
            try
            {
                if (JsonNode.Parse(File.ReadAllBytes(file)) is JsonObject item && item["InstallLocation"] is JsonValue location)
                {
                    location.TryGetValue(out folder);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
            }
            if (folder is not null)
            {
                yield return folder;
            }
        }
    }

    private static IEnumerable<string> ChildFolders(string folder)
    {
        DirectoryInfo[] children;
        try
        {
            children = new DirectoryInfo(folder).GetDirectories();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return [];
        }
        return children
            .Where(child => !child.Attributes.HasFlag(FileAttributes.ReparsePoint) && !SkippedFolders.Contains(child.Name))
            .Select(child => child.FullName);
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string VdfUnescape(string value) => value.Replace("\\\\", "\\");

    private static IReadOnlyList<string> FixedDriveRoots()
    {
        try
        {
            return [.. DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady).Select(drive => drive.RootDirectory.FullName)];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

internal sealed class WindowsInstallRegistry : IInstallRegistry
{
    public string? GetString(string key, string name)
    {
        using var opened = Open(key);
        try
        {
            return opened?.GetValue(name) as string;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    public IReadOnlyList<string> SubKeyNames(string key)
    {
        using var opened = Open(key);
        try
        {
            return opened?.GetSubKeyNames() ?? [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    private static RegistryKey? Open(string key)
    {
        var separator = key.IndexOf('\\');
        var hive = separator < 0 ? key : key[..separator];
        var path = separator < 0 ? "" : key[(separator + 1)..];
        try
        {
            // The 64-bit view so explicit WOW6432Node paths and native 64-bit keys both resolve.
            using var root = hive switch
            {
                "HKEY_LOCAL_MACHINE" => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
                "HKEY_CURRENT_USER" => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64),
                _ => null,
            };
            return root?.OpenSubKey(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
