# hc-plugin-sdk-dotnet

[![CI](https://github.com/homeCore-io/hc-plugin-sdk-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/homeCore-io/hc-plugin-sdk-dotnet/actions/workflows/ci.yml)

Write a [homeCore](https://github.com/homeCore-io/homeCore) plugin in .NET.

Create a `PluginClient`, say what your devices are, handle commands. The SDK
covers the MQTT connection, registration, the management protocol, notices, and
capability actions.

Targets **net8.0**, so it runs on .NET 8 and later.

## Your first plugin

```csharp
using System.Text.Json.Nodes;
using HomeCore.PluginSdk;

var client = new PluginClient(new PluginOptions { PluginId = "plugin.mylight" });

client.OnConnected += async () =>
{
    // Register here, not before connecting — this runs again after a reconnect.
    await client.RegisterDeviceFullAsync("light.01", "Desk Lamp", deviceType: "light");
    await client.PublishAvailabilityAsync("light.01", true);
    await client.PublishStateAsync("light.01", new { on = false, brightness = 0 });
};

client.OnCommand += async (deviceId, payload) =>
{
    // Do whatever the real device needs, then publish what actually happened —
    // homeCore never writes device state itself.
    var state = new JsonObject { ["on"] = payload.GetProperty("on").GetBoolean() };
    await client.PublishStateForCommandAsync(deviceId, state, payload, "mylight");
};

await client.ConnectAsync();
await client.RunAsync();
```

Point it at a broker with `PluginOptions`, or the `HC_BROKER_HOST` /
`HC_BROKER_PORT` / `HC_PLUGIN_PASSWORD` environment variables.

## How a plugin fits into homeCore

Everything travels over MQTT. Your plugin owns its devices' state; homeCore owns
the rules and the UI.

```
your device  ←→  your plugin  ──state──▶  homeCore  ──▶  rules, UI, history
                              ◀──cmd────
```

Three consequences worth internalising:

1. **Publish what happened, not what was asked.** A command is a request. If the
   bulb refuses, publish the state it is actually in. That is why the UI can
   show a light as off after a failed command instead of lying.
2. **Register in `OnConnected`.** It fires on every connect, so a reconnect
   re-registers and re-subscribes.
3. **You only see your own devices.** Registering a device subscribes to that
   device's command topic and nothing else.

## Installing it into homeCore

homeCore owns your plugin's config at `config/plugins/<plugin_id>.toml` and
passes the path as the first argument:

```csharp
var configPath = args.Length > 0 ? args[0] : "config/config.toml";
```

Declare the plugin in homeCore's `homecore.toml` so it gets supervised:

```toml
[[plugins]]
id      = "plugin.mylight"
binary  = "/usr/bin/dotnet"
config  = "config/plugins/plugin.mylight.toml"
enabled = true
```

## Management: heartbeat, config, actions

Call `EnableManagementAsync` from `OnConnected` and homeCore can supervise the
plugin — heartbeat it, restart it, read and write its config, change its log
level. Without it the plugin runs but shows as offline.

```csharp
await client.EnableManagementAsync(new ManagementOptions
{
    HeartbeatIntervalSecs = 60,   // core marks a plugin offline after 90s
    Version = "1.2.0",
    ConfigPath = configPath,
});
```

## Notices — telling the operator what is wrong

A status of *active* answers "is the process alive". It cannot say "alive, but
unable to do its job", and that is the state operators actually get stuck in.

A notice puts your diagnosis on the plugin's card in the UI:

```csharp
if (!BridgeReachable())
{
    client.Notices.Raise(PluginNotice.Error(
        "bridge_unreachable",
        "The bridge stopped answering, so no device state is updating.",
        "Check that the bridge is powered on and on this network."));
}
else
{
    client.Notices.Clear("bridge_unreachable");
}
```

**A notice is state, not a log line.** The full set rides on every heartbeat and
homeCore replaces what it held, so a cleared notice disappears on its own —
nothing to acknowledge, nothing to expire.

The trap is raising once at startup and never looking again. A plugin that
reports `no_devices_configured` at boot is still showing it after the operator
has added devices. Re-derive conditions where you already loop: after a poll,
after a reconnect, after a config change. `Notices.Set([...])` replaces the
whole set at once, which is the safest shape when a sync cycle recomputes
everything.

## Capability actions — buttons in the UI

Declare an action and it appears as a button on your plugin's page, and becomes
callable from hc-mcp. Neither needs code written for your plugin specifically.

```csharp
await client.EnableManagementAsync(new ManagementOptions
{
    ConfigPath = configPath,
    Capabilities = new Capabilities
    {
        Actions = new[]
        {
            new PluginAction
            {
                Id = "rescan",
                Label = "Rescan devices",
                Description = "Ask the bridge for its current device list.",
            },
        },
    },
});

client.OnAction = async (action, @params, ctx) =>
{
    if (action == "rescan")
    {
        var found = await RescanAsync();
        return new JsonObject { ["found"] = found.Count };  // the result
    }
    return null;                                            // null → "not mine"
};
```

### Actions that take a while

Set `Stream = true` and your handler receives a `StreamContext` to report
through as it works. That is what drives a live progress bar and a list of
devices appearing one at a time, instead of a spinner that says nothing.

```csharp
new PluginAction
{
    Id = "discover", Label = "Discover devices", Stream = true,
    Cancelable = true, ItemKey = "serial", TimeoutMs = 30_000,
}
```

```csharp
client.OnAction = async (action, @params, ctx) =>
{
    if (action != "discover") return null;
    var hosts = Candidates();
    for (var i = 0; i < hosts.Count; i++)
    {
        if (ctx!.IsCanceled())          // cooperative — nothing interrupts you
        {
            await ctx.CanceledAsync();
            return null;
        }
        await ctx.ProgressAsync(percent: 100 * i / hosts.Count, message: $"Probing {hosts[i]}");
        var dev = await ProbeAsync(hosts[i]);
        if (dev is not null)
            await ctx.ItemAddAsync(new JsonObject { ["serial"] = dev.Serial, ["name"] = dev.Name });
    }
    await ctx!.CompleteAsync(new JsonObject { ["found"] = hosts.Count });
    return null;
};
```

Streaming handlers run on their own task, so blocking work is fine.

| Stage | Meaning |
|---|---|
| `ProgressAsync(...)` | percent / label / message, as often as useful |
| `ItemAddAsync/ItemUpdateAsync/ItemRemoveAsync(data)` | one thing found or changed — include the `ItemKey` field so the UI updates a row rather than appending |
| `WarningAsync(msg)` | recoverable; **the stream continues** |
| `AwaitingUserAsync(prompt)` | ask for something, then `await ctx.AwaitRespondAsync()` |
| `CompleteAsync(data)` | terminal, success |
| `ErrorAsync(msg)` | terminal, failure |
| `CanceledAsync()` | terminal, after you notice `IsCanceled()` |

Terminal stages are latched — the first wins, a second throws. If your handler
returns or throws without emitting one, the SDK sends an `error`, so the UI is
never left waiting on a stream that quietly stopped.

### Asking the operator something

```csharp
await ctx.AwaitingUserAsync("Press the pairing button on the device now.");
var answer = await ctx.AwaitRespondAsync();
```

## Cross-device plugins

To read devices you do **not** own — a thermostat consuming sensors from other
plugins — subscribe explicitly and handle `OnState`:

```csharp
await client.SubscribeStateAsync("sensor.hallway_temp");
client.OnState = async (deviceId, state) => await RecomputeAsync(deviceId, state);
```

This needs a broader broker ACL than a normal plugin:
`allow_sub = ["homecore/devices/+/state"]`.

## Remote config

With `ConfigPath` set, homeCore can read and write your config file. The raw
TOML editor sends text, which the SDK writes verbatim.

If you declare a `ConfigSchema`, the UI renders a form and sends a structured
object instead. The SDK will not guess at TOML serialisation, so set
`OnSetConfig` to take it:

```csharp
client.OnSetConfig = async config =>
{
    await File.WriteAllTextAsync(configPath, ToToml(config));
    return true;      // false → the SDK answers with an error
};
```

## API reference

### Devices

| Method | Purpose |
|---|---|
| `RegisterDeviceFullAsync(id, name, deviceType:, area:, capabilities:)` | Register. Everything optional but id and name |
| `RegisterDeviceTypedAsync(id, name, deviceType, area)` | Register against a built-in type |
| `RegisterDeviceAsync(id, name, capabilities, area)` | Register with an explicit JSON Schema |
| `RegisterDeviceSchemaAsync(id, schema)` | Publish a schema separately |
| `UnregisterDeviceAsync(id)` | Retire it and clear its retained topics |
| `PublishAvailabilityAsync(id, bool)` | online / offline |

Registering also subscribes to that device's commands. In the Rust SDK those are
two separate calls and forgetting the second is the classic first-plugin bug;
here it is one.

### State

| Method | Purpose |
|---|---|
| `PublishStateAsync(id, state)` | Full state, retained |
| `PublishStatePartialAsync(id, patch)` | Merge-patch — only the keys given |
| `PublishStateForCommandAsync(id, state, cmd, fallbackSource)` | Full state, with provenance from the command |
| `PublishStatePartialForCommandAsync(...)` | The partial equivalent |

Use the `ForCommand` forms when responding to a command: they carry who caused
the change, so the UI and the audit log can say so.

### Plugin

| Member | Purpose |
|---|---|
| `EnableManagementAsync(ManagementOptions)` | Heartbeat, remote management, action manifest |
| `EnableLogForwarding(minLevel)` | Send your logs to homeCore's live log stream |
| `ForwardLogAsync(level, message, target:, fields:)` | Forward one line directly |
| `PublishPluginStatusAsync(status)` | active / degraded / offline |
| `PublishEventAsync(type, payload)` | A structured event on the bus |
| `Notices` | `.Raise()`, `.Clear()`, `.Set()`, `.Snapshot()` |

### Events and handlers

| Member | When |
|---|---|
| `OnConnected` | Connected. Register devices, enable management |
| `OnCommand` | A command for one of your devices |
| `OnAction` | A capability action; `ctx` is non-null only when streaming |
| `OnState` | A device you subscribed to changed |
| `OnSetConfig` | A structured config write |
| `OnManagementCommand` | Legacy escape hatch, tried before `OnAction` |

## Log forwarding

Send this plugin's logs to homeCore's live log stream, so they appear alongside
core's own instead of only in the plugin's stdout. Register the provider on any
logging builder and everything the plugin logs is forwarded:

```csharp
client.EnableLogForwarding(LogLevel.Information);

using var factory = LoggerFactory.Create(b =>
{
    b.AddConsole();
    b.AddHomeCore(client, LogLevel.Information);
});
var log = factory.CreateLogger("mylight.bridge");

log.LogInformation("connected to {Host}", host);
```

Forwarding is **off until you enable it** — linking this SDK does not start
shipping your logs to a topic anything can subscribe to. An operator can raise
or lower the forwarded level at runtime from the UI (`set_log_level`); that
affects forwarding only, not your own console or file sinks.

`ForwardLogAsync` is the direct route for code with no `ILogger` to hand.

### Secrets

The log topic is one anything can subscribe to, so fields whose **names** look
secret — anything containing `password`, `secret`, `token`, `key`, `psk`,
`passcode`, `credential`, or `auth` — are published as `<redacted>`.

**.NET needs more care here than Rust does.** In Rust,
`tracing::info!(api_key = %k, "connecting")` keeps the value out of the message
text, so redacting the field is enough. .NET's structured logging renders every
template argument *into* the message, so this:

```csharp
log.LogInformation("connecting with {ApiKey}", key);
```

would publish the key in the message even with the field masked. Masking that
looks like protection but is not, so the SDK also removes the values of
secret-named fields from the rendered message.

That is a backstop, not a licence. A secret you interpolate yourself —
`log.LogInformation($"connecting with {key}")` — has no field name to be
recognised by, and nothing can find it. Values shorter than six characters are
left alone, because replacing every `"1"` in a sentence would turn the line into
nonsense.

The honest rule stays: do not log secrets.

## Parity with the Rust SDK

Everything the Rust SDK does is here: registration, state, availability, the
management protocol, notices, capability actions including streaming,
cross-device state subscription, and log forwarding.

Not here: device persistence / `reconcile_devices`, the Rust SDK's helper for
unregistering devices that disappeared from your upstream while the plugin was
down. That is the only remaining gap, and it is the same one in the Python and
Node.js SDKs.

## Development

```bash
dotnet build --configuration Release
dotnet test tests/HomeCore.PluginSdk.Tests.csproj
```

The library targets net8.0; the test project targets the current runtime, so
`dotnet test` works on a machine that has only the latest .NET installed.

`examples/DiscoveryPlugin.cs` demonstrates notices and both kinds of capability
action.

## License

Dual-licensed under **MIT** or **Apache-2.0**, at your option.
