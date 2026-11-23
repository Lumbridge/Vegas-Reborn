namespace VegasReborn.Core.Display;

/// <summary>How the companion presents the game window.</summary>
public enum DisplayMode
{
    GameDefault,
    Borderless,
    Windowed,
    Fullscreen,
}

/// <summary>Preference strings, shared with the 0.4.x Python companion's preferences.json.</summary>
public static class DisplayModes
{
    public static IReadOnlyList<DisplayMode> All { get; } =
        [DisplayMode.GameDefault, DisplayMode.Borderless, DisplayMode.Windowed, DisplayMode.Fullscreen];

    /// <summary>The stored and displayed name ("Game default", "Borderless", ...).</summary>
    public static string Name(DisplayMode mode) => mode switch
    {
        DisplayMode.GameDefault => "Game default",
        DisplayMode.Borderless => "Borderless",
        DisplayMode.Windowed => "Windowed",
        DisplayMode.Fullscreen => "Fullscreen",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static bool TryParse(string? name, out DisplayMode mode)
    {
        foreach (var candidate in All)
        {
            if (Name(candidate) == name)
            {
                mode = candidate;
                return true;
            }
        }
        mode = DisplayMode.GameDefault;
        return false;
    }
}
