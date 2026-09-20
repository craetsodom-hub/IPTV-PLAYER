namespace IptvPlayer.Contracts.Channels;

public sealed record StreamVariantIdentity(string Key, string QualityLabel, int QualitySortRank, bool HasExplicitLocaleQualifier, string? VerifiedLocaleQualifier);
