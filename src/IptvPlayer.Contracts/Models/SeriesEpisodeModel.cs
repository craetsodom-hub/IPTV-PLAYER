namespace IptvPlayer.Contracts.Models;

public sealed record SeriesEpisodeModel(
    string Id,
    int EpisodeNumber,
    string Title,
    string? PosterUri,
    string? Description,
    string? Duration,
    string? Rating,
    Uri PlaybackUri);
