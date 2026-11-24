using System.Net;
using System.Net.Http.Headers;

namespace VegasReborn.Core.Update;

/// <summary>Bounded HTTPS downloads of release metadata and archives.</summary>
public interface IReleaseDownloader
{
    /// <summary>Downloads at most <paramref name="limit"/> bytes into memory.</summary>
    Task<byte[]> DownloadBytesAsync(Uri url, long limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads at most <paramref name="limit"/> bytes into a new file (never overwritten),
    /// reporting the running byte count. Returns the byte count.
    /// </summary>
    Task<long> DownloadFileAsync(Uri url, long limit, string destination, IProgress<long>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Redirects may only lead to GitHub's own HTTPS release hosts.</summary>
public static class ReleaseRedirects
{
    public static IReadOnlySet<string> AllowedHosts { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com", "github-releases.githubusercontent.com",
    };

    /// <exception cref="UpdateException">The redirect target is not an allowed GitHub HTTPS URL.</exception>
    public static void Validate(Uri target)
    {
        if (!target.IsAbsoluteUri || target.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(target.IdnHost)
            || target.UserInfo.Length > 0 || target.Port != 443)
        {
            throw new UpdateException("The update download was redirected outside GitHub");
        }
    }
}

/// <summary>
/// HttpClient downloader with the 0.4.x limits: 20 s per network operation, 180 s per download,
/// size limits enforced from Content-Length and while streaming, and GitHub-only redirects.
/// </summary>
/// <remarks>
/// TLS uses SChannel with the Windows certificate store and its chain engine (including
/// intermediate discovery), which is what the Python build obtained through <c>truststore</c>.
/// No certificate callback is installed, and no process-wide setting is changed.
/// </remarks>
public sealed class ReleaseDownloader : IReleaseDownloader, IDisposable
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan UpdateDeadline = TimeSpan.FromSeconds(180);

    /// <summary>Deadline for large optional content downloads (Extra Maps is about 1 GB).</summary>
    public static readonly TimeSpan ContentDeadline = TimeSpan.FromMinutes(30);
    private const int MaxRedirects = 10;
    private const int BlockSize = 1024 * 1024;

    private readonly HttpClient _client;
    private readonly string _userAgent;
    private readonly TimeSpan _downloadDeadline;

    public ReleaseDownloader() : this(UpdateDeadline)
    {
    }

    public ReleaseDownloader(TimeSpan downloadDeadline) : this(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        ConnectTimeout = OperationTimeout,
    }, downloadDeadline: downloadDeadline)
    {
    }

    internal ReleaseDownloader(HttpMessageHandler handler, string? version = null, TimeSpan? downloadDeadline = null)
    {
        _downloadDeadline = downloadDeadline ?? UpdateDeadline;
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _userAgent = "Vegas-Reborn-Companion/" + (version ?? ReleaseInfo.CurrentVersion);
    }

    public async Task<byte[]> DownloadBytesAsync(Uri url, long limit, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await TransferAsync(url, limit, () => buffer, leaveOpen: true, progress: null, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    public Task<long> DownloadFileAsync(Uri url, long limit, string destination, IProgress<long>? progress = null,
        CancellationToken cancellationToken = default) =>
        TransferAsync(url, limit, () => new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BlockSize),
            leaveOpen: false, progress, cancellationToken);

    public void Dispose() => _client.Dispose();

    private async Task<long> TransferAsync(Uri url, long limit, Func<Stream> openOutput, bool leaveOpen,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_downloadDeadline);
        try
        {
            using var response = await SendAsync(url, deadline.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > limit)
            {
                throw new UpdateException("The update download exceeds its size limit");
            }
            await using var body = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            var output = openOutput();
            try
            {
                var buffer = new byte[BlockSize];
                long count = 0;
                while (true)
                {
                    int read;
                    using (var operation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
                    {
                        operation.CancelAfter(OperationTimeout);
                        read = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(BlockSize, limit - count + 1)), operation.Token)
                            .ConfigureAwait(false);
                    }
                    if (read == 0)
                    {
                        break;
                    }
                    count += read;
                    if (count > limit)
                    {
                        throw new UpdateException("The update download exceeds its size limit");
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
                    progress?.Report(count);
                }
                await output.FlushAsync(deadline.Token).ConfigureAwait(false);
                return count;
            }
            finally
            {
                if (!leaveOpen)
                {
                    await output.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (UpdateException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new UpdateException("The update download timed out");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException
                                          or UnauthorizedAccessException or InvalidOperationException or UriFormatException)
        {
            throw new UpdateException("Could not download the update. Check your connection and retry.");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Uri url, CancellationToken cancellationToken)
    {
        var accept = url.AbsoluteUri.Contains("api.github.com/", StringComparison.Ordinal)
            ? "application/vnd.github+json"
            : "application/octet-stream";
        var current = url;
        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            HttpResponseMessage response;
            using (var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                operation.CancelAfter(OperationTimeout);
                response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, operation.Token)
                    .ConfigureAwait(false);
            }
            var status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null || redirects >= MaxRedirects)
                {
                    throw new HttpRequestException("Unusable redirect");
                }
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                ReleaseRedirects.Validate(current);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                throw new HttpRequestException("HTTP " + status);
            }
            return response;
        }
    }
}
