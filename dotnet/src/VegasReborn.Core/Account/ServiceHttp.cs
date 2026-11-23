using System.Net.Http.Headers;
using System.Text.Json;

namespace VegasReborn.Core.Account;

/// <summary>Status code and parsed JSON body (null when the body was empty).</summary>
internal readonly record struct ServiceResponse(int Status, JsonElement? Body);

/// <summary>Error codes and exception factory for one service family.</summary>
internal sealed record ServiceErrors(string Redirect, string TooLarge, string Failed, Func<string, Exception> Create);

/// <summary>
/// Fixed-origin HTTPS with a total deadline, a bounded response and no redirects.
/// The handler is injectable for tests; the default never follows redirects,
/// stores cookies or uses a proxy, like the Python http.client transport.
/// </summary>
internal sealed class ServiceHttp : IDisposable
{
    private readonly HttpClient _client;
    private readonly ServiceErrors _errors;

    public ServiceHttp(AccountOrigin origin, HttpMessageHandler? handler, ServiceErrors errors)
    {
        _errors = errors;
        _client = new HttpClient(handler ?? CreateDefaultHandler(), disposeHandler: handler is null)
        {
            BaseAddress = origin.BaseAddress,
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public static SocketsHttpHandler CreateDefaultHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = System.Net.DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.Zero,
    };

    public async Task<ServiceResponse> SendAsync(HttpMethod method, string path, byte[]? body, string? bearer,
        TimeSpan timeout, int limit, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.ConnectionClose = true;
            if (bearer is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            }
            if (body is not null)
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
            {
                throw _errors.Create(_errors.Redirect);
            }
            var payload = await ReadBoundedAsync(response.Content, limit, deadline.Token).ConfigureAwait(false);
            if (payload is null)
            {
                throw _errors.Create(_errors.TooLarge);
            }
            if (payload.Length == 0)
            {
                return new ServiceResponse(status, null);
            }
            using var document = JsonDocument.Parse(payload);
            return new ServiceResponse(status, document.RootElement.Clone());
        }
        catch (ServiceException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw _errors.Create(_errors.Failed);
        }
    }

    /// <summary>Reads at most <paramref name="limit"/> bytes; null when the body is larger.</summary>
    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }
            if (buffer.Length + read > limit)
            {
                return null;
            }
            buffer.Write(chunk, 0, read);
        }
    }

    public void Dispose() => _client.Dispose();
}
