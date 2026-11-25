using System.Text.Json;
using VegasReborn.Core.Account;
using VegasReborn.Core.Launch;
using VegasReborn.Core.Titles;

namespace VegasReborn.Core.Tests.Account;

public sealed class GuestAccountTests : IDisposable
{
    private const string GuestId = "123456789012345";
    private const string Install = "0123456789abcdef";
    private static readonly string Token = new('b', 64);
    private const string GuestAccount =
        """{"id":"123456789012345","name":"Ryan#3fa2","claimedName":"Ryan.3fa2","claimRequired":false,"nameClaimsEnabled":false,"nameChangeAllowed":null,"guest":true}""";

    private readonly string root = Path.Combine(Path.GetTempPath(), "vr-guest-" + Guid.NewGuid().ToString("N"));

    public GuestAccountTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, recursive: true);

    private static string Issued(string account) => $$"""{"token":"{{Token}}","account":{{account}}}""";

    private static AccountSession Guest() => new(GuestId, "Ryan#3fa2", Token, "Ryan.3fa2", IsGuest: true);

    [Fact]
    public async Task GuestSignInSendsInstallAndNameAndReadsTheTaggedAccount()
    {
        var handler = new FakeHandler((200, Issued(GuestAccount)));
        using var client = new DiscordClient(handler: handler);
        var session = await client.PlayAsGuestAsync(Install, "  Ryan  ");
        Assert.Equal(new RecordedRequest("POST", "/api/v1/account/guest", """{"installId":"0123456789abcdef","name":"Ryan"}""", null),
            Assert.Single(handler.Calls));
        Assert.Equal(Guest(), session);
        Assert.Equal("Ryan#3fa2", session.PlayerName);
        Assert.Equal("Ryan", session.GuestBaseName);
        Assert.False(session.NeedsNameClaim);
        Assert.DoesNotContain(Token, session.ToString());
        Assert.Equal("Playing as guest Ryan#3fa2", AccountMessages.SignedInAs(session));
    }

    [Theory]
    [InlineData(404, "guest_unavailable")]
    [InlineData(429, "guest_capacity")]
    [InlineData(400, "guest_sign_in_failed")]
    [InlineData(503, "guest_sign_in_failed")]
    public async Task GuestSignInFailuresHavePlainMessages(int status, string code)
    {
        using var client = new DiscordClient(handler: new FakeHandler((status, """{"error":"x"}""")));
        var error = await Assert.ThrowsAsync<AccountException>(() => client.PlayAsGuestAsync(Install, "Ryan"));
        Assert.Equal(code, error.Code);
        Assert.NotEqual(AccountException.GenericMessage, error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123456789ABCDEF")]
    [InlineData("0123456789abcde")]
    [InlineData("0123456789abcdef0")]
    public async Task InvalidInstallIdIsNeverSent(string install)
    {
        var handler = new FakeHandler();
        using var client = new DiscordClient(handler: handler);
        await Assert.ThrowsAsync<AccountException>(() => client.PlayAsGuestAsync(install, "Ryan"));
        Assert.Empty(handler.Calls);
    }

    [Theory]
    [InlineData("""{"id":"123456789012345","name":"Ryan#3fa2","claimedName":"Ryan.3fa3","guest":true}""")]
    [InlineData("""{"id":"123456789012345","name":"Ryan#3fa2","claimedName":"Ryan#3fa2","guest":true}""")]
    [InlineData("""{"id":"123456789012345","name":"Ryan#3fa2","guest":true}""")]
    [InlineData("""{"id":"123456789012345","name":"Ryan#3FA2","claimedName":"Ryan.3FA2","guest":true}""")]
    [InlineData("""{"id":"123456789012345","name":"Abcdefghijkl#3fa2","claimedName":"Abcdefghijkl.3fa2","guest":true}""")]
    [InlineData("""{"id":"123456789012345","name":"Ryan#3fa2","claimedName":"Ryan.3fa2","nameClaimsEnabled":true,"claimRequired":false,"guest":true}""")]
    [InlineData("""{"id":"123456789012345","name":"Ryan#3fa2","claimedName":"Ryan.3fa2","guest":"yes"}""")]
    [InlineData("""{"id":"123456789012345678","name":"Example"}""")]
    public async Task MalformedOrNonGuestAccountsAreRejected(string account)
    {
        using var client = new DiscordClient(handler: new FakeHandler((200, Issued(account))));
        var error = await Assert.ThrowsAsync<AccountException>(() => client.PlayAsGuestAsync(Install, "Ryan"));
        Assert.Equal("invalid_account_response", error.Code);
    }

    [Fact]
    public void DiscordSessionFileIsUnchangedAndGuestFileRoundTrips()
    {
        var path = Path.Combine(root, "account-session.json");
        AccountSessionStore.Persist(path, new AccountSession("123456789012345678", "Example", Token));
        using (var document = JsonDocument.Parse(File.ReadAllText(path)))
        {
            Assert.DoesNotContain("guest", document.RootElement.GetProperty("account").EnumerateObject().Select(p => p.Name));
        }
        AccountSessionStore.Persist(path, Guest());
        using (var document = JsonDocument.Parse(File.ReadAllText(path)))
        {
            Assert.True(document.RootElement.GetProperty("account").GetProperty("guest").GetBoolean());
        }
        Assert.Equal(Guest(), AccountSessionStore.Load(path));
    }

    [Fact]
    public void InstallIdIsCreatedOnceAndKeptAfterSignOut()
    {
        var paths = new AppPaths(root);
        using var account = new CompanionAccount(paths, new DiscordClient(handler: new FakeHandler()));
        var first = account.GuestInstallId();
        Assert.Matches("^[0-9a-f]{16}$", first);
        Assert.Equal(first, account.GuestInstallId());
        account.SignOut();
        using var reopened = new CompanionAccount(paths, new DiscordClient(handler: new FakeHandler()));
        Assert.Equal(first, reopened.GuestInstallId());
        File.WriteAllText(paths.GuestInstall, "not an id");
        Assert.NotEqual(first, reopened.GuestInstallId());
    }

    [Fact]
    public async Task GuestPlaysWithItsTaggedInGameNameAndNoCloudSaves()
    {
        var paths = new AppPaths(root);
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(paths.CompanionConfig).FullName, "guest-install.txt"), Install + "\n");
        var native = """{"username":"d0123456789abcd","password":"UseOnce123","expiresIn":90,"discordId":"123456789012345","displayName":"Ryan.3fa2","guest":true}""";
        var handler = new FakeHandler((200, Issued(GuestAccount)), (200, GuestAccount), (200, native));
        using var account = new CompanionAccount(paths, new DiscordClient(handler: handler));
        var session = await account.PlayAsGuestAsync("Ryan", CancellationToken.None);
        Assert.True(account.IsGuest);
        Assert.Contains(Install, handler.Calls[0].Body);
        Assert.Equal(session, AccountSessionStore.Load(paths.AccountSession));

        ICredentialSource credentials = account;
        var launch = await credentials.RefreshAccountAsync(CancellationToken.None);
        Assert.Equal(new LaunchAccount(GuestId, "Ryan.3fa2"), launch);
        var credential = await credentials.MintNativeAsync(new NativeCredentialRequest(TitleCatalog.Vegas1), CancellationToken.None);
        Assert.Equal("d0123456789abcd", credential.Username);
        var dedicated = await Assert.ThrowsAsync<AccountException>(() =>
            credentials.MintNativeAsync(new NativeCredentialRequest(TitleCatalog.Vegas1, Dedicated: true), CancellationToken.None));
        Assert.Equal("guest_dedicated", dedicated.Code);

        IProfileSync sync = account;
        await sync.PullAsync(TitleCatalog.Vegas1, CancellationToken.None);
        Assert.False(await sync.PushAsync(TitleCatalog.Vegas1, CancellationToken.None));
        Assert.Equal(Path.Combine(paths.RetailAccounts, GuestId), sync.AccountDirectory(TitleCatalog.Vegas1));
        var cloud = await Assert.ThrowsAsync<AccountException>(() => account.SynchronizeAsync(TitleCatalog.Vegas1, null, CancellationToken.None));
        Assert.Equal("guest_no_cloud", cloud.Code);
        Assert.Equal(3, handler.Calls.Count);
    }

    [Fact]
    public async Task ExpiredGuestSessionIsRenewedWithTheSameName()
    {
        var paths = new AppPaths(root);
        Directory.CreateDirectory(paths.CompanionConfig);
        File.WriteAllText(paths.GuestInstall, Install + "\n");
        AccountSessionStore.Save(paths.AccountSession, Guest() with { Token = new string('c', 64) });
        var handler = new FakeHandler((401, """{"error":"expired"}"""), (200, Issued(GuestAccount)));
        using var account = new CompanionAccount(paths, new DiscordClient(handler: handler));
        var renewed = await account.RefreshAsync(CancellationToken.None);
        Assert.Equal(Guest(), renewed);
        Assert.Equal("/api/v1/account/guest", handler.Calls[1].Path);
        Assert.Equal("""{"installId":"0123456789abcdef","name":"Ryan"}""", handler.Calls[1].Body);
    }
}
