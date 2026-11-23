using VegasReborn.Core.Launch;

namespace VegasReborn.Core.Tests.Launch;

public sealed class ServiceAndCarrierTests
{
    private static string Config(string license, int port = 3074, string? sha = null) =>
        "{\"version\": 1, \"nativeLicenseId\": \"" + license + "\", \"host\": \"vegas.lumbridge.co.uk\", \"rootPublicSha256\": \""
        + (sha ?? Synthetic.Sha(Synthetic.KeyDer)) + "\", \"port\": " + port + "}";

    [Fact]
    public void ServiceConfigurationRequiresPinnedFieldsAndKeyIntegrity()
    {
        using var directory = new TestDirectory();
        var path = directory.File("service.json", Config(new string('a', 48)));
        directory.Bytes("root-public.der", Synthetic.KeyDer);
        var value = ServiceConfiguration.Load(path);
        Assert.Equal(("vegas.lumbridge.co.uk", 3074), (value.Host, value.Port));
        Assert.Equal(Path.Combine(directory.Path, "root-public.der"), value.PublicKeyPath);
        foreach (var invalid in new[] { Config(new string('a', 47)), Config(new string('A', 48)), Config(new string('a', 48) + "\n"), Config(new string('a', 48), 3080), "[]", "{" })
        {
            File.WriteAllText(path, invalid);
            Assert.Equal("Invalid companion service configuration", Assert.Throws<LaunchException>(() => ServiceConfiguration.Load(path)).Message);
        }
        File.WriteAllText(path, Config(new string('a', 48), sha: new string('0', 64)));
        Assert.Equal("Companion public key failed its integrity check", Assert.Throws<LaunchException>(() => ServiceConfiguration.Load(path)).Message);
    }
}
