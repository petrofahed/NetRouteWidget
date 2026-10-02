using System.Xml.Linq;

namespace NetRoute.Core.Tests;

public class StartupTaskXmlTests
{
    static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    const string Exe = @"F:\Apps & Tools\NetRouteWidget\NetRouteWidget.exe";

    static string Value(XDocument doc, string name) => doc.Descendants(Ns + name).Single().Value;

    [Fact]
    public void Task_runs_elevated_at_logon_of_the_user()
    {
        var doc = StartupTaskXml.Build(Exe, @"PC\someone");

        Assert.Equal("HighestAvailable", Value(doc, "RunLevel"));
        Assert.Equal("InteractiveToken", Value(doc, "LogonType"));
        Assert.Equal(@"PC\someone", doc.Descendants(Ns + "LogonTrigger").Single().Element(Ns + "UserId")!.Value);
        Assert.Equal(Exe, Value(doc, "Command"));
    }

    [Fact]
    public void Task_runs_on_battery_and_never_times_out()
    {
        var doc = StartupTaskXml.Build(Exe, @"PC\someone");

        Assert.Equal("false", Value(doc, "DisallowStartIfOnBatteries"));
        Assert.Equal("false", Value(doc, "StopIfGoingOnBatteries"));
        Assert.Equal("PT0S", Value(doc, "ExecutionTimeLimit"));
        Assert.Equal("IgnoreNew", Value(doc, "MultipleInstancesPolicy"));
    }

    [Fact]
    public void Special_characters_in_the_path_are_escaped()
    {
        var xml = StartupTaskXml.Build(Exe, @"PC\someone").ToString();

        Assert.Contains("Apps &amp; Tools", xml);
    }

    [Fact]
    public void Saved_file_is_utf16_with_matching_declaration()
    {
        var path = Path.Combine(Path.GetTempPath(), $"netroute-task-{Guid.NewGuid():N}.xml");
        try
        {
            StartupTaskXml.Save(StartupTaskXml.Build(Exe, @"PC\someone"), path);

            var bytes = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0xFF, 0xFE }, bytes[..2]);
            Assert.Contains("encoding=\"utf-16\"", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Exe, Value(XDocument.Load(path), "Command"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
