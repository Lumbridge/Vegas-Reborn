using System.IO.Pipes;

namespace VegasReborn.Core.Presence;

/// <summary>
/// \\.\pipe\discord-ipc-N with bounded asynchronous I/O, so a hung Discord cannot hold
/// the companion. Reads only what PeekNamedPipe reports as available.
/// </summary>
internal sealed class WindowsDiscordPipe : IDiscordPipe
{
    private static readonly TimeSpan IoTimeout = TimeSpan.FromMilliseconds(200);
    private readonly NamedPipeClientStream _pipe;
    private readonly DiscordFrameReader _reader = new();

    private WindowsDiscordPipe(NamedPipeClientStream pipe) => _pipe = pipe;

    public static IDiscordPipe ConnectDesktop()
    {
        for (var index = 0; index < 10; index++)
        {
            var pipe = new NamedPipeClientStream(".", "discord-ipc-" + index, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                pipe.Connect(0);
                return new WindowsDiscordPipe(pipe);
            }
            catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException)
            {
                pipe.Dispose();
            }
        }
        throw new IOException("Discord desktop unavailable");
    }

    private void Run(Func<CancellationToken, Task> operation)
    {
        using var timeout = new CancellationTokenSource(IoTimeout);
        try
        {
            operation(timeout.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("Discord IPC timed out");
        }
    }

    public void Send(int opcode, byte[] payload)
    {
        var data = DiscordIpcFrames.Frame(opcode, payload);
        Run(token => _pipe.WriteAsync(data, token).AsTask());
    }

    public IReadOnlyList<(int Opcode, byte[] Payload)> Poll()
    {
        if (!NativeMethods.PeekNamedPipe(_pipe.SafePipeHandle, IntPtr.Zero, 0, IntPtr.Zero, out var available, IntPtr.Zero))
        {
            throw new IOException("Discord IPC closed");
        }
        if (available == 0)
        {
            return [];
        }
        var buffer = new byte[Math.Min(available, DiscordIpcFrames.MaxPayload + 8)];
        var received = 0;
        Run(async token => received = await _pipe.ReadAsync(buffer, token).ConfigureAwait(false));
        if (received == 0)
        {
            throw new IOException("Discord IPC closed");
        }
        return _reader.Feed(buffer.AsSpan(0, received));
    }

    public void Dispose() => _pipe.Dispose();
}
