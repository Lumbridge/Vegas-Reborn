using System.Text.RegularExpressions;

namespace VegasReborn.Core.Update;

/// <summary>
/// The release-file allowlist. It is deliberately the 0.4.x Python updater's <c>managed_path</c>:
/// GitHub only offers the latest release, so every .NET release must stay installable by an
/// unmodified 0.4.14 companion. Everything a .NET release adds lives under
/// <c>runtime-&lt;version&gt;/</c>. Account, preference, save and game directories are never managed.
/// </summary>
public static partial class ManagedPaths
{
    /// <summary>Installed only when absent; the user's copy is never replaced.</summary>
    public static IReadOnlySet<string> SeedFiles { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "local/companion/service.json", "local/companion/root-public.der",
    };

    public static IReadOnlySet<string> RootFiles { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "VegasReborn.exe", "README.txt", "LICENSE-Python.txt", "LICENSE-Minhook.txt", "THIRD-PARTY-NOTICES.txt", "version.json",
    };

    /// <summary>Vegas 1 native components accepted under <c>local/companion/bin/</c>.</summary>
    public static IReadOnlySet<string> NativeFiles { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "VegasCompanion.dll", "VegasDedicated.dll", "VegasInjector.exe", "VegasSteamGate.dll", "VegasSteamControl.exe", "build.json",
    };

    // Python rejects CON, PRN, AUX, NUL, COM1-9 and LPT1-9. Windows also treats CONIN$, CONOUT$
    // and the superscript COM/LPT digits as devices; rejecting them too is strictly safer.
    [GeneratedRegex(@"\A(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])(?:\..*)?\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex DeviceName();

    [GeneratedRegex(@"\AVegas(?:Companion|Dedicated)-[1-9][0-9]*\.dll\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionedNativeModule();

    /// <summary>True when a forward-slash release path may be written by an update to <paramref name="version"/>.</summary>
    public static bool IsManaged(string? name, string version)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('\\') || name.Contains(':'))
        {
            return false;
        }
        var parts = name.Split('/');
        foreach (var part in parts)
        {
            if (part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || DeviceName().IsMatch(part)
                // Python rejects control characters. The other Win32-invalid characters cannot be
                // created on Windows anyway and are rejected up front.
                || part.Any(character => character < 32 || character is '<' or '>' or '"' or '|' or '?' or '*'))
            {
                return false;
            }
        }
        if (RootFiles.Contains(name) || SeedFiles.Contains(name))
        {
            return true;
        }
        if (name.StartsWith("runtime-" + version + "/", StringComparison.Ordinal))
        {
            return true;
        }
        if (name.StartsWith("local/companion/bin/", StringComparison.Ordinal) && parts.Length == 4)
        {
            return NativeFiles.Contains(parts[^1]) || VersionedNativeModule().IsMatch(parts[^1]);
        }
        return false;
    }
}
