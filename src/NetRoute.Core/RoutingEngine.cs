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
