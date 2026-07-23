using IptvPlayer.Contracts.Models;
using IptvPlayer.Contracts.Player;
using IptvPlayer.Contracts.Services;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.Application.Services;

public sealed class PlaybackOrchestrator
{
    private readonly IPlaybackService _playbackService;
    private readonly ILogger<PlaybackOrchestrator> _logger;

    public PlaybackOrchestrator(
        IPlaybackService playbackService,
        ILogger<PlaybackOrchestrator> logger)
    {
        _playbackService = playbackService;
        _logger = logger;
        _playbackService.StatusChanged += (_, status) => StatusChanged?.Invoke(this, status);
    }

    public event EventHandler<PlayerStatus>? StatusChanged;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => _playbackService.InitializeAsync(cancellationToken);

    public Task PauseAsync(CancellationToken cancellationToken = default)
        => _playbackService.PauseAsync(cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken = default)
        => _playbackService.ResumeAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default)
        => _playbackService.StopAsync(cancellationToken);

    public Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default)
        => _playbackService.SetMutedAsync(muted, cancellationToken);

    public Task<PlaybackProgress> GetProgressAsync(CancellationToken cancellationToken = default)
        => _playbackService.GetProgressAsync(cancellationToken);

    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
        => _playbackService.SeekAsync(position, cancellationToken);

    public Task SeekRelativeAsync(TimeSpan offset, CancellationToken cancellationToken = default)
        => _playbackService.SeekRelativeAsync(offset, cancellationToken);

    public async Task PlayAsync(ChannelModel channel, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Requesting playback for channel {ChannelId} - {ChannelName}", channel.Id, channel.Name);
        await _playbackService.PlayAsync(channel.StreamUri, cancellationToken);
    }

    public async Task PlayUriAsync(Uri streamUri, string title, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Requesting playback for media item {MediaTitle}", title);
        await _playbackService.PlayAsync(streamUri, cancellationToken);
    }
}
