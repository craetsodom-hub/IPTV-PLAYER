using CommunityToolkit.Mvvm.ComponentModel;
using IptvPlayer.Contracts.Channels;
using IptvPlayer.Presentation.Localization;

namespace IptvPlayer.Presentation.ViewModels;

public sealed class QuickQualityVariantViewModel : ObservableObject
{
    private string _qualityName;
    private string _qualityDetails;

    public QuickQualityVariantViewModel(
        ChannelItemViewModel channel,
        StreamVariantIdentity identity,
        bool isSelected)
    {
        Channel = channel;
        QualityLabel = identity.QualityLabel;
        (_qualityName, _qualityDetails) = BuildQualityPresentation(identity.QualityLabel);
        QualitySortRank = identity.QualitySortRank;
        IsSelected = isSelected;
    }

    public ChannelItemViewModel Channel { get; }

    public string Id => Channel.Id;

    public string DisplayName => Channel.DisplayName;

    public string? LogoUri => Channel.LogoUri;

    public string QualityLabel { get; }

    public string QualityName
    {
        get => _qualityName;
        private set => SetProperty(ref _qualityName, value);
    }

    public string QualityDetails
    {
        get => _qualityDetails;
        private set => SetProperty(ref _qualityDetails, value);
    }

    public int QualitySortRank { get; }

    public bool IsSelected { get; }

    public void RefreshLocalizedText()
        => (QualityName, QualityDetails) = BuildQualityPresentation(QualityLabel);

    private static (string Name, string Details) BuildQualityPresentation(string qualityLabel)
    {
        var parts = qualityLabel
            .Split('·', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sourceName = parts.FirstOrDefault() ?? "SOURCE";
        var name = sourceName switch
        {
            "LQ" => UiLocalization.Current.GetString("LowQuality"),
            "HQ" => UiLocalization.Current.GetString("HighQuality"),
            "RAW" or "SOURCE" => UiLocalization.Current.GetString("OriginalQuality"),
            _ => sourceName,
        };
        var resolution = sourceName switch
        {
            "LQ" => UiLocalization.Current.GetString("LowerQuality"),
            "SD" => "720×576",
            "HQ" => UiLocalization.Current.GetString("HigherQuality"),
            "HD" or "HD+" or "720P" or "720I" => "1280×720",
            "FHD" or "FHD+" or "1080P" or "1080I" => "1920×1080",
            "UHD" or "UHD+" or "4K" or "2160P" or "2160I" => "3840×2160",
            "8K" or "4320P" or "4320I" => "7680×4320",
            "HEVC" or "HEVC 10-BIT" => UiLocalization.Current.GetString("EfficientVideo"),
            "AVC" or "H.264" => UiLocalization.Current.GetString("StandardVideo"),
            "VVC" => UiLocalization.Current.GetString("NewerVideo"),
            "RAW" => UiLocalization.Current.GetString("OriginalQuality"),
            "SOURCE" => UiLocalization.Current.GetString("FromProvider"),
            _ => string.Empty,
        };

        var secondaryParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(resolution))
        {
            secondaryParts.Add(resolution);
        }

        secondaryParts.AddRange(parts.Skip(1));
        return (name, secondaryParts.Count == 0
            ? UiLocalization.Current.GetString("ExactVersion")
            : string.Join(" · ", secondaryParts));
    }
}
