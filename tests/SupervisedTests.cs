using HomeCore.PluginSdk;
using Xunit;

namespace HomeCore.PluginSdk.Tests;

public class SupervisedTests
{
    [Fact]
    public void WatchesOnlyWhenCoreIsTheParent()
    {
        Assert.Equal(4242, Supervised.Decide("4242", 4242));
        // Started through a launcher that stayed: its parent isn't core.
        Assert.Null(Supervised.Decide("4242", 5000));
        // Not started by core at all.
        Assert.Null(Supervised.Decide(null, 4242));
        Assert.Null(Supervised.Decide("", 4242));
        Assert.Null(Supervised.Decide("core", 4242));
    }

    [Fact]
    public void ReadsTheParentTheSystemHas()
    {
        if (!OperatingSystem.IsLinux()) return;
        // The fourth field of /proc/self/stat, after the name in parentheses.
        var stat = File.ReadAllText("/proc/self/stat");
        var after = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
        Assert.Equal(int.Parse(after[1]), Supervised.ParentId());
    }
}
