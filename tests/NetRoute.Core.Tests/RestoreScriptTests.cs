using System.Diagnostics;

namespace NetRoute.Core.Tests;

/// Runs the break-glass script in dry-run mode only; it must not change anything.
[Trait("Category", "Integration")]
public class RestoreScriptTests
{
    [Fact]
    public void Dry_run_reports_and_changes_nothing()
    {
        var script = Path.Combine(RepoRoot(), "tools", "Restore-Network.cmd");
        Assert.True(File.Exists(script), $"missing {script}");
        var before = MetricSnapshot();

        var (exitCode, output) = Run("cmd.exe", "/c", script, "/check");

        Assert.Equal(0, exitCode);
        Assert.Contains("Dry run", output);
        Assert.Contains("settings file:", output);
        Assert.Matches("would restore|nothing to restore", output);
        Assert.Contains("startup task:", output);
        Assert.Contains("sing-box:", output);
        Assert.Matches(@"NetRoute adapter: (present|absent)", output); // a leftover TUN is what kills the internet
        Assert.Equal(before, MetricSnapshot());
    }

    // ---- the settings rewrite step, run against a scratch %APPDATA% (the cmd itself is never executed) ----

    /// The PowerShell command of the "saved mode to Auto" step, lifted out of the script so the test exercises the real text.
    static string SettingsRewriteSnippet()
    {
        var line = File.ReadAllLines(Path.Combine(RepoRoot(), "tools", "Restore-Network.cmd"))
            .Single(l => l.TrimStart().StartsWith("powershell", StringComparison.OrdinalIgnoreCase) && l.Contains("$s.Mode = 'Auto'"));
        var m = System.Text.RegularExpressions.Regex.Match(line, "-Command \"(?<script>.*)\"\\s*$");
        Assert.True(m.Success, "could not find the -Command script in: " + line);
        return m.Groups["script"].Value;
    }

    static (string Output, string SettingsText) RunRewrite(string? initial, bool bom = false)
    {
        var appData = Directory.CreateTempSubdirectory("netroute-restore-").FullName;
        try
        {
            var dir = Path.Combine(appData, "NetRouteWidget");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "settings.json");
            if (initial is not null) File.WriteAllText(file, initial, new System.Text.UTF8Encoding(bom));
            var psi = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(SettingsRewriteSnippet());
            psi.Environment["APPDATA"] = appData;
            using var process = Process.Start(psi)!;
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEndAsync();
            Assert.True(process.WaitForExit(60_000), "powershell timed out");
            Assert.Equal(0, process.ExitCode);
            return (stdout.Result + stderr.Result, File.Exists(file) ? File.ReadAllText(file) : "");
        }
        finally { Directory.Delete(appData, recursive: true); }
    }

    const string RichSettings = """
        { "Mode": "Phone", "PhoneOverride": "SAMSUNG", "CardLeft": 12.5, "CardVisible": false, "StartWithWindows": true,
          "SmartRouting": { "Enabled": true, "LastLanInterface": "Ethernet",
                            "Items": { "youtube": false },
                            "UserRules": [ { "Type": "App", "Value": "a.exe", "Enabled": true } ] } }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Settings_rewrite_sets_mode_auto_and_switches_smart_routing_off_keeping_everything_else(bool bom)
    {
        var (_, text) = RunRewrite(RichSettings, bom);

        var root = System.Text.Json.Nodes.JsonNode.Parse(text.TrimStart('\uFEFF'))!;
        Assert.Equal("Auto", (string)root["Mode"]!);
        Assert.False((bool)root["SmartRouting"]!["Enabled"]!);
        Assert.Equal("SAMSUNG", (string)root["PhoneOverride"]!);
        Assert.Equal(12.5, (double)root["CardLeft"]!);
        Assert.False((bool)root["CardVisible"]!);
        Assert.True((bool)root["StartWithWindows"]!);
        Assert.Equal("Ethernet", (string)root["SmartRouting"]!["LastLanInterface"]!);
        Assert.False((bool)root["SmartRouting"]!["Items"]!["youtube"]!);
        var rule = Assert.Single(root["SmartRouting"]!["UserRules"]!.AsArray())!;
        Assert.Equal(("App", "a.exe", true), ((string)rule["Type"]!, (string)rule["Value"]!, (bool)rule["Enabled"]!));
    }

    [Fact]
    public void Settings_rewrite_without_smart_routing_still_works_and_adds_no_structure()
    {
        var (_, text) = RunRewrite("""{ "Mode": "Lan", "CardLeft": 3 }""");

        var root = System.Text.Json.Nodes.JsonNode.Parse(text.TrimStart('\uFEFF'))!.AsObject();
        Assert.Equal("Auto", (string)root["Mode"]!);
        Assert.False(root.ContainsKey("SmartRouting"));
        Assert.Equal(3, (int)root["CardLeft"]!);
    }

    [Fact]
    public void Settings_rewrite_with_a_null_smart_routing_does_not_fail()
    {
        var (_, text) = RunRewrite("""{ "Mode": "Phone", "SmartRouting": null }""");

        Assert.Equal("Auto", (string)System.Text.Json.Nodes.JsonNode.Parse(text.TrimStart('\uFEFF'))!["Mode"]!);
    }

    [Fact]
    public void Settings_rewrite_replaces_a_garbage_file_with_mode_auto()
    {
        var (output, text) = RunRewrite("{ this is not json");

        Assert.Equal("""{"Mode":"Auto"}""", text.TrimStart('\uFEFF').Trim());
        Assert.Contains("unreadable", output);
    }

    static string MetricSnapshot() =>
        Run("powershell.exe", "-NoProfile", "-Command",
            "Get-NetIPInterface | Sort-Object ifIndex, AddressFamily | ForEach-Object { '{0},{1},{2},{3}' -f $_.ifIndex, $_.AddressFamily, $_.InterfaceMetric, $_.AutomaticMetric }").Output;

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root (global.json) not found");
    }

    static (int ExitCode, string Output) Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"{file} timed out");
        }
        return (process.ExitCode, stdout.Result + stderr.Result);
    }
}
