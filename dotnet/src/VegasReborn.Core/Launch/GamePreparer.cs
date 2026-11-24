using System.Text;
using System.Text.RegularExpressions;
using VegasReborn.Core.Titles;

namespace VegasReborn.Core.Launch;

/// <summary>
/// Companion-owned game copy: hash-checked copy of the installed game, copy-only ini
/// repairs, the realm trust key and the CD key. The installed game is only read.
/// </summary>
internal static partial class GamePreparer
{
    public const string ReadyMarker = ".companion-ready.json";

    internal static readonly string[] EngineFiles =
    [
        "KellerGame/Config/DefaultEngine.ini", "KellerGame/Config/PC/DefaultEngine.ini",
        "KellerGame/Config/PC/KellerEngine.ini", "KellerGame/Config/PCKellerEngine.ini",
    ];

    /// <summary>Copy owned assets, preserving the installed reference and user profiles.</summary>
    public static async Task<string> PrepareAsync(
        AppPaths paths,
        GameTitle title,
        TitleLaunchProfile profile,
        string source,
        IReadOnlySet<string> disabledFixes,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        IProgress<CopyProgress>? copyProgress = null)
    {
        source = Path.GetFullPath(source);
        var destination = Path.GetFullPath(paths.GameCopy(title.Key));
        if (!PrivateFile.IsWithin(destination, paths.Local) || PrivateFile.IsWithin(source, destination)
            || PrivateFile.IsWithin(destination, source))
        {
            throw new LaunchException("The launcher's game copy must be separate from the installation");
        }
        var executable = Path.Combine(source, title.ClientExecutable);
        if (!File.Exists(executable))
        {
            throw new LaunchException($"{title.DisplayName} was not found in the selected game folder. Choose the folder the game is installed in.");
        }
        if (await Task.Run(() => PrivateFile.Sha256(executable), cancellationToken).ConfigureAwait(false) != title.ClientSha256)
        {
            throw new LaunchException("This game version is not supported by the compatibility module");
        }
        var marker = Path.Combine(destination, ReadyMarker);
        if (!File.Exists(marker))
        {
            if (Directory.Exists(destination) || File.Exists(destination))
            {
                throw new LaunchException("An unfinished game copy exists; preserve it before preparing another");
            }
            progress?.Report("Preparing a separate game copy. The installed game is preserved.");
            await Task.Run(() => CopyTree(source, destination, cancellationToken, copyProgress), cancellationToken).ConfigureAwait(false);
            PrivateFile.WriteText(marker, title.Key == TitleCatalog.Vegas1.Key
                ? $"{{\"version\": 1, \"sourceSha256\": \"{title.ClientSha256}\"}}"
                : $"{{\"version\": 1, \"title\": \"{title.Key}\", \"sourceSha256\": \"{title.ClientSha256}\"}}");
        }
        var copiedExecutable = Path.Combine(destination, title.ClientExecutable);
        var largeAddressAware = title.Fix(TitleCatalog.LargeAddressAware) is not null;
        if (!await Task.Run(() => largeAddressAware
                ? LargeAddressAware.Matches(File.ReadAllBytes(copiedExecutable), title.ClientSha256)
                : PrivateFile.Sha256(copiedExecutable) == title.ClientSha256, cancellationToken).ConfigureAwait(false))
        {
            throw new LaunchException("The launcher's game copy failed its integrity check");
        }
        if (largeAddressAware && !await Task.Run(() => LargeAddressAware.Apply(copiedExecutable), cancellationToken).ConfigureAwait(false))
        {
            throw new LaunchException("The launcher's game copy failed its integrity check");
        }
        if (title.Fix(TitleCatalog.SixteenPlayers) is not null)
        {
            await Task.Run(() => MissionPlayerLimits.Apply(destination, source), cancellationToken).ConfigureAwait(false);
        }
        foreach (var (relative, expected) in profile.PinnedFiles)
        {
            if (PrivateFile.Sha256(Path.Combine(destination, relative)) != expected)
            {
                throw new LaunchException("The bundled physics runtime is not the supported original version");
            }
        }
        var occlusionFix = title.Fix(TitleCatalog.OcclusionQueries) is not null;
        if (profile.RewriteMenuUrls || occlusionFix)
        {
            var disableOcclusion = occlusionFix && !disabledFixes.Contains(TitleCatalog.OcclusionQueries);
            foreach (var relative in EngineFiles)
            {
                RepairEngineIni(Path.Combine(destination, relative), Path.Combine(source, relative),
                    profile.RewriteMenuUrls, occlusionFix, disableOcclusion);
            }
        }
        return destination;
    }

    private static bool Ignored(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower is "session" or "memdump.dmp" || lower.EndsWith(".log", StringComparison.Ordinal);
    }

    internal static void CopyTree(string source, string destination, CancellationToken cancellationToken,
        IProgress<CopyProgress>? progress = null)
    {
        var total = 0L;
        if (progress is not null)
        {
            total = TreeSize(source, cancellationToken);
            progress.Report(new CopyProgress(0, total));
        }
        var completed = 0L;
        var reported = 0L;
        var buffer = new byte[CopyChunk];
        CopyDirectory(source, destination, buffer, cancellationToken, bytes =>
        {
            completed += bytes;
            // Report about every 0.25% so the UI thread is not flooded.
            if (progress is not null && (completed - reported >= total / 400 || completed == total))
            {
                reported = completed;
                progress.Report(new CopyProgress(completed, total));
            }
        });
    }

    private static long TreeSize(string source, CancellationToken cancellationToken)
    {
        var size = 0L;
        foreach (var file in Directory.EnumerateFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Ignored(Path.GetFileName(file)))
            {
                size += new FileInfo(file).Length;
            }
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            if (!Ignored(Path.GetFileName(directory)))
            {
                size += TreeSize(directory, cancellationToken);
            }
        }
        return size;
    }

    private static void CopyDirectory(string source, string destination, byte[] buffer, CancellationToken cancellationToken, Action<long> copied)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            if (!Ignored(name))
            {
                CopyFile(file, Path.Combine(destination, name), buffer, cancellationToken, copied);
            }
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (!Ignored(name))
            {
                CopyDirectory(directory, Path.Combine(destination, name), buffer, cancellationToken, copied);
            }
        }
    }

    private const int CopyChunk = 1 << 20;

    private static void CopyFile(string source, string destination, byte[] buffer, CancellationToken cancellationToken, Action<long> copied)
    {
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1))
        {
            output.SetLength(input.Length);
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
                copied(read);
            }
        }
        File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
        File.SetAttributes(destination, File.GetAttributes(source));
    }

    private static void RepairEngineIni(string target, string original, bool menuUrls, bool occlusionFix, bool disableOcclusion)
    {
        if (!File.Exists(target))
        {
            return;
        }
        string? originalOcclusion = null;
        if (occlusionFix && !disableOcclusion && File.Exists(original))
        {
            // Off plays as the original: use the installed file's own value.
            var section = "";
            foreach (var line in PrivateFile.Lines(PrivateFile.ReadConfigText(original)))
            {
                if (line.StartsWith('['))
                {
                    section = line;
                }
                if (section == "[D3DDrv.D3DRenderDevice]" && line.StartsWith("DisableOcclusion=", StringComparison.Ordinal))
                {
                    originalOcclusion ??= line;
                }
            }
        }
        var current = "";
        var lines = new List<string>();
        foreach (var text in PrivateFile.Lines(PrivateFile.ReadConfigText(target)))
        {
            var line = text;
            if (line.StartsWith('['))
            {
                current = line;
            }
            if (menuUrls && current == "[URL]" && MapLine().IsMatch(line))
            {
                line = line.Split('=', 2)[0] + "=Menu.rmpc";
            }
            if (menuUrls && current == "[URL]" && MapExtLine().IsMatch(line))
            {
                line = line.Split('=', 2)[0] + "=rmpc";
            }
            if (occlusionFix && current == "[D3DDrv.D3DRenderDevice]" && line.StartsWith("DisableOcclusion=", StringComparison.Ordinal))
            {
                // The original blocking D3D9 occlusion-query wait can hang lobby
                // travel on current drivers. Use the game's own switch in this
                // private copy; retain native rendering/results.
                line = disableOcclusion ? "DisableOcclusion=True" : originalOcclusion ?? "DisableOcclusion=False";
            }
            lines.Add(line);
        }
        PrivateFile.WriteText(target, string.Join('\n', lines) + "\n");
    }

    /// <summary>Install the realm's trust key in the copy, per the title's trust mechanism.</summary>
    public static void InstallTrustKey(string game, GameTitle title, TitleLaunchProfile profile, string publicKey)
    {
        switch (title.TrustKey)
        {
            case TrustKeyKind.AgoraConfigLua:
            {
                var config = Path.Combine(game, "KellerGame/Config/PC/AgoraConfig.lua");
                var text = PrivateFile.ReadConfigText(config);
                var count = TrustLine().Count(text);
                if (count != 1)
                {
                    throw new LaunchException("The copied game's trust configuration is unsupported");
                }
                var keyPath = Path.GetFullPath(publicKey).Replace('\\', '/');
                text = TrustLine().Replace(text, _ => "UbiSigKeyPub = \"" + keyPath + "\";");
                PrivateFile.WriteText(config, text);
                break;
            }
            case TrustKeyKind.SignaturePub:
            {
                var data = File.ReadAllBytes(publicKey);
                // The original is a 270-byte DER RSA-2048 public key (PKCS#1).
                if (data.Length is < 200 or > 600 || data[0] != 0x30)
                {
                    throw new LaunchException("The launcher's public key is not a supported RSA key. Reinstall a complete official release.");
                }
                var target = Path.Combine(game, "KellerGame/Config/PC/signature.pub");
                var current = File.Exists(target) ? PrivateFile.Sha256(target) : null;
                var installed = PrivateFile.Sha256(data);
                if (current != installed)
                {
                    if (current != profile.OriginalSignatureSha256)
                    {
                        throw new LaunchException("The copied game's trust configuration is unsupported");
                    }
                    PrivateFile.Write(target, data);
                }
                break;
            }
            default:
                throw new LaunchException("The copied game's trust configuration is unsupported");
        }
    }

    /// <summary>
    /// [CDKEY] KEYID in PlayerProfile.ini. Both titles store the realm license, which is
    /// already the encoded CD key (<see cref="KeyIdCodec"/>), so one master test key serves
    /// both. For Vegas 1 an existing profile file is kept. For Vegas 2 KEYID is set in the
    /// profile file on every launch (other lines are kept), and also written to the profile
    /// file shipped in the copy.
    /// </summary>
    public static void InstallCdKey(string game, string profileDirectory, TitleLaunchProfile profile, string licenseId)
    {
        var keyId = licenseId;
        var text = "[CDKEY]\nKEYID=" + keyId + "\n";
        Directory.CreateDirectory(profileDirectory);
        var licenseFile = Path.Combine(profileDirectory, "PlayerProfile.ini");
        if (!File.Exists(licenseFile))
        {
            PrivateFile.Write(licenseFile, Encoding.ASCII.GetBytes(text));
        }
        else if (profile.EncodedKeyId)
        {
            var current = File.ReadAllText(licenseFile, Encoding.ASCII);
            var updated = KeyIdLine().IsMatch(current)
                ? KeyIdLine().Replace(current, "KEYID=" + keyId, 1)
                : text + current;
            if (updated != current)
            {
                PrivateFile.Write(licenseFile, Encoding.ASCII.GetBytes(updated));
            }
        }
        if (profile.CopyProfileIni is { } copied)
        {
            PrivateFile.Write(Path.Combine(game, copied), Encoding.ASCII.GetBytes(text));
        }
    }

    [GeneratedRegex(@"(?m)^KEYID=[^\r\n]*")]
    private static partial Regex KeyIdLine();

    [GeneratedRegex("^(LocalMap|Map|LocalMapRetail|MapRetail)=")]
    private static partial Regex MapLine();

    [GeneratedRegex("^MapExt(Retail)?=")]
    private static partial Regex MapExtLine();

    [GeneratedRegex(@"^UbiSigKeyPub\s*=.*?;\s*$", RegexOptions.Multiline)]
    private static partial Regex TrustLine();
}
