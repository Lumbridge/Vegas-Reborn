namespace VegasReborn.Core;

/// <summary>
/// Installation layout, shared with the 0.4.x Python companion and its updater:
/// <c>VegasReborn.exe</c> and <c>runtime-&lt;version&gt;/</c> at the root, account and
/// game state under <c>local/</c>, which updates never replace.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string root)
    {
        Root = Path.GetFullPath(root);
    }

    /// <summary>Paths for the running installation (the folder holding VegasReborn.exe).</summary>
    public static AppPaths ForInstallation() => new(AppContext.BaseDirectory.Contains("runtime-", StringComparison.OrdinalIgnoreCase)
        ? Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName
        : AppContext.BaseDirectory);

    public string Root { get; }
    public string Local => Path.Combine(Root, "local");
    public string CompanionConfig => Path.Combine(Local, "companion");
    /// <summary>Vegas 1 native components (allowlisted by the 0.4.x updater).</summary>
    public string NativeBin => Path.Combine(CompanionConfig, "bin");
    /// <summary>
    /// Native modules the 0.4.x updater does not accept under <see cref="NativeBin"/> (the
    /// Vegas 2 adapter). Releases ship them in the running version's runtime folder.
    /// </summary>
    public string RuntimeNative => Path.Combine(Root, "runtime-" + Update.ReleaseInfo.CurrentVersion, "native");

    /// <summary>
    /// Where a title's adapter is loaded from: the runtime folder in a release, otherwise
    /// <see cref="NativeBin"/> (development layouts and the Vegas 1 adapter).
    /// </summary>
    public string NativeAdapter(string name) =>
        File.Exists(Path.Combine(RuntimeNative, name)) ? Path.Combine(RuntimeNative, name) : Path.Combine(NativeBin, name);
    public string ServiceConfig => Path.Combine(CompanionConfig, "service.json");
    public string RootPublicKey => Path.Combine(CompanionConfig, "root-public.der");
    public string AccountSession => Path.Combine(CompanionConfig, "account-session.json");
    public string Preferences => Path.Combine(CompanionConfig, "preferences.json");
    public string RetailAccounts => Path.Combine(Local, "retail-accounts");
    /// <summary>Launcher-owned Vegas 1 Maps Pack store: files/, staging/ and record.json.</summary>
    public string MapsPack => Path.Combine(Local, "content", "vegas1-maps-pack");
    public string Updates => Path.Combine(Root, ".updates");

    /// <summary>Private game copy for a title. Vegas 1 keeps the 0.4.x location.</summary>
    public string GameCopy(string titleKey) => titleKey == "vegas1"
        ? Path.Combine(CompanionConfig, "game")
        : Path.Combine(Local, titleKey, "game");
}
