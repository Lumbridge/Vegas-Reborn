namespace VegasReborn.Core.Display;

/// <summary>Monitor rectangle in physical pixels (right and bottom exclusive).</summary>
public readonly record struct MonitorBounds(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>A display as Windows names it (for example \\.\DISPLAY1).</summary>
public sealed record DisplayMonitor(string Device, MonitorBounds Bounds, bool Primary)
{
    public string Label => $"{Device} · {Bounds.Width} × {Bounds.Height}" + (Primary ? " · Primary" : "");
}
