using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Shapes;
using Microsoft.Win32;
using NetRoute.Core;

namespace NetRoute.App;

public partial class CardWindow : Window
{
    RoutingMode _mode;
    bool _allowClose;

    public CardWindow()
    {
        InitializeComponent();
        Theme.Apply(Resources);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public event Action<RoutingMode>? ModeRequested;
    public event Action? HideRequested;
    public event Action<double, double>? Moved;
    public event Action? StartWithWindowsToggled;
    public event Action? ChooseAdaptersRequested;
    public event Action? OpenNetworkSettingsRequested;
    public event Action? RestartAsAdminRequested;
    public event Action? QuitRequested;
    public event Action? SmartSettingsRequested;
    public event Action? SpeedTestRequested;

    /// Shows the card at its saved spot, or bottom-right when that spot is no longer on screen.
    public void ShowAndPlace(double? savedLeft, double? savedTop)
    {
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
            UpdateLayout();
            var virtualScreen = new Bounds(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var work = SystemParameters.WorkArea;
            (Left, Top) = CardPlacement.Resolve(savedLeft, savedTop, ActualWidth, ActualHeight,
                virtualScreen, new Bounds(work.Left, work.Top, work.Width, work.Height));
            Opacity = 1;
        }
        Activate();
    }

    public void Render(CardView view, RoutingMode mode, bool canModify, bool startWithWindows)
    {
        _mode = mode;
        Header.Text = "🌐 " + view.Header;
        Header.SetResourceReference(TextBlock.ForegroundProperty, view.HeaderWarning ? "Warning" : "TextPrimary");
        Note.Text = view.Note ?? "";
        Note.Visibility = view.Note is null ? Visibility.Collapsed : Visibility.Visible;
        RenderRow(view.Phone, PhoneDot, PhoneLabel, PhoneAddress, PhoneLatency);
        RenderRow(view.Lan, LanDot, LanLabel, LanAddress, LanLatency);
        SyncModeButtons();
        PhoneButton.IsEnabled = LanButton.IsEnabled = AutoButton.IsEnabled = canModify;
        AdminHint.Visibility = canModify ? Visibility.Collapsed : Visibility.Visible;
        StartWithWindowsItem.IsChecked = startWithWindows;
        StartWithWindowsItem.IsEnabled = canModify;
    }

    public void RenderSmart(SmartRow row)
    {
        SmartText.Text = row.Text;
        SmartText.SetResourceReference(TextBlock.ForegroundProperty, row.Tone == SmartTone.Warning ? "Warning" : "TextSecondary");
        SmartText.Opacity = row.Tone == SmartTone.Muted ? 0.7 : 1.0;
    }

    /// The live speed through each exit, left of its latency. An empty text hides the figure (the row looks as before).
    /// Only touches a TextBlock when its text changed: this is called every second.
    public void RenderSpeeds(string phone, string lan)
    {
        SetSpeed(PhoneSpeed, phone);
        SetSpeed(LanSpeed, lan);
    }

    static void SetSpeed(TextBlock block, string text)
    {
        if (block.Text != text) block.Text = text;
        var visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (block.Visibility != visibility) block.Visibility = visibility;
    }

    public void ShowSpeed(string text)
    {
        SpeedText.Text = text;
        SpeedText.Visibility = Visibility.Visible;
    }

    /// Closes for real on app exit; any other close (Alt+F4) just hides to the tray.
    public void ForceClose()
    {
        _allowClose = true;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            HideRequested?.Invoke();
        }
        base.OnClosing(e);
    }

    static void RenderRow(AdapterRow row, Ellipse dot, TextBlock label, TextBlock address, TextBlock latency)
    {
        dot.SetResourceReference(Shape.FillProperty, row.Dot switch
        {
            Dot.Green => "DotGreen",
            Dot.Amber => "DotAmber",
            _ => "DotGray",
        });
        label.Text = row.Label;
        label.FontWeight = row.IsActive ? FontWeights.Bold : FontWeights.Normal;
        address.Text = row.Address;
        latency.Text = row.Latency;
    }

    void SyncModeButtons()
    {
        PhoneButton.IsChecked = _mode == RoutingMode.Phone;
        LanButton.IsChecked = _mode == RoutingMode.Lan;
        AutoButton.IsChecked = _mode == RoutingMode.Auto;
    }

    void OnMode(object sender, RoutedEventArgs e)
    {
        var mode = Enum.Parse<RoutingMode>((string)((FrameworkElement)sender).Tag);
        SyncModeButtons(); // undo the click's own toggle; Render shows the new mode once applied
        ModeRequested?.Invoke(mode);
    }

    void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        DragMove();
        Moved?.Invoke(Left, Top);
    }

    void OnMenu(object sender, RoutedEventArgs e)
    {
        OptionsMenu.PlacementTarget = MenuButton;
        OptionsMenu.Placement = PlacementMode.Bottom;
        OptionsMenu.IsOpen = true;
    }

    void OnHide(object sender, RoutedEventArgs e) => HideRequested?.Invoke();

    void OnStartWithWindows(object sender, RoutedEventArgs e) => StartWithWindowsToggled?.Invoke();

    void OnChooseAdapters(object sender, RoutedEventArgs e) => ChooseAdaptersRequested?.Invoke();

    void OnOpenNetworkSettings(object sender, RoutedEventArgs e) => OpenNetworkSettingsRequested?.Invoke();

    void OnRestartAsAdmin(object sender, RoutedEventArgs e) => RestartAsAdminRequested?.Invoke();

    void OnQuit(object sender, RoutedEventArgs e) => QuitRequested?.Invoke();

    void OnSmartSettings(object sender, RoutedEventArgs e) => SmartSettingsRequested?.Invoke();

    void OnSmartTextClick(object sender, MouseButtonEventArgs e) => SmartSettingsRequested?.Invoke();

    void OnSpeedTest(object sender, RoutedEventArgs e) => SpeedTestRequested?.Invoke();

    void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General) Dispatcher.BeginInvoke(new Action(() => Theme.Apply(Resources)));
    }
}
