using IptvPlayer.Contracts.Models;
using IptvPlayer.Contracts.Services;

namespace IptvPlayer.Infrastructure.Services;

public sealed class InMemorySourceCatalogService : ISourceCatalogService
{
    private static readonly Guid DemoSourceId = Guid.Parse("2E987408-A459-4EA8-8A23-A4B07186FC62");

    private readonly IReadOnlyList<PlaylistSource> _sources;
    private readonly IReadOnlyDictionary<Guid, IReadOnlyList<CategoryModel>> _categoriesBySource;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ChannelModel>> _channelsByCategory;

    public InMemorySourceCatalogService()
    {
        _sources = new List<PlaylistSource>
        {
            new(
                DemoSourceId,
                "Premium Demo Source",
                SourceKind.M3uUrl,
                "https://example.local/demo.m3u",
                new AccountStatusInfo("Active", DateTimeOffset.UtcNow.AddDays(21), true)),
        };

        var categories = new List<CategoryModel>
        {
            new("news", "News", 1),
            new("sports", "Sports", 2),
            new("movies", "Movies", 3),
            new("kids", "Kids", 4),
        };

        _categoriesBySource = new Dictionary<Guid, IReadOnlyList<CategoryModel>>
        {
            [DemoSourceId] = categories,
        };

        _channelsByCategory = new Dictionary<string, IReadOnlyList<ChannelModel>>(StringComparer.OrdinalIgnoreCase)
        {
            [BuildCategoryKey(DemoSourceId, "news")] =
            [
                BuildChannel("news-1", "news", "Global News One", "https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8", "https://dummyimage.com/64x64/0ea5e9/0f172a.png?text=N1"),
                BuildChannel("news-2", "news", "International Live", "https://test-streams.mux.dev/test_001/stream.m3u8", "https://dummyimage.com/64x64/22d3ee/0f172a.png?text=N2"),
            ],
            [BuildCategoryKey(DemoSourceId, "sports")] =
            [
                BuildChannel("sports-1", "sports", "Arena Sport HD", "https://bitdash-a.akamaihd.net/content/sintel/hls/playlist.m3u8", "https://dummyimage.com/64x64/818cf8/0f172a.png?text=S1"),
                BuildChannel("sports-2", "sports", "Champions Replay", "https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8", "https://dummyimage.com/64x64/a78bfa/0f172a.png?text=S2"),
            ],
            [BuildCategoryKey(DemoSourceId, "movies")] =
            [
                BuildChannel("movies-1", "movies", "Cinema Prime", "https://bitdash-a.akamaihd.net/content/sintel/hls/playlist.m3u8", "https://dummyimage.com/64x64/f59e0b/0f172a.png?text=M1"),
                BuildChannel("movies-2", "movies", "Retro Movies", "https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8", "https://dummyimage.com/64x64/fbbf24/0f172a.png?text=M2"),
            ],
            [BuildCategoryKey(DemoSourceId, "kids")] =
            [
                BuildChannel("kids-1", "kids", "Kids Planet", "https://test-streams.mux.dev/test_001/stream.m3u8", "https://dummyimage.com/64x64/34d399/0f172a.png?text=K1"),
                BuildChannel("kids-2", "kids", "Family Toon", "https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8", "https://dummyimage.com/64x64/4ade80/0f172a.png?text=K2"),
            ],
        };
    }

    public Task<IReadOnlyList<PlaylistSource>> GetSourcesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_sources);

    public Task<IReadOnlyList<CategoryModel>> GetCategoriesAsync(Guid sourceId, CancellationToken cancellationToken = default)
    {
        if (_categoriesBySource.TryGetValue(sourceId, out var categories))
        {
            return Task.FromResult(categories);
        }

        return Task.FromResult<IReadOnlyList<CategoryModel>>(Array.Empty<CategoryModel>());
    }

    public Task<IReadOnlyList<ChannelModel>> GetChannelsAsync(
        Guid sourceId,
        string categoryId,
        CancellationToken cancellationToken = default)
    {
        var key = BuildCategoryKey(sourceId, categoryId);
        if (_channelsByCategory.TryGetValue(key, out var channels))
        {
            return Task.FromResult(channels);
        }

        return Task.FromResult<IReadOnlyList<ChannelModel>>(Array.Empty<ChannelModel>());
    }

    public Task<IReadOnlyList<ChannelModel>> GetFavoriteChannelsAsync(
        Guid sourceId,
        IReadOnlyCollection<string> favoriteChannelIds,
        CancellationToken cancellationToken = default)
    {
        var favoriteIds = favoriteChannelIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var channels = _channelsByCategory
            .Where(item => item.Key.StartsWith($"{sourceId:N}:", StringComparison.OrdinalIgnoreCase))
            .SelectMany(item => item.Value)
            .Where(channel => favoriteIds.Contains(channel.Id))
            .ToArray();

        return Task.FromResult<IReadOnlyList<ChannelModel>>(channels);
    }

    public Task<ChannelEpgModel> GetChannelEpgAsync(
        Guid sourceId,
        string channelId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(ChannelEpgModel.Empty);

    public Task WarmOnDemandMetadataCacheAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<CategoryModel>> GetMovieCategoriesAsync(Guid sourceId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CategoryModel>>(Array.Empty<CategoryModel>());

    public Task<IReadOnlyList<MovieModel>> GetMoviesAsync(
        Guid sourceId,
        string? categoryId = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<MovieModel>>(Array.Empty<MovieModel>());

    public Task<MovieDetailsModel?> GetMovieDetailsAsync(
        Guid sourceId,
        string movieId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<MovieDetailsModel?>(null);

    public Task<IReadOnlyList<CategoryModel>> GetSeriesCategoriesAsync(Guid sourceId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CategoryModel>>(Array.Empty<CategoryModel>());

    public Task<IReadOnlyList<SeriesModel>> GetSeriesAsync(
        Guid sourceId,
        string? categoryId = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<SeriesModel>>(Array.Empty<SeriesModel>());

    public Task<SeriesDetailsModel?> GetSeriesDetailsAsync(
        Guid sourceId,
        string seriesId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<SeriesDetailsModel?>(null);

    private static string BuildCategoryKey(Guid sourceId, string categoryId)
        => $"{sourceId:N}:{categoryId}";

    private static ChannelModel BuildChannel(
        string id,
        string categoryId,
        string name,
        string streamUri,
        string logoUri)
        => new(
            id,
            categoryId,
            name,
            new Uri(streamUri, UriKind.Absolute),
            logoUri,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            false);
}
