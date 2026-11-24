using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using VegasReborn.Core.Account;

namespace VegasReborn.Core.Tests.Account;

public sealed class DiscordClientTests : IDisposable
{
    private const string Origin = "https://vegas.example.invalid";
    private static readonly string Token = new('a', 64);
    private const string Id = "123456789012345678";
    private const string Account = """{"id":"123456789012345678","name":"Example"}""";
    private const string Link = """{"code":"ABCD12","secret":"private-poll-secret","url":"https://vegas.example.invalid/link/ABCD12","expiresIn":300,"pollSeconds":3}""";
    private static readonly AccountSession Session = new(Id, "Example", Token);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vr-account-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static string With(string json, string key, JsonNode? value)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        node[key] = value;
        return node.ToJsonString();
    }

    private static DiscordClient Client(FakeHandler handler, FakeClock? clock = null) =>
        clock is null ? new DiscordClient(Origin, handler) : new DiscordClient(Origin, handler, clock.Read, clock.Delay);

    private static async Task<AccountException> Fails(Func<Task> action, string? code = null)
    {
        var error = await Assert.ThrowsAsync<AccountException>(action);
        if (code is not null)
        {
            Assert.Equal(code, error.Code);
        }
        return error;
    }

    [Fact]
    public async Task ClaimMetadataRefreshExplicitClaimAndLegacyFallback()
    {
        var unclaimed = """{"id":"123456789012345678","name":"Example","nameClaimsEnabled":true,"claimedName":null,"claimRequired":true,"nameChangeAllowed":null}""";
        var claimed = """{"id":"123456789012345678","name":"Example","nameClaimsEnabled":true,"claimedName":"Test_Name","claimRequired":false,"nameChangeAllowed":"2026-12-16T10:00:00Z"}""";
        var handler = new FakeHandler((200, unclaimed), (200, claimed), (200, Account));
        using var client = Client(handler);
        var pending = await client.RefreshAccountAsync(Session);
        Assert.True(pending.ClaimRequired);
        Assert.True(pending.NameClaimsEnabled);
        Assert.True(pending.NeedsNameClaim);
        Assert.Equal(AccountMessages.ChooseName, AccountMessages.Ready(pending));
        var result = await client.ClaimNameAsync(pending, " Test_Name ");
        Assert.Equal(Session.DiscordId, result.DiscordId);
        Assert.Equal(Session.DisplayName, result.DisplayName);
        Assert.Equal("Test_Name", result.ClaimedName);
        Assert.False(result.ClaimRequired);
        Assert.Equal(new RecordedRequest("POST", "/api/v1/account/name", """{"name":"Test_Name"}""", Token), handler.Calls[1]);
        Assert.Equal("GET", handler.Calls[0].Method);
        Assert.Null(handler.Calls[0].Body);
        Assert.True(result.IsRenameOnCooldown(DateTimeOffset.Parse("2026-12-01T00:00:00Z")));
        Assert.False(result.IsRenameOnCooldown(DateTimeOffset.Parse("2026-12-17T00:00:00Z")));

        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "session.json");
        AccountSessionStore.Save(path, result);
        Assert.Equal(result, AccountSessionStore.Load(path));
        Assert.Equal(Session, await client.RefreshAccountAsync(result));
    }

    [Fact]
    public async Task ClaimValidationErrorsAndIdentityConfusionFailClosed()
    {
        foreach (var name in new[] { "ry", "", "bad name", "_invalid", "åbc", "toolongname_abcdefg" })
        {
            var handler = new FakeHandler();
            using var client = Client(handler);
            await Fails(() => client.ClaimNameAsync(Session, name), "invalid_player_name");
            Assert.Empty(handler.Calls);
        }
        foreach (var (status, code) in new[] { (400, "invalid_player_name"), (401, "account_session_expired_or_revoked"),
                     (403, "player_name_change_too_soon"), (409, "player_name_taken") })
        {
            using var client = Client(new FakeHandler((status, """{"error":"not printed"}""")));
            var error = await Fails(() => client.ClaimNameAsync(Session, "ValidName"), code);
            Assert.DoesNotContain("not printed", error.Message);
            Assert.Equal(AccountException.MessageFor(code), error.Message);
        }
        foreach (var response in new[]
                 {
                     With(Account, "id", "2"),
                     With(Account, "nameClaimsEnabled", "yes"),
                     With(With(With(Account, "nameClaimsEnabled", true), "claimedName", null), "claimRequired", false),
                     With(With(With(With(Account, "nameClaimsEnabled", true), "claimedName", "ValidName"), "claimRequired", false), "nameChangeAllowed", "invalid"),
                     With(With(With(With(Account, "nameClaimsEnabled", true), "claimedName", "ValidName"), "claimRequired", false), "nameChangeAllowed", "2026-12-16T10:00:00"),
                     With(Account, "nameClaimsEnabled", null),
                     With(Account, "id", true),
                     With(Account, "id", 1.5),
                     With(Account, "id", "18446744073709551616"),
                     With(Account, "id", "0"),
                     With(Account, "name", ""),
                     With(Account, "name", new string('n', 101)),
                 })
        {
            using var client = Client(new FakeHandler((200, response)));
            await Fails(() => client.RefreshAccountAsync(Session), "invalid_account_response");
        }
    }

    [Fact]
    public async Task IntegerAndPaddedIdentifiersAreNormalised()
    {
        using var client = Client(new FakeHandler((200, """{"id":123456789012345678,"name":"Example"}"""),
            (200, """{"id":"00123456789012345678","name":"Example"}""")));
        Assert.Equal(Session, await client.RefreshAccountAsync(Session));
        Assert.Equal(Session, await client.RefreshAccountAsync(Session));
    }

    [Fact]
    public async Task DedicatedMintUsesExplicitRouteAndRejectsRoleConfusion()
    {
        var host = """{"username":"h0123456789abcd","password":"Temporary123AbC","expiresIn":90,"discordId":"123456789012345678","displayName":"Example"}""";
        var handler = new FakeHandler((200, host));
        using (var client = Client(handler))
        {
            var lease = await client.MintNativeAsync(Session, NativeCredentialPurpose.Dedicated);
            Assert.Equal("h0123456789abcd", lease.Username);
            Assert.Equal([new RecordedRequest("POST", "/retail/v1/dedicated-session", null, Token)], handler.Calls);
            Assert.DoesNotContain(lease.Password, lease.ToString());
        }
        foreach (var (purpose, response) in new[]
                 {
                     (NativeCredentialPurpose.Player, host),
                     (NativeCredentialPurpose.Dedicated, With(host, "username", "d0123456789abcd")),
                     (NativeCredentialPurpose.Dedicated, With(host, "discordId", "2")),
                     (NativeCredentialPurpose.Dedicated, With(host, "expiresIn", 91)),
                     (NativeCredentialPurpose.Dedicated, host.Replace("\"expiresIn\":90", "\"expiresIn\":90.0")),
                     (NativeCredentialPurpose.Dedicated, With(host, "password", "has space")),
                 })
        {
            using var client = Client(new FakeHandler((200, response)));
            await Fails(() => client.MintNativeAsync(Session, purpose), "invalid_native_credential_response");
        }
        var none = new FakeHandler();
        using (var client = Client(none))
        {
            await Fails(() => client.MintNativeAsync(Session, (NativeCredentialPurpose)7), "invalid_native_credential_purpose");
            await Fails(() => client.MintNativeAsync(Session, statisticsCodec: "other"), "invalid_statistics_codec");
            Assert.Empty(none.Calls);
        }
        using (var client = Client(new FakeHandler((401, "{}"))))
        {
            await Fails(() => client.MintNativeAsync(Session, NativeCredentialPurpose.Dedicated), "account_session_expired_or_revoked");
        }
    }

    [Fact]
    public async Task StatisticsCodecIsSentAndMustBeAcknowledged()
    {
        var native = """{"username":"d0123456789abcd","password":"Temporary123AbC","expiresIn":90,"discordId":"123456789012345678","displayName":"Example"}""";
        var handler = new FakeHandler((200, With(native, "statisticsCodec", "native-base-v1")), (200, native));
        using var client = Client(handler);
        await client.MintNativeAsync(Session, statisticsCodec: DiscordClient.StatisticsCodecNativeBase);
        Assert.Equal("""{"statisticsCodec":"native-base-v1"}""", handler.Calls[0].Body);
        await Fails(() => client.MintNativeAsync(Session, statisticsCodec: DiscordClient.StatisticsCodecNativeBase), "statistics_codec_not_acknowledged");
    }

    [Fact]
    public async Task ExistingLinkPollAndLaterNativeMint()
    {
        var native = """{"username":"d0123456789abcd","password":"Temporary123AbC","expiresIn":90,"discordId":"123456789012345678","displayName":"Example"}""";
        var handler = new FakeHandler((200, Link), (202, null), (200, $$"""{"token":"{{Token}}","account":{{Account}}}"""), (200, native));
        var clock = new FakeClock();
        using var client = Client(handler, clock);
        var link = await client.BeginLinkAsync();
        Assert.Equal(new RecordedRequest("POST", "/api/v1/account/link", "{}", null), handler.Calls[0]);
        var session = await client.WaitForLinkAsync(link);
        Assert.Equal(Id, session.DiscordId);
        Assert.DoesNotContain(Token, session.ToString());
        Assert.DoesNotContain("private-poll-secret", link.ToString());
        Assert.Equal(TimeSpan.FromSeconds(103), clock.Now);
        foreach (var call in handler.Calls.Skip(1))
        {
            Assert.Equal("/api/v1/account/link/ABCD12", call.Path);
            Assert.Equal("""{"secret":"private-poll-secret"}""", call.Body);
            Assert.Null(call.Bearer);
        }
        var lease = await client.MintNativeAsync(session);
        Assert.Equal("d0123456789abcd", lease.Username);
        Assert.DoesNotContain("Temporary123AbC", lease.ToString());
        var last = handler.Calls[^1];
        Assert.Equal("/retail/v1/session", last.Path);
        Assert.Equal(Token, last.Bearer);
        Assert.Null(last.Body);
    }

    [Fact]
    public async Task SignInReportsApprovalAndOpensTheCheckedUrl()
    {
        var handler = new FakeHandler((200, Link), (200, $$"""{"token":"{{Token}}","account":{{Account}}}"""));
        using var client = Client(handler, new FakeClock());
        var messages = new List<string>();
        string? opened = null;
        var session = await client.SignInAsync(url => opened = url, new SynchronousProgress(messages.Add));
        Assert.Equal("https://vegas.example.invalid/link/ABCD12", opened);
        Assert.Equal([AccountMessages.ApproveInBrowser], messages);
        Assert.Equal(Session, session);
    }

    [Fact]
    public void NextNameChangeReportsCooldownEndInLocalTime()
    {
        var renamed = Session with { ClaimedName = "Test_Name", NameClaimsEnabled = true, NameChangeAllowed = "2026-12-16T09:30:00+00:00" };
        var before = DateTimeOffset.Parse("2026-12-01T00:00:00Z", CultureInfo.InvariantCulture);
        var after = DateTimeOffset.Parse("2026-12-17T00:00:00Z", CultureInfo.InvariantCulture);

        Assert.Equal(DateTimeOffset.Parse("2026-12-16T09:30:00Z", CultureInfo.InvariantCulture), renamed.NextNameChange(before));
        Assert.Null(renamed.NextNameChange(after));
        Assert.Null((renamed with { ClaimRequired = true }).NextNameChange(before));
        Assert.Null((renamed with { NameChangeAllowed = "not a time" }).NextNameChange(before));

        var zone = TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");
        Assert.Equal(
            "You can change your name again on Wednesday, 16 December 2026 11:30. Names can change every 30 days.",
            AccountMessages.NextNameChange(renamed.NextNameChange(before)!.Value, zone, CultureInfo.GetCultureInfo("en-GB")));
    }

    [Fact]
    public async Task SameOriginLinkRequiredBeforeAnyBrowserHandoff()
    {
        foreach (var url in new[] { "http://vegas.example.invalid/link/ABCD12", "https://elsewhere.invalid/link/ABCD12",
                     Origin + "/link/ABCD12?token=private", Origin + "/link/WRONG", "https://user@vegas.example.invalid/link/ABCD12",
                     Origin + ":444/link/ABCD12", Origin + "/link/ABCD12#x" })
        {
            var handler = new FakeHandler((200, With(Link, "url", url)));
            using var client = Client(handler);
            await Fails(() => client.BeginLinkAsync());
            Assert.Single(handler.Calls);
        }
        using (var client = Client(new FakeHandler((200, With(Link, "url", Origin + ":443/link/ABCD12")))))
        {
            await client.BeginLinkAsync();
        }
        foreach (var origin in new[] { "http://vegas.example.invalid", "https://user:password@vegas.example.invalid",
                     Origin + "/api", Origin + ":0", Origin + "?x=1", "https:vegas.example.invalid" })
        {
            var error = Assert.Throws<AccountException>(() => new DiscordClient(origin, new FakeHandler()));
            Assert.Equal("invalid_https_account_origin", error.Code);
        }
        new DiscordClient(Origin + "/", new FakeHandler()).Dispose();
    }

    [Fact]
    public async Task PendingPollingExpiresAfterFiveMinutesAndCancels()
    {
        var clock = new FakeClock();
        var responses = new List<(int, string?)> { (200, With(With(Link, "expiresIn", 3600), "pollSeconds", 30)) };
        responses.AddRange(Enumerable.Repeat<(int, string?)>((202, null), 10));
        var handler = new FakeHandler([.. responses]);
        using var client = Client(handler, clock);
        var link = await client.BeginLinkAsync();
        Assert.Equal(300, link.ExpiresIn);
        await Fails(() => client.WaitForLinkAsync(link), "account_link_expired");
        Assert.Equal(TimeSpan.FromSeconds(400), clock.Now);
        Assert.Equal(11, handler.Calls.Count);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Fails(() => client.WaitForLinkAsync(link, cancel.Token), "account_link_cancelled");
        Assert.Equal(11, handler.Calls.Count);
    }

    [Fact]
    public async Task InvalidLinkResponsesAreRejected()
    {
        foreach (var response in new[] { With(Link, "pollSeconds", 31), With(Link, "pollSeconds", true), With(Link, "expiresIn", 0),
                     With(Link, "expiresIn", 1.5), With(Link, "code", "bad code"), With(Link, "secret", "") })
        {
            using var client = Client(new FakeHandler((200, response)));
            await Fails(() => client.BeginLinkAsync(), "invalid_account_link_response");
        }
        using (var client = Client(new FakeHandler((500, null))))
        {
            await Fails(() => client.BeginLinkAsync(), "account_link_start_failed");
        }
        var clock = new FakeClock();
        using (var client = Client(new FakeHandler((200, Link), (200, """{"token":"short"}""")), clock))
        {
            var link = await client.BeginLinkAsync();
            await Fails(() => client.WaitForLinkAsync(link), "invalid_account_session");
        }
        using (var client = Client(new FakeHandler((200, Link), (500, null)), clock))
        {
            var link = await client.BeginLinkAsync();
            await Fails(() => client.WaitForLinkAsync(link), "account_link_poll_failed");
        }
    }

    [Fact]
    public async Task BearerFailureAndCrossAccountNativeLeaseRejected()
    {
        using (var client = Client(new FakeHandler((401, """{"error":"not authorized"}"""))))
        {
            await Fails(() => client.MintNativeAsync(Session), "account_session_expired_or_revoked");
        }
        var forged = """{"username":"d0123456789abcd","password":"Temporary123AbC","expiresIn":90,"discordId":"2","displayName":"Different"}""";
        using (var client = Client(new FakeHandler((200, forged))))
        {
            await Fails(() => client.MintNativeAsync(Session), "invalid_native_credential_response");
        }
        var none = new FakeHandler();
        using (var client = Client(none))
        {
            await Fails(() => client.RefreshAccountAsync(Session with { Token = "not-hex" }), "invalid_account_session");
            Assert.Empty(none.Calls);
        }
    }

    [Fact]
    public async Task RedirectsOversizedAndMalformedResponsesFailWithoutFollowup()
    {
        var redirect = new FakeHandler((302, null));
        using (var client = Client(redirect))
        {
            await Fails(() => client.MintNativeAsync(Session), "account_redirect_rejected");
            Assert.Single(redirect.Calls);
        }
        using (var client = Client(new FakeHandler((200, "\"" + new string('x', 16384) + "\""))))
        {
            await Fails(() => client.RefreshAccountAsync(Session), "account_response_too_large");
        }
        using (var client = Client(new FakeHandler((200, "{not json"))))
        {
            await Fails(() => client.RefreshAccountAsync(Session), "account_request_failed");
        }
    }

    [Fact]
    public async Task CallerCancellationIsNotReportedAsAServiceError()
    {
        using var client = Client(new FakeHandler((200, Account)));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.RefreshAccountAsync(Session, cancel.Token));
    }

    [Fact]
    public void PrivateSessionAndLeaseFilesAreNewAndSecretSafe()
    {
        var lease = new NativeLease("d0123456789abcd", 90, Id, "Example", "Temporary123AbC");
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "session.json");
        AccountSessionStore.Save(path, Session);
        Assert.Equal(Session, AccountSessionStore.Load(path));
        var original = File.ReadAllBytes(path);
        Assert.Throws<IOException>(() => AccountSessionStore.Save(path, Session));
        Assert.Equal(original, File.ReadAllBytes(path));
        var leasePath = AccountSessionStore.SaveNativeLease(Path.Combine(_directory, "lease.json"), lease);
        using var leaseJson = JsonDocument.Parse(File.ReadAllBytes(leasePath));
        Assert.Equal(lease.Password, leaseJson.RootElement.GetProperty("password").GetString());
        Assert.DoesNotContain(Token, Session.ToString());
    }

    [Fact]
    public void SessionFileKeepsThePythonShapeAndReadsPythonFiles()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "account-session.json");
        var claimed = new AccountSession(Id, "Exámple", Token, "Test_Name", true, false, "2026-12-16T10:00:00Z");
        AccountSessionStore.Persist(path, claimed);
        AccountSessionStore.Persist(path, claimed);
        Assert.False(File.Exists(path + ".new"));
        var text = File.ReadAllText(path);
        Assert.EndsWith("\n", text);
        Assert.True(text.All(c => c < 128));
        using (var document = JsonDocument.Parse(text))
        {
            var root = document.RootElement;
            Assert.Equal(["version", "account", "token", "savedAt"], root.EnumerateObject().Select(p => p.Name));
            Assert.Equal(["id", "name", "claimedName", "nameClaimsEnabled", "claimRequired", "nameChangeAllowed"],
                root.GetProperty("account").EnumerateObject().Select(p => p.Name));
        }
        Assert.Equal(claimed, AccountSessionStore.Load(path));

        // Written by the 0.4.x Python companion (json.dump, ensure_ascii).
        var python = $$"""{"version": 1, "account": {"id": "{{Id}}", "name": "Exámple", "claimedName": null, "nameClaimsEnabled": false, "claimRequired": false, "nameChangeAllowed": null}, "token": "{{Token}}", "savedAt": "2026-01-01T00:00:00.000000+00:00"}""" + "\n";
        File.WriteAllText(path, python);
        Assert.Equal(new AccountSession(Id, "Exámple", Token), AccountSessionStore.Load(path));

        foreach (var invalid in new[] { "{}", "[]", "not json", python.Replace("\"version\": 1", "\"version\": 2"),
                     python.Replace(Token, "short"), new string(' ', 16385) })
        {
            File.WriteAllText(path, invalid);
            Assert.Equal("invalid_account_session_file", Assert.Throws<AccountException>(() => AccountSessionStore.Load(path)).Code);
            Assert.Null(AccountSessionStore.TryLoad(path));
        }
        AccountSessionStore.Delete(path);
        Assert.Throws<FileNotFoundException>(() => AccountSessionStore.Load(path));
        Assert.Null(AccountSessionStore.TryLoad(path));
    }

    private sealed class SynchronousProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
