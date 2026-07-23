using IptvPlayer.Contracts.Models;
using IptvPlayer.Contracts.Services;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.Application.Services;

public sealed class CatalogOrchestrator
{
    private readonly ISourceCatalogService _sourceCatalogService;
    private readonly ILogger<CatalogOrchestrator> _logger;

    public CatalogOrchestrator(
        ISourceCatalogService sourceCatalogService,
        ILogger<CatalogOrchestrator> logger)
    {
        _sourceCatalogService = sourceCatalogService;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PlaylistSource>> GetSourcesAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Loading IPTV sources");
        return await _sourceCatalogService.GetSourcesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CategoryModel>> GetCategoriesAsync(
        Guid sourceId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Loading categories for source {SourceId}", sourceId);
        return await _sourceCatalogService.GetCategoriesAsync(sourceId, cancellationToken);
    }

    public async Task<IReadOnlyList<ChannelModel>> GetChannelsAsync(
        Guid sourceId,
        string categoryId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Loading channels for source {SourceId} and category {CategoryId}",
            sourceId,
            categoryId);

        return await _sourceCatalogService.GetChannelsAsync(sourceId, categoryId, cancellationToken);
    }

    public Task<IReadOnlyList<ChannelModel>> GetFavoriteChannelsAsync(
        Guid sourceId,
        IReadOnlyCollection<string> favoriteChannelIds,
        CancellationToken cancellationToken = default)
        => _sourceCatalogService.GetFavoriteChannelsAsync(sourceId, favoriteChannelIds, cancellationToken);

    public async Task<ChannelEpgModel> GetChannelEpgAsync(
        Guid sourceId,
        string channelId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Loading real EPG for source {SourceId} and channel {ChannelId}", sourceId, channelId);
        return await _sourceCatalogService.GetChannelEpgAsync(sourceId, channelId, cancellationToken);
    }

    public Task WarmOnDemandMetadataCacheAsync(CancellationToken cancellationToken = default)
        => _sourceCatalogService.WarmOnDemandMetadataCacheAsync(cancellationToken);

    public async Task<IReadOnlyList<CategoryModel>> GetMovieCategoriesAsync(
        Guid sourceId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Loading movie categories for source {SourceId}", sourceId);
        return await _sourceCatalogService.GetMovieCategoriesAsync(sourceId, cancellationToken);
    }

    public async Task<IReadOnlyList<MovieModel>> GetMoviesAsync(
        Guid sourceId,
        string? categoryId = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Loading movies for source {SourceId} and category {CategoryId}",
            sourceId,
            categoryId ?? "all");

        return await _sourceCatalogService.GetMoviesAsync(sourceId, categoryId, cancellationToken);
    }

    public async Task<MovieDetailsModel?> GetMovieDetailsAsync(
        Guid sourceId,
        string movieId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Loading movie details for source {SourceId} and movie {MovieId}", sourceId, movieId);
        return await _sourceCatalogService.GetMovieDetailsAsync(sourceId, movieId, cancellationToken);
    }

    public async Task<IReadOnlyList<CategoryModel>> GetSeriesCategoriesAsync(
        Guid sourceId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Loading series categories for source {SourceId}", sourceId);
        return await _sourceCatalogService.GetSeriesCategoriesAsync(sourceId, cancellationToken);
    }

    public async Task<IReadOnlyList<SeriesModel>> GetSeriesAsync(
        Guid sourceId,
        string? categoryId = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Loading series for source {SourceId} and category {CategoryId}",
            sourceId,
            categoryId ?? "all");

        return await _sourceCatalogService.GetSeriesAsync(sourceId, categoryId, cancellationToken);
    }

    public async Task<SeriesDetailsModel?> GetSeriesDetailsAsync(
        Guid sourceId,
        string seriesId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Loading series details for source {SourceId} and series {SeriesId}", sourceId, seriesId);
        return await _sourceCatalogService.GetSeriesDetailsAsync(sourceId, seriesId, cancellationToken);
    }
}
