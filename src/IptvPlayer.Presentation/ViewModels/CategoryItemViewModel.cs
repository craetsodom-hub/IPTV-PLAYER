using CommunityToolkit.Mvvm.ComponentModel;
using IptvPlayer.Presentation.Localization;

namespace IptvPlayer.Presentation.ViewModels;

public sealed class CategoryItemViewModel : ObservableObject
{
    private string _name;

    public CategoryItemViewModel(string id, string name)
    {
        Id = id;
        _name = name;
    }

    public string Id { get; }

    public string Name
    {
        get => _name;
        private set => SetProperty(ref _name, value);
    }

    public void RefreshLocalizedName(string resourceKey)
        => Name = UiLocalization.Current.GetString(resourceKey);
}
