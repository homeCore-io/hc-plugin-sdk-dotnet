using System.Text.Json.Nodes;

namespace HomeCore.PluginSdk;

/// <summary>
/// Notices — a plugin's own account of what is wrong with it.
/// </summary>
/// <remarks>
/// <para>
/// A plugin's status answers "is the process alive": active, offline, stopped.
/// It cannot answer "alive, but structurally unable to do its job", and that is
/// the state operators actually get stuck in. A plugin whose receiver is bound
/// to the wrong interface starts cleanly, heartbeats, reports active, and
/// silently drops everything. On the dashboard it reads as healthy.
/// </para>
/// <para>
/// A notice carries the diagnosis to the UI, where it appears on the plugin's
/// card rather than only in a log stream nobody is reading.
/// </para>
/// <para>
/// Notices are <b>current state, not an event log</b>. The full set rides on
/// every heartbeat and homeCore replaces what it held, so a cleared condition
/// disappears on its own — nothing to acknowledge, nothing to expire.
/// </para>
/// <para>
/// The trap is raising once at startup and never looking again: a plugin that
/// reports <c>no_devices_configured</c> at boot is still showing it after the
/// operator has added devices. Re-derive conditions where you already loop.
/// </para>
/// </remarks>
public enum NoticeLevel
{
    /// <summary>Worth knowing, nothing is wrong — a deliberate non-default mode, say.</summary>
    Info,

    /// <summary>Runs, but something it needs is missing and some function is unavailable.</summary>
    Warning,

    /// <summary>Cannot do its job at all; operator action required.</summary>
    Error,
}

/// <summary>One condition a plugin is reporting about itself.</summary>
public sealed record PluginNotice
{
    /// <summary>How much the operator should care.</summary>
    public required NoticeLevel Level { get; init; }

    /// <summary>
    /// Stable snake_case identifier (<c>bridge_unreachable</c>). The UI keys off
    /// this to dedupe, so <see cref="Message"/> stays free to be reworded.
    /// Keep it specific to the condition, not the plugin — two plugins with the
    /// same problem should use the same code.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// What is wrong, in a sentence an operator can act on. Say what is
    /// happening and why it matters, not just which setting is unset.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// What to do about it, when that can be stated concretely. Leave null when
    /// the remedy is situational enough that guessing would mislead.
    /// </summary>
    public string? Remedy { get; init; }

    public static PluginNotice Info(string code, string message, string? remedy = null) =>
        new() { Level = NoticeLevel.Info, Code = code, Message = message, Remedy = remedy };

    public static PluginNotice Warning(string code, string message, string? remedy = null) =>
        new() { Level = NoticeLevel.Warning, Code = code, Message = message, Remedy = remedy };

    public static PluginNotice Error(string code, string message, string? remedy = null) =>
        new() { Level = NoticeLevel.Error, Code = code, Message = message, Remedy = remedy };

    /// <summary>
    /// The wire form. <c>remedy</c> is omitted when unset, matching the Rust
    /// SDK's <c>skip_serializing_if</c> so both produce the same JSON.
    /// </summary>
    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["level"] = Level switch
            {
                NoticeLevel.Info => "info",
                NoticeLevel.Warning => "warning",
                _ => "error",
            },
            ["code"] = Code,
            ["message"] = Message,
        };
        if (Remedy is not null) o["remedy"] = Remedy;
        return o;
    }
}

/// <summary>
/// The set of notices a plugin is currently reporting. Obtain one as
/// <see cref="PluginClient.Notices"/>.
/// </summary>
/// <remarks>
/// Thread-safe: plugins typically raise from a polling task while the heartbeat
/// loop reads.
/// </remarks>
public sealed class PluginNotices
{
    private readonly object _lock = new();
    private readonly Dictionary<string, PluginNotice> _notices = new();
    private readonly Action? _onChange;

    /// <param name="onChange">
    /// Called when the set actually changes, so the plugin can push a heartbeat
    /// immediately. Notices ride on the heartbeat, so without this a condition
    /// raised at startup would not reach the UI until the next beat — up to a
    /// minute of the operator looking at a plugin that seems fine.
    /// </param>
    public PluginNotices(Action? onChange = null) => _onChange = onChange;

    /// <summary>
    /// Add or replace the notice with this code. Re-raising overwrites, so
    /// re-deriving conditions on a poll loop is the intended usage rather than
    /// something to guard against.
    /// </summary>
    public void Raise(PluginNotice notice)
    {
        bool changed;
        lock (_lock)
        {
            changed = !_notices.TryGetValue(notice.Code, out var existing) || existing != notice;
            _notices[notice.Code] = notice;
        }
        Notify(changed);
    }

    /// <summary>
    /// Drop the notice with this code. A no-op if it is not raised, so callers
    /// never need to check first.
    /// </summary>
    public void Clear(string code)
    {
        bool changed;
        lock (_lock) changed = _notices.Remove(code);
        Notify(changed);
    }

    /// <summary>
    /// Replace the whole set at once.
    /// </summary>
    /// <remarks>
    /// The right call when a sync cycle re-derives every condition together — it
    /// cannot leave a stale notice behind the way individual raise/clear pairs
    /// can.
    /// </remarks>
    public void Set(IEnumerable<PluginNotice> notices)
    {
        bool changed;
        lock (_lock)
        {
            var next = notices.ToDictionary(n => n.Code);
            changed = next.Count != _notices.Count
                      || next.Any(kv => !_notices.TryGetValue(kv.Key, out var e) || e != kv.Value);
            _notices.Clear();
            foreach (var kv in next) _notices[kv.Key] = kv.Value;
        }
        Notify(changed);
    }

    public void ClearAll()
    {
        bool changed;
        lock (_lock)
        {
            changed = _notices.Count > 0;
            _notices.Clear();
        }
        Notify(changed);
    }

    /// <summary>What the next heartbeat will carry.</summary>
    public IReadOnlyList<PluginNotice> Snapshot()
    {
        lock (_lock) return _notices.Values.ToList();
    }

    public JsonArray ToWire()
    {
        var arr = new JsonArray();
        foreach (var n in Snapshot()) arr.Add(n.ToJson());
        return arr;
    }

    public bool Has(string code)
    {
        lock (_lock) return _notices.ContainsKey(code);
    }

    public int Count
    {
        get { lock (_lock) return _notices.Count; }
    }

    // Fired with the lock released: the callback publishes a heartbeat, which
    // reads the set back through Snapshot() and would re-enter this lock.
    // Monitor is reentrant on the same thread so it would not deadlock, but
    // holding a lock across a publish is a stall waiting to happen.
    private void Notify(bool changed)
    {
        if (changed) _onChange?.Invoke();
    }
}
