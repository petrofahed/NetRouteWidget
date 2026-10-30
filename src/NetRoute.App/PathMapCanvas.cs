using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NetRoute.Core.PathMap;

namespace NetRoute.App;

/// Draws the path map from a view model on a 980-wide design canvas, 380 high for up to two lanes and taller for more
/// (the window scales it with a Viewbox).
/// The shapes are rebuilt on every Render (a few dozen, twice a second). The flowing dashes and pulses are driven by the
/// render clock, not by Render, so they stay smooth across rebuilds and keep running while data is unchanged.
sealed class PathMapCanvas : Canvas
{
    public const double CanvasWidth = 980, CanvasHeight = 380;
    const double NodeW = 108, NodeH = 66, PcW = 132, PcH = 84, NetW = 132, NetH = 96;
    const double FirstLaneY = 108, DefaultMidY = 190, MinGap = 22;
    /// Up to two lanes keep the v5 layout (184 apart); a third packs them a little closer.
    const double TwoLaneSpacing = 184, ThreeLaneSpacing = 150, BelowLastLane = 88;
    /// What the This PC and Internet boxes grow by for each lane past the second (one more row each).
    const double PcRowGrowth = 18, NetRowGrowth = 22;
    const double DashPeriodUnits = 4.667; // one dash + gap in stroke-thickness units, for the flow speed

    readonly List<(Shape Shape, double Speed)> _flows = [];
    readonly List<Shape> _halos = [];
    readonly List<Ellipse> _rings = [];
    readonly List<FrameworkElement> _clickables = [];
    string? _signature;
    double _phase;
    TimeSpan _lastFrame;

    public PathMapCanvas()
    {
        Width = CanvasWidth;
        Height = CanvasHeight;
        Loaded += (_, _) => CompositionTarget.Rendering += OnFrame;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnFrame;
    }

    /// Freezes the animations (the user's Pause button).
    public bool Paused { get; set; }

    public event Action<string>? NodeClicked;

    /// Rebuilds the shapes only when what they show has changed. A rebuild every tick would destroy keyboard focus and open
    /// tooltips; the animations are driven by OnFrame, so they keep running when nothing is rebuilt.
    public void Render(PathMapView view, string? selectedId)
    {
        var signature = Signature(view, selectedId);
        if (signature == _signature) return;
        _signature = signature;

        var focusedId = Keyboard.FocusedElement is FrameworkElement { Tag: string id } focused && _clickables.Contains(focused) ? id : null;

        Children.Clear();
        _flows.Clear();
        _halos.Clear();
        _rings.Clear();
        _clickables.Clear();

        var lanes = view.Lanes;
        var spacing = lanes.Count <= 2 ? TwoLaneSpacing : ThreeLaneSpacing;
        List<double> ys = lanes.Count == 1 ? [DefaultMidY] : lanes.Select((_, i) => FirstLaneY + i * spacing).ToList();
        // Up to two lanes keep the v5 centre line (190, a little above the lanes' midpoint) so that layout is unchanged.
        var midY = lanes.Count <= 2 ? DefaultMidY : (ys[0] + ys[^1]) / 2;
        Height = Math.Max(CanvasHeight, ys.Count == 0 ? 0 : ys[^1] + BelowLastLane);

        var extraRows = Math.Max(0, lanes.Count - 2);
        var pcH = PcH + PcRowGrowth * extraRows;
        var netH = NetH + NetRowGrowth * extraRows;
        var pc = new Rect(20, midY - pcH / 2, PcW, pcH);
        var net = new Rect(CanvasWidth - 12 - NetW, midY - netH / 2, NetW, netH);
        for (var i = 0; i < lanes.Count; i++) DrawLane(lanes[i], ys[i], midY, pc, net, selectedId);
        DrawPc(pc, view);
        DrawInternet(net, view, selectedId);

        if (focusedId is not null && _clickables.FirstOrDefault(e => (string?)e.Tag == focusedId) is { } again && !again.Focus())
            Dispatcher.BeginInvoke(() => again.Focus(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// Everything Render draws, plus the colour theme, as one string.
    string Signature(PathMapView view, string? selectedId)
    {
        var sb = new StringBuilder();
        sb.Append(selectedId).Append('|').Append((B("NodeBackground") as SolidColorBrush)?.Color);
        foreach (var lane in view.Lanes)
        {
            sb.Append('|').Append(lane.Id).Append('/').Append(lane.Caption).Append('/').Append(lane.AdapterName).Append('/').Append(lane.Connected);
            foreach (var n in lane.Nodes)
                sb.Append('|').Append(n.Id).Append('/').Append(n.Kind).Append('/').Append(n.Title).Append('/').Append(n.Address)
                  .Append('/').Append(n.Metric).Append('/').Append(n.State).Append('/').Append(n.StateLabel);
            foreach (var l in lane.Links) sb.Append('|').Append(l);
            var i = lane.Internet;
            sb.Append('|').Append(i.Id).Append('/').Append(i.Label).Append('/').Append(i.Metric).Append('/').Append(i.State);
        }
        return sb.ToString();
    }

    void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        var dt = (now - _lastFrame).TotalSeconds;
        _lastFrame = now;
        if (Paused || !SystemParameters.ClientAreaAnimation) return;
        _phase += Math.Clamp(dt, 0, 0.25);

        foreach (var (shape, speed) in _flows) shape.StrokeDashOffset = -_phase * DashPeriodUnits * speed;
        var pulse = 0.5 + 0.5 * Math.Sin(_phase * 3.5);
        foreach (var halo in _halos) halo.Opacity = 0.12 + 0.4 * pulse;
        var t = (_phase * 0.6) % 1.0;
        foreach (var ring in _rings)
        {
            ring.Opacity = 0.8 * (1 - t);
            ring.RenderTransform = new ScaleTransform(1 + 1.4 * t, 1 + 1.4 * t, 9, 9);
        }
    }

    Brush B(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    // ----- lanes -----

    void DrawLane(LaneView lane, double y, double midY, Rect pc, Rect net, string? selectedId)
    {
        var captionY = y - NodeH / 2 - 22;
        if (!lane.Connected)
        {
            Text(lane.Caption, 10.5, FontWeights.SemiBold, "TextSecondary", pc.Right + 50, captionY);
            Text("Not connected", 12, FontWeights.Normal, "TextSecondary", pc.Right + 60, y - 8);
            return;
        }

        var n = lane.Nodes.Count;
        var available = net.Left - pc.Right;
        var width = Math.Min(NodeW, (available - (n + 1) * MinGap) / Math.Max(n, 1));
        var gap = (available - n * width) / (n + 1);
        var rects = Enumerable.Range(0, n)
            .Select(i => new Rect(pc.Right + gap * (i + 1) + width * i, y - NodeH / 2, width, NodeH)).ToList();
        // The caption starts at the first box so it stays clear of the curve leaving This PC.
        Text(lane.Caption, 10.5, FontWeights.SemiBold, "TextSecondary", n > 0 ? rects[0].Left : pc.Right + 50, captionY);

        var from = new Point(pc.Right, midY);
        for (var i = 0; i < n; i++)
        {
            Link(from, new Point(rects[i].Left, y), lane.Links[i]);
            from = new Point(rects[i].Right, y);
        }
        Link(from, new Point(net.Left, midY), lane.Links[n]);

        var pulseId = lane.Nodes.FirstOrDefault(x => x.State is HopDisplay.Broken or HopDisplay.Suspect)?.Id;
        for (var i = 0; i < n; i++) DrawNode(lane.Nodes[i], rects[i], selectedId, pulse: lane.Nodes[i].Id == pulseId);
    }

    void Link(Point a, Point b, LinkState state)
    {
        var figure = new PathFigure { StartPoint = a };
        if (Math.Abs(a.Y - b.Y) < 0.5) figure.Segments.Add(new LineSegment(b, true));
        else
        {
            var dx = (b.X - a.X) / 2;
            figure.Segments.Add(new BezierSegment(new Point(a.X + dx, a.Y), new Point(b.X - dx, b.Y), b, true));
        }
        var geometry = new PathGeometry([figure]);

        Children.Add(new Path { Data = geometry, Stroke = B("Track"), StrokeThickness = 9, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });

        var (brush, dashes, opacity, flow) = state switch
        {
            LinkState.Healthy => ("DotGreen", new[] { 2.0, 2.667 }, 1.0, 1.0),
            LinkState.Suspect => ("DotAmber", new[] { 2.0, 2.667 }, 1.0, 0.5),
            LinkState.Broken => ("DotRed", new[] { 0.7, 2.3 }, 1.0, 0.0),
            LinkState.Measuring => ("DotMeasuring", new[] { 2.0, 2.667 }, 0.9, 0.35),
            LinkState.Unavailable => ("DotGray", new[] { 0.4, 1.6 }, 0.55, 0.0),
            LinkState.Muted => ("DotGray", new[] { 0.7, 2.7 }, 0.45, 0.0),
            _ => ("DotGray", new[] { 1.0, 3.0 }, 0.6, 0.0),
        };
        var line = new Path { Data = geometry, Stroke = B(brush), StrokeThickness = 3, StrokeDashArray = new DoubleCollection(dashes), Opacity = opacity };
        Children.Add(line);
        if (flow > 0) _flows.Add((line, flow));

        if (state == LinkState.Broken) DrawCut(new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2));
    }

    /// The red ✕ on a broken link, with a ring that pulses outwards.
    void DrawCut(Point centre)
    {
        var ring = new Ellipse { Width = 18, Height = 18, Stroke = B("DotRed"), StrokeThickness = 2, IsHitTestVisible = false };
        SetLeft(ring, centre.X - 9);
        SetTop(ring, centre.Y - 9);
        Children.Add(ring);
        _rings.Add(ring);

        var disc = new Ellipse { Width = 18, Height = 18, Fill = B("NodeBackground"), Stroke = B("DotRed"), StrokeThickness = 2 };
        SetLeft(disc, centre.X - 9);
        SetTop(disc, centre.Y - 9);
        Children.Add(disc);

        var cross = new Path
        {
            Data = Geometry.Parse($"M{centre.X - 3.5},{centre.Y - 3.5} L{centre.X + 3.5},{centre.Y + 3.5} M{centre.X + 3.5},{centre.Y - 3.5} L{centre.X - 3.5},{centre.Y + 3.5}"),
            Stroke = B("DotRed"), StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
        Children.Add(cross);
    }

    // ----- boxes -----

    void DrawNode(NodeView node, Rect r, string? selectedId, bool pulse)
    {
        var selected = node.Id == selectedId;
        var dashed = node.State is HopDisplay.Quiet or HopDisplay.Hidden or HopDisplay.Silent or HopDisplay.Unavailable
                     || node.Kind == NodeKind.TraceIncomplete;

        if (pulse)
        {
            var halo = new Rectangle
            {
                Width = r.Width + 6, Height = r.Height + 6, RadiusX = 13, RadiusY = 13, Opacity = 0.3,
                Stroke = B(StateBrush(node.State)), StrokeThickness = 5, IsHitTestVisible = false,
            };
            SetLeft(halo, r.Left - 3);
            SetTop(halo, r.Top - 3);
            Children.Add(halo);
            _halos.Add(halo);
        }

        var box = new Grid { Width = r.Width, Height = r.Height, Cursor = Cursors.Hand, Focusable = true, Background = Brushes.Transparent };
        box.Children.Add(new Rectangle
        {
            RadiusX = 10, RadiusY = 10, Fill = B("NodeBackground"),
            Stroke = selected ? B("Accent") : node.State is HopDisplay.Answering ? B("NodeBorder") : B(StateBrush(node.State)),
            StrokeThickness = selected ? 2 : 1,
            StrokeDashArray = dashed ? new DoubleCollection([4.0, 3.0]) : null,
        });

        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = B(StateBrush(node.State)), Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new TextBlock { Text = node.Title, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = B("TextPrimary"), TextTrimming = TextTrimming.CharacterEllipsis });

        var showMetric = node.State is HopDisplay.Answering or HopDisplay.Suspect or HopDisplay.Broken && node.Metric.Length > 0;
        var panel = new StackPanel { Margin = new Thickness(11, 9, 8, 6) };
        panel.Children.Add(title);
        panel.Children.Add(new TextBlock { Text = node.Address, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 10.5, Foreground = B("TextSecondary"), Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock
        {
            Text = showMetric ? node.Metric : node.StateLabel, FontSize = 10.5, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 3, 0, 0),
            Foreground = B(node.State switch { HopDisplay.Suspect => "DotAmber", HopDisplay.Broken => "DotRed", _ => "TextSecondary" }),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        box.Children.Add(panel);

        Clickable(box, node.Id, $"{node.Title}, {node.Address}, {node.StateLabel}");
        box.ToolTip = node.StateLabel;
        SetLeft(box, r.Left);
        SetTop(box, r.Top);
        Children.Add(box);
    }

    void DrawPc(Rect r, PathMapView view)
    {
        var box = new Grid { Width = r.Width, Height = r.Height };
        box.Children.Add(new Rectangle { RadiusX = 12, RadiusY = 12, Fill = B("NodeBackground"), Stroke = B("NodeBorder"), StrokeThickness = 1 });
        var panel = new StackPanel { Margin = new Thickness(12, 10, 8, 6) };
        panel.Children.Add(new TextBlock { Text = "This PC", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = B("TextPrimary") });
        for (var i = 0; i < view.Lanes.Count; i++)
        {
            var lane = view.Lanes[i];
            panel.Children.Add(PcRow(PcLabel(lane.Name, lane), lane.Connected ? lane.Internet.State : HopDisplay.Hidden, i == 0 ? 8 : 5));
        }
        box.Children.Add(panel);
        SetLeft(box, r.Left);
        SetTop(box, r.Top);
        Children.Add(box);
    }

    static string PcLabel(string lane, LaneView view) => view.AdapterName.Length > 0 ? $"{lane} · {view.AdapterName}" : lane;

    StackPanel PcRow(string text, HopDisplay state, double top)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, top, 0, 0) };
        row.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = B(StateBrush(state)), Margin = new Thickness(0, 0, 7, 0) });
        row.Children.Add(new TextBlock { Text = text, FontSize = 10.5, Foreground = B("TextSecondary") });
        return row;
    }

    void DrawInternet(Rect r, PathMapView view, string? selectedId)
    {
        var box = new Grid { Width = r.Width, Height = r.Height };
        box.Children.Add(new Rectangle { RadiusX = 12, RadiusY = 12, Fill = B("NodeBackground"), Stroke = B("NodeBorder"), StrokeThickness = 1 });
        var panel = new StackPanel { Margin = new Thickness(12, 10, 6, 6) };
        panel.Children.Add(new TextBlock { Text = "Internet", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = B("TextPrimary") });
        panel.Children.Add(new TextBlock { Text = "1.1.1.1 · 8.8.8.8", FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 10.5, Foreground = B("TextSecondary"), Margin = new Thickness(0, 2, 0, 4) });
        foreach (var lane in view.Lanes) panel.Children.Add(InternetRow(lane.Internet, selectedId));
        box.Children.Add(panel);
        SetLeft(box, r.Left);
        SetTop(box, r.Top);
        Children.Add(box);
    }

    Grid InternetRow(InternetRowView row, string? selectedId)
    {
        var grid = new Grid { Height = 22, Cursor = Cursors.Hand, Focusable = true, Background = row.Id == selectedId ? B("Track") : Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var dot = new Ellipse { Width = 8, Height = 8, Fill = B(StateBrush(row.State)), HorizontalAlignment = HorizontalAlignment.Left };
        var name = new TextBlock { Text = row.Label, FontSize = 11.5, Foreground = B("TextPrimary"), VerticalAlignment = VerticalAlignment.Center };
        var metric = new TextBlock
        {
            Text = row.Metric, FontSize = 10.5, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
            Foreground = B(row.State switch { HopDisplay.Suspect => "DotAmber", HopDisplay.Broken => "DotRed", _ => "TextSecondary" }),
        };
        Grid.SetColumn(name, 1);
        Grid.SetColumn(metric, 2);
        grid.Children.Add(dot);
        grid.Children.Add(name);
        grid.Children.Add(metric);
        Clickable(grid, row.Id, $"Internet via {row.Label}, {row.Metric}");
        return grid;
    }

    // ----- helpers -----

    void Clickable(FrameworkElement element, string id, string name)
    {
        AutomationProperties.SetName(element, name);
        element.Tag = id;
        _clickables.Add(element);
        element.MouseLeftButtonUp += (_, _) => NodeClicked?.Invoke(id);
        element.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space) { e.Handled = true; NodeClicked?.Invoke(id); }
        };
    }

    void Text(string text, double size, FontWeight weight, string brush, double x, double y)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight, Foreground = B(brush), IsHitTestVisible = false };
        SetLeft(block, x);
        SetTop(block, y);
        Children.Add(block);
    }

    internal static string StateBrush(HopDisplay state) => state switch
    {
        HopDisplay.Answering => "DotGreen",
        HopDisplay.Suspect => "DotAmber",
        HopDisplay.Broken => "DotRed",
        HopDisplay.Measuring => "DotMeasuring",
        _ => "DotGray",
    };
}
