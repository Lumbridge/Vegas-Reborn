using System.Globalization;

namespace VegasReborn.Core.Update;

/// <summary>An update command found on the command line.</summary>
public abstract record UpdateCommand;

/// <summary><c>--apply-update &lt;plan.json&gt; --parent-pid &lt;pid&gt;</c>: run the install helper.</summary>
public sealed record ApplyUpdateCommand(string PlanPath, int ParentProcessId) : UpdateCommand;

/// <summary><c>--install-staged-update &lt;plan.json&gt;</c>: hand the plan to its staged helper and exit.</summary>
public sealed record InstallStagedUpdateCommand(string PlanPath) : UpdateCommand;

/// <summary>The command line could not be parsed (argparse exits with status 2).</summary>
public sealed record InvalidUpdateCommand(string Reason) : UpdateCommand;

/// <summary>
/// Update routing for the App's entry point, run before any other startup work. A 0.4.x Python
/// companion starts the new release's staged <c>VegasReborn.exe</c> with
/// <c>--apply-update &lt;plan.json&gt; --parent-pid &lt;pid&gt;</c>; .NET releases use the same contract.
/// </summary>
public static class UpdateCommandLine
{
    public const string ApplyUpdate = "--apply-update";
    public const string ParentPid = "--parent-pid";
    public const string InstallStagedUpdate = "--install-staged-update";

    /// <summary>Finds an update command; null when the arguments do not ask for one. Other arguments are ignored.</summary>
    public static UpdateCommand? Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? apply = null, install = null, parent = null;
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            foreach (var option in new[] { ApplyUpdate, ParentPid, InstallStagedUpdate })
            {
                string? value;
                if (argument == option)
                {
                    if (index + 1 >= args.Count || (args[index + 1].StartsWith("--", StringComparison.Ordinal) && args[index + 1].Length > 2))
                    {
                        return new InvalidUpdateCommand($"argument {option}: expected one argument");
                    }
                    value = args[++index];
                }
                else if (argument.StartsWith(option + "=", StringComparison.Ordinal))
                {
                    value = argument[(option.Length + 1)..];
                }
                else
                {
                    continue;
                }
                switch (option)
                {
                    case ApplyUpdate: apply = value; break;
                    case ParentPid: parent = value; break;
                    default: install = value; break;
                }
                break;
            }
        }
        var parentId = 0;
        if (parent is not null && !int.TryParse(parent, NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite,
                CultureInfo.InvariantCulture, out parentId))
        {
            return new InvalidUpdateCommand($"argument {ParentPid}: invalid int value: '{parent}'");
        }
        if (!string.IsNullOrEmpty(apply))
        {
            return new ApplyUpdateCommand(apply, parentId);
        }
        if (!string.IsNullOrEmpty(install))
        {
            return new InstallStagedUpdateCommand(install);
        }
        return null;
    }

    /// <summary>
    /// Runs an update command and returns the process exit code, or null when the App should
    /// start normally. Call it first in <c>Main</c>/<c>OnStartup</c>, before creating windows,
    /// reading accounts or touching game data.
    /// </summary>
    /// <param name="presentHelper">
    /// Shows the install helper's progress window and returns <c>await helper.RunAsync()</c>'s exit
    /// code. When null the helper runs without a window.
    /// </param>
    public static int? TryRun(IReadOnlyList<string> args, Func<StagedUpdateHelper, int>? presentHelper = null)
    {
        switch (Parse(args))
        {
            case null:
                return null;
            case InvalidUpdateCommand:
                return 2;
            case ApplyUpdateCommand apply:
                var helper = new StagedUpdateHelper(apply.PlanPath, apply.ParentProcessId);
                return presentHelper is null ? helper.RunAsync().GetAwaiter().GetResult() : presentHelper(helper);
            case InstallStagedUpdateCommand install:
                try
                {
                    StagedUpdate.HandOff(install.PlanPath, Environment.ProcessId);
                    return 0;
                }
                catch (UpdateException)
                {
                    return 1;
                }
            default:
                return null;
        }
    }
}

/// <summary>
/// The website's updater-only ZIP reuses an installation's Steam gate and launch helpers. If one
/// is extracted into an empty folder, the App explains how to install instead of starting setup.
/// </summary>
public static class UpdaterOnlyPackage
{
    public const string Title = "Install Vegas Reborn";
    public const string Message =
        "For first-time setup, extract the full 0.4.13 Windows download into a new folder. "
        + "Open that Companion and choose Updates to install 0.4.14.\n\nOpen the full download page?";
    public static readonly Uri FullDownloadPage = new("https://github.com/Lumbridge/Vegas-Reborn/releases/tag/v0.4.13");

    /// <summary>Launch helpers an updater-only package expects to find already installed.</summary>
    public static IReadOnlyList<string> RequiredHelpers { get; } = ["VegasInjector.exe", "VegasSteamGate.dll", "VegasSteamControl.exe"];

    /// <summary>
    /// True when this is an updater-only package (<c>runtime-&lt;version&gt;/updater-only</c>) without
    /// any of the launch helpers, i.e. a first install from the wrong download. Check it after
    /// <see cref="UpdateCommandLine.TryRun"/> and before asking for an account or creating local data.
    /// </summary>
    public static bool NeedsFullInstall(AppPaths paths, string? version = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var marker = Path.Combine(paths.Root, "runtime-" + (version ?? ReleaseInfo.CurrentVersion), "updater-only");
        return File.Exists(marker) && !RequiredHelpers.Any(name => Path.Exists(Path.Combine(paths.NativeBin, name)));
    }
}
