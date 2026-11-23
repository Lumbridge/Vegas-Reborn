using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using VegasReborn.Core.Update;

namespace VegasReborn.Core.Tests.Update;

/// <summary>Signed, synthetic update fixtures; no game, accounts or GitHub writes.</summary>
public sealed class UpdateFixture : IDisposable
{
    private readonly Ed25519PrivateKeyParameters _key;
    private int _archives;

    public UpdateFixture()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vegas-update-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        // Hosted runners may provide an 8.3 alias in %TEMP%.
        Root = SafePaths.Resolve(directory);
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        _key = (Ed25519PrivateKeyParameters)pair.Private;
        PublicKeyHex = Convert.ToHexStringLower(((Ed25519PublicKeyParameters)pair.Public).GetEncoded());
        Contents = new Dictionary<string, byte[]>
        {
            ["VegasReborn.exe"] = "new companion"u8.ToArray(),
            ["runtime-0.5.1/VegasReborn.Core.dll"] = "new runtime"u8.ToArray(),
            ["README.txt"] = "new readme"u8.ToArray(),
            ["version.json"] = """{"version":"0.5.1"}"""u8.ToArray(),
            ["local/companion/service.json"] = "public defaults"u8.ToArray(),
        };
    }

    public string Root { get; }
    public string PublicKeyHex { get; }
    public Dictionary<string, byte[]> Contents { get; }

    public byte[] Sign(byte[] data)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, _key);
        signer.BlockUpdate(data, 0, data.Length);
        return signer.GenerateSignature();
    }

    public static byte[] ManifestBytes(object manifest) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest));

    public static Dictionary<string, object> ManifestFor(string archive, IReadOnlyDictionary<string, byte[]> contents, string version = "0.5.1") => new()
    {
        ["schema"] = 1,
        ["version"] = version,
        ["repository"] = ReleaseInfo.Repository,
        ["platform"] = "win64",
        ["archive"] = $"VegasReborn-{version}-win64.zip",
        ["bytes"] = new FileInfo(archive).Length,
        ["sha256"] = SafePaths.Digest(archive),
        ["files"] = contents.Select(pair => new Dictionary<string, object>
        {
            ["path"] = pair.Key,
            ["bytes"] = pair.Value.Length,
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(pair.Value)),
            ["mode"] = ManagedPaths.SeedFiles.Contains(pair.Key) ? "seed" : "replace",
        }).ToList(),
    };

    public (string Archive, Release Release) Build(IReadOnlyDictionary<string, byte[]>? contents = null, Action<ZipArchive>? zipHook = null)
    {
        contents ??= Contents;
        var archive = Path.Combine(Root, $"bundle-{_archives++}.zip");
        using (var bundle = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            foreach (var (name, data) in contents)
            {
                using var stream = bundle.CreateEntry("VegasReborn/" + name).Open();
                stream.Write(data);
            }
            zipHook?.Invoke(bundle);
        }
        var manifest = ManifestBytes(ManifestFor(archive, contents));
        return (archive, ReleaseManifest.Verify(manifest, Sign(manifest), PublicKeyHex));
    }

    public (string Payload, Release Release) Payload()
    {
        var (archive, release) = Build();
        var payload = Path.Combine(Root, "payload");
        Directory.CreateDirectory(payload);
        UpdateArchive.Extract(archive, payload, release);
        return (payload, release);
    }

    public static readonly IReadOnlyDictionary<string, string> Sentinels = new Dictionary<string, string>
    {
        ["local/companion/accounts/test/profile/R6_PECPEC"] = "character fixture",
        ["local/companion/account-session.json"] = "synthetic session",
        ["local/companion/preferences.json"] = "user preferences",
        ["local/companion/game/owned-asset"] = "synthetic game sentinel",
        ["local/companion/service.json"] = "existing config",
    };

    public string InstallRoot()
    {
        var root = Path.Combine(Root, "installed");
        Directory.CreateDirectory(Path.Combine(root, "local/companion/accounts/test/profile"));
        Directory.CreateDirectory(Path.Combine(root, "local/companion/game"));
        File.WriteAllText(Path.Combine(root, "VegasReborn.exe"), "old companion");
        File.WriteAllText(Path.Combine(root, "version.json"), """{"version":"0.4.14"}""");
        File.WriteAllText(Path.Combine(root, "README.txt"), "old readme");
        foreach (var (name, data) in Sentinels)
        {
            File.WriteAllText(Path.Combine(root, name), data);
        }
        return root;
    }

    public static void AssertSentinels(string root)
    {
        foreach (var (name, data) in Sentinels)
        {
            Assert.Equal(data, File.ReadAllText(Path.Combine(root, name)));
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
