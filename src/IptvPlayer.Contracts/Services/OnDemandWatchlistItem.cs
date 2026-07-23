namespace IptvPlayer.Contracts.Services;

public sealed record OnDemandWatchlistItem(
    string Id,
    string CategoryId,
    string Title,
    string? PosterUri,
    string? BackdropUri,
    string? Description,
    string? Year,
    string? Duration,
    string? Rating,
    string? PlaybackUri);
