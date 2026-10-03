using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using NetRoute.Core;

namespace NetRoute.App;

/// The Usage tab. Updated in place and keyed by row key: a refresh once a second never rebuilds the list, steals focus
/// or closes an open "Goes via" drop-down. Rows only change places when the mouse and the keyboard focus are away from the list and no
/// drop-down is open, or right after the user clicked a column header.
public partial class UsageTab : UserControl
{
    static readonly TimeSpan PendingTimeout = TimeSpan.FromSeconds(5);
    const double ViaWidth = 96, ByteWidth = 84, NowWidth = 150;

    readonly Dictionary<int, ToggleButton> _rangeButtons = new();
    readonly Dictionary<UsageSortColumn, Button> _headers = new();
    readonly Dictionary<string, RowView> _rows = new();
    readonly TextBlock _totalPhone = new(), _totalLan = new();
    List<string> _order = [];
    UsageSort _sort = UsageSort.Default;
    bool _forceReorder;

    public UsageTab()
    {
        InitializeComponent();
        BuildRangeBar();
        BuildHeader();
        BuildTotals();
        RowScroll.ScrollChanged += (_, _) => MatchScrollBarWidth();
        RowScroll.SizeChanged += (_, _) => MatchScrollBarWidth();
    }

    public event Action<int>? RangeChanged;
    public event Action<UsageSort>? SortChanged;
    public event Action<string, bool>? AssignmentRequested;

    public void Render(UsageReportModel model, UsageSort sort, string statusText, bool recording)
    {
        _sort = sort;
        StatusText.Text = statusText;
        StatusText.ToolTip = statusText;
        foreach (var (days, button) in _rangeButtons) button.IsChecked = days == model.RangeDays;
        UpdateHeaders();

        var banner = new List<string>();
        if (!recording) banner.Add("Usage is recorded only while Smart routing is running. History stays visible.");
        if (model.RecordingSince is { } since)
            banner.Add($"Recording since {since.ToString("d MMM yyyy", CultureInfo.CurrentCulture)}.");
        Banner.Text = string.Join("\n", banner);
        Banner.Visibility = banner.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = model.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var byKey = model.Rows.ToDictionary(r => r.Key);
        foreach (var key in _rows.Keys.Where(k => !byKey.ContainsKey(k)).ToList())
        {
            Rows.Children.Remove(_rows[key].Root);
            _rows.Remove(key);
        }
        foreach (var row in model.Rows)
        {
            if (!_rows.TryGetValue(row.Key, out var view))
            {
                view = _rows[row.Key] = new RowView(row.Key, (key, toLan) => AssignmentRequested?.Invoke(key, toLan));
                Rows.Children.Add(view.Root);
            }
            view.Apply(row);
        }

        var freeze = !_forceReorder && (Rows.IsMouseOver || Rows.IsKeyboardFocusWithin || _rows.Values.Any(v => v.DropDownOpen));
        _forceReorder = false;
        var next = UsageRowOrder.Next(_order, model.Rows.Select(r => r.Key).ToList(), freeze);
        if (!next.SequenceEqual(_order))
        {
            for (var i = 0; i < next.Count; i++)
            {
                var element = _rows[next[i]].Root;
                if (Rows.Children.IndexOf(element) == i) continue;
                Rows.Children.Remove(element);
                Rows.Children.Insert(i, element);
            }
            _order = next.ToList();
        }

        _totalPhone.Text = ByteFormat.Human(model.PhoneTotal);
        _totalLan.Text = ByteFormat.Human(model.LanTotal);
        KeptText.Text = $"Kept off 4G in this period: {(model.KeptOffIsLowerBound ? "at least " : "")}{ByteFormat.Human(model.KeptOff)}";
        KeptText.ToolTip = model.KeptOffIsLowerBound
            ? "Smart routing was stopped or restarted during this period; the bytes just before each stop were not recorded."
            : null;
        AdapterText.Text = "Phone adapter total in this period (exact, from Windows): "
            + (model.PhoneAdapterTotal > 0 ? ByteFormat.Human(model.PhoneAdapterTotal) : "not measured yet");
    }

    /// The header and the totals take the same width as the rows, whatever the scroll bar's width (or visibility) is in the current theme.
    void MatchScrollBarWidth()
    {
        var bar = Math.Max(0, RowScroll.ActualWidth - RowScroll.ViewportWidth);
        HeaderGrid.Margin = new Thickness(0, 12, bar, 0);
        TotalGrid.Margin = new Thickness(0, 0, bar, 0);
    }

    void BuildRangeBar()
    {
        RangeBar.Children.Add(Secondary(new TextBlock
        {
            Text = "Show usage for:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
        }));
        foreach (var days in UsageReport.Ranges)
        {
            var chosen = days;
            var button = new ToggleButton
            {
                Content = UsageReport.RangeLabel(days), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 4, 0),
            };
            button.Click += (_, _) =>
            {
                foreach (var (d, other) in _rangeButtons) other.IsChecked = d == chosen; // the model confirms it on the next render
                RangeChanged?.Invoke(chosen);
            };
            _rangeButtons[days] = button;
            RangeBar.Children.Add(button);
        }
    }

    void BuildHeader()
    {
        DefineColumns(HeaderGrid);
        AddHeader(UsageSortColumn.Name, 0, HorizontalAlignment.Left,
            "Each connection counts under one row: an application that has its own rule, otherwise the site rule it matches " +
            "(YouTube traffic from Chrome counts under YouTube), otherwise the application.");
        var via = Secondary(new TextBlock { Text = "Goes via", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(via, 1);
        HeaderGrid.Children.Add(via);
        AddHeader(UsageSortColumn.Phone, 2, HorizontalAlignment.Right, null);
        AddHeader(UsageSortColumn.Lan, 3, HorizontalAlignment.Right, null);
        AddHeader(UsageSortColumn.Now, 4, HorizontalAlignment.Left, null);
    }

    void AddHeader(UsageSortColumn column, int index, HorizontalAlignment align, string? tooltip)
    {
        var button = new Button
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(6, 2, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = align, FontWeight = FontWeights.SemiBold, ToolTip = tooltip,
        };
        button.Click += (_, _) =>
        {
            _sort = _sort.Click(column);
            _forceReorder = true; // the user asked for this order: apply it even with the mouse over the list
            UpdateHeaders();
            SortChanged?.Invoke(_sort);
        };
        _headers[column] = button;
        Grid.SetColumn(button, index);
        HeaderGrid.Children.Add(button);
    }

    void UpdateHeaders()
    {
        foreach (var (column, button) in _headers)
        {
            var label = column switch
            {
                UsageSortColumn.Name => "Application / site",
                UsageSortColumn.Phone => "Phone",
                UsageSortColumn.Lan => "LAN",
                _ => "Now",
            };
            button.Content = column == _sort.Column ? $"{label} {(_sort.Descending ? "▼" : "▲")}" : label;
        }
    }

    void BuildTotals()
    {
        DefineColumns(TotalGrid);
        var label = new TextBlock { Text = "Total", FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 0, 0, 0) };
        _totalPhone.HorizontalAlignment = _totalLan.HorizontalAlignment = HorizontalAlignment.Right;
        _totalPhone.Margin = _totalLan.Margin = new Thickness(0, 0, 6, 0);
        _totalPhone.FontWeight = _totalLan.FontWeight = FontWeights.SemiBold;
        Grid.SetColumn(_totalPhone, 2);
        Grid.SetColumn(_totalLan, 3);
        TotalGrid.Children.Add(label);
        TotalGrid.Children.Add(_totalPhone);
        TotalGrid.Children.Add(_totalLan);
    }

    /// Name | Goes via | Phone | LAN | Now. Fixed widths so the header, the rows and the totals line up.
    static void DefineColumns(Grid grid)
    {
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ViaWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ByteWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ByteWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NowWidth) });
    }

    /// Dimmed text that follows the theme (never a fixed colour).
    static TextBlock Secondary(TextBlock text)
    {
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        return text;
    }

    /// One row of the list. Created once per row key and then only updated.
    sealed class RowView
    {
        readonly Grid _grid = new() { Background = Brushes.Transparent }; // transparent: the whole row is hit-testable, so the list counts as "under the mouse"
        readonly TextBlock _name = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0, 0, 0) };
        readonly ComboBox _via = new() { Width = ViaWidth - 8, Margin = new Thickness(8, 2, 0, 2), HorizontalAlignment = HorizontalAlignment.Left };
        readonly TextBlock _phone = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        readonly TextBlock _lan = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        readonly TextBlock _now = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        bool _suppress;
        GoesVia? _pending; // the user's pick, kept until the model reports it (or gives up on it)
        DateTime _pendingSince;

        public RowView(string key, Action<string, bool> assign)
        {
            DefineColumns(_grid);
            _via.Items.Add("Phone");
            _via.Items.Add("LAN");
            AutomationProperties.SetName(_via, "Goes via");
            _via.SelectionChanged += (_, _) =>
            {
                if (_suppress || _via.SelectedIndex < 0) return;
                _pending = _via.SelectedIndex == 1 ? GoesVia.Lan : GoesVia.Phone;
                _pendingSince = DateTime.UtcNow;
                assign(key, _via.SelectedIndex == 1);
            };
            Place(_name, 0);
            Place(_via, 1);
            Place(_phone, 2);
            Place(_lan, 3);
            Place(_now, 4);
        }

        public UIElement Root => _grid;
        public bool DropDownOpen => _via.IsDropDownOpen;

        public void Apply(UsageReportRow row)
        {
            _name.Text = row.Name;
            _name.ToolTip = row.Name;
            _phone.Text = ByteFormat.Human(row.PhoneBytes);
            _lan.Text = ByteFormat.Human(row.LanBytes);
            _now.Text = UsageReport.NowText(row.Now);
            if (row.Now is null) _now.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            else _now.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            _via.IsEnabled = row.CanChange;
            if (_via.IsDropDownOpen) return; // never change the selection under the user's hand
            if (_pending is { } pick)
            {
                // The drop-down closes before the model catches up: keep the pick for a few seconds (not the old value),
                // then trust the model again (the assignment may have been refused).
                if (row.Via == pick || DateTime.UtcNow - _pendingSince > PendingTimeout) _pending = null;
                else return;
            }
            _suppress = true;
            _via.SelectedIndex = row.Via == GoesVia.Lan ? 1 : 0;
            _suppress = false;
        }

        void Place(UIElement element, int column)
        {
            Grid.SetColumn(element, column);
            _grid.Children.Add(element);
        }
    }
}
