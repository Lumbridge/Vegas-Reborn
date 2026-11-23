using System.Net;
using System.Text;
using System.Text.Json;
using VegasReborn.Core.Update;

namespace VegasReborn.Core.Tests.Update;

public sealed class DownloadTests : IDisposable
{
    private readonly UpdateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Theory]
    [InlineData("http://github.com/unsafe")]
    [InlineData("https://example.com/payload")]
    [InlineData("https://user:pass@github.com/payload")]
    [InlineData("https://github.com:8443/payload")]
    [InlineData("https://github.com.example.com/payload")]
    [InlineData("https://api.github.com/payload")]
    public void RedirectOutsideGitHubRejected(string url) =>
        Assert.Equal("The update download was redirected outside GitHub",
            Assert.Throws<UpdateException>(() => ReleaseRedirects.Validate(new Uri(url))).Message);

    [Theory]
    [InlineData("https://github.com/Lumbridge/Vegas-Reborn/releases/download/v0.5.1/update.json")]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1")]
    [InlineData("https://objects.githubusercontent.com:443/x")]
    public void GitHubRedirectAccepted(string url) => ReleaseRedirects.Validate(new Uri(url));

    [Fact]
    public async Task FollowsGitHubRedirectsAndSendsVersionedUserAgent()
    {
        var handler = new FakeHandler(request => request.RequestUri!.Host == "github.com"
            ? Redirect("https://release-assets.githubusercontent.com/asset")
            : Ok("payload"u8.ToArray()));
        using var downloader = new ReleaseDownloader(handler, "0.5.0");
        var data = await downloader.DownloadBytesAsync(new Uri("https://github.com/Lumbridge/Vegas-Reborn/releases/download/v0.5.1/update.sig"), 64);
        Assert.Equal("payload"u8.ToArray(), data);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("Vegas-Reborn-Companion/0.5.0", request.Headers.UserAgent.ToString());
            Assert.Equal("application/octet-stream", request.Headers.Accept.ToString());
        });
    }

    [Fact]
    public async Task ApiRequestsAskForGitHubJson()
    {
        var handler = new FakeHandler(_ => Ok("{}"u8.ToArray()));
        using var downloader = new ReleaseDownloader(handler, "0.5.0");
        await downloader.DownloadBytesAsync(new Uri("https://api.github.com/repos/Lumbridge/Vegas-Reborn/releases/latest"), 1024);
        Assert.Equal("application/vnd.github+json", handler.Requests[0].Headers.Accept.ToString());
    }

    [Fact]
    public async Task RedirectOutsideGitHubIsNotFollowed()
    {
        var handler = new FakeHandler(request => request.RequestUri!.Host == "github.com"
            ? Redirect("https://example.com/payload")
            : Ok("evil"u8.ToArray()));
        using var downloader = new ReleaseDownloader(handler, "0.5.0");
        var error = await Assert.ThrowsAsync<UpdateException>(() => downloader.DownloadBytesAsync(new Uri("https://github.com/x"), 64));
        Assert.Equal("The update download was redirected outside GitHub", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SizeLimitsApplyToHeaderAndBody()
    {
        using var declared = new ReleaseDownloader(new FakeHandler(_ => Ok(new byte[65])), "0.5.0");
        Assert.Equal("The update download exceeds its size limit",
            (await Assert.ThrowsAsync<UpdateException>(() => declared.DownloadBytesAsync(new Uri("https://github.com/x"), 64))).Message);

        using var undeclared = new ReleaseDownloader(new FakeHandler(_ => Ok(new byte[65], declareLength: false)), "0.5.0");
        Assert.Equal("The update download exceeds its size limit",
            (await Assert.ThrowsAsync<UpdateException>(() => undeclared.DownloadBytesAsync(new Uri("https://github.com/x"), 64))).Message);
    }

    [Fact]
    public async Task HttpErrorsAreConnectionErrorsAndFilesAreNeverOverwritten()
    {
        using var failing = new ReleaseDownloader(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)), "0.5.0");
        Assert.Equal("Could not download the update. Check your connection and retry.",
            (await Assert.ThrowsAsync<UpdateException>(() => failing.DownloadBytesAsync(new Uri("https://github.com/x"), 64))).Message);

        var existing = Path.Combine(_fixture.Root, "existing.zip");
        File.WriteAllText(existing, "keep");
        using var downloader = new ReleaseDownloader(new FakeHandler(_ => Ok("new"u8.ToArray())), "0.5.0");
        await Assert.ThrowsAsync<UpdateException>(() => downloader.DownloadFileAsync(new Uri("https://github.com/x"), 64, existing));
        Assert.Equal("keep", File.ReadAllText(existing));
    }

    [Fact]
    public async Task FileDownloadReportsBytes()
    {
        var destination = Path.Combine(_fixture.Root, "archive.zip");
        var counts = new List<long>();
        using var downloader = new ReleaseDownloader(new FakeHandler(_ => Ok(new byte[3000])), "0.5.0");
        var count = await downloader.DownloadFileAsync(new Uri("https://github.com/x"), 3000, destination, new InlineCounter(counts));
        Assert.Equal(3000, count);
        Assert.Equal(3000, new FileInfo(destination).Length);
        Assert.Equal(3000, counts[^1]);
    }

    [Fact]
    public async Task LatestReleaseIsVerifiedAgainstGitHubMetadata()
    {
        var (archive, release) = _fixture.Build();
        var feed = new FeedDownloader(archive, release);
        var found = await ReleaseFeed.CheckLatestAsync(feed, "0.5.0", _fixture.PublicKeyHex, default);
        Assert.NotNull(found);
        Assert.Equal("0.5.1", found.Version);
        Assert.Equal(release.Manifest, found.Manifest);

        Assert.Null(await ReleaseFeed.CheckLatestAsync(feed, "0.5.1", _fixture.PublicKeyHex, default));
        Assert.Null(await ReleaseFeed.CheckLatestAsync(feed, "0.6.0", _fixture.PublicKeyHex, default));
        // The production key does not trust the synthetic manifest.
        await Assert.ThrowsAsync<UpdateException>(() => ReleaseFeed.CheckLatestAsync(feed, "0.5.0", default));
    }

    [Fact]
    public async Task DraftsAndPreReleasesAreNotOffered()
    {
        var (archive, release) = _fixture.Build();
        Assert.Null(await ReleaseFeed.CheckLatestAsync(new FeedDownloader(archive, release) { Draft = true }, "0.5.0", _fixture.PublicKeyHex, default));
        Assert.Null(await ReleaseFeed.CheckLatestAsync(new FeedDownloader(archive, release) { Prerelease = true }, "0.5.0", _fixture.PublicKeyHex, default));
    }

    [Fact]
    public async Task InconsistentGitHubMetadataRejected()
    {
        var (archive, release) = _fixture.Build();
        foreach (var feed in new[]
                 {
                     new FeedDownloader(archive, release) { AssetUrlHost = "example.com" },
                     new FeedDownloader(archive, release) { Digest = "sha256:" + new string('0', 64) },
                     new FeedDownloader(archive, release) { SizeDelta = 1 },
                     new FeedDownloader(archive, release) { Tag = "v0.5.2" },
                 })
        {
            var error = await Assert.ThrowsAsync<UpdateException>(() => ReleaseFeed.CheckLatestAsync(feed, "0.5.0", _fixture.PublicKeyHex, default));
            Assert.Equal("GitHub returned an invalid update release", error.Message);
        }
        Assert.Equal("Invalid release version", (await Assert.ThrowsAsync<UpdateException>(() =>
            ReleaseFeed.CheckLatestAsync(new FeedDownloader(archive, release) { Tag = "latest" }, "0.5.0", _fixture.PublicKeyHex, default))).Message);
    }

    private static HttpResponseMessage Ok(byte[] body, bool declareLength = true)
    {
        HttpContent content = declareLength ? new ByteArrayContent(body) : new StreamContent(new MemoryStream(body));
        if (!declareLength)
        {
            content.Headers.ContentLength = null;
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class InlineCounter(List<long> counts) : IProgress<long>
    {
        public void Report(long value) => counts.Add(value);
    }

    /// <summary>A synthetic GitHub "latest release" plus its signed assets.</summary>
    private sealed class FeedDownloader(string archive, Release release) : IReleaseDownloader
    {
        public bool Draft { get; init; }
        public bool Prerelease { get; init; }
        public string Tag { get; init; } = "v" + release.Version;
        public string AssetUrlHost { get; init; } = "github.com";
        public string? Digest { get; init; } = "sha256:" + release.Sha256;
        public long SizeDelta { get; init; }

        public Task<byte[]> DownloadBytesAsync(Uri url, long limit, CancellationToken cancellationToken = default)
        {
            var baseUrl = release.BaseUrl;
            if (url.AbsoluteUri == $"https://api.github.com/repos/{ReleaseInfo.Repository}/releases/latest")
            {
                object Asset(string name, long size) => new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["browser_download_url"] = baseUrl.Replace("github.com", AssetUrlHost) + name,
                    ["size"] = size,
                    ["digest"] = name.EndsWith(".zip") ? Digest : null,
                };
                var latest = new Dictionary<string, object>
                {
                    ["tag_name"] = Tag,
                    ["draft"] = Draft,
                    ["prerelease"] = Prerelease,
                    ["assets"] = new[]
                    {
                        Asset("update.json", release.Manifest.Length),
                        Asset("update.sig", 64),
                        Asset(release.Archive, new FileInfo(archive).Length + SizeDelta),
                    },
                };
                return Task.FromResult(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(latest)));
            }
            Assert.StartsWith(baseUrl.Replace("v0.5.1", Tag), url.AbsoluteUri);
            return Task.FromResult(url.AbsoluteUri.EndsWith("update.json") ? release.Manifest
                : url.AbsoluteUri.EndsWith("update.sig") ? release.Signature
                : throw new InvalidOperationException(url.AbsoluteUri));
        }

        public Task<long> DownloadFileAsync(Uri url, long limit, string destination, IProgress<long>? progress = null,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}
