using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace NetRoute.Core.Windows;

/// Client for sing-box's local Clash API (127.0.0.1, bearer secret).
public sealed class SingBoxApi : ISingBoxApi, IDisposable
{
    readonly HttpClient _http;

    public SingBoxApi(int port, string secret)
    {
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = TimeSpan.FromSeconds(3),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
    }

    public async Task<bool> SelectAsync(string group, string outbound, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PutAsJsonAsync($"proxies/{Uri.EscapeDataString(group)}", new { name = outbound }, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<SingBoxConnection>?> GetConnectionsAsync(CancellationToken ct = default)
    {
        try
        {
            return ParseConnections(await _http.GetStringAsync("connections", ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    /// Entries that are not shaped as expected are skipped rather than failing the whole poll.
    internal static IReadOnlyList<SingBoxConnection> ParseConnections(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("connections", out var list) || list.ValueKind != JsonValueKind.Array) return [];

        var result = new List<SingBoxConnection>();
        foreach (var c in list.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object
                || !c.TryGetProperty("metadata", out var meta) || meta.ValueKind != JsonValueKind.Object
                || !c.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String
                || !c.TryGetProperty("chains", out var chainsElement) || chainsElement.ValueKind != JsonValueKind.Array
                || !TryGetLong(c, "upload", out var upload) || !TryGetLong(c, "download", out var download))
                continue;

            var chains = new List<string>();
            foreach (var x in chainsElement.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String) chains.Add(x.GetString()!);

            var host = GetString(meta, "host");
            var path = GetString(meta, "processPath");
            result.Add(new SingBoxConnection(idElement.GetString()!, host, path is null ? null : Path.GetFileName(path), chains, upload, download));
        }
        return result;
    }

    static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s ? s : null;

    static bool TryGetLong(JsonElement obj, string name, out long value)
    {
        value = 0;
        return obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out value);
    }

    public void Dispose() => _http.Dispose();
}
