namespace IptvPlayer.Contracts.Services;

public sealed record OnDemandHistoryEntry(
    string MediaId,
    string? ParentId,
    string Title,
    string? Subtitle,
    string? PosterUri,
    string PlaybackUri,
    DateTimeOffset UpdatedUtc)
{
    public string? SourceId { get; init; }

    public double? ProgressPercent { get; init; }
}
