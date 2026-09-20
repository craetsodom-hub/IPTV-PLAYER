using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace IptvPlayer.App.Controls;

public partial class AppearanceSelector : UserControl
{
    public AppearanceSelector()
    {
        InitializeComponent();
        Loaded += AppearanceSelector_OnLoaded;
        Unloaded += AppearanceSelector_OnUnloaded;
    }

    private bool _isCoordinatorSubscribed;
    private Window? _ownerWindow;

    private void AppearanceSelector_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isCoordinatorSubscribed)
        {
            return;
        }

        HeaderPopoverCoordinator.PopoverOpened += HeaderPopoverCoordinator_OnPopoverOpened;
        InputManager.Current.PreProcessInput += InputManager_OnPreProcessInput;
        _ownerWindow = Window.GetWindow(this);
        if (_ownerWindow is not null)
        {
            _ownerWindow.Deactivated += OwnerWindow_OnDeactivated;
        }

        _isCoordinatorSubscribed = true;
    }

    private void AppearanceSelector_OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_isCoordinatorSubscribed)
        {
            return;
        }

        HeaderPopoverCoordinator.PopoverOpened -= HeaderPopoverCoordinator_OnPopoverOpened;
        InputManager.Current.PreProcessInput -= InputManager_OnPreProcessInput;
        if (_ownerWindow is not null)
        {
            _ownerWindow.Deactivated -= OwnerWindow_OnDeactivated;
            _ownerWindow = null;
        }

        AppearancePopup.IsOpen = false;
        _isCoordinatorSubscribed = false;
    }

    private void SettingsButton_OnClick(object sender, RoutedEventArgs e)
        => AppearancePopup.IsOpen = !AppearancePopup.IsOpen;

    private void AppearancePopup_OnOpened(object? sender, EventArgs e)
    {
        SettingsButton.Tag = true;
        HeaderPopoverCoordinator.NotifyOpened(this);
        PopupTitle.Focus();
    }

    private void AppearancePopup_OnClosed(object? sender, EventArgs e)
        => SettingsButton.Tag = false;

    private CustomPopupPlacement[] AppearancePopup_OnCustomPlacement(
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
            AppearancePopup.IsOpen = false;
        }
    }

    private void OwnerWindow_OnDeactivated(object? sender, EventArgs e)
        => AppearancePopup.IsOpen = false;

    private void InputManager_OnPreProcessInput(object sender, PreProcessInputEventArgs e)
    {
        if (!AppearancePopup.IsOpen)
        {
            return;
        }

        if (e.StagingItem.Input is KeyEventArgs { Key: Key.Escape } keyEvent)
        {
            AppearancePopup.IsOpen = false;
            SettingsButton.Focus();
            keyEvent.Handled = true;
            return;
        }

        if (e.StagingItem.Input is not MouseButtonEventArgs mouseEvent
            || mouseEvent.ButtonState != MouseButtonState.Pressed
            || SettingsButton.IsMouseOver
            || AppearancePopup.Child is UIElement popupChild && popupChild.IsMouseOver)
        {
            return;
        }

        AppearancePopup.IsOpen = false;
    }

    private void AppearanceSelector_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !AppearancePopup.IsOpen)
        {
            return;
        }

        AppearancePopup.IsOpen = false;
        SettingsButton.Focus();
        e.Handled = true;
    }
}
