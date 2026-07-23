using IptvPlayer.Contracts.Models;
using IptvPlayer.Presentation.Localization;

namespace IptvPlayer.Presentation.ViewModels;

public sealed class MovieDetailsViewModel
{
    private MovieDetailsViewModel(
        string id,
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

    public string Title { get; }

    public string? PosterUri { get; }

    public string? BackdropUri { get; }

    public string? Description { get; }

    public string? Year { get; }

    public string? Duration { get; }

    public string? Rating { get; }

    public Uri PlaybackUri { get; }

    public string DescriptionText => string.IsNullOrWhiteSpace(Description)
        ? UiLocalization.Current.GetString("NoMovieDescription")
        : Description;

    public string DurationLabel => string.IsNullOrWhiteSpace(Duration)
        ? UiLocalization.Current.GetString("DurationUnavailable")
        : Duration;

    public string RatingLabel => string.IsNullOrWhiteSpace(Rating)
        ? UiLocalization.Current.GetString("RatingUnavailable")
        : Rating;

    public string MetadataLine
        => string.Join("  ", new[] { Year, Duration, Rating }.Where(value => !string.IsNullOrWhiteSpace(value)));

    public static MovieDetailsViewModel FromModel(MovieDetailsModel model)
        => new(
            model.Id,
            model.Title,
            model.PosterUri,
            model.BackdropUri,
            model.Description,
            model.Year,
            model.Duration,
            model.Rating,
            model.PlaybackUri);

    public static MovieDetailsViewModel FromMovie(MovieItemViewModel movie)
        => new(
            movie.Id,
            movie.Title,
            movie.PosterUri,
            movie.BackdropUri,
            movie.Description,
            movie.Year,
            movie.Duration,
            movie.Rating,
            movie.PlaybackUri);
}
