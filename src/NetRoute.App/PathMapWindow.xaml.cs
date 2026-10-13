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
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    string? _selected;
    const string PausedFooter = "Display paused. Checks continue in the background.";
    bool _paused;
    string _diagnosisKey = "";

    public PathMapWindow(Func<string?, PathMapView> present)
    {
        InitializeComponent();
        Theme.Track(this);
        _present = present;
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
        };
        Loaded += (_, _) =>
        {
            Refresh();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
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
        view.Lan.Nodes.Concat(view.Phone.Nodes)
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

    /// Latency over the window. A lost check is a red tick on the baseline; a check that could not be measured is a grey one.
    void DrawSpark(IReadOnlyList<Sample> series)
    {
        Spark.Children.Clear();
        if (series.Count == 0) return;
        Brush Resource(string key) => (Brush)(TryFindResource(key) ?? Brushes.Gray);

        var values = series.Where(s => s.Outcome == ProbeOutcome.Reply && s.RttMs is not null).Select(s => s.RttMs!.Value).ToList();
        var max = Math.Max(1, values.Count == 0 ? 1 : values.Max()) * 1.15;
        var step = series.Count > 1 ? (Spark.Width - 10) / (series.Count - 1) : 0;
        Spark.Children.Add(new Line { X1 = 0, X2 = Spark.Width, Y1 = 44, Y2 = 44, Stroke = Resource("CardBorder"), StrokeThickness = 1 });

        var points = new PointCollection();
        void Flush()
        {
            if (points.Count > 1) Spark.Children.Add(new Polyline { Points = points, Stroke = Resource("Accent"), StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round });
            else if (points.Count == 1) Spark.Children.Add(new Ellipse { Width = 3, Height = 3, Fill = Resource("Accent"), Margin = new Thickness(points[0].X - 1.5, points[0].Y - 1.5, 0, 0) });
            points = new PointCollection();
        }

        for (var i = 0; i < series.Count; i++)
        {
            var x = 5 + i * step;
            switch (series[i].Outcome)
            {
                case ProbeOutcome.Reply:
                    points.Add(new Point(x, 44 - (series[i].RttMs ?? 0) / max * 38));
                    break;
                default:
                    Flush();
                    Spark.Children.Add(new Line
                    {
                        X1 = x, X2 = x, Y1 = 46, Y2 = 54, StrokeThickness = 2,
                        Stroke = Resource(series[i].Outcome == ProbeOutcome.NoReply ? "DotRed" : "DotGray"),
                    });
                    break;
            }
        }
        Flush();
    }

    void OnPause(object sender, RoutedEventArgs e)
    {
        _paused = !_paused;
        Map.Paused = _paused;
        PauseButton.Content = _paused ? "Resume display" : "Pause display";
        if (_paused) Footer.Text = PausedFooter;
        else Refresh();
    }
}
