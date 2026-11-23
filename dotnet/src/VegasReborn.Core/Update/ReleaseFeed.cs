using System.Text.Json;

namespace VegasReborn.Core.Update;

/// <summary>The update feed: the repository's latest stable GitHub release.</summary>
public static class ReleaseFeed
{
    /// <summary>
    /// Returns the verified latest release when it is newer than <paramref name="currentVersion"/>,
    /// or null when up to date. Drafts and pre-releases are never offered.
    /// </summary>
    /// <exception cref="UpdateException">Download, signature or release validation failed.</exception>
    public static Task<Release?> CheckLatestAsync(IReleaseDownloader downloader, string? currentVersion = null,
        CancellationToken cancellationToken = default) =>
        CheckLatestAsync(downloader, currentVersion ?? ReleaseInfo.CurrentVersion, UpdateKey.PublicKeyHex, cancellationToken);

    internal static async Task<Release?> CheckLatestAsync(IReleaseDownloader downloader, string currentVersion, string publicKeyHex,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(downloader);
        try
        {
            var latest = await downloader.DownloadBytesAsync(
                new Uri($"https://api.github.com/repos/{ReleaseInfo.Repository}/releases/latest"), 1024 * 1024, cancellationToken)
                .ConfigureAwait(false);
            using var document = JsonDocument.Parse(latest);
            var value = document.RootElement;
            var tag = Json.String(value, "tag_name");
            var version = tag.StartsWith('v') ? tag[1..] : tag;
            if (Json.Truthy(value, "draft") || Json.Truthy(value, "prerelease")
                || ReleaseVersion.Parse(version) <= ReleaseVersion.Parse(currentVersion))
            {
                return null;
            }
            var baseUrl = $"https://github.com/{ReleaseInfo.Repository}/releases/download/v{version}/";
            var assets = Json.Property(value, "assets");
            if (assets.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException();
            }
            var byName = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var asset in assets.EnumerateArray())
            {
                byName[Json.String(asset, "name")] = asset;
            }
            foreach (var name in new[] { "update.json", "update.sig", $"VegasReborn-{version}-win64.zip" })
            {
                if (Json.String(byName[name], "browser_download_url") != baseUrl + name)
                {
                    throw new FormatException();
                }
            }
            var manifest = await downloader.DownloadBytesAsync(new Uri(baseUrl + "update.json"), ReleaseManifest.MaxManifestBytes,
                cancellationToken).ConfigureAwait(false);
            var signature = await downloader.DownloadBytesAsync(new Uri(baseUrl + "update.sig"), 64, cancellationToken)
                .ConfigureAwait(false);
            var release = ReleaseManifest.Verify(manifest, signature, publicKeyHex);
            var archive = byName[release.Archive];
            if (release.Version != version || Json.Int64(archive, "size") != release.Size)
            {
                throw new FormatException();
            }
            if (archive.TryGetProperty("digest", out var digest) && digest.ValueKind != JsonValueKind.Null
                && (digest.ValueKind != JsonValueKind.String || digest.GetString() != "sha256:" + release.Sha256))
            {
                throw new FormatException();
            }
            return release;
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException
                                          or KeyNotFoundException or UriFormatException)
        {
            throw new UpdateException("GitHub returned an invalid update release");
        }
    }

    /// <summary>
    /// Writes <c>.updates/last-check.json</c>, a small public-state diagnostic. It never records
    /// account data or HTTP headers. The 0.4.x companion only records checks in packaged builds.
    /// </summary>
    public static void RecordCheck(string root, Release? release, string? checkedVersion = null)
    {
        var path = SafePaths.Beneath(SafePaths.Resolve(root), ".updates/last-check.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".new";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("checkedVersion", checkedVersion ?? ReleaseInfo.CurrentVersion);
            if (release is null)
            {
                writer.WriteNull("availableVersion");
            }
            else
            {
                writer.WriteString("availableVersion", release.Version);
            }
            writer.WriteString("status", release is null ? "up-to-date" : "available");
            writer.WriteNumber("checkedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            writer.WriteEndObject();
        }
        File.Move(temporary, path, overwrite: true);
    }
}
