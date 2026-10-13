namespace NetRoute.Core.PathMap;

/// A rolling window of the samples of one device, kept sorted by the time each probe was sent. Thread-safe: probes add while the screen reads snapshots.
public sealed class PathSamples(TimeSpan window)
{
    readonly List<Sample> _samples = [];
    readonly Lock _gate = new();

    public void Add(Sample sample)
    {
        lock (_gate)
        {
            // Stamped when sent, stored when finished: a slow failed check can arrive after a later fast reply. Keep the list in
            // time order (equal times keep their arrival order) so "newest" and the consecutive runs mean what they say.
            var at = _samples.Count;
            while (at > 0 && _samples[at - 1].At > sample.At) at--;
            _samples.Insert(at, sample);
            Prune(_samples[^1].At);
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
