namespace NetRoute.Core.Tests;

public class LiveSpeedDisplayTests
{
    [Theory]
    [InlineData(SmartState.Running)]
    [InlineData(SmartState.Starting)]
    public void The_card_shows_live_speeds_while_smart_routing_runs_or_starts(SmartState state)
    {
        Assert.True(LiveSpeedDisplay.IsShown(state, measuring: false));
        Assert.True(LiveSpeedDisplay.IsShown(state, measuring: true));
    }

    [Theory]
    [InlineData(SmartState.Off)]
    [InlineData(SmartState.Unavailable)]
    [InlineData(SmartState.Faulted)]
    public void The_card_shows_live_speeds_without_smart_routing_only_while_windows_tracing_counts(SmartState state)
    {
        Assert.False(LiveSpeedDisplay.IsShown(state, measuring: false));
        Assert.True(LiveSpeedDisplay.IsShown(state, measuring: true));
    }
}
