using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using NetRoute.Core.PathMap;

namespace NetRoute.Core.Windows;

/// Fresh authenticated HTTPS responses, bound to physical IPv4 interfaces. OS DNS is deadline-bounded,
/// but not per-adapter. Pools retain at most three exact source/index bindings; health results are never cached.
public sealed class HttpsInternetProbe(IReadOnlyList<Uri> targets, TimeSpan timeout) : IInternetProbe, IDisposable
{
    readonly Lock _gate = new();
    readonly Dictionary<InternetProbeBinding, Entry> _entries = [];
    readonly CancellationTokenSource _shutdown = new();
    bool _disposed;
    int _activeChecks;
    public static HttpsInternetProbe Default() => new(
        [new("https://www.google.com/generate_204"), new("https://connectivitycheck.grapheneos.network/generate_204")], TimeSpan.FromSeconds(4));

    // Test-local roots retain normal hostname validation. Production uses the system trust store.
    internal X509ChainPolicy? CertificateChainPolicy { get; init; }
    internal Func<string, CancellationToken, Task<IPAddress[]>> Resolve { get; init; } =
        static (host, ct) => Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct);
    internal Action<Socket>? SocketConfigured { get; init; }
    // Synthetic binding keys are used only by pool lifecycle tests, never by App callers.
    internal Func<InternetProbeBinding, bool> BindingExists { get; init; } = static binding =>
        NetworkInterface.GetAllNetworkInterfaces().Any(adapter => adapter.Supports(NetworkInterfaceComponent.IPv4)
            && adapter.GetIPProperties().GetIPv4Properties().Index == binding.InterfaceIndex
            && adapter.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(binding.Source)));

    internal int RetainedBindings { get { lock (_gate) return _entries.Count; } }

    sealed class Entry : IDisposable
    {
        public int Active;
        public long LastUsed;
        public readonly Dictionary<Uri, HttpClient> Clients = [];
        public readonly List<Socket> Sockets = [];
        public void Dispose()
        {
            foreach (var client in Clients.Values) client.Dispose();
            lock (Sockets) { foreach (var socket in Sockets) socket.Dispose(); Sockets.Clear(); }
        }
    }

    // A cancelled SendAsync may finish before its connection worker; prevent late socket creation and
    // close newly-created in-flight sockets on cancellation. Completed pooled connections belong to Entry.
    sealed class RequestSockets
    {
        readonly List<Socket> _sockets = [];
        bool _finished;
        public Socket Create(CancellationToken token)
        {
            lock (_sockets)
            {
                token.ThrowIfCancellationRequested();
                if (_finished) throw new OperationCanceledException(token);
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                _sockets.Add(socket);
                return socket;
            }
        }
        public void Cancel() { lock (_sockets) { _finished = true; foreach (var socket in _sockets) socket.Dispose(); } }
        public void Finish() { lock (_sockets) _finished = true; }
    }
    static readonly HttpRequestOptionsKey<RequestSockets> RequestSocketKey = new("NetRoute.internet.sockets");

    Entry Acquire(InternetProbeBinding binding, out bool retained)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(binding, out var entry))
            {
                if (_entries.Count == 3)
                {
                    var idle = _entries.Where(pair => pair.Value.Active == 0).MinBy(pair => pair.Value.LastUsed);
                    if (idle.Value is not null) { _entries.Remove(idle.Key); idle.Value.Dispose(); }
                }
                entry = new Entry();
                foreach (var target in targets) entry.Clients[target] = CreateClient(binding, entry);
                if (_entries.Count < 3) _entries.Add(binding, entry);
            }
            retained = _entries.TryGetValue(binding, out var saved) && ReferenceEquals(entry, saved);
            entry.Active++;
            _activeChecks++;
            entry.LastUsed = Stopwatch.GetTimestamp();
            return entry;
        }
    }

    HttpClient CreateClient(InternetProbeBinding binding, Entry entry)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, UseCookies = false, MaxResponseHeadersLength = 8,
            PooledConnectionLifetime = TimeSpan.FromSeconds(30), PooledConnectionIdleTimeout = TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer = 2,
            SslOptions = new SslClientAuthenticationOptions { CertificateChainPolicy = CertificateChainPolicy },
            ConnectCallback = async (context, token) =>
            {
                if (!context.InitialRequestMessage.Options.TryGetValue(RequestSocketKey, out var request))
                    throw new InvalidOperationException("Missing physical internet probe request");
                var addresses = await Resolve(context.DnsEndPoint.Host, token).ConfigureAwait(false);
                Exception? lastRemote = null;
                foreach (var address in addresses.Where(address => address.AddressFamily == AddressFamily.InterNetwork))
                {
                    var socket = request.Create(token);
                    lock (entry.Sockets) { entry.Sockets.RemoveAll(s => s.SafeHandle.IsClosed); entry.Sockets.Add(socket); }
                    try
                    {
                        socket.Bind(new IPEndPoint(binding.Source, 0));
                        socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31 /* Windows IP_UNICAST_IF */, IPAddress.HostToNetworkOrder(binding.InterfaceIndex));
                        SocketConfigured?.Invoke(socket);
                        await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (SocketException ex) when (TcpEndpointProbe.Classify(ex.SocketErrorCode) == ProbeOutcome.NoReply)
                    { socket.Dispose(); lastRemote = ex; }
                    catch { socket.Dispose(); throw; }
                }
                throw lastRemote ?? new SocketException((int)SocketError.HostNotFound);
            },
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<EndpointResult> CheckAsync(InternetProbeBinding binding, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || binding.Source.AddressFamily != AddressFamily.InterNetwork || binding.InterfaceIndex <= 0
            || targets.Count is < 1 or > 2 || targets.Any(uri => uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0))
            return new(ProbeOutcome.Unavailable);
        try { if (!BindingExists(binding)) return new(ProbeOutcome.Unavailable); }
        catch (Exception ex) when (ex is NetworkInformationException or SocketException or InvalidOperationException or NotSupportedException)
        { return new(ProbeOutcome.Unavailable); }
        Entry entry; bool retained;
        try { entry = Acquire(binding, out retained); }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or ArgumentException or NotSupportedException)
        { return new(ProbeOutcome.Unavailable); }
        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        race.CancelAfter(timeout);
        var attempts = targets.Select(uri => CheckOneAsync(entry.Clients[uri], uri, race.Token, ct)).ToList();
        var unavailable = false;
        try
        {
            while (attempts.Count > 0)
            {
                var done = await Task.WhenAny(attempts).ConfigureAwait(false);
                attempts.Remove(done);
                var result = await done.ConfigureAwait(false);
                if (ct.IsCancellationRequested || _shutdown.IsCancellationRequested) return new(ProbeOutcome.Unavailable);
                if (result.Outcome == ProbeOutcome.Reply) return result;
                unavailable |= result.Outcome == ProbeOutcome.Unavailable;
            }
            return new(unavailable ? ProbeOutcome.Unavailable : ProbeOutcome.NoReply);
        }
        finally
        {
            race.Cancel();
            await ((Task)Task.WhenAll(attempts)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            lock (_gate)
            {
                entry.Active--; entry.LastUsed = Stopwatch.GetTimestamp();
                if (--_activeChecks == 0 && _disposed) _shutdown.Dispose();
            }
            if (!retained) entry.Dispose();
        }
    }

    static async Task<EndpointResult> CheckOneAsync(HttpClient client, Uri uri, CancellationToken deadline, CancellationToken caller)
    {
        var watch = Stopwatch.StartNew();
        var sockets = new RequestSockets();
        using var cancel = deadline.Register(sockets.Cancel);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri)
            { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            request.Options.Set(RequestSocketKey, sockets);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.NoContent || response.Content.Headers.ContentLength is > 0
                || response.Headers.TransferEncoding.Count > 0) return new(ProbeOutcome.NoReply);
            await using var body = await response.Content.ReadAsStreamAsync(deadline).ConfigureAwait(false);
            if (await body.ReadAsync(new byte[1], deadline).ConfigureAwait(false) != 0) return new(ProbeOutcome.NoReply);
            return new(ProbeOutcome.Reply, (int)Math.Max(1, watch.ElapsedMilliseconds));
        }
        catch (OperationCanceledException) { return new(caller.IsCancellationRequested ? ProbeOutcome.Unavailable : ProbeOutcome.NoReply); }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException or AuthenticationException
            or InvalidOperationException or ArgumentException or NotSupportedException or ObjectDisposedException)
        { return new(Classify(ex)); }
        finally { cancel.Dispose(); sockets.Finish(); }
    }

    internal static ProbeOutcome Classify(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket)
                return socket.SocketErrorCode is SocketError.HostNotFound or SocketError.TryAgain
                    ? ProbeOutcome.NoReply : TcpEndpointProbe.Classify(socket.SocketErrorCode);
            if (current is AuthenticationException) return ProbeOutcome.NoReply;
        }
        return error switch
        {
            IOException => ProbeOutcome.NoReply,
            HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError
                or HttpRequestError.InvalidResponse or HttpRequestError.ResponseEnded or HttpRequestError.HttpProtocolError } => ProbeOutcome.NoReply,
            _ => ProbeOutcome.Unavailable,
        };
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown.Cancel();
            foreach (var entry in _entries.Values) entry.Dispose();
            _entries.Clear();
            if (_activeChecks == 0) _shutdown.Dispose();
        }
        // The last active lease disposes the token source after its cancellation path completes.
    }
}
