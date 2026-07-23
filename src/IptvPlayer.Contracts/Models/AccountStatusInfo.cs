namespace IptvPlayer.Contracts.Models;

public sealed record AccountStatusInfo(
    string AccountState,
    DateTimeOffset? ExpiresAtUtc,
    bool ExpirationProvided)
{
    public int? GetDaysRemaining(DateTimeOffset nowUtc)
    {
        if (!ExpirationProvided || ExpiresAtUtc is null)
        {
            return null;
        }

        return (int)Math.Ceiling((ExpiresAtUtc.Value - nowUtc).TotalDays);
    }
}
