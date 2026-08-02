using System.Text.Json;
using System.Text.Json.Nodes;

namespace HomeCore.PluginSdk;

/// <summary>Whether a second invocation may start while the first is running.</summary>
public enum Concurrency
{
    /// <summary>May run concurrently with itself.</summary>
    Multi,

    /// <summary>A second invocation is rejected with <c>busy</c> and the active request id.</summary>
    Single,
}

/// <summary>
/// The least-privileged role allowed to invoke an action.
/// </summary>
/// <remarks>
/// homeCore enforces this; it is not a UI hint. Use <see cref="Admin"/> for
/// anything destructive — unregistering devices, clearing pairings, resets.
/// </remarks>
public enum RequiresRole
{
    Admin,
    User,
    ReadOnly,
}

/// <summary>Item operations a streaming action may emit, if it emits items at all.</summary>
public enum ItemOp
{
    Add,
    Update,
    Remove,
}

/// <summary>
/// One declared action — a plugin-specific command the UI renders as a button.
/// </summary>
/// <remarks>
/// <para>
/// A device command tells one device to do something. A capability action is
/// aimed at the plugin itself: "Pair the bridge", "Rescan the network", "Forget
/// devices that no longer answer". Declaring one is all it takes for it to
/// appear on the plugin's page in hc-web and to become callable from hc-mcp —
/// neither needs code written for your plugin specifically.
/// </para>
/// <para>
/// Immediate actions (<see cref="Stream"/> false) return a
/// <see cref="JsonObject"/>. Streaming actions get a
/// <see cref="StreamContext"/> and report progress as they work.
/// </para>
/// </remarks>
public sealed record PluginAction
{
    /// <summary>
    /// Stable identifier. This is what arrives as <c>action</c> in the
    /// management command, and what your handler dispatches on.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>What the button says.</summary>
    public required string Label { get; init; }

    /// <summary>
    /// Shown next to the button. Say what it will do, and to what — an operator
    /// is deciding whether to press it.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// JSON Schema for the parameters. Null means the action takes none, and
    /// the UI renders a plain button rather than a form.
    /// </summary>
    public JsonNode? Params { get; init; }

    /// <summary>JSON Schema of the result, for display.</summary>
    public JsonNode? Result { get; init; }

    /// <summary>True if the handler takes a <see cref="StreamContext"/>.</summary>
    public bool Stream { get; init; }

    /// <summary>
    /// True if the stream honours a cancel. Only meaningful with
    /// <see cref="Stream"/>, and only claim it if you actually check.
    /// </summary>
    public bool Cancelable { get; init; }

    public Concurrency Concurrency { get; init; } = Concurrency.Multi;

    /// <summary>
    /// For a streaming action that emits items, the field in each item that
    /// identifies it — so the UI updates a row rather than appending a
    /// duplicate.
    /// </summary>
    public string? ItemKey { get; init; }

    /// <summary>Which of add/update/remove this action emits.</summary>
    public IReadOnlyList<ItemOp>? ItemOperations { get; init; }

    public RequiresRole RequiresRole { get; init; } = RequiresRole.User;

    /// <summary>
    /// How long homeCore should wait before giving up. Set it above the
    /// action's realistic worst case; the default window is short, and a sweep
    /// that gets cut off looks like a broken plugin rather than a slow network.
    /// </summary>
    public int? TimeoutMs { get; init; }

    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["id"] = Id,
            ["label"] = Label,
            ["stream"] = Stream,
            ["cancelable"] = Cancelable,
            ["concurrency"] = Concurrency == Concurrency.Single ? "single" : "multi",
            ["requires_role"] = RequiresRole switch
            {
                RequiresRole.Admin => "admin",
                RequiresRole.ReadOnly => "read_only",
                _ => "user",
            },
        };
        // Absent optionals are omitted rather than sent as null, so this
        // manifest is comparable with the Rust SDK's.
        if (Description is not null) o["description"] = Description;
        if (Params is not null) o["params"] = Params.DeepClone();
        if (Result is not null) o["result"] = Result.DeepClone();
        if (ItemKey is not null) o["item_key"] = ItemKey;
        if (ItemOperations is not null)
        {
            var ops = new JsonArray();
            foreach (var op in ItemOperations)
                ops.Add(op switch
                {
                    ItemOp.Add => "add",
                    ItemOp.Update => "update",
                    _ => "remove",
                });
            o["item_operations"] = ops;
        }
        if (TimeoutMs is not null) o["timeout_ms"] = TimeoutMs;
        return o;
    }
}

/// <summary>
/// The manifest: everything this plugin declares about itself.
/// </summary>
/// <remarks>
/// <c>plugin_id</c> is filled in by the SDK — it has to match the MQTT client id
/// and there is no reason to say it twice.
/// </remarks>
public sealed class Capabilities
{
    public IReadOnlyList<PluginAction> Actions { get; init; } = Array.Empty<PluginAction>();

    /// <summary>
    /// JSON Schema for the plugin's own config file. When present, hc-web
    /// renders a typed settings form instead of a raw text box — and then sends
    /// structured config, which means overriding
    /// <see cref="PluginClient.OnSetConfig"/>.
    /// </summary>
    public JsonNode? ConfigSchema { get; init; }

    /// <summary>
    /// A plugin-authored field descriptor. Takes precedence over
    /// <see cref="ConfigSchema"/> for rendering, when you want to control
    /// grouping, labels and help text rather than let a schema be guessed at.
    /// </summary>
    public JsonNode? ConfigDescriptor { get; init; }

    internal string PluginId { get; set; } = "";

    public JsonObject ToJson()
    {
        var actions = new JsonArray();
        foreach (var a in Actions) actions.Add(a.ToJson());

        var o = new JsonObject
        {
            ["spec"] = "1",
            ["plugin_id"] = PluginId,
            ["actions"] = actions,
        };
        // These ride on the manifest rather than a topic of their own; core
        // extracts them from this payload.
        if (ConfigSchema is not null) o["config_schema"] = ConfigSchema.DeepClone();
        if (ConfigDescriptor is not null) o["config_descriptor"] = ConfigDescriptor.DeepClone();
        return o;
    }
}
