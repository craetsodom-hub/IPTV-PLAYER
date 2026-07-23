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
    }

    public async Task<UserSessionState> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

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
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(UserSessionState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        await _gate.WaitAsync(cancellationToken);

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
        finally
        {
            _gate.Release();
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
}
