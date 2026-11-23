using VegasReborn.Core.Profiles.Vegas1;

namespace VegasReborn.Core.Launch;

/// <summary>Repairs the leaderboard carrier in a companion-owned copy and returns the statistics codec.</summary>
internal interface IStatisticsCarrier
{
    string Ensure(string sourceGameRoot, string ownedGameRoot);
}

/// <summary>Launch-side seam over <see cref="MatchmakingCarrier"/>, the one port of trueskill_carrier.py.</summary>
internal sealed class StatisticsCarrier : IStatisticsCarrier
{
    public const string Codec = MatchmakingCarrier.Codec;

    public string Ensure(string sourceGameRoot, string ownedGameRoot) =>
        MatchmakingCarrier.EnsureCarrier(sourceGameRoot, ownedGameRoot);
}
