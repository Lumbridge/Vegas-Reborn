using System.Text.Json;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace VegasReborn.Core.Update;

/// <summary>How a release file is installed.</summary>
public enum ReleaseFileMode
{
    /// <summary>Always replaced by the signed copy.</summary>
    Replace,
    /// <summary>Installed only when the file is absent (public service defaults).</summary>
    Seed,
}

/// <summary>One signed file of a release: forward-slash path, size and SHA-256.</summary>
public sealed record ReleaseFile(string Path, long Bytes, string Sha256, ReleaseFileMode Mode);

/// <summary>A release whose signed <c>update.json</c> passed every check.</summary>
public sealed record Release(
    string Version,
    string Archive,
    string Sha256,
    long Size,
    IReadOnlyList<ReleaseFile> Files,
    byte[] Manifest,
    byte[] Signature)
{
    public string BaseUrl => $"https://github.com/{ReleaseInfo.Repository}/releases/download/v{Version}/";
}

/// <summary>Verification of the signed <c>update.json</c> (schema 1), as the 0.4.x Python updater does it.</summary>
public static partial class ReleaseManifest
{
    public const long MaxDownload = 200L * 1024 * 1024;
    public const long MaxUnpacked = 700L * 1024 * 1024;
    public const int MaxManifestBytes = 2 * 1024 * 1024;
    public const int MaxFiles = 5000;

    [GeneratedRegex(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
    internal static partial Regex Sha256Hex();

    /// <summary>Verifies <paramref name="data"/> with the pinned release key and validates its contents.</summary>
    /// <exception cref="UpdateException">The signature or the manifest is invalid.</exception>
    public static Release Verify(byte[] data, byte[] signature) => Verify(data, signature, UpdateKey.PublicKeyHex);

    internal static Release Verify(byte[] data, byte[] signature, string publicKeyHex)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(signature);
        if (data.Length > MaxManifestBytes || signature.Length != 64)
        {
            throw new UpdateException("Invalid signed update manifest");
        }
        if (!VerifySignature(data, signature, publicKeyHex))
        {
            throw new UpdateException("The update signature could not be verified");
        }
        try
        {
            using var document = JsonDocument.Parse(data);
            var value = document.RootElement;
            var version = Json.String(value, "version");
            ReleaseVersion.Parse(version);
            var archive = $"VegasReborn-{version}-win64.zip";
            var bytes = Json.Int64(value, "bytes");
            var sha256 = Json.String(value, "sha256");
            if (!Json.IsInteger(value, "schema", 1) || Json.String(value, "repository") != ReleaseInfo.Repository
                || Json.String(value, "platform") != "win64" || Json.String(value, "archive") != archive
                || bytes is not (> 0 and <= MaxDownload) || !Sha256Hex().IsMatch(sha256))
            {
                throw new FormatException();
            }
            var entries = Json.Property(value, "files");
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() is < 1 or > MaxFiles)
            {
                throw new FormatException();
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<ReleaseFile>();
            long total = 0;
            foreach (var entry in entries.EnumerateArray())
            {
                var path = Json.String(entry, "path");
                var size = Json.Int64(entry, "bytes");
                var digest = Json.String(entry, "sha256");
                var mode = ManagedPaths.SeedFiles.Contains(path) ? ReleaseFileMode.Seed : ReleaseFileMode.Replace;
                if (!ManagedPaths.IsManaged(path, version) || !seen.Add(path) || size < 0 || !Sha256Hex().IsMatch(digest)
                    || Json.String(entry, "mode") != (mode == ReleaseFileMode.Seed ? "seed" : "replace"))
                {
                    throw new FormatException();
                }
                total = checked(total + size);
                files.Add(new ReleaseFile(path, size, digest, mode));
            }
            if (total > MaxUnpacked || !seen.Contains("VegasReborn.exe") || !seen.Contains("version.json"))
            {
                throw new FormatException();
            }
            return new Release(version, archive, sha256, bytes, files, (byte[])data.Clone(), (byte[])signature.Clone());
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException
                                          or KeyNotFoundException or OverflowException)
        {
            throw new UpdateException("The signed update manifest is unsupported or unsafe");
        }
    }

    private static bool VerifySignature(byte[] data, byte[] signature, string publicKeyHex)
    {
        try
        {
            var key = new Ed25519PublicKeyParameters(Convert.FromHexString(publicKeyHex));
            var verifier = new Ed25519Signer();
            verifier.Init(false, key);
            verifier.BlockUpdate(data, 0, data.Length);
            return verifier.VerifySignature(signature);
        }
        catch (Exception error) when (error is FormatException or ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>Strict accessors matching Python's <c>value["key"]</c> plus <c>type(...) is int/str</c> checks.</summary>
internal static class Json
{
    public static JsonElement Property(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            ? property
            : throw new KeyNotFoundException(name);

    public static string String(JsonElement value, string name)
    {
        var property = Property(value, name);
        return property.ValueKind == JsonValueKind.String ? property.GetString()! : throw new FormatException(name);
    }

    public static long Int64(JsonElement value, string name)
    {
        var property = Property(value, name);
        return property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number)
            ? number
            : throw new FormatException(name);
    }

    public static bool IsInteger(JsonElement value, string name, long expected) =>
        Property(value, name) is { ValueKind: JsonValueKind.Number } property
        && property.TryGetInt64(out var number) && number == expected;

    /// <summary>Python truthiness of an optional member (missing and null are false).</summary>
    public static bool Truthy(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property))
        {
            return false;
        }
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => property.GetString()!.Length > 0,
            JsonValueKind.Number => property.GetDouble() != 0,
            JsonValueKind.Array => property.GetArrayLength() > 0,
            JsonValueKind.Object => property.EnumerateObject().Any(),
            _ => false,
        };
    }
}
