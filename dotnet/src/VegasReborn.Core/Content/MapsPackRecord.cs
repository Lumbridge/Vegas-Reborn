using System.Text.Json;
using VegasReborn.Core.Launch;

namespace VegasReborn.Core.Content;

public enum MapsPackState
{
    NotInstalled,
    Installed,
    Damaged,
}

/// <summary>What the launcher installed into its Maps Pack store: every file with its size and SHA-256.</summary>
public sealed record MapsPackRecord(int Version, IReadOnlyList<ContentFile> Files)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>The record, or null when the file is missing, unreadable or malformed.</summary>
    public static MapsPackRecord? Read(string path)
    {
        try
        {
            var record = JsonSerializer.Deserialize<MapsPackRecord>(File.ReadAllText(path), Options);
            if (record?.Files is null || record.Files.Count == 0 || record.Files.Any(file => file is null || !IsSafe(file.Path)))
            {
                return null;
            }
            return record;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    public void Write(string path) => PrivateFile.WriteText(path, JsonSerializer.Serialize(this, Options));

    /// <summary>
    /// Not installed without a record; damaged when any recorded file is missing or its
    /// size or SHA-256 differs.
    /// </summary>
    public static MapsPackState Check(AppPaths paths, out MapsPackRecord? record)
    {
        record = Read(Path.Combine(paths.MapsPack, "record.json"));
        if (record is null)
        {
            return MapsPackState.NotInstalled;
        }
        var files = Path.Combine(paths.MapsPack, "files");
        foreach (var file in record.Files)
        {
            var path = Path.Combine(files, file.Path.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (!File.Exists(path)
                    || new FileInfo(path).Length != file.Bytes
                    || !string.Equals(PrivateFile.Sha256(path), file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return MapsPackState.Damaged;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return MapsPackState.Damaged;
            }
        }
        return MapsPackState.Installed;
    }

    private static bool IsSafe(string? path) =>
        !string.IsNullOrEmpty(path)
        && !path.Contains('\\')
        && !path.StartsWith('/')
        && !path.Contains(':')
        && !path.Split('/').Any(part => part is ".." or "." or "" || part.EndsWith('.') || part.EndsWith(' '));
}
