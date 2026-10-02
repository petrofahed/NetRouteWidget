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
}
