using CommunityToolkit.Mvvm.ComponentModel;
using IptvPlayer.Contracts.Services;
using IptvPlayer.Presentation.Localization;

namespace IptvPlayer.Presentation.ViewModels;

public sealed class RecentChannelItemViewModel : ObservableObject
{
    private string _lastWatchedTimeRange = string.Empty;
    private double _progressPercent;

    public RecentChannelItemViewModel(
        ChannelItemViewModel channel,
        RecentChannelHistoryEntry? history)
    {
        Channel = channel;
        UpdateHistory(history);
    }

    public ChannelItemViewModel Channel { get; }

    public string Id => Channel.Id;

    public string DisplayName => Channel.DisplayName;

    public string? LogoUri => Channel.LogoUri;

    public string LastWatchedTimeRange
    {
        get => _lastWatchedTimeRange;
        private set => SetProperty(ref _lastWatchedTimeRange, value);
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    public void UpdateHistory(RecentChannelHistoryEntry? history)
    {
        LastWatchedTimeRange = FormatLastWatchedTimeRange(Channel, history);
        ProgressPercent = history is null || !double.IsFinite(history.ProgressPercent)
            ? Math.Clamp(Channel.CurrentProgramProgressPercent, 0d, 100d)
            : Math.Clamp(history.ProgressPercent, 0d, 100d);
    }

    public void UpdateActiveSession(TimeSpan elapsed)
    {
        var wholeSeconds = Math.Max(0L, (long)Math.Floor(elapsed.TotalSeconds));
        var hours = wholeSeconds / 3600L;
        var minutes = wholeSeconds / 60L % 60L;
        var seconds = wholeSeconds % 60L;

        LastWatchedTimeRange = hours > 0L
            ? $"{hours:00}:{minutes:00}:{seconds:00}"
            : $"{minutes:00}:{seconds:00}";
        ProgressPercent = Math.Clamp(elapsed.TotalMinutes / 30d * 100d, 0d, 100d);
    }

    public void RefreshLocalizedText()
        => LastWatchedTimeRange = UiLocalization.Current.Relocalize(LastWatchedTimeRange);

    private static string FormatLastWatchedTimeRange(
        ChannelItemViewModel channel,
        RecentChannelHistoryEntry? history)
    {
        if (history?.WatchedFromUtc is { } watchedFromUtc)
        {
            var watchedFrom = watchedFromUtc.ToLocalTime();
            var watchedTo = (history.WatchedToUtc ?? watchedFromUtc).ToLocalTime();
            if (watchedTo < watchedFrom)
            {
                watchedTo = watchedFrom;
            }

            return $"{watchedFrom:HH:mm}\u2013{watchedTo:HH:mm}";
        }

        return string.IsNullOrWhiteSpace(channel.CurrentProgramTimeRange)
            ? UiLocalization.Current.GetString("RecentlyWatched")
            : channel.CurrentProgramTimeRange;
    }
}
