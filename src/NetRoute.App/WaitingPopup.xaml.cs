using System.Windows;
using NetRoute.Core;

namespace NetRoute.App;

/// Tray-anchored popup with real buttons (tray balloons can't have buttons).
/// Sits above the card when it is visible so it never covers it; otherwise bottom-right of the work area.
public partial class WaitingPopup : Window
{
    public WaitingPopup(WaitingTarget target, Bounds? cardBounds = null)
    {
        InitializeComponent();
        Target = target;
        Message.Text = SmartRoutingPresenter.WaitingText(target);
        UseMainButton.Content = SmartRoutingPresenter.UseMainText(target);
        WarningText.Text = Connections.IsMetered(target.Main) ? "Uses mobile data." : "";
        Loaded += (_, _) =>
        {
            var work = SystemParameters.WorkArea;
            (Left, Top) = CardPlacement.PopupAboveCard(
                cardBounds, ActualWidth, ActualHeight, new Bounds(work.Left, work.Top, work.Width, work.Height));
        };
    }

    public WaitingTarget Target { get; }
    public event Action? UseMainClicked;
    public event Action? KeepWaitingClicked;

    public void SetText(string text) => Message.Text = text;

    void OnUsePhone(object sender, RoutedEventArgs e) { UseMainClicked?.Invoke(); Close(); }

    void OnKeepWaiting(object sender, RoutedEventArgs e) { KeepWaitingClicked?.Invoke(); Close(); }
}
