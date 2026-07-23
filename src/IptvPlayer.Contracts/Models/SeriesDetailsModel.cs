namespace IptvPlayer.Contracts.Models;

public sealed record SeriesDetailsModel(
    string Id,
    string Title,
    string? PosterUri,
    string? BackdropUri,
    string? Description,
    string? Year,
    string? Rating,
    IReadOnlyList<SeriesSeasonModel> Seasons);
