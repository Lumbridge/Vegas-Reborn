using System.Text.RegularExpressions;
using VegasReborn.Core.Account;

namespace VegasReborn.Core.Profiles;

/// <summary>
/// Bounded fixed-origin HTTPS for the cloud character, with no redirects and a total
/// deadline. Only the account read and profile read/write routes are allowed.
/// </summary>
public sealed partial class CloudProfileTransport : IDisposable
{
    public const int MaxJson = 48 * 1024 * 1024;
    private const int AccountLimit = 16384;
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly ServiceHttp _http;

    public CloudProfileTransport(string baseUrl = DiscordClient.DefaultBaseUrl, HttpMessageHandler? handler = null)
    {
        var origin = AccountOrigin.TryParse(baseUrl) ?? throw new ProfileSyncException("invalid_https_cloud_origin");
        _http = new ServiceHttp(origin, handler, new ServiceErrors(
            "cloud_redirect_rejected", "cloud_response_too_large", "cloud_request_failed",
            code => new ProfileSyncException(code)));
    }

    internal Task<ServiceResponse> SendAsync(HttpMethod method, string path, string bearer, byte[]? body,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var allowed = (method.Method, path) is ("GET", "/api/v1/account") or ("GET", "/api/v1/account/profile") or ("PUT", "/api/v1/account/profile");
        if (!allowed || bearer is null || !Bearer().IsMatch(bearer) || timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(60))
        {
            throw new ProfileSyncException("invalid_cloud_request");
        }
        if (body is not null && body.Length > MaxJson)
        {
            throw new ProfileSyncException("cloud_request_too_large");
        }
        var limit = path == "/api/v1/account" ? AccountLimit : MaxJson;
        return _http.SendAsync(method, path, body, bearer, timeout, limit, cancellationToken);
    }

    public void Dispose() => _http.Dispose();

    [GeneratedRegex(@"^[A-Fa-f0-9]{64}\z")]
    private static partial Regex Bearer();
}
