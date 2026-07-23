namespace IptvPlayer.Domain.ValueObjects;

public readonly record struct ExpirationInfo(
    DateTimeOffset? ExpiresAtUtc,
    bool IsProvided)
{
    public int? GetDaysRemaining(DateTimeOffset nowUtc)
    {
        if (!IsProvided || ExpiresAtUtc is null)
        {
            return null;
        }

        return (int)Math.Ceiling((ExpiresAtUtc.Value - nowUtc).TotalDays);
    }
}
