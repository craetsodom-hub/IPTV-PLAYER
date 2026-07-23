namespace IptvPlayer.Contracts.Models;

public sealed record ChannelModel(
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
