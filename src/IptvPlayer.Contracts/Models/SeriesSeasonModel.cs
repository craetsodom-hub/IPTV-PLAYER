namespace IptvPlayer.Contracts.Models;

public sealed record SeriesSeasonModel(
    int SeasonNumber,
    string Name,
    IReadOnlyList<SeriesEpisodeModel> Episodes);
