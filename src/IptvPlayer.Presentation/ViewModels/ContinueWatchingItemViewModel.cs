using IptvPlayer.Contracts.Services;
using IptvPlayer.Presentation.Localization;

namespace IptvPlayer.Presentation.ViewModels;

public sealed class ContinueWatchingItemViewModel
{
    private ContinueWatchingItemViewModel(
        string? sourceId,
        string mediaId,
        string? parentId,
        string title,
        string? subtitle,
        string? posterUri,
        Uri playbackUri,
        DateTimeOffset updatedUtc,
        double? progressPercent)
    {
        SourceId = sourceId;
        MediaId = mediaId;
        ParentId = parentId;
        Title = title;
        Subtitle = subtitle;
        PosterUri = posterUri;
        PlaybackUri = playbackUri;
        UpdatedUtc = updatedUtc;
        ProgressPercent = NormalizeProgress(progressPercent);
    }

    public string? SourceId { get; }

    public string MediaId { get; }

    public string? ParentId { get; }

    public string Title { get; }

    public string? Subtitle { get; }

    public string? PosterUri { get; }

    public Uri PlaybackUri { get; }

    public DateTimeOffset UpdatedUtc { get; }

    public double? ProgressPercent { get; }

    public double ProgressValue => ProgressPercent ?? 0d;

    public bool HasProgress => ProgressPercent.HasValue && ProgressPercent.Value > 0d;

    public bool HasResumableProgress => ProgressPercent is > 0d and < 100d;

    public ContinueWatchingItemViewModel WithProgress(double progressPercent)
        => new(
            SourceId,
            MediaId,
            ParentId,
            Title,
            Subtitle,
            PosterUri,
            PlaybackUri,
            DateTimeOffset.UtcNow,
            progressPercent);

    public string SubtitleText => string.IsNullOrWhiteSpace(Subtitle)
        ? UiLocalization.Current.GetString("ReadyToResume")
        : Subtitle;

    public OnDemandHistoryEntry ToStateEntry()
        => new(
            MediaId,
            ParentId,
            Title,
            Subtitle,
            PosterUri,
            PlaybackUri.ToString(),
            UpdatedUtc)
        {
            SourceId = SourceId,
            ProgressPercent = ProgressPercent,
        };

    public static ContinueWatchingItemViewModel FromStateEntry(OnDemandHistoryEntry entry)
    {
        var playbackUri = Uri.TryCreate(entry.PlaybackUri, UriKind.Absolute, out var parsedUri)
            ? parsedUri
            : new Uri("about:blank");

        return new ContinueWatchingItemViewModel(
            entry.SourceId,
            entry.MediaId,
            entry.ParentId,
            entry.Title,
            entry.Subtitle,
            entry.PosterUri,
            playbackUri,
            entry.UpdatedUtc,
            entry.ProgressPercent);
    }

    public static ContinueWatchingItemViewModel FromMovie(MovieDetailsViewModel movie, Guid sourceId)
        => new(
            sourceId.ToString("D"),
            movie.Id,
            null,
            movie.Title,
            movie.MetadataLine,
            movie.PosterUri,
            movie.PlaybackUri,
            DateTimeOffset.UtcNow,
            null);

    public static ContinueWatchingItemViewModel FromSeriesEpisode(
        SeriesDetailsViewModel? series,
        SeriesEpisodeViewModel episode,
        Guid sourceId)
        => new(
            sourceId.ToString("D"),
            episode.Id,
            series?.Id,
            series is null ? episode.Title : series.Title,
            episode.DisplayTitle,
            episode.PosterUri ?? series?.PosterUri,
            episode.PlaybackUri,
            DateTimeOffset.UtcNow,
            null);

    private static double? NormalizeProgress(double? progressPercent)
    {
        if (!progressPercent.HasValue)
        {
            return null;
        }

        return Math.Clamp(progressPercent.Value, 0d, 100d);
    }
}
