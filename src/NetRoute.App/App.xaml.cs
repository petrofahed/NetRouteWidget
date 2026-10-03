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
    RouteExit? _editingProfile; // the Config tab's view; null = follow the active profile
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
            var elevated = Elevation.IsElevated();
            _startupEnabled = _startupTask.Exists();
            log.Info($"Starting: elevated={elevated}, mode={loaded.Settings.Mode}, startWithWindows={_startupEnabled}");

            var controller = _controller = new RouteController(
                new WindowsAdapterSource(), new WindowsInterfaceMetrics(), new WindowsRouteQuery(), TcpLatencyProbe.Default(),
                loaded.Settings, elevated, loaded.Unreadable ? _ => { } : store.Save, log.Info);
            controller.StatusChanged += status => Dispatcher.BeginInvoke(new Action(() => Render(status)));
            controller.AutoSwitched += message => Dispatcher.BeginInvoke(new Action(() => _tray?.Notify(message)));

            SetUpSmartRouting(controller, log);

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
            _card.SmartSettingsRequested += OpenSmartRouting;

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
            if (loaded.Settings.CardVisible) ShowCard();
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
                counters: new WindowsAdapterCounters());
            smart.StatusChanged += s => Dispatcher.BeginInvoke(new Action(() => RenderSmart(s)));
            smart.Notify += m => Dispatcher.BeginInvoke(new Action(() => _tray?.Notify(m)));
            smart.WaitingDetected += names => Dispatcher.BeginInvoke(new Action(() => ShowWaitingPopup(names)));

            // Hook into the v1 controller last, so a failure above leaves it untouched.
            controller.ExternalPathResolver = index =>
            {
                var status = smart.Status;
                return status.IsActive && index == TunIndex() // TUN looked up by name on every call: its index changes on each sing-box start
                    ? (status.DefaultExit == RouteExit.Lan ? InternetPath.Lan : InternetPath.Phone) : null;
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

    /// The card's live speed next to each latency: the total through that exit right now. Blank whenever Smart routing
    /// is not running, so a stale figure never lingers (it measures what passes through Smart routing).
    void RenderLiveSpeeds(SmartRoutingController smart)
    {
        if (_card is not { } card) return;
        RenderTotals(smart);
        if (smart.Status.State is not (SmartState.Running or SmartState.Starting))
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
        _card?.RenderSmart(SmartRoutingPresenter.Row(status));
        if (status.State is not (SmartState.Running or SmartState.Starting)) _card?.RenderSpeeds("", "");
        RenderSmartWindow();
        if (_waitingPopup is not null && (status.LanOnline || status.LanRulesOnPhone
            || status.State is SmartState.Off or SmartState.Unavailable or SmartState.Faulted)) // Starting is a transient restart: keep the popup
            _waitingPopup.Close();
    }

    void ShowWaitingPopup(IReadOnlyList<string> names)
    {
        // The event was queued on the dispatcher: the outage may be over by now.
        if (_smart is not { } smart) return;
        var current = smart.Status;
        if (current.LanOnline || current.LanRulesOnPhone
            || current.State is not (SmartState.Running or SmartState.Starting)) return;
        var text = SmartRoutingPresenter.WaitingText(names);
        if (_waitingPopup is not null)
        {
            _waitingPopup.SetText(text);
            return;
        }
        Bounds? cardBounds = _card is { IsVisible: true } card
            ? new Bounds(card.Left, card.Top, card.ActualWidth, card.ActualHeight)
            : null;
        var popup = _waitingPopup = new WaitingPopup(text, cardBounds);
        popup.UsePhoneClicked += () => _ = _smart?.UseLanRulesOnPhoneAsync();
        popup.KeepWaitingClicked += () => _ = _smart?.KeepWaitingAsync();
        popup.Closed += (_, _) => _waitingPopup = null;
        popup.Show();
    }

    RouteExit EditingProfile => _editingProfile ?? (_smart?.Status.Profile ?? RouteExit.Phone);

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
                window.RenderUsage(report, _usageSort, SmartRoutingPresenter.Row(status).Text, recording: status.State == SmartState.Running);
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
        window.UserRuleToggled += (rule, on, shown) => ChangeSmart(s => shown == RouteExit.Lan
            ? s.WithPhoneUserRule(rule with { Enabled = on })
            : s.WithUserRule(rule with { Enabled = on }));
        window.UserRuleRemoved += (rule, shown) => ChangeSmart(s => shown == RouteExit.Lan
            ? s.WithoutPhoneUserRule(rule)
            : s.WithoutUserRule(rule));
        window.AddRuleRequested += (type, shown) =>
        {
            var dialog = new AddRuleWindow(type) { Owner = window };
            if (dialog.ShowDialog() == true && dialog.Result is { } rule) ChangeSmart(s => shown == RouteExit.Lan ? s.WithPhoneUserRule(rule) : s.WithUserRule(rule));
        };
        window.UsePhoneRequested += () => _ = _smart?.UseLanRulesOnPhoneAsync();
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
        window.Closed += (_, _) => _smartWindow = null;
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
