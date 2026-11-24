namespace VegasReborn.Core.Content;

/// <summary>Fixed, user-safe content diagnostics. <see cref="Exception.Message"/> may be shown as is.</summary>
public sealed class ContentException(string message) : Exception(message);
