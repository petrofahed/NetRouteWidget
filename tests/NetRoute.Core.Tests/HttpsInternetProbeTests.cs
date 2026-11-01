using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NetRoute.Core.PathMap;
using NetRoute.Core.Windows;

namespace NetRoute.Core.Tests;

public class HttpsInternetProbeTests
{
    static InternetProbeBinding Loopback => new(IPAddress.Loopback, NetworkInterface.GetAllNetworkInterfaces()
        .Single(adapter => adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback).GetIPProperties().GetIPv4Properties().Index);

    [Fact]
    public async Task Tcp_accept_then_TLS_reset_is_not_a_successful_internet_check()
    {
        await using var server = new TlsServer(reset: true);
        Assert.Equal(ProbeOutcome.NoReply, (await server.Own(new HttpsInternetProbe([server.Uri], TimeSpan.FromSeconds(2)))
            .CheckAsync(Loopback, CancellationToken.None)).Outcome);
        Assert.True(server.Accepted > 0);
    }

    [Fact]
    public async Task Valid_TLS_and_expected_HTTP_response_are_required_and_socket_is_physically_bound_and_closed()
    {
        await using var server = new TlsServer();
        var sockets = new ConcurrentBag<Socket>();
        using var probe = BoundProbe();
        HttpsInternetProbe BoundProbe() => new([server.Uri], TimeSpan.FromSeconds(2))
        {
            CertificateChainPolicy = server.Trust(),
            SocketConfigured = socket =>
            {
                Assert.Equal(Loopback.Source, ((IPEndPoint)socket.LocalEndPoint!).Address);
                Assert.Equal(Loopback.InterfaceIndex, (int)socket.GetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31 /* Windows IP_UNICAST_IF */)!);
                sockets.Add(socket);
            },
        };
        var result = await probe.CheckAsync(Loopback, CancellationToken.None);
        Assert.Equal(ProbeOutcome.Reply, result.Outcome);
        Assert.InRange(result.RttMs!.Value, 1, 2000);
        Assert.NotEmpty(sockets);
        Assert.All(sockets, socket => Assert.True(socket.SafeHandle.IsClosed));
        Assert.Equal("GET /generate_204 HTTP/1.1", server.RequestLine);
    }

    [Theory]
    [InlineData("HTTP/1.1 302 Found\r\nLocation: https://localhost/captive\r\nContent-Length: 0\r\n\r\n")]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n")]
    [InlineData("HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\n\r\n")]
    [InlineData("HTTP/1.1 500 Server Error\r\nContent-Length: 0\r\n\r\n")]
    [InlineData("HTTP/1.1 204 No Content\r\nContent-Length: 1\r\n\r\nx")]
    [InlineData("HTTP/1.1 204 No Content\r\nTransfer-Encoding: chunked\r\n\r\n1\r\nx\r\n0\r\n\r\n")]
    public async Task Captive_redirect_error_and_unexpected_body_are_not_internet_success(string response)
    {
        await using var server = new TlsServer(response: response);
        Assert.Equal(ProbeOutcome.NoReply, (await Trusted(server).CheckAsync(Loopback, CancellationToken.None)).Outcome);
        Assert.Equal(1, server.Accepted); // no redirect/auth retry
    }

    [Fact]
    public async Task Trusted_but_wrong_hostname_certificate_is_rejected()
    {
        await using var server = new TlsServer(hostname: "wrong.invalid");
        Assert.Equal(ProbeOutcome.NoReply, (await Trusted(server).CheckAsync(Loopback, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Production_trust_rejects_the_test_private_root()
    {
        await using var server = new TlsServer();
        Assert.Equal(ProbeOutcome.NoReply, (await server.Own(new HttpsInternetProbe([server.Uri], TimeSpan.FromSeconds(2)))
            .CheckAsync(Loopback, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task One_provider_TLS_failure_does_not_hide_another_providers_valid_response()
    {
        await using var reset = new TlsServer(reset: true);
        await using var valid = new TlsServer();
        using var probe = new HttpsInternetProbe([reset.Uri, valid.Uri], TimeSpan.FromSeconds(2)) { CertificateChainPolicy = valid.Trust() };
        Assert.Equal(ProbeOutcome.Reply, (await probe.CheckAsync(Loopback, CancellationToken.None)).Outcome);
        Assert.True(reset.Accepted > 0);
    }

    [Fact]
    public async Task First_valid_response_cancels_closes_and_awaits_losing_TLS_socket()
    {
        await using var stalled = new TlsServer(stallTls: true);
        await using var valid = new TlsServer();
        var sockets = new ConcurrentBag<Socket>();
        using var probe = new HttpsInternetProbe([stalled.Uri, valid.Uri], TimeSpan.FromSeconds(2))
        { CertificateChainPolicy = valid.Trust(), SocketConfigured = socket => sockets.Add(socket) };
        Assert.Equal(ProbeOutcome.Reply, (await probe.CheckAsync(Loopback, CancellationToken.None)).Outcome);
        Assert.Equal(2, sockets.Count);
        Assert.All(sockets, socket => Assert.True(socket.SafeHandle.IsClosed));
        await stalled.PeerClosed.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task TLS_and_HTTP_wait_share_one_deadline_and_cancellation_is_not_remote_loss()
    {
        await using var first = new TlsServer(stallTls: true);
        await using var second = new TlsServer(stallHeaders: true);
        using var probe = new HttpsInternetProbe([first.Uri, second.Uri], TimeSpan.FromMilliseconds(250)) { CertificateChainPolicy = second.Trust() };
        var clock = Stopwatch.StartNew();
        Assert.Equal(ProbeOutcome.NoReply, (await probe.CheckAsync(Loopback, CancellationToken.None)).Outcome);
        Assert.InRange(clock.ElapsedMilliseconds, 150, 1500);
        using var cancel = new CancellationTokenSource(80);
        Assert.Equal(ProbeOutcome.Unavailable, (await probe.CheckAsync(Loopback, cancel.Token)).Outcome);
    }

    [Fact]
    public async Task DNS_uses_the_same_deadline_and_no_wrong_family_socket_is_attempted()
    {
        await using var server = new TlsServer();
        using var pending = new HttpsInternetProbe([server.Uri], TimeSpan.FromMilliseconds(100))
        { Resolve = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return []; } };
        Assert.Equal(ProbeOutcome.NoReply, (await pending.CheckAsync(Loopback, CancellationToken.None)).Outcome);
        using var filtered = new HttpsInternetProbe([server.Uri], TimeSpan.FromSeconds(2))
        { CertificateChainPolicy = server.Trust(), Resolve = (_, _) => Task.FromResult(new[] { IPAddress.IPv6Loopback, IPAddress.Loopback }) };
        Assert.Equal(ProbeOutcome.Reply, (await filtered.CheckAsync(Loopback, CancellationToken.None)).Outcome);
    }

    [Theory]
    [InlineData("203.0.113.7", 1)]
    [InlineData("127.0.0.1", 0)]
    [InlineData("::1", 1)]
    public async Task Invalid_or_unavailable_source_binding_is_unavailable_not_remote_loss(string address, int index)
    {
        await using var server = new TlsServer();
        Assert.Equal(ProbeOutcome.Unavailable, (await Trusted(server).CheckAsync(new(IPAddress.Parse(address), index), CancellationToken.None)).Outcome);
        Assert.Equal(0, server.Accepted);
    }

    [Fact]
    public void Unknown_wrapped_local_failure_is_not_remote_loss()
    {
        Assert.Equal(ProbeOutcome.Unavailable, HttpsInternetProbe.Classify(new HttpRequestException("local setup", new InvalidOperationException())));
    }

    [Fact]
    public async Task Plaintext_HTTP_on_an_HTTPS_socket_cannot_be_green()
    {
        await using var server = new TlsServer(plaintext: true);
        Assert.Equal(ProbeOutcome.NoReply, (await Trusted(server).CheckAsync(Loopback, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Same_binding_gets_two_fresh_HTTP_responses_over_one_authenticated_connection()
    {
        await using var server = new TlsServer(keepAlive: true);
        using var probe = Trusted(server);
        Assert.Equal(ProbeOutcome.Reply, (await probe.CheckAsync(Loopback, CancellationToken.None)).Outcome);
        Assert.Equal(ProbeOutcome.Reply, (await probe.CheckAsync(Loopback, CancellationToken.None)).Outcome);
        Assert.Equal(2, server.Requests);
        Assert.Equal(1, server.Accepted);
    }

    [Fact]
    public async Task Synthetic_binding_keys_cannot_borrow_warm_sockets_and_idle_entries_are_bounded()
    {
        await using var server = new TlsServer(keepAlive: true);
        var sockets = new ConcurrentBag<Socket>();
        using var probe = new HttpsInternetProbe([server.Uri], TimeSpan.FromSeconds(2))
        { BindingExists = _ => true, CertificateChainPolicy = server.Trust(), SocketConfigured = socket => sockets.Add(socket) };
        Assert.Equal(ProbeOutcome.Reply, (await probe.CheckAsync(Loopback, CancellationToken.None)).Outcome);
        var originalSocket = Assert.Single(sockets);
        for (var index = int.MaxValue; index > int.MaxValue - 4; index--)
        {
            Assert.Equal(ProbeOutcome.Reply, (await probe.CheckAsync(Loopback with { InterfaceIndex = index }, CancellationToken.None)).Outcome);
            Assert.InRange(probe.RetainedBindings, 1, 3);
        }
        Assert.True(originalSocket.SafeHandle.IsClosed); // old idle binding was retired
        Assert.Equal(5, server.Accepted);
        Assert.Equal(ProbeOutcome.Reply, (await probe.CheckAsync(Loopback, CancellationToken.None)).Outcome);
        Assert.Equal(6, server.Accepted);
    }

    [Fact]
    public async Task Busy_binding_limit_uses_owned_fallback_and_cancellation_allows_a_later_check()
    {
        await using var server = new TlsServer();
        var resolveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var probe = new HttpsInternetProbe([server.Uri], TimeSpan.FromSeconds(2))
        { BindingExists = _ => true, CertificateChainPolicy = server.Trust(), Resolve = async (_, ct) => { await resolveGate.Task.WaitAsync(ct); return [IPAddress.Loopback]; } };
        using var cancel = new CancellationTokenSource();
        var checks = Enumerable.Range(1, 4).Select(index => probe.CheckAsync(Loopback with { InterfaceIndex = index }, cancel.Token)).ToArray();
        Assert.Equal(3, probe.RetainedBindings);
        cancel.Cancel();
        Assert.All(await Task.WhenAll(checks), result => Assert.Equal(ProbeOutcome.Unavailable, result.Outcome));
        resolveGate.SetResult();
        Assert.Equal(ProbeOutcome.Reply, (await probe.CheckAsync(Loopback, CancellationToken.None)).Outcome);
        Assert.Equal(3, probe.RetainedBindings);
        probe.Dispose();
        Assert.Equal(0, probe.RetainedBindings);
        Assert.Equal(ProbeOutcome.Unavailable, (await probe.CheckAsync(Loopback, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Production_binding_validation_rejects_a_nonexistent_index_before_connecting()
    {
        await using var server = new TlsServer(keepAlive: true);
        using var probe = Trusted(server);
        Assert.Equal(ProbeOutcome.Reply, (await probe.CheckAsync(Loopback, CancellationToken.None)).Outcome);
        Assert.Equal(ProbeOutcome.Unavailable, (await probe.CheckAsync(Loopback with { InterfaceIndex = int.MaxValue }, CancellationToken.None)).Outcome);
        Assert.Equal(1, server.Accepted);
        Assert.Equal(1, server.Requests); // invalid binding cannot borrow the old socket or establish a new one
    }

    static HttpsInternetProbe Trusted(TlsServer server) => server.Own(new([server.Uri], TimeSpan.FromSeconds(2)) { CertificateChainPolicy = server.Trust() });

    sealed class TlsServer : IAsyncDisposable
    {
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource _stop = new();
        readonly ConcurrentBag<Socket> _sockets = [];
        readonly List<Task> _clients = [];
        readonly List<HttpsInternetProbe> _probes = [];
        public HttpsInternetProbe Own(HttpsInternetProbe probe) { _probes.Add(probe); return probe; }
        readonly X509Certificate2 _root, _leaf;
        readonly Task _accept;
        int _accepted, _requests;
        public int Requests => _requests;
        public int Accepted => _accepted;
        public Uri Uri { get; }
        public string? RequestLine { get; private set; }
        public TaskCompletionSource PeerClosed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TlsServer(bool keepAlive = false, bool plaintext = false, bool reset = false, bool stallTls = false, bool stallHeaders = false, string hostname = "localhost", string? response = null)
        {
            using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var rootRequest = new CertificateRequest("CN=NetRoute loopback test root", rootKey, HashAlgorithmName.SHA256);
            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            _root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(3));
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=" + hostname, key, HashAlgorithmName.SHA256);
            var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(hostname);
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new System.Security.Cryptography.OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            using var signed = request.Create(_root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));
            using var withKey = signed.CopyWithPrivateKey(key);
            _leaf = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
            _listener.Start();
            Uri = new Uri($"https://localhost:{((IPEndPoint)_listener.LocalEndpoint).Port}/generate_204");
            _accept = Accept();

            async Task Accept()
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        var socket = await _listener.AcceptSocketAsync(_stop.Token);
                        Interlocked.Increment(ref _accepted);
                        _sockets.Add(socket);
                        _clients.Add(Serve(socket));
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
            }
            async Task Serve(Socket socket)
            {
                using (socket)
                {
                    try
                    {
                        if (reset) { socket.LingerState = new LingerOption(true, 0); return; }
                        await using var network = new NetworkStream(socket, ownsSocket: false);
                        if (plaintext)
                        {
                            await network.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n"), _stop.Token);
                            return;
                        }
                        if (stallTls)
                        {
                            var b = new byte[4096];
                            while (await network.ReadAsync(b, _stop.Token) != 0) { }
                            PeerClosed.TrySetResult(); return;
                        }
                        await using var tls = new SslStream(network);
                        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _leaf }, _stop.Token);
                        do
                        {
                            var bytes = new List<byte>(); var one = new byte[1];
                            while (bytes.Count < 4096 && await tls.ReadAsync(one, _stop.Token) != 0)
                            {
                                bytes.Add(one[0]);
                                if (bytes.Count >= 4 && Encoding.ASCII.GetString(bytes.TakeLast(4).ToArray()) == "\r\n\r\n") break;
                            }
                            if (bytes.Count == 0) { PeerClosed.TrySetResult(); return; }
                            Interlocked.Increment(ref _requests);
                            RequestLine = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n")[0];
                            if (stallHeaders) { while (await tls.ReadAsync(one, _stop.Token) != 0) { } PeerClosed.TrySetResult(); return; }
                            await tls.WriteAsync(Encoding.ASCII.GetBytes(response ?? (keepAlive
                                ? "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n"
                                : "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")), _stop.Token);
                            await tls.FlushAsync(_stop.Token);
                        } while (keepAlive);
                    }
                    catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or OperationCanceledException or SocketException or ObjectDisposedException) { if ((stallTls || stallHeaders) && !_stop.IsCancellationRequested) PeerClosed.TrySetResult(); }
                }
            }
        }
        public X509ChainPolicy Trust()
        {
            var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
            policy.CustomTrustStore.Add(_root);
            return policy;
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var probe in _probes) probe.Dispose();
            _stop.Cancel(); _listener.Stop();
            foreach (var socket in _sockets) socket.Dispose();
            await _accept;
            await Task.WhenAll(_clients).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _leaf.Dispose(); _root.Dispose(); _stop.Dispose();
        }
    }
}

