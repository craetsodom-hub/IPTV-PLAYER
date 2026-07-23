namespace IptvPlayer.Contracts.Models;

public sealed record MovieModel(
    string Id,
    string CategoryId,
    string Title,
    string? PosterUri,
    string? BackdropUri,
    string? Description,
    string? Year,
    string? Duration,
    string? Rating,
    Uri PlaybackUri);
