namespace IptvPlayer.Contracts.Services;

public interface IOnDemandStateStore
{
    Task<OnDemandState> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(OnDemandState state, CancellationToken cancellationToken = default);
}
