using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IptvPlayer.Contracts.Channels;

namespace IptvPlayer.Presentation.ViewModels;

public sealed partial class EventChannelVariantGroupViewModel : ObservableObject
{
    public const int MaxDisplayedVariants = 10;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpansionGlyph))]
    private bool _isExpanded;

    public string CanonicalName { get; }

    public string TerritoryCode { get; }

    public string TerritoryLabel { get; }

    public EventChannelOptionViewModel Representative { get; }

    public ObservableCollection<EventChannelOptionViewModel> Variants { get; }

    public bool HasMultipleVariants => Variants.Count > 1;

    public bool HasSingleVariant => Variants.Count == 1;

    public int VariantCount => Variants.Count;

    public string ExpansionGlyph => IsExpanded ? "⌃" : "⌄";

    public IRelayCommand ToggleCommand { get; }

    public EventChannelVariantGroupViewModel(IReadOnlyList<EventChannelOptionViewModel> variants)
    {
        ArgumentOutOfRangeException.ThrowIfZero(variants.Count, nameof(variants.Count));
        Variants = new ObservableCollection<EventChannelOptionViewModel>(variants.Take(MaxDisplayedVariants));
        CanonicalName = variants[0].BroadcasterName;
        TerritoryCode = variants[0].TerritoryCode;
        TerritoryLabel = variants[0].TerritoryLabel;
        Representative = variants[0];
        ToggleCommand = new RelayCommand(Toggle);
    }

    public static string GetGroupKey(EventChannelOptionViewModel option)
    {
        return string.Join("\u001f", BroadcastChannelIdentity.Key(option.BroadcasterName), option.TerritoryCode.Trim().ToUpperInvariant());
    }

    private void Toggle()
    {
        if (HasMultipleVariants)
        {
            IsExpanded = !IsExpanded;
        }
    }
}
