using CommunityToolkit.Mvvm.ComponentModel;
using IptvPlayer.Contracts.Models;
using IptvPlayer.Contracts.Services;
using IptvPlayer.Presentation.Localization;

namespace IptvPlayer.Presentation.ViewModels;

public sealed partial class SeriesItemViewModel : ObservableObject
{
    private SeriesItemViewModel(
        string id,
        string categoryId,
        string title,
        string? posterUri,
        string? backdropUri,
        string? description,
        string? year,
        string? rating)
    {
        Id = id;
        CategoryId = categoryId;
        Title = title;
        PosterUri = posterUri;
        BackdropUri = backdropUri;
        Description = description;
        Year = year;
        Rating = rating;
    }

    public string Id { get; }

    public string CategoryId { get; }

    public string Title { get; }

    public string? PosterUri { get; }

    public string? BackdropUri { get; }

    public string? Description { get; }

    public string? Year { get; }

    public string? Rating { get; }

    [ObservableProperty]
    private bool isInWatchlist;

    public string MetadataLine
        => string.Join("  ", new[] { Year, Rating }.Where(value => !string.IsNullOrWhiteSpace(value)));

    public string WatchlistGlyph => IsInWatchlist ? UiLocalization.Current.GetString("Added") : "+";

    public string WatchlistText => IsInWatchlist
        ? UiLocalization.Current.GetString("InWatchlist")
        : UiLocalization.Current.GetString("Watchlist");

    public static SeriesItemViewModel FromModel(SeriesModel model)
        => new(
            model.Id,
            model.CategoryId,
            model.Title,
            model.PosterUri,
            model.BackdropUri,
            model.Description,
            model.Year,
            model.Rating);

    public static SeriesItemViewModel FromWatchlistItem(OnDemandWatchlistItem item)
        => new(
            item.Id,
            item.CategoryId,
            item.Title,
            item.PosterUri,
            item.BackdropUri,
            item.Description,
            item.Year,
            item.Rating);

    public OnDemandWatchlistItem ToWatchlistItem()
        => new(
            Id,
            CategoryId,
            Title,
            PosterUri,
            BackdropUri,
            Description,
            Year,
            null,
            Rating,
            null);

    partial void OnIsInWatchlistChanged(bool value)
    {
        OnPropertyChanged(nameof(WatchlistGlyph));
        OnPropertyChanged(nameof(WatchlistText));
    }
}
