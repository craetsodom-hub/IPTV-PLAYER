using IptvPlayer.Contracts.Player;

namespace IptvPlayer.Contracts.Services;

public interface IPlaybackService : IAsyncDisposable
{
    event EventHandler<PlayerStatus>? StatusChanged;

    bool IsPlaying { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task PlayAsync(Uri streamUri, CancellationToken cancellationToken = default);


    Task ResumeAsync(CancellationToken cancellationToken = default);

    Task PauseAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default);

    Task<PlaybackProgress> GetProgressAsync(CancellationToken cancellationToken = default);

    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);

    Task SeekRelativeAsync(TimeSpan offset, CancellationToken cancellationToken = default);
}
