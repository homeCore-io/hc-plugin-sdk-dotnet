// A plugin's own screens, carried and served by the plugin.
//
// The UI is a built folder — web components in one ES module, plus a
// manifest.json the build writes:
//
//   { "host_api": 1, "entry": "index.js",
//     "slots": [{ "slot": "plugin.page", "element": "hc-x-page" }] }
//
// It travels in whatever package installs the plugin, beside the program as
// ui/. PluginClient.WithUiDir reads it, declares it on the manifest with a hash
// of its files as its build, and answers homeCore's ui_file requests from it —
// homeCore passes the files through to the app and keeps none.
//
// This is the .NET twin of plugin_sdk_rs::ui and must agree with it: the same
// folder gives the same build in every SDK.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HomeCore.PluginSdk;

/// <summary>Why a folder offers no UI, or why a file can't be served.</summary>
public sealed class UiException : Exception
{
    public UiException(string message) : base(message) { }
}

/// <summary>One place in the app a plugin's component fills.</summary>
/// <param name="Slot">
/// <c>plugin.page</c> (its page, in place of the generic action list) or
/// <c>device.panel</c> (its devices' panel, in place of the generic controls).
/// </param>
/// <param name="Element">The custom element the module defines for it.</param>
/// <param name="DeviceTypes">
/// For <c>device.panel</c>: only devices of these types; every one of the
/// plugin's devices when empty.
/// </param>
public sealed record UiSlot(string Slot, string Element, IReadOnlyList<string>? DeviceTypes = null)
{
    public JsonObject ToJson()
    {
        var o = new JsonObject { ["slot"] = Slot, ["element"] = Element };
        if (DeviceTypes is { Count: > 0 })
        {
            var types = new JsonArray();
            foreach (var t in DeviceTypes) types.Add(t);
            o["device_types"] = types;
        }
        return o;
    }
}

/// <summary>What a plugin's UI is and where it goes — the manifest's <c>ui</c>.</summary>
public sealed record PluginUi
{
    /// <summary>The <c>host</c> interface version it was built against.</summary>
    public uint HostApi { get; init; }

    /// <summary>The plugin version it came with — the plugin's own.</summary>
    public string Version { get; init; } = "";

    /// <summary>A hash of the UI's files; set by the SDK from the folder it serves.</summary>
    public string Build { get; init; } = "";

    /// <summary>The module to load, relative to the folder.</summary>
    public string Entry { get; init; } = "index.js";

    /// <summary>Where its components go.</summary>
    public IReadOnlyList<UiSlot> Slots { get; init; } = Array.Empty<UiSlot>();

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["host_api"] = HostApi, ["version"] = Version };
        if (Build.Length > 0) o["build"] = Build;
        o["entry"] = Entry;
        var slots = new JsonArray();
        foreach (var s in Slots) slots.Add(s.ToJson());
        o["slots"] = slots;
        return o;
    }

    /// <summary>
    /// Whether it can be offered at all: a version, a module inside the folder,
    /// and at least one slot the app knows, each naming a valid custom element.
    /// Slots that don't pass are dropped; <c>null</c> when none is left.
    /// </summary>
    public PluginUi? Validated()
    {
        if (Version.Trim().Length == 0
            || !Ui.RelativePath(Entry)
            || !Build.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'))
            return null;
        var slots = Slots.Where(s => Ui.Slots.Contains(s.Slot) && CustomElement(s.Element)).ToList();
        return slots.Count == 0 ? null : this with { Slots = slots };
    }

    /// <summary>A custom element name: lowercase, starting with a letter, with a hyphen.</summary>
    private static bool CustomElement(string name) =>
        name.Contains('-')
        && name.Length > 0 && name[0] is >= 'a' and <= 'z'
        && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.' or '_');
}

/// <summary>Finding, reading and serving a plugin's UI folder.</summary>
public static class Ui
{
    /// <summary>The version of the app's <c>host</c> interface a UI is built against.</summary>
    public const uint HostApi = 1;

    /// <summary>The slots the app knows.</summary>
    public static readonly IReadOnlyList<string> Slots = new[] { "plugin.page", "device.panel" };

    /// <summary>
    /// Where a plugin's UI folder is. In order:
    /// <list type="number">
    /// <item><c>HC_PLUGIN_UI_DIR</c> when set — taken as given;</item>
    /// <item><c>ui/</c> beside the program (<see cref="AppContext.BaseDirectory"/>) —
    /// how a package installs it (copy it to the output directory);</item>
    /// <item>for a plugin run from its source tree, <c>&lt;project&gt;/ui/dist</c>:
    /// <paramref name="projectDir"/> when given, else the nearest folder above
    /// the program holding a <c>.csproj</c>.</item>
    /// </list>
    /// Apart from the first, a folder counts only if it holds <c>manifest.json</c>.
    /// </summary>
    public static string? FindDir(string? projectDir = null) =>
        FindDir(projectDir, AppContext.BaseDirectory);

    internal static string? FindDir(string? projectDir, string programDir)
    {
        var env = Environment.GetEnvironmentVariable("HC_PLUGIN_UI_DIR");
        if (!string.IsNullOrEmpty(env)) return env;
        var candidates = new List<string> { Path.Combine(programDir, "ui") };
        var project = projectDir ?? ProjectAbove(programDir);
        if (project is not null) candidates.Add(Path.Combine(project, "ui", "dist"));
        return candidates.FirstOrDefault(d => File.Exists(Path.Combine(d, "manifest.json")));
    }

    private static string? ProjectAbove(string start)
    {
        for (var d = new DirectoryInfo(Path.GetFullPath(start)); d is not null; d = d.Parent)
        {
            if (d.Exists && d.EnumerateFiles("*.csproj").Any()) return d.FullName;
        }
        return null;
    }

    /// <summary>The UI in <paramref name="dir"/>, as its manifest declares it.</summary>
    /// <exception cref="UiException">With the reason, when there is none to offer.</exception>
    public static PluginUi Load(string dir, string version)
    {
        var manifestPath = Path.Combine(dir, "manifest.json");
        string text;
        try { text = File.ReadAllText(manifestPath); }
        catch (Exception ex) { throw new UiException($"{manifestPath}: {ex.Message}"); }

        uint hostApi;
        string entry = "index.js";
        var slots = new List<UiSlot>();
        try
        {
            using var doc = JsonDocument.Parse(text);
            var m = doc.RootElement;
            if (m.ValueKind != JsonValueKind.Object
                || !m.TryGetProperty("host_api", out var h)
                || h.ValueKind != JsonValueKind.Number
                || !h.TryGetUInt32(out hostApi))
                throw new UiException("manifest.json: `host_api` must be a whole number");
            if (m.TryGetProperty("entry", out var e))
            {
                if (e.ValueKind != JsonValueKind.String)
                    throw new UiException("manifest.json: `entry` must be a string");
                entry = e.GetString()!;
            }
            if (m.TryGetProperty("slots", out var s))
            {
                if (s.ValueKind != JsonValueKind.Array)
                    throw new UiException("manifest.json: `slots` must be a list");
                foreach (var v in s.EnumerateArray()) slots.Add(SlotFrom(v));
            }
        }
        catch (JsonException ex) { throw new UiException($"manifest.json: {ex.Message}"); }

        if (!File.Exists(Path.Combine(dir, entry)))
            throw new UiException($"its entry {entry} isn't there");
        return new PluginUi
        {
            HostApi = hostApi,
            Version = version,
            Build = BuildOf(dir),
            Entry = entry,
            Slots = slots,
        }.Validated() ?? throw new UiException("it declares nowhere the app can put it");
    }

    private static UiSlot SlotFrom(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Object)
            throw new UiException("manifest.json: a slot is not an object");
        if (!v.TryGetProperty("slot", out var slot) || slot.ValueKind != JsonValueKind.String
            || !v.TryGetProperty("element", out var element) || element.ValueKind != JsonValueKind.String)
            throw new UiException("manifest.json: a slot needs `slot` and `element` strings");
        var types = new List<string>();
        if (v.TryGetProperty("device_types", out var t))
        {
            if (t.ValueKind != JsonValueKind.Array
                || t.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))
                throw new UiException("manifest.json: `device_types` must be a list of strings");
            types.AddRange(t.EnumerateArray().Select(x => x.GetString()!));
        }
        return new UiSlot(slot.GetString()!, element.GetString()!, types);
    }

    /// <summary>
    /// A hash of every file's name and contents: what changes when the UI does.
    /// SHA-256 over the files sorted by relative path (forward slashes, UTF-8
    /// byte order), each fed as its path, a 0 byte, then its contents; the
    /// first 8 bytes, in lowercase hex. Identical in every homeCore SDK.
    /// </summary>
    public static string BuildOf(string dir)
    {
        var files = new List<string>();
        try { Walk(dir, dir, files); }
        catch (Exception ex) { throw new UiException(ex.Message); }
        var keyed = files.Select(f => (Bytes: Encoding.UTF8.GetBytes(f), Name: f)).ToList();
        keyed.Sort((a, b) => a.Bytes.AsSpan().SequenceCompareTo(b.Bytes));
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (bytes, name) in keyed)
        {
            h.AppendData(bytes);
            h.AppendData(new byte[] { 0 });
            try { h.AppendData(File.ReadAllBytes(Path.Combine(dir, name))); }
            catch (Exception ex) { throw new UiException(ex.Message); }
        }
        return Convert.ToHexString(h.GetHashAndReset(), 0, 8).ToLowerInvariant();
    }

    private static void Walk(string root, string dir, List<string> output)
    {
        foreach (var p in Directory.GetFileSystemEntries(dir))
        {
            if (Directory.Exists(p)) Walk(root, p, output);
            else output.Add(Path.GetRelativePath(root, p).Replace('\\', '/'));
        }
    }

    /// <summary>A path that stays inside the folder.</summary>
    public static bool RelativePath(string p) =>
        p.Length > 0
        && !p.StartsWith('/')
        && !p.Contains('\\')
        && p.Split('/').All(seg => seg.Length > 0 && seg != "..");

    /// <summary>Up to <paramref name="max"/> bytes of <paramref name="file"/> from
    /// <paramref name="offset"/>, and whether that is the end.</summary>
    /// <exception cref="UiException"><c>no such file</c> for a path outside the
    /// folder or one that isn't there.</exception>
    public static (byte[] Data, bool Done) Piece(string dir, string file, ulong offset, int max)
    {
        if (!RelativePath(file)) throw new UiException("no such file");
        var path = Path.Combine(dir, file);
        if (!File.Exists(path)) throw new UiException("no such file");
        FileStream f;
        try { f = File.OpenRead(path); }
        catch (FileNotFoundException) { throw new UiException("no such file"); }
        catch (DirectoryNotFoundException) { throw new UiException("no such file"); }
        catch (Exception ex) { throw new UiException(ex.Message); }
        using (f)
        {
            try
            {
                var size = (ulong)f.Length;
                var start = Math.Min(offset, size);
                f.Seek((long)start, SeekOrigin.Begin);
                var buf = new byte[(int)Math.Min((ulong)max, size - start)];
                var got = 0;
                while (got < buf.Length)
                {
                    var n = f.Read(buf, got, buf.Length - got);
                    if (n == 0) break;
                    got += n;
                }
                if (got < buf.Length) Array.Resize(ref buf, got);
                // offset + max >= size, without overflowing.
                var done = offset >= size || size - offset <= (ulong)max;
                return (buf, done);
            }
            catch (Exception ex) { throw new UiException(ex.Message); }
        }
    }
}
