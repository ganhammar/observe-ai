using ObserveAi;

namespace ObserveAi.Tests;

/// <summary>
/// The exact event demo/emit-error.sh writes, which is also what the Python
/// Lambda runtime logs for demo/pricing-demo: carriage returns between lines,
/// /var/task as the mount, the innermost frame printed last and parsed first.
/// </summary>
public class DemoTraceTests
{
    private const string Message =
        "[ERROR] KeyError: 'enterprise'\r" +
        "Traceback (most recent call last):\r" +
        "  File \"/var/task/handler.py\", line 26, in handler\r" +
        "    total = price_for(customer, quantity)\r" +
        "  File \"/var/task/handler.py\", line 19, in price_for\r" +
        "    unit = TIERS[customer[\"tier\"]]";

    [Fact]
    public void TheDemoEventParsesToAnInAppKeyErrorInHandlerPy()
    {
        var trace = TraceParser.Parse(Message);

        Assert.NotNull(trace);
        Assert.Equal("python", trace!.Runtime);
        Assert.Equal("KeyError", trace.ExceptionType);
        Assert.Equal(["price_for", "handler"], trace.Frames.Select(f => f.Method));
        Assert.All(trace.Frames, f => Assert.True(f.InApp));
        Assert.Equal(["handler.py"], SourceFetch.PathsFor(trace, Message));
    }
}
