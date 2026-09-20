namespace IptvPlayer.Presentation.ViewModels;

public sealed class EventChannelOptionViewModel
{
    public EventChannelOptionViewModel(
        string broadcasterName,
        string territoryCode,
        string territoryLabel,
        ChannelItemViewModel channel)
    {
        BroadcasterName = broadcasterName;
        TerritoryCode = territoryCode;
        TerritoryLabel = territoryLabel;
        Channel = channel;
    }

    public string BroadcasterName { get; }

    public string TerritoryCode { get; }

    public string TerritoryLabel { get; }

    public ChannelItemViewModel Channel { get; }
}
