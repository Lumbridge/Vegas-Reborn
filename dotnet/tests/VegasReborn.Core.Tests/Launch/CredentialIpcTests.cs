using System.Net.Sockets;
using System.Text;
using VegasReborn.Core.Launch;
using VegasReborn.Core.Titles;

namespace VegasReborn.Core.Tests.Launch;

/// <summary>Companion IPC checks; actual retail acceptance is recorded separately.</summary>
public sealed class CredentialIpcTests : IAsyncLifetime
{
    private readonly FakeCredentials _client = new();
    private readonly Messages _messages = new();
    private CredentialIpc _ipc = null!;

    public Task InitializeAsync()
    {
        _ipc = new CredentialIpc(_client, new NativeCredentialRequest(TitleCatalog.Vegas1), _messages);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _ipc.DisposeAsync();

    private async Task<(int Status, string Body, string Head)> Request(
        string path = "/native-credential", string? capability = null, string body = "", string method = "POST",
        string? extraHeaders = null, CredentialIpc? ipc = null)
    {
        ipc ??= _ipc;
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", ipc.Port);
        var stream = client.GetStream();
        var request = $"{method} {path} HTTP/1.1\r\nHost: 127.0.0.1\r\nAuthorization: Bearer {capability ?? ipc.Capability}\r\n"
            + $"Content-Length: {Encoding.ASCII.GetByteCount(body)}\r\n{extraHeaders}\r\n{body}";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        using var reader = new StreamReader(stream, Encoding.ASCII);
        var response = await reader.ReadToEndAsync();
        var split = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = response[..split];
        return (int.Parse(head.Split(' ')[1]), response[(split + 4)..], head);
    }

    [Fact]
    public async Task CapabilityRequiredBeforeMint()
    {
        var (status, body, _) = await Request(capability: new string('b', 64));
        Assert.Equal(401, status);
        Assert.Empty(_client.Mints);
        Assert.DoesNotContain("UseOnce123", body);
    }

    [Fact]
    public async Task OnlyExactEmptyRequestIsAccepted()
    {
        Assert.Equal(404, (await Request(path: "/native-credential?account=123")).Status);
        Assert.Equal(401, (await Request(body: "untrusted-account-id")).Status);
        Assert.Equal(401, (await Request(extraHeaders: "Transfer-Encoding: chunked\r\n")).Status);
        Assert.Equal(501, (await Request(method: "GET")).Status);
        Assert.Empty(_client.Mints);
    }

    [Fact]
    public async Task ValidRequestMintsAtGameLogin()
    {
        Assert.Empty(_client.Mints);
        var (status, body, head) = await Request();
        Assert.Equal(200, status);
        Assert.Equal("d0123456789abcd\nUseOnce123\n", body);
        Assert.Contains("Cache-Control: no-store", head);
        Assert.Single(_client.Mints);
        Assert.DoesNotContain("UseOnce123", _messages.ToString());
        Assert.Contains("Discord credentials supplied to the game.", _messages.Items);
    }

    [Fact]
    public async Task UpstreamFailureIsSanitized()
    {
        _client.Fail = true;
        var (status, body, _) = await Request();
        Assert.Equal(503, status);
        Assert.DoesNotContain("private-token", body);
        Assert.DoesNotContain("private-token", _messages.ToString());
        Assert.Contains("Game sign-in failed. Sign in to Discord again, then retry in the game.", _messages.Items);
    }

    [Fact]
    public async Task ConcurrentMintIsRejected()
    {
        Assert.True(_ipc.MintLock.Wait(0));
        try
        {
            Assert.Equal(503, (await Request()).Status);
            Assert.Empty(_client.Mints);
        }
        finally
        {
            _ipc.MintLock.Release();
        }
    }

    [Fact]
    public async Task DedicatedIpcUsesHostRoleWithoutChangingGameRequest()
    {
        await using var dedicated = new CredentialIpc(_client, new NativeCredentialRequest(TitleCatalog.Vegas1, Dedicated: true));
        var (status, body, _) = await Request(ipc: dedicated);
        Assert.Equal((200, "h0123456789abcd\nUseOnce123\n"), (status, body));
        Assert.True(Assert.Single(_client.Mints).Dedicated);
    }

    [Fact]
    public void UnsupportedStatisticsCodecIsRejected() =>
        Assert.Throws<LaunchException>(() => new CredentialIpc(_client, new NativeCredentialRequest(TitleCatalog.Vegas1, "other")));

    [Fact]
    public async Task ClosedEndpointRefusesConnections()
    {
        var port = _ipc.Port;
        await _ipc.DisposeAsync();
        using var client = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => client.ConnectAsync("127.0.0.1", port));
    }
}
