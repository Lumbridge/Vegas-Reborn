using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using VegasReborn.Core.Titles;

namespace VegasReborn.Core.Profiles.Vegas1;

/// <param name="Id">Leaderboard id, formatted 0x%08x.</param>
/// <param name="Name">Leaderboard name.</param>
public sealed record CarrierBoard(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name);

/// <summary>Deterministic record of a carrier repair (same fields as the Python receipt).</summary>
public sealed record CarrierReceipt(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("statisticsCodec")] string StatisticsCodec,
    [property: JsonPropertyName("sourceSha256")] string SourceSha256,
    [property: JsonPropertyName("sourceBytes")] int SourceBytes,
    [property: JsonPropertyName("derivedSha256")] string DerivedSha256,
    [property: JsonPropertyName("derivedBytes")] int DerivedBytes,
    [property: JsonPropertyName("chunkCount")] int ChunkCount,
    [property: JsonPropertyName("boardCount")] int BoardCount,
    [property: JsonPropertyName("propertyCount")] int PropertyCount,
    [property: JsonPropertyName("modifiedBoardCount")] int ModifiedBoardCount,
    [property: JsonPropertyName("removedColumnCount")] int RemovedColumnCount,
    [property: JsonPropertyName("modifiedBoards")] IReadOnlyList<CarrierBoard> ModifiedBoards,
    [property: JsonPropertyName("unchangedChunkCount")] int UnchangedChunkCount,
    [property: JsonPropertyName("unchangedChunksSha256")] string UnchangedChunksSha256)
{
    /// <summary>True when <see cref="MatchmakingCarrier.Ensure"/> replaced the owned file.</summary>
    [JsonPropertyName("changed")]
    public bool? Changed { get; init; }

    [JsonPropertyName("readbackVerified")]
    public bool? ReadbackVerified { get; init; }
}

/// <summary>
/// Rainbow Six Vegas (1) only. Repairs only unbound computed-board columns in
/// <c>MatchmakingData.bin</c> of a companion-owned game copy. The installed carrier is read
/// only. This is an explicit client compatibility repair: the original base statistics
/// reader is unchanged, while its local metadata no longer asks that reader to
/// dereference an absent SPYC property. No wire column type, rating default or vendor
/// TrueSkill algorithm is invented. Source and result are hash-pinned.
/// Failures raise <see cref="InvalidDataException"/> or an I/O exception.
/// </summary>
public static class MatchmakingCarrier
{
    public const string Codec = "native-base-v1";
    public const string RelativeCarrier = "KellerGame/Content/Agora/runtime/MatchmakingData.bin";
    public const string OriginalSha256 = "c16445075ce36036be6c1e5c37527561bd6f7f64c2286c759abc6d6c3864ebe7";
    public const int OriginalBytes = 46170;
    public const string DerivedSha256 = "d005f2ce631007e8823b515eedb2ff23aa4097a222a5da7371b6c9182d0d2633";

    /// <summary>Launcher text when <see cref="EnsureCarrier"/> fails.</summary>
    public const string SetupFailedMessage = "The copied game's leaderboard compatibility setup failed";
    /// <summary>Launcher text when the returned codec is not <see cref="Codec"/>.</summary>
    public const string NotVerifiedMessage = "The copied game's leaderboard compatibility was not verified";

    private static readonly string[] Modes =
        ["None", "AttackAndDefend", "Survival", "Sharpshooter", "TeamSurvival", "TeamSharpshooter", "Retrieval", "CoopStory", "CoopHunt"];

    private static readonly Dictionary<uint, string> ExpectedBoards = BuildBoards();

    private static readonly (uint Id, string Name, uint Type, uint Binding)[] ExpectedColumns =
        [(61, "Skill", 2, 0xffffffff), (62, "Games_Played", 2, 0xffffffff), (63, "Mu", 2, 0xffffffff), (64, "Sigma", 2, 0xffffffff)];

    private static Dictionary<uint, string> BuildBoards()
    {
        var boards = new Dictionary<uint, string>();
        foreach (var (baseId, suffix) in new[] { (0xffff0000u, "Ranked"), (0xfffe0000u, "Standard") })
        {
            for (var index = 0; index < Modes.Length; index++)
            {
                boards[baseId + (uint)index] = $"{Modes[index]}_{suffix}_TrueSkill";
            }
        }
        return boards;
    }

    /// <summary>Deterministically derives the repaired bytes and a receipt from the pinned original.</summary>
    public static (byte[] Derived, CarrierReceipt Receipt) Derive(ReadOnlySpan<byte> raw)
    {
        if (raw.Length != OriginalBytes || Sha(raw) != OriginalSha256)
        {
            throw new InvalidDataException("Unsupported original MatchmakingData carrier");
        }
        var body = Unwrap(raw);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(body);
        if (count != 500)
        {
            throw new InvalidDataException("Unsupported original chunk count");
        }
        using var output = new MemoryStream();
        using var untouched = new MemoryStream();
        output.Write(body, 0, 4);
        var offset = 4;
        var changed = new SortedDictionary<uint, string>();
        var untouchedCount = 0;
        int boardCount = 0, propertyCount = 0;
        for (var c = 0; c < count; c++)
        {
            if (offset + 8 > body.Length)
            {
                throw new InvalidDataException("Truncated carrier chunk");
            }
            var tag = Encoding.ASCII.GetString(body, offset, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset + 4));
            if (size < 8 || offset + (long)size > body.Length)
            {
                throw new InvalidDataException("Invalid carrier chunk size");
            }
            var chunk = body.AsSpan(offset, (int)size);
            var payload = chunk[8..];
            if (tag == "SPYC")
            {
                propertyCount++;
            }
            if (tag == "SLBC")
            {
                boardCount++;
                var reader = new DefinitionReader(payload.ToArray());
                if (reader.Number(6) != 3)
                {
                    throw new InvalidDataException("Unsupported leaderboard version");
                }
                var name = reader.String();
                var identifier = reader.Number(6);
                var ranked = reader.Number(11);
                var selector24 = reader.Number(3, 1);
                var selector28 = reader.Number(3, 1);
                var flag15 = reader.Number(11);
                var sort = reader.Number(7);
                var countOffset = reader.Offset;
                var fieldCount = reader.Number(5, 2);
                var fields = new (uint Id, string Name, uint Type, uint Binding)[fieldCount];
                for (var f = 0; f < fieldCount; f++)
                {
                    fields[f] = (reader.Number(6), reader.String(), reader.Number(3, 1), reader.Number(6));
                }
                reader.Finish();
                if (ExpectedBoards.TryGetValue(identifier, out var expectedName))
                {
                    var expectedRanked = identifier >> 16 == 0xffff ? 1u : 0u;
                    if (changed.ContainsKey(identifier) || name != expectedName
                        || (ranked, selector24, selector28, flag15, sort) != (expectedRanked, 0u, 1u, 0u, 0u)
                        || !fields.SequenceEqual(ExpectedColumns))
                    {
                        throw new InvalidDataException("Computed leaderboard definition changed");
                    }
                    // Retain every metadata byte before the original field count.
                    // A typed u16 zero makes the native column initializer empty.
                    var repairedLength = countOffset + 3;
                    output.Write(chunk[..4]);
                    output.Write(BitConverter.GetBytes((uint)(repairedLength + 8)));
                    output.Write(payload[..countOffset]);
                    output.Write([0x05, 0x00, 0x00]);
                    changed[identifier] = name;
                }
                else
                {
                    if (selector28 == 1 || fields.Any(field => field.Binding == 0xffffffff))
                    {
                        throw new InvalidDataException("Unexpected additional unbound board");
                    }
                    output.Write(chunk);
                    untouched.Write(chunk);
                    untouchedCount++;
                }
            }
            else
            {
                output.Write(chunk);
                untouched.Write(chunk);
                untouchedCount++;
            }
            offset += (int)size;
        }
        if (offset != body.Length || boardCount != 75 || propertyCount != 390
            || !changed.Keys.ToHashSet().SetEquals(ExpectedBoards.Keys) || untouchedCount != 482)
        {
            throw new InvalidDataException("Unsupported original definition inventory");
        }
        var repairedBody = output.ToArray();
        var result = Wrap(repairedBody);
        if (!Unwrap(result).AsSpan().SequenceEqual(repairedBody))
        {
            throw new InvalidDataException("Derived carrier encoding failed");
        }
        var receipt = new CarrierReceipt(
            "original_pc_trueskill_carrier_repair", Codec, OriginalSha256, raw.Length,
            Sha(result), result.Length, (int)count, boardCount, propertyCount,
            changed.Count, changed.Count * 4,
            [.. changed.Select(pair => new CarrierBoard($"0x{pair.Key:x8}", pair.Value))],
            untouchedCount, Sha(untouched.ToArray()));
        return (result, receipt);
    }

    /// <summary>
    /// Installs the repair into an existing companion-owned Vegas 1 copy and returns a
    /// read-back receipt. Both arguments are game roots. Unknown destination bytes are
    /// preserved and rejected; an already repaired, hash-identical copy is accepted.
    /// </summary>
    public static CarrierReceipt Ensure(string referenceGame, string ownedGame)
    {
        var title = TitleCatalog.Vegas1;
        var reference = Path.GetFullPath(referenceGame);
        if (!Directory.Exists(reference))
        {
            throw new DirectoryNotFoundException("The reference game folder is unavailable");
        }
        var owned = PlainPath(ownedGame);
        if (!Directory.Exists(owned) || SamePath(reference, owned) || IsWithin(owned, reference) || IsWithin(reference, owned))
        {
            throw new InvalidDataException("The owned game copy must be separate from the reference");
        }
        var marker = PlainPath(Path.Combine(owned, ".companion-ready.json"));
        using (var ownership = JsonDocument.Parse(File.ReadAllBytes(marker)))
        {
            var root = ownership.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || version.GetDouble() != 1
                || !root.TryGetProperty("sourceSha256", out var source) || source.ValueKind != JsonValueKind.String
                || source.GetString() != title.ClientSha256)
            {
                throw new InvalidDataException("The destination is not a supported launcher-owned game copy");
            }
        }
        var executable = PlainPath(Path.Combine(owned, Native(title.ClientExecutable)));
        if (Sha(File.ReadAllBytes(executable)) != title.ClientSha256)
        {
            throw new InvalidDataException("The owned game executable failed its identity check");
        }
        var sourceCarrier = Path.Combine(reference, Native(RelativeCarrier));
        var target = PlainPath(Path.Combine(owned, Native(RelativeCarrier)));
        if (!IsWithin(target, owned) || !File.Exists(target))
        {
            throw new InvalidDataException("The owned carrier is unavailable");
        }
        if (FileIdentity.Same(sourceCarrier, target) || FileIdentity.LinkCount(target) != 1)
        {
            throw new InvalidDataException("The owned carrier must be an independent file");
        }
        var (repaired, receipt) = Derive(File.ReadAllBytes(sourceCarrier));
        if (receipt.DerivedSha256 != DerivedSha256)
        {
            throw new InvalidDataException("The deterministic derived carrier hash changed");
        }
        var current = File.ReadAllBytes(target);
        if (Sha(current) is not (OriginalSha256 or DerivedSha256))
        {
            throw new InvalidDataException("Unknown owned carrier bytes were preserved");
        }
        var needsChange = !current.AsSpan().SequenceEqual(repaired);
        receipt = receipt with { Changed = needsChange };
        if (needsChange)
        {
            var temporary = Path.Combine(Path.GetDirectoryName(target)!, ".trueskill-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(repaired);
                    stream.Flush(flushToDisk: true);
                }
                if (Sha(File.ReadAllBytes(temporary)) != DerivedSha256)
                {
                    throw new InvalidDataException("Temporary repaired carrier failed readback");
                }
                // Recheck ownership/path before replacing the one independent file.
                if (!SamePath(PlainPath(target), target) || FileIdentity.LinkCount(target) != 1)
                {
                    throw new InvalidDataException("Owned carrier path changed during preparation");
                }
                if (Sha(File.ReadAllBytes(target)) is not (OriginalSha256 or DerivedSha256))
                {
                    throw new InvalidDataException("Owned carrier changed during preparation");
                }
                File.Move(temporary, target, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
        var readback = File.ReadAllBytes(target);
        if (Sha(readback) != DerivedSha256 || !Unwrap(readback).AsSpan().SequenceEqual(Unwrap(repaired)))
        {
            throw new InvalidDataException("Owned repaired carrier failed final readback");
        }
        if (Sha(File.ReadAllBytes(sourceCarrier)) != OriginalSha256)
        {
            throw new InvalidDataException("The read-only reference carrier changed");
        }
        return receipt with { ReadbackVerified = true };
    }

    /// <summary>Returns the statistics codec only after source validation and owned-copy readback.</summary>
    public static string EnsureCarrier(string sourceGameRoot, string ownedGameRoot) =>
        Ensure(sourceGameRoot, ownedGameRoot).StatisticsCodec;

    /// <summary>Decodes the MD5-keyed XOR wrapper: digest(16) | length(4) | body.</summary>
    public static byte[] Unwrap(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 24 || BinaryPrimitives.ReadUInt32LittleEndian(raw[16..]) != (uint)(raw.Length - 20))
        {
            throw new InvalidDataException("Invalid carrier length");
        }
        var digest = raw[..16];
        var body = new byte[raw.Length - 20];
        for (var index = 0; index < body.Length; index++)
        {
            body[index] = (byte)(raw[20 + index] ^ digest[index % 32 / 2]);
        }
        if (!MD5.HashData(body).AsSpan().SequenceEqual(digest))
        {
            throw new InvalidDataException("Invalid carrier digest");
        }
        return body;
    }

    /// <summary>Encodes a body with the MD5-keyed XOR wrapper.</summary>
    public static byte[] Wrap(ReadOnlySpan<byte> body)
    {
        var digest = MD5.HashData(body);
        var result = new byte[body.Length + 20];
        digest.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), (uint)body.Length);
        for (var index = 0; index < body.Length; index++)
        {
            result[20 + index] = (byte)(body[index] ^ digest[index % 32 / 2]);
        }
        return result;
    }

    private static string Sha(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static string Native(string relative) => relative.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>Rejects symlink/junction traversal before resolving an owned write path.</summary>
    private static string PlainPath(string path)
    {
        var full = Path.GetFullPath(path);
        for (var item = full; !string.IsNullOrEmpty(item); item = Path.GetDirectoryName(item))
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(item);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Owned game paths cannot traverse links or junctions");
            }
        }
        return full;
    }

    private static bool SamePath(string first, string second) =>
        Path.TrimEndingDirectorySeparator(first).Equals(Path.TrimEndingDirectorySeparator(second), StringComparison.OrdinalIgnoreCase);

    private static bool IsWithin(string path, string root) =>
        SamePath(path, root) || Path.TrimEndingDirectorySeparator(path)
            .StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private sealed class DefinitionReader(byte[] data)
    {
        public int Offset { get; private set; }

        private ReadOnlySpan<byte> Take(int length)
        {
            if (length < 0 || Offset + length > data.Length)
            {
                throw new InvalidDataException("Truncated original definition");
            }
            var value = data.AsSpan(Offset, length);
            Offset += length;
            return value;
        }

        public uint Number(byte tag, int size = 4)
        {
            if (Take(1)[0] != tag)
            {
                throw new InvalidDataException("Unsupported definition tag");
            }
            var bytes = Take(size);
            uint value = 0;
            for (var i = size - 1; i >= 0; i--)
            {
                value = (value << 8) | bytes[i];
            }
            return value;
        }

        public string String()
        {
            var prefix = Take(2);
            if (prefix[0] != 0x0a || prefix[1] != 0x07)
            {
                throw new InvalidDataException("Unsupported definition string");
            }
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
            if (length is < 1 or > 256)
            {
                throw new InvalidDataException("Invalid definition string length");
            }
            var value = Take(length);
            if (value[^1] != 0 || value[..^1].Contains((byte)0))
            {
                throw new InvalidDataException("Invalid definition string");
            }
            foreach (var b in value[..^1])
            {
                if (b > 0x7f)
                {
                    throw new InvalidDataException("Invalid definition string");
                }
            }
            return Encoding.ASCII.GetString(value[..^1]);
        }

        public void Finish()
        {
            if (Offset != data.Length)
            {
                throw new InvalidDataException("Unexpected definition suffix");
            }
        }
    }

    /// <summary>NTFS file identity and hard-link count (Python samefile / st_nlink).</summary>
    private static class FileIdentity
    {
        public static bool Same(string first, string second)
        {
            var a = Read(first);
            var b = Read(second);
            return a.VolumeSerialNumber == b.VolumeSerialNumber && a.FileIndexHigh == b.FileIndexHigh && a.FileIndexLow == b.FileIndexLow;
        }

        public static uint LinkCount(string path) => Read(path).NumberOfLinks;

        private static ByHandleFileInformation Read(string path)
        {
            using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(handle, out var information))
            {
                throw new IOException("File identity unavailable", Marshal.GetHRForLastWin32Error());
            }
            return information;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public uint CreationTimeLow, CreationTimeHigh;
            public uint LastAccessTimeLow, LastAccessTimeHigh;
            public uint LastWriteTimeLow, LastWriteTimeHigh;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh, FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh, FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);
    }
}
