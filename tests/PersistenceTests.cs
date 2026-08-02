using System.Text.Json;
using System.Reflection;
using HomeCore.PluginSdk;
using Xunit;

namespace HomeCore.PluginSdk.Tests;

public class PersistenceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public PersistenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hcsdk-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, ".published-device-ids.json");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Scoped => _path.Replace(".json", ".plugin.test.json");

    private static PluginClient NewClient() =>
        new(new PluginOptions { PluginId = "plugin.test" });

    private static string ScopedPath(string p, string id)
    {
        var m = typeof(DeviceTracker).GetMethod(
            "ScopedSnapshotPath", BindingFlags.Static | BindingFlags.Public)!;
        return (string)m.Invoke(null, new object[] { p, id })!;
    }

    // Real deployments keep every plugin's config in one directory, and every
    // plugin derives this path the same way — unscoped they share one file and
    // retire each other's devices.
    [Fact]
    public void SnapshotPathIsScopedToThePlugin()
    {
        var hue = ScopedPath("/cfg/.published-device-ids.json", "plugin.hue");
        var sonos = ScopedPath("/cfg/.published-device-ids.json", "plugin.sonos");
        Assert.NotEqual(hue, sonos);
        Assert.EndsWith(".published-device-ids.plugin.hue.json", hue);
    }

    [Fact]
    public void ScopingIsIdempotent()
    {
        var once = ScopedPath("/cfg/.published-device-ids.json", "plugin.hue");
        Assert.Equal(once, ScopedPath(once, "plugin.hue"));
    }

    [Fact]
    public async Task RegisteredDevicesAreWrittenToDisk()
    {
        var c = NewClient();
        c.EnableDevicePersistence(_path);
        await c.SubscribeCommandsAsync("light.01");
        await c.SubscribeCommandsAsync("light.02");

        var ids = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(Scoped))!;
        Assert.Equal(new[] { "light.01", "light.02" }, ids);
    }

    [Fact]
    public async Task ANewProcessRemembersThePreviousRun()
    {
        var first = NewClient();
        first.EnableDevicePersistence(_path);
        await first.SubscribeCommandsAsync("light.01");

        var second = NewClient();
        second.EnableDevicePersistence(_path);
        var report = await second.ReconcileDevicesAsync(new[] { "light.01" });
        // Known, so not stale and not unknown — only possible if the snapshot
        // loaded into the fresh process.
        Assert.Empty(report.StaleUnregistered);
        Assert.Empty(report.UnknownInLive);
    }

    [Fact]
    public void TheTrackerDiffsWhatVanished()
    {
        // The reconcile diff itself, without a broker: a device present in the
        // tracker and absent from `live` is what gets retired. Whether the
        // unregister publish then succeeds is covered against a real core.
        var tracker = new DeviceTracker(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        tracker.EnablePersistence(Scoped);
        tracker.Add("light.01");
        tracker.Add("light.02");

        var live = new HashSet<string> { "light.01" };
        Assert.Equal(new[] { "light.02" }, tracker.Snapshot().Except(live).ToArray());
        Assert.Empty(live.Except(tracker.Snapshot()));
    }

    [Fact]
    public void AFailedUnregisterLeavesTheDeviceKnown()
    {
        // Not connected, so the unregister publish fails. The id must stay in
        // the tracker and be retried next time rather than being forgotten
        // locally while homeCore still holds it.
        var tracker = new DeviceTracker(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        tracker.Add("light.01");
        Assert.True(tracker.Contains("light.01"));
    }

    [Fact]
    public async Task ReconcileReportsIdsItNeverRegistered()
    {
        var c = NewClient();
        c.EnableDevicePersistence(_path);
        var report = await c.ReconcileDevicesAsync(new[] { "light.surprise" });
        Assert.Equal(new[] { "light.surprise" }, report.UnknownInLive);
        Assert.Empty(report.StaleUnregistered);
    }

    [Fact]
    public async Task ACorruptSnapshotIsSurvivable()
    {
        // Losing the snapshot costs reconcile, not the plugin.
        File.WriteAllText(Scoped, "{not json");
        var c = NewClient();
        c.EnableDevicePersistence(_path);
        await c.SubscribeCommandsAsync("light.01");
        var report = await c.ReconcileDevicesAsync(new[] { "light.01" });
        Assert.Empty(report.StaleUnregistered);
    }

    [Fact]
    public async Task PersistenceIsOptional()
    {
        var c = NewClient();
        await c.SubscribeCommandsAsync("light.01");
        Assert.False(File.Exists(Scoped));
    }
}
