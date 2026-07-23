using IptvPlayer.Contracts.Models;

namespace IptvPlayer.Contracts.Services;

public interface ISourceCatalogService
{
    Task<IReadOnlyList<PlaylistSource>> GetSourcesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CategoryModel>> GetCategoriesAsync(Guid sourceId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChannelModel>> GetChannelsAsync(
        Guid sourceId,
        string categoryId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChannelModel>> GetFavoriteChannelsAsync(
        Guid sourceId,
        IReadOnlyCollection<string> favoriteChannelIds,
        CancellationToken cancellationToken = default);

    Task<ChannelEpgModel> GetChannelEpgAsync(
        Guid sourceId,
        string channelId,
        CancellationToken cancellationToken = default);

    Task WarmOnDemandMetadataCacheAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CategoryModel>> GetMovieCategoriesAsync(Guid sourceId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MovieModel>> GetMoviesAsync(
        Guid sourceId,
        string? categoryId = null,
        CancellationToken cancellationToken = default);

    Task<MovieDetailsModel?> GetMovieDetailsAsync(
        Guid sourceId,
        string movieId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CategoryModel>> GetSeriesCategoriesAsync(Guid sourceId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SeriesModel>> GetSeriesAsync(
        Guid sourceId,
        string? categoryId = null,
        CancellationToken cancellationToken = default);

    Task<SeriesDetailsModel?> GetSeriesDetailsAsync(
        Guid sourceId,
        string seriesId,
        CancellationToken cancellationToken = default);
}
