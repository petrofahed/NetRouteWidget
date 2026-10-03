using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using NetRoute.Core;

namespace NetRoute.App;

/// The Usage tab, a view of Phone and LAN use. The only edit is the right-click menu on a row (send it to / exclude it from the
/// active profile's exception list); rules are otherwise changed on the Config tab. Updated in place and keyed by row key: a
/// refresh once a second never rebuilds the list or steals focus. Rows only change places when the mouse and the keyboard focus
/// are away from the list and no row menu is open, or right after the user clicked a column header.
public partial class UsageTab : UserControl
{
    const double ByteWidth = 84, NowWidth = 96;

    readonly Dictionary<int, ToggleButton> _rangeButtons = new();
    readonly Dictionary<UsageSortColumn, Button> _headers = new();
    readonly Dictionary<string, RowView> _rows = new();
    readonly TextBlock _totalLabel = new(), _totalPhone = new(), _totalLan = new();
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
        FilterBox.TextChanged += (_, _) => FilterChanged?.Invoke(FilterBox.Text);
        FilterBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || FilterBox.Text.Length == 0) return;
            FilterBox.Clear(); // raises TextChanged, which tells the app
            e.Handled = true;
        };
    }

    public event Action<int>? RangeChanged;
    public event Action<UsageSort>? SortChanged;
    /// The filter text as typed (untrimmed). The filter is per-session UI state and the box is never written by Render.
    public event Action<string>? FilterChanged;
    /// The row key whose context-menu item was clicked: add it to / remove it from the active profile's exception list.
    public event Action<string>? ExceptionToggleRequested;

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
        EmptyText.Text = model.Filtered
            ? $"No rows match “{FilterBox.Text.Trim()}”."
            : "No usage recorded in this period yet.";
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
                view = _rows[row.Key] = new RowView(row.Key, key => ExceptionToggleRequested?.Invoke(key));
                Rows.Children.Add(view.Root);
            }
            view.Apply(row);
        }

        var freeze = !_forceReorder && (RowScroll.IsMouseOver || Rows.IsKeyboardFocusWithin || _rows.Values.Any(r => r.MenuOpen));
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

        _totalLabel.Text = model.Filtered ? "Total (filtered)" : "Total";
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
        AddHeader(UsageSortColumn.Phone, 1, HorizontalAlignment.Right, null);
        AddHeader(UsageSortColumn.Lan, 2, HorizontalAlignment.Right, null);
        AddHeader(UsageSortColumn.NowPhone, 3, HorizontalAlignment.Right,
            "Current speed over the last few seconds on the phone connection, shown only above 1 KB/s.");
        AddHeader(UsageSortColumn.NowLan, 4, HorizontalAlignment.Right,
            "Current speed over the last few seconds on the LAN connection, shown only above 1 KB/s.");
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
                UsageSortColumn.NowPhone => "Now phone",
                _ => "Now LAN",
            };
            button.Content = column == _sort.Column ? $"{label} {(_sort.Descending ? "▼" : "▲")}" : label;
        }
    }

    void BuildTotals()
    {
        DefineColumns(TotalGrid);
        _totalLabel.Text = "Total";
        _totalLabel.FontWeight = FontWeights.SemiBold;
        _totalLabel.Margin = new Thickness(6, 0, 0, 0);
        _totalPhone.HorizontalAlignment = _totalLan.HorizontalAlignment = HorizontalAlignment.Right;
        _totalPhone.Margin = _totalLan.Margin = new Thickness(0, 0, 6, 0);
        _totalPhone.FontWeight = _totalLan.FontWeight = FontWeights.SemiBold;
        Grid.SetColumn(_totalPhone, 1);
        Grid.SetColumn(_totalLan, 2);
        TotalGrid.Children.Add(_totalLabel);
        TotalGrid.Children.Add(_totalPhone);
        TotalGrid.Children.Add(_totalLan);
    }

    /// Name | Phone | LAN | Now phone | Now LAN. Fixed widths so the header, the rows and the totals line up.
    static void DefineColumns(Grid grid)
    {
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ByteWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ByteWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NowWidth) });
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
        readonly TextBlock _tagText = new() { FontSize = 11 };
        readonly Border _tag = new()
        {
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 0, 5, 0),
            Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
        };
        readonly MenuItem _menuItem = new();
        readonly TextBlock _phone = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        readonly TextBlock _lan = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        readonly TextBlock _nowPhone = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        readonly TextBlock _nowLan = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };

        public RowView(string key, Action<string> toggle)
        {
            DefineColumns(_grid);
            _tagText.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            _tag.SetResourceReference(Border.BorderBrushProperty, "Accent");
            _tag.Child = _tagText;
            DockPanel.SetDock(_tag, Dock.Right);
            var nameCell = new DockPanel();
            nameCell.Children.Add(_tag); // docked first so the long name is trimmed before the tag
            nameCell.Children.Add(_name);
            Place(nameCell, 0);
            _menuItem.Click += (_, _) => toggle(key);
            var menu = new ContextMenu();
            menu.Items.Add(_menuItem);
            menu.Opened += (_, _) => MenuOpen = true;
            menu.Closed += (_, _) => MenuOpen = false;
            _grid.ContextMenu = menu;
            Place(_phone, 1);
            Place(_lan, 2);
            Place(_nowPhone, 3);
            Place(_nowLan, 4);
        }

        public UIElement Root => _grid;

        /// True while this row's context menu is showing: the list must not reorder under it.
        public bool MenuOpen { get; private set; }

        public void Apply(UsageReportRow row)
        {
            _name.Text = row.Name;
            _name.ToolTip = row.Name;
            var tag = UsageReport.TagText(row.Exception);
            _tagText.Text = tag ?? "";
            _tag.Visibility = tag is null ? Visibility.Collapsed : Visibility.Visible;
            _menuItem.Header = UsageReport.MenuText(row.Exception);
            _menuItem.IsEnabled = row.CanChange;
            _phone.Text = ByteFormat.Human(row.PhoneBytes);
            _lan.Text = ByteFormat.Human(row.LanBytes);
            ApplyRate(_nowPhone, row.Now?.PhoneBytesPerSecond ?? 0);
            ApplyRate(_nowLan, row.Now?.LanBytesPerSecond ?? 0);
        }

        /// Active speeds use the accent colour, idle ones the dimmed text colour.
        static void ApplyRate(TextBlock cell, long bytesPerSecond)
        {
            cell.Text = UsageReport.NowText(bytesPerSecond);
            cell.SetResourceReference(TextBlock.ForegroundProperty, bytesPerSecond > 0 ? "Accent" : "TextSecondary");
        }

        void Place(UIElement element, int column)
        {
            Grid.SetColumn(element, column);
            _grid.Children.Add(element);
        }
    }
}
