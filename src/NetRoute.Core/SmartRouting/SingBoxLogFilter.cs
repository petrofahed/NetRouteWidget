using System.Text.RegularExpressions;

namespace NetRoute.Core;

/// Decides which sing-box output lines are worth writing to the widget log. A plain substring test is wrong:
/// every DNS answer line contains "NOERROR", which would flood the log and eat the rate limiter's budget.
public static partial class SingBoxLogFilter
{
    public static bool IsNoteworthy(string line) => Level().IsMatch(Ansi().Replace(line, ""));

    [GeneratedRegex(@"\x1b\[[0-9;]*m")] private static partial Regex Ansi();
    [GeneratedRegex(@"\b(ERROR|WARN|FATAL)\b")] private static partial Regex Level();
}
