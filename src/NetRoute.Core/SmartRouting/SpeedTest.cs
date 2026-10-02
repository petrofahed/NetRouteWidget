using System.Globalization;
using System.Net;

namespace NetRoute.Core;

public interface ISpeedProbe
{
    /// Download speed in Mbit/s from the given local address, or null on failure.
    Task<double?> MeasureMbpsAsync(IPAddress source, CancellationToken ct);
}

public sealed record SpeedTestResult(double? PhoneMbps, double? LanMbps);

public static class SpeedTest
{
    public static async Task<SpeedTestResult> RunAsync(ISpeedProbe probe, DetectionResult adapters, CancellationToken ct = default)
    {
        var phone = Measure(probe, adapters.Phone, ct);
        var lan = Measure(probe, adapters.Lan, ct);
        return new SpeedTestResult(await phone, await lan);
    }

    public static string Describe(SpeedTestResult r) => $"⚡ Phone {Mbps(r.PhoneMbps)} · LAN {Mbps(r.LanMbps)}";

    static Task<double?> Measure(ISpeedProbe probe, AdapterInfo? adapter, CancellationToken ct) =>
        adapter?.IPv4 is { } ip ? probe.MeasureMbpsAsync(IPAddress.Parse(ip), ct) : Task.FromResult<double?>(null);

    static string Mbps(double? value) =>
        value is { } v ? $"{v.ToString("0.0", CultureInfo.InvariantCulture)} Mbit/s" : "—";
}
