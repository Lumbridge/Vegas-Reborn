namespace VegasReborn.Core.Content;

/// <summary>Where the optional Vegas 1 Maps Pack is published.</summary>
public static class ContentInfo
{
    public const string Repository = "Lumbridge/R6V-content";
    public const string Id = "vegas1-maps-pack";
    public const string Tag = "vegas1-maps-pack-v1";

    public static string BaseUrl => $"https://github.com/{Repository}/releases/download/{Tag}/";
}
