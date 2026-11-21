namespace NetRoute.Core;

/// The card's per-connection "Speed" cells, from the adapter ledger's rates.
public static class CardSpeedPresenter
{
    /// One label per connection: the live speed text, or "" when it is idle (the cell hides).
    public static IReadOnlyDictionary<ConnectionId, string> Labels(IReadOnlyDictionary<ConnectionId, long> rates) =>
        Connections.All.ToDictionary(id => id, id =>
        {
            var rate = rates.GetValueOrDefault(id);
            return rate > 0 ? UsageReport.NowText(rate) : "";
        });
}
