using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HomeCore.PluginSdk;

/// <summary>Outcome of <see cref="PluginClient.ReconcileDevicesAsync"/>.</summary>
public sealed record ReconcileReport
{
    /// <summary>
    /// Registered before this reconcile but absent from the live set, so
    /// unregistered.
    /// </summary>
    public IReadOnlyList<string> StaleUnregistered { get; init; } = Array.Empty<string>();

    /// <summary>
    /// In the live set but never registered. Usually empty; non-empty means the
    /// caller passed ids it never registered with the SDK. Reported for
    /// diagnosis, no action taken.
    /// </summary>
    public IReadOnlyList<string> UnknownInLive { get; init; } = Array.Empty<string>();
}

/// <summary>
/// The set of devices this plugin has registered, optionally mirrored to disk.
/// </summary>
/// <remarks>
/// <para>
/// When a device disappears from a plugin's authoritative source — a Hue bulb
/// deleted from the bridge, a Z-Wave node excluded, an entry removed from
/// config — its homeCore record has to go too. Otherwise it lingers forever,
/// still shown in the UI and still accepting commands nothing will execute.
/// </para>
/// <para>
/// Working out what disappeared means knowing what existed <i>before</i>, and a
/// plugin that has just restarted knows nothing. So every register/unregister
/// is mirrored to a small JSON file, loaded at startup.
/// </para>
/// </remarks>
internal sealed class DeviceTracker
{
    private readonly object _lock = new();
    private readonly HashSet<string> _ids = new();
    private readonly ILogger _logger;
    private string? _path;

    internal DeviceTracker(ILogger logger) => _logger = logger;

    /// <summary>
    /// Insert <paramref name="pluginId"/> into a snapshot filename.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>.published-device-ids.json</c> →
    /// <c>.published-device-ids.plugin.hue.json</c>
    /// </para>
    /// <para>
    /// Real deployments keep every plugin's config in one directory, and every
    /// plugin derives this path the same way — so without scoping they share
    /// one file and unregister each other's devices.
    /// </para>
    /// <para>
    /// Idempotent, so repeated calls cannot keep extending the name. Works on
    /// the whole filename rather than splitting on the extension, because
    /// plugin ids contain dots and so does the base filename.
    /// </para>
    /// </remarks>
    public static string ScopedSnapshotPath(string path, string pluginId)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileName(path);
        string scoped;
        if (name.EndsWith(".json", StringComparison.Ordinal))
        {
            var stem = name[..^".json".Length];
            scoped = stem.EndsWith(pluginId, StringComparison.Ordinal)
                ? name
                : $"{stem}.{pluginId}.json";
        }
        else
        {
            scoped = name.EndsWith(pluginId, StringComparison.Ordinal)
                ? name
                : $"{name}.{pluginId}";
        }
        return Path.Combine(dir, scoped);
    }

    /// <summary>
    /// Load any previous snapshot, then mirror every change to
    /// <paramref name="path"/>.
    /// </summary>
    /// <remarks>
    /// A failure to load is logged loudly and never thrown. It is not fatal —
    /// the plugin still works — but it does silently cost the ability to retire
    /// devices from earlier runs, so it must not pass unnoticed.
    /// </remarks>
    public void EnablePersistence(string path)
    {
        try
        {
            var ids = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path));
            if (ids is not null)
            {
                lock (_lock) foreach (var id in ids) _ids.Add(id);
                _logger.LogDebug("Loaded {Count} ids from device snapshot {Path}", ids.Count, path);
            }
        }
        catch (FileNotFoundException)
        {
            _logger.LogDebug("No device snapshot at {Path} yet — first run", path);
        }
        catch (DirectoryNotFoundException)
        {
            _logger.LogDebug("No device snapshot at {Path} yet — first run", path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Cannot read device snapshot {Path} ({Error}) — devices registered in "
                + "earlier runs cannot be reconciled and will linger in homeCore",
                path, ex.Message);
        }
        _path = path;
    }

    public void Add(string deviceId)
    {
        bool changed;
        lock (_lock) changed = _ids.Add(deviceId);
        if (changed) Save();
    }

    public void Remove(string deviceId)
    {
        bool changed;
        lock (_lock) changed = _ids.Remove(deviceId);
        if (changed) Save();
    }

    public bool Contains(string deviceId)
    {
        lock (_lock) return _ids.Contains(deviceId);
    }

    public HashSet<string> Snapshot()
    {
        lock (_lock) return new HashSet<string>(_ids);
    }

    public int Count
    {
        get { lock (_lock) return _ids.Count; }
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            List<string> ordered;
            lock (_lock) ordered = _ids.OrderBy(x => x, StringComparer.Ordinal).ToList();

            // Write to a temp file in the same directory and move, so a crash
            // mid-write cannot leave a truncated snapshot that reads as "this
            // plugin registered nothing" and retires every device on the next
            // reconcile.
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(
                ordered, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Device snapshot write failed ({Path}): {Error}", _path, ex.Message);
        }
    }
}
