using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Threading;
using NetRoute.Core;
using NetRoute.Core.Windows;

namespace NetRoute.App;

public partial class App : Application
{
    static readonly TimeSpan VisiblePoll = TimeSpan.FromSeconds(5);
    static readonly TimeSpan HiddenPoll = TimeSpan.FromSeconds(10);

    readonly SingleInstance _instance = new();
    readonly StartupTask _startupTask = new();
    readonly DispatcherTimer _poll = new();
    FileLog? _log;
    RouteController? _controller;
    TrayIcon? _tray;
    CardWindow? _card;
    Debouncer? _debouncer;
    bool _startupEnabled;
    DateTime _lastPruneDay;
    SmartRoutingController? _smart;
    IReadOnlyList<RuleItem> _catalog = [];
    long _lastSavedStatsTotal = -1;
    bool _speedTestRunning;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!_instance.TryAcquire(waitForPrevious: e.Args.Contains("--replace")))
        {
            Shutdown();
            return;
        }

        // Anything failing from here on must not leave a process running with no tray icon.
        try
        {
            var log = _log = new FileLog(AppPaths.LogDir);
            DispatcherUnhandledException += (_, args) =>
            {
                log.Error("Unhandled UI exception", args.Exception);
                args.Handled = true;
            };
            PruneLogs();

            var store = new SettingsStore(AppPaths.SettingsFile);
            var loaded = store.Load();
            if (loaded.Unreadable) log.Error("Settings file could not be read; running on defaults and NOT saving changes this session");
            else if (loaded.Recovered) log.Error("Settings file was unreadable or corrupt; using defaults");
            var elevated = Elevation.IsElevated();
            _startupEnabled = _startupTask.Exists();
            log.Info($"Starting: elevated={elevated}, mode={loaded.Settings.Mode}, startWithWindows={_startupEnabled}");

            var controller = _controller = new RouteController(
                new WindowsAdapterSource(), new WindowsInterfaceMetrics(), new WindowsRouteQuery(), TcpLatencyProbe.Default(),
                loaded.Settings, elevated, loaded.Unreadable ? _ => { } : store.Save, log.Info);
            controller.StatusChanged += status => Dispatcher.BeginInvoke(new Action(() => Render(status)));
            controller.AutoSwitched += message => Dispatcher.BeginInvoke(new Action(() => _tray?.Notify(message)));

            _catalog = RuleCatalog.Load(RuleCatalog.DefaultPath);
            var orphans = SingBoxHost.KillOrphans(AppPaths.SingBoxExe);
            if (orphans > 0) log.Info($"Stopped {orphans} leftover sing-box process(es)");
            var host = new SingBoxHost(AppPaths.SingBoxExe); // config is passed on stdin: nothing is written to a user-writable file
            host.LineReceived += line =>
            {
                if (line.Contains("ERROR") || line.Contains("FATAL") || line.Contains("WARN")) log.Info("sing-box: " + line);
            };
            var smart = _smart = new SmartRoutingController(
                _catalog, host, (port, secret) => new SingBoxApi(port, secret), FreePort.Next,
                DataSavedCounter.LoadFile(AppPaths.StatsFile), log.Info);
            smart.StatusChanged += s => Dispatcher.BeginInvoke(new Action(() => RenderSmart(s)));
            smart.Notify += m => Dispatcher.BeginInvoke(new Action(() => _tray?.Notify(m)));
            smart.WaitingDetected += names => Dispatcher.BeginInvoke(new Action(() =>
                _tray?.Notify(SmartRoutingPresenter.WaitingText(names))));
            controller.ExternalPathResolver = index =>
                smart.Status.IsActive && index == TunIndex() // TUN looked up by name on every call: its index changes on each sing-box start
                    ? (smart.Status.DefaultExit == RouteExit.Lan ? InternetPath.Lan : InternetPath.Phone) : null;
            controller.StatusChanged += status => _ = smart.ApplyAsync(status, controller.Settings);

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
            _card.ChooseAdaptersRequested += ChooseAdapters;
            _card.RestartAsAdminRequested += RestartAsAdmin;
            _card.QuitRequested += Quit;
            _card.SpeedTestRequested += RunSpeedTest;

            _instance.ListenForShow(() => Dispatcher.BeginInvoke(new Action(ShowCard)));

            _debouncer = new Debouncer(TimeSpan.FromMilliseconds(1500), () => _ = controller.RefreshAsync(measureLatency: true));
            NetworkChange.NetworkAddressChanged += OnNetworkEvent;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkEvent;
            _poll.Tick += async (_, _) =>
            {
                if (DateTime.Today != _lastPruneDay) PruneLogs(); // the widget may run for days
                await controller.RefreshAsync(measureLatency: true);
                await smart.PollStatsAsync();
                SaveStatsIfChanged();
            };

            Render(controller.Status);
            if (loaded.Settings.CardVisible) ShowCard();
            UpdatePollInterval();
            _poll.Start();
            await controller.RefreshAsync(measureLatency: true);
            _ = smart.ApplyAsync(controller.Status, controller.Settings); // first status exists now: starts Smart routing when it was enabled in settings
        }
        catch (Exception ex)
        {
            _log?.Error("Startup failed", ex);
            MessageBox.Show($"NetRoute Widget could not start: {ex.Message}", "NetRoute Widget",
                MessageBoxButton.OK, MessageBoxImage.Error);
            _card?.ForceClose(); // a real close, so OnClosing does not cancel it or persist CardVisible=false
            Shutdown();
        }
    }

    void PruneLogs()
    {
        _lastPruneDay = DateTime.Today;
        _log?.PruneOldFiles();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkEvent;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkEvent;
        _poll.Stop();
        _debouncer?.Dispose();
        StopSmartRouting();
        _tray?.Dispose();
        _log?.Info("Exiting; routing left as-is");
        _instance.Dispose();
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        StopSmartRouting();
        _card?.ForceClose(); // a real close, so OnClosing does not persist CardVisible=false
        base.OnSessionEnding(e);
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

    async void ChooseAdapters()
    {
        var candidates = AdapterDetector.Candidates(new WindowsAdapterSource().GetAdapters()).ToList();
        var picker = new AdapterPickerWindow(candidates, _controller!.Settings.Overrides);
        if (picker.ShowDialog() == true) await _controller.SetOverridesAsync(picker.Result);
    }

    static void OpenNetworkSettings() =>
        Process.Start(new ProcessStartInfo("ms-settings:network") { UseShellExecute = true });

    void RestartAsAdmin()
    {
        if (Elevation.TryStartElevated("--replace")) Quit();
    }

    void Quit()
    {
        StopSmartRouting();
        _card?.ForceClose();
        Shutdown();
    }

    static int? TunIndex()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.Name != "NetRoute") continue;
            try { return nic.GetIPProperties().GetIPv4Properties()?.Index; }
            catch (NetworkInformationException) { return null; }
        }
        return null;
    }

    void UpdateSmart(Func<SmartRoutingSettings, SmartRoutingSettings> change)
    {
        _controller!.UpdateSettings(s => s with { SmartRouting = change(s.SmartRouting) });
        _ = _smart!.ApplyAsync(_controller.Status, _controller.Settings);
    }

    void RenderSmart(SmartRoutingStatus status) => _card?.RenderSmart(SmartRoutingPresenter.Row(status));

    void SaveStatsIfChanged()
    {
        if (_smart is not { } smart || smart.Status.Today.Total == _lastSavedStatsTotal) return;
        try
        {
            DataSavedCounter.SaveFile(AppPaths.StatsFile, smart.Status.Today);
            _lastSavedStatsTotal = smart.Status.Today.Total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Error("Saving stats failed", ex);
        }
    }

    async void RunSpeedTest()
    {
        if (_speedTestRunning || _controller is null) return;
        _speedTestRunning = true;
        _card?.ShowSpeed("⚡ Measuring… (about 5 MB of mobile data)");
        try
        {
            var result = await SpeedTest.RunAsync(HttpSpeedProbe.Default(), _controller.Status.Adapters);
            _card?.ShowSpeed($"{SpeedTest.Describe(result)}  ({DateTime.Now:HH:mm})");
            _log?.Info("Speed test: " + SpeedTest.Describe(result));
        }
        finally
        {
            _speedTestRunning = false;
        }
    }

    /// Stops sing-box for good at quit (ShutdownAsync latches the controller). The controller awaits with
    /// ConfigureAwait(false), so waiting here on the UI thread cannot deadlock.
    void StopSmartRouting()
    {
        if (_smart is not { } smart) return;
        SaveStatsIfChanged();
        Task.Run(() => smart.ShutdownAsync()).Wait(TimeSpan.FromSeconds(5)); // ShutdownAsync latches: nothing restarts sing-box afterwards
    }
}
