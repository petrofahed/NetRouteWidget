using System.Windows;
using NetRoute.Core;

namespace NetRoute.App;

/// Tray-anchored popup with real buttons (tray balloons can't have buttons).
/// Sits above the card when it is visible so it never covers it; otherwise bottom-right of the work area.
public partial class WaitingPopup : Window
{
    public WaitingPopup(string text, Bounds? cardBounds = null)
    {
        InitializeComponent();
        Message.Text = text;
        Loaded += (_, _) =>
        {
            var work = SystemParameters.WorkArea;
            (Left, Top) = CardPlacement.PopupAboveCard(
                cardBounds, ActualWidth, ActualHeight, new Bounds(work.Left, work.Top, work.Width, work.Height));
        };
    }

    public event Action? UsePhoneClicked;
    public event Action? KeepWaitingClicked;

    public void SetText(string text) => Message.Text = text;

    void OnUsePhone(object sender, RoutedEventArgs e) { UsePhoneClicked?.Invoke(); Close(); }

    void OnKeepWaiting(object sender, RoutedEventArgs e) { KeepWaitingClicked?.Invoke(); Close(); }
}
