using System.Net;

namespace NetRoute.Core.Windows;

/// Reads ETW payload values without trusting their .NET type: the same field can arrive as an IPAddress, a number or bytes
/// depending on the event version (the Task 1 probe records what this machine delivers).
public static class EtwPayload
{
    public static IPAddress? ToAddress(object? value) => value switch
    {
        IPAddress ip => ip,
        uint v4 => new IPAddress(v4), // four network-order bytes read as a little-endian number
        int v4 => new IPAddress(unchecked((uint)v4)),
        byte[] { Length: 4 or 16 } bytes => new IPAddress(bytes),
        string text when IPAddress.TryParse(text, out var parsed) => parsed,
        _ => null,
    };

    public static long ToLong(object? value) => value switch
    {
        int i => i,
        uint u => u,
        long l => l,
        ulong ul => ul > long.MaxValue ? long.MaxValue : (long)ul,
        ushort s => s,
        byte b => b,
        _ => 0,
    };
}
