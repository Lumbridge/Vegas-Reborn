namespace VegasReborn.Core.Launch;

/// <summary>
/// A launch failure whose <see cref="Exception.Message"/> is plain user-facing text.
/// It never contains credentials, capabilities or raw diagnostics.
/// </summary>
public class LaunchException : Exception
{
    public LaunchException(string message) : base(message)
    {
    }

    public LaunchException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>A required native launch component is missing, blocked or failed.</summary>
public sealed class NativeLaunchException : LaunchException
{
    public NativeLaunchException(string message) : base(message)
    {
    }
}
