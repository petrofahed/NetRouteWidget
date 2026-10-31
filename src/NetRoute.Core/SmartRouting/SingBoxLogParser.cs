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

/// Correlates only-target matches with dial failures; each target has independent recent failures.
public sealed class RuleWaitTracker(IReadOnlyDictionary<int, string> ruleIndexToEntryId,
    IReadOnlyDictionary<int, RuleTarget> ruleIndexToTarget, TimeProvider? time = null)
{
    const int MaxTrackedConnections = 4096;
    readonly TimeProvider _time = time ?? TimeProvider.System;
    readonly Dictionary<string, (string Entry, RuleTarget Target, string Outbound)> _matches = new();
    readonly Dictionary<(RuleTarget Target, string Entry), DateTimeOffset> _failures = new();

    public string? Observe(SingBoxLogEvent e)
    {
        switch (e)
        {
            case RuleMatched match when ruleIndexToEntryId.TryGetValue(match.RuleIndex, out var entry)
                && ruleIndexToTarget.TryGetValue(match.RuleIndex, out var target)
                && match.Outbound == SmartRoutingPolicy.SelectorTag(target):
                if (_matches.Count >= MaxTrackedConnections) _matches.Clear();
                _matches[match.ConnectionId] = (entry, target, match.Outbound);
                return null;
            case DialFailed failure when _matches.Remove(failure.ConnectionId, out var matched) && failure.Outbound == matched.Outbound:
                _failures[(matched.Target, matched.Entry)] = _time.GetUtcNow();
                return matched.Entry;
            default: return null;
        }
    }

    public IReadOnlyList<string> RecentFailures(RuleTarget target, TimeSpan window) => _failures
        .Where(pair => pair.Key.Target == target && pair.Value >= _time.GetUtcNow() - window)
        .Select(pair => pair.Key.Entry).Order().ToList();

    public void Clear(RuleTarget target)
    {
        foreach (var key in _failures.Keys.Where(key => key.Target == target).ToList()) _failures.Remove(key);
        foreach (var key in _matches.Where(pair => pair.Value.Target == target).Select(pair => pair.Key).ToList()) _matches.Remove(key);
    }
}
