using System.Net;
using System.Net.Sockets;
using HomeCore.PluginSdk;
using MQTTnet;
using MQTTnet.Server;
using Xunit;

namespace HomeCore.PluginSdk.Tests;

/// <summary>
/// Against a real broker in this process: the heartbeat loop across repeated
/// EnableManagementAsync calls, and what a dropped connection does.
/// </summary>
public class ReconnectTests
{
    private sealed class Broker : IAsyncDisposable
    {
        private readonly MqttServer _server;
        private readonly List<string> _topics = new();

        private Broker(MqttServer server)
        {
            _server = server;
            _server.InterceptingPublishAsync += e =>
            {
                lock (_topics) _topics.Add(e.ApplicationMessage.Topic);
                return Task.CompletedTask;
            };
        }

        public static async Task<Broker> StartAsync(int port)
        {
            var options = new MqttServerOptionsBuilder()
                .WithDefaultEndpoint()
                .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
                .WithDefaultEndpointPort(port)
                .Build();
            var broker = new Broker(new MqttFactory().CreateMqttServer(options));
            await broker._server.StartAsync();
            return broker;
        }

        public bool Saw(string topic)
        {
            lock (_topics) return _topics.Contains(topic);
        }

        public async ValueTask DisposeAsync()
        {
            await _server.StopAsync();
            _server.Dispose();
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what);
            await Task.Delay(20);
        }
    }

    private static PluginClient Client(int port) =>
        new(new PluginOptions { PluginId = "plugin.test", BrokerHost = "127.0.0.1", BrokerPort = port })
        {
            ReconnectDelay = TimeSpan.FromMilliseconds(100),
            ReconnectDelayMax = TimeSpan.FromMilliseconds(200),
        };

    [Fact]
    public async Task EnableManagementOnEveryConnectKeepsOneHeartbeatLoop()
    {
        var port = FreePort();
        await using var broker = await Broker.StartAsync(port);
        await using var client = Client(port);
        await client.ConnectAsync();

        for (var i = 0; i < 3; i++)
            await client.EnableManagementAsync(new ManagementOptions { HeartbeatIntervalSecs = 3600 });

        await Until(() => client.HeartbeatLoops >= 1, "a heartbeat loop");
        await Task.Delay(200); // replaced loops have time to end
        Assert.Equal(1, client.HeartbeatLoops);
    }

    [Fact]
    public async Task ADroppedConnectionReconnectsAndRunsOnConnectedAgain()
    {
        var port = FreePort();
        var first = await Broker.StartAsync(port);
        await using var client = Client(port);
        var connects = 0;
        var disconnects = 0;
        client.OnConnected += async () =>
        {
            Interlocked.Increment(ref connects);
            await client.EnableManagementAsync(new ManagementOptions { HeartbeatIntervalSecs = 3600 });
            await client.RegisterDeviceFullAsync("light.01", "Desk Lamp", deviceType: "light");
        };
        client.OnDisconnected += () =>
        {
            Interlocked.Increment(ref disconnects);
            return Task.CompletedTask;
        };

        await client.ConnectAsync();
        Assert.Equal(1, connects);

        await first.DisposeAsync(); // the broker goes away
        await Until(() => Volatile.Read(ref disconnects) == 1, "OnDisconnected");
        var run = client.RunAsync();
        await Task.Delay(300); // a few failed attempts
        Assert.False(run.IsCompleted);

        await using var second = await Broker.StartAsync(port);
        await Until(() => Volatile.Read(ref connects) == 2, "OnConnected after the reconnect");
        await Until(() => second.Saw("homecore/plugins/plugin.test/heartbeat"), "a heartbeat");
        await Until(() => second.Saw("homecore/plugins/plugin.test/register"), "the device registered again");
        Assert.Equal(1, client.HeartbeatLoops);
        Assert.Equal(1, disconnects);

        await client.StopAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task StoppingIsNotADroppedConnection()
    {
        var port = FreePort();
        await using var broker = await Broker.StartAsync(port);
        await using var client = Client(port);
        var connects = 0;
        var disconnects = 0;
        client.OnConnected += () => { Interlocked.Increment(ref connects); return Task.CompletedTask; };
        client.OnDisconnected += () => { Interlocked.Increment(ref disconnects); return Task.CompletedTask; };

        await client.ConnectAsync();
        await client.StopAsync();
        await Task.Delay(400);

        Assert.Equal(1, connects);
        Assert.Equal(0, disconnects);
    }
}
