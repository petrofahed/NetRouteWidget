using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NetRoute.Core;

namespace NetRoute.App;

/// Management page. Rebuilt from a PageModel only when its structure changes; otherwise updated in place.
/// Keeps which groups are expanded.
public partial class SmartRoutingWindow : Window
{
    readonly HashSet<string> _expanded = new() { "video" };
    readonly Dictionary<string, CheckBox> _groupBoxes = new(), _itemBoxes = new(), _ruleBoxes = new();
    readonly Dictionary<string, TextBlock> _groupBytes = new(), _itemBytes = new(), _ruleBytes = new();
    readonly Dictionary<string, Control> _controls = new(); // by Tag, to restore keyboard focus after a rebuild
    readonly Dictionary<string, bool?> _groupOn = new(); // the model's state per group (null = mixed)
    readonly List<Button> _addButtons = [];
    string? _structureKey;

    public SmartRoutingWindow() => InitializeComponent();

    public event Action<bool>? MasterToggled;
    public event Action<string, bool>? ItemToggled;
    public event Action<string, bool>? GroupToggled;
    public event Action<UserRule, bool>? UserRuleToggled;
    public event Action<UserRule>? UserRuleRemoved;
    public event Action<UserRuleType>? AddRuleRequested;
    public event Action? UsePhoneRequested;

    /// Rebuilds the controls only when the structure (groups, items, user rules) changed; otherwise updates them in place,
    /// so the frequent stats publishes do not steal keyboard focus or swallow an in-flight click.
    public void Render(PageModel model)
    {
        var key = StructureKey(model);
        if (key != _structureKey)
        {
            var focused = (Keyboard.FocusedElement as FrameworkElement)?.Tag as string;
            Rebuild(model);
            _structureKey = key;
            if (focused is not null && _controls.TryGetValue(focused, out var control))
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => control.Focus())); // the new tree is not laid out yet
        }
        Update(model);
    }

    /// Ids, names and rule values only: no state, no numbers.
    static string StructureKey(PageModel model)
    {
        var sb = new StringBuilder();
        foreach (var group in model.Groups)
        {
            sb.Append('G').Append(group.Id).Append('\u001f').Append(group.Name);
            foreach (var item in group.Items) sb.Append('\u001e').Append(item.Id).Append('\u001f').Append(item.Name);
            sb.Append('\n');
        }
        foreach (var rule in model.UserRules) sb.Append('U').Append((int)rule.Rule.Type).Append('\u001f').Append(rule.Rule.Value).Append('\n');
        return sb.ToString();
    }

    void Rebuild(PageModel model)
    {
        _groupBoxes.Clear();
        _groupBytes.Clear();
        _itemBoxes.Clear();
        _itemBytes.Clear();
        _ruleBoxes.Clear();
        _ruleBytes.Clear();
        _groupOn.Clear();
        _addButtons.Clear();
        _controls.Clear();

        Body.Children.Clear();
        foreach (var group in model.Groups) Body.Children.Add(GroupView(group));
        Body.Children.Add(UserRulesView(model));
    }

    /// Applies every piece of state to the existing controls. Setting IsChecked programmatically raises no Click,
    /// and nothing here subscribes to Checked/Unchecked.
    void Update(PageModel model)
    {
        MasterToggle.IsChecked = model.Enabled;
        StatusText.Text = model.Status.Text;
        TotalText.Text = $"Kept off 4G today: {ByteFormat.Human(model.TotalToday)}";
        UsePhoneButton.Visibility = model.CanUsePhone ? Visibility.Visible : Visibility.Collapsed;

        foreach (var group in model.Groups)
        {
            _groupOn[group.Id] = group.On;
            if (_groupBoxes.TryGetValue(group.Id, out var groupBox)) { groupBox.IsChecked = group.On; groupBox.IsEnabled = model.Enabled; }
            if (_groupBytes.TryGetValue(group.Id, out var groupBytes)) groupBytes.Text = ByteFormat.Human(group.TodayBytes);
            foreach (var item in group.Items)
            {
                if (_itemBoxes.TryGetValue(item.Id, out var box)) { box.IsChecked = item.On; box.IsEnabled = model.Enabled; }
                if (_itemBytes.TryGetValue(item.Id, out var bytes)) bytes.Text = item.On ? ByteFormat.Human(item.TodayBytes) : "→ via phone";
            }
        }
        foreach (var rule in model.UserRules)
        {
            var id = UserRule.IdOf(rule.Rule);
            if (_ruleBoxes.TryGetValue(id, out var box)) { box.IsChecked = rule.Rule.Enabled; box.IsEnabled = model.Enabled; }
            if (_ruleBytes.TryGetValue(id, out var bytes)) bytes.Text = ByteFormat.Human(rule.TodayBytes);
        }
        foreach (var button in _addButtons) button.IsEnabled = model.Enabled;
    }

    UIElement GroupView(PageGroup group)
    {
        var groupToggle = new CheckBox { IsThreeState = false, VerticalAlignment = VerticalAlignment.Center, Tag = "g:" + group.Id };
        // Do NOT read groupToggle.IsChecked here: WPF has already flipped it, and its cycle is true->false, false->true, null->false,
        // so a mixed group would read "off". Decide from the model's state instead; Update then overwrites the box with the new model.
        groupToggle.Click += (_, _) => GroupToggled?.Invoke(group.Id, SmartRoutingPage.NextGroupState(_groupOn.GetValueOrDefault(group.Id)));
        _groupOn[group.Id] = group.On;
        _groupBoxes[group.Id] = groupToggle;
        _controls[(string)groupToggle.Tag] = groupToggle;
        var header = new DockPanel();
        header.Children.Add(groupToggle);
        var bytes = new TextBlock { Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(12, 0, 0, 0) };
        _groupBytes[group.Id] = bytes;
        DockPanel.SetDock(bytes, Dock.Right);
        header.Children.Add(bytes);
        header.Children.Add(new TextBlock { Text = group.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 0, 0, 0) });

        var items = new StackPanel { Margin = new Thickness(28, 4, 0, 4) };
        foreach (var item in group.Items)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var toggle = new CheckBox { Content = item.Name, Tag = "i:" + item.Id };
            toggle.Click += (_, _) => ItemToggled?.Invoke(item.Id, toggle.IsChecked == true);
            var itemBytes = new TextBlock { Foreground = System.Windows.Media.Brushes.Gray };
            _itemBoxes[item.Id] = toggle;
            _itemBytes[item.Id] = itemBytes;
            _controls[(string)toggle.Tag] = toggle;
            DockPanel.SetDock(itemBytes, Dock.Right);
            row.Children.Add(itemBytes);
            row.Children.Add(toggle);
            items.Children.Add(row);
        }

        var expander = new Expander { Header = header, Content = items, IsExpanded = _expanded.Contains(group.Id), Margin = new Thickness(0, 4, 0, 4) };
        expander.Expanded += (_, _) => _expanded.Add(group.Id);
        expander.Collapsed += (_, _) => _expanded.Remove(group.Id);
        return expander;
    }

    UIElement UserRulesView(PageModel model)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var header = new DockPanel();
        var addWebsite = new Button { Content = "+ Website…", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0), Tag = "add:website" };
        addWebsite.Click += (_, _) => AddRuleRequested?.Invoke(UserRuleType.Website);
        var addApp = new Button { Content = "+ App…", Padding = new Thickness(8, 2, 8, 2), Tag = "add:app" };
        addApp.Click += (_, _) => AddRuleRequested?.Invoke(UserRuleType.App);
        _addButtons.Add(addWebsite);
        _addButtons.Add(addApp);
        _controls["add:website"] = addWebsite;
        _controls["add:app"] = addApp;
        DockPanel.SetDock(addWebsite, Dock.Right);
        DockPanel.SetDock(addApp, Dock.Right);
        header.Children.Add(addWebsite);
        header.Children.Add(addApp);
        header.Children.Add(new TextBlock { Text = "My rules", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(header);

        if (model.UserRules.Count == 0)
            panel.Children.Add(new TextBlock { Text = "No rules yet — add an app or a website.", Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(28, 4, 0, 0) });

        foreach (var rule in model.UserRules)
        {
            var id = UserRule.IdOf(rule.Rule);
            var row = new DockPanel { Margin = new Thickness(28, 2, 0, 2) };
            var remove = new Button { Content = "🗑", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(8, 0, 0, 0), ToolTip = "Remove", Tag = "rm:" + id };
            remove.Click += (_, _) => UserRuleRemoved?.Invoke(rule.Rule);
            var bytes = new TextBlock { Foreground = System.Windows.Media.Brushes.Gray, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(remove, Dock.Right);
            DockPanel.SetDock(bytes, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(bytes);
            var toggle = new CheckBox
            {
                Content = $"{(rule.Rule.Type == UserRuleType.App ? "App" : "Website")}: {rule.Rule.Value}",
                Tag = "r:" + id,
            };
            toggle.Click += (_, _) => UserRuleToggled?.Invoke(rule.Rule, toggle.IsChecked == true);
            _ruleBoxes[id] = toggle;
            _ruleBytes[id] = bytes;
            _controls[(string)toggle.Tag] = toggle;
            _controls[(string)remove.Tag] = remove;
            row.Children.Add(toggle);
            panel.Children.Add(row);
        }
        return panel;
    }

    void OnMaster(object sender, RoutedEventArgs e) => MasterToggled?.Invoke(MasterToggle.IsChecked == true);

    void OnUsePhone(object sender, RoutedEventArgs e) => UsePhoneRequested?.Invoke();
}
