namespace VegasReborn.Core.Settings;

/// <summary>
/// The vertical field of view choices. Both games use 59.84 degrees by default
/// (see docs/NATIVE_FIELD_OF_VIEW.md); the adapters accept 50 to 100.
/// </summary>
public static class FieldOfView
{
    public static IReadOnlyList<int> Choices { get; } = [65, 70, 75, 80, 85, 90];

    public static bool IsChoice(int degrees) => Choices.Contains(degrees);
}
