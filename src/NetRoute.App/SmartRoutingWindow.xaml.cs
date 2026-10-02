using System.Windows;
using System.Windows.Controls;
using NetRoute.Core;

namespace NetRoute.App;

/// Management page. Rebuilt from a PageModel on every change; keeps which groups are expanded.
public partial class SmartRoutingWindow : Window
{
    readonly HashSet<string> _expanded = new() { "video" };

    public SmartRoutingWindow() => InitializeComponent();

    public event Action<bool>? MasterToggled;
    public event Action<string, bool>? ItemToggled;
    public event Action<string, bool>? GroupToggled;
    public event Action<UserRule, bool>? UserRuleToggled;
    public event Action<UserRule>? UserRuleRemoved;
    public event Action<UserRuleType>? AddRuleRequested;
    public event Action? UsePhoneRequested;

    public void Render(PageModel model)
    {
        MasterToggle.IsChecked = model.Enabled;
        StatusText.Text = model.Status.Text;
        TotalText.Text = $"Kept off 4G today: {ByteFormat.Human(model.TotalToday)}";
        UsePhoneButton.Visibility = model.CanUsePhone ? Visibility.Visible : Visibility.Collapsed;

        Body.Children.Clear();
        foreach (var group in model.Groups) Body.Children.Add(GroupView(group, model.Enabled));
        Body.Children.Add(UserRulesView(model));
    }

    UIElement GroupView(PageGroup group, bool enabled)
    {
        var groupToggle = new CheckBox { IsThreeState = false, IsChecked = group.On, IsEnabled = enabled, VerticalAlignment = VerticalAlignment.Center };
        groupToggle.Click += (_, _) => GroupToggled?.Invoke(group.Id, group.On != true);
        var header = new DockPanel();
        header.Children.Add(groupToggle);
        var bytes = new TextBlock { Text = ByteFormat.Human(group.TodayBytes), Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(12, 0, 0, 0) };
        DockPanel.SetDock(bytes, Dock.Right);
        header.Children.Add(bytes);
        header.Children.Add(new TextBlock { Text = group.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 0, 0, 0) });

        var items = new StackPanel { Margin = new Thickness(28, 4, 0, 4) };
        foreach (var item in group.Items)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var toggle = new CheckBox { IsChecked = item.On, IsEnabled = enabled, Content = item.Name };
            toggle.Click += (_, _) => ItemToggled?.Invoke(item.Id, toggle.IsChecked == true);
            var itemBytes = new TextBlock { Text = item.On ? ByteFormat.Human(item.TodayBytes) : "→ via phone", Foreground = System.Windows.Media.Brushes.Gray };
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
        var addWebsite = new Button { Content = "+ Website…", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0), IsEnabled = model.Enabled };
        addWebsite.Click += (_, _) => AddRuleRequested?.Invoke(UserRuleType.Website);
        var addApp = new Button { Content = "+ App…", Padding = new Thickness(8, 2, 8, 2), IsEnabled = model.Enabled };
        addApp.Click += (_, _) => AddRuleRequested?.Invoke(UserRuleType.App);
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
            var row = new DockPanel { Margin = new Thickness(28, 2, 0, 2) };
            var remove = new Button { Content = "🗑", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(8, 0, 0, 0), ToolTip = "Remove" };
            remove.Click += (_, _) => UserRuleRemoved?.Invoke(rule.Rule);
            var bytes = new TextBlock { Text = ByteFormat.Human(rule.TodayBytes), Foreground = System.Windows.Media.Brushes.Gray, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(remove, Dock.Right);
            DockPanel.SetDock(bytes, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(bytes);
            var toggle = new CheckBox
            {
                IsChecked = rule.Rule.Enabled, IsEnabled = model.Enabled,
                Content = $"{(rule.Rule.Type == UserRuleType.App ? "App" : "Website")}: {rule.Rule.Value}",
            };
            toggle.Click += (_, _) => UserRuleToggled?.Invoke(rule.Rule, toggle.IsChecked == true);
            row.Children.Add(toggle);
            panel.Children.Add(row);
        }
        return panel;
    }

    void OnMaster(object sender, RoutedEventArgs e) => MasterToggled?.Invoke(MasterToggle.IsChecked == true);

    void OnUsePhone(object sender, RoutedEventArgs e) => UsePhoneRequested?.Invoke();
}
