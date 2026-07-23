using CommunityToolkit.Mvvm.ComponentModel;
using IptvPlayer.Contracts.Models;

namespace IptvPlayer.Presentation.ViewModels;

public sealed partial class ChannelItemViewModel : ObservableObject
{
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

    public Uri StreamUri { get; }

    public string? LogoUri { get; }

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
}
