using HomeCore.PluginSdk;
using Xunit;

namespace HomeCore.PluginSdk.Tests;

public class StoppingTests
{
    private static (PluginClient, List<string>) Client()
    {
        var said = new List<string>();
        var c = new PluginClient(new PluginOptions { PluginId = "plugin.test" });
        c.PublishSink = (t, p, r) =>
        {
            lock (said) said.Add($"{t} {p} retain={r}");
            return Task.CompletedTask;
        };
        return (c, said);
    }

    [Fact]
    public async Task OnStopRunsThenEveryDeviceAndThePluginGoOffline()
    {
        var (c, said) = Client();
        await c.SubscribeCommandsAsync("light.b");
        await c.SubscribeCommandsAsync("light.a");
        c.OnStop = () => c.PublishAvailabilityAsync("heat.a", false);

        var run = c.RunAsync();
        await c.StopAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(1)); // RunAsync returns

        Assert.Equal(new[]
        {
            "homecore/devices/heat.a/availability offline retain=True",
            "homecore/devices/light.a/availability offline retain=True",
            "homecore/devices/light.b/availability offline retain=True",
            "homecore/plugins/plugin.test/status offline retain=True",
        }, said);

        // A second SIGTERM, or StopAsync after Ctrl-C, says nothing again.
        await c.StopAsync();
        Assert.Equal(4, said.Count);
    }

    [Fact]
    public async Task AnOnStopThatThrowsOrHangsStillMarksDevicesOffline()
    {
        var (throws, said) = Client();
        await throws.SubscribeCommandsAsync("light.a");
        throws.OnStop = () => throw new InvalidOperationException("hook failed");
        await throws.StopAsync();
        Assert.Contains("homecore/devices/light.a/availability offline retain=True", said);

        var (hangs, said2) = Client();
        await hangs.SubscribeCommandsAsync("light.a");
        hangs.OnStop = () => Task.Delay(Timeout.Infinite);
        var t0 = DateTime.UtcNow;
        await hangs.StopAsync();
        Assert.True(DateTime.UtcNow - t0 < TimeSpan.FromMilliseconds(1500));
        Assert.Contains("homecore/plugins/plugin.test/status offline retain=True", said2);
    }
}
