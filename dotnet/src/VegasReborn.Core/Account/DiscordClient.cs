using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

namespace VegasReborn.Core.Account;

/// <summary>
/// Companion sign-in through the service's existing device-link flow.
/// Discord passwords and OAuth secrets never enter this client: the service owns
/// OAuth/PKCE. The retail game keeps its native login; mint a short-lived native
/// lease only when the game is ready to use it.
/// </summary>
public sealed partial class DiscordClient : IDisposable
{
    public const string DefaultBaseUrl = "https://vegas.lumbridge.co.uk";
    public const string StatisticsCodecNativeBase = "native-base-v1";

    private const int ResponseLimit = 16384;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxLinkLifetime = TimeSpan.FromSeconds(300);

    private readonly AccountOrigin _origin;
    private readonly ServiceHttp _http;
    private readonly Func<TimeSpan> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="baseUrl">HTTPS service origin.</param>
    /// <param name="handler">HTTP handler; tests inject a fake. The default never follows redirects.</param>
    /// <param name="clock">Monotonic clock (tests).</param>
    /// <param name="delay">Poll delay (tests).</param>
    public DiscordClient(string baseUrl = DefaultBaseUrl, HttpMessageHandler? handler = null,
        Func<TimeSpan>? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _origin = AccountOrigin.Parse(baseUrl, "invalid_https_account_origin");
        _http = new ServiceHttp(_origin, handler, new ServiceErrors(
            "account_redirect_rejected", "account_response_too_large", "account_request_failed",
            code => new AccountException(code)));
        _clock = clock ?? (() => Stopwatch.GetElapsedTime(0));
        _delay = delay ?? ((span, token) => Task.Delay(span, token));
    }

    /// <summary>
    /// Full sign-in: start a link, report <see cref="AccountMessages.ApproveInBrowser"/>,
    /// open the browser through <paramref name="openBrowser"/> and wait for approval.
    /// </summary>
    public async Task<AccountSession> SignInAsync(Action<string> openBrowser, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(openBrowser);
        var link = await BeginLinkAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(AccountMessages.ApproveInBrowser);
        openBrowser(link.Url);
        return await WaitForLinkAsync(link, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Starts a device link. The returned URL is checked to be on this origin.</summary>
    public async Task<AuthLink> BeginLinkAsync(CancellationToken cancellationToken = default)
    {
        var started = _clock();
        var (status, response) = await RequestAsync(HttpMethod.Post, "/api/v1/account/link",
            Serialize(new EmptyRequest(), AccountJsonContext.Default.EmptyRequest), null, RequestTimeout, cancellationToken).ConfigureAwait(false);
        if (status != 200 || !StrictJson.IsObject(response))
        {
            throw new AccountException("account_link_start_failed");
        }
        var body = response!.Value;
        var code = StrictJson.GetString(StrictJson.Get(body, "code"));
        var secret = StrictJson.GetString(StrictJson.Get(body, "secret"));
        var url = StrictJson.GetString(StrictJson.Get(body, "url"));
        var expiresElement = StrictJson.Get(body, "expiresIn");
        var intervalElement = StrictJson.Get(body, "pollSeconds");
        if (code is null || !LinkCode().IsMatch(code)
            || secret is null || secret.Length is < 1 or > 1024 || secret.Contains('\0')
            || url is null || !StrictJson.TryGetInteger(expiresElement, out var expires) || expires <= 0
            || !StrictJson.TryGetNumber(intervalElement, out var interval) || interval is <= 0 or > 30)
        {
            throw new AccountException("invalid_account_link_response");
        }
        var target = LinkUrl().Match(url);
        if (!target.Success
            || target.Groups["host"].Value.ToLowerInvariant() != _origin.Host
            || (target.Groups["port"].Success ? (int.TryParse(target.Groups["port"].Value, out var port) ? port : -1) : 443) != _origin.Port
            || target.Groups["path"].Value != "/link/" + code)
        {
            throw new AccountException("account_link_origin_mismatch");
        }
        var ttl = (int)Math.Min(expires, (long)MaxLinkLifetime.TotalSeconds);
        return new AuthLink(code, url, ttl, interval, started + TimeSpan.FromSeconds(ttl), secret);
    }

    /// <summary>
    /// Polls until the link is approved, it expires (five minutes at most) or the token is
    /// cancelled. Cancellation raises <see cref="AccountException"/> with code
    /// <c>account_link_cancelled</c>, as the Python companion does.
    /// </summary>
    public async Task<AccountSession> WaitForLinkAsync(AuthLink link, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        var poll = Serialize(new LinkPollRequest(link.Secret), AccountJsonContext.Default.LinkPollRequest);
        while (true)
        {
            ThrowIfLinkCancelled(cancellationToken);
            var remaining = link.Deadline - _clock();
            if (remaining <= TimeSpan.Zero)
            {
                throw new AccountException("account_link_expired");
            }
            ServiceResponse result;
            try
            {
                result = await RequestAsync(HttpMethod.Post, "/api/v1/account/link/" + link.Code, poll, null,
                    remaining < RequestTimeout ? remaining : RequestTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new AccountException("account_link_cancelled");
            }
            ThrowIfLinkCancelled(cancellationToken);
            if (_clock() >= link.Deadline)
            {
                throw new AccountException("account_link_expired");
            }
            if (result.Status == 200)
            {
                var token = result.Body is { } body && StrictJson.IsObject(body) ? StrictJson.GetString(StrictJson.Get(body, "token")) : null;
                if (!AccountRules.IsToken(token))
                {
                    throw new AccountException("invalid_account_session");
                }
                return SessionFromAccount(StrictJson.Get(result.Body!.Value, "account"), token!);
            }
            if (result.Status != 202)
            {
                throw new AccountException("account_link_poll_failed");
            }
            var left = link.Deadline - _clock();
            var delay = TimeSpan.FromSeconds(Math.Min(link.PollSeconds, Math.Max(0, left.TotalSeconds)));
            try
            {
                await _delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new AccountException("account_link_cancelled");
            }
        }
    }

    /// <summary>Re-reads the account (claim state) with the saved bearer.</summary>
    public async Task<AccountSession> RefreshAccountAsync(AccountSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var (status, response) = await RequestAsync(HttpMethod.Get, "/api/v1/account", null, session.Token, RequestTimeout, cancellationToken).ConfigureAwait(false);
        if (status == 401)
        {
            throw new AccountException("account_session_expired_or_revoked");
        }
        if (status != 200)
        {
            throw new AccountException("account_refresh_failed");
        }
        var updated = SessionFromAccount(response, session.Token);
        if (updated.DiscordId != session.DiscordId)
        {
            throw new AccountException("invalid_account_response");
        }
        return updated;
    }

    /// <summary>Claims or changes the player name. The name is trimmed and validated locally first.</summary>
    public async Task<AccountSession> ClaimNameAsync(AccountSession session, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var trimmed = name?.Trim();
        if (trimmed is null || !AccountRules.PlayerName().IsMatch(trimmed))
        {
            throw new AccountException("invalid_player_name");
        }
        var (status, response) = await RequestAsync(HttpMethod.Post, "/api/v1/account/name",
            Serialize(new ClaimNameRequest(trimmed), AccountJsonContext.Default.ClaimNameRequest), session.Token, RequestTimeout, cancellationToken).ConfigureAwait(false);
        var failure = status switch
        {
            400 => "invalid_player_name",
            401 => "account_session_expired_or_revoked",
            403 => "player_name_change_too_soon",
            409 => "player_name_taken",
            200 => null,
            _ => "player_name_claim_failed",
        };
        if (failure is not null)
        {
            throw new AccountException(failure);
        }
        var updated = SessionFromAccount(response, session.Token);
        if (updated.DiscordId != session.DiscordId || !updated.NameClaimsEnabled || updated.ClaimRequired)
        {
            throw new AccountException("invalid_account_response");
        }
        return updated;
    }

    /// <summary>Mints a short-lived native credential for the game or a dedicated host.</summary>
    /// <param name="statisticsCodec">Empty, or <see cref="StatisticsCodecNativeBase"/> once the leaderboard carrier is verified.</param>
    public async Task<NativeLease> MintNativeAsync(AccountSession session, NativeCredentialPurpose purpose = NativeCredentialPurpose.Player,
        string statisticsCodec = "", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!Enum.IsDefined(purpose))
        {
            throw new AccountException("invalid_native_credential_purpose");
        }
        if (statisticsCodec is not ("" or StatisticsCodecNativeBase))
        {
            throw new AccountException("invalid_statistics_codec");
        }
        var dedicated = purpose == NativeCredentialPurpose.Dedicated;
        var path = dedicated ? "/retail/v1/dedicated-session" : "/retail/v1/session";
        var body = statisticsCodec.Length != 0 ? Serialize(new MintRequest(statisticsCodec), AccountJsonContext.Default.MintRequest) : null;
        var (status, response) = await RequestAsync(HttpMethod.Post, path, body, session.Token, RequestTimeout, cancellationToken).ConfigureAwait(false);
        if (status == 401)
        {
            throw new AccountException("account_session_expired_or_revoked");
        }
        if (status != 200 || !StrictJson.IsObject(response))
        {
            throw new AccountException("native_credential_mint_failed");
        }
        var value = response!.Value;
        if (statisticsCodec.Length != 0 && StrictJson.GetString(StrictJson.Get(value, "statisticsCodec")) != statisticsCodec)
        {
            throw new AccountException("statistics_codec_not_acknowledged");
        }
        var (identifier, displayName) = ParseAccount(StrictJson.Get(value, "discordId"), StrictJson.Get(value, "displayName"));
        var username = StrictJson.GetString(StrictJson.Get(value, "username"));
        var password = StrictJson.GetString(StrictJson.Get(value, "password"));
        if (identifier != session.DiscordId || username is null
            || !(dedicated ? HostUsername() : PlayerUsername()).IsMatch(username)
            || password is null || !LeasePassword().IsMatch(password)
            || !StrictJson.TryGetInteger(StrictJson.Get(value, "expiresIn"), out var expires) || expires is < 1 or > 90)
        {
            throw new AccountException("invalid_native_credential_response");
        }
        return new NativeLease(username, (int)expires, identifier, displayName, password);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Builds a session from an account object, validating claim metadata.</summary>
    /// <summary>
    /// Starts a guest session. The service adds the tag (OpenSCCT's: four hex characters
    /// of SHA-256 over <paramref name="installId"/>), so the player only chooses the name.
    /// </summary>
    public async Task<AccountSession> PlayAsGuestAsync(string installId, string name, CancellationToken cancellationToken = default)
    {
        if (installId is null || !AccountRules.GuestInstallId().IsMatch(installId))
        {
            throw new AccountException("invalid_guest_install");
        }
        var requested = (name ?? "").Trim();
        if (requested.Length > 64)
        {
            requested = requested[..64];
        }
        var (status, response) = await RequestAsync(HttpMethod.Post, "/api/v1/account/guest",
            Serialize(new GuestRequest(installId, requested), AccountJsonContext.Default.GuestRequest), null, RequestTimeout,
            cancellationToken).ConfigureAwait(false);
        var failure = status switch
        {
            200 => null,
            404 or 405 => "guest_unavailable",
            429 => "guest_capacity",
            _ => "guest_sign_in_failed",
        };
        if (failure is not null)
        {
            throw new AccountException(failure);
        }
        var token = StrictJson.IsObject(response) ? StrictJson.GetString(StrictJson.Get(response!.Value, "token")) : null;
        if (!AccountRules.IsToken(token))
        {
            throw new AccountException("invalid_account_session");
        }
        var session = SessionFromAccount(StrictJson.Get(response!.Value, "account"), token!);
        if (!session.IsGuest)
        {
            throw new AccountException("invalid_account_response");
        }
        return session;
    }

    internal static AccountSession SessionFromAccount(JsonElement? value, string token)
    {
        if (!StrictJson.IsObject(value))
        {
            throw new AccountException("invalid_account_response");
        }
        var account = value!.Value;
        var (identifier, name) = ParseAccount(StrictJson.Get(account, "id"), StrictJson.Get(account, "name"));
        if (StrictJson.Has(account, "guest"))
        {
            var guest = StrictJson.GetBool(account.GetProperty("guest"));
            if (guest is null)
            {
                throw new AccountException("invalid_account_response");
            }
            if (guest.Value)
            {
                // A guest has no name claims; its in-game name is the service's Name.tag.
                var inGame = StrictJson.GetString(StrictJson.Get(account, "claimedName"));
                if (StrictJson.GetBool(StrictJson.Get(account, "nameClaimsEnabled")) is not (null or false)
                    || !AccountRules.IsGuestName(name, inGame))
                {
                    throw new AccountException("invalid_account_response");
                }
                return new AccountSession(identifier, name, token, inGame, IsGuest: true);
            }
        }
        bool enabled;
        if (!StrictJson.Has(account, "nameClaimsEnabled"))
        {
            enabled = false;
        }
        else if (StrictJson.GetBool(account.GetProperty("nameClaimsEnabled")) is { } flag)
        {
            enabled = flag;
        }
        else
        {
            throw new AccountException("invalid_account_response");
        }
        if (!enabled)
        {
            return new AccountSession(identifier, name, token);
        }
        var claimedElement = StrictJson.Get(account, "claimedName");
        var required = StrictJson.GetBool(StrictJson.Get(account, "claimRequired"));
        var allowedElement = StrictJson.Get(account, "nameChangeAllowed");
        var claimed = StrictJson.GetString(claimedElement);
        if (required is null || required.Value != (claimedElement is null)
            || (claimedElement is not null && (claimed is null || !AccountRules.PlayerName().IsMatch(claimed))))
        {
            throw new AccountException("invalid_account_response");
        }
        string? allowed = null;
        if (allowedElement is not null)
        {
            allowed = StrictJson.GetString(allowedElement);
            if (allowed is null || allowed.Length > 64 || !AccountRules.TryParseTimestamp(allowed, out _))
            {
                throw new AccountException("invalid_account_response");
            }
        }
        return new AccountSession(identifier, name, token, claimed, true, required.Value, allowed);
    }

    private static (string Id, string Name) ParseAccount(JsonElement? id, JsonElement? name)
    {
        var identifier = AccountRules.NormaliseId(StrictJson.IdText(id));
        var display = StrictJson.GetString(name);
        if (identifier is null || string.IsNullOrEmpty(display) || StrictJson.CodePoints(display) > 100 || display.Contains('\0'))
        {
            throw new AccountException("invalid_account_response");
        }
        return (identifier, display);
    }

    private Task<ServiceResponse> RequestAsync(HttpMethod method, string path, byte[]? body, string? bearer,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal) || path.IndexOfAny(['?', '#', '\0']) >= 0
            || timeout <= TimeSpan.Zero || timeout > RequestTimeout)
        {
            throw new AccountException("invalid_account_request");
        }
        if (bearer is not null && !AccountRules.IsToken(bearer))
        {
            throw new AccountException("invalid_account_session");
        }
        return _http.SendAsync(method, path, body, bearer, timeout, ResponseLimit, cancellationToken);
    }

    private static byte[] Serialize<T>(T value, JsonTypeInfo<T> type) => JsonSerializer.SerializeToUtf8Bytes(value, type);

    private static void ThrowIfLinkCancelled(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw new AccountException("account_link_cancelled");
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_\-]{1,128}\z")]
    private static partial Regex LinkCode();

    // Literal (unnormalised) https URL: no credentials, query or fragment.
    [GeneratedRegex(@"^https://(?<host>[^/?#@:\[\]]+)(?::(?<port>[0-9]{1,5}))?(?<path>/[^?#]*)\z", RegexOptions.IgnoreCase)]
    private static partial Regex LinkUrl();

    [GeneratedRegex(@"^d[0-9a-f]{14}\z")]
    private static partial Regex PlayerUsername();

    [GeneratedRegex(@"^h[0-9a-f]{14}\z")]
    private static partial Regex HostUsername();

    [GeneratedRegex(@"^[A-Za-z0-9]{1,15}\z")]
    private static partial Regex LeasePassword();
}
