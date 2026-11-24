using System.Text.Json;
using System.Text.RegularExpressions;

namespace VegasReborn.Core.Launch;

/// <summary>
/// The signed-release service configuration (local/companion/service.json) and the
/// realm root public key next to it, checked against its pinned SHA-256.
/// </summary>
public sealed partial record ServiceConfiguration(
    string Host,
    int Port,
    string NativeLicenseId,
    string RootPublicSha256,
    string PublicKeyPath)
{
    public const string ServiceHost = "vegas.lumbridge.co.uk";

    public static ServiceConfiguration Load(AppPaths paths) => Load(paths.ServiceConfig);

    public static ServiceConfiguration Load(string path)
    {
        path = Path.GetFullPath(path);
        string host, license, rootSha;
        int port;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var versionNumber) || versionNumber != 1
                || !TryString(value, "host", out host) || host != ServiceHost
                || !value.TryGetProperty("port", out var portValue) || portValue.ValueKind != JsonValueKind.Number
                || !portValue.TryGetInt32(out port) || port != 3074
                || !TryString(value, "nativeLicenseId", out license) || !LicensePattern().IsMatch(license)
                || !TryString(value, "rootPublicSha256", out rootSha) || !ShaPattern().IsMatch(rootSha))
            {
                throw new LaunchException("Invalid launcher service configuration");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new LaunchException("Invalid launcher service configuration");
        }
        var publicKey = Path.Combine(Path.GetDirectoryName(path)!, "root-public.der");
        string actual;
        try
        {
            actual = PrivateFile.Sha256(publicKey);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new LaunchException("Launcher public key failed its integrity check");
        }
        if (actual != rootSha)
        {
            throw new LaunchException("Launcher public key failed its integrity check");
        }
        return new ServiceConfiguration(host, port, license, rootSha, publicKey);
    }

    private static bool TryString(JsonElement value, string name, out string result)
    {
        result = "";
        if (!value.TryGetProperty(name, out var item) || item.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        result = item.GetString()!;
        return true;
    }

    [GeneratedRegex(@"\A[0-9a-f]{48}\z")]
    private static partial Regex LicensePattern();

    [GeneratedRegex(@"\A[0-9a-f]{64}\z")]
    private static partial Regex ShaPattern();
}
