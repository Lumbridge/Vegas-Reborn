using VegasReborn.Core;
using VegasReborn.Core.Content;

namespace VegasReborn.Server;

/// <summary>
/// <c>VegasServer maps-pack install|remove|status</c>: manages the Vegas 1 Maps Pack in the
/// installation's content store. The server's game copy picks it up on its next start.
/// </summary>
internal static class MapsPackCommand
{
    internal const string StopFirst = "Stop the server first.";

    /// <param name="acquireHostLock">The Vegas 1 server's host lock for this installation, or null while a server holds it.</param>
    /// <param name="createInstaller">Only called for install and remove, after the host lock is held.</param>
    public static async Task<int> RunAsync(
        string verb,
        AppPaths paths,
        Func<IDisposable?> acquireHostLock,
        Func<MapsPackInstaller> createInstaller,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
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
                        output.WriteLine("Maps Pack removed.");
                        return 0;
                    }
                    var last = -1;
                    // Callbacks run synchronously on the downloader's thread, so lines keep their order.
                    var progress = new SynchronousProgress<MapsPackProgress>(value =>
                    {
                        var percent = Percent(value);
                        if (percent != last)
                        {
                            last = percent;
                            output.WriteLine($"Downloading the Maps Pack: {percent}%");
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
                    var state = MapsPackRecord.CheckQuick(paths, out var installed);
                    output.WriteLine(state == MapsPackState.Installed
                        ? $"Maps Pack {Describe(state, installed)}. It is used the next time the server starts."
                        : $"Maps Pack: {Describe(state, installed)}");
                    return 0;
                }
            default:
                throw new ArgumentException($"Unknown maps-pack command: {verb}", nameof(verb));
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
