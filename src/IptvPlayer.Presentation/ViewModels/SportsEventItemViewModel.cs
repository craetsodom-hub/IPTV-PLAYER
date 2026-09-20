using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using IptvPlayer.Contracts.Models;
using IptvPlayer.Presentation.Localization;

namespace IptvPlayer.Presentation.ViewModels;

public sealed partial class SportsEventItemViewModel : ObservableObject
{
    public SportsEventItemViewModel(
        SportsEventModel model,
        string timeLabel,
        string sportLabel,
        IReadOnlyList<EventChannelOptionViewModel> channelOptions,
        bool isChannelOptionsVisible)
    {
        Id = model.Id;
        Model = model;
        Title = model.Title;
        SportLabel = sportLabel;
        Competition = model.Competition;
        TimeLabel = timeLabel;
        HomeTeamName = model.HomeTeam?.Name;
        AwayTeamName = model.AwayTeam?.Name;
        HomeTeamBadgeUri = model.HomeTeam?.BadgeUri;
        AwayTeamBadgeUri = model.AwayTeam?.BadgeUri;
        ArtworkUri = model.ArtworkUri;
        BroadcastSummary = BuildBroadcastSummary(model.Broadcasts);
        ChannelOptions = new ObservableCollection<EventChannelOptionViewModel>(channelOptions);
        ChannelGroups = new ObservableCollection<EventChannelVariantGroupViewModel>(BuildChannelGroups(channelOptions));
        CountryOptions = new ObservableCollection<EventCountryOptionViewModel>(BuildCountryOptions(model.Broadcasts, channelOptions));
        selectedCountryOption = CountryOptions.FirstOrDefault();
        this.isChannelOptionsVisible = isChannelOptionsVisible;
    }

    internal SportsEventModel Model { get; }

    public string Id { get; }

    public string Title { get; }

    public string SportLabel { get; }

    public string? Competition { get; }

    public string TimeLabel { get; }

    public string? HomeTeamName { get; }

    public string? AwayTeamName { get; }

    public string? HomeTeamBadgeUri { get; }

    public string? AwayTeamBadgeUri { get; }

    public string? ArtworkUri { get; }

    public string BroadcastSummary { get; }

    public ObservableCollection<EventChannelOptionViewModel> ChannelOptions { get; }

    public ObservableCollection<EventChannelVariantGroupViewModel> ChannelGroups { get; }

    public ObservableCollection<EventCountryOptionViewModel> CountryOptions { get; }

    public IReadOnlyList<EventChannelOptionViewModel> VisibleChannelOptions
    {
        get
        {
            var selectedCode = SelectedCountryOption?.Code;
            return string.IsNullOrWhiteSpace(selectedCode)
                ? ChannelOptions
                : ChannelOptions
                    .Where(option => string.Equals(option.TerritoryCode, selectedCode, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
        }
    }

    public IReadOnlyList<EventChannelVariantGroupViewModel> VisibleChannelGroups
    {
        get
        {
            var selectedCode = SelectedCountryOption?.Code;
            return string.IsNullOrWhiteSpace(selectedCode)
                ? ChannelGroups
                : ChannelGroups
                    .Where(group => string.Equals(group.TerritoryCode, selectedCode, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
        }
    }

    public bool HasCompetition => !string.IsNullOrWhiteSpace(Competition);

    public bool HasTeams => !string.IsNullOrWhiteSpace(HomeTeamName) || !string.IsNullOrWhiteSpace(AwayTeamName);

    public bool IsStandaloneEvent => !HasTeams;

    public bool HasHomeTeamBadge => !string.IsNullOrWhiteSpace(HomeTeamBadgeUri);

    public bool HasAwayTeamBadge => !string.IsNullOrWhiteSpace(AwayTeamBadgeUri);

    public bool HasArtwork => !string.IsNullOrWhiteSpace(ArtworkUri);

    public bool HasStandaloneArtwork => IsStandaloneEvent && HasArtwork;

    public bool HasChannelOptions => ChannelOptions.Count > 0;

    public bool HasCountryOptions => CountryOptions.Count > 1;

    public bool HasVisibleCountrySelector => IsChannelOptionsVisible && HasCountryOptions;

    public bool HasVisibleChannelOptions => IsChannelOptionsVisible && VisibleChannelGroups.Count > 0;

    public bool HasNoVisibleChannelOptions => IsChannelOptionsVisible && VisibleChannelGroups.Count == 0;

    public bool HasBroadcastSummary => !string.IsNullOrWhiteSpace(BroadcastSummary);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVisibleChannelOptions))]
    [NotifyPropertyChangedFor(nameof(HasVisibleCountrySelector))]
    [NotifyPropertyChangedFor(nameof(HasNoVisibleChannelOptions))]
    private bool isChannelOptionsVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleChannelOptions))]
    [NotifyPropertyChangedFor(nameof(VisibleChannelGroups))]
    [NotifyPropertyChangedFor(nameof(HasVisibleChannelOptions))]
    [NotifyPropertyChangedFor(nameof(HasNoVisibleChannelOptions))]
    private EventCountryOptionViewModel? selectedCountryOption;

    public string TeamsLine
    {
        get
        {
            var teams = new[] { HomeTeamName, AwayTeamName }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
            return teams.Length == 2
                ? UiLocalization.Current.Format("TeamsVersusFormat", teams[0], teams[1])
                : teams.FirstOrDefault() ?? string.Empty;
        }
    }

    public void UpdateChannelOptions(IReadOnlyList<EventChannelOptionViewModel> channelOptions)
    {
        var selectedCode = SelectedCountryOption?.Code;
        ReplaceCollection(ChannelOptions, channelOptions);
        ReplaceCollection(ChannelGroups, BuildChannelGroups(channelOptions));
        ReplaceCollection(CountryOptions, BuildCountryOptions(Model.Broadcasts, channelOptions));
        SelectedCountryOption = CountryOptions.FirstOrDefault(option =>
                string.Equals(option.Code, selectedCode, StringComparison.OrdinalIgnoreCase))
            ?? CountryOptions.FirstOrDefault();

        OnPropertyChanged(nameof(HasChannelOptions));
        OnPropertyChanged(nameof(HasCountryOptions));
        OnPropertyChanged(nameof(HasVisibleCountrySelector));
        OnPropertyChanged(nameof(VisibleChannelOptions));
        OnPropertyChanged(nameof(VisibleChannelGroups));
        OnPropertyChanged(nameof(HasVisibleChannelOptions));
        OnPropertyChanged(nameof(HasNoVisibleChannelOptions));
    }

    private static IReadOnlyList<EventChannelVariantGroupViewModel> BuildChannelGroups(IReadOnlyList<EventChannelOptionViewModel> channelOptions)
    {
        return channelOptions
            .GroupBy(EventChannelVariantGroupViewModel.GetGroupKey, StringComparer.Ordinal)
            .Select(group => new EventChannelVariantGroupViewModel(group.ToArray()))
            .ToArray();
    }

    private static string BuildBroadcastSummary(IReadOnlyList<EventBroadcastModel> broadcasts)
    {
        var names = broadcasts
            .Where(broadcast => broadcast.Confirmed)
            .Select(broadcast => broadcast.ChannelName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();

        return names.Length == 0 ? string.Empty : string.Join(", ", names);
    }

    private static IReadOnlyList<EventCountryOptionViewModel> BuildCountryOptions(
        IReadOnlyList<EventBroadcastModel> broadcasts,
        IReadOnlyList<EventChannelOptionViewModel> channelOptions)
    {
        var options = new List<EventCountryOptionViewModel>
        {
            new(string.Empty, UiLocalization.Current.GetString("Worldwide")),
        };

        var codes = channelOptions
            .Where(option => !string.IsNullOrWhiteSpace(option.TerritoryCode))
            .Select(option => option.TerritoryCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(code => new EventCountryOptionViewModel(code, FormatTerritory(code)))
            .OrderBy(option => option.Label, StringComparer.OrdinalIgnoreCase);

        options.AddRange(codes);

        return options;
    }

    private static string FormatTerritory(string territory)
    {
        try
        {
            var region = new RegionInfo(territory.Trim().ToUpperInvariant());
            return region.DisplayName;
        }
        catch (ArgumentException)
        {
            return territory.Trim().ToUpperInvariant();
        }
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }
}
