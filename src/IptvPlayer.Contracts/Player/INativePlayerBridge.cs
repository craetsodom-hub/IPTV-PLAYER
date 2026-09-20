namespace IptvPlayer.Contracts.Player;

public interface INativePlayerBridge
{
    object? NativePlayer { get; }

    void SetVideoHostHandle(IntPtr handle);
}
