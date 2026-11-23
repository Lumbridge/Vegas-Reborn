using VegasReborn.Core.Launch;

namespace VegasReborn.Core.Tests.Launch;

public sealed class KeyIdCodecTests
{
    [Fact]
    public void Rc4MatchesThePublishedVector()
    {
        Assert.Equal("bbf316e8d940af0ad3",
            Convert.ToHexStringLower(KeyIdCodec.Rc4("Key"u8.ToArray(), "Plaintext"u8.ToArray())));
    }

    [Fact]
    public void EncodesTheKeyTheVegas2ClientSignsWith()
    {
        // Accepted by the original Vegas 2 client against a local realm, 2026-11-23.
        Assert.Equal("98e33c6067a29591dc4b8a24a5f2947e33f537cece65e48e", KeyIdCodec.Encode("RS6-TEST-0000-0000-0001"));
        Assert.Equal("RS6-TEST-0000-0000-0001", KeyIdCodec.Decode("98e33c6067a29591dc4b8a24a5f2947e33f537cece65e48e"));
    }

    [Fact]
    public void LicenseIsAlreadyAnEncodedKey()
    {
        // A realm license decodes to its 23-character native test key and re-encodes unchanged.
        const string license = "98e33c6067a29591dc4b8a24a5f2947e33f537cece65e48e";
        Assert.Equal(license, KeyIdCodec.Encode(KeyIdCodec.Decode(license)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("123456789012345678901234")]
    [InlineData("has space")]
    public void RejectsKeysTheBufferCannotHold(string key)
    {
        Assert.ThrowsAny<ArgumentException>(() => KeyIdCodec.Encode(key));
    }

    [Fact]
    public void UpdatesTheKeyInAnExistingVegas2Profile()
    {
        var root = Directory.CreateTempSubdirectory("keyid-").FullName;
        try
        {
            var game = Directory.CreateDirectory(Path.Combine(root, "game", "KellerGame/Config/PC")).Parent!.Parent!.Parent!.FullName;
            var profileDirectory = Path.Combine(root, "profile");
            Directory.CreateDirectory(profileDirectory);
            var file = Path.Combine(profileDirectory, "PlayerProfile.ini");
            File.WriteAllText(file, "[CDKEY]\r\nKEYID=old\r\n[Other]\r\nValue=1\r\n");
            var profile = TitleLaunchProfile.For(VegasReborn.Core.Titles.TitleCatalog.ByKey("vegas2"));

            GamePreparer.InstallCdKey(game, profileDirectory, profile, new string('a', 48));

            Assert.Equal("[CDKEY]\r\nKEYID=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\r\n[Other]\r\nValue=1\r\n",
                File.ReadAllText(file));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
