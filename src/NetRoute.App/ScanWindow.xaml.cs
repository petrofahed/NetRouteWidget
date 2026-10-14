using System.Windows;
using NetRoute.Core.PathMap;

namespace NetRoute.App;

/// Scans the LAN adapter's subnet and lists what answered; the chosen device is returned for the add dialog. Closing the window
/// cancels a scan that is still running.
public partial class ScanWindow : Window
{
    readonly CancellationTokenSource _cts = new();
    readonly IReadOnlyList<LocalDevice> _existing;
    IReadOnlyList<ScanResult> _results = [];

    public ScanWindow(Func<IProgress<int>, CancellationToken, Task<IReadOnlyList<ScanResult>>> scan, IReadOnlyList<LocalDevice> existing)
    {
        InitializeComponent();
        Theme.Track(this);
        _existing = existing;
        Bar.Maximum = NetworkScanner.MaxHosts;
        Closed += (_, _) =>
        {
            _cts.Cancel();
            _cts.Dispose();
        };
        Loaded += async (_, _) => await RunAsync(scan);
    }

    public ScanResult? Result { get; private set; }

    async Task RunAsync(Func<IProgress<int>, CancellationToken, Task<IReadOnlyList<ScanResult>>> scan)
    {
        try
        {
            var progress = new Progress<int>(done => Bar.Value = done); // created on the UI thread: reports arrive there
            _results = await scan(progress, _cts.Token);
            Bar.Value = Bar.Maximum;
            List.ItemsSource = _results.Select(Describe).ToList();
            Status.Text = _results.Count == 0
                ? "No devices were found. Check that the LAN cable is connected."
                : $"{_results.Count} device(s) found. Pick one to add it.";
        }
        catch (OperationCanceledException)
        {
            // the window was closed
        }
        catch (Exception ex)
        {
            Status.Text = "Scan failed: " + ex.Message;
        }
    }

    string Describe(ScanResult r)
    {
        var added = _existing.Any(d => string.Equals(d.Address, r.Address.ToString(), StringComparison.OrdinalIgnoreCase)) ? "   (already in your list)" : "";
        var name = string.IsNullOrEmpty(r.Name) ? "" : $"   {r.Name}";
        var mac = r.Mac is null ? "" : $"   {r.Mac}";
        var quiet = r.AnsweredPing ? "" : "   (doesn't answer ping)";
        return $"{r.Address}{name}{mac}{quiet}{added}";
    }

    void OnSelect(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UseButton.IsEnabled = List.SelectedIndex >= 0;

    void OnUse(object sender, RoutedEventArgs e)
    {
        if (List.SelectedIndex < 0 || List.SelectedIndex >= _results.Count) return;
        Result = _results[List.SelectedIndex];
        DialogResult = true;
    }
}
