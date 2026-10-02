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
