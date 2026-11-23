namespace VegasReborn.Core.Update;

/// <summary>Fixed, user-safe update diagnostics. <see cref="Exception.Message"/> may be shown as is.</summary>
public class UpdateException : Exception
{
    public UpdateException(string message) : base(message) { }
}

/// <summary>Installation is waiting for original Vegas or Vegas 2 game/server processes.</summary>
public sealed class GamesBlockingUpdateException : UpdateException
{
    public GamesBlockingUpdateException(string message) : base(message) { }
}
