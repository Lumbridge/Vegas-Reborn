using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VegasReborn.Core.Account;
using VegasReborn.Core.Profiles;
using VegasReborn.Core.Profiles.Vegas1;
using VegasReborn.Core.Titles;

namespace VegasReborn.Core.Tests.Profiles;

/// <summary>Synthetic codec and fake-account-service checks, not retail interoperability.</summary>
public sealed class RetailProfileSyncTests : IDisposable
{
    private const string Pec = "R6_PECPEC";
    private const string Equip = "R6_EquipEquipmentTemplate";
    private static readonly AccountSession Session = new("123456789", "Local Tester", new string('a', 64));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "vr-sync-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly string _owned;
    private readonly FakeCloud _cloud;
    private readonly CloudProfileTransport _transport;
    private readonly RetailProfileSync _sync;

    public RetailProfileSyncTests()
    {
        _paths = new AppPaths(_root);
        _owned = Path.Combine(_paths.Local, "sync-unit");
        Directory.CreateDirectory(_owned);
        _cloud = new FakeCloud(Session.DiscordId, Session.Token);
        _transport = new CloudProfileTransport("https://vegas.example.invalid", _cloud);
        _sync = new RetailProfileSync(_paths, TitleCatalog.Vegas1, _owned, _transport, () => false);
    }

    public void Dispose()
    {
        _sync.Dispose();
        _transport.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    internal static byte[] Fixture(uint xp = 10, string appearance = "synthetic appearance")
    {
        var look = Encoding.ASCII.GetBytes(appearance);
        var raw = new byte[9 + look.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(raw, 0x19);
        raw[4] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(5), xp);
        look.CopyTo(raw, 9);
        var payload = new byte[8 + raw.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 6);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), (uint)raw.Length);
        raw.CopyTo(payload, 8);
        var result = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, PecArchive.Checksum(payload));
        payload.CopyTo(result, 4);
        return result;
    }

    private string Profile => _sync.ProfileDirectory(Session);

    private string Write(string name, byte[] data)
    {
        var path = Path.Combine(Profile, name);
        Directory.CreateDirectory(Profile);
        File.WriteAllBytes(path, data);
        return path;
    }

    private void Remote(string name, byte[] data)
    {
        var slot = TitleCatalog.Vegas1.SaveSlots.Single(pair => pair.Value == name).Key;
        _cloud.Files[slot] = Convert.ToBase64String(data);
    }

    private byte[] RemoteBytes(string slot) => Convert.FromBase64String((string)_cloud.Files[slot]!);

    private bool AnyPut => _cloud.Calls.Any(call => call.Method == "PUT");

    private static async Task<T> Fails<T>(Func<Task> action, string? code = null) where T : ProfileSyncException
    {
        var error = await Assert.ThrowsAsync<T>(action);
        if (code is not null)
        {
            Assert.Equal(code, error.Code);
        }
        return error;
    }

    [Fact]
    public async Task IdentityBeforeAnyLocalWrite()
    {
        _cloud.Identity = "987654321";
        await Fails<ProfileSyncException>(() => _sync.PullAsync(Session), "cloud_identity_mismatch");
        Assert.Empty(Directory.EnumerateFileSystemEntries(_owned));
        Assert.Single(_cloud.Calls);
    }

    [Fact]
    public async Task PullNativeBlobAndCanonicalMaxWithoutTouchingRemake()
    {
        var native = Fixture(100);
        Remote(Pec, native);
        Remote(Equip, Encoding.ASCII.GetBytes("opaque equipment"));
        _cloud.Files["VegasPEC_v1"] = "UE-opaque-never-decoded";
        _cloud.SetExperience(500);
        var result = await _sync.PullAsync(Session);
        var saved = File.ReadAllBytes(Path.Combine(Profile, Pec));
        Assert.Equal(500u, PecArchive.ReadXp(saved));
        Assert.Equal(native[4..17], saved[4..17]);
        Assert.Equal(native[21..], saved[21..]);
        Assert.Equal(new[] { Pec, Equip }.Order(StringComparer.Ordinal), result.FilesChanged);
        Assert.Equal("opaque equipment", File.ReadAllText(Path.Combine(Profile, Equip)));
        Assert.Equal("UE-opaque-never-decoded", (string)_cloud.Files["VegasPEC_v1"]!);
        Assert.All(_cloud.Calls, call => Assert.Equal("GET", call.Method));
        Assert.EndsWith(Path.Combine("profile", "Ubisoft", "R6Vegas"), Profile);
    }

    [Fact]
    public async Task NoFabricatedNativeCharacter()
    {
        _cloud.SetExperience(500);
        Assert.Equal(500u, (await _sync.PullAsync(Session)).Experience);
        Assert.False(File.Exists(Path.Combine(Profile, Pec)));
        await Fails<ProfileSyncException>(() => _sync.PushAsync(Session), "native_profile_missing");
    }

    [Fact]
    public async Task CrcInvalidLocalAndRemotePreserveExisting()
    {
        var path = Write(Pec, Fixture());
        var original = File.ReadAllBytes(path);
        var corrupt = (byte[])original.Clone();
        corrupt[^1] ^= 1;
        Remote(Pec, corrupt);
        await Fails<ProfileSyncException>(() => _sync.PullAsync(Session), "cloud_profile_invalid");
        Assert.Equal(original, File.ReadAllBytes(path));
        File.WriteAllText(path, "invalid local");
        await Fails<ProfileSyncException>(() => _sync.PushAsync(Session), "local_pec_invalid");
        Assert.False(AnyPut);
    }

    [Fact]
    public async Task UnsyncedAppearanceConflictKeepsBoth()
    {
        Remote(Pec, Fixture());
        await _sync.PullAsync(Session);
        var local = Fixture(30, "local unsynced");
        var path = Write(Pec, local);
        Remote(Pec, Fixture(40, "other machine"));
        var conflict = await Fails<SyncConflictException>(() => _sync.PullAsync(Session), "retail_profile_conflict");
        Assert.Equal(SyncConflictException.ConflictMessage, conflict.Message);
        await Fails<SyncConflictException>(() => _sync.PushAsync(Session), "cloud_profile_changed");
        Assert.Equal(local, File.ReadAllBytes(path));
        Assert.False(AnyPut);
    }

    [Fact]
    public async Task SafeRemoteAppearanceRestoreAndPrivateBackup()
    {
        var initial = Fixture();
        Remote(Pec, initial);
        await _sync.PullAsync(Session);
        var replacement = Fixture(15, "saved on other machine");
        Remote(Pec, replacement);
        await _sync.PullAsync(Session);
        Assert.Equal(replacement, File.ReadAllBytes(Path.Combine(Profile, Pec)));
        var backups = Directory.GetFiles(Path.Combine(_sync.AccountDirectory(Session), "backups"), Pec, SearchOption.AllDirectories);
        Assert.Single(backups);
        Assert.Equal(initial, File.ReadAllBytes(backups[0]));
    }

    [Fact]
    public async Task PullKeepsUnsyncedLocalWhenRemoteUnchanged()
    {
        Remote(Pec, Fixture());
        await _sync.PullAsync(Session);
        var local = Fixture(900, "new local customization");
        var path = Write(Pec, local);
        _cloud.SetExperience(1000);
        await _sync.PullAsync(Session);
        Assert.Equal(PecArchive.PatchXp(local, 1000), File.ReadAllBytes(path));
        await _sync.PushAsync(Session);
        Assert.Equal(PecArchive.PatchXp(local, 1000), RemoteBytes("VegasRetailPEC_v1"));
    }

    [Fact]
    public async Task PushPreservesOtherFamilyPreferencesAndRemoteEquipment()
    {
        var path = Write(Pec, Fixture(600));
        Remote(Equip, Encoding.ASCII.GetBytes("remote equipment preserved"));
        _cloud.Files["VegasPEC_v1"] = "UE receipt ledger bytes";
        var result = await _sync.PushAsync(Session);
        Assert.Equal(600u, result.Experience);
        Assert.Equal("UE receipt ledger bytes", (string)_cloud.Files["VegasPEC_v1"]!);
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["remakeSetting"] = true }, _cloud.Data["preferences"]));
        Assert.Equal("remote equipment preserved", Encoding.ASCII.GetString(RemoteBytes("VegasRetailEquipment_v1")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, Equip)));
        var sent = _cloud.Calls[^1].Body!;
        Assert.Equal("0", sent["revision"]!.ToJsonString());
        Assert.Equal(FakeCloud.OwnSlots.Order(), sent["data"]!["files"]!.AsObject().Select(pair => pair.Key).Order());
        var manifest = File.ReadAllText(Path.Combine(_sync.AccountDirectory(Session), "sync.json"));
        Assert.DoesNotContain(Session.Token, manifest);
        using var document = JsonDocument.Parse(manifest);
        Assert.Equal(["baselineHashes", "discordId", "remoteHashes", "revision", "version"],
            document.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task AcknowledgedHigherXpUpdatesOnlyXpAndCrc()
    {
        var original = Fixture(50);
        var path = Write(Pec, original);
        _cloud.AckXp = 700;
        Assert.Equal(700u, (await _sync.PushAsync(Session)).Experience);
        Assert.Equal(PecArchive.PatchXp(original, 700), File.ReadAllBytes(path));
    }

    [Fact]
    public async Task SourceChangedDuringPutNotOverwritten()
    {
        var path = Write(Pec, Fixture(50));
        var edited = Fixture(80, "edited during HTTP");
        _cloud.OnPut = () => File.WriteAllBytes(path, edited);
        _cloud.AckXp = 700;
        var result = await _sync.PushAsync(Session);
        Assert.True(result.LocalChangedDuringUpload);
        Assert.Equal(edited, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task RevisionConflictNotRetriedOrForced()
    {
        var path = Write(Pec, Fixture(50));
        _cloud.Conflict = true;
        await Fails<SyncConflictException>(() => _sync.PushAsync(Session), "cloud_revision_conflict");
        Assert.Equal(50u, PecArchive.ReadXp(File.ReadAllBytes(path)));
        Assert.Equal(1, _cloud.Calls.Count(call => call.Method == "PUT"));
        Assert.False(File.Exists(Path.Combine(_sync.AccountDirectory(Session), "sync.json")));
        Assert.False(File.Exists(Path.Combine(_sync.AccountDirectory(Session), ".sync.lock")));
    }

    [Fact]
    public async Task GameStartedDuringHttpBlocksLocalSaveChanges()
    {
        var original = Fixture(50);
        var path = Write(Pec, original);
        var running = false;
        _sync.GameRunning = () => running;
        _cloud.OnPut = () => running = true;
        _cloud.AckXp = 700;
        await Fails<ProfileSyncException>(() => _sync.PushAsync(Session), "retail_game_must_be_stopped");
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(File.Exists(Path.Combine(_sync.AccountDirectory(Session), "sync.json")));
    }

    [Fact]
    public async Task Uint32MaxAndBadSchemaTypes()
    {
        Remote(Pec, Fixture(uint.MaxValue));
        Assert.Equal(uint.MaxValue, (await _sync.PullAsync(Session)).Experience);
        Assert.Equal(uint.MaxValue, (await _sync.PushAsync(Session)).Experience);
        _cloud.Data["progression"]!["schemaVersion"] = true;
        await Fails<ProfileSyncException>(() => _sync.PullAsync(Session), "cloud_profile_invalid");
    }

    [Fact]
    public async Task ProcessGuardAndPathLimits()
    {
        using (var sync = new RetailProfileSync(_paths, TitleCatalog.Vegas1, _owned, _transport, () => true))
        {
            await Fails<ProfileSyncException>(() => sync.PullAsync(Session), "retail_game_must_be_stopped");
        }
        Assert.Empty(Directory.EnumerateFileSystemEntries(_owned));
        using (var sync = new RetailProfileSync(_paths, TitleCatalog.Vegas1, _owned, _transport, () => throw new InvalidOperationException()))
        {
            await Fails<ProfileSyncException>(() => sync.PullAsync(Session), "cannot_verify_game_stopped");
        }
        var error = Assert.Throws<ProfileSyncException>(() => new RetailProfileSync(_paths, TitleCatalog.Vegas1, Path.Combine(_root, "src"), _transport));
        Assert.Equal("retail_sync_root_must_be_local", error.Code);
        Assert.Equal(ProfileSyncException.GenericMessage, error.Message);
        await Fails<ProfileSyncException>(() => _sync.PullAsync(Session with { DiscordId = "../escape" }), "invalid_account_session");
        await Fails<ProfileSyncException>(() => _sync.PullAsync(Session with { DiscordId = "18446744073709551616" }), "invalid_account_session");
    }

    [Fact]
    public async Task ConcurrentSyncIsRejectedByTheLock()
    {
        var account = _sync.AccountDirectory(Session);
        Directory.CreateDirectory(account);
        File.WriteAllText(Path.Combine(account, ".sync.lock"), "");
        await Fails<ProfileSyncException>(() => _sync.PullAsync(Session), "retail_sync_already_running");
        Assert.True(File.Exists(Path.Combine(account, ".sync.lock")));
    }

    [Fact]
    public async Task StrictCanonicalAndOwnerValidation()
    {
        foreach (var value in new JsonNode?[] { -1, 1L << 32, true, 1.5, "100" })
        {
            _cloud.SetExperience(value);
            await Fails<ProfileSyncException>(() => _sync.PullAsync(Session), "cloud_profile_invalid");
        }
        _cloud.SetExperience(0);
        await _sync.PullAsync(Session);
        var manifest = Path.Combine(_sync.AccountDirectory(Session), "sync.json");
        var data = JsonNode.Parse(File.ReadAllText(manifest))!;
        data["discordId"] = "another account";
        File.WriteAllText(manifest, data.ToJsonString());
        await Fails<ProfileSyncException>(() => _sync.PushAsync(Session), "retail_sync_owner_manifest_invalid");
    }

    [Fact]
    public async Task StrictCloudBase64AndUnknownDataKeys()
    {
        foreach (var encoded in new[] { "QQ", "QQ==\n", "Q Q==", "@@@@" })
        {
            _cloud.Files["VegasRetailEquipment_v1"] = encoded;
            await Fails<ProfileSyncException>(() => _sync.PullAsync(Session), "cloud_profile_invalid");
        }
        _cloud.Files.Remove("VegasRetailEquipment_v1");
        _cloud.Data["unexpected"] = 1;
        await Fails<ProfileSyncException>(() => _sync.PullAsync(Session), "cloud_profile_invalid");
    }

    [Fact]
    public async Task PythonWrittenManifestIsAccepted()
    {
        var local = Fixture(20);
        Write(Pec, local);
        Remote(Pec, local);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(local));
        var account = _sync.AccountDirectory(Session);
        File.WriteAllText(Path.Combine(account, "sync.json"),
            $$"""{"baselineHashes": {"R6_PECPEC": "{{hash}}"}, "discordId": "123456789", "remoteHashes": {"R6_PECPEC": "{{hash}}"}, "revision": 0, "version": 1}""" + "\n");
        var result = await _sync.SynchronizeAsync(Session);
        Assert.Equal(SyncOperation.Push, result.Operation);
        Assert.Equal(1, result.Revision);
    }

    [Fact]
    public async Task ExplicitCloudChoicePreservesLocalBackupAndHigherXp()
    {
        var local = Fixture(900, "local appearance");
        var remote = Fixture(500, "cloud appearance");
        Write(Pec, local);
        Remote(Pec, remote);
        await Fails<SyncConflictException>(() => _sync.PullAsync(Session));
        var messages = new List<string>();
        await _sync.ResolveConflictAsync(Session, ConflictChoice.KeepCloud, new Progress(messages));
        Assert.Equal([SyncMessages.ChoiceSaved], messages);
        Assert.Equal(PecArchive.PatchXp(remote, 900), File.ReadAllBytes(Path.Combine(Profile, Pec)));
        var backups = Directory.GetFiles(Path.Combine(_sync.AccountDirectory(Session), "backups"), Pec, SearchOption.AllDirectories);
        Assert.Contains(backups, path => File.ReadAllBytes(path).SequenceEqual(local));
    }

    [Fact]
    public async Task ExplicitLocalChoicePreservesCloudBackupAndRevisionCheck()
    {
        var local = Fixture(500, "local appearance");
        var remote = Fixture(1100, "cloud appearance");
        var path = Write(Pec, local);
        Remote(Pec, remote);
        await Fails<SyncConflictException>(() => _sync.PushAsync(Session));
        _cloud.Conflict = true;
        await Fails<SyncConflictException>(() => _sync.PushAsync(Session, replaceCloud: true), "cloud_revision_conflict");
        Assert.Equal(local, File.ReadAllBytes(path));
        _cloud.Conflict = false;
        await _sync.ResolveConflictAsync(Session, ConflictChoice.KeepLocal);
        Assert.Equal(PecArchive.PatchXp(local, 1100), File.ReadAllBytes(path));
        Assert.Equal(PecArchive.PatchXp(local, 1100), RemoteBytes("VegasRetailPEC_v1"));
        var backups = Directory.GetFiles(Path.Combine(_sync.AccountDirectory(Session), "backups"), Pec, SearchOption.AllDirectories);
        Assert.Contains(backups, item => File.ReadAllBytes(item).SequenceEqual(remote));
    }

    [Fact]
    public async Task SynchronizeAndSaveAfterGameReportCompanionMessages()
    {
        var messages = new List<string>();
        Assert.Null(await _sync.SaveAfterGameAsync(Session, new Progress(messages)));
        Assert.Equal([SyncMessages.SavingAfterGame, SyncMessages.NoCharacterCreated], messages);
        messages.Clear();
        Write(Pec, Fixture(5));
        Assert.NotNull(await _sync.SaveAfterGameAsync(Session, new Progress(messages)));
        Assert.Equal([SyncMessages.SavingAfterGame, SyncMessages.SavedAfterGame], messages);
        messages.Clear();
        var result = await _sync.SynchronizeAsync(Session, new Progress(messages));
        Assert.Equal([SyncMessages.UpToDate], messages);
        Assert.Equal(SyncOperation.Push, result.Operation);
    }

    [Fact]
    public async Task Vegas2HasNothingToSyncAndTouchesNothing()
    {
        using var sync = new RetailProfileSync(_paths, TitleCatalog.Vegas2, _owned, _transport, () => throw new InvalidOperationException("not called"));
        Assert.False(sync.HasSaveSlots);
        Assert.Null(sync.ProgressionFile(Session));
        Assert.EndsWith(Path.Combine("profile", "My Games", "Ubisoft", "R6Vegas2"), sync.ProfileDirectory(Session));
        var pull = await sync.PullAsync(Session);
        var push = await sync.PushAsync(Session, replaceCloud: true);
        Assert.False(pull.HasSaveSlots);
        Assert.False(push.HasSaveSlots);
        Assert.Empty(pull.FilesChanged);
        var messages = new List<string>();
        Assert.False((await sync.SynchronizeAsync(Session, new Progress(messages))).HasSaveSlots);
        Assert.Equal([SyncMessages.NotAvailable], messages);
        Assert.Null(await sync.SaveAfterGameAsync(Session));
        Assert.Empty(_cloud.Calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_owned));
    }

    [Fact]
    public async Task UngroundedSaveSlotsAreRefusedNotInvented()
    {
        var invented = TitleCatalog.Vegas2 with { SaveSlots = new Dictionary<string, string> { ["Vegas2Slot"] = "Profile.bin" } };
        using var sync = new RetailProfileSync(_paths, invented, _owned, _transport, () => false);
        await Fails<ProfileSyncException>(() => sync.PullAsync(Session), "retail_sync_title_unsupported");
        await Fails<ProfileSyncException>(() => sync.PushAsync(Session), "retail_sync_title_unsupported");
        Assert.Empty(_cloud.Calls);
    }

    [Fact]
    public async Task TransportRejectsRedirectsOversizeAndForeignRoutes()
    {
        using (var transport = new CloudProfileTransport("https://vegas.example.invalid", new Account.FakeHandler((302, null))))
        using (var sync = new RetailProfileSync(_paths, TitleCatalog.Vegas1, _owned, transport, () => false))
        {
            await Fails<ProfileSyncException>(() => sync.PullAsync(Session), "cloud_redirect_rejected");
        }
        using (var transport = new CloudProfileTransport("https://vegas.example.invalid", new Account.FakeHandler((200, "\"" + new string('x', 16384) + "\""))))
        using (var sync = new RetailProfileSync(_paths, TitleCatalog.Vegas1, _owned, transport, () => false))
        {
            await Fails<ProfileSyncException>(() => sync.PullAsync(Session), "cloud_response_too_large");
        }
        Assert.Equal("invalid_https_cloud_origin",
            Assert.Throws<ProfileSyncException>(() => new CloudProfileTransport("http://example.test")).Code);
    }

    [Fact]
    public void RetailProcessNamesCoverBothTitles()
    {
        Assert.Equal("R6Vegas_Game.exe", RetailProcesses.ImageName(TitleCatalog.Vegas1));
        Assert.Equal("R6Vegas2_Game.exe", RetailProcesses.ImageName(TitleCatalog.Vegas2));
        _ = RetailProcesses.IsAnyRunning();
    }

    private sealed class Progress(List<string> messages) : IProgress<string>
    {
        public void Report(string value) => messages.Add(value);
    }
}
