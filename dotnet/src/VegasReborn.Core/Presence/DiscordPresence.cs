using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VegasReborn.Core.Presence;

/// <summary>A connected Discord desktop IPC pipe.</summary>
public interface IDiscordPipe : IDisposable
{
    void Send(int opcode, byte[] payload);

    /// <summary>Frames received so far; never blocks waiting for data.</summary>
    IReadOnlyList<(int Opcode, byte[] Payload)> Poll();
}

/// <summary>
/// Native Discord desktop IPC Rich Presence.
/// No OAuth bearer or native game credential is sent over this connection. Discord being
/// unavailable never affects play. Status text goes to the progress callback.
/// </summary>
public sealed class DiscordPresence : IAsyncDisposable, IDisposable
{
    /// <summary>The Vegas Reborn Discord application.</summary>
    public const string ApplicationId = "1571611222636236904";

    private readonly IProgress<string>? _notify;
    private readonly Func<IDiscordPipe> _connector;
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task? _loop;
    private string? _desired;
    private string? _sent;
    private (string Nonce, string Value, double Deadline)? _pending;
    private double _nextConnect, _nextSend, _handshakeDeadline;
    private int _nonce;
    private string? _status;

    /// <summary>Starts the background IPC loop against the Discord desktop app.</summary>
    public DiscordPresence(IProgress<string>? notify = null)
        : this(notify, WindowsDiscordPipe.ConnectDesktop, start: true)
    {
    }

    internal DiscordPresence(IProgress<string>? notify, Func<IDiscordPipe> connector, bool start)
    {
        _notify = notify;
        _connector = connector;
        if (start)
        {
            _loop = Task.Run(RunAsync);
        }
    }

    internal IDiscordPipe? Pipe { get; private set; }

    internal bool Ready { get; private set; }

    /// <summary>Last status text ("Discord desktop connected", ...).</summary>
    public string? Status => _status;

    /// <summary>Share this activity; null clears it and disconnects (the privacy switch).</summary>
    public void SetActivity(PresenceActivity? activity)
    {
        lock (_lock)
        {
            _desired = activity?.ToJson();
        }
    }

    private void SetStatus(string value)
    {
        if (_status != value)
        {
            _status = value;
            _notify?.Report(value);
        }
    }

    private void Disconnect()
    {
        Pipe?.Dispose();
        Pipe = null;
        Ready = false;
        _sent = null;
        _pending = null;
    }

    private void SendActivity(string? value)
    {
        _nonce++;
        Pipe!.Send(1, Encoding.UTF8.GetBytes("{\"cmd\":\"SET_ACTIVITY\",\"args\":{\"pid\":" + Environment.ProcessId
            + ",\"activity\":" + (value ?? "null") + "},\"nonce\":\"" + _nonce + "\"}"));
    }

    /// <summary>One step of the IPC state machine at monotonic time <paramref name="now"/> (seconds).</summary>
    internal void Tick(double now)
    {
        string? desired;
        lock (_lock)
        {
            desired = _desired;
        }
        if (desired is null)
        {
            if (Pipe is not null && Ready)
            {
                SendActivity(null);
            }
            Disconnect();
            SetStatus("Discord activity sharing off");
            return;
        }
        if (Pipe is null)
        {
            if (now < _nextConnect)
            {
                return;
            }
            _nextConnect = now + 5;
            SetStatus("Open Discord desktop to share your activity");
            Pipe = _connector();
            Pipe.Send(0, Encoding.UTF8.GetBytes("{\"v\":1,\"client_id\":\"" + ApplicationId + "\"}"));
            _handshakeDeadline = now + 10;
            SetStatus("Connecting to Discord desktop…");
        }
        foreach (var (opcode, payload) in Pipe.Poll())
        {
            if (opcode == 3)
            {
                Pipe.Send(4, payload);
            }
            else if (opcode == 2)
            {
                throw new IOException("Discord IPC closed");
            }
            else if (opcode == 1)
            {
                if (JsonNode.Parse(payload) is not JsonObject reply)
                {
                    throw new InvalidDataException("Invalid Discord reply");
                }
                var evt = Text(reply["evt"]);
                if (evt == "READY")
                {
                    Ready = true;
                    SetStatus("Discord desktop connected");
                }
                else if (evt == "ERROR")
                {
                    throw new InvalidDataException("Discord rejected activity");
                }
                else if (_pending is { } pending && Text(reply["nonce"]) == pending.Nonce && Text(reply["cmd"]) == "SET_ACTIVITY")
                {
                    _sent = pending.Value;
                    _pending = null;
                    SetStatus("Sharing activity with Discord");
                }
            }
        }
        if (!Ready && now > _handshakeDeadline)
        {
            throw new TimeoutException("Discord handshake timed out");
        }
        if (_pending is { } waiting && now > waiting.Deadline)
        {
            throw new TimeoutException("Discord activity acknowledgement timed out");
        }
        if (Ready && _pending is null && desired != _sent && now >= _nextSend)
        {
            SendActivity(desired);
            _pending = (_nonce.ToString(System.Globalization.CultureInfo.InvariantCulture), desired, now + 10);
            _nextSend = now + 2;
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static double Seconds => Environment.TickCount64 / 1000.0;

    internal static bool IsDiscordFailure(Exception error) =>
        error is IOException or TimeoutException or InvalidDataException or JsonException
            or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException;

    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    Tick(Seconds);
                }
                catch (Exception error) when (IsDiscordFailure(error))
                {
                    Disconnect();
                    _nextConnect = Seconds + 5;
                    SetStatus("Discord unavailable — play continues normally");
                }
                try
                {
                    await Task.Delay(50, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            try
            {
                if (Pipe is not null && Ready)
                {
                    SendActivity(null);
                }
            }
            catch (Exception error) when (IsDiscordFailure(error))
            {
            }
            Disconnect();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null)
        {
            await Task.WhenAny(_loop, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        }
        else
        {
            Disconnect();
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
