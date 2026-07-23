namespace IptvPlayer.Domain.Entities;

public sealed record Channel(
    string Id,
    string CategoryId,
    string Name,
    Uri StreamUri,
    string? LogoUri,
    string? CurrentProgram,
    string? NextProgram,
    string? CurrentProgramTitle,
    string? CurrentProgramDescription,
    string? CurrentProgramTimeRange,
    string? NextProgramTitle,
    string? NextProgramDescription,
    string? NextProgramTimeRange,
    bool IsFavorite);
