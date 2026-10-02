using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using NetRoute.Core;

namespace NetRoute.App;

public partial class AddRuleWindow : Window
{
    readonly UserRuleType _type;

    public AddRuleWindow(UserRuleType type)
    {
        InitializeComponent();
        _type = type;
        Loaded += (_, _) => Input.Focus();
        // An editable ComboBox's inner TextBox raises TextChanged, which bubbles up to the ComboBox.
        Input.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new System.Windows.Controls.TextChangedEventHandler((_, _) => Error.Visibility = Visibility.Collapsed));
        if (type == UserRuleType.App)
        {
            Title = "Add app";
            Prompt.Text = "App to keep off 4G — pick a running app or type its .exe name:";
            Input.ItemsSource = RunningExeNames();
        }
        else
        {
            Title = "Add website";
            Prompt.Text = "Website to keep off 4G (its subdomains are included), e.g. netflix.com:";
            BrowseButton.Visibility = Visibility.Collapsed;
        }
    }

    public UserRule? Result { get; private set; }

    /// Process names of everything running. A process can exit (or deny access) mid-enumeration: skip it, never throw.
    static List<string> RunningExeNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or PlatformNotSupportedException)
        {
            return []; // the list is a convenience: the user can still type or browse
        }
        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    names.Add(process.ProcessName + ".exe");
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // exited meanwhile, or not accessible
                }
            }
        }
        return [.. names.Order(StringComparer.OrdinalIgnoreCase)];
    }

    void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe" };
        if (dialog.ShowDialog(this) == true) Input.Text = dialog.FileName;
    }

    void OnAdd(object sender, RoutedEventArgs e)
    {
        if (UserRule.TryCreate(_type, Input.Text ?? "", out var rule, out var error))
        {
            Result = rule;
            DialogResult = true;
            return;
        }
        Error.Text = error;
        Error.Visibility = Visibility.Visible;
    }
}
