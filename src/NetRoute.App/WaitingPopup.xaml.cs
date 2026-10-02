using System.Windows;
using NetRoute.Core;

namespace NetRoute.App;

/// Tray-anchored popup with real buttons (tray balloons can't have buttons).
public partial class WaitingPopup : Window
{
    public WaitingPopup(string text)
    {
        InitializeComponent();
        Message.Text = text;
        Loaded += (_, _) =>
        {
            var work = SystemParameters.WorkArea;
            Left = work.Right - ActualWidth - CardPlacement.Margin;
            Top = work.Bottom - ActualHeight - CardPlacement.Margin;
        };
    }

    public event Action? UsePhoneClicked;
    public event Action? KeepWaitingClicked;

    public void SetText(string text) => Message.Text = text;

    void OnUsePhone(object sender, RoutedEventArgs e) { UsePhoneClicked?.Invoke(); Close(); }

    void OnKeepWaiting(object sender, RoutedEventArgs e) { KeepWaitingClicked?.Invoke(); Close(); }
}
