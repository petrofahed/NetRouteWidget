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
            using var response = await _http.PutAsJsonAsync($"proxies/{Uri.EscapeDataString(group)}", new { name = outbound }, ct);
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
            return ParseConnections(await _http.GetStringAsync("connections", ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    internal static IReadOnlyList<SingBoxConnection> ParseConnections(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("connections", out var list) || list.ValueKind != JsonValueKind.Array) return [];

        var result = new List<SingBoxConnection>();
        foreach (var c in list.EnumerateArray())
        {
            var meta = c.GetProperty("metadata");
            var host = meta.TryGetProperty("host", out var h) && h.GetString() is { Length: > 0 } hs ? hs : null;
            var path = meta.TryGetProperty("processPath", out var p) && p.GetString() is { Length: > 0 } ps ? ps : null;
            result.Add(new SingBoxConnection(
                c.GetProperty("id").GetString()!, host, path is null ? null : Path.GetFileName(path),
                c.GetProperty("chains").EnumerateArray().Select(x => x.GetString()!).ToList(),
                c.GetProperty("upload").GetInt64(), c.GetProperty("download").GetInt64()));
        }
        return result;
    }

    public void Dispose() => _http.Dispose();
}
