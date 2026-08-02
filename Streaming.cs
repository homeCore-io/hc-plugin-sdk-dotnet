using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace HomeCore.PluginSdk;

/// <summary>Thrown when emitting after a terminal stage has already been sent.</summary>
public sealed class StreamTerminatedException : InvalidOperationException
{
    public StreamTerminatedException(string message) : base(message) { }
}

/// <summary>
/// Handle passed to a streaming action handler. One per invocation.
/// </summary>
/// <remarks>
/// <para>
/// An immediate action returns an object and is done. A streaming action
/// publishes events while it works, which is what lets hc-web show a live
/// progress bar, devices appearing one by one, and a prompt like "press the
/// button on the device now".
/// </para>
/// <para>
/// Events go to <c>homecore/plugins/{pluginId}/commands/{requestId}/events</c>.
/// Six stages: <c>progress</c>, <c>item</c>, <c>warning</c> (non-terminal),
/// <c>awaiting_user</c>, <c>complete</c> and <c>error</c> (terminal). Plus
/// <c>canceled</c>, which you emit yourself after noticing
/// <see cref="IsCanceled"/> — only your code knows what needs rolling back
/// first.
/// </para>
/// <para>
/// <b>Terminal stages are latched.</b> The first wins; a second throws. If your
/// handler returns or throws without emitting one, the SDK synthesises an
/// <c>error</c>, so the UI is never left waiting on a stream that quietly
/// stopped.
/// </para>
/// </remarks>
public sealed class StreamContext
{
    private readonly PluginClient _plugin;
    private readonly Channel<JsonObject> _responses =
        Channel.CreateUnbounded<JsonObject>();
    private int _terminal;
    private volatile bool _canceled;

    internal StreamContext(PluginClient plugin, string requestId, string actionId)
    {
        _plugin = plugin;
        RequestId = requestId;
        ActionId = actionId;
        Topic = $"homecore/plugins/{plugin.PluginId}/commands/{requestId}/events";
    }

    public string RequestId { get; }
    public string ActionId { get; }
    public string Topic { get; }

    // ── non-terminal stages ──────────────────────────────────────────────

    /// <summary>Report progress. Every field is optional — send whichever you have.</summary>
    public Task ProgressAsync(int? percent = null, string? label = null, string? message = null)
    {
        var ev = new JsonObject { ["stage"] = "progress" };
        if (percent is not null) ev["percent"] = percent;
        if (label is not null) ev["label"] = label;
        if (message is not null) ev["message"] = message;
        return EmitAsync(ev, terminal: false);
    }

    /// <summary>
    /// One thing was found. Include the manifest's <c>ItemKey</c> field so the
    /// UI can tell rows apart.
    /// </summary>
    public Task ItemAddAsync(JsonObject data) =>
        EmitAsync(new JsonObject { ["stage"] = "item", ["op"] = "add", ["data"] = data }, false);

    /// <summary>
    /// Something already reported has changed — same <c>ItemKey</c>, so the UI
    /// updates that row instead of appending another.
    /// </summary>
    public Task ItemUpdateAsync(JsonObject data) =>
        EmitAsync(new JsonObject { ["stage"] = "item", ["op"] = "update", ["data"] = data }, false);

    public Task ItemRemoveAsync(JsonObject data) =>
        EmitAsync(new JsonObject { ["stage"] = "item", ["op"] = "remove", ["data"] = data }, false);

    /// <summary>
    /// A recoverable problem. The stream continues.
    /// </summary>
    /// <remarks>
    /// Use this for a retry or a host that did not answer. If the action cannot
    /// continue, that is <see cref="ErrorAsync"/>, which is terminal.
    /// </remarks>
    public Task WarningAsync(string message, JsonObject? data = null)
    {
        var ev = new JsonObject { ["stage"] = "warning", ["message"] = message };
        if (data is not null) ev["data"] = data;
        return EmitAsync(ev, terminal: false);
    }

    /// <summary>
    /// Ask the operator for something and keep the stream open. Emit this, then
    /// await <see cref="AwaitRespondAsync"/>.
    /// </summary>
    public Task AwaitingUserAsync(string prompt, JsonNode? responseSchema = null)
    {
        var ev = new JsonObject { ["stage"] = "awaiting_user", ["prompt"] = prompt };
        if (responseSchema is not null) ev["response_schema"] = responseSchema.DeepClone();
        return EmitAsync(ev, terminal: false);
    }

    // ── terminal stages ──────────────────────────────────────────────────

    /// <summary>Terminal, success. <paramref name="data"/> should match the manifest's Result.</summary>
    public Task CompleteAsync(JsonObject? data = null) =>
        EmitAsync(new JsonObject { ["stage"] = "complete", ["data"] = data ?? new JsonObject() }, true);

    /// <summary>Terminal, failure. For something recoverable use <see cref="WarningAsync"/>.</summary>
    public Task ErrorAsync(string message) =>
        EmitAsync(new JsonObject { ["stage"] = "error", ["message"] = message }, true);

    /// <summary>
    /// Terminal, acknowledging a cancel. Call it yourself once
    /// <see cref="IsCanceled"/> is true and you have unwound whatever needed
    /// unwinding — the SDK cannot know when your rollback is finished.
    /// </summary>
    public Task CanceledAsync() =>
        EmitAsync(new JsonObject { ["stage"] = "canceled" }, true);

    // ── cancel / respond ─────────────────────────────────────────────────

    /// <summary>
    /// Whether a cancel has arrived. Cooperative — check it in your loop;
    /// nothing interrupts your handler.
    /// </summary>
    public bool IsCanceled() => _canceled;

    /// <summary>
    /// Wait for the operator's answer to an <see cref="AwaitingUserAsync"/> prompt.
    /// </summary>
    public async Task<JsonObject> AwaitRespondAsync(CancellationToken ct = default) =>
        await _responses.Reader.ReadAsync(ct);

    // ── internals, driven by PluginClient ────────────────────────────────

    internal void Cancel() => _canceled = true;

    internal void DeliverResponse(JsonObject response) => _responses.Writer.TryWrite(response);

    private async Task EmitAsync(JsonObject ev, bool terminal)
    {
        if (Volatile.Read(ref _terminal) == 1)
            throw new StreamTerminatedException(
                $"stream {RequestId} already terminated; cannot emit {ev["stage"]}");

        if (terminal) Volatile.Write(ref _terminal, 1);

        ev["request_id"] = RequestId;
        ev["ts"] = DateTime.UtcNow.ToString("o");
        // Retained, so a UI that subscribes mid-action sees the latest frame
        // rather than an empty screen until the next one.
        await _plugin.PublishRawInternalAsync(Topic, ev.ToJsonString(), retain: true);
    }

    /// <summary>
    /// Guarantee exactly one terminal stage, then clear the retained topic.
    /// </summary>
    /// <remarks>
    /// A handler that returns without terminating, or throws, would otherwise
    /// leave the UI waiting forever on a stream that has already stopped.
    /// </remarks>
    internal async Task FinalizeAsync(Exception? error)
    {
        if (Interlocked.CompareExchange(ref _terminal, 1, 0) == 0)
        {
            var ev = new JsonObject
            {
                ["stage"] = "error",
                ["request_id"] = RequestId,
                ["ts"] = DateTime.UtcNow.ToString("o"),
                ["message"] = error is null
                    ? "plugin dropped stream without emitting a terminal stage"
                    : $"plugin action failed: {error.Message}",
                ["data"] = new JsonObject { ["reason"] = "plugin_dropped_stream" },
            };
            await _plugin.PublishRawInternalAsync(Topic, ev.ToJsonString(), retain: true);
        }

        // An empty retained payload deletes the retained frame, so a subscriber
        // arriving later does not replay a stale terminal as if it were live.
        await _plugin.PublishRawInternalAsync(Topic, "", retain: true);
    }
}
