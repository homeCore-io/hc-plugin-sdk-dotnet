using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using HomeCore.PluginSdk;
using Xunit;

namespace HomeCore.PluginSdk.Tests;

/// <summary>
/// Exercises the parts of <see cref="PluginClient"/> that do not need a broker:
/// message routing, the management dispatcher, and config handling.
/// </summary>
/// <remarks>
/// MQTTnet's client is sealed behind an interface the SDK creates itself, so
/// rather than inject a mock these tests drive the private handlers directly.
/// The end-to-end behaviour is covered by running a plugin against a real core.
/// </remarks>
public class PluginClientTests
{
    private static PluginClient NewClient(string id = "plugin.test") =>
        new(new PluginOptions { PluginId = id });

    private static object? Invoke(PluginClient c, string method, params object?[] args)
    {
        var m = typeof(PluginClient).GetMethod(
            method, BindingFlags.Instance | BindingFlags.NonPublic)!;
        return m.Invoke(c, args);
    }

    private static JsonElement Cmd(string json) => JsonDocument.Parse(json).RootElement;

    private static async Task<JsonObject> Manage(PluginClient c, string json)
    {
        var task = (Task<JsonObject>)Invoke(c, "HandleManagementCommandAsync", Cmd(json))!;
        return await task;
    }

    private static void SetMgmt(PluginClient c, ManagementOptions opts)
    {
        typeof(PluginClient).GetField("_mgmt", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(c, opts);
        if (opts.Capabilities is not null)
            typeof(Capabilities).GetProperty("PluginId",
                BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(opts.Capabilities, c.PluginId);
    }

    // ── management built-ins ─────────────────────────────────────────────

    [Fact]
    public async Task PingAnswersOk()
    {
        var c = NewClient();
        SetMgmt(c, new ManagementOptions());
        var resp = await Manage(c, """{"action":"ping","request_id":"r1"}""");
        Assert.Equal("ok", resp["status"]!.GetValue<string>());
        Assert.Equal("r1", resp["request_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetConfigAnswersWithTheDataKeyCoreReads()
    {
        // Core reads resp["data"] and falls back to the whole envelope when it
        // is absent, so a wrong key shows the operator {request_id, status, …}
        // where their config should be.
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "[demo]\nvalue = 42\n");
        try
        {
            var c = NewClient();
            SetMgmt(c, new ManagementOptions { ConfigPath = path });
            var resp = await Manage(c, """{"action":"get_config","request_id":"r1"}""");
            Assert.Equal("ok", resp["status"]!.GetValue<string>());
            Assert.Equal("[demo]\nvalue = 42\n", resp["data"]!.GetValue<string>());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task SetConfigWritesTheStringForm()
    {
        var path = Path.GetTempFileName();
        try
        {
            var c = NewClient();
            SetMgmt(c, new ManagementOptions { ConfigPath = path });
            var resp = await Manage(
                c, """{"action":"set_config","request_id":"r1","config":"[demo]\nvalue = 99\n"}""");
            Assert.Equal("ok", resp["status"]!.GetValue<string>());
            Assert.Equal("[demo]\nvalue = 99\n", await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task SetConfigUnwrapsTheRawFormCoreSends()
    {
        // Core forwards the request body when it has no top-level `config` key,
        // so the raw-TOML editor arrives as {"raw": "<text>"} rather than a
        // bare string.
        var path = Path.GetTempFileName();
        try
        {
            var c = NewClient();
            SetMgmt(c, new ManagementOptions { ConfigPath = path });
            var resp = await Manage(
                c,
                """{"action":"set_config","request_id":"r1","config":{"raw":"[demo]\nvalue = 7\n"}}""");
            Assert.Equal("ok", resp["status"]!.GetValue<string>());
            Assert.Equal("[demo]\nvalue = 7\n", await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task StructuredConfigIsRefusedRatherThanWrittenAsJson()
    {
        // It used to GetRawText() and write JSON into a .toml file, which looks
        // like success and leaves an unparseable config behind.
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "[demo]\nvalue = 42\n");
        try
        {
            var c = NewClient();
            SetMgmt(c, new ManagementOptions { ConfigPath = path });
            var resp = await Manage(
                c, """{"action":"set_config","request_id":"r1","config":{"demo":{"value":7}}}""");
            Assert.Equal("error", resp["status"]!.GetValue<string>());
            Assert.Equal("[demo]\nvalue = 42\n", await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task OnSetConfigOverrideTakesOver()
    {
        var path = Path.GetTempFileName();
        try
        {
            var c = NewClient();
            SetMgmt(c, new ManagementOptions { ConfigPath = path });
            JsonElement? seen = null;
            c.OnSetConfig = cfg => { seen = cfg; return Task.FromResult(true); };

            var resp = await Manage(
                c, """{"action":"set_config","request_id":"r1","config":{"demo":{"value":7}}}""");
            Assert.Equal("ok", resp["status"]!.GetValue<string>());
            Assert.NotNull(seen);
            Assert.Equal(7, seen!.Value.GetProperty("demo").GetProperty("value").GetInt32());
        }
        finally { File.Delete(path); }
    }

    // ── capability actions ───────────────────────────────────────────────

    [Fact]
    public async Task ImmediateActionReturnsItsResult()
    {
        var c = NewClient();
        SetMgmt(c, new ManagementOptions
        {
            Capabilities = new Capabilities
            {
                Actions = new[] { new PluginAction { Id = "hello", Label = "Hello" } },
            },
        });
        c.OnAction = (action, @params, ctx) =>
            Task.FromResult<JsonObject?>(
                action == "hello"
                    ? new JsonObject { ["message"] = "hi", ["echo"] = @params.GetRawText() }
                    : null);

        var resp = await Manage(c, """{"action":"hello","request_id":"r1","value":7}""");
        Assert.Equal("ok", resp["status"]!.GetValue<string>());
        Assert.Equal("hi", resp["message"]!.GetValue<string>());
        // Envelope keys are stripped from params.
        Assert.DoesNotContain("request_id", resp["echo"]!.GetValue<string>());
        Assert.Contains("value", resp["echo"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnknownActionIsAnError()
    {
        var c = NewClient();
        SetMgmt(c, new ManagementOptions());
        c.OnAction = (_, _, _) => Task.FromResult<JsonObject?>(null);
        var resp = await Manage(c, """{"action":"nope","request_id":"r1"}""");
        Assert.Equal("error", resp["status"]!.GetValue<string>());
        Assert.Contains("unknown action", resp["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task AThrowingHandlerBecomesAnErrorResponse()
    {
        // A plugin bug must not take down the message loop.
        var c = NewClient();
        SetMgmt(c, new ManagementOptions());
        c.OnAction = (_, _, _) => throw new InvalidOperationException("handler exploded");
        var resp = await Manage(c, """{"action":"boom","request_id":"r1"}""");
        Assert.Equal("error", resp["status"]!.GetValue<string>());
        Assert.Contains("handler exploded", resp["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task CancelForAnUnknownStreamIsAnError()
    {
        var c = NewClient();
        SetMgmt(c, new ManagementOptions());
        var resp = await Manage(
            c, """{"action":"cancel","request_id":"r1","target_request_id":"nope"}""");
        Assert.Equal("error", resp["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task RespondForAnUnknownStreamIsAnError()
    {
        var c = NewClient();
        SetMgmt(c, new ManagementOptions());
        var resp = await Manage(
            c, """{"action":"respond","request_id":"r1","target_request_id":"nope"}""");
        Assert.Equal("error", resp["status"]!.GetValue<string>());
    }

    // ── device ownership ─────────────────────────────────────────────────

    [Fact]
    public async Task SubscribingTracksTheDeviceEvenWhenDisconnected()
    {
        // The set is what the reconnect path replays and what the command
        // filter consults, so it must be populated before the socket exists.
        var c = NewClient();
        await c.SubscribeCommandsAsync("light.01");

        var devices = (DeviceTracker)typeof(PluginClient)
            .GetField("_devices", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(c)!;
        Assert.True(devices.Contains("light.01"));

        await c.UnsubscribeCommandsAsync("light.01");
        Assert.False(devices.Contains("light.01"));
    }
}
