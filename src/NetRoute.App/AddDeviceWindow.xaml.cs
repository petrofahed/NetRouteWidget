using System.Windows;
using System.Windows.Controls;
using NetRoute.Core.PathMap;

namespace NetRoute.App;

public partial class AddDeviceWindow : Window
{
    readonly IReadOnlyList<LocalDevice> _existing;

    public AddDeviceWindow(IReadOnlyList<LocalDevice> existing, string name = "", string address = "")
    {
        InitializeComponent();
        Theme.Track(this);
        _existing = existing;
        NameBox.Text = name;
        AddressBox.Text = address;
        Loaded += (_, _) => AddressBox.Focus();
        AddressBox.TextChanged += (_, _) => Error.Visibility = Visibility.Collapsed;
    }

    public LocalDevice? Result { get; private set; }

    void OnAdd(object sender, RoutedEventArgs e)
    {
        if (LocalDeviceValidator.TryCreate(NameBox.Text, AddressBox.Text, _existing, out var device, out var error))
        {
            Result = device;
            DialogResult = true;
            return;
        }
        Error.Text = error;
        Error.Visibility = Visibility.Visible;
    }
}
