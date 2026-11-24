using System.Text.Json;
using VegasReborn.Core.Update;

namespace VegasReborn.Core.Content;

/// <summary>One downloadable archive of a content release.</summary>
public sealed record ContentArchive(string Name, long Bytes, string Sha256);

/// <summary>One file a content archive installs: forward-slash path, size, SHA-256 and owning archive.</summary>
public sealed record ContentFile(string Path, long Bytes, string Sha256, string Archive);

/// <summary>A content release whose signed manifest passed every check.</summary>
public sealed record ContentRelease(
    string Id,
    int Version,
    string Title,
    IReadOnlyList<ContentArchive> Archives,
    IReadOnlyList<ContentFile> Files);

/// <summary>Verification of the signed content manifest (schema 1, kind "content").</summary>
public static class ContentManifest
{
    public const int MaxManifestBytes = 4 * 1024 * 1024;
    public const long MaxArchiveBytes = 1536L * 1024 * 1024;
    public const long MaxUnpacked = 2L * 1024 * 1024 * 1024;
    public const int MaxFiles = 5000;

    private const string Unsafe = "The Maps Pack download list is unsupported or unsafe";
    private const string Unverified = "The Maps Pack download list could not be verified";
    private const string ContentPrefix = "KellerGame/Content/CookedPc/MapsPack/";
    private const string LocalizationPrefix = "KellerGame/Localization/";
    private const string ImagePrefix = "KellerGame/ImageMap/PAK_";

    private static readonly string[] Languages = ["INT", "FRA", "DEU", "ESP", "ITA", "POL", "RUS", "JPN"];

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".usdx", ".rmpc", ".shaders", ".ini", ".uppc", ".int", ".fra", ".deu", ".esp", ".ita", ".pol", ".rus", ".jpn", ".dds",
    };

    /// <summary>Verifies <paramref name="data"/> with the pinned release key and validates its contents.</summary>
    /// <exception cref="ContentException">The signature or the manifest is invalid.</exception>
    public static ContentRelease Verify(byte[] data, byte[] signature) => Verify(data, signature, UpdateKey.PublicKeyHex);

    internal static ContentRelease Verify(byte[] data, byte[] signature, string publicKeyHex)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(signature);
        if (data.Length > MaxManifestBytes || signature.Length != 64)
        {
            throw new ContentException(Unsafe);
        }
        if (!ReleaseManifest.VerifySignature(data, signature, publicKeyHex))
        {
            throw new ContentException(Unverified);
        }
        try
        {
            using var document = JsonDocument.Parse(data);
            var value = document.RootElement;
            var version = Json.Int64(value, "version");
            if (!Json.IsInteger(value, "schema", 1) || Json.String(value, "kind") != "content"
                || Json.String(value, "id") != ContentInfo.Id || Json.String(value, "title") != "vegas1"
                || version is < 1 or > int.MaxValue)
            {
                throw new FormatException();
            }
            var archiveEntries = Json.Property(value, "archives");
            var fileEntries = Json.Property(value, "files");
            if (archiveEntries.ValueKind != JsonValueKind.Array || archiveEntries.GetArrayLength() is < 1 or > MaxFiles
                || fileEntries.ValueKind != JsonValueKind.Array || fileEntries.GetArrayLength() is < 1 or > MaxFiles)
            {
                throw new FormatException();
            }
            var archives = new List<ContentArchive>();
            var archiveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archiveEntries.EnumerateArray())
            {
                var name = Json.String(entry, "name");
                var bytes = Json.Int64(entry, "bytes");
                var digest = Json.String(entry, "sha256");
                if (!IsArchiveName(name) || !archiveNames.Add(name) || bytes is not (> 0 and <= MaxArchiveBytes)
                    || !ReleaseManifest.Sha256Hex().IsMatch(digest))
                {
                    throw new FormatException();
                }
                archives.Add(new ContentArchive(name, bytes, digest));
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<ContentFile>();
            long total = 0;
            foreach (var entry in fileEntries.EnumerateArray())
            {
                var path = Json.String(entry, "path");
                var size = Json.Int64(entry, "bytes");
                var digest = Json.String(entry, "sha256");
                var archive = Json.String(entry, "archive");
                if (!IsAllowedPath(path) || !seen.Add(path) || size < 0 || !ReleaseManifest.Sha256Hex().IsMatch(digest)
                    || !archiveNames.Contains(archive))
                {
                    throw new FormatException();
                }
                total = checked(total + size);
                if (total > MaxUnpacked)
                {
                    throw new FormatException();
                }
                files.Add(new ContentFile(path, size, digest, archive));
            }
            return new ContentRelease(ContentInfo.Id, (int)version, "vegas1", archives, files);
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException
                                          or KeyNotFoundException or OverflowException)
        {
            throw new ContentException(Unsafe);
        }
    }

    private static bool IsArchiveName(string name) =>
        name.Length is > 0 and <= 128 && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') && !name.StartsWith('.');

    internal static bool IsAllowedPath(string path)
    {
        if (path.Length is 0 or > 260 || path.Contains('\\') || path.Contains(':') || path.StartsWith('/')
            || path.Any(char.IsControl))
        {
            return false;
        }
        var segments = path.Split('/');
        if (segments.Any(s => s.Length == 0 || s == "." || s == ".." || s.EndsWith('.') || s.EndsWith(' ')))
        {
            return false;
        }
        if (!Extensions.Contains(Path.GetExtension(path)))
        {
            return false;
        }
        return path.StartsWith(ContentPrefix, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(ImagePrefix, StringComparison.OrdinalIgnoreCase)
               || Languages.Any(language => path.StartsWith($"{LocalizationPrefix}{language}/PAK_", StringComparison.OrdinalIgnoreCase));
    }
}
