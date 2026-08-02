# hc-plugin-sdk-dotnet

.NET plugin SDK for HomeCore. Create a `PluginClient`, subscribe to events, call `RunAsync()`.

## Quick start

```csharp
using HomeCore.PluginSdk;

var client = new PluginClient(new PluginOptions { PluginId = "plugin.example" });

client.OnCommand += (deviceId, payload) => {
    Console.WriteLine($"Command for {deviceId}: {payload}");
};

await client.ConnectAsync();
await client.RegisterDeviceFullAsync("example_sensor", "Example Sensor", deviceType: "sensor");

// Registration and command subscription are separate: without this the device
// appears in homeCore and silently ignores every command.
await client.SubscribeCommandsAsync("example_sensor");

await client.PublishStateAsync("example_sensor", new { temperature = 21.5 });
await client.PublishAvailabilityAsync("example_sensor", true);
await client.RunAsync();
```

## Features

- **PluginClient** — async MQTT client with connection lifecycle events
- **Device registration** — full schema or by type name
- **State publishing** — full (retained) and partial (merge-patch) with change metadata
- **Management protocol** — heartbeat, remote config, dynamic log level
- **Configuration** — `PluginOptions`, env vars (`HC_BROKER_HOST`, `HC_BROKER_PORT`, `HC_PLUGIN_PASSWORD`), or defaults

## What this SDK does not have

The Rust SDK is the reference implementation and is ahead of this one in two
places that are worth knowing about before you choose a language:

- **Notices** — the structured, self-clearing problem reports the web UI shows
  on a plugin's card ("bridge unreachable", "no devices found yet"). A plugin
  written with this SDK can log a problem, but cannot surface it there.
- **Capability actions** — the plugin-level command manifest that makes the UI
  render buttons ("Pair bridge", "Rescan") and lets MCP call them, with no UI
  code. Device *capability schemas* work fine here; it is the plugin's own
  action manifest that is Rust-only.

Everything else — registration, state publishing, availability, the
management protocol, log forwarding — is the same across all four SDKs.

Requires .NET 8.0.
