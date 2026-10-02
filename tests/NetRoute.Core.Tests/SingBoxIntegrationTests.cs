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
}
