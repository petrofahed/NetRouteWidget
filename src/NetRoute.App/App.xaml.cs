using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Threading;
using NetRoute.Core;
using NetRoute.Core.PathMap;
using NetRoute.Core.Windows;

namespace NetRoute.App;

public partial class App : Application
{
    static readonly TimeSpan VisiblePoll = TimeSpan.FromSeconds(5);
    static readonly TimeSpan HiddenPoll = TimeSpan.FromSeconds(10);
    static readonly TimeSpan UsagePollInterval = TimeSpan.FromSeconds(1);
    static readonly TimeSpan UsageSaveInterval = TimeSpan.FromSeconds(30);

    readonly SingleInstance _instance = new();
    readonly StartupTask _startupTask = new();
    readonly DispatcherTimer _poll = new();
    readonly DispatcherTimer _usagePoll = new() { Interval = UsagePollInterval };
    readonly DispatcherTimer _usageSave = new() { Interval = UsageSaveInterval };
    bool _usagePolling;
    UsageSort _usageSort = UsageSort.Default;
    string _usageFilter = ""; // the Usage tab's filter box; per session, a new window starts empty
    ConnectionId? _editingProfile; // the Config tab's view; null = follow the active profile
    int _renderSeq;
    bool _renderRunning, _renderAgain;
    FileLog? _log;
    RouteController? _controller;
    TrayIcon? _tray;
    CardWindow? _card;
    Debouncer? _debouncer;
    bool _startupEnabled;
    DateTime _lastPruneDay;
    SmartRoutingController? _smart;
    IReadOnlyList<RuleItem> _catalog = [];
    bool _usageUnreadable;
    bool _usageSaveFailed;
    bool _speedTestRunning;
    bool _smartStopped;
    SmartRoutingWindow? _smartWindow;
    PathMapWindow? _pathWindow;
    WaitingPopup? _waitingPopup;

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

            var settings = loaded.Settings;
            if (!loaded.Unreadable)
            {
                try
                {
                    var migrated = SettingsMigration.ToV6(settings, new WindowsAdapterSource().GetAdapters());
                    if (migrated != settings)
                    {
                        store.Save(migrated); // written before settings is updated: if this throws, in-memory and on-disk stay the old, unmigrated settings together
                        settings = migrated;
                        log.Info("Settings migrated to v6");
                    }
                }
                catch (Exception ex)
                {
                    // Migration is a one-shot convenience, retried on every start until it succeeds: a failure here
                    // (adapter enumeration or the settings write) must not stop the widget from starting on defaults.
                    log.Error("Settings migration to v6 failed; continuing unmigrated", ex);
                }
            }

            var elevated = Elevation.IsElevated();
            _startupEnabled = _startupTask.Exists();
            log.Info($"Starting: elevated={elevated}, mode={settings.Mode}, startWithWindows={_startupEnabled}");

            var controller = _controller = new RouteController(
                new WindowsAdapterSource(), new WindowsInterfaceMetrics(), new WindowsRouteQuery(), TcpLatencyProbe.Default(),
                settings, elevated, loaded.Unreadable ? _ => { } : store.Save, log.Info,
                subnetRoutes: new WindowsSubnetRoutes(), names: new WindowsNetworkNames());
            controller.StatusChanged += status => Dispatcher.BeginInvoke(new Action(() => Render(status)));
            controller.AutoSwitched += message => Dispatcher.BeginInvoke(new Action(() => _tray?.Notify(message)));

            SetUpSmartRouting(controller, log);

            _tray = new TrayIcon();
            _tray.ModeRequested += mode => _ = controller.SetModeAsync(mode);
            _tray.ToggleCardRequested += ToggleCard;
            _tray.ShowCardRequested += ShowCard;
            _tray.StartWithWindowsToggled += ToggleStartWithWindows;
            _tray.AutoHealToggled += ToggleAutoHeal;
            _tray.QuitRequested += Quit;

            _card = new CardWindow();
            _card.ModeRequested += mode => _ = controller.SetModeAsync(mode);
            _card.TurnedOffRequested += (id, off) => _ = controller.SetTurnedOffAsync(id, off);
            _card.HideRequested += HideCard;
            _card.Moved += (left, top) => controller.UpdateSettings(s => s with { CardLeft = left, CardTop = top });
            _card.StartWithWindowsToggled += ToggleStartWithWindows;
            _card.AutoHealToggled += ToggleAutoHeal;
            _card.OpenNetworkSettingsRequested += OpenNetworkSettings;
            _card.ChooseAdaptersRequested += ChooseAdapters;
            _card.RestartAsAdminRequested += RestartAsAdmin;
            _card.QuitRequested += Quit;
            _card.SpeedTestRequested += RunSpeedTest;
            _card.SmartSettingsRequested += OpenSmartRouting;
            _card.PathMapRequested += OpenPathMap;
            _card.SmartToggleRequested += on => { if (_smart is not null) ChangeSmart(s => s with { Enabled = on }); };

            _instance.ListenForShow(() => Dispatcher.BeginInvoke(new Action(ShowCard)));

            _debouncer = new Debouncer(TimeSpan.FromMilliseconds(1500), () => _ = controller.RefreshAsync(measureLatency: true));
            NetworkChange.NetworkAddressChanged += OnNetworkEvent;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkEvent;
            _poll.Tick += async (_, _) =>
            {
                if (DateTime.Today != _lastPruneDay) PruneLogs(); // the widget may run for days
                await controller.RefreshAsync(measureLatency: true);
            };

            Render(controller.Status);
            RenderTotals(_smart); // restored history shows before the first poll
            if (settings.CardVisible) ShowCard();
            UpdatePollInterval();
            _poll.Start();
            if (_smart is { } smartForUsage)
            {
                // One second: short connections would be missed by a slower poll (see the v3 spec, "Why a plain connection poll is not enough").
                _usagePoll.Tick += async (_, _) =>
                {
                    var owner = false; // only the tick that took the guard may release it
                    try
                    {
                        // Before the guard: while the gate is held (a restart or stop can take seconds) polls are skipped, and the
                        // controller has already zeroed the speed or Smart routing has left Running, so a stale figure clears now.
                        RenderLiveSpeeds(smartForUsage);
                        if (_usagePolling) return; // the previous poll is still waiting for the API
                        _usagePolling = owner = true;
                        await smartForUsage.PollStatsAsync();
                        RenderLiveSpeeds(smartForUsage);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log.Error("Usage poll failed", ex); // an async void handler must never throw: it would end the app
                    }
                    finally
                    {
                        if (owner) _usagePolling = false;
                    }
                };
                _usageSave.Tick += async (_, _) =>
                {
                    try
                    {
                        await SaveUsageAsync();
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log.Error("Usage save failed", ex); // SaveUsageAsync only catches IO errors
                    }
                };
                _usagePoll.Start();
                _usageSave.Start();
            }
            await controller.RefreshAsync(measureLatency: true); // publishes the first status, which the StatusChanged subscription applies to Smart routing
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
        _usagePoll.Stop();
        _usageSave.Stop();
        _debouncer?.Dispose();
        StopSmartRouting();
        _tray?.Dispose();
        _log?.Info("Exiting; routing left as-is");
        _instance.Dispose();
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Do NOT stop Smart routing here: the sign-out can still be cancelled by another app, and ShutdownAsync latches
        // the controller for good. A real logoff ends the process, and the kill-on-close job object ends sing-box with it.
        Task.Run(() => SaveUsageAsync(force: true)).Wait(TimeSpan.FromSeconds(3));
        _controller?.ReleasePin();
        _card?.ForceClose(); // a real close, so OnClosing does not persist CardVisible=false
        base.OnSessionEnding(e);
    }

    void OnNetworkEvent(object? sender, EventArgs e) => _debouncer?.Signal();

    void Render(NetworkStatus status)
    {
        var view = StatusPresenter.Present(status);
        var autoHeal = _controller?.Settings.AutoHeal ?? true;
        _tray?.Update(view, status.Mode, status.CanModify, _startupEnabled, autoHeal);
        _card?.Render(view, status.Mode, status.CanModify, _startupEnabled, autoHeal);
        if (_smart is { } smart) RenderSmartCard(smart.Status); // the Smart-off line names the mode, which just changed
    }

    void RenderSmartCard(SmartRoutingStatus status)
    {
        if (_card is null || _controller is not { } controller) return;
        _card.RenderSmart(SmartRoutingPresenter.Row(status, controller.Status.Mode), controller.Settings.SmartRouting.Enabled);
    }

    void ShowCard()
    {
        var settings = _controller!.Settings;
        _card!.ShowAndPlace(settings.CardLeft, settings.CardTop);
        if (!settings.CardVisible) _controller.UpdateSettings(s => s with { CardVisible = true });
        RenderTotals(_smart);
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

    void ToggleAutoHeal()
    {
        var on = !_controller!.Settings.AutoHeal;
        _controller.UpdateSettings(s => s with { AutoHeal = on });
        _log!.Info($"Auto-heal: {(on ? "on" : "off")}");
        _ = _controller.RefreshAsync(measureLatency: false); // an active heal ends now and the menu shows the new state
    }

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
        var settings = _controller!.Settings;
        var picker = new AdapterPickerWindow(candidates, settings.Overrides, settings.TurnedOff, new WindowsNetworkNames());
        if (picker.ShowDialog() == true) await _controller.SetAdapterChoicesAsync(picker.Result, picker.TurnedOffResult);
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
        _controller?.ReleasePin();
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

    /// Smart routing is optional: when its setup failed, _smart is null and v1 carries on without it.
    void SetUpSmartRouting(RouteController controller, FileLog log)
    {
        try
        {
            try
            {
                _catalog = RuleCatalog.LoadMerged(RuleCatalog.DefaultPath, AppPaths.UserRulesFile, log.Info);
            }
            catch (Exception ex)
            {
                log.Error("Smart routing rules could not be loaded; Smart routing has no built-in items", ex);
                _catalog = [];
            }
            try
            {
                var orphans = SingBoxHost.KillOrphans(AppPaths.SingBoxExe);
                if (orphans > 0) log.Info($"Stopped {orphans} leftover sing-box process(es)");
            }
            catch (Exception ex)
            {
                log.Error("Looking for leftover sing-box processes failed", ex);
            }

            var host = new SingBoxHost(AppPaths.SingBoxExe); // config is passed on stdin: nothing is written to a user-writable file
            var limiter = new LogRateLimiter(20, TimeSpan.FromMinutes(1)); // a LAN outage makes every failed dial an ERROR line
            host.LineReceived += line =>
            {
                if (!SingBoxLogFilter.IsNoteworthy(line)) return; // not Contains("ERROR"): every DNS answer line says NOERROR
                if (!limiter.TryAllow(out var suppressed)) return;
                if (suppressed > 0) log.Info($"sing-box: {suppressed} more error/warning lines suppressed");
                log.Info("sing-box: " + line);
            };
            UsageLoadResult usage;
            try
            {
                usage = UsageStore.Load(AppPaths.UsageFile, DateOnly.FromDateTime(DateTime.Now));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.Error("usage.json could not be loaded", ex); // a usage-file problem must never disable Smart routing
                usage = new UsageLoadResult(UsageSnapshot.Empty, true); // as unreadable: never overwritten this session
            }
            _usageUnreadable = usage.Unreadable;
            if (usage.Unreadable) log.Error("usage.json could not be read; usage is kept in memory only this session and NOT saved");
            var smart = new SmartRoutingController(
                _catalog, host, (port, secret) => new SingBoxApi(port, secret), FreePort.Next, usage.Usage, log.Info,
                counters: new WindowsAdapterCounters(), traffic: new WindowsTrafficTracker());
            smart.StatusChanged += s => Dispatcher.BeginInvoke(new Action(() => RenderSmart(s)));
            smart.Notify += m => Dispatcher.BeginInvoke(new Action(() => _tray?.Notify(m)));
            smart.WaitingDetected += names => Dispatcher.BeginInvoke(new Action(() => ShowWaitingPopup(names)));

            // Hook into the v1 controller last, so a failure above leaves it untouched.
            controller.ExternalPathResolver = index =>
            {
                var status = smart.Status;
                return SmartRoutingPathResolver.Resolve(status.IsActive, status.DefaultExit, index, TunIndex());
            };
            controller.StatusChanged += status =>
            {
                RememberLan(controller, status, log);
                _ = smart.ApplyAsync(status, controller.Settings);
            };
            _smart = smart;
        }
        catch (Exception ex)
        {
            log.Error("Smart routing could not be set up; continuing without it", ex);
            _smart = null;
        }
    }

    /// Saves the LAN adapter's name so a later start with the LAN absent (cable out at boot, router rebooting) still
    /// binds Smart routing's "lan" outbound to it. Written only when the name changes. It is not part of the rule
    /// fingerprint or the sing-box key, so it never restarts anything.
    static void RememberLan(RouteController controller, NetworkStatus status, FileLog log)
    {
        try
        {
            var name = status.Adapters.Lan?.Name;
            if (name is null || !controller.Settings.SmartRouting.TryRememberLan(name, out _)) return;
            controller.UpdateSettings(s => s.SmartRouting.TryRememberLan(name, out var updated) ? s with { SmartRouting = updated } : s);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Error("Remembering the LAN adapter name failed", ex); // a convenience: it must never break the v1 status handler or skip ApplyAsync
        }
    }

    void UpdateSmart(Func<SmartRoutingSettings, SmartRoutingSettings> change)
    {
        if (_controller is not { } controller) return;
        controller.UpdateSettings(s => s with { SmartRouting = change(s.SmartRouting) });
        if (_smart is { } smart) _ = smart.ApplyAsync(controller.Status, controller.Settings);
    }

    /// The card's live speed next to each latency: the total through that exit right now. Blank whenever neither Smart
    /// routing nor Windows tracing is counting, so a stale figure never lingers.
    void RenderLiveSpeeds(SmartRoutingController smart)
    {
        if (_card is not { } card) return;
        RenderTotals(smart);
        if (!LiveSpeedDisplay.IsShown(smart.Status.State, smart.IsMeasuring))
        {
            card.RenderSpeeds("", "");
            return;
        }
        var live = smart.LiveSpeed;
        card.RenderSpeeds(SpeedLabel(live.PhoneBytesPerSecond), SpeedLabel(live.LanBytesPerSecond));
    }

    /// Today's total data through each exit, from the usage history: it stays visible when Smart routing is off or idle.
    void RenderTotals(SmartRoutingController? smart)
    {
        if (_card is not { } card || smart is null) return;
        var totals = smart.TodayTotals;
        card.RenderTotals(TotalLabel(totals.PhoneBytes), TotalLabel(totals.LanBytes));
    }

    static string TotalLabel(long bytes) => bytes > 0 ? ByteFormat.Human(bytes) : "";

    static string SpeedLabel(long bytesPerSecond) => bytesPerSecond <= 0 ? "" : UsageReport.NowText(bytesPerSecond);

    void RenderSmart(SmartRoutingStatus status)
    {
        RenderSmartCard(status);
        if (!LiveSpeedDisplay.IsShown(status.State, _smart?.IsMeasuring == true)) _card?.RenderSpeeds("", "");
        RenderSmartWindow();
        if (_waitingPopup is { } popup && (!status.Targets.Any(target => target.Target == popup.Target.Target
            && target.Main == popup.Target.Main && target.Generation == popup.Target.Generation && target.CanUseMain)
            || status.State is SmartState.Off or SmartState.Unavailable or SmartState.Faulted)) popup.Close();
    }

    void ShowWaitingPopup(WaitingTarget waiting)
    {
        // Capture the event's target/main; a queued or stale popup must not act on a different outage.
        if (_smart is not { } smart || smart.Status.State != SmartState.Running
            || !smart.Status.Targets.Any(target => target.Target == waiting.Target && target.Main == waiting.Main && target.Generation == waiting.Generation && target.CanUseMain)) return;
        if (_waitingPopup is { } existing)
        {
            if (existing.Target.Target == waiting.Target && existing.Target.Main == waiting.Main && existing.Target.Generation == waiting.Generation)
            {
                existing.SetText(SmartRoutingPresenter.WaitingText(waiting));
                return;
            }
            existing.Close();
        }
        Bounds? cardBounds = _card is { IsVisible: true } card ? new Bounds(card.Left, card.Top, card.ActualWidth, card.ActualHeight) : null;
        var popup = _waitingPopup = new WaitingPopup(waiting, cardBounds);
        popup.UseMainClicked += () => _ = _smart?.UseMainAsync(waiting);
        popup.KeepWaitingClicked += () => _ = _smart?.KeepWaitingAsync(waiting);
        popup.Closed += (_, _) => { if (ReferenceEquals(_waitingPopup, popup)) _waitingPopup = null; };
        popup.Show();
    }

    ConnectionId EditingProfile => _editingProfile ?? (_smart?.Status.Profile ?? ConnectionId.Phone);

    /// Renders both tabs. The Config tab is synchronous; the Usage tab needs a copy of the usage taken under the
    /// controller's gate, so it arrives a moment later. A render that is already running is not stacked: one more is queued.
    void RenderSmartWindow()
    {
        if (_smartWindow is null || _smart is null || _controller is null) return;
        if (_renderRunning)
        {
            _renderAgain = true;
            return;
        }
        _ = RenderSmartWindowAsync();
    }

    async Task RenderSmartWindowAsync()
    {
        _renderRunning = true;
        try
        {
            do
            {
                _renderAgain = false;
                if (_smartWindow is not { } window || _smart is not { } smart || _controller is not { } controller) return;
                var seq = ++_renderSeq;
                var settings = controller.Settings.SmartRouting;
                var status = smart.Status;
                window.Render(SmartRoutingPage.Build(_catalog, settings, status, EditingProfile));

                var usage = await smart.GetUsageAsync(); // continues on the UI thread
                if (seq != _renderSeq || _smartWindow != window) continue;
                var canAssign = settings.Enabled && (status.State is SmartState.Running or SmartState.Starting);
                var report = UsageReport.Build(
                    usage, DateOnly.FromDateTime(DateTime.Now), settings.UsageRangeDays, _catalog, settings, canAssign, _usageSort, _usageFilter, status.Profile);
                window.RenderUsage(report, _usageSort, SmartRoutingPresenter.Row(status).Text,
                    recording: status.State == SmartState.Running, windowsTracking: smart.IsMeasuring);
            }
            while (_renderAgain);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Error("Rendering the management window failed", ex);
        }
        finally
        {
            _renderRunning = false;
        }
    }

    /// Opens the network path window (one at a time). Probing runs only while it is open: the monitor starts here and
    /// is cancelled when the window closes.
    void OpenPathMap()
    {
        if (_controller is not { } controller) return;
        if (_pathWindow is not null)
        {
            if (_pathWindow.WindowState == WindowState.Minimized) _pathWindow.WindowState = WindowState.Normal;
            _pathWindow.Activate();
            return;
        }

        var monitor = new PathMonitor(new IcmpPathProbe(), TcpEndpointProbe.Default(), TimeProvider.System, PathThresholds.Default, message => _log?.Info(message));
        var cts = new CancellationTokenSource();
        var localMonitor = new LocalNetworkMonitor(new IcmpPathProbe(), HostResolver.ResolveIPv4Async, TimeProvider.System, PathThresholds.Default, message => _log?.Info(message));
        var netInfo = new CachingNetworkInfo(new WindowsNetworkInfo(), TimeProvider.System); // the tab asks several times a second; each real read lists every adapter
        var scanner = new NetworkScanner(new IcmpPathProbe(), new NeighborTable(), HostResolver.ReverseAsync);
        string? nicAdapter = null;
        NicStats? nicBaseline = null;
        var localStarted = false;
        var token = cts.Token; // read before queuing: the Closed handler may dispose cts before the pool runs a lambda

        (AdapterInfo? Lan, IPAddress? Source, int? Prefix) CurrentLan()
        {
            var lan = controller.Status.Adapters.Lan;
            var source = lan?.IPv4 is { } text && IPAddress.TryParse(text, out var ip) ? ip : null;
            return (lan, source, lan is not null && source is not null ? netInfo.PrefixLengthV4(lan.Name, source) : null);
        }

        var localServices = new LocalNetworkTabServices(
            Present: selectedKey =>
            {
                var (lan, _, prefix) = CurrentLan();
                localMonitor.SetNetwork(lan, lan is null ? null : netInfo.GatewayV4(lan.Name), controller.Settings.LocalDevices);
                var nic = lan is null ? null : netInfo.ReadNic(lan.Name);
                if (lan?.Name != nicAdapter)
                {
                    nicAdapter = lan?.Name; // a different card starts its error count again
                    nicBaseline = nic;
                }
                // The LAN lane is absent while the LAN is Not used or turned off: the tab then reads it as not connected.
                var internet = LocalNetworkPresenter.InternetOf(
                    PathMapPresenter.Present(controller.Status, monitor.Snapshot(), null, PathThresholds.Default)
                        .Lane(ConnectionId.Lan)?.Internet.State ?? HopDisplay.Hidden);
                return LocalNetworkPresenter.Present(localMonitor.Snapshot(), nic, nicBaseline, internet, selectedKey, PathThresholds.Default, prefix);
            },
            GetDevices: () => controller.Settings.LocalDevices,
            SaveDevices: devices => controller.UpdateSettings(s => s with { LocalDevices = devices }),
            Scan: (progress, ct) =>
            {
                var (_, source, prefix) = CurrentLan();
                return source is null || prefix is null
                    ? Task.FromResult<IReadOnlyList<ScanResult>>([])
                    : scanner.ScanAsync(source, prefix.Value, progress, ct);
            },
            Start: () =>
            {
                if (localStarted) return;
                localStarted = true;
                var run = Task.Run(() => localMonitor.RunAsync(token));
                _ = run.ContinueWith(t => _log?.Error("Local network monitor stopped unexpectedly", t.Exception?.GetBaseException()),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            });
        var window = _pathWindow = new PathMapWindow(selected =>
        {
            // Keep the monitor on the adapters the card shows; a no-op when nothing changed.
            var adapters = controller.Status.Adapters;
            monitor.SetAdapters(Connections.All.ToDictionary(id => id, adapters.Adapter));
            return PathMapPresenter.Present(controller.Status, monitor.Snapshot(), selected, PathThresholds.Default);
        }, localServices);
        window.Closed += (_, _) =>
        {
            cts.Cancel();
            cts.Dispose();
            _pathWindow = null;
        };
        var initial = controller.Status.Adapters;
        monitor.SetAdapters(Connections.All.ToDictionary(id => id, initial.Adapter));
        var run = Task.Run(() => monitor.RunAsync(token)); // off the UI thread: its loops, probe continuations, locks and the log callback must not run there
        // Last resort: the monitor swallows cancellation and logs its own errors, but an unobserved fault here would vanish silently.
        _ = run.ContinueWith(t => _log?.Error("Path map monitor stopped unexpectedly", t.Exception?.GetBaseException()),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        window.Show();
    }

    void OpenSmartRouting()
    {
        if (_smart is null || _controller is null)
        {
            MessageBox.Show("Smart routing is unavailable (see the log)", "NetRoute Widget",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_smartWindow is not null)
        {
            if (_smartWindow.WindowState == WindowState.Minimized) _smartWindow.WindowState = WindowState.Normal;
            _smartWindow.Activate();
            return;
        }
        var window = _smartWindow = new SmartRoutingWindow();
        _smart?.SetUsageWatched(true); // its Usage tab is what the Windows tracing counts for; the trace starts on the next poll
        _usageFilter = ""; // the new window's filter box is empty
        _editingProfile = null; // a new window follows the active profile
        window.MasterToggled += on => ChangeSmart(s => s with { Enabled = on });
        window.ItemToggled += (id, on) => ChangeSmart(s => s.WithItem(id, on));
        window.GroupToggled += (groupId, on) => ChangeSmart(s => SmartRoutingPage.WithGroup(_catalog, s, groupId, on));
        window.EditingProfileChanged += profile =>
        {
            _editingProfile = profile;
            RenderSmartWindow();
        };
        // The window passes the profile of the list it was showing when the user clicked (captured before the add dialog
        // opens), so a rule always lands in the list on screen, not in whatever EditingProfile says by now.
        window.UserRuleToggled += (rule, on, shown) => ChangeSmart(s => s.WithUserRule(shown, rule with { Enabled = on }));
        window.UserRuleRemoved += (rule, shown) => ChangeSmart(s => s.WithoutUserRule(shown, rule));
        window.AddRuleRequested += (type, shown) =>
        {
            var dialog = new AddRuleWindow(type) { Owner = window };
            if (dialog.ShowDialog() == true && dialog.Result is { } rule) ChangeSmart(s => s.WithUserRule(shown, rule));
        };
        window.UseMainRequested += waiting => _ = _smart?.UseMainAsync(waiting);
        window.UsageRangeChanged += days =>
        {
            _controller!.UpdateSettings(s => s with { SmartRouting = s.SmartRouting with { UsageRangeDays = UsageReport.NormalizeRange(days) } });
            RenderSmartWindow(); // no ApplyAsync: a range change must never touch sing-box
        };
        window.UsageSortChanged += sort =>
        {
            _usageSort = sort;
            RenderSmartWindow();
        };
        window.UsageFilterChanged += text =>
        {
            _usageFilter = text;
            RenderSmartWindow(); // no ApplyAsync: a filter change must never touch sing-box
        };
        window.UsageExceptionRequested += (key, renderedProfile, on) =>
            ChangeSmart(s => UsageExceptions.Set(_catalog, s, renderedProfile, key, on)); // what the menu showed, not the live status
        window.EditRulesFileRequested += () => OpenRulesFile();
        window.ClearUsageRequested += async () =>
        {
            if (_smart is not { } smart) return;
            try
            {
                await smart.ClearUsageAsync();
                await SaveUsageAsync(force: true);
                RenderSmartWindow();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.Error("Clearing usage failed", ex); // an async void handler must never throw: it would end the app
            }
        };
        window.Closed += (_, _) =>
        {
            _smartWindow = null;
            _smart?.SetUsageWatched(false); // Windows tracing stops on the next poll
        };
        RenderSmartWindow();
        window.Show();
    }

    /// Opens the user's rules file in the default editor, creating a small template first. Changes apply after a restart.
    void OpenRulesFile()
    {
        try
        {
            if (!File.Exists(AppPaths.UserRulesFile))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.UserRulesFile)!);
                File.WriteAllText(AppPaths.UserRulesFile,
                    "{\n  \"version\": 1,\n  \"groups\": [\n    { \"id\": \"my-apps\", \"name\": \"My apps\", \"exit\": \"phone\", \"items\": [] }\n  ]\n}\n");
            }
            try
            {
                using var _ = Process.Start(new ProcessStartInfo(AppPaths.UserRulesFile) { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                using var _ = Process.Start(new ProcessStartInfo("notepad.exe", $"\"{AppPaths.UserRulesFile}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _log?.Error("Could not open the rules file", ex);
        }
    }

    void ChangeSmart(Func<SmartRoutingSettings, SmartRoutingSettings> change)
    {
        UpdateSmart(change);
        if (_smart is { } smart) RenderSmartCard(smart.Status); // the card's Smart button follows the setting at once
        RenderSmartWindow(); // immediate feedback; the controller's StatusChanged re-renders again once applied
    }

    /// Saves usage.json when something changed (or when force is set, or a previous save failed). Never throws.
    async Task SaveUsageAsync(bool force = false)
    {
        if (_smart is not { } smart || _usageUnreadable) return;
        try
        {
            var usage = force || _usageSaveFailed ? await smart.GetUsageAsync() : await smart.TakeUsageIfChangedAsync();
            if (usage is null) return;
            if (usage.Days.Count == 0 && !File.Exists(AppPaths.UsageFile)) return; // nothing recorded yet: no file for someone who never used it
            await Task.Run(() => UsageStore.Save(AppPaths.UsageFile, usage));
            _usageSaveFailed = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _usageSaveFailed = true;
            _log?.Error("Saving usage failed", ex);
        }
    }

    async void RunSpeedTest()
    {
        if (_speedTestRunning || _controller is null) return;
        _speedTestRunning = true;
        try
        {
            _card?.ShowSpeed("⏱ Measuring… (about 5 MB of mobile data)");
            var adapters = _controller.Status.Adapters; // read on the UI thread, before the work moves off it
            var result = await Task.Run(() => SpeedTest.RunAsync(HttpSpeedProbe.Default(), adapters));
            _card?.ShowSpeed($"{SpeedTest.Describe(result)}  ({DateTime.Now:HH:mm})");
            _log?.Info("Speed test: " + SpeedTest.Describe(result));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Error("Speed test failed", ex);
            _card?.ShowSpeed("⏱ Speed test failed");
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
        if (_smartStopped || _smart is not { } smart) return; // called from Quit and again from OnExit
        _smartStopped = true;
        // ShutdownAsync latches: nothing restarts sing-box afterwards
        if (!Task.Run(() => smart.ShutdownAsync()).Wait(TimeSpan.FromSeconds(5))) _log?.Error("Smart routing did not stop within 5 s");
        Task.Run(() => SaveUsageAsync(force: true)).Wait(TimeSpan.FromSeconds(3));
    }
}
