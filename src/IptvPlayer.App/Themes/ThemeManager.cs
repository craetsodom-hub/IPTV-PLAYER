using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using IptvPlayer.Presentation.Localization;

namespace IptvPlayer.App.Themes;

public sealed class ThemeManager : INotifyPropertyChanged
{
    private const string DefaultThemeId = "midnight";
    private const string ThemeDictionaryMarker = "Theme.ActiveDictionary";

    private static readonly string[] SourceColors =
    [
        "#000000", "#007A4D08", "#009C6510", "#00FFFFFF", "#01000000", "#070C16",
        "#0B1628", "#0F1A2C", "#101621", "#101A2C", "#101F34", "#10FFFFFF",
        "#111E33", "#111F35", "#121B2A", "#122138", "#12FFFFFF", "#132238",
        "#13253D", "#141F33", "#14233A", "#14243A", "#15120A", "#152846",
        "#161F2D", "#16263E", "#164EA2FF", "#16FFFFFF", "#17253B", "#182D49",
        "#18304D", "#18FFFFFF", "#1A2A45", "#1A2E48", "#1A3555", "#1B2B43",
        "#1B3150", "#1C314F", "#1CFFFFFF", "#1D304A", "#1D324F", "#1D3558",
        "#1E2A3B", "#203655", "#204EA2FF", "#223B5D", "#22FFFFFF", "#233B5B",
        "#234EA2FF", "#24200F", "#243C60", "#24466D", "#244EA2FF", "#24FFD166",
        "#24FFFFFF", "#264EA2FF", "#273854", "#284268", "#284462", "#2B405F",
        "#2B4566", "#2E4EA2FF", "#30FFFFFF", "#324866", "#324F71", "#335073",
        "#33FFFFFF", "#345B82", "#34FFFFFF", "#357FD0", "#365372", "#365578",
        "#38658F", "#3A5C84", "#3A69B1FF", "#3D5F87", "#40FFFFFF", "#426690",
        "#456A96", "#4C76A8", "#4EA2FF", "#554EA2FF", "#574EA2FF", "#604EA2FF",
        "#663E8BFF", "#664EA2FF", "#69B1FF", "#6D69B1FF", "#704EA2FF", "#78FFD166",
        "#7A69B1FF", "#8E939A", "#90A4C4", "#9A69B1FF", "#A3A7AE", "#A6070C16",
        "#AA69B1FF", "#B20B1422", "#C1C6CE", "#C3D0E7", "#CC08101D", "#CC17243A",
        "#D4070C16", "#D4AF37", "#D61D3D63", "#D7070C16", "#E8233B5F", "#F0101A2C",
        "#F01B2D49", "#F2070C16", "#F4F7FF", "#F5C84B", "#F9BC64", "#FF000000",
        "#FF08101D", "#FF4EA2FF", "#FF69B1FF", "#FF7A4D08", "#FF90C8FF", "#FF9C6510",
        "#FFC5D5EF", "#FFD166", "#FFD4AF37", "#FFE59A", "#FFE8F1FF", "#FFFFE59A",
        "#FFFFFFFF",
    ];

    private static readonly IReadOnlyList<ThemePalette> Palettes =
    [
        new(
            "midnight", "ThemeMidnightBlue", "#070C16", "#101A2C", "#273854", "#111E33", "#16263E",
            "#0B1628", "#324866", "#F4F7FF", "#C3D0E7", "#90A4C4", "#4EA2FF", "#69B1FF",
            "#357FD0", "#203655", "#4C76A8", "#F9BC64", "#122138", "#335073", "#426690",
            "#4EA2FF", "#69B1FF", "#FFD166", "#101621", "#161F2D", "#1E2A3B"),
        new(
            "emerald", "ThemeEmeraldNight", "#040C0A", "#091713", "#1D3B33", "#0A1C17", "#0E281F",
            "#06120F", "#285045", "#F2FBF7", "#BFDBD0", "#82AA9B", "#35C690", "#55DCAA",
            "#22966C", "#12352B", "#376E5C", "#F0BD63", "#0E261F", "#2E6857", "#40816C",
            "#35C690", "#55DCAA", "#FFD166", "#07130F", "#0D211B", "#122B23"),
        new(
            "deep-teal", "ThemeDeepTeal", "#03151C", "#08242D", "#205160", "#0A2028", "#0D2C36",
            "#061A21", "#285967", "#F2FAFC", "#A9CBD2", "#86AEB7", "#1FA3B8", "#42C2D3",
            "#14768A", "#123A46", "#2D7080", "#1FA3B8", "#031E26", "#0B6078", "#117A91",
            "#1FA3B8", "#42C2D3", "#1FA3B8", "#06181E", "#0A242C", "#0F3039"),
        new(
            "midnight-indigo", "ThemeCinemaNavy", "#040912", "#0B192A", "#213E5B", "#0A1727", "#10263D",
            "#07131F", "#294968", "#F4FAFF", "#B8CEE6", "#86A3C0", "#4B9FDF", "#6DBBFA",
            "#285F93", "#173653", "#2C5B84", "#5EB4FF", "#050B14", "#1D4A7A", "#3479B8",
            "#4B9FDF", "#6DBBFA", "#5EB4FF", "#081321", "#0D1D30", "#122945"),
        new(
            "arctic-ice", "ThemeArcticIce", "#071117", "#0D202A", "#294A5A", "#0B1A22", "#122A35",
            "#08171E", "#315565", "#F4FCFF", "#B9D5E0", "#86A8B6", "#66C8F2", "#8DDFFF",
            "#3185AA", "#143442", "#39758B", "#79D8FF", "#0E1820", "#2E5F79", "#55A1C3",
            "#66C8F2", "#8DDFFF", "#79D8FF", "#09171E", "#0E222C", "#142F3B"),
        new(
            "forest-moss", "ThemeForestMoss", "#070D09", "#102219", "#2C4D3D", "#0E1C15", "#172E23",
            "#0B1711", "#355B47", "#F4FCF7", "#BDD8C8", "#87A994", "#68C38F", "#8BDBAB",
            "#3B8B60", "#17372A", "#3D7157", "#7FD6A7", "#0D1511", "#37634D", "#5FA080",
            "#68C38F", "#8BDBAB", "#7FD6A7", "#0B1711", "#10251B", "#173127"),
        new(
            "slate-cyan", "ThemeSlateCyan", "#080D10", "#111C21", "#2D4650", "#101C21", "#192B31",
            "#0C161A", "#35545F", "#F4FBFC", "#B8D2D8", "#88A8B0", "#64BDD1", "#86D7E8",
            "#347A8C", "#18343C", "#3B6977", "#7AD0E6", "#0F1417", "#356274", "#63A8BF",
            "#64BDD1", "#86D7E8", "#7AD0E6", "#0D171B", "#142229", "#1C3037"),
    ];

    private readonly string _preferencePath;
    private readonly IReadOnlyList<ThemeOption> _themes;
    private ResourceDictionary? _applicationResources;
    private ResourceDictionary? _activeDictionary;
    private ThemeOption _currentTheme;
    private bool _isInitialized;

    private ThemeManager()
    {
        _preferencePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhoseIPTV",
            "ui-theme.txt");

        _themes = Palettes.Select(palette => new ThemeOption(
            palette.Id,
            palette.NameResourceKey,
            palette.CardBackground,
            palette.Accent,
            palette.StreamAccent,
            palette.SelectionBorder)).ToArray();
        _currentTheme = _themes[0];
        UiLocalization.Current.CultureChanged += Localization_OnCultureChanged;
    }

    public static ThemeManager Current { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? ThemeChanged;

    public IReadOnlyList<ThemeOption> Themes => _themes;

    public ThemeOption CurrentTheme
    {
        get => _currentTheme;
        set
        {
            if (value is not null)
            {
                SelectTheme(value.Id);
            }
        }
    }

    public void Initialize(ResourceDictionary applicationResources)
    {
        ArgumentNullException.ThrowIfNull(applicationResources);
        if (_isInitialized)
        {
            return;
        }

        _applicationResources = applicationResources;
        var persistedThemeId = ReadPersistedThemeId();
        var palette = FindPalette(persistedThemeId) ?? FindPalette(DefaultThemeId)!;
        ApplyTheme(palette, persist: false, notify: false);
        _isInitialized = true;
    }

    public bool SelectTheme(string themeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(themeId);
        if (!_isInitialized || _applicationResources is null)
        {
            return false;
        }

        var palette = FindPalette(themeId);
        if (palette is null)
        {
            return false;
        }

        if (string.Equals(_currentTheme.Id, palette.Id, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        ApplyTheme(palette, persist: true, notify: true);
        return true;
    }

    private void ApplyTheme(ThemePalette palette, bool persist, bool notify)
    {
        var nextDictionary = BuildThemeDictionary(palette);
        var mergedDictionaries = _applicationResources!.MergedDictionaries;
        var activeIndex = _activeDictionary is null ? -1 : mergedDictionaries.IndexOf(_activeDictionary);
        if (activeIndex >= 0)
        {
            mergedDictionaries[activeIndex] = nextDictionary;
        }
        else
        {
            mergedDictionaries.Insert(0, nextDictionary);
        }

        _activeDictionary = nextDictionary;
        _currentTheme = _themes.First(theme => string.Equals(theme.Id, palette.Id, StringComparison.OrdinalIgnoreCase));

        if (persist)
        {
            PersistThemeId(palette.Id);
        }

        if (notify)
        {
            OnPropertyChanged(nameof(CurrentTheme));
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static ResourceDictionary BuildThemeDictionary(ThemePalette palette)
    {
        var dictionary = new ResourceDictionary
        {
            [ThemeDictionaryMarker] = palette.Id,
        };

        foreach (var sourceHex in SourceColors)
        {
            var mappedColor = MapColor(sourceHex, palette);
            var resourceSuffix = sourceHex[1..].ToUpperInvariant();
            dictionary[$"Theme.Color.{resourceSuffix}"] = mappedColor;
            dictionary[$"Theme.Brush.{resourceSuffix}"] = CreateBrush(mappedColor);
        }

        AddBrush(dictionary, "Brush.WindowBackground", palette.WindowBackground);
        AddBrush(dictionary, "Brush.CardBackground", palette.CardBackground);
        AddBrush(dictionary, "Brush.CardBorder", palette.CardBorder);
        AddBrush(dictionary, "Brush.SurfaceMuted", palette.SurfaceMuted);
        AddBrush(dictionary, "Brush.SurfaceRaised", palette.SurfaceRaised);
        AddBrush(dictionary, "Brush.InputBackground", palette.InputBackground);
        AddBrush(dictionary, "Brush.InputBorder", palette.InputBorder);
        AddBrush(dictionary, "Brush.TextPrimary", palette.TextPrimary);
        AddBrush(dictionary, "Brush.TextSecondary", palette.TextSecondary);
        AddBrush(dictionary, "Brush.TextMuted", palette.TextMuted);
        AddBrush(dictionary, "Brush.Accent", palette.Accent);
        AddBrush(dictionary, "Brush.AccentHover", palette.AccentHover);
        AddBrush(dictionary, "Brush.AccentPressed", palette.AccentPressed);
        dictionary["Theme.Color.Accent"] = ParseColor(palette.Accent);
        dictionary["Theme.Color.AccentHover"] = ParseColor(palette.AccentHover);
        dictionary["Theme.Color.AccentPressed"] = ParseColor(palette.AccentPressed);
        AddBrush(dictionary, "Brush.SelectionBackground", palette.SelectionBackground);
        AddBrush(dictionary, "Brush.SelectionBorder", palette.SelectionBorder);
        AddBrush(dictionary, "Brush.Warning", palette.Warning);
        AddBrush(dictionary, "Brush.ScrollTrack", palette.ScrollTrack);
        AddBrush(dictionary, "Brush.ScrollThumb", palette.ScrollThumb);
        AddBrush(dictionary, "Brush.ScrollThumbHover", palette.ScrollThumbHover);
        AddBrush(dictionary, "Brush.StreamAccent", palette.StreamAccent);
        AddBrush(dictionary, "Brush.StreamAccentHover", palette.StreamAccentHover);
        AddBrush(dictionary, "Brush.StreamGold", palette.Gold);
        AddBrush(dictionary, "Brush.Gold", palette.Gold);
        AddBrush(dictionary, "Brush.OnDemandSoftSurface", palette.OnDemandSoftSurface);
        AddBrush(dictionary, "Brush.OnDemandRaisedSurface", palette.OnDemandRaisedSurface);
        AddBrush(dictionary, "Brush.OnDemandHoverSurface", palette.OnDemandHoverSurface);
        AddColorBrush(dictionary, "Brush.EpgCardBackground", Blend(ParseColor(palette.Accent), ParseColor(palette.WindowBackground), 0.72d, 255));
        AddColorBrush(dictionary, "Brush.EpgCardBorder", Blend(ParseColor(palette.Accent), ParseColor(palette.WindowBackground), 0.52d, 255));
        return dictionary;
    }

    private static Color MapColor(string sourceHex, ThemePalette palette)
    {
        var source = ParseColor(sourceHex);
        if (string.Equals(palette.Id, DefaultThemeId, StringComparison.OrdinalIgnoreCase))
        {
            return source;
        }

        var rgbKey = $"{source.R:X2}{source.G:X2}{source.B:X2}";
        var mappedHex = rgbKey switch
        {
            "000000" => palette.WindowBackground,
            "070C16" => palette.WindowBackground,
            "101A2C" => palette.CardBackground,
            "273854" => palette.CardBorder,
            "111E33" => palette.SurfaceMuted,
            "16263E" => palette.SurfaceRaised,
            "0B1628" => palette.InputBackground,
            "324866" => palette.InputBorder,
            "F4F7FF" or "FFFFFF" or "E8F1FF" => palette.TextPrimary,
            "C3D0E7" or "C5D5EF" => palette.TextSecondary,
            "90A4C4" or "8E939A" or "A3A7AE" or "C1C6CE" => palette.TextMuted,
            "4EA2FF" => palette.Accent,
            "69B1FF" or "90C8FF" => palette.AccentHover,
            "357FD0" => palette.AccentPressed,
            "203655" => palette.SelectionBackground,
            "4C76A8" => palette.SelectionBorder,
            "F9BC64" => palette.Warning,
            "122138" => palette.ScrollTrack,
            "335073" => palette.ScrollThumb,
            "426690" => palette.ScrollThumbHover,
            "FFD166" or "F5C84B" or "D4AF37" or "FFE59A" => palette.Gold,
            "101621" => palette.OnDemandSoftSurface,
            "161F2D" => palette.OnDemandRaisedSurface,
            "1E2A3B" => palette.OnDemandHoverSurface,
            _ => null,
        };

        var mapped = mappedHex is null
            ? MapUnclassifiedColor(source, palette)
            : ParseColor(mappedHex);
        mapped.A = source.A;
        return mapped;
    }

    private static Color MapUnclassifiedColor(Color source, ThemePalette palette)
    {
        if (source.R == 255 && source.G == 255 && source.B == 255)
        {
            return WithAlpha(ParseColor(palette.TextPrimary), source.A);
        }

        var (hue, saturation, lightness) = ToHsl(source);
        if (hue is >= 32d and <= 58d && saturation >= 0.35d)
        {
            return MatchLightness(ParseColor(palette.Gold), source, 0.78d);
        }

        if (hue is >= 190d and <= 250d && saturation >= 0.2d)
        {
            return MatchLightness(ParseColor(palette.Accent), source, 0.68d);
        }

        if (lightness >= 0.52d)
        {
            var amount = Math.Clamp((lightness - 0.52d) / 0.48d, 0d, 1d);
            return Blend(ParseColor(palette.TextMuted), ParseColor(palette.TextPrimary), amount, source.A);
        }

        var surfaceAmount = Math.Clamp(lightness / 0.32d, 0d, 1d);
        return Blend(ParseColor(palette.WindowBackground), ParseColor(palette.CardBorder), surfaceAmount, source.A);
    }

    private static Color MatchLightness(Color target, Color source, double anchor)
    {
        var (_, targetSaturation, targetLightness) = ToHsl(target);
        var (_, _, sourceLightness) = ToHsl(source);
        var adjustedLightness = Math.Clamp(targetLightness + (sourceLightness - anchor) * 0.72d, 0.08d, 0.94d);
        return FromHsl(ToHsl(target).Hue, targetSaturation, adjustedLightness, source.A);
    }

    private static (double Hue, double Saturation, double Lightness) ToHsl(Color color)
    {
        var r = color.R / 255d;
        var g = color.G / 255d;
        var b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        var lightness = (max + min) / 2d;
        if (delta <= double.Epsilon)
        {
            return (0d, 0d, lightness);
        }

        var saturation = delta / (1d - Math.Abs(2d * lightness - 1d));
        var hue = max == r
            ? 60d * (((g - b) / delta) % 6d)
            : max == g
                ? 60d * (((b - r) / delta) + 2d)
                : 60d * (((r - g) / delta) + 4d);
        if (hue < 0d)
        {
            hue += 360d;
        }

        return (hue, saturation, lightness);
    }

    private static Color FromHsl(double hue, double saturation, double lightness, byte alpha)
    {
        var chroma = (1d - Math.Abs(2d * lightness - 1d)) * saturation;
        var hueSegment = hue / 60d;
        var x = chroma * (1d - Math.Abs(hueSegment % 2d - 1d));
        var (r1, g1, b1) = hueSegment switch
        {
            < 1d => (chroma, x, 0d),
            < 2d => (x, chroma, 0d),
            < 3d => (0d, chroma, x),
            < 4d => (0d, x, chroma),
            < 5d => (x, 0d, chroma),
            _ => (chroma, 0d, x),
        };
        var m = lightness - chroma / 2d;
        return Color.FromArgb(
            alpha,
            (byte)Math.Round((r1 + m) * 255d),
            (byte)Math.Round((g1 + m) * 255d),
            (byte)Math.Round((b1 + m) * 255d));
    }

    private static Color Blend(Color from, Color to, double amount, byte alpha)
    {
        amount = Math.Clamp(amount, 0d, 1d);
        return Color.FromArgb(
            alpha,
            (byte)Math.Round(from.R + (to.R - from.R) * amount),
            (byte)Math.Round(from.G + (to.G - from.G) * amount),
            (byte)Math.Round(from.B + (to.B - from.B) * amount));
    }

    private static Color WithAlpha(Color color, byte alpha)
        => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static void AddBrush(ResourceDictionary dictionary, string key, string colorHex)
        => dictionary[key] = CreateBrush(ParseColor(colorHex));

    private static void AddColorBrush(ResourceDictionary dictionary, string key, Color color)
        => dictionary[key] = CreateBrush(color);

    private static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color ParseColor(string value)
        => (Color)ColorConverter.ConvertFromString(value);

    private static ThemePalette? FindPalette(string? themeId)
        => string.IsNullOrWhiteSpace(themeId)
            ? null
            : Palettes.FirstOrDefault(theme => string.Equals(theme.Id, themeId, StringComparison.OrdinalIgnoreCase));

    private string? ReadPersistedThemeId()
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

    private void PersistThemeId(string themeId)
    {
        var temporaryPath = _preferencePath + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(_preferencePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(temporaryPath, themeId);
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

    private static void TryDeleteTemporaryPreference(string path)
    {
        try
        {
            File.Delete(path);
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

    private void Localization_OnCultureChanged(object? sender, EventArgs e)
    {
        foreach (var theme in _themes)
        {
            theme.RefreshLocalizedText();
        }
    }

    private sealed record ThemePalette(
        string Id,
        string NameResourceKey,
        string WindowBackground,
        string CardBackground,
        string CardBorder,
        string SurfaceMuted,
        string SurfaceRaised,
        string InputBackground,
        string InputBorder,
        string TextPrimary,
        string TextSecondary,
        string TextMuted,
        string Accent,
        string AccentHover,
        string AccentPressed,
        string SelectionBackground,
        string SelectionBorder,
        string Warning,
        string ScrollTrack,
        string ScrollThumb,
        string ScrollThumbHover,
        string StreamAccent,
        string StreamAccentHover,
        string Gold,
        string OnDemandSoftSurface,
        string OnDemandRaisedSurface,
        string OnDemandHoverSurface);
}

public sealed class ThemeOption : INotifyPropertyChanged
{
    private readonly string _nameResourceKey;

    public ThemeOption(
        string id,
        string nameResourceKey,
        string surfaceHex,
        string primaryHex,
        string secondaryHex,
        string tertiaryHex)
    {
        Id = id;
        _nameResourceKey = nameResourceKey;
        SurfaceBrush = CreateBrush(surfaceHex);
        PrimaryBrush = CreateBrush(primaryHex);
        SecondaryBrush = CreateBrush(secondaryHex);
        TertiaryBrush = CreateBrush(tertiaryHex);
    }

    public string Id { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name => UiLocalization.Current.GetString(_nameResourceKey);

    public Brush SurfaceBrush { get; }

    public Brush PrimaryBrush { get; }

    public Brush SecondaryBrush { get; }

    public Brush TertiaryBrush { get; }

    internal void RefreshLocalizedText()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));

    private static Brush CreateBrush(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
}
