using VegasReborn.Core.Launch;
using VegasReborn.Core.Profiles;
using VegasReborn.Core.Titles;

namespace VegasReborn.Core.Account;

/// <summary>
/// The companion's signed-in account: the saved session, the account service client and
/// per-title character sync. It is the <see cref="ICredentialSource"/> and
/// <see cref="IProfileSync"/> the launcher uses, so the bearer never leaves this class.
/// </summary>
public sealed class CompanionAccount : ICredentialSource, IProfileSync, IDisposable
{
    private readonly AppPaths _paths;
    private readonly DiscordClient _client;
    private readonly Func<GameTitle, RetailProfileSync> _syncFactory;
    private readonly Dictionary<string, RetailProfileSync> _syncs = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private AccountSession? _session;

    public CompanionAccount(AppPaths paths, DiscordClient? client = null, Func<GameTitle, RetailProfileSync>? syncFactory = null)
    {
        _paths = paths;
        _client = client ?? new DiscordClient();
        _syncFactory = syncFactory ?? (title => new RetailProfileSync(paths, title, SyncRoot(paths, title)));
        _session = AccountSessionStore.TryLoad(paths.AccountSession);
    }

    /// <summary>Raised after sign-in, sign-out, refresh or a name claim.</summary>
    public event EventHandler? Changed;

    public AccountSession? Session
    {
        get
        {
            lock (_gate)
            {
                return _session;
            }
        }
    }

    public bool IsSignedIn => Session is not null;

    /// <summary>
    /// Titles whose native credentials the account service issues. Leases are
    /// not title-specific: each title's master validates them against the
    /// shared credential store.
    /// </summary>
    public static bool IssuesCredentialsFor(GameTitle title) =>
        title.Key == TitleCatalog.Vegas1.Key || title.Key == TitleCatalog.Vegas2.Key;

    /// <summary>Where a title's account folders live. Matches <see cref="LaunchLayout.DefaultAccountDirectory"/>.</summary>
    public static string SyncRoot(AppPaths paths, GameTitle title) =>
        title.Key == TitleCatalog.Vegas1.Key ? paths.RetailAccounts : Path.Combine(paths.Local, title.Key, "accounts");

    public async Task<AccountSession> SignInAsync(Action<string> openBrowser, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var session = await _client.SignInAsync(openBrowser, progress, cancellationToken).ConfigureAwait(false);
        Store(session);
        return session;
    }

    public void SignOut()
    {
        lock (_gate)
        {
            _session = null;
        }
        try
        {
            AccountSessionStore.Delete(_paths.AccountSession);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The in-memory session is gone; a stale file is replaced on the next sign-in.
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<AccountSession> RefreshAsync(CancellationToken cancellationToken)
    {
        var updated = await _client.RefreshAccountAsync(Require(), cancellationToken).ConfigureAwait(false);
        Store(updated);
        return updated;
    }

    public async Task<AccountSession> ClaimNameAsync(string name, CancellationToken cancellationToken)
    {
        var updated = await _client.ClaimNameAsync(Require(), name, cancellationToken).ConfigureAwait(false);
        Store(updated);
        return updated;
    }

    public RetailProfileSync SyncFor(GameTitle title)
    {
        lock (_gate)
        {
            if (!_syncs.TryGetValue(title.Key, out var sync))
            {
                _syncs[title.Key] = sync = _syncFactory(title);
            }
            return sync;
        }
    }

    /// <summary>Pull then push the title's character. Throws <see cref="SyncConflictException"/> when a choice is needed.</summary>
    public Task<SyncResult> SynchronizeAsync(GameTitle title, IProgress<string>? progress, CancellationToken cancellationToken) =>
        SyncFor(title).SynchronizeAsync(Require(), progress, cancellationToken);

    public Task<SyncResult> ResolveConflictAsync(GameTitle title, ConflictChoice choice, IProgress<string>? progress, CancellationToken cancellationToken) =>
        SyncFor(title).ResolveConflictAsync(Require(), choice, progress, cancellationToken);

    async Task<LaunchAccount> ICredentialSource.RefreshAccountAsync(CancellationToken cancellationToken)
    {
        var session = await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return new LaunchAccount(session.DiscordId, session.ClaimedName, session.NameClaimsEnabled, session.ClaimRequired);
    }

    bool ICredentialSource.SupportsTitle(GameTitle title) => IssuesCredentialsFor(title);

    async Task<NativeCredential> ICredentialSource.MintNativeAsync(NativeCredentialRequest request, CancellationToken cancellationToken)
    {
        if (!IssuesCredentialsFor(request.Title))
        {
            throw new AccountException("native_credential_mint_failed");
        }
        var purpose = request.Dedicated ? NativeCredentialPurpose.Dedicated : NativeCredentialPurpose.Player;
        var lease = await _client.MintNativeAsync(Require(), purpose, request.StatisticsCodec, cancellationToken).ConfigureAwait(false);
        return new NativeCredential(lease.Username, lease.Password);
    }

    string IProfileSync.AccountDirectory(GameTitle title) => SyncFor(title).AccountDirectory(Require());

    async Task IProfileSync.PullAsync(GameTitle title, CancellationToken cancellationToken) =>
        await SyncFor(title).PullAsync(Require(), false, cancellationToken).ConfigureAwait(false);

    async Task<bool> IProfileSync.PushAsync(GameTitle title, CancellationToken cancellationToken) =>
        await SyncFor(title).SaveAfterGameAsync(Require(), null, cancellationToken).ConfigureAwait(false) is not null;

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var sync in _syncs.Values)
            {
                sync.Dispose();
            }
            _syncs.Clear();
        }
        _client.Dispose();
    }

    private AccountSession Require() => Session ?? throw new AccountException("account_session_expired_or_revoked");

    private void Store(AccountSession session)
    {
        lock (_gate)
        {
            _session = session;
        }
        AccountSessionStore.Persist(_paths.AccountSession, session);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
