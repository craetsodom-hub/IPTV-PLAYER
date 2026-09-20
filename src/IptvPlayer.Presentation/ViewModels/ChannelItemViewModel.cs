using CommunityToolkit.Mvvm.ComponentModel;
using IptvPlayer.Contracts.Models;
using IptvPlayer.Presentation.Localization;
using System.Text.RegularExpressions;

namespace IptvPlayer.Presentation.ViewModels;

public sealed partial class ChannelItemViewModel : ObservableObject
{
    private static readonly Regex EpgTimeRangePattern = new(
        @"(?<start>\d{1,2}:\d{2})\s*(?:–|—|-)\s*(?<end>\d{1,2}:\d{2})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PipeCountryPrefixPattern = new(
        @"^\s*(?:(?:[A-Z]{2,3}\s*\|\s*)|(?:\|\s*[A-Z]{2,3}\s*\|\s*)|(?:[\[\(]\s*[A-Z]{2,3}\s*[\]\)]\s*))+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CountryPrefixPattern = new(
        @"^\s*(?:[\|\[\(【]\s*[A-Za-z]{2,3}\s*[\|\]\)】]\s*)+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ColonCountryPrefixPattern = new(
        @"^\s*[A-Z]{2,3}\s*:\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public ChannelItemViewModel(
        string id,
        string categoryId,
        string name,
        Uri streamUri,
        string? logoUri,
        string? currentProgram,
        string? nextProgram,
        string? currentProgramTitle,
        string? currentProgramDescription,
        string? currentProgramTimeRange,
        string? nextProgramTitle,
        string? nextProgramDescription,
        string? nextProgramTimeRange,
        bool isFavorite)
    {
        Id = id;
        CategoryId = categoryId;
        Name = name;
        DisplayName = StripCountryPrefix(name);
        CountryPrefixFreeDisplayName = StripChannelDisplayPrefix(name);
        StreamUri = streamUri;
        LogoUri = logoUri;
        CurrentProgram = currentProgram;
        NextProgram = nextProgram;
        CurrentProgramTitle = currentProgramTitle;
        CurrentProgramDescription = currentProgramDescription;
        CurrentProgramTimeRange = currentProgramTimeRange;
        NextProgramTitle = nextProgramTitle;
        NextProgramDescription = nextProgramDescription;
        NextProgramTimeRange = nextProgramTimeRange;
        IsFavorite = isFavorite;
    }

    public string Id { get; }

    public string CategoryId { get; }

    public string Name { get; }

    public string DisplayName { get; }

    public string CountryPrefixFreeDisplayName { get; }

    public Uri StreamUri { get; }

    public string? LogoUri { get; }

    public static string StripCountryPrefix(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var stripped = CountryPrefixPattern.Replace(PipeCountryPrefixPattern.Replace(value, string.Empty), string.Empty).Trim();
        return string.IsNullOrWhiteSpace(stripped) ? value.Trim() : stripped;
    }

    public static string StripChannelDisplayPrefix(string value)
    {
        var stripped = ColonCountryPrefixPattern.Replace(StripCountryPrefix(value), string.Empty).Trim();
        return string.IsNullOrWhiteSpace(stripped) ? value.Trim() : stripped;
    }

    [ObservableProperty]
    private string? currentProgram;

    [ObservableProperty]
    private string? nextProgram;

    [ObservableProperty]
    private string? currentProgramTitle;

    [ObservableProperty]
    private string? currentProgramDescription;

    [ObservableProperty]
    private string? currentProgramTimeRange;

    [ObservableProperty]
    private string? nextProgramTitle;

    [ObservableProperty]
    private string? nextProgramDescription;

    [ObservableProperty]
    private string? nextProgramTimeRange;

    public double CurrentProgramProgressPercent
    {
        get
        {
            if (!TryGetEpgWindow(CurrentProgramTimeRange, out var start, out var end))
            {
                return 0d;
            }

            var now = DateTime.Now;
            if (now <= start)
            {
                return 0d;
            }

            if (now >= end)
            {
                return 100d;
            }

            return Math.Clamp((now - start).TotalMilliseconds / (end - start).TotalMilliseconds * 100d, 0d, 100d);
        }
    }

    public bool HasCurrentProgramTiming
        => TryGetEpgWindow(CurrentProgramTimeRange, out _, out _);

    public bool HasNextProgramTiming
        => TryGetEpgWindow(NextProgramTimeRange, out _, out _);

    public string CurrentProgramRemainingText
    {
        get
        {
            if (!TryGetEpgWindow(CurrentProgramTimeRange, out _, out var end))
            {
                return string.Empty;
            }

            var remaining = end - DateTime.Now;
            return remaining > TimeSpan.Zero ? FormatRemaining(remaining) : string.Empty;
        }
    }

    public string CurrentProgramElapsedText
    {
        get
        {
            if (!TryGetEpgWindow(CurrentProgramTimeRange, out var start, out var end))
            {
                return UiLocalization.Current.GetString("Live");
            }

            var duration = end - start;
            var elapsed = DateTime.Now - start;
            elapsed = elapsed < TimeSpan.Zero
                ? TimeSpan.Zero
                : elapsed > duration
                    ? duration
                    : elapsed;

            return elapsed.TotalHours >= 1d
                ? elapsed.ToString(@"h\:mm\:ss")
                : elapsed.ToString(@"mm\:ss");
        }
    }

    public string NextProgramDurationText
    {
        get
        {
            if (!TryGetEpgWindow(NextProgramTimeRange, out var start, out var end))
            {
                return string.Empty;
            }

            return FormatDuration(end - start);
        }
    }

    [ObservableProperty]
    private bool isFavorite;

    public string FavoriteGlyph => IsFavorite ? "★" : "☆";

    public static ChannelItemViewModel FromModel(ChannelModel model, bool? isFavoriteOverride = null)
        => new(
            model.Id,
            model.CategoryId,
            model.Name,
            model.StreamUri,
            model.LogoUri,
            model.CurrentProgram,
            model.NextProgram,
            model.CurrentProgramTitle,
            model.CurrentProgramDescription,
            model.CurrentProgramTimeRange,
            model.NextProgramTitle,
            model.NextProgramDescription,
            model.NextProgramTimeRange,
            isFavoriteOverride ?? model.IsFavorite);

    public void ApplyEpg(ChannelEpgModel epg)
    {
        CurrentProgram = string.IsNullOrWhiteSpace(epg.CurrentProgram) ? null : epg.CurrentProgram;
        NextProgram = string.IsNullOrWhiteSpace(epg.NextProgram) ? null : epg.NextProgram;
        CurrentProgramTitle = string.IsNullOrWhiteSpace(epg.CurrentProgramTitle) ? null : epg.CurrentProgramTitle;
        CurrentProgramDescription = string.IsNullOrWhiteSpace(epg.CurrentProgramDescription) ? null : epg.CurrentProgramDescription;
        CurrentProgramTimeRange = string.IsNullOrWhiteSpace(epg.CurrentProgramTimeRange) ? null : epg.CurrentProgramTimeRange;
        NextProgramTitle = string.IsNullOrWhiteSpace(epg.NextProgramTitle) ? null : epg.NextProgramTitle;
        NextProgramDescription = string.IsNullOrWhiteSpace(epg.NextProgramDescription) ? null : epg.NextProgramDescription;
        NextProgramTimeRange = string.IsNullOrWhiteSpace(epg.NextProgramTimeRange) ? null : epg.NextProgramTimeRange;
        NotifyEpgTimingChanged();
    }

    public void RefreshEpgClock()
    {
        OnPropertyChanged(nameof(CurrentProgramProgressPercent));
        OnPropertyChanged(nameof(CurrentProgramElapsedText));
        OnPropertyChanged(nameof(CurrentProgramRemainingText));
    }

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(CurrentProgramTitle));
        OnPropertyChanged(nameof(NextProgramTitle));
        OnPropertyChanged(nameof(CurrentProgramElapsedText));
        OnPropertyChanged(nameof(CurrentProgramRemainingText));
        OnPropertyChanged(nameof(NextProgramDurationText));
    }

    public ChannelModel ToModel()
        => new(
            Id,
            CategoryId,
            Name,
            StreamUri,
            LogoUri,
            CurrentProgram,
            NextProgram,
            CurrentProgramTitle,
            CurrentProgramDescription,
            CurrentProgramTimeRange,
            NextProgramTitle,
            NextProgramDescription,
            NextProgramTimeRange,
            IsFavorite);

    partial void OnIsFavoriteChanged(bool value)
        => OnPropertyChanged(nameof(FavoriteGlyph));

    partial void OnCurrentProgramTimeRangeChanged(string? value)
    {
        OnPropertyChanged(nameof(HasCurrentProgramTiming));
        OnPropertyChanged(nameof(CurrentProgramProgressPercent));
        OnPropertyChanged(nameof(CurrentProgramElapsedText));
        OnPropertyChanged(nameof(CurrentProgramRemainingText));
    }

    partial void OnNextProgramTimeRangeChanged(string? value)
    {
        OnPropertyChanged(nameof(HasNextProgramTiming));
        OnPropertyChanged(nameof(NextProgramDurationText));
    }

    private void NotifyEpgTimingChanged()
    {
        OnPropertyChanged(nameof(HasCurrentProgramTiming));
        OnPropertyChanged(nameof(HasNextProgramTiming));
        OnPropertyChanged(nameof(CurrentProgramProgressPercent));
        OnPropertyChanged(nameof(CurrentProgramElapsedText));
        OnPropertyChanged(nameof(CurrentProgramRemainingText));
        OnPropertyChanged(nameof(NextProgramDurationText));
    }

    private static bool TryGetEpgWindow(string? timeRange, out DateTime start, out DateTime end)
    {
        start = default;
        end = default;
        if (string.IsNullOrWhiteSpace(timeRange))
        {
            return false;
        }

        var match = EpgTimeRangePattern.Match(timeRange);
        if (!match.Success
            || !TimeSpan.TryParse(match.Groups["start"].Value, out var startTime)
            || !TimeSpan.TryParse(match.Groups["end"].Value, out var endTime))
        {
            return false;
        }

        var today = DateTime.Today;
        start = today.Add(startTime);
        end = today.Add(endTime);
        if (end <= start)
        {
            end = end.AddDays(1);
        }

        if (DateTime.Now < start && start - DateTime.Now > TimeSpan.FromHours(12))
        {
            start = start.AddDays(-1);
            end = end.AddDays(-1);
        }

        return true;
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        var totalMinutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        if (hours == 0)
        {
            return UiLocalization.Current.Format("MinutesRemainingFormat", minutes);
        }

        return minutes == 0
            ? UiLocalization.Current.Format("HoursRemainingFormat", hours)
            : UiLocalization.Current.Format("HoursMinutesRemainingFormat", hours, minutes);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        var totalMinutes = Math.Max(0, (int)Math.Round(duration.TotalMinutes));
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        return hours == 0
            ? UiLocalization.Current.Format("MinutesShortFormat", minutes)
            : UiLocalization.Current.Format("HoursMinutesShortFormat", hours, minutes);
    }

}
