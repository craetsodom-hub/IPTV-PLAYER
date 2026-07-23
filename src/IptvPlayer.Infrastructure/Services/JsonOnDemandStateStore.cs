using System.Security.Cryptography;
using System.Text.Json;
using IptvPlayer.Contracts.Services;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.Infrastructure.Services;

public sealed class JsonOnDemandStateStore : IOnDemandStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<JsonOnDemandStateStore> _logger;
    private readonly string _stateFilePath;

    public JsonOnDemandStateStore(ILogger<JsonOnDemandStateStore> logger)
    {
        _logger = logger;

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IptvPlayer",
            "state");

        _stateFilePath = Path.Combine(root, "on-demand-state.json");
    }

    public async Task<OnDemandState> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (!File.Exists(_stateFilePath))
            {
                return OnDemandState.Empty;
            }

            var wasProtected = ProtectedCatalogFile.IsProtected(_stateFilePath);
            var payload = wasProtected
                ? ProtectedCatalogFile.Read(_stateFilePath)
                : await File.ReadAllBytesAsync(_stateFilePath, cancellationToken);
            OnDemandState? state;
            try
            {
                state = JsonSerializer.Deserialize<OnDemandState>(payload, JsonOptions);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }

            if (!wasProtected)
            {
                await SaveProtectedAsync(Normalize(state), cancellationToken);
                _logger.LogInformation("Protected the legacy on-demand state for the current Windows user");
            }

            return Normalize(state);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to load on-demand state");
            return OnDemandState.Empty;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(OnDemandState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        await _gate.WaitAsync(cancellationToken);

        try
        {
            await SaveProtectedAsync(Normalize(state), cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to save on-demand state");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveProtectedAsync(OnDemandState state, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        try
        {
            await ProtectedCatalogFile.WriteAtomicAsync(_stateFilePath, payload, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static OnDemandState Normalize(OnDemandState? state)
        => state is null
            ? OnDemandState.Empty
            : new OnDemandState(
                state.WatchlistMovieIds ?? Array.Empty<string>(),
                state.WatchlistSeriesIds ?? Array.Empty<string>(),
                state.ContinueWatchingMovies ?? Array.Empty<OnDemandHistoryEntry>(),
                state.ContinueWatchingSeries ?? Array.Empty<OnDemandHistoryEntry>())
            {
                WatchlistMovieIdsBySource = NormalizeSourceCollections(state.WatchlistMovieIdsBySource),
                WatchlistSeriesIdsBySource = NormalizeSourceCollections(state.WatchlistSeriesIdsBySource),
                WatchlistMoviesBySource = NormalizeWatchlistItems(state.WatchlistMoviesBySource),
                WatchlistSeriesBySource = NormalizeWatchlistItems(state.WatchlistSeriesBySource),
            };

    private static IReadOnlyDictionary<string, IReadOnlyCollection<string>> NormalizeSourceCollections(
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? values)
        => values?
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
            .ToDictionary(
                entry => entry.Key,
                entry => (IReadOnlyCollection<string>)(entry.Value ?? Array.Empty<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, IReadOnlyCollection<OnDemandWatchlistItem>> NormalizeWatchlistItems(
        IReadOnlyDictionary<string, IReadOnlyCollection<OnDemandWatchlistItem>>? values)
        => values?
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
            .ToDictionary(
                entry => entry.Key,
                entry => (IReadOnlyCollection<OnDemandWatchlistItem>)(entry.Value ?? Array.Empty<OnDemandWatchlistItem>())
                    .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Title))
                    .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, IReadOnlyCollection<OnDemandWatchlistItem>>(StringComparer.OrdinalIgnoreCase);
}
