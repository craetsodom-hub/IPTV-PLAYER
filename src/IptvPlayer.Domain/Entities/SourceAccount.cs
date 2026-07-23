using IptvPlayer.Contracts.Models;
using IptvPlayer.Domain.ValueObjects;

namespace IptvPlayer.Domain.Entities;

public sealed record SourceAccount(
    Guid Id,
    string Name,
    SourceKind Kind,
    string Endpoint,
    ExpirationInfo ExpirationInfo,
    string AccountState);
