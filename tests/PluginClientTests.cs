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

    // ── who called ───────────────────────────────────────────────────────

    private const string WithCaller =
        """{"action":"whoami","request_id":"r1","value":7,"caller":{"uid":"u1","username":"ann","role":"user","scopes":["devices:read"],"kind":"api_key"}}""";

    private static Capabilities WhoAmI(bool stream = false) => new()
    {
        Actions = new[]
        {
            new PluginAction { Id = "whoami", Label = "Who am I", Stream = stream },
        },
    };

    [Fact]
    public async Task OnActionWithCallerHearsWhoCalledAndParamsStayClean()
    {
        var c = NewClient();
        SetMgmt(c, new ManagementOptions { Capabilities = WhoAmI() });
        Caller? seen = null;
        string? paramsSeen = null;
        c.OnActionWithCaller = (_, @params, ctx, caller) =>
        {
            seen = caller;
            paramsSeen = @params.GetRawText();
            return Task.FromResult<JsonObject?>(new JsonObject { ["name"] = caller?.Username });
        };

        var resp = await Manage(c, WithCaller);
        Assert.Equal("ann", resp["name"]!.GetValue<string>());
        Assert.Equal(
            ("u1", "ann", "user", "api_key"),
            (seen!.Uid, seen.Username, seen.Role, seen.Kind));
        Assert.Equal(new[] { "devices:read" }, seen.Scopes);
        Assert.DoesNotContain("caller", paramsSeen);
        Assert.Contains("value", paramsSeen);
    }

    [Fact]
    public async Task OnActionAloneStillWorksAndNeverSeesTheCaller()
    {
        var c = NewClient();
        SetMgmt(c, new ManagementOptions { Capabilities = WhoAmI() });
        string? paramsSeen = null;
        c.OnAction = (_, @params, _) =>
        {
            paramsSeen = @params.GetRawText();
            return Task.FromResult<JsonObject?>(new JsonObject());
        };

        var resp = await Manage(c, WithCaller);
        Assert.Equal("ok", resp["status"]!.GetValue<string>());
        Assert.DoesNotContain("caller", paramsSeen);
    }

    [Fact]
    public async Task OnActionWithCallerWinsWhenBothAreSet()
    {
        var c = NewClient();
        SetMgmt(c, new ManagementOptions { Capabilities = WhoAmI() });
        c.OnAction = (_, _, _) => Task.FromResult<JsonObject?>(new JsonObject { ["by"] = "plain" });
        c.OnActionWithCaller = (_, _, _, _) =>
            Task.FromResult<JsonObject?>(new JsonObject { ["by"] = "caller" });

        var resp = await Manage(c, WithCaller);
        Assert.Equal("caller", resp["by"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"action":"whoami","request_id":"r1"}""")]
    [InlineData("""{"action":"whoami","request_id":"r1","caller":"ann"}""")]
    [InlineData("""{"action":"whoami","request_id":"r1","caller":null}""")]
    public async Task CallerIsNullWhenAbsentOrNotAnObject(string cmd)
    {
        var c = NewClient();
        SetMgmt(c, new ManagementOptions { Capabilities = WhoAmI() });
        var called = false;
        Caller? seen = null;
        c.OnActionWithCaller = (_, _, _, caller) =>
        {
            called = true;
            seen = caller;
            return Task.FromResult<JsonObject?>(new JsonObject());
        };

        await Manage(c, cmd);
        Assert.True(called);
        Assert.Null(seen);
    }

    [Fact]
    public async Task AStreamingActionReadsTheCallerFromItsContext()
    {
        var c = NewClient();
        c.PublishSink = (_, _, _) => Task.CompletedTask;
        SetMgmt(c, new ManagementOptions { Capabilities = WhoAmI(stream: true) });
        var got = new TaskCompletionSource<Caller?>();
        c.OnAction = async (_, _, ctx) =>
        {
            got.TrySetResult(ctx!.Caller);
            await ctx.CompleteAsync();
            return null;
        };

        var resp = await Manage(c, WithCaller);
        Assert.Equal("accepted", resp["status"]!.GetValue<string>());
        var caller = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("ann", caller!.Username);
        Assert.Equal("api_key", caller.Kind);
    }

    // ── REST access from homeCore ────────────────────────────────────────

    [Fact]
    public async Task ApiAccessIsKeptAndTheHookHearsIt()
    {
        var c = NewClient();
        SetMgmt(c, new ManagementOptions());
        (string Url, string Key, IReadOnlyList<string> Scopes)? heard = null;
        c.OnApiAccess = (url, key, scopes) => { heard = (url, key, scopes); return Task.CompletedTask; };
        Assert.Null(c.ApiAccess);

        var resp = await Manage(
            c,
            """{"action":"api_access","request_id":"r1","url":"http://127.0.0.1:8080/api/v1/","key":"hc_sk_secret","scopes":["devices:read","devices:command"]}""");

        Assert.Equal("ok", resp["status"]!.GetValue<string>());
        var access = c.ApiAccess!;
        Assert.Equal("http://127.0.0.1:8080/api/v1", access.Url);
        Assert.Equal("hc_sk_secret", access.Key);
        Assert.Equal(new[] { "devices:read", "devices:command" }, access.Scopes);
        Assert.Equal(("http://127.0.0.1:8080/api/v1", "hc_sk_secret"), (heard!.Value.Url, heard.Value.Key));
        Assert.Same(access, await c.WaitForApiAccessAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task ApiAccessAcceptsANestedParamsObject()
    {
        var c = NewClient();
        SetMgmt(c, new ManagementOptions());
        var resp = await Manage(
            c,
            """{"action":"api_access","request_id":"r1","params":{"url":"http://h/api/v1","key":"k"}}""");
        Assert.Equal("ok", resp["status"]!.GetValue<string>());
        Assert.Equal("http://h/api/v1", c.ApiAccess!.Url);
        Assert.Empty(c.ApiAccess.Scopes);
    }

    [Theory]
    [InlineData("""{"action":"api_access","request_id":"r1","key":"k"}""")]
    [InlineData("""{"action":"api_access","request_id":"r1","url":"http://h/api/v1"}""")]
    [InlineData("""{"action":"api_access","request_id":"r1","url":"","key":"k"}""")]
    public async Task ApiAccessWithoutAUrlAndAKeyIsAnError(string cmd)
    {
        var c = NewClient();
        SetMgmt(c, new ManagementOptions());
        var resp = await Manage(c, cmd);
        Assert.Equal("error", resp["status"]!.GetValue<string>());
        Assert.Equal("api_access needs a url and a key", resp["error"]!.GetValue<string>());
        Assert.Null(c.ApiAccess);
    }

    [Fact]
    public async Task AThrowingApiAccessHookStillAnswersOk()
    {
        var c = NewClient();
        SetMgmt(c, new ManagementOptions());
        c.OnApiAccess = (_, _, _) => throw new InvalidOperationException("hook exploded");
        var resp = await Manage(
            c, """{"action":"api_access","request_id":"r1","url":"http://h/api/v1","key":"k"}""");
        Assert.Equal("ok", resp["status"]!.GetValue<string>());
        Assert.NotNull(c.ApiAccess);
    }

    [Fact]
    public async Task WaitForApiAccessReturnsNullOnTimeoutAndTheAccessOnceItArrives()
    {
        var c = NewClient();
        SetMgmt(c, new ManagementOptions());
        Assert.Null(await c.WaitForApiAccessAsync(TimeSpan.FromMilliseconds(20)));

        var waiting = c.WaitForApiAccessAsync(TimeSpan.FromSeconds(5));
        Assert.False(waiting.IsCompleted);
        await Manage(
            c, """{"action":"api_access","request_id":"r1","url":"http://h/api/v1","key":"k"}""");
        Assert.Equal("http://h/api/v1", (await waiting)!.Url);
    }

    [Fact]
    public async Task WaitForApiAccessHonoursCancellation()
    {
        var c = NewClient();
        using var cts = new CancellationTokenSource();
        var waiting = c.WaitForApiAccessAsync(TimeSpan.FromSeconds(5), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public void TheKeyNeverShowsInToString()
    {
        var access = new ApiAccess("http://h/api/v1", "hc_sk_secret", new[] { "devices:read" });
        var text = access.ToString();
        Assert.DoesNotContain("hc_sk_secret", text);
        Assert.Contains("http://h/api/v1", text);
        Assert.Contains("devices:read", text);
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
