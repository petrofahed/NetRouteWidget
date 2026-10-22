namespace NetRoute.Core;

public static class MetricPolicy
{
    public const uint Preferred = 5;
    public const uint Backup = 50;
    public const uint SecondBackup = 60;

    /// Desired metric per connection: the main (carrier, or the mode's connection) gets
    /// Preferred; the other connections get graded backups in Connections.FallbackOrder
    /// (skipping the main). Auto with no carrier returns null (automatic) for every connection.
    public static IReadOnlyDictionary<ConnectionId, uint?> For(RoutingMode mode, ConnectionId? carrier = null)
    {
        var main = carrier ?? Connections.Of(mode);
        if (main is null) return Connections.All.ToDictionary(id => id, _ => (uint?)null);

        var result = new Dictionary<ConnectionId, uint?> { [main.Value] = Preferred };
        var grades = new[] { Backup, SecondBackup };
        var i = 0;
        foreach (var id in Connections.FallbackOrder)
        {
            if (id == main.Value) continue;
            result[id] = grades[i++];
        }
        return result;
    }
}
