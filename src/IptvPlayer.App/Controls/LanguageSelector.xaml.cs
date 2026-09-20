using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using IptvPlayer.Presentation.Localization;

namespace IptvPlayer.App.Controls;

public partial class LanguageSelector : UserControl, INotifyPropertyChanged
{
    private readonly UiLocalization _localization = UiLocalization.Current;
    private readonly ObservableCollection<LanguageRow> _rows = [];
    private bool _isSubscribed;
    private Window? _ownerWindow;

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
        HeaderPopoverCoordinator.PopoverOpened += HeaderPopoverCoordinator_OnPopoverOpened;
        InputManager.Current.PreProcessInput += InputManager_OnPreProcessInput;
        _ownerWindow = Window.GetWindow(this);
        if (_ownerWindow is not null)
        {
            _ownerWindow.Deactivated += OwnerWindow_OnDeactivated;
        }

        _isSubscribed = true;
    }

    private void LanguageSelector_OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_isSubscribed)
        {
            return;
        }

        _localization.CultureChanged -= Localization_OnCultureChanged;
        HeaderPopoverCoordinator.PopoverOpened -= HeaderPopoverCoordinator_OnPopoverOpened;
        InputManager.Current.PreProcessInput -= InputManager_OnPreProcessInput;
        if (_ownerWindow is not null)
        {
            _ownerWindow.Deactivated -= OwnerWindow_OnDeactivated;
            _ownerWindow = null;
        }

        LanguagePopup.IsOpen = false;
        _isSubscribed = false;
    }

    private void LanguageButton_OnClick(object sender, RoutedEventArgs e)
        => LanguagePopup.IsOpen = !LanguagePopup.IsOpen;

    private void LanguagePopup_OnOpened(object? sender, EventArgs e)
    {
        LanguageButton.Tag = true;
        HeaderPopoverCoordinator.NotifyOpened(this);
        LanguageSearchBox.Focus();
        LanguageSearchBox.SelectAll();
    }

    private void LanguagePopup_OnClosed(object? sender, EventArgs e)
        => LanguageButton.Tag = false;

    private CustomPopupPlacement[] LanguagePopup_OnCustomPlacement(
        Size popupSize,
        Size targetSize,
        Point offset)
        =>
        [
            new CustomPopupPlacement(
                new Point(targetSize.Width - popupSize.Width, targetSize.Height + 3),
                PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(
                new Point(0, targetSize.Height + 3),
                PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(
                new Point(targetSize.Width - popupSize.Width, -popupSize.Height - 3),
                PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(
                new Point(0, -popupSize.Height - 3),
                PopupPrimaryAxis.Horizontal),
        ];

    private void HeaderPopoverCoordinator_OnPopoverOpened(object owner)
    {
        if (!ReferenceEquals(owner, this))
        {
            LanguagePopup.IsOpen = false;
        }
    }

    private void OwnerWindow_OnDeactivated(object? sender, EventArgs e)
        => LanguagePopup.IsOpen = false;

    private void InputManager_OnPreProcessInput(object sender, PreProcessInputEventArgs e)
    {
        if (!LanguagePopup.IsOpen)
        {
            return;
        }

        if (e.StagingItem.Input is KeyEventArgs { Key: Key.Escape } keyEvent)
        {
            LanguagePopup.IsOpen = false;
            LanguageButton.Focus();
            keyEvent.Handled = true;
            return;
        }

        if (e.StagingItem.Input is not MouseButtonEventArgs mouseEvent
            || mouseEvent.ButtonState != MouseButtonState.Pressed
            || LanguageButton.IsMouseOver
            || LanguagePopup.Child is UIElement popupChild && popupChild.IsMouseOver)
        {
            return;
        }

        LanguagePopup.IsOpen = false;
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
