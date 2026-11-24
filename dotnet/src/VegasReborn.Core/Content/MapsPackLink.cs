using System.Security.Cryptography;
using System.Text.Json;
using VegasReborn.Core.Launch;
using VegasReborn.Core.Update;
using NativeMethods = VegasReborn.Core.Update.NativeMethods;

namespace VegasReborn.Core.Content;

/// <summary>
/// Puts the verified Extra Maps from the launcher's content store into a Vegas 1 game copy, or
/// takes it out again when the pack is gone. Pack files are hard links to the store (copies when
/// linking fails), except the pack's own mission inis, which are private copies so the
/// 16-player raise can rewrite them. The copy's <see cref="LinkedList"/> records what was added,
/// and only those files are ever removed.
/// </summary>
internal static class MapsPackLink
{
    public const string LinkedList = ".vegasreborn-mapspack.json";
    public const string PackFolder = "KellerGame/Content/CookedPc/MapsPack";

    /// <summary>
    /// Links or unlinks the pack. Returns true when the pack is installed and linked.
    /// </summary>
    /// <exception cref="LaunchException">The store no longer matches its record.</exception>
    public static bool Apply(AppPaths paths, string copy)
    {
        // Every launch: sizes only. The full hash check is too slow to run per prepare.
        var state = MapsPackRecord.CheckQuick(paths, out var record);
        if (state == MapsPackState.Damaged)
        {
            throw new LaunchException("Extra Maps is damaged. Download it again or remove it.");
        }
        var listPath = Path.Combine(copy, LinkedList);
        var previous = ReadList(listPath);
        if (state == MapsPackState.NotInstalled)
        {
            Remove(copy, previous);
            PrivateFile.Delete(listPath);
            return false;
        }

        var store = Path.Combine(paths.MapsPack, "files");
        var current = record!.Files.Select(file => file.Path).ToList();
        var kept = new HashSet<string>(current, StringComparer.OrdinalIgnoreCase);
        // List everything that may be present before changing the copy, so an interrupted
        // prepare still knows what to remove.
        WriteList(listPath, previous, current.Concat(previous.Where(path => !kept.Contains(path))).ToList());
        foreach (var file in record.Files)
        {
            string target;
            try
            {
                target = SafePaths.Beneath(copy, file.Path);
            }
            catch (UpdateException)
            {
                throw new LaunchException("An Extra Maps file would be placed outside the game copy");
            }
            var origin = Path.Combine(store, file.Path);
            if (IsPackIni(file.Path))
            {
                // A private copy: the raise pass below always rebuilds it from the store original.
                if (!File.Exists(target))
                {
                    Place(origin, target, link: false);
                }
            }
            else if (!SameFile(target, origin))
            {
                Place(origin, target, link: true);
            }
        }
        Remove(copy, previous.Where(path => !kept.Contains(path)).ToList());
        WriteList(listPath, ReadList(listPath), current);
        // The +Paths=..\KellerGame\Content\CookedPc\MapsPack engine-ini fallback would go here if the retail check shows it is needed.
        MissionPlayerLimits.Apply(copy, store, extendModes: false, folder: PackFolder);
        return true;
    }

    /// <summary>True when both paths are the same file on disk (hard links included).</summary>
    internal static bool SameFile(string left, string right)
    {
        var a = NativeMethods.FileIdentity(left);
        return a is not null && a == NativeMethods.FileIdentity(right);
    }

    private static bool IsPackIni(string path) =>
        path.StartsWith(PackFolder + "/", StringComparison.OrdinalIgnoreCase)
        && path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the target is an earlier copy of the store file: same size and last-write time
    /// (File.Copy keeps it). Used only where linking is not possible, so files are never hashed
    /// per launch.
    /// </summary>
    private static bool CopyCurrent(string target, string origin)
    {
        var info = new FileInfo(target);
        var source = new FileInfo(origin);
        return info.Exists && info.Length == source.Length && info.LastWriteTimeUtc == source.LastWriteTimeUtc;
    }

    /// <summary>
    /// Replaces the target with a hard link to (or a copy of) the store file. When linking is
    /// not possible, an existing copy that still matches the store file is kept.
    /// </summary>
    private static void Place(string origin, string target, bool link)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + "." + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        try
        {
            // Linking fails across volumes or on file systems without hard links; copy then.
            if (!link || !NativeMethods.CreateHardLink(temporary, origin, 0))
            {
                if (link && CopyCurrent(target, origin))
                {
                    return;
                }
                File.Copy(origin, temporary);
            }
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            PrivateFile.Delete(temporary);
        }
    }

    /// <summary>
    /// Deletes listed files from the copy. Entries that are not pack paths or that resolve
    /// outside the copy (through "..", absolute paths or junctions) are skipped.
    /// </summary>
    private static void Remove(string copy, IReadOnlyList<string> listed)
    {
        if (listed.Count == 0)
        {
            return;
        }
        foreach (var entry in listed)
        {
            if (!ContentManifest.IsAllowedPath(entry))
            {
                continue;
            }
            string target;
            try
            {
                target = SafePaths.Beneath(copy, entry);
            }
            catch (UpdateException)
            {
                continue;
            }
            if (File.Exists(target))
            {
                File.Delete(target);
            }
        }
        RemoveEmpty(Path.Combine(copy, PackFolder));
    }

    /// <summary>Deletes empty folders under (and including) the pack folder. Junctions are left alone.</summary>
    private static void RemoveEmpty(string folder)
    {
        var info = new DirectoryInfo(folder);
        if (!info.Exists || info.LinkTarget is not null)
        {
            return;
        }
        foreach (var child in info.EnumerateDirectories())
        {
            RemoveEmpty(child.FullName);
        }
        if (!info.EnumerateFileSystemInfos().Any())
        {
            info.Delete();
        }
    }

    private static List<string> ReadList(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string?>>(File.ReadAllText(path))?
                .OfType<string>().ToList() ?? [];
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return [];
        }
    }

    private static void WriteList(string path, IReadOnlyList<string> existing, IReadOnlyList<string> paths)
    {
        if (!existing.SequenceEqual(paths, StringComparer.Ordinal) || !File.Exists(path))
        {
            PrivateFile.WriteText(path, JsonSerializer.Serialize(paths));
        }
    }
}
