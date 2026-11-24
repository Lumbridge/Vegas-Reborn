using VegasReborn.Core;
using VegasReborn.Core.Content;

namespace VegasReborn.Server;

/// <summary>
/// <c>VegasServer extra-maps install [--force]|remove|status</c> (also accepted as <c>maps-pack</c>): manages Vegas 1 Extra Maps in the
/// installation's content store. The server's game copy picks it up on its next start.
/// </summary>
internal static class MapsPackCommand
{
    internal const string StopFirst = "Stop the server first.";
    internal const string DamagedText = "Extra Maps is damaged. Download it again or remove it.";

    /// <param name="acquireHostLock">The Vegas 1 server's host lock for this installation, or null while a server holds it.</param>
    /// <param name="createInstaller">Only called for install and remove, after the host lock is held.</param>
    /// <param name="force">Install even when the published version is already installed.</param>
    public static async Task<int> RunAsync(
        string verb,
        AppPaths paths,
        Func<IDisposable?> acquireHostLock,
        Func<MapsPackInstaller> createInstaller,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken,
        bool force = false)
    {
        switch (verb)
        {
            case "status":
                output.WriteLine(Describe(MapsPackRecord.Check(paths, out var record), record));
                return 0;
            case "install":
            case "remove":
                using (var hostLock = acquireHostLock())
                {
                    if (hostLock is null)
                    {
                        error.WriteLine(StopFirst);
                        return 1;
                    }
                    var installer = createInstaller();
                    if (verb == "remove")
                    {
                        installer.Remove();
                        output.WriteLine("Extra Maps removed.");
                        return 0;
                    }
                    if (!force && MapsPackRecord.CheckQuick(paths, out var current) == MapsPackState.Installed)
                    {
                        var release = await installer.FetchAsync(cancellationToken).ConfigureAwait(false);
                        if (current!.Version >= release.Version)
                        {
                            output.WriteLine($"Extra Maps already installed (version {current.Version}).");
                            return 0;
                        }
                    }
                    var last = -1;
                    // Callbacks run synchronously on the downloader's thread, so lines keep their order.
                    var progress = new SynchronousProgress<MapsPackProgress>(value =>
                    {
                        var percent = Percent(value);
                        if (percent != last)
                        {
                            last = percent;
                            output.WriteLine($"Downloading Extra Maps: {percent}%");
                        }
                    });
                    try
                    {
                        await installer.InstallAsync(progress, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        error.WriteLine("Cancelled.");
                        return 1;
                    }
                    return ReportInstall(paths, output, error);
                }
            default:
                throw new ArgumentException($"Unknown extra-maps command: {verb}", nameof(verb));
        }
    }

    /// <summary>Checks the store after an install; anything but installed is a failure.</summary>
    internal static int ReportInstall(AppPaths paths, TextWriter output, TextWriter error)
    {
        var state = MapsPackRecord.CheckQuick(paths, out var installed);
        switch (state)
        {
            case MapsPackState.Installed:
                output.WriteLine($"Extra Maps {Describe(state, installed)}. It is used the next time the server starts.");
                return 0;
            case MapsPackState.Damaged:
                error.WriteLine(DamagedText);
                return 1;
            default:
                error.WriteLine($"Extra Maps: {Describe(state, installed)}");
                return 1;
        }
    }

    internal static string Describe(MapsPackState state, MapsPackRecord? record) => state switch
    {
        MapsPackState.Installed => $"Installed (version {record?.Version})",
        MapsPackState.Damaged => "Damaged",
        _ => "Not installed",
    };

    internal static int Percent(MapsPackProgress value) =>
        value.TotalBytes <= 0 ? 0 : (int)Math.Clamp(value.Bytes * 100 / value.TotalBytes, 0, 100);

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
