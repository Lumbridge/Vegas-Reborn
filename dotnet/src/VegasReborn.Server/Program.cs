using System.Runtime.InteropServices;
using VegasReborn.Core;
using VegasReborn.Core.Account;
using VegasReborn.Core.Content;
using VegasReborn.Core.Dedicated;
using VegasReborn.Core.Launch;
using VegasReborn.Core.Titles;
using VegasReborn.Core.Update;

namespace VegasReborn.Server;

/// <summary>
/// Headless dedicated server host: <c>VegasServer.exe init|check|login|run|stop</c>.
/// It uses the installation's service configuration, native components and account
/// session, and keeps each game's server state under <c>local/dedicated/&lt;game&gt;</c>.
/// </summary>
public static class Program
{
    private const string Usage = """
        Usage: VegasServer <command> [options]

        Commands:
          init --game-folder <path>   Write server.json for the installed game
          check                       Check the game files, components and server.json
          login                       Sign in with Discord for online hosting
          run [--local-test]          Run the server until Ctrl+C or 'stop'
          stop                        Ask a running server to stop
          extra-maps install [--force] Install Vegas 1 Extra Maps (--force reinstalls it)
          extra-maps remove|status    Remove Extra Maps or show its state

        Options:
          --game vegas1|vegas2        Which game (default vegas1)
          --root <path>               Installation folder (default: this installation)

        Server settings are in local\dedicated\<game>\server.json.
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return 0;
        }
        try
        {
            var options = Options.Parse(args);
            var paths = options.Root is null ? AppPaths.ForInstallation() : new AppPaths(options.Root);
            return options.Command switch
            {
                "init" => Init(paths, options),
                "check" => Check(paths, options),
                "login" => await LoginAsync(paths).ConfigureAwait(false),
                "run" => await RunAsync(paths, options).ConfigureAwait(false),
                "stop" => Stop(paths, options),
                "extra-maps" or "maps-pack" => await MapsPackAsync(paths, options).ConfigureAwait(false),
                _ => Fail(Usage),
            };
        }
        catch (UsageException error)
        {
            return Fail(error.Message + "\n\n" + Usage);
        }
        catch (Exception error) when (error is LaunchException or ServiceException or ContentException)
        {
            return Fail(error.Message);
        }
    }

    private static int Init(AppPaths paths, Options options)
    {
        var folder = options.GameFolder ?? throw new UsageException("init needs --game-folder");
        if (!Path.IsPathFullyQualified(folder))
        {
            throw new UsageException("--game-folder must be a full path");
        }
        DedicatedAdapters.For(options.Title);
        var file = DedicatedHost.SettingsFile(paths, options.Title);
        if (File.Exists(file))
        {
            return Fail($"{file} already exists. Edit it, or delete it and run init again.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, DedicatedSettings.Template(options.Title, folder));
        Console.WriteLine($"Wrote {file}");
        return Check(paths, options);
    }

    private static int Check(AppPaths paths, Options options)
    {
        var settings = DedicatedSettings.Load(DedicatedHost.SettingsFile(paths, options.Title));
        var problems = new DedicatedHost(paths).Check(settings);
        foreach (var problem in problems)
        {
            Console.Error.WriteLine(problem);
        }
        if (problems.Count > 0)
        {
            return 1;
        }
        Console.WriteLine($"{settings.Title.DisplayName} server files and settings are ready.");
        using var account = new CompanionAccount(paths);
        Console.WriteLine(account.IsSignedIn
            ? "Signed in for online hosting."
            : "Not signed in. Run 'VegasServer login' before hosting online.");
        return 0;
    }

    private static async Task<int> LoginAsync(AppPaths paths)
    {
        using var account = new CompanionAccount(paths);
        using var cancel = CancelOnSignal();
        var session = await account.SignInAsync(url =>
        {
            Console.WriteLine("Open this link in a browser on any device and approve the sign-in:");
            Console.WriteLine(url);
        }, null, cancel.Token).ConfigureAwait(false);
        Console.WriteLine(AccountMessages.SignedInAs(session));
        return 0;
    }

    private static async Task<int> RunAsync(AppPaths paths, Options options)
    {
        var settings = DedicatedSettings.Load(DedicatedHost.SettingsFile(paths, options.Title));
        using var account = new CompanionAccount(paths);
        if (!options.LocalTest && !account.IsSignedIn)
        {
            return Fail("Not signed in. Run 'VegasServer login' first.");
        }
        using var cancel = CancelOnSignal();
        var result = await new DedicatedHost(paths).RunAsync(new DedicatedRunRequest
        {
            Settings = settings,
            Credentials = options.LocalTest ? null : account,
        }, new SynchronousProgress(Console.WriteLine), cancel.Token).ConfigureAwait(false);
        Console.WriteLine($"Run record: {Path.Combine(result.RunDirectory, "receipt.json")}");
        return result.StopReason == "stop_request" ? 0 : 1;
    }

    private static int Stop(AppPaths paths, Options options)
    {
        DedicatedHost.RequestStop(paths, options.Title);
        Console.WriteLine($"Stop requested for the {options.Title.DisplayName} server.");
        return 0;
    }

    private static async Task<int> MapsPackAsync(AppPaths paths, Options options)
    {
        using var cancel = CancelOnSignal();
        using var downloader = new ReleaseDownloader(ReleaseDownloader.ContentDeadline);
        var lockRoot = Path.GetFullPath(DedicatedHost.Root(paths, TitleCatalog.Vegas1));
        return await MapsPackCommand.RunAsync(
            options.Verb!,
            paths,
            () => new WindowsDedicatedSystem().TryAcquireHostLock(lockRoot),
            () => new MapsPackInstaller(paths, downloader),
            Console.Out,
            Console.Error,
            cancel.Token,
            options.Force).ConfigureAwait(false);
    }

    /// <summary>Ctrl+C, Ctrl+Break, console close and service/container termination cancel the token.</summary>
    private static SignalCancellation CancelOnSignal() => new();

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private sealed class SignalCancellation : IDisposable
    {
        private readonly CancellationTokenSource _source = new();
        private readonly List<PosixSignalRegistration> _registrations = [];

        public SignalCancellation()
        {
            foreach (var signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGQUIT, PosixSignal.SIGTERM })
            {
                _registrations.Add(PosixSignalRegistration.Create(signal, context =>
                {
                    // Keep the process alive until the server has been stopped and recorded.
                    context.Cancel = true;
                    _source.Cancel();
                }));
            }
        }

        public CancellationToken Token => _source.Token;

        public void Dispose()
        {
            foreach (var registration in _registrations)
            {
                registration.Dispose();
            }
            _source.Dispose();
        }
    }

    /// <summary>Reports on the calling thread so console output keeps its order.</summary>
    private sealed class SynchronousProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    internal sealed class UsageException(string message) : Exception(message);

    internal sealed record Options(string Command, GameTitle Title, string? GameFolder, string? Root, bool LocalTest, string? Verb = null, bool Force = false)
    {
        public static Options Parse(string[] args)
        {
            if (args.Length == 0)
            {
                throw new UsageException("No command given");
            }
            var title = TitleCatalog.Vegas1;
            string? folder = null, root = null;
            var localTest = false;
            var force = false;
            string? verb = null;
            var first = 1;
            if (args[0] is "extra-maps" or "maps-pack")
            {
                if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new UsageException($"{args[0]} needs install, remove or status");
                }
                verb = args[1] is "install" or "remove" or "status"
                    ? args[1]
                    : throw new UsageException($"Unknown {args[0]} command: {args[1]}");
                first = 2;
            }
            for (var index = first; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--game":
                        var key = Value(args, ref index);
                        title = TitleCatalog.All.FirstOrDefault(item => item.Key == key) ?? throw new UsageException("--game must be vegas1 or vegas2");
                        break;
                    case "--game-folder":
                        folder = Value(args, ref index);
                        break;
                    case "--root":
                        root = Value(args, ref index);
                        break;
                    case "--local-test":
                        localTest = true;
                        break;
                    case "--force" when verb == "install":
                        force = true;
                        break;
                    default:
                        throw new UsageException($"Unknown option: {args[index]}");
                }
            }
            if (verb is not null && title.Key != TitleCatalog.Vegas1.Key)
            {
                throw new UsageException("Extra Maps is only for Rainbow Six Vegas");
            }
            return new Options(args[0], title, folder, root, localTest, verb, force);
        }

        private static string Value(string[] args, ref int index) =>
            ++index < args.Length ? args[index] : throw new UsageException($"{args[index - 1]} needs a value");
    }
}
