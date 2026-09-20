using IptvPlayer.Contracts.Models;

namespace IptvPlayer.Contracts.Services;

public interface ISportsEventService
{
    Task<SportsEventFeedModel?> LoadCachedAsync(CancellationToken cancellationToken = default);

    Task<SportsEventFeedModel> RefreshAsync(CancellationToken cancellationToken = default);
}
