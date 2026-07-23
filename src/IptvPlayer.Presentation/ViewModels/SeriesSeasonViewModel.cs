using IptvPlayer.Contracts.Models;
using IptvPlayer.Presentation.Localization;

namespace IptvPlayer.Presentation.ViewModels;

public sealed class SeriesSeasonViewModel
{
    private SeriesSeasonViewModel(
        int seasonNumber,
        string name,
        IReadOnlyList<SeriesEpisodeViewModel> episodes)
    {
        SeasonNumber = seasonNumber;
        Name = name;
        Episodes = episodes;
    }

    public int SeasonNumber { get; }

    public string Name { get; }

    public IReadOnlyList<SeriesEpisodeViewModel> Episodes { get; }

    public string Header => Episodes.Count == 1
        ? UiLocalization.Current.Format("OneEpisodeFormat", Name)
        : UiLocalization.Current.Format("EpisodeCountFormat", Name, Episodes.Count);

    public static SeriesSeasonViewModel FromModel(SeriesSeasonModel model)
        => new(
            model.SeasonNumber,
            model.Name,
            model.Episodes.Select(SeriesEpisodeViewModel.FromModel).ToArray());
}
