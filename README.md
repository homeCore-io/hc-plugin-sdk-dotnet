# hc-plugin-sdk-dotnet

.NET plugin SDK for HomeCore. Create a `PluginClient`, subscribe to events, call `RunAsync()`.

## Quick start

```csharp
using HomeCoreSdk;

var client = new PluginClient(new PluginOptions { PluginId = "plugin.example" });

client.OnCommand += async (deviceId, payload) => {
    Console.WriteLine($"Command for {deviceId}: {payload}");
};

await client.ConnectAsync();
await client.RegisterDeviceFull("example_sensor", "Example Sensor", deviceType: "sensor");
await client.PublishState("example_sensor", new { temperature = 21.5 });
await client.RunAsync();
```

## Features

- **PluginClient** — async MQTT client with connection lifecycle events
- **Device registration** — full schema or by type name
- **State publishing** — full (retained) and partial (merge-patch) with change metadata
- **Management protocol** — heartbeat, remote config, dynamic log level
- **Configuration** — `PluginOptions`, env vars (`HC_BROKER_HOST`, `HC_BROKER_PORT`, `HC_PLUGIN_PASSWORD`), or defaults

Requires .NET 8.0.
