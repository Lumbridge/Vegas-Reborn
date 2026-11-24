using System.IO.Compression;
using System.Security.Cryptography;
using VegasReborn.Core.Update;

namespace VegasReborn.Core.Content;

/// <summary>Bytes downloaded so far across all archives, and the total download size.</summary>
public sealed record MapsPackProgress(long Bytes, long TotalBytes);

/// <summary>
/// Downloads the Maps Pack into the launcher-owned content store. Nothing counts as installed until
/// every archive and every extracted file matches the signed manifest; the record is written last.
/// </summary>
public sealed class MapsPackInstaller(AppPaths paths, IReleaseDownloader downloader, string? publicKeyHex = null, string? baseUrl = null)
{
    private const string DownloadFailed = "The Maps Pack download failed. Check your connection and try again.";
    private const string InstallFailed = "The Maps Pack could not be installed: ";
    private const int SignatureBytes = 64;
    private const int BlockSize = 1024 * 1024;

    private readonly string _baseUrl = baseUrl ?? ContentInfo.BaseUrl;

    private string Store => paths.MapsPack;
    private string RecordPath => Path.Combine(Store, "record.json");
    private string FilesPath => Path.Combine(Store, "files");
    private string StagingPath => Path.Combine(Store, "staging");

    /// <summary>Downloads and verifies the published manifest.</summary>
    /// <exception cref="ContentException">The download failed or the manifest is not valid.</exception>
    public async Task<ContentRelease> FetchAsync(CancellationToken ct)
    {
        byte[] manifest;
        byte[] signature;
        try
        {
            manifest = await downloader.DownloadBytesAsync(new Uri(_baseUrl + "content.json"), ContentManifest.MaxManifestBytes, ct)
                .ConfigureAwait(false);
            signature = await downloader.DownloadBytesAsync(new Uri(_baseUrl + "content.sig"), SignatureBytes, ct)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (IsDownloadFailure(error, ct))
        {
            throw new ContentException(DownloadFailed);
        }
        return publicKeyHex is null
            ? ContentManifest.Verify(manifest, signature)
            : ContentManifest.Verify(manifest, signature, publicKeyHex);
    }

    /// <summary>True when a record exists and the published version is greater.</summary>
    public async Task<bool> UpdateAvailableAsync(CancellationToken ct)
    {
        MapsPackRecord.Check(paths, out var record);
        if (record is null)
        {
            return false;
        }
        var release = await FetchAsync(ct).ConfigureAwait(false);
        return release.Version > record.Version;
    }

    public async Task InstallAsync(IProgress<MapsPackProgress>? progress, CancellationToken ct)
    {
        var release = await FetchAsync(ct).ConfigureAwait(false);
        try
        {
            DeleteDirectory(StagingPath);
            Directory.CreateDirectory(StagingPath);
            var total = release.Archives.Sum(archive => archive.Bytes);
            long done = 0;
            foreach (var archive in release.Archives)
            {
                var destination = Path.Combine(StagingPath, archive.Name);
                var baseline = done;
                var reporter = progress is null ? null : new Progress<long>(bytes =>
                    progress.Report(new MapsPackProgress(baseline + bytes, total)));
                await DownloadArchiveAsync(archive, destination, reporter, ct).ConfigureAwait(false);
                done += archive.Bytes;
                progress?.Report(new MapsPackProgress(done, total));
            }
            var stagedFiles = Path.Combine(StagingPath, "files");
            Directory.CreateDirectory(stagedFiles);
            foreach (var archive in release.Archives)
            {
                ct.ThrowIfCancellationRequested();
                Extract(Path.Combine(StagingPath, archive.Name), archive, release, stagedFiles, ct);
            }
            ct.ThrowIfCancellationRequested();
            if (File.Exists(RecordPath))
            {
                File.Delete(RecordPath);
            }
            DeleteDirectory(FilesPath);
            Directory.Move(stagedFiles, FilesPath);
            new MapsPackRecord(release.Version, release.Files).Write(RecordPath);
            DeleteDirectory(StagingPath);
        }
        catch (Exception error) when (error is not (OperationCanceledException or ContentException))
        {
            CleanUpAfterFailure();
            throw error is IOException or UnauthorizedAccessException or InvalidDataException or UpdateException
                ? Fail("a file could not be written or read.")
                : error;
        }
        catch
        {
            CleanUpAfterFailure();
            throw;
        }
    }

    /// <summary>Deletes the record first, then the installed files and any staging folder.</summary>
    public void Remove()
    {
        try
        {
            if (File.Exists(RecordPath))
            {
                File.Delete(RecordPath);
            }
            DeleteDirectory(FilesPath);
            DeleteDirectory(StagingPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ContentException("The Maps Pack could not be removed: a file is in use or cannot be deleted.");
        }
    }

    private async Task DownloadArchiveAsync(ContentArchive archive, string destination, IProgress<long>? progress, CancellationToken ct)
    {
        try
        {
            await downloader.DownloadFileAsync(new Uri(_baseUrl + archive.Name), archive.Bytes, destination, progress, ct)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (IsDownloadFailure(error, ct))
        {
            throw new ContentException(DownloadFailed);
        }
        if (new FileInfo(destination).Length != archive.Bytes
            || !string.Equals(SafePaths.Digest(destination), archive.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw Fail($"{archive.Name} did not match the download list.");
        }
    }

    private static void Extract(string archivePath, ContentArchive archive, ContentRelease release, string root, CancellationToken ct)
    {
        var listed = release.Files
            .Where(file => string.Equals(file.Archive, archive.Name, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var zip = ZipFile.OpenRead(archivePath);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/') && entry.Length == 0)
            {
                continue;
            }
            if (!listed.TryGetValue(entry.FullName, out var file))
            {
                throw Fail($"{archive.Name} contains a file that is not on the download list.");
            }
            if (!seen.Add(file.Path))
            {
                throw Fail($"{archive.Name} contains a file more than once.");
            }
            var target = SafePaths.Beneath(root, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            WriteVerified(entry, target, file, ct);
        }
        if (seen.Count != listed.Count)
        {
            throw Fail($"{archive.Name} is missing files from the download list.");
        }
    }

    private static void WriteVerified(ZipArchiveEntry entry, string target, ContentFile file, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long count = 0;
        using (var source = entry.Open())
        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, BlockSize))
        {
            var buffer = new byte[BlockSize];
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var read = source.Read(buffer, 0, (int)Math.Min(BlockSize, file.Bytes - count + 1));
                if (read == 0)
                {
                    break;
                }
                count += read;
                if (count > file.Bytes)
                {
                    throw Fail($"{file.Path} is larger than the download list says.");
                }
                output.Write(buffer, 0, read);
                hash.AppendData(buffer, 0, read);
            }
        }
        if (count != file.Bytes
            || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw Fail($"{file.Path} did not match the download list.");
        }
    }

    private static bool IsDownloadFailure(Exception error, CancellationToken ct) =>
        error is UpdateException or HttpRequestException
        || (error is OperationCanceledException && !ct.IsCancellationRequested);

    private static ContentException Fail(string reason) => new(InstallFailed + reason);

    private void CleanUpAfterFailure()
    {
        // Verification failed or was cancelled before the old pack was touched, so only staging goes.
        // If the swap itself failed, the record is already gone and so are any partial files.
        try
        {
            DeleteDirectory(StagingPath);
            if (!File.Exists(RecordPath))
            {
                DeleteDirectory(FilesPath);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
