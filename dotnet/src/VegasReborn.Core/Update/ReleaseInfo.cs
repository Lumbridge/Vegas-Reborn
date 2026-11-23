using System.Reflection;

namespace VegasReborn.Core.Update;

/// <summary>Identity of this build, as <c>src/vegas_reborn/version.py</c> defines it for 0.4.x.</summary>
public static class ReleaseInfo
{
    /// <summary>GitHub repository whose latest stable release is the update feed.</summary>
    public const string Repository = "Lumbridge/Vegas-Reborn";

    /// <summary>
    /// The running companion's version (MAJOR.MINOR.PATCH), taken from the assembly version that
    /// <c>dotnet/Directory.Build.props</c> sets. The packaged <c>version.json</c> must match it.
    /// </summary>
    public static string CurrentVersion { get; } = FromAssembly(typeof(ReleaseInfo).Assembly);

    internal static string FromAssembly(Assembly assembly)
    {
        var version = assembly.GetName().Version ?? new Version(0, 0, 0);
        return $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
    }
}
