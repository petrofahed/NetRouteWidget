using System.Windows;
using System.Windows.Controls;
using NetRoute.Core;

namespace NetRoute.App;

public partial class AdapterPickerWindow : Window
{
    /// Value is what gets stored: the description for the phone, the MAC for LAN/Wi-Fi.
    /// "none" (AdapterOverrides.NotUsed) is the explicit "Not used" entry.
    sealed record Choice(string Label, string? Value, AdapterInfo? Adapter);

    readonly IReadOnlyList<AdapterInfo> _candidates;
    readonly INetworkNameSource _names;

    public AdapterPickerWindow(IReadOnlyList<AdapterInfo> candidates, AdapterOverrides current, INetworkNameSource names)
    {
        InitializeComponent();
        Theme.Track(this);
        _candidates = candidates;
        _names = names;

        var suggestion = AdapterDetector.Suggest(candidates);

        Fill(PhoneBox, a => a.Description, current.PhoneDescription ?? suggestion.PhoneDescription);
        Fill(LanBox, a => a.Mac, current.LanMac ?? suggestion.LanMac);
        Fill(WifiAdapterBox, a => a.Mac, current.WifiMac ?? AdapterOverrides.NotUsed);

        UpdateDetail(PhoneDetail, ConnectionId.Phone, (Choice)PhoneBox.SelectedItem);
        UpdateDetail(LanDetail, ConnectionId.Lan, (Choice)LanBox.SelectedItem);
        UpdateWifiDetail((Choice)WifiAdapterBox.SelectedItem);
    }

    public AdapterOverrides Result { get; private set; } = AdapterOverrides.None;

    static string Label(AdapterInfo a) => $"{a.Name} — {a.Description}{(a.IsUp ? "" : " (disconnected)")}";

    /// Keeps a saved choice selectable even when that adapter is not plugged in right now.
    void Fill(ComboBox box, Func<AdapterInfo, string> valueOf, string? current)
    {
        var choices = new List<Choice> { new("Not used", AdapterOverrides.NotUsed, null) };
        choices.AddRange(_candidates.Select(a => new Choice(Label(a), valueOf(a), a)));

        Choice? selected;
        if (current == AdapterOverrides.NotUsed || current is null)
        {
            selected = choices[0];
        }
        else
        {
            selected = choices.FirstOrDefault(c =>
                c.Adapter is not null && string.Equals(c.Value, current, StringComparison.OrdinalIgnoreCase));
            if (selected is null)
            {
                selected = new Choice($"{current} (not connected)", current, null);
                choices.Add(selected);
            }
        }

        box.ItemsSource = choices;
        box.SelectedItem = selected;
    }

    void OnPhoneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PhoneBox.SelectedItem is not Choice choice) return;
        ErrorText.Visibility = Visibility.Collapsed;
        UpdateDetail(PhoneDetail, ConnectionId.Phone, choice);
    }

    void OnLanChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanBox.SelectedItem is not Choice choice) return;
        ErrorText.Visibility = Visibility.Collapsed;
        UpdateDetail(LanDetail, ConnectionId.Lan, choice);
    }

    void OnWifiChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WifiAdapterBox.SelectedItem is not Choice choice) return;
        ErrorText.Visibility = Visibility.Collapsed;
        UpdateWifiDetail(choice);
    }

    static void UpdateDetail(TextBlock detail, ConnectionId slot, Choice choice) =>
        detail.Text = choice.Adapter is { } adapter ? AdapterDetector.DetailLine(adapter, slot) : "Not used";

    void UpdateWifiDetail(Choice choice)
    {
        if (choice.Adapter is not { } adapter)
        {
            WifiDetail.Text = "Not used";
            WifiNetworkBox.ItemsSource = new[] { new Choice("not connected", null, null) };
            WifiNetworkBox.SelectedIndex = 0;
            return;
        }

        WifiDetail.Text = AdapterDetector.DetailLine(adapter, ConnectionId.Wifi);

        string networkName;
        try { networkName = _names.NameOf(adapter) ?? "not connected"; }
        catch { networkName = "not connected"; }

        WifiNetworkBox.ItemsSource = new[] { new Choice(networkName, null, null) };
        WifiNetworkBox.SelectedIndex = 0;
    }

    void OnDetect(object sender, RoutedEventArgs e)
    {
        var suggestion = AdapterDetector.Suggest(_candidates);
        Select(PhoneBox, suggestion.PhoneDescription);
        Select(LanBox, suggestion.LanMac);
        Select(WifiAdapterBox, suggestion.WifiMac);
    }

    static void Select(ComboBox box, string? value)
    {
        var choices = (IReadOnlyList<Choice>)box.ItemsSource;
        box.SelectedItem = choices.FirstOrDefault(c =>
            string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase)) ?? choices[0];
    }

    void OnOk(object sender, RoutedEventArgs e)
    {
        var phone = (Choice)PhoneBox.SelectedItem;
        var lan = (Choice)LanBox.SelectedItem;
        var wifi = (Choice)WifiAdapterBox.SelectedItem;

        // Identity by live adapter index when one is selected (so Phone's description-keyed
        // choice and LAN/Wi-Fi's MAC-keyed choice still collide correctly when they're the same
        // physical adapter), or by the raw saved value for a stale "(not connected)" choice that
        // has no live AdapterInfo to key by. "Not used"/null never collides with anything.
        var identities = new[] { phone, lan, wifi }
            .Select(Identity)
            .Where(id => id is not null)
            .ToList();
        if (identities.Count != identities.Distinct().Count())
        {
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        ErrorText.Visibility = Visibility.Collapsed;
        Result = new AdapterOverrides(phone.Value, lan.Value, wifi.Value);
        DialogResult = true;
    }

    static string? Identity(Choice choice) => choice switch
    {
        { Adapter: { } adapter } => $"idx:{adapter.Index}",
        { Value: { } value } when value != AdapterOverrides.NotUsed => $"val:{value.ToUpperInvariant()}",
        _ => null,
    };
}
