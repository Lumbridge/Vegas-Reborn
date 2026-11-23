using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace VegasReborn.Core.Launch;

/// <summary>
/// Loopback capability endpoint for the native adapter's on-demand game Login. No Discord
/// bearer is handed to the game: only an exact, empty, capability-authorized
/// <c>POST /native-credential</c> mints one native credential at a time.
/// </summary>
public sealed class CredentialIpc : IAsyncDisposable, IDisposable
{
    private const int MaxHeaderBytes = 65536;
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(5);

    private readonly ICredentialSource _source;
    private readonly NativeCredentialRequest _request;
    private readonly IProgress<string>? _progress;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _closing = new();
    private readonly Task _acceptLoop;
    private volatile bool _closed;

    public CredentialIpc(ICredentialSource source, NativeCredentialRequest request, IProgress<string>? progress = null)
    {
        if (request.StatisticsCodec is not ("" or StatisticsCarrier.Codec))
        {
            throw new LaunchException("Unsupported native statistics codec");
        }
        _source = source;
        _request = request;
        _progress = progress;
        Capability = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    /// <summary>The 64-hex bearer capability written to native-route.txt.</summary>
    public string Capability { get; }

    /// <summary>Held while a mint is in progress; a concurrent request is rejected (503).</summary>
    internal SemaphoreSlim MintLock { get; } = new(1, 1);

    private async Task AcceptLoopAsync()
    {
        while (!_closing.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_closing.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var connection = client;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        timeout.CancelAfter(ConnectionTimeout);
        try
        {
            var stream = connection.GetStream();
            var head = await ReadHeadAsync(stream, timeout.Token).ConfigureAwait(false);
            if (head is null)
            {
                return;
            }
            var (status, payload) = await RespondAsync(head, timeout.Token).ConfigureAwait(false);
            var response = new StringBuilder()
                .Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n")
                .Append("Content-Type: text/plain\r\n")
                .Append("Content-Length: ").Append(payload.Length).Append("\r\n")
                .Append("Cache-Control: no-store\r\n")
                .Append("Connection: close\r\n\r\n");
            var bytes = Encoding.ASCII.GetBytes(response.ToString());
            var all = new byte[bytes.Length + payload.Length];
            bytes.CopyTo(all, 0);
            payload.CopyTo(all, bytes.Length);
            CryptographicOperations.ZeroMemory(payload);
            try
            {
                await stream.WriteAsync(all, timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(all);
            }
        }
        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var received = new List<byte>();
        while (received.Count < MaxHeaderBytes)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return null;
            }
            received.AddRange(buffer.AsSpan(0, count));
            var text = Encoding.Latin1.GetString(received.ToArray());
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0)
            {
                return text[..end];
            }
        }
        return null;
    }

    private async Task<(int Status, byte[] Payload)> RespondAsync(string head, CancellationToken cancellationToken)
    {
        var lines = head.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/", StringComparison.Ordinal))
        {
            return (400, "Bad request"u8.ToArray());
        }
        if (requestLine[0] != "POST")
        {
            return (501, "Unsupported method"u8.ToArray());
        }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                return (400, "Bad request"u8.ToArray());
            }
            headers.TryAdd(line[..colon].Trim(), line[(colon + 1)..].Trim());
        }
        if (requestLine[1] != "/native-credential")
        {
            return (404, "Not found"u8.ToArray());
        }
        if (headers.ContainsKey("Transfer-Encoding")
            || !headers.TryGetValue("Content-Length", out var length) || length != "0"
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(headers.GetValueOrDefault("Authorization", "")),
                Encoding.UTF8.GetBytes("Bearer " + Capability)))
        {
            return (401, "Unauthorized"u8.ToArray());
        }
        if (_closed || !MintLock.Wait(0))
        {
            return (503, "Unavailable"u8.ToArray());
        }
        try
        {
            var lease = await _source.MintNativeAsync(_request, cancellationToken).ConfigureAwait(false);
            var payload = Encoding.ASCII.GetBytes(lease.Username + "\n" + lease.Password + "\n");
            _progress?.Report("Discord credentials supplied to the game.");
            return (200, payload);
        }
        catch (Exception)
        {
            // Never forward upstream details (they can carry tokens) to the game or the UI.
            _progress?.Report("Game sign-in failed. Sign in to Discord again, then retry in the game.");
            return (503, "Unavailable"u8.ToArray());
        }
        finally
        {
            MintLock.Release();
        }
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        404 => "Not Found",
        501 => "Not Implemented",
        _ => "Service Unavailable",
    };

    public async ValueTask DisposeAsync()
    {
        if (_closed)
        {
            return;
        }
        _closed = true;
        _closing.Cancel();
        _listener.Stop();
        await Task.WhenAny(_acceptLoop, Task.Delay(TimeSpan.FromSeconds(6))).ConfigureAwait(false);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
