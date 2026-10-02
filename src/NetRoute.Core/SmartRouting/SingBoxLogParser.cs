using System.Text.RegularExpressions;

namespace NetRoute.Core;

public abstract record SingBoxLogEvent(string ConnectionId);
public sealed record RuleMatched(string ConnectionId, int RuleIndex, string Outbound) : SingBoxLogEvent(ConnectionId);
public sealed record DialFailed(string ConnectionId, string Outbound) : SingBoxLogEvent(ConnectionId);

/// Parses the two sing-box 1.14 debug lines Smart routing needs (format captured from the real binary).
public static partial class SingBoxLogParser
{
    public static SingBoxLogEvent? Parse(string line)
    {
        var clean = Ansi().Replace(line, "");
        var m = MatchLine().Match(clean);
        if (m.Success)
            return int.TryParse(m.Groups["idx"].Value, out var index)
                ? new RuleMatched(m.Groups["id"].Value, index, m.Groups["out"].Value)
                : null;
        var f = FailLine().Match(clean);
        return f.Success ? new DialFailed(f.Groups["id"].Value, f.Groups["out"].Value) : null;
    }

    [GeneratedRegex(@"\x1b\[[0-9;]*m")] private static partial Regex Ansi();
    [GeneratedRegex(@"\[(?<id>\d+) [^\]]*\] router: match\[(?<idx>\d+)\] .*=> route\((?<out>[^)]+)\)")] private static partial Regex MatchLine();
    [GeneratedRegex(@"ERROR \[(?<id>\d+) [^\]]*\] connection: open connection to \S+ using outbound/\w+\[(?<out>[^\]]+)\]")] private static partial Regex FailLine();
}

/// Remembers which entry each LAN-only connection matched, and reports entries whose LAN-only dials failed.
/// Not thread-safe: callers must serialize all calls (the SmartRoutingController does this under its gate).
public sealed class LanWaitTracker(IReadOnlyDictionary<int, string> ruleIndexToEntryId, TimeProvider? time = null)
{
    const int MaxTrackedConnections = 4096;
    readonly TimeProvider _time = time ?? TimeProvider.System;
    readonly Dictionary<string, string> _entryByConnection = new();
    readonly Dictionary<string, DateTimeOffset> _lastFailure = new();

    public string? Observe(SingBoxLogEvent e)
    {
        switch (e)
        {
            case RuleMatched m when m.Outbound == SingBoxConfigBuilder.LanOnlyTag
                                    && ruleIndexToEntryId.TryGetValue(m.RuleIndex, out var entry):
                if (_entryByConnection.Count >= MaxTrackedConnections) _entryByConnection.Clear();
                _entryByConnection[m.ConnectionId] = entry;
                return null;
            case DialFailed f when _entryByConnection.Remove(f.ConnectionId, out var failed):
                _lastFailure[failed] = _time.GetUtcNow();
                return failed;
            default:
                return null;
        }
    }

    public IReadOnlyList<string> RecentFailures(TimeSpan window)
    {
        var cutoff = _time.GetUtcNow() - window;
        return _lastFailure.Where(kv => kv.Value >= cutoff).Select(kv => kv.Key).Order().ToList();
    }

    public void Clear() => _lastFailure.Clear();
}
