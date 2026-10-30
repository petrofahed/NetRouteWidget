using System.ComponentModel;
using System.Linq;
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
    /// One connection's named row elements: the row's own height (collapsed to 0 when the connection isn't used,
    /// since a fixed RowDefinition height doesn't shrink just because its cells are Collapsed) plus its four
    /// always-rendered cells. Speed/Total are handled separately (RenderSpeeds/RenderTotals; see below).
    /// DotHit wraps Dot: the larger click area of the row's on/off switch.
    sealed record RowControls(RowDefinition RowDef, Border DotHit, Ellipse Dot, TextBlock Label, TextBlock Address, TextBlock Latency, TextBlock Speed, TextBlock Total);

    readonly Dictionary<ConnectionId, RowControls> _rows;
    /// Which shown rows are turned off, from the last Render: a dot click asks for the opposite.
    readonly HashSet<ConnectionId> _off = [];
    RoutingMode _mode;
    bool _allowClose;
    bool _wifiShown;

    public CardWindow()
    {
        InitializeComponent();
        _rows = new Dictionary<ConnectionId, RowControls>
        {
            [ConnectionId.Phone] = new(PhoneRowDef, PhoneDotHit, PhoneDot, PhoneLabel, PhoneAddress, PhoneLatency, PhoneSpeed, PhoneTotal),
            [ConnectionId.Lan] = new(LanRowDef, LanDotHit, LanDot, LanLabel, LanAddress, LanLatency, LanSpeed, LanTotal),
            [ConnectionId.Wifi] = new(WifiRowDef, WifiDotHit, WifiDot, WifiLabel, WifiAddress, WifiLatency, WifiSpeed, WifiTotal),
        };
        var version = typeof(CardWindow).Assembly.GetName().Version;
        if (version is not null) VersionText.Text = $"v{version.Major}.{version.Minor}.{version.Build}";
        Theme.Apply(Resources);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public event Action<RoutingMode>? ModeRequested;
    /// A row's dot was clicked: turn that connection off (true) or back on (false).
    public event Action<ConnectionId, bool>? TurnedOffRequested;
    public event Action? HideRequested;
    public event Action<double, double>? Moved;
    public event Action? StartWithWindowsToggled;
    public event Action? AutoHealToggled;
    public event Action? ChooseAdaptersRequested;
    public event Action? OpenNetworkSettingsRequested;
    public event Action? RestartAsAdminRequested;
    public event Action? QuitRequested;
    public event Action? SmartSettingsRequested;
    public event Action<bool>? SmartToggleRequested;
    public event Action? SpeedTestRequested;
    public event Action? PathMapRequested;

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

    public void Render(CardView view, RoutingMode mode, bool canModify, bool startWithWindows, bool autoHeal)
    {
        _mode = mode;
        Header.Text = "🌐 " + view.Header;
        Header.SetResourceReference(TextBlock.ForegroundProperty, view.HeaderWarning ? "Warning" : "TextPrimary");
        Note.Text = view.Note ?? "";
        Note.Visibility = view.Note is null ? Visibility.Collapsed : Visibility.Visible;
        _off.Clear();
        foreach (var id in Connections.CardOrder)
        {
            var controls = _rows[id];
            var row = view.Rows.FirstOrDefault(r => r.Id == id);
            if (row is not null)
            {
                RenderRow(row.Row, controls.Dot, controls.Label, controls.Address, controls.Latency);
                controls.DotHit.Visibility = controls.Label.Visibility = controls.Address.Visibility = controls.Latency.Visibility = Visibility.Visible;
                controls.DotHit.ToolTip = $"Turn {Connections.Label(id)} {(row.Row.IsOff ? "on" : "off")}";
                controls.Label.Opacity = controls.Address.Opacity = row.Row.IsOff ? 0.55 : 1.0;
                if (row.Row.IsOff) _off.Add(id);
                controls.RowDef.Height = new GridLength(22);
            }
            else
            {
                controls.DotHit.Visibility = controls.Label.Visibility = controls.Address.Visibility = controls.Latency.Visibility = Visibility.Collapsed;
                controls.RowDef.Height = new GridLength(0);
                // RenderSpeeds/RenderTotals run on their own cadence and won't revisit this row until it's
                // used again; clear any stale figure now so a hidden row can't leave a visible leftover.
                SetSpeed(controls.Speed, "");
                SetSpeed(controls.Total, "");
            }
        }
        _wifiShown = view.Rows.Any(r => r.Id == ConnectionId.Wifi);
        ModeGrid.Columns = _wifiShown ? 4 : 3;
        WifiButton.Visibility = _wifiShown ? Visibility.Visible : Visibility.Collapsed;
        SyncModeButtons();
        SmartButton.IsEnabled = canModify;
        // An off connection's mode button stays visible but cannot be picked until it is turned on again.
        PhoneButton.IsEnabled = canModify && !_off.Contains(ConnectionId.Phone);
        LanButton.IsEnabled = canModify && !_off.Contains(ConnectionId.Lan);
        WifiButton.IsEnabled = canModify && !_off.Contains(ConnectionId.Wifi);
        AdminHint.Visibility = canModify ? Visibility.Collapsed : Visibility.Visible;
        StartWithWindowsItem.IsChecked = startWithWindows;
        StartWithWindowsItem.IsEnabled = canModify;
        AutoHealItem.IsChecked = autoHeal;
        AutoHealItem.IsEnabled = canModify;
    }

    public void RenderSmart(SmartRow row, bool smartOn)
    {
        SmartButton.IsChecked = smartOn;
        SmartText.Text = row.Text;
        SmartText.SetResourceReference(TextBlock.ForegroundProperty, row.Tone == SmartTone.Warning ? "Warning" : "TextSecondary");
        SmartText.Opacity = row.Tone == SmartTone.Muted ? 0.7 : 1.0;
    }

    /// The live speed through each exit, left of its latency. An empty text hides the figure (the row looks as before).
    /// Only touches a TextBlock when its text changed: this is called every second.
    /// Wi-Fi has no speed source yet (v6.3): it shows "—" whenever phone or lan shows a real figure, else nothing.
    public void RenderSpeeds(string phone, string lan)
    {
        SetSpeed(PhoneSpeed, phone);
        SetSpeed(LanSpeed, lan);
        SetSpeed(WifiSpeed, WifiPlaceholder(phone, lan));
    }

    static void SetSpeed(TextBlock block, string text)
    {
        if (block.Text != text) block.Text = text;
        var visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (block.Visibility != visibility) block.Visibility = visibility;
    }

    /// Today's total data through each exit, right of its latency. An empty text hides the figure (the row looks as before).
    /// Wi-Fi has no totals source yet (v6.3): it shows "—" whenever phone or lan shows a real figure, else nothing.
    public void RenderTotals(string phone, string lan)
    {
        SetSpeed(PhoneTotal, phone);
        SetSpeed(LanTotal, lan);
        SetSpeed(WifiTotal, WifiPlaceholder(phone, lan));
    }

    /// "—" once Wi-Fi is a shown row and either sibling has a real figure; empty otherwise (Wi-Fi row hidden,
    /// or neither sibling has anything to show, e.g. before the first poll or while speeds aren't displayed).
    string WifiPlaceholder(string phone, string lan) => _wifiShown && (phone.Length > 0 || lan.Length > 0) ? "—" : "";

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
        WifiButton.IsChecked = _mode == RoutingMode.Wifi;
    }

    void OnMode(object sender, RoutedEventArgs e)
    {
        var mode = Enum.Parse<RoutingMode>((string)((FrameworkElement)sender).Tag);
        SyncModeButtons(); // undo the click's own toggle; Render shows the new mode once applied
        ModeRequested?.Invoke(mode);
    }

    /// The dot is the row's on/off switch. Handled here so the press does not also start dragging the card.
    void OnDotDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var id = Enum.Parse<ConnectionId>((string)((FrameworkElement)sender).Tag);
        TurnedOffRequested?.Invoke(id, !_off.Contains(id));
    }

    void OnSmart(object sender, RoutedEventArgs e)
    {
        var turnOn = SmartButton.IsChecked == true; // the click already toggled it
        SmartButton.IsChecked = !turnOn; // undo; RenderSmart shows the new state once applied
        SmartToggleRequested?.Invoke(turnOn);
    }

    void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        var (left, top) = (Left, Top);
        DragMove();
        Moved?.Invoke(Left, Top);
        // Pressing and releasing on the status line without moving the card is a click on it: open the path map.
        if (ReferenceEquals(e.OriginalSource, Header) && Left == left && Top == top) PathMapRequested?.Invoke();
    }

    void OnMenu(object sender, RoutedEventArgs e)
    {
        OptionsMenu.PlacementTarget = MenuButton;
        OptionsMenu.Placement = PlacementMode.Bottom;
        OptionsMenu.IsOpen = true;
    }

    void OnHide(object sender, RoutedEventArgs e) => HideRequested?.Invoke();

    void OnStartWithWindows(object sender, RoutedEventArgs e) => StartWithWindowsToggled?.Invoke();

    void OnAutoHeal(object sender, RoutedEventArgs e) => AutoHealToggled?.Invoke();

    void OnChooseAdapters(object sender, RoutedEventArgs e) => ChooseAdaptersRequested?.Invoke();

    void OnOpenNetworkSettings(object sender, RoutedEventArgs e) => OpenNetworkSettingsRequested?.Invoke();

    void OnRestartAsAdmin(object sender, RoutedEventArgs e) => RestartAsAdminRequested?.Invoke();

    void OnQuit(object sender, RoutedEventArgs e) => QuitRequested?.Invoke();

    void OnSmartSettings(object sender, RoutedEventArgs e) => SmartSettingsRequested?.Invoke();

    void OnSmartTextClick(object sender, MouseButtonEventArgs e) => SmartSettingsRequested?.Invoke();

    void OnSpeedTest(object sender, RoutedEventArgs e) => SpeedTestRequested?.Invoke();

    void OnPathMap(object sender, RoutedEventArgs e) => PathMapRequested?.Invoke();

    void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General) Dispatcher.BeginInvoke(new Action(() => Theme.Apply(Resources)));
    }
}
