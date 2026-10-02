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
        var host = new NetRoute.Core.Windows.SingBoxHost(SingBoxExe, Path.Combine(Path.GetTempPath(), $"netroute-itest-{Guid.NewGuid():N}.json"));
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
            var matched = events.OfType<RuleMatched>().First(m => m.Outbound == "lan-only");
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
}
