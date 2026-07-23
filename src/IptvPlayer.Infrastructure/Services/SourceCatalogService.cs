using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IptvPlayer.Contracts.Import;
using IptvPlayer.Contracts.Models;
using IptvPlayer.Contracts.Services;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.Infrastructure.Services;

public sealed class SourceCatalogService : ISourceCatalogService, ISourceImportService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly Regex M3uAttributeRegex = new("(?<key>[\\w-]+)=\"(?<value>[^\"]*)\"", RegexOptions.Compiled);
    private static readonly Regex SlugRegex = new("[^a-z0-9]+", RegexOptions.Compiled);
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan OnDemandCategoryRequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OnDemandDetailsRequestTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan OnDemandListRequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MetadataCacheTtl = TimeSpan.FromHours(12);
    private static readonly TimeSpan MetadataCacheFastWait = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan MetadataCacheSaveDelay = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan EpgCatalogSaveDelay = TimeSpan.FromSeconds(4);
    private const long MaxMetadataCacheFileBytes = 128L * 1024L * 1024L;
    private const int MaxCachedListEntries = 80;
    private const int MaxCachedDetailEntries = 240;
    private const int MaxCachedMediaListItems = 25000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _metadataCacheSaveGate = new(1, 1);
    private readonly object _storeLoadLock = new();
    private readonly object _metadataCacheLoadLock = new();
    private readonly HttpClient _httpClient;
    private readonly ILogger<SourceCatalogService> _logger;
    private readonly string _catalogFilePath;
    private readonly string _metadataCacheFilePath;
    private Task? _storeLoadTask;
    private Task? _metadataCacheLoadTask;
    private int _metadataCacheSaveDirty;
    private int _metadataCacheSaveRunning;
    private int _catalogSaveDirty;
    private int _catalogSaveRunning;

    private CatalogStore _store = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, MetadataCacheEntry<IReadOnlyList<CategoryModel>>> _categoryMetadataCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, MetadataCacheEntry<IReadOnlyList<MovieModel>>> _movieMetadataCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, MetadataCacheEntry<MovieDetailsModel?>> _movieDetailsMetadataCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, MetadataCacheEntry<IReadOnlyList<SeriesModel>>> _seriesMetadataCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, MetadataCacheEntry<SeriesDetailsModel?>> _seriesDetailsMetadataCache = new(StringComparer.OrdinalIgnoreCase);

    public SourceCatalogService(ILogger<SourceCatalogService> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IptvPlayer",
            "catalog");

        _catalogFilePath = Path.Combine(root, "sources.json");
        _metadataCacheFilePath = Path.Combine(root, "metadata-cache.json");
        _storeLoadTask = Task.Run(LoadFromDiskOrSeed);
    }

    public async Task<IReadOnlyList<PlaylistSource>> GetSourcesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureStoreLoadedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _store.Sources
                .Select(MapSource)
                .OrderBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<CategoryModel>> GetCategoriesAsync(Guid sourceId, CancellationToken cancellationToken = default)
    {
        await EnsureStoreLoadedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var source = _store.Sources.FirstOrDefault(entry => entry.Id == sourceId);
            if (source is null)
            {
                return Array.Empty<CategoryModel>();
            }

            return source.Categories
                .OrderBy(category => category.SortOrder)
                .Select(category => new CategoryModel(category.Id, category.Name, category.SortOrder))
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ChannelModel>> GetChannelsAsync(
        Guid sourceId,
        string categoryId,
        CancellationToken cancellationToken = default)
    {
        await EnsureStoreLoadedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var source = _store.Sources.FirstOrDefault(entry => entry.Id == sourceId);
            if (source is null)
            {
                return Array.Empty<ChannelModel>();
            }

            var category = source.Categories.FirstOrDefault(entry =>
                string.Equals(entry.Id, categoryId, StringComparison.OrdinalIgnoreCase));

            if (category is null)
            {
                return Array.Empty<ChannelModel>();
            }

            var channels = new List<ChannelModel>(category.Channels.Count);
            foreach (var channel in category.Channels)
            {
                if (!Uri.TryCreate(channel.StreamUri, UriKind.Absolute, out var streamUri))
                {
                    continue;
                }

                var hasRealEpg = channel.EpgUpdatedUtc.HasValue;
                channels.Add(new ChannelModel(
                    channel.Id,
                    category.Id,
                    channel.Name,
                    streamUri,
                    channel.LogoUri,
                    hasRealEpg ? channel.CurrentProgram : null,
                    hasRealEpg ? channel.NextProgram : null,
                    hasRealEpg ? channel.CurrentProgramTitle ?? channel.CurrentProgram : null,
                    hasRealEpg ? channel.CurrentProgramDescription : null,
                    hasRealEpg ? channel.CurrentProgramTimeRange : null,
                    hasRealEpg ? channel.NextProgramTitle ?? channel.NextProgram : null,
                    hasRealEpg ? channel.NextProgramDescription : null,
                    hasRealEpg ? channel.NextProgramTimeRange : null,
                    channel.IsFavorite));
            }

            return channels;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ChannelModel>> GetFavoriteChannelsAsync(
        Guid sourceId,
        IReadOnlyCollection<string> favoriteChannelIds,
        CancellationToken cancellationToken = default)
    {
        if (favoriteChannelIds.Count == 0)
        {
            return Array.Empty<ChannelModel>();
        }

        await EnsureStoreLoadedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var source = _store.Sources.FirstOrDefault(entry => entry.Id == sourceId);
            if (source is null)
            {
                return Array.Empty<ChannelModel>();
            }

            var favoriteIds = favoriteChannelIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var channels = new List<ChannelModel>(favoriteIds.Count);
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var category in source.Categories)
            {
                foreach (var channel in category.Channels)
                {
                    if (!favoriteIds.Contains(channel.Id)
                        || !seenIds.Add(channel.Id)
                        || !Uri.TryCreate(channel.StreamUri, UriKind.Absolute, out var streamUri))
                    {
                        continue;
                    }

                    var hasRealEpg = channel.EpgUpdatedUtc.HasValue;
                    channels.Add(new ChannelModel(
                        channel.Id,
                        category.Id,
                        channel.Name,
                        streamUri,
                        channel.LogoUri,
                        hasRealEpg ? channel.CurrentProgram : null,
                        hasRealEpg ? channel.NextProgram : null,
                        hasRealEpg ? channel.CurrentProgramTitle ?? channel.CurrentProgram : null,
                        hasRealEpg ? channel.CurrentProgramDescription : null,
                        hasRealEpg ? channel.CurrentProgramTimeRange : null,
                        hasRealEpg ? channel.NextProgramTitle ?? channel.NextProgram : null,
                        hasRealEpg ? channel.NextProgramDescription : null,
                        hasRealEpg ? channel.NextProgramTimeRange : null,
                        true));
                }
            }

            return channels
                .OrderBy(channel => channel.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ChannelEpgModel> GetChannelEpgAsync(
        Guid sourceId,
        string channelId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            return ChannelEpgModel.Empty;
        }

        ChannelEpgLookup? lookup;
        await EnsureStoreLoadedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var source = _store.Sources.FirstOrDefault(entry => entry.Id == sourceId);
            var channel = source?.Categories
                .SelectMany(category => category.Channels)
                .FirstOrDefault(entry => string.Equals(entry.Id, channelId, StringComparison.OrdinalIgnoreCase));

            if (source is null || channel is null)
            {
                return ChannelEpgModel.Empty;
            }

            var hasRealEpg = channel.EpgUpdatedUtc.HasValue;
            if (HasStructuredEpg(channel)
                && channel.EpgUpdatedUtc is { } epgUpdatedUtc
                && epgUpdatedUtc > DateTimeOffset.UtcNow.AddMinutes(-10))
            {
                return MapStoredEpg(channel);
            }

            lookup = new ChannelEpgLookup(
                source.Id,
                source.Kind,
                source.Endpoint,
                channel.Id,
                channel.StreamUri,
                hasRealEpg,
                channel.CurrentProgram,
                channel.NextProgram,
                channel.CurrentProgramTitle,
                channel.CurrentProgramDescription,
                channel.CurrentProgramTimeRange,
                channel.NextProgramTitle,
                channel.NextProgramDescription,
                channel.NextProgramTimeRange);
        }
        finally
        {
            _gate.Release();
        }

        if (lookup.Kind != SourceKind.XtreamCodes)
        {
            return GetCachedEpgOrEmpty(lookup);
        }

        if (!TryBuildXtreamShortEpgUrl(lookup, out var epgUrl))
        {
            return GetCachedEpgOrEmpty(lookup);
        }

        try
        {
            var epg = await FetchXtreamShortEpgAsync(epgUrl, cancellationToken);

            try
            {
                await UpdateStoredChannelEpgAsync(sourceId, channelId, epg, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Real EPG was loaded but could not be cached for channel {ChannelId}", channelId);
            }

            return epg;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load real EPG for channel {ChannelId}", channelId);
            return GetCachedEpgOrEmpty(lookup);
        }
    }

    public Task WarmOnDemandMetadataCacheAsync(CancellationToken cancellationToken = default)
        => EnsureMetadataCacheLoadedAsync(cancellationToken);

    public async Task<IReadOnlyList<CategoryModel>> GetMovieCategoriesAsync(Guid sourceId, CancellationToken cancellationToken = default)
    {
        await TryEnsureMetadataCacheAvailableAsync(cancellationToken).ConfigureAwait(false);
        var cacheKey = BuildMetadataCacheKey(sourceId, "movie-categories", null);
        if (TryReadMetadataCache(_categoryMetadataCache, cacheKey, out var cachedCategories))
        {
            return cachedCategories;
        }

        var context = await GetXtreamCatalogContextAsync(sourceId, cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            return Array.Empty<CategoryModel>();
        }

        var categoriesUrl = BuildXtreamUrl(context.ServerUrl, context.Username, context.Password, "get_vod_categories");
        using var categoriesDocument = await GetJsonDocumentWithRetryAsync(
            categoriesUrl,
            cancellationToken,
            OnDemandCategoryRequestTimeout).ConfigureAwait(false);

        var categories = ParseXtreamCategories(categoriesDocument.RootElement)
            .Values
            .OrderBy(category => category.SortOrder)
            .Select(MapCategory)
            .ToArray();

        WriteMetadataCache(_categoryMetadataCache, cacheKey, categories);
        return categories;
    }

    public async Task<IReadOnlyList<MovieModel>> GetMoviesAsync(
        Guid sourceId,
        string? categoryId = null,
        CancellationToken cancellationToken = default)
    {
        await TryEnsureMetadataCacheAvailableAsync(cancellationToken).ConfigureAwait(false);
        var cacheKey = BuildMetadataCacheKey(sourceId, "movies", categoryId);
        if (TryReadMetadataCache(_movieMetadataCache, cacheKey, out var cachedMovies))
        {
            return cachedMovies;
        }

        var context = await GetXtreamCatalogContextAsync(sourceId, cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            return Array.Empty<MovieModel>();
        }

        var streamsUrl = BuildXtreamUrl(
            context.ServerUrl,
            context.Username,
            context.Password,
            "get_vod_streams",
            BuildCategoryParameters(categoryId));

        using var streamsDocument = await GetJsonDocumentWithRetryAsync(
            streamsUrl,
            cancellationToken,
            OnDemandListRequestTimeout).ConfigureAwait(false);
        var movies = ParseXtreamMovies(streamsDocument.RootElement, context);
        if (movies.Count <= MaxCachedMediaListItems)
        {
            WriteMetadataCache(_movieMetadataCache, cacheKey, movies);
        }

        return movies;
    }

    public async Task<MovieDetailsModel?> GetMovieDetailsAsync(
        Guid sourceId,
        string movieId,
        CancellationToken cancellationToken = default)
    {
        await TryEnsureMetadataCacheAvailableAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(movieId))
        {
            return null;
        }

        var cacheKey = BuildMetadataCacheKey(sourceId, "movie-details", movieId);
        if (TryReadMetadataCache(_movieDetailsMetadataCache, cacheKey, out var cachedDetails))
        {
            return cachedDetails;
        }

        var context = await GetXtreamCatalogContextAsync(sourceId, cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            return null;
        }

        var rawMovieId = RemoveMediaPrefix(movieId, "vod-");
        var detailsUrl = BuildXtreamUrl(
            context.ServerUrl,
            context.Username,
            context.Password,
            "get_vod_info",
            new Dictionary<string, string> { ["vod_id"] = rawMovieId });

        using var detailsDocument = await GetJsonDocumentWithRetryAsync(
            detailsUrl,
            cancellationToken,
            OnDemandDetailsRequestTimeout).ConfigureAwait(false);
        var details = ParseXtreamMovieDetails(detailsDocument.RootElement, context, rawMovieId);
        WriteMetadataCache(_movieDetailsMetadataCache, cacheKey, details);
        return details;
    }

    public async Task<IReadOnlyList<CategoryModel>> GetSeriesCategoriesAsync(Guid sourceId, CancellationToken cancellationToken = default)
    {
        await TryEnsureMetadataCacheAvailableAsync(cancellationToken).ConfigureAwait(false);
        var cacheKey = BuildMetadataCacheKey(sourceId, "series-categories", null);
        if (TryReadMetadataCache(_categoryMetadataCache, cacheKey, out var cachedCategories))
        {
            return cachedCategories;
        }

        var context = await GetXtreamCatalogContextAsync(sourceId, cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            return Array.Empty<CategoryModel>();
        }

        var categoriesUrl = BuildXtreamUrl(context.ServerUrl, context.Username, context.Password, "get_series_categories");
        using var categoriesDocument = await GetJsonDocumentWithRetryAsync(
            categoriesUrl,
            cancellationToken,
            OnDemandCategoryRequestTimeout).ConfigureAwait(false);

        var categories = ParseXtreamCategories(categoriesDocument.RootElement)
            .Values
            .OrderBy(category => category.SortOrder)
            .Select(MapCategory)
            .ToArray();

        WriteMetadataCache(_categoryMetadataCache, cacheKey, categories);
        return categories;
    }

    public async Task<IReadOnlyList<SeriesModel>> GetSeriesAsync(
        Guid sourceId,
        string? categoryId = null,
        CancellationToken cancellationToken = default)
    {
        await TryEnsureMetadataCacheAvailableAsync(cancellationToken).ConfigureAwait(false);
        var cacheKey = BuildMetadataCacheKey(sourceId, "series", categoryId);
        if (TryReadMetadataCache(_seriesMetadataCache, cacheKey, out var cachedSeries))
        {
            return cachedSeries;
        }

        var context = await GetXtreamCatalogContextAsync(sourceId, cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            return Array.Empty<SeriesModel>();
        }

        var seriesUrl = BuildXtreamUrl(
            context.ServerUrl,
            context.Username,
            context.Password,
            "get_series",
            BuildCategoryParameters(categoryId));

        using var seriesDocument = await GetJsonDocumentWithRetryAsync(
            seriesUrl,
            cancellationToken,
            OnDemandListRequestTimeout).ConfigureAwait(false);
        var series = ParseXtreamSeries(seriesDocument.RootElement);
        if (series.Count <= MaxCachedMediaListItems)
        {
            WriteMetadataCache(_seriesMetadataCache, cacheKey, series);
        }

        return series;
    }

    public async Task<SeriesDetailsModel?> GetSeriesDetailsAsync(
        Guid sourceId,
        string seriesId,
        CancellationToken cancellationToken = default)
    {
        await TryEnsureMetadataCacheAvailableAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(seriesId))
        {
            return null;
        }

        var cacheKey = BuildMetadataCacheKey(sourceId, "series-details", seriesId);
        if (TryReadMetadataCache(_seriesDetailsMetadataCache, cacheKey, out var cachedDetails))
        {
            return cachedDetails;
        }

        var context = await GetXtreamCatalogContextAsync(sourceId, cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            return null;
        }

        var rawSeriesId = RemoveMediaPrefix(seriesId, "series-");
        var detailsUrl = BuildXtreamUrl(
            context.ServerUrl,
            context.Username,
            context.Password,
            "get_series_info",
            new Dictionary<string, string> { ["series_id"] = rawSeriesId });

        using var detailsDocument = await GetJsonDocumentWithRetryAsync(
            detailsUrl,
            cancellationToken,
            OnDemandDetailsRequestTimeout).ConfigureAwait(false);
        var details = ParseXtreamSeriesDetails(detailsDocument.RootElement, context, rawSeriesId);
        WriteMetadataCache(_seriesDetailsMetadataCache, cacheKey, details);
        return details;
    }

    public async Task<SourceImportResult> ImportAsync(SourceImportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureStoreLoadedAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var importedSource = request.Mode switch
            {
                SourceImportMode.XtreamCodes => await ImportXtreamAsync(request, cancellationToken),
                SourceImportMode.M3uUrl => await ImportM3uUrlAsync(request, cancellationToken),
                SourceImportMode.M3uFile => await ImportM3uFileAsync(request, cancellationToken),
                SourceImportMode.M3u8Link => await ImportM3u8Async(request, cancellationToken),
                _ => null,
            };

            if (importedSource is null)
            {
                return SourceImportResult.Failed("Invalid import mode.");
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _store.Sources.RemoveAll(existing => existing.Id == importedSource.Source.Id);
                _store.Sources.RemoveAll(existing =>
                    existing.Kind == importedSource.Source.Kind
                    && string.Equals(GetSourceIdentity(existing), GetSourceIdentity(importedSource.Source), StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(existing.Id.ToString("N"), importedSource.Source.Id.ToString("N"), StringComparison.OrdinalIgnoreCase));

                _store.Sources.Add(importedSource.Source);
                ClearMetadataCache();
                await SaveUnsafeAsync(cancellationToken);
            }
            finally
            {
                _gate.Release();
            }

            return SourceImportResult.Succeeded(MapSource(importedSource.Source), importedSource.SuccessMessage);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Source import failed");
            return SourceImportResult.Failed(exception.Message);
        }
    }

    public async Task<bool> DeleteSourceAsync(Guid sourceId, CancellationToken cancellationToken = default)
    {
        await EnsureStoreLoadedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var removed = _store.Sources.RemoveAll(source => source.Id == sourceId) > 0;
            if (!removed)
            {
                return false;
            }

            await SaveUnsafeAsync(cancellationToken);
            ClearMetadataCache();
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to delete source {SourceId}", sourceId);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SourceImportResult> UpdateSourceAsync(SourceUpdateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureStoreLoadedAsync(cancellationToken).ConfigureAwait(false);

        StoredSource? existing;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            existing = _store.Sources.FirstOrDefault(source => source.Id == request.SourceId);
            if (existing is null)
            {
                return SourceImportResult.Failed("Playlist was not found.");
            }
        }
        finally
        {
            _gate.Release();
        }

        var displayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? existing.Name
            : request.DisplayName.Trim();

        var requestedInput = request.PrimaryInput?.Trim();
        var endpointChanged = !string.IsNullOrWhiteSpace(requestedInput)
            && !string.Equals(requestedInput, existing.Endpoint, StringComparison.OrdinalIgnoreCase);

        var usernameChanged = !string.IsNullOrWhiteSpace(request.Username)
            && !string.Equals(request.Username.Trim(), existing.XtreamUsername, StringComparison.Ordinal);

        var passwordChanged = !string.IsNullOrWhiteSpace(request.Password)
            && !string.Equals(request.Password, existing.XtreamPassword, StringComparison.Ordinal);

        if (!endpointChanged && !usernameChanged && !passwordChanged)
        {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var source = _store.Sources.FirstOrDefault(source => source.Id == request.SourceId);
                if (source is null)
                {
                    return SourceImportResult.Failed("Playlist was not found.");
                }

                source.Name = displayName;
                await SaveUnsafeAsync(cancellationToken);
                return SourceImportResult.Succeeded(MapSource(source), "Playlist saved.");
            }
            finally
            {
                _gate.Release();
            }
        }

        try
        {
            var importRequest = BuildUpdateImportRequest(existing, request, displayName);
            var importedSource = importRequest.Mode switch
            {
                SourceImportMode.XtreamCodes => await ImportXtreamAsync(importRequest, cancellationToken, "Playlist saved."),
                SourceImportMode.M3uUrl => await ImportM3uUrlAsync(importRequest, cancellationToken),
                SourceImportMode.M3uFile => await ImportM3uFileAsync(importRequest, cancellationToken),
                SourceImportMode.M3u8Link => await ImportM3u8Async(importRequest, cancellationToken),
                _ => null,
            };

            if (importedSource is null)
            {
                return SourceImportResult.Failed("Playlist could not be saved.");
            }

            importedSource.Source.Id = existing.Id;

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _store.Sources.RemoveAll(source => source.Id == existing.Id);
                _store.Sources.RemoveAll(source =>
                    source.Kind == importedSource.Source.Kind
                    && string.Equals(GetSourceIdentity(source), GetSourceIdentity(importedSource.Source), StringComparison.OrdinalIgnoreCase)
                    && source.Id != importedSource.Source.Id);

                _store.Sources.Add(importedSource.Source);
                ClearMetadataCache();
                await SaveUnsafeAsync(cancellationToken);
            }
            finally
            {
                _gate.Release();
            }

            return SourceImportResult.Succeeded(MapSource(importedSource.Source), "Playlist saved.");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Source update failed for {SourceId}", request.SourceId);
            return SourceImportResult.Failed(exception.Message);
        }
    }

    private async Task<ImportExecution?> ImportXtreamAsync(
        SourceImportRequest request,
        CancellationToken cancellationToken,
        string successMessage = "Playlist added successfully.")
    {
        var serverUrl = request.PrimaryInput?.Trim() ?? string.Empty;
        var username = request.Username?.Trim() ?? string.Empty;
        var password = request.Password?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Please enter the server URL, username, and password.");
        }

        var normalizedServer = NormalizeServerUrl(serverUrl);

        var authUrl = BuildXtreamUrl(normalizedServer, username, password, null);
        using var authDocument = await GetJsonDocumentWithRetryAsync(authUrl, cancellationToken).ConfigureAwait(false);

        var root = authDocument.RootElement;
        if (!root.TryGetProperty("user_info", out var userInfo))
        {
            throw new InvalidOperationException("The playlist provider did not return account information.");
        }

        var authValue = ReadStringOrNull(userInfo, "auth") ?? "0";
        if (!string.Equals(authValue, "1", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(authValue, "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Playlist login failed. Verify the server URL and credentials.");
        }

        var accountState = ReadStringOrNull(userInfo, "status") ?? "Active";
        var expirationRaw = ReadStringOrNull(userInfo, "exp_date");
        var expiresAtUtc = ParseUnixDate(expirationRaw);

        var categoriesUrl = BuildXtreamUrl(normalizedServer, username, password, "get_live_categories");
        var streamsUrl = BuildXtreamUrl(normalizedServer, username, password, "get_live_streams");
        var categoriesTask = GetJsonDocumentWithRetryAsync(categoriesUrl, cancellationToken);
        var streamsTask = GetJsonDocumentWithRetryAsync(streamsUrl, cancellationToken);

        using var categoriesDocument = await categoriesTask.ConfigureAwait(false);
        using var streamsDocument = await streamsTask.ConfigureAwait(false);

        var categories = ParseXtreamCategories(categoriesDocument.RootElement);
        ParseXtreamStreams(streamsDocument.RootElement, categories, normalizedServer, username, password);

        if (categories.Count == 0)
        {
            throw new InvalidOperationException("Playlist added, but no live categories were returned.");
        }

        var source = new StoredSource
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(request.DisplayName) ? $"Xtream {username}" : request.DisplayName,
            Kind = SourceKind.XtreamCodes,
            Endpoint = normalizedServer,
            ImportIdentity = BuildXtreamIdentity(normalizedServer, username),
            XtreamUsername = username,
            XtreamPassword = password,
            StatusInfo = new AccountStatusInfo(accountState, expiresAtUtc, expiresAtUtc.HasValue),
            Categories = categories.Values
                .OrderBy(category => category.SortOrder)
                .ToList(),
        };

        return new ImportExecution(source, successMessage);
    }

    private async Task<ImportExecution?> ImportM3uUrlAsync(SourceImportRequest request, CancellationToken cancellationToken)
    {
        var url = request.PrimaryInput?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var playlistUri))
        {
            throw new InvalidOperationException("M3U URL is invalid.");
        }

        if (TryCreateXtreamImportRequestsFromM3uUrl(playlistUri, request.DisplayName, out var xtreamRequests))
        {
            _logger.LogInformation(
                "Detected Xtream-compatible M3U URL for host {Host}. Attempting Xtream login first.",
                playlistUri.Host);

            foreach (var xtreamRequest in xtreamRequests)
            {
                try
                {
                    return await ImportXtreamAsync(
                        xtreamRequest,
                        cancellationToken,
                        "Playlist added successfully as Xtream.").ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _logger.LogInformation(
                        exception,
                        "Xtream auto-detect failed for {Url} using server {ServerUrl}.",
                        RedactSensitiveUrl(url),
                        xtreamRequest.PrimaryInput);
                }
            }

            _logger.LogInformation(
                "All Xtream auto-detect attempts failed for {Url}. Falling back to direct M3U parsing.",
                RedactSensitiveUrl(url));
        }

        var content = await GetStringWithRetryAsync(url, cancellationToken).ConfigureAwait(false);
        var parsedCategories = ParseM3u(content);

        if (parsedCategories.Count == 0)
        {
            throw new InvalidOperationException("No playable channels found in the M3U URL.");
        }

        var source = new StoredSource
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(request.DisplayName) ? $"M3U {playlistUri.Host}" : request.DisplayName,
            Kind = SourceKind.M3uUrl,
            Endpoint = url,
            ImportIdentity = url,
            StatusInfo = new AccountStatusInfo("Available", null, false),
            Categories = parsedCategories,
        };

        var message = TryCreateXtreamImportRequestsFromM3uUrl(playlistUri, request.DisplayName, out _)
            ? "Playlist added as basic M3U. Xtream login was detected but could not connect."
            : "Playlist added successfully.";

        return new ImportExecution(source, message);
    }

    private async Task<ImportExecution?> ImportM3uFileAsync(SourceImportRequest request, CancellationToken cancellationToken)
    {
        var path = request.PrimaryInput?.Trim() ?? string.Empty;
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("M3U file path does not exist.");
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var parsedCategories = ParseM3u(content);

        if (parsedCategories.Count == 0)
        {
            throw new InvalidOperationException("No playable channels found in the M3U file.");
        }

        var source = new StoredSource
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(request.DisplayName)
                ? Path.GetFileNameWithoutExtension(path)
                : request.DisplayName,
            Kind = SourceKind.M3uFile,
            Endpoint = path,
            ImportIdentity = path,
            StatusInfo = new AccountStatusInfo("Available", null, false),
            Categories = parsedCategories,
        };

        return new ImportExecution(source, "Playlist added successfully.");
    }

    private Task<ImportExecution?> ImportM3u8Async(SourceImportRequest request, CancellationToken cancellationToken)
    {
        var url = request.PrimaryInput?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var streamUri))
        {
            throw new InvalidOperationException("M3U8 stream URL is invalid.");
        }

        var category = new StoredCategory
        {
            Id = "direct",
            Name = "Direct Streams",
            SortOrder = 1,
            Channels =
            [
                new StoredChannel
                {
                    Id = $"direct-{Guid.NewGuid():N}",
                    CategoryId = "direct",
                    Name = string.IsNullOrWhiteSpace(request.DisplayName) ? streamUri.Host : request.DisplayName,
                    StreamUri = streamUri.ToString(),
                    LogoUri = null,
                    CurrentProgram = null,
                    NextProgram = null,
                    IsFavorite = false,
                },
            ],
        };

        var source = new StoredSource
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(request.DisplayName) ? $"M3U8 {streamUri.Host}" : request.DisplayName,
            Kind = SourceKind.M3u8Link,
            Endpoint = streamUri.ToString(),
            ImportIdentity = streamUri.ToString(),
            StatusInfo = new AccountStatusInfo("Available", null, false),
            Categories = [category],
        };

        return Task.FromResult<ImportExecution?>(new ImportExecution(source, "Playlist added successfully."));
    }

    private static SourceImportRequest BuildUpdateImportRequest(
        StoredSource existing,
        SourceUpdateRequest update,
        string displayName)
    {
        var primaryInput = string.IsNullOrWhiteSpace(update.PrimaryInput)
            ? existing.Endpoint
            : update.PrimaryInput.Trim();

        if (existing.Kind != SourceKind.XtreamCodes)
        {
            var mode = existing.Kind switch
            {
                SourceKind.M3uUrl => SourceImportMode.M3uUrl,
                SourceKind.M3uFile => SourceImportMode.M3uFile,
                SourceKind.M3u8Link => SourceImportMode.M3u8Link,
                _ => SourceImportMode.None,
            };

            return new SourceImportRequest(mode, primaryInput, DisplayName: displayName);
        }

        var username = string.IsNullOrWhiteSpace(update.Username)
            ? existing.XtreamUsername
            : update.Username.Trim();
        var password = string.IsNullOrWhiteSpace(update.Password)
            ? existing.XtreamPassword
            : update.Password;

        return new SourceImportRequest(
            SourceImportMode.XtreamCodes,
            primaryInput,
            username,
            password,
            displayName);
    }

    private static bool TryCreateXtreamImportRequestsFromM3uUrl(
        Uri playlistUri,
        string? displayName,
        out IReadOnlyList<SourceImportRequest> requests)
    {
        requests = Array.Empty<SourceImportRequest>();

        if (!TryExtractXtreamLoginFromUri(playlistUri, out var username, out var password))
        {
            return false;
        }

        requests = BuildXtreamServerCandidates(playlistUri)
            .Select(serverUrl => new SourceImportRequest(SourceImportMode.XtreamCodes, serverUrl, username, password, displayName))
            .ToArray();

        return requests.Count > 0;
    }

    private static bool TryExtractXtreamLoginFromUri(Uri uri, out string username, out string password)
    {
        username = string.Empty;
        password = string.Empty;

        var queryParameters = ParseQueryParameters(uri.Query);
        username = ReadFirstQueryValue(queryParameters, "username", "user", "u");
        password = ReadFirstQueryValue(queryParameters, "password", "pass", "p");

        if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
        {
            return true;
        }

        return TryExtractXtreamCredentials(uri.ToString(), out username, out password, out _);
    }

    private static string ReadFirstQueryValue(
        IReadOnlyDictionary<string, string> queryParameters,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (queryParameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static IReadOnlyList<string> BuildXtreamServerCandidates(Uri uri)
    {
        var candidates = new List<string>();

        AddXtreamServerCandidate(candidates, uri, string.Empty);

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var length = 1; length < segments.Length; length++)
        {
            var first = segments[0];
            if (first.EndsWith(".php", StringComparison.OrdinalIgnoreCase)
                || string.Equals(first, "get", StringComparison.OrdinalIgnoreCase)
                || string.Equals(first, "live", StringComparison.OrdinalIgnoreCase)
                || string.Equals(first, "movie", StringComparison.OrdinalIgnoreCase)
                || string.Equals(first, "series", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            AddXtreamServerCandidate(candidates, uri, "/" + string.Join('/', segments.Take(length)));
        }

        return candidates;
    }

    private static void AddXtreamServerCandidate(List<string> candidates, Uri uri, string path)
    {
        var builder = new UriBuilder(uri)
        {
            Scheme = Uri.UriSchemeHttp,
            Path = path,
            Query = string.Empty,
            Fragment = string.Empty,
        };

        if (builder.Port == 443)
        {
            builder.Port = 80;
        }

        var candidate = builder.Uri.ToString().TrimEnd('/');
        if (!candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(candidate);
        }
    }

    private static bool TryCreateXtreamImportRequestFromM3uUrl(
        Uri playlistUri,
        string? displayName,
        out SourceImportRequest request)
    {
        request = default!;

        if (!TryCreateXtreamImportRequestsFromM3uUrl(playlistUri, displayName, out var requests) || requests.Count == 0)
        {
            return false;
        }

        request = requests[0];
        return true;
    }

    private static Dictionary<string, string> ParseQueryParameters(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
        {
            return values;
        }

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = pair.IndexOf('=');
            var rawKey = separatorIndex >= 0 ? pair[..separatorIndex] : pair;
            if (string.IsNullOrWhiteSpace(rawKey))
            {
                continue;
            }

            var rawValue = separatorIndex >= 0 ? pair[(separatorIndex + 1)..] : string.Empty;
            var key = Uri.UnescapeDataString(rawKey.Replace('+', ' '));
            var value = Uri.UnescapeDataString(rawValue.Replace('+', ' '));
            values[key] = value;
        }

        return values;
    }

    private static string BuildXtreamIdentity(string serverUrl, string username)
        => $"{serverUrl}|{username.Trim().ToLowerInvariant()}";

    private static string GetSourceIdentity(StoredSource source)
        => string.IsNullOrWhiteSpace(source.ImportIdentity)
            ? source.Endpoint
            : source.ImportIdentity;

    private static string BuildMetadataCacheKey(Guid sourceId, string scope, string? categoryOrItemId)
        => $"{sourceId:N}:{scope}:{categoryOrItemId ?? "all"}";

    private static bool TryReadMetadataCache<T>(
        System.Collections.Concurrent.ConcurrentDictionary<string, MetadataCacheEntry<T>> cache,
        string key,
        out T value)
    {
        value = default!;

        if (!cache.TryGetValue(key, out var entry))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - entry.CachedUtc > MetadataCacheTtl)
        {
            cache.TryRemove(key, out _);
            return false;
        }

        value = entry.Value;
        return true;
    }

    private void WriteMetadataCache<T>(
        System.Collections.Concurrent.ConcurrentDictionary<string, MetadataCacheEntry<T>> cache,
        string key,
        T value)
    {
        cache[key] = new MetadataCacheEntry<T>(value, DateTimeOffset.UtcNow);
        QueueMetadataCacheSave();
    }

    private void ClearMetadataCache()
    {
        _categoryMetadataCache.Clear();
        _movieMetadataCache.Clear();
        _movieDetailsMetadataCache.Clear();
        _seriesMetadataCache.Clear();
        _seriesDetailsMetadataCache.Clear();
        QueueMetadataCacheSave();
    }

    private async Task EnsureMetadataCacheLoadedAsync(CancellationToken cancellationToken)
    {
        var loadTask = StartMetadataCacheLoad();
        await loadTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureStoreLoadedAsync(CancellationToken cancellationToken)
    {
        Task loadTask;
        lock (_storeLoadLock)
        {
            _storeLoadTask ??= Task.Run(LoadFromDiskOrSeed);
            loadTask = _storeLoadTask;
        }

        await loadTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task TryEnsureMetadataCacheAvailableAsync(CancellationToken cancellationToken)
    {
        Task? loadTask;
        lock (_metadataCacheLoadLock)
        {
            loadTask = _metadataCacheLoadTask;
        }

        if (loadTask is null)
        {
            return;
        }

        if (loadTask.IsCompleted)
        {
            await loadTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await loadTask.WaitAsync(MetadataCacheFastWait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogDebug("On-demand metadata cache is still warming; using provider data without waiting.");
        }
    }

    private Task StartMetadataCacheLoad()
    {
        lock (_metadataCacheLoadLock)
        {
            _metadataCacheLoadTask ??= Task.Run(LoadMetadataCacheFromDisk);
            return _metadataCacheLoadTask;
        }
    }

    private void LoadMetadataCacheFromDisk()
    {
        try
        {
            if (!File.Exists(_metadataCacheFilePath))
            {
                return;
            }

            var cacheFile = new FileInfo(_metadataCacheFilePath);
            if (cacheFile.Length > MaxMetadataCacheFileBytes)
            {
                _logger.LogInformation(
                    "Skipping oversized on-demand metadata cache at {MetadataCacheFilePath} ({MetadataCacheBytes} bytes)",
                    _metadataCacheFilePath,
                    cacheFile.Length);
                return;
            }

            var wasProtected = ProtectedCatalogFile.IsProtected(_metadataCacheFilePath);
            var payload = wasProtected
                ? ProtectedCatalogFile.Read(_metadataCacheFilePath)
                : File.ReadAllBytes(_metadataCacheFilePath);
            MetadataCacheStore? store;
            try
            {
                store = JsonSerializer.Deserialize<MetadataCacheStore>(payload, JsonOptions);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }

            if (store is null)
            {
                return;
            }

            foreach (var item in store.CategoryLists.Where(item => IsMetadataCacheFresh(item.CachedUtc)))
            {
                WriteFreshestMetadataCache(
                    _categoryMetadataCache,
                    item.Key,
                    new MetadataCacheEntry<IReadOnlyList<CategoryModel>>(item.Values, item.CachedUtc));
            }

            foreach (var item in store.MovieLists.Where(item => IsMetadataCacheFresh(item.CachedUtc)))
            {
                WriteFreshestMetadataCache(
                    _movieMetadataCache,
                    item.Key,
                    new MetadataCacheEntry<IReadOnlyList<MovieModel>>(item.Values, item.CachedUtc));
            }

            foreach (var item in store.MovieDetails.Where(item => IsMetadataCacheFresh(item.CachedUtc)))
            {
                WriteFreshestMetadataCache(
                    _movieDetailsMetadataCache,
                    item.Key,
                    new MetadataCacheEntry<MovieDetailsModel?>(item.Value, item.CachedUtc));
            }

            foreach (var item in store.SeriesLists.Where(item => IsMetadataCacheFresh(item.CachedUtc)))
            {
                WriteFreshestMetadataCache(
                    _seriesMetadataCache,
                    item.Key,
                    new MetadataCacheEntry<IReadOnlyList<SeriesModel>>(item.Values, item.CachedUtc));
            }

            foreach (var item in store.SeriesDetails.Where(item => IsMetadataCacheFresh(item.CachedUtc)))
            {
                WriteFreshestMetadataCache(
                    _seriesDetailsMetadataCache,
                    item.Key,
                    new MetadataCacheEntry<SeriesDetailsModel?>(item.Value, item.CachedUtc));
            }

            if (!wasProtected)
            {
                MigrateLegacyMetadataCacheToProtectedStorage(store);
            }

            _logger.LogInformation("Loaded on-demand metadata cache from {MetadataCacheFilePath}", _metadataCacheFilePath);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load on-demand metadata cache");
        }
    }

    private static bool IsMetadataCacheFresh(DateTimeOffset cachedUtc)
        => DateTimeOffset.UtcNow - cachedUtc <= MetadataCacheTtl;

    private static void WriteFreshestMetadataCache<T>(
        System.Collections.Concurrent.ConcurrentDictionary<string, MetadataCacheEntry<T>> cache,
        string key,
        MetadataCacheEntry<T> entry)
        => cache.AddOrUpdate(
            key,
            entry,
            (_, existing) => existing.CachedUtc >= entry.CachedUtc ? existing : entry);

    private void QueueMetadataCacheSave()
    {
        Interlocked.Exchange(ref _metadataCacheSaveDirty, 1);
        if (Interlocked.CompareExchange(ref _metadataCacheSaveRunning, 1, 0) == 0)
        {
            _ = Task.Run(ProcessMetadataCacheSaveQueueAsync);
        }
    }

    private async Task ProcessMetadataCacheSaveQueueAsync()
    {
        try
        {
            while (Interlocked.Exchange(ref _metadataCacheSaveDirty, 0) == 1)
            {
                await Task.Delay(MetadataCacheSaveDelay).ConfigureAwait(false);
                await SaveMetadataCacheSnapshotAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _metadataCacheSaveRunning, 0);
            if (Interlocked.CompareExchange(ref _metadataCacheSaveDirty, 0, 0) == 1)
            {
                QueueMetadataCacheSave();
            }
        }
    }

    private async Task SaveMetadataCacheSnapshotAsync()
    {
        try
        {
            await EnsureMetadataCacheLoadedAsync(CancellationToken.None).ConfigureAwait(false);
            var snapshot = BuildMetadataCacheStore();
            await _metadataCacheSaveGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
                try
                {
                    await ProtectedCatalogFile.WriteAtomicAsync(
                            _metadataCacheFilePath,
                            payload,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(payload);
                }
            }
            finally
            {
                _metadataCacheSaveGate.Release();
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to save on-demand metadata cache");
        }
    }

    private void MigrateLegacyMetadataCacheToProtectedStorage(MetadataCacheStore store)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(store, JsonOptions);
        try
        {
            ProtectedCatalogFile.WriteAtomic(_metadataCacheFilePath, payload);
            _logger.LogInformation("Protected the legacy on-demand metadata cache for the current Windows user");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private void QueueCatalogSave()
    {
        Interlocked.Exchange(ref _catalogSaveDirty, 1);
        if (Interlocked.CompareExchange(ref _catalogSaveRunning, 1, 0) == 0)
        {
            _ = Task.Run(ProcessCatalogSaveQueueAsync);
        }
    }

    private async Task ProcessCatalogSaveQueueAsync()
    {
        try
        {
            while (Interlocked.Exchange(ref _catalogSaveDirty, 0) == 1)
            {
                await Task.Delay(EpgCatalogSaveDelay).ConfigureAwait(false);

                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await SaveUnsafeAsync(CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to save source catalog cache");
        }
        finally
        {
            Interlocked.Exchange(ref _catalogSaveRunning, 0);
            if (Interlocked.CompareExchange(ref _catalogSaveDirty, 0, 0) == 1)
            {
                QueueCatalogSave();
            }
        }
    }

    private MetadataCacheStore BuildMetadataCacheStore()
        => new()
        {
            CategoryLists = _categoryMetadataCache
                .OrderByDescending(item => item.Value.CachedUtc)
                .Take(MaxCachedListEntries)
                .Select(item => new CachedCategoryList(item.Key, item.Value.CachedUtc, item.Value.Value.ToArray()))
                .ToList(),
            MovieLists = _movieMetadataCache
                .Where(item => item.Value.Value.Count <= MaxCachedMediaListItems)
                .OrderByDescending(item => item.Value.CachedUtc)
                .Take(MaxCachedListEntries)
                .Select(item => new CachedMovieList(item.Key, item.Value.CachedUtc, item.Value.Value.ToArray()))
                .ToList(),
            MovieDetails = _movieDetailsMetadataCache
                .OrderByDescending(item => item.Value.CachedUtc)
                .Take(MaxCachedDetailEntries)
                .Select(item => new CachedMovieDetails(item.Key, item.Value.CachedUtc, item.Value.Value))
                .ToList(),
            SeriesLists = _seriesMetadataCache
                .Where(item => item.Value.Value.Count <= MaxCachedMediaListItems)
                .OrderByDescending(item => item.Value.CachedUtc)
                .Take(MaxCachedListEntries)
                .Select(item => new CachedSeriesList(item.Key, item.Value.CachedUtc, item.Value.Value.ToArray()))
                .ToList(),
            SeriesDetails = _seriesDetailsMetadataCache
                .OrderByDescending(item => item.Value.CachedUtc)
                .Take(MaxCachedDetailEntries)
                .Select(item => new CachedSeriesDetails(item.Key, item.Value.CachedUtc, item.Value.Value))
                .ToList(),
        };

    private async Task<XtreamCatalogContext?> GetXtreamCatalogContextAsync(Guid sourceId, CancellationToken cancellationToken)
    {
        await EnsureStoreLoadedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var source = _store.Sources.FirstOrDefault(entry => entry.Id == sourceId);
            if (source is null || source.Kind != SourceKind.XtreamCodes)
            {
                return null;
            }

            return TryCreateXtreamCatalogContext(source, out var context)
                ? context
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool TryCreateXtreamCatalogContext(StoredSource source, out XtreamCatalogContext context)
    {
        context = default!;

        var username = source.XtreamUsername;
        var password = source.XtreamPassword;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            foreach (var streamUri in source.Categories.SelectMany(category => category.Channels).Select(channel => channel.StreamUri))
            {
                if (!TryExtractXtreamCredentials(streamUri, out username, out password, out _))
                {
                    continue;
                }

                break;
            }
        }

        if (string.IsNullOrWhiteSpace(source.Endpoint)
            || string.IsNullOrWhiteSpace(username)
            || string.IsNullOrWhiteSpace(password))
        {
            return false;
        }

        context = new XtreamCatalogContext(source.Endpoint.TrimEnd('/'), username, password);
        return true;
    }

    private static IReadOnlyDictionary<string, string>? BuildCategoryParameters(string? categoryId)
        => string.IsNullOrWhiteSpace(categoryId) || categoryId.StartsWith("__all_", StringComparison.OrdinalIgnoreCase)
            ? null
            : new Dictionary<string, string> { ["category_id"] = categoryId };

    private Dictionary<string, StoredCategory> ParseXtreamCategories(JsonElement root)
    {
        var categories = new Dictionary<string, StoredCategory>(StringComparer.OrdinalIgnoreCase);
        var sortOrder = 1;

        if (root.ValueKind != JsonValueKind.Array)
        {
            return categories;
        }

        foreach (var item in root.EnumerateArray())
        {
            var categoryId = ReadStringOrNull(item, "category_id");
            var categoryName = ReadStringOrNull(item, "category_name");

            if (string.IsNullOrWhiteSpace(categoryId) || string.IsNullOrWhiteSpace(categoryName))
            {
                continue;
            }

            categories[categoryId] = new StoredCategory
            {
                Id = categoryId,
                Name = categoryName,
                SortOrder = sortOrder++,
                Channels = [],
            };
        }

        return categories;
    }

    private void ParseXtreamStreams(
        JsonElement root,
        Dictionary<string, StoredCategory> categories,
        string serverUrl,
        string username,
        string password)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var uncategorized = categories.TryGetValue("uncategorized", out var existing)
            ? existing
            : new StoredCategory
            {
                Id = "uncategorized",
                Name = "Uncategorized",
                SortOrder = categories.Count + 1,
                Channels = [],
            };

        if (!categories.ContainsKey("uncategorized"))
        {
            categories[uncategorized.Id] = uncategorized;
        }

        foreach (var item in root.EnumerateArray())
        {
            var streamId = ReadStringOrNull(item, "stream_id");
            var channelName = ReadStringOrNull(item, "name");

            if (string.IsNullOrWhiteSpace(streamId) || string.IsNullOrWhiteSpace(channelName))
            {
                continue;
            }

            var categoryId = ReadStringOrNull(item, "category_id") ?? "uncategorized";
            var logoUri = ReadStringOrNull(item, "stream_icon");
            var extension = ReadStringOrNull(item, "container_extension");
            var streamSource = ReadStringOrNull(item, "direct_source");

            if (!categories.TryGetValue(categoryId, out var category))
            {
                category = uncategorized;
            }

            var playbackUrl = BuildXtreamLiveStreamUrl(serverUrl, username, password, streamId, extension, streamSource);
            category.Channels.Add(new StoredChannel
            {
                Id = $"xt-{streamId}",
                CategoryId = category.Id,
                Name = channelName,
                StreamUri = playbackUrl,
                LogoUri = logoUri,
                CurrentProgram = null,
                IsFavorite = false,
            });
        }
    }

    private static string BuildXtreamLiveStreamUrl(
        string serverUrl,
        string username,
        string password,
        string streamId,
        string? extension,
        string? directSource)
    {
        if (!string.IsNullOrWhiteSpace(directSource)
            && Uri.TryCreate(directSource, UriKind.Absolute, out var directUri))
        {
            return directUri.ToString();
        }

        var safeExtension = string.IsNullOrWhiteSpace(extension) ? "ts" : extension;
        return $"{serverUrl}/live/{Uri.EscapeDataString(username)}/{Uri.EscapeDataString(password)}/{streamId}.{safeExtension}";
    }

    private static IReadOnlyList<MovieModel> ParseXtreamMovies(JsonElement root, XtreamCatalogContext context)
    {
        var movies = new List<MovieModel>();
        if (root.ValueKind != JsonValueKind.Array)
        {
            return movies;
        }

        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var streamId = ReadFirstStringOrNull(item, "stream_id", "vod_id", "id");
            var title = DecodeXtreamText(ReadFirstStringOrNull(item, "name", "title"));
            if (string.IsNullOrWhiteSpace(streamId) || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var playbackUrl = BuildXtreamMovieStreamUrl(
                context.ServerUrl,
                context.Username,
                context.Password,
                streamId,
                ReadFirstStringOrNull(item, "container_extension", "container"),
                ReadStringOrNull(item, "direct_source"));

            if (!Uri.TryCreate(playbackUrl, UriKind.Absolute, out var playbackUri))
            {
                continue;
            }

            movies.Add(new MovieModel(
                $"vod-{streamId}",
                ReadStringOrNull(item, "category_id") ?? string.Empty,
                title,
                ReadFirstStringOrNull(item, "stream_icon", "movie_image", "cover", "cover_big"),
                ReadBackdropUri(item),
                DecodeXtreamText(ReadFirstStringOrNull(item, "plot", "description", "overview")),
                ReadYear(item),
                NormalizeMediaDuration(ReadFirstStringOrNull(item, "duration", "duration_secs", "runtime")),
                NormalizeMediaText(ReadFirstStringOrNull(item, "rating", "rating_5based", "imdb_rating")),
                playbackUri));
        }

        return movies
            .OrderBy(movie => movie.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static MovieDetailsModel? ParseXtreamMovieDetails(JsonElement root, XtreamCatalogContext context, string rawMovieId)
    {
        var info = GetObjectOrRoot(root, "info");
        var movieData = GetObjectOrRoot(root, "movie_data");

        var streamId = ReadFirstStringOrNull(movieData, "stream_id", "vod_id", "id") ?? rawMovieId;
        var title = DecodeXtreamText(ReadFirstStringOrNull(info, "name", "title"))
            ?? DecodeXtreamText(ReadFirstStringOrNull(movieData, "name", "title"))
            ?? $"Movie {streamId}";

        var playbackUrl = BuildXtreamMovieStreamUrl(
            context.ServerUrl,
            context.Username,
            context.Password,
            streamId,
            ReadFirstStringOrNull(movieData, "container_extension", "container"),
            ReadStringOrNull(movieData, "direct_source"));

        if (!Uri.TryCreate(playbackUrl, UriKind.Absolute, out var playbackUri))
        {
            return null;
        }

        return new MovieDetailsModel(
            $"vod-{streamId}",
            title,
            ReadFirstStringOrNull(info, "movie_image", "stream_icon", "cover", "cover_big", "poster_path"),
            ReadBackdropUri(info),
            DecodeXtreamText(ReadFirstStringOrNull(info, "plot", "description", "overview")),
            ReadYear(info),
            NormalizeMediaDuration(ReadFirstStringOrNull(info, "duration", "duration_secs", "runtime")),
            NormalizeMediaText(ReadFirstStringOrNull(info, "rating", "rating_5based", "imdb_rating")),
            playbackUri);
    }

    private static IReadOnlyList<SeriesModel> ParseXtreamSeries(JsonElement root)
    {
        var series = new List<SeriesModel>();
        if (root.ValueKind != JsonValueKind.Array)
        {
            return series;
        }

        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var seriesId = ReadFirstStringOrNull(item, "series_id", "id");
            var title = DecodeXtreamText(ReadFirstStringOrNull(item, "name", "title"));
            if (string.IsNullOrWhiteSpace(seriesId) || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            series.Add(new SeriesModel(
                $"series-{seriesId}",
                ReadStringOrNull(item, "category_id") ?? string.Empty,
                title,
                ReadFirstStringOrNull(item, "cover", "cover_big", "stream_icon", "poster_path"),
                ReadBackdropUri(item),
                DecodeXtreamText(ReadFirstStringOrNull(item, "plot", "description", "overview")),
                ReadYear(item),
                NormalizeMediaText(ReadFirstStringOrNull(item, "rating", "rating_5based", "imdb_rating"))));
        }

        return series
            .OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static SeriesDetailsModel? ParseXtreamSeriesDetails(JsonElement root, XtreamCatalogContext context, string rawSeriesId)
    {
        var info = GetObjectOrRoot(root, "info");
        var title = DecodeXtreamText(ReadFirstStringOrNull(info, "name", "title"))
            ?? $"Series {rawSeriesId}";

        var seasonsByNumber = ParseXtreamSeasonInfo(root);
        var episodesBySeason = ParseXtreamEpisodes(root, context)
            .GroupBy(entry => entry.SeasonNumber)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(entry => entry.Episode.EpisodeNumber)
                    .Select(entry => entry.Episode)
                    .ToArray());

        var seasonNumbers = seasonsByNumber.Keys
            .Concat(episodesBySeason.Keys)
            .Distinct()
            .OrderBy(number => number)
            .ToArray();

        var seasons = seasonNumbers
            .Select(number =>
            {
                var seasonName = seasonsByNumber.TryGetValue(number, out var knownSeasonName)
                    ? knownSeasonName
                    : $"Season {number}";

                return new SeriesSeasonModel(
                    number,
                    seasonName,
                    episodesBySeason.TryGetValue(number, out var episodes)
                        ? episodes
                        : Array.Empty<SeriesEpisodeModel>());
            })
            .ToArray();

        return new SeriesDetailsModel(
            $"series-{rawSeriesId}",
            title,
            ReadFirstStringOrNull(info, "cover", "cover_big", "movie_image", "stream_icon", "poster_path"),
            ReadBackdropUri(info),
            DecodeXtreamText(ReadFirstStringOrNull(info, "plot", "description", "overview")),
            ReadYear(info),
            NormalizeMediaText(ReadFirstStringOrNull(info, "rating", "rating_5based", "imdb_rating")),
            seasons);
    }

    private static Dictionary<int, string> ParseXtreamSeasonInfo(JsonElement root)
    {
        var seasons = new Dictionary<int, string>();
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("seasons", out var seasonsElement)
            || seasonsElement.ValueKind != JsonValueKind.Array)
        {
            return seasons;
        }

        foreach (var season in seasonsElement.EnumerateArray())
        {
            if (season.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var seasonNumber = ReadIntOrNull(season, "season_number", "season", "id");
            if (!seasonNumber.HasValue)
            {
                continue;
            }

            var seasonName = DecodeXtreamText(ReadFirstStringOrNull(season, "name", "title"))
                ?? $"Season {seasonNumber.Value}";
            seasons[seasonNumber.Value] = seasonName;
        }

        return seasons;
    }

    private static IReadOnlyList<(int SeasonNumber, SeriesEpisodeModel Episode)> ParseXtreamEpisodes(
        JsonElement root,
        XtreamCatalogContext context)
    {
        var episodes = new List<(int SeasonNumber, SeriesEpisodeModel Episode)>();

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("episodes", out var episodesElement))
        {
            return episodes;
        }

        if (episodesElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var season in episodesElement.EnumerateObject())
            {
                var seasonNumber = int.TryParse(season.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSeason)
                    ? parsedSeason
                    : 1;

                if (season.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var fallbackEpisodeNumber = 1;
                foreach (var episode in season.Value.EnumerateArray())
                {
                    AddXtreamEpisode(episodes, episode, seasonNumber, fallbackEpisodeNumber++, context);
                }
            }
        }
        else if (episodesElement.ValueKind == JsonValueKind.Array)
        {
            var fallbackEpisodeNumber = 1;
            foreach (var episode in episodesElement.EnumerateArray())
            {
                AddXtreamEpisode(episodes, episode, 1, fallbackEpisodeNumber++, context);
            }
        }

        return episodes;
    }

    private static void AddXtreamEpisode(
        List<(int SeasonNumber, SeriesEpisodeModel Episode)> episodes,
        JsonElement item,
        int seasonNumber,
        int fallbackEpisodeNumber,
        XtreamCatalogContext context)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var episodeId = ReadFirstStringOrNull(item, "id", "episode_id", "stream_id");
        if (string.IsNullOrWhiteSpace(episodeId))
        {
            return;
        }

        var info = GetObjectOrRoot(item, "info");
        var episodeNumber = ReadIntOrNull(item, "episode_num", "episode_number", "episode")
            ?? fallbackEpisodeNumber;
        var title = DecodeXtreamText(ReadFirstStringOrNull(item, "title", "name"))
            ?? $"Episode {episodeNumber}";

        var playbackUrl = BuildXtreamSeriesStreamUrl(
            context.ServerUrl,
            context.Username,
            context.Password,
            episodeId,
            ReadFirstStringOrNull(item, "container_extension", "container"),
            ReadStringOrNull(item, "direct_source"));

        if (!Uri.TryCreate(playbackUrl, UriKind.Absolute, out var playbackUri))
        {
            return;
        }

        episodes.Add((
            seasonNumber,
            new SeriesEpisodeModel(
                episodeId,
                episodeNumber,
                title,
                ReadFirstStringOrNull(info, "movie_image", "cover", "cover_big", "stream_icon", "poster_path"),
                DecodeXtreamText(ReadFirstStringOrNull(info, "plot", "description", "overview")),
                NormalizeMediaDuration(ReadFirstStringOrNull(info, "duration", "duration_secs", "runtime")),
                NormalizeMediaText(ReadFirstStringOrNull(info, "rating", "rating_5based", "imdb_rating")),
                playbackUri)));
    }

    private static string BuildXtreamMovieStreamUrl(
        string serverUrl,
        string username,
        string password,
        string streamId,
        string? extension,
        string? directSource)
    {
        if (!string.IsNullOrWhiteSpace(directSource)
            && Uri.TryCreate(directSource, UriKind.Absolute, out var directUri))
        {
            return directUri.ToString();
        }

        var safeExtension = string.IsNullOrWhiteSpace(extension) ? "mp4" : extension;
        return $"{serverUrl}/movie/{Uri.EscapeDataString(username)}/{Uri.EscapeDataString(password)}/{streamId}.{safeExtension}";
    }

    private static string BuildXtreamSeriesStreamUrl(
        string serverUrl,
        string username,
        string password,
        string episodeId,
        string? extension,
        string? directSource)
    {
        if (!string.IsNullOrWhiteSpace(directSource)
            && Uri.TryCreate(directSource, UriKind.Absolute, out var directUri))
        {
            return directUri.ToString();
        }

        var safeExtension = string.IsNullOrWhiteSpace(extension) ? "mp4" : extension;
        return $"{serverUrl}/series/{Uri.EscapeDataString(username)}/{Uri.EscapeDataString(password)}/{episodeId}.{safeExtension}";
    }

    private async Task<ChannelEpgModel> FetchXtreamShortEpgAsync(string epgUrl, CancellationToken cancellationToken)
    {
        using var epgDocument = await GetJsonDocumentWithRetryAsync(epgUrl, cancellationToken);
        var programs = ParseXtreamEpgPrograms(epgDocument.RootElement);

        if (programs.Count == 0)
        {
            return ChannelEpgModel.Empty;
        }

        var current = BuildEpgDisplay(programs[0]);
        var next = programs.Count > 1 ? BuildEpgDisplay(programs[1]) : null;

        return new ChannelEpgModel(
            current?.Summary,
            next?.Summary,
            current?.Title,
            current?.Description,
            current?.TimeRange,
            next?.Title,
            next?.Description,
            next?.TimeRange);
    }

    private async Task UpdateStoredChannelEpgAsync(
        Guid sourceId,
        string channelId,
        ChannelEpgModel epg,
        CancellationToken cancellationToken)
    {
        await EnsureStoreLoadedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var channel = _store.Sources
                .Where(source => source.Id == sourceId)
                .SelectMany(source => source.Categories)
                .SelectMany(category => category.Channels)
                .FirstOrDefault(entry => string.Equals(entry.Id, channelId, StringComparison.OrdinalIgnoreCase));

            if (channel is null)
            {
                return;
            }

            channel.CurrentProgram = NormalizeEpgDisplay(epg.CurrentProgram);
            channel.NextProgram = NormalizeEpgDisplay(epg.NextProgram);
            channel.CurrentProgramTitle = NormalizeEpgDisplay(epg.CurrentProgramTitle);
            channel.CurrentProgramDescription = NormalizeEpgDisplay(epg.CurrentProgramDescription);
            channel.CurrentProgramTimeRange = NormalizeEpgDisplay(epg.CurrentProgramTimeRange);
            channel.NextProgramTitle = NormalizeEpgDisplay(epg.NextProgramTitle);
            channel.NextProgramDescription = NormalizeEpgDisplay(epg.NextProgramDescription);
            channel.NextProgramTimeRange = NormalizeEpgDisplay(epg.NextProgramTimeRange);
            channel.EpgUpdatedUtc = DateTimeOffset.UtcNow;

            QueueCatalogSave();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static ChannelEpgModel GetCachedEpgOrEmpty(ChannelEpgLookup lookup)
        => lookup.HasRealEpg
            ? new ChannelEpgModel(
                lookup.CachedCurrentProgram,
                lookup.CachedNextProgram,
                lookup.CachedCurrentProgramTitle ?? lookup.CachedCurrentProgram,
                lookup.CachedCurrentProgramDescription,
                lookup.CachedCurrentProgramTimeRange,
                lookup.CachedNextProgramTitle ?? lookup.CachedNextProgram,
                lookup.CachedNextProgramDescription,
                lookup.CachedNextProgramTimeRange)
            : ChannelEpgModel.Empty;

    private static bool HasStructuredEpg(StoredChannel channel)
        => !string.IsNullOrWhiteSpace(channel.CurrentProgramTitle)
           || !string.IsNullOrWhiteSpace(channel.NextProgramTitle)
           || !string.IsNullOrWhiteSpace(channel.CurrentProgramTimeRange)
           || !string.IsNullOrWhiteSpace(channel.NextProgramTimeRange);

    private static ChannelEpgModel MapStoredEpg(StoredChannel channel)
        => new(
            channel.CurrentProgram,
            channel.NextProgram,
            channel.CurrentProgramTitle ?? channel.CurrentProgram,
            channel.CurrentProgramDescription,
            channel.CurrentProgramTimeRange,
            channel.NextProgramTitle ?? channel.NextProgram,
            channel.NextProgramDescription,
            channel.NextProgramTimeRange);

    private static bool TryBuildXtreamShortEpgUrl(ChannelEpgLookup lookup, out string epgUrl)
    {
        epgUrl = string.Empty;

        if (!TryExtractXtreamCredentials(lookup.StreamUri, out var username, out var password, out var streamIdFromUri))
        {
            return false;
        }

        var streamId = TryReadXtreamStreamId(lookup.ChannelId) ?? streamIdFromUri;
        if (string.IsNullOrWhiteSpace(streamId))
        {
            return false;
        }

        epgUrl = $"{lookup.Endpoint}/player_api.php?username={Uri.EscapeDataString(username)}&password={Uri.EscapeDataString(password)}&action=get_short_epg&stream_id={Uri.EscapeDataString(streamId)}&limit=2";
        return true;
    }

    private static bool TryExtractXtreamCredentials(
        string streamUriText,
        out string username,
        out string password,
        out string? streamId)
    {
        username = string.Empty;
        password = string.Empty;
        streamId = null;

        if (!Uri.TryCreate(streamUriText, UriKind.Absolute, out var streamUri))
        {
            return false;
        }

        var segments = streamUri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();

        var liveIndex = Array.FindIndex(segments, segment => string.Equals(segment, "live", StringComparison.OrdinalIgnoreCase));
        if (liveIndex >= 0 && liveIndex + 3 < segments.Length)
        {
            username = segments[liveIndex + 1];
            password = segments[liveIndex + 2];
            streamId = RemoveStreamExtension(segments[liveIndex + 3]);
            return !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password);
        }

        if (segments.Length >= 3)
        {
            username = segments[^3];
            password = segments[^2];
            streamId = RemoveStreamExtension(segments[^1]);
            return !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password);
        }

        return false;
    }

    private static string? TryReadXtreamStreamId(string channelId)
        => channelId.StartsWith("xt-", StringComparison.OrdinalIgnoreCase)
            ? channelId[3..]
            : null;

    private static string RemoveStreamExtension(string value)
    {
        var extensionIndex = value.LastIndexOf('.');
        return extensionIndex > 0 ? value[..extensionIndex] : value;
    }

    private static List<XtreamEpgProgram> ParseXtreamEpgPrograms(JsonElement root)
    {
        var programs = new List<XtreamEpgProgram>();

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("epg_listings", out var listings)
            && listings.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in listings.EnumerateArray())
            {
                AddXtreamEpgProgram(programs, item);
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                AddXtreamEpgProgram(programs, item);
            }
        }

        return programs;
    }

    private static void AddXtreamEpgProgram(List<XtreamEpgProgram> programs, JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var title = DecodeXtreamText(ReadFirstStringOrNull(item, "title", "name", "programme_name"));
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        var description = DecodeXtreamText(ReadFirstStringOrNull(item, "description", "desc"));
        programs.Add(new XtreamEpgProgram(
            title,
            description,
            ReadEpgDateTimeOrNull(item, "start_timestamp", "start", "start_time"),
            ReadEpgDateTimeOrNull(item, "stop_timestamp", "end", "end_time", "stop")));
    }

    private static EpgDisplayParts? BuildEpgDisplay(XtreamEpgProgram program)
    {
        var title = NormalizeEpgDisplay(program.Title);
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var timeRange = FormatEpgTimeRange(program.StartUtc, program.EndUtc);
        var summary = string.IsNullOrWhiteSpace(timeRange) ? title : $"{timeRange} {title}";
        return new EpgDisplayParts(
            TruncateEpgDisplay(summary, 120),
            TruncateEpgDisplay(title, 100),
            TruncateEpgDisplay(NormalizeEpgDisplay(program.Description), 190),
            timeRange);
    }

    private static string? NormalizeEpgDisplay(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Regex.Replace(value.Trim(), "\\s+", " ");
    }

    private static string? NormalizeMediaText(string? value)
        => NormalizeEpgDisplay(value);

    private static string? NormalizeMediaDuration(string? value)
    {
        var normalized = NormalizeMediaText(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (long.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var totalSeconds))
        {
            return FormatDuration(TimeSpan.FromSeconds(Math.Max(0, totalSeconds)));
        }

        if (TimeSpan.TryParse(normalized, CultureInfo.InvariantCulture, out var duration))
        {
            return FormatDuration(duration);
        }

        return normalized;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1d)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        if (duration.TotalMinutes >= 1d)
        {
            return $"{(int)duration.TotalMinutes}m";
        }

        return duration.TotalSeconds > 0d
            ? $"{(int)duration.TotalSeconds}s"
            : "0m";
    }

    private static string? TruncateEpgDisplay(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length <= maxLength ? value : value[..Math.Max(0, maxLength - 3)] + "...";
    }

    private static string? FormatEpgTimeRange(DateTimeOffset? startUtc, DateTimeOffset? endUtc)
    {
        if (!startUtc.HasValue && !endUtc.HasValue)
        {
            return null;
        }

        if (startUtc.HasValue && endUtc.HasValue)
        {
            return $"{startUtc.Value.ToLocalTime():HH:mm}-{endUtc.Value.ToLocalTime():HH:mm}";
        }

        return startUtc.HasValue
            ? startUtc.Value.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
            : endUtc!.Value.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private static string? ReadFirstStringOrNull(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = ReadStringOrNull(element, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? ReadBackdropUri(JsonElement element)
    {
        var directValue = ReadFirstStringOrNull(element, "backdrop_path", "backdrop", "fanart", "background");
        if (!string.IsNullOrWhiteSpace(directValue))
        {
            return directValue;
        }

        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("backdrop_path", out var backdropArray)
            && backdropArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in backdropArray.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    return item.GetString();
                }
            }
        }

        return null;
    }

    private static string? ReadYear(JsonElement element)
    {
        var value = ReadFirstStringOrNull(element, "year", "releaseDate", "release_date", "releasedate", "air_date");
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = Regex.Match(value, "\\b(19|20)\\d{2}\\b");
        return match.Success ? match.Value : value;
    }

    private static int? ReadIntOrNull(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var rawValue = ReadStringOrNull(element, propertyName);
            if (int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static JsonElement GetObjectOrRoot(JsonElement root, string propertyName)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.Object
            ? value
            : root;

    private static DateTimeOffset? ReadEpgDateTimeOrNull(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var rawValue = ReadStringOrNull(element, propertyName);
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                continue;
            }

            if (long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixValue))
            {
                return unixValue > 100_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(unixValue)
                    : DateTimeOffset.FromUnixTimeSeconds(unixValue);
            }

            if (DateTimeOffset.TryParse(
                    rawValue,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out var parsedDate))
            {
                return parsedDate.ToUniversalTime();
            }
        }

        return null;
    }

    private static string? DecodeXtreamText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (!LooksLikeBase64(trimmed))
        {
            return trimmed;
        }

        try
        {
            var padded = trimmed.PadRight(trimmed.Length + ((4 - trimmed.Length % 4) % 4), '=');
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(padded)).Trim();
            return IsReadableDecodedText(decoded) ? decoded : trimmed;
        }
        catch (FormatException)
        {
            return trimmed;
        }
    }

    private static bool LooksLikeBase64(string value)
    {
        if (value.Length < 4)
        {
            return false;
        }

        return value.All(character => char.IsLetterOrDigit(character) || character is '+' or '/' or '=' or '-' or '_');
    }

    private static bool IsReadableDecodedText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.Contains('\uFFFD'))
        {
            return false;
        }

        return value.Any(char.IsLetterOrDigit) && value.All(character => !char.IsControl(character) || char.IsWhiteSpace(character));
    }

    private static List<StoredCategory> ParseM3u(string content)
    {
        var categories = new Dictionary<string, StoredCategory>(StringComparer.OrdinalIgnoreCase);

        string? pendingName = null;
        string? pendingGroup = null;
        string? pendingLogo = null;
        var categoryOrder = 1;
        var channelCounter = 1;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                var commaIndex = line.IndexOf(',');
                var metadata = commaIndex >= 0 ? line[..commaIndex] : line;
                pendingName = commaIndex >= 0 ? line[(commaIndex + 1)..].Trim() : null;
                pendingGroup = ReadM3uAttribute(metadata, "group-title");
                pendingLogo = ReadM3uAttribute(metadata, "tvg-logo");
                continue;
            }

            if (line.StartsWith('#'))
            {
                continue;
            }

            if (!Uri.TryCreate(line, UriKind.Absolute, out var streamUri))
            {
                continue;
            }

            var categoryName = string.IsNullOrWhiteSpace(pendingGroup) ? "Uncategorized" : pendingGroup;
            var categoryId = CreateSlug(categoryName);

            if (!categories.TryGetValue(categoryId, out var category))
            {
                category = new StoredCategory
                {
                    Id = categoryId,
                    Name = categoryName,
                    SortOrder = categoryOrder++,
                    Channels = [],
                };

                categories[categoryId] = category;
            }

            var channelName = string.IsNullOrWhiteSpace(pendingName)
                ? $"Channel {channelCounter}"
                : pendingName;

            category.Channels.Add(new StoredChannel
            {
                Id = $"m3u-{channelCounter}",
                CategoryId = category.Id,
                Name = channelName,
                StreamUri = streamUri.ToString(),
                LogoUri = pendingLogo,
                CurrentProgram = null,
                IsFavorite = false,
            });

            channelCounter++;
            pendingName = null;
            pendingGroup = null;
            pendingLogo = null;
        }

        return categories.Values
            .OrderBy(category => category.SortOrder)
            .ToList();
    }

    private static string? ReadM3uAttribute(string line, string attributeName)
    {
        var matches = M3uAttributeRegex.Matches(line);
        foreach (Match match in matches)
        {
            if (!match.Success)
            {
                continue;
            }

            var key = match.Groups["key"].Value;
            if (!string.Equals(key, attributeName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return match.Groups["value"].Value;
        }

        return null;
    }

    private static string CreateSlug(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        normalized = SlugRegex.Replace(normalized, "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "uncategorized" : normalized;
    }

    private static CategoryModel MapCategory(StoredCategory category)
        => new(category.Id, category.Name, category.SortOrder);

    private static PlaylistSource MapSource(StoredSource source)
        => new(source.Id, source.Name, source.Kind, source.Endpoint, source.StatusInfo);

    private static string RemoveMediaPrefix(string value, string prefix)
        => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value[prefix.Length..]
            : value;

    private static string NormalizeServerUrl(string serverUrl)
    {
        if (!Uri.TryCreate(serverUrl.Trim(), UriKind.Absolute, out var parsedUri))
        {
            throw new InvalidOperationException("Server URL is invalid.");
        }

        var builder = new UriBuilder(parsedUri)
        {
            Path = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
        };

        return builder.Uri.ToString().TrimEnd('/');
    }

    private static string BuildXtreamUrl(
        string serverUrl,
        string username,
        string password,
        string? action,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        var builder = new StringBuilder(
            $"{serverUrl}/player_api.php?username={Uri.EscapeDataString(username)}&password={Uri.EscapeDataString(password)}");

        if (!string.IsNullOrWhiteSpace(action))
        {
            builder.Append("&action=").Append(Uri.EscapeDataString(action));
        }

        if (parameters is not null)
        {
            foreach (var parameter in parameters)
            {
                if (string.IsNullOrWhiteSpace(parameter.Key) || string.IsNullOrWhiteSpace(parameter.Value))
                {
                    continue;
                }

                builder
                    .Append('&')
                    .Append(Uri.EscapeDataString(parameter.Key))
                    .Append('=')
                    .Append(Uri.EscapeDataString(parameter.Value));
            }
        }

        return builder.ToString();
    }

    private static string RedactSensitiveUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var queryParts = string.IsNullOrWhiteSpace(uri.Query)
            ? Array.Empty<string>()
            : uri.Query
                .TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part =>
                {
                    var equalsIndex = part.IndexOf('=');
                    var rawKey = equalsIndex >= 0 ? part[..equalsIndex] : part;
                    var key = Uri.UnescapeDataString(rawKey);

                    return key.Equals("username", StringComparison.OrdinalIgnoreCase)
                           || key.Equals("password", StringComparison.OrdinalIgnoreCase)
                        ? $"{rawKey}=***"
                        : part;
                })
                .ToArray();

        var pathSegments = uri.AbsolutePath.Split('/');
        for (var index = 0; index < pathSegments.Length - 2; index++)
        {
            if (!pathSegments[index].Equals("live", StringComparison.OrdinalIgnoreCase)
                && !pathSegments[index].Equals("movie", StringComparison.OrdinalIgnoreCase)
                && !pathSegments[index].Equals("series", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            pathSegments[index + 1] = "***";
            pathSegments[index + 2] = "***";
            break;
        }

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Path = string.Join('/', pathSegments),
            Query = string.Join("&", queryParts),
        };

        return builder.Uri.ToString();
    }

    private async Task<string> GetStringWithRetryAsync(
        string url,
        CancellationToken cancellationToken,
        TimeSpan? requestTimeout = null)
    {
        Exception? lastException = null;
        var timeout = requestTimeout ?? DefaultRequestTimeout;
        var safeUrl = RedactSensitiveUrl(url);

        var maxAttempts = timeout <= TimeSpan.FromSeconds(5) ? 1 : 2;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(timeout);

                using var response = await _httpClient
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                    .ConfigureAwait(false);

                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (attempt < maxAttempts)
            {
                lastException = exception;
                _logger.LogWarning(exception, "HTTP request failed for {Url}, attempt {Attempt}", safeUrl, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                lastException = exception;
                break;
            }
        }

        throw new InvalidOperationException($"Failed to load URL: {safeUrl}", lastException);
    }

    private async Task<JsonDocument> GetJsonDocumentWithRetryAsync(
        string url,
        CancellationToken cancellationToken,
        TimeSpan? requestTimeout = null)
    {
        var payload = await GetStringWithRetryAsync(url, cancellationToken, requestTimeout).ConfigureAwait(false);
        try
        {
            return JsonDocument.Parse(payload);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("Invalid JSON response from server.", exception);
        }
    }

    private static string? ReadStringOrNull(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static DateTimeOffset? ParseUnixDate(string? unixText)
    {
        if (string.IsNullOrWhiteSpace(unixText) || !long.TryParse(unixText, out var unixSeconds))
        {
            return null;
        }

        if (unixSeconds <= 0)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
    }

    private void LoadFromDiskOrSeed()
    {
        try
        {
            if (!File.Exists(_catalogFilePath))
            {
                _store = CreateSeedStore();
                var seedPayload = JsonSerializer.SerializeToUtf8Bytes(_store, JsonOptions);
                try
                {
                    ProtectedCatalogFile.WriteAtomic(_catalogFilePath, seedPayload);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(seedPayload);
                }

                _logger.LogInformation("Seed source catalog created at {CatalogFilePath}", _catalogFilePath);
                return;
            }

            var isProtected = ProtectedCatalogFile.IsProtected(_catalogFilePath);
            if (isProtected)
            {
                var protectedPayload = ProtectedCatalogFile.Read(_catalogFilePath);
                try
                {
                    _store = JsonSerializer.Deserialize<CatalogStore>(protectedPayload, JsonOptions) ?? CreateSeedStore();
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(protectedPayload);
                }
            }
            else
            {
                using (var stream = File.OpenRead(_catalogFilePath))
                {
                    _store = JsonSerializer.Deserialize<CatalogStore>(stream, JsonOptions) ?? CreateSeedStore();
                }

                MigrateLegacyCatalogToProtectedStorage();
            }

            _logger.LogInformation("Loaded {SourceCount} IPTV sources from {CatalogFilePath}", _store.Sources.Count, _catalogFilePath);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to load source catalog. A seed catalog will be used.");
            _store = CreateSeedStore();
        }
    }

    private async Task SaveUnsafeAsync(CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(_store, JsonOptions);
        try
        {
            await ProtectedCatalogFile.WriteAtomicAsync(_catalogFilePath, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private void MigrateLegacyCatalogToProtectedStorage()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(_store, JsonOptions);
        try
        {
            ProtectedCatalogFile.WriteAtomic(_catalogFilePath, payload);
            _logger.LogInformation("Legacy source catalog migrated to Windows-protected storage");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Legacy source catalog could not be migrated to Windows-protected storage; the original file was preserved");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static CatalogStore CreateSeedStore()
    {
        var demoSource = new StoredSource
        {
            Id = Guid.Parse("2E987408-A459-4EA8-8A23-A4B07186FC62"),
            Name = "Premium Demo Source",
            Kind = SourceKind.M3uUrl,
            Endpoint = "https://example.local/demo.m3u",
            ImportIdentity = "https://example.local/demo.m3u",
            StatusInfo = new AccountStatusInfo("Active", DateTimeOffset.UtcNow.AddDays(21), true),
            Categories =
            [
                new StoredCategory
                {
                    Id = "news",
                    Name = "News",
                    SortOrder = 1,
                    Channels =
                    [
                        new StoredChannel
                        {
                            Id = "news-1",
                            CategoryId = "news",
                            Name = "Global News One",
                            StreamUri = "https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8",
                            LogoUri = "https://dummyimage.com/64x64/0ea5e9/0f172a.png?text=N1",
                            CurrentProgram = null,
                            NextProgram = null,
                            IsFavorite = false,
                        },
                    ],
                },
                new StoredCategory
                {
                    Id = "sports",
                    Name = "Sports",
                    SortOrder = 2,
                    Channels =
                    [
                        new StoredChannel
                        {
                            Id = "sports-1",
                            CategoryId = "sports",
                            Name = "Arena Sport HD",
                            StreamUri = "https://bitdash-a.akamaihd.net/content/sintel/hls/playlist.m3u8",
                            LogoUri = "https://dummyimage.com/64x64/818cf8/0f172a.png?text=S1",
                            CurrentProgram = null,
                            NextProgram = null,
                            IsFavorite = false,
                        },
                    ],
                },
            ],
        };

        return new CatalogStore
        {
            Sources = [demoSource],
        };
    }

    private sealed class CatalogStore
    {
        public List<StoredSource> Sources { get; set; } = [];
    }

    private sealed class MetadataCacheStore
    {
        public List<CachedCategoryList> CategoryLists { get; set; } = [];

        public List<CachedMovieList> MovieLists { get; set; } = [];

        public List<CachedMovieDetails> MovieDetails { get; set; } = [];

        public List<CachedSeriesList> SeriesLists { get; set; } = [];

        public List<CachedSeriesDetails> SeriesDetails { get; set; } = [];
    }

    private sealed record CachedCategoryList(
        string Key,
        DateTimeOffset CachedUtc,
        CategoryModel[] Values);

    private sealed record CachedMovieList(
        string Key,
        DateTimeOffset CachedUtc,
        MovieModel[] Values);

    private sealed record CachedMovieDetails(
        string Key,
        DateTimeOffset CachedUtc,
        MovieDetailsModel? Value);

    private sealed record CachedSeriesList(
        string Key,
        DateTimeOffset CachedUtc,
        SeriesModel[] Values);

    private sealed record CachedSeriesDetails(
        string Key,
        DateTimeOffset CachedUtc,
        SeriesDetailsModel? Value);

    private sealed record ImportExecution(StoredSource Source, string SuccessMessage);

    private sealed record MetadataCacheEntry<T>(T Value, DateTimeOffset CachedUtc);

    private sealed record ChannelEpgLookup(
        Guid SourceId,
        SourceKind Kind,
        string Endpoint,
        string ChannelId,
        string StreamUri,
        bool HasRealEpg,
        string? CachedCurrentProgram,
        string? CachedNextProgram,
        string? CachedCurrentProgramTitle,
        string? CachedCurrentProgramDescription,
        string? CachedCurrentProgramTimeRange,
        string? CachedNextProgramTitle,
        string? CachedNextProgramDescription,
        string? CachedNextProgramTimeRange);

    private sealed record XtreamCatalogContext(
        string ServerUrl,
        string Username,
        string Password);

    private sealed record XtreamEpgProgram(
        string Title,
        string? Description,
        DateTimeOffset? StartUtc,
        DateTimeOffset? EndUtc);

    private sealed record EpgDisplayParts(
        string? Summary,
        string? Title,
        string? Description,
        string? TimeRange);

    private sealed class StoredSource
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public SourceKind Kind { get; set; }

        public string Endpoint { get; set; } = string.Empty;

        public string ImportIdentity { get; set; } = string.Empty;

        public string? XtreamUsername { get; set; }

        public string? XtreamPassword { get; set; }

        public AccountStatusInfo StatusInfo { get; set; } = new("Unknown", null, false);

        public List<StoredCategory> Categories { get; set; } = [];
    }

    private sealed class StoredCategory
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public int SortOrder { get; set; }

        public List<StoredChannel> Channels { get; set; } = [];
    }

    private sealed class StoredChannel
    {
        public string Id { get; set; } = string.Empty;

        public string CategoryId { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string StreamUri { get; set; } = string.Empty;

        public string? LogoUri { get; set; }

        public string? CurrentProgram { get; set; }

        public string? NextProgram { get; set; }

        public string? CurrentProgramTitle { get; set; }

        public string? CurrentProgramDescription { get; set; }

        public string? CurrentProgramTimeRange { get; set; }

        public string? NextProgramTitle { get; set; }

        public string? NextProgramDescription { get; set; }

        public string? NextProgramTimeRange { get; set; }

        public DateTimeOffset? EpgUpdatedUtc { get; set; }

        public bool IsFavorite { get; set; }
    }
}
