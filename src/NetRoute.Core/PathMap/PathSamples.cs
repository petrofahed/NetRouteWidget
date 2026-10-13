namespace NetRoute.Core.PathMap;

/// A rolling window of the samples of one device. Thread-safe: probes add while the screen reads snapshots.
public sealed class PathSamples(TimeSpan window)
{
    readonly List<Sample> _samples = [];
    readonly Lock _gate = new();

    public void Add(Sample sample)
    {
        lock (_gate)
        {
            _samples.Add(sample);
            Prune(sample.At);
        }
    }

    /// The samples still inside the window at 'now', oldest first.
    public IReadOnlyList<Sample> Snapshot(DateTimeOffset now)
    {
        lock (_gate)
        {
            Prune(now);
            return _samples.ToArray();
        }
    }

    void Prune(DateTimeOffset now)
    {
        var cutoff = now - window;
        _samples.RemoveAll(s => s.At < cutoff);
    }
}
