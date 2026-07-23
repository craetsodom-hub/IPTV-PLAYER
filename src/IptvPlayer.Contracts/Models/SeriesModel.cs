namespace IptvPlayer.Contracts.Models;

public sealed record SeriesModel(
    string Id,
    string CategoryId,
    string Title,
    string? PosterUri,
    string? BackdropUri,
    string? Description,
    string? Year,
    string? Rating);
