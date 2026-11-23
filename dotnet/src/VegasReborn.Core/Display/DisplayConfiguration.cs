using System.Text.Json;
using VegasReborn.Core.Launch;

namespace VegasReborn.Core.Display;

/// <summary>Startup display keys in the companion-owned copy's engine ini files only.</summary>
public static class DisplayConfiguration
{
    public static readonly IReadOnlyList<string> EngineFiles =
        ["DefaultEngine.ini", "PC/DefaultEngine.ini", "PC/KellerEngine.ini", "PCKellerEngine.ini"];

    private const string BaselineFile = ".companion-display.json";
    private static readonly string[] Keys = ["StartupFullscreen", "StartupResolutionX", "StartupResolutionY"];

    /// <summary>The saved monitor, else the primary, else the first; null when none.</summary>
    public static DisplayMonitor? SelectMonitor(IReadOnlyList<DisplayMonitor> monitors, string device = "") =>
        monitors.FirstOrDefault(monitor => monitor.Device == device)
        ?? monitors.FirstOrDefault(monitor => monitor.Primary)
        ?? monitors.FirstOrDefault();

    /// <summary>
    /// Use keys present in shipped [WinDrv.WindowsClient]. The copied startup settings are
    /// remembered so choosing Game default undoes the companion's overrides.
    /// </summary>
    public static void Configure(string game, DisplayMode mode, DisplayMonitor? monitor = null)
    {
        var baselinePath = Path.Combine(game, BaselineFile);
        var baseline = File.Exists(baselinePath)
            ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(baselinePath)) ?? []
            : [];
        var restoring = mode == DisplayMode.GameDefault;
        if (restoring && baseline.Count == 0)
        {
            return;
        }
        var values = new Dictionary<string, string>
        {
            ["StartupFullscreen"] = mode == DisplayMode.Fullscreen ? "True" : "False",
        };
        if (mode == DisplayMode.Borderless && monitor is not null)
        {
            values["StartupResolutionX"] = monitor.Bounds.Width.ToString(System.Globalization.CultureInfo.InvariantCulture);
            values["StartupResolutionY"] = monitor.Bounds.Height.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        foreach (var relative in EngineFiles)
        {
            var target = Path.Combine(game, "KellerGame/Config", relative);
            if (!File.Exists(target))
            {
                continue;
            }
            var section = "";
            var lines = new List<string>();
            if (!baseline.TryGetValue(relative, out var original))
            {
                baseline[relative] = original = [];
            }
            foreach (var text in PrivateFile.Lines(PrivateFile.ReadConfigText(target)))
            {
                var line = text;
                if (line.Trim().StartsWith('['))
                {
                    section = line.Trim();
                }
                var key = line.Split('=', 2)[0].Trim();
                if (section == "[WinDrv.WindowsClient]" && Keys.Contains(key))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length == 2)
                    {
                        original.TryAdd(key, parts[1]);
                    }
                    if (restoring)
                    {
                        line = key + "=" + original[key];
                    }
                    else if (values.TryGetValue(key, out var value))
                    {
                        line = key + "=" + value;
                    }
                    else if (mode == DisplayMode.Windowed)
                    {
                        line = key + "=" + original[key];
                    }
                }
                lines.Add(line);
            }
            PrivateFile.WriteText(target, string.Join('\n', lines) + "\n");
        }
        if (restoring)
        {
            File.Delete(baselinePath);
        }
        else
        {
            PrivateFile.WriteText(baselinePath, JsonSerializer.Serialize(baseline));
        }
    }
}
