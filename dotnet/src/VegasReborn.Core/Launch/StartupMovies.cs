using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VegasReborn.Core.Launch;

/// <summary>Reversible startup-movie settings for the companion-owned game copy.</summary>
public static partial class StartupMovies
{
    public static readonly IReadOnlyList<string> MovieFiles = ["MovieLoading.ini", "PC/MovieLoading.ini"];
    private const string BaselineFile = ".companion-movies.json";
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    public static void Configure(string game, bool skip)
    {
        var baselinePath = Path.Combine(game, BaselineFile);
        if (!skip && !File.Exists(baselinePath))
        {
            return;
        }
        var baseline = File.Exists(baselinePath)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(baselinePath)) ?? []
            : [];
        var changes = new List<(string Path, byte[] Data)>();
        foreach (var relative in MovieFiles)
        {
            var path = Path.Combine(game, "KellerGame/Config", relative);
            if (!File.Exists(path))
            {
                continue;
            }
            var section = "";
            var found = false;
            var output = new StringBuilder();
            foreach (var original in SplitKeepEnds(StrictUtf8.GetString(File.ReadAllBytes(path))))
            {
                var line = original;
                var stripped = line.Trim().TrimStart('﻿');
                if (stripped.StartsWith('['))
                {
                    section = stripped.ToUpperInvariant();
                }
                var match = EnableLine().Match(line);
                if (section == "[INTRO]" && match.Success)
                {
                    found = true;
                    baseline.TryAdd(relative, match.Groups[1].Value);
                    var value = skip ? "FALSE" : baseline[relative];
                    var group = match.Groups[1];
                    line = line[..group.Index] + value + line[(group.Index + group.Length)..];
                }
                output.Append(line);
            }
            if (!found)
            {
                throw new LaunchException("The copied game's startup movie settings are unsupported");
            }
            changes.Add((path, StrictUtf8.GetBytes(output.ToString())));
        }
        if (changes.Count == 0)
        {
            throw new LaunchException("The copied game's startup movie settings are missing");
        }
        // Save original values before changing either platform configuration.
        PrivateFile.WriteText(baselinePath, JsonSerializer.Serialize(baseline));
        foreach (var (path, data) in changes)
        {
            PrivateFile.Write(path, data);
        }
        if (!skip)
        {
            File.Delete(baselinePath);
        }
    }

    private static IEnumerable<string> SplitKeepEnds(string text)
    {
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n' || text[index] == '\r')
            {
                if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }
                yield return text[start..(index + 1)];
                start = index + 1;
            }
        }
        if (start < text.Length)
        {
            yield return text[start..];
        }
    }

    [GeneratedRegex(@"^\s*Enable\s*=\s*(TRUE|FALSE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnableLine();
}
