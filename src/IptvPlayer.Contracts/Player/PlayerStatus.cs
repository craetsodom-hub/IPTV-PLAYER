namespace IptvPlayer.Contracts.Player;

public sealed record PlayerStatus(
    PlaybackState State,
    string Message,
    float? BufferingPercent = null,
    string? ErrorCode = null);
