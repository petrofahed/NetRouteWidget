# Local network tab — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a "Local network" tab to the network path window that pings the router and user-chosen devices (a NAS, anything), shows this PC's network card (link speed, errors), offers a subnet scan to pick devices from, and states in plain words whether the local network or the internet side is at fault.

**Architecture:** A `LocalNetworkMonitor` (Core/PathMap, same pattern as `PathMonitor`) sends one ICMP echo per device per second through the existing `IPathProbe` and keeps 30 s of `PathSamples`. A pure `LocalNetworkPresenter` (with `LocalVerdict` rules) turns a monitor snapshot, NIC statistics and the path map's internet state into a view model. `NetworkScanner` sweeps the LAN subnet and merges the Windows neighbour table. The App gets a `TabControl` in `PathMapWindow`, a `LocalNetworkTab` control and two small dialogs. The device list is saved in `settings.json`.

**Tech Stack:** C# / .NET 10 (`net10.0-windows`), WPF, xUnit; existing `IcmpPathProbe`, `PathSamples`, `SampleStats`, `PathObserver`, `PathThresholds`; `System.Net.NetworkInformation`; `iphlpapi.dll` (`GetIpNetTable`).

**Spec:** `docs/superpowers/specs/2026-10-14-netroute-v5-2-local-network-design.md` (read it first).

## Global Constraints

- Probing: one ICMP echo per device per second, from the LAN adapter's IPv4 address, TTL `PathThresholds.DirectTtl` (64), timeout 1 s, through `IPathProbe`; 30 s window (`PathThresholds.Window`); at most 3 outstanding rounds; runs only while the path window is open, and only once the Local tab has been shown.
- Device rules: at most 20 devices; name at most 40 characters; address at most 253 characters, IPv4 or hostname only (no IPv6); no duplicate address (case-insensitive); the router row is automatic and not removable. An invalid saved entry is dropped on load, never a failed load.
- Scan: only the LAN adapter's own subnet, never more than 254 addresses; ICMP sweep 64 at a time, 500 ms timeout; names by reverse DNS 8 at a time, 1 s timeout; user-triggered, cancellable, results not saved.
- NIC card: link speed below 1 Gbps on an Ethernet adapter is a warning; error and discard counters are shown as the change since the tab was opened.
- Verdict wording is exactly as in Task 3's rule table; "Measuring… N of 10 s." while the router has fewer than `HopMinSamples` valid samples.
- State colours reuse `PathMapCanvas.StateBrush` (Answering green, Suspect amber, Broken red, Measuring blue-grey, everything else grey).
- Tests live in `tests/NetRoute.Core.Tests` (xUnit, hand-written fakes; `FakeTimeProvider` from `Microsoft.Extensions.Time.Testing`). Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~<Name>"`; the whole suite must stay green (888 tests at the start of this plan).
- Core code is in `src/NetRoute.Core` (`PathMap/` for the monitor, presenter and scanner; `Windows/` for Windows-only code). The App (`src/NetRoute.App`) holds only WPF code.
- Comments follow the surrounding style: short, explain why. Commit messages end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`. Work is on branch `feat/v5-2-local-network`.

## Review Focus

- A hostname device that does not resolve must show "name not found" (Unavailable), never "no reply"/loss. Pinned in Task 2.
- The LAN adapter unplugged, then replugged with another address: no stale samples, probing restarts from the new source. Pinned in Task 2.
- A device that never answers pings must be grey and must not count as healthy or as a problem in the verdict. Pinned in Task 3.
- Router lossy while the NAS is fine must say "not a cable"; devices on another subnet must not drive the verdict. Pinned in Task 3.
- A hand-edited `settings.json` with garbage in `LocalDevices` (null, duplicates, bad addresses, 100 entries) must load. Pinned in Task 1.
- Scan on a /16, a /31 or a /32 stays bounded (at most 254 addresses) and cancels; neighbour-table entries with a zero or broadcast MAC are ignored. Pinned in Task 4.
- Known limitation (documented, not tested): a NAS that rate-limits or blocks pings looks lossy or silent without being faulty.

---

### Task 1: `LocalDevice`, its validator, and the saved list

**Files:**
- Create: `src/NetRoute.Core/PathMap/LocalDevice.cs`
- Modify: `src/NetRoute.Core/Settings.cs` (add the property, sanitise on load)
- Test: `tests/NetRoute.Core.Tests/LocalDeviceTests.cs`

**Interfaces:**
- Produces:
  - `sealed record LocalDevice(string Name, string Address)` (namespace `NetRoute.Core.PathMap`)
  - `static class LocalDeviceValidator { const int MaxDevices = 20, MaxNameLength = 40, MaxAddressLength = 253; bool TryCreate(string? name, string? address, IReadOnlyList<LocalDevice> existing, out LocalDevice device, out string error); IReadOnlyList<LocalDevice> Sanitize(IReadOnlyList<LocalDevice>? saved) }`
  - `AppSettings.LocalDevices : IReadOnlyList<LocalDevice>` (default empty)

- [ ] **Step 1: Write the failing tests**

`tests/NetRoute.Core.Tests/LocalDeviceTests.cs`:

```csharp
using NetRoute.Core.PathMap;

namespace NetRoute.Core.Tests;

public sealed class LocalDeviceTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("netroute-localdevices-").FullName;
    string FilePath => Path.Combine(_dir, "settings.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static readonly IReadOnlyList<LocalDevice> None = [];

    [Fact]
    public void An_ipv4_address_with_a_name_is_accepted_and_trimmed()
    {
        Assert.True(LocalDeviceValidator.TryCreate("  NAS ", " 192.168.86.44 ", None, out var device, out var error));

        Assert.Equal(new LocalDevice("NAS", "192.168.86.44"), device);
        Assert.Equal("", error);
    }

    [Fact]
    public void Without_a_name_the_address_is_the_name()
    {
        Assert.True(LocalDeviceValidator.TryCreate("", "192.168.86.44", None, out var device, out _));

        Assert.Equal(new LocalDevice("192.168.86.44", "192.168.86.44"), device);
    }

    [Theory]
    [InlineData("nas")]
    [InlineData("my-nas.local")]
    [InlineData("MyBookLive")]
    public void A_hostname_is_accepted(string host)
    {
        Assert.True(LocalDeviceValidator.TryCreate(null, host, None, out var device, out _));

        Assert.Equal(host, device.Address);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("1.2.3")]            // numbers and dots only must be a full IPv4 address
    [InlineData("1.2.3.4.5")]
    [InlineData("999.1.1.1")]
    [InlineData("fe80::1")]          // IPv6
    [InlineData("bad host")]
    [InlineData("-x.local")]
    [InlineData("a..b")]
    [InlineData("nas_01")]
    public void A_bad_address_is_refused_with_a_reason(string? address)
    {
        Assert.False(LocalDeviceValidator.TryCreate("x", address, None, out _, out var error));

        Assert.NotEqual("", error);
    }

    [Fact]
    public void A_name_longer_than_the_limit_is_refused()
    {
        Assert.False(LocalDeviceValidator.TryCreate(new string('a', LocalDeviceValidator.MaxNameLength + 1), "192.168.86.44", None, out _, out _));
        Assert.True(LocalDeviceValidator.TryCreate(new string('a', LocalDeviceValidator.MaxNameLength), "192.168.86.44", None, out _, out _));
    }

    [Fact]
    public void An_address_longer_than_the_limit_is_refused()
    {
        var host = string.Join(".", Enumerable.Repeat(new string('a', 50), 6)); // 305 characters
        Assert.False(LocalDeviceValidator.TryCreate(null, host, None, out _, out _));
    }

    [Fact]
    public void A_duplicate_address_is_refused_whatever_its_case()
    {
        var existing = new[] { new LocalDevice("NAS", "MyNas") };

        Assert.False(LocalDeviceValidator.TryCreate("again", "mynas", existing, out _, out var error));
        Assert.Contains("already", error);
    }

    [Fact]
    public void The_twenty_first_device_is_refused()
    {
        var existing = Enumerable.Range(1, LocalDeviceValidator.MaxDevices).Select(i => new LocalDevice($"d{i}", $"10.0.0.{i}")).ToList();

        Assert.False(LocalDeviceValidator.TryCreate("one more", "10.0.0.99", existing, out _, out var error));
        Assert.Contains("20", error);
    }

    [Fact]
    public void Sanitize_keeps_the_good_entries_in_order_and_drops_nulls_duplicates_and_garbage()
    {
        var saved = new List<LocalDevice>
        {
            new("NAS", "192.168.86.44"), null!, new("dup", "192.168.86.44"), new("", "not an address!"), new("TV", "tv.local"),
        };

        var clean = LocalDeviceValidator.Sanitize(saved);

        Assert.Equal([new LocalDevice("NAS", "192.168.86.44"), new LocalDevice("TV", "tv.local")], clean);
    }

    [Fact]
    public void Sanitize_caps_the_list_and_accepts_null()
    {
        var many = Enumerable.Range(1, 100).Select(i => new LocalDevice($"d{i}", $"10.0.1.{i}")).ToList();

        Assert.Equal(LocalDeviceValidator.MaxDevices, LocalDeviceValidator.Sanitize(many).Count);
        Assert.Empty(LocalDeviceValidator.Sanitize(null));
    }

    [Fact]
    public void Settings_default_to_no_devices_and_round_trip_the_list()
    {
        Assert.Empty(new AppSettings().LocalDevices);
        var store = new SettingsStore(FilePath);
        var settings = new AppSettings { LocalDevices = [new LocalDevice("NAS", "192.168.86.44"), new LocalDevice("TV", "tv.local")] };

        store.Save(settings);

        Assert.Equal(settings.LocalDevices, store.Load().Settings.LocalDevices);
    }

    [Fact]
    public void A_settings_file_from_before_this_version_loads_with_no_devices()
    {
        File.WriteAllText(FilePath, "{ \"Mode\": \"Lan\" }");

        var result = new SettingsStore(FilePath).Load();

        Assert.False(result.Recovered);
        Assert.Empty(result.Settings.LocalDevices);
    }

    [Fact]
    public void Garbage_in_the_saved_device_list_is_dropped_without_losing_the_other_settings()
    {
        File.WriteAllText(FilePath, """
            { "Mode": "Lan", "LocalDevices": [ { "Name": "NAS", "Address": "192.168.86.44" }, { "Name": "x", "Address": "??" }, null,
              { "Name": "NAS again", "Address": "192.168.86.44" } ] }
            """);

        var result = new SettingsStore(FilePath).Load();

        Assert.False(result.Recovered);
        Assert.Equal(RoutingMode.Lan, result.Settings.Mode);
        Assert.Equal([new LocalDevice("NAS", "192.168.86.44")], result.Settings.LocalDevices);
    }

    [Fact]
    public void A_null_device_list_in_the_file_loads_as_empty()
    {
        File.WriteAllText(FilePath, "{ \"LocalDevices\": null }");

        Assert.Empty(new SettingsStore(FilePath).Load().Settings.LocalDevices);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~LocalDeviceTests"`
Expected: build FAIL (`LocalDevice`, `LocalDeviceValidator`, `AppSettings.LocalDevices` do not exist).

- [ ] **Step 3: Implement**

`src/NetRoute.Core/PathMap/LocalDevice.cs`:

```csharp
using System.Net;
using System.Text.RegularExpressions;

namespace NetRoute.Core.PathMap;

/// A device on the local network the user wants to test: a name for the list and an IPv4 address or hostname.
public sealed record LocalDevice(string Name, string Address);

/// The rules for a device the user adds, shared by the add dialog and the settings loader.
public static partial class LocalDeviceValidator
{
    public const int MaxDevices = 20;
    public const int MaxNameLength = 40;
    public const int MaxAddressLength = 253;

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$")]
    private static partial Regex Hostname();

    public static bool TryCreate(string? name, string? address, IReadOnlyList<LocalDevice> existing, out LocalDevice device, out string error)
    {
        device = new LocalDevice("", "");
        var host = (address ?? "").Trim();
        var label = (name ?? "").Trim();

        if (host.Length == 0) return Fail("Enter an IPv4 address or a device name.", out error);
        if (host.Length > MaxAddressLength) return Fail($"The address is longer than {MaxAddressLength} characters.", out error);
        if (host.Contains(':')) return Fail("Only IPv4 addresses and hostnames are supported.", out error);
        if (host.All(c => char.IsAsciiDigit(c) || c == '.'))
        {
            // Numbers and dots only: it has to be a complete IPv4 address, never a "hostname" like 1.2.3.
            if (host.Count(c => c == '.') != 3 || !IPAddress.TryParse(host, out _)) return Fail("That is not a valid IPv4 address.", out error);
        }
        else if (!Hostname().IsMatch(host))
        {
            return Fail("That is not a valid address or device name.", out error);
        }
        if (label.Length > MaxNameLength) return Fail($"The name is longer than {MaxNameLength} characters.", out error);
        if (existing.Count >= MaxDevices) return Fail($"You can test at most {MaxDevices} devices.", out error);
        if (existing.Any(d => string.Equals(d.Address, host, StringComparison.OrdinalIgnoreCase))) return Fail("That address is already in the list.", out error);

        device = new LocalDevice(label.Length == 0 ? host : label, host);
        error = "";
        return true;
    }

    static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    /// A saved list with its invalid and duplicate entries dropped, in order, at most MaxDevices.
    public static IReadOnlyList<LocalDevice> Sanitize(IReadOnlyList<LocalDevice>? saved)
    {
        var kept = new List<LocalDevice>();
        foreach (var entry in saved ?? [])
            if (entry is not null && TryCreate(entry.Name, entry.Address, kept, out var ok, out _)) kept.Add(ok);
        // One shared empty list: AppSettings is a record, and two settings with "no devices" must compare equal.
        return kept.Count == 0 ? Array.Empty<LocalDevice>() : kept;
    }
}
```

In `src/NetRoute.Core/Settings.cs` add `using NetRoute.Core.PathMap;` at the top, the property after `SmartRouting`, and the sanitising in `Load`:

```csharp
    /// Devices on the local network the user tests in the path window's "Local network" tab.
    public IReadOnlyList<LocalDevice> LocalDevices { get; init; } = Array.Empty<LocalDevice>();
```
```csharp
            if (settings.SmartRouting is null) settings = settings with { SmartRouting = new SmartRoutingSettings() };
            settings = settings with { LocalDevices = LocalDeviceValidator.Sanitize(settings.LocalDevices) }; // bad entries are dropped, never a failed load
```
(the second line goes right after the existing `SmartRouting is null` line, before the `IsValid` check.)

- [ ] **Step 4: Run to verify they pass, then the whole suite**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~LocalDeviceTests"` then `dotnet test tests/NetRoute.Core.Tests`
Expected: all PASS, including the existing `SettingsStoreTests` (they compare `AppSettings` records, which is why the empty list is the shared `Array.Empty<LocalDevice>()` everywhere).

- [ ] **Step 5: Commit**

```bash
git add src/NetRoute.Core/PathMap/LocalDevice.cs src/NetRoute.Core/Settings.cs tests/NetRoute.Core.Tests/LocalDeviceTests.cs
git commit -m "feat(core): LocalDevice, its validator and the saved device list

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 2: `LocalNetworkMonitor`

**Files:**
- Create: `src/NetRoute.Core/PathMap/LocalNetworkMonitor.cs`
- Test: `tests/NetRoute.Core.Tests/LocalNetworkMonitorTests.cs`

**Interfaces:**
- Consumes: `IPathProbe`, `EchoResult`, `Sample`, `ProbeOutcome`, `PathSamples`, `PathThresholds` (existing); `LocalDevice` (Task 1); `AdapterInfo` (existing).
- Produces:
  - `sealed record DeviceSnapshot(string Key, string Name, string Address, IPAddress? Resolved, bool IsRouter, bool NameNotFound, IReadOnlyList<Sample> Samples)`
  - `sealed record LocalSnapshot(DateTimeOffset At, AdapterInfo? Lan, IPAddress? Source, IReadOnlyList<DeviceSnapshot> Devices)`
  - `LocalNetworkMonitor(IPathProbe probe, Func<string, Task<IReadOnlyList<IPAddress>>> resolve, TimeProvider time, PathThresholds thresholds, Action<string> log)` with `void SetNetwork(AdapterInfo? lan, IPAddress? gateway, IReadOnlyList<LocalDevice> devices)`, `Task RoundAsync(CancellationToken ct)`, `LocalSnapshot Snapshot()`, `Task RunAsync(CancellationToken ct)`; constants `RouterKey = "router"`, `Period`, `EchoTimeout`, `ResolveRetry`, `ResolveRefresh`, `MaxOutstandingRounds`; `static string KeyOf(string address)` (the key of a user device is `"dev:" + address.Trim().ToLowerInvariant()`).

- [ ] **Step 1: Write the failing tests**

`tests/NetRoute.Core.Tests/LocalNetworkMonitorTests.cs`:

```csharp
using System.Net;
using Microsoft.Extensions.Time.Testing;
using NetRoute.Core.PathMap;

namespace NetRoute.Core.Tests;

public class LocalNetworkMonitorTests
{
    static IPAddress Ip(string s) => IPAddress.Parse(s);
    static readonly IPAddress Router = Ip("192.168.86.1");
    static readonly IPAddress Nas = Ip("192.168.86.44");
    static readonly LocalDevice NasDevice = new("NAS", "192.168.86.44");

    sealed class FakeLocalProbe : IPathProbe
    {
        public Dictionary<IPAddress, EchoResult> ByTarget { get; } = new();
        public EchoResult Default { get; set; } = new(ProbeOutcome.NoReply);
        public List<(IPAddress Source, IPAddress Target, int Ttl, TimeSpan Timeout)> Sent { get; } = new();
        public TaskCompletionSource? Gate { get; set; }
        public Exception? Throw { get; set; }

        public async Task<EchoResult> EchoAsync(IPAddress source, IPAddress target, int ttl, TimeSpan timeout, CancellationToken ct)
        {
            lock (Sent) Sent.Add((source, target, ttl, timeout));
            if (Gate is { } gate) await gate.Task;
            if (Throw is { } ex) throw ex;
            return ByTarget.TryGetValue(target, out var r) ? r : Default;
        }
    }

    sealed class Rig
    {
        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
        public FakeLocalProbe Probe { get; } = new();
        public Dictionary<string, IReadOnlyList<IPAddress>> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Resolved { get; } = new();
        public List<string> Logs { get; } = new();
        public LocalNetworkMonitor Monitor { get; }

        public Rig()
        {
            Probe.ByTarget[Router] = new EchoResult(ProbeOutcome.Reply, Router, 1);
            Probe.ByTarget[Nas] = new EchoResult(ProbeOutcome.Reply, Nas, 2);
            Monitor = new LocalNetworkMonitor(Probe,
                name =>
                {
                    lock (Resolved) Resolved.Add(name);
                    return Task.FromResult<IReadOnlyList<IPAddress>>(Names.TryGetValue(name, out var found) ? found : []);
                },
                Time, PathThresholds.Default, Logs.Add);
        }

        public async Task Rounds(int count)
        {
            for (var i = 0; i < count; i++)
            {
                await Monitor.RoundAsync(CancellationToken.None);
                Time.Advance(TimeSpan.FromSeconds(1));
            }
        }

        public DeviceSnapshot Device(string key) => Monitor.Snapshot().Devices.Single(d => d.Key == key);
    }

    [Fact]
    public async Task The_router_and_each_device_are_pinged_once_per_round_from_the_lan_address()
    {
        var rig = new Rig();
        rig.Monitor.SetNetwork(TestAdapters.Lan(), Router, [NasDevice]);

        await rig.Rounds(3);

        Assert.Equal(6, rig.Probe.Sent.Count);
        Assert.All(rig.Probe.Sent, s =>
        {
            Assert.Equal(Ip("192.168.86.42"), s.Source);
            Assert.Equal(PathThresholds.Default.DirectTtl, s.Ttl);
            Assert.Equal(LocalNetworkMonitor.EchoTimeout, s.Timeout);
        });
        Assert.Equal(3, rig.Device(LocalNetworkMonitor.RouterKey).Samples.Count);
        var nas = rig.Device(LocalNetworkMonitor.KeyOf("192.168.86.44"));
        Assert.Equal("NAS", nas.Name);
        Assert.All(nas.Samples, s => Assert.Equal((ProbeOutcome.Reply, 2), (s.Outcome, s.RttMs ?? 0)));
    }

    [Fact]
    public async Task A_lost_ping_is_no_reply_and_a_probe_that_could_not_run_is_unavailable()
    {
        var rig = new Rig();
        rig.Probe.ByTarget[Nas] = new EchoResult(ProbeOutcome.NoReply);
        rig.Probe.ByTarget[Router] = new EchoResult(ProbeOutcome.Unavailable);
        rig.Monitor.SetNetwork(TestAdapters.Lan(), Router, [NasDevice]);

        await rig.Rounds(2);

        Assert.All(rig.Device(LocalNetworkMonitor.KeyOf("192.168.86.44")).Samples, s => Assert.Equal(ProbeOutcome.NoReply, s.Outcome));
        Assert.All(rig.Device(LocalNetworkMonitor.RouterKey).Samples, s => Assert.Equal(ProbeOutcome.Unavailable, s.Outcome));
    }

    [Fact]
    public async Task Without_a_lan_adapter_nothing_is_probed_and_there_are_no_devices()
    {
        var rig = new Rig();
        rig.Monitor.SetNetwork(null, null, [NasDevice]);

        await rig.Rounds(2);

        Assert.Empty(rig.Probe.Sent);
        Assert.Empty(rig.Monitor.Snapshot().Devices);
        Assert.Null(rig.Monitor.Snapshot().Lan);
    }

    [Fact]
    public async Task A_lan_adapter_that_has_no_address_yet_is_not_probed()
    {
        var rig = new Rig();
        rig.Monitor.SetNetwork(TestAdapters.Lan() with { IPv4 = null }, Router, [NasDevice]);

        await rig.Rounds(2);

        Assert.Empty(rig.Probe.Sent);
    }

    [Fact]
    public async Task A_replugged_lan_with_a_new_address_starts_clean_from_the_new_source()
    {
        var rig = new Rig();
        rig.Monitor.SetNetwork(TestAdapters.Lan(), Router, [NasDevice]);
        await rig.Rounds(3);

        rig.Monitor.SetNetwork(null, null, [NasDevice]); // cable out
        await rig.Rounds(2);
        Assert.Equal(6, rig.Probe.Sent.Count); // nothing was sent while it was out

        rig.Monitor.SetNetwork(TestAdapters.Lan() with { IPv4 = "192.168.86.77" }, Router, [NasDevice]); // cable back, new lease
        await rig.Rounds(1);

        Assert.Equal(Ip("192.168.86.77"), rig.Probe.Sent[^1].Source);
        Assert.Single(rig.Device(LocalNetworkMonitor.RouterKey).Samples); // the old samples are gone
    }

    [Fact]
    public async Task Adding_a_device_keeps_the_others_samples_and_removing_it_drops_only_its_own()
    {
        var rig = new Rig();
        rig.Monitor.SetNetwork(TestAdapters.Lan(), Router, []);
        await rig.Rounds(3);

        rig.Monitor.SetNetwork(TestAdapters.Lan(), Router, [NasDevice]);
        await rig.Rounds(1);
        Assert.Equal(4, rig.Device(LocalNetworkMonitor.RouterKey).Samples.Count);
        Assert.Single(rig.Device(LocalNetworkMonitor.KeyOf("192.168.86.44")).Samples);

        rig.Monitor.SetNetwork(TestAdapters.Lan(), Router, []);
        Assert.DoesNotContain(rig.Monitor.Snapshot().Devices, d => d.Name == "NAS");
        Assert.Equal(4, rig.Device(LocalNetworkMonitor.RouterKey).Samples.Count);
    }

    [Fact]
    public async Task A_new_gateway_gives_the_router_row_fresh_samples()
    {
        var rig = new Rig();
        rig.Monitor.SetNetwork(TestAdapters.Lan(), Router, []);
        await rig.Rounds(3);

        rig.Monitor.SetNetwork(TestAdapters.Lan(), Ip("192.168.86.2"), []);
        await rig.Rounds(1);

        var router = rig.Device(LocalNetworkMonitor.RouterKey);
        Assert.Equal("192.168.86.2", router.Address);
        Assert.Single(router.Samples);
    }

    [Fact]
    public async Task A_hostname_is_resolved_once_and_the_address_it_gave_is_pinged()
    {
        var rig = new Rig();
        rig.Names["nas.local"] = [Nas];
        rig.Monitor.SetNetwork(TestAdapters.Lan(), Router, [new LocalDevice("NAS", "nas.local")]);

        await rig.Rounds(3);

        Assert.Equal(["nas.local"], rig.Resolved);
        var nas = rig.Device(LocalNetworkMonitor.KeyOf("nas.local"));
        Assert.Equal(Nas, nas.Resolved);
        Assert.False(nas.NameNotFound);
        Assert.Contains(rig.Probe.Sent, s => s.Target.Equals(Nas));
    }

    [Fact]
    public async Task A_name_that_does_not_resolve_is_unavailable_not_lost_and_is_retried_later()
    {
        var rig = new Rig();
        rig.Monitor.SetNetwork(TestAdapters.Lan(), Router, [new LocalDevice("Ghost", "ghost.local")]);

        await rig.Rounds(3);

        var ghost = rig.Device(LocalNetworkMonitor.KeyOf("ghost.local"));
        Assert.True(ghost.NameNotFound);
        Assert.All(ghost.Samples, s => Assert.Equal(ProbeOutcome.Unavailable, s.Outcome));
        Assert.Single(rig.Resolved); // not asked again inside the retry delay

        rig.Names["ghost.local"] = [Ip("192.168.86.99")];
        rig.Time.Advance(LocalNetworkMonitor.ResolveRetry);
        await rig.Rounds(1);

        Assert.Equal(2, rig.Resolved.Count);
        Assert.False(rig.Device(LocalNetworkMonitor.KeyOf("ghost.local")).NameNotFound);
    }

    [Fact]
    public async Task A_probe_that_was_in_flight_when_the_network_changed_is_dropped()
    {
        var rig = new Rig();
        rig.Probe.Gate = new TaskCompletionSource();
        rig.Monitor.SetNetwork(TestAdapters.Lan(), Router, []);
        var round = rig.Monitor.RoundAsync(CancellationToken.None);

        rig.Monitor.SetNetwork(TestAdapters.Lan() with { IPv4 = "192.168.86.77" }, Router, []); // the lane was reset meanwhile
        rig.Probe.Gate.SetResult();
        await round;

        Assert.Empty(rig.Device(LocalNetworkMonitor.RouterKey).Samples);
    }

    [Fact]
    public async Task A_probe_that_throws_is_unavailable_and_logged_once()
    {
        var rig = new Rig();
        rig.Probe.Throw = new InvalidOperationException("icmp is blocked");
        rig.Monitor.SetNetwork(TestAdapters.Lan(), Router, [NasDevice]);

        await rig.Rounds(3);

        Assert.All(rig.Device(LocalNetworkMonitor.RouterKey).Samples, s => Assert.Equal(ProbeOutcome.Unavailable, s.Outcome));
        Assert.Equal(1, rig.Logs.Count(l => l.Contains("icmp is blocked")));
    }

    [Fact]
    public async Task A_cancelled_run_ends_without_throwing()
    {
        var rig = new Rig();
        rig.Monitor.SetNetwork(TestAdapters.Lan(), Router, [NasDevice]);
        using var cts = new CancellationTokenSource();
        var run = rig.Monitor.RunAsync(cts.Token);

        cts.Cancel();
        await run; // would throw if cancellation leaked out

        Assert.True(run.IsCompletedSuccessfully);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~LocalNetworkMonitorTests"`
Expected: build FAIL (`LocalNetworkMonitor`, `DeviceSnapshot`, `LocalSnapshot` do not exist).

- [ ] **Step 3: Implement**

`src/NetRoute.Core/PathMap/LocalNetworkMonitor.cs`:

```csharp
using System.Net;
using System.Net.Sockets;

namespace NetRoute.Core.PathMap;

public sealed record DeviceSnapshot(
    string Key, string Name, string Address, IPAddress? Resolved, bool IsRouter, bool NameNotFound, IReadOnlyList<Sample> Samples);

public sealed record LocalSnapshot(DateTimeOffset At, AdapterInfo? Lan, IPAddress? Source, IReadOnlyList<DeviceSnapshot> Devices);

/// Pings the router and the user's devices once a second from the LAN adapter's address while it runs, and keeps the last 30 s of
/// samples per device. Hands out immutable snapshots; it never touches the UI. The round is public so tests can drive it without timers.
public sealed class LocalNetworkMonitor
{
    public const string RouterKey = "router";
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan EchoTimeout = TimeSpan.FromSeconds(1);
    /// A hostname that did not resolve is asked again after this long; one that did is asked again after ResolveRefresh.
    public static readonly TimeSpan ResolveRetry = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ResolveRefresh = TimeSpan.FromMinutes(1);
    /// Rounds start on a fixed rate even while earlier ones wait for a timeout; this many may be outstanding.
    public const int MaxOutstandingRounds = 3;
    const int MaxLoggedMessages = 20;

    readonly IPathProbe _probe;
    readonly Func<string, Task<IReadOnlyList<IPAddress>>> _resolve;
    readonly TimeProvider _time;
    readonly PathThresholds _t;
    readonly Action<string> _log;
    readonly Lock _gate = new();
    readonly HashSet<string> _logged = [];
    AdapterInfo? _lan;
    IPAddress? _source;
    List<Dev> _devices = [];
    /// Bumped when the LAN adapter or its address changes, so a probe that was in flight for the old state can be recognised and dropped.
    int _generation;

    public LocalNetworkMonitor(
        IPathProbe probe, Func<string, Task<IReadOnlyList<IPAddress>>> resolve, TimeProvider time, PathThresholds thresholds, Action<string> log)
    {
        _probe = probe;
        _resolve = resolve;
        _time = time;
        _t = thresholds;
        _log = log;
    }

    sealed class Dev(string key, string name, string address, bool isRouter, PathThresholds t)
    {
        public string Key { get; } = key;
        public string Name { get; set; } = name;
        public string Address { get; } = address;
        public bool IsRouter { get; } = isRouter;
        public PathSamples Samples { get; } = new(t.Window);
        public IPAddress? Resolved;
        /// When the name was last asked about (successfully or not).
        public DateTimeOffset ResolvedAt = DateTimeOffset.MinValue;
        public bool NameNotFound;
    }

    public static string KeyOf(string address) => "dev:" + address.Trim().ToLowerInvariant();

    /// The network to watch: the LAN adapter, its gateway (the router row) and the user's devices. A different adapter or address clears
    /// everything; otherwise devices keep their samples, new ones start empty and removed ones go.
    public void SetNetwork(AdapterInfo? lan, IPAddress? gateway, IReadOnlyList<LocalDevice> devices)
    {
        lock (_gate)
        {
            var source = lan?.IPv4 is { } text && IPAddress.TryParse(text, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork ? ip : null;
            var unchanged = lan?.Index == _lan?.Index && Equals(source, _source);
            _lan = lan;
            if (!unchanged)
            {
                _source = source;
                _generation++;
                _devices = [];
            }

            var next = new List<Dev>();
            if (gateway is not null && source is not null)
            {
                var existing = _devices.FirstOrDefault(d => d.IsRouter && d.Address == gateway.ToString());
                next.Add(existing ?? new Dev(RouterKey, "Router", gateway.ToString(), true, _t));
            }
            if (source is not null)
            {
                foreach (var device in devices)
                {
                    var key = KeyOf(device.Address);
                    var existing = _devices.FirstOrDefault(d => d.Key == key);
                    if (existing is not null)
                    {
                        existing.Name = device.Name;
                        next.Add(existing);
                    }
                    else next.Add(new Dev(key, device.Name, device.Address.Trim(), false, _t));
                }
            }
            _devices = next;
        }
    }

    /// One echo per device, in parallel.
    public async Task RoundAsync(CancellationToken ct)
    {
        var work = new List<Task>();
        lock (_gate)
        {
            if (_source is not { } source) return;
            var generation = _generation;
            foreach (var dev in _devices) work.Add(ProbeAsync(dev, generation, source, ct));
        }
        await Task.WhenAll(work);
    }

    async Task ProbeAsync(Dev dev, int generation, IPAddress source, CancellationToken ct)
    {
        // A sample belongs to the moment its probe was sent, not the moment it finished.
        var sentAt = _time.GetUtcNow();
        var target = await TargetOf(dev, generation);
        if (ct.IsCancellationRequested) return; // the window is closing: a cancelled check is discarded, not stored
        if (target is null)
        {
            Store(dev, generation, new Sample(sentAt, ProbeOutcome.Unavailable)); // a name that does not resolve says nothing about the network
            return;
        }
        var result = await Guard(() => _probe.EchoAsync(source, target, _t.DirectTtl, EchoTimeout, ct), new EchoResult(ProbeOutcome.Unavailable));
        if (ct.IsCancellationRequested) return;
        Store(dev, generation, new Sample(sentAt, result.Outcome, result.RttMs));
    }

    void Store(Dev dev, int generation, Sample sample)
    {
        lock (_gate)
        {
            if (generation != _generation || !_devices.Contains(dev)) return; // the network changed or the device was removed meanwhile
            dev.Samples.Add(sample);
        }
    }

    /// The address to ping: an IPv4 literal as typed, or the name resolved (cached; a failed name is retried after ResolveRetry).
    async Task<IPAddress?> TargetOf(Dev dev, int generation)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (IPAddress.TryParse(dev.Address, out var literal) && literal.AddressFamily == AddressFamily.InterNetwork)
            {
                dev.Resolved = literal;
                dev.NameNotFound = false;
                return literal;
            }
            if (dev.Resolved is not null && now - dev.ResolvedAt < ResolveRefresh) return dev.Resolved;
            if (dev.Resolved is null && now - dev.ResolvedAt < ResolveRetry) return null;
        }

        IReadOnlyList<IPAddress> found;
        try
        {
            found = await _resolve(dev.Address);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            LogOnce(ex.Message);
            found = [];
        }
        var address = found.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        lock (_gate)
        {
            if (generation == _generation)
            {
                dev.ResolvedAt = now;
                dev.Resolved = address;
                dev.NameNotFound = address is null;
            }
        }
        return address;
    }

    public LocalSnapshot Snapshot()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            return new LocalSnapshot(now, _lan, _source, _devices
                .Select(d => new DeviceSnapshot(d.Key, d.Name, d.Address, d.Resolved, d.IsRouter, d.NameNotFound, d.Samples.Snapshot(now)))
                .ToList());
        }
    }

    /// Runs until cancelled, then waits for the rounds still out (they watch the same token). Fixed rate: a new round starts every
    /// period whether or not the previous one has finished, so the 30 s window fills at the stated rate during an outage.
    public async Task RunAsync(CancellationToken ct)
    {
        var rounds = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tick = Task.Delay(Period, _time, ct);
                rounds.RemoveAll(t => t.IsCompleted);
                if (rounds.Count < MaxOutstandingRounds) rounds.Add(Launched(() => RoundAsync(ct)));
                await tick;
            }
        }
        catch (OperationCanceledException) { }
        finally { await Task.WhenAll(rounds); }
    }

    /// Starts work without awaiting it. The returned task never faults: an exception is logged (once per message) and the loop carries on.
    async Task Launched(Func<Task> work)
    {
        try { await work(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogOnce(ex.Message); }
    }

    /// A probe must not throw for a failed measurement; if one does, the sample is Unavailable (never loss) and the problem is logged once.
    async Task<T> Guard<T>(Func<Task<T>> probe, T onError)
    {
        try { return await probe(); }
        catch (OperationCanceledException) { return onError; }
        catch (Exception ex)
        {
            LogOnce(ex.Message);
            return onError;
        }
    }

    void LogOnce(string message)
    {
        bool fresh;
        lock (_gate) fresh = _logged.Count < MaxLoggedMessages && _logged.Add(message);
        if (fresh) _log($"Local network: a check failed to run: {message}");
    }
}
```

- [ ] **Step 4: Run to verify they pass, then the whole suite**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~LocalNetworkMonitorTests"` then `dotnet test tests/NetRoute.Core.Tests`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/NetRoute.Core/PathMap/LocalNetworkMonitor.cs tests/NetRoute.Core.Tests/LocalNetworkMonitorTests.cs
git commit -m "feat(core): LocalNetworkMonitor - one ping per device per second from the LAN address

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 3: `LocalNetworkPresenter` and the verdict

**Files:**
- Create: `src/NetRoute.Core/PathMap/LocalNetworkPresenter.cs` (view records, `LocalState`, `InternetState`, `INetworkInfo`/`NicStats` live in `NetworkInfo.cs`, see below)
- Create: `src/NetRoute.Core/PathMap/NetworkInfo.cs`
- Modify: `src/NetRoute.Core/PathMap/PathMapPresenter.cs` (make `StatsOf` `internal static`)
- Test: `tests/NetRoute.Core.Tests/LocalNetworkPresenterTests.cs`

**Interfaces:**
- Consumes: `LocalSnapshot`, `DeviceSnapshot` (Task 2); `SampleStats`, `PathObserver`, `Observation`, `PathThresholds`, `DetailView`, `HopDisplay`, `Sample` (existing).
- Produces:
  - `sealed record NicStats(string Name, long? SpeedBps, bool IsEthernet, long InErrors, long InDiscards, long OutErrors, long OutDiscards)`
  - `interface INetworkInfo { NicStats? ReadNic(string adapterName); IPAddress? GatewayV4(string adapterName); int? PrefixLengthV4(string adapterName, IPAddress address); }`
  - `enum LocalState { Measuring, Answering, Lossy, Down, NoPing, NotFound, Unavailable }`, `enum InternetState { Unknown, Fine, Failing }`
  - `sealed record LocalDeviceRow(string Key, string Name, string Address, LocalState State, string StateLabel, string Metric, bool IsRouter, bool OnSubnet)`
  - `sealed record NicView(string Name, string SpeedText, bool SpeedWarning, string SpeedNote, string ErrorsText, bool ErrorsWarning)`
  - `sealed record LocalNetworkView(bool Connected, string Message, IReadOnlyList<string> Verdict, bool VerdictWarning, NicView? Nic, IReadOnlyList<LocalDeviceRow> Rows, DetailView? Detail)`
  - `static class LocalNetworkPresenter { LocalNetworkView Present(LocalSnapshot snap, NicStats? nic, NicStats? nicBaseline, InternetState internet, string? selectedKey, PathThresholds t, int? prefixLength); InternetState InternetOf(HopDisplay state); HopDisplay AsHop(LocalState state) }`

- [ ] **Step 1: Write the failing tests**

`tests/NetRoute.Core.Tests/LocalNetworkPresenterTests.cs`:

```csharp
using System.Net;
using NetRoute.Core.PathMap;

namespace NetRoute.Core.Tests;

public class LocalNetworkPresenterTests
{
    static IPAddress Ip(string s) => IPAddress.Parse(s);
    static readonly PathThresholds T = PathKit.T;
    static readonly IPAddress Source = Ip("192.168.86.42");

    static DeviceSnapshot Router(Func<int, ProbeOutcome> outcome, int rtt = 1) =>
        new(LocalNetworkMonitor.RouterKey, "Router", "192.168.86.1", Ip("192.168.86.1"), true, false, PathKit.Every(1, outcome, rtt));

    static DeviceSnapshot Device(string name, string address, Func<int, ProbeOutcome> outcome, int rtt = 1, string? resolved = null, bool notFound = false) =>
        new(LocalNetworkMonitor.KeyOf(address), name, address, resolved is null ? (IPAddress.TryParse(address, out var ip) ? ip : null) : Ip(resolved),
            false, notFound, notFound ? PathKit.Every(1, PathKit.AllUnavailable) : PathKit.Every(1, outcome, rtt));

    static LocalSnapshot Snap(params DeviceSnapshot[] devices) => new(PathKit.Now, TestAdapters.Lan(), Source, devices);

    static LocalNetworkView Present(LocalSnapshot snap, InternetState internet = InternetState.Unknown, string? selected = null,
                                    NicStats? nic = null, NicStats? baseline = null) =>
        LocalNetworkPresenter.Present(snap, nic, baseline, internet, selected, T, 24);

    static string Verdict(LocalNetworkView view) => string.Join(" ", view.Verdict);

    [Fact]
    public void While_the_router_has_too_few_samples_the_verdict_says_it_is_measuring()
    {
        var shortSeries = new DeviceSnapshot(LocalNetworkMonitor.RouterKey, "Router", "192.168.86.1", Ip("192.168.86.1"), true, false,
            PathKit.Every(1, PathKit.AllReply).Take(4).ToList());

        var view = Present(Snap(shortSeries));

        Assert.Equal(["Measuring… 4 of 10 s."], view.Verdict);
        Assert.False(view.VerdictWarning);
    }

    [Fact]
    public void Everything_local_healthy_and_the_internet_failing_points_beyond_the_router()
    {
        var view = Present(Snap(Router(PathKit.AllReply), Device("NAS", "192.168.86.44", PathKit.AllReply)), InternetState.Failing);

        Assert.Equal("Your local network is healthy: Router and NAS answer normally. The fault is beyond your router (its link to the modem, the modem, or the line).", Verdict(view));
        Assert.True(view.VerdictWarning);
    }

    [Fact]
    public void Everything_local_healthy_and_the_internet_fine_or_unknown_just_says_healthy()
    {
        foreach (var internet in new[] { InternetState.Fine, InternetState.Unknown })
        {
            var view = Present(Snap(Router(PathKit.AllReply), Device("NAS", "192.168.86.44", PathKit.AllReply)), internet);

            Assert.Equal("Your local network is healthy.", Verdict(view));
            Assert.False(view.VerdictWarning);
        }
    }

    [Fact]
    public void The_router_and_another_device_losing_packets_points_at_this_pcs_cable_port_and_switch()
    {
        var view = Present(Snap(Router(PathKit.LostDuring(20)), Device("NAS", "192.168.86.44", PathKit.LostDuring(20))));

        Assert.Equal("Several devices on your local network are losing packets. Check this PC's cable and its switch port, and the switch.", Verdict(view));
        Assert.True(view.VerdictWarning);
    }

    [Fact]
    public void Two_devices_losing_packets_with_a_fine_router_points_at_the_switch_they_share()
    {
        var view = Present(Snap(Router(PathKit.AllReply), Device("NAS", "192.168.86.44", PathKit.LostDuring(20)), Device("TV", "192.168.86.50", PathKit.LostDuring(20))));

        Assert.Equal("Several devices are losing packets but the router is fine: check the switch they share and their cables.", Verdict(view));
    }

    [Fact]
    public void One_device_losing_packets_names_that_device()
    {
        var view = Present(Snap(Router(PathKit.AllReply), Device("NAS", "192.168.86.44", PathKit.LostDuring(20)), Device("TV", "192.168.86.50", PathKit.AllReply)));

        Assert.Equal("NAS is losing packets while the router and the others are fine: check that device's cable and switch port.", Verdict(view));
    }

    [Fact]
    public void A_lossy_router_with_healthy_other_devices_is_not_a_cable_problem()
    {
        var view = Present(Snap(Router(PathKit.LostDuring(20)), Device("NAS", "192.168.86.44", PathKit.AllReply)));

        Assert.Equal("The router answers pings badly but your other devices are fine: the router may be de-prioritising pings. This does not point to a cable.", Verdict(view));
    }

    [Fact]
    public void A_lossy_router_alone_asks_for_another_device_to_compare_with()
    {
        var view = Present(Snap(Router(PathKit.LostDuring(20))));

        Assert.Equal("The router is losing packets. Add another device (like your NAS) to tell a PC-side cable problem from a router problem.", Verdict(view));
    }

    [Fact]
    public void A_device_that_never_answers_pings_is_grey_and_judged_neither_way()
    {
        var view = Present(Snap(Router(PathKit.AllReply), Device("Printer", "192.168.86.60", PathKit.AllLost)), InternetState.Fine);

        var printer = view.Rows.Single(r => r.Name == "Printer");
        Assert.Equal(LocalState.NoPing, printer.State);
        Assert.Equal("Doesn't answer ping", printer.StateLabel);
        Assert.Equal("Your local network is healthy.", Verdict(view));
    }

    [Fact]
    public void With_nothing_measurable_the_verdict_asks_for_a_device()
    {
        var view = Present(Snap(Router(PathKit.AllUnavailable), Device("Printer", "192.168.86.60", PathKit.AllLost)));

        Assert.Equal("No device on your local network can be measured yet. Add your NAS or another device that answers pings.", Verdict(view));
    }

    [Fact]
    public void A_name_that_was_not_found_is_listed_and_ignored_by_the_verdict()
    {
        var view = Present(Snap(Router(PathKit.AllReply), Device("Ghost", "ghost.local", PathKit.AllReply, notFound: true)), InternetState.Fine);

        Assert.Equal(LocalState.NotFound, view.Rows.Single(r => r.Name == "Ghost").State);
        Assert.Equal("Your local network is healthy.", Verdict(view));
    }

    [Fact]
    public void A_lossy_device_on_another_subnet_is_listed_but_ignored_by_the_verdict()
    {
        var view = Present(Snap(Router(PathKit.AllReply), Device("Office", "10.0.0.5", PathKit.LostDuring(20))), InternetState.Fine);

        var office = view.Rows.Single(r => r.Name == "Office");
        Assert.False(office.OnSubnet);
        Assert.Equal(LocalState.Down, office.State);
        Assert.Equal("Your local network is healthy.", Verdict(view));
    }

    [Fact]
    public void Rows_show_state_latency_and_loss_with_the_router_first()
    {
        var view = Present(Snap(Router(PathKit.AllReply, rtt: 1), Device("NAS", "192.168.86.44", PathKit.LostDuring(25), rtt: 3)));

        Assert.Equal(["Router", "NAS"], view.Rows.Select(r => r.Name));
        Assert.Equal((LocalState.Answering, "Answering", "1 ms · 0%"), (view.Rows[0].State, view.Rows[0].StateLabel, view.Rows[0].Metric));
        Assert.Equal(LocalState.Down, view.Rows[1].State);
        Assert.Equal("No reply", view.Rows[1].StateLabel);
        Assert.True(view.Rows[0].IsRouter);
        Assert.True(view.Rows[0].OnSubnet);
    }

    [Fact]
    public void A_partly_lossy_device_is_losing_packets()
    {
        // 3 of every 10 pings lost, but never 3 in a row: lossy, not silent.
        var view = Present(Snap(Router(PathKit.AllReply), Device("NAS", "192.168.86.44", sec => sec % 10 is 2 or 5 or 8 ? ProbeOutcome.NoReply : ProbeOutcome.Reply)));

        var nas = view.Rows.Single(r => r.Name == "NAS");
        Assert.Equal(LocalState.Lossy, nas.State);
        Assert.Equal("Losing packets", nas.StateLabel);
    }

    [Fact]
    public void Without_a_lan_adapter_the_tab_says_so()
    {
        var view = Present(new LocalSnapshot(PathKit.Now, null, null, []));

        Assert.False(view.Connected);
        Assert.Equal("No LAN connection: plug in the LAN cable to test your local network.", view.Message);
        Assert.Empty(view.Rows);
        Assert.Empty(view.Verdict);
    }

    [Fact]
    public void A_router_that_is_missing_means_no_verdict_can_be_given()
    {
        var view = Present(Snap(Device("NAS", "192.168.86.44", PathKit.AllReply)));

        Assert.Equal("No router was found for the LAN connection, so the local network cannot be judged.", Verdict(view));
    }

    [Theory]
    [InlineData(1_000_000_000L, "1 Gbps", false)]
    [InlineData(2_500_000_000L, "2.5 Gbps", false)]
    [InlineData(100_000_000L, "100 Mbps", true)]
    [InlineData(10_000_000L, "10 Mbps", true)]
    public void An_ethernet_link_below_one_gigabit_is_a_warning(long bps, string text, bool warning)
    {
        var view = Present(Snap(Router(PathKit.AllReply)), nic: new NicStats("Ethernet", bps, true, 0, 0, 0, 0));

        Assert.Equal(text, view.Nic!.SpeedText);
        Assert.Equal(warning, view.Nic.SpeedWarning);
        Assert.Equal(warning, view.Nic.SpeedNote != "");
    }

    [Fact]
    public void A_wireless_or_unknown_speed_is_never_a_warning()
    {
        var wifi = Present(Snap(Router(PathKit.AllReply)), nic: new NicStats("Wi-Fi", 54_000_000, false, 0, 0, 0, 0));
        var unknown = Present(Snap(Router(PathKit.AllReply)), nic: new NicStats("Ethernet", null, true, 0, 0, 0, 0));

        Assert.False(wifi.Nic!.SpeedWarning);
        Assert.False(unknown.Nic!.SpeedWarning);
        Assert.Equal("unknown", unknown.Nic.SpeedText);
    }

    [Fact]
    public void Errors_are_the_change_since_the_tab_was_opened()
    {
        var baseline = new NicStats("Ethernet", 1_000_000_000, true, InErrors: 5, InDiscards: 2, OutErrors: 0, OutDiscards: 0);
        var calm = Present(Snap(Router(PathKit.AllReply)), nic: baseline, baseline: baseline);
        var growing = Present(Snap(Router(PathKit.AllReply)), baseline: baseline,
            nic: baseline with { InErrors = 9, InDiscards = 3, OutErrors = 1 });

        Assert.Equal("0 errors and 0 discarded packets since you opened this tab", calm.Nic!.ErrorsText);
        Assert.False(calm.Nic.ErrorsWarning);
        Assert.Equal("5 errors and 1 discarded packets since you opened this tab", growing.Nic!.ErrorsText);
        Assert.True(growing.Nic.ErrorsWarning);
    }

    [Fact]
    public void Without_nic_statistics_there_is_no_card()
    {
        Assert.Null(Present(Snap(Router(PathKit.AllReply))).Nic);
    }

    [Fact]
    public void A_selected_row_gets_a_detail_with_stats_the_series_and_a_note()
    {
        var view = Present(Snap(Router(PathKit.AllReply), Device("NAS", "192.168.86.44", PathKit.LostDuring(25), rtt: 3)),
            selected: LocalNetworkMonitor.KeyOf("192.168.86.44"));

        var detail = view.Detail!;
        Assert.Equal("NAS", detail.Title);
        Assert.Equal("192.168.86.44", detail.Address);
        Assert.Equal(HopDisplay.Broken, detail.State);
        Assert.Equal(30, detail.Series.Count);
        Assert.Contains(detail.Stats, s => s.Label == "Packets lost");
        Assert.Contains("answered earlier", detail.Note);
    }

    [Fact]
    public void A_selection_that_no_longer_exists_has_no_detail()
    {
        Assert.Null(Present(Snap(Router(PathKit.AllReply)), selected: "dev:gone").Detail);
    }

    [Theory]
    [InlineData(HopDisplay.Answering, InternetState.Fine)]
    [InlineData(HopDisplay.Suspect, InternetState.Failing)]
    [InlineData(HopDisplay.Broken, InternetState.Failing)]
    [InlineData(HopDisplay.Silent, InternetState.Failing)]
    [InlineData(HopDisplay.Measuring, InternetState.Unknown)]
    [InlineData(HopDisplay.Unavailable, InternetState.Unknown)]
    public void The_path_maps_internet_row_becomes_an_internet_state(HopDisplay display, InternetState expected)
    {
        Assert.Equal(expected, LocalNetworkPresenter.InternetOf(display));
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~LocalNetworkPresenterTests"`
Expected: build FAIL (`LocalNetworkPresenter`, `NicStats`, … do not exist).

- [ ] **Step 3: Implement**

`src/NetRoute.Core/PathMap/NetworkInfo.cs`:

```csharp
using System.Net;

namespace NetRoute.Core.PathMap;

/// A network card's link speed and error counters at one moment.
public sealed record NicStats(string Name, long? SpeedBps, bool IsEthernet, long InErrors, long InDiscards, long OutErrors, long OutDiscards);

/// What Windows knows about the LAN adapter, behind an interface so tests use fakes.
public interface INetworkInfo
{
    NicStats? ReadNic(string adapterName);

    /// The adapter's IPv4 default gateway (the router), or null.
    IPAddress? GatewayV4(string adapterName);

    /// The prefix length of the adapter's IPv4 address (24 for a /24), or null.
    int? PrefixLengthV4(string adapterName, IPAddress address);
}
```

In `src/NetRoute.Core/PathMap/PathMapPresenter.cs` change `static IReadOnlyList<(string, string)> StatsOf(` to `internal static IReadOnlyList<(string, string)> StatsOf(`.

`src/NetRoute.Core/PathMap/LocalNetworkPresenter.cs`:

```csharp
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace NetRoute.Core.PathMap;

/// What a device in the local list is doing. NoPing: it never answered in the window (off, unplugged, or it blocks pings): grey, and
/// judged neither way. Down: it answered earlier in the window but not now.
public enum LocalState { Measuring, Answering, Lossy, Down, NoPing, NotFound, Unavailable }

public enum InternetState { Unknown, Fine, Failing }

public sealed record LocalDeviceRow(string Key, string Name, string Address, LocalState State, string StateLabel, string Metric, bool IsRouter, bool OnSubnet);

public sealed record NicView(string Name, string SpeedText, bool SpeedWarning, string SpeedNote, string ErrorsText, bool ErrorsWarning);

public sealed record LocalNetworkView(
    bool Connected, string Message, IReadOnlyList<string> Verdict, bool VerdictWarning, NicView? Nic,
    IReadOnlyList<LocalDeviceRow> Rows, DetailView? Detail);

/// Pure mapping from a monitor snapshot (plus the card's statistics and the path map's internet state) to everything the tab shows.
public static class LocalNetworkPresenter
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    const long Gigabit = 1_000_000_000;

    public static LocalNetworkView Present(
        LocalSnapshot snap, NicStats? nic, NicStats? nicBaseline, InternetState internet, string? selectedKey, PathThresholds t, int? prefixLength)
    {
        if (snap.Lan is null || snap.Source is null)
            return new LocalNetworkView(false, "No LAN connection: plug in the LAN cable to test your local network.", [], false, null, [], null);

        var devices = snap.Devices.Select(d => (Device: d, Stats: SampleStats.Of(d.Samples), State: StateOf(d, t))).ToList();
        var rows = devices.Select(x => new LocalDeviceRow(
            x.Device.Key, x.Device.Name, x.Device.Address, x.State, LabelOf(x.State), MetricOf(x.Stats, x.State),
            x.Device.IsRouter, x.Device.IsRouter || OnSubnet(snap.Source, x.Device.Resolved, prefixLength))).ToList();

        var verdict = LocalVerdict.Of(
            rows.Zip(devices, (row, x) => new LocalVerdict.Entry(row.Name, row.State, row.IsRouter, row.OnSubnet, x.Stats.Valid)).ToList(), internet, t);

        var detail = selectedKey is null ? null : devices.Where(x => x.Device.Key == selectedKey).Select(x => DetailOf(x.Device, x.Stats, x.State, t)).FirstOrDefault();
        return new LocalNetworkView(true, "", verdict.Lines, verdict.Warning, nic is null ? null : NicOf(nic, nicBaseline), rows, detail);
    }

    /// The path map's internet row for the LAN lane, as the verdict needs it.
    public static InternetState InternetOf(HopDisplay state) => state switch
    {
        HopDisplay.Answering => InternetState.Fine,
        HopDisplay.Suspect or HopDisplay.Broken or HopDisplay.Silent => InternetState.Failing,
        _ => InternetState.Unknown,
    };

    /// The colour family of a state in the path map (the tab draws its dots and chip with the same brushes).
    public static HopDisplay AsHop(LocalState state) => state switch
    {
        LocalState.Measuring => HopDisplay.Measuring,
        LocalState.Answering => HopDisplay.Answering,
        LocalState.Lossy => HopDisplay.Suspect,
        LocalState.Down => HopDisplay.Broken,
        LocalState.NoPing => HopDisplay.Quiet,
        _ => HopDisplay.Unavailable,
    };

    static LocalState StateOf(DeviceSnapshot d, PathThresholds t)
    {
        if (d.NameNotFound) return LocalState.NotFound;
        var s = SampleStats.Of(d.Samples);
        return PathObserver.Observe(s, hidden: false, endpoint: false, t) switch
        {
            Observation.Answering => LocalState.Answering,
            Observation.Lossy => LocalState.Lossy,
            Observation.Silent => s.Replies == 0 ? LocalState.NoPing : LocalState.Down,
            Observation.Unavailable => LocalState.Unavailable,
            _ => LocalState.Measuring,
        };
    }

    static string LabelOf(LocalState state) => state switch
    {
        LocalState.Measuring => "Measuring",
        LocalState.Answering => "Answering",
        LocalState.Lossy => "Losing packets",
        LocalState.Down => "No reply",
        LocalState.NoPing => "Doesn't answer ping",
        LocalState.NotFound => "Name not found",
        _ => "Unavailable",
    };

    static string MetricOf(SampleStats s, LocalState state) => state switch
    {
        LocalState.NotFound or LocalState.Unavailable => "",
        _ when s.Valid == 0 => "",
        _ when s.Lost == s.Valid => "no reply",
        _ => $"{s.AvgMs ?? 0} ms · {s.LossPercent.ToString("0", Inv)}%",
    };

    /// A device whose address is unknown (a name still to resolve) is never held against the verdict, so it counts as on the subnet.
    static bool OnSubnet(IPAddress source, IPAddress? device, int? prefixLength)
    {
        if (device is null) return true;
        if (source.AddressFamily != AddressFamily.InterNetwork || device.AddressFamily != AddressFamily.InterNetwork) return false;
        var prefix = Math.Clamp(prefixLength ?? 24, 0, 32);
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        return (ToUInt(source) & mask) == (ToUInt(device) & mask);
    }

    static uint ToUInt(IPAddress address) => BitConverter.ToUInt32(address.GetAddressBytes().Reverse().ToArray(), 0);

    static DetailView DetailOf(DeviceSnapshot d, SampleStats stats, LocalState state, PathThresholds t) =>
        new(d.Key, d.Name, d.Address, LabelOf(state), AsHop(state), PathMapPresenter.StatsOf(stats, t), d.Samples, NoteOf(state, d.IsRouter));

    static string NoteOf(LocalState state, bool router) => state switch
    {
        LocalState.Measuring => "Not enough checks yet to judge this device.",
        LocalState.Answering => router ? "Your router, answering its pings normally." : "Answering its pings normally.",
        LocalState.Lossy => "It loses some of its pings. If other devices are fine, check this device's cable and switch port.",
        LocalState.Down => "It answered earlier in the last 30 s but not now.",
        LocalState.NoPing => "It did not answer any ping in the last 30 s. A device that is off or unplugged looks like this, and so does one that ignores pings.",
        LocalState.NotFound => "This name could not be found on your network.",
        _ => "The checks could not run on this PC.",
    };

    static NicView NicOf(NicStats nic, NicStats? baseline)
    {
        var speedWarning = nic.IsEthernet && nic.SpeedBps is > 0 and < Gigabit;
        var errors = Math.Max(0, nic.InErrors - (baseline?.InErrors ?? nic.InErrors)) + Math.Max(0, nic.OutErrors - (baseline?.OutErrors ?? nic.OutErrors));
        var discards = Math.Max(0, nic.InDiscards - (baseline?.InDiscards ?? nic.InDiscards)) + Math.Max(0, nic.OutDiscards - (baseline?.OutDiscards ?? nic.OutDiscards));
        return new NicView(
            nic.Name, SpeedText(nic.SpeedBps), speedWarning,
            speedWarning ? "Gigabit is normal on modern cables; 100 Mbps often means a damaged cable or an old device in the path." : "",
            $"{errors.ToString(Inv)} errors and {discards.ToString(Inv)} discarded packets since you opened this tab", errors + discards > 0);
    }

    static string SpeedText(long? bps) => bps switch
    {
        null or <= 0 => "unknown",
        >= Gigabit => $"{(bps.Value / (double)Gigabit).ToString("0.##", Inv)} Gbps",
        _ => $"{(bps.Value / 1_000_000).ToString(Inv)} Mbps",
    };
}

/// The plain-words conclusion of the Local network tab: first matching rule wins (see the v5.2 spec).
static class LocalVerdict
{
    internal sealed record Entry(string Name, LocalState State, bool IsRouter, bool OnSubnet, int Valid);

    internal sealed record Result(IReadOnlyList<string> Lines, bool Warning);

    internal static Result Of(IReadOnlyList<Entry> entries, InternetState internet, PathThresholds t)
    {
        static Result Say(bool warning, string line) => new([line], warning);

        var onLan = entries.Where(e => e.OnSubnet).ToList();
        var router = onLan.FirstOrDefault(e => e.IsRouter);
        if (router is null) return Say(false, "No router was found for the LAN connection, so the local network cannot be judged.");
        if (router.State == LocalState.Measuring) return Say(false, $"Measuring… {Math.Min(t.HopMinSamples, router.Valid)} of {t.HopMinSamples} s.");

        static bool Bad(Entry e) => e.State is LocalState.Lossy or LocalState.Down;
        var others = onLan.Where(e => !e.IsRouter).ToList();
        var badOthers = others.Where(Bad).ToList();

        if (Bad(router) && badOthers.Count > 0)
            return Say(true, "Several devices on your local network are losing packets. Check this PC's cable and its switch port, and the switch.");
        if (!Bad(router) && badOthers.Count >= 2)
            return Say(true, "Several devices are losing packets but the router is fine: check the switch they share and their cables.");
        if (!Bad(router) && badOthers.Count == 1)
            return Say(true, $"{badOthers[0].Name} is losing packets while the router and the others are fine: check that device's cable and switch port.");

        var healthy = onLan.Where(e => e.State == LocalState.Answering).ToList();
        if (Bad(router))
            return healthy.Count > 0
                ? Say(true, "The router answers pings badly but your other devices are fine: the router may be de-prioritising pings. This does not point to a cable.")
                : Say(true, "The router is losing packets. Add another device (like your NAS) to tell a PC-side cable problem from a router problem.");

        if (healthy.Count == 0)
            return Say(false, "No device on your local network can be measured yet. Add your NAS or another device that answers pings.");
        return internet == InternetState.Failing
            ? Say(true, $"Your local network is healthy: {Join(healthy.Select(e => e.Name).ToList())} answer normally. The fault is beyond your router (its link to the modem, the modem, or the line).")
            : Say(false, "Your local network is healthy.");
    }

    static string Join(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
}
```

- [ ] **Step 4: Run to verify they pass, then the whole suite**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~LocalNetworkPresenterTests"` then `dotnet test tests/NetRoute.Core.Tests`
Expected: all PASS. (`A_partly_lossy_device_is_losing_packets` loses 9 of 30 pings (30 %) and the last loss is at second 28, so only one reply follows it: Lossy.)

- [ ] **Step 5: Commit**

```bash
git add src/NetRoute.Core/PathMap/NetworkInfo.cs src/NetRoute.Core/PathMap/LocalNetworkPresenter.cs src/NetRoute.Core/PathMap/PathMapPresenter.cs tests/NetRoute.Core.Tests/LocalNetworkPresenterTests.cs
git commit -m "feat(core): LocalNetworkPresenter - rows, NIC card, plain-words verdict

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 4: `NetworkScanner`

**Files:**
- Create: `src/NetRoute.Core/PathMap/NetworkScanner.cs`
- Test: `tests/NetRoute.Core.Tests/NetworkScannerTests.cs`

**Interfaces:**
- Consumes: `IPathProbe`, `EchoResult`, `ProbeOutcome` (existing).
- Produces:
  - `sealed record ScanResult(IPAddress Address, string? Mac, string? Name, bool AnsweredPing)`
  - `interface INeighborTable { IReadOnlyList<(IPAddress Address, string Mac)> Read(); }`
  - `sealed class NetworkScanner(IPathProbe probe, INeighborTable neighbors, Func<IPAddress, CancellationToken, Task<string?>> reverseName)` with `const int MaxHosts = 254`, `static IReadOnlyList<IPAddress> HostsOf(IPAddress address, int prefixLength)`, `Task<IReadOnlyList<ScanResult>> ScanAsync(IPAddress source, int prefixLength, IProgress<int>? progress, CancellationToken ct)` (throws `OperationCanceledException` when cancelled)

- [ ] **Step 1: Write the failing tests**

`tests/NetRoute.Core.Tests/NetworkScannerTests.cs`:

```csharp
using System.Net;
using NetRoute.Core.PathMap;

namespace NetRoute.Core.Tests;

public class NetworkScannerTests
{
    static IPAddress Ip(string s) => IPAddress.Parse(s);
    static readonly IPAddress Source = Ip("192.168.86.42");

    sealed class FakeSweepProbe : IPathProbe
    {
        public HashSet<IPAddress> Answering { get; } = new();
        public List<(IPAddress Source, IPAddress Target, TimeSpan Timeout)> Sent { get; } = new();
        public int MaxInFlight { get; private set; }
        int _inFlight;

        public async Task<EchoResult> EchoAsync(IPAddress source, IPAddress target, int ttl, TimeSpan timeout, CancellationToken ct)
        {
            lock (Sent)
            {
                Sent.Add((source, target, timeout));
                MaxInFlight = Math.Max(MaxInFlight, ++_inFlight);
            }
            await Task.Yield();
            lock (Sent) _inFlight--;
            return Answering.Contains(target) ? new EchoResult(ProbeOutcome.Reply, target, 1, TargetReached: true) : new EchoResult(ProbeOutcome.NoReply);
        }
    }

    sealed class FakeNeighbors(params (string Address, string Mac)[] entries) : INeighborTable
    {
        public IReadOnlyList<(IPAddress Address, string Mac)> Read() => entries.Select(e => (Ip(e.Address), e.Mac)).ToList();
    }

    static NetworkScanner Scanner(FakeSweepProbe probe, INeighborTable? neighbors = null, Func<IPAddress, CancellationToken, Task<string?>>? names = null) =>
        new(probe, neighbors ?? new FakeNeighbors(), names ?? ((_, _) => Task.FromResult<string?>(null)));

    [Fact]
    public void A_slash_24_has_every_host_but_the_network_the_broadcast_and_this_pc()
    {
        var hosts = NetworkScanner.HostsOf(Source, 24);

        Assert.Equal(253, hosts.Count);
        Assert.Equal(Ip("192.168.86.1"), hosts[0]);
        Assert.Equal(Ip("192.168.86.254"), hosts[^1]);
        Assert.DoesNotContain(Source, hosts);
        Assert.DoesNotContain(Ip("192.168.86.0"), hosts);
        Assert.DoesNotContain(Ip("192.168.86.255"), hosts);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(8)]
    [InlineData(0)]
    public void A_subnet_larger_than_a_slash_24_scans_the_slash_24_around_the_address(int prefix)
    {
        var hosts = NetworkScanner.HostsOf(Source, prefix);

        Assert.Equal(253, hosts.Count);
        Assert.All(hosts, h => Assert.StartsWith("192.168.86.", h.ToString()));
    }

    [Fact]
    public void A_slash_28_scans_its_own_fourteen_hosts()
    {
        var hosts = NetworkScanner.HostsOf(Ip("192.168.86.37"), 28); // 192.168.86.32 - .47

        Assert.Equal(13, hosts.Count); // 14 hosts minus this PC
        Assert.Equal(Ip("192.168.86.33"), hosts[0]);
        Assert.Equal(Ip("192.168.86.46"), hosts[^1]);
        Assert.DoesNotContain(Ip("192.168.86.37"), hosts);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(32)]
    public void A_subnet_with_no_room_for_hosts_scans_nothing(int prefix)
    {
        Assert.Empty(NetworkScanner.HostsOf(Source, prefix));
    }

    [Fact]
    public async Task Devices_that_answer_are_listed_in_address_order_with_their_names_and_macs()
    {
        var probe = new FakeSweepProbe();
        probe.Answering.Add(Ip("192.168.86.44"));
        probe.Answering.Add(Ip("192.168.86.1"));
        var neighbors = new FakeNeighbors(("192.168.86.1", "AA-BB-CC-00-00-01"), ("192.168.86.44", "AA-BB-CC-00-00-44"));
        var scanner = Scanner(probe, neighbors, (ip, _) => Task.FromResult<string?>(ip.ToString().EndsWith(".44") ? "wdmycloud" : null));

        var found = await scanner.ScanAsync(Source, 24, null, CancellationToken.None);

        Assert.Equal(
            [new ScanResult(Ip("192.168.86.1"), "AA-BB-CC-00-00-01", null, true), new ScanResult(Ip("192.168.86.44"), "AA-BB-CC-00-00-44", "wdmycloud", true)],
            found);
        Assert.Equal(253, probe.Sent.Count);
        Assert.All(probe.Sent, s => Assert.Equal((Source, NetworkScanner.PingTimeout), (s.Source, s.Timeout)));
    }

    [Fact]
    public async Task A_device_that_ignores_pings_but_is_in_the_neighbour_table_is_listed()
    {
        var neighbors = new FakeNeighbors(("192.168.86.60", "11-22-33-44-55-66"));

        var found = await Scanner(new FakeSweepProbe(), neighbors).ScanAsync(Source, 24, null, CancellationToken.None);

        Assert.Equal([new ScanResult(Ip("192.168.86.60"), "11-22-33-44-55-66", null, false)], found);
    }

    [Theory]
    [InlineData("00-00-00-00-00-00")]
    [InlineData("FF-FF-FF-FF-FF-FF")]
    [InlineData("")]
    public async Task Neighbour_entries_without_a_real_mac_are_ignored(string mac)
    {
        var found = await Scanner(new FakeSweepProbe(), new FakeNeighbors(("192.168.86.60", mac))).ScanAsync(Source, 24, null, CancellationToken.None);

        Assert.Empty(found);
    }

    [Fact]
    public async Task Neighbours_outside_the_scanned_subnet_and_this_pc_are_ignored()
    {
        var neighbors = new FakeNeighbors(("10.0.0.5", "11-22-33-44-55-66"), ("192.168.86.42", "AA-AA-AA-AA-AA-AA"), ("192.168.86.0", "AA-AA-AA-AA-AA-AB"));

        var found = await Scanner(new FakeSweepProbe(), neighbors).ScanAsync(Source, 24, null, CancellationToken.None);

        Assert.Empty(found);
    }

    [Fact]
    public async Task A_name_lookup_that_throws_leaves_the_name_empty()
    {
        var probe = new FakeSweepProbe();
        probe.Answering.Add(Ip("192.168.86.44"));
        var scanner = Scanner(probe, names: (_, _) => throw new InvalidOperationException("dns broke"));

        var found = await scanner.ScanAsync(Source, 24, null, CancellationToken.None);

        Assert.Null(Assert.Single(found).Name);
    }

    [Fact]
    public async Task The_sweep_never_has_more_than_the_allowed_number_of_pings_out_and_reports_progress()
    {
        var probe = new FakeSweepProbe();
        var reports = new List<int>();

        await Scanner(probe).ScanAsync(Source, 24, new SyncProgress(reports), CancellationToken.None);

        Assert.True(probe.MaxInFlight <= NetworkScanner.PingConcurrency, $"{probe.MaxInFlight} pings were out at once");
        Assert.Equal(253, reports.Max());
        Assert.Equal(253, reports.Count);
    }

    sealed class SyncProgress(List<int> reports) : IProgress<int>
    {
        public void Report(int value) { lock (reports) reports.Add(value); }
    }

    [Fact]
    public async Task A_cancelled_scan_throws_cancellation_and_does_not_return_a_partial_list()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scanner(new FakeSweepProbe()).ScanAsync(Source, 24, null, cts.Token));
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~NetworkScannerTests"`
Expected: build FAIL (`NetworkScanner`, `ScanResult`, `INeighborTable` do not exist).

- [ ] **Step 3: Implement**

`src/NetRoute.Core/PathMap/NetworkScanner.cs`:

```csharp
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace NetRoute.Core.PathMap;

public sealed record ScanResult(IPAddress Address, string? Mac, string? Name, bool AnsweredPing);

/// The PC's IPv4 neighbour (ARP) table: addresses it has talked to recently, with their MACs.
public interface INeighborTable
{
    IReadOnlyList<(IPAddress Address, string Mac)> Read();
}

/// Finds the devices on the LAN adapter's own subnet: a ping sweep plus the neighbour table (which also lists devices that do not
/// answer pings), then a best-effort name for each. Never more than MaxHosts addresses; throws OperationCanceledException when cancelled.
public sealed class NetworkScanner(IPathProbe probe, INeighborTable neighbors, Func<IPAddress, CancellationToken, Task<string?>> reverseName)
{
    public const int MaxHosts = 254;
    public const int PingConcurrency = 64;
    public const int NameConcurrency = 8;
    public static readonly TimeSpan PingTimeout = TimeSpan.FromMilliseconds(500);
    const int Ttl = 64;

    /// The host addresses to scan: the subnet of the address (a subnet larger than a /24 is cut down to the /24 around the address),
    /// without the network and broadcast addresses and without this PC. Empty for a /31 or /32.
    public static IReadOnlyList<IPAddress> HostsOf(IPAddress address, int prefixLength)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || prefixLength >= 31) return [];
        var prefix = Math.Clamp(prefixLength, 24, 30);
        var self = ToUInt(address);
        var mask = uint.MaxValue << (32 - prefix);
        var network = self & mask;
        var broadcast = network | ~mask;
        var hosts = new List<IPAddress>();
        for (var ip = network + 1; ip < broadcast; ip++)
            if (ip != self) hosts.Add(FromUInt(ip));
        return hosts;
    }

    public async Task<IReadOnlyList<ScanResult>> ScanAsync(IPAddress source, int prefixLength, IProgress<int>? progress, CancellationToken ct)
    {
        var hosts = HostsOf(source, prefixLength);
        var answered = new ConcurrentDictionary<IPAddress, bool>();
        var done = 0;
        using (var gate = new SemaphoreSlim(PingConcurrency))
        {
            await Task.WhenAll(hosts.Select(async host =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var reply = await probe.EchoAsync(source, host, Ttl, PingTimeout, ct);
                    if (reply.Outcome == ProbeOutcome.Reply && reply.TargetReached) answered[host] = true;
                }
                finally
                {
                    gate.Release();
                    progress?.Report(Interlocked.Increment(ref done));
                }
            }));
        }
        ct.ThrowIfCancellationRequested();

        var wanted = hosts.ToHashSet();
        var macs = new Dictionary<IPAddress, string>();
        foreach (var (address, mac) in neighbors.Read())
            if (wanted.Contains(address) && IsRealMac(mac)) macs[address] = mac.ToUpperInvariant();

        var found = hosts.Where(h => answered.ContainsKey(h) || macs.ContainsKey(h)).ToList(); // hosts is in address order
        var names = new string?[found.Count];
        using (var gate = new SemaphoreSlim(NameConcurrency))
        {
            await Task.WhenAll(found.Select(async (host, i) =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    names[i] = await reverseName(host, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    names[i] = null; // a name is a convenience: never fail the scan for it
                }
                finally { gate.Release(); }
            }));
        }
        ct.ThrowIfCancellationRequested();

        return found.Select((host, i) => new ScanResult(host, macs.GetValueOrDefault(host), names[i], answered.ContainsKey(host))).ToList();
    }

    static bool IsRealMac(string mac)
    {
        var digits = mac.Where(Uri.IsHexDigit).ToArray();
        return digits.Length == 12 && digits.Any(c => c != '0') && digits.Any(c => c is not ('f' or 'F'));
    }

    static uint ToUInt(IPAddress address) => BitConverter.ToUInt32(address.GetAddressBytes().Reverse().ToArray(), 0);

    static IPAddress FromUInt(uint value) => new(BitConverter.GetBytes(value).Reverse().ToArray());
}
```

- [ ] **Step 4: Run to verify they pass, then the whole suite**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~NetworkScannerTests"` then `dotnet test tests/NetRoute.Core.Tests`
Expected: all PASS. (The progress test uses its own `SyncProgress` so reports are collected synchronously instead of through `Progress<T>`'s dispatch.)

- [ ] **Step 5: Commit**

```bash
git add src/NetRoute.Core/PathMap/NetworkScanner.cs tests/NetRoute.Core.Tests/NetworkScannerTests.cs
git commit -m "feat(core): NetworkScanner - ping sweep plus neighbour table, bounded to 254 hosts

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Windows implementations (network info, neighbour table, name lookups)

**Files:**
- Create: `src/NetRoute.Core/Windows/WindowsNetworkInfo.cs`
- Create: `src/NetRoute.Core/Windows/NeighborTable.cs`
- Create: `src/NetRoute.Core/Windows/HostResolver.cs`
- Test: `tests/NetRoute.Core.Tests/NeighborTableTests.cs`
- Modify: `tests/NetRoute.Core.Tests/WindowsIntegrationTests.cs` (two read-only tests)

**Interfaces:**
- Consumes: `INetworkInfo`, `NicStats` (Task 3), `INeighborTable` (Task 4).
- Produces: `WindowsNetworkInfo : INetworkInfo`; `NeighborTable : INeighborTable` with `internal static IReadOnlyList<(IPAddress, string)> Parse(byte[] buffer)`; `static class HostResolver { Task<IReadOnlyList<IPAddress>> ResolveIPv4Async(string host); Task<string?> ReverseAsync(IPAddress address, CancellationToken ct) }`.

- [ ] **Step 1: Write the failing tests**

`tests/NetRoute.Core.Tests/NeighborTableTests.cs`:

```csharp
using System.Net;
using System.Runtime.InteropServices;
using NetRoute.Core.Windows;

namespace NetRoute.Core.Tests;

public class NeighborTableTests
{
    // MIB_IPNETTABLE: a DWORD count, then MIB_IPNETROW rows (24 bytes: index, physical address length, 8 bytes of address, IPv4 address, type).
    static byte[] Table(params (uint Index, byte[] Mac, string Ip, uint Type)[] rows)
    {
        var bytes = new List<byte>(BitConverter.GetBytes((uint)rows.Length));
        foreach (var (index, mac, ip, type) in rows)
        {
            bytes.AddRange(BitConverter.GetBytes(index));
            bytes.AddRange(BitConverter.GetBytes((uint)mac.Length));
            bytes.AddRange(mac.Concat(new byte[8]).Take(8));
            bytes.AddRange(IPAddress.Parse(ip).GetAddressBytes());
            bytes.AddRange(BitConverter.GetBytes(type));
        }
        return [.. bytes];
    }

    [Fact]
    public void The_native_row_is_24_bytes_like_the_layout_the_parser_reads()
    {
        Assert.Equal(24, Marshal.SizeOf<NeighborTable.MIB_IPNETROW>());
    }

    [Fact]
    public void Rows_become_address_and_mac_pairs()
    {
        var table = Table(
            (10, [0xAA, 0xBB, 0xCC, 0x00, 0x00, 0x44], "192.168.86.44", 3),
            (10, [0xAA, 0xBB, 0xCC, 0x00, 0x00, 0x01], "192.168.86.1", 4));

        var rows = NeighborTable.Parse(table);

        Assert.Equal(2, rows.Count);
        Assert.Equal((IPAddress.Parse("192.168.86.44"), "AA-BB-CC-00-00-44"), (rows[0].Address, rows[0].Mac));
        Assert.Equal((IPAddress.Parse("192.168.86.1"), "AA-BB-CC-00-00-01"), (rows[1].Address, rows[1].Mac));
    }

    [Fact]
    public void Invalid_entries_and_rows_without_a_six_byte_address_are_skipped()
    {
        var table = Table(
            (10, [1, 2, 3, 4, 5, 6], "192.168.86.10", 2),   // invalid
            (10, [1, 2, 3, 4], "192.168.86.11", 3),         // not a MAC
            (10, [], "192.168.86.12", 3),                   // incomplete (no address yet)
            (10, [1, 2, 3, 4, 5, 6], "192.168.86.13", 3));

        var rows = NeighborTable.Parse(table);

        Assert.Equal((IPAddress.Parse("192.168.86.13"), "01-02-03-04-05-06"), (Assert.Single(rows).Address, rows[0].Mac));
    }

    [Fact]
    public void A_truncated_or_empty_buffer_gives_no_rows_instead_of_throwing()
    {
        Assert.Empty(NeighborTable.Parse([]));
        Assert.Empty(NeighborTable.Parse([1, 0, 0, 0, 9, 9])); // says one row, holds half of one
    }
}
```

In `tests/NetRoute.Core.Tests/WindowsIntegrationTests.cs` add inside the class (read-only checks, same style as the existing ones; they never change any setting):

```csharp
    [Fact]
    public void Network_info_reads_the_internet_adapter_speed_gateway_and_prefix()
    {
        // Smart routing's own TUN carries the default route while it runs and has no gateway: use a real adapter beside it.
        var adapter = new WindowsAdapterSource().GetAdapters()
            .First(a => a.Name != "NetRoute" && a.IsUp && a.HasGateway && a.IPv4 is not null);
        var info = new WindowsNetworkInfo();

        var nic = info.ReadNic(adapter.Name);

        Assert.NotNull(nic);
        Assert.Equal(adapter.Name, nic.Name);
        Assert.NotNull(info.GatewayV4(adapter.Name));
        Assert.InRange(info.PrefixLengthV4(adapter.Name, System.Net.IPAddress.Parse(adapter.IPv4!)) ?? 0, 1, 32);
    }

    [Fact]
    public void Neighbour_table_reads_without_throwing_and_every_entry_has_a_mac()
    {
        var entries = new NeighborTable().Read();

        Assert.All(entries, e => Assert.Matches("^[0-9A-F]{2}(-[0-9A-F]{2}){5}$", e.Mac));
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~NeighborTableTests|FullyQualifiedName~Network_info_reads|FullyQualifiedName~Neighbour_table_reads"`
Expected: build FAIL (`NeighborTable`, `WindowsNetworkInfo` do not exist).

- [ ] **Step 3: Implement**

`src/NetRoute.Core/Windows/NeighborTable.cs`:

```csharp
using System.Net;
using System.Runtime.InteropServices;
using NetRoute.Core.PathMap;

namespace NetRoute.Core.Windows;

/// Windows' IPv4 neighbour (ARP) table through GetIpNetTable: the addresses this PC has talked to recently and their MACs.
public sealed class NeighborTable : INeighborTable
{
    const int ErrorInsufficientBuffer = 122;
    const uint TypeInvalid = 2;
    const int RowSize = 24;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MIB_IPNETROW
    {
        public uint Index;
        public uint PhysAddrLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] PhysAddr;
        public uint Addr;
        public uint Type;
    }

    [DllImport("iphlpapi.dll")]
    static extern int GetIpNetTable(IntPtr table, ref int size, bool order);

    public IReadOnlyList<(IPAddress Address, string Mac)> Read()
    {
        // The table can grow between the size query and the read: ask again a few times.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var size = 0;
            if (GetIpNetTable(IntPtr.Zero, ref size, false) != ErrorInsufficientBuffer || size <= 0) return [];
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var result = GetIpNetTable(buffer, ref size, false);
                if (result == ErrorInsufficientBuffer) continue;
                if (result != 0) return [];
                var bytes = new byte[size];
                Marshal.Copy(buffer, bytes, 0, size);
                return Parse(bytes);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return [];
    }

    /// Pure, so a handcrafted buffer tests it. Skips invalid entries and rows whose physical address is not 6 bytes (incomplete entries).
    internal static IReadOnlyList<(IPAddress Address, string Mac)> Parse(byte[] buffer)
    {
        var rows = new List<(IPAddress, string)>();
        if (buffer.Length < 4) return rows;
        var count = BitConverter.ToUInt32(buffer, 0);
        for (var i = 0; i < count; i++)
        {
            var at = 4 + i * RowSize;
            if (at + RowSize > buffer.Length) break;
            var macLength = BitConverter.ToUInt32(buffer, at + 4);
            var type = BitConverter.ToUInt32(buffer, at + 20);
            if (type == TypeInvalid || macLength != 6) continue;
            var mac = string.Join("-", buffer.Skip(at + 8).Take(6).Select(b => b.ToString("X2")));
            rows.Add((new IPAddress(buffer.AsSpan(at + 16, 4)), mac));
        }
        return rows;
    }
}
```

`src/NetRoute.Core/Windows/WindowsNetworkInfo.cs`:

```csharp
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetRoute.Core.PathMap;

namespace NetRoute.Core.Windows;

public sealed class WindowsNetworkInfo : INetworkInfo
{
    public NicStats? ReadNic(string adapterName)
    {
        try
        {
            var nic = Find(adapterName);
            if (nic is null) return null;
            var stats = nic.GetIPStatistics();
            return new NicStats(
                nic.Name, nic.Speed > 0 ? nic.Speed : null, nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet,
                stats.IncomingPacketsWithErrors, stats.IncomingPacketsDiscarded, stats.OutgoingPacketsWithErrors, stats.OutgoingPacketsDiscarded);
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    public IPAddress? GatewayV4(string adapterName)
    {
        try
        {
            return Find(adapterName)?.GetIPProperties().GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    public int? PrefixLengthV4(string adapterName, IPAddress address)
    {
        try
        {
            return Find(adapterName)?.GetIPProperties().UnicastAddresses.FirstOrDefault(u => u.Address.Equals(address))?.PrefixLength;
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    static NetworkInterface? Find(string adapterName) =>
        NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => string.Equals(n.Name, adapterName, StringComparison.OrdinalIgnoreCase));
}
```

`src/NetRoute.Core/Windows/HostResolver.cs`:

```csharp
using System.Net;
using System.Net.Sockets;

namespace NetRoute.Core.Windows;

/// Name lookups with a short timeout: a home router's DNS can be slow, and a lookup must never hold the tab up.
public static class HostResolver
{
    static readonly TimeSpan ForwardTimeout = TimeSpan.FromSeconds(3);
    static readonly TimeSpan ReverseTimeout = TimeSpan.FromSeconds(1);

    /// The IPv4 addresses of a name; empty when it cannot be found.
    public static async Task<IReadOnlyList<IPAddress>> ResolveIPv4Async(string host)
    {
        try
        {
            using var cts = new CancellationTokenSource(ForwardTimeout);
            return await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cts.Token);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return [];
        }
    }

    /// The name of an address, or null when it has none (or the lookup took too long). Cancelling the caller's token throws.
    public static async Task<string?> ReverseAsync(IPAddress address, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ReverseTimeout);
            var entry = await Dns.GetHostEntryAsync(address.ToString(), cts.Token);
            return string.IsNullOrWhiteSpace(entry.HostName) || entry.HostName == address.ToString() ? null : entry.HostName;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return null;
        }
    }
}
```

- [ ] **Step 4: Run to verify they pass, then the whole suite**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~NeighborTableTests|FullyQualifiedName~Network_info_reads|FullyQualifiedName~Neighbour_table_reads"` then `dotnet test tests/NetRoute.Core.Tests`
Expected: all PASS. (The two integration tests read the real machine; if the first finds no adapter that is up with a gateway and an IPv4, that is the machine being offline, not a code failure: say so in the ledger.)

- [ ] **Step 5: Commit**

```bash
git add src/NetRoute.Core/Windows/WindowsNetworkInfo.cs src/NetRoute.Core/Windows/NeighborTable.cs src/NetRoute.Core/Windows/HostResolver.cs tests/NetRoute.Core.Tests/NeighborTableTests.cs tests/NetRoute.Core.Tests/WindowsIntegrationTests.cs
git commit -m "feat(core): Windows network info, neighbour table and name lookups for the Local network tab

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 6: The tab in the path window (view, services, wiring)

WPF code has no unit-test harness in this repo (every pure decision it uses is tested in Tasks 1–5). This task is verified by a clean build, the unchanged test suite, and the real-machine check in Task 8. Write the code exactly as below.

**Files:**
- Create: `src/NetRoute.App/SparkLine.cs`
- Create: `src/NetRoute.App/LocalNetworkTabServices.cs`
- Create: `src/NetRoute.App/LocalNetworkTab.xaml`
- Create: `src/NetRoute.App/LocalNetworkTab.xaml.cs`
- Modify: `src/NetRoute.App/PathMapWindow.xaml` (tab control), `src/NetRoute.App/PathMapWindow.xaml.cs` (constructor, timer, spark extraction)
- Modify: `src/NetRoute.App/App.xaml.cs` (`OpenPathMap`)

**Interfaces:**
- Consumes: `LocalNetworkView`, `LocalDeviceRow`, `NicView`, `LocalNetworkPresenter` (Task 3); `LocalNetworkMonitor` (Task 2); `LocalDevice` (Task 1); `NetworkScanner`, `ScanResult` (Task 4); `WindowsNetworkInfo`, `NeighborTable`, `HostResolver` (Task 5); existing `PathMapCanvas.StateBrush`, `Theme`.
- Produces:
  - `static class SparkLine { void Draw(Canvas spark, IReadOnlyList<Sample> series, Func<string, Brush> resource) }`
  - `sealed record LocalNetworkTabServices(Func<string?, LocalNetworkView> Present, Func<IReadOnlyList<LocalDevice>> GetDevices, Action<IReadOnlyList<LocalDevice>> SaveDevices, Func<IProgress<int>, CancellationToken, Task<IReadOnlyList<ScanResult>>> Scan, Action Start)`
  - `LocalNetworkTab` with `internal void Attach(LocalNetworkTabServices services)`, `internal void Refresh()`, and events `AddRequested`, `ScanRequested`, `RemoveRequested(LocalDeviceRow row)` (Task 7 subscribes the tab to its own events)
  - `PathMapWindow(Func<string?, PathMapView> present, LocalNetworkTabServices local)`

- [ ] **Step 1: Extract the spark drawing**

`src/NetRoute.App/SparkLine.cs` (the body is `PathMapWindow.DrawSpark` moved out unchanged, with the brush lookup passed in):

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using NetRoute.Core.PathMap;

namespace NetRoute.App;

/// The small latency graph under a device's details: latency over the window, a lost check as a red tick on the baseline, a check that
/// could not be measured as a grey one. Shared by the path map's and the Local network tab's detail panels.
static class SparkLine
{
    public static void Draw(Canvas spark, IReadOnlyList<Sample> series, Func<string, Brush> resource)
    {
        spark.Children.Clear();
        if (series.Count == 0) return;

        var values = series.Where(s => s.Outcome == ProbeOutcome.Reply && s.RttMs is not null).Select(s => s.RttMs!.Value).ToList();
        var max = Math.Max(1, values.Count == 0 ? 1 : values.Max()) * 1.15;
        var step = series.Count > 1 ? (spark.Width - 10) / (series.Count - 1) : 0;
        spark.Children.Add(new Line { X1 = 0, X2 = spark.Width, Y1 = 44, Y2 = 44, Stroke = resource("CardBorder"), StrokeThickness = 1 });

        var points = new PointCollection();
        void Flush()
        {
            if (points.Count > 1) spark.Children.Add(new Polyline { Points = points, Stroke = resource("Accent"), StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round });
            else if (points.Count == 1) spark.Children.Add(new Ellipse { Width = 3, Height = 3, Fill = resource("Accent"), Margin = new Thickness(points[0].X - 1.5, points[0].Y - 1.5, 0, 0) });
            points = new PointCollection();
        }

        for (var i = 0; i < series.Count; i++)
        {
            var x = 5 + i * step;
            switch (series[i].Outcome)
            {
                case ProbeOutcome.Reply:
                    points.Add(new Point(x, 44 - (series[i].RttMs ?? 0) / max * 38));
                    break;
                default:
                    Flush();
                    spark.Children.Add(new Line
                    {
                        X1 = x, X2 = x, Y1 = 46, Y2 = 54, StrokeThickness = 2,
                        Stroke = resource(series[i].Outcome == ProbeOutcome.NoReply ? "DotRed" : "DotGray"),
                    });
                    break;
            }
        }
        Flush();
    }
}
```

In `src/NetRoute.App/PathMapWindow.xaml.cs` replace the body of `DrawSpark` so it delegates (keeps the call site `DrawSpark(detail.Series)` unchanged), and delete the now-unused usings if the compiler warns:

```csharp
    void DrawSpark(IReadOnlyList<Sample> series) =>
        SparkLine.Draw(Spark, series, key => (Brush)(TryFindResource(key) ?? Brushes.Gray));
```

- [ ] **Step 2: Create the services record**

`src/NetRoute.App/LocalNetworkTabServices.cs`:

```csharp
using NetRoute.Core.PathMap;

namespace NetRoute.App;

/// Everything the Local network tab needs from the app, so the control itself knows nothing about controllers or monitors.
// Present: the view for the selected device key (null = none); it also keeps the monitor on the current adapters and devices.
// Scan: scans the LAN adapter's subnet; progress reports the number of addresses done.
// Start: starts the monitor the first time the tab is shown.
sealed record LocalNetworkTabServices(
    Func<string?, LocalNetworkView> Present,
    Func<IReadOnlyList<LocalDevice>> GetDevices,
    Action<IReadOnlyList<LocalDevice>> SaveDevices,
    Func<IProgress<int>, CancellationToken, Task<IReadOnlyList<ScanResult>>> Scan,
    Action Start);
```

- [ ] **Step 3: Create the tab control**

`src/NetRoute.App/LocalNetworkTab.xaml`:

```xml
<UserControl x:Class="NetRoute.App.LocalNetworkTab"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <DockPanel Margin="4,16,4,4">
        <StackPanel DockPanel.Dock="Top">
            <TextBlock x:Name="Message" Visibility="Collapsed" TextWrapping="Wrap" Foreground="{DynamicResource Warning}" />
            <StackPanel x:Name="Verdict" />
        </StackPanel>

        <Border x:Name="NicCard" DockPanel.Dock="Top" Margin="0,12,0,0" Padding="14,10" CornerRadius="8" BorderThickness="1"
                Background="{DynamicResource CardBackground}" BorderBrush="{DynamicResource CardBorder}" Visibility="Collapsed">
            <StackPanel>
                <TextBlock x:Name="NicTitle" FontWeight="SemiBold" />
                <WrapPanel x:Name="NicStats" Margin="0,6,0,0" />
                <TextBlock x:Name="NicNote" Margin="0,6,0,0" TextWrapping="Wrap" Foreground="{DynamicResource TextSecondary}" Visibility="Collapsed" />
            </StackPanel>
        </Border>

        <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="0,12,0,0">
            <Button x:Name="AddButton" Content="Add device…" Padding="12,3" Click="OnAdd" />
            <Button x:Name="RemoveButton" Content="Remove" Padding="12,3" Margin="8,0,0,0" IsEnabled="False" Click="OnRemove" />
            <Button x:Name="ScanButton" Content="Scan network…" Padding="12,3" Margin="8,0,0,0" Click="OnScan" />
        </StackPanel>

        <Border DockPanel.Dock="Bottom" Margin="0,12,0,0" Padding="14,10" CornerRadius="8" BorderThickness="1"
                Background="{DynamicResource CardBackground}" BorderBrush="{DynamicResource CardBorder}">
            <StackPanel>
                <TextBlock x:Name="DetailHint" Text="Click a device to see its details." Foreground="{DynamicResource TextSecondary}" />
                <StackPanel x:Name="DetailBody" Visibility="Collapsed">
                    <DockPanel>
                        <Border x:Name="DetailChipBox" DockPanel.Dock="Right" BorderThickness="1" CornerRadius="10" Padding="9,1" VerticalAlignment="Top">
                            <TextBlock x:Name="DetailChip" FontSize="11.5" FontWeight="SemiBold" />
                        </Border>
                        <StackPanel Orientation="Horizontal">
                            <TextBlock x:Name="DetailTitle" FontSize="14" FontWeight="SemiBold" />
                            <TextBlock x:Name="DetailAddress" Margin="10,2,0,0" FontFamily="Cascadia Mono, Consolas" FontSize="12" Foreground="{DynamicResource TextSecondary}" />
                        </StackPanel>
                    </DockPanel>
                    <WrapPanel x:Name="DetailStats" Margin="0,8,0,0" />
                    <Canvas x:Name="Spark" Width="320" Height="56" HorizontalAlignment="Left" Margin="0,8,0,0" />
                    <TextBlock x:Name="DetailNote" Margin="0,6,0,0" TextWrapping="Wrap" Foreground="{DynamicResource TextSecondary}" />
                </StackPanel>
            </StackPanel>
        </Border>

        <Border Margin="0,12,0,0" Padding="8,4" CornerRadius="8" BorderThickness="1"
                Background="{DynamicResource CardBackground}" BorderBrush="{DynamicResource CardBorder}">
            <ScrollViewer VerticalScrollBarVisibility="Auto">
                <StackPanel x:Name="Rows" />
            </ScrollViewer>
        </Border>
    </DockPanel>
</UserControl>
```

`src/NetRoute.App/LocalNetworkTab.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NetRoute.Core.PathMap;

namespace NetRoute.App;

/// The "Local network" tab: a plain-words verdict, this PC's network card, the device list and a detail panel. It owns only the screen:
/// the window's timer calls Refresh, and every number comes from the services' view.
public partial class LocalNetworkTab : UserControl
{
    LocalNetworkTabServices? _services;
    string? _selected;
    LocalNetworkView? _view;
    string _verdictKey = "", _rowsKey = "";

    public LocalNetworkTab()
    {
        InitializeComponent();
    }

    public event Action? AddRequested;
    public event Action? ScanRequested;
    public event Action<LocalDeviceRow>? RemoveRequested;

    internal void Attach(LocalNetworkTabServices services) => _services = services;

    internal void Refresh()
    {
        if (_services is null) return;
        LocalNetworkView view;
        try
        {
            view = _services.Present(_selected);
        }
        catch (Exception)
        {
            return; // keep the last view on screen; a throw out of a timer tick would repeat every 500 ms
        }
        if (_selected is not null && view.Detail is null) _selected = null;
        _view = view;
        Render(view);
    }

    void Render(LocalNetworkView view)
    {
        Message.Text = view.Message;
        Message.Visibility = view.Message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RenderVerdict(view);
        RenderNic(view.Nic);
        RenderRows(view.Rows);
        RenderDetail(view.Detail);
        var selectedRow = view.Rows.FirstOrDefault(r => r.Key == _selected);
        RemoveButton.IsEnabled = selectedRow is { IsRouter: false };
        AddButton.IsEnabled = ScanButton.IsEnabled = view.Connected;
    }

    void RenderVerdict(LocalNetworkView view)
    {
        var key = view.VerdictWarning + "|" + string.Join("\n", view.Verdict);
        if (key == _verdictKey) return;
        _verdictKey = key;
        Verdict.Children.Clear();
        foreach (var line in view.Verdict)
        {
            var text = new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) };
            text.SetResourceReference(TextBlock.ForegroundProperty, view.VerdictWarning ? "Warning" : "TextPrimary");
            Verdict.Children.Add(text);
        }
    }

    void RenderNic(NicView? nic)
    {
        NicCard.Visibility = nic is null ? Visibility.Collapsed : Visibility.Visible;
        if (nic is null) return;
        NicTitle.Text = "This PC's network card · " + nic.Name;
        NicStats.Children.Clear();
        AddStat(NicStats, "Link speed", nic.SpeedText, nic.SpeedWarning);
        AddStat(NicStats, "Errors", nic.ErrorsText, nic.ErrorsWarning);
        NicNote.Text = nic.SpeedNote;
        NicNote.Visibility = nic.SpeedNote.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    static void AddStat(Panel panel, string label, string value, bool warning)
    {
        var stat = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
        var caption = new TextBlock { Text = label, FontSize = 11.5 };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        var number = new TextBlock { Text = value, FontSize = 14, FontWeight = FontWeights.SemiBold };
        number.SetResourceReference(TextBlock.ForegroundProperty, warning ? "Warning" : "TextPrimary");
        stat.Children.Add(caption);
        stat.Children.Add(number);
        panel.Children.Add(stat);
    }

    void RenderRows(IReadOnlyList<LocalDeviceRow> rows)
    {
        var key = _selected + "||" + string.Join("\n", rows.Select(r => $"{r.Key}|{r.Name}|{r.Address}|{r.State}|{r.Metric}|{r.OnSubnet}"));
        if (key == _rowsKey) return;
        _rowsKey = key;
        Rows.Children.Clear();
        foreach (var row in rows)
        {
            var selected = row.Key == _selected;
            var grid = new Grid { Margin = new Thickness(6, 5, 6, 5), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dot = new Ellipse { Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            dot.SetResourceReference(Shape.FillProperty, PathMapCanvas.StateBrush(LocalNetworkPresenter.AsHop(row.State)));
            Grid.SetColumn(dot, 0);

            var title = new StackPanel { Orientation = Orientation.Horizontal };
            title.Children.Add(new TextBlock { Text = row.Name, FontWeight = selected ? FontWeights.Bold : FontWeights.SemiBold, Margin = new Thickness(0, 0, 10, 0) });
            var address = new TextBlock { Text = row.Address + (row.OnSubnet ? "" : " · other network"), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12 };
            address.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            title.Children.Add(address);
            Grid.SetColumn(title, 1);

            var state = new StackPanel { Orientation = Orientation.Horizontal };
            var label = new TextBlock { Text = row.StateLabel, Margin = new Thickness(0, 0, 14, 0) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            state.Children.Add(label);
            state.Children.Add(new TextBlock { Text = row.Metric, MinWidth = 90, TextAlignment = TextAlignment.Right });
            Grid.SetColumn(state, 2);

            grid.Children.Add(dot);
            grid.Children.Add(title);
            grid.Children.Add(state);
            var border = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = grid };
            border.SetResourceReference(Border.BorderBrushProperty, selected ? "Accent" : "CardBackground");
            var rowKey = row.Key;
            border.MouseLeftButtonUp += (_, _) =>
            {
                _selected = rowKey;
                Refresh();
            };
            Rows.Children.Add(border);
        }
    }

    void RenderDetail(DetailView? detail)
    {
        DetailHint.Visibility = detail is null ? Visibility.Visible : Visibility.Collapsed;
        DetailBody.Visibility = detail is null ? Visibility.Collapsed : Visibility.Visible;
        if (detail is null) return;

        DetailTitle.Text = detail.Title;
        DetailAddress.Text = detail.Address;
        DetailChip.Text = detail.Chip;
        var brush = PathMapCanvas.StateBrush(detail.State);
        DetailChip.SetResourceReference(TextBlock.ForegroundProperty, brush);
        DetailChipBox.SetResourceReference(Border.BorderBrushProperty, brush);

        DetailStats.Children.Clear();
        foreach (var (label, value) in detail.Stats) AddStat(DetailStats, label, value, warning: false);

        SparkLine.Draw(Spark, detail.Series, key => (Brush)(TryFindResource(key) ?? Brushes.Gray));
        Spark.Visibility = detail.Series.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        DetailNote.Text = detail.Note;
    }

    void OnAdd(object sender, RoutedEventArgs e) => AddRequested?.Invoke();

    void OnScan(object sender, RoutedEventArgs e) => ScanRequested?.Invoke();

    void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_view?.Rows.FirstOrDefault(r => r.Key == _selected) is { IsRouter: false } row) RemoveRequested?.Invoke(row);
    }
}
```

- [ ] **Step 4: Put the tab into the window**

Replace `src/NetRoute.App/PathMapWindow.xaml` with:

```xml
<Window x:Class="NetRoute.App.PathMapWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:local="clr-namespace:NetRoute.App"
        Icon="Assets/NetRouteWidget.ico"
        Title="NetRoute — Network path" Width="1020" Height="780" MinWidth="720" MinHeight="600"
        WindowStartupLocation="CenterScreen" FontFamily="Segoe UI" FontSize="13" ThemeMode="System"
        Background="{DynamicResource WindowBackground}" Foreground="{DynamicResource TextPrimary}">
    <TabControl x:Name="Tabs" Margin="8" SelectionChanged="OnTabChanged">
        <TabItem x:Name="InternetTab" Header="Internet path">
            <DockPanel Margin="16">
                <StackPanel DockPanel.Dock="Top">
                    <TextBlock x:Name="Headline" FontSize="16" FontWeight="SemiBold" TextWrapping="Wrap" />
                    <TextBlock x:Name="ReachabilityTitle" Margin="0,8,0,0" FontSize="12" Foreground="{DynamicResource TextSecondary}" TextWrapping="Wrap" />
                    <StackPanel x:Name="Diagnosis" Margin="0,2,0,0" />
                </StackPanel>

                <DockPanel DockPanel.Dock="Bottom" Margin="0,12,0,0">
                    <Button x:Name="PauseButton" DockPanel.Dock="Right" Content="Pause display" Padding="12,3" Click="OnPause"
                            ToolTip="Freezes the Internet path tab and its animation. The checks keep running in the background while the window is open." />
                    <TextBlock DockPanel.Dock="Right" x:Name="Footer" Margin="0,0,16,0" VerticalAlignment="Center" Foreground="{DynamicResource TextSecondary}" />
                    <TextBlock VerticalAlignment="Center" Foreground="{DynamicResource TextSecondary}" TextWrapping="Wrap">
                        <Run Foreground="{DynamicResource DotGreen}" Text="●" /> Answering
                        <Run Foreground="{DynamicResource DotAmber}" Text="●" /> Losing packets
                        <Run Foreground="{DynamicResource DotRed}" Text="●" /> No reply
                        <Run Foreground="{DynamicResource DotGray}" Text="●" /> Ignores probes, hidden or unavailable
                        <Run Foreground="{DynamicResource DotMeasuring}" Text="●" /> Measuring
                    </TextBlock>
                </DockPanel>

                <Border DockPanel.Dock="Bottom" Margin="0,12,0,0" Padding="14,10" CornerRadius="8" BorderThickness="1"
                        Background="{DynamicResource CardBackground}" BorderBrush="{DynamicResource CardBorder}">
                    <StackPanel>
                        <TextBlock x:Name="DetailHint" Text="Click a device on the map to see its details." Foreground="{DynamicResource TextSecondary}" />
                        <StackPanel x:Name="DetailBody" Visibility="Collapsed">
                            <DockPanel>
                                <Border x:Name="DetailChipBox" DockPanel.Dock="Right" BorderThickness="1" CornerRadius="10" Padding="9,1" VerticalAlignment="Top">
                                    <TextBlock x:Name="DetailChip" FontSize="11.5" FontWeight="SemiBold" />
                                </Border>
                                <StackPanel Orientation="Horizontal">
                                    <TextBlock x:Name="DetailTitle" FontSize="14" FontWeight="SemiBold" />
                                    <TextBlock x:Name="DetailAddress" Margin="10,2,0,0" FontFamily="Cascadia Mono, Consolas" FontSize="12" Foreground="{DynamicResource TextSecondary}" />
                                </StackPanel>
                            </DockPanel>
                            <WrapPanel x:Name="DetailStats" Margin="0,8,0,0" />
                            <Canvas x:Name="Spark" Width="320" Height="56" HorizontalAlignment="Left" Margin="0,8,0,0" />
                            <TextBlock x:Name="DetailNote" Margin="0,6,0,0" TextWrapping="Wrap" Foreground="{DynamicResource TextSecondary}" />
                        </StackPanel>
                    </StackPanel>
                </Border>

                <Border Margin="0,12,0,0" CornerRadius="8" BorderThickness="1" Background="{DynamicResource CardBackground}" BorderBrush="{DynamicResource CardBorder}">
                    <Viewbox Stretch="Uniform" Margin="6">
                        <local:PathMapCanvas x:Name="Map" />
                    </Viewbox>
                </Border>
            </DockPanel>
        </TabItem>
        <TabItem x:Name="LocalTabItem" Header="Local network">
            <local:LocalNetworkTab x:Name="LocalTab" />
        </TabItem>
    </TabControl>
</Window>
```

In `src/NetRoute.App/PathMapWindow.xaml.cs`:
- change the constructor to `public PathMapWindow(Func<string?, PathMapView> present, LocalNetworkTabServices local)`;
- store `readonly LocalNetworkTabServices _local;` and in the constructor body add `_local = local; LocalTab.Attach(local);`;
- in the timer tick, after the existing `if (!_paused) Refresh(); else { ... }` block, add `if (LocalTabItem.IsSelected) LocalTab.Refresh();`;
- add the handler:

```csharp
    bool _localStarted;

    void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != Tabs || !LocalTabItem.IsSelected) return; // ignore selection events bubbling up from inside a tab
        if (!_localStarted)
        {
            _localStarted = true;
            _local.Start(); // the checks start the first time the tab is shown and stop when the window closes
        }
        LocalTab.Refresh();
    }
```

- [ ] **Step 5: Wire the services in `OpenPathMap`**

In `src/NetRoute.App/App.xaml.cs`, inside `OpenPathMap`, after `var cts = new CancellationTokenSource();` and before `var window = _pathWindow = new PathMapWindow(...)`, add (and change the window construction to pass `localServices`):

```csharp
        var localMonitor = new LocalNetworkMonitor(new IcmpPathProbe(), HostResolver.ResolveIPv4Async, TimeProvider.System, PathThresholds.Default, message => _log?.Info(message));
        var netInfo = new WindowsNetworkInfo();
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
                var internet = LocalNetworkPresenter.InternetOf(
                    PathMapPresenter.Present(controller.Status, monitor.Snapshot(), null, PathThresholds.Default).Lan.Internet.State);
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
```

Change `new PathMapWindow(selected => {...})` to `new PathMapWindow(selected => {...}, localServices)` and delete the later duplicate `var token = cts.Token;` line (the one already present further down) so `token` is declared once. Add `using System.Net;` if the file lacks it.

- [ ] **Step 6: Build and run the suite**

Run: `dotnet build NetRouteWidget.slnx` then `dotnet test tests/NetRoute.Core.Tests`
Expected: `Build succeeded.` with 0 warnings, and the suite passes unchanged.

- [ ] **Step 7: Commit**

```bash
git add src/NetRoute.App
git commit -m "feat(app): Local network tab in the path window (verdict, network card, device list, detail)

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Add, remove and scan dialogs

**Files:**
- Create: `src/NetRoute.App/AddDeviceWindow.xaml`, `src/NetRoute.App/AddDeviceWindow.xaml.cs`
- Create: `src/NetRoute.App/ScanWindow.xaml`, `src/NetRoute.App/ScanWindow.xaml.cs`
- Modify: `src/NetRoute.App/LocalNetworkTab.xaml.cs` (handle the three events inside the tab)

**Interfaces:**
- Consumes: `LocalDeviceValidator`, `LocalDevice` (Task 1); `ScanResult` (Task 4); `LocalNetworkTabServices` (Task 6).
- Produces: `AddDeviceWindow(IReadOnlyList<LocalDevice> existing, string name = "", string address = "")` with `LocalDevice? Result`; `ScanWindow(Func<IProgress<int>, CancellationToken, Task<IReadOnlyList<ScanResult>>> scan, IReadOnlyList<LocalDevice> existing)` with `ScanResult? Result`.

- [ ] **Step 1: The add dialog**

`src/NetRoute.App/AddDeviceWindow.xaml` (same look as `AddRuleWindow`):

```xml
<Window x:Class="NetRoute.App.AddDeviceWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Icon="Assets/NetRouteWidget.ico"
        Title="Add device" Width="440" SizeToContent="Height" ResizeMode="NoResize"
        WindowStartupLocation="CenterOwner" FontFamily="Segoe UI" FontSize="13" ThemeMode="System"
        Background="{DynamicResource WindowBackground}" Foreground="{DynamicResource TextPrimary}">
    <StackPanel Margin="16">
        <TextBlock Text="Device to test on your local network, for example your NAS:" TextWrapping="Wrap" />
        <TextBlock Text="Address (IPv4, or a device name)" Margin="0,10,0,2" Foreground="{DynamicResource TextSecondary}" />
        <TextBox x:Name="AddressBox" Padding="6,4" />
        <TextBlock Text="Name (optional)" Margin="0,10,0,2" Foreground="{DynamicResource TextSecondary}" />
        <TextBox x:Name="NameBox" Padding="6,4" />
        <TextBlock x:Name="Error" Foreground="{DynamicResource Warning}" Margin="0,6,0,0" TextWrapping="Wrap" Visibility="Collapsed" />
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,16,0,0">
            <Button Content="Add" Width="80" IsDefault="True" Click="OnAdd" />
            <Button Content="Cancel" Width="80" Margin="8,0,0,0" IsCancel="True" />
        </StackPanel>
    </StackPanel>
</Window>
```

`src/NetRoute.App/AddDeviceWindow.xaml.cs`:

```csharp
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
```

- [ ] **Step 2: The scan dialog**

`src/NetRoute.App/ScanWindow.xaml`:

```xml
<Window x:Class="NetRoute.App.ScanWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Icon="Assets/NetRouteWidget.ico"
        Title="Scan network" Width="560" Height="440" MinWidth="460" MinHeight="320"
        WindowStartupLocation="CenterOwner" FontFamily="Segoe UI" FontSize="13" ThemeMode="System"
        Background="{DynamicResource WindowBackground}" Foreground="{DynamicResource TextPrimary}">
    <DockPanel Margin="16">
        <TextBlock x:Name="Status" DockPanel.Dock="Top" TextWrapping="Wrap" Text="Scanning your local network…" />
        <ProgressBar x:Name="Bar" DockPanel.Dock="Top" Height="6" Margin="0,8,0,0" Minimum="0" Maximum="1" />
        <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,12,0,0">
            <Button x:Name="UseButton" Content="Use selected" Width="110" IsEnabled="False" IsDefault="True" Click="OnUse" />
            <Button Content="Close" Width="80" Margin="8,0,0,0" IsCancel="True" />
        </StackPanel>
        <ListBox x:Name="List" Margin="0,12,0,0" SelectionChanged="OnSelect" MouseDoubleClick="OnUse" />
    </DockPanel>
</Window>
```

`src/NetRoute.App/ScanWindow.xaml.cs`:

```csharp
using System.Windows;
using NetRoute.Core.PathMap;

namespace NetRoute.App;

/// Scans the LAN adapter's subnet and lists what answered; the chosen device is returned for the add dialog. Closing the window
/// cancels a scan that is still running.
public partial class ScanWindow : Window
{
    readonly CancellationTokenSource _cts = new();
    readonly IReadOnlyList<LocalDevice> _existing;
    IReadOnlyList<ScanResult> _results = [];

    public ScanWindow(Func<IProgress<int>, CancellationToken, Task<IReadOnlyList<ScanResult>>> scan, IReadOnlyList<LocalDevice> existing)
    {
        InitializeComponent();
        Theme.Track(this);
        _existing = existing;
        Bar.Maximum = NetworkScanner.MaxHosts;
        Closed += (_, _) =>
        {
            _cts.Cancel();
            _cts.Dispose();
        };
        Loaded += async (_, _) => await RunAsync(scan);
    }

    public ScanResult? Result { get; private set; }

    async Task RunAsync(Func<IProgress<int>, CancellationToken, Task<IReadOnlyList<ScanResult>>> scan)
    {
        try
        {
            var progress = new Progress<int>(done => Bar.Value = done); // created on the UI thread: reports arrive there
            _results = await scan(progress, _cts.Token);
            Bar.Value = Bar.Maximum;
            List.ItemsSource = _results.Select(Describe).ToList();
            Status.Text = _results.Count == 0
                ? "No devices were found. Check that the LAN cable is connected."
                : $"{_results.Count} device(s) found. Pick one to add it.";
        }
        catch (OperationCanceledException)
        {
            // the window was closed
        }
        catch (Exception ex)
        {
            Status.Text = "Scan failed: " + ex.Message;
        }
    }

    string Describe(ScanResult r)
    {
        var added = _existing.Any(d => string.Equals(d.Address, r.Address.ToString(), StringComparison.OrdinalIgnoreCase)) ? "   (already in your list)" : "";
        var name = string.IsNullOrEmpty(r.Name) ? "" : $"   {r.Name}";
        var mac = r.Mac is null ? "" : $"   {r.Mac}";
        var quiet = r.AnsweredPing ? "" : "   (doesn't answer ping)";
        return $"{r.Address}{name}{mac}{quiet}{added}";
    }

    void OnSelect(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UseButton.IsEnabled = List.SelectedIndex >= 0;

    void OnUse(object sender, RoutedEventArgs e)
    {
        if (List.SelectedIndex < 0 || List.SelectedIndex >= _results.Count) return;
        Result = _results[List.SelectedIndex];
        DialogResult = true;
    }
}
```

- [ ] **Step 3: Handle the tab's three events inside the tab**

In the constructor of `src/NetRoute.App/LocalNetworkTab.xaml.cs` (after `InitializeComponent();`) subscribe the tab to its own events:

```csharp
        AddRequested += () => ShowAddDialog("", "");
        ScanRequested += ShowScanDialog;
        RemoveRequested += row =>
        {
            if (_services is null) return;
            _services.SaveDevices([.. _services.GetDevices().Where(d => !string.Equals(d.Address, row.Address, StringComparison.OrdinalIgnoreCase))]);
            _selected = null;
            Refresh();
        };
```

and add these methods to the class:

```csharp
    void ShowAddDialog(string name, string address)
    {
        if (_services is null) return;
        var dialog = new AddDeviceWindow(_services.GetDevices(), name, address) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Result is not { } device) return;
        _services.SaveDevices([.. _services.GetDevices(), device]);
        Refresh();
    }

    void ShowScanDialog()
    {
        if (_services is null) return;
        var dialog = new ScanWindow(_services.Scan, _services.GetDevices()) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Result is not { } found) return;
        ShowAddDialog(found.Name ?? "", found.Address.ToString()); // the add dialog lets the user edit the name before it is saved
    }
```

- [ ] **Step 4: Build and run the suite**

Run: `dotnet build NetRouteWidget.slnx` then `dotnet test tests/NetRoute.Core.Tests`
Expected: `Build succeeded.` (0 warnings) and `Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/NetRoute.App
git commit -m "feat(app): add, remove and scan dialogs for the Local network tab

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Docs, spec touch-ups, full verification, staged build

**Files:**
- Modify: `README.md`
- Modify: `docs/superpowers/specs/2026-10-14-netroute-v5-2-local-network-design.md`

- [ ] **Step 1: README**

Run `grep -n -i "path map\|Network path\|v5" README.md` and, in the path-map section, add this bullet after the existing "What it shows" bullet:

```markdown
- **Local network tab:** a second tab tests your own wired network apart from the internet path. It pings your router and any devices you add (a NAS, a printer, anything with an address or name; **Scan network…** lists what is on your subnet) once a second, shows this PC's network card (link speed, errors) and says in plain words whether the local network is healthy or whether the fault is beyond your router. Devices are saved in `settings.json`.
```

and in the version list at the end add: `- **v5.2:** A Local network tab in the path window: ping your router and chosen devices, scan the subnet, and tell a local cable or switch problem from a fault beyond the router. See [the v5.2 spec](docs/superpowers/specs/2026-10-14-netroute-v5-2-local-network-design.md). ✅`

- [ ] **Step 2: Spec touch-ups**

In the spec: set `**Status:** Approved; implemented by docs/superpowers/plans/2026-10-14-netroute-v5-2-local-network.md. The real-machine check is still to be run by the user.`; in "Scan" change `GetIpNetTable2` to `GetIpNetTable` (the IPv4 table, same data, a simpler layout); under "The tab" replace the sentence about Pause with: `The **Pause display** button stays on the Internet path tab and freezes that tab only; the Local network tab updates twice a second and needs no pause.`; under "Measuring" replace the hostname sentence with: `A hostname is resolved the first time it is probed and again after a minute (a name that failed is asked again after 10 s); a name that does not resolve gives state "Name not found" (Unavailable samples), never "no reply".`; under "Error handling" replace the first bullet with: `Probe failures are Unavailable samples, never exceptions (the IPathProbe contract). A round that throws is logged once per message and the next round runs; if the monitor task itself faults it is logged.`; in "Verdict" add after rule 6: `A device that never answers pings (grey, "doesn't answer ping") and a name that was not found count as neither healthy nor bad; with nothing healthy and nothing bad the verdict says no device can be measured yet.`

- [ ] **Step 3: Full verification**

Run: `dotnet build NetRouteWidget.slnx --no-incremental` then `dotnet test tests/NetRoute.Core.Tests`
Expected: `Build succeeded.` with 0 warnings, and `Failed: 0`.

- [ ] **Step 4: Commit**

```bash
git add README.md docs/superpowers/specs/2026-10-14-netroute-v5-2-local-network-design.md
git commit -m "docs(v5.2): README and spec touch-ups for the Local network tab

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

- [ ] **Step 5: Stage a Release build for the user's real-machine check**

Run `dotnet publish src/NetRoute.App -c Release -r win-x64 --self-contained false -o "<scratchpad>/NetRouteWidget-staged"` (the session scratchpad folder), confirm `NetRouteWidget.exe` is there, and give the user the admin install commands (stop the widget and sing-box, publish to `$env:ProgramFiles\NetRouteWidget`, start it). Ask the user to: open the path map (the ⋮ menu or the map button on the card), switch to **Local network**, and check that the Router row and (after **Add device…** with 192.168.86.44) the NAS show Answering with 0% loss and sub-millisecond latency, the card shows 1 Gbps and zero errors, **Scan network…** lists the NAS, and unplugging the NAS turns it red within a few seconds.
