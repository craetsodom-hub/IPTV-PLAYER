namespace IptvPlayer.Contracts.Services;

public interface IUserStateStore
{
    Task<UserSessionState> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(UserSessionState state, CancellationToken cancellationToken = default);
}
