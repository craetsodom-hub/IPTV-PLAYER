using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace IptvPlayer.App.Controls;

public partial class InlineSearchField : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(InlineSearchField),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty CloseCommandProperty = DependencyProperty.Register(
        nameof(CloseCommand),
        typeof(ICommand),
        typeof(InlineSearchField));

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder),
        typeof(string),
        typeof(InlineSearchField),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ClearToolTipProperty = DependencyProperty.Register(
        nameof(ClearToolTip),
        typeof(string),
        typeof(InlineSearchField),
        new PropertyMetadata(string.Empty));

    public InlineSearchField()
    {
        InitializeComponent();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public ICommand? CloseCommand
    {
        get => (ICommand?)GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    public string ClearToolTip
    {
        get => (string)GetValue(ClearToolTipProperty);
        set => SetValue(ClearToolTipProperty, value);
    }

    private void InlineSearchField_OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!IsVisible)
            {
                return;
            }

            SearchTextBox.Focus();
            Keyboard.Focus(SearchTextBox);
            SearchTextBox.SelectAll();
        });
    }
}
