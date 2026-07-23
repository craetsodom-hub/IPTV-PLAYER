using IptvPlayer.Contracts.Models;
using IptvPlayer.Presentation.Localization;

namespace IptvPlayer.Presentation.ViewModels;

public sealed class SeriesDetailsViewModel
{
    private SeriesDetailsViewModel(
        string id,
        string title,
        string? posterUri,
        string? backdropUri,
        string? description,
        string? year,
        string? rating,
        IReadOnlyList<SeriesSeasonViewModel> seasons)
    {
        Id = id;
        Title = title;
        PosterUri = posterUri;
        BackdropUri = backdropUri;
        Description = description;
        Year = year;
        Rating = rating;
        Seasons = seasons;
    }

    public string Id { get; }

    public string Title { get; }

    public string? PosterUri { get; }

    public string? BackdropUri { get; }

    public string? Description { get; }

    public string? Year { get; }

    public string? Rating { get; }

    public IReadOnlyList<SeriesSeasonViewModel> Seasons { get; }

    public string DescriptionText => string.IsNullOrWhiteSpace(Description)
        ? UiLocalization.Current.GetString("NoSeriesDescription")
        : Description;

    public string RatingLabel => string.IsNullOrWhiteSpace(Rating)
        ? UiLocalization.Current.GetString("RatingUnavailable")
        : Rating;

    public string MetadataLine
        => string.Join("  ", new[] { Year, Rating }.Where(value => !string.IsNullOrWhiteSpace(value)));

    public bool HasEpisodes => Seasons.Any(season => season.Episodes.Count > 0);

    public static SeriesDetailsViewModel FromModel(SeriesDetailsModel model)
        => new(
            model.Id,
            model.Title,
            model.PosterUri,
            model.BackdropUri,
            model.Description,
            model.Year,
            model.Rating,
            model.Seasons.Select(SeriesSeasonViewModel.FromModel).ToArray());

    public static SeriesDetailsViewModel FromSeries(SeriesItemViewModel series)
        => new(
            series.Id,
            series.Title,
            series.PosterUri,
            series.BackdropUri,
            series.Description,
            series.Year,
            series.Rating,
            Array.Empty<SeriesSeasonViewModel>());
}
