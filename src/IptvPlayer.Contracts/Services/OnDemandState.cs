namespace IptvPlayer.Contracts.Services;

public sealed record OnDemandState(
    IReadOnlyCollection<string> WatchlistMovieIds,
    IReadOnlyCollection<string> WatchlistSeriesIds,
    IReadOnlyCollection<OnDemandHistoryEntry> ContinueWatchingMovies,
    IReadOnlyCollection<OnDemandHistoryEntry> ContinueWatchingSeries)
{
    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> WatchlistMovieIdsBySource { get; init; }
        = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> WatchlistSeriesIdsBySource { get; init; }
        = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, IReadOnlyCollection<OnDemandWatchlistItem>> WatchlistMoviesBySource { get; init; }
        = new Dictionary<string, IReadOnlyCollection<OnDemandWatchlistItem>>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, IReadOnlyCollection<OnDemandWatchlistItem>> WatchlistSeriesBySource { get; init; }
        = new Dictionary<string, IReadOnlyCollection<OnDemandWatchlistItem>>(StringComparer.OrdinalIgnoreCase);

    public static OnDemandState Empty { get; } = new(
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<OnDemandHistoryEntry>(),
        Array.Empty<OnDemandHistoryEntry>());
}
