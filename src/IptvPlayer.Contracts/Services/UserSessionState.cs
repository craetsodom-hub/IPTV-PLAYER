namespace IptvPlayer.Contracts.Services;

public sealed record UserSessionState(
    Guid? LastSourceId,
    string? LastCategoryId,
    string? LastChannelId,
    IReadOnlyCollection<string> FavoriteChannelIds,
    IReadOnlyCollection<string> RecentChannelIds,
    bool IsMuted)
{
    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> FavoriteChannelIdsBySource { get; init; }
        = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> RecentChannelIdsBySource { get; init; }
        = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, IReadOnlyCollection<RecentChannelHistoryEntry>> RecentChannelHistoryBySource { get; init; }
        = new Dictionary<string, IReadOnlyCollection<RecentChannelHistoryEntry>>(StringComparer.OrdinalIgnoreCase);

    public static UserSessionState Empty { get; } = new(
        null,
        null,
        null,
        Array.Empty<string>(),
        Array.Empty<string>(),
        false);
}

public sealed record RecentChannelHistoryEntry(
    string ChannelId,
    DateTimeOffset? WatchedFromUtc,
    DateTimeOffset? WatchedToUtc,
    double ProgressPercent);
