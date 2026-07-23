using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using IptvPlayer.Presentation.Localization;

namespace IptvPlayer.App.Controls;

public partial class LanguageSelector : UserControl, INotifyPropertyChanged
{
    private readonly UiLocalization _localization = UiLocalization.Current;
    private readonly ObservableCollection<LanguageRow> _rows = [];
    private bool _isSubscribed;

    public LanguageSelector()
    {
        _localization.Initialize();
        FilteredLanguages = CollectionViewSource.GetDefaultView(_rows);
        InitializeComponent();

        FilteredLanguages.Filter = MatchesSearch;
        RebuildRows();

        Loaded += LanguageSelector_OnLoaded;
        Unloaded += LanguageSelector_OnUnloaded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICollectionView FilteredLanguages { get; }

    public string CurrentFlagCode => _localization.CurrentLanguage.FlagCode;

    private void LanguageSelector_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isSubscribed)
        {
            return;
        }

        _localization.CultureChanged += Localization_OnCultureChanged;
        _isSubscribed = true;
    }

    private void LanguageSelector_OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_isSubscribed)
        {
            return;
        }

        _localization.CultureChanged -= Localization_OnCultureChanged;
        _isSubscribed = false;
    }

    private void LanguageButton_OnClick(object sender, RoutedEventArgs e)
        => LanguagePopup.IsOpen = !LanguagePopup.IsOpen;

    private void LanguagePopup_OnOpened(object? sender, EventArgs e)
    {
        LanguageSearchBox.Focus();
        LanguageSearchBox.SelectAll();
    }

    private void LanguageSearchBox_OnTextChanged(object sender, TextChangedEventArgs e)
        => FilteredLanguages.Refresh();

    private void LanguageList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageList.SelectedItem is not LanguageRow row)
        {
            return;
        }

        _localization.SelectLanguage(row.CultureName);
        LanguageList.SelectedItem = null;
        LanguagePopup.IsOpen = false;
        LanguageSearchBox.Clear();
    }

    private void LanguageSelector_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !LanguagePopup.IsOpen)
        {
            return;
        }

        LanguagePopup.IsOpen = false;
        LanguageButton.Focus();
        e.Handled = true;
    }

    private bool MatchesSearch(object value)
    {
        if (value is not LanguageRow row)
        {
            return false;
        }

        var query = LanguageSearchBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return row.NativeName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || row.EnglishName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.CultureName.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void Localization_OnCultureChanged(object? sender, EventArgs e)
    {
        RebuildRows();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentFlagCode)));
    }

    private void RebuildRows()
    {
        var activeCulture = _localization.CurrentLanguage.CultureName;
        _rows.Clear();

        foreach (var language in _localization.SupportedLanguages)
        {
            _rows.Add(new LanguageRow(
                language.CultureName,
                language.NativeName,
                language.EnglishName,
                language.FlagCode,
                string.Equals(language.CultureName, activeCulture, StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible
                    : Visibility.Collapsed));
        }
    }

    public sealed record LanguageRow(
        string CultureName,
        string NativeName,
        string EnglishName,
        string FlagCode,
        Visibility CheckmarkVisibility);
}
