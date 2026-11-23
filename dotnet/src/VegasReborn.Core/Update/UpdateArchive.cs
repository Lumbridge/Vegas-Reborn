using System.IO.Compression;
using System.Text.Json;

namespace VegasReborn.Core.Update;

/// <summary>Extraction and verification of a signed release archive.</summary>
public static class UpdateArchive
{
    private const string Prefix = "VegasReborn/";
    private const int MaxEntries = 10000;

    /// <summary>
    /// Checks the archive digest, then extracts exactly the signed files into the empty
    /// <paramref name="folder"/> and verifies the resulting tree.
    /// </summary>
    /// <exception cref="UpdateException">The archive or one of its entries is unsafe or does not match.</exception>
    public static void Extract(string archive, string folder, Release release)
    {
        if (new FileInfo(archive).Length != release.Size || SafePaths.Digest(archive) != release.Sha256)
        {
            throw new UpdateException("The update archive failed its integrity check");
        }
        var entries = release.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var bundle = ZipFile.OpenRead(archive);
            if (bundle.Entries.Count > MaxEntries)
            {
                throw new UpdateException("The update contains too many files");
            }
            foreach (var member in bundle.Entries)
            {
                if (member.FullName.EndsWith('/'))
                {
                    continue;
                }
                const int fileTypeMask = 0xF000, symbolicLink = 0xA000;
                if (!member.FullName.StartsWith(Prefix, StringComparison.Ordinal) || member.IsEncrypted
                    || ((member.ExternalAttributes >> 16) & fileTypeMask) == symbolicLink)
                {
                    throw new UpdateException("The update archive contains an unsafe entry");
                }
                var name = member.FullName[Prefix.Length..];
                if (!entries.TryGetValue(name, out var entry) || seen.Contains(name) || !ManagedPaths.IsManaged(name, release.Version))
                {
                    throw new UpdateException("The update archive contains unexpected files");
                }
                if (member.Length != entry.Bytes)
                {
                    throw new UpdateException("An update file size does not match");
                }
                var target = SafePaths.Beneath(folder, name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (var source = member.Open())
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    CopyBounded(source, output, entry.Bytes);
                }
                seen.Add(name);
            }
        }
        catch (InvalidDataException)
        {
            throw new UpdateException("The update archive failed its integrity check");
        }
        if (!seen.SetEquals(entries.Keys))
        {
            throw new UpdateException("The update archive is incomplete");
        }
        VerifyTree(folder, release);
    }

    /// <summary>
    /// The folder must hold exactly the signed files with their signed sizes and digests,
    /// no links or reparse points, and a <c>version.json</c> naming the release version.
    /// </summary>
    /// <exception cref="UpdateException">The tree does not match the release.</exception>
    public static void VerifyTree(string folder, Release release)
    {
        var expected = release.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        var actual = new HashSet<string>(StringComparer.Ordinal);
        if (Directory.Exists(folder))
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false };
            foreach (var item in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
            {
                if (item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new UpdateException("The staged update contains missing or unexpected files");
                }
                if (item is FileInfo)
                {
                    actual.Add(Path.GetRelativePath(folder, item.FullName).Replace('\\', '/'));
                }
            }
        }
        if (!actual.SetEquals(expected))
        {
            throw new UpdateException("The staged update contains missing or unexpected files");
        }
        foreach (var entry in release.Files)
        {
            var path = SafePaths.Beneath(folder, entry.Path);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != entry.Bytes || SafePaths.Digest(path) != entry.Sha256)
            {
                throw new UpdateException("An update file failed its integrity check");
            }
        }
        if (ReadVersion(Path.Combine(folder, "version.json")) != release.Version)
        {
            throw new UpdateException("The update application version does not match");
        }
    }

    /// <summary>The <c>version</c> member of a <c>version.json</c> (UTF-8, optional BOM), or null.</summary>
    internal static string? ReadVersion(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("version", out var version)
                   && version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void CopyBounded(Stream source, Stream output, long expected)
    {
        var buffer = new byte[1024 * 1024];
        long count = 0;
        int read;
        while ((read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, expected - count + 1))) > 0)
        {
            count += read;
            if (count > expected)
            {
                throw new UpdateException("An update file size does not match");
            }
            output.Write(buffer, 0, read);
        }
    }
}
