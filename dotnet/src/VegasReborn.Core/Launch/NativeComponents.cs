using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using VegasReborn.Core.Titles;

namespace VegasReborn.Core.Launch;

/// <summary>Actionable failures for required native launch components; no secrets logged.</summary>
public static class NativeComponents
{
    public const string SteamControl = "VegasSteamControl.exe";
    public const string SteamGate = "VegasSteamGate.dll";
    public const string Injector = "VegasInjector.exe";
    private static readonly TimeSpan ComponentTimeout = TimeSpan.FromSeconds(40);

    /// <summary>The Windows error code carried by an exception, if any.</summary>
    internal static int? WindowsError(Exception error) => error switch
    {
        Win32Exception win32 => win32.NativeErrorCode,
        _ when (error.HResult & 0xFFFF0000) == 0x80070000 => error.HResult & 0xFFFF,
        _ => null,
    };

    public static string ComponentError(string name, Exception error)
    {
        var code = WindowsError(error);
        if (code is 225 or 226)
        {
            return $"Windows Security blocked or quarantined {name} (Windows error {code}).\n\n"
                + "This required launch component cannot run. Review the detection in Windows Security > "
                + "Virus & threat protection > Protection history. Repeated sign-in will not repair it. "
                + "Your installed game and local character were preserved.";
        }
        if (error is FileNotFoundException or DirectoryNotFoundException || code is 2 or 3)
        {
            return $"Required launch component is missing: {name}.\n\n"
                + "The installation is incomplete, or security software removed this file. "
                + "Check Windows Security > Protection history, then reinstall a complete official release once the file detection has been addressed. "
                + "Your installed game and local character were preserved.";
        }
        if (error is UnauthorizedAccessException || code == 5)
        {
            return $"Windows denied access to the required launch component {name}. Check its file access and security-software history. Your local character was preserved.";
        }
        var detail = code is null ? "" : $" (Windows error {code})";
        return $"Could not access the required launch component {name}{detail}. Your installed game and local character were preserved.";
    }

    /// <summary>The component names a title's launch needs in the native folder.</summary>
    public static IReadOnlyList<string> Required(GameTitle title) => [SteamControl, SteamGate, Injector, title.NativeAdapter];

    public static void RequireComponents(string directory, GameTitle title, string? adapterPath = null)
    {
        foreach (var name in Required(title))
        {
            try
            {
                var path = name == title.NativeAdapter && adapterPath is not null ? adapterPath : Path.Combine(directory, name);
                using var stream = File.OpenRead(path);
                if (stream.ReadByte() < 0)
                {
                    throw new NativeLaunchException($"Required launch component is empty: {name}. Reinstall a complete official release. Your local character was preserved.");
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new NativeLaunchException(ComponentError(name, error));
            }
        }
    }

    /// <summary>Keep Steam's mapped gate image outside the replaceable installation tree.</summary>
    public static string RuntimeGate(string source, string cache)
    {
        var name = Path.GetFileName(source);
        try
        {
            var contents = File.ReadAllBytes(source);
            var directory = Path.Combine(cache, PrivateFile.Sha256(contents));
            var target = Path.Combine(directory, name);
            try
            {
                if (File.ReadAllBytes(target).AsSpan().SequenceEqual(contents))
                {
                    return target;
                }
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
            }
            Directory.CreateDirectory(directory);
            // The launcher mutex serializes publication across companion installs.
            var temporary = Path.Combine(directory, Path.GetRandomFileName());
            try
            {
                File.WriteAllBytes(temporary, contents);
                File.Move(temporary, target, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
            return target;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new NativeLaunchException(ComponentError(name, error));
        }
    }

    /// <summary>
    /// Runs a native helper and returns its JSON object result. The caller's cancellation
    /// is deliberately not applied: a helper interrupted mid-way could leave Steam gated.
    /// </summary>
    public static async Task<JsonObject> RunAsync(IReadOnlyList<string> arguments)
    {
        var name = Path.GetFileName(arguments[0]);
        var info = new ProcessStartInfo(arguments[0])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments.Skip(1))
        {
            info.ArgumentList.Add(argument);
        }
        Process process;
        try
        {
            process = Process.Start(info) ?? throw new Win32Exception(2);
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException)
        {
            throw new NativeLaunchException(ComponentError(name, error));
        }
        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(ComponentTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception)
                {
                }
                throw new NativeLaunchException($"{name} did not finish launch setup within 40 seconds. Your local character was preserved.");
            }
            var stdout = await output.ConfigureAwait(false);
            await errors.ConfigureAwait(false);
            return ParseResult(name, process.ExitCode, stdout);
        }
    }

    internal static JsonObject ParseResult(string name, int exitCode, string stdout)
    {
        if (exitCode != 0)
        {
            throw new NativeLaunchException($"{name} failed during launch setup (exit code {exitCode}). Your installed game and local character were preserved.");
        }
        try
        {
            if (JsonNode.Parse(stdout) is JsonObject value)
            {
                return value;
            }
        }
        catch (JsonException)
        {
        }
        throw new NativeLaunchException($"{name} returned an invalid launch result. Your local character was preserved.");
    }
}
