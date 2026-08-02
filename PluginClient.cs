// HomeCore Plugin SDK for .NET
//
// Provides PluginClient for connecting to the HomeCore MQTT broker,
// publishing device state, registering devices, and handling commands.
//
// Usage:
//   var client = new PluginClient(new PluginOptions { PluginId = "plugin.mydevice" });
//   client.OnCommand += (deviceId, payload) => { /* handle command */ };
//   await client.ConnectAsync();
//   await client.RegisterDeviceTypedAsync("my_device_1", "My Device", "switch");
//   await client.PublishStateAsync("my_device_1", new { on = true });
//   await client.RunAsync(); // blocks until cancellation

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace HomeCore.PluginSdk;

/// <summary>
/// Configuration options for <see cref="PluginClient"/>.
/// </summary>
public sealed class PluginOptions
{
    /// <summary>Plugin identifier used as MQTT client ID and for topic routing.</summary>
    public required string PluginId { get; init; }

    /// <summary>MQTT broker hostname. Falls back to HC_BROKER_HOST env var, then "127.0.0.1".</summary>
    public string? BrokerHost { get; init; }

    /// <summary>MQTT broker port. Falls back to HC_BROKER_PORT env var, then 1883.</summary>
    public int? BrokerPort { get; init; }

    /// <summary>MQTT password. Falls back to HC_PLUGIN_PASSWORD env var.</summary>
    public string? Password { get; init; }

    internal string EffectiveBrokerHost =>
        BrokerHost
        ?? Environment.GetEnvironmentVariable("HC_BROKER_HOST")
        ?? "127.0.0.1";

    internal int EffectiveBrokerPort =>
        BrokerPort
        ?? (int.TryParse(Environment.GetEnvironmentVariable("HC_BROKER_PORT"), out var p) ? p : 1883);

    internal string EffectivePassword =>
        Password
        ?? Environment.GetEnvironmentVariable("HC_PLUGIN_PASSWORD")
        ?? "";
}

/// <summary>
/// Management protocol options for <see cref="PluginClient.EnableManagementAsync"/>.
/// </summary>
public sealed class ManagementOptions
{
    /// <summary>Heartbeat interval in seconds (default 60).</summary>
    public int HeartbeatIntervalSecs { get; init; } = 60;

    /// <summary>Plugin version string included in heartbeats.</summary>
    public string? Version { get; init; }

    /// <summary>Path to the plugin config file (enables get_config/set_config).</summary>
    public string? ConfigPath { get; init; }

    /// <summary>
    /// The action manifest. Declared actions become buttons on the plugin's
    /// page in hc-web and calls hc-mcp can make.
    /// </summary>
    public Capabilities? Capabilities { get; init; }
}

/// <summary>
/// Delegate for handling inbound device commands.
/// </summary>
public delegate Task CommandHandler(string deviceId, JsonElement payload);

/// <summary>
/// Delegate for handling management commands (set_log_level, custom actions).
/// </summary>
public delegate Task<JsonObject?> ManagementCommandHandler(string action, JsonElement command);

/// <summary>
/// Handles a capability action declared in the manifest.
/// </summary>
/// <param name="action">The action id.</param>
/// <param name="params">Everything the command carried but the envelope.</param>
/// <param name="ctx">
/// Present only for streaming actions. Report through it and return null.
/// </param>
/// <returns>
/// The result of an immediate action, or null to say "not mine" — the SDK then
/// answers with <c>unknown action</c>.
/// </returns>
public delegate Task<JsonObject?> ActionHandler(string action, JsonElement @params, StreamContext? ctx);

/// <summary>Handles a state update for a device this plugin does not own.</summary>
public delegate Task StateHandler(string deviceId, JsonElement state);

/// <summary>
/// Handles a structured <c>set_config</c> payload.
/// </summary>
/// <remarks>
/// homeCore sends config as raw text when the operator edits TOML directly, and
/// as an object when the plugin declared a <c>ConfigSchema</c> and the UI
/// rendered a form. The SDK writes the text form verbatim; it cannot turn an
/// object into TOML, so handle this if you declare a schema.
/// </remarks>
/// <returns>True if you handled and persisted it.</returns>
public delegate Task<bool> SetConfigHandler(JsonElement config);

/// <summary>
/// Connected HomeCore plugin client.
/// Publishes device state, registers devices, subscribes to commands,
/// and optionally runs the management protocol (heartbeat + remote config).
/// </summary>
public sealed class PluginClient : IAsyncDisposable
{
    private readonly PluginOptions _options;
    private readonly IMqttClient _mqtt;
    private readonly ILogger _logger;
    private readonly DateTime _startedAt = DateTime.UtcNow;

    private ManagementOptions? _mgmt;
    private CancellationTokenSource? _heartbeatCts;

    // Devices this plugin has registered. Drives the heartbeat's device_count,
    // decides which command topics we subscribe to, and — once persistence is
    // enabled — survives a restart so ReconcileDevicesAsync can tell what has
    // since disappeared.
    private readonly DeviceTracker _devices;
    private readonly Dictionary<string, StreamContext> _activeStreams = new();
    private readonly object _streamsLock = new();

    private bool _logForwardEnabled;
    private LogLevel _logForwardMinLevel = LogLevel.Information;

    /// <summary>This SDK's version, reported in every heartbeat.</summary>
    public const string SdkVersion = "0.2.0";

    /// <summary>
    /// The wire protocol this SDK speaks, which is core's hc-types version.
    /// Core compares it against its own to decide whether the two agree on the
    /// shape of a device, an event and a command.
    /// </summary>
    public const string ProtocolVersion = "0.1.5";

    /// <summary>The plugin identifier.</summary>
    public string PluginId => _options.PluginId;

    /// <summary>Fired when a device command arrives on homecore/devices/{id}/cmd.</summary>
    public event CommandHandler? OnCommand;

    /// <summary>Fired when a management command arrives that is not handled internally.
    /// Return a JsonObject response or null to use the default "unknown action" response.</summary>
    public event ManagementCommandHandler? OnManagementCommand;

    /// <summary>Fired after the MQTT connection is established.</summary>
    public event Func<Task>? OnConnected;

    /// <summary>Handles capability actions. See <see cref="ActionHandler"/>.</summary>
    public ActionHandler? OnAction { get; set; }

    /// <summary>
    /// A device subscribed to with <see cref="SubscribeStateAsync"/> changed.
    /// Only for cross-device consumers.
    /// </summary>
    public StateHandler? OnState { get; set; }

    /// <summary>Accepts a structured config write. See <see cref="SetConfigHandler"/>.</summary>
    public SetConfigHandler? OnSetConfig { get; set; }

    /// <summary>
    /// Conditions this plugin is currently reporting about itself. Raised and
    /// cleared by your code, republished in full on every heartbeat.
    /// </summary>
    public PluginNotices Notices { get; }

    public PluginClient(PluginOptions options, ILogger? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger.Instance;
        _devices = new DeviceTracker(_logger);

        var factory = new MqttFactory();
        _mqtt = factory.CreateMqttClient();

        // Notices ride on the heartbeat, so a change publishes one immediately —
        // otherwise a condition raised at startup would not reach the UI until
        // the next beat, up to a minute later.
        Notices = new PluginNotices(() =>
        {
            if (_mgmt is not null && _mqtt.IsConnected)
                _ = PublishHeartbeatAsync();
        });

        _mqtt.ApplicationMessageReceivedAsync += HandleMessageAsync;
    }

    // ── Connection ────────────────────────────────────────────────────────

    /// <summary>Connect to the HomeCore MQTT broker.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.EffectiveBrokerHost, _options.EffectiveBrokerPort)
            .WithClientId(_options.PluginId)
            .WithCleanSession(true)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30));

        var password = _options.EffectivePassword;
        if (!string.IsNullOrEmpty(password))
            builder.WithCredentials(_options.PluginId, password);

        var mqttOptions = builder.Build();
        await _mqtt.ConnectAsync(mqttOptions, ct);
        _logger.LogInformation("Connected to HomeCore broker at {Host}:{Port}",
            _options.EffectiveBrokerHost, _options.EffectiveBrokerPort);

        // Re-subscribe to the devices we already knew about. On a reconnect the
        // broker has forgotten our subscriptions, and OnConnected may register
        // the same devices again — idempotent, but this covers lazy
        // registration. There is deliberately no `homecore/devices/+/cmd`
        // wildcard here: it delivered every other plugin's commands to this one.
        var known = _devices.Snapshot().OrderBy(x => x, StringComparer.Ordinal).ToList();
        foreach (var deviceId in known)
            await SubscribeCommandsAsync(deviceId);

        if (_mgmt is not null)
        {
            await _mqtt.SubscribeAsync(
                new MqttTopicFilterBuilder()
                    .WithTopic($"homecore/plugins/{PluginId}/manage/cmd")
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build(),
                ct);
            await PublishCapabilitiesAsync();
        }

        if (OnConnected is not null)
            await OnConnected.Invoke();
    }

    // ── State Publishing ──────────────────────────────────────────────────

    /// <summary>Publish full device state (retained).</summary>
    public Task PublishStateAsync(string deviceId, object state, JsonObject? change = null)
    {
        var payload = WithChangeMetadata(state, change);
        return PublishAsync($"homecore/devices/{deviceId}/state", payload, retain: true);
    }

    /// <summary>Publish partial device state (JSON merge-patch, not retained).</summary>
    public Task PublishStatePartialAsync(string deviceId, object patch, JsonObject? change = null)
    {
        var payload = WithChangeMetadata(patch, change);
        return PublishAsync($"homecore/devices/{deviceId}/state/partial", payload, retain: false);
    }

    /// <summary>Publish full device state caused by an inbound command.</summary>
    public Task PublishStateForCommandAsync(
        string deviceId, object state, JsonElement commandPayload, string? fallbackSource = null)
    {
        var change = ChangeFromCommand(commandPayload, fallbackSource);
        return PublishStateAsync(deviceId, state, change);
    }

    /// <summary>Publish partial device state caused by an inbound command.</summary>
    public Task PublishStatePartialForCommandAsync(
        string deviceId, object patch, JsonElement commandPayload, string? fallbackSource = null)
    {
        var change = ChangeFromCommand(commandPayload, fallbackSource);
        return PublishStatePartialAsync(deviceId, patch, change);
    }

    // ── Availability ──────────────────────────────────────────────────────

    /// <summary>Publish device availability (retained).</summary>
    public Task PublishAvailabilityAsync(string deviceId, bool online) =>
        PublishRawAsync(
            $"homecore/devices/{deviceId}/availability",
            online ? "online" : "offline",
            retain: true);

    // ── Plugin Status ─────────────────────────────────────────────────────

    /// <summary>Publish plugin status: "active", "degraded", or "offline" (retained).</summary>
    public Task PublishPluginStatusAsync(string status) =>
        PublishRawAsync($"homecore/plugins/{PluginId}/status", status, retain: true);

    // ── Events ────────────────────────────────────────────────────────────

    /// <summary>Publish a structured event to homecore/events/{eventType}.</summary>
    public Task PublishEventAsync(string eventType, object payload) =>
        PublishAsync($"homecore/events/{eventType}", payload, retain: false);

    // ── Device Registration ───────────────────────────────────────────────

    /// <summary>Register a device with a JSON capability schema.</summary>
    public async Task RegisterDeviceAsync(string deviceId, string name, object capabilities, string? area = null)
    {
        var payload = new JsonObject
        {
            ["device_id"] = deviceId,
            ["plugin_id"] = PluginId,
            ["name"] = name,
            ["capabilities"] = JsonSerializer.SerializeToNode(capabilities),
        };
        if (area is not null) payload["area"] = area;
        await PublishAsync($"homecore/plugins/{PluginId}/register", payload, retain: false);
        await TrackDeviceAsync(deviceId);
    }

    /// <summary>Register a device by type name (HomeCore resolves capabilities from catalog).</summary>
    public async Task RegisterDeviceTypedAsync(
        string deviceId, string name, string deviceType, string? area = null)
    {
        var payload = new JsonObject
        {
            ["device_id"] = deviceId,
            ["plugin_id"] = PluginId,
            ["name"] = name,
            ["device_type"] = deviceType,
        };
        if (area is not null) payload["area"] = area;
        await PublishAsync($"homecore/plugins/{PluginId}/register", payload, retain: false);
        await TrackDeviceAsync(deviceId);
    }

    /// <summary>Register a device with all optional fields.</summary>
    public async Task RegisterDeviceFullAsync(
        string deviceId, string name,
        string? deviceType = null, string? area = null, object? capabilities = null)
    {
        var payload = new JsonObject
        {
            ["device_id"] = deviceId,
            ["plugin_id"] = PluginId,
            ["name"] = name,
        };
        if (deviceType is not null) payload["device_type"] = deviceType;
        if (area is not null) payload["area"] = area;
        if (capabilities is not null) payload["capabilities"] = JsonSerializer.SerializeToNode(capabilities);
        await PublishAsync($"homecore/plugins/{PluginId}/register", payload, retain: false);
        await TrackDeviceAsync(deviceId);
    }

    /// <summary>Publish a device capability schema (retained).</summary>
    public Task RegisterDeviceSchemaAsync(string deviceId, object schema) =>
        PublishAsync($"homecore/devices/{deviceId}/schema", schema, retain: true);

    /// <summary>
    /// Receive commands for one device.
    /// </summary>
    /// <remarks>
    /// Every <c>RegisterDevice*</c> call does this for you, so you rarely need
    /// it. Reach for it only when homeCore knows about a device this plugin did
    /// not register.
    /// </remarks>
    public Task SubscribeCommandsAsync(string deviceId)
    {
        _devices.Add(deviceId);
        if (!_mqtt.IsConnected) return Task.CompletedTask;
        return _mqtt.SubscribeAsync(
            new MqttTopicFilterBuilder()
                .WithTopic($"homecore/devices/{deviceId}/cmd")
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build());
    }

    /// <summary>Stop receiving commands for one device.</summary>
    public Task UnsubscribeCommandsAsync(string deviceId)
    {
        _devices.Remove(deviceId);
        return _mqtt.IsConnected
            ? _mqtt.UnsubscribeAsync($"homecore/devices/{deviceId}/cmd")
            : Task.CompletedTask;
    }

    /// <summary>
    /// Remember across restarts which devices this plugin registered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call once at startup, before registering anything. The plugin id is
    /// inserted into the filename, so plugins sharing a config directory cannot
    /// share a snapshot and retire each other's devices.
    /// </para>
    /// <para>
    /// Without this, <see cref="ReconcileDevicesAsync"/> can only see devices
    /// registered in the <i>current</i> process, so anything dropped while the
    /// plugin was down lingers in homeCore forever.
    /// </para>
    /// </remarks>
    /// <param name="path">
    /// Typically <c>&lt;configDir&gt;/.published-device-ids.json</c>.
    /// </param>
    public void EnableDevicePersistence(string path) =>
        _devices.EnablePersistence(DeviceTracker.ScopedSnapshotPath(path, PluginId));

    /// <summary>
    /// Unregister every device this plugin knows about that is not in
    /// <paramref name="live"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The "set what is live this cycle, let the SDK clean up the rest"
    /// workflow. Combined with <see cref="EnableDevicePersistence"/> it also
    /// retires devices registered in earlier runs.
    /// </para>
    /// <para>
    /// <b>Only call this after a sync you trust.</b> On a partial fetch it will
    /// unregister live devices behind a temporarily unreachable upstream —
    /// which looks exactly like the bug it exists to prevent, but worse,
    /// because the devices were fine. Track an "everything succeeded" flag
    /// across your per-source loop and pass the live set only when it holds.
    /// </para>
    /// <para>
    /// Ids in <paramref name="live"/> that were never registered are reported
    /// in <see cref="ReconcileReport.UnknownInLive"/> and otherwise ignored —
    /// register them first if you meant to keep them.
    /// </para>
    /// </remarks>
    public async Task<ReconcileReport> ReconcileDevicesAsync(IEnumerable<string> live)
    {
        var liveSet = live as HashSet<string> ?? new HashSet<string>(live);
        var known = _devices.Snapshot();

        var stale = known.Except(liveSet).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var unknown = liveSet.Except(known).OrderBy(x => x, StringComparer.Ordinal).ToList();

        var unregistered = new List<string>(stale.Count);
        foreach (var deviceId in stale)
        {
            try
            {
                await UnregisterDeviceAsync(deviceId);
                unregistered.Add(deviceId);
                _logger.LogInformation("Unregistered stale device {DeviceId}", deviceId);
            }
            catch (Exception ex)
            {
                // One failure must not stop the rest.
                _logger.LogWarning(
                    "Failed to unregister stale device {DeviceId}: {Error}", deviceId, ex.Message);
            }
        }

        if (unknown.Count > 0)
        {
            _logger.LogDebug(
                "ReconcileDevices saw {Count} live ids not registered with the SDK; "
                + "register them first if they should be kept", unknown.Count);
        }

        return new ReconcileReport
        {
            StaleUnregistered = unregistered,
            UnknownInLive = unknown,
        };
    }

    /// <summary>
    /// Receive <i>state</i> updates for a device this plugin does not own.
    /// </summary>
    /// <remarks>
    /// For cross-device consumers — a thermostat reading sensors that belong to
    /// other plugins. Updates arrive on <see cref="OnState"/>. The broker ACL
    /// has to allow it: such a plugin needs
    /// <c>allow_sub = ["homecore/devices/+/state"]</c>, broader than a typical
    /// plugin's.
    /// </remarks>
    public Task SubscribeStateAsync(string deviceId) =>
        _mqtt.SubscribeAsync(
            new MqttTopicFilterBuilder()
                .WithTopic($"homecore/devices/{deviceId}/state")
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build());

    /// <summary>Stop receiving state for a device.</summary>
    public Task UnsubscribeStateAsync(string deviceId) =>
        _mqtt.UnsubscribeAsync($"homecore/devices/{deviceId}/state");

    /// <summary>
    /// Record a device as ours and subscribe to its command topic.
    /// </summary>
    /// <remarks>
    /// Registration and subscription are one step here on purpose. In the Rust
    /// SDK they are separate calls, and forgetting the second is the classic
    /// first-plugin bug: the device appears in homeCore, its state updates, and
    /// every command silently goes nowhere.
    /// </remarks>
    private Task TrackDeviceAsync(string deviceId) => SubscribeCommandsAsync(deviceId);

    /// <summary>Unregister a device: clear retained topics and publish unregister message.</summary>
    public async Task UnregisterDeviceAsync(string deviceId)
    {
        await ClearRetainedAsync($"homecore/devices/{deviceId}/state");
        await ClearRetainedAsync($"homecore/devices/{deviceId}/availability");
        await ClearRetainedAsync($"homecore/devices/{deviceId}/schema");
        await PublishAsync(
            $"homecore/plugins/{PluginId}/unregister",
            new { device_id = deviceId },
            retain: false);
        await UnsubscribeCommandsAsync(deviceId);
    }

    // ── Management Protocol ───────────────────────────────────────────────

    /// <summary>
    /// Enable the management protocol: heartbeat publisher + command listener
    /// for get_config, set_config, set_log_level, and ping.
    /// Call after ConnectAsync, before RunAsync.
    /// </summary>
    public async Task EnableManagementAsync(ManagementOptions options, CancellationToken ct = default)
    {
        _mgmt = options;
        if (options.Capabilities is not null) options.Capabilities.PluginId = PluginId;

        // Subscribe to management command topic.
        await _mqtt.SubscribeAsync(
            new MqttTopicFilterBuilder()
                .WithTopic($"homecore/plugins/{PluginId}/manage/cmd")
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build(),
            ct);

        await PublishCapabilitiesAsync();

        // Start heartbeat publisher, and beat once now rather than making the
        // operator wait a full interval to see the plugin at all.
        _heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => HeartbeatLoopAsync(options, _heartbeatCts.Token), _heartbeatCts.Token);
        await PublishHeartbeatAsync();

        _logger.LogInformation(
            "Management protocol enabled (heartbeat every {Interval}s)",
            options.HeartbeatIntervalSecs);
    }

    // ── Event Loop ────────────────────────────────────────────────────────

    /// <summary>
    /// Block until cancellation. The MQTT client handles messages via events.
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Plugin {PluginId} event loop running", PluginId);
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Plugin {PluginId} shutting down", PluginId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _heartbeatCts?.Cancel();
        if (_mqtt.IsConnected)
            await _mqtt.DisconnectAsync();
        _mqtt.Dispose();
    }

    // ── Internal: Message Routing ─────────────────────────────────────────

    private async Task HandleMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var parts = topic.Split('/');

        // homecore/devices/{id}/cmd
        if (parts.Length == 4
            && parts[0] == "homecore"
            && parts[1] == "devices"
            && parts[3] == "cmd")
        {
            var deviceId = parts[2];
            // Belt and braces alongside the per-device subscription: a broker
            // that hands us a topic we did not ask for must not turn into this
            // plugin acting on another plugin's device.
            if (!_devices.Contains(deviceId)) return;

            JsonElement payload;
            try
            {
                var bytes = e.ApplicationMessage.PayloadSegment;
                payload = JsonDocument.Parse(bytes).RootElement;
            }
            catch
            {
                var raw = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
                payload = JsonDocument.Parse($"{{\"raw\":\"{Escape(raw)}\"}}").RootElement;
            }

            if (OnCommand is not null)
            {
                try { await OnCommand.Invoke(deviceId, payload); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Command handler failed for {DeviceId}", deviceId);
                }
            }
            return;
        }

        // homecore/devices/{id}/state — a device owned by someone else, for
        // cross-device consumers.
        if (parts.Length == 4
            && parts[0] == "homecore"
            && parts[1] == "devices"
            && parts[3] == "state"
            && OnState is not null)
        {
            try
            {
                var state = JsonDocument.Parse(e.ApplicationMessage.PayloadSegment).RootElement;
                await OnState.Invoke(parts[2], state);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "State handler failed for {DeviceId}", parts[2]);
            }
            return;
        }

        // homecore/plugins/{id}/manage/cmd
        if (_mgmt is not null
            && parts.Length == 5
            && parts[0] == "homecore"
            && parts[1] == "plugins"
            && parts[3] == "manage"
            && parts[4] == "cmd")
        {
            try
            {
                var bytes = e.ApplicationMessage.PayloadSegment;
                var cmd = JsonDocument.Parse(bytes).RootElement;
                var response = await HandleManagementCommandAsync(cmd);
                await PublishAsync(
                    $"homecore/plugins/{PluginId}/manage/response",
                    response,
                    retain: false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Management command handling failed");
            }
        }
    }

    // ── Internal: Management ──────────────────────────────────────────────

    private async Task<JsonObject> HandleManagementCommandAsync(JsonElement cmd)
    {
        var action = cmd.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
        var requestId = cmd.TryGetProperty("request_id", out var r) ? r.GetString() ?? "" : "";

        switch (action)
        {
            case "ping":
                return new JsonObject
                {
                    ["request_id"] = requestId,
                    ["status"] = "ok",
                };

            case "get_config":
                if (_mgmt?.ConfigPath is { } path)
                {
                    try
                    {
                        var content = await File.ReadAllTextAsync(path);
                        return new JsonObject
                        {
                            ["request_id"] = requestId,
                            ["status"] = "ok",
                            ["data"] = content,
                        };
                    }
                    catch (Exception ex)
                    {
                        return ErrorResponse(requestId, $"failed to read config: {ex.Message}");
                    }
                }
                return ErrorResponse(requestId, "no config path configured");

            case "set_config":
                return await HandleSetConfigAsync(cmd, requestId);

            case "set_log_level":
            {
                var level = cmd.TryGetProperty("level", out var lv) ? lv.GetString() ?? "info" : "info";
                // Applies to forwarding immediately. It cannot reach into the
                // host's own ILoggerFactory, so a plugin that also logs to
                // stdout keeps whatever level it was configured with there.
                _logForwardMinLevel = LogForwarding.ParseLevel(level);
                _logger.LogInformation("Management: forward log level set to {Level}", level);
                return new JsonObject
                {
                    ["request_id"] = requestId,
                    ["status"] = "ok",
                    ["note"] = "forwarding level changed; the plugin's own sinks are unaffected",
                };
            }

            case "cancel":
            {
                var target = cmd.TryGetProperty("target_request_id", out var t)
                    ? t.GetString() ?? "" : "";
                StreamContext? ctx;
                lock (_streamsLock) _activeStreams.TryGetValue(target, out ctx);
                if (ctx is null)
                    return ErrorResponse(requestId, "no active stream for target_request_id");
                ctx.Cancel();
                return Ok(requestId);
            }

            case "respond":
            {
                var target = cmd.TryGetProperty("target_request_id", out var t)
                    ? t.GetString() ?? "" : "";
                StreamContext? ctx;
                lock (_streamsLock) _activeStreams.TryGetValue(target, out ctx);
                if (ctx is null)
                    return ErrorResponse(
                        requestId, "no active awaiting_user stream for target_request_id");
                var response = cmd.TryGetProperty("response", out var rv)
                    ? JsonNode.Parse(rv.GetRawText()) as JsonObject ?? new JsonObject()
                    : new JsonObject();
                ctx.DeliverResponse(response);
                return Ok(requestId);
            }

            default:
                // A legacy escape hatch, kept so existing plugins keep working.
                if (OnManagementCommand is not null)
                {
                    var result = await OnManagementCommand.Invoke(action, cmd);
                    if (result is not null) return result;
                }
                return await DispatchActionAsync(action, requestId, cmd);
        }
    }

    /// <summary>
    /// Write a <c>set_config</c> payload.
    /// </summary>
    /// <remarks>
    /// Core sends a string when the operator edited raw TOML, and an object when
    /// the plugin declared a config schema and the UI rendered a form. It also
    /// forwards the request body verbatim when that body has no top-level
    /// <c>config</c> key, so the raw editor arrives as
    /// <c>{"raw": "&lt;text&gt;"}</c>. Strings are written as-is; anything else
    /// is <see cref="OnSetConfig"/>'s to handle, because turning an object into
    /// TOML is not something this SDK can do — writing its JSON into a .toml
    /// file, which is what it used to do, is not the same thing.
    /// </remarks>
    private async Task<JsonObject> HandleSetConfigAsync(JsonElement cmd, string requestId)
    {
        if (_mgmt?.ConfigPath is not { } path)
            return ErrorResponse(requestId, "no config path configured");

        if (!cmd.TryGetProperty("config", out var cfg))
            return ErrorResponse(requestId, "missing 'config' field");

        // Unwrap the raw-editor shape core forwards.
        if (cfg.ValueKind == JsonValueKind.Object
            && cfg.TryGetProperty("raw", out var raw)
            && raw.ValueKind == JsonValueKind.String)
        {
            cfg = raw;
        }

        if (cfg.ValueKind != JsonValueKind.String)
        {
            if (OnSetConfig is not null && await OnSetConfig.Invoke(cfg))
                return Ok(requestId);
            return ErrorResponse(
                requestId,
                "structured config received; set OnSetConfig to accept it, "
                + "or edit the raw form instead");
        }

        try
        {
            await File.WriteAllTextAsync(path, cfg.GetString()!);
            return Ok(requestId);
        }
        catch (Exception ex)
        {
            return ErrorResponse(requestId, $"failed to write config: {ex.Message}");
        }
    }

    /// <summary>Route a management command that is not a built-in to <see cref="OnAction"/>.</summary>
    private async Task<JsonObject> DispatchActionAsync(
        string action, string requestId, JsonElement cmd)
    {
        var declared = _mgmt?.Capabilities?.Actions.FirstOrDefault(a => a.Id == action);

        // Params are everything that is not protocol envelope.
        var paramsObj = new JsonObject();
        foreach (var prop in cmd.EnumerateObject())
        {
            if (prop.Name is "action" or "request_id" or "target_request_id") continue;
            paramsObj[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
        }
        var paramsElement = JsonDocument.Parse(paramsObj.ToJsonString()).RootElement;

        if (declared is { Stream: true })
            return StartStream(declared, requestId, paramsElement);

        if (OnAction is null)
            return ErrorResponse(requestId, $"unknown action: {action}");

        try
        {
            var result = await OnAction.Invoke(action, paramsElement, null);
            if (result is null)
                return ErrorResponse(requestId, $"unknown action: {action}");
            result["request_id"] = requestId;
            result["status"] ??= "ok";
            return result;
        }
        catch (Exception ex)
        {
            // A plugin bug must not take down the message loop.
            _logger.LogWarning(ex, "Action {Action} threw", action);
            return ErrorResponse(requestId, $"action failed: {ex.Message}");
        }
    }

    /// <summary>Run a streaming action and answer <c>accepted</c> straight away.</summary>
    private JsonObject StartStream(PluginAction declared, string requestId, JsonElement @params)
    {
        if (string.IsNullOrEmpty(requestId))
            return ErrorResponse("", "streaming action requires request_id");

        if (declared.Concurrency == Concurrency.Single)
        {
            string? busy = null;
            lock (_streamsLock)
                busy = _activeStreams.FirstOrDefault(kv => kv.Value.ActionId == declared.Id).Key;
            if (busy is not null)
                return new JsonObject
                {
                    ["request_id"] = requestId,
                    ["status"] = "busy",
                    ["active_request_id"] = busy,
                };
        }

        var ctx = new StreamContext(this, requestId, declared.Id);
        lock (_streamsLock) _activeStreams[requestId] = ctx;

        // Runs on its own task, so a slow action never stalls the message loop.
        // Whatever happens, exactly one terminal stage must land and the
        // retained topic must be cleared.
        _ = Task.Run(async () =>
        {
            Exception? failure = null;
            try
            {
                if (OnAction is not null)
                    await OnAction.Invoke(declared.Id, @params, ctx);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Streaming action {Action} threw", declared.Id);
                failure = ex;
            }
            finally
            {
                await ctx.FinalizeAsync(failure);
                lock (_streamsLock) _activeStreams.Remove(requestId);
            }
        });

        return new JsonObject
        {
            ["request_id"] = requestId,
            ["status"] = "accepted",
            ["stream_topic"] = ctx.Topic,
        };
    }

    private static JsonObject Ok(string requestId) =>
        new() { ["request_id"] = requestId, ["status"] = "ok" };

    private async Task HeartbeatLoopAsync(ManagementOptions options, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.HeartbeatIntervalSecs));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await PublishHeartbeatAsync();
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>Publish one heartbeat now.</summary>
    internal async Task PublishHeartbeatAsync()
    {
        if (_mgmt is null) return;
        var deviceCount = _devices.Count;

        var payload = new JsonObject
        {
            ["timestamp"] = DateTime.UtcNow.ToString("o"),
            ["version"] = _mgmt.Version,
            ["sdk_version"] = SdkVersion,
            ["protocol_version"] = ProtocolVersion,
            ["uptime_secs"] = (long)(DateTime.UtcNow - _startedAt).TotalSeconds,
            ["device_count"] = deviceCount,
            // Full current set every beat. Core replaces rather than merges, so
            // a cleared condition disappears on its own and nothing expires.
            ["notices"] = Notices.ToWire(),
        };
        await PublishAsync($"homecore/plugins/{PluginId}/heartbeat", payload, retain: false);
    }

    /// <summary>
    /// Publish the action manifest, retained.
    /// </summary>
    /// <remarks>
    /// Retained because homeCore may start, or restart, after this plugin —
    /// otherwise a late-joining core never learns the plugin has actions.
    /// </remarks>
    private async Task PublishCapabilitiesAsync()
    {
        if (_mgmt?.Capabilities is not { } caps) return;
        caps.PluginId = PluginId;
        await PublishRawInternalAsync(
            $"homecore/plugins/{PluginId}/capabilities",
            caps.ToJson().ToJsonString(),
            retain: true);
    }

    // ── Log forwarding ────────────────────────────────────────────────────

    /// <summary>
    /// Publish one log line to homeCore's live log stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Usually you do not call this: register
    /// <see cref="MqttLoggerProvider"/> on your logging builder
    /// (<c>builder.AddHomeCore(client)</c>) and everything the plugin logs is
    /// forwarded. This is the direct route for code that has no
    /// <see cref="ILogger"/> to hand.
    /// </para>
    /// <para>
    /// QoS 0 and not retained: logs are a stream, and a reconnecting plugin
    /// should not replay its last line as though it were new. Publishing is
    /// best-effort — a logging call must never throw or block, so a broker
    /// error is swallowed rather than surfaced.
    /// </para>
    /// <para>
    /// Field names that look secret are redacted; the message string is
    /// published as-is. Pass secrets as fields, not interpolated into the text.
    /// </para>
    /// </remarks>
    public async Task ForwardLogAsync(
        string level,
        string message,
        string? target = null,
        JsonObject? fields = null)
    {
        if (!_logForwardEnabled) return;
        if (LogForwarding.ParseLevel(level) < _logForwardMinLevel) return;
        if (!_mqtt.IsConnected) return;

        var line = new JsonObject
        {
            ["timestamp"] = DateTime.UtcNow.ToString("o"),
            ["level"] = level,
            ["target"] = target ?? PluginId,
            ["message"] = message,
        };
        // `fields` is skipped when null on the core side, so omit rather than
        // send an empty object.
        if (fields is not null) line["fields"] = fields;

        try
        {
            var msg = new MqttApplicationMessageBuilder()
                .WithTopic($"homecore/plugins/{PluginId}/logs")
                .WithPayload(Encoding.UTF8.GetBytes(line.ToJsonString()))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                .WithRetainFlag(false)
                .Build();
            await _mqtt.PublishAsync(msg);
        }
        catch
        {
            // Never let logging take down the caller.
        }
    }

    /// <summary>
    /// Turn on log forwarding and set the minimum level.
    /// </summary>
    /// <remarks>
    /// Off until called, so a plugin does not start shipping logs to the broker
    /// merely by linking this SDK. The level is also what
    /// <c>set_log_level</c> adjusts at runtime, so an operator can turn a
    /// misbehaving plugin up to debug from the UI without restarting it.
    /// </remarks>
    public void EnableLogForwarding(LogLevel minLevel = LogLevel.Information)
    {
        _logForwardEnabled = true;
        _logForwardMinLevel = minLevel;
    }

    /// <summary>The level below which lines are not forwarded.</summary>
    public LogLevel LogForwardMinLevel => _logForwardMinLevel;

    /// <summary>Publish a raw payload. Used by <see cref="StreamContext"/>.</summary>
    internal Task PublishRawInternalAsync(string topic, string payload, bool retain) =>
        PublishRawAsync(topic, payload, retain);

    // ── Internal: Publish Helpers ─────────────────────────────────────────

    private Task PublishAsync(string topic, object payload, bool retain)
    {
        var json = JsonSerializer.Serialize(payload);
        return PublishRawAsync(topic, json, retain);
    }

    private async Task PublishRawAsync(string topic, string payload, bool retain)
    {
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(Encoding.UTF8.GetBytes(payload))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(retain)
            .Build();
        await _mqtt.PublishAsync(msg);
    }

    private Task ClearRetainedAsync(string topic) =>
        PublishRawAsync(topic, "", retain: true);

    // ── Internal: Change Metadata ─────────────────────────────────────────

    private object WithChangeMetadata(object payload, JsonObject? change)
    {
        if (change is null) return payload;

        // Serialize payload to JsonNode, inject _hc.change, return.
        var node = JsonSerializer.SerializeToNode(payload);
        if (node is JsonObject obj)
        {
            var hc = new JsonObject { ["change"] = JsonNode.Parse(change.ToJsonString()) };
            obj["_hc"] = hc;
            return obj;
        }
        return payload;
    }

    /// <summary>Extract _hc.command metadata from an inbound command payload.</summary>
    public static JsonObject? ExtractCommandChange(JsonElement commandPayload)
    {
        if (commandPayload.TryGetProperty("_hc", out var hc)
            && hc.TryGetProperty("command", out var cmd)
            && cmd.ValueKind == JsonValueKind.Object)
        {
            return JsonNode.Parse(cmd.GetRawText()) as JsonObject;
        }
        return null;
    }

    /// <summary>Build a change metadata object from a command payload.</summary>
    public JsonObject ChangeFromCommand(JsonElement commandPayload, string? fallbackSource = null)
    {
        var extracted = ExtractCommandChange(commandPayload);
        if (extracted is not null) return extracted;

        return new JsonObject
        {
            ["changed_at"] = DateTime.UtcNow.ToString("o"),
            ["kind"] = "homecore",
            ["source"] = fallbackSource ?? PluginId,
        };
    }

    private static JsonObject ErrorResponse(string requestId, string error) =>
        new()
        {
            ["request_id"] = requestId,
            ["status"] = "error",
            ["error"] = error,
        };

    private static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
