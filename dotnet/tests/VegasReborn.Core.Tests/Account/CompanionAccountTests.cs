using VegasReborn.Core.Account;
using VegasReborn.Core.Launch;
using VegasReborn.Core.Titles;

namespace VegasReborn.Core.Tests.Account;

public sealed class CompanionAccountTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vr-account-" + Guid.NewGuid().ToString("N"));

    public CompanionAccountTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void SyncFoldersMatchTheLaunchLayout()
    {
        var paths = new AppPaths(root);
        foreach (var title in TitleCatalog.All)
        {
            Assert.Equal(
                LaunchLayout.DefaultAccountDirectory(paths, title, "123456789012345678"),
                Path.Combine(CompanionAccount.SyncRoot(paths, title), "123456789012345678"));
        }
    }

    [Fact]
    public void Vegas1AndVegas2CredentialsAreIssued()
    {
        foreach (var title in TitleCatalog.All)
        {
            Assert.True(CompanionAccount.IssuesCredentialsFor(title));
        }
    }

    [Fact]
    public async Task SignedOutAccountRefusesAccountOperations()
    {
        using var account = new CompanionAccount(new AppPaths(root), new DiscordClient(handler: new FakeHandler()));
        Assert.False(account.IsSignedIn);
        ICredentialSource credentials = account;
        var error = await Assert.ThrowsAsync<AccountException>(() => credentials.RefreshAccountAsync(CancellationToken.None));
        Assert.Equal("account_session_expired_or_revoked", error.Code);
    }

    [Fact]
    public async Task Vegas2MintUsesThePlayerLease()
    {
        var paths = new AppPaths(root);
        Directory.CreateDirectory(paths.CompanionConfig);
        AccountSessionStore.Save(paths.AccountSession, new AccountSession("123456789012345678", "Player", new string('a', 64)));
        var handler = new FakeHandler((200,
            """{"username":"d0123456789abcd","password":"UseOnce123","expiresIn":90,"discordId":"123456789012345678","displayName":"Player"}"""));
        using var account = new CompanionAccount(paths, new DiscordClient(handler: handler));
        ICredentialSource credentials = account;
        Assert.True(credentials.SupportsTitle(TitleCatalog.Vegas2));
        var credential = await credentials.MintNativeAsync(new NativeCredentialRequest(TitleCatalog.Vegas2), CancellationToken.None);
        Assert.Equal("d0123456789abcd", credential.Username);
        var call = Assert.Single(handler.Calls);
        Assert.Equal("/retail/v1/session", call.Path);
    }

    [Fact]
    public void SignOutRemovesTheSavedSession()
    {
        var paths = new AppPaths(root);
        Directory.CreateDirectory(paths.CompanionConfig);
        AccountSessionStore.Save(paths.AccountSession, new AccountSession("123456789012345678", "Player", new string('a', 64)));
        using var account = new CompanionAccount(paths, new DiscordClient(handler: new FakeHandler()));
        Assert.True(account.IsSignedIn);
        var changed = 0;
        account.Changed += (_, _) => changed++;
        account.SignOut();
        Assert.False(account.IsSignedIn);
        Assert.False(File.Exists(paths.AccountSession));
        Assert.Equal(1, changed);
    }
}
