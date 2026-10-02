using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Threading;
using NetRoute.Core;
using NetRoute.Core.Windows;

namespace NetRoute.App;

public partial class App : Application
{
    static readonly TimeSpan VisiblePoll = TimeSpan.FromSeconds(5);
    static readonly TimeSpan HiddenPoll = TimeSpan.FromSeconds(30);

    readonly SingleInstance _instance = new();
    readonly StartupTask _startupTask = new();
    readonly DispatcherTimer _poll = new();
    FileLog? _log;
    RouteController? _controller;
    TrayIcon? _tray;
    CardWindow? _card;
    Debouncer? _debouncer;
    bool _startupEnabled;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!_instance.TryAcquire(waitForPrevious: e.Args.Contains("--replace")))
        {
            Shutdown();
            return;
        }

        var log = _log = new FileLog(AppPaths.LogDir);
        log.PruneOldFiles();
        DispatcherUnhandledException += (_, args) =>
        {
            log.Error("Unhandled UI exception", args.Exception);
            args.Handled = true;
        };

        var store = new SettingsStore(AppPaths.SettingsFile);
        var loaded = store.Load();
        if (loaded.Recovered) log.Error("Settings file was corrupt; defaults restored");
        var elevated = Elevation.IsElevated();
        _startupEnabled = _startupTask.Exists();
        log.Info($"Starting: elevated={elevated}, mode={loaded.Settings.Mode}, startWithWindows={_startupEnabled}");

        var controller = _controller = new RouteController(
            new WindowsAdapterSource(), new WindowsInterfaceMetrics(), new WindowsRouteQuery(), TcpLatencyProbe.Default(),
            loaded.Settings, elevated, store.Save, log.Info);
        controller.StatusChanged += status => Dispatcher.InvokeAsync(() => Render(status));
        controller.AutoSwitched += message => Dispatcher.InvokeAsync(() => _tray?.Notify(message));

        _tray = new TrayIcon();
        _tray.ModeRequested += mode => _ = controller.SetModeAsync(mode);
        _tray.ToggleCardRequested += ToggleCard;
        _tray.ShowCardRequested += ShowCard;
        _tray.StartWithWindowsToggled += ToggleStartWithWindows;
        _tray.QuitRequested += Quit;

        _card = new CardWindow();
        _card.ModeRequested += mode => _ = controller.SetModeAsync(mode);
        _card.HideRequested += HideCard;
        _card.Moved += (left, top) => controller.UpdateSettings(s => s with { CardLeft = left, CardTop = top });
        _card.StartWithWindowsToggled += ToggleStartWithWindows;
        _card.OpenNetworkSettingsRequested += OpenNetworkSettings;
        _card.RestartAsAdminRequested += RestartAsAdmin;
        _card.QuitRequested += Quit;

        _instance.ListenForShow(() => Dispatcher.InvokeAsync(ShowCard));

        _debouncer = new Debouncer(TimeSpan.FromMilliseconds(1500), () => _ = controller.RefreshAsync(measureLatency: true));
        NetworkChange.NetworkAddressChanged += OnNetworkEvent;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkEvent;
        _poll.Tick += async (_, _) => await controller.RefreshAsync(measureLatency: true);

        Render(controller.Status);
        if (loaded.Settings.CardVisible) ShowCard();
        UpdatePollInterval();
        _poll.Start();
        await controller.RefreshAsync(measureLatency: true);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkEvent;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkEvent;
        _poll.Stop();
        _debouncer?.Dispose();
        _tray?.Dispose();
        _log?.Info("Exiting; routing left as-is");
        _instance.Dispose();
        base.OnExit(e);
    }

    void OnNetworkEvent(object? sender, EventArgs e) => _debouncer?.Signal();

    void Render(NetworkStatus status)
    {
        var view = StatusPresenter.Present(status);
        _tray?.Update(view, status.Mode, status.CanModify, _startupEnabled);
        _card?.Render(view, status.Mode, status.CanModify, _startupEnabled);
    }

    void ShowCard()
    {
        var settings = _controller!.Settings;
        _card!.ShowAndPlace(settings.CardLeft, settings.CardTop);
        if (!settings.CardVisible) _controller.UpdateSettings(s => s with { CardVisible = true });
        UpdatePollInterval();
    }

    void HideCard()
    {
        _card!.Hide();
        _controller!.UpdateSettings(s => s with { CardVisible = false });
        UpdatePollInterval();
    }

    void ToggleCard()
    {
        if (_card!.IsVisible) HideCard();
        else ShowCard();
    }

    void UpdatePollInterval() => _poll.Interval = _card?.IsVisible == true ? VisiblePoll : HiddenPoll;

    void ToggleStartWithWindows()
    {
        try
        {
            if (_startupEnabled) _startupTask.Disable();
            else _startupTask.Enable(Environment.ProcessPath!);
            _startupEnabled = _startupTask.Exists();
            _controller!.UpdateSettings(s => s with { StartWithWindows = _startupEnabled });
            _log!.Info($"Start with Windows: {_startupEnabled}");
        }
        catch (Exception ex)
        {
            _log!.Error("Changing Start with Windows failed", ex);
            MessageBox.Show($"Could not change Start with Windows:\n{ex.Message}", "NetRoute Widget",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Render(_controller!.Status);
    }

    static void OpenNetworkSettings() =>
        Process.Start(new ProcessStartInfo("ms-settings:network") { UseShellExecute = true });

    void RestartAsAdmin()
    {
        if (Elevation.TryStartElevated("--replace")) Quit();
    }

    void Quit()
    {
        _card?.ForceClose();
        Shutdown();
    }
}
