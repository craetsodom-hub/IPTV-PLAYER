namespace IptvPlayer.Contracts.Player;

public enum PlaybackState
{
    Idle = 0,
    Connecting = 1,
    Buffering = 2,
    Playing = 3,
    Stopped = 4,
    Failed = 5,
    Paused = 6,
}
