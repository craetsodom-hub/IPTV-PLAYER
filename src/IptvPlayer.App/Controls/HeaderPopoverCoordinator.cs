namespace IptvPlayer.App.Controls;

internal static class HeaderPopoverCoordinator
{
    public static event Action<object>? PopoverOpened;

    public static void NotifyOpened(object owner)
        => PopoverOpened?.Invoke(owner);
}
