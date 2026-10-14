using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NetRoute.Core.PathMap;

namespace NetRoute.App;

/// The "Local network" tab: a plain-words verdict, this PC's network card, the device list and a detail panel. It owns only the screen:
/// the window's timer calls Refresh, and every number comes from the services' view.
public partial class LocalNetworkTab : UserControl
{
    LocalNetworkTabServices? _services;
    string? _selected;
    LocalNetworkView? _view;
    string _verdictKey = "", _rowsKey = "";

    public LocalNetworkTab()
    {
        InitializeComponent();
    }

    public event Action? AddRequested;
    public event Action? ScanRequested;
    public event Action<LocalDeviceRow>? RemoveRequested;

    internal void Attach(LocalNetworkTabServices services) => _services = services;

    internal void Refresh()
    {
        if (_services is null) return;
        LocalNetworkView view;
        try
        {
            view = _services.Present(_selected);
        }
        catch (Exception)
        {
            return; // keep the last view on screen; a throw out of a timer tick would repeat every 500 ms
        }
        if (_selected is not null && view.Detail is null) _selected = null;
        _view = view;
        Render(view);
    }

    void Render(LocalNetworkView view)
    {
        Message.Text = view.Message;
        Message.Visibility = view.Message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RenderVerdict(view);
        RenderNic(view.Nic);
        RenderRows(view.Rows);
        RenderDetail(view.Detail);
        var selectedRow = view.Rows.FirstOrDefault(r => r.Key == _selected);
        RemoveButton.IsEnabled = selectedRow is { IsRouter: false };
        AddButton.IsEnabled = ScanButton.IsEnabled = view.Connected;
    }

    void RenderVerdict(LocalNetworkView view)
    {
        var key = view.VerdictWarning + "|" + string.Join("\n", view.Verdict);
        if (key == _verdictKey) return;
        _verdictKey = key;
        Verdict.Children.Clear();
        foreach (var line in view.Verdict)
        {
            var text = new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) };
            text.SetResourceReference(TextBlock.ForegroundProperty, view.VerdictWarning ? "Warning" : "TextPrimary");
            Verdict.Children.Add(text);
        }
    }

    void RenderNic(NicView? nic)
    {
        NicCard.Visibility = nic is null ? Visibility.Collapsed : Visibility.Visible;
        if (nic is null) return;
        NicTitle.Text = "This PC's network card · " + nic.Name;
        NicStats.Children.Clear();
        AddStat(NicStats, "Link speed", nic.SpeedText, nic.SpeedWarning);
        AddStat(NicStats, "Errors", nic.ErrorsText, nic.ErrorsWarning);
        NicNote.Text = nic.SpeedNote;
        NicNote.Visibility = nic.SpeedNote.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    static void AddStat(Panel panel, string label, string value, bool warning)
    {
        var stat = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
        var caption = new TextBlock { Text = label, FontSize = 11.5 };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        var number = new TextBlock { Text = value, FontSize = 14, FontWeight = FontWeights.SemiBold };
        number.SetResourceReference(TextBlock.ForegroundProperty, warning ? "Warning" : "TextPrimary");
        stat.Children.Add(caption);
        stat.Children.Add(number);
        panel.Children.Add(stat);
    }

    void RenderRows(IReadOnlyList<LocalDeviceRow> rows)
    {
        var key = _selected + "||" + string.Join("\n", rows.Select(r => $"{r.Key}|{r.Name}|{r.Address}|{r.State}|{r.Metric}|{r.OnSubnet}"));
        if (key == _rowsKey) return;
        _rowsKey = key;
        Rows.Children.Clear();
        foreach (var row in rows)
        {
            var selected = row.Key == _selected;
            var grid = new Grid { Margin = new Thickness(6, 5, 6, 5), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dot = new Ellipse { Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            dot.SetResourceReference(Shape.FillProperty, PathMapCanvas.StateBrush(LocalNetworkPresenter.AsHop(row.State)));
            Grid.SetColumn(dot, 0);

            var title = new StackPanel { Orientation = Orientation.Horizontal };
            title.Children.Add(new TextBlock { Text = row.Name, FontWeight = selected ? FontWeights.Bold : FontWeights.SemiBold, Margin = new Thickness(0, 0, 10, 0) });
            var address = new TextBlock { Text = row.Address + (row.OnSubnet ? "" : " · other network"), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12 };
            address.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            title.Children.Add(address);
            Grid.SetColumn(title, 1);

            var state = new StackPanel { Orientation = Orientation.Horizontal };
            var label = new TextBlock { Text = row.StateLabel, Margin = new Thickness(0, 0, 14, 0) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            state.Children.Add(label);
            state.Children.Add(new TextBlock { Text = row.Metric, MinWidth = 90, TextAlignment = TextAlignment.Right });
            Grid.SetColumn(state, 2);

            grid.Children.Add(dot);
            grid.Children.Add(title);
            grid.Children.Add(state);
            var border = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = grid };
            border.SetResourceReference(Border.BorderBrushProperty, selected ? "Accent" : "CardBackground");
            var rowKey = row.Key;
            border.MouseLeftButtonUp += (_, _) =>
            {
                _selected = rowKey;
                Refresh();
            };
            Rows.Children.Add(border);
        }
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
        foreach (var (label, value) in detail.Stats) AddStat(DetailStats, label, value, warning: false);

        SparkLine.Draw(Spark, detail.Series, key => (Brush)(TryFindResource(key) ?? Brushes.Gray));
        Spark.Visibility = detail.Series.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        DetailNote.Text = detail.Note;
    }

    void OnAdd(object sender, RoutedEventArgs e) => AddRequested?.Invoke();

    void OnScan(object sender, RoutedEventArgs e) => ScanRequested?.Invoke();

    void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_view?.Rows.FirstOrDefault(r => r.Key == _selected) is { IsRouter: false } row) RemoveRequested?.Invoke(row);
    }
}
