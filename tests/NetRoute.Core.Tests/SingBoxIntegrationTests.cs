using System.Diagnostics;

namespace NetRoute.Core.Tests;

/// Uses the real, pinned sing-box binary. Never creates a TUN or touches routing.
[Trait("Category", "Integration")]
public partial class SingBoxIntegrationTests
{
    internal static string SingBoxExe => Path.Combine(AppContext.BaseDirectory, "sing-box", "sing-box.exe");

    internal static (int ExitCode, string Output) RunSingBox(params string[] args)
    {
        var psi = new ProcessStartInfo(SingBoxExe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(30_000)) { p.Kill(true); Assert.Fail("sing-box timed out"); }
        return (p.ExitCode, output + err.Result);
    }

    [Fact]
    public void Pinned_sing_box_ships_next_to_the_assembly()
    {
        Assert.True(File.Exists(SingBoxExe), $"missing {SingBoxExe}");
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "sing-box", "LICENSE")));
        var copying = Path.Combine(AppContext.BaseDirectory, "sing-box", "COPYING");
        Assert.True(File.Exists(copying), $"missing {copying}");
        Assert.StartsWith("GNU GENERAL PUBLIC LICENSE", File.ReadAllText(copying).TrimStart());
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md")));

        var (exit, output) = RunSingBox("version");

        Assert.Equal(0, exit);
        Assert.Contains("sing-box version 1.14.2", output);
        Assert.Contains("with_clash_api", output);
    }

    [Fact]
    public void Built_config_passes_sing_box_check()
    {
        var config = SingBoxConfigBuilder.Build(new SingBoxConfigInput(
            RuleSet.Build(RuleCatalog.Load(RuleCatalog.DefaultPath), new SmartRoutingSettings()),
            "Ethernet 5", "Ethernet", "192.168.86.1", RouteExit.Phone, false, 41234, "s3cret"));
        var path = Path.Combine(Path.GetTempPath(), $"netroute-check-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, config.Json);
        try
        {
            var (exit, output) = RunSingBox("check", "-c", path);
            Assert.True(exit == 0, output);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Config_with_process_rules_passes_sing_box_check()
    {
        var rules = new RuleSet([new RuleEntry("app", "My App", ["My App.exe", "qbittorrent.exe"], ["example.com"])]);
        var config = SingBoxConfigBuilder.Build(new SingBoxConfigInput(
            rules, "Ethernet 5", "Ethernet", "192.168.86.1", RouteExit.Phone, false, 41234, "s3cret"));
        var path = Path.Combine(Path.GetTempPath(), $"netroute-check-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, config.Json);
        try
        {
            var (exit, output) = RunSingBox("check", "-c", path);
            Assert.True(exit == 0, output);
        }
        finally { File.Delete(path); }
    }

    /// The rule uses THIS test process's file name in the WRONG case: sing-box's process_name would not match it,
    /// the case-insensitive path regex must. Loopback proxy only, no TUN; the target is an unrouted TEST-NET address
    /// and the "lan" outbound is bound to a missing adapter, so nothing leaves the machine.
    [Fact]
    public async Task Process_path_regex_matches_the_process_name_in_the_wrong_case()
    {
        var proxyPort = NetRoute.Core.Windows.FreePort.Next();
        var apiPort = NetRoute.Core.Windows.FreePort.Next();
        var wrongCase = Path.GetFileName(Environment.ProcessPath!).ToUpperInvariant();
        Assert.NotEqual(Path.GetFileName(Environment.ProcessPath!), wrongCase); // otherwise the test proves nothing
        var regex = System.Text.Json.JsonSerializer.Serialize(SingBoxConfigBuilder.ProcessPathRegex(wrongCase));
        var config = $$"""
            { "log": { "level": "debug", "timestamp": true },
              "inbounds": [ { "type": "mixed", "tag": "mixed-in", "listen": "127.0.0.1", "listen_port": {{proxyPort}} } ],
              "outbounds": [
                { "type": "direct", "tag": "phone" },
                { "type": "direct", "tag": "lan", "bind_interface": "NoSuchAdapter" },
                { "type": "selector", "tag": "lan-only", "outbounds": ["lan", "phone"], "default": "lan" },
                { "type": "selector", "tag": "default", "outbounds": ["phone", "lan"], "default": "phone" } ],
              "route": { "rules": [ { "action": "sniff" }, { "process_path_regex": [{{regex}}], "outbound": "lan-only" } ], "final": "default" },
              "experimental": { "clash_api": { "external_controller": "127.0.0.1:{{apiPort}}", "secret": "itest" } } }
            """;
        var host = new NetRoute.Core.Windows.SingBoxHost(SingBoxExe);
        var events = new System.Collections.Concurrent.ConcurrentQueue<SingBoxLogEvent>();
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        host.LineReceived += l => { lines.Enqueue(l); if (SingBoxLogParser.Parse(l) is { } e) events.Enqueue(e); };
        using var api = new NetRoute.Core.Windows.SingBoxApi(apiPort, "itest");

        await host.StartAsync(config);
        try
        {
            Assert.True(SpinWait.SpinUntil(() => api.GetConnectionsAsync().Result is not null, TimeSpan.FromSeconds(10)), "API never came up");
            using var client = new HttpClient(new HttpClientHandler { Proxy = new System.Net.WebProxy($"http://127.0.0.1:{proxyPort}"), UseProxy = true })
                { Timeout = TimeSpan.FromSeconds(10) };

            await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync("http://192.0.2.1:81/")); // lan-only -> missing adapter

            Assert.True(SpinWait.SpinUntil(() => events.OfType<RuleMatched>().Any(m => m.Outbound == "lan-only"), TimeSpan.FromSeconds(10)),
                "the process rule never matched; sing-box said: " + string.Join(" | ", lines));
            Assert.Equal(1, events.OfType<RuleMatched>().First(m => m.Outbound == "lan-only").RuleIndex);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Host_api_and_parser_work_end_to_end_through_a_local_proxy()
    {
        var proxyPort = NetRoute.Core.Windows.FreePort.Next();
        var apiPort = NetRoute.Core.Windows.FreePort.Next();
        var config = $$"""
            { "log": { "level": "debug", "timestamp": true },
              "inbounds": [ { "type": "mixed", "tag": "mixed-in", "listen": "127.0.0.1", "listen_port": {{proxyPort}} } ],
              "outbounds": [
                { "type": "direct", "tag": "phone" },
                { "type": "direct", "tag": "lan", "bind_interface": "NoSuchAdapter" },
                { "type": "selector", "tag": "lan-only", "outbounds": ["lan", "phone"], "default": "lan" },
                { "type": "selector", "tag": "default", "outbounds": ["phone", "lan"], "default": "phone" } ],
              "route": { "rules": [ { "action": "sniff" }, { "domain_suffix": ["example.com"], "outbound": "lan-only" } ], "final": "default" },
              "experimental": { "clash_api": { "external_controller": "127.0.0.1:{{apiPort}}", "secret": "itest" } } }
            """;
        var host = new NetRoute.Core.Windows.SingBoxHost(SingBoxExe);
        var events = new System.Collections.Concurrent.ConcurrentQueue<SingBoxLogEvent>();
        var exits = 0;
        host.LineReceived += l => { if (SingBoxLogParser.Parse(l) is { } e) events.Enqueue(e); };
        host.Exited += _ => Interlocked.Increment(ref exits);
        using var api = new NetRoute.Core.Windows.SingBoxApi(apiPort, "itest");

        await host.StartAsync(config);
        try
        {
            Assert.True(host.IsRunning);
            Assert.True(SpinWait.SpinUntil(() => api.GetConnectionsAsync().Result is not null, TimeSpan.FromSeconds(10)), "API never came up");
            using var client = new HttpClient(new HttpClientHandler { Proxy = new System.Net.WebProxy($"http://127.0.0.1:{proxyPort}"), UseProxy = true })
                { Timeout = TimeSpan.FromSeconds(15) };

            await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync("https://example.com/")); // lan-only -> missing adapter
            Assert.True(SpinWait.SpinUntil(() => events.OfType<DialFailed>().Any(), TimeSpan.FromSeconds(10)));
            var matched = events.OfType<RuleMatched>().FirstOrDefault(m => m.Outbound == "lan-only");
            Assert.True(matched is not null, "no RuleMatched for lan-only; parsed events: " + string.Join(" | ", events));
            Assert.Equal(1, matched.RuleIndex);
            Assert.Contains(events.OfType<DialFailed>(), f => f.ConnectionId == matched.ConnectionId && f.Outbound == "lan-only");

            Assert.True(await api.SelectAsync("lan-only", "phone"));
            using var ok = await client.GetAsync("https://example.com/"); // now via the working direct outbound
            Assert.True(ok.IsSuccessStatusCode);
        }
        finally
        {
            await host.StopAsync();
        }

        Assert.False(host.IsRunning);
        Assert.Equal(1, exits); // raised before StopAsync returned
    }

    static string LoopbackOnlyConfig() => $$"""
        { "log": { "level": "info" },
          "inbounds": [ { "type": "mixed", "tag": "mixed-in", "listen": "127.0.0.1", "listen_port": {{NetRoute.Core.Windows.FreePort.Next()}} } ],
          "outbounds": [ { "type": "direct", "tag": "direct" } ] }
        """;

    [Fact]
    public async Task Started_sing_box_is_in_the_kill_on_close_job()
    {
        var host = new NetRoute.Core.Windows.SingBoxHost(SingBoxExe);
        await host.StartAsync(LoopbackOnlyConfig());
        try
        {
            var process = host.CurrentProcess;
            Assert.NotNull(process);
            Assert.True(host.KillOnParentExit);
            var job = NetRoute.Core.Windows.JobObjectNative.SharedKillOnCloseJob;
            Assert.NotEqual(IntPtr.Zero, job);
            // Query our own job (not "any job"): a test runner may itself live in one.
            Assert.True(NetRoute.Core.Windows.JobObjectNative.IsProcessInJob(process!.Handle, job, out var inJob));
            Assert.True(inJob);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Config_is_fed_on_stdin_and_a_throwing_subscriber_breaks_nothing()
    {
        var host = new NetRoute.Core.Windows.SingBoxHost(SingBoxExe);
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var exits = 0;
        host.LineReceived += _ => throw new InvalidOperationException("bad subscriber");
        host.LineReceived += lines.Enqueue; // still called after the throwing one
        host.Exited += _ => throw new InvalidOperationException("bad subscriber");
        host.Exited += _ => Interlocked.Increment(ref exits);

        await host.StartAsync(LoopbackOnlyConfig());
        Assert.True(SpinWait.SpinUntil(() => !lines.IsEmpty, TimeSpan.FromSeconds(10)), "sing-box printed nothing, so it did not start from stdin");
        Assert.True(host.IsRunning);
        await host.StopAsync();

        Assert.False(host.IsRunning);
        Assert.Equal(1, exits);
    }

    [Fact]
    public async Task Natural_exit_raises_Exited_once_with_the_exit_code_and_stop_is_idempotent()
    {
        var host = new NetRoute.Core.Windows.SingBoxHost(SingBoxExe);
        var codes = new System.Collections.Concurrent.ConcurrentQueue<int>();
        host.Exited += codes.Enqueue;

        await host.StartAsync("{ this is not json"); // sing-box rejects it and exits by itself; nothing is started

        Assert.True(SpinWait.SpinUntil(() => !codes.IsEmpty, TimeSpan.FromSeconds(10)), "Exited never fired");
        Assert.False(host.IsRunning);
        await host.StopAsync();
        await host.StopAsync(); // twice is safe
        Assert.Single(codes);
        Assert.NotEqual(0, codes.Single());

        await host.StartAsync(LoopbackOnlyConfig()); // restartable after a natural exit
        Assert.True(host.IsRunning);
        await host.StopAsync();
        await host.StopAsync();
        Assert.Equal(2, codes.Count);
    }

    [Fact]
    public void KillOrphans_ignores_processes_that_do_not_match_the_path()
    {
        var before = System.Diagnostics.Process.GetProcessesByName("sing-box").Select(p => p.Id).ToHashSet();

        Assert.Equal(0, NetRoute.Core.Windows.SingBoxHost.KillOrphans(Path.Combine(Path.GetTempPath(), "no-such-dir", "sing-box.exe")));

        var after = System.Diagnostics.Process.GetProcessesByName("sing-box").Select(p => p.Id).ToHashSet();
        Assert.True(before.SetEquals(after), "KillOrphans must not touch sing-box processes from another path");
    }
}
