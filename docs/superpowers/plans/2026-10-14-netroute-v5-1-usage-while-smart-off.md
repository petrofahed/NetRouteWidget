# Usage tracking while Smart routing is off — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep per-application Phone/LAN usage recording when Smart routing (sing-box) is not running, by counting packets with Windows kernel network tracing (ETW).

**Architecture:** A new `ITrafficSource` (Windows implementation `WindowsTrafficTracker`, an ETW session on `Microsoft-Windows-Kernel-Network`) aggregates bytes per (process, exit). `SmartRoutingController`, which already owns the `UsageCounter`, the gate and the 1 s usage poll, drains it into a new `UsageCounter.AddMeasured` while sing-box is not running, and discards the samples while it runs (no double counting). A pure `TrafficClassifier` maps a packet's two addresses to Phone/LAN/ignored.

**Tech Stack:** C# / .NET 10 (`net10.0-windows`), WPF, xUnit, `Microsoft.Diagnostics.Tracing.TraceEvent` 3.2.8 (MIT).

**Spec:** `docs/superpowers/specs/2026-10-14-netroute-v5-1-usage-while-smart-off-design.md` (read it first). Related: `docs/superpowers/specs/2026-10-02-netroute-v3-usage-and-routing-design.md`.

## Global Constraints

- Tests live in `tests/NetRoute.Core.Tests` (xUnit, `Assert`, no mocking library; fakes are hand-written in `SmartFakes.cs`). Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~<Name>"`; the whole suite must stay green (794 tests at the start of this plan).
- Core code is under `src/NetRoute.Core` (namespace `NetRoute.Core`; Windows-only code in `src/NetRoute.Core/Windows`). The App (`src/NetRoute.App`) holds only WPF code.
- Tracing counts **only** packets whose address is one of the Phone or LAN adapter's addresses (IPv4 and IPv6) and whose remote address is not loopback/private/link-local/multicast/broadcast. Everything else is ignored.
- While sing-box is running, tracker samples are **discarded** (never added).
- Row keys come from `UsageAttribution.Resolve(processName, host: null)`; process names are image file names such as `chrome.exe`.
- A start failure of the tracker never affects Smart routing or the widget: log once per distinct message, retry no more often than every 30 s.
- Comments follow the surrounding style: short, explain why. No new settings. User-visible banner text exactly: `Smart routing is off: usage is counted per application by Windows (no website rows).`
- Commit messages end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`. Work is on branch `feat/v5-1-heal-and-usage`.

## Review Focus

- **IPv6-only or dual-stack traffic** (the normal case on mobile and many home lines) must count: classification uses all of an adapter's addresses, not just `AdapterInfo.IPv4`. Pinned in Task 2.
- **Phone replugged / DHCP lease changed:** the tracker must follow the new addresses, not keep the old ones. Pinned in Task 4 (`SetAdapters` on every `ApplyAsync`).
- **Not running as administrator:** the trace cannot start; expect one log line, no crash, the old banner, no retry storm. Pinned in Task 4.
- **Smart switched on and off repeatedly, and `ApplyAsync` called every 5–10 s while Smart is off:** nothing is counted twice, and the Windows adapter baseline and live speed are not reset on every apply. Pinned in Task 4 (this needs a fix in `DropApi`).
- **Traffic to home devices and the phone's own network:** ignored. Pinned in Task 2.
- **Day rollover and idle decay of live speed while measuring.** Pinned in Task 3.
- Known limitation (documented, not tested): packets NATed for VMs/WSL carry no owning process and may be missing, so the per-app total can be below the adapter total.

---

### Task 0: Commit the pending v5.1 work (already built and tested)

The working tree holds the finished auto-heal changes (60 s fail window, `AutoHeal` setting, card/tray toggle, display smoothing) and the header version label. Commit them first so this plan starts from a clean tree.

**Files:**
- Modify (already modified, just commit): `README.md`, `src/NetRoute.App/App.xaml.cs`, `src/NetRoute.App/CardWindow.xaml`, `src/NetRoute.App/CardWindow.xaml.cs`, `src/NetRoute.App/NetRoute.App.csproj`, `src/NetRoute.App/TrayIcon.cs`, `src/NetRoute.Core/RouteController.cs`, `src/NetRoute.Core/Settings.cs`, `tests/NetRoute.Core.Tests/RouteControllerTests.cs`

- [ ] **Step 1: Verify the suite is green**

Run: `dotnet test tests/NetRoute.Core.Tests`
Expected: `Passed! - Failed: 0, Passed: 794`

- [ ] **Step 2: Commit**

```bash
git add README.md src/NetRoute.App src/NetRoute.Core/RouteController.cs src/NetRoute.Core/Settings.cs tests/NetRoute.Core.Tests/RouteControllerTests.cs
git commit -m "feat(core): calmer auto-heal (60 s fail window, off switch), no-reply needs 2 misses, version label

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 1: Throwaway ETW probe (run once by the user, as administrator)

Confirms the real event ids, payload field names and payload value types on this machine before the tracker is written. Everything here is **throwaway** and is deleted in Task 7.

**Files:**
- Create: `tools/EtwProbe/EtwProbe.csproj`
- Create: `tools/EtwProbe/Program.cs`

**Interfaces:**
- Produces: a written "Probe results" section appended to the spec (event ids for send/receive per protocol, payload names, payload value types of `saddr`/`daddr`/`size`/`PID`). Later tasks use the constants in Task 5's `KernelNetworkEvents` table; if the probe differs from that table, change the table, nothing else.

- [ ] **Step 1: Create the project file**

`tools/EtwProbe/EtwProbe.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <!-- THROWAWAY probe: deleted once the tracker is written. Not part of NetRouteWidget.slnx. -->
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Diagnostics.Tracing.TraceEvent" Version="3.2.8" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create the program**

`tools/EtwProbe/Program.cs`:

```csharp
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

// THROWAWAY: prints what Microsoft-Windows-Kernel-Network delivers, so the real tracker is written against facts.
var seconds = args.Length > 0 ? int.Parse(args[0]) : 20;
var kinds = new Dictionary<int, (string Task, string Opcode, string Payload, int Count)>();
var perProcess = new Dictionary<(int Pid, int Id), long>();

using var session = new TraceEventSession("NetRouteWidget-Probe") { StopOnDispose = true };
session.EnableProvider(new Guid("7dd42a49-5329-4832-8dfd-43d979153a88"), TraceEventLevel.Informational, 0x30);
session.Source.Dynamic.All += e =>
{
    lock (kinds)
    {
        var id = (int)e.ID;
        if (!kinds.TryGetValue(id, out var kind))
        {
            var fields = string.Join(", ", e.PayloadNames.Select(n => $"{n}={e.PayloadByName(n)?.GetType().Name}:{e.PayloadByName(n)}"));
            kind = (e.TaskName, e.OpcodeName, fields, 0);
        }
        kinds[id] = kind with { Count = kind.Count + 1 };
        if (e.PayloadNames.Contains("size") && e.PayloadNames.Contains("PID"))
        {
            var key = (Convert.ToInt32(e.PayloadByName("PID")), id);
            perProcess[key] = perProcess.GetValueOrDefault(key) + Convert.ToInt64(e.PayloadByName("size"));
        }
    }
};
var thread = new Thread(() => session.Source.Process()) { IsBackground = true };
thread.Start();
Console.WriteLine($"Tracing for {seconds} s. Use the network now (browse, download something).");
Thread.Sleep(TimeSpan.FromSeconds(seconds));
session.Dispose();
thread.Join(5000);

Console.WriteLine("--- event kinds (first sample of each) ---");
lock (kinds)
{
    foreach (var (id, kind) in kinds.OrderBy(x => x.Key))
        Console.WriteLine($"id {id,3} {kind.Task}/{kind.Opcode} x{kind.Count}: {kind.Payload}");
    Console.WriteLine("--- bytes by process and event id (top 15) ---");
    foreach (var ((pid, id), bytes) in perProcess.OrderByDescending(x => x.Value).Take(15))
    {
        string name;
        try { name = System.Diagnostics.Process.GetProcessById(pid).ProcessName; } catch { name = "(gone)"; }
        Console.WriteLine($"{name,-24} pid {pid,6} id {id,3} {bytes,12:N0} bytes");
    }
}
```

- [ ] **Step 3: Build it**

Run: `dotnet build tools/EtwProbe -c Release`
Expected: `Build succeeded.` (if `TraceEvent` 3.2.8 does not restore, stop and report: the package version is the only assumption).

- [ ] **Step 4: Ask the user to run it as administrator and paste the output**

Tell the user: open an **administrator** PowerShell in the repo folder and run `dotnet run --project tools/EtwProbe -c Release -- 20`, and browse/download something (include a video or a QUIC site such as youtube.com, and an IPv6 site if available) during the 20 s. Wait for the pasted output. Do not continue to Task 5 without it (Tasks 2–4 do not depend on it).

- [ ] **Step 5: Record the results in the spec**

Append a `## Probe results (2026-10-14)` section to the spec with: the id list per protocol (TCP/UDP × v4/v6 × send/recv), the exact payload names, and the .NET type of `saddr`/`daddr`/`size`/`PID`. Compare with the table in Task 5 (`KernelNetworkEvents`) and `EtwPayload.ToAddress` (Task 5); adjust those if different.

- [ ] **Step 6: Commit**

```bash
git add tools/EtwProbe docs/superpowers/specs/2026-10-14-netroute-v5-1-usage-while-smart-off-design.md
git commit -m "chore(v5.1): throwaway ETW probe and its recorded results

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 2: `TrafficClassifier`, `ITrafficSource`, `MeasuredTraffic`, adapter addresses

**Files:**
- Create: `src/NetRoute.Core/SmartRouting/TrafficSource.cs`
- Create: `src/NetRoute.Core/Windows/AdapterAddresses.cs`
- Test: `tests/NetRoute.Core.Tests/TrafficClassifierTests.cs`
- Test: `tests/NetRoute.Core.Tests/AdapterAddressesTests.cs`

**Interfaces:**
- Produces:
  - `record MeasuredTraffic(string? ProcessName, RouteExit Exit, Traffic Traffic)`
  - `interface ITrafficSource : IDisposable { bool IsRunning { get; } string? EnsureRunning(); void SetAdapters(AdapterInfo? phone, AdapterInfo? lan); IReadOnlyList<MeasuredTraffic> Drain(); }`
  - `class TrafficClassifier(IReadOnlyCollection<IPAddress> phone, IReadOnlyCollection<IPAddress> lan)` with `RouteExit? ExitOf(IPAddress a, IPAddress b)`, `static TrafficClassifier None`, `static bool IsLocal(IPAddress ip)`
  - `static class AdapterAddresses { IReadOnlyList<IPAddress> Of(AdapterInfo? adapter, Func<string, IReadOnlyList<IPAddress>> lookup); IReadOnlyList<IPAddress> FromWindows(string adapterName) }`

- [ ] **Step 1: Write the failing classifier tests**

`tests/NetRoute.Core.Tests/TrafficClassifierTests.cs`:

```csharp
using System.Net;

namespace NetRoute.Core.Tests;

public class TrafficClassifierTests
{
    static IPAddress Ip(string text) => IPAddress.Parse(text);

    static readonly IPAddress Phone4 = Ip("192.168.42.11");
    static readonly IPAddress Lan4 = Ip("192.168.86.42");
    static readonly IPAddress Phone6 = Ip("2001:db8:1::2");
    static readonly IPAddress Lan6 = Ip("2001:db8:2::9");
    static readonly TrafficClassifier Classifier = new([Phone4, Phone6], [Lan4, Lan6]);

    [Fact]
    public void An_internet_packet_from_the_phone_counts_for_the_phone_whichever_address_comes_first()
    {
        Assert.Equal(RouteExit.Phone, Classifier.ExitOf(Phone4, Ip("1.1.1.1")));
        Assert.Equal(RouteExit.Phone, Classifier.ExitOf(Ip("1.1.1.1"), Phone4));
    }

    [Fact]
    public void An_internet_packet_on_the_lan_counts_for_the_lan()
    {
        Assert.Equal(RouteExit.Lan, Classifier.ExitOf(Lan4, Ip("8.8.8.8")));
        Assert.Equal(RouteExit.Lan, Classifier.ExitOf(Ip("8.8.8.8"), Lan4));
    }

    [Fact]
    public void Ipv6_addresses_of_the_adapter_count_too()
    {
        Assert.Equal(RouteExit.Phone, Classifier.ExitOf(Phone6, Ip("2606:4700:4700::1111")));
        Assert.Equal(RouteExit.Lan, Classifier.ExitOf(Ip("2606:4700:4700::1111"), Lan6));
    }

    [Fact]
    public void An_ipv4_mapped_ipv6_address_is_read_as_the_ipv4_one()
    {
        Assert.Equal(RouteExit.Phone, Classifier.ExitOf(Ip("::ffff:192.168.42.11"), Ip("1.1.1.1")));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.9")]
    [InlineData("172.31.255.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.3.4")]
    [InlineData("224.0.0.251")]
    [InlineData("255.255.255.255")]
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::5")]
    [InlineData("ff02::fb")]
    public void Traffic_to_the_home_network_loopback_and_multicast_is_ignored(string remote)
    {
        Assert.Null(Classifier.ExitOf(Lan4, Ip(remote)));
        Assert.Null(Classifier.ExitOf(Ip(remote), Phone4));
    }

    [Fact]
    public void A_public_address_next_to_the_private_172_block_still_counts()
    {
        Assert.Equal(RouteExit.Lan, Classifier.ExitOf(Lan4, Ip("172.32.0.1")));
        Assert.Equal(RouteExit.Lan, Classifier.ExitOf(Lan4, Ip("172.15.255.255")));
    }

    [Fact]
    public void Packets_of_other_adapters_are_ignored()
    {
        Assert.Null(Classifier.ExitOf(Ip("10.8.0.2"), Ip("8.8.8.8"))); // a VPN or virtual adapter
    }

    [Fact]
    public void Traffic_between_the_two_adapters_or_to_ourselves_is_ignored()
    {
        Assert.Null(Classifier.ExitOf(Phone4, Lan4));
        Assert.Null(Classifier.ExitOf(Phone4, Phone4));
        Assert.Null(Classifier.ExitOf(Phone4, Phone6));
    }

    [Fact]
    public void Nothing_counts_before_the_adapters_are_known()
    {
        Assert.Null(TrafficClassifier.None.ExitOf(Phone4, Ip("1.1.1.1")));
    }

    [Fact]
    public void A_phone_with_a_public_address_does_not_count_traffic_to_its_own_other_address()
    {
        var c = new TrafficClassifier([Ip("100.64.0.5"), Ip("203.0.113.7")], []);
        Assert.Null(c.ExitOf(Ip("100.64.0.5"), Ip("203.0.113.7")));
        Assert.Equal(RouteExit.Phone, c.ExitOf(Ip("203.0.113.7"), Ip("1.1.1.1")));
    }
}
```

`tests/NetRoute.Core.Tests/AdapterAddressesTests.cs`:

```csharp
using System.Net;
using NetRoute.Core.Windows;

namespace NetRoute.Core.Tests;

public class AdapterAddressesTests
{
    [Fact]
    public void No_adapter_has_no_addresses()
    {
        Assert.Empty(AdapterAddresses.Of(null, _ => [IPAddress.Loopback]));
    }

    [Fact]
    public void The_detected_ipv4_comes_first_and_the_lookup_adds_the_rest_without_duplicates()
    {
        var adapter = TestAdapters.Phone();
        var all = AdapterAddresses.Of(adapter, name =>
        {
            Assert.Equal(adapter.Name, name);
            return [IPAddress.Parse("192.168.42.11"), IPAddress.Parse("2001:db8::2")];
        });

        Assert.Equal([IPAddress.Parse("192.168.42.11"), IPAddress.Parse("2001:db8::2")], all);
    }

    [Fact]
    public void A_failing_lookup_still_returns_the_detected_ipv4()
    {
        var all = AdapterAddresses.Of(TestAdapters.Lan(), _ => throw new InvalidOperationException("no nic"));

        Assert.Equal([IPAddress.Parse("192.168.86.42")], all);
    }

    [Fact]
    public void An_adapter_without_an_ipv4_yet_uses_the_lookup_only()
    {
        var all = AdapterAddresses.Of(TestAdapters.Lan() with { IPv4 = null }, _ => [IPAddress.Parse("2001:db8::9")]);

        Assert.Equal([IPAddress.Parse("2001:db8::9")], all);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~TrafficClassifierTests|FullyQualifiedName~AdapterAddressesTests"`
Expected: build FAIL (`TrafficClassifier`, `AdapterAddresses` do not exist).

- [ ] **Step 3: Implement the types**

`src/NetRoute.Core/SmartRouting/TrafficSource.cs`:

```csharp
using System.Net;
using System.Net.Sockets;

namespace NetRoute.Core;

/// Bytes one process moved through one exit since the previous Drain. ProcessName is the image file name ("chrome.exe"),
/// or null when the process could not be identified (it counts under "Other").
public sealed record MeasuredTraffic(string? ProcessName, RouteExit Exit, Traffic Traffic);

/// Per-application traffic that works without sing-box (Windows kernel tracing). Used by SmartRoutingController while
/// sing-box is not running; every call comes from under the controller's gate except Dispose.
public interface ITrafficSource : IDisposable
{
    bool IsRunning { get; }

    /// Starts the source when it is not running. Null = running (or just started); otherwise the reason it could not.
    /// Cheap to call every second: a failed start is not retried before the source's own retry delay.
    string? EnsureRunning();

    /// The adapters whose packets count. Called on every apply, so a replugged phone with a new address is followed.
    void SetAdapters(AdapterInfo? phone, AdapterInfo? lan);

    /// What was measured since the previous call (per process and exit), and forgets it.
    IReadOnlyList<MeasuredTraffic> Drain();
}

/// Decides which exit a packet belongs to from its two addresses. Pure.
///   - one address must be an address of the Phone adapter (-> Phone) or of the LAN adapter (-> LAN); both addresses are
///     checked because the local/remote order differs between event types;
///   - the OTHER address (the remote) must be a real internet address: loopback, private, link-local, multicast and
///     broadcast addresses, and our own addresses, are ignored (traffic to the home network is not usage);
///   - packets of any other adapter (VPN, virtual switch, the sing-box TUN) match neither set and are ignored.
public sealed class TrafficClassifier
{
    public static readonly TrafficClassifier None = new([], []);

    readonly HashSet<IPAddress> _phone;
    readonly HashSet<IPAddress> _lan;

    public TrafficClassifier(IReadOnlyCollection<IPAddress> phone, IReadOnlyCollection<IPAddress> lan)
    {
        _phone = [.. phone.Select(Plain)];
        _lan = [.. lan.Select(Plain)];
    }

    public RouteExit? ExitOf(IPAddress a, IPAddress b)
    {
        a = Plain(a);
        b = Plain(b);
        var onPhone = _phone.Contains(a) || _phone.Contains(b);
        var onLan = _lan.Contains(a) || _lan.Contains(b);
        if (onPhone == onLan) return null; // neither adapter, or both (traffic between our own two adapters)

        var ours = onPhone ? _phone : _lan;
        var remote = ours.Contains(a) ? b : a;
        if (_phone.Contains(remote) || _lan.Contains(remote) || IsLocal(remote)) return null;
        return onPhone ? RouteExit.Phone : RouteExit.Lan;
    }

    /// Loopback, private, link-local, multicast, broadcast and unspecified addresses: not internet traffic.
    public static bool IsLocal(IPAddress ip)
    {
        ip = Plain(ip);
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10
                || (bytes[0] == 172 && (bytes[1] & 0xF0) == 16)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254)
                || bytes[0] >= 224; // multicast, reserved and the broadcast address
        }
        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (bytes[0] & 0xFE) == 0xFC; // fc00::/7 unique local
    }

    static IPAddress Plain(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}
```

`src/NetRoute.Core/Windows/AdapterAddresses.cs`:

```csharp
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetRoute.Core.Windows;

/// All addresses (IPv4 and IPv6) of the adapter, so IPv6 traffic is attributed to the right exit too:
/// AdapterInfo only knows the IPv4 address.
public static class AdapterAddresses
{
    /// The detected IPv4 first, then whatever the lookup finds by adapter name; a failing lookup keeps what we have.
    public static IReadOnlyList<IPAddress> Of(AdapterInfo? adapter, Func<string, IReadOnlyList<IPAddress>> lookup)
    {
        if (adapter is null) return [];
        var all = new List<IPAddress>();
        if (IPAddress.TryParse(adapter.IPv4, out var v4)) all.Add(v4);
        try
        {
            foreach (var ip in lookup(adapter.Name))
                if (!all.Contains(ip)) all.Add(ip);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // keep the detected address: the next apply tries again
        }
        return all;
    }

    /// The unicast addresses Windows lists for the adapter with this name.
    public static IReadOnlyList<IPAddress> FromWindows(string adapterName)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!string.Equals(nic.Name, adapterName, StringComparison.OrdinalIgnoreCase)) continue;
            return nic.GetIPProperties().UnicastAddresses
                .Select(u => u.Address)
                .Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .ToList();
        }
        return [];
    }
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~TrafficClassifierTests|FullyQualifiedName~AdapterAddressesTests"`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/NetRoute.Core/SmartRouting/TrafficSource.cs src/NetRoute.Core/Windows/AdapterAddresses.cs tests/NetRoute.Core.Tests/TrafficClassifierTests.cs tests/NetRoute.Core.Tests/AdapterAddressesTests.cs
git commit -m "feat(core): traffic classifier, traffic source contract and adapter addresses

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 3: `UsageCounter.AddMeasured`

**Files:**
- Modify: `src/NetRoute.Core/SmartRouting/UsageCounter.cs` (add a method after `Update`, before `Snapshot`, i.e. after line 172)
- Test: `tests/NetRoute.Core.Tests/UsageCounterMeasuredTests.cs`

**Interfaces:**
- Consumes: `MeasuredTraffic`, `UsageAttribution.Resolve(string? processName, string? host)`, `Traffic`, `RouteExit` (Task 2 / existing).
- Produces: `void UsageCounter.AddMeasured(IReadOnlyList<MeasuredTraffic> samples, UsageAttribution attribution, long? phoneAdapterBytes, DateTimeOffset now)`.

- [ ] **Step 1: Write the failing tests**

`tests/NetRoute.Core.Tests/UsageCounterMeasuredTests.cs`:

```csharp
namespace NetRoute.Core.Tests;

public class UsageCounterMeasuredTests
{
    static readonly IReadOnlyList<RuleItem> Catalog =
        [new("onedrive", "sync", "Sync", "OneDrive", ["onedrive.exe"], [], true)];
    static readonly UsageAttribution Attr = UsageAttribution.Build(Catalog, new SmartRoutingSettings());
    static readonly DateOnly D0 = new(2026, 10, 2);
    static readonly DateTimeOffset T0 = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    static readonly IReadOnlyList<MeasuredTraffic> None = [];

    static MeasuredTraffic Sample(string? process, RouteExit exit, long up, long down) => new(process, exit, new Traffic(up, down));

    static UsageRow Row(UsageCounter counter, DateOnly day, string key) => counter.Snapshot().Days[day].Rows[key];

    [Fact]
    public void Bytes_are_counted_per_application_and_exit()
    {
        var counter = new UsageCounter(null, D0);

        counter.AddMeasured([
            Sample("chrome.exe", RouteExit.Phone, 10, 90),
            Sample("chrome.exe", RouteExit.Lan, 0, 50),
            Sample(null, RouteExit.Phone, 0, 7),
        ], Attr, null, T0);

        Assert.Equal(new UsageRow(new Traffic(10, 90), new Traffic(0, 50), 0), Row(counter, D0, "app:chrome.exe"));
        Assert.Equal(new Traffic(0, 7), Row(counter, D0, UsageAttribution.OtherKey).Phone);
    }

    [Fact]
    public void A_built_in_application_item_keeps_its_own_row()
    {
        var counter = new UsageCounter(null, D0);

        counter.AddMeasured([Sample("OneDrive.exe", RouteExit.Lan, 5, 95)], Attr, null, T0);

        Assert.Equal(100, Row(counter, D0, "onedrive").Lan.Total);
    }

    [Fact]
    public void Nothing_is_kept_off_4G_by_measured_traffic()
    {
        var counter = new UsageCounter(null, D0);

        counter.AddMeasured([Sample("chrome.exe", RouteExit.Lan, 0, 5000)], Attr, null, T0);

        Assert.Equal(0, Row(counter, D0, "app:chrome.exe").Kept);
        Assert.Equal(0, counter.TodayKept(D0).Total);
    }

    [Fact]
    public void Empty_and_zero_samples_add_nothing_and_do_not_dirty_the_counter()
    {
        var counter = new UsageCounter(null, D0);

        counter.AddMeasured(None, Attr, null, T0);
        counter.AddMeasured([Sample("chrome.exe", RouteExit.Phone, 0, 0)], Attr, null, T0.AddSeconds(1));

        Assert.False(counter.TakeDirty());
        Assert.Empty(counter.Snapshot().Days);
    }

    [Fact]
    public void Adding_marks_the_counter_dirty_once()
    {
        var counter = new UsageCounter(null, D0);

        counter.AddMeasured([Sample("chrome.exe", RouteExit.Phone, 0, 10)], Attr, null, T0);

        Assert.True(counter.TakeDirty());
        Assert.False(counter.TakeDirty());
    }

    [Fact]
    public void Live_speed_follows_the_measured_bytes_and_decays_to_zero_when_traffic_stops()
    {
        var counter = new UsageCounter(null, D0);

        counter.AddMeasured([Sample("chrome.exe", RouteExit.Phone, 0, 6000)], Attr, null, T0);
        Assert.Equal(2000, counter.Live.PhoneBytesPerSecond); // 6000 bytes over the 3 s window
        Assert.Equal(2000, counter.Snapshot().Rates["app:chrome.exe"].PhoneBytesPerSecond);

        counter.AddMeasured(None, Attr, null, T0.AddSeconds(4));

        Assert.Equal(new UsageRate(0, 0), counter.Live);
        Assert.Empty(counter.Snapshot().Rates);
    }

    [Fact]
    public void The_windows_phone_adapter_total_grows_between_measured_polls()
    {
        var counter = new UsageCounter(null, D0);

        counter.AddMeasured(None, Attr, 1000, T0); // the first reading is only a baseline
        counter.AddMeasured(None, Attr, 1600, T0.AddSeconds(1));
        counter.AddMeasured(None, Attr, null, T0.AddSeconds(2)); // unreadable: baseline dropped, nothing added
        counter.AddMeasured(None, Attr, 2000, T0.AddSeconds(3));

        Assert.Equal(600, counter.Snapshot().Days[D0].PhoneAdapterBytes);
    }

    [Fact]
    public void A_new_local_day_starts_a_new_day_of_history()
    {
        var counter = new UsageCounter(null, D0);

        counter.AddMeasured([Sample("chrome.exe", RouteExit.Phone, 0, 10)], Attr, null, new DateTimeOffset(2026, 10, 2, 23, 59, 59, TimeSpan.Zero));
        counter.AddMeasured([Sample("chrome.exe", RouteExit.Phone, 0, 20)], Attr, null, new DateTimeOffset(2026, 10, 3, 0, 0, 1, TimeSpan.Zero));

        Assert.Equal(10, Row(counter, D0, "app:chrome.exe").Phone.Total);
        Assert.Equal(20, Row(counter, D0.AddDays(1), "app:chrome.exe").Phone.Total);
    }

    [Fact]
    public void Rows_beyond_the_daily_cap_fold_into_other()
    {
        var counter = new UsageCounter(null, D0);
        var many = Enumerable.Range(0, UsageCounter.MaxRowsPerDay + 5)
            .Select(i => Sample($"app{i}.exe", RouteExit.Phone, 0, 10)).ToList();

        counter.AddMeasured(many, Attr, null, T0);

        var rows = counter.Snapshot().Days[D0].Rows;
        Assert.Equal(UsageCounter.MaxRowsPerDay + 1, rows.Count); // the cap plus "other"
        Assert.Equal(50, rows[UsageAttribution.OtherKey].Phone.Total);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~UsageCounterMeasuredTests"`
Expected: build FAIL (`AddMeasured` does not exist).

- [ ] **Step 3: Implement `AddMeasured`**

In `UsageCounter.cs`, insert after the end of `Update` (the closing brace at line 172) and before the `/// An immutable copy of everything.` comment:

```csharp
    /// Records what Windows tracing measured while sing-box is not running: bytes per application and exit, attributed
    /// like Update does (so history stays continuous), but with no host (no website rows) and nothing "kept off 4G".
    /// Called once a second even when nothing was measured, so the live rates fade out when traffic stops.
    public void AddMeasured(IReadOnlyList<MeasuredTraffic> samples, UsageAttribution attribution, long? phoneAdapterBytes, DateTimeOffset now)
    {
        var day = DateOnly.FromDateTime(now.DateTime);
        if (day != _today)
        {
            _today = day;
            Prune();
        }
        var dayData = DayFor(day);

        long phone = 0, lan = 0;
        foreach (var sample in samples)
        {
            if (sample.Traffic.Total <= 0) continue;
            var key = attribution.Resolve(sample.ProcessName, host: null);
            Add(dayData.Rows, key, sample.Exit, sample.Traffic, kept: 0);
            AddRate(key, sample.Exit, sample.Traffic.Total, sample.Traffic.Up, now);
            if (sample.Exit == RouteExit.Phone) phone += sample.Traffic.Total;
            else lan += sample.Traffic.Total;
        }

        // Windows' own phone adapter counter keeps the footer's "exact" total comparable; same baseline rule as Update.
        if (phoneAdapterBytes is { } adapterNow && _lastAdapter is { } adapterBefore && adapterNow >= adapterBefore)
        {
            dayData.PhoneAdapterBytes += adapterNow - adapterBefore;
            _dirty = true;
        }
        _lastAdapter = phoneAdapterBytes;

        if (phone + lan != 0) _liveSamples.Enqueue((now, phone, lan));
        RebuildRates(now);
        RebuildLive(now);
    }
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~UsageCounter"`
Expected: all PASS (new and existing `UsageCounterTests`).

If `Empty_and_zero_samples_...` fails because a day entry exists: `Snapshot()` skips days with no rows and no adapter bytes, so `Days` stays empty; if it fails anyway, read the assertion output and fix the test expectation to match `Snapshot`'s documented skip rule, not the counter.

- [ ] **Step 5: Commit**

```bash
git add src/NetRoute.Core/SmartRouting/UsageCounter.cs tests/NetRoute.Core.Tests/UsageCounterMeasuredTests.cs
git commit -m "feat(core): UsageCounter.AddMeasured for traffic measured without sing-box

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 4: `SmartRoutingController` drains the traffic source while sing-box is off

**Files:**
- Modify: `src/NetRoute.Core/SmartRouting/SmartRoutingController.cs` (usings; fields near line 54; constructor lines 88-106; `ApplyAsync` near line 129; `PollStatsAsync` lines 273-283; `ShutdownAsync` lines 360-373; `StartAsync` near line 474; `DropApi` lines 529-544)
- Modify: `tests/NetRoute.Core.Tests/SmartFakes.cs` (add `FakeTrafficSource`)
- Modify: `tests/NetRoute.Core.Tests/SmartRoutingControllerTests.cs` (`Create` helper line 26-33, new tests at the end of the class)

**Interfaces:**
- Consumes: `ITrafficSource`, `MeasuredTraffic` (Task 2); `UsageCounter.AddMeasured` (Task 3).
- Produces: constructor parameter `ITrafficSource? traffic = null` (last parameter, after `counters`); `public bool IsMeasuring` (true while Windows tracing is counting usage because sing-box is not running).

- [ ] **Step 1: Add the fake and the `Create` parameter**

Append to `tests/NetRoute.Core.Tests/SmartFakes.cs`:

```csharp
sealed class FakeTrafficSource : ITrafficSource
{
    public bool IsRunning { get; set; }
    public string? StartError { get; set; }
    public int StartAttempts { get; private set; }
    public int Drains { get; private set; }
    public bool Disposed { get; private set; }
    public AdapterInfo? Phone { get; private set; }
    public AdapterInfo? Lan { get; private set; }
    public List<MeasuredTraffic> Pending { get; } = new();

    public string? EnsureRunning()
    {
        if (IsRunning) return null;
        StartAttempts++;
        if (StartError is { } error) return error;
        IsRunning = true;
        return null;
    }

    public void SetAdapters(AdapterInfo? phone, AdapterInfo? lan) => (Phone, Lan) = (phone, lan);

    public IReadOnlyList<MeasuredTraffic> Drain()
    {
        Drains++;
        var taken = Pending.ToList();
        Pending.Clear();
        return taken;
    }

    public void Dispose() => Disposed = true;
}
```

In `SmartRoutingControllerTests.cs`, change `Create` (lines 26-33) to:

```csharp
    SmartRoutingController Create(Func<int>? freePort = null, IAdapterCounters? counters = null, UsageSnapshot? restored = null,
                                  IReadOnlyList<RuleItem>? catalog = null, ITrafficSource? traffic = null)
    {
        var c = new SmartRoutingController(catalog ?? Catalog, _host, (_, _) => _api, freePort ?? (() => 40000), restored, _logs.Add, _time, counters, traffic);
        c.WaitingDetected += _popups.Add;
        c.Notify += _notes.Add;
        return c;
    }
```

- [ ] **Step 2: Write the failing tests**

Add at the end of the `SmartRoutingControllerTests` class (before its closing brace):

```csharp
    static MeasuredTraffic Measured(string? process, RouteExit exit, long up, long down) => new(process, exit, new Traffic(up, down));

    [Fact]
    public async Task With_smart_off_the_measured_traffic_is_recorded_and_the_card_totals_move()
    {
        var traffic = new FakeTrafficSource();
        var c = Create(traffic: traffic);
        await c.ApplyAsync(Net(), On(false));
        traffic.Pending.Add(Measured("chrome.exe", RouteExit.Phone, 10, 90));

        await c.PollStatsAsync();

        var day = (await c.GetUsageAsync()).Days.Values.Single();
        Assert.Equal(100, day.Rows["app:chrome.exe"].Phone.Total);
        Assert.Equal(new ExitTotals(100, 0), c.TodayTotals);
        Assert.True(c.IsMeasuring);
    }

    [Fact]
    public async Task The_traffic_source_follows_the_adapters_on_every_apply_so_a_replugged_phone_is_followed()
    {
        var traffic = new FakeTrafficSource();
        var c = Create(traffic: traffic);

        await c.ApplyAsync(Net(phoneIndex: 31), On(false));
        Assert.Equal(31, traffic.Phone!.Index);

        await c.ApplyAsync(Net(phoneIndex: 36), On(false));
        Assert.Equal(36, traffic.Phone!.Index);
        Assert.NotNull(traffic.Lan);
    }

    [Fact]
    public async Task While_sing_box_runs_the_measured_traffic_is_discarded_not_counted_twice()
    {
        var traffic = new FakeTrafficSource();
        var c = Create(traffic: traffic);
        await c.ApplyAsync(Net(), On());
        traffic.Pending.Add(Measured("chrome.exe", RouteExit.Phone, 0, 500));

        await c.PollStatsAsync();

        Assert.Empty((await c.GetUsageAsync()).Days);
        Assert.Empty(traffic.Pending); // drained and dropped, so it does not pile up for later
        Assert.False(c.IsMeasuring);
    }

    [Fact]
    public async Task Switching_smart_on_and_off_counts_only_the_traffic_measured_while_it_was_off()
    {
        var traffic = new FakeTrafficSource();
        var c = Create(traffic: traffic);
        await c.ApplyAsync(Net(), On());
        traffic.Pending.Add(Measured("chrome.exe", RouteExit.Phone, 0, 500));
        await c.PollStatsAsync(); // discarded: sing-box counted this one

        await c.ApplyAsync(Net(), On(false));
        traffic.Pending.Add(Measured("chrome.exe", RouteExit.Phone, 0, 50));
        await c.PollStatsAsync();

        Assert.Equal(50, (await c.GetUsageAsync()).Days.Values.Single().Rows["app:chrome.exe"].Phone.Total);
        Assert.True(c.IsMeasuring);

        await c.ApplyAsync(Net(), On()); // Smart back on: measuring ends
        Assert.False(c.IsMeasuring);
    }

    [Fact]
    public async Task A_traffic_source_that_cannot_start_is_logged_once_and_never_blocks_anything()
    {
        var traffic = new FakeTrafficSource { StartError = "Access is denied" };
        var c = Create(traffic: traffic);
        await c.ApplyAsync(Net(), On(false));

        await c.PollStatsAsync();
        await c.PollStatsAsync();

        Assert.Equal(1, _logs.Count(l => l.Contains("Access is denied")));
        Assert.False(c.IsMeasuring);
        Assert.Empty((await c.GetUsageAsync()).Days);

        traffic.StartError = null; // e.g. the widget was restarted elevated: the next poll starts it
        await c.PollStatsAsync();
        Assert.True(c.IsMeasuring);
    }

    [Fact]
    public async Task A_trace_that_stops_by_itself_marks_a_gap_for_the_day()
    {
        var traffic = new FakeTrafficSource();
        var c = Create(traffic: traffic);
        await c.ApplyAsync(Net(), On(false));
        traffic.Pending.Add(Measured("chrome.exe", RouteExit.Phone, 0, 10));
        await c.PollStatsAsync();

        traffic.IsRunning = false; // someone stopped the session
        traffic.StartError = "session in use";
        await c.PollStatsAsync();

        Assert.Equal(1, (await c.GetUsageAsync()).Days.Values.Single().Gaps);
        Assert.False(c.IsMeasuring);
    }

    [Fact]
    public async Task Live_speed_works_while_measuring()
    {
        var traffic = new FakeTrafficSource();
        var c = Create(traffic: traffic);
        await c.ApplyAsync(Net(), On(false));
        traffic.Pending.Add(Measured("chrome.exe", RouteExit.Phone, 0, 6000));

        await c.PollStatsAsync();

        Assert.Equal(2000, c.LiveSpeed.PhoneBytesPerSecond);
    }

    [Fact]
    public async Task Applying_again_while_smart_is_off_keeps_the_adapter_baseline_and_the_live_speed()
    {
        var counters = new FakeAdapterCounters { Bytes = 1000 };
        var traffic = new FakeTrafficSource();
        var c = Create(counters: counters, traffic: traffic);
        await c.ApplyAsync(Net(), On(false));
        await c.PollStatsAsync(); // baseline 1000

        traffic.Pending.Add(Measured("chrome.exe", RouteExit.Phone, 0, 6000));
        await c.PollStatsAsync();
        await c.ApplyAsync(Net(), On(false)); // the widget refreshes every 5-10 s: this must not reset anything
        counters.Bytes = 1500;
        await c.PollStatsAsync();

        Assert.Equal(500, (await c.GetUsageAsync()).Days.Values.Single().PhoneAdapterBytes);
        Assert.Equal(2000, c.LiveSpeed.PhoneBytesPerSecond);
    }

    [Fact]
    public async Task Shutdown_disposes_the_traffic_source()
    {
        var traffic = new FakeTrafficSource();
        var c = Create(traffic: traffic);
        await c.ApplyAsync(Net(), On(false));

        await c.ShutdownAsync();

        Assert.True(traffic.Disposed);
        Assert.False(c.IsMeasuring);
    }

    [Fact]
    public async Task Without_a_traffic_source_nothing_changes()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On(false));

        await c.PollStatsAsync();

        Assert.False(c.IsMeasuring);
        Assert.Empty((await c.GetUsageAsync()).Days);
    }
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~SmartRoutingControllerTests"`
Expected: build FAIL (constructor has no `traffic` parameter, no `IsMeasuring`).

- [ ] **Step 4: Implement the controller changes**

(a) At the top of `SmartRoutingController.cs` add `using System.Net;` above `using System.Text.RegularExpressions;`.

(b) Fields — after `readonly IAdapterCounters? _counters;` (line 54) add:

```csharp
    readonly ITrafficSource? _traffic;
```
and after `volatile ExitTotals _todayTotals = ExitTotals.Zero;` (line 86) add:

```csharp
    bool _measuring; // under the gate: Windows tracing is counting usage because sing-box is not running
    volatile bool _isMeasuring;
    string? _trafficError;
```

(c) Constructor: add the parameter and assignment.

```csharp
        Func<int> freePort, UsageSnapshot? restoredUsage, Action<string> log, TimeProvider? time = null,
        IAdapterCounters? counters = null, ITrafficSource? traffic = null)
```
and after `_counters = counters;` add `_traffic = traffic;`.

(d) Public property, after `public ExitTotals TodayTotals => _todayTotals;` (line 116):

```csharp

    /// True while usage is being counted by Windows tracing because sing-box is not running (the card shows live speeds and
    /// the Usage tab says so). Read without the gate.
    public bool IsMeasuring => _isMeasuring;
```

(e) `ApplyAsync`: after `_settings = settings;` (line 130) add:

```csharp
                _traffic?.SetAdapters(net.Adapters.Phone, net.Adapters.Lan);
```

(f) `PollStatsAsync`: replace line 278 (`if (_shutdown || _api is null) return;`) with:

```csharp
            if (_shutdown) return;
            if (_api is null)
            {
                MeasureWithoutSingBox();
                return;
            }
            // sing-box's own packets cross the physical adapters as well: counting them too would double the traffic.
            DiscardMeasured();
```

(g) Add these methods right after `PollStatsAsync` (before `PhoneAdapterBytes`):

```csharp
    /// Smart routing is not running: count per-application traffic from Windows tracing instead. Under the gate.
    void MeasureWithoutSingBox()
    {
        if (_traffic is null) return;
        try
        {
            if (_measuring && !_traffic.IsRunning) StopMeasuring(noteGap: true); // the trace ended by itself
            if (_traffic.EnsureRunning() is { } error)
            {
                FailMeasuring(error);
                return;
            }
            _trafficError = null;

            var before = _liveSpeed;
            var wasMeasuring = _measuring;
            _measuring = _isMeasuring = true;
            var drained = _traffic.Drain();
            _usage.AddMeasured(drained, _attribution, PhoneAdapterBytes(), _time.GetLocalNow());
            _liveSpeed = _usage.Live;
            _todayTotals = _usage.TodayTotals(Today());
            if (drained.Count > 0 || _liveSpeed != before || !wasMeasuring) SafePublish(); // a quiet poll changes nothing to show
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FailMeasuring(ex.Message);
        }
    }

    void FailMeasuring(string message)
    {
        if (message != _trafficError) _log($"Usage tracking without Smart routing is unavailable: {message}"); // once per distinct reason: this runs every second
        _trafficError = message;
        StopMeasuring(noteGap: true);
    }

    void StopMeasuring(bool noteGap)
    {
        if (!_measuring) return;
        _measuring = _isMeasuring = false;
        if (noteGap) _usage.NoteGap(Today()); // the bytes since the last poll were not recorded
        _usage.ClearRates();
        _liveSpeed = NoSpeed;
        SafePublish();
    }

    void DiscardMeasured()
    {
        if (_traffic is null) return;
        _measuring = _isMeasuring = false;
        try
        {
            _traffic.Drain();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"Smart routing: dropping measured traffic failed: {ex.Message}");
        }
    }
```

(h) `ShutdownAsync`: inside the `try`, after `_liveSpeed = NoSpeed;` add:

```csharp
            _measuring = _isMeasuring = false;
            _traffic?.Dispose();
```

(i) `StartAsync`: after `_graceLogged = false;` (line 475) add:

```csharp
        _usage.ResetBaselines(); // a new sing-box starts from zero totals and new connection ids (DropApi only resets when one was running)
        _measuring = _isMeasuring = false;
```

(j) `DropApi` (lines 529-544): replace its first four lines (from `if (_api is not null) _usage.NoteGap...` through `_liveSpeed = NoSpeed;`) with:

```csharp
        if (_api is not null)
        {
            _usage.NoteGap(Today()); // a running sing-box went away: the bytes just before that were not recorded
            _usage.ResetBaselines(); // the next sing-box starts from zero totals and new connection ids
            _liveSpeed = NoSpeed;
        }
        // Without a running sing-box nothing needs resetting: ApplyAsync calls this every 5-10 s while Smart is off, and
        // resetting then would drop the Windows adapter baseline and the live speed that tracing keeps.
```

- [ ] **Step 5: Run the controller tests**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~SmartRoutingControllerTests"`
Expected: all PASS, including every pre-existing test. If a pre-existing test fails because it relied on `DropApi` resetting baselines without a running API, read it: the `StartAsync` reset (i) restores that behaviour for every real start; adjust only the test's setup if it called `PollStatsAsync` across a stop without a start.

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test tests/NetRoute.Core.Tests`
Expected: `Failed: 0`.

- [ ] **Step 7: Commit**

```bash
git add src/NetRoute.Core/SmartRouting/SmartRoutingController.cs tests/NetRoute.Core.Tests/SmartFakes.cs tests/NetRoute.Core.Tests/SmartRoutingControllerTests.cs
git commit -m "feat(core): count usage from Windows tracing while sing-box is not running

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 5: `WindowsTrafficTracker` (ETW) and its helpers

Requires the probe results from Task 1 (Step 4/5). Pure helpers are unit-tested; the ETW session itself is verified by Task 7's accuracy script.

**Files:**
- Modify: `src/NetRoute.Core/NetRoute.Core.csproj` (package reference)
- Modify: `THIRD-PARTY-NOTICES.md`
- Create: `src/NetRoute.Core/Windows/EtwPayload.cs`
- Create: `src/NetRoute.Core/Windows/ProcessNameCache.cs`
- Create: `src/NetRoute.Core/Windows/ProcessImage.cs`
- Create: `src/NetRoute.Core/Windows/KernelNetworkEvents.cs`
- Create: `src/NetRoute.Core/Windows/WindowsTrafficTracker.cs`
- Test: `tests/NetRoute.Core.Tests/EtwPayloadTests.cs`
- Test: `tests/NetRoute.Core.Tests/ProcessNameCacheTests.cs`
- Test: `tests/NetRoute.Core.Tests/KernelNetworkEventsTests.cs`

**Interfaces:**
- Consumes: `ITrafficSource`, `MeasuredTraffic`, `TrafficClassifier`, `AdapterAddresses` (Task 2).
- Produces: `WindowsTrafficTracker : ITrafficSource` with a parameterless-callable constructor `new WindowsTrafficTracker()`; `ProcessNameCache(Func<int, string?> resolve, TimeProvider? time = null)` with `string? NameOf(int pid)`; `EtwPayload.ToAddress(object?)`, `EtwPayload.ToLong(object?)`; `KernelNetworkEvents.IsData(int)`, `IsSend(int)`.

- [ ] **Step 1: Add the package and the notice**

In `src/NetRoute.Core/NetRoute.Core.csproj`, add before the `<Import ... SingBox.Fetch.targets />` line:

```xml
  <ItemGroup>
    <PackageReference Include="Microsoft.Diagnostics.Tracing.TraceEvent" Version="3.2.8" />
  </ItemGroup>

```

Append to `THIRD-PARTY-NOTICES.md`:

```markdown

## Microsoft.Diagnostics.Tracing.TraceEvent 3.2.8
- Project: https://github.com/microsoft/perfview
- License: MIT. Used as a library (linked) to read Windows kernel network events, so usage can be counted per application while Smart routing is off.
```

Run: `dotnet build src/NetRoute.Core`
Expected: `Build succeeded.`

- [ ] **Step 2: Write the failing helper tests**

`tests/NetRoute.Core.Tests/EtwPayloadTests.cs`:

```csharp
using System.Net;
using NetRoute.Core.Windows;

namespace NetRoute.Core.Tests;

public class EtwPayloadTests
{
    [Fact]
    public void An_ip_address_passes_through()
    {
        var ip = IPAddress.Parse("1.2.3.4");
        Assert.Same(ip, EtwPayload.ToAddress(ip));
    }

    [Fact]
    public void An_ipv4_address_read_as_a_little_endian_number_is_the_dotted_address()
    {
        Assert.Equal(IPAddress.Parse("127.0.0.1"), EtwPayload.ToAddress(0x0100007Fu));
        Assert.Equal(IPAddress.Parse("192.168.42.11"), EtwPayload.ToAddress(unchecked((int)0x0B2AA8C0)));
    }

    [Fact]
    public void Byte_arrays_and_text_become_addresses()
    {
        Assert.Equal(IPAddress.Parse("1.1.1.1"), EtwPayload.ToAddress(new byte[] { 1, 1, 1, 1 }));
        Assert.Equal(IPAddress.Parse("2001:db8::1"), EtwPayload.ToAddress(IPAddress.Parse("2001:db8::1").GetAddressBytes()));
        Assert.Equal(IPAddress.Parse("::1"), EtwPayload.ToAddress("::1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not an address")]
    [InlineData(2.5)]
    public void Anything_else_is_not_an_address(object? value)
    {
        Assert.Null(EtwPayload.ToAddress(value));
    }

    [Fact]
    public void Numbers_of_any_width_become_longs()
    {
        Assert.Equal(5, EtwPayload.ToLong(5));
        Assert.Equal(4_000_000_000L, EtwPayload.ToLong(4_000_000_000u));
        Assert.Equal(7, EtwPayload.ToLong((ushort)7));
        Assert.Equal(long.MaxValue, EtwPayload.ToLong(ulong.MaxValue));
        Assert.Equal(0, EtwPayload.ToLong("x"));
        Assert.Equal(0, EtwPayload.ToLong(null));
    }
}
```

`tests/NetRoute.Core.Tests/ProcessNameCacheTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;
using NetRoute.Core.Windows;

namespace NetRoute.Core.Tests;

public class ProcessNameCacheTests
{
    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero));
    readonly List<int> _asked = new();

    ProcessNameCache Create(Func<int, string?>? resolve = null) =>
        new(resolve ?? (pid => { _asked.Add(pid); return pid == 7 ? "chrome.exe" : null; }), _time);

    [Fact]
    public void A_name_is_resolved_once_and_then_remembered()
    {
        var cache = Create();

        Assert.Equal("chrome.exe", cache.NameOf(7));
        Assert.Equal("chrome.exe", cache.NameOf(7));

        Assert.Equal([7], _asked);
    }

    [Fact]
    public void An_unknown_process_is_remembered_as_unknown()
    {
        var cache = Create();

        Assert.Null(cache.NameOf(9));
        Assert.Null(cache.NameOf(9));

        Assert.Equal([9], _asked);
    }

    [Fact]
    public void A_name_is_looked_up_again_after_it_expires_because_ids_get_reused()
    {
        var names = new Queue<string?>(["a.exe", "b.exe"]);
        var cache = Create(_ => names.Dequeue());

        Assert.Equal("a.exe", cache.NameOf(5));
        _time.Advance(ProcessNameCache.Lifetime + TimeSpan.FromSeconds(1));

        Assert.Equal("b.exe", cache.NameOf(5));
    }

    [Fact]
    public void A_resolver_that_throws_gives_unknown_instead_of_breaking_the_trace()
    {
        var cache = Create(_ => throw new InvalidOperationException("access denied"));

        Assert.Null(cache.NameOf(4));
    }
}
```

`tests/NetRoute.Core.Tests/KernelNetworkEventsTests.cs`:

```csharp
using NetRoute.Core.Windows;

namespace NetRoute.Core.Tests;

public class KernelNetworkEventsTests
{
    [Theory]
    [InlineData(10)]
    [InlineData(26)]
    [InlineData(42)]
    [InlineData(58)]
    public void Send_events_are_data_and_upload(int id)
    {
        Assert.True(KernelNetworkEvents.IsData(id));
        Assert.True(KernelNetworkEvents.IsSend(id));
    }

    [Theory]
    [InlineData(11)]
    [InlineData(27)]
    [InlineData(43)]
    [InlineData(59)]
    public void Receive_events_are_data_and_download(int id)
    {
        Assert.True(KernelNetworkEvents.IsData(id));
        Assert.False(KernelNetworkEvents.IsSend(id));
    }

    [Theory]
    [InlineData(12)] // connect attempt
    [InlineData(13)] // disconnect
    [InlineData(14)] // retransmit: not new data
    [InlineData(0)]
    public void Other_events_are_not_data(int id)
    {
        Assert.False(KernelNetworkEvents.IsData(id));
    }
}
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~EtwPayloadTests|FullyQualifiedName~ProcessNameCacheTests|FullyQualifiedName~KernelNetworkEventsTests"`
Expected: build FAIL (types do not exist).

- [ ] **Step 4: Implement the helpers**

`src/NetRoute.Core/Windows/EtwPayload.cs`:

```csharp
using System.Net;

namespace NetRoute.Core.Windows;

/// Reads ETW payload values without trusting their .NET type: the same field can arrive as an IPAddress, a number or bytes
/// depending on the event version (the Task 1 probe recorded what this machine delivers).
public static class EtwPayload
{
    public static IPAddress? ToAddress(object? value) => value switch
    {
        IPAddress ip => ip,
        uint v4 => new IPAddress(v4), // four network-order bytes read as a little-endian number
        int v4 => new IPAddress(unchecked((uint)v4)),
        byte[] { Length: 4 or 16 } bytes => new IPAddress(bytes),
        string text when IPAddress.TryParse(text, out var parsed) => parsed,
        _ => null,
    };

    public static long ToLong(object? value) => value switch
    {
        int i => i,
        uint u => u,
        long l => l,
        ulong ul => ul > long.MaxValue ? long.MaxValue : (long)ul,
        ushort s => s,
        byte b => b,
        _ => 0,
    };
}
```

`src/NetRoute.Core/Windows/ProcessNameCache.cs`:

```csharp
using System.Collections.Concurrent;

namespace NetRoute.Core.Windows;

/// Process id -> image file name ("chrome.exe"), remembered for a while: the trace sees thousands of packets per process and
/// resolving a name is a system call. Ids are reused, so an entry expires; an id that cannot be resolved is remembered as unknown.
public sealed class ProcessNameCache(Func<int, string?> resolve, TimeProvider? time = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    const int MaxEntries = 4096;

    readonly TimeProvider _time = time ?? TimeProvider.System;
    readonly ConcurrentDictionary<int, (string? Name, DateTimeOffset At)> _names = new();

    public string? NameOf(int pid)
    {
        var now = _time.GetUtcNow();
        if (_names.TryGetValue(pid, out var hit) && now - hit.At < Lifetime) return hit.Name;

        string? name;
        try
        {
            name = resolve(pid);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            name = null; // a protected or vanished process: its traffic counts under "Other"
        }
        if (_names.Count >= MaxEntries) _names.Clear(); // a bound, not a policy: ids come and go
        _names[pid] = (name, now);
        return name;
    }
}
```

`src/NetRoute.Core/Windows/ProcessImage.cs`:

```csharp
using System.Runtime.InteropServices;
using System.Text;

namespace NetRoute.Core.Windows;

/// The image file name of a running process, from its id.
public static class ProcessImage
{
    const uint ProcessQueryLimitedInformation = 0x1000;

    /// "chrome.exe"; "System" for the kernel's own traffic (id 4); null when the process cannot be opened or has gone.
    public static string? NameOf(int pid)
    {
        if (pid == 4) return "System";
        if (pid <= 0) return null;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var path = new StringBuilder(1024);
            var size = (uint)path.Capacity;
            return QueryFullProcessImageNameW(handle, 0, path, ref size) ? Path.GetFileName(path.ToString()) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inheritHandle, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageNameW(IntPtr handle, uint flags, StringBuilder name, ref uint size);
}
```

`src/NetRoute.Core/Windows/KernelNetworkEvents.cs`:

```csharp
namespace NetRoute.Core.Windows;

/// Event ids of the data events of Microsoft-Windows-Kernel-Network (confirmed by the Task 1 probe): bytes sent and received
/// over TCP and UDP, IPv4 and IPv6. Connection set-up, teardown and retransmits are not new data and are ignored.
public static class KernelNetworkEvents
{
    static readonly HashSet<int> Send = [10, 26, 42, 58];    // TCPv4, TCPv6, UDPv4, UDPv6 sent
    static readonly HashSet<int> Receive = [11, 27, 43, 59]; // TCPv4, TCPv6, UDPv4, UDPv6 received

    public static bool IsData(int id) => Send.Contains(id) || Receive.Contains(id);

    public static bool IsSend(int id) => Send.Contains(id);
}
```

- [ ] **Step 5: Run the helper tests**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~EtwPayloadTests|FullyQualifiedName~ProcessNameCacheTests|FullyQualifiedName~KernelNetworkEventsTests"`
Expected: all PASS. (If the probe results from Task 1 show different ids or payload types, change `KernelNetworkEvents` and `EtwPayload` and their tests to the probed facts first.)

- [ ] **Step 6: Implement the tracker**

`src/NetRoute.Core/Windows/WindowsTrafficTracker.cs`:

```csharp
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace NetRoute.Core.Windows;

/// Counts bytes per process and exit from the Windows kernel network trace (ETW), so usage can be recorded while Smart
/// routing is off. Needs administrator rights. A named user-mode session, so it never fights over the single "NT Kernel
/// Logger". The trace thread only adds to a table under a short lock; Drain swaps the table out.
public sealed class WindowsTrafficTracker : ITrafficSource
{
    public const string SessionName = "NetRouteWidget-Traffic";
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    static readonly Guid KernelNetwork = new("7dd42a49-5329-4832-8dfd-43d979153a88");
    const ulong Ipv4AndIpv6Keywords = 0x10 | 0x20;

    readonly ProcessNameCache _names;
    readonly TimeProvider _time;
    readonly Func<string, IReadOnlyList<System.Net.IPAddress>> _addressesOf;
    readonly object _gate = new();
    Dictionary<(int Pid, RouteExit Exit), (long Up, long Down)> _table = new();
    volatile TrafficClassifier _classifier = TrafficClassifier.None;
    TraceEventSession? _session;
    volatile bool _running;
    bool _disposed;
    string? _lastError;
    DateTimeOffset _nextTry;

    public WindowsTrafficTracker(
        ProcessNameCache? names = null, TimeProvider? time = null,
        Func<string, IReadOnlyList<System.Net.IPAddress>>? addressesOf = null)
    {
        _names = names ?? new ProcessNameCache(ProcessImage.NameOf);
        _time = time ?? TimeProvider.System;
        _addressesOf = addressesOf ?? AdapterAddresses.FromWindows;
    }

    public bool IsRunning => _running;

    public string? EnsureRunning()
    {
        if (_running) return null;
        if (_disposed) return "stopped";
        var now = _time.GetUtcNow();
        if (now < _nextTry) return _lastError ?? "waiting to retry";
        try
        {
            StopSession(); // tidy a session whose thread ended
            var session = new TraceEventSession(SessionName) { StopOnDispose = true }; // replaces a leftover session of the same name
            session.EnableProvider(KernelNetwork, TraceEventLevel.Informational, Ipv4AndIpv6Keywords);
            session.Source.Dynamic.All += OnEvent;
            _session = session;
            _running = true;
            new Thread(() => Process(session)) { IsBackground = true, Name = "NetRoute traffic trace" }.Start();
            _lastError = null;
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lastError = ex.Message;
            _nextTry = now + RetryDelay;
            StopSession();
            return _lastError;
        }
    }

    public void SetAdapters(AdapterInfo? phone, AdapterInfo? lan) =>
        _classifier = new TrafficClassifier(AdapterAddresses.Of(phone, _addressesOf), AdapterAddresses.Of(lan, _addressesOf));

    public IReadOnlyList<MeasuredTraffic> Drain()
    {
        Dictionary<(int Pid, RouteExit Exit), (long Up, long Down)> taken;
        lock (_gate)
        {
            taken = _table;
            _table = new();
        }
        var merged = new Dictionary<(string? Name, RouteExit Exit), Traffic>();
        foreach (var ((pid, exit), (up, down)) in taken)
        {
            var key = (_names.NameOf(pid), exit);
            var traffic = new Traffic(up, down);
            merged[key] = merged.TryGetValue(key, out var existing) ? existing + traffic : traffic;
        }
        return merged.Select(m => new MeasuredTraffic(m.Key.Name, m.Key.Exit, m.Value)).ToList();
    }

    public void Dispose()
    {
        _disposed = true;
        StopSession();
    }

    void OnEvent(TraceEvent data)
    {
        var id = (int)data.ID;
        if (!KernelNetworkEvents.IsData(id)) return;
        if (EtwPayload.ToAddress(data.PayloadByName("saddr")) is not { } a || EtwPayload.ToAddress(data.PayloadByName("daddr")) is not { } b) return;
        if (_classifier.ExitOf(a, b) is not { } exit) return;
        var size = EtwPayload.ToLong(data.PayloadByName("size"));
        if (size <= 0) return;

        var pid = (int)EtwPayload.ToLong(data.PayloadByName("PID")); // the owning process, not the one that logged the event
        _names.NameOf(pid); // resolved now, while the process most likely still exists
        var send = KernelNetworkEvents.IsSend(id);
        lock (_gate)
        {
            _table.TryGetValue((pid, exit), out var total);
            _table[(pid, exit)] = send ? (total.Up + size, total.Down) : (total.Up, total.Down + size);
        }
    }

    void Process(TraceEventSession session)
    {
        try
        {
            session.Source.Process(); // blocks until the session is disposed or stopped
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lastError = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_session, session)) _running = false; // a newer session must not be marked stopped by an old thread
        }
    }

    void StopSession()
    {
        var session = _session;
        _session = null;
        _running = false;
        try
        {
            session?.Dispose();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // already gone
        }
    }
}
```

- [ ] **Step 7: Build and run the whole suite**

Run: `dotnet build NetRouteWidget.slnx` then `dotnet test tests/NetRoute.Core.Tests`
Expected: `Build succeeded.` and `Failed: 0`. If `Source.Dynamic.All`, `PayloadByName` or `TraceEventSession` do not compile against 3.2.8, fix the call to the API the package actually exposes (the probe project from Task 1 compiles the same calls, copy from it).

- [ ] **Step 8: Commit**

```bash
git add src/NetRoute.Core THIRD-PARTY-NOTICES.md tests/NetRoute.Core.Tests/EtwPayloadTests.cs tests/NetRoute.Core.Tests/ProcessNameCacheTests.cs tests/NetRoute.Core.Tests/KernelNetworkEventsTests.cs
git commit -m "feat(core): WindowsTrafficTracker - per-application bytes from the kernel network trace

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Wire it into the app: tracker, banner, card live speed, docs

**Files:**
- Create: `src/NetRoute.Core/SmartRouting/UsageBanner.cs`
- Test: `tests/NetRoute.Core.Tests/UsageBannerTests.cs`
- Modify: `src/NetRoute.App/App.xaml.cs` (controller construction line 349-351; `RenderLiveSpeeds` line 403-414; `RenderSmartWindowAsync` line 495)
- Modify: `src/NetRoute.App/SmartRoutingWindow.xaml.cs` (line 54-55)
- Modify: `src/NetRoute.App/UsageTab.xaml.cs` (lines 60 and 70)
- Modify: `README.md`

**Interfaces:**
- Consumes: `WindowsTrafficTracker` (Task 5), `SmartRoutingController.IsMeasuring` (Task 4).
- Produces: `static class UsageBanner { string? Text(bool smartRecording, bool windowsTracking) }`; `UsageTab.Render(..., bool recording, bool windowsTracking)` and `SmartRoutingWindow.RenderUsage(..., bool recording, bool windowsTracking)`.

- [ ] **Step 1: Write the failing banner tests**

`tests/NetRoute.Core.Tests/UsageBannerTests.cs`:

```csharp
namespace NetRoute.Core.Tests;

public class UsageBannerTests
{
    [Fact]
    public void While_smart_routing_records_there_is_no_banner()
    {
        Assert.Null(UsageBanner.Text(smartRecording: true, windowsTracking: false));
        Assert.Null(UsageBanner.Text(smartRecording: true, windowsTracking: true));
    }

    [Fact]
    public void With_smart_off_and_windows_tracing_the_banner_says_usage_is_counted_per_application()
    {
        Assert.Equal("Smart routing is off: usage is counted per application by Windows (no website rows).",
            UsageBanner.Text(smartRecording: false, windowsTracking: true));
    }

    [Fact]
    public void With_neither_the_old_banner_says_usage_is_only_recorded_while_smart_runs()
    {
        Assert.Equal("Usage is recorded only while Smart routing is running. History stays visible.",
            UsageBanner.Text(smartRecording: false, windowsTracking: false));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~UsageBannerTests"`
Expected: build FAIL (`UsageBanner` does not exist).

- [ ] **Step 3: Implement `UsageBanner`**

`src/NetRoute.Core/SmartRouting/UsageBanner.cs`:

```csharp
namespace NetRoute.Core;

/// The line at the top of the Usage tab that says how the numbers are being recorded right now.
public static class UsageBanner
{
    public const string SmartOnlyText = "Usage is recorded only while Smart routing is running. History stays visible.";
    public const string WindowsText = "Smart routing is off: usage is counted per application by Windows (no website rows).";

    /// Null while Smart routing itself is recording (nothing to explain).
    public static string? Text(bool smartRecording, bool windowsTracking) =>
        smartRecording ? null : windowsTracking ? WindowsText : SmartOnlyText;
}
```

Run: `dotnet test tests/NetRoute.Core.Tests --filter "FullyQualifiedName~UsageBannerTests"`
Expected: PASS.

- [ ] **Step 4: Use it in the Usage tab and window**

`src/NetRoute.App/UsageTab.xaml.cs` — change the `Render` signature (line 60) and the banner line (line 70):

```csharp
    public void Render(UsageReportModel model, UsageSort sort, string statusText, bool recording, bool windowsTracking)
```
```csharp
        if (UsageBanner.Text(recording, windowsTracking) is { } bannerText) banner.Add(bannerText);
```
(replacing `if (!recording) banner.Add("Usage is recorded only while Smart routing is running. History stays visible.");`).

`src/NetRoute.App/SmartRoutingWindow.xaml.cs` lines 54-55:

```csharp
    public void RenderUsage(UsageReportModel model, UsageSort sort, string statusText, bool recording, bool windowsTracking) =>
        Usage.Render(model, sort, statusText, recording, windowsTracking);
```

- [ ] **Step 5: Wire the tracker and the live speeds in `App.xaml.cs`**

Controller construction (lines 349-351):

```csharp
            var smart = new SmartRoutingController(
                _catalog, host, (port, secret) => new SingBoxApi(port, secret), FreePort.Next, usage.Usage, log.Info,
                counters: new WindowsAdapterCounters(), traffic: new WindowsTrafficTracker());
```

`RenderLiveSpeeds` (line 407) — keep the live figures while Windows tracing counts; replace the condition with:

```csharp
        if (smart.Status.State is not (SmartState.Running or SmartState.Starting) && !smart.IsMeasuring)
```
and update its doc comment's second sentence to: `Blank whenever neither Smart routing nor Windows tracing is counting, so a stale figure never lingers.`

`RenderSmartWindowAsync` (line 495):

```csharp
                window.RenderUsage(report, _usageSort, SmartRoutingPresenter.Row(status).Text,
                    recording: status.State == SmartState.Running, windowsTracking: smart.IsMeasuring);
```

- [ ] **Step 6: Update the README**

Run: `grep -n -i "only while Smart\|recorded only\|while Smart routing is running\|Usage" README.md` and, in the usage section, replace any sentence that says usage is recorded only while Smart routing is running with: `Usage is recorded per application whether Smart routing is on or off. With Smart on, sing-box counts it (including website rows such as YouTube); with Smart off, Windows network tracing counts it per application (no website rows; the widget needs administrator rights for this).`

- [ ] **Step 7: Build and run everything**

Run: `dotnet build NetRouteWidget.slnx` then `dotnet test tests/NetRoute.Core.Tests`
Expected: `Build succeeded.` with 0 warnings and `Failed: 0`.

- [ ] **Step 8: Commit**

```bash
git add src/NetRoute.Core/SmartRouting/UsageBanner.cs tests/NetRoute.Core.Tests/UsageBannerTests.cs src/NetRoute.App README.md
git commit -m "feat(app): show and count usage while Smart routing is off (Windows tracing)

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Accuracy script, spec touch-ups, remove the probe, final check

**Files:**
- Create: `tools/Check-UsageWhileSmartOff.ps1`
- Modify: `docs/superpowers/specs/2026-10-14-netroute-v5-1-usage-while-smart-off-design.md`
- Delete: `tools/EtwProbe/`

- [ ] **Step 1: Create the accuracy script**

`tools/Check-UsageWhileSmartOff.ps1`:

```powershell
<#
.SYNOPSIS
  Checks usage tracking while Smart routing is OFF: downloads a known number of bytes with curl.exe and compares them with
  what the widget recorded for the curl.exe row in %AppData%\NetRouteWidget\usage.json.
.DESCRIPTION
  Run it with Smart routing OFF and the widget running elevated, on a quiet connection (other programs do not affect the
  curl.exe row, but they do slow nothing down either). It only reads usage.json and settings.json.
  PASS = the curl.exe row (phone + LAN) grew by 90%..110% of the downloaded bytes.
#>
param(
    [string]$Url = 'https://speed.cloudflare.com/__down?bytes=20000000',
    [int]$Count = 1,
    [string]$RowKey = 'app:curl.exe',
    [int]$WaitSeconds = 40
)

$ErrorActionPreference = 'Stop'
$usageFile = Join-Path $env:APPDATA 'NetRouteWidget\usage.json'
$settingsFile = Join-Path $env:APPDATA 'NetRouteWidget\settings.json'
$today = (Get-Date).ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)

function Get-Recorded {
    if (-not (Test-Path -LiteralPath $usageFile)) { return 0L }
    $json = Get-Content -LiteralPath $usageFile -Raw | ConvertFrom-Json
    $day = $json.days.$today
    if (-not $day) { return 0L }
    $row = $day.rows.$RowKey
    if (-not $row) { return 0L }
    return [long]$row.phone.up + [long]$row.phone.down + [long]$row.lan.up + [long]$row.lan.down
}

if (-not (Get-Process NetRouteWidget -ErrorAction SilentlyContinue)) { throw 'The widget is not running.' }
if (Test-Path -LiteralPath $settingsFile) {
    $settings = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
    if ($settings.SmartRouting.Enabled) { throw 'Smart routing is ON. Turn it off first: this checks the Windows tracing path.' }
}

Write-Host "Waiting for the widget to save the current numbers..."
Start-Sleep -Seconds $WaitSeconds
$before = Get-Recorded

$downloaded = 0L
for ($i = 1; $i -le $Count; $i++) {
    $bytes = & curl.exe -s -L -o NUL -w '%{size_download}' --max-time 60 $Url
    $downloaded += [long]$bytes
}
Write-Host ("Downloaded {0:N0} bytes in {1} request(s) from {2}" -f $downloaded, $Count, $Url)

Write-Host "Waiting for the widget to save (up to $WaitSeconds s)..."
Start-Sleep -Seconds $WaitSeconds
$after = Get-Recorded
$grew = $after - $before
$ratio = if ($downloaded -gt 0) { $grew / $downloaded } else { 0 }
Write-Host ("Recorded growth of '{0}': {1:N0} bytes = {2:P0} of the download" -f $RowKey, $grew, $ratio)

if ($ratio -ge 0.9 -and $ratio -le 1.1) { Write-Host 'PASS' -ForegroundColor Green; exit 0 }
Write-Host 'FAIL: outside 90%..110%. Is the widget elevated (see the log for "Usage tracking without Smart routing is unavailable")? If it still fails, send usage.json and the log.' -ForegroundColor Red
exit 1
```

- [ ] **Step 2: Update the spec**

In the spec, make these three edits:
1. Status line → `**Status:** Approved; implemented by `docs/superpowers/plans/2026-10-14-netroute-v5-1-usage-while-smart-off.md`.`
2. In "Aggregation": replace "lock-free table" with "table updated under a short lock" (the trace thread takes the lock for one dictionary update; `Drain` swaps the table).
3. In "Exit classification": say a packet counts for an exit when one of its addresses is **any IPv4 or IPv6 address of that adapter** (looked up by adapter name on every apply), not only the detected IPv4; and add to "Open risks": "Packets NATed for VMs/WSL carry no owning process and may be missing, so the per-app total can be below the adapter total."

- [ ] **Step 3: Remove the throwaway probe**

```bash
git rm -r tools/EtwProbe
```

- [ ] **Step 4: Full verification**

Run: `dotnet build NetRouteWidget.slnx --no-incremental` then `dotnet test tests/NetRoute.Core.Tests`
Expected: `Build succeeded.`, 0 warnings, `Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add tools/Check-UsageWhileSmartOff.ps1 docs/superpowers/specs/2026-10-14-netroute-v5-1-usage-while-smart-off-design.md
git commit -m "test(v5.1): accuracy script for usage while Smart is off; spec updated; probe removed

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

- [ ] **Step 6: Hand the build to the user for the real-machine check**

Publish to the scratchpad staging folder as before (`dotnet publish src/NetRoute.App -c Release -r win-x64 --self-contained false -o <staging>`), confirm the staged folder contains `NetRouteWidget.exe` and the TraceEvent files, and give the user the admin install commands (stop the widget first: `Stop-Process -Name NetRouteWidget -Force; Stop-Process -Name sing-box -Force -ErrorAction SilentlyContinue`, then publish to `$env:ProgramFiles\NetRouteWidget` and start it). Then ask the user to: turn Smart off, run `.\tools\Check-UsageWhileSmartOff.ps1` from an administrator PowerShell, and open the Usage tab (banner text, rows growing, live speeds on the card).
