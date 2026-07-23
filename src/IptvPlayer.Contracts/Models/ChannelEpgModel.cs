namespace IptvPlayer.Contracts.Models;

public sealed record ChannelEpgModel(
    string? CurrentProgram,
    string? NextProgram,
    string? CurrentProgramTitle,
    string? CurrentProgramDescription,
    string? CurrentProgramTimeRange,
    string? NextProgramTitle,
    string? NextProgramDescription,
    string? NextProgramTimeRange)
{
    public static ChannelEpgModel Empty { get; } = new(null, null, null, null, null, null, null, null);
}
