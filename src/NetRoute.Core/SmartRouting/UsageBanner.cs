namespace NetRoute.Core;

/// The line at the top of the Usage tab that says how the numbers are being recorded right now.
public static class UsageBanner
{
    public const string SmartOnlyText = "Usage is recorded only while Smart routing is running. History stays visible.";
    public const string WindowsText = "Smart routing is off: usage is counted per application by Windows (no website rows).";

    /// Null while Smart routing itself is recording (nothing to explain).
    public static string? Text(bool smartRecording, bool windowsTracking) =>
        smartRecording ? null : windowsTracking ? WindowsText : SmartOnlyText;
}
