using IptvPlayer.Contracts.Services;

namespace IptvPlayer.Application.Services;

public sealed class SessionOrchestrator
{
    private readonly IUserStateStore _stateStore;

    public SessionOrchestrator(IUserStateStore stateStore)
    {
        _stateStore = stateStore;
    }

    public Task<UserSessionState> LoadAsync(CancellationToken cancellationToken = default)
        => _stateStore.LoadAsync(cancellationToken);

    public Task SaveAsync(UserSessionState state, CancellationToken cancellationToken = default)
        => _stateStore.SaveAsync(state, cancellationToken);
}
