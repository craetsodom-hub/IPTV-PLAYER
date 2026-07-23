namespace IptvPlayer.Contracts.Models;

public sealed record MovieDetailsModel(
    string Id,
    string Title,
    string? PosterUri,
    string? BackdropUri,
    string? Description,
    string? Year,
    string? Duration,
    string? Rating,
    Uri PlaybackUri);
