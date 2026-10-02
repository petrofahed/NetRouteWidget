# NetRoute Widget v1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Windows tray icon + floating card that sends internet traffic through the phone's USB tethering (or the LAN, or Windows' automatic choice) while printer/NAS traffic stays on the LAN.

**Architecture:**
- A UI-free `NetRoute.Core` library holds all logic: adapter detection, metric policy, routing engine, controller, presenter, settings and logging. Windows APIs sit behind small interfaces, so the logic is unit-tested with fakes.
- Real Windows implementations (IP Helper P/Invoke, `NetworkInterface`, TCP latency probe, `schtasks`) live in `NetRoute.Core/Windows/` and get read-only integration tests.
- A thin WPF app (`NetRoute.App`, exe name `NetRouteWidget`) wires everything to a WinForms `NotifyIcon` and a borderless always-on-top card.

**Tech Stack:** .NET 10 (C#), WPF + WinForms `NotifyIcon`, xUnit, `Microsoft.Extensions.TimeProvider.Testing` (tests only), Win32 `iphlpapi.dll`.

**Spec:** `docs/superpowers/specs/2026-10-02-netroute-widget-v1-design.md`

## Global Constraints

- **SDK:** pinned with `global.json` to `10.0.401`, `rollForward: latestFeature`. The machine also has a .NET 11 RC, which must not be picked.
- **Target framework:** `net10.0-windows` for all three projects. `Nullable` and `ImplicitUsings` enabled.
- **Dependencies:** no third-party runtime packages. Test-only packages: the `dotnet new xunit` template defaults, plus `Microsoft.Extensions.TimeProvider.Testing`.
- **Metrics:** preferred = `5`, backup = `50`. Auto = Windows automatic metric. Applied to both IPv4 and IPv6 interfaces. **Routes are never deleted.**
- **Paths:** settings in `%AppData%\NetRouteWidget\settings.json`; logs in `%AppData%\NetRouteWidget\logs\netroute-yyyyMMdd.log`, 7 days kept.
- **Timing:** debounce 1.5 s; poll every 5 s with the card visible and 30 s with it hidden; latency probe TCP `1.1.1.1:443` with a 2 s timeout.
- **Path-check targets:** IPv4 `1.1.1.1`, IPv6 `2606:4700:4700::1111`.
- **Ignored adapter markers** (case-insensitive, in name or description): `VMware`, `Hyper-V`, `Virtual`, `vEthernet`, `VirtualBox`, `WireGuard`, `NordLynx`, `TAP-`, `Wintun`, `Tunnel`, `VPN`, `Loopback`, `Bluetooth`.
- **Phone markers** (in description): `Remote NDIS`, `Apple Mobile Device Ethernet`.
- **Elevation:** the app runs `asInvoker` (no manifest). Elevation comes from the "Start with Windows" scheduled task (`HighestAvailable`) or from "Restart as admin" (`runas`).
- **Exit:** quitting leaves routing as it is.
- **Public repo:** never commit this machine's real MAC addresses. Test fixtures use made-up MACs (`AA-BB-CC-…`, `02-00-00-…`).
- **Commits:** every commit message ends with:
  ```
  Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01AypeDHU2CKcJxywGQXDkT3
  ```
- **Working directory:** all commands run from `F:\source\NetRouteWidget`.

## Review Focus

1. **Phone replugged:** Windows recreates the adapter with a new ifIndex, a `#2` description suffix and an automatic metric. Expected: it is still detected as the phone and the mode is re-applied. Pinned by: Task 1 `Detects_replugged_phone_…` and `Phone_override_matches_…`; Task 6 `Phone_unplug_and_replug_…`.
2. **App started without admin rights:** expected that no metric is ever written, mode buttons are disabled and a clear message appears. Pinned by: Task 6 `Not_elevated_never_writes_metrics`.
3. **Corrupt or hand-edited settings file** (garbage, `null`, unknown mode number): expected that defaults load and the file is repaired. Pinned by: Task 3 `Corrupt_file_…` and `Unknown_mode_number_…`.
4. **Saved card position is off-screen** after a monitor is unplugged: expected that the card appears bottom-right on the primary work area. Pinned by: Task 9 `CardPlacementTests`.
5. **Laptop on battery at logon, or running for days:** expected that the scheduled task still starts and is never killed after 72 h. The `schtasks` defaults do both, so XML is used. Pinned by: Task 8 `Task_runs_on_battery_and_never_times_out`.

**Restore command if anything goes wrong while testing on the real machine:**

```powershell
Get-NetIPInterface | Where-Object AutomaticMetric -eq Disabled | Set-NetIPInterface -AutomaticMetric Enabled
```

---

## File Structure

```
global.json
NetRouteWidget.sln(x)
src/NetRoute.Core/
  NetRoute.Core.csproj
  Models.cs              RoutingMode, AdapterKind, InternetPath, AdapterInfo
  AdapterDetector.cs     AdapterOverrides, DetectionIssue, DetectionResult, AdapterDetector
  Abstractions.cs        IpFamily, InterfaceMetricState, IInterfaceMetrics, IAdapterSource, IRouteQuery, ILatencyProbe
  MetricPolicy.cs        MetricTarget, MetricPolicy
  RoutingEngine.cs       ApplyResult, RoutingEngine
  Settings.cs            AppSettings, SettingsLoadResult, SettingsStore
  FileLog.cs             FileLog
  Debouncer.cs           Debouncer
  NetworkStatus.cs       NetworkStatus
  StatusPresenter.cs     Dot, TrayColor, AdapterRow, CardView, StatusPresenter
  RouteController.cs     RouteController
  CardPlacement.cs       Bounds, CardPlacement
  StartupTaskXml.cs      StartupTaskXml
  Windows/IpHelperNative.cs        P/Invoke + MIB_IPINTERFACE_ROW
  Windows/WindowsInterfaceMetrics.cs
  Windows/WindowsRouteQuery.cs
  Windows/WindowsAdapterSource.cs
  Windows/TcpLatencyProbe.cs
  Windows/StartupTask.cs           schtasks.exe runner
src/NetRoute.App/
  NetRoute.App.csproj  App.xaml  App.xaml.cs  AppPaths.cs  Elevation.cs  SingleInstance.cs
  Theme.cs  TrayIcon.cs  CardWindow.xaml  CardWindow.xaml.cs
  AdapterPickerWindow.xaml  AdapterPickerWindow.xaml.cs
tests/NetRoute.Core.Tests/
  NetRoute.Core.Tests.csproj  TestAdapters.cs  Fakes.cs
  AdapterDetectorTests.cs  RoutingEngineTests.cs  SettingsStoreTests.cs  FileLogTests.cs
  DebouncerTests.cs  StatusPresenterTests.cs  RouteControllerTests.cs  CardPlacementTests.cs
  StartupTaskXmlTests.cs  IpHelperLayoutTests.cs  TcpLatencyProbeTests.cs  WindowsIntegrationTests.cs
```

**Test commands used throughout:**
- Unit tests: `dotnet test --filter "Category!=Integration"`
- Integration tests (read-only, real machine): `dotnet test --filter "Category=Integration"`

---

### Task 1: Solution scaffold, models and adapter detection

**Files:**
- Create: `global.json`, solution file, `src/NetRoute.Core/NetRoute.Core.csproj`, `src/NetRoute.Core/Models.cs`, `src/NetRoute.Core/AdapterDetector.cs`
- Create: `tests/NetRoute.Core.Tests/NetRoute.Core.Tests.csproj`, `tests/NetRoute.Core.Tests/TestAdapters.cs`, `tests/NetRoute.Core.Tests/AdapterDetectorTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces:
  - `enum RoutingMode { Phone, Lan, Auto }`
  - `enum AdapterKind { Ethernet, Wireless, Other }`
  - `enum InternetPath { None, Phone, Lan, Other }`
  - `record AdapterInfo(int Index, string Name, string Description, AdapterKind Kind, bool IsUp, bool HasGateway, string? IPv4, string Mac)`
  - `record AdapterOverrides(string? PhoneDescription, string? LanMac)` with `AdapterOverrides.None`
  - `enum DetectionIssue { None, NotFound, Ambiguous }`
  - `record DetectionResult(AdapterInfo? Phone, DetectionIssue PhoneIssue, AdapterInfo? Lan, DetectionIssue LanIssue)` with `DetectionResult.Empty`
  - `static AdapterDetector`: `Detect(IReadOnlyList<AdapterInfo>, AdapterOverrides) → DetectionResult`, `Candidates(IEnumerable<AdapterInfo>) → IEnumerable<AdapterInfo>`, `IsPhone(AdapterInfo)`, `IsVirtual(AdapterInfo)`
  - test helper `TestAdapters` (Phone/Lan/Wifi/VmNet/HyperV/NordLynx)

- [ ] **Step 1: Scaffold the solution**

```powershell
Set-Content global.json '{ "sdk": { "version": "10.0.401", "rollForward": "latestFeature" } }'
dotnet --version   # must print 10.0.4xx, not 11.x
dotnet new sln -n NetRouteWidget
dotnet new classlib -n NetRoute.Core -o src/NetRoute.Core -f net10.0
dotnet new xunit -n NetRoute.Core.Tests -o tests/NetRoute.Core.Tests -f net10.0
dotnet sln add src/NetRoute.Core tests/NetRoute.Core.Tests
dotnet add tests/NetRoute.Core.Tests reference src/NetRoute.Core
dotnet add tests/NetRoute.Core.Tests package Microsoft.Extensions.TimeProvider.Testing
Remove-Item src/NetRoute.Core/Class1.cs, tests/NetRoute.Core.Tests/UnitTest1.cs
```

Replace `src/NetRoute.Core/NetRoute.Core.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="NetRoute.Core.Tests" />
  </ItemGroup>

</Project>
```

In `tests/NetRoute.Core.Tests/NetRoute.Core.Tests.csproj`:
- change `<TargetFramework>net10.0</TargetFramework>` to `<TargetFramework>net10.0-windows</TargetFramework>`;
- make sure the file contains `<Using Include="Xunit" />` in an `ItemGroup`, and add it if the template left it out.

- [ ] **Step 2: Write the test helper and the failing tests**

`tests/NetRoute.Core.Tests/TestAdapters.cs`:

```csharp
namespace NetRoute.Core.Tests;

/// Adapter fixtures modelled on a real dual-homed PC. MACs are made up (public repo).
static class TestAdapters
{
    public static AdapterInfo Phone(int index = 31, string description = "SAMSUNG Mobile USB Remote NDIS Network Device") =>
        new(index, $"Ethernet {index}", description, AdapterKind.Ethernet, IsUp: true, HasGateway: true, "192.168.42.11", "02-00-00-00-00-31");

    public static AdapterInfo Lan(int index = 10, string mac = "AA-BB-CC-00-00-10") =>
        new(index, "Ethernet", "Realtek Gaming 2.5GbE Family Controller", AdapterKind.Ethernet, IsUp: true, HasGateway: true, "192.168.86.42", mac);

    public static AdapterInfo Wifi() =>
        new(18, "Wi-Fi", "Intel(R) Wi-Fi 6E AX211 160MHz", AdapterKind.Wireless, IsUp: true, HasGateway: true, "192.168.1.20", "AA-BB-CC-00-00-18");

    public static AdapterInfo VmNet(int index) =>
        new(index, $"VMware Network Adapter VMnet{index}", "VMware Virtual Ethernet Adapter for VMnet", AdapterKind.Ethernet, IsUp: true, HasGateway: true, "192.168.80.1", $"00-50-56-C0-00-{index:D2}");

    public static AdapterInfo HyperV() =>
        new(34, "vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", AdapterKind.Ethernet, IsUp: true, HasGateway: true, "172.26.16.1", "00-15-5D-00-00-34");

    public static AdapterInfo NordLynx() =>
        new(40, "NordLynx", "NordLynx Tunnel", AdapterKind.Other, IsUp: true, HasGateway: true, "10.5.0.2", "");
}
```

`tests/NetRoute.Core.Tests/AdapterDetectorTests.cs`:

```csharp
namespace NetRoute.Core.Tests;

public class AdapterDetectorTests
{
    [Fact]
    public void Detects_phone_and_lan_and_ignores_virtual_adapters()
    {
        var all = new[] { TestAdapters.Phone(), TestAdapters.Lan(), TestAdapters.VmNet(29), TestAdapters.HyperV(), TestAdapters.NordLynx() };

        var result = AdapterDetector.Detect(all, AdapterOverrides.None);

        Assert.Equal(31, result.Phone?.Index);
        Assert.Equal(DetectionIssue.None, result.PhoneIssue);
        Assert.Equal(10, result.Lan?.Index);
        Assert.Equal(DetectionIssue.None, result.LanIssue);
    }

    [Fact]
    public void Detects_replugged_phone_with_new_index_name_and_numbered_description()
    {
        var replugged = TestAdapters.Phone(index: 36, description: "SAMSUNG Mobile USB Remote NDIS Network Device #2");

        var result = AdapterDetector.Detect([replugged, TestAdapters.Lan()], AdapterOverrides.None);

        Assert.Equal(36, result.Phone?.Index);
    }

    [Fact]
    public void Phone_override_matches_description_ignoring_number_suffix()
    {
        var replugged = TestAdapters.Phone(index: 36, description: "SAMSUNG Mobile USB Remote NDIS Network Device #2");
        var overrides = new AdapterOverrides("SAMSUNG Mobile USB Remote NDIS Network Device", null);

        var result = AdapterDetector.Detect([replugged, TestAdapters.Lan()], overrides);

        Assert.Equal(36, result.Phone?.Index);
    }

    [Fact]
    public void Missing_phone_is_reported_as_not_found()
    {
        var result = AdapterDetector.Detect([TestAdapters.Lan()], AdapterOverrides.None);

        Assert.Null(result.Phone);
        Assert.Equal(DetectionIssue.NotFound, result.PhoneIssue);
        Assert.Equal(10, result.Lan?.Index);
    }

    [Fact]
    public void Two_lan_candidates_are_reported_as_ambiguous()
    {
        var second = TestAdapters.Lan(index: 12, mac: "AA-BB-CC-00-00-12");

        var result = AdapterDetector.Detect([TestAdapters.Phone(), TestAdapters.Lan(), second], AdapterOverrides.None);

        Assert.Null(result.Lan);
        Assert.Equal(DetectionIssue.Ambiguous, result.LanIssue);
    }

    [Fact]
    public void Lan_override_by_mac_resolves_ambiguity_case_insensitively()
    {
        var second = TestAdapters.Lan(index: 12, mac: "AA-BB-CC-00-00-12");

        var result = AdapterDetector.Detect([TestAdapters.Lan(), second], new AdapterOverrides(null, "aa-bb-cc-00-00-12"));

        Assert.Equal(12, result.Lan?.Index);
    }

    [Fact]
    public void Down_adapters_and_lan_without_gateway_are_ignored()
    {
        var downPhone = TestAdapters.Phone() with { IsUp = false };
        var noGateway = TestAdapters.Lan() with { HasGateway = false };

        var result = AdapterDetector.Detect([downPhone, noGateway], AdapterOverrides.None);

        Assert.Equal(DetectionIssue.NotFound, result.PhoneIssue);
        Assert.Equal(DetectionIssue.NotFound, result.LanIssue);
    }

    [Fact]
    public void Wifi_is_not_auto_detected_as_lan_but_can_be_chosen_by_override()
    {
        var wifi = TestAdapters.Wifi();

        Assert.Equal(DetectionIssue.NotFound, AdapterDetector.Detect([wifi], AdapterOverrides.None).LanIssue);
        Assert.Equal(18, AdapterDetector.Detect([wifi], new AdapterOverrides(null, wifi.Mac)).Lan?.Index);
    }

    [Fact]
    public void Candidates_exclude_virtual_and_non_network_adapters()
    {
        var all = new[] { TestAdapters.Phone(), TestAdapters.Lan(), TestAdapters.Wifi(), TestAdapters.VmNet(29), TestAdapters.HyperV(), TestAdapters.NordLynx() };

        var indexes = AdapterDetector.Candidates(all).Select(a => a.Index).Order().ToArray();

        Assert.Equal(new[] { 10, 18, 31 }, indexes);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --filter "Category!=Integration"`
Expected: build FAILS with `CS0246: The type or namespace name 'AdapterInfo' could not be found`.

- [ ] **Step 4: Implement models and detector**

`src/NetRoute.Core/Models.cs`:

```csharp
namespace NetRoute.Core;

public enum RoutingMode { Phone, Lan, Auto }

public enum AdapterKind { Ethernet, Wireless, Other }

/// Which detected adapter Windows would use for internet traffic right now.
public enum InternetPath { None, Phone, Lan, Other }

public sealed record AdapterInfo(
    int Index,
    string Name,
    string Description,
    AdapterKind Kind,
    bool IsUp,
    bool HasGateway,
    string? IPv4,
    string Mac);
```

`src/NetRoute.Core/AdapterDetector.cs`:

```csharp
using System.Text.RegularExpressions;

namespace NetRoute.Core;

public enum DetectionIssue { None, NotFound, Ambiguous }

/// User choices that win over auto-detection. Phone is matched by description
/// (tethering MACs can be randomised), the LAN by MAC address.
public sealed record AdapterOverrides(string? PhoneDescription, string? LanMac)
{
    public static readonly AdapterOverrides None = new(null, null);
}

public sealed record DetectionResult(
    AdapterInfo? Phone, DetectionIssue PhoneIssue,
    AdapterInfo? Lan, DetectionIssue LanIssue)
{
    public static readonly DetectionResult Empty = new(null, DetectionIssue.NotFound, null, DetectionIssue.NotFound);
}

public static partial class AdapterDetector
{
    static readonly string[] PhoneMarkers = ["Remote NDIS", "Apple Mobile Device Ethernet"];

    static readonly string[] VirtualMarkers =
    [
        "VMware", "Hyper-V", "Virtual", "vEthernet", "VirtualBox", "WireGuard", "NordLynx",
        "TAP-", "Wintun", "Tunnel", "VPN", "Loopback", "Bluetooth",
    ];

    public static bool IsPhone(AdapterInfo a) => ContainsAny(a.Description, PhoneMarkers);

    public static bool IsVirtual(AdapterInfo a) =>
        ContainsAny(a.Description, VirtualMarkers) || ContainsAny(a.Name, VirtualMarkers);

    /// Adapters a user may pick manually: real network hardware, up or down.
    public static IEnumerable<AdapterInfo> Candidates(IEnumerable<AdapterInfo> adapters) =>
        adapters.Where(a => !IsVirtual(a) && (a.Kind != AdapterKind.Other || IsPhone(a)));

    public static DetectionResult Detect(IReadOnlyList<AdapterInfo> adapters, AdapterOverrides overrides)
    {
        var up = adapters.Where(a => a.IsUp && !IsVirtual(a)).ToList();

        var phones = overrides.PhoneDescription is { } description
            ? up.Where(a => NormalizeDescription(a.Description) == NormalizeDescription(description)).ToList()
            : up.Where(IsPhone).ToList();

        var lans = overrides.LanMac is { } mac
            ? up.Where(a => string.Equals(a.Mac, mac, StringComparison.OrdinalIgnoreCase)).ToList()
            : up.Where(a => a.Kind == AdapterKind.Ethernet && a.HasGateway && !IsPhone(a)).ToList();

        // An adapter chosen as the phone can never also be the LAN.
        lans.RemoveAll(l => phones.Any(p => p.Index == l.Index));

        var (phone, phoneIssue) = Pick(phones);
        var (lan, lanIssue) = Pick(lans);
        return new DetectionResult(phone, phoneIssue, lan, lanIssue);
    }

    /// Windows appends " #2", " #3"… when the same device is re-plugged.
    internal static string NormalizeDescription(string description) =>
        NumberSuffix().Replace(description.Trim(), "").ToUpperInvariant();

    static (AdapterInfo?, DetectionIssue) Pick(List<AdapterInfo> found) => found.Count switch
    {
        0 => (null, DetectionIssue.NotFound),
        1 => (found[0], DetectionIssue.None),
        _ => (null, DetectionIssue.Ambiguous),
    };

    static bool ContainsAny(string text, string[] markers) =>
        markers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"\s+#\d+$")]
    private static partial Regex NumberSuffix();
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "Category!=Integration"`
Expected: PASS, 9 tests.

- [ ] **Step 6: Commit**

```powershell
git add -A
git commit -m "feat(core): scaffold solution and adapter detection" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`nClaude-Session: https://claude.ai/code/session_01AypeDHU2CKcJxywGQXDkT3"
```

---

### Task 2: Metric policy and routing engine

**Files:**
- Create: `src/NetRoute.Core/Abstractions.cs`, `src/NetRoute.Core/MetricPolicy.cs`, `src/NetRoute.Core/RoutingEngine.cs`
- Create: `tests/NetRoute.Core.Tests/Fakes.cs`, `tests/NetRoute.Core.Tests/RoutingEngineTests.cs`

**Interfaces:**
- Consumes: `RoutingMode`, `AdapterInfo`, `DetectionResult`, `DetectionIssue`, `TestAdapters` (Task 1)
- Produces:
  - `enum IpFamily { IPv4, IPv6 }`
  - `record InterfaceMetricState(bool UseAutomatic, uint Metric)`
  - `interface IInterfaceMetrics { InterfaceMetricState? Get(int ifIndex, IpFamily family); void Set(int ifIndex, IpFamily family, uint? metric); }` (`null` metric = automatic; `Get` returns `null` when the family is absent; `Set` throws on failure)
  - `interface IAdapterSource { IReadOnlyList<AdapterInfo> GetAdapters(); }`
  - `interface IRouteQuery { int? GetBestInterfaceIndex(IPAddress destination); }` (IPv4 and IPv6; `null` = no route)
  - `interface ILatencyProbe { Task<int?> MeasureAsync(IPAddress source, CancellationToken ct); }` (`null` = no reply)
  - `record MetricTarget(uint? Phone, uint? Lan)`; `MetricPolicy.Preferred = 5`, `MetricPolicy.Backup = 50`, `MetricPolicy.For(RoutingMode)`
  - `record ApplyResult(bool Success, string? Error)` with `ApplyResult.Ok`
  - `class RoutingEngine(IInterfaceMetrics)`: `ApplyResult Apply(RoutingMode, DetectionResult)`, `ApplyResult Reset(AdapterInfo)`, `bool IsInSync(RoutingMode, DetectionResult)`
  - test fake `FakeInterfaceMetrics`

- [ ] **Step 1: Write the fake and the failing tests**

`tests/NetRoute.Core.Tests/Fakes.cs`:

```csharp
namespace NetRoute.Core.Tests;

sealed class FakeInterfaceMetrics : IInterfaceMetrics
{
    public Dictionary<(int, IpFamily), InterfaceMetricState> State { get; } = new();
    public HashSet<(int, IpFamily)> FailOnSet { get; } = new();
    public HashSet<(int, IpFamily)> IgnoreSet { get; } = new();
    public List<(int Index, IpFamily Family, uint? Metric)> SetCalls { get; } = new();

    /// Registers an interface with Windows-style automatic metric 25.
    public void Add(int index, bool ipv6 = true)
    {
        State[(index, IpFamily.IPv4)] = new(true, 25);
        if (ipv6) State[(index, IpFamily.IPv6)] = new(true, 25);
    }

    public InterfaceMetricState? Get(int ifIndex, IpFamily family) =>
        State.TryGetValue((ifIndex, family), out var s) ? s : null;

    public void Set(int ifIndex, IpFamily family, uint? metric)
    {
        SetCalls.Add((ifIndex, family, metric));
        if (FailOnSet.Contains((ifIndex, family))) throw new InvalidOperationException("Access is denied.");
        if (IgnoreSet.Contains((ifIndex, family))) return;
        State[(ifIndex, family)] = metric is { } m ? new(false, m) : new(true, 25);
    }
}
```

`tests/NetRoute.Core.Tests/RoutingEngineTests.cs`:

```csharp
namespace NetRoute.Core.Tests;

public class RoutingEngineTests
{
    static DetectionResult Both(AdapterInfo? phone = null) =>
        new(phone ?? TestAdapters.Phone(), DetectionIssue.None, TestAdapters.Lan(), DetectionIssue.None);

    static FakeInterfaceMetrics MetricsFor(params int[] indexes)
    {
        var metrics = new FakeInterfaceMetrics();
        foreach (var i in indexes) metrics.Add(i);
        return metrics;
    }

    [Fact]
    public void Phone_mode_prefers_phone_on_both_ip_families()
    {
        var metrics = MetricsFor(31, 10);

        var result = new RoutingEngine(metrics).Apply(RoutingMode.Phone, Both());

        Assert.True(result.Success, result.Error);
        Assert.Equal(new InterfaceMetricState(false, 5), metrics.Get(31, IpFamily.IPv4));
        Assert.Equal(new InterfaceMetricState(false, 5), metrics.Get(31, IpFamily.IPv6));
        Assert.Equal(new InterfaceMetricState(false, 50), metrics.Get(10, IpFamily.IPv4));
        Assert.Equal(new InterfaceMetricState(false, 50), metrics.Get(10, IpFamily.IPv6));
    }

    [Fact]
    public void Lan_mode_prefers_lan()
    {
        var metrics = MetricsFor(31, 10);

        new RoutingEngine(metrics).Apply(RoutingMode.Lan, Both());

        Assert.Equal(new InterfaceMetricState(false, 50), metrics.Get(31, IpFamily.IPv4));
        Assert.Equal(new InterfaceMetricState(false, 5), metrics.Get(10, IpFamily.IPv4));
    }

    [Fact]
    public void Auto_mode_restores_automatic_metrics()
    {
        var metrics = MetricsFor(31, 10);
        var engine = new RoutingEngine(metrics);
        engine.Apply(RoutingMode.Phone, Both());

        var result = engine.Apply(RoutingMode.Auto, Both());

        Assert.True(result.Success, result.Error);
        Assert.All(metrics.State.Values, s => Assert.True(s.UseAutomatic));
    }

    [Fact]
    public void Interface_without_ipv6_is_skipped()
    {
        var metrics = new FakeInterfaceMetrics();
        metrics.Add(31, ipv6: false);
        metrics.Add(10);

        var result = new RoutingEngine(metrics).Apply(RoutingMode.Phone, Both());

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain(metrics.SetCalls, c => c.Index == 31 && c.Family == IpFamily.IPv6);
    }

    [Fact]
    public void Missing_phone_only_touches_lan()
    {
        var metrics = MetricsFor(10);
        var lanOnly = new DetectionResult(null, DetectionIssue.NotFound, TestAdapters.Lan(), DetectionIssue.None);

        var result = new RoutingEngine(metrics).Apply(RoutingMode.Phone, lanOnly);

        Assert.True(result.Success, result.Error);
        Assert.All(metrics.SetCalls, c => Assert.Equal(10, c.Index));
    }

    [Fact]
    public void Failure_on_one_interface_is_reported_and_the_rest_still_applied()
    {
        var metrics = MetricsFor(31, 10);
        metrics.FailOnSet.Add((31, IpFamily.IPv4));

        var result = new RoutingEngine(metrics).Apply(RoutingMode.Phone, Both());

        Assert.False(result.Success);
        Assert.Contains("Ethernet 31 IPv4", result.Error);
        Assert.Contains("Access is denied.", result.Error);
        Assert.Equal(new InterfaceMetricState(false, 50), metrics.Get(10, IpFamily.IPv4));
    }

    [Fact]
    public void Metric_that_does_not_stick_is_reported()
    {
        var metrics = MetricsFor(31, 10);
        metrics.IgnoreSet.Add((10, IpFamily.IPv4));

        var result = new RoutingEngine(metrics).Apply(RoutingMode.Phone, Both());

        Assert.False(result.Success);
        Assert.Contains("did not stick", result.Error);
    }

    [Fact]
    public void IsInSync_detects_drift_after_phone_replug()
    {
        var metrics = MetricsFor(31, 10);
        var engine = new RoutingEngine(metrics);
        engine.Apply(RoutingMode.Phone, Both());
        Assert.True(engine.IsInSync(RoutingMode.Phone, Both()));

        metrics.Add(36); // re-plugged phone comes back with an automatic metric
        var replugged = Both(TestAdapters.Phone(index: 36));
        Assert.False(engine.IsInSync(RoutingMode.Phone, replugged));

        engine.Apply(RoutingMode.Phone, replugged);
        Assert.True(engine.IsInSync(RoutingMode.Phone, replugged));
    }

    [Fact]
    public void Reset_restores_automatic_metric_on_both_families()
    {
        var metrics = MetricsFor(31, 10);
        var engine = new RoutingEngine(metrics);
        engine.Apply(RoutingMode.Phone, Both());

        var result = engine.Reset(TestAdapters.Lan());

        Assert.True(result.Success, result.Error);
        Assert.True(metrics.Get(10, IpFamily.IPv4)!.UseAutomatic);
        Assert.True(metrics.Get(10, IpFamily.IPv6)!.UseAutomatic);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "Category!=Integration"`
Expected: build FAILS with `CS0246: The type or namespace name 'IInterfaceMetrics' could not be found`.

- [ ] **Step 3: Implement**

`src/NetRoute.Core/Abstractions.cs`:

```csharp
using System.Net;

namespace NetRoute.Core;

public enum IpFamily { IPv4, IPv6 }

public sealed record InterfaceMetricState(bool UseAutomatic, uint Metric);

public interface IInterfaceMetrics
{
    /// Null when the interface has no stack for this family (e.g. IPv6 disabled).
    InterfaceMetricState? Get(int ifIndex, IpFamily family);

    /// metric null = automatic. Throws when Windows rejects the change.
    void Set(int ifIndex, IpFamily family, uint? metric);
}

public interface IAdapterSource
{
    IReadOnlyList<AdapterInfo> GetAdapters();
}

public interface IRouteQuery
{
    /// Interface Windows would use to reach destination (IPv4 or IPv6); null when there is no route.
    int? GetBestInterfaceIndex(IPAddress destination);
}

public interface ILatencyProbe
{
    /// Round-trip in ms from the given local address; null when there is no reply.
    Task<int?> MeasureAsync(IPAddress source, CancellationToken ct);
}
```

`src/NetRoute.Core/MetricPolicy.cs`:

```csharp
namespace NetRoute.Core;

/// Desired interface metric per adapter; null = Windows automatic metric.
public sealed record MetricTarget(uint? Phone, uint? Lan);

public static class MetricPolicy
{
    public const uint Preferred = 5;
    public const uint Backup = 50;

    public static MetricTarget For(RoutingMode mode) => mode switch
    {
        RoutingMode.Phone => new(Preferred, Backup),
        RoutingMode.Lan => new(Backup, Preferred),
        RoutingMode.Auto => new(null, null),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };
}
```

`src/NetRoute.Core/RoutingEngine.cs`:

```csharp
namespace NetRoute.Core;

public sealed record ApplyResult(bool Success, string? Error)
{
    public static readonly ApplyResult Ok = new(true, null);
}

/// Sets interface metrics for a mode and verifies them by reading back.
/// Never deletes routes: the non-preferred adapter keeps its default route as fallback.
public sealed class RoutingEngine(IInterfaceMetrics metrics)
{
    static readonly IpFamily[] Families = [IpFamily.IPv4, IpFamily.IPv6];

    public ApplyResult Apply(RoutingMode mode, DetectionResult adapters) =>
        ApplyTargets(Targets(adapters, MetricPolicy.For(mode)));

    /// Hands an adapter back to Windows' automatic metric.
    public ApplyResult Reset(AdapterInfo adapter) => ApplyTargets([(adapter, null)]);

    public bool IsInSync(RoutingMode mode, DetectionResult adapters)
    {
        foreach (var (adapter, metric) in Targets(adapters, MetricPolicy.For(mode)))
        foreach (var family in Families)
        {
            var state = metrics.Get(adapter.Index, family);
            if (state is not null && !Matches(state, metric)) return false;
        }
        return true;
    }

    ApplyResult ApplyTargets(IEnumerable<(AdapterInfo Adapter, uint? Metric)> targets)
    {
        var errors = new List<string>();
        foreach (var (adapter, metric) in targets)
        foreach (var family in Families)
        {
            try
            {
                if (metrics.Get(adapter.Index, family) is null) continue;
                metrics.Set(adapter.Index, family, metric);
                var after = metrics.Get(adapter.Index, family);
                if (after is null || !Matches(after, metric))
                    errors.Add($"{adapter.Name} {family}: metric did not stick");
            }
            catch (Exception ex)
            {
                errors.Add($"{adapter.Name} {family}: {ex.Message}");
            }
        }
        return errors.Count == 0 ? ApplyResult.Ok : new ApplyResult(false, string.Join("; ", errors));
    }

    static bool Matches(InterfaceMetricState state, uint? metric) =>
        metric is { } m ? !state.UseAutomatic && state.Metric == m : state.UseAutomatic;

    static IEnumerable<(AdapterInfo, uint?)> Targets(DetectionResult adapters, MetricTarget target)
    {
        if (adapters.Phone is { } phone) yield return (phone, target.Phone);
        if (adapters.Lan is { } lan) yield return (lan, target.Lan);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "Category!=Integration"`
Expected: PASS, 18 tests.

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "feat(core): metric policy and routing engine with read-back verification" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`nClaude-Session: https://claude.ai/code/session_01AypeDHU2CKcJxywGQXDkT3"
```

---

### Task 3: Settings store and file log

**Files:**
- Create: `src/NetRoute.Core/Settings.cs`, `src/NetRoute.Core/FileLog.cs`
- Create: `tests/NetRoute.Core.Tests/SettingsStoreTests.cs`, `tests/NetRoute.Core.Tests/FileLogTests.cs`

**Interfaces:**
- Consumes: `RoutingMode`, `AdapterOverrides` (Task 1)
- Produces:
  - `record AppSettings { RoutingMode Mode = Phone; string? PhoneOverride; string? LanOverrideMac; double? CardLeft; double? CardTop; bool CardVisible = true; bool StartWithWindows; AdapterOverrides Overrides (computed, not serialized) }`
  - `record SettingsLoadResult(AppSettings Settings, bool Recovered)`
  - `class SettingsStore(string path)`: `SettingsLoadResult Load()`, `void Save(AppSettings)`
  - `class FileLog(string directory, TimeProvider? time = null)`: `Info(string)`, `Error(string, Exception? = null)`, `PruneOldFiles()`, `const int KeepDays = 7`

- [ ] **Step 1: Write the failing tests**

`tests/NetRoute.Core.Tests/SettingsStoreTests.cs`:

```csharp
namespace NetRoute.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("netroute-settings-").FullName;
    string FilePath => Path.Combine(_dir, "settings.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Missing_file_loads_defaults_and_creates_the_file()
    {
        var result = new SettingsStore(FilePath).Load();

        Assert.Equal(new AppSettings(), result.Settings);
        Assert.Equal(RoutingMode.Phone, result.Settings.Mode);
        Assert.True(result.Settings.CardVisible);
        Assert.False(result.Recovered);
        Assert.True(File.Exists(FilePath));
    }

    [Fact]
    public void Saved_settings_round_trip_with_mode_as_text()
    {
        var store = new SettingsStore(FilePath);
        var settings = new AppSettings
        {
            Mode = RoutingMode.Lan, PhoneOverride = "Phone NDIS", LanOverrideMac = "AA-BB-CC-00-00-10",
            CardLeft = 100.5, CardTop = 200, CardVisible = false, StartWithWindows = true,
        };

        store.Save(settings);

        Assert.Equal(settings, store.Load().Settings);
        Assert.Contains("\"Lan\"", File.ReadAllText(FilePath));
        Assert.DoesNotContain("Overrides", File.ReadAllText(FilePath));
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("{ \"Mode\": \"Bogus\" }")]
    public void Corrupt_file_loads_defaults_and_is_repaired(string content)
    {
        File.WriteAllText(FilePath, content);
        var store = new SettingsStore(FilePath);

        var result = store.Load();

        Assert.True(result.Recovered);
        Assert.Equal(new AppSettings(), result.Settings);
        Assert.False(store.Load().Recovered);
    }

    [Fact]
    public void Unknown_mode_number_is_treated_as_corrupt()
    {
        File.WriteAllText(FilePath, "{ \"Mode\": 7 }");

        var result = new SettingsStore(FilePath).Load();

        Assert.True(result.Recovered);
        Assert.Equal(RoutingMode.Phone, result.Settings.Mode);
    }

    [Fact]
    public void Overrides_are_built_from_settings()
    {
        var settings = new AppSettings { PhoneOverride = "P", LanOverrideMac = "M" };

        Assert.Equal(new AdapterOverrides("P", "M"), settings.Overrides);
    }
}
```

`tests/NetRoute.Core.Tests/FileLogTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;

namespace NetRoute.Core.Tests;

public sealed class FileLogTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "netroute-logs-" + Guid.NewGuid().ToString("N"));
    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 2, 9, 30, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Writes_timestamped_lines_to_a_daily_file()
    {
        var log = new FileLog(_dir, _time);

        log.Info("Mode set to Phone");
        log.Error("Apply failed", new InvalidOperationException("Access is denied."));

        var lines = File.ReadAllLines(Path.Combine(_dir, "netroute-20261002.log"));
        Assert.Equal("2026-10-02 09:30:00.000 INFO Mode set to Phone", lines[0]);
        Assert.StartsWith("2026-10-02 09:30:00.000 ERROR Apply failed: System.InvalidOperationException: Access is denied.", lines[1]);
    }

    [Fact]
    public void Prune_keeps_seven_days_and_ignores_other_files()
    {
        Directory.CreateDirectory(_dir);
        foreach (var name in new[] { "netroute-20261002.log", "netroute-20260926.log", "netroute-20260925.log", "netroute-20260101.log", "notes.txt" })
            File.WriteAllText(Path.Combine(_dir, name), "x");

        new FileLog(_dir, _time).PruneOldFiles();

        var left = Directory.GetFiles(_dir).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(new[] { "netroute-20260926.log", "netroute-20261002.log", "notes.txt" }, left);
    }

    [Fact]
    public void Prune_on_missing_directory_does_nothing()
    {
        new FileLog(_dir, _time).PruneOldFiles();

        Assert.False(Directory.Exists(_dir));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "Category!=Integration"`
Expected: build FAILS with `CS0246: The type or namespace name 'SettingsStore' could not be found`.

- [ ] **Step 3: Implement**

`src/NetRoute.Core/Settings.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetRoute.Core;

public sealed record AppSettings
{
    public RoutingMode Mode { get; init; } = RoutingMode.Phone;
    public string? PhoneOverride { get; init; }
    public string? LanOverrideMac { get; init; }
    public double? CardLeft { get; init; }
    public double? CardTop { get; init; }
    public bool CardVisible { get; init; } = true;
    public bool StartWithWindows { get; init; }

    [JsonIgnore]
    public AdapterOverrides Overrides => new(PhoneOverride, LanOverrideMac);
}

public sealed record SettingsLoadResult(AppSettings Settings, bool Recovered);

public sealed class SettingsStore(string path)
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// Missing file: defaults (written). Corrupt file: defaults (rewritten), Recovered = true.
    public SettingsLoadResult Load()
    {
        if (!File.Exists(path))
        {
            var defaults = new AppSettings();
            Save(defaults);
            return new(defaults, false);
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options)
                ?? throw new JsonException("Settings file is null");
            if (!Enum.IsDefined(settings.Mode)) throw new JsonException($"Unknown mode {(int)settings.Mode}");
            return new(settings, false);
        }
        catch (JsonException)
        {
            var defaults = new AppSettings();
            Save(defaults);
            return new(defaults, true);
        }
    }

    /// Writes to a temp file first so a crash never leaves a half-written settings file.
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        File.Move(temp, path, overwrite: true);
    }
}
```

`src/NetRoute.Core/FileLog.cs`:

```csharp
using System.Globalization;

namespace NetRoute.Core;

/// Minimal daily-rolling log. Never throws: logging must not crash the widget.
public sealed class FileLog(string directory, TimeProvider? time = null)
{
    public const int KeepDays = 7;

    readonly TimeProvider _time = time ?? TimeProvider.System;
    readonly Lock _gate = new();

    public void Info(string message) => Write("INFO", message);

    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}: {exception}");

    public void PruneOldFiles()
    {
        if (!Directory.Exists(directory)) return;
        var oldestKept = _time.GetLocalNow().Date.AddDays(-(KeepDays - 1));
        foreach (var file in Directory.GetFiles(directory, "netroute-*.log"))
        {
            var stamp = Path.GetFileNameWithoutExtension(file)["netroute-".Length..];
            if (DateTime.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                && day < oldestKept)
            {
                try { File.Delete(file); } catch (IOException) { }
            }
        }
    }

    void Write(string level, string message)
    {
        var now = _time.GetLocalNow();
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, $"netroute-{now:yyyyMMdd}.log"),
                    $"{now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} {level} {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "Category!=Integration"`
Expected: PASS, 29 tests.

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "feat(core): settings store with corrupt-file recovery and daily file log" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`nClaude-Session: https://claude.ai/code/session_01AypeDHU2CKcJxywGQXDkT3"
```

---

### Task 4: Debouncer

**Files:**
- Create: `src/NetRoute.Core/Debouncer.cs`
- Create: `tests/NetRoute.Core.Tests/DebouncerTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `class Debouncer(TimeSpan quiet, Action action, TimeProvider? time = null) : IDisposable` with `void Signal()`. The action runs once, `quiet` after the **last** signal, on a timer thread.

- [ ] **Step 1: Write the failing tests**

`tests/NetRoute.Core.Tests/DebouncerTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;

namespace NetRoute.Core.Tests;

public class DebouncerTests
{
    readonly FakeTimeProvider _time = new();
    int _calls;

    Debouncer Create() => new(TimeSpan.FromMilliseconds(1500), () => _calls++, _time);

    [Fact]
    public void Burst_of_signals_fires_once_after_quiet_period()
    {
        using var debouncer = Create();
        for (var i = 0; i < 5; i++)
        {
            debouncer.Signal();
            _time.Advance(TimeSpan.FromMilliseconds(200));
        }
        Assert.Equal(0, _calls);

        _time.Advance(TimeSpan.FromMilliseconds(1300));
        Assert.Equal(1, _calls);

        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(1, _calls);
    }

    [Fact]
    public void Each_new_signal_restarts_the_quiet_period()
    {
        using var debouncer = Create();
        debouncer.Signal();
        _time.Advance(TimeSpan.FromMilliseconds(1400));
        debouncer.Signal();
        _time.Advance(TimeSpan.FromMilliseconds(1400));
        Assert.Equal(0, _calls);

        _time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, _calls);
    }

    [Fact]
    public void Separate_bursts_fire_separately()
    {
        using var debouncer = Create();
        debouncer.Signal();
        _time.Advance(TimeSpan.FromMilliseconds(1500));
        debouncer.Signal();
        _time.Advance(TimeSpan.FromMilliseconds(1500));

        Assert.Equal(2, _calls);
    }

    [Fact]
    public void Without_signals_nothing_fires()
    {
        using var debouncer = Create();
        _time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(0, _calls);
    }

    [Fact]
    public void Disposed_debouncer_does_not_fire()
    {
        var debouncer = Create();
        debouncer.Signal();
        debouncer.Dispose();
        _time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(0, _calls);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "Category!=Integration"`
Expected: build FAILS with `CS0246: The type or namespace name 'Debouncer' could not be found`.

- [ ] **Step 3: Implement**

`src/NetRoute.Core/Debouncer.cs`:

```csharp
namespace NetRoute.Core;

/// Collapses bursts of signals (e.g. a phone being plugged in fires many network events)
/// into one action, run `quiet` after the last signal.
public sealed class Debouncer : IDisposable
{
    readonly TimeSpan _quiet;
    readonly ITimer _timer;

    public Debouncer(TimeSpan quiet, Action action, TimeProvider? time = null)
    {
        _quiet = quiet;
        _timer = (time ?? TimeProvider.System).CreateTimer(
            _ => action(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Signal() => _timer.Change(_quiet, Timeout.InfiniteTimeSpan);

    public void Dispose() => _timer.Dispose();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "Category!=Integration"`
Expected: PASS, 34 tests.

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "feat(core): debouncer for network event bursts" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`nClaude-Session: https://claude.ai/code/session_01AypeDHU2CKcJxywGQXDkT3"
```

---

### Task 5: Network status and status presenter

**Files:**
- Create: `src/NetRoute.Core/NetworkStatus.cs`, `src/NetRoute.Core/StatusPresenter.cs`
- Create: `tests/NetRoute.Core.Tests/StatusPresenterTests.cs`

**Interfaces:**
- Consumes: `RoutingMode`, `InternetPath`, `AdapterInfo`, `DetectionResult`, `DetectionIssue` (Task 1)
- Produces:
  - `record NetworkStatus(RoutingMode Mode, DetectionResult Adapters, InternetPath ActivePath, InternetPath Ipv6Path, int? PhoneLatencyMs, int? LanLatencyMs, bool CanModify, string? Error)` with `bool IsFallback`, `static NetworkStatus Initial(RoutingMode, bool canModify)` and `static InternetPath ResolvePath(int? bestInterfaceIndex, DetectionResult)`
  - `enum Dot { Green, Amber, Gray }`, `enum TrayColor { Green, Blue, Gray }`
  - `record AdapterRow(string Label, string Address, string Latency, Dot Dot, bool IsActive)`
  - `record CardView(string Header, bool HeaderWarning, string? Note, AdapterRow Phone, AdapterRow Lan, string Tooltip, TrayColor TrayColor, bool TrayBadge)`
  - `static StatusPresenter`: `CardView Present(NetworkStatus)`, `string? ToastFor(NetworkStatus before, NetworkStatus after)`

- [ ] **Step 1: Write the failing tests**

`tests/NetRoute.Core.Tests/StatusPresenterTests.cs`:

```csharp
namespace NetRoute.Core.Tests;

public class StatusPresenterTests
{
    static NetworkStatus Status(
        RoutingMode mode = RoutingMode.Phone, InternetPath path = InternetPath.Phone,
        bool phone = true, bool lan = true, int? phoneMs = 38, int? lanMs = 12,
        string? error = null, InternetPath ipv6 = InternetPath.None,
        DetectionIssue missingLanIssue = DetectionIssue.NotFound) =>
        new(mode,
            new DetectionResult(
                phone ? TestAdapters.Phone() : null, phone ? DetectionIssue.None : DetectionIssue.NotFound,
                lan ? TestAdapters.Lan() : null, lan ? DetectionIssue.None : missingLanIssue),
            path, ipv6, phone ? phoneMs : null, lan ? lanMs : null, CanModify: true, error);

    [Fact]
    public void Normal_phone_mode()
    {
        var view = StatusPresenter.Present(Status());

        Assert.Equal("Internet via PHONE", view.Header);
        Assert.False(view.HeaderWarning);
        Assert.Null(view.Note);
        Assert.Equal(new AdapterRow("Phone", "192.168.42.11", "38 ms", Dot.Green, true), view.Phone);
        Assert.Equal(new AdapterRow("LAN", "192.168.86.42", "12 ms", Dot.Green, false), view.Lan);
        Assert.Equal("Internet: Phone (38 ms) · LAN ready", view.Tooltip);
        Assert.Equal(TrayColor.Green, view.TrayColor);
        Assert.False(view.TrayBadge);
    }

    [Fact]
    public void Phone_unplugged_in_phone_mode_shows_fallback()
    {
        var view = StatusPresenter.Present(Status(path: InternetPath.Lan, phone: false));

        Assert.Equal("Internet via LAN (phone offline)", view.Header);
        Assert.True(view.HeaderWarning);
        Assert.True(view.TrayBadge);
        Assert.Equal(new AdapterRow("Phone", "not connected", "", Dot.Gray, false), view.Phone);
        Assert.True(view.Lan.IsActive);
        Assert.Equal("Internet: LAN (12 ms) · Phone offline", view.Tooltip);
    }

    [Fact]
    public void Phone_connected_but_not_routing_shows_reason()
    {
        var view = StatusPresenter.Present(Status(path: InternetPath.Lan));

        Assert.Equal("Internet via LAN (phone not routing)", view.Header);
    }

    [Fact]
    public void Lan_mode_fallback_to_phone()
    {
        var view = StatusPresenter.Present(Status(mode: RoutingMode.Lan, path: InternetPath.Phone, lan: false));

        Assert.Equal("Internet via PHONE (LAN offline)", view.Header);
        Assert.Equal(TrayColor.Blue, view.TrayColor);
        Assert.True(view.TrayBadge);
    }

    [Fact]
    public void Adapter_without_probe_reply_is_amber()
    {
        var view = StatusPresenter.Present(Status(phoneMs: null));

        Assert.Equal(new AdapterRow("Phone", "192.168.42.11", "no reply", Dot.Amber, true), view.Phone);
    }

    [Fact]
    public void Ambiguous_adapter_asks_to_choose()
    {
        var view = StatusPresenter.Present(Status(lan: false, missingLanIssue: DetectionIssue.Ambiguous));

        Assert.Equal("choose adapter…", view.Lan.Address);
        Assert.Equal(Dot.Gray, view.Lan.Dot);
    }

    [Fact]
    public void No_internet_is_a_warning()
    {
        var view = StatusPresenter.Present(Status(path: InternetPath.None));

        Assert.Equal("No internet", view.Header);
        Assert.True(view.HeaderWarning);
        Assert.True(view.TrayBadge);
        Assert.Equal("Internet: none · LAN ready", view.Tooltip);
    }

    [Fact]
    public void Error_replaces_header_and_tooltip()
    {
        var view = StatusPresenter.Present(Status(error: "Ethernet IPv4: Access is denied."));

        Assert.Equal("Ethernet IPv4: Access is denied.", view.Header);
        Assert.True(view.HeaderWarning);
        Assert.Equal("NetRoute: Ethernet IPv4: Access is denied.", view.Tooltip);
    }

    [Fact]
    public void Ipv6_on_a_different_adapter_adds_a_note()
    {
        Assert.Equal("IPv6 traffic goes via LAN", StatusPresenter.Present(Status(ipv6: InternetPath.Lan)).Note);
        Assert.Null(StatusPresenter.Present(Status(ipv6: InternetPath.Phone)).Note);
        Assert.Null(StatusPresenter.Present(Status(ipv6: InternetPath.None)).Note);
    }

    [Fact]
    public void Auto_mode_is_gray_and_other_adapter_is_named()
    {
        var auto = StatusPresenter.Present(Status(mode: RoutingMode.Auto, path: InternetPath.Lan));
        Assert.Equal(TrayColor.Gray, auto.TrayColor);
        Assert.False(auto.TrayBadge);
        Assert.Equal("Internet via LAN", auto.Header);

        Assert.Equal("Internet via other adapter", StatusPresenter.Present(Status(path: InternetPath.Other)).Header);
    }

    [Fact]
    public void Toast_when_phone_disconnects()
    {
        Assert.Equal("Phone disconnected — internet via LAN",
            StatusPresenter.ToastFor(Status(), Status(path: InternetPath.Lan, phone: false)));
    }

    [Fact]
    public void Toast_when_phone_comes_back()
    {
        Assert.Equal("Phone back — internet via Phone",
            StatusPresenter.ToastFor(Status(path: InternetPath.Lan, phone: false), Status()));
    }

    [Fact]
    public void Toast_when_phone_stays_connected_but_loses_the_route()
    {
        Assert.Equal("Phone lost internet — internet via LAN",
            StatusPresenter.ToastFor(Status(), Status(path: InternetPath.Lan)));
    }

    [Fact]
    public void No_toast_when_path_is_unchanged()
    {
        Assert.Null(StatusPresenter.ToastFor(Status(), Status(phoneMs: 99)));
    }

    [Fact]
    public void Toast_when_internet_is_lost()
    {
        Assert.Equal("No internet connection", StatusPresenter.ToastFor(Status(), Status(path: InternetPath.None)));
    }

    [Fact]
    public void ResolvePath_maps_best_interface_to_adapter()
    {
        var adapters = Status().Adapters;

        Assert.Equal(InternetPath.Phone, NetworkStatus.ResolvePath(31, adapters));
        Assert.Equal(InternetPath.Lan, NetworkStatus.ResolvePath(10, adapters));
        Assert.Equal(InternetPath.Other, NetworkStatus.ResolvePath(40, adapters));
        Assert.Equal(InternetPath.None, NetworkStatus.ResolvePath(null, adapters));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "Category!=Integration"`
Expected: build FAILS with `CS0246: The type or namespace name 'NetworkStatus' could not be found`.

- [ ] **Step 3: Implement**

`src/NetRoute.Core/NetworkStatus.cs`:

```csharp
namespace NetRoute.Core;

public sealed record NetworkStatus(
    RoutingMode Mode,
    DetectionResult Adapters,
    InternetPath ActivePath,
    InternetPath Ipv6Path,
    int? PhoneLatencyMs,
    int? LanLatencyMs,
    bool CanModify,
    string? Error)
{
    /// The preferred adapter is not the one carrying internet.
    public bool IsFallback =>
        (Mode == RoutingMode.Phone && ActivePath == InternetPath.Lan) ||
        (Mode == RoutingMode.Lan && ActivePath == InternetPath.Phone);

    public static NetworkStatus Initial(RoutingMode mode, bool canModify) =>
        new(mode, DetectionResult.Empty, InternetPath.None, InternetPath.None, null, null, canModify, null);

    public static InternetPath ResolvePath(int? bestInterfaceIndex, DetectionResult adapters) => bestInterfaceIndex switch
    {
        null => InternetPath.None,
        var i when adapters.Phone?.Index == i => InternetPath.Phone,
        var i when adapters.Lan?.Index == i => InternetPath.Lan,
        _ => InternetPath.Other,
    };
}
```

`src/NetRoute.Core/StatusPresenter.cs`:

```csharp
namespace NetRoute.Core;

public enum Dot { Green, Amber, Gray }

public enum TrayColor { Green, Blue, Gray }

public sealed record AdapterRow(string Label, string Address, string Latency, Dot Dot, bool IsActive);

public sealed record CardView(
    string Header, bool HeaderWarning, string? Note,
    AdapterRow Phone, AdapterRow Lan,
    string Tooltip, TrayColor TrayColor, bool TrayBadge);

/// Pure mapping from status to what the card, tray icon and toasts show.
public static class StatusPresenter
{
    public static CardView Present(NetworkStatus s)
    {
        var noInternet = s.ActivePath == InternetPath.None;
        return new CardView(
            HeaderText(s),
            HeaderWarning: s.Error is not null || s.IsFallback || noInternet,
            NoteText(s),
            Row("Phone", s.Adapters.Phone, s.Adapters.PhoneIssue, s.PhoneLatencyMs, s.ActivePath == InternetPath.Phone),
            Row("LAN", s.Adapters.Lan, s.Adapters.LanIssue, s.LanLatencyMs, s.ActivePath == InternetPath.Lan),
            TooltipText(s),
            s.Mode switch { RoutingMode.Phone => TrayColor.Green, RoutingMode.Lan => TrayColor.Blue, _ => TrayColor.Gray },
            TrayBadge: s.IsFallback || noInternet);
    }

    /// Message for an automatic (not user-initiated) change of internet path; null when the path did not change.
    public static string? ToastFor(NetworkStatus before, NetworkStatus after)
    {
        if (before.ActivePath == after.ActivePath) return null;
        return after.ActivePath switch
        {
            InternetPath.None => "No internet connection",
            InternetPath.Other => "Internet now via another adapter (VPN?)",
            InternetPath.Lan when after.IsFallback =>
                $"Phone {(after.Adapters.Phone is null ? "disconnected" : "lost internet")} — internet via LAN",
            InternetPath.Phone when after.IsFallback =>
                $"LAN {(after.Adapters.Lan is null ? "disconnected" : "lost internet")} — internet via Phone",
            InternetPath.Phone => after.Mode == RoutingMode.Phone ? "Phone back — internet via Phone" : "Internet now via Phone",
            _ => after.Mode == RoutingMode.Lan ? "LAN back — internet via LAN" : "Internet now via LAN",
        };
    }

    static string HeaderText(NetworkStatus s)
    {
        if (s.Error is { } error) return error;
        return s.ActivePath switch
        {
            InternetPath.None => "No internet",
            InternetPath.Other => "Internet via other adapter",
            InternetPath.Phone when s.IsFallback => $"Internet via PHONE ({Reason(s.Adapters.Lan, "LAN")})",
            InternetPath.Lan when s.IsFallback => $"Internet via LAN ({Reason(s.Adapters.Phone, "phone")})",
            InternetPath.Phone => "Internet via PHONE",
            _ => "Internet via LAN",
        };
    }

    static string Reason(AdapterInfo? preferred, string name) =>
        preferred is null ? $"{name} offline" : $"{name} not routing";

    static string? NoteText(NetworkStatus s) =>
        s.Ipv6Path != InternetPath.None && s.ActivePath != InternetPath.None && s.Ipv6Path != s.ActivePath
            ? $"IPv6 traffic goes via {PathName(s.Ipv6Path)}"
            : null;

    static AdapterRow Row(string label, AdapterInfo? adapter, DetectionIssue issue, int? latencyMs, bool active)
    {
        if (adapter is null)
            return new(label, issue == DetectionIssue.Ambiguous ? "choose adapter…" : "not connected", "", Dot.Gray, false);
        var address = adapter.IPv4 ?? "no IPv4";
        return latencyMs is { } ms
            ? new(label, address, $"{ms} ms", Dot.Green, active)
            : new(label, address, "no reply", Dot.Amber, active);
    }

    static string TooltipText(NetworkStatus s)
    {
        if (s.Error is { } error) return $"NetRoute: {error}";
        var path = s.ActivePath switch
        {
            InternetPath.Phone => $"Phone{Ms(s.PhoneLatencyMs)}",
            InternetPath.Lan => $"LAN{Ms(s.LanLatencyMs)}",
            InternetPath.Other => "other adapter",
            _ => "none",
        };
        var backup = s.ActivePath == InternetPath.Lan
            ? $"Phone {(s.Adapters.Phone is null ? "offline" : "ready")}"
            : $"LAN {(s.Adapters.Lan is null ? "offline" : "ready")}";
        return $"Internet: {path} · {backup}";
    }

    static string Ms(int? ms) => ms is { } v ? $" ({v} ms)" : "";

    static string PathName(InternetPath path) => path switch
    {
        InternetPath.Phone => "Phone",
        InternetPath.Lan => "LAN",
        _ => "another adapter",
    };
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "Category!=Integration"`
Expected: PASS, 50 tests.

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "feat(core): network status model and presenter for card, tray and toasts" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`nClaude-Session: https://claude.ai/code/session_01AypeDHU2CKcJxywGQXDkT3"
```

---

### Task 6: Route controller

**Files:**
- Create: `src/NetRoute.Core/RouteController.cs`
- Modify: `tests/NetRoute.Core.Tests/Fakes.cs` (add two usings, append three fakes)
- Create: `tests/NetRoute.Core.Tests/RouteControllerTests.cs`

**Interfaces:**
- Consumes: `IAdapterSource`, `IInterfaceMetrics`, `IRouteQuery`, `ILatencyProbe`, `RoutingEngine`, `ApplyResult` (Task 2); `AppSettings` (Task 3); `NetworkStatus`, `StatusPresenter.ToastFor` (Task 5); `AdapterDetector`, `AdapterOverrides` (Task 1)
- Produces: `class RouteController` with:
  - constructor `RouteController(IAdapterSource adapters, IInterfaceMetrics metrics, IRouteQuery routes, ILatencyProbe probe, AppSettings settings, bool canModify, Action<AppSettings> persist, Action<string> log)`
  - `static IPAddress ProbeTargetV4` (1.1.1.1) and `ProbeTargetV6` (2606:4700:4700::1111)
  - `NetworkStatus Status { get; }`, `AppSettings Settings { get; }`
  - `event Action<NetworkStatus>? StatusChanged`, `event Action<string>? AutoSwitched`
  - `Task RefreshAsync(bool measureLatency, CancellationToken ct = default)` (never throws except on cancellation)
  - `Task<ApplyResult> SetModeAsync(RoutingMode mode, CancellationToken ct = default)`
  - `Task SetOverridesAsync(AdapterOverrides overrides, CancellationToken ct = default)`
  - `void UpdateSettings(Func<AppSettings, AppSettings> change)` (persists)

- [ ] **Step 1: Extend the fakes**

At the **top** of `tests/NetRoute.Core.Tests/Fakes.cs`, above `namespace`, add:

```csharp
using System.Net;
using System.Net.Sockets;
```

Append at the end of the same file:

```csharp
sealed class FakeAdapterSource : IAdapterSource
{
    public List<AdapterInfo> Adapters { get; } = new();
    public Exception? Throw { get; set; }

    public IReadOnlyList<AdapterInfo> GetAdapters() => Throw is { } ex ? throw ex : Adapters.ToList();
}

sealed class FakeRouteQuery : IRouteQuery
{
    public int? BestV4 { get; set; }
    public int? BestV6 { get; set; }

    public int? GetBestInterfaceIndex(IPAddress destination) =>
        destination.AddressFamily == AddressFamily.InterNetworkV6 ? BestV6 : BestV4;
}

sealed class FakeLatencyProbe : ILatencyProbe
{
    public Dictionary<string, int?> BySource { get; } = new();

    public Task<int?> MeasureAsync(IPAddress source, CancellationToken ct) =>
        Task.FromResult(BySource.TryGetValue(source.ToString(), out var ms) ? ms : null);
}
```

- [ ] **Step 2: Write the failing tests**

`tests/NetRoute.Core.Tests/RouteControllerTests.cs`:

```csharp
namespace NetRoute.Core.Tests;

public class RouteControllerTests
{
    readonly FakeAdapterSource _adapters = new();
    readonly FakeInterfaceMetrics _metrics = new();
    readonly FakeRouteQuery _routes = new();
    readonly FakeLatencyProbe _probe = new();
    readonly List<AppSettings> _saved = new();
    readonly List<string> _toasts = new();

    public RouteControllerTests()
    {
        _adapters.Adapters.AddRange([TestAdapters.Phone(), TestAdapters.Lan()]);
        _metrics.Add(31);
        _metrics.Add(10);
        _routes.BestV4 = 31;
        _probe.BySource["192.168.42.11"] = 38;
        _probe.BySource["192.168.86.42"] = 12;
    }

    RouteController Create(AppSettings? settings = null, bool canModify = true)
    {
        var controller = new RouteController(_adapters, _metrics, _routes, _probe,
            settings ?? new AppSettings(), canModify, _saved.Add, _ => { });
        controller.AutoSwitched += _toasts.Add;
        return controller;
    }

    [Fact]
    public async Task First_refresh_applies_mode_and_reports_status_without_toast()
    {
        var controller = Create();
        NetworkStatus? published = null;
        controller.StatusChanged += s => published = s;

        await controller.RefreshAsync(measureLatency: true);

        Assert.Equal(new InterfaceMetricState(false, 5), _metrics.Get(31, IpFamily.IPv4));
        Assert.Equal(new InterfaceMetricState(false, 50), _metrics.Get(10, IpFamily.IPv4));
        Assert.Equal(InternetPath.Phone, controller.Status.ActivePath);
        Assert.Equal(38, controller.Status.PhoneLatencyMs);
        Assert.Equal(12, controller.Status.LanLatencyMs);
        Assert.Null(controller.Status.Error);
        Assert.Same(controller.Status, published);
        Assert.Empty(_toasts);
    }

    [Fact]
    public async Task Refresh_when_in_sync_writes_nothing()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);
        var writes = _metrics.SetCalls.Count;

        await controller.RefreshAsync(measureLatency: true);

        Assert.Equal(writes, _metrics.SetCalls.Count);
    }

    [Fact]
    public async Task Phone_unplug_and_replug_switches_and_toasts()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);

        _adapters.Adapters.RemoveAll(a => a.Index == 31);
        _routes.BestV4 = 10;
        await controller.RefreshAsync(measureLatency: true);

        Assert.Equal(InternetPath.Lan, controller.Status.ActivePath);
        Assert.True(controller.Status.IsFallback);
        Assert.Equal(new[] { "Phone disconnected — internet via LAN" }, _toasts);

        _adapters.Adapters.Add(TestAdapters.Phone(index: 36, description: "SAMSUNG Mobile USB Remote NDIS Network Device #2"));
        _metrics.Add(36);
        _routes.BestV4 = 36;
        await controller.RefreshAsync(measureLatency: true);

        Assert.Equal(new InterfaceMetricState(false, 5), _metrics.Get(36, IpFamily.IPv4));
        Assert.Equal(InternetPath.Phone, controller.Status.ActivePath);
        Assert.Equal("Phone back — internet via Phone", _toasts.Last());
    }

    [Fact]
    public async Task SetMode_applies_persists_and_does_not_toast()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);
        _routes.BestV4 = 10;

        var result = await controller.SetModeAsync(RoutingMode.Lan);

        Assert.True(result.Success, result.Error);
        Assert.Equal(new InterfaceMetricState(false, 5), _metrics.Get(10, IpFamily.IPv4));
        Assert.Equal(RoutingMode.Lan, controller.Status.Mode);
        Assert.Equal(InternetPath.Lan, controller.Status.ActivePath);
        Assert.Equal(RoutingMode.Lan, _saved.Last().Mode);
        Assert.Empty(_toasts);
    }

    [Fact]
    public async Task Path_settling_after_user_mode_change_does_not_toast()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);
        await controller.SetModeAsync(RoutingMode.Lan); // route table still reports the phone

        _routes.BestV4 = 10; // ...and settles on the LAN a moment later
        await controller.RefreshAsync(measureLatency: true);

        Assert.Equal(InternetPath.Lan, controller.Status.ActivePath);
        Assert.Empty(_toasts);
    }

    [Fact]
    public async Task Not_elevated_never_writes_metrics()
    {
        var controller = Create(canModify: false);

        await controller.RefreshAsync(measureLatency: true);
        var result = await controller.SetModeAsync(RoutingMode.Lan);

        Assert.False(result.Success);
        Assert.Contains("Administrator", result.Error);
        Assert.Empty(_metrics.SetCalls);
        Assert.Empty(_saved);
        Assert.Equal(RoutingMode.Phone, controller.Status.Mode);
        Assert.False(controller.Status.CanModify);
    }

    [Fact]
    public async Task Failed_SetMode_keeps_previous_mode_and_shows_error()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);
        _metrics.FailOnSet.Add((10, IpFamily.IPv4));

        var result = await controller.SetModeAsync(RoutingMode.Lan);

        Assert.False(result.Success);
        Assert.Equal(RoutingMode.Phone, controller.Status.Mode);
        Assert.Equal(RoutingMode.Phone, controller.Settings.Mode);
        Assert.Contains("Access is denied.", controller.Status.Error);
        Assert.DoesNotContain(_saved, s => s.Mode == RoutingMode.Lan);
    }

    [Fact]
    public async Task Refresh_failure_is_reported_not_thrown()
    {
        var controller = Create();
        _adapters.Throw = new InvalidOperationException("boom");

        await controller.RefreshAsync(measureLatency: true);

        Assert.Contains("boom", controller.Status.Error);
    }

    [Fact]
    public async Task Ipv6_path_is_resolved_separately()
    {
        var controller = Create();
        _routes.BestV6 = 10;

        await controller.RefreshAsync(measureLatency: true);

        Assert.Equal(InternetPath.Phone, controller.Status.ActivePath);
        Assert.Equal(InternetPath.Lan, controller.Status.Ipv6Path);
    }

    [Fact]
    public async Task Changing_lan_override_resets_previous_adapter_to_automatic()
    {
        _adapters.Adapters.Add(TestAdapters.Lan(index: 12, mac: "AA-BB-CC-00-00-12"));
        _metrics.Add(12);
        var controller = Create(new AppSettings { LanOverrideMac = "AA-BB-CC-00-00-10" });
        await controller.RefreshAsync(measureLatency: true);
        Assert.Equal(new InterfaceMetricState(false, 50), _metrics.Get(10, IpFamily.IPv4));

        await controller.SetOverridesAsync(new AdapterOverrides(null, "AA-BB-CC-00-00-12"));

        Assert.True(_metrics.Get(10, IpFamily.IPv4)!.UseAutomatic);
        Assert.Equal(new InterfaceMetricState(false, 50), _metrics.Get(12, IpFamily.IPv4));
        Assert.Equal(12, controller.Status.Adapters.Lan?.Index);
        Assert.Equal("AA-BB-CC-00-00-12", _saved.Last().LanOverrideMac);
    }

    [Fact]
    public async Task Latency_is_kept_between_probes_and_cleared_when_adapter_disappears()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);

        await controller.RefreshAsync(measureLatency: false);
        Assert.Equal(38, controller.Status.PhoneLatencyMs);

        _adapters.Adapters.RemoveAll(a => a.Index == 31);
        await controller.RefreshAsync(measureLatency: false);
        Assert.Null(controller.Status.PhoneLatencyMs);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --filter "Category!=Integration"`
Expected: build FAILS with `CS0246: The type or namespace name 'RouteController' could not be found`.

- [ ] **Step 4: Implement**

`src/NetRoute.Core/RouteController.cs`:

```csharp
using System.Net;

namespace NetRoute.Core;

/// Orchestrates detect → apply metrics → resolve path → publish status.
/// Work is serialised by a gate, so it is safe to call from timers, network events and the UI.
/// Events are raised on the calling thread; UI subscribers must marshal to their dispatcher.
public sealed class RouteController
{
    public static readonly IPAddress ProbeTargetV4 = IPAddress.Parse("1.1.1.1");
    public static readonly IPAddress ProbeTargetV6 = IPAddress.Parse("2606:4700:4700::1111");

    readonly IAdapterSource _adapters;
    readonly IRouteQuery _routes;
    readonly ILatencyProbe _probe;
    readonly RoutingEngine _engine;
    readonly Action<AppSettings> _persist;
    readonly Action<string> _log;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly Lock _settingsLock = new();
    AppSettings _settings;
    bool _hasRefreshed;
    bool _userChangePending;

    public RouteController(
        IAdapterSource adapters, IInterfaceMetrics metrics, IRouteQuery routes, ILatencyProbe probe,
        AppSettings settings, bool canModify, Action<AppSettings> persist, Action<string> log)
    {
        _adapters = adapters;
        _routes = routes;
        _probe = probe;
        _engine = new RoutingEngine(metrics);
        _settings = settings;
        _persist = persist;
        _log = log;
        Status = NetworkStatus.Initial(settings.Mode, canModify);
    }

    public NetworkStatus Status { get; private set; }

    public AppSettings Settings
    {
        get { lock (_settingsLock) return _settings; }
    }

    public event Action<NetworkStatus>? StatusChanged;

    /// Raised for internet-path changes the user did not cause (unplug, replug, lost route).
    public event Action<string>? AutoSwitched;

    public void UpdateSettings(Func<AppSettings, AppSettings> change)
    {
        AppSettings updated;
        lock (_settingsLock) updated = _settings = change(_settings);
        _persist(updated);
    }

    public async Task RefreshAsync(bool measureLatency, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var before = Status;
            var mode = Settings.Mode;
            var detection = AdapterDetector.Detect(_adapters.GetAdapters(), Settings.Overrides);

            string? error = null;
            if (before.CanModify && !_engine.IsInSync(mode, detection))
            {
                _log($"Metrics differ from {mode} mode; applying");
                var result = _engine.Apply(mode, detection);
                if (!result.Success)
                {
                    error = result.Error;
                    _log($"Apply {mode} failed: {result.Error}");
                }
            }

            var (phoneMs, lanMs) = measureLatency
                ? await MeasureAsync(detection, ct)
                : (detection.Phone is null ? null : before.PhoneLatencyMs, detection.Lan is null ? null : before.LanLatencyMs);

            var after = before with
            {
                Mode = mode,
                Adapters = detection,
                ActivePath = NetworkStatus.ResolvePath(_routes.GetBestInterfaceIndex(ProbeTargetV4), detection),
                Ipv6Path = NetworkStatus.ResolvePath(_routes.GetBestInterfaceIndex(ProbeTargetV6), detection),
                PhoneLatencyMs = phoneMs,
                LanLatencyMs = lanMs,
                Error = error,
            };

            // Right after a user mode change the route table may settle a moment later:
            // that move to the preferred adapter is the user's doing, not worth a toast.
            var settlingAfterUserChange = _userChangePending && !after.IsFallback;
            _userChangePending = false;
            Publish(after, _hasRefreshed && !settlingAfterUserChange ? StatusPresenter.ToastFor(before, after) : null);
            _hasRefreshed = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"Refresh failed: {ex}");
            Publish(Status with { Error = $"Refresh failed: {ex.Message}" }, toast: null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ApplyResult> SetModeAsync(RoutingMode mode, CancellationToken ct = default)
    {
        if (!Status.CanModify)
            return new ApplyResult(false, "Administrator rights are needed to change routing");

        await _gate.WaitAsync(ct);
        try
        {
            var detection = AdapterDetector.Detect(_adapters.GetAdapters(), Settings.Overrides);
            var result = _engine.Apply(mode, detection);
            if (result.Success)
            {
                UpdateSettings(s => s with { Mode = mode });
                _log($"Mode set to {mode}");
            }
            else
            {
                _log($"Mode {mode} failed: {result.Error}");
            }

            _userChangePending = true;
            Publish(Status with
            {
                Mode = Settings.Mode,
                Adapters = detection,
                ActivePath = NetworkStatus.ResolvePath(_routes.GetBestInterfaceIndex(ProbeTargetV4), detection),
                Ipv6Path = NetworkStatus.ResolvePath(_routes.GetBestInterfaceIndex(ProbeTargetV6), detection),
                Error = result.Error,
            }, toast: null);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"Mode {mode} failed: {ex}");
            Publish(Status with { Error = ex.Message }, toast: null);
            return new ApplyResult(false, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// Saves the user's adapter choice. An adapter that stops being phone/LAN is reset to the
    /// automatic metric so a stale preferred metric cannot keep winning.
    public async Task SetOverridesAsync(AdapterOverrides overrides, CancellationToken ct = default)
    {
        UpdateSettings(s => s with { PhoneOverride = overrides.PhoneDescription, LanOverrideMac = overrides.LanMac });
        _log($"Adapter overrides: phone={overrides.PhoneDescription ?? "auto"}, lan={overrides.LanMac ?? "auto"}");

        if (Status.CanModify)
        {
            await _gate.WaitAsync(ct);
            try
            {
                var previous = Status.Adapters;
                var next = AdapterDetector.Detect(_adapters.GetAdapters(), overrides);
                foreach (var old in new[] { previous.Phone, previous.Lan })
                {
                    if (old is null || old.Index == next.Phone?.Index || old.Index == next.Lan?.Index) continue;
                    var reset = _engine.Reset(old);
                    _log(reset.Success ? $"Reset {old.Name} to automatic metric" : $"Reset {old.Name} failed: {reset.Error}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log($"Override reset failed: {ex}");
            }
            finally
            {
                _gate.Release();
            }
        }

        await RefreshAsync(measureLatency: true, ct);
    }

    async Task<(int?, int?)> MeasureAsync(DetectionResult adapters, CancellationToken ct)
    {
        var phone = Probe(adapters.Phone, ct);
        var lan = Probe(adapters.Lan, ct);
        return (await phone, await lan);
    }

    Task<int?> Probe(AdapterInfo? adapter, CancellationToken ct) =>
        adapter?.IPv4 is { } ip ? _probe.MeasureAsync(IPAddress.Parse(ip), ct) : Task.FromResult<int?>(null);

    void Publish(NetworkStatus status, string? toast)
    {
        Status = status;
        StatusChanged?.Invoke(status);
        if (toast is null) return;
        _log($"Auto-switch: {toast}");
        AutoSwitched?.Invoke(toast);
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "Category!=Integration"`
Expected: PASS, 61 tests.

- [ ] **Step 6: Commit**

```powershell
git add -A
git commit -m "feat(core): route controller orchestrating detection, metrics, status and toasts" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`nClaude-Session: https://claude.ai/code/session_01AypeDHU2CKcJxywGQXDkT3"
```

---

### Task 7: Windows interop (IP Helper, adapters, latency probe)

**Files:**
- Create: `src/NetRoute.Core/Windows/IpHelperNative.cs`, `src/NetRoute.Core/Windows/WindowsInterfaceMetrics.cs`, `src/NetRoute.Core/Windows/WindowsRouteQuery.cs`, `src/NetRoute.Core/Windows/WindowsAdapterSource.cs`, `src/NetRoute.Core/Windows/TcpLatencyProbe.cs`
- Create: `tests/NetRoute.Core.Tests/IpHelperLayoutTests.cs`, `tests/NetRoute.Core.Tests/TcpLatencyProbeTests.cs`, `tests/NetRoute.Core.Tests/WindowsIntegrationTests.cs`

**Interfaces:**
- Consumes: `IInterfaceMetrics`, `IRouteQuery`, `IAdapterSource`, `ILatencyProbe`, `IpFamily`, `InterfaceMetricState` (Task 2); `AdapterInfo`, `AdapterKind` (Task 1); `RouteController.ProbeTargetV4/V6` (Task 6)
- Produces (namespace `NetRoute.Core.Windows`):
  - `class WindowsInterfaceMetrics : IInterfaceMetrics`
  - `class WindowsRouteQuery : IRouteQuery`
  - `class WindowsAdapterSource : IAdapterSource`
  - `class TcpLatencyProbe(IPEndPoint target, TimeSpan timeout) : ILatencyProbe` with `static TcpLatencyProbe Default()` (1.1.1.1:443, 2 s)
  - `internal static class IpHelperNative` (P/Invoke and `MIB_IPINTERFACE_ROW`, 168 bytes on x64)

> **Safety gate:** `SetIpInterfaceEntry` writes back the **whole** row. A wrong struct layout would write junk into neighbouring fields (forwarding, DAD, `DisableDefaultRoutes`). This task only **reads** from the real machine. The integration tests in Step 6 must pass before Task 9, which is the first time the app writes metrics on the real machine.

- [ ] **Step 1: Write the failing unit tests**

`tests/NetRoute.Core.Tests/IpHelperLayoutTests.cs`:

```csharp
using System.Runtime.InteropServices;
using NetRoute.Core.Windows;

namespace NetRoute.Core.Tests;

public class IpHelperLayoutTests
{
    [Fact]
    public void Interface_row_matches_native_size_on_x64()
    {
        Assert.True(Environment.Is64BitProcess, "Layout check assumes x64");
        Assert.Equal(168, Marshal.SizeOf<IpHelperNative.MIB_IPINTERFACE_ROW>());
    }
}
```

`tests/NetRoute.Core.Tests/TcpLatencyProbeTests.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using NetRoute.Core.Windows;

namespace NetRoute.Core.Tests;

public class TcpLatencyProbeTests
{
    [Fact]
    public async Task Measures_connect_time_to_a_listening_port()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var probe = new TcpLatencyProbe((IPEndPoint)listener.LocalEndpoint, TimeSpan.FromSeconds(2));

            var ms = await probe.MeasureAsync(IPAddress.Loopback, CancellationToken.None);

            Assert.NotNull(ms);
            Assert.InRange(ms.Value, 1, 2000);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Returns_null_when_nothing_answers()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var closed = (IPEndPoint)listener.LocalEndpoint;
        listener.Stop();
        var probe = new TcpLatencyProbe(closed, TimeSpan.FromMilliseconds(500));

        Assert.Null(await probe.MeasureAsync(IPAddress.Loopback, CancellationToken.None));
    }
}
```

- [ ] **Step 2: Write the read-only integration tests**

`tests/NetRoute.Core.Tests/WindowsIntegrationTests.cs`:

```csharp
using System.Diagnostics;
using NetRoute.Core.Windows;

namespace NetRoute.Core.Tests;

/// Read-only checks against the real machine. They never change any setting.
[Trait("Category", "Integration")]
public class WindowsIntegrationTests
{
    [Fact]
    public void Metrics_read_through_ip_helper_match_Get_NetIPInterface()
    {
        var expected = PowerShell(
                "Get-NetIPInterface -ConnectionState Connected | ForEach-Object { '{0},{1},{2},{3}' -f $_.ifIndex, $_.AddressFamily, $_.InterfaceMetric, $_.AutomaticMetric }")
            .Select(line => line.Split(','))
            .Select(f => (Index: int.Parse(f[0]), Family: f[1] == "IPv4" ? IpFamily.IPv4 : IpFamily.IPv6,
                          Metric: uint.Parse(f[2]), Automatic: f[3] == "Enabled"))
            .ToList();
        Assert.NotEmpty(expected);

        var metrics = new WindowsInterfaceMetrics();
        foreach (var e in expected)
        {
            var actual = metrics.Get(e.Index, e.Family);
            Assert.NotNull(actual);
            Assert.Equal((e.Index, e.Family, e.Automatic, e.Metric), (e.Index, e.Family, actual.UseAutomatic, actual.Metric));
        }
    }

    [Fact]
    public void Best_interface_for_1_1_1_1_matches_Find_NetRoute()
    {
        var expected = int.Parse(PowerShell("(Find-NetRoute -RemoteIPAddress 1.1.1.1 | Select-Object -First 1).InterfaceIndex").Single());

        Assert.Equal(expected, new WindowsRouteQuery().GetBestInterfaceIndex(RouteController.ProbeTargetV4));
    }

    [Fact]
    public void Ipv6_route_query_does_not_throw()
    {
        var index = new WindowsRouteQuery().GetBestInterfaceIndex(RouteController.ProbeTargetV6);

        Assert.True(index is null or > 0);
    }

    [Fact]
    public void Adapter_source_reports_the_internet_adapter_as_up_with_an_address()
    {
        var best = new WindowsRouteQuery().GetBestInterfaceIndex(RouteController.ProbeTargetV4);

        var adapter = new WindowsAdapterSource().GetAdapters().Single(a => a.Index == best);

        Assert.True(adapter.IsUp);
        Assert.True(adapter.HasGateway);
        Assert.NotNull(adapter.IPv4);
    }

    static string[] PowerShell(string script)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --filter "Category!=Integration"`
Expected: build FAILS with `CS0246: The type or namespace name 'TcpLatencyProbe' could not be found`.

- [ ] **Step 4: Implement**

`src/NetRoute.Core/Windows/IpHelperNative.cs`:

```csharp
using System.Runtime.InteropServices;

namespace NetRoute.Core.Windows;

/// iphlpapi.dll interop. MIB_IPINTERFACE_ROW layout follows netioapi.h exactly (168 bytes on x64);
/// BOOLEAN fields are single bytes. A layout mistake corrupts other interface settings on Set,
/// so IpHelperLayoutTests and WindowsIntegrationTests guard it.
internal static class IpHelperNative
{
    public const ushort AF_INET = 2;
    public const ushort AF_INET6 = 23;
    public const int NO_ERROR = 0;
    public const int ERROR_NOT_FOUND = 1168;

    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_IPINTERFACE_ROW
    {
        public ushort Family;
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public uint MaxReassemblySize;
        public ulong InterfaceIdentifier;
        public uint MinRouterAdvertisementInterval;
        public uint MaxRouterAdvertisementInterval;
        public byte AdvertisingEnabled;
        public byte ForwardingEnabled;
        public byte WeakHostSend;
        public byte WeakHostReceive;
        public byte UseAutomaticMetric;
        public byte UseNeighborUnreachabilityDetection;
        public byte ManagedAddressConfigurationSupported;
        public byte OtherStatefulConfigurationSupported;
        public byte AdvertiseDefaultRoute;
        public int RouterDiscoveryBehavior;
        public uint DadTransmits;
        public uint BaseReachableTime;
        public uint RetransmitTime;
        public uint PathMtuDiscoveryTimeout;
        public int LinkLocalAddressBehavior;
        public uint LinkLocalAddressTimeout;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public uint[] ZoneIndices;
        public uint SitePrefixLength;
        public uint Metric;
        public uint NlMtu;
        public byte Connected;
        public byte SupportsWakeUpPatterns;
        public byte SupportsNeighborDiscovery;
        public byte SupportsRouterDiscovery;
        public uint ReachableTime;
        public byte TransmitOffload;
        public byte ReceiveOffload;
        public byte DisableDefaultRoutes;
    }

    [DllImport("iphlpapi.dll")]
    public static extern int GetIpInterfaceEntry(ref MIB_IPINTERFACE_ROW row);

    [DllImport("iphlpapi.dll")]
    public static extern int SetIpInterfaceEntry(ref MIB_IPINTERFACE_ROW row);

    /// destAddr is a raw SOCKADDR (sockaddr_in or sockaddr_in6).
    [DllImport("iphlpapi.dll")]
    public static extern int GetBestInterfaceEx(byte[] destAddr, out uint bestIfIndex);
}
```

`src/NetRoute.Core/Windows/WindowsInterfaceMetrics.cs`:

```csharp
using System.ComponentModel;

namespace NetRoute.Core.Windows;

/// Reads and writes interface metrics with GetIpInterfaceEntry / SetIpInterfaceEntry (needs admin to write).
public sealed class WindowsInterfaceMetrics : IInterfaceMetrics
{
    public InterfaceMetricState? Get(int ifIndex, IpFamily family)
    {
        var row = Read(ifIndex, family, out var rc);
        if (rc == IpHelperNative.ERROR_NOT_FOUND) return null;
        if (rc != IpHelperNative.NO_ERROR) throw new Win32Exception(rc);
        return new InterfaceMetricState(row.UseAutomaticMetric != 0, row.Metric);
    }

    public void Set(int ifIndex, IpFamily family, uint? metric)
    {
        var row = Read(ifIndex, family, out var rc);
        if (rc != IpHelperNative.NO_ERROR) throw new Win32Exception(rc);

        row.UseAutomaticMetric = metric is null ? (byte)1 : (byte)0;
        if (metric is { } m) row.Metric = m;
        // Documented quirk: for IPv4 SitePrefixLength must be 0, or Set fails with ERROR_INVALID_PARAMETER.
        if (family == IpFamily.IPv4) row.SitePrefixLength = 0;

        rc = IpHelperNative.SetIpInterfaceEntry(ref row);
        if (rc != IpHelperNative.NO_ERROR) throw new Win32Exception(rc);
    }

    static IpHelperNative.MIB_IPINTERFACE_ROW Read(int ifIndex, IpFamily family, out int rc)
    {
        var row = new IpHelperNative.MIB_IPINTERFACE_ROW
        {
            Family = family == IpFamily.IPv4 ? IpHelperNative.AF_INET : IpHelperNative.AF_INET6,
            InterfaceIndex = (uint)ifIndex,
            ZoneIndices = new uint[16],
        };
        rc = IpHelperNative.GetIpInterfaceEntry(ref row);
        return row;
    }
}
```

`src/NetRoute.Core/Windows/WindowsRouteQuery.cs`:

```csharp
using System.Net;

namespace NetRoute.Core.Windows;

/// Asks Windows which interface it would use for a destination, honouring interface metrics.
public sealed class WindowsRouteQuery : IRouteQuery
{
    public int? GetBestInterfaceIndex(IPAddress destination)
    {
        var socketAddress = new IPEndPoint(destination, 0).Serialize();
        var buffer = new byte[socketAddress.Size];
        for (var i = 0; i < buffer.Length; i++) buffer[i] = socketAddress[i];

        return IpHelperNative.GetBestInterfaceEx(buffer, out var index) == IpHelperNative.NO_ERROR ? (int)index : null;
    }
}
```

`src/NetRoute.Core/Windows/WindowsAdapterSource.cs`:

```csharp
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetRoute.Core.Windows;

public sealed class WindowsAdapterSource : IAdapterSource
{
    public IReadOnlyList<AdapterInfo> GetAdapters()
    {
        var adapters = new List<AdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var properties = nic.GetIPProperties();
            if (IndexOf(properties) is not { } index) continue;

            var ipv4 = properties.UnicastAddresses
                .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
            var hasGateway = properties.GatewayAddresses
                .Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any));

            adapters.Add(new AdapterInfo(
                index, nic.Name, nic.Description, KindOf(nic.NetworkInterfaceType),
                nic.OperationalStatus == OperationalStatus.Up, hasGateway, ipv4, FormatMac(nic.GetPhysicalAddress())));
        }
        return adapters;
    }

    /// The ifIndex is shared by both IP families; an interface may have only one of them.
    static int? IndexOf(IPInterfaceProperties properties)
    {
        try
        {
            if (properties.GetIPv4Properties() is { } v4) return v4.Index;
        }
        catch (NetworkInformationException) { }
        try
        {
            if (properties.GetIPv6Properties() is { } v6) return v6.Index;
        }
        catch (NetworkInformationException) { }
        return null;
    }

    static AdapterKind KindOf(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => AdapterKind.Ethernet,
        NetworkInterfaceType.Wireless80211 => AdapterKind.Wireless,
        _ => AdapterKind.Other,
    };

    static string FormatMac(PhysicalAddress mac) =>
        string.Join("-", mac.GetAddressBytes().Select(b => b.ToString("X2")));
}
```

`src/NetRoute.Core/Windows/TcpLatencyProbe.cs`:

```csharp
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace NetRoute.Core.Windows;

/// Latency = TCP handshake time from a socket bound to one adapter's address.
/// Windows' strong-host model sends it out of that adapter. TCP instead of ICMP because some networks drop ICMP.
public sealed class TcpLatencyProbe(IPEndPoint target, TimeSpan timeout) : ILatencyProbe
{
    public static TcpLatencyProbe Default() =>
        new(new IPEndPoint(RouteController.ProbeTargetV4, 443), TimeSpan.FromSeconds(2));

    public async Task<int?> MeasureAsync(IPAddress source, CancellationToken ct)
    {
        using var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            socket.Bind(new IPEndPoint(source, 0));
            var stopwatch = Stopwatch.StartNew();
            await socket.ConnectAsync(target, timeoutCts.Token);
            return (int)Math.Max(1, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }
}
```

- [ ] **Step 5: Run unit tests to verify they pass**

Run: `dotnet test --filter "Category!=Integration"`
Expected: PASS, 64 tests.

- [ ] **Step 6: Run the read-only integration tests on the real machine**

Run: `dotnet test --filter "Category=Integration"`
Expected: PASS, 4 tests.
- If `Metrics_read_through_ip_helper_match_Get_NetIPInterface` fails, the struct layout is wrong. **Stop**: do not continue to Task 9 until it passes.
- If `Best_interface_…` fails because the machine is offline, reconnect and re-run.

- [ ] **Step 7: Commit**

```powershell
git add -A
git commit -m "feat(core): Windows interop for interface metrics, best route, adapters and TCP latency" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`nClaude-Session: https://claude.ai/code/session_01AypeDHU2CKcJxywGQXDkT3"
```

---

### Task 8: "Start with Windows" scheduled task

**Files:**
- Create: `src/NetRoute.Core/StartupTaskXml.cs`, `src/NetRoute.Core/Windows/StartupTask.cs`
- Create: `tests/NetRoute.Core.Tests/StartupTaskXmlTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces:
  - `static StartupTaskXml`: `XDocument Build(string exePath, string userId)` and `void Save(XDocument document, string path)` (UTF-16)
  - `class NetRoute.Core.Windows.StartupTask(string taskName = "NetRouteWidget")`: `bool Exists()`, `void Enable(string exePath)` (needs admin), `void Disable()`. Failures throw `InvalidOperationException` carrying the schtasks error text.

- [ ] **Step 1: Write the failing tests**

`tests/NetRoute.Core.Tests/StartupTaskXmlTests.cs`:

```csharp
using System.Xml.Linq;

namespace NetRoute.Core.Tests;

public class StartupTaskXmlTests
{
    static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    const string Exe = @"F:\Apps & Tools\NetRouteWidget\NetRouteWidget.exe";

    static string Value(XDocument doc, string name) => doc.Descendants(Ns + name).Single().Value;

    [Fact]
    public void Task_runs_elevated_at_logon_of_the_user()
    {
        var doc = StartupTaskXml.Build(Exe, @"PC\someone");

        Assert.Equal("HighestAvailable", Value(doc, "RunLevel"));
        Assert.Equal("InteractiveToken", Value(doc, "LogonType"));
        Assert.Equal(@"PC\someone", doc.Descendants(Ns + "LogonTrigger").Single().Element(Ns + "UserId")!.Value);
        Assert.Equal(Exe, Value(doc, "Command"));
    }

    [Fact]
    public void Task_runs_on_battery_and_never_times_out()
    {
        var doc = StartupTaskXml.Build(Exe, @"PC\someone");

        Assert.Equal("false", Value(doc, "DisallowStartIfOnBatteries"));
        Assert.Equal("false", Value(doc, "StopIfGoingOnBatteries"));
        Assert.Equal("PT0S", Value(doc, "ExecutionTimeLimit"));
        Assert.Equal("IgnoreNew", Value(doc, "MultipleInstancesPolicy"));
    }

    [Fact]
    public void Special_characters_in_the_path_are_escaped()
    {
        var xml = StartupTaskXml.Build(Exe, @"PC\someone").ToString();

        Assert.Contains("Apps &amp; Tools", xml);
    }

    [Fact]
    public void Saved_file_is_utf16_with_matching_declaration()
    {
        var path = Path.Combine(Path.GetTempPath(), $"netroute-task-{Guid.NewGuid():N}.xml");
        try
        {
            StartupTaskXml.Save(StartupTaskXml.Build(Exe, @"PC\someone"), path);

            var bytes = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0xFF, 0xFE }, bytes[..2]);
            Assert.Contains("encoding=\"utf-16\"", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Exe, Value(XDocument.Load(path), "Command"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "Category!=Integration"`
Expected: build FAILS with `CS0103: The name 'StartupTaskXml' does not exist in the current context`.

- [ ] **Step 3: Implement**

`src/NetRoute.Core/StartupTaskXml.cs`:

```csharp
using System.Text;
using System.Xml.Linq;

namespace NetRoute.Core;

/// Task Scheduler definition for "Start with Windows": at logon, elevated, so no UAC prompt.
/// Built as XML because schtasks' command-line defaults refuse to start on battery
/// and stop the task after 72 hours.
public static class StartupTaskXml
{
    static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static XDocument Build(string exePath, string userId) =>
        new(new XDeclaration("1.0", "UTF-16", null),
            new XElement(Ns + "Task", new XAttribute("version", "1.2"),
                new XElement(Ns + "RegistrationInfo",
                    new XElement(Ns + "Description", "Starts NetRoute Widget at logon with administrator rights.")),
                new XElement(Ns + "Triggers",
                    new XElement(Ns + "LogonTrigger",
                        new XElement(Ns + "Enabled", "true"),
                        new XElement(Ns + "UserId", userId))),
                new XElement(Ns + "Principals",
                    new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                        new XElement(Ns + "UserId", userId),
                        new XElement(Ns + "LogonType", "InteractiveToken"),
                        new XElement(Ns + "RunLevel", "HighestAvailable"))),
                new XElement(Ns + "Settings",
                    new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(Ns + "StopIfGoingOnBatteries", "false"),
                    new XElement(Ns + "ExecutionTimeLimit", "PT0S"),
                    new XElement(Ns + "Priority", "7")),
                new XElement(Ns + "Actions", new XAttribute("Context", "Author"),
                    new XElement(Ns + "Exec",
                        new XElement(Ns + "Command", exePath)))));

    /// schtasks /XML expects a UTF-16 file when the declaration says UTF-16.
    public static void Save(XDocument document, string path)
    {
        using var writer = new StreamWriter(path, append: false, Encoding.Unicode);
        document.Save(writer);
    }
}
```

`src/NetRoute.Core/Windows/StartupTask.cs`:

```csharp
using System.Diagnostics;
using System.Security.Principal;

namespace NetRoute.Core.Windows;

/// Creates and removes the "Start with Windows" scheduled task through schtasks.exe.
/// Enable/Disable need administrator rights; Exists does not.
public sealed class StartupTask(string taskName = "NetRouteWidget")
{
    public bool Exists() => Run("/Query", "/TN", taskName).ExitCode == 0;

    public void Enable(string exePath)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var xmlPath = Path.Combine(Path.GetTempPath(), $"{taskName}-{Guid.NewGuid():N}.xml");
        try
        {
            StartupTaskXml.Save(StartupTaskXml.Build(exePath, identity.Name), xmlPath);
            Check(Run("/Create", "/TN", taskName, "/XML", xmlPath, "/F"), "create");
        }
        finally
        {
            File.Delete(xmlPath);
        }
    }

    public void Disable()
    {
        if (Exists()) Check(Run("/Delete", "/TN", taskName, "/F"), "delete");
    }

    static void Check((int ExitCode, string Error) result, string action)
    {
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Could not {action} the startup task: {result.Error.Trim()}");
    }

    static (int ExitCode, string Error) Run(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var error = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, error.Result);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "Category!=Integration"`
Expected: PASS, 68 tests.

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "feat(core): elevated logon scheduled task for Start with Windows" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`nClaude-Session: https://claude.ai/code/session_01AypeDHU2CKcJxywGQXDkT3"
```

---

### Task 9: WPF app — tray icon, floating card, wiring

**Files:**
- Create: `src/NetRoute.Core/CardPlacement.cs`, `tests/NetRoute.Core.Tests/CardPlacementTests.cs`
- Create: `src/NetRoute.App/NetRoute.App.csproj`, `App.xaml`, `App.xaml.cs`, `AppPaths.cs`, `Elevation.cs`, `SingleInstance.cs`, `Theme.cs`, `TrayIcon.cs`, `CardWindow.xaml`, `CardWindow.xaml.cs` (all under `src/NetRoute.App/`)

**Interfaces:**
- Consumes: everything from Tasks 1–8. Specifically `RouteController` (ctor, `Status`, `Settings`, `StatusChanged`, `AutoSwitched`, `RefreshAsync`, `SetModeAsync`, `UpdateSettings`), `StatusPresenter.Present`, `CardView`, `AdapterRow`, `Dot`, `TrayColor`, `Debouncer`, `SettingsStore`, `FileLog`, `WindowsAdapterSource`, `WindowsInterfaceMetrics`, `WindowsRouteQuery`, `TcpLatencyProbe.Default()`, `StartupTask`.
- Produces:
  - `record Bounds(double Left, double Top, double Width, double Height)` and `static CardPlacement.Resolve(double? savedLeft, double? savedTop, double cardWidth, double cardHeight, Bounds virtualScreen, Bounds workArea) → (double Left, double Top)`, `CardPlacement.Margin = 16`
  - `CardWindow`, with events `ModeRequested`, `HideRequested`, `Moved`, `StartWithWindowsToggled`, `OpenNetworkSettingsRequested`, `RestartAsAdminRequested`, `QuitRequested` and methods `ShowAndPlace`, `Render`, `ForceClose`. Task 10 adds `ChooseAdaptersRequested`.
  - `App` private methods that Task 10 hooks into: `OnStartup` and `Render`.

- [ ] **Step 1: Write the failing placement tests**

`tests/NetRoute.Core.Tests/CardPlacementTests.cs`:

```csharp
namespace NetRoute.Core.Tests;

public class CardPlacementTests
{
    static readonly Bounds Screen = new(0, 0, 1920, 1080);
    static readonly Bounds Work = new(0, 0, 1920, 1040);

    [Fact]
    public void No_saved_position_goes_bottom_right_of_work_area()
    {
        Assert.Equal((1614d, 874d), CardPlacement.Resolve(null, null, 290, 150, Screen, Work));
    }

    [Fact]
    public void Saved_position_on_screen_is_kept()
    {
        Assert.Equal((100d, 200d), CardPlacement.Resolve(100, 200, 290, 150, Screen, Work));
    }

    [Fact]
    public void Position_on_an_unplugged_monitor_falls_back_to_bottom_right()
    {
        Assert.Equal((1614d, 874d), CardPlacement.Resolve(2500, 300, 290, 150, Screen, Work));
    }

    [Fact]
    public void Partially_off_screen_position_falls_back_to_bottom_right()
    {
        Assert.Equal((1614d, 874d), CardPlacement.Resolve(1800, 1000, 290, 150, Screen, Work));
    }

    [Fact]
    public void Position_on_a_monitor_left_of_the_primary_is_kept()
    {
        var twoMonitors = new Bounds(-1920, 0, 3840, 1080);

        Assert.Equal((-1500d, 100d), CardPlacement.Resolve(-1500, 100, 290, 150, twoMonitors, Work));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "Category!=Integration"`
Expected: build FAILS with `CS0246: The type or namespace name 'Bounds' could not be found`.

- [ ] **Step 3: Implement placement**

`src/NetRoute.Core/CardPlacement.cs`:

```csharp
namespace NetRoute.Core;

public sealed record Bounds(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

public static class CardPlacement
{
    public const double Margin = 16;

    /// Keeps the saved position when the whole card still fits on the (multi-monitor) virtual screen;
    /// otherwise, e.g. after a monitor was unplugged, puts it bottom-right of the primary work area.
    public static (double Left, double Top) Resolve(
        double? savedLeft, double? savedTop, double cardWidth, double cardHeight, Bounds virtualScreen, Bounds workArea)
    {
        if (savedLeft is { } left && savedTop is { } top &&
            left >= virtualScreen.Left && top >= virtualScreen.Top &&
            left + cardWidth <= virtualScreen.Right && top + cardHeight <= virtualScreen.Bottom)
            return (left, top);

        return (workArea.Right - cardWidth - Margin, workArea.Bottom - cardHeight - Margin);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "Category!=Integration"`
Expected: PASS, 73 tests.

- [ ] **Step 5: Create the WPF project**

`src/NetRoute.App/NetRoute.App.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWPF>true</UseWPF>
    <UseWindowsForms>true</UseWindowsForms>
    <AssemblyName>NetRouteWidget</AssemblyName>
    <RootNamespace>NetRoute.App</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <!-- WPF + WinForms (for NotifyIcon): drop the WinForms global usings so Application,
         MessageBox, Color… resolve to WPF. WinForms types are used fully qualified. -->
    <Using Remove="System.Windows.Forms" />
    <Using Remove="System.Drawing" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\NetRoute.Core\NetRoute.Core.csproj" />
  </ItemGroup>

</Project>
```

`src/NetRoute.App/App.xaml`:

```xml
<Application x:Class="NetRoute.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             ShutdownMode="OnExplicitShutdown" />
```

`src/NetRoute.App/AppPaths.cs`:

```csharp
namespace NetRoute.App;

static class AppPaths
{
    public static readonly string Root =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetRouteWidget");

    public static readonly string SettingsFile = Path.Combine(Root, "settings.json");

    public static readonly string LogDir = Path.Combine(Root, "logs");
}
```

`src/NetRoute.App/Elevation.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace NetRoute.App;

static class Elevation
{
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// Starts this exe elevated (UAC prompt). False when the user cancels the prompt.
    public static bool TryStartElevated(string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arguments) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
```

`src/NetRoute.App/SingleInstance.cs`:

```csharp
using System.Windows;

namespace NetRoute.App;

/// One widget per user session. A second launch asks the first one to show its card.
sealed class SingleInstance : IDisposable
{
    const string MutexName = @"Local\NetRouteWidget";
    const string ShowEventName = @"Local\NetRouteWidget.Show";

    Mutex? _mutex;
    EventWaitHandle? _show;
    RegisteredWaitHandle? _registration;

    /// waitForPrevious: used by "Restart as admin", where the old instance is still shutting down.
    public bool TryAcquire(bool waitForPrevious)
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            if (!createdNew && !WaitForOwner(mutex, waitForPrevious))
            {
                mutex.Dispose();
                SignalExisting();
                return false;
            }
            _mutex = mutex;
        }
        catch (UnauthorizedAccessException)
        {
            SignalExisting(); // the running instance is elevated; we are not
            return false;
        }

        _show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        return true;
    }

    public void ListenForShow(Action onShow) =>
        _registration = ThreadPool.RegisterWaitForSingleObject(_show!, (_, _) => onShow(), null, Timeout.Infinite, executeOnlyOnce: false);

    public void Dispose()
    {
        _registration?.Unregister(null);
        _show?.Dispose();
        if (_mutex is null) return;
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    static bool WaitForOwner(Mutex mutex, bool waitForPrevious)
    {
        if (!waitForPrevious) return false;
        try
        {
            return mutex.WaitOne(TimeSpan.FromSeconds(10));
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    static void SignalExisting()
    {
        try
        {
            using var show = EventWaitHandle.OpenExisting(ShowEventName);
            show.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
        {
            MessageBox.Show("NetRoute Widget is already running as administrator. Use its tray icon.",
                "NetRoute Widget", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
```

`src/NetRoute.App/Theme.cs`:

```csharp
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace NetRoute.App;

/// Card colours following the Windows app theme (light/dark).
static class Theme
{
    static readonly Dictionary<string, (string Light, string Dark)> Palette = new()
    {
        ["CardBackground"] = ("#F2FAFAFA", "#F21F1F1F"),
        ["CardBorder"] = ("#22000000", "#33FFFFFF"),
        ["TextPrimary"] = ("#FF1B1B1B", "#FFF3F3F3"),
        ["TextSecondary"] = ("#FF5F5F5F", "#FFB0B0B0"),
        ["ButtonBackground"] = ("#FFE6E6E6", "#FF2D2D2D"),
        ["Accent"] = ("#FF2563EB", "#FF3B82F6"),
        ["Warning"] = ("#FFB45309", "#FFF5A623"),
        ["DotGreen"] = ("#FF2EA043", "#FF3FB950"),
        ["DotAmber"] = ("#FFD97706", "#FFF5A623"),
        ["DotGray"] = ("#FF8B949E", "#FF6E7681"),
    };

    public static void Apply(ResourceDictionary resources)
    {
        var light = IsLight();
        foreach (var (key, (lightHex, darkHex)) in Palette)
            resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? lightHex : darkHex));
    }

    static bool IsLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
    }
}
```

`src/NetRoute.App/TrayIcon.cs`:

```csharp
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using NetRoute.Core;
using Forms = System.Windows.Forms;

namespace NetRoute.App;

/// Tray icon: globe coloured by mode (green Phone, blue LAN, gray Auto), amber badge on fallback.
sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon _icon = new();
    readonly Dictionary<(TrayColor, bool), Icon> _icons = new();
    readonly Forms.ToolStripMenuItem _phone = new("Phone");
    readonly Forms.ToolStripMenuItem _lan = new("LAN");
    readonly Forms.ToolStripMenuItem _auto = new("Auto");
    readonly Forms.ToolStripMenuItem _show = new("Show card");
    readonly Forms.ToolStripMenuItem _startup = new("Start with Windows");
    readonly Forms.ToolStripMenuItem _quit = new("Quit");

    public TrayIcon()
    {
        _phone.Click += (_, _) => ModeRequested?.Invoke(RoutingMode.Phone);
        _lan.Click += (_, _) => ModeRequested?.Invoke(RoutingMode.Lan);
        _auto.Click += (_, _) => ModeRequested?.Invoke(RoutingMode.Auto);
        _show.Click += (_, _) => ShowCardRequested?.Invoke();
        _startup.Click += (_, _) => StartWithWindowsToggled?.Invoke();
        _quit.Click += (_, _) => QuitRequested?.Invoke();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.AddRange(new Forms.ToolStripItem[]
        {
            _phone, _lan, _auto, new Forms.ToolStripSeparator(), _show, _startup, new Forms.ToolStripSeparator(), _quit,
        });
        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) ToggleCardRequested?.Invoke();
        };
        _icon.Icon = IconFor(TrayColor.Gray, badge: false);
        _icon.Text = "NetRoute Widget";
        _icon.Visible = true;
    }

    public event Action<RoutingMode>? ModeRequested;
    public event Action? ToggleCardRequested;
    public event Action? ShowCardRequested;
    public event Action? StartWithWindowsToggled;
    public event Action? QuitRequested;

    public void Update(CardView view, RoutingMode mode, bool canModify, bool startWithWindows)
    {
        _icon.Icon = IconFor(view.TrayColor, view.TrayBadge);
        _icon.Text = view.Tooltip.Length > 127 ? view.Tooltip[..127] : view.Tooltip;
        _phone.Checked = mode == RoutingMode.Phone;
        _lan.Checked = mode == RoutingMode.Lan;
        _auto.Checked = mode == RoutingMode.Auto;
        _phone.Enabled = _lan.Enabled = _auto.Enabled = canModify;
        _startup.Checked = startWithWindows;
        _startup.Enabled = canModify;
    }

    public void Notify(string message) =>
        _icon.ShowBalloonTip(5000, "NetRoute Widget", message, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var icon in _icons.Values) icon.Dispose();
    }

    Icon IconFor(TrayColor color, bool badge)
    {
        if (!_icons.TryGetValue((color, badge), out var icon))
            _icons[(color, badge)] = icon = Draw(color, badge);
        return icon;
    }

    static Icon Draw(TrayColor color, bool badge)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var fill = color switch
            {
                TrayColor.Green => Color.FromArgb(0x2E, 0xA0, 0x43),
                TrayColor.Blue => Color.FromArgb(0x25, 0x63, 0xEB),
                _ => Color.FromArgb(0x8B, 0x94, 0x9E),
            };
            using var globe = new SolidBrush(fill);
            using var lines = new Pen(Color.White, 2);
            g.FillEllipse(globe, 2, 2, 28, 28);
            g.DrawEllipse(lines, 10, 3, 12, 26);
            g.DrawLine(lines, 3, 16, 29, 16);
            if (badge)
            {
                using var amber = new SolidBrush(Color.FromArgb(0xF5, 0xA6, 0x23));
                using var ring = new Pen(Color.White, 1.5f);
                g.FillEllipse(amber, 18, 18, 13, 13);
                g.DrawEllipse(ring, 18, 18, 13, 13);
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}
```

`src/NetRoute.App/CardWindow.xaml`:

```xml
<Window x:Class="NetRoute.App.CardWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="NetRoute Widget" Width="290" SizeToContent="Height"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        Topmost="True" ShowInTaskbar="False" ResizeMode="NoResize"
        FontFamily="Segoe UI" FontSize="12">
    <Window.Resources>
        <Style x:Key="IconButton" TargetType="Button">
            <Setter Property="Width" Value="22" />
            <Setter Property="Height" Value="22" />
            <Setter Property="Margin" Value="2,0,0,0" />
            <Setter Property="Foreground" Value="{DynamicResource TextSecondary}" />
            <Setter Property="Cursor" Value="Hand" />
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Button">
                        <Border x:Name="Bd" Background="Transparent" CornerRadius="4">
                            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="Bd" Property="Background" Value="{DynamicResource ButtonBackground}" />
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <Style x:Key="ModeButton" TargetType="ToggleButton">
            <Setter Property="Margin" Value="2" />
            <Setter Property="Padding" Value="0,6" />
            <Setter Property="Foreground" Value="{DynamicResource TextPrimary}" />
            <Setter Property="Background" Value="{DynamicResource ButtonBackground}" />
            <Setter Property="Cursor" Value="Hand" />
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="ToggleButton">
                        <Border x:Name="Bd" CornerRadius="6" Background="{TemplateBinding Background}" Padding="{TemplateBinding Padding}">
                            <ContentPresenter HorizontalAlignment="Center" />
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsChecked" Value="True">
                                <Setter TargetName="Bd" Property="Background" Value="{DynamicResource Accent}" />
                                <Setter Property="Foreground" Value="White" />
                                <Setter Property="FontWeight" Value="SemiBold" />
                            </Trigger>
                            <Trigger Property="IsEnabled" Value="False">
                                <Setter TargetName="Bd" Property="Opacity" Value="0.45" />
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <Style x:Key="Primary" TargetType="TextBlock">
            <Setter Property="Foreground" Value="{DynamicResource TextPrimary}" />
            <Setter Property="VerticalAlignment" Value="Center" />
        </Style>

        <Style x:Key="Secondary" TargetType="TextBlock">
            <Setter Property="Foreground" Value="{DynamicResource TextSecondary}" />
            <Setter Property="VerticalAlignment" Value="Center" />
        </Style>
    </Window.Resources>

    <Border CornerRadius="10" Padding="12,10" BorderThickness="1"
            Background="{DynamicResource CardBackground}" BorderBrush="{DynamicResource CardBorder}">
        <StackPanel>
            <DockPanel Background="Transparent" MouseLeftButtonDown="OnDrag">
                <Button DockPanel.Dock="Right" Content="✕" Style="{StaticResource IconButton}" Click="OnHide" ToolTip="Hide to tray" />
                <Button x:Name="MenuButton" DockPanel.Dock="Right" Content="⋮" Style="{StaticResource IconButton}" Click="OnMenu" ToolTip="Options">
                    <Button.ContextMenu>
                        <ContextMenu x:Name="OptionsMenu">
                            <MenuItem x:Name="StartWithWindowsItem" Header="Start with Windows" IsCheckable="True" Click="OnStartWithWindows" />
                            <MenuItem Header="Open network settings" Click="OnOpenNetworkSettings" />
                            <Separator />
                            <MenuItem Header="Quit" Click="OnQuit" />
                        </ContextMenu>
                    </Button.ContextMenu>
                </Button>
                <TextBlock x:Name="Header" FontSize="13" FontWeight="SemiBold" Style="{StaticResource Primary}" TextTrimming="CharacterEllipsis" />
            </DockPanel>
            <TextBlock x:Name="Note" Visibility="Collapsed" Margin="0,2,0,0" TextWrapping="Wrap" Foreground="{DynamicResource Warning}" />

            <Border Height="1" Margin="0,8" Background="{DynamicResource CardBorder}" />

            <Grid Background="Transparent" MouseLeftButtonDown="OnDrag">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="16" />
                    <ColumnDefinition Width="52" />
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>
                <Grid.RowDefinitions>
                    <RowDefinition Height="22" />
                    <RowDefinition Height="22" />
                </Grid.RowDefinitions>
                <Ellipse x:Name="PhoneDot" Width="9" Height="9" VerticalAlignment="Center" HorizontalAlignment="Left" />
                <TextBlock x:Name="PhoneLabel" Grid.Column="1" Style="{StaticResource Primary}" />
                <TextBlock x:Name="PhoneAddress" Grid.Column="2" Style="{StaticResource Secondary}" />
                <TextBlock x:Name="PhoneLatency" Grid.Column="3" Style="{StaticResource Secondary}" />
                <Ellipse x:Name="LanDot" Grid.Row="1" Width="9" Height="9" VerticalAlignment="Center" HorizontalAlignment="Left" />
                <TextBlock x:Name="LanLabel" Grid.Row="1" Grid.Column="1" Style="{StaticResource Primary}" />
                <TextBlock x:Name="LanAddress" Grid.Row="1" Grid.Column="2" Style="{StaticResource Secondary}" />
                <TextBlock x:Name="LanLatency" Grid.Row="1" Grid.Column="3" Style="{StaticResource Secondary}" />
            </Grid>

            <Border Height="1" Margin="0,8" Background="{DynamicResource CardBorder}" />

            <UniformGrid Columns="3">
                <ToggleButton x:Name="PhoneButton" Content="Phone" Tag="Phone" Style="{StaticResource ModeButton}" Click="OnMode" />
                <ToggleButton x:Name="LanButton" Content="LAN" Tag="Lan" Style="{StaticResource ModeButton}" Click="OnMode" />
                <ToggleButton x:Name="AutoButton" Content="Auto" Tag="Auto" Style="{StaticResource ModeButton}" Click="OnMode" />
            </UniformGrid>

            <TextBlock x:Name="AdminHint" Visibility="Collapsed" Margin="0,8,0,0" HorizontalAlignment="Center">
                <Hyperlink Click="OnRestartAsAdmin" Foreground="{DynamicResource Warning}">Read-only — restart as admin</Hyperlink>
            </TextBlock>
        </StackPanel>
    </Border>
</Window>
```

`src/NetRoute.App/CardWindow.xaml.cs`:

```csharp
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Shapes;
using Microsoft.Win32;
using NetRoute.Core;

namespace NetRoute.App;

public partial class CardWindow : Window
{
    RoutingMode _mode;
    bool _allowClose;

    public CardWindow()
    {
        InitializeComponent();
        Theme.Apply(Resources);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public event Action<RoutingMode>? ModeRequested;
    public event Action? HideRequested;
    public event Action<double, double>? Moved;
    public event Action? StartWithWindowsToggled;
    public event Action? OpenNetworkSettingsRequested;
    public event Action? RestartAsAdminRequested;
    public event Action? QuitRequested;

    /// Shows the card at its saved spot, or bottom-right when that spot is no longer on screen.
    public void ShowAndPlace(double? savedLeft, double? savedTop)
    {
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
            UpdateLayout();
            var virtualScreen = new Bounds(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var work = SystemParameters.WorkArea;
            (Left, Top) = CardPlacement.Resolve(savedLeft, savedTop, ActualWidth, ActualHeight,
                virtualScreen, new Bounds(work.Left, work.Top, work.Width, work.Height));
            Opacity = 1;
        }
        Activate();
    }

    public void Render(CardView view, RoutingMode mode, bool canModify, bool startWithWindows)
    {
        _mode = mode;
        Header.Text = "🌐 " + view.Header;
        Header.SetResourceReference(TextBlock.ForegroundProperty, view.HeaderWarning ? "Warning" : "TextPrimary");
        Note.Text = view.Note ?? "";
        Note.Visibility = view.Note is null ? Visibility.Collapsed : Visibility.Visible;
        RenderRow(view.Phone, PhoneDot, PhoneLabel, PhoneAddress, PhoneLatency);
        RenderRow(view.Lan, LanDot, LanLabel, LanAddress, LanLatency);
        SyncModeButtons();
        PhoneButton.IsEnabled = LanButton.IsEnabled = AutoButton.IsEnabled = canModify;
        AdminHint.Visibility = canModify ? Visibility.Collapsed : Visibility.Visible;
        StartWithWindowsItem.IsChecked = startWithWindows;
        StartWithWindowsItem.IsEnabled = canModify;
    }

    /// Closes for real on app exit; any other close (Alt+F4) just hides to the tray.
    public void ForceClose()
    {
        _allowClose = true;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            HideRequested?.Invoke();
        }
        base.OnClosing(e);
    }

    static void RenderRow(AdapterRow row, Ellipse dot, TextBlock label, TextBlock address, TextBlock latency)
    {
        dot.SetResourceReference(Shape.FillProperty, row.Dot switch
        {
            Dot.Green => "DotGreen",
            Dot.Amber => "DotAmber",
            _ => "DotGray",
        });
        label.Text = row.Label;
        label.FontWeight = row.IsActive ? FontWeights.Bold : FontWeights.Normal;
        address.Text = row.Address;
        latency.Text = row.Latency;
    }

    void SyncModeButtons()
    {
        PhoneButton.IsChecked = _mode == RoutingMode.Phone;
        LanButton.IsChecked = _mode == RoutingMode.Lan;
        AutoButton.IsChecked = _mode == RoutingMode.Auto;
    }

    void OnMode(object sender, RoutedEventArgs e)
    {
        var mode = Enum.Parse<RoutingMode>((string)((FrameworkElement)sender).Tag);
        SyncModeButtons(); // undo the click's own toggle; Render shows the new mode once applied
        ModeRequested?.Invoke(mode);
    }

    void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        DragMove();
        Moved?.Invoke(Left, Top);
    }

    void OnMenu(object sender, RoutedEventArgs e)
    {
        OptionsMenu.PlacementTarget = MenuButton;
        OptionsMenu.Placement = PlacementMode.Bottom;
        OptionsMenu.IsOpen = true;
    }

    void OnHide(object sender, RoutedEventArgs e) => HideRequested?.Invoke();

    void OnStartWithWindows(object sender, RoutedEventArgs e) => StartWithWindowsToggled?.Invoke();

    void OnOpenNetworkSettings(object sender, RoutedEventArgs e) => OpenNetworkSettingsRequested?.Invoke();

    void OnRestartAsAdmin(object sender, RoutedEventArgs e) => RestartAsAdminRequested?.Invoke();

    void OnQuit(object sender, RoutedEventArgs e) => QuitRequested?.Invoke();

    void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General) Dispatcher.InvokeAsync(() => Theme.Apply(Resources));
    }
}
```

`src/NetRoute.App/App.xaml.cs`:

```csharp
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
```

- [ ] **Step 6: Add to the solution and build**

```powershell
dotnet sln add src/NetRoute.App
dotnet build
```

Expected: `Build succeeded` with 0 errors. Fix any warnings in new code before continuing.

- [ ] **Step 7: Run all unit tests**

Run: `dotnet test --filter "Category!=Integration"`
Expected: PASS, 73 tests.

- [ ] **Step 8: Manual check — read-only mode (not elevated)**

Run from a normal, **non-admin** terminal: `dotnet run --project src/NetRoute.App`

Expected:
- A gray globe appears in the tray, and the card appears bottom-right.
- The card header shows which adapter carries internet right now. Both rows show an IP and a latency.
- The Phone/LAN/Auto buttons are dimmed, and "Read-only — restart as admin" is visible.
- Hovering the tray icon shows a tooltip like `Internet: Phone (38 ms) · LAN ready`.
- Left-clicking the tray icon hides and shows the card. Dragging the card moves it.
- Run `Get-NetIPInterface -AddressFamily IPv4 | ft ifIndex,InterfaceAlias,InterfaceMetric,AutomaticMetric`: nothing changed, all `AutomaticMetric Enabled`.

- [ ] **Step 9: Manual check — elevated, the first real metric writes**

Prerequisite: Task 7's integration tests passed.

Click "Read-only — restart as admin" and accept the UAC prompt.

Expected:
- The old instance exits and the new one starts. The tray globe turns green; the header reads `🌐 Internet via PHONE`.
- `Get-NetIPInterface -AddressFamily IPv4 | ft ifIndex,InterfaceAlias,InterfaceMetric,AutomaticMetric` shows the phone adapter at `5 Disabled` and `Ethernet` at `50 Disabled`.
- `Find-NetRoute -RemoteIPAddress 1.1.1.1 | select -First 1 InterfaceAlias` reports the phone adapter.
- `Test-Connection <printer-or-NAS-IP> -Count 2` still replies.
- Click **LAN**: the metrics flip to 5/50 the other way round, the globe turns blue, and `Find-NetRoute` reports `Ethernet`.
- Click **Auto**: `AutomaticMetric` shows `Enabled` again on both, and the globe turns gray.
- Click **Phone** again.
- If anything looks wrong, run the restore command from **Review Focus**.

- [ ] **Step 10: Commit**

```powershell
git add -A
git commit -m "feat(app): tray icon and floating card wired to the route controller" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`nClaude-Session: https://claude.ai/code/session_01AypeDHU2CKcJxywGQXDkT3"
```

---

### Task 10: Adapter picker

**Files:**
- Create: `src/NetRoute.App/AdapterPickerWindow.xaml`, `src/NetRoute.App/AdapterPickerWindow.xaml.cs`
- Modify: `src/NetRoute.App/CardWindow.xaml` (one menu item), `src/NetRoute.App/CardWindow.xaml.cs` (one event and one handler), `src/NetRoute.App/App.xaml.cs` (one subscription and one method)

**Interfaces:**
- Consumes: `AdapterDetector.Candidates`, `AdapterOverrides`, `AdapterInfo` (Task 1); `RouteController.SetOverridesAsync`, `RouteController.Settings` (Task 6); `WindowsAdapterSource` (Task 7); `CardWindow` and `App` (Task 9)
- Produces:
  - `AdapterPickerWindow(IReadOnlyList<AdapterInfo> candidates, AdapterOverrides current)` with `AdapterOverrides Result` (valid after `ShowDialog() == true`)
  - `CardWindow.ChooseAdaptersRequested` event

- [ ] **Step 1: Create the picker window**

`src/NetRoute.App/AdapterPickerWindow.xaml`:

```xml
<Window x:Class="NetRoute.App.AdapterPickerWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Choose adapters" Width="440" SizeToContent="Height"
        WindowStartupLocation="CenterScreen" ResizeMode="NoResize" Topmost="True"
        FontFamily="Segoe UI" FontSize="12">
    <StackPanel Margin="16">
        <TextBlock Text="Phone (carries internet in Phone mode)" FontWeight="SemiBold" />
        <ComboBox x:Name="PhoneBox" Margin="0,4,0,12" DisplayMemberPath="Label" />
        <TextBlock Text="LAN (printer, NAS, other PCs)" FontWeight="SemiBold" />
        <ComboBox x:Name="LanBox" Margin="0,4,0,12" DisplayMemberPath="Label" />
        <TextBlock TextWrapping="Wrap" Foreground="Gray"
                   Text="Automatic finds the phone by its USB-tethering driver and the LAN as the wired adapter that has a gateway." />
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,16,0,0">
            <Button Content="OK" Width="80" IsDefault="True" Click="OnOk" />
            <Button Content="Cancel" Width="80" Margin="8,0,0,0" IsCancel="True" />
        </StackPanel>
    </StackPanel>
</Window>
```

`src/NetRoute.App/AdapterPickerWindow.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using NetRoute.Core;

namespace NetRoute.App;

public partial class AdapterPickerWindow : Window
{
    /// Value is what gets stored: the description for the phone, the MAC for the LAN. Null = automatic.
    sealed record Choice(string Label, string? Value);

    public AdapterPickerWindow(IReadOnlyList<AdapterInfo> candidates, AdapterOverrides current)
    {
        InitializeComponent();
        Fill(PhoneBox, candidates.Select(a => new Choice(Label(a), a.Description)), current.PhoneDescription);
        Fill(LanBox, candidates.Select(a => new Choice(Label(a), a.Mac)), current.LanMac);
    }

    public AdapterOverrides Result { get; private set; } = AdapterOverrides.None;

    static string Label(AdapterInfo a) => $"{a.Name} — {a.Description}{(a.IsUp ? "" : " (disconnected)")}";

    /// Keeps a saved choice selectable even when that adapter is not plugged in right now.
    static void Fill(ComboBox box, IEnumerable<Choice> adapters, string? current)
    {
        var choices = new List<Choice> { new("Automatic", null) };
        choices.AddRange(adapters);
        var selected = choices.FirstOrDefault(c =>
            c.Value is not null && string.Equals(c.Value, current, StringComparison.OrdinalIgnoreCase));
        if (selected is null && current is not null)
        {
            selected = new Choice($"{current} (not connected)", current);
            choices.Add(selected);
        }
        box.ItemsSource = choices;
        box.SelectedItem = selected ?? choices[0];
    }

    void OnOk(object sender, RoutedEventArgs e)
    {
        Result = new AdapterOverrides(((Choice)PhoneBox.SelectedItem).Value, ((Choice)LanBox.SelectedItem).Value);
        DialogResult = true;
    }
}
```

- [ ] **Step 2: Add the menu item to the card**

In `src/NetRoute.App/CardWindow.xaml`, directly after the `StartWithWindowsItem` line, insert:

```xml
                            <MenuItem Header="Choose adapters…" Click="OnChooseAdapters" />
```

In `src/NetRoute.App/CardWindow.xaml.cs`, after `public event Action? StartWithWindowsToggled;`, add:

```csharp
    public event Action? ChooseAdaptersRequested;
```

and after the `OnStartWithWindows` handler, add:

```csharp
    void OnChooseAdapters(object sender, RoutedEventArgs e) => ChooseAdaptersRequested?.Invoke();
```

- [ ] **Step 3: Wire it in the app**

In `src/NetRoute.App/App.xaml.cs`, after `_card.OpenNetworkSettingsRequested += OpenNetworkSettings;`, add:

```csharp
        _card.ChooseAdaptersRequested += ChooseAdapters;
```

and after the `ToggleStartWithWindows` method, add:

```csharp
    async void ChooseAdapters()
    {
        var candidates = AdapterDetector.Candidates(new WindowsAdapterSource().GetAdapters()).ToList();
        var picker = new AdapterPickerWindow(candidates, _controller!.Settings.Overrides);
        if (picker.ShowDialog() == true) await _controller.SetOverridesAsync(picker.Result);
    }
```

- [ ] **Step 4: Build and run the unit tests**

```powershell
dotnet build
dotnet test --filter "Category!=Integration"
```

Expected: build succeeds; PASS, 73 tests.

- [ ] **Step 5: Manual check**

Run elevated: `dotnet run --project src/NetRoute.App`. Then go to ⋮ → **Choose adapters…**

Expected:
- Both boxes default to "Automatic".
- The lists show the Samsung phone, Realtek Ethernet and any Wi-Fi, but **no** VMware, vEthernet or NordLynx adapters.
- Pick the Realtek adapter for LAN and click OK: the card is unchanged and `%AppData%\NetRouteWidget\settings.json` now contains `LanOverrideMac`.
- Set both back to Automatic and click OK: `LanOverrideMac` is `null` again.

- [ ] **Step 6: Commit**

```powershell
git add -A
git commit -m "feat(app): adapter picker for manual phone/LAN choice" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`nClaude-Session: https://claude.ai/code/session_01AypeDHU2CKcJxywGQXDkT3"
```

---

### Task 11: Install, verify on the real machine, document, publish to GitHub

**Files:**
- Modify: `README.md`, `docs/superpowers/specs/2026-10-02-netroute-widget-v1-design.md` (status line only)

**Interfaces:**
- Consumes: the finished app (Tasks 1–10)
- Produces: an installed build at `F:\Apps\NetRouteWidget\NetRouteWidget.exe`, a verified checklist and an updated README

- [ ] **Step 1: Full test run**

```powershell
dotnet test --filter "Category!=Integration"
dotnet test --filter "Category=Integration"
```

Expected: PASS, 73 unit and 4 integration tests.

- [ ] **Step 2: Publish to a stable location**

The startup task points at the exe path, so it must not live in `bin/`.

```powershell
dotnet publish src/NetRoute.App -c Release -r win-x64 --self-contained false -o F:\Apps\NetRouteWidget
```

Expected: `F:\Apps\NetRouteWidget\NetRouteWidget.exe` exists.

- [ ] **Step 3: Spec verification checklist**

Quit any running dev instance from the tray. Start `F:\Apps\NetRouteWidget\NetRouteWidget.exe`, click "Restart as admin", then work through:

1. **Phone mode:** `Find-NetRoute -RemoteIPAddress 1.1.1.1 | select -First 1 InterfaceAlias` reports the phone adapter. The printer/NAS answers **by IP** (`Test-Connection <ip> -Count 2`) **and by name** (`Test-Connection <nas-name> -Count 2`, or open `\\<nas-name>`). If the name fails but the IP works, record it: that is the known DNS follow-up from the spec, not a v1 bug.
2. **Unplug the phone:** within about 2 s a toast says "Phone disconnected — internet via LAN", the tray shows the amber badge, the header reads "Internet via LAN (phone offline)", and browsing still works.
3. **Replug the phone:** a toast says "Phone back — internet via Phone". `Get-NetIPInterface -AddressFamily IPv4` shows the **new** phone adapter (new ifIndex) at metric `5 Disabled`.
4. **LAN, then Auto:** `Get-NetIPInterface` shows 50/5, then `AutomaticMetric Enabled` on both. Switch back to Phone.
5. **Start with Windows:** ⋮ → Start with Windows (checked). `schtasks /Query /TN NetRouteWidget /V /FO LIST` shows `Run Level: Highest` (field names vary by Windows language). Sign out and in, or reboot: the widget starts **without a UAC prompt**, in Phone mode, with the card where you left it.
6. **Log:** `%AppData%\NetRouteWidget\logs\netroute-<today>.log` contains the mode changes and the unplug/replug events.

Expected: all six pass. Note any failure with the log lines, and fix it before continuing.

- [ ] **Step 4: Update the README**

Replace `README.md` with:

````markdown
# NetRoute Widget

A small Windows tray widget for PCs connected to **two networks at once**, e.g. a wired LAN and a phone's USB tethering.

- Sends **internet traffic through the phone** (or the LAN, or lets Windows decide) with one click.
- Keeps **local devices (printer, NAS, other PCs) on the LAN** the whole time.
- Shows which connection is carrying internet right now, plus the latency through each one.
- When the preferred connection drops, internet **falls back to the other one automatically**, with a notification. It switches back when the connection returns.

## How it works

The widget only changes **interface metrics**: the preferred adapter gets `5`, the other `50`, and Auto restores Windows' automatic metrics. It never deletes routes, so the other adapter stays as a working fallback. LAN-subnet traffic always uses the LAN because Windows has a direct route for it.

## Install

```powershell
dotnet publish src/NetRoute.App -c Release -r win-x64 --self-contained false -o F:\Apps\NetRouteWidget
F:\Apps\NetRouteWidget\NetRouteWidget.exe
```

1. Changing routing needs administrator rights. Click **Read-only — restart as admin** on the card.
2. Then go to ⋮ → **Start with Windows**. This creates an elevated logon task, so later starts need no UAC prompt.

## Undo everything

```powershell
# Give every interface back to Windows' automatic metric
Get-NetIPInterface | Where-Object AutomaticMetric -eq Disabled | Set-NetIPInterface -AutomaticMetric Enabled
# Remove the startup task
schtasks /Delete /TN NetRouteWidget /F
# Remove settings and logs
Remove-Item -Recurse "$env:APPDATA\NetRouteWidget"
```

## Build and test

Requires the .NET 10 SDK; it is pinned in `global.json`.

```powershell
dotnet test --filter "Category!=Integration"   # unit tests
dotnet test --filter "Category=Integration"    # read-only checks against this machine
dotnet run --project src/NetRoute.App
```

## Roadmap

- **v1:** Phone / LAN / Auto switch for the internet path, local access preserved ✅
- **v2:** site/IP exceptions routed via the LAN
- **v3:** per-app routing

Design: [docs/superpowers/specs/2026-10-02-netroute-widget-v1-design.md](docs/superpowers/specs/2026-10-02-netroute-widget-v1-design.md)
````

- [ ] **Step 5: Mark the spec as implemented**

In `docs/superpowers/specs/2026-10-02-netroute-widget-v1-design.md`, change the `**Status:**` line to:

```markdown
**Status:** Implemented (v1)
```

- [ ] **Step 6: Commit and push**

```powershell
git add -A
git commit -m "docs: README install/undo instructions; mark v1 spec implemented" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`nClaude-Session: https://claude.ai/code/session_01AypeDHU2CKcJxywGQXDkT3"
git push
```

Expected: `main -> main` pushed to `https://github.com/petrofahed/NetRouteWidget`.
