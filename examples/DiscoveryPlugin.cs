// DiscoveryPlugin.cs — notices and capability actions, end to end.
//
// A plugin for an imaginary hub. It has nothing to control until the hub is
// discovered, which is the situation notices exist for: without one it would
// sit there looking healthy and doing nothing.
//
// Build and run it against a homeCore. Create the project beside the SDK's
// folder, not inside it: the SDK's project file compiles every .cs file under
// its folder, so a project created there gets built into the library.
//
//   # from the folder that holds hc-plugin-sdk-dotnet
//   dotnet new console -o DiscoveryDemo
//   cd DiscoveryDemo
//   dotnet add reference ../hc-plugin-sdk-dotnet/HomeCoreSdk.csproj
//   cp ../hc-plugin-sdk-dotnet/examples/DiscoveryPlugin.cs Program.cs
//   HC_BROKER_HOST=127.0.0.1 dotnet run
//
// The environment variables are for trying it by hand. homeCore starts a
// plugin with its config file as the only argument, and a real plugin reads
// the broker, plugin id and password from that file's [homecore] table; the
// README's "Your first plugin" shows how.
//
// Then, in the web UI, open Plugins → Discovery Demo and you will see:
//
//   - a warning notice saying no hub is configured, with a remedy,
//   - a "Discover hubs" button that streams progress and results,
//   - a "Ping hub" button that answers immediately.
//
// Press Discover and the notice clears itself, because the condition it
// reports stopped being true — that is the whole model.

using System.Text.Json.Nodes;
using HomeCore.PluginSdk;

// Stand-ins for a real network sweep.
var candidateHosts = Enumerable.Range(10, 6).Select(n => $"192.168.1.{n}").ToList();
var hubsThatAnswer = new Dictionary<string, string> { ["192.168.1.12"] = "HUB-A1B2" };

string? hubHost = null;

var client = new PluginClient(new PluginOptions { PluginId = "plugin.discovery_demo_net" });

// ── notices ──────────────────────────────────────────────────────────────
//
// Re-derive every condition from current state. Called after connect and after
// each sweep. Deriving the whole set and calling Set cannot leave a stale
// notice behind, which is the failure mode of scattered Raise/Clear pairs.
void RefreshNotices()
{
    var notices = new List<PluginNotice>();
    if (hubHost is null)
    {
        notices.Add(PluginNotice.Warning(
            "no_hub_configured",
            "No hub has been found, so this plugin publishes nothing.",
            "Run the Discover hubs action."));
    }
    else if (!hubsThatAnswer.ContainsKey(hubHost))
    {
        notices.Add(PluginNotice.Error(
            "hub_unreachable",
            $"The hub at {hubHost} stopped answering.",
            "Check that it is powered on and on this network."));
    }
    client.Notices.Set(notices);
}

// ── actions ──────────────────────────────────────────────────────────────

client.OnAction = async (action, @params, ctx) =>
{
    switch (action)
    {
        case "discover_hubs":
        {
            var found = 0;
            for (var i = 0; i < candidateHosts.Count; i++)
            {
                // Cancellation is cooperative — nothing interrupts this loop,
                // so it has to be checked. Emitting `canceled` is also ours to
                // do, because only we know when any rollback is finished.
                if (ctx!.IsCanceled())
                {
                    await ctx.CanceledAsync();
                    return null;
                }

                var host = candidateHosts[i];
                await ctx.ProgressAsync(
                    percent: 100 * i / candidateHosts.Count,
                    message: $"Probing {host}");
                await Task.Delay(300); // a real probe would be a socket timeout

                if (!hubsThatAnswer.TryGetValue(host, out var serial)) continue;

                found++;
                hubHost = host;
                var deviceId = $"hub_{serial}";
                await client.RegisterDeviceFullAsync(deviceId, $"Hub {serial}", deviceType: "switch");
                await client.PublishAvailabilityAsync(deviceId, true);
                await client.PublishStateAsync(deviceId, new { on = false });
                // `serial` is the manifest's ItemKey, so the UI keys the row on
                // it and an update lands on the same row rather than appending.
                await ctx.ItemAddAsync(new JsonObject
                {
                    ["serial"] = serial,
                    ["host"] = host,
                    ["name"] = $"Hub {serial}",
                });
            }

            if (found == 0)
            {
                // Non-terminal: the sweep finished, it just found nothing. An
                // error would be wrong — nothing failed.
                await ctx!.WarningAsync("No hubs answered on this subnet.");
            }

            RefreshNotices();
            await ctx!.CompleteAsync(new JsonObject { ["found"] = found });
            return null;
        }

        case "ping_hub":
            return new JsonObject
            {
                ["reachable"] = hubHost is not null && hubsThatAnswer.ContainsKey(hubHost),
            };

        case "forget_hub":
            // The result is merged into the response, whose "status" says
            // whether the action succeeded, so the result can't use that key.
            if (hubHost is null) return new JsonObject { ["outcome"] = "nothing to forget" };
            await client.UnregisterDeviceAsync($"hub_{hubsThatAnswer[hubHost]}");
            hubHost = null;
            RefreshNotices();
            return new JsonObject { ["outcome"] = "forgotten" };

        default:
            return null; // not ours — the SDK answers "unknown action"
    }
};

client.OnCommand += async (deviceId, payload) =>
{
    var state = new JsonObject();
    foreach (var prop in payload.EnumerateObject())
        if (!prop.Name.StartsWith('_'))
            state[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
    await client.PublishStateForCommandAsync(deviceId, state, payload, "discovery_demo");
};

// ── lifecycle ────────────────────────────────────────────────────────────

client.OnConnected += async () =>
{
    await client.EnableManagementAsync(new ManagementOptions
    {
        HeartbeatIntervalSecs = 30,
        Version = "1.0.0",
        Capabilities = new Capabilities
        {
            Actions = new[]
            {
                new PluginAction
                {
                    Id = "discover_hubs",
                    Label = "Discover hubs",
                    Description = "Probe the local subnet for hubs and register what answers.",
                    Stream = true,
                    Cancelable = true,
                    ItemKey = "serial",
                    // Above the realistic worst case: core's default window is
                    // short, and a sweep that gets cut off looks like a broken
                    // plugin rather than a slow network.
                    TimeoutMs = 30_000,
                },
                new PluginAction
                {
                    Id = "ping_hub",
                    Label = "Ping hub",
                    Description = "Check the configured hub is still answering.",
                    Result = JsonNode.Parse("""{"reachable":{"type":"boolean"}}"""),
                },
                new PluginAction
                {
                    Id = "forget_hub",
                    Label = "Forget hub",
                    Description = "Unregister the hub and its devices.",
                    // Destructive, so an operator account should not press it
                    // by accident.
                    RequiresRole = RequiresRole.Admin,
                },
            },
        },
    });
    RefreshNotices();
};

await client.ConnectAsync();
await client.RunAsync();
