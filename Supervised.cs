// A plugin homeCore started goes when homeCore does.
//
// Core sets HOMECORE_SUPERVISED to its own process id on every plugin it
// starts. Core stopped the usual way stops its plugins first. Core killed or
// crashed leaves them running, and the next core starts a second copy of each
// under the same broker id; the two knock each other off the broker every few
// seconds for as long as both run. So a plugin whose parent was core, and is
// no longer, stops.
//
// A plugin started any other way (by hand, by systemd, in its own container)
// has no such variable and is left alone. One started by core through another
// process, a launcher script that doesn't exec, has a parent that isn't core,
// and is left alone too, rather than stopped by mistake.

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace HomeCore.PluginSdk;

public static class Supervised
{
    /// <summary>The variable core sets on each plugin it starts: core's process id.</summary>
    public const string Env = "HOMECORE_SUPERVISED";

    private static readonly TimeSpan Every = TimeSpan.FromSeconds(2);
    private static int _started;

    [DllImport("libc", EntryPoint = "getppid")]
    private static extern int GetPpid();

    /// <summary>This process's parent, as the system has it now.</summary>
    public static int ParentId() => GetPpid();

    /// <summary>Core's id when core is the parent and so worth watching, else null.</summary>
    public static int? Decide(string? value, int parent) =>
        int.TryParse(value?.Trim(), out var core) && core > 0 && core == parent ? core : null;

    /// <summary>Start watching, once per process, if core is the parent.</summary>
    public static void Watch(string pluginId, ILogger logger)
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())) return;
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        var value = Environment.GetEnvironmentVariable(Env);
        var core = Decide(value, GetPpid());
        if (core is null)
        {
            if (value is not null)
                logger.LogInformation(
                    "{PluginId}: started by homeCore through another process, so not watching for homeCore stopping",
                    pluginId);
            return;
        }
        var thread = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(Every);
                if (GetPpid() == core) continue;
                // Its output went to core, which is gone: writing may fail, and
                // mustn't keep the plugin from stopping.
                try
                {
                    logger.LogWarning(
                        "{PluginId}: homeCore (pid {Core}), which started this plugin, has stopped; stopping too, so the next homeCore's copy runs alone",
                        pluginId, core);
                }
                catch
                {
                    // nothing to tell, and no one to tell it to
                }
                Environment.Exit(0);
            }
        })
        {
            IsBackground = true,
            Name = "homecore-watch",
        };
        thread.Start();
    }
}
