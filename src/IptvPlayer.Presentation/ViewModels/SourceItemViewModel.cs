using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using IptvPlayer.Contracts.Models;
using IptvPlayer.Presentation.Localization;

namespace IptvPlayer.Presentation.ViewModels;

public sealed class SourceItemViewModel : ObservableObject
{
    private readonly string? _statusResourceKey;
    private readonly bool _expirationProvided;
    private readonly DateTimeOffset? _expiresAtUtc;
    private string _statusLabel;
    private string _expirationLabel;
    private string _daysRemainingLabel;

    public SourceItemViewModel(
        Guid id,
        string name,
        SourceKind kind,
        string endpoint,
        string statusLabel,
        string expirationLabel,
        string daysRemainingLabel,
        bool expirationProvided = false,
        DateTimeOffset? expiresAtUtc = null)
    {
        Id = id;
        Name = name;
        Kind = kind;
        Endpoint = endpoint;
        _statusLabel = statusLabel;
        _expirationLabel = expirationLabel;
        _daysRemainingLabel = daysRemainingLabel;
        _statusResourceKey = ResolveStatusResourceKey(statusLabel);
        _expirationProvided = expirationProvided;
        _expiresAtUtc = expiresAtUtc;
    }

    public Guid Id { get; }

    public string Name { get; }

    public SourceKind Kind { get; }

    public string Endpoint { get; }

    public string StatusLabel
    {
        get => _statusLabel;
        private set => SetProperty(ref _statusLabel, value);
    }

    public string ExpirationLabel
    {
        get => _expirationLabel;
        private set => SetProperty(ref _expirationLabel, value);
    }

    public string DaysRemainingLabel
    {
        get => _daysRemainingLabel;
        private set => SetProperty(ref _daysRemainingLabel, value);
    }

    public string KindLabel => Kind switch
    {
        SourceKind.XtreamCodes => UiLocalization.Current.GetString("Account"),
        SourceKind.M3uUrl => UiLocalization.Current.GetString("PlaylistLink"),
        SourceKind.M3uFile => UiLocalization.Current.GetString("PlaylistFile"),
        SourceKind.M3u8Link => UiLocalization.Current.GetString("DirectStream"),
        _ => UiLocalization.Current.GetString("Unknown"),
    };

    public override string ToString() => Name;

    public void RefreshLocalizedText()
    {
        var localization = UiLocalization.Current;
        StatusLabel = _statusResourceKey is null
            ? localization.Relocalize(StatusLabel)
            : localization.GetString(_statusResourceKey);
        ExpirationLabel = _expirationProvided
            ? _expiresAtUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentUICulture)
                ?? localization.GetString("Unknown")
            : localization.GetString("NotProvided");
        DaysRemainingLabel = FormatDaysRemaining(localization);
        OnPropertyChanged(nameof(KindLabel));
    }

    private string FormatDaysRemaining(UiLocalization localization)
    {
        if (!_expirationProvided || _expiresAtUtc is null)
        {
            return localization.GetString("NotAvailableShort");
        }

        var daysRemaining = (int)Math.Ceiling((_expiresAtUtc.Value - DateTimeOffset.UtcNow).TotalDays);
        return Math.Max(0, daysRemaining).ToString(CultureInfo.CurrentUICulture);
    }

    public static SourceItemViewModel FromModel(PlaylistSource model)
    {
        var expirationLabel = UiLocalization.Current.GetString("NotProvided");
        var daysRemainingLabel = UiLocalization.Current.GetString("NotAvailableShort");
        var statusLabel = model.StatusInfo.AccountState;

        if (model.StatusInfo.ExpirationProvided)
        {
            expirationLabel = model.StatusInfo.ExpiresAtUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentUICulture)
                ?? UiLocalization.Current.GetString("Unknown");
            var daysRemaining = model.StatusInfo.GetDaysRemaining(DateTimeOffset.UtcNow);
            if (daysRemaining.HasValue)
            {
                if (daysRemaining.Value <= 0)
                {
                    statusLabel = UiLocalization.Current.GetString("Expired");
                    daysRemainingLabel = "0";
                }
                else
                {
                    daysRemainingLabel = daysRemaining.Value.ToString(CultureInfo.CurrentUICulture);
                }
            }
        }

        var item = new SourceItemViewModel(
            model.Id,
            model.Name,
            model.Kind,
            model.Endpoint,
            statusLabel,
            expirationLabel,
            daysRemainingLabel,
            model.StatusInfo.ExpirationProvided,
            model.StatusInfo.ExpiresAtUtc);
        item.RefreshLocalizedText();
        return item;
    }

    private static string? ResolveStatusResourceKey(string status)
        => status.Trim().ToLowerInvariant() switch
        {
            "active" => "Active",
            "available" => "Available",
            "disabled" => "Disabled",
            "banned" or "blocked" => "Blocked",
            "trial" => "Trial",
            "expired" => "Expired",
            "unknown" => "Unknown",
            _ => null,
        };
}
