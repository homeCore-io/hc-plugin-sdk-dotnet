using System.Text.Json.Nodes;
using HomeCore.PluginSdk;
using Xunit;

namespace HomeCore.PluginSdk.Tests;

public class CapabilitiesTests
{
    [Fact]
    public void MinimalActionSerialisesTheRequiredFields()
    {
        var json = new PluginAction { Id = "rescan", Label = "Rescan" }.ToJson();

        Assert.Equal("rescan", json["id"]!.GetValue<string>());
        Assert.Equal("Rescan", json["label"]!.GetValue<string>());
        Assert.False(json["stream"]!.GetValue<bool>());
        Assert.False(json["cancelable"]!.GetValue<bool>());
        Assert.Equal("multi", json["concurrency"]!.GetValue<string>());
        Assert.Equal("user", json["requires_role"]!.GetValue<string>());
    }

    [Fact]
    public void AbsentOptionalsAreOmittedNotNull()
    {
        // Matching the Rust SDK's skip_serializing_if, so both SDKs produce a
        // comparable manifest.
        var json = new PluginAction { Id = "rescan", Label = "Rescan" }.ToJson();
        Assert.False(json.ContainsKey("description"));
        Assert.False(json.ContainsKey("params"));
        Assert.False(json.ContainsKey("result"));
        Assert.False(json.ContainsKey("item_key"));
        Assert.False(json.ContainsKey("item_operations"));
        Assert.False(json.ContainsKey("timeout_ms"));
    }

    [Fact]
    public void EnumsUseTheSnakeCaseWireForm()
    {
        var json = new PluginAction
        {
            Id = "wipe",
            Label = "Wipe",
            RequiresRole = RequiresRole.ReadOnly,
            Concurrency = Concurrency.Single,
            ItemOperations = new[] { ItemOp.Add, ItemOp.Remove },
        }.ToJson();

        Assert.Equal("read_only", json["requires_role"]!.GetValue<string>());
        Assert.Equal("single", json["concurrency"]!.GetValue<string>());
        Assert.Equal(
            new[] { "add", "remove" },
            json["item_operations"]!.AsArray().Select(x => x!.GetValue<string>()));
    }

    [Fact]
    public void ManifestCarriesSpecAndPluginId()
    {
        var caps = new Capabilities
        {
            Actions = new[] { new PluginAction { Id = "a", Label = "A" } },
        };
        // Normally set by the SDK from the MQTT client id.
        typeof(Capabilities).GetProperty("PluginId",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(caps, "plugin.test");

        var json = caps.ToJson();
        Assert.Equal("1", json["spec"]!.GetValue<string>());
        Assert.Equal("plugin.test", json["plugin_id"]!.GetValue<string>());
        Assert.Single(json["actions"]!.AsArray());
    }

    [Fact]
    public void ConfigSchemaRidesOnTheManifest()
    {
        // Core extracts these from the capability payload rather than a topic
        // of their own, so they have to be here or the settings form never
        // renders.
        var caps = new Capabilities
        {
            ConfigSchema = JsonNode.Parse("""{"type":"object"}"""),
            ConfigDescriptor = JsonNode.Parse("""{"fields":[]}"""),
        };
        var json = caps.ToJson();
        Assert.Equal("object", json["config_schema"]!["type"]!.GetValue<string>());
        Assert.NotNull(json["config_descriptor"]);
    }
}
