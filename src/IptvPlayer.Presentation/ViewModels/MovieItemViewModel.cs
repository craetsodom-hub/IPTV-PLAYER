using CommunityToolkit.Mvvm.ComponentModel;
using IptvPlayer.Contracts.Models;
using IptvPlayer.Contracts.Services;
using IptvPlayer.Presentation.Localization;

namespace IptvPlayer.Presentation.ViewModels;

public sealed partial class MovieItemViewModel : ObservableObject
{
    private MovieItemViewModel(
        string id,
        string categoryId,
        string title,
        string? posterUri,
        string? backdropUri,
        string? description,
        string? year,
        string? duration,
        string? rating,
        Uri playbackUri)
    {
        Id = id;
        CategoryId = categoryId;
        Title = title;
        PosterUri = posterUri;
        BackdropUri = backdropUri;
        Description = description;
        Year = year;
        Duration = duration;
        Rating = rating;
        PlaybackUri = playbackUri;
    }

    public string Id { get; }

    public string CategoryId { get; }

    public string Title { get; }

    public string? PosterUri { get; }

    public string? BackdropUri { get; }

    public string? Description { get; }

    public string? Year { get; }

    public string? Duration { get; }

    public string? Rating { get; }

    public Uri PlaybackUri { get; }

    [ObservableProperty]
    private bool isInWatchlist;

    public string MetadataLine
        => string.Join("  ", new[] { Year, Duration, Rating }.Where(value => !string.IsNullOrWhiteSpace(value)));

    public string WatchlistGlyph => IsInWatchlist ? UiLocalization.Current.GetString("Added") : "+";

    public string WatchlistText => IsInWatchlist
        ? UiLocalization.Current.GetString("InWatchlist")
        : UiLocalization.Current.GetString("Watchlist");

    public static MovieItemViewModel FromModel(MovieModel model)
        => new(
            model.Id,
            model.CategoryId,
            model.Title,
            model.PosterUri,
            model.BackdropUri,
            model.Description,
            model.Year,
            model.Duration,
            model.Rating,
            model.PlaybackUri);

    public static MovieItemViewModel FromWatchlistItem(OnDemandWatchlistItem item)
        => new(
            item.Id,
            item.CategoryId,
            item.Title,
            item.PosterUri,
            item.BackdropUri,
            item.Description,
            item.Year,
            item.Duration,
            item.Rating,
            Uri.TryCreate(item.PlaybackUri, UriKind.Absolute, out var playbackUri)
                ? playbackUri
                : new Uri("about:blank"));

    public OnDemandWatchlistItem ToWatchlistItem()
        => new(
            Id,
            CategoryId,
            Title,
            PosterUri,
            BackdropUri,
            Description,
            Year,
            Duration,
            Rating,
            PlaybackUri.ToString());

    partial void OnIsInWatchlistChanged(bool value)
    {
        OnPropertyChanged(nameof(WatchlistGlyph));
        OnPropertyChanged(nameof(WatchlistText));
    }
}
