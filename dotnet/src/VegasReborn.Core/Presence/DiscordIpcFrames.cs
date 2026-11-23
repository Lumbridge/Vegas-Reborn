using System.Buffers.Binary;

namespace VegasReborn.Core.Presence;

/// <summary>Discord desktop IPC framing: little-endian opcode and length, then a JSON payload.</summary>
public static class DiscordIpcFrames
{
    public const int MaxPayload = 65536;

    public static byte[] Frame(int opcode, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayload)
        {
            throw new InvalidDataException("Discord IPC payload too large");
        }
        var data = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(data.AsSpan(8));
        return data;
    }
}

/// <summary>Reassembles frames from fragmented reads; rejects oversized or unknown frames.</summary>
public sealed class DiscordFrameReader
{
    private readonly List<byte> _buffer = [];

    public IReadOnlyList<(int Opcode, byte[] Payload)> Feed(ReadOnlySpan<byte> data)
    {
        _buffer.AddRange(data);
        var frames = new List<(int, byte[])>();
        while (_buffer.Count >= 8)
        {
            var header = _buffer.GetRange(0, 8).ToArray();
            var opcode = BinaryPrimitives.ReadUInt32LittleEndian(header);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            if (size > DiscordIpcFrames.MaxPayload || opcode > 4)
            {
                throw new InvalidDataException("Invalid Discord IPC frame");
            }
            if (_buffer.Count < size + 8)
            {
                break;
            }
            frames.Add(((int)opcode, _buffer.GetRange(8, (int)size).ToArray()));
            _buffer.RemoveRange(0, 8 + (int)size);
        }
        return frames;
    }
}
