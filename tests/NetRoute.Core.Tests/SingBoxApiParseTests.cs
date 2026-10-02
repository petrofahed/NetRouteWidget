using NetRoute.Core.Windows;

namespace NetRoute.Core.Tests;

public class SingBoxApiParseTests
{
    [Fact]
    public void Parses_connections_with_process_file_names()
    {
        const string json = """
            {"connections":[{"chains":["phone","default"],"download":100777,"id":"749c46b5","metadata":{"destinationIP":"","destinationPort":"443","host":"www.wikipedia.org","network":"tcp","processPath":"C:\\Program Files\\Git\\ucrt64\\bin\\curl.exe","sourceIP":"127.0.0.1","sourcePort":"53649","type":"mixed/mixed-in"},"rule":"final","rulePayload":"","start":"2026-10-02T16:50:47+03:00","upload":485},
                             {"chains":["lan","lan-only"],"download":5,"id":"b","metadata":{"host":"","destinationIP":"142.250.1.1","processPath":""},"upload":1}],
             "downloadTotal":100777,"uploadTotal":485}
            """;

        var conns = SingBoxApi.ParseConnections(json);

        Assert.Equal(2, conns.Count);
        Assert.Equal(new SingBoxConnection("749c46b5", "www.wikipedia.org", "curl.exe", ["phone", "default"], 485, 100777) with { Chains = conns[0].Chains }, conns[0]);
        Assert.Equal(new[] { "phone", "default" }, conns[0].Chains);
        Assert.Null(conns[1].Host);
        Assert.Null(conns[1].ProcessName);
        Assert.True(conns[1].IsLanOnly);
    }

    [Theory]
    [InlineData("""{"connections":null}""")]
    [InlineData("""{}""")]
    public void Missing_connections_parse_as_empty(string json) => Assert.Empty(SingBoxApi.ParseConnections(json));

    [Fact]
    public void Malformed_entries_are_skipped_not_fatal()
    {
        const string json = """
            {"connections":[
              {"chains":["phone","default"],"download":2,"id":"good","metadata":{"host":"a.example"},"upload":1},
              {"chains":["phone"],"download":2,"id":"no-metadata","upload":1},
              {"chains":"phone","download":"2","id":"wrong-types","metadata":{"host":"b.example"},"upload":1},
              {"chains":["x"],"download":2,"metadata":{},"upload":1},
              {"chains":["x"],"upload":1,"id":"no-download","metadata":{}},
              "not-an-object", null]}
            """;

        var conns = SingBoxApi.ParseConnections(json);

        var only = Assert.Single(conns);
        Assert.Equal("good", only.Id);
        Assert.Equal("a.example", only.Host);
    }

    [Fact]
    public void FreePort_returns_usable_distinct_ports()
    {
        var ports = Enumerable.Range(0, 5).Select(_ => FreePort.Next()).ToList();

        Assert.All(ports, port =>
        {
            Assert.InRange(port, 1, 65535);
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start(); // would throw if the port were not actually free
            listener.Stop();
        });
        Assert.Equal(ports.Count, ports.Distinct().Count());
    }

    [Fact]
    public void Job_object_limit_struct_matches_the_native_layout()
    {
        // winnt.h: 144 bytes on x64 (72 basic + 48 io counters + 4 * 8); 112 on x86.
        Assert.Equal(IntPtr.Size == 8 ? 144 : 112, System.Runtime.InteropServices.Marshal.SizeOf<JobObjectNative.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
    }
}
