# NetRoute v6.0: Wi-Fi as a routing mode — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship Phase 1 (v6.0) of the v6 design:
- Wi-Fi is a third, equal routing connection, with its own card row, mode button and tray item.
- The adapter dialog shows concrete choices plus a Detect button.
- Auto-heal falls back in the order LAN → Wi-Fi → Phone.
- Local-subnet traffic stays on the cable.
- Settings migrate safely.
- Smart routing pauses while Wi-Fi carries the internet.

**Architecture:**
- A new `ConnectionId { Lan, Wifi, Phone }` and a static `Connections` helper replace the phone/LAN pairs in the **Core routing path**: detection, metrics, status, controller, presenter, settings.
- Smart routing, usage and the path map keep reading `Phone` and `Lan` through compatibility accessors until their own phases (v6.1 to v6.3). Phase 1 only teaches Smart routing to pause on Wi-Fi.
- A route pin (lowering the LAN's on-link subnet route metric) keeps local traffic on Ethernet when Wi-Fi shares the LAN's subnet.

**Tech Stack:** .NET 10 (`net10.0-windows`), WPF and WinForms (tray), xUnit with hand-written fakes, iphlpapi P/Invoke.

**Spec:** `docs/superpowers/specs/2026-10-22-netroute-v6-wifi-connection-design.md`. Read the sections "Generic model", "Choose adapters", "Widget", "Routing modes and metrics" (including "Auto-heal" and "Local traffic stays on the cable"), "Settings and migration" and "Phases → v6.0".

## Global Constraints

- Branch: `feature/v6-wifi-connection`. Commit after every task.
- **Tests.**
  - Unit tests: `dotnet test tests/NetRoute.Core.Tests --filter "Category!=Integration"`.
  - The whole unit suite stays green at the end of every task.
  - Integration tests (`Category=Integration`) are run in Task 15 only.
- **Connection order.**
  - `Connections.FallbackOrder = [Lan, Wifi, Phone]` drives healing and backup metrics.
  - `Connections.CardOrder = [Phone, Lan, Wifi]` drives card rows, mode buttons and tray items.
  - Path map lanes are unchanged in v6.0.
- **Metrics.**
  - Main connection: `5`.
  - Backups in `FallbackOrder` (skipping the main): first `50`, second `60`.
  - Auto mode: every managed adapter goes back to automatic. No routes are ever deleted.
- **Healing.** Existing constants stay: `HealAfterFailingFor` = 60 s, `HealAfterFailedChecks` = 3, `RecoverAfterGoodChecks` = 3, `MaxRecoverAfterGoodChecks` = 48, `CalmChecksToResetBackoff` = 60.
  - **Stepping down** to the next answering connection is immediate.
  - **Stepping back up** to a higher-priority connection happens after **3** good checks in a row.
  - **Moves inside a heal** do not count towards the backoff or the sticky heal.
- **Connection-keyed code.**
  - No new positional `(phone, lan)` or `(lan, phone)` pairs.
  - No `== Lan ? … :` or `_ =>` arm that maps an unknown `ConnectionId` to a side.
  - A switch over `ConnectionId` is exhaustive and throws `ArgumentOutOfRangeException` otherwise.
- **Not used.** A slot set to Not used is stored as the string `"none"`.
  - Phone and LAN: nothing saved (`null`) means **detect**.
  - Wi-Fi: nothing saved (`null`) means **Not used**.
- **Settings.**
  - Existing JSON property names are unchanged.
  - The backup file is `settings.v5-backup.json`, next to `settings.json`, written once.
  - `AppSettings.CurrentVersion = 6`.
- **Display names.**
  - Card labels: "Phone", "LAN", and for Wi-Fi the network name (fallback "Wi-Fi").
  - Header path names: "PHONE", "LAN", "Wi-Fi".
  - Tray colours: Phone green, LAN blue, Wi-Fi purple `#8B5CF6`, Auto grey.
- **Copy (exact strings).**
  - Smart pause: `"Smart routing paused while on Wi-Fi"`.
  - Dialog hint: `"Detect fills each box with what it finds: the phone by its USB-tethering driver, the LAN as the cable with a gateway, Wi-Fi as the wireless adapter. You can still change any of them."`.
- **Out of v6.0** (later phases):
  - Smart routing over Wi-Fi.
  - Per-connection usage or speed test.
  - The Wi-Fi path-map lane.
  - The network picker (WlanApi).
  - In the card, the Wi-Fi row's live speed and today's total show `—` whenever the other rows show figures.

## Review Focus

1. **The Wi-Fi USB stick is unplugged while Wi-Fi is the main connection.**
   - Expected: the row reads "not connected", internet heals to the next answering connection in order, the route pin is removed, nothing throws.
   - Test in Task 7: `Wifi_main_unplugged_heals_to_lan_then_phone`.
   - Test in Task 8: `Pin_is_removed_when_wifi_disappears`.
2. **LAN and Wi-Fi share a subnet but the LAN is preferred** (Phone or LAN mode).
   - Expected: no pin, because the LAN already wins local traffic.
   - Test in Task 8: `No_pin_when_lan_already_preferred_over_wifi`.
3. **The same adapter is picked for two slots.**
   - Expected: detection never assigns one adapter twice, and the dialog refuses OK.
   - Test in Task 4: `One_adapter_never_fills_two_slots`.
4. **A v5.3 file whose wireless `LanOverrideMac` adapter is unplugged at upgrade.**
   - Expected: migration waits (the version is not bumped) instead of guessing.
   - Test in Task 9: `Migration_waits_when_the_lan_override_adapter_is_absent`.
5. **Wi-Fi is up but has no IPv4 or no gateway yet** (DHCP pending).
   - Expected: it is not probed, not a carrier and not a heal target.
   - Test in Task 6: `Carrier_skips_adapter_without_gateway_or_ipv4`.
   - Test in Task 7: `Heal_skips_wifi_without_ipv4`.

---

## File structure

**Core, new files:**
- `src/NetRoute.Core/Connections.cs`: `ConnectionId`, `Connections` (orders, labels, mode and path mapping, `IsMetered`).
- `src/NetRoute.Core/RoutePin.cs`: decides and applies the local-subnet pin.
- `src/NetRoute.Core/SettingsMigration.cs`: v5 → v6 migration.
- `src/NetRoute.Core/Windows/WindowsSubnetRoutes.cs`: `ISubnetRoutes` over `GetIpForwardEntry2`/`SetIpForwardEntry2`.
- `src/NetRoute.Core/Windows/WindowsNetworkNames.cs`: `INetworkNameSource` over Network List Manager COM.

**Core, modified:** `Models.cs`, `Abstractions.cs`, `AdapterDetector.cs`, `MetricPolicy.cs`, `RoutingEngine.cs`, `NetworkStatus.cs`, `RouteController.cs`, `StatusPresenter.cs`, `Settings.cs`, `SmartRouting/SmartRoutingSettings.cs`, `SmartRouting/SmartRoutingController.cs`, `SmartRouting/SmartRoutingPresenter.cs`, `Windows/WindowsAdapterSource.cs`, `Windows/IpHelperNative.cs`.

**App, modified:** `CardWindow.xaml(.cs)`, `TrayIcon.cs`, `AdapterPickerWindow.xaml(.cs)`, `App.xaml.cs`, `NetRoute.App.csproj` (version).

**Tools:** `tools/Restore-Network.cmd` (adds the pin reset).

**Tests:**
- New: `ConnectionsTests.cs`, `RoutePinTests.cs`, `SettingsMigrationTests.cs`.
- Extended: `AdapterDetectorTests`, `RoutingEngineTests`, `RouteControllerTests`, `StatusPresenterTests`, `SettingsStoreTests`, `SmartRoutingControllerTests`, `RestoreScriptTests`.
- Helpers: `TestAdapters.cs`, `Fakes.cs`.

**Docs:** `docs/superpowers/spikes/2026-10-22-v6-route-pin.md` (Task 1), `README.md` (Task 15).

---

### Task 1: Route-pin spike on the user's machine (gate)

This is not product code. It decides the pin mechanism used in Task 8. It needs an **administrator** PowerShell on the user's PC, with Ethernet and "Wi-Fi 2" both joined to pzhome (192.168.86.0/24). Every change is in `ActiveStore` and is reverted at the end.

**Files:** Create `docs/superpowers/spikes/2026-10-22-v6-route-pin.md`.

- [ ] **Step 1: Record the starting state**

Run:
```powershell
Get-NetIPInterface -AddressFamily IPv4 -InterfaceAlias 'Ethernet','Wi-Fi 2' | ft InterfaceAlias,AutomaticMetric,InterfaceMetric
Get-NetRoute -AddressFamily IPv4 -DestinationPrefix 192.168.86.0/24 | ft InterfaceAlias,RouteMetric,PolicyStore
Find-NetRoute -RemoteIPAddress 192.168.86.1 | select -Last 1 InterfaceAlias
```
Expected: both interfaces have an on-link `192.168.86.0/24` route, normally with RouteMetric 256.

- [ ] **Step 2: Simulate Wi-Fi mode and show the problem**

Run:
```powershell
Set-NetIPInterface -InterfaceAlias 'Wi-Fi 2' -AddressFamily IPv4 -InterfaceMetric 5
Set-NetIPInterface -InterfaceAlias 'Ethernet' -AddressFamily IPv4 -InterfaceMetric 50
Find-NetRoute -RemoteIPAddress 192.168.86.1 | select -Last 1 InterfaceAlias
Find-NetRoute -RemoteIPAddress 1.1.1.1 | select -Last 1 InterfaceAlias
```
Expected: both lines print `Wi-Fi 2`. That is the bug.

- [ ] **Step 3: Apply mechanism A (lower the LAN's on-link route metric)**

Run:
```powershell
Set-NetRoute -InterfaceAlias 'Ethernet' -DestinationPrefix 192.168.86.0/24 -PolicyStore ActiveStore -RouteMetric 0
Find-NetRoute -RemoteIPAddress 192.168.86.1 | select -Last 1 InterfaceAlias   # want: Ethernet
Find-NetRoute -RemoteIPAddress 1.1.1.1 | select -Last 1 InterfaceAlias        # want: Wi-Fi 2
ping -n 4 192.168.86.1
```
Expected: Ethernet, then Wi-Fi 2, then replies. Also check `Get-NetAdapterStatistics -Name Ethernet` before and after a `ping -n 50 <a camera IP>`: the Ethernet SentUnicastPackets count rises.

- [ ] **Step 4: Repeat Step 3's checks with Smart routing running**

Start NetRoute with Smart on, then repeat the `Find-NetRoute` and ping checks.
Expected: the same result, because sing-box excludes 192.168.0.0/16.

- [ ] **Step 5: Check that the pin survives a reconnect**

Run `ipconfig /renew Ethernet`, then `Get-NetRoute -InterfaceAlias Ethernet -DestinationPrefix 192.168.86.0/24 | ft RouteMetric`.
Record whether the metric went back to 256. Either result is fine, because Task 8 re-applies the pin on every refresh, but record it.

- [ ] **Step 6: Revert**

Run:
```powershell
Set-NetRoute -InterfaceAlias 'Ethernet' -DestinationPrefix 192.168.86.0/24 -PolicyStore ActiveStore -RouteMetric 256
Set-NetIPInterface -InterfaceAlias 'Wi-Fi 2','Ethernet' -AddressFamily IPv4 -AutomaticMetric Enabled
```
Then let NetRoute re-apply its own metrics (click its current mode).

- [ ] **Step 7: Write the spike note and decide**

Write the results of Steps 1–5 to the spike file.
- **Mechanism A works** (Ethernet for 192.168.86.x and Wi-Fi 2 for 1.1.1.1, with and without sing-box): write "Decision: A, PinnedMetric 0, DefaultMetric 256" and continue.
- **It does not work:** stop the plan here and report to the user. The spec requires this.

- [ ] **Step 8: Commit**

```bash
git add docs/superpowers/spikes/2026-10-22-v6-route-pin.md
git commit -m "docs(v6): route-pin spike results"
```

---

### Task 2: `ConnectionId` and `Connections`

**Files:**
- Create: `src/NetRoute.Core/Connections.cs`
- Modify: `src/NetRoute.Core/Models.cs` (`RoutingMode`, `InternetPath`), `src/NetRoute.Core/StatusPresenter.cs:5` (`TrayColor`)
- Test: `tests/NetRoute.Core.Tests/ConnectionsTests.cs`

**Interfaces, produced:**
- `public enum ConnectionId { Lan, Wifi, Phone }`
- `RoutingMode { Phone, Lan, Auto, Wifi }`. The value is **appended** so stored ordinals and existing tests don't move.
- `InternetPath { None, Phone, Lan, Other, Wifi }`, also appended.
- `TrayColor { Green, Blue, Gray, Purple }`
- `public static class Connections`:
  - `IReadOnlyList<ConnectionId> All`, `FallbackOrder` (`[Lan, Wifi, Phone]`), `CardOrder` (`[Phone, Lan, Wifi]`)
  - `bool IsMetered(ConnectionId id)`: true only for Phone
  - `ConnectionId? Of(RoutingMode mode)`: Auto gives null
  - `RoutingMode ModeOf(ConnectionId id)`
  - `InternetPath PathOf(ConnectionId id)`
  - `ConnectionId? Of(InternetPath path)`: None and Other give null
  - `string Label(ConnectionId id)`: "Phone", "LAN", "Wi-Fi"
  - `string HeaderName(ConnectionId id)`: "PHONE", "LAN", "Wi-Fi"

- [ ] **Step 1: Write the failing tests**

```csharp
public class ConnectionsTests
{
    [Fact]
    public void Orders_are_fixed()
    {
        Assert.Equal([ConnectionId.Lan, ConnectionId.Wifi, ConnectionId.Phone], Connections.FallbackOrder);
        Assert.Equal([ConnectionId.Phone, ConnectionId.Lan, ConnectionId.Wifi], Connections.CardOrder);
    }

    [Theory, MemberData(nameof(Every))]
    public void Every_connection_round_trips_through_mode_and_path(ConnectionId id)
    {
        Assert.Equal(id, Connections.Of(Connections.ModeOf(id)));
        Assert.Equal(id, Connections.Of(Connections.PathOf(id)));
    }

    [Fact]
    public void Only_the_phone_is_metered() =>
        Assert.Equal([ConnectionId.Phone], Connections.All.Where(Connections.IsMetered));

    [Fact]
    public void Auto_none_and_other_map_to_no_connection()
    {
        Assert.Null(Connections.Of(RoutingMode.Auto));
        Assert.Null(Connections.Of(InternetPath.None));
        Assert.Null(Connections.Of(InternetPath.Other));
    }

    [Fact]
    public void Labels() =>
        Assert.Equal(["Phone", "LAN", "Wi-Fi"], Connections.CardOrder.Select(Connections.Label));

    public static TheoryData<ConnectionId> Every => new(Enum.GetValues<ConnectionId>());
}
```

- [ ] **Step 2: Run the tests and confirm they fail**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~ConnectionsTests"`
Expected: a build error (`ConnectionId` is not defined).

- [ ] **Step 3: Implement `Connections.cs` and the three enum additions**

Every mapping is an exhaustive `switch` that throws `ArgumentOutOfRangeException` on any other value.

- [ ] **Step 4: Run the tests and confirm they pass**

Run the ConnectionsTests filter, then the full unit suite. Expected: PASS. The existing suite is unaffected.

- [ ] **Step 5: Commit**

```bash
git add src/NetRoute.Core/Connections.cs src/NetRoute.Core/Models.cs src/NetRoute.Core/StatusPresenter.cs tests/NetRoute.Core.Tests/ConnectionsTests.cs
git commit -m "feat(v6): ConnectionId and Connections helper"
```

---

### Task 3: More facts per adapter (`AdapterInfo`)

**Files:**
- Modify: `src/NetRoute.Core/Models.cs:10-19`, `src/NetRoute.Core/Windows/WindowsAdapterSource.cs`
- Modify: `tests/NetRoute.Core.Tests/TestAdapters.cs`
- Test: `tests/NetRoute.Core.Tests/WindowsIntegrationTests.cs` (existing integration file)

**Interfaces, produced:** `AdapterInfo` gains trailing optional parameters, so every existing call compiles unchanged:
- `string? Id = null`: `NetworkInterface.Id`, the adapter GUID in braces.
- `int? PrefixLength = null`: the IPv4 prefix length of the first IPv4 unicast address.
- `string? Gateway = null`: the first IPv4 gateway.
- `long? SpeedBps = null`: `NetworkInterface.Speed` when it is > 0.

Also: `static string? AdapterInfo.SubnetOf(string ipv4, int prefixLength)`, which returns the CIDR text, for example `"192.168.86.0/24"`.

**Test fixtures:**
- `TestAdapters.Wifi(int index = 18, string ipv4 = "192.168.1.20", string mac = "AA-BB-CC-00-00-18", bool hasGateway = true)` sets `PrefixLength: 24, Gateway: "192.168.1.1"`.
- New `TestAdapters.WifiOnLan()` is `Wifi(ipv4: "192.168.86.35")` with `Gateway: "192.168.86.1"`. This is the user's pzhome case.
- `TestAdapters.Lan` gains `PrefixLength: 24, Gateway: "192.168.86.1"`.

- [ ] **Step 1: Write the failing test** in `ConnectionsTests` (pure, no Windows):

```csharp
[Theory]
[InlineData("192.168.86.42", 24, "192.168.86.0/24")]
[InlineData("10.5.0.2", 8, "10.0.0.0/8")]
[InlineData("172.19.0.1", 30, "172.19.0.0/30")]
public void Subnet_of_an_address(string ip, int prefix, string expected) =>
    Assert.Equal(expected, AdapterInfo.SubnetOf(ip, prefix));
```

- [ ] **Step 2: Run the test and confirm it fails** (build error).

- [ ] **Step 3: Implement.** Add the four parameters and `SubnetOf`. Fill them in `WindowsAdapterSource.GetAdapters()` from `nic.Id`, `UnicastIPAddressInformation.PrefixLength`, the gateway addresses and `nic.Speed`. Update the fixtures.

- [ ] **Step 4: Add an integration assertion** to the existing adapter-read integration test in `WindowsIntegrationTests.cs`: every adapter that is up with an IPv4 address has a non-null `PrefixLength` and `Id`.

- [ ] **Step 5: Run the unit suite.** Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git commit -am "feat(v6): adapter id, prefix, gateway and speed"
```

---

### Task 4: Detection over three slots

**Files:**
- Modify: `src/NetRoute.Core/AdapterDetector.cs`
- Test: `tests/NetRoute.Core.Tests/AdapterDetectorTests.cs`

**Interfaces.**
- Consumes: `ConnectionId`, `Connections` (Task 2).
- Produces:
  - `DetectionIssue { None, NotFound, Ambiguous, NotUsed }`.
  - `record AdapterOverrides(string? PhoneDescription, string? LanMac, string? WifiMac = null)`, with `public const string NotUsed = "none"`.
  - `DetectionResult` holds one `(AdapterInfo? Adapter, DetectionIssue Issue)` per `ConnectionId`:
    - `AdapterInfo? Adapter(ConnectionId id)`
    - `DetectionIssue Issue(ConnectionId id)`
    - `IEnumerable<(ConnectionId Id, AdapterInfo Adapter)> Present`: in `Connections.All` order, only non-null adapters.
    - `bool IsUsed(ConnectionId id)`: `Issue(id) != NotUsed`.
    - Compatibility accessors `Phone`, `PhoneIssue`, `Lan`, `LanIssue` (expression-bodied over the above), so Smart routing, usage and the path map compile unchanged.
    - `Wifi` and `WifiIssue` are added.
  - Constructor: `DetectionResult(IReadOnlyDictionary<ConnectionId, (AdapterInfo?, DetectionIssue)> slots)`.
  - Factory for tests and old callers: `static DetectionResult Of(AdapterInfo? phone, DetectionIssue phoneIssue, AdapterInfo? lan, DetectionIssue lanIssue, AdapterInfo? wifi = null, DetectionIssue wifiIssue = DetectionIssue.NotUsed)`.
  - `Empty`: every slot NotFound, except Wi-Fi, which is NotUsed.
  - `AdapterDetector.Detect(adapters, overrides)`: three slots.
    - **Wi-Fi:** `WifiMac` null or `"none"` gives NotUsed. A MAC matches an up, non-virtual adapter, otherwise NotFound.
    - **Phone/LAN:** `"none"` gives NotUsed. Otherwise today's rules apply.
    - **One adapter fills at most one slot.** Precedence: Phone, then Wi-Fi (explicit MAC), then LAN.
  - `AdapterDetector.Suggest(IReadOnlyList<AdapterInfo> adapters) → AdapterOverrides`: what Detect would pick, as concrete values.
    - Phone: the description of the single tethering adapter.
    - LAN: the MAC of the single wired adapter with a gateway.
    - Wi-Fi: the MAC of the single non-virtual wireless adapter, up or down.
    - Any slot with zero or several candidates gives `"none"`.

- [ ] **Step 1: Update the 7 `new DetectionResult(` call sites in tests** to `DetectionResult.Of(...)`, keeping the same arguments. Do the same for the single call site in `AdapterDetector.cs`.

- [ ] **Step 2: Write the failing tests**

```csharp
[Fact]
public void Wifi_is_not_used_until_chosen()
{
    var result = AdapterDetector.Detect([TestAdapters.Phone(), TestAdapters.Lan(), TestAdapters.Wifi()], AdapterOverrides.None);
    Assert.Null(result.Wifi);
    Assert.Equal(DetectionIssue.NotUsed, result.WifiIssue);
    Assert.False(result.IsUsed(ConnectionId.Wifi));
}

[Fact]
public void Wifi_override_detects_wifi_as_wifi()
{
    var result = AdapterDetector.Detect([TestAdapters.Phone(), TestAdapters.Lan(), TestAdapters.Wifi()],
        new AdapterOverrides(null, null, "AA-BB-CC-00-00-18"));
    Assert.Equal(18, result.Wifi?.Index);
    Assert.Equal(10, result.Lan?.Index);
}

[Fact]
public void Not_used_phone_and_lan()
{
    var result = AdapterDetector.Detect([TestAdapters.Phone(), TestAdapters.Lan()],
        new AdapterOverrides(AdapterOverrides.NotUsed, AdapterOverrides.NotUsed));
    Assert.Equal(DetectionIssue.NotUsed, result.PhoneIssue);
    Assert.Equal(DetectionIssue.NotUsed, result.LanIssue);
    Assert.Empty(result.Present);
}

[Fact]
public void One_adapter_never_fills_two_slots()
{
    var wifi = TestAdapters.Wifi();
    var result = AdapterDetector.Detect([TestAdapters.Phone(), wifi], new AdapterOverrides(null, wifi.Mac, wifi.Mac));
    Assert.Equal(18, result.Wifi?.Index);
    Assert.Null(result.Lan);
}

[Fact]
public void Suggest_fills_every_slot_it_can()
{
    var s = AdapterDetector.Suggest([TestAdapters.Phone(), TestAdapters.Lan(), TestAdapters.Wifi(), TestAdapters.VmNet(29)]);
    Assert.Equal("SAMSUNG Mobile USB Remote NDIS Network Device", s.PhoneDescription);
    Assert.Equal("AA-BB-CC-00-00-10", s.LanMac);
    Assert.Equal("AA-BB-CC-00-00-18", s.WifiMac);
}

[Fact]
public void Suggest_marks_missing_or_ambiguous_slots_not_used()
{
    var s = AdapterDetector.Suggest([TestAdapters.Lan(), TestAdapters.Wifi(), TestAdapters.Wifi(index: 19, mac: "AA-BB-CC-00-00-19")]);
    Assert.Equal(AdapterOverrides.NotUsed, s.PhoneDescription);
    Assert.Equal(AdapterOverrides.NotUsed, s.WifiMac);
}
```

Also **replace** the existing test `Wifi_is_not_auto_detected_as_lan_but_can_be_chosen_by_override` (line 83) with `Wifi_is_never_auto_detected_as_lan`: no overrides, so `result.Lan` is the Ethernet adapter and `result.Wifi` is null.

- [ ] **Step 3: Run the tests and confirm they fail.** Run the `AdapterDetectorTests` filter. Expected: FAIL or a build error.

- [ ] **Step 4: Implement** the types and the detector as specified above.

- [ ] **Step 5: Run the full unit suite.** Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git commit -am "feat(v6): detection over Phone, LAN and Wi-Fi slots with Not used"
```

---

### Task 5: Graded metrics over every present adapter

**Files:**
- Modify: `src/NetRoute.Core/MetricPolicy.cs`, `src/NetRoute.Core/RoutingEngine.cs`
- Test: `tests/NetRoute.Core.Tests/RoutingEngineTests.cs`

**Interfaces.**
- Consumes: `DetectionResult.Present` (Task 4).
- Produces:
  - `MetricPolicy`: `const uint Preferred = 5, Backup = 50, SecondBackup = 60`.
  - `static IReadOnlyDictionary<ConnectionId, uint?> For(RoutingMode mode, ConnectionId? carrier = null)`. The main is `carrier ?? Connections.Of(mode)`. The main gets 5, then the others in `FallbackOrder` (skipping the main) get 50 and 60. Auto with no carrier gives null for every connection. `MetricTarget` is removed.
  - `RoutingEngine.Apply(RoutingMode mode, DetectionResult adapters, ConnectionId? carrier = null)`, `IsInSync(mode, adapters, carrier = null)`, `Reset(AdapterInfo)` (unchanged). Targets are `adapters.Present` mapped through `For`.

- [ ] **Step 1: Write the failing tests** (the existing Phone and LAN tests stay as they are)

```csharp
static DetectionResult All3(AdapterInfo? wifi = null) => DetectionResult.Of(
    TestAdapters.Phone(), DetectionIssue.None, TestAdapters.Lan(), DetectionIssue.None, wifi ?? TestAdapters.Wifi(), DetectionIssue.None);

[Theory]
[InlineData(RoutingMode.Phone, 5u, 50u, 60u)]
[InlineData(RoutingMode.Lan, 60u, 5u, 50u)]
[InlineData(RoutingMode.Wifi, 60u, 50u, 5u)]
public void Every_mode_grades_the_backups_in_fallback_order(RoutingMode mode, uint phone, uint lan, uint wifi)
{
    var metrics = MetricsFor(31, 10, 18);
    new RoutingEngine(metrics).Apply(mode, All3());
    Assert.Equal(new InterfaceMetricState(false, phone), metrics.Get(31, IpFamily.IPv4));
    Assert.Equal(new InterfaceMetricState(false, lan), metrics.Get(10, IpFamily.IPv4));
    Assert.Equal(new InterfaceMetricState(false, wifi), metrics.Get(18, IpFamily.IPv4));
}

[Fact]
public void Carrier_overrides_the_mode_while_healing()
{
    var metrics = MetricsFor(31, 10, 18);
    new RoutingEngine(metrics).Apply(RoutingMode.Lan, All3(), carrier: ConnectionId.Wifi);
    Assert.Equal(5u, metrics.Get(18, IpFamily.IPv4)!.Metric);
    Assert.Equal(50u, metrics.Get(10, IpFamily.IPv4)!.Metric);
    Assert.Equal(60u, metrics.Get(31, IpFamily.IPv4)!.Metric);
}

[Fact]
public void Auto_returns_all_three_to_automatic()
{
    var metrics = MetricsFor(31, 10, 18);
    new RoutingEngine(metrics).Apply(RoutingMode.Auto, All3());
    Assert.All(new[] { 31, 10, 18 }, i => Assert.True(metrics.Get(i, IpFamily.IPv4)!.UseAutomatic));
}

[Fact]
public void Unused_wifi_is_never_touched()
{
    var metrics = MetricsFor(31, 10, 18);
    new RoutingEngine(metrics).Apply(RoutingMode.Phone, DetectionResult.Of(TestAdapters.Phone(), DetectionIssue.None, TestAdapters.Lan(), DetectionIssue.None));
    Assert.DoesNotContain(metrics.SetCalls, c => c.Index == 18);
}
```

- [ ] **Step 2: Run the tests and confirm they fail.** Run the `RoutingEngineTests` filter.

- [ ] **Step 3: Implement** `MetricPolicy.For` and the `RoutingEngine` targets. Update the existing `Phone_mode_prefers_phone…` and `Lan_mode_prefers_lan` helper `Both()` to use `DetectionResult.Of`.

- [ ] **Step 4: Run the full unit suite.** Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git commit -am "feat(v6): graded metrics over every chosen connection"
```

---

### Task 6: Generic `NetworkStatus`

**Files:**
- Modify: `src/NetRoute.Core/NetworkStatus.cs`
- Test: `tests/NetRoute.Core.Tests/StatusPresenterTests.cs` (the status helpers live there), plus new tests in `ConnectionsTests.cs`

**Interfaces.**
- Consumes: Tasks 2 and 4.
- Produces, on `NetworkStatus`:
  - `IReadOnlyDictionary<ConnectionId, int?> LatencyMs` replaces the `PhoneLatencyMs` and `LanLatencyMs` positional parameters.
  - Get-only compatibility properties `PhoneLatencyMs`, `LanLatencyMs` and `WifiLatencyMs` read from `LatencyMs`.
  - `NetworkStatus WithLatency(ConnectionId id, int? ms)` for tests and callers that used `with { LanLatencyMs = … }`.
  - New init properties: `ConnectionId? HealTarget` (null when not healing), `ConnectionId? Carrier`, `string? WifiNetwork`.
  - `static InternetPath ResolvePath(int? bestIndex, DetectionResult adapters)`: loops over `adapters.Present` and returns `Connections.PathOf(id)`.
  - `bool IsFallback`: `Connections.Of(Mode) is { } main && Connections.Of(ActivePath) is { } path && path != main`.
  - `static ConnectionId? CarrierFor(RoutingMode mode, ConnectionId? healTarget, DetectionResult adapters)`: the first connection, in the order main (`healTarget ?? Of(mode)`) then the rest of `FallbackOrder`, whose adapter is present with `HasGateway` and a non-null `IPv4`. Auto gives null.

- [ ] **Step 1: Migrate callers.**
  - Replace every `with { PhoneLatencyMs = x }` and `with { LanLatencyMs = x }` with `.WithLatency(ConnectionId.Phone/Lan, x)`.
  - The usage count was 3 in tests and 13 in src. Find them with: `grep -rn "LatencyMs =" src tests`.

- [ ] **Step 2: Write the failing tests**

```csharp
[Fact]
public void Wifi_path_resolves_to_wifi()
{
    var adapters = DetectionResult.Of(TestAdapters.Phone(), DetectionIssue.None, TestAdapters.Lan(), DetectionIssue.None, TestAdapters.Wifi(), DetectionIssue.None);
    Assert.Equal(InternetPath.Wifi, NetworkStatus.ResolvePath(18, adapters));
}

[Theory]
[InlineData(RoutingMode.Wifi, InternetPath.Lan, true)]
[InlineData(RoutingMode.Wifi, InternetPath.Wifi, false)]
[InlineData(RoutingMode.Lan, InternetPath.Wifi, true)]
[InlineData(RoutingMode.Auto, InternetPath.Wifi, false)]
public void Fallback_is_any_path_other_than_the_main(RoutingMode mode, InternetPath path, bool expected) =>
    Assert.Equal(expected, (NetworkStatus.Initial(mode, true) with { ActivePath = path }).IsFallback);

[Fact]
public void Carrier_is_the_main_when_present()
{
    var all = DetectionResult.Of(TestAdapters.Phone(), DetectionIssue.None, TestAdapters.Lan(), DetectionIssue.None, TestAdapters.Wifi(), DetectionIssue.None);
    Assert.Equal(ConnectionId.Wifi, NetworkStatus.CarrierFor(RoutingMode.Wifi, null, all));
    Assert.Equal(ConnectionId.Phone, NetworkStatus.CarrierFor(RoutingMode.Lan, ConnectionId.Phone, all));
}

[Fact]
public void Carrier_falls_to_wifi_when_the_lan_cable_is_out()
{
    var noLan = DetectionResult.Of(TestAdapters.Phone(), DetectionIssue.None, null, DetectionIssue.NotFound, TestAdapters.Wifi(), DetectionIssue.None);
    Assert.Equal(ConnectionId.Wifi, NetworkStatus.CarrierFor(RoutingMode.Lan, null, noLan));
}

[Fact]
public void Carrier_skips_adapter_without_gateway_or_ipv4()
{
    var pending = TestAdapters.Wifi() with { IPv4 = null };
    var adapters = DetectionResult.Of(TestAdapters.Phone(), DetectionIssue.None, null, DetectionIssue.NotFound, pending, DetectionIssue.None);
    Assert.Equal(ConnectionId.Phone, NetworkStatus.CarrierFor(RoutingMode.Wifi, null, adapters));
}
```

- [ ] **Step 3: Run the tests and confirm they fail.**

- [ ] **Step 4: Implement.** Keep `Initial(mode, canModify)` with an empty `LatencyMs`.

- [ ] **Step 5: Run the full unit suite.** Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git commit -am "feat(v6): per-connection latency, carrier and generic path in NetworkStatus"
```

---

### Task 7: `RouteController` probes all connections and heals in order

**Files:**
- Modify: `src/NetRoute.Core/RouteController.cs`
- Test: `tests/NetRoute.Core.Tests/RouteControllerTests.cs`

**Interfaces.**
- Consumes: Tasks 4–6.
- Produces:
  - `RefreshAsync` probes every `adapters.Present` adapter with an IPv4, in parallel. "no reply" smoothing (`NoReplyAfterMissedChecks`) is per connection, through a `Dictionary<ConnectionId, int>` miss count.
  - `Status.HealTarget`, `Status.Carrier = NetworkStatus.CarrierFor(mode, _healTarget, detection)`, and `Apply`/`IsInSync` called with `carrier: _healTarget`.
  - `EffectiveMode` is deleted. The heal target replaces it.
  - `SetOverridesAsync` resets every previously present adapter that is not present in the new detection, across all three slots.
  - `StatusPresenter.StickyHealToast(RoutingMode mode, ConnectionId target)`: the signature change lands here. Task 10 fills in the generic text.

**Healing algorithm.** It replaces `UpdateHealing`. Keep the existing backoff and sticky fields and their meaning.

```
main = Connections.Of(mode); none (Auto) or !canModify or !AutoHeal → ResetHealing, return
answering(c) = c present && c.IPv4 != null && measured latency[c] != null
candidates = FallbackOrder without main, filtered to present adapters with IPv4
if not healing:
    count a failure when !answering(main) && candidates.Any(answering)   // same 60 s + 3-check window as today
    on threshold: _healTarget = first answering candidate; apply today's backoff/sticky rules; healing = true
else:
    if !answering(_healTarget): next = first answering candidate after… any candidate ≠ target in FallbackOrder
        next exists → _healTarget = next (immediate, no backoff change)
        none → EndHeal (restore main)
    else if a candidate earlier in FallbackOrder than _healTarget has answered 3 checks in a row → _healTarget = it (step up)
    recovery to main: unchanged (good checks of main ≥ _recoverThreshold → EndHeal), not while sticky
```

When there is more than one higher-priority candidate, the step-up counter is kept per candidate.

- [ ] **Step 1: Migrate the existing tests.**
  - The constructor fixture is unchanged.
  - Assertions on `PhoneLatencyMs` and `LanLatencyMs` keep working through the compatibility properties.
  - The existing two-connection heal tests must pass **unchanged**, because Phone ↔ LAN is the same behaviour with Wi-Fi not used. Run them first to be sure.

- [ ] **Step 2: Write the failing tests.**
  - Fixture addition: `_adapters.Adapters.Add(TestAdapters.Wifi()); _metrics.Add(18); _probe.BySource["192.168.1.20"] = 20;`.
  - Settings: `new AppSettings { Mode = RoutingMode.Lan, WifiOverrideMac = "AA-BB-CC-00-00-18" }`. The `WifiOverrideMac` property is added in Task 9. Add the property itself now and leave its migration to Task 9.

```csharp
[Fact] public async Task Lan_failing_heals_to_wifi_not_phone()            // LAN probe null for HealChecks → Status.HealTarget == Wifi, metrics Wifi 5 / LAN 50 / Phone 60, toast names Wi-Fi
[Fact] public async Task Wifi_target_dying_steps_down_to_phone_at_once()   // healed on Wi-Fi, Wi-Fi probe null for 1 check → HealTarget == Phone on that same refresh
[Fact] public async Task Wifi_back_steps_up_after_three_good_checks()     // healed on Phone (Wi-Fi was dead), Wi-Fi answers 2 checks → still Phone; 3rd → Wifi
[Fact] public async Task Step_moves_do_not_grow_the_backoff()             // after a step down and a step up, RecoverThreshold == RecoverAfterGoodChecks
[Fact] public async Task No_candidate_answering_means_no_heal()           // LAN, Wi-Fi and Phone all null → never healing
[Fact] public async Task Heal_skips_wifi_without_ipv4()                   // Wi-Fi adapter IPv4 null → LAN failing heals to Phone
[Fact] public async Task Wifi_main_unplugged_heals_to_lan_then_phone()     // Mode=Wifi; remove Wi-Fi adapter → Carrier == Lan (metric fallback, no heal needed); then LAN probe fails HealChecks → still on Lan carrier because Wi-Fi absent, Phone candidate answers → HealTarget == Phone
[Fact] public async Task Lan_recovering_ends_the_heal_from_wifi()          // healed on Wi-Fi, LAN answers RecoverAfterGoodChecks → HealTarget null, LAN 5
[Fact] public async Task Choosing_not_used_for_wifi_resets_its_metric()    // SetOverridesAsync with WifiMac "none" → SetCalls contains (18, IPv4, null)
```

Write each test fully, in the existing file's style (`Create(...)`, `RefreshAsync`, `_clock.Advance(TimeSpan.FromSeconds(5))` between checks, as the existing heal tests do).

- [ ] **Step 3: Run the tests and confirm the new ones fail.**

- [ ] **Step 4: Implement.** Update the log lines to use adapter names, as today.

- [ ] **Step 5: Run the full unit suite.** Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git commit -am "feat(v6): probe every connection and heal in LAN, Wi-Fi, Phone order"
```

---

### Task 8: Route pin (local traffic stays on the cable)

**Files:**
- Create: `src/NetRoute.Core/RoutePin.cs`, `src/NetRoute.Core/Windows/WindowsSubnetRoutes.cs`
- Modify: `src/NetRoute.Core/Abstractions.cs`, `src/NetRoute.Core/Windows/IpHelperNative.cs`, `src/NetRoute.Core/RouteController.cs`, `tools/Restore-Network.cmd`
- Test: `tests/NetRoute.Core.Tests/RoutePinTests.cs`, `tests/NetRoute.Core.Tests/Fakes.cs`, `tests/NetRoute.Core.Tests/RestoreScriptTests.cs`

**Interfaces.**
- Consumes: Task 1's decision (mechanism A, metrics 0 and 256), plus `AdapterInfo.PrefixLength`/`SubnetOf` (Task 3) and `MetricPolicy.For` (Task 5).
- Produces:
  - In `Abstractions.cs`:

    ```csharp
    public interface ISubnetRoutes
    {
        /// RouteMetric of the on-link IPv4 route for prefix (e.g. "192.168.86.0/24") on ifIndex in ActiveStore; null when absent.
        uint? GetOnLinkMetric(int ifIndex, string prefix);
        /// Sets that route's RouteMetric in ActiveStore. Throws when Windows rejects it.
        void SetOnLinkMetric(int ifIndex, string prefix, uint metric);
    }
    ```

  - `public sealed class RoutePin(ISubnetRoutes routes, Action<string> log)`:
    - `const uint PinnedMetric = 0, DefaultMetric = 256`.
    - `static (int IfIndex, string Prefix)? Wanted(RoutingMode mode, ConnectionId? carrier, DetectionResult adapters)`: returns the LAN's index and subnet when **all** of these hold:
      - The mode is not Auto.
      - The LAN is present with an IPv4 address and a prefix.
      - Some other present connection's IPv4 is inside the LAN's subnet.
      - That connection's metric from `MetricPolicy.For(mode, carrier)` is lower than the LAN's.
      Otherwise null.
    - `void Sync((int IfIndex, string Prefix)? wanted)`:
      - Applies the pin when it is wanted and the current metric is not 0.
      - Reverts the previously pinned route (to 256) when the pin moves or is no longer wanted.
      - Logs each change and swallows and logs any exception.
    - `void Release()`: reverts any pin.
  - `RouteController`:
    - Takes an optional `ISubnetRoutes? subnetRoutes = null` constructor parameter, last.
    - Calls `_pin.Sync(RoutePin.Wanted(...))` after the metrics in `RefreshAsync` and `SetModeAsync`, only when `CanModify`.
    - Exposes `public void ReleasePin()`.
  - `FakeSubnetRoutes`, in `Fakes.cs`: holds a `Dictionary<(int, string), uint>` and records its calls.

- [ ] **Step 1: Write the failing tests**

```csharp
public class RoutePinTests
{
    static DetectionResult LanAndWifiOnPzhome(AdapterInfo? wifi = null) => DetectionResult.Of(
        TestAdapters.Phone(), DetectionIssue.None, TestAdapters.Lan(), DetectionIssue.None, wifi ?? TestAdapters.WifiOnLan(), DetectionIssue.None);

    [Fact]
    public void Wifi_mode_on_the_lan_subnet_pins_the_lan_route() =>
        Assert.Equal((10, "192.168.86.0/24"), RoutePin.Wanted(RoutingMode.Wifi, null, LanAndWifiOnPzhome()));

    [Fact]
    public void Healing_onto_wifi_pins_too() =>
        Assert.Equal((10, "192.168.86.0/24"), RoutePin.Wanted(RoutingMode.Lan, ConnectionId.Wifi, LanAndWifiOnPzhome()));

    [Theory, InlineData(RoutingMode.Lan), InlineData(RoutingMode.Phone), InlineData(RoutingMode.Auto)]
    public void No_pin_when_lan_already_preferred_over_wifi(RoutingMode mode) =>
        Assert.Null(RoutePin.Wanted(mode, null, LanAndWifiOnPzhome()));

    [Fact]
    public void No_pin_when_wifi_is_on_another_subnet() =>
        Assert.Null(RoutePin.Wanted(RoutingMode.Wifi, null, LanAndWifiOnPzhome(TestAdapters.Wifi())));

    [Fact]
    public void Sync_applies_once_and_reapplies_after_drift()
    {
        var routes = new FakeSubnetRoutes { [(10, "192.168.86.0/24")] = 256 };
        var pin = new RoutePin(routes, _ => { });
        pin.Sync((10, "192.168.86.0/24"));
        pin.Sync((10, "192.168.86.0/24"));
        Assert.Single(routes.SetCalls);
        routes[(10, "192.168.86.0/24")] = 256;          // DHCP renewal put it back
        pin.Sync((10, "192.168.86.0/24"));
        Assert.Equal(2, routes.SetCalls.Count);
        Assert.Equal(0u, routes[(10, "192.168.86.0/24")]);
    }

    [Fact]
    public void Pin_is_removed_when_wifi_disappears()
    {
        var routes = new FakeSubnetRoutes { [(10, "192.168.86.0/24")] = 256 };
        var pin = new RoutePin(routes, _ => { });
        pin.Sync((10, "192.168.86.0/24"));
        pin.Sync(null);
        Assert.Equal(256u, routes[(10, "192.168.86.0/24")]);
    }

    [Fact]
    public void Release_reverts_and_a_failing_set_is_logged_not_thrown() { /* Release → 256; routes.FailOnSet = true → Sync does not throw, log has one line */ }
}
```

Also add a `RouteControllerTests` case, `Wifi_mode_pins_and_auto_releases`: with the pzhome adapters, `SetModeAsync(Wifi)` sets the fake route to 0, and `SetModeAsync(Auto)` sets it back to 256.

- [ ] **Step 2: Run the tests and confirm they fail.**

- [ ] **Step 3: Implement `RoutePin` and the `RouteController` hook.**

- [ ] **Step 4: Implement `WindowsSubnetRoutes`.**
  - Add `MIB_IPFORWARD_ROW2`, `GetIpForwardEntry2` and `SetIpForwardEntry2` to `IpHelperNative`, following the existing `MIB_IPINTERFACE_ROW` pattern.
  - Address the row by `InterfaceIndex`, `DestinationPrefix` (from the CIDR) and `NextHop` 0.0.0.0, then set `Metric`.
  - Add a layout test next to the existing `IpHelperLayoutTests`: the struct size must be **104 bytes** on x64. Verify that number against the Windows SDK `netioapi.h` when writing the test.
  - Wire `new WindowsSubnetRoutes()` into the `RouteController` construction in `App.xaml.cs` (around line 77). Call `controller.ReleasePin()` in `Quit()` and in `OnSessionEnding`, before the card closes.

- [ ] **Step 5: Add the pin reset to `tools/Restore-Network.cmd`.**
  - After the metrics step, a PowerShell line sets `RouteMetric 256` on every IPv4 `ActiveStore` route where all of these hold: `NextHop -eq '0.0.0.0'`, `RouteMetric -eq 0`, prefix length between 1 and 31, and not `0.0.0.0/0`.
  - It prints "restored local route <prefix> on <alias>" for each.
  - The `/check` path lists them instead.
  - Extend `RestoreScriptTests` with an assertion that the script text contains `RouteMetric 256` and `-PolicyStore ActiveStore`.

- [ ] **Step 6: Run the full unit suite.** Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git commit -am "feat(v6): pin the LAN subnet route so local traffic stays on the cable"
```

---

### Task 9: Settings fields, backup and migration

**Files:**
- Create: `src/NetRoute.Core/SettingsMigration.cs`
- Modify: `src/NetRoute.Core/Settings.cs`, `src/NetRoute.Core/SmartRouting/SmartRoutingSettings.cs`, `src/NetRoute.App/App.xaml.cs` (startup)
- Test: `tests/NetRoute.Core.Tests/SettingsMigrationTests.cs`, `tests/NetRoute.Core.Tests/SettingsStoreTests.cs`

**Interfaces.**
- Consumes: Task 4's `AdapterOverrides`.
- Produces:
  - `AppSettings`:
    - `string? WifiOverrideMac` (added in Task 7)
    - `int SettingsVersion` (default 0)
    - `const int CurrentVersion = 6`
    - `Overrides => new(PhoneOverride, LanOverrideMac, WifiOverrideMac)`
  - `SmartRoutingSettings`: `IReadOnlyList<UserRule> WifiUserRules` (default empty). It is included in `IsValid`, `Equals` and `GetHashCode` like `PhoneUserRules`.
  - `SettingsStore`:
    - `Load` copies the file to `settings.v5-backup.json` (same folder) once, when the loaded `SettingsVersion < CurrentVersion` and no backup exists.
    - `public string BackupPath`.
  - `static AppSettings SettingsMigration.ToV6(AppSettings settings, IReadOnlyList<AdapterInfo> adapters)`:
    - It is a no-op when `SettingsVersion >= 6`.
    - **`LanOverrideMac` names an adapter with `Kind == Wireless`:**
      - `WifiOverrideMac` is set to that MAC, and `LanOverrideMac` becomes null.
      - `Mode` Lan becomes Wifi.
      - `SmartRouting.WifiUserRules` is set to a copy of `PhoneUserRules`.
      - If `SmartRouting.LastLanInterface` equals that adapter's name, it is cleared.
    - **`LanOverrideMac` names no present adapter:** the settings are returned unchanged, version included, so migration retries at the next start.
    - **Otherwise:** `SettingsVersion = 6`.
  - App startup (`App.xaml.cs`, after `SettingsStore.Load` and before the `RouteController` is constructed): `settings = SettingsMigration.ToV6(settings, new WindowsAdapterSource().GetAdapters())`. Save it if it changed, unless the load was `Unreadable`.

- [ ] **Step 1: Write the failing tests**

```csharp
public class SettingsMigrationTests
{
    [Fact]
    public void Wireless_lan_choice_moves_to_wifi_with_mode_and_rules()
    {
        var rule = new UserRule(UserRuleType.Process, "Teams.exe", true);
        var v5 = new AppSettings { Mode = RoutingMode.Lan, LanOverrideMac = "AA-BB-CC-00-00-18",
            SmartRouting = new SmartRoutingSettings { PhoneUserRules = [rule], LastLanInterface = "Wi-Fi" } };

        var v6 = SettingsMigration.ToV6(v5, [TestAdapters.Lan(), TestAdapters.Wifi()]);

        Assert.Equal("AA-BB-CC-00-00-18", v6.WifiOverrideMac);
        Assert.Null(v6.LanOverrideMac);
        Assert.Equal(RoutingMode.Wifi, v6.Mode);
        Assert.Equal([rule], v6.SmartRouting.WifiUserRules);
        Assert.Equal([rule], v6.SmartRouting.PhoneUserRules);
        Assert.Null(v6.SmartRouting.LastLanInterface);
        Assert.Equal(6, v6.SettingsVersion);
    }

    [Fact]
    public void Wired_lan_choice_is_kept() { /* LanOverrideMac = Lan MAC → unchanged except SettingsVersion 6 */ }

    [Fact]
    public void Migration_waits_when_the_lan_override_adapter_is_absent()
    {
        var v5 = new AppSettings { Mode = RoutingMode.Lan, LanOverrideMac = "AA-BB-CC-00-00-18" };
        Assert.Equal(v5, SettingsMigration.ToV6(v5, [TestAdapters.Lan()]));
    }

    [Fact]
    public void Already_v6_is_untouched() { /* SettingsVersion 6 with wireless LanOverrideMac → returned as is */ }
}
```

In `SettingsStoreTests`:
- `Loading_a_v5_file_writes_the_backup_once`: write a v5 JSON, `Load`, and assert the backup exists with identical bytes. Change the file, `Load` again, and assert the backup still has the original bytes.
- `A_v6_file_writes_no_backup`.
- `A_v5_3_file_without_new_fields_loads`: the JSON has no `WifiOverrideMac`, `SettingsVersion` or `WifiUserRules`; it loads, and `WifiUserRules` is empty.

- [ ] **Step 2: Run the tests and confirm they fail.**

- [ ] **Step 3: Implement.** In the startup wiring, log a single line when migration changed something.

- [ ] **Step 4: Run the full unit suite.**
  - Unit suite: PASS.
  - `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~RestoreScriptTests"` must still pass. The new fields are additive.

- [ ] **Step 5: Commit**

```bash
git commit -am "feat(v6): settings version, one-time v5 backup and wireless-LAN migration"
```

---

### Task 10: Generic `StatusPresenter`

**Files:**
- Modify: `src/NetRoute.Core/StatusPresenter.cs`
- Test: `tests/NetRoute.Core.Tests/StatusPresenterTests.cs`

**Interfaces.**
- Consumes: Tasks 2 and 6.
- Produces:
  - `CardView(string Header, bool HeaderWarning, string? Note, IReadOnlyList<ConnectionRow> Rows, string Tooltip, TrayColor TrayColor, bool TrayBadge)`.
    - `Rows` holds one entry per connection where `adapters.IsUsed(id)`, in `CardOrder`.
    - Compatibility helpers: `AdapterRow Row(ConnectionId id)`, plus `Phone` and `Lan` getters that return `Row(...)`.
  - `record ConnectionRow(ConnectionId Id, AdapterRow Row)`.
  - `AdapterRow.Label`: "Phone", "LAN", or `s.WifiNetwork ?? "Wi-Fi"`.
  - Header: `"Internet via {HeaderName(path)}"`. A fallback appends `" ({Reason(main)})"`. Reason uses the main's label (lower-case "phone" for the phone, as today) and the main's button label.
  - `ToastFor`, generalised over `Connections` with today's wording:
    - fallback: `"{MainLabel} {disconnected|lost internet} — internet via {PathLabel}"`
    - back on the main: `"{X} back — internet via {X}"`
    - otherwise: `"Internet now via {X}"`
  - `StickyHealToast(RoutingMode mode, ConnectionId target)`: `"{MainLabel} keeps losing internet — staying on {TargetLabel}. Click {MainLabel} to retry."`.
  - Tooltip: `"Internet: {path}{ (ms)} · {other used connections: "<Label> ready|offline"} joined by " · "}`.
  - Tray colour by mode: Phone Green, Lan Blue, Wifi Purple, Auto Gray.

- [ ] **Step 1: Run the existing `StatusPresenterTests` untouched first.** They define today's wording for Phone and LAN and must still pass after the change. Update only `view.Phone` and `view.Lan` access if the compatibility getters are not used.

- [ ] **Step 2: Write the failing identity and Wi-Fi tests**

```csharp
[Theory]
[InlineData(RoutingMode.Phone, TrayColor.Green)]
[InlineData(RoutingMode.Lan, TrayColor.Blue)]
[InlineData(RoutingMode.Wifi, TrayColor.Purple)]
[InlineData(RoutingMode.Auto, TrayColor.Gray)]
public void Tray_colour_per_mode(RoutingMode mode, TrayColor color) => Assert.Equal(color, StatusPresenter.Present(StatusWith3(mode)).TrayColor);

[Fact] public void Wifi_mode_header() => Assert.Equal("Internet via Wi-Fi", StatusPresenter.Present(StatusWith3(RoutingMode.Wifi) with { ActivePath = InternetPath.Wifi }).Header);
[Fact] public void Healed_onto_wifi_header() // Mode Lan, IsHealing, ActivePath Wifi → "Internet via Wi-Fi (LAN has no internet)"
[Fact] public void Wifi_row_uses_the_network_name() // WifiNetwork "pzhome" → Row(Wifi).Label == "pzhome"; null → "Wi-Fi"
[Fact] public void Unused_wifi_has_no_row() // Wi-Fi NotUsed → Rows ids == [Phone, Lan]
[Fact] public void Toast_when_lan_lost_and_wifi_took_over() // before ActivePath Lan, after ActivePath Wifi, Mode Lan → "LAN lost internet — internet via Wi-Fi"
[Fact] public void Sticky_toast_names_the_target() => Assert.Equal("LAN keeps losing internet — staying on Wi-Fi. Click LAN to retry.", StatusPresenter.StickyHealToast(RoutingMode.Lan, ConnectionId.Wifi));
[Fact] public void Tooltip_lists_every_other_connection() // ActivePath Wifi → "Internet: Wi-Fi (20 ms) · Phone ready · LAN ready"
```

`StatusWith3(mode)` is a test helper: three present adapters, latencies Phone 38, LAN 12 and Wi-Fi 20, `ActivePath` set to the mode's path.

- [ ] **Step 3: Run the tests and confirm they fail.**

- [ ] **Step 4: Implement.**

- [ ] **Step 5: Run the full unit suite.** Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git commit -am "feat(v6): status texts, rows and tray colour for any connection"
```

---

### Task 11: Smart routing pauses while Wi-Fi carries the internet

**Files:**
- Modify: `src/NetRoute.Core/SmartRouting/SmartRoutingController.cs:522-545`, `src/NetRoute.Core/SmartRouting/SmartRoutingPresenter.cs`
- Test: `tests/NetRoute.Core.Tests/SmartRoutingControllerTests.cs`, `SmartRoutingPresenterTests.cs`

**Interfaces.**
- Consumes: `NetworkStatus.Carrier` (Task 6).
- Produces:
  - `SmartRoutingController.Desired()`: after the Auto check, `if (net.Carrier == ConnectionId.Wifi) return (SmartState.Unavailable, WifiPausedMessage)`, where `public const string WifiPausedMessage = "Smart routing paused while on Wi-Fi"`.
  - `WantedExit` and `ProfileOf` are unchanged. They are never reached on Wi-Fi.
  - `SmartRoutingPresenter.Row` shows that message as the footer text, in the muted tone used for "Paused in Auto mode".

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Wifi_mode_pauses_smart_routing()            // net Mode Wifi, Carrier Wifi → Status.State Unavailable, Message WifiPausedMessage, host stopped
[Fact] public async Task Lan_unplugged_with_wifi_present_pauses()    // Mode Lan, Lan null, Carrier Wifi → paused (no traffic sent to the phone)
[Fact] public async Task Leaving_wifi_resumes()                       // Carrier Wifi then Carrier Lan → Running again
[Fact] public async Task Phone_carrier_keeps_running()               // Mode Lan healed to Phone (Carrier Phone) → Running, as today
```

Use the existing `SmartRoutingControllerTests` helpers. Extend the status builder at line ~41 with optional `wifi` and `carrier` parameters.

- [ ] **Step 2: Run the tests and confirm they fail.**

- [ ] **Step 3: Implement.**

- [ ] **Step 4: Run the full unit suite.** Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git commit -am "feat(v6): pause Smart routing while Wi-Fi carries the internet"
```

---

### Task 12: Wi-Fi network name (connection profile)

**Files:**
- Create: `src/NetRoute.Core/Windows/WindowsNetworkNames.cs`
- Modify: `src/NetRoute.Core/Abstractions.cs`, `src/NetRoute.Core/RouteController.cs`, `src/NetRoute.App/App.xaml.cs`
- Test: `tests/NetRoute.Core.Tests/RouteControllerTests.cs`, `WindowsIntegrationTests.cs`

**Interfaces.**
- Consumes: `AdapterInfo.Id` (Task 3).
- Produces:
  - `public interface INetworkNameSource { string? NameOf(AdapterInfo adapter); }`: the Windows network profile name of the network the adapter is connected to, or null.
  - `WindowsNetworkNames : INetworkNameSource`:
    - Network List Manager through `[ComImport]` interfaces: CLSID `DCB00C01-570F-4A9B-8D69-199FDBA5723B`, `INetworkListManager.GetNetworkConnections`, `INetworkConnection.GetAdapterId`/`GetNetwork`, `INetwork.GetName`.
    - It matches `GetAdapterId()` to `Guid.Parse(adapter.Id)`.
    - Any COM failure returns null and is never thrown.
  - `RouteController`:
    - Takes an optional `INetworkNameSource? names = null` constructor parameter.
    - Sets `Status.WifiNetwork = names?.NameOf(detection.Wifi)` when a Wi-Fi adapter is present, otherwise null.
    - It looks the name up only when the Wi-Fi adapter's `(Index, IPv4)` changed since the last lookup, so a reconnect refreshes it and the COM call does not run every poll.

- [ ] **Step 1: Write the failing tests**
  - `Wifi_network_name_is_published`: a fake source returns "pzhome", so `Status.WifiNetwork == "pzhome"`.
  - `Name_is_looked_up_again_only_after_a_reconnect`: the fake counts calls. Two refreshes give 1 call. Changing the Wi-Fi IPv4 gives 2 calls.
  - Integration test `Network_names_read_without_throwing` (Category Integration): for each up wireless adapter, `NameOf` returns null or a non-empty string.

- [ ] **Step 2: Run the tests and confirm they fail.**

- [ ] **Step 3: Implement.** Wire `new WindowsNetworkNames()` in `App.xaml.cs`.

- [ ] **Step 4: Run the unit suite**, then the new integration test by name. Expected: PASS, and on the user's PC the name is "pzhome".

- [ ] **Step 5: Commit**

```bash
git commit -am "feat(v6): show the Wi-Fi network name from the Windows connection profile"
```

---

### Task 13: Card and tray show Wi-Fi

**Files:**
- Modify: `src/NetRoute.App/CardWindow.xaml:120-157`, `src/NetRoute.App/CardWindow.xaml.cs:58-155`, `src/NetRoute.App/TrayIcon.cs`, `src/NetRoute.App/App.xaml.cs:206-213, 404-423`

**Interfaces.**
- Consumes: `CardView.Rows`, `TrayColor.Purple`, `RoutingMode.Wifi` (Tasks 2 and 10).
- Produces, in `CardWindow`:
  - **XAML:**
    - A third grid row of named elements `WifiDot/WifiLabel/WifiAddress/WifiSpeed/WifiLatency/WifiTotal`, collapsed by default.
    - `WifiButton` (`Content="Wi-Fi" Tag="Wifi"`) in the `UniformGrid`, which gets `x:Name="ModeGrid"`.
    - WifiButton tooltip: `"Everything through Wi-Fi. Smart routing pauses on Wi-Fi until v6.1"`.
  - **Code-behind:**
    - A `Dictionary<ConnectionId, RowControls>` maps each connection to its named elements. `Render` loops over `view.Rows`, shows those, and collapses the rest.
    - `ModeGrid.Columns = shownConnections + 1`. `WifiButton.Visibility` follows whether a Wi-Fi row is shown.
    - `SyncModeButtons` sets `IsChecked` from `_mode` for all three buttons.
    - `RenderSpeeds(phone, lan)` and `RenderTotals(phone, lan)` keep their signatures. The Wi-Fi speed and total show `"—"` when either other text is non-empty, and are empty otherwise.
- Produces, in `TrayIcon`:
  - A `_wifi` menu item ("Wi-Fi"), placed after LAN and before Auto, visible only when the view has a Wi-Fi row.
  - It raises `ModeRequested(RoutingMode.Wifi)`.
  - `Draw` colours `TrayColor.Purple` as `Color.FromArgb(0x8B, 0x5C, 0xF6)`.

There are no unit tests: WPF code-behind isn't unit-tested in this repo. The logic sits in the presenter, which Task 10 tests.

- [ ] **Step 1: Implement the XAML and code-behind changes.**

- [ ] **Step 2: Build.**

Run: `dotnet build src/NetRoute.App`
Expected: 0 errors and 0 new warnings.

- [ ] **Step 3: Check the app by hand** (`dotnet run --project src/NetRoute.App`, as administrator):
  - With Wi-Fi not used, the card looks exactly as in v5.3: 2 rows and 3 buttons.
  - Set Wi-Fi in `settings.json` (`"WifiOverrideMac": "90-61-0C-A5-FB-F9"`) and restart. Expected:
    - a third row "pzhome · 192.168.86.35 · <ms>";
    - 4 buttons;
    - a "Wi-Fi" tray item.
  - Click Wi-Fi. Expected:
    - header "Internet via Wi-Fi";
    - a purple tray icon;
    - the footer reads "Smart routing paused while on Wi-Fi" (if Smart is on);
    - `Find-NetRoute -RemoteIPAddress 1.1.1.1` gives Wi-Fi 2;
    - `Find-NetRoute -RemoteIPAddress 192.168.86.1` gives Ethernet.
  - Click LAN. Expected: the pin is released (`Get-NetRoute -InterfaceAlias Ethernet -DestinationPrefix 192.168.86.0/24` shows 256).

- [ ] **Step 4: Commit**

```bash
git commit -am "feat(v6): Wi-Fi row, mode button and tray item"
```

---

### Task 14: Choose adapters dialog (screen 4)

**Files:**
- Modify: `src/NetRoute.App/AdapterPickerWindow.xaml(.cs)`, `src/NetRoute.App/App.xaml.cs:272-277`
- Test: `tests/NetRoute.Core.Tests/AdapterDetectorTests.cs`, for the label helper

**Interfaces.**
- Consumes: `AdapterDetector.Suggest`, `AdapterOverrides` (Task 4), and `AdapterInfo` `Gateway`/`SpeedBps` (Task 3).
- Produces, in Core:
  - `static string AdapterDetector.DetailLine(AdapterInfo adapter, ConnectionId slot)`. Values are joined by `" · "`:
    - `IPv4 ?? "no IPv4"`
    - `"gateway " + Gateway` when there is one
    - for the phone: `"USB tethering"`
    - for LAN/Wi-Fi with `SpeedBps`: `"1 Gbps"` or `"72 Mbps"`, rounded, using decimal units
    - `"not connected"` when `!IsUp`
- Produces, the window:
  - **Layout** matches mockup screen 4:
    - Sections "Phone", "LAN (cable)" and "Wi-Fi", each with a `ComboBox` whose items are "Not used" followed by `AdapterDetector.Candidates`, labelled `"{Name} — {Description}"`.
    - The Wi-Fi section also has a read-only network label (the connection-profile name, or "not connected"). It becomes a picker in v6.4.
    - A detail `TextBlock` under each box shows `DetailLine` for the selection.
    - Buttons: **Detect automatically**, OK and Cancel. The hint text is the exact copy from Global Constraints.
  - **First open:** each slot whose saved value is null is pre-selected from `Suggest`. Phone/LAN null means detect, Wi-Fi null means Not used, and the dialog still pre-fills Wi-Fi from `Suggest`.
  - **Detect automatically:** re-selects every box from `Suggest`.
  - **OK** stores concrete values (description, MAC or `"none"`). It is refused, with an inline message "An adapter can fill only one slot", when two boxes hold the same adapter.
  - **App:** `ChooseAdapters` passes the network name source and calls `SetOverridesAsync(picker.Result)`.

- [ ] **Step 1: Write the failing `DetailLine` tests**

```csharp
[Fact] public void Detail_line_for_lan() => Assert.Equal("192.168.86.42 · gateway 192.168.86.1 · 1 Gbps", AdapterDetector.DetailLine(TestAdapters.Lan() with { SpeedBps = 1_000_000_000 }, ConnectionId.Lan));
[Fact] public void Detail_line_for_phone() => Assert.Equal("192.168.42.11 · gateway 192.168.42.129 · USB tethering", AdapterDetector.DetailLine(TestAdapters.Phone() with { Gateway = "192.168.42.129" }, ConnectionId.Phone));
[Fact] public void Detail_line_for_down_adapter() => Assert.EndsWith("not connected", AdapterDetector.DetailLine(TestAdapters.Wifi() with { IsUp = false, IPv4 = null }, ConnectionId.Wifi));
```

- [ ] **Step 2: Run the tests and confirm they fail, then implement `DetailLine`, then run them again and confirm they pass.**

- [ ] **Step 3: Rebuild the window XAML and code-behind** to the layout above.

- [ ] **Step 4: Build and check by hand:**
  - Open the card's ⋮ menu and choose **Choose adapters…**. The boxes show Ethernet 5, Ethernet and Wi-Fi 2 with their detail lines.
  - Set Wi-Fi to Not used and click OK. The Wi-Fi row and button disappear, and the Wi-Fi metric goes back to automatic.
  - Pick Ethernet in both the LAN and Wi-Fi boxes. OK is refused with the message.

- [ ] **Step 5: Commit**

```bash
git commit -am "feat(v6): Choose adapters shows concrete choices, Wi-Fi and Detect"
```

---

### Task 15: Version, README and the final check on the machine

**Files:** Modify `src/NetRoute.App/NetRoute.App.csproj` (`<Version>6.0.0</Version>`), `README.md`, and `docs/superpowers/specs/2026-10-22-netroute-v6-wifi-connection-design.md` (Status line).

- [ ] **Step 1: Update the README.**
  - Add a "Wi-Fi as a third connection (v6.0)" section covering:
    - choosing Wi-Fi in Choose adapters;
    - the Wi-Fi button;
    - the fallback order LAN → Wi-Fi → Phone;
    - local devices staying on the cable (the pin);
    - Smart routing pausing on Wi-Fi until v6.1.
  - Correct the line "LAN-subnet traffic always uses the LAN, because Windows has a direct route for it" so that it mentions the pin when Wi-Fi shares the LAN's network.
  - Add a downgrade note: "Going back to 5.3? Restore `settings.v5-backup.json` over `settings.json` first."
  - Add a "v6.0" row to the Versions list.

- [ ] **Step 2: Update the spec's Status line** to: "Approved; v6.0 implemented. v6.1–v6.4 to follow."

- [ ] **Step 3: Run the full suites.**

Run: `dotnet test --filter "Category!=Integration"` and then `dotnet test --filter "Category=Integration"`.
Expected: both pass.

- [ ] **Step 4: Run the on-machine checks from the spec,** with Ethernet and Wi-Fi both on pzhome and the phone tethered:
  - **Wi-Fi mode, Smart off and on:**
    - `Find-NetRoute -RemoteIPAddress 192.168.86.1` gives Ethernet.
    - `Find-NetRoute -RemoteIPAddress 1.1.1.1` gives Wi-Fi 2.
    - With Smart on, it is paused.
  - **LAN mode:**
    - Pull the cable. The carrier becomes Wi-Fi, and the card shows "Internet via Wi-Fi".
    - Disconnect Wi-Fi. After 60 s the internet heals to the Phone.
    - Reconnect Wi-Fi. After 3 good checks it steps back up to Wi-Fi.
    - Plug the cable back in. After the recovery checks it returns to LAN.
  - **Quit:** the Ethernet `192.168.86.0/24` route metric is back to 256.
  - **`Restore-Network.cmd /check`:** lists nothing left over.

- [ ] **Step 5: Commit**

```bash
git commit -am "docs(v6.0): README, version 6.0.0, spec status"
```
