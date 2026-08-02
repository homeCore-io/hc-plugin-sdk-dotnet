using HomeCore.PluginSdk;
using Xunit;

namespace HomeCore.PluginSdk.Tests;

public class NoticesTests
{
    [Fact]
    public void RaiseAndClear()
    {
        var n = new PluginNotices();
        n.Raise(PluginNotice.Error("bridge_unreachable", "gone"));
        Assert.True(n.Has("bridge_unreachable"));
        n.Clear("bridge_unreachable");
        Assert.False(n.Has("bridge_unreachable"));
    }

    [Fact]
    public void ClearingSomethingNotRaisedIsANoOp()
    {
        var changes = 0;
        var n = new PluginNotices(() => changes++);
        n.Clear("never_raised");
        Assert.Equal(0, changes);
    }

    [Fact]
    public void ReRaisingACodeReplacesIt()
    {
        // Re-deriving conditions on a poll loop is the intended usage, so this
        // must overwrite rather than accumulate.
        var n = new PluginNotices();
        n.Raise(PluginNotice.Warning("c", "first"));
        n.Raise(PluginNotice.Error("c", "second"));
        Assert.Equal(1, n.Count);
        var only = Assert.Single(n.Snapshot());
        Assert.Equal("second", only.Message);
        Assert.Equal(NoticeLevel.Error, only.Level);
    }

    [Fact]
    public void WireFormOmitsRemedyWhenAbsent()
    {
        var n = new PluginNotices();
        n.Set(new[]
        {
            PluginNotice.Info("a", "no remedy"),
            PluginNotice.Info("b", "has one", "do this"),
        });
        var wire = n.ToWire().ToDictionary(x => x!["code"]!.GetValue<string>());
        Assert.Null(wire["a"]!["remedy"]);
        Assert.Equal("do this", wire["b"]!["remedy"]!.GetValue<string>());
        Assert.Equal("info", wire["a"]!["level"]!.GetValue<string>());
    }

    [Fact]
    public void SetReplacesTheWholeSet()
    {
        var n = new PluginNotices();
        n.Raise(PluginNotice.Warning("stale", "left over"));
        n.Set(new[] { PluginNotice.Info("fresh", "current") });
        Assert.Equal(new[] { "fresh" }, n.Snapshot().Select(x => x.Code));
    }

    [Fact]
    public void OnlyAnActualChangeNotifies()
    {
        var changes = 0;
        var n = new PluginNotices(() => changes++);

        n.Raise(PluginNotice.Warning("c", "m"));
        Assert.Equal(1, changes);

        // Same content — re-deriving must not cost a publish.
        n.Raise(PluginNotice.Warning("c", "m"));
        Assert.Equal(1, changes);

        n.Set(new[] { PluginNotice.Warning("c", "m") });
        Assert.Equal(1, changes);

        n.Clear("c");
        Assert.Equal(2, changes);
    }

    [Fact]
    public void RaisingFromTheCallbackDoesNotDeadlock()
    {
        // The callback publishes a heartbeat, which reads the set back through
        // Snapshot(). Holding the lock across that call is how the Python SDK
        // deadlocked; this asserts the .NET one does not.
        PluginNotices? n = null;
        var observed = 0;
        n = new PluginNotices(() => observed = n!.Snapshot().Count);

        var done = Task.Run(() => n.Raise(PluginNotice.Error("x", "y")));
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "Raise deadlocked against its own callback");
        Assert.Equal(1, observed);
    }
}
