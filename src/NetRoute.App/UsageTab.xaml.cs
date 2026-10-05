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
    // Column widths and the inner gutters: text sits NameInset from the left edge of the name cell and CellInset from the right edge
    // of a number cell, in the header, the rows and the totals alike, so the three line up.
    // Column order: Name | Phone up, down, total | LAN up, down, total | Now phone | Now LAN.
    const double ByteWidth = 84, NowWidth = 150, NameMinWidth = 270, NameInset = 10, CellInset = 14;

    readonly Dictionary<int, ToggleButton> _rangeButtons = new();
    readonly Dictionary<UsageSortColumn, Button> _headers = new();
    readonly Dictionary<string, RowView> _rows = new();
    readonly TextBlock _totalLabel = new();
    readonly TextBlock[] _totals = [new(), new(), new(), new(), new(), new()]; // Phone up, down, total, LAN up, down, total
    List<string> _order = [];
    UsageSort _sort = UsageSort.Default;
    UsageSort? _headerSort; // the sort the header labels currently show
    bool _forceReorder;

    public UsageTab()
    {
        InitializeComponent();
        BuildRangeBar();
        BuildGroups();
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
    /// A row's context-menu item was clicked: (row key, the profile the menu was rendered for, true = send to the exception
    /// list / false = exclude from it). The state is the one the menu DISPLAYED, never re-read at click time.
    public event Action<string, RouteExit, bool>? ExceptionRequested;

    RouteExit _renderedProfile = RouteExit.Phone;

    public void Render(UsageReportModel model, UsageSort sort, string statusText, bool recording)
    {
        _sort = sort;
        _renderedProfile = model.Profile;
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
                view = _rows[row.Key] = new RowView(row.Key, (key, on) => ExceptionRequested?.Invoke(key, _renderedProfile, on));
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
        // The last row has no line under it: the totals band has its own line.
        for (var i = 0; i < _order.Count; i++) _rows[_order[i]].SetLast(i == _order.Count - 1);

        _totalLabel.Text = model.Filtered ? "Total (filtered)" : "Total";
        SetTotals(model);
        PhoneText.Text = $"Phone in this period{(model.Filtered ? " (filtered)" : "")}: ↑ {ByteFormat.Human(model.PhoneUpTotal)} · "
            + $"↓ {ByteFormat.Human(model.PhoneTotal - model.PhoneUpTotal)} · total {ByteFormat.Human(model.PhoneTotal)}";
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
        GroupGrid.Margin = HeaderGrid.Margin = new Thickness(0, 0, bar, 0);
        TotalGrid.Margin = new Thickness(0, 0, bar, 0);
    }

    void BuildRangeBar()
    {
        RangeBar.Children.Add(Secondary(new TextBlock
        {
            Text = "Show usage for:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
        }));
        foreach (var days in UsageReport.Ranges)
        {
            var chosen = days;
            var button = new ToggleButton
            {
                Content = UsageReport.RangeLabel(days), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0),
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
        AddHeader(UsageSortColumn.PhoneUp, 1, HorizontalAlignment.Right, "Data sent (uploaded) through the phone.");
        AddHeader(UsageSortColumn.PhoneDown, 2, HorizontalAlignment.Right, "Data received (downloaded) through the phone.");
        AddHeader(UsageSortColumn.Phone, 3, HorizontalAlignment.Right, "Uploaded + downloaded through the phone.");
        AddHeader(UsageSortColumn.LanUp, 4, HorizontalAlignment.Right, "Data sent (uploaded) through the LAN.");
        AddHeader(UsageSortColumn.LanDown, 5, HorizontalAlignment.Right, "Data received (downloaded) through the LAN.");
        AddHeader(UsageSortColumn.Lan, 6, HorizontalAlignment.Right, "Uploaded + downloaded through the LAN.");
        AddHeader(UsageSortColumn.NowPhone, 7, HorizontalAlignment.Right,
            "Current upload and download speed over the last few seconds on the phone connection, shown only above 1 KB/s.");
        AddHeader(UsageSortColumn.NowLan, 8, HorizontalAlignment.Right,
            "Current upload and download speed over the last few seconds on the LAN connection, shown only above 1 KB/s.");
    }

    /// The "Phone" and "LAN" labels above their three columns each.
    void BuildGroups()
    {
        DefineColumns(GroupGrid);
        AddGroup("Phone", 1);
        AddGroup("LAN", 4);
    }

    void AddGroup(string text, int firstColumn)
    {
        var label = Secondary(new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        var line = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(8, 0, 8, 2), Padding = new Thickness(0, 0, 0, 2), Child = label };
        line.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
        Grid.SetColumn(line, firstColumn);
        Grid.SetColumnSpan(line, 3);
        GroupGrid.Children.Add(line);
    }

    void AddHeader(UsageSortColumn column, int index, HorizontalAlignment align, string? tooltip)
    {
        var button = new Button
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontSize = 12,
            Padding = align == HorizontalAlignment.Left ? new Thickness(NameInset, 4, 6, 4) : new Thickness(6, 4, CellInset, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = align, FontWeight = FontWeights.SemiBold, ToolTip = tooltip,
        };
        button.SetResourceReference(Control.ForegroundProperty, "TextSecondary");
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

    /// The active sort column is in the primary text colour with its arrow, the others in the secondary one. Only touched when the sort changes.
    void UpdateHeaders()
    {
        if (_headerSort == _sort) return;
        _headerSort = _sort;
        foreach (var (column, button) in _headers)
        {
            var label = column switch
            {
                UsageSortColumn.Name => "Application / site",
                UsageSortColumn.PhoneUp or UsageSortColumn.LanUp => "↑ Up",
                UsageSortColumn.PhoneDown or UsageSortColumn.LanDown => "↓ Down",
                UsageSortColumn.Phone or UsageSortColumn.Lan => "Total",
                UsageSortColumn.NowPhone => "Now phone",
                _ => "Now LAN",
            };
            button.Content = column == _sort.Column ? $"{label} {(_sort.Descending ? "▼" : "▲")}" : label;
            button.SetResourceReference(Control.ForegroundProperty, column == _sort.Column ? "TextPrimary" : "TextSecondary");
        }
    }

    void BuildTotals()
    {
        DefineColumns(TotalGrid);
        _totalLabel.Text = "Total";
        _totalLabel.FontWeight = FontWeights.SemiBold;
        _totalLabel.Margin = new Thickness(NameInset, 0, 0, 0);
        TotalGrid.Children.Add(_totalLabel);
        for (var i = 0; i < _totals.Length; i++)
        {
            _totals[i].HorizontalAlignment = HorizontalAlignment.Right;
            _totals[i].Margin = new Thickness(0, 0, CellInset, 0);
            _totals[i].FontWeight = FontWeights.SemiBold;
            Grid.SetColumn(_totals[i], i + 1);
            TotalGrid.Children.Add(_totals[i]);
        }
    }

    void SetTotals(UsageReportModel model)
    {
        _totals[0].Text = ByteFormat.Human(model.PhoneUpTotal);
        _totals[1].Text = ByteFormat.Human(model.PhoneTotal - model.PhoneUpTotal);
        _totals[2].Text = ByteFormat.Human(model.PhoneTotal);
        _totals[3].Text = ByteFormat.Human(model.LanUpTotal);
        _totals[4].Text = ByteFormat.Human(model.LanTotal - model.LanUpTotal);
        _totals[5].Text = ByteFormat.Human(model.LanTotal);
    }

    /// Name | Phone up, down, total | LAN up, down, total | Now phone | Now LAN. Fixed widths so the header, the rows and the totals line up.
    static void DefineColumns(Grid grid)
    {
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = NameMinWidth }); // wide enough for the longest built-in names: the name is never cut
        for (var i = 0; i < 6; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ByteWidth) });
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
        // Transparent, not null: the whole row is hit-testable, so the list counts as "under the mouse"; hovering swaps in the highlight.
        readonly Border _root = new() { Background = Brushes.Transparent, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 1, 0, 1) };
        readonly Grid _grid = new() { MinHeight = 32 };
        readonly Border _separator = new() { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(NameInset, 0, NameInset, 0), IsHitTestVisible = false };
        readonly TextBlock _name = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(NameInset, 0, 0, 0) };
        readonly TextBlock _tagText = new() { FontSize = 11 };
        readonly Border _tag = new()
        {
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
        };
        readonly MenuItem _menuItem = new();
        readonly TextBlock[] _bytes = [Number(), Number(), Number(), Number(), Number(), Number()]; // Phone up, down, total, LAN up, down, total
        readonly TextBlock _nowPhone = Number(), _nowLan = Number();
        bool _hovered;
        bool _sendOn; // what the menu item does: true = send to the exception list, false = exclude (the displayed state)

        public RowView(string key, Action<string, bool> request)
        {
            DefineColumns(_grid);
            _root.Child = _grid;
            // The two catch-all rows are not applications: dim their names.
            _name.SetResourceReference(TextBlock.ForegroundProperty, key is UsageAttribution.OtherKey or UsageAttribution.UnattributedKey ? "TextSecondary" : "TextPrimary");
            _separator.SetResourceReference(Border.BackgroundProperty, "CardBorder");
            _tagText.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            _tag.SetResourceReference(Border.BorderBrushProperty, "Accent");
            _tag.Child = _tagText;
            DockPanel.SetDock(_tag, Dock.Right);
            var nameCell = new DockPanel();
            nameCell.Children.Add(_tag); // docked first so the long name is trimmed before the tag
            nameCell.Children.Add(_name);
            Place(nameCell, 0);
            _menuItem.Click += (_, _) => request(key, _sendOn);
            var menu = new ContextMenu();
            menu.Items.Add(_menuItem);
            menu.Opened += (_, _) => { MenuOpen = true; UpdateHover(); };
            menu.Closed += (_, _) => { MenuOpen = false; UpdateHover(); };
            _root.ContextMenu = menu;
            _root.MouseEnter += (_, _) => UpdateHover();
            _root.MouseLeave += (_, _) => UpdateHover();
            for (var i = 0; i < _bytes.Length; i++) Place(_bytes[i], i + 1);
            Place(_nowPhone, 7);
            Place(_nowLan, 8);
            Grid.SetColumnSpan(_separator, 9);
            _grid.Children.Add(_separator);
        }

        public UIElement Root => _root;

        /// True while this row's context menu is showing: the list must not reorder under it.
        public bool MenuOpen { get; private set; }

        /// The line under a row; the last row of the list has none.
        public void SetLast(bool last) => _separator.Visibility = last ? Visibility.Collapsed : Visibility.Visible;

        public void Apply(UsageReportRow row)
        {
            _name.Text = row.Name;
            _name.ToolTip = row.Name;
            var tag = UsageReport.TagText(row.Exception);
            _tagText.Text = tag ?? "";
            _tag.Visibility = tag is null ? Visibility.Collapsed : Visibility.Visible;
            _sendOn = !row.Exception.InException;
            _menuItem.Header = UsageReport.MenuText(row.Exception);
            _menuItem.IsEnabled = row.CanChange;
            _bytes[0].Text = ByteFormat.Human(row.PhoneUp);
            _bytes[1].Text = ByteFormat.Human(row.PhoneDown);
            _bytes[2].Text = ByteFormat.Human(row.PhoneBytes);
            _bytes[3].Text = ByteFormat.Human(row.LanUp);
            _bytes[4].Text = ByteFormat.Human(row.LanDown);
            _bytes[5].Text = ByteFormat.Human(row.LanBytes);
            ApplyRate(_nowPhone, row.Now?.PhoneBytesPerSecond ?? 0, row.NowPhoneUp);
            ApplyRate(_nowLan, row.Now?.LanBytesPerSecond ?? 0, row.NowLanUp);
        }

        /// The row is highlighted while the mouse is over it or its menu is open.
        void UpdateHover()
        {
            var on = _root.IsMouseOver || MenuOpen;
            if (on == _hovered) return;
            _hovered = on;
            if (on) _root.SetResourceReference(Border.BackgroundProperty, "ButtonBackground");
            else _root.Background = Brushes.Transparent; // replaces the reference; transparent keeps the row hit-testable
        }

        static TextBlock Number() => new()
        {
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, CellInset, 0),
        };

        /// Active speeds use the accent colour, idle ones the dimmed text colour. Only re-pointed when the state flips.
        static void ApplyRate(TextBlock cell, long bytesPerSecond, long uploadBytesPerSecond)
        {
            cell.Text = UsageReport.NowSplitText(bytesPerSecond, uploadBytesPerSecond);
            var key = bytesPerSecond > 0 ? "Accent" : "TextSecondary";
            if (!ReferenceEquals(cell.Tag, key))
            {
                cell.Tag = key;
                cell.SetResourceReference(TextBlock.ForegroundProperty, key);
            }
        }

        void Place(UIElement element, int column)
        {
            Grid.SetColumn(element, column);
            _grid.Children.Add(element);
        }
    }
}
