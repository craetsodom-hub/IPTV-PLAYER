using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using IptvPlayer.Presentation.Localization;

namespace IptvPlayer.App.Themes;

public sealed class DesignManager : INotifyPropertyChanged
{
    private const string DefaultDesignId = "rounded-modern";
    private const string DesignDictionaryMarker = "Design.ActiveDictionary";

    private static readonly IReadOnlyList<DesignOption> AvailableDesigns =
    [
        new("rounded-modern", "DesignRounded"),
        new("sharp-technical", "DesignSharp"),
    ];

    private readonly string _preferencePath;
    private ResourceDictionary? _applicationResources;
    private ResourceDictionary? _activeDictionary;
    private DesignOption _currentDesign = AvailableDesigns[0];
    private bool _isInitialized;

    private DesignManager()
    {
        _preferencePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhoseIPTV",
            "ui-design.txt");
        UiLocalization.Current.CultureChanged += Localization_OnCultureChanged;
    }

    public static DesignManager Current { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? DesignChanged;

    public IReadOnlyList<DesignOption> Designs => AvailableDesigns;

    public DesignOption CurrentDesign
    {
        get => _currentDesign;
        set
        {
            if (value is not null)
            {
                SelectDesign(value.Id);
            }
        }
    }

    public void Initialize(ResourceDictionary applicationResources)
    {
        ArgumentNullException.ThrowIfNull(applicationResources);
        if (_isInitialized)
        {
            return;
        }

        _applicationResources = applicationResources;
        var design = FindDesign(ReadPersistedDesignId()) ?? FindDesign(DefaultDesignId)!;
        ApplyDesign(design, persist: false, notify: false);
        _isInitialized = true;
    }

    public bool SelectDesign(string designId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(designId);
        if (!_isInitialized || _applicationResources is null)
        {
            return false;
        }

        var design = FindDesign(designId);
        if (design is null)
        {
            return false;
        }

        if (string.Equals(_currentDesign.Id, design.Id, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        ApplyDesign(design, persist: true, notify: true);
        return true;
    }

    private void ApplyDesign(DesignOption design, bool persist, bool notify)
    {
        var nextDictionary = BuildDesignDictionary(design);
        var mergedDictionaries = _applicationResources!.MergedDictionaries;
        var activeIndex = _activeDictionary is null ? -1 : mergedDictionaries.IndexOf(_activeDictionary);
        if (activeIndex >= 0)
        {
            mergedDictionaries[activeIndex] = nextDictionary;
        }
        else
        {
            mergedDictionaries.Insert(0, nextDictionary);
        }

        _activeDictionary = nextDictionary;
        _currentDesign = design;

        if (persist)
        {
            PersistDesignId(design.Id);
        }

        if (notify)
        {
            OnPropertyChanged(nameof(CurrentDesign));
            DesignChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static ResourceDictionary BuildDesignDictionary(DesignOption design)
    {
        var isSharp = string.Equals(design.Id, "sharp-technical", StringComparison.OrdinalIgnoreCase);
        var technicalRadius = new CornerRadius(2);

        return new ResourceDictionary
        {
            [DesignDictionaryMarker] = design.Id,
            ["Shape.CornerRadius.0"] = new CornerRadius(0),
            ["Shape.CornerRadius.1_5"] = isSharp ? new CornerRadius(1) : new CornerRadius(1.5),
            ["Shape.CornerRadius.2"] = new CornerRadius(2),
            ["Shape.CornerRadius.3"] = isSharp ? technicalRadius : new CornerRadius(3),
            ["Shape.CornerRadius.4"] = isSharp ? technicalRadius : new CornerRadius(4),
            ["Shape.CornerRadius.5"] = isSharp ? technicalRadius : new CornerRadius(5),
            ["Shape.CornerRadius.6"] = isSharp ? technicalRadius : new CornerRadius(6),
            ["Shape.CornerRadius.7"] = isSharp ? technicalRadius : new CornerRadius(7),
            ["Shape.CornerRadius.8"] = isSharp ? technicalRadius : new CornerRadius(8),
            ["Shape.CornerRadius.9"] = isSharp ? technicalRadius : new CornerRadius(9),
            ["Shape.CornerRadius.10"] = isSharp ? technicalRadius : new CornerRadius(10),
            ["Shape.CornerRadius.12"] = isSharp ? technicalRadius : new CornerRadius(12),
            ["Shape.CornerRadius.14"] = isSharp ? technicalRadius : new CornerRadius(14),
            ["Shape.CornerRadius.15"] = isSharp ? technicalRadius : new CornerRadius(15),
            ["Shape.CornerRadius.16"] = isSharp ? technicalRadius : new CornerRadius(16),
            ["Shape.CornerRadius.18"] = isSharp ? technicalRadius : new CornerRadius(18),
            ["Shape.CornerRadius.19"] = isSharp ? technicalRadius : new CornerRadius(19),
            ["Shape.CornerRadius.20"] = isSharp ? technicalRadius : new CornerRadius(20),
            ["Shape.CornerRadius.22"] = isSharp ? technicalRadius : new CornerRadius(22),
            ["Shape.CornerRadius.26"] = isSharp ? technicalRadius : new CornerRadius(26),
            ["Shape.Radius.9"] = isSharp ? 2d : 9d,
            ["Shape.Radius.15"] = isSharp ? 2d : 15d,
            ["Shape.CornerRadius.SwatchLeft"] = isSharp
                ? new CornerRadius(2, 0, 0, 2)
                : new CornerRadius(3, 0, 0, 3),
            ["Shape.CornerRadius.SwatchRight"] = isSharp
                ? new CornerRadius(0, 2, 2, 0)
                : new CornerRadius(0, 3, 3, 0),
            ["Shape.BorderThickness"] = new Thickness(1),
            ["Design.Opacity.0_18"] = isSharp ? 0.03d : 0.18d,
            ["Design.Opacity.0_22"] = isSharp ? 0.04d : 0.22d,
            ["Design.Opacity.0_24"] = isSharp ? 0.04d : 0.24d,
            ["Design.Opacity.0_28"] = isSharp ? 0.06d : 0.28d,
            ["Design.Opacity.0_32"] = isSharp ? 0.06d : 0.32d,
            ["Design.Opacity.0_38"] = isSharp ? 0.08d : 0.38d,
            ["Design.Opacity.0_42"] = isSharp ? 0.09d : 0.42d,
            ["Design.Opacity.0_48"] = isSharp ? 0.10d : 0.48d,
            ["Design.Opacity.0_58"] = isSharp ? 0.12d : 0.58d,
            ["Design.Opacity.0_72"] = isSharp ? 0.14d : 0.72d,
        };
    }

    private static DesignOption? FindDesign(string? designId)
        => string.IsNullOrWhiteSpace(designId)
            ? null
            : AvailableDesigns.FirstOrDefault(design =>
                string.Equals(design.Id, designId, StringComparison.OrdinalIgnoreCase));

    private string? ReadPersistedDesignId()
    {
        try
        {
            if (!File.Exists(_preferencePath))
            {
                return null;
            }

            var value = File.ReadAllText(_preferencePath).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void PersistDesignId(string designId)
    {
        var temporaryPath = _preferencePath + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(_preferencePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(temporaryPath, designId);
            File.Move(temporaryPath, _preferencePath, overwrite: true);
        }
        catch (IOException)
        {
            TryDeleteTemporaryPreference(temporaryPath);
        }
        catch (UnauthorizedAccessException)
        {
            TryDeleteTemporaryPreference(temporaryPath);
        }
    }

    private static void TryDeleteTemporaryPreference(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void Localization_OnCultureChanged(object? sender, EventArgs e)
    {
        foreach (var design in AvailableDesigns)
        {
            design.RefreshLocalizedText();
        }
    }
}

public sealed class DesignOption : INotifyPropertyChanged
{
    private readonly string _nameResourceKey;

    public DesignOption(string id, string nameResourceKey)
    {
        Id = id;
        _nameResourceKey = nameResourceKey;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }

    public string Name => UiLocalization.Current.GetString(_nameResourceKey);

    internal void RefreshLocalizedText()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
}
