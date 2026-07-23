using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Resources;
using System.Runtime.CompilerServices;

namespace IptvPlayer.Presentation.Localization;

public sealed class UiLocalization : INotifyPropertyChanged
{
    private const string DefaultCultureName = "en-GB";
    private const string ResourceBaseName = "IptvPlayer.Presentation.Resources.UiStrings";
    private static readonly ResourceManager Resources = new(ResourceBaseName, typeof(UiLocalization).Assembly);
    private static readonly IReadOnlyList<LanguageOption> Languages =
    [
        new("en-GB", "English", "English", "GB"),
        new("es-ES", "Español", "Spanish", "ES"),
        new("fr-FR", "Français", "French", "FR"),
        new("de-DE", "Deutsch", "German", "DE"),
        new("it-IT", "Italiano", "Italian", "IT"),
        new("pt-PT", "Português", "Portuguese", "PT"),
        new("nl-NL", "Nederlands", "Dutch", "NL"),
        new("pl-PL", "Polski", "Polish", "PL"),
        new("ro-RO", "Română", "Romanian", "RO"),
        new("cs-CZ", "Čeština", "Czech", "CZ"),
        new("hu-HU", "Magyar", "Hungarian", "HU"),
        new("el-GR", "Ελληνικά", "Greek", "GR"),
        new("tr-TR", "Türkçe", "Turkish", "TR"),
        new("uk-UA", "Українська", "Ukrainian", "UA"),
        new("ru-RU", "Русский", "Russian", "RU"),
        new("sv-SE", "Svenska", "Swedish", "SE"),
        new("da-DK", "Dansk", "Danish", "DK"),
        new("nb-NO", "Norsk", "Norwegian", "NO"),
        new("fi-FI", "Suomi", "Finnish", "FI"),
        new("ar-SA", "العربية", "Arabic", "SA", IsRightToLeft: true),
        new("zh-Hans", "简体中文", "Simplified Chinese", "CN"),
        new("zh-Hant", "繁體中文", "Traditional Chinese", "TW"),
        new("ja-JP", "日本語", "Japanese", "JP"),
        new("ko-KR", "한국어", "Korean", "KR"),
        new("hi-IN", "हिन्दी", "Hindi", "IN"),
        new("id-ID", "Bahasa Indonesia", "Indonesian", "ID"),
    ];

    private readonly string _preferencePath;
    private LanguageOption _currentLanguage = Languages[0];
    private CultureInfo _previousUiCulture = CultureInfo.CurrentUICulture;
    private bool _isInitialized;

    private UiLocalization()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhoseIPTV",
            "ui-language.txt"))
    {
    }

    internal UiLocalization(string preferencePath)
    {
        _preferencePath = preferencePath;
    }

    public static UiLocalization Current { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? CultureChanged;

    public IReadOnlyList<LanguageOption> SupportedLanguages => Languages;

    public LanguageOption CurrentLanguage => _currentLanguage;

    public bool IsRightToLeft => _currentLanguage.IsRightToLeft;

    public string this[string key] => GetString(key);

    public void Initialize()
    {
        if (_isInitialized)
        {
            return;
        }

        var displayCulture = CultureInfo.CurrentUICulture;
        var persistedCultureName = ReadPersistedCultureName();
        var language = persistedCultureName is null
            ? ResolveSupportedLanguage(displayCulture)
            : FindExactLanguage(persistedCultureName) ?? ResolveSupportedLanguage(displayCulture);

        ApplyLanguage(language, persist: false, notify: false);
        _isInitialized = true;
    }

    public bool SelectLanguage(string cultureName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cultureName);
        Initialize();

        var language = FindExactLanguage(cultureName);
        if (language is null)
        {
            return false;
        }

        if (string.Equals(language.CultureName, _currentLanguage.CultureName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        ApplyLanguage(language, persist: true, notify: true);
        return true;
    }

    public string GetString(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Initialize();

        try
        {
            return Resources.GetString(key, CultureInfo.CurrentUICulture)
                ?? Resources.GetString(key, CultureInfo.InvariantCulture)
                ?? key;
        }
        catch (MissingManifestResourceException)
        {
            return key;
        }
    }

    public string Format(string key, params object?[] arguments)
        => string.Format(CultureInfo.CurrentCulture, GetString(key), arguments);

    public string Relocalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        try
        {
            var previousResources = Resources.GetResourceSet(_previousUiCulture, createIfNotExists: true, tryParents: true);
            if (previousResources is null)
            {
                return value;
            }

            foreach (System.Collections.DictionaryEntry entry in previousResources)
            {
                if (entry.Key is string key
                    && entry.Value is string resourceValue
                    && string.Equals(value, resourceValue, StringComparison.Ordinal))
                {
                    return GetString(key);
                }
            }
        }
        catch (MissingManifestResourceException)
        {
        }

        return value;
    }

    public static LanguageOption ResolveSupportedLanguage(CultureInfo? displayCulture)
    {
        if (displayCulture is null)
        {
            return Languages[0];
        }

        var exact = FindExactLanguage(displayCulture.Name);
        if (exact is not null)
        {
            return exact;
        }

        var languageCode = displayCulture.TwoLetterISOLanguageName;
        if (string.Equals(languageCode, "zh", StringComparison.OrdinalIgnoreCase))
        {
            var traditional = displayCulture.Name.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                || displayCulture.Name.EndsWith("-TW", StringComparison.OrdinalIgnoreCase)
                || displayCulture.Name.EndsWith("-HK", StringComparison.OrdinalIgnoreCase)
                || displayCulture.Name.EndsWith("-MO", StringComparison.OrdinalIgnoreCase);
            return Languages.First(language => language.CultureName == (traditional ? "zh-Hant" : "zh-Hans"));
        }

        if (languageCode is "no" or "nb" or "nn")
        {
            return Languages.First(language => language.CultureName == "nb-NO");
        }

        return Languages.FirstOrDefault(language =>
                   string.Equals(
                       CultureInfo.GetCultureInfo(language.CultureName).TwoLetterISOLanguageName,
                       languageCode,
                       StringComparison.OrdinalIgnoreCase))
            ?? Languages[0];
    }

    private static LanguageOption? FindExactLanguage(string cultureName)
        => Languages.FirstOrDefault(language =>
            string.Equals(language.CultureName, cultureName, StringComparison.OrdinalIgnoreCase));

    private void ApplyLanguage(LanguageOption language, bool persist, bool notify)
    {
        _previousUiCulture = CultureInfo.CurrentUICulture;
        var culture = CultureInfo.GetCultureInfo(language.CultureName);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        _currentLanguage = language;

        if (persist)
        {
            PersistCultureName(language.CultureName);
        }

        if (!notify)
        {
            return;
        }

        OnPropertyChanged(nameof(CurrentLanguage));
        OnPropertyChanged(nameof(IsRightToLeft));
        OnPropertyChanged("Item[]");
        CultureChanged?.Invoke(this, EventArgs.Empty);
    }

    private string? ReadPersistedCultureName()
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

    private void PersistCultureName(string cultureName)
    {
        var temporaryPath = _preferencePath + ".tmp";

        try
        {
            var directory = Path.GetDirectoryName(_preferencePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(temporaryPath, cultureName);
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

    private static void TryDeleteTemporaryPreference(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
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
}
