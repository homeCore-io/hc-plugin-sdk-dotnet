using System.Globalization;
using System.Text.Json.Nodes;

namespace HomeCore.PluginSdk;

/// <summary>How serious an alert is.</summary>
/// <remarks>
/// <see cref="Critical"/> exists because it is on the wire, but it is homeCore's
/// own (smoke, water): a plugin asking for it gets <see cref="Error"/>.
/// </remarks>
public enum AlertSeverity
{
    Info,
    Warning,
    Error,
    Critical,
}

/// <summary>Where an <see cref="AlertAction"/> runs.</summary>
public enum AlertActionTarget
{
    /// <summary>On the alert's device, as one of its device actions. The default.</summary>
    Device,

    /// <summary>As one of the plugin's own manifest actions.</summary>
    Plugin,
}

/// <summary>
/// The button on a plugin's alert: one of the device's own actions, or one of
/// the plugin's, run with these params when someone (or an automation)
/// presses it.
/// </summary>
public sealed record AlertAction
{
    /// <summary>What the button says: "Update…".</summary>
    public required string Label { get; init; }

    /// <summary>
    /// The action to run: a device action id (<c>install_firmware</c>) when the
    /// alert is about a device, else one of the plugin's manifest actions.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// <see cref="AlertActionTarget.Device"/> (the default) runs it on the
    /// alert's device; <see cref="AlertActionTarget.Plugin"/> runs it as a
    /// plugin action.
    /// </summary>
    public AlertActionTarget Target { get; init; } = AlertActionTarget.Device;

    /// <summary>Passed to the action.</summary>
    public JsonNode? Params { get; init; }

    /// <summary>Ask before running it: it interrupts something.</summary>
    public bool Confirm { get; init; }

    /// <summary>
    /// The wire form. <c>target</c> is omitted when it is <c>device</c>,
    /// <c>params</c> when null and <c>confirm</c> when false — matching the
    /// Rust SDK's <c>skip_serializing_if</c>.
    /// </summary>
    public JsonObject ToJson()
    {
        var o = new JsonObject { ["label"] = Label, ["id"] = Id };
        if (Target == AlertActionTarget.Plugin) o["target"] = "plugin";
        if (Params is not null) o["params"] = Params.DeepClone();
        if (Confirm) o["confirm"] = true;
        return o;
    }
}

/// <summary>
/// A kind of alert a plugin can raise, declared on its capability manifest
/// (<see cref="Capabilities.AlertKinds"/>) — which is how the settings screen
/// can list it, and let someone turn it on or off, before it has ever happened.
/// </summary>
public sealed record PluginAlertKind
{
    /// <summary><c>snake_case</c>, stable: what <see cref="PluginAlert.Topic"/> names.</summary>
    public required string Topic { get; init; }

    /// <summary>For a person: "Firmware update available".</summary>
    public required string Label { get; init; }

    public string? Description { get; init; }

    /// <summary>
    /// How serious one is. Never <see cref="AlertSeverity.Critical"/>: that is
    /// homeCore's own; a plugin asking for it gets <c>error</c>.
    /// </summary>
    public AlertSeverity Severity { get; init; } = AlertSeverity.Warning;

    /// <summary>
    /// Shown unless someone turns it off (true), or only once someone turns it
    /// on (false) — right for news that isn't a problem.
    /// </summary>
    public bool DefaultOn { get; init; } = true;

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["topic"] = Topic, ["label"] = Label };
        if (Description is not null) o["description"] = Description;
        o["severity"] = AlertWire.Severity(Severity);
        o["default_on"] = DefaultOn;
        return o;
    }
}

/// <summary>
/// One alert a plugin says is true now, for one device (or for itself).
/// </summary>
/// <remarks>
/// <b>Current state, not an event</b>: the plugin publishes the whole list for
/// a device each time (<see cref="PluginClient.SetAlertsAsync"/>), and homeCore
/// replaces what it held — an alert it stops sending has cleared.
/// </remarks>
public sealed record PluginAlert
{
    /// <summary>One of the plugin's declared kinds.</summary>
    public required string Topic { get; init; }

    /// <summary>
    /// What this one is about within its kind (<c>17.0.0</c>); a different key
    /// is a different alert, so "skip this one" skips this version, not the kind.
    /// </summary>
    public string? Key { get; init; }

    /// <summary>Without the device's name, for its card: "Firmware 17.0.0 is out".</summary>
    public required string Reason { get; init; }

    /// <summary>
    /// With the device's name, when the plugin wants other words than
    /// "{name}: {reason}".
    /// </summary>
    public string? Message { get; init; }

    /// <summary>Overrides the kind's declared severity for this one; capped at <c>error</c>.</summary>
    public AlertSeverity? Severity { get; init; }

    /// <summary>The button on it.</summary>
    public AlertAction? Action { get; init; }

    /// <summary>
    /// When the condition began, if the plugin knows; else homeCore uses when
    /// it first heard of it.
    /// </summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>The wire form. Unset optionals are omitted, not sent as null.</summary>
    public JsonObject ToJson()
    {
        var o = new JsonObject { ["topic"] = Topic };
        if (Key is not null) o["key"] = Key;
        o["reason"] = Reason;
        if (Message is not null) o["message"] = Message;
        if (Severity is { } s) o["severity"] = AlertWire.Severity(s);
        if (Action is not null) o["action"] = Action.ToJson();
        if (Since is { } since)
            o["since"] = since.UtcDateTime.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);
        return o;
    }
}

internal static class AlertWire
{
    internal static string Severity(AlertSeverity s) => s switch
    {
        AlertSeverity.Info => "info",
        AlertSeverity.Error => "error",
        AlertSeverity.Critical => "critical",
        _ => "warning",
    };

    internal static string Serialize(IEnumerable<PluginAlert> alerts)
    {
        var arr = new JsonArray();
        foreach (var a in alerts) arr.Add(a.ToJson());
        return arr.ToJsonString();
    }
}

/// <summary>
/// The alerts last set, by topic, so a reconnect can say them again: core's
/// broker forgets retained messages when it restarts, and an alert the plugin
/// set an hour ago must not vanish because core did.
/// </summary>
/// <remarks>
/// An empty list (a clear) is held only until it has been published: MQTTnet's
/// client does not queue while disconnected, so a clear set offline has to
/// wait for the connect — or the broker keeps the stale list it retained.
/// </remarks>
internal sealed class AlertTracker
{
    private readonly object _lock = new();
    private readonly Dictionary<string, string> _held = new();

    internal const string Empty = "[]";

    /// <summary>Remember the latest payload for <paramref name="topic"/>.</summary>
    internal void Hold(string topic, string payload)
    {
        lock (_lock) _held[topic] = payload;
    }

    /// <summary>
    /// A payload reached the broker. A clear is forgotten once delivered — but
    /// only if nothing newer replaced it meanwhile.
    /// </summary>
    internal void Delivered(string topic, string payload)
    {
        if (payload != Empty) return;
        lock (_lock)
        {
            if (_held.TryGetValue(topic, out var current) && current == Empty)
                _held.Remove(topic);
        }
    }

    internal IReadOnlyList<KeyValuePair<string, string>> Snapshot()
    {
        lock (_lock) return _held.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
    }
}
