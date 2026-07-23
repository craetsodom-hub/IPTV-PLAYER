namespace IptvPlayer.Presentation.Localization;

public sealed record LanguageOption(
    string CultureName,
    string NativeName,
    string EnglishName,
    string FlagCode,
    bool IsRightToLeft = false);
