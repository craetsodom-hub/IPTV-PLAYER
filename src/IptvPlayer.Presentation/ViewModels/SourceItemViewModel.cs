using IptvPlayer.Contracts.Models;
using IptvPlayer.Presentation.Localization;

namespace IptvPlayer.Presentation.ViewModels;

public sealed class SourceItemViewModel
{
    public SourceItemViewModel(
        Guid id,
        string name,
        SourceKind kind,
        string endpoint,
        string statusLabel,
        string expirationLabel,
        string daysRemainingLabel)
    {
        Id = id;
        Name = name;
        Kind = kind;
        Endpoint = endpoint;
        StatusLabel = statusLabel;
        ExpirationLabel = expirationLabel;
        DaysRemainingLabel = daysRemainingLabel;
    }

    public Guid Id { get; }

    public string Name { get; }

    public SourceKind Kind { get; }

    public string Endpoint { get; }

    public string StatusLabel { get; }

    public string ExpirationLabel { get; }

    public string DaysRemainingLabel { get; }

    public string KindLabel => Kind switch
    {
        SourceKind.XtreamCodes => "Xtream",
        SourceKind.M3uUrl => "M3U URL",
        SourceKind.M3uFile => "M3U File",
        SourceKind.M3u8Link => "M3U8",
        _ => UiLocalization.Current.GetString("Unknown"),
    };

    public override string ToString() => Name;

    public static SourceItemViewModel FromModel(PlaylistSource model)
    {
        var expirationLabel = UiLocalization.Current.GetString("NotProvided");
        var daysRemainingLabel = UiLocalization.Current.GetString("NotAvailableShort");
        var statusLabel = model.StatusInfo.AccountState;

        if (model.StatusInfo.ExpirationProvided)
        {
            expirationLabel = model.StatusInfo.ExpiresAtUtc?.ToLocalTime().ToString("g")
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
                    daysRemainingLabel = daysRemaining.Value.ToString();
                }
            }
        }

        return new SourceItemViewModel(
            model.Id,
            model.Name,
            model.Kind,
            model.Endpoint,
            statusLabel,
            expirationLabel,
            daysRemainingLabel);
    }
}
