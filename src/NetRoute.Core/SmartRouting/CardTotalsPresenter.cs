namespace NetRoute.Core;

/// The card's per-connection "Total" cells, from the adapter ledger's today totals.
public static class CardTotalsPresenter
{
    /// One label per connection: the human size of today's up + down, or "" when nothing was counted (the cell hides).
    public static IReadOnlyDictionary<ConnectionId, string> Labels(Func<ConnectionId, Traffic> today) =>
        Connections.All.ToDictionary(id => id, id =>
        {
            var traffic = today(id);
            var total = traffic.Up + traffic.Down;
            return total > 0 ? ByteFormat.Human(total) : "";
        });
}
