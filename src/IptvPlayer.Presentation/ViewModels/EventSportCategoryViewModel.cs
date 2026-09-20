using CommunityToolkit.Mvvm.ComponentModel;

namespace IptvPlayer.Presentation.ViewModels;

public sealed partial class EventSportCategoryViewModel : ObservableObject
{
    public EventSportCategoryViewModel(string id, string label)
    {
        Id = id;
        this.label = label;
    }

    public string Id { get; }

    private string label;

    public string Label
    {
        get => label;
        private set => SetProperty(ref label, value);
    }

    [ObservableProperty]
    private bool isSelected;

    public void UpdateLabel(string label)
        => Label = label;
}
