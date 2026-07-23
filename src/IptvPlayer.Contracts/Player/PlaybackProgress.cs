namespace IptvPlayer.Contracts.Player;

public sealed record PlaybackProgress(
    TimeSpan Position,
    TimeSpan Duration,
    bool CanSeek);
