using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using VegasReborn.Core.Presence;
using VegasReborn.Core.Tests.Launch;
using VegasReborn.Core.Titles;

namespace VegasReborn.Core.Tests.Presence;

/// <summary>Synthetic Discord RPC peer: no desktop Discord or account traffic.</summary>
public sealed class DiscordPresenceTests : IDisposable
{
    private sealed class Pipe : IDiscordPipe
    {
        public List<(int Opcode, byte[] Payload)> Sent { get; } = [];
        public List<(int Opcode, byte[] Payload)> Replies { get; } = [];
        public bool Closed { get; private set; }

        public void Send(int opcode, byte[] payload) => Sent.Add((opcode, payload));

        public IReadOnlyList<(int Opcode, byte[] Payload)> Poll()
        {
            var result = Replies.ToList();
            Replies.Clear();
            return result;
        }

        public void Dispose() => Closed = true;

        public void Reply(JsonObject value) => Replies.Add((1, Encoding.UTF8.GetBytes(value.ToJsonString())));
    }

    private readonly Pipe _pipe = new();
    private readonly Messages _messages = new();
    private readonly DiscordPresence _presence;

    public DiscordPresenceTests() => _presence = new DiscordPresence(_messages, () => _pipe, start: false);

    public void Dispose() => _presence.Dispose();

    private static JsonObject Parse(byte[] payload) => JsonNode.Parse(payload)!.AsObject();

    private void Ready()
    {
        _presence.SetActivity(PresenceActivity.For());
        _presence.Tick(0);
        Assert.Single(_pipe.Sent); // Handshake must precede activity.
        var handshake = Parse(_pipe.Sent[0].Payload);
        Assert.Equal(1, (int)handshake["v"]!);
        Assert.Equal(DiscordPresence.ApplicationId, (string)handshake["client_id"]!);
        _pipe.Reply(new JsonObject { ["evt"] = "READY" });
        _presence.Tick(.1);
    }

    [Fact]
    public void FragmentedFramesAndBoundaries()
    {
        var reader = new DiscordFrameReader();
        var data = DiscordIpcFrames.Frame(1, "{\"evt\":\"READY\"}"u8).Concat(DiscordIpcFrames.Frame(3, "ping"u8)).ToArray();
        var result = new List<(int, byte[])>();
        foreach (var value in data)
        {
            result.AddRange(reader.Feed([value]));
        }
        Assert.Equal(2, result.Count);
        Assert.Equal((1, "{\"evt\":\"READY\"}"), (result[0].Item1, Encoding.UTF8.GetString(result[0].Item2)));
        Assert.Equal((3, "ping"), (result[1].Item1, Encoding.UTF8.GetString(result[1].Item2)));
        foreach (var (opcode, size) in new[] { (1u, 65537u), (5u, 0u) })
        {
            var header = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(header, opcode);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), size);
            Assert.Throws<InvalidDataException>(() => new DiscordFrameReader().Feed(header));
        }
        Assert.Throws<InvalidDataException>(() => DiscordIpcFrames.Frame(1, new byte[65537]));
    }

    [Fact]
    public void ActivityAcknowledgedAndUpdatesThrottled()
    {
        Ready();
        var first = Parse(_pipe.Sent[^1].Payload);
        Assert.Equal("SET_ACTIVITY", (string)first["cmd"]!);
        Assert.DoesNotContain("Sharing activity with Discord", _messages.Items);
        _pipe.Reply(new JsonObject { ["cmd"] = "SET_ACTIVITY", ["nonce"] = (string)first["nonce"]! });
        _presence.Tick(.2);
        Assert.Contains("Sharing activity with Discord", _messages.Items);
        var count = _pipe.Sent.Count;
        _presence.SetActivity(PresenceActivity.For(playing: true, started: 123));
        _presence.Tick(.3);
        Assert.Equal(count, _pipe.Sent.Count);
        _presence.Tick(2.2);
        var activity = Parse(_pipe.Sent[^1].Payload)["args"]!["activity"]!.AsObject();
        Assert.Equal(123, (long)activity["timestamps"]!["start"]!);
        Assert.False(activity.ContainsKey("secrets"));
        Assert.False(activity.ContainsKey("party"));
    }

    [Fact]
    public void PrivacyToggleClearsActivityAndDisconnects()
    {
        Ready();
        _presence.SetActivity(null);
        _presence.Tick(.2);
        Assert.Null(Parse(_pipe.Sent[^1].Payload)["args"]!["activity"]);
        Assert.True(_pipe.Closed);
        Assert.Null(_presence.Pipe);
        Assert.Equal("Discord activity sharing off", _presence.Status);
    }

    [Fact]
    public void PingAndHandshakeTimeout()
    {
        _presence.SetActivity(PresenceActivity.For());
        _presence.Tick(0);
        _pipe.Replies.Add((3, "opaque ping"u8.ToArray()));
        _presence.Tick(1);
        Assert.Equal(4, _pipe.Sent[^1].Opcode);
        Assert.Equal("opaque ping", Encoding.UTF8.GetString(_pipe.Sent[^1].Payload));
        Assert.Throws<TimeoutException>(() => _presence.Tick(11));
    }

    [Fact]
    public void ActivityErrorsAreNotSuccess()
    {
        Ready();
        _pipe.Reply(new JsonObject { ["evt"] = "ERROR", ["data"] = new JsonObject { ["message"] = "untrusted response" } });
        var error = Assert.Throws<InvalidDataException>(() => _presence.Tick(.2));
        Assert.True(DiscordPresence.IsDiscordFailure(error));
        Assert.DoesNotContain("Sharing activity with Discord", _messages.Items);
        Assert.DoesNotContain("untrusted response", _messages.ToString());
    }

    [Fact]
    public void UnacknowledgedActivityTimesOut()
    {
        Ready();
        Assert.Throws<TimeoutException>(() => _presence.Tick(11));
    }

    [Fact]
    public void ActivityTextIsTitleAware()
    {
        Assert.Equal("Ready to play", PresenceActivity.For().State);
        Assert.Equal("Vegas Reborn Launcher", PresenceActivity.For().Details);
        Assert.Equal("Playing Rainbow Six Vegas", PresenceActivity.For(playing: true).State);
        Assert.Equal("Playing Rainbow Six Vegas 2", PresenceActivity.For(TitleCatalog.Vegas2, playing: true).State);
        Assert.Equal("Original PC game", PresenceActivity.For(TitleCatalog.Vegas2, playing: true).Details);
        Assert.Equal("Preparing dedicated server", PresenceActivity.For(preparingHost: true).State);
        Assert.Equal("Dedicated server running", PresenceActivity.For(hosting: true).State);
        Assert.Equal("Playing · Dedicated server running", PresenceActivity.For(playing: true, hosting: true).State);
        Assert.Null(PresenceActivity.For(started: 123).StartTimestamp);
        Assert.Equal("{\"details\":\"Original PC game\",\"state\":\"Playing · Dedicated server running\","
            + "\"buttons\":[{\"label\":\"Get Vegas Reborn\",\"url\":\"https://github.com/Lumbridge/Vegas-Reborn/releases/latest\"}]}",
            PresenceActivity.For(playing: true, hosting: true).ToJson());
    }

    [Fact]
    public void ActivityHasOneDownloadButtonAndNoJoinData()
    {
        var activity = JsonNode.Parse(PresenceActivity.For(playing: true, started: 5).ToJson())!.AsObject();
        var buttons = activity["buttons"]!.AsArray();
        var button = Assert.Single(buttons)!.AsObject();
        Assert.Equal("Get Vegas Reborn", (string)button["label"]!);
        Assert.Equal("https://github.com/Lumbridge/Vegas-Reborn/releases/latest", (string)button["url"]!);
        Assert.False(activity.ContainsKey("secrets"));
        Assert.False(activity.ContainsKey("party"));
    }
}
