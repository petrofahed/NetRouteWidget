using System.Net;
using System.Text.RegularExpressions;

namespace NetRoute.Core.PathMap;

/// A device on the local network the user wants to test: a name for the list and an IPv4 address or hostname.
public sealed record LocalDevice(string Name, string Address);

/// The rules for a device the user adds, shared by the add dialog and the settings loader.
public static partial class LocalDeviceValidator
{
    public const int MaxDevices = 20;
    public const int MaxNameLength = 40;
    public const int MaxAddressLength = 253;

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$")]
    private static partial Regex Hostname();

    public static bool TryCreate(string? name, string? address, IReadOnlyList<LocalDevice> existing, out LocalDevice device, out string error)
    {
        device = new LocalDevice("", "");
        var host = (address ?? "").Trim();
        var label = (name ?? "").Trim();

        if (host.Length == 0) return Fail("Enter an IPv4 address or a device name.", out error);
        if (host.Length > MaxAddressLength) return Fail($"The address is longer than {MaxAddressLength} characters.", out error);
        if (host.Contains(':')) return Fail("Only IPv4 addresses and hostnames are supported.", out error);
        if (host.All(c => char.IsAsciiDigit(c) || c == '.'))
        {
            // Numbers and dots only: it has to be a complete IPv4 address, never a "hostname" like 1.2.3.
            if (host.Count(c => c == '.') != 3 || !IPAddress.TryParse(host, out _)) return Fail("That is not a valid IPv4 address.", out error);
        }
        else if (!Hostname().IsMatch(host))
        {
            return Fail("That is not a valid address or device name.", out error);
        }
        if (label.Length > MaxNameLength) return Fail($"The name is longer than {MaxNameLength} characters.", out error);
        if (existing.Count >= MaxDevices) return Fail($"You can test at most {MaxDevices} devices.", out error);
        if (existing.Any(d => string.Equals(d.Address, host, StringComparison.OrdinalIgnoreCase))) return Fail("That address is already in the list.", out error);

        device = new LocalDevice(label.Length == 0 ? host : label, host);
        error = "";
        return true;
    }

    static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    /// A saved list with its invalid and duplicate entries dropped, in order, at most MaxDevices.
    public static IReadOnlyList<LocalDevice> Sanitize(IReadOnlyList<LocalDevice>? saved)
    {
        var kept = new List<LocalDevice>();
        foreach (var entry in saved ?? [])
            if (entry is not null && TryCreate(entry.Name, entry.Address, kept, out var ok, out _)) kept.Add(ok);
        // One shared empty list: AppSettings is a record, and two settings with "no devices" must compare equal.
        return kept.Count == 0 ? Array.Empty<LocalDevice>() : kept;
    }
}
