namespace NetRoute.Core;

/// Whether the card shows live speeds next to the latencies. One rule for both places that draw them, so a status refresh
/// can never blank what the usage poll just drew.
public static class LiveSpeedDisplay
{
    /// While Smart routing runs or starts, or while Windows tracing counts because sing-box is not running.
    public static bool IsShown(SmartState state, bool measuring) =>
        state is SmartState.Running or SmartState.Starting || measuring;
}
