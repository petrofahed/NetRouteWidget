using System.Windows;
using System.Windows.Controls;
using NetRoute.Core;

namespace NetRoute.App;

public partial class AdapterPickerWindow : Window
{
    /// Value is what gets stored: the description for the phone, the MAC for the LAN. Null = automatic.
    sealed record Choice(string Label, string? Value);

    public AdapterPickerWindow(IReadOnlyList<AdapterInfo> candidates, AdapterOverrides current)
    {
        InitializeComponent();
        Fill(PhoneBox, candidates.Select(a => new Choice(Label(a), a.Description)), current.PhoneDescription);
        Fill(LanBox, candidates.Select(a => new Choice(Label(a), a.Mac)), current.LanMac);
    }

    public AdapterOverrides Result { get; private set; } = AdapterOverrides.None;

    static string Label(AdapterInfo a) => $"{a.Name} — {a.Description}{(a.IsUp ? "" : " (disconnected)")}";

    /// Keeps a saved choice selectable even when that adapter is not plugged in right now.
    static void Fill(ComboBox box, IEnumerable<Choice> adapters, string? current)
    {
        var choices = new List<Choice> { new("Automatic", null) };
        choices.AddRange(adapters);
        var selected = choices.FirstOrDefault(c =>
            c.Value is not null && string.Equals(c.Value, current, StringComparison.OrdinalIgnoreCase));
        if (selected is null && current is not null)
        {
            selected = new Choice($"{current} (not connected)", current);
            choices.Add(selected);
        }
        box.ItemsSource = choices;
        box.SelectedItem = selected ?? choices[0];
    }

    void OnOk(object sender, RoutedEventArgs e)
    {
        Result = new AdapterOverrides(((Choice)PhoneBox.SelectedItem).Value, ((Choice)LanBox.SelectedItem).Value);
        DialogResult = true;
    }
}
