namespace NetRoute.Core;

public sealed record ApplyResult(bool Success, string? Error)
{
    public static readonly ApplyResult Ok = new(true, null);
}

/// Sets interface metrics for a mode and verifies them by reading back.
/// Never deletes routes: non-main adapters keep their default route as fallback.
public sealed class RoutingEngine(IInterfaceMetrics metrics)
{
    static readonly IpFamily[] Families = [IpFamily.IPv4, IpFamily.IPv6];

    public ApplyResult Apply(RoutingMode mode, DetectionResult adapters, ConnectionId? carrier = null) =>
        ApplyTargets(Targets(adapters, MetricPolicy.For(mode, carrier)));

    /// Hands an adapter back to Windows' automatic metric.
    public ApplyResult Reset(AdapterInfo adapter) => ApplyTargets([(adapter, null)]);

    /// Whether every family of the adapter is already on the automatic metric (an unknown family counts as automatic).
    public bool IsAutomatic(AdapterInfo adapter) =>
        Families.All(family => metrics.Get(adapter.Index, family) is not { UseAutomatic: false });

    public bool IsInSync(RoutingMode mode, DetectionResult adapters, ConnectionId? carrier = null)
    {
        foreach (var (adapter, metric) in Targets(adapters, MetricPolicy.For(mode, carrier)))
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

    /// An absent connection doesn't hold a backup grade: re-grade the present backups in rank order,
    /// so a missing one never leaves a gap (e.g. Lan main with no Wi-Fi still grades Phone 50, not 60).
    static IEnumerable<(AdapterInfo, uint?)> Targets(DetectionResult adapters, IReadOnlyDictionary<ConnectionId, uint?> target)
    {
        uint[] grades = [MetricPolicy.Backup, MetricPolicy.SecondBackup];
        var backups = adapters.Present.Where(p => target[p.Id] is > MetricPolicy.Preferred)
            .OrderBy(p => target[p.Id]).Select(p => p.Id).ToList();
        return adapters.Present.Select(p =>
            (p.Adapter, target[p.Id] is > MetricPolicy.Preferred ? grades[backups.IndexOf(p.Id)] : target[p.Id]));
    }
}
