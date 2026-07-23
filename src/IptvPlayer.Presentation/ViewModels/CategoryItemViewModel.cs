namespace IptvPlayer.Presentation.ViewModels;

public sealed class CategoryItemViewModel
{
    public CategoryItemViewModel(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }

    public string Name { get; }
}
