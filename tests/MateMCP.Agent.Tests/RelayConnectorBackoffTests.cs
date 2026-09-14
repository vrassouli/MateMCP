using MateMCP.Agent.Relay;

namespace MateMCP.Agent.Tests;

public sealed class RelayConnectorBackoffTests
{
    [Theory]
    [InlineData(1, 500)]
    [InlineData(2, 1000)]
    [InlineData(3, 2000)]
    [InlineData(4, 4000)]
    [InlineData(5, 8000)]
    [InlineData(9, 8000)]
    public void Reconnect_delay_grows_exponentially_and_caps_at_eight_seconds(int failureCount, double expectedMilliseconds)
    {
        var delay = RelayConnector.ComputeReconnectDelay(failureCount, 0.5);

        Assert.Equal(expectedMilliseconds, delay.TotalMilliseconds, precision: 3);
    }

    [Fact]
    public void Reconnect_delay_applies_bounded_twenty_percent_jitter()
    {
        var low = RelayConnector.ComputeReconnectDelay(3, 0);
        var middle = RelayConnector.ComputeReconnectDelay(3, 0.5);
        var high = RelayConnector.ComputeReconnectDelay(3, 1);

        Assert.Equal(1600, low.TotalMilliseconds, precision: 3);
        Assert.Equal(2000, middle.TotalMilliseconds, precision: 3);
        Assert.Equal(2400, high.TotalMilliseconds, precision: 3);

        var cappedHigh = RelayConnector.ComputeReconnectDelay(8, 1);
        Assert.Equal(8000, cappedHigh.TotalMilliseconds, precision: 3);
    }
}
