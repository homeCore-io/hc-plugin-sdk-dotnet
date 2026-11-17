# hc-plugin-sdk-dotnet

[![CI](https://github.com/homeCore-io/hc-plugin-sdk-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/homeCore-io/hc-plugin-sdk-dotnet/actions/workflows/ci.yml)

Write a [homeCore](https://github.com/homeCore-io/homeCore) plugin in .NET.

Create a `PluginClient`, say what your devices are, handle commands. The SDK
covers the MQTT connection, registration, the management protocol, notices,
alerts, and capability actions.

## Installing

Targets **net8.0**, so it runs on .NET 8 and later.

### Working inside the homeCore workspace

This is the normal case today. If you cloned the whole workspace — `core/`,
`plugins/`, `sdks/` side by side — install from the checkout. Your plugin then
picks up SDK edits with no commit, tag, or reinstall in between, which is what
you want while developing the two together.

```bash
# from your plugin's directory, e.g. plugins/hc-mything/
dotnet add reference ../../sdks/hc-plugin-sdk-dotnet/HomeCoreSdk.csproj
```

A project reference builds the SDK from source as part of your build, so an
edit in `sdks/hc-plugin-sdk-dotnet` takes effect on the next `dotnet build`.

### From a release

Nothing is published to NuGet yet, and NuGet can't install from git, so install
from the GitHub tag: clone it beside your plugin's project and add a project
reference.

```bash
# from the folder that holds your plugin's project folder
git clone --branch v0.4.5 https://github.com/homeCore-io/hc-plugin-sdk-dotnet
cd hc-mylight
dotnet add reference ../hc-plugin-sdk-dotnet/HomeCoreSdk.csproj
```

Keep your project in its own folder beside the SDK's, never inside it: the
SDK's project file compiles every `.cs` file under its folder, so a plugin
created there gets built into the library.

## Your first plugin

homeCore starts a plugin with one argument, the path of its config file, and
writes a `[homecore]` table into that file with everything needed to log in to
its broker:

```toml
[homecore]
broker_host = "127.0.0.1"
broker_port = 1883
plugin_id   = "plugin.mylight"
password    = "..."
```

This SDK has no config loader, so the plugin reads the file itself. Any TOML
parser works; this uses [Tomlyn](https://www.nuget.org/packages/Tomlyn) 1.0 or
later, and logs to the console:

```bash
dotnet new console -o hc-mylight
cd hc-mylight
dotnet add reference ../hc-plugin-sdk-dotnet/HomeCoreSdk.csproj
dotnet add package Tomlyn
dotnet add package Microsoft.Extensions.Logging.Console
```

```csharp
using System.Text.Json.Nodes;
using HomeCore.PluginSdk;
using Microsoft.Extensions.Logging;
using Tomlyn;
using Tomlyn.Model;

// homeCore starts a plugin as `<binary> <config path>`.
if (args.Length < 1)
{
    Console.Error.WriteLine("usage: hc-mylight <config.toml>");
    return 2;
}
var configPath = args[0];
var config = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(configPath))!;
var hc = (TomlTable)config["homecore"]!;

using var logs = LoggerFactory.Create(b => b.AddConsole());

var client = new PluginClient(new PluginOptions
{
    PluginId = (string)hc["plugin_id"],
    BrokerHost = hc.TryGetValue("broker_host", out var host) ? (string)host : null,
    BrokerPort = hc.TryGetValue("broker_port", out var port) ? (int)(long)port : null,
    Password = hc.TryGetValue("password", out var password) ? (string)password : null,
}, logs.CreateLogger("mylight"));

client.OnConnected += async () =>
{
    await client.EnableManagementAsync(new ManagementOptions
    {
        Version = "1.0.0",
        ConfigPath = configPath,
    });
    await client.RegisterDeviceFullAsync("light.01", "Desk Lamp", deviceType: "light");
    await client.PublishAvailabilityAsync("light.01", true);
    await client.PublishStateAsync("light.01", new { on = false, brightness_pct = 0 });
};

client.OnCommand += async (deviceId, payload) =>
{
    // Do whatever the real device needs, then publish what actually happened:
    // homeCore never writes device state itself.
    var state = new JsonObject { ["on"] = payload.GetProperty("on").GetBoolean() };
    await client.PublishStateForCommandAsync(deviceId, state, payload, "mylight");
};

await client.ConnectAsync();
await client.RunAsync();
return 0;
```

Take the plugin id from `plugin_id`: homeCore minted the password for that id,
and the broker refuses any other. Keep your own settings in other tables of the
same file, and pass `ConfigPath` to `EnableManagementAsync` so homeCore's
config editor edits that same file.

`PluginOptions` left unset fall back to the `HC_BROKER_HOST` /
`HC_BROKER_PORT` / `HC_PLUGIN_PASSWORD` environment variables, which are for
running a plugin by hand without a config file.

## How a plugin fits into homeCore

Everything travels over MQTT. Your plugin owns its devices' state; homeCore owns
the rules and the UI.

```
your device  ←→  your plugin  ──state──▶  homeCore  ──▶  rules, UI, history
                              ◀──cmd────
```

Four consequences worth internalising:

1. **Publish what happened, not what was asked.** A command is a request. If the
   bulb refuses, publish the state it is actually in. That is why the UI can
   show a light as off after a failed command instead of lying.
2. **Register in `OnConnected`.** It fires once `ConnectAsync` has connected
   and subscribed to what the SDK needs. This SDK does not reconnect on its
   own; see "When the connection drops" below.
3. **You only see your own devices.** Registering a device subscribes to that
   device's command topic and nothing else.
4. **Use homeCore's names and units.** A shared idea has one name, one unit:
   `on`, `brightness_pct` (0–100), `color_temp` (kelvin), `transition_ms`,
   `open` (`true` = open), `occupied`, `battery_pct`, `humidity`,
   `illuminance` (lux), temperatures in **°C**, and the rest of the table in
   `core/crates/hc-types/src/device_vocabulary.rs`. Convert from the device's
   own scale inside the plugin, both ways; showing °F is the app's job. A
   command arrives as one of two shapes — an attribute write, `{"on": true}`,
   or a declared action, `{"action": "toggle"}` — never `{"command": …}`.
   `{"on": true}` restores the last level. See "Names and units" in
   `core/docs/plugin-development.md`.

## Installing it into homeCore

homeCore owns your plugin's config at `config/plugins/<plugin_id>.toml` and
starts the plugin as `<binary> <config path>`, so the path arrives as
`args[0]`.

Because the config path is the only argument, `binary` has to be an executable
that runs your plugin by itself. `/usr/bin/dotnet` won't work: it would be
started as `dotnet <config path>` and try to run the config file. Publish the
plugin, and point `binary` at the executable the publish writes (named after
the project):

```bash
# needs the .NET runtime on the machine that runs it
dotnet publish -c Release -o /opt/hc-mylight

# or bundle the runtime, so the machine needs no .NET at all
dotnet publish -c Release -r linux-x64 --self-contained -o /opt/hc-mylight
```

The first form looks for .NET in the usual places (`/usr/share/dotnet`, or
`DOTNET_ROOT` when set). Use the second when the runtime isn't installed where
it can find it, or to make the plugin a single folder to copy.

Declare the plugin in homeCore's `homecore.toml` so it gets supervised:

```toml
[[plugins]]
id      = "plugin.mylight"
binary  = "/opt/hc-mylight/hc-mylight"
config  = "config/plugins/plugin.mylight.toml"
enabled = true
```

## When the connection drops

This SDK does not reconnect. If the broker connection is lost (homeCore
restarted, the network dropped), the client stays disconnected: `OnConnected`
does not fire again, heartbeats stop, every publish throws
`MqttClientNotConnectedException`, and `RunAsync()` keeps waiting. homeCore
restarts a plugin it started once it has gone 5 minutes without a heartbeat,
and the new process connects and registers from the start.

To come back sooner, end the process yourself: when a publish throws because
the client is not connected, exit with a non-zero code. homeCore restarts a
plugin that exits, after a short backoff (2 seconds at first). A first
`ConnectAsync` that can't reach the broker throws too; let that end the
process for the same reason.

## Management: heartbeat, config, actions

Call `EnableManagementAsync` from `OnConnected` and homeCore can supervise the
plugin — heartbeat it, restart it, read and write its config, change its log
level. Without it the plugin runs but shows as offline.

```csharp
await client.EnableManagementAsync(new ManagementOptions
{
    HeartbeatIntervalSecs = 60,   // core marks a plugin offline after 90s
    Version = "1.2.0",
    ConfigPath = configPath,      // args[0], the file homeCore passed
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

## Alerts — what needs attention about a device

Notices are about the plugin; **alerts** are about its devices, and live in
the house's alert list (homeCore 0.1.85). Declare the kinds you can raise on
the manifest, then say which are true of a device now — the whole list each
time; an empty one clears them:

```csharp
await client.EnableManagementAsync(new ManagementOptions
{
    Capabilities = new Capabilities
    {
        AlertKinds = new[]
        {
            new PluginAlertKind
            {
                Topic = "firmware_update",
                Label = "Firmware update available",
                Severity = AlertSeverity.Info,
                DefaultOn = false, // news, not a problem: off until someone turns it on
            },
        },
    },
});

await client.SetAlertsAsync("wled_1", new[]
{
    new PluginAlert
    {
        Topic = "firmware_update",
        Reason = "Firmware 17.0.0 is out",
        Key = "17.0.0", // "skip this one" skips this version, not the kind
        Action = new AlertAction
        {
            Label = "Update…",
            Id = "install_firmware", // one of the device's actions
            Params = JsonNode.Parse("""{"version":"17.0.0"}"""),
            Confirm = true,
        },
    },
});

// ...and once it is installed:
await client.SetAlertsAsync("wled_1", Array.Empty<PluginAlert>());
```

**An alert is current state, not an event.** homeCore replaces what it held
with each list, so one you stop sending has cleared. Set it when something
changes, not on a timer.

homeCore decides whether each is shown and where it is sent, never shows a
plugin's alert as critical (a plugin asking for `Critical` gets `error`), and
lets automations start on, check and act on them. A kind's `DefaultOn`
defaults to `true`; set it `false` for news that isn't a problem, so it stays
off until someone turns it on. `AlertAction` runs one of the device's actions
by default; `Target = AlertActionTarget.Plugin` runs one of the plugin's own.
`SetPluginAlertsAsync(...)` does the same for alerts about the plugin itself —
a login that expired, a bridge that needs its button pressed. Alerts are
retained and said again on every reconnect; set while disconnected, they are
held and published on the next connect.

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
| `ProgressAsync(...)` | percent / label / message / data, as often as useful |
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

`ProgressAsync` and `AwaitingUserAsync` also take `data:`, a `JsonObject` a
plugin's own screen reads from the stream (text as it is written, a preview to
confirm). The generic UI ignores it.

### Who called

homeCore passes the verified caller with every action as a `Caller`: `Uid`,
`Username`, `Role`, `Scopes` and `Kind` (`user`, `api_key`, `local_admin` or
`whitelist`). It is never mixed into `params`. A streaming handler reads
`ctx.Caller`; for an immediate one, set `OnActionWithCaller` in place of
`OnAction` and the SDK passes it. Plugins that set only `OnAction` keep working.

```csharp
client.OnActionWithCaller = async (action, @params, ctx, caller) =>
{
    if (action == "whoami")
        return new JsonObject { ["name"] = caller?.Username };
    return null;
};
```

Most actions still need an admin (`plugins:write`), whatever role they declare.
An action declared with `RequiresRole = RequiresRole.SignedIn` can be called by
anyone signed in, household members and read-only accounts included, so use
`caller.Scopes` to stay within what that person may do.

## REST access from homeCore

A plugin that needs homeCore's REST API asks for scopes in its manifest, and
homeCore mints it a key on every start. Nothing to paste, nothing stored in the
config file.

```csharp
new Capabilities
{
    Actions = new[] { /* ... */ },
    ApiScopes = new[] { "devices:read", "devices:command" },
}
```

The key arrives with the `api_access` management action. The SDK keeps it in
`client.ApiAccess` (`Url`, `Key`, `Scopes`; the key never shows in
`ToString()`) and calls your handler:

```csharp
client.OnApiAccess = (url, key, scopes) =>
{
    rest = new MyClient(url, key);      // url already ends in /api/v1
    return Task.CompletedTask;
};

// or, after EnableManagementAsync:
var access = await client.WaitForApiAccessAsync(TimeSpan.FromSeconds(30));
```

homeCore leaves out scopes no plugin may hold (`users:write`,
`api_keys:admin`), so check `Scopes` rather than assuming you got everything.

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

## Your own screens

A plugin can carry web components — a page for itself, a panel for its devices
— and serve them itself. Build them into a folder with one ES module and a
`manifest.json` saying where each goes, ship the folder beside the program as
`ui/` (copy it to the output directory), and, before enabling management:

```csharp
if (Ui.FindDir() is { } dir)
    client.WithUiDir(dir, "1.2.0");
await client.EnableManagementAsync(new ManagementOptions { Version = "1.2.0" });
```

`Ui.FindDir(projectDir)` looks, in order, at `HC_PLUGIN_UI_DIR`; `ui/` beside
the program (`AppContext.BaseDirectory`, how a package installs it); and, run
from a source checkout, `<project>/ui/dist` — `projectDir` when given, else the
nearest folder above the program holding a `.csproj`. Apart from the
environment variable, a folder counts only if it holds `manifest.json`.

The SDK declares them on the manifest (`ui`, with a hash of the files as their
`build`) and answers homeCore's requests for the files; homeCore passes them to
the web app and keeps none. A plugin with screens and no actions still
publishes its manifest. A folder that isn't there, or declares nowhere the app
can put it, is said in the log and skipped — the app shows its generic screens.
Every slot goes on as declared, extra fields (`label`, `forms`, `settings`, …)
included: which slots exist is homeCore's to decide, so the SDK never keeps a
plugin from offering one newer than itself.
Each element is handed the app's `host` — the plugin's devices, commands,
actions, settings and the app's look; its types are `core/sdk/ui`
(`@homecore/plugin-ui`). `plugins/hc-ecowitt/ui` in core is a complete one.

The `build` is the same in every homeCore SDK for the same folder: the first 8
bytes of SHA-256 over every file, sorted by path, each fed as its path, a 0
byte and its contents.

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
| `WithUiDir(dir, version)` | Carry and serve the plugin's own screens |
| `EnableLogForwarding(minLevel)` | Send your logs to homeCore's live log stream |
| `ForwardLogAsync(level, message, target:, fields:)` | Forward one line directly |
| `PublishPluginStatusAsync(status)` | active / degraded / offline |
| `PublishEventAsync(type, payload)` | A structured event on the bus |
| `Notices` | `.Raise()`, `.Clear()`, `.Set()`, `.Snapshot()` |
| `SetAlertsAsync(deviceId, alerts)` | The alerts true of a device now; empty clears |
| `SetPluginAlertsAsync(alerts)` | The same, about the plugin itself |
| `EnableDevicePersistence(path)` | Remember registered devices across restarts |
| `ReconcileDevicesAsync(live)` | Unregister everything not in the live set |
| `StopAsync()` | `OnStop`, every device offline, the plugin offline, disconnect |
| `ApiAccess` / `WaitForApiAccessAsync(timeout)` | REST access homeCore granted, or null |

### Events and handlers

| Member | When |
|---|---|
| `OnConnected` | Connected. Register devices, enable management |
| `OnCommand` | A command for one of your devices |
| `OnAction` | A capability action; `ctx` is non-null only when streaming |
| `OnActionWithCaller` | The same, with the `Caller`; used in place of `OnAction` when set |
| `OnState` | A device you subscribed to changed |
| `OnSetConfig` | A structured config write |
| `OnApiAccess` | homeCore sent REST access (every start) |
| `OnStop` | Stopping. Leave what you control safe; 1 second |
| `OnManagementCommand` | Legacy escape hatch, tried before `OnAction` |

**Stopping.** homeCore and a plugin runtime stop a plugin with SIGTERM, and
kill it 2 seconds later. After `ConnectAsync`, SIGTERM and Ctrl-C run
`StopAsync()`: your `OnStop`, then every registered device offline, the plugin
offline, and a disconnect. Then the process exits. Called directly,
`StopAsync()` ends `RunAsync()` and leaves the process running.

## Log forwarding

Send this plugin's logs to homeCore's live log stream, so they appear alongside
core's own instead of only in the plugin's stdout. Register the provider on any
logging builder and everything the plugin logs is forwarded. `AddConsole()`
comes from a package the SDK doesn't reference, so add it to your plugin:

```bash
dotnet add package Microsoft.Extensions.Logging.Console
```

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

## Device persistence

When a device disappears from your upstream — a bulb deleted from the bridge, a
Z-Wave node excluded, an entry removed from config — its homeCore record has to
go too. Otherwise it lingers forever, still shown in the UI and still accepting
commands nothing will execute.

Knowing what disappeared means knowing what existed *before*, and a plugin that
has just restarted knows nothing. So the SDK can mirror every
register/unregister to a small JSON file:

```csharp
// Once, at startup, before registering anything.
client.EnableDevicePersistence(Path.Combine(configDir, ".published-device-ids.json"));
```

Then, after a sync where you know the full live set:

```csharp
var report = await client.ReconcileDevicesAsync(bridge.Devices().Select(d => d.Id));
// report.StaleUnregistered — gone upstream, now gone from homeCore
// report.UnknownInLive     — ids you passed but never registered
```

Devices registered in *earlier runs* are retired too, which is the point: a
fresh process that has registered nothing can still clean up what the previous
one left behind.

The plugin id is inserted into the filename
(`.published-device-ids.plugin.hue.json`), because real deployments keep every
plugin's config in one directory and every plugin derives this path the same
way — unscoped, they would share one file and retire each other's devices.

**Only reconcile after a sync you trust.** On a partial fetch this unregisters
live devices behind a temporarily unreachable upstream — which looks exactly
like the bug it exists to prevent, except the devices were fine. Track an
"everything succeeded" flag across your per-source loop and pass the live set
only when it holds.

Plugins whose upstream reports irregularly — battery sensors that go quiet for
hours — should enable persistence but skip auto-reconcile. An operator can clear
zombies with `DELETE /api/v1/plugins/{id}/devices`.

## Parity with the Rust SDK

Almost everything the Rust SDK does is here: registration, state, availability, the
management protocol, notices, alerts, capability actions including streaming,
cross-device state subscription, log forwarding, and the plugin's own screens.

One thing is Rust-only so far: **files sent to an action** (`POST
/plugins/{id}/upload/{action}`, which the Rust SDK receives as `upload_chunk`
pieces and hands the action as `file_path`).

The whole process of building a plugin — screens and cards included — is in
the homeCore repository's `docs/plugin-development.md`.

## Development

```bash
dotnet build --configuration Release
dotnet test tests/HomeCore.PluginSdk.Tests.csproj
```

The library targets net8.0; the test project targets the current runtime, so
`dotnet test` works on a machine that has only the latest .NET installed.

`examples/DiscoveryPlugin.cs` demonstrates notices and both kinds of capability
action. Its header says how to run it from a project beside the SDK's folder.

## License

Dual-licensed under **MIT** or **Apache-2.0**, at your option.
