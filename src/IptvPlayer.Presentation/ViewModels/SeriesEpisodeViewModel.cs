using IptvPlayer.Contracts.Models;

namespace IptvPlayer.Presentation.ViewModels;

public sealed class SeriesEpisodeViewModel
{
    private SeriesEpisodeViewModel(
        string id,
        int episodeNumber,
        string title,
        string? posterUri,
        string? description,
        string? duration,
        string? rating,
        Uri playbackUri)
    {
        Id = id;
        EpisodeNumber = episodeNumber;
        Title = title;
        PosterUri = posterUri;
        Description = description;
        Duration = duration;
        Rating = rating;
        PlaybackUri = playbackUri;
    }

    public string Id { get; }

    public int EpisodeNumber { get; }

    public string Title { get; }

    public string? PosterUri { get; }

    public string? Description { get; }

    public string? Duration { get; }

    public string? Rating { get; }

    public Uri PlaybackUri { get; }

    public string DisplayTitle => EpisodeNumber > 0
        ? $"E{EpisodeNumber:00}  {Title}"
        : Title;

    public string MetadataLine
        => string.Join("  ", new[] { Duration, Rating }.Where(value => !string.IsNullOrWhiteSpace(value)));

    public static SeriesEpisodeViewModel FromModel(SeriesEpisodeModel model)
        => new(
            model.Id,
            model.EpisodeNumber,
            model.Title,
            model.PosterUri,
            model.Description,
            model.Duration,
            model.Rating,
            model.PlaybackUri);
}
