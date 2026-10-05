using System.Text.Json;
using IptvPlayer.Contracts.Services;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.Infrastructure.Services;

public sealed class JsonUserStateStore : IUserStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<JsonUserStateStore> _logger;
    private readonly string _stateFilePath;

    public JsonUserStateStore(ILogger<JsonUserStateStore> logger)
    {
        _logger = logger;

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IptvPlayer",
            "state");

        _stateFilePath = Path.Combine(root, "session-state.json");
#if PLAYBACK_DIAGNOSTICS
        if (Environment.GetEnvironmentVariable("IPTV_PLAYBACK_TEST_DATA_ROOT") is { Length: > 0 } testRoot)
            _stateFilePath = Path.Combine(Path.GetFullPath(testRoot), "state", "session-state.json");
#endif
    }

    public async Task<UserSessionState> LoadAsync(CancellationToken cancellationToken = default)
        // File opens, JSON normalization, and atomic replacement can block even
        // when serialization uses an async API. Keep the whole operation off UI callers.
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Run(() => LoadCoreAsync(cancellationToken)).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<UserSessionState> LoadCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_stateFilePath))
            {
                return UserSessionState.Empty;
            }

            await using var stream = File.OpenRead(_stateFilePath);
            var state = await JsonSerializer.DeserializeAsync<UserSessionState>(stream, JsonOptions, cancellationToken);
            return Normalize(state);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to load user session state");
            return UserSessionState.Empty;
        }
    }

    public async Task SaveAsync(UserSessionState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await Task.Run(() => SaveCoreAsync(state, cancellationToken)).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task SaveCoreAsync(UserSessionState state, CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.GetDirectoryName(_stateFilePath)!;
            Directory.CreateDirectory(directory);

            var tempPath = _stateFilePath + ".tmp";

            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, Normalize(state), JsonOptions, cancellationToken);
            }

            File.Move(tempPath, _stateFilePath, overwrite: true);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to save user session state");
        }
    }

    private static UserSessionState Normalize(UserSessionState? state)
        => state is null
            ? UserSessionState.Empty
            : state with
            {
                FavoriteChannelIds = state.FavoriteChannelIds ?? Array.Empty<string>(),
                RecentChannelIds = state.RecentChannelIds ?? Array.Empty<string>(),
                FavoriteChannelIdsBySource = NormalizeSourceCollections(state.FavoriteChannelIdsBySource),
                RecentChannelIdsBySource = NormalizeSourceCollections(state.RecentChannelIdsBySource),
                RecentChannelHistoryBySource = NormalizeRecentChannelHistory(state.RecentChannelHistoryBySource),
            };

    private static IReadOnlyDictionary<string, IReadOnlyCollection<RecentChannelHistoryEntry>> NormalizeRecentChannelHistory(
        IReadOnlyDictionary<string, IReadOnlyCollection<RecentChannelHistoryEntry>>? values)
        => values?
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
            .ToDictionary(
                entry => entry.Key,
                entry => (IReadOnlyCollection<RecentChannelHistoryEntry>)(entry.Value ?? Array.Empty<RecentChannelHistoryEntry>())
                    .Where(value => value is not null && !string.IsNullOrWhiteSpace(value.ChannelId))
                    .DistinctBy(value => value.ChannelId, StringComparer.OrdinalIgnoreCase)
                    .Select(value => value with
                    {
                        ProgressPercent = double.IsFinite(value.ProgressPercent)
                            ? Math.Clamp(value.ProgressPercent, 0d, 100d)
                            : 0d,
                    })
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, IReadOnlyCollection<RecentChannelHistoryEntry>>(StringComparer.OrdinalIgnoreCase);

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
}
