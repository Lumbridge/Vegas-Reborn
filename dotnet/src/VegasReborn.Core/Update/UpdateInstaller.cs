namespace VegasReborn.Core.Update;

/// <summary>Transactional installation of a verified payload into the companion folder.</summary>
public static class UpdateInstaller
{
    /// <summary>
    /// Installs only signed managed files, newest-version-only, while no game runs. Previous
    /// files are kept under <c>.updates/backups</c>; a failed replacement restores them. Seed files
    /// are only written when absent. Everything else in the folder is left alone.
    /// </summary>
    /// <param name="gamesRunning">Blocking-process check; defaults to <see cref="GameProcesses.AnyRunning"/>.</param>
    /// <exception cref="GamesBlockingUpdateException">A Vegas game or server is running.</exception>
    /// <exception cref="UpdateException">Verification or installation failed.</exception>
    public static void Install(string root, string payload, Release release, Func<bool>? gamesRunning = null,
        IProgress<UpdateProgress>? progress = null)
    {
        root = SafePaths.Resolve(root);
        payload = SafePaths.Resolve(payload);
        UpdateArchive.VerifyTree(payload, release);
        var installed = Path.Combine(root, "version.json");
        if (File.Exists(installed))
        {
            string? current;
            try
            {
                current = UpdateArchive.ReadVersion(installed) ?? throw new FormatException();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
            {
                throw new UpdateException("Could not verify the installed companion version");
            }
            if (ReleaseVersion.Parse(release.Version) <= ReleaseVersion.Parse(current))
            {
                throw new UpdateException("This update is not newer than the installed companion");
            }
        }
        if ((gamesRunning ?? GameProcesses.AnyRunning)())
        {
            throw new GamesBlockingUpdateException("A Vegas game or server is blocking the update. Close it and retry.");
        }
        var backup = SafePaths.Beneath(root, ".updates/backups/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        var changed = new List<(string Target, string? Old)>();
        try
        {
            // Install the executable last; all its new runtime files exist first.
            var ordered = release.Files.OrderBy(file => file.Path == "VegasReborn.exe").ToList();
            for (var index = 0; index < ordered.Count; index++)
            {
                var entry = ordered[index];
                progress?.Report(new UpdateProgress(UpdateStage.Installing,
                    $"Installing update files: {index + 1} of {ordered.Count}", index + 1, ordered.Count));
                var target = SafePaths.Beneath(root, entry.Path);
                if (entry.Mode == ReleaseFileMode.Seed && File.Exists(target))
                {
                    continue;
                }
                var source = SafePaths.Beneath(payload, entry.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string? old = null;
                if (File.Exists(target))
                {
                    old = SafePaths.Beneath(backup, entry.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(old)!);
                    File.Copy(target, old);
                }
                var temporary = target + ".update-" + Guid.NewGuid().ToString("N");
                try
                {
                    File.Copy(source, temporary);
                    if (SafePaths.Digest(temporary) != entry.Sha256)
                    {
                        throw new UpdateException("An installed update file failed verification");
                    }
                    File.Move(temporary, target, overwrite: true);
                    changed.Add((target, old));
                }
                finally
                {
                    TryDelete(temporary);
                }
            }
        }
        catch (Exception error)
        {
            var failedRollback = false;
            for (var index = changed.Count - 1; index >= 0; index--)
            {
                var (target, old) = changed[index];
                try
                {
                    if (old is not null)
                    {
                        File.Copy(old, target, overwrite: true);
                    }
                    else
                    {
                        File.Delete(target);
                    }
                }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
                {
                    failedRollback = true;
                }
            }
            if (failedRollback)
            {
                throw new UpdateException("Update failed. Recovery copies are in .updates/backups; account and game data were preserved.");
            }
            if (error is UpdateException)
            {
                throw;
            }
            throw new UpdateException("Update could not be installed. The previous app was restored; close other companion windows and retry.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary next to an installed file is harmless.
        }
    }
}
