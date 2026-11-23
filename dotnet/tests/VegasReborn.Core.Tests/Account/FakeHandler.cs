using System.Net;
using System.Text;

namespace VegasReborn.Core.Tests.Account;

/// <summary>A recorded request: method, path, JSON body (or null) and bearer (or null).</summary>
public sealed record RecordedRequest(string Method, string Path, string? Body, string? Bearer);

/// <summary>Scripted HTTP responses; no network.</summary>
public sealed class FakeHandler : HttpMessageHandler
{
    private readonly Queue<(int Status, string? Json)> _responses;

    public FakeHandler(params (int Status, string? Json)[] responses)
    {
        _responses = new Queue<(int, string?)>(responses);
    }

    public List<RecordedRequest> Calls { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Add(new RecordedRequest(request.Method.Method, request.RequestUri!.AbsolutePath, body, request.Headers.Authorization?.Parameter));
        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("Unexpected request");
        }
        var (status, json) = _responses.Dequeue();
        return new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new ByteArrayContent(json is null ? [] : Encoding.UTF8.GetBytes(json)),
        };
    }
}

/// <summary>Monotonic fake clock with a delay that advances it.</summary>
public sealed class FakeClock
{
    public TimeSpan Now { get; set; } = TimeSpan.FromSeconds(100);

    public TimeSpan Read() => Now;

    public Task Delay(TimeSpan span, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Now += span;
        return Task.CompletedTask;
    }
}
