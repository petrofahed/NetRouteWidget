using System.Text;
using System.Xml.Linq;

namespace NetRoute.Core;

/// Task Scheduler definition for "Start with Windows": at logon, elevated, so no UAC prompt.
/// Built as XML because schtasks' command-line defaults refuse to start on battery
/// and stop the task after 72 hours.
public static class StartupTaskXml
{
    static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static XDocument Build(string exePath, string userId) =>
        new(new XDeclaration("1.0", "UTF-16", null),
            new XElement(Ns + "Task", new XAttribute("version", "1.2"),
                new XElement(Ns + "RegistrationInfo",
                    new XElement(Ns + "Description", "Starts NetRoute Widget at logon with administrator rights.")),
                new XElement(Ns + "Triggers",
                    new XElement(Ns + "LogonTrigger",
                        new XElement(Ns + "Enabled", "true"),
                        new XElement(Ns + "UserId", userId))),
                new XElement(Ns + "Principals",
                    new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                        new XElement(Ns + "UserId", userId),
                        new XElement(Ns + "LogonType", "InteractiveToken"),
                        new XElement(Ns + "RunLevel", "HighestAvailable"))),
                new XElement(Ns + "Settings",
                    new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(Ns + "StopIfGoingOnBatteries", "false"),
                    new XElement(Ns + "ExecutionTimeLimit", "PT0S"),
                    new XElement(Ns + "Priority", "7")),
                new XElement(Ns + "Actions", new XAttribute("Context", "Author"),
                    new XElement(Ns + "Exec",
                        new XElement(Ns + "Command", exePath)))));

    /// schtasks /XML expects a UTF-16 file when the declaration says UTF-16.
    public static void Save(XDocument document, string path)
    {
        using var writer = new StreamWriter(path, append: false, Encoding.Unicode);
        document.Save(writer);
    }
}
