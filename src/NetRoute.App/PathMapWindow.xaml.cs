using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NetRoute.Core.PathMap;

namespace NetRoute.App;

/// The network path window. It owns only the screen: a timer asks for the latest view every 500 ms (the probes run on their own
/// schedule inside the PathMonitor), and clicking a device selects it for the detail panel.
public partial class PathMapWindow : Window
{
    readonly Func<string?, PathMapView> _present;
    readonly LocalNetworkTabServices _local;
    bool _localStarted;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    string? _selected;
    const string PausedFooter = "Display paused. Checks continue in the background.";
    bool _paused;
    string _diagnosisKey = "";

    public PathMapWindow(Func<string?, PathMapView> present, LocalNetworkTabServices local)
    {
        InitializeComponent();
        Theme.Track(this);
        _present = present;
        _local = local;
        LocalTab.Attach(local);
        Map.NodeClicked += id =>
        {
            _selected = id;
            Refresh();
        };
        _timer.Tick += (_, _) =>
        {
            if (!_paused) Refresh();
            else
            {
                // Paused: nothing is drawn, but the monitor keeps following the adapters (the present callback updates them).
                try { _present(_selected); }
                catch (Exception) { }
            }
            if (LocalTabItem.IsSelected) LocalTab.Refresh();
        };
        Loaded += (_, _) =>
        {
            Refresh();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != Tabs || !LocalTabItem.IsSelected) return; // ignore selection events bubbling up from inside a tab
        if (!_localStarted)
        {
            _localStarted = true;
            _local.Start(); // the checks start the first time the tab is shown and stop when the window closes
        }
        LocalTab.Refresh();
    }

    void Refresh()
    {
        PathMapView view;
        try
        {
            view = _present(_selected);
            // With nothing selected yet, show the first device that looks like trouble.
            if (_selected is null && FirstProblem(view) is { } id)
            {
                _selected = id;
                view = _present(id);
            }
        }
        catch (Exception)
        {
            // Keep the last view on screen; a throw out of a timer tick would log every 500 ms.
            Footer.Text = "Could not update the map";
            return;
        }
        // A selection that no longer resolves to anything is dropped, so the first-problem pick can re-arm.
        if (_selected is not null && view.Detail is null) _selected = null;

        Headline.Text = "🌐 " + view.Headline;
        Headline.SetResourceReference(TextBlock.ForegroundProperty, view.HeadlineWarning ? "Warning" : "TextPrimary");
        // A click while paused still redraws (the user asked), but the footer keeps saying the display is paused.
        Footer.Text = _paused ? PausedFooter : view.FooterText;
        ReachabilityTitle.Text = view.ReachabilityTitle;
        RenderDiagnosis(view.Diagnosis);
        Map.Render(view, _selected);
        RenderDetail(view.Detail);
    }

    static string? FirstProblem(PathMapView view) =>
        view.Lanes.SelectMany(l => l.Nodes)
            .FirstOrDefault(n => n.State is HopDisplay.Broken or HopDisplay.Suspect)?.Id;

    void RenderDiagnosis(IReadOnlyList<string> lines)
    {
        var key = string.Join("\n", lines);
        if (key == _diagnosisKey) return;
        _diagnosisKey = key;
        Diagnosis.Children.Clear();
        foreach (var line in lines)
            Diagnosis.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2) });
    }

    void RenderDetail(DetailView? detail)
    {
        DetailHint.Visibility = detail is null ? Visibility.Visible : Visibility.Collapsed;
        DetailBody.Visibility = detail is null ? Visibility.Collapsed : Visibility.Visible;
        if (detail is null) return;

        DetailTitle.Text = detail.Title;
        DetailAddress.Text = detail.Address;
        DetailChip.Text = detail.Chip;
        var brush = PathMapCanvas.StateBrush(detail.State);
        DetailChip.SetResourceReference(TextBlock.ForegroundProperty, brush);
        DetailChipBox.SetResourceReference(Border.BorderBrushProperty, brush);

        DetailStats.Children.Clear();
        foreach (var (label, value) in detail.Stats)
        {
            var stat = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
            var caption = new TextBlock { Text = label, FontSize = 11.5 };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            stat.Children.Add(caption);
            stat.Children.Add(new TextBlock { Text = value, FontSize = 14, FontWeight = FontWeights.SemiBold });
            DetailStats.Children.Add(stat);
        }

        DrawSpark(detail.Series);
        Spark.Visibility = detail.Series.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        DetailNote.Text = detail.Note;
    }

    /// Latency over the window (see SparkLine).
    void DrawSpark(IReadOnlyList<Sample> series) =>
        SparkLine.Draw(Spark, series, key => (Brush)(TryFindResource(key) ?? Brushes.Gray));

    void OnPause(object sender, RoutedEventArgs e)
    {
        _paused = !_paused;
        Map.Paused = _paused;
        PauseButton.Content = _paused ? "Resume display" : "Pause display";
        if (_paused) Footer.Text = PausedFooter;
        else Refresh();
    }
}
