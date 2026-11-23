using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using VegasReborn.Core.Account;
using VegasReborn.Core.Profiles.Vegas1;
using VegasReborn.Core.Titles;

namespace VegasReborn.Core.Profiles;

/// <summary>
/// Cloud sync for account-isolated original save files of one title.
/// This auxiliary HTTPS account service does not replace the game's native
/// Agora/Demonware protocol. Uses optimistic cloud revisions, never deletes or invents
/// a character, keeps the higher XP, and backs up anything it replaces.
/// <para>
/// Layout (shared with the Python companion): <c>local/retail-accounts/&lt;discordId&gt;/</c>
/// holds <c>profile/&lt;title profile folder&gt;/</c> (the game's redirected Documents),
/// <c>sync.json</c> (Vegas 1 manifest), <c>backups/</c> and <c>.sync.lock</c>.
/// </para>
/// A title without <see cref="GameTitle.SaveSlots"/> (Vegas 2 today) has nothing to sync:
/// every operation returns <see cref="SyncResult.NothingToSync"/> without network or disk access.
/// </summary>
public sealed partial class RetailProfileSync : IDisposable
{
    private const string PecSlot = "VegasRetailPEC_v1";
    private const int ManifestLimit = 16384;

    private readonly CloudProfileTransport _transport;
    private readonly bool _ownsTransport;
    private readonly IReadOnlyList<KeyValuePair<string, string>> _slots;
    private readonly HashSet<string> _fileNames;
    private readonly string? _pec;

    /// <param name="paths">Installation layout; the owned root must be inside <see cref="AppPaths.Local"/>.</param>
    /// <param name="title">Game whose saves are synchronised.</param>
    /// <param name="ownedRoot">Defaults to <see cref="AppPaths.RetailAccounts"/>.</param>
    /// <param name="transport">Cloud transport; defaults to the live service origin.</param>
    /// <param name="gameRunning">Process check; defaults to <see cref="RetailProcesses.IsRunning"/> for the title.</param>
    public RetailProfileSync(AppPaths paths, GameTitle title, string? ownedRoot = null,
        CloudProfileTransport? transport = null, Func<bool>? gameRunning = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Title = title ?? throw new ArgumentNullException(nameof(title));
        var local = Path.GetFullPath(paths.Local);
        OwnedRoot = Path.GetFullPath(ownedRoot ?? paths.RetailAccounts);
        if (!IsWithin(local, Path.GetFullPath(paths.Root)) || !IsWithin(OwnedRoot, local))
        {
            throw new ProfileSyncException("retail_sync_root_must_be_local");
        }
        _transport = transport ?? new CloudProfileTransport();
        _ownsTransport = transport is null;
        GameRunning = gameRunning ?? (() => RetailProcesses.IsRunning(title));
        _slots = [.. title.SaveSlots];
        _fileNames = new HashSet<string>(title.SaveSlots.Values, StringComparer.Ordinal);
        // Only the Vegas 1 PEC archive format is grounded. Other titles' saves are not decoded.
        _pec = title.Key == TitleCatalog.Vegas1.Key && title.SaveSlots.TryGetValue(PecSlot, out var pec) ? pec : null;
    }

    public GameTitle Title { get; }

    public string OwnedRoot { get; }

    /// <summary>Process check used before and during every local write.</summary>
    public Func<bool> GameRunning { get; set; }

    /// <summary>False when the title has no synchronised saves yet.</summary>
    public bool HasSaveSlots => _slots.Count != 0;

    public string AccountDirectory(AccountSession session)
    {
        ValidateSession(session);
        return Safe(Path.Combine(OwnedRoot, session.DiscordId));
    }

    /// <summary>The game's redirected profile folder for this title.</summary>
    public string ProfileDirectory(AccountSession session) =>
        Safe(Path.Combine(AccountDirectory(session), "profile", Title.ProfileSubdirectory.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>The save whose presence means a character exists (Vegas 1 <c>R6_PECPEC</c>), or null.</summary>
    public string? ProgressionFile(AccountSession session) => _pec is null ? null : Path.Combine(ProfileDirectory(session), _pec);

    /// <summary>
    /// Pull then, when a local character exists, push. Raises
    /// <see cref="SyncConflictException"/> when the player must choose a character.
    /// </summary>
    public async Task<SyncResult> SynchronizeAsync(AccountSession session, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!HasSaveSlots)
        {
            progress?.Report(SyncMessages.NotAvailable);
            return SyncResult.NothingToSync(SyncOperation.Pull);
        }
        var result = await PullAsync(session, false, cancellationToken).ConfigureAwait(false);
        var progression = ProgressionFile(session);
        if (progression is not null && File.Exists(progression))
        {
            result = await PushAsync(session, false, cancellationToken).ConfigureAwait(false);
        }
        progress?.Report(SyncMessages.UpToDate);
        return result;
    }

    /// <summary>Applies the player's choice after a conflict. The replaced character is backed up.</summary>
    public async Task<SyncResult> ResolveConflictAsync(AccountSession session, ConflictChoice choice,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var result = choice switch
        {
            ConflictChoice.KeepCloud => await PullAsync(session, replaceLocal: true, cancellationToken).ConfigureAwait(false),
            ConflictChoice.KeepLocal => await PushAsync(session, replaceCloud: true, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(choice)),
        };
        progress?.Report(SyncMessages.ChoiceSaved);
        return result;
    }

    /// <summary>After the game exits: push the character if one exists. Returns null when none was created.</summary>
    public async Task<SyncResult?> SaveAfterGameAsync(AccountSession session, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!HasSaveSlots)
        {
            return null;
        }
        progress?.Report(SyncMessages.SavingAfterGame);
        var progression = ProgressionFile(session);
        var result = progression is not null && File.Exists(progression)
            ? await PushAsync(session, false, cancellationToken).ConfigureAwait(false)
            : null;
        progress?.Report(result is not null ? SyncMessages.SavedAfterGame : SyncMessages.NoCharacterCreated);
        return result;
    }

    /// <summary>
    /// Downloads the cloud character. Local files are replaced only when they are unchanged
    /// since the last sync (or <paramref name="replaceLocal"/>), or when only XP differs.
    /// </summary>
    public async Task<SyncResult> PullAsync(AccountSession session, bool replaceLocal = false, CancellationToken cancellationToken = default)
    {
        if (!HasSaveSlots)
        {
            return SyncResult.NothingToSync(SyncOperation.Pull);
        }
        RequireSupported();
        await VerifyIdentityAsync(session, cancellationToken).ConfigureAwait(false);
        using var _ = Lock(session);
        var manifest = ReadManifest(session);
        var local = ReadLocal(session);
        var (revision, remote, canonical) = await FetchAsync(session, cancellationToken).ConfigureAwait(false);
        var pec = _pec!;
        var xp = Math.Max(canonical, Math.Max(Xp(local[pec]), Xp(remote[pec])));
        var changes = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        var baseline = new Dictionary<string, string>(manifest.Baseline, StringComparer.Ordinal);
        foreach (var (name, incoming) in remote)
        {
            var current = local[name];
            if (incoming is null)
            {
                continue; // No implicit deletion or invention of a character.
            }
            var safe = replaceLocal || current is null || current.AsSpan().SequenceEqual(incoming)
                || Hash(current) == manifest.Baseline.GetValueOrDefault(name);
            if (safe)
            {
                changes[name] = incoming;
            }
            else if (SameAppearance(current, incoming, name))
            {
                changes[name] = current;
                safe = true;
            }
            else if (Hash(incoming) != manifest.Remote.GetValueOrDefault(name))
            {
                throw new SyncConflictException("retail_profile_conflict");
            }
            if (safe)
            {
                baseline[name] = Hash(changes[name])!;
            }
        }
        var selected = changes.TryGetValue(pec, out var chosen) ? chosen : local[pec];
        if (selected is not null)
        {
            changes[pec] = PecArchive.PatchXp(selected, xp);
            if (baseline.ContainsKey(pec) && (replaceLocal || local[pec] is null
                || Hash(local[pec]) == manifest.Baseline.GetValueOrDefault(pec) || SameAppearance(local[pec], remote[pec], pec)))
            {
                baseline[pec] = Hash(changes[pec])!;
            }
        }
        var updated = new Manifest(revision, baseline, Hashes(remote));
        var changed = Commit(session, local, changes, updated);
        return new SyncResult(SyncOperation.Pull, revision, xp, changed);
    }

    /// <summary>
    /// Uploads the local character against the current cloud revision. Fails with a
    /// conflict when the cloud changed since the last sync, unless <paramref name="replaceCloud"/>.
    /// </summary>
    public async Task<SyncResult> PushAsync(AccountSession session, bool replaceCloud = false, CancellationToken cancellationToken = default)
    {
        if (!HasSaveSlots)
        {
            return SyncResult.NothingToSync(SyncOperation.Push);
        }
        RequireSupported();
        await VerifyIdentityAsync(session, cancellationToken).ConfigureAwait(false);
        using var _ = Lock(session);
        var manifest = ReadManifest(session);
        var local = ReadLocal(session);
        var pec = _pec!;
        if (local[pec] is null)
        {
            throw new ProfileSyncException("native_profile_missing");
        }
        var (revision, remote, canonical) = await FetchAsync(session, cancellationToken).ConfigureAwait(false);
        foreach (var (name, current) in local)
        {
            if (current is null)
            {
                continue;
            }
            if (!replaceCloud && Hash(remote[name]) != manifest.Remote.GetValueOrDefault(name) && !SameAppearance(current, remote[name], name))
            {
                throw new SyncConflictException("cloud_profile_changed");
            }
        }
        if (replaceCloud)
        {
            // An explicit choice may replace the other character. Preserve its complete
            // native bytes before the revision-checked upload; identity and CRC checks still apply.
            var backup = Path.Combine(AccountDirectory(session), "backups", "cloud-" + NewId());
            foreach (var (name, value) in remote)
            {
                if (value is not null)
                {
                    WriteAtomic(Path.Combine(backup, name), value);
                }
            }
        }
        var xp = Math.Max(canonical, Math.Max(Xp(local[pec]), Xp(remote[pec])));
        // A missing local equipment file never deletes an existing remote template.
        // The native PEC itself must have been created in game.
        var uploaded = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        foreach (var (name, value) in local)
        {
            uploaded[name] = value ?? remote[name];
        }
        uploaded[pec] = PecArchive.PatchXp(local[pec]!, xp);
        var (status, response) = await RequestAsync(session, HttpMethod.Put, "/api/v1/account/profile",
            UploadBody(revision, uploaded, xp), cancellationToken).ConfigureAwait(false);
        if (status == 409)
        {
            throw new SyncConflictException("cloud_revision_conflict");
        }
        if (status != 200)
        {
            throw new ProfileSyncException("cloud_profile_upload_failed");
        }
        var (acknowledgedRevision, acknowledged, acknowledgedXp) = ParseCloud(response);
        if (acknowledgedRevision != revision + 1 || !SameFiles(acknowledged, uploaded))
        {
            throw new ProfileSyncException("cloud_profile_acknowledgement_invalid");
        }
        var finalXp = Math.Max(xp, acknowledgedXp);
        var final = new Dictionary<string, byte[]?>(local, StringComparer.Ordinal)
        {
            [pec] = PecArchive.PatchXp(uploaded[pec]!, finalXp),
        };
        var current2 = ReadLocal(session);
        var changedDuring = !SameFiles(current2, local);
        var updated = new Manifest(acknowledgedRevision, Hashes(final), Hashes(acknowledged));
        // A source edited during HTTP remains untouched. Baseline only the uploaded
        // snapshot so a later sync still sees those local changes.
        var changed = Commit(session, current2, changedDuring ? new Dictionary<string, byte[]?>() : final, updated);
        return new SyncResult(SyncOperation.Push, acknowledgedRevision, finalXp, changed, changedDuring);
    }

    public void Dispose()
    {
        if (_ownsTransport)
        {
            _transport.Dispose();
        }
    }

    // ---- Validation, identity and transport ----

    private void RequireSupported()
    {
        if (_pec is null)
        {
            // Save slots exist but their format is not grounded; never invent one.
            throw new ProfileSyncException("retail_sync_title_unsupported");
        }
    }

    private static void ValidateSession(AccountSession session)
    {
        if (session is null || session.DiscordId is null || session.Token is null || !SessionId().IsMatch(session.DiscordId)
            || !ulong.TryParse(session.DiscordId, NumberStyles.None, CultureInfo.InvariantCulture, out _)
            || !AccountRules.IsToken(session.Token))
        {
            throw new ProfileSyncException("invalid_account_session");
        }
    }

    private void Stopped()
    {
        bool running;
        try
        {
            running = GameRunning();
        }
        catch (Exception error)
        {
            throw new ProfileSyncException("cannot_verify_game_stopped", error);
        }
        if (running)
        {
            throw new ProfileSyncException("retail_game_must_be_stopped");
        }
    }

    private async Task<ServiceResponse> RequestAsync(AccountSession session, HttpMethod method, string path, byte[]? body,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _transport.SendAsync(method, path, session.Token, body, CloudProfileTransport.DefaultTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (ProfileSyncException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new ProfileSyncException("cloud_request_failed", error);
        }
    }

    private async Task VerifyIdentityAsync(AccountSession session, CancellationToken cancellationToken)
    {
        ValidateSession(session);
        var (status, response) = await RequestAsync(session, HttpMethod.Get, "/api/v1/account", null, cancellationToken).ConfigureAwait(false);
        if (status != 200 || !StrictJson.IsObject(response))
        {
            throw new ProfileSyncException("cloud_identity_unverified");
        }
        if (StrictJson.IdText(StrictJson.Get(response!.Value, "id")) != session.DiscordId)
        {
            throw new ProfileSyncException("cloud_identity_mismatch");
        }
    }

    private async Task<(long Revision, Dictionary<string, byte[]?> Files, uint Experience)> FetchAsync(AccountSession session, CancellationToken cancellationToken)
    {
        var (status, response) = await RequestAsync(session, HttpMethod.Get, "/api/v1/account/profile", null, cancellationToken).ConfigureAwait(false);
        if (status != 200)
        {
            throw new ProfileSyncException("cloud_profile_read_failed");
        }
        return ParseCloud(response);
    }

    private (long Revision, Dictionary<string, byte[]?> Files, uint Experience) ParseCloud(JsonElement? response)
    {
        try
        {
            if (!StrictJson.IsObject(response))
            {
                throw new FormatException();
            }
            var root = response!.Value;
            if (!root.TryGetProperty("revision", out var revisionElement) || !root.TryGetProperty("data", out var data)
                || !StrictJson.TryGetInteger(revisionElement, out var revision) || revision < 0 || data.ValueKind != JsonValueKind.Object
                || !StrictJson.TryGetInteger(StrictJson.Get(data, "schemaVersion"), out var schema) || schema != 1
                || StrictJson.Get(data, "files") is not { ValueKind: JsonValueKind.Object } files
                || StrictJson.Get(data, "preferences") is not { ValueKind: JsonValueKind.Object })
            {
                throw new FormatException();
            }
            var fileCount = 0;
            foreach (var file in files.EnumerateObject())
            {
                if (file.Value.ValueKind != JsonValueKind.String || ++fileCount > 256)
                {
                    throw new FormatException();
                }
            }
            foreach (var property in data.EnumerateObject())
            {
                if (property.Name is not ("schemaVersion" or "files" or "preferences" or "progression"))
                {
                    throw new FormatException();
                }
            }
            long canonical = 0;
            if (data.TryGetProperty("progression", out var progression))
            {
                if (progression.ValueKind != JsonValueKind.Object
                    || progression.EnumerateObject().Count() != 2
                    || !progression.TryGetProperty("schemaVersion", out _) || !progression.TryGetProperty("totalExperience", out _)
                    || !StrictJson.TryGetInteger(progression.GetProperty("schemaVersion"), out var progressionSchema) || progressionSchema != 1
                    || !StrictJson.TryGetInteger(progression.GetProperty("totalExperience"), out canonical) || canonical is < 0 or > uint.MaxValue)
                {
                    throw new FormatException();
                }
            }
            var result = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
            foreach (var (slot, name) in _slots)
            {
                if (!files.TryGetProperty(slot, out var encoded))
                {
                    result[name] = null;
                    continue;
                }
                var text = encoded.GetString()!;
                if (text.Length > (PecArchive.MaxBytes + 2) / 3 * 4)
                {
                    throw new FormatException();
                }
                var bytes = DecodeBase64Strict(text);
                if (bytes.Length > PecArchive.MaxBytes)
                {
                    throw new FormatException();
                }
                result[name] = bytes;
            }
            if (result[_pec!] is { } pec)
            {
                PecArchive.ReadXp(pec);
            }
            return (revision, result, (uint)canonical);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new ProfileSyncException("cloud_profile_invalid", error);
        }
    }

    private byte[] UploadBody(long revision, Dictionary<string, byte[]?> uploaded, uint xp)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("revision", revision);
            writer.WriteStartObject("data");
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteStartObject("files");
            foreach (var (slot, name) in _slots)
            {
                if (uploaded[name] is { } value)
                {
                    writer.WriteBase64String(slot, value);
                }
            }
            writer.WriteEndObject();
            writer.WriteStartObject("preferences");
            writer.WriteEndObject();
            writer.WriteStartObject("progression");
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteNumber("totalExperience", xp);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    // ---- Local files ----

    private sealed record Manifest(long Revision, Dictionary<string, string> Baseline, Dictionary<string, string> Remote);

    private string ManifestPath(AccountSession session) => Path.Combine(AccountDirectory(session),
        Title.Key == TitleCatalog.Vegas1.Key ? "sync.json" : $"sync-{Title.Key}.json");

    private Manifest ReadManifest(AccountSession session)
    {
        var data = ReadBounded(ManifestPath(session), ManifestLimit);
        if (data is null)
        {
            return new Manifest(0, new(StringComparer.Ordinal), new(StringComparer.Ordinal));
        }
        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (!StrictJson.TryGetInteger(root.GetProperty("version"), out var version) || version != 1
                || root.GetProperty("discordId") is not { ValueKind: JsonValueKind.String } owner || owner.GetString() != session.DiscordId
                || !StrictJson.TryGetInteger(root.GetProperty("revision"), out var revision) || revision < 0)
            {
                throw new FormatException();
            }
            return new Manifest(revision, ReadHashes(root.GetProperty("baselineHashes")), ReadHashes(root.GetProperty("remoteHashes")));
        }
        catch (Exception error)
        {
            throw new ProfileSyncException("retail_sync_owner_manifest_invalid", error);
        }
    }

    private Dictionary<string, string> ReadHashes(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException();
        }
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            var hash = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            if (!_fileNames.Contains(property.Name) || hash is null || !HashText().IsMatch(hash))
            {
                throw new FormatException();
            }
            result[property.Name] = hash;
        }
        return result;
    }

    private static byte[] ManifestBytes(AccountSession session, Manifest manifest)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            // Sorted keys, like the Python json.dumps(sort_keys=True).
            writer.WriteStartObject();
            WriteHashes(writer, "baselineHashes", manifest.Baseline);
            writer.WriteString("discordId", session.DiscordId);
            WriteHashes(writer, "remoteHashes", manifest.Remote);
            writer.WriteNumber("revision", manifest.Revision);
            writer.WriteNumber("version", 1);
            writer.WriteEndObject();
        }
        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    private static void WriteHashes(Utf8JsonWriter writer, string name, Dictionary<string, string> hashes)
    {
        writer.WriteStartObject(name);
        foreach (var (file, hash) in hashes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WriteString(file, hash);
        }
        writer.WriteEndObject();
    }

    private Dictionary<string, byte[]?> ReadLocal(AccountSession session)
    {
        var directory = ProfileDirectory(session);
        var files = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        foreach (var (_, name) in _slots)
        {
            files[name] = ReadBounded(Path.Combine(directory, name), PecArchive.MaxBytes);
        }
        if (files[_pec!] is { } pec && !PecArchive.TryReadXp(pec, out _))
        {
            throw new ProfileSyncException("local_pec_invalid");
        }
        return files;
    }

    private byte[]? ReadBounded(string path, int maximum)
    {
        path = Safe(path);
        if (!File.Exists(path))
        {
            return null;
        }
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > maximum)
            {
                throw new ProfileSyncException("local_profile_too_large");
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private IDisposable Lock(AccountSession session)
    {
        Stopped();
        var directory = AccountDirectory(session);
        Directory.CreateDirectory(directory);
        var lockPath = Safe(Path.Combine(directory, ".sync.lock"));
        try
        {
            new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None).Dispose();
        }
        catch (IOException) when (File.Exists(lockPath))
        {
            throw new ProfileSyncException("retail_sync_already_running");
        }
        return new LockRelease(this, lockPath);
    }

    private sealed class LockRelease(RetailProfileSync owner, string path) : IDisposable
    {
        public void Dispose() => File.Delete(owner.Safe(path));
    }

    private void WriteAtomic(string path, byte[] data)
    {
        path = Safe(path);
        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        var temporary = Safe(Path.Combine(parent, ".sync-" + NewId()));
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(data);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private IReadOnlyList<string> Commit(AccountSession session, Dictionary<string, byte[]?> before,
        Dictionary<string, byte[]?> changes, Manifest manifest)
    {
        Stopped();
        if (!SameFiles(ReadLocal(session), before))
        {
            throw new SyncConflictException("local_profile_changed_during_sync");
        }
        var directory = ProfileDirectory(session);
        var changed = changes.Where(pair => pair.Value is not null && !Equal(pair.Value, before[pair.Key]))
            .Select(pair => (Name: pair.Key, Data: pair.Value!)).ToList();
        // Backups are durable and private before any original file is replaced.
        if (changed.Count != 0)
        {
            var backup = Path.Combine(AccountDirectory(session), "backups", NewId());
            foreach (var (name, _) in changed)
            {
                if (before[name] is { } original)
                {
                    WriteAtomic(Path.Combine(backup, name), original);
                }
            }
        }
        var replaced = new List<string>();
        try
        {
            foreach (var (name, data) in changed)
            {
                Stopped();
                WriteAtomic(Path.Combine(directory, name), data);
                replaced.Add(name);
            }
            Stopped();
            WriteAtomic(ManifestPath(session), ManifestBytes(session, manifest));
        }
        catch
        {
            for (var index = replaced.Count - 1; index >= 0; index--)
            {
                var name = replaced[index];
                if (before[name] is { } original)
                {
                    WriteAtomic(Path.Combine(directory, name), original);
                }
                else
                {
                    File.Delete(Safe(Path.Combine(directory, name)));
                }
            }
            throw;
        }
        return [.. changed.Select(item => item.Name).Order(StringComparer.Ordinal)];
    }

    // ---- Path safety ----

    /// <summary>Rejects paths outside the owned root and any link or junction below it.</summary>
    private string Safe(string path)
    {
        var full = Path.GetFullPath(path);
        if (!IsWithin(full, OwnedRoot))
        {
            throw new ProfileSyncException("retail_sync_path_escaped");
        }
        var current = OwnedRoot;
        var relative = Path.GetRelativePath(OwnedRoot, full);
        if (relative == ".")
        {
            return full;
        }
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                break;
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ProfileSyncException("retail_sync_symlink_rejected");
            }
        }
        return full;
    }

    private static bool IsWithin(string path, string root)
    {
        var trimmedRoot = Path.TrimEndingDirectorySeparator(root);
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        return trimmed.Equals(trimmedRoot, StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith(trimmedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Helpers ----

    private bool SameAppearance(byte[]? first, byte[]? second, string name)
    {
        if (first is null || second is null)
        {
            return first is null && second is null;
        }
        // Both buffers are checksum/version validated before reaching this helper.
        return name == _pec ? PecArchive.SameAppearance(first, second) : first.AsSpan().SequenceEqual(second);
    }

    private static uint Xp(byte[]? pec) => pec is null ? 0 : PecArchive.ReadXp(pec);

    private static bool Equal(byte[]? first, byte[]? second) =>
        first is null || second is null ? first is null && second is null : first.AsSpan().SequenceEqual(second);

    private static bool SameFiles(Dictionary<string, byte[]?> first, Dictionary<string, byte[]?> second) =>
        first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out var other) && Equal(pair.Value, other));

    private static string? Hash(byte[]? data) => data is null ? null : Convert.ToHexStringLower(SHA256.HashData(data));

    private static Dictionary<string, string> Hashes(Dictionary<string, byte[]?> files) =>
        files.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => Hash(pair.Value)!, StringComparer.Ordinal);

    private static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>Python base64.b64decode(validate=True): alphabet and padding only, no whitespace.</summary>
    private static byte[] DecodeBase64Strict(string text)
    {
        if (text.Length % 4 != 0 || !Base64Text().IsMatch(text))
        {
            throw new FormatException();
        }
        return Convert.FromBase64String(text);
    }

    [GeneratedRegex(@"^[1-9][0-9]{0,19}\z")]
    private static partial Regex SessionId();

    [GeneratedRegex(@"^[0-9a-f]{64}\z")]
    private static partial Regex HashText();

    [GeneratedRegex(@"^[A-Za-z0-9+/]*={0,2}\z")]
    private static partial Regex Base64Text();
}
