using System.Reflection;
using System.Text.Json.Nodes;
using HomeCore.PluginSdk;
using Xunit;

namespace HomeCore.PluginSdk.Tests;

/// <summary>
/// Alerts — what a plugin says needs attention about its devices. The wire
/// shape is core's <c>hc_types::alert::{PluginAlert, PluginAlertKind}</c>;
/// these pin it.
/// </summary>
public class AlertsTests
{
    private sealed record Published(string Topic, string Payload, bool Retain);

    private static (PluginClient, List<Published>) Captured()
    {
        var c = new PluginClient(new PluginOptions { PluginId = "plugin.test" });
        var log = new List<Published>();
        c.PublishSink = (t, p, r) => { log.Add(new Published(t, p, r)); return Task.CompletedTask; };
        return (c, log);
    }

    private static Task Republish(PluginClient c) =>
        (Task)typeof(PluginClient)
            .GetMethod("RepublishAlertsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(c, null)!;

    private static void AssertJson(string expected, JsonNode actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), actual), actual.ToJsonString());

    // ── wire form ────────────────────────────────────────────────────────

    [Fact]
    public void ABareAlertCarriesOnlyTopicAndReason() =>
        AssertJson(
            """{"topic":"firmware_update","reason":"Firmware 17.0.0 is out"}""",
            new PluginAlert { Topic = "firmware_update", Reason = "Firmware 17.0.0 is out" }.ToJson());

    [Fact]
    public void AFullAlertMatchesTheRustWireForm()
    {
        var alert = new PluginAlert
        {
            Topic = "firmware_update",
            Reason = "Firmware 17.0.0 is out",
            Key = "17.0.0",
            Message = "Kitchen strip: firmware 17.0.0 is out",
            Severity = AlertSeverity.Info,
            Action = new AlertAction
            {
                Label = "Update…",
                Id = "install_firmware",
                Params = JsonNode.Parse("""{"version":"17.0.0"}"""),
                Confirm = true,
            },
            Since = new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.FromHours(-4)),
        };
        // target is omitted when it is the default, `device`; since is UTC.
        AssertJson("""
            {
              "topic": "firmware_update",
              "key": "17.0.0",
              "reason": "Firmware 17.0.0 is out",
              "message": "Kitchen strip: firmware 17.0.0 is out",
              "severity": "info",
              "action": {
                "label": "Update…",
                "id": "install_firmware",
                "params": {"version": "17.0.0"},
                "confirm": true
              },
              "since": "2026-10-19T12:00:00Z"
            }
            """, alert.ToJson());
    }

    [Fact]
    public void AnActionOmitsTargetParamsAndConfirmByDefault() =>
        AssertJson("""{"label":"Re-pair","id":"pair"}""",
            new AlertAction { Label = "Re-pair", Id = "pair" }.ToJson());

    [Fact]
    public void APluginTargetIsSpelledOut() =>
        AssertJson("""{"label":"Sign in","id":"login","target":"plugin"}""",
            new AlertAction { Label = "Sign in", Id = "login", Target = AlertActionTarget.Plugin }.ToJson());

    [Theory]
    [InlineData(AlertSeverity.Info, "info")]
    [InlineData(AlertSeverity.Warning, "warning")]
    [InlineData(AlertSeverity.Error, "error")]
    [InlineData(AlertSeverity.Critical, "critical")]
    public void SeveritiesUseTheLowercaseWireForm(AlertSeverity s, string wire) =>
        Assert.Equal(wire,
            new PluginAlert { Topic = "t", Reason = "r", Severity = s }.ToJson()["severity"]!.GetValue<string>());

    [Fact]
    public void AnAlertKindAlwaysCarriesSeverityAndDefaultOn()
    {
        AssertJson("""
            {"topic":"bridge_button","label":"Bridge needs its button pressed",
             "severity":"warning","default_on":true}
            """,
            new PluginAlertKind { Topic = "bridge_button", Label = "Bridge needs its button pressed" }.ToJson());
        AssertJson("""
            {"topic":"firmware_update","label":"Firmware update available",
             "description":"A newer firmware is published.",
             "severity":"info","default_on":false}
            """,
            new PluginAlertKind
            {
                Topic = "firmware_update",
                Label = "Firmware update available",
                Description = "A newer firmware is published.",
                Severity = AlertSeverity.Info,
                DefaultOn = false,
            }.ToJson());
    }

    [Fact]
    public void AlertKindsRideOnTheManifest()
    {
        var json = new Capabilities
        {
            AlertKinds = new[]
            {
                new PluginAlertKind { Topic = "firmware_update", Label = "Firmware update available" },
            },
        }.ToJson();
        Assert.Empty(json["actions"]!.AsArray());
        AssertJson("""
            [{"topic":"firmware_update","label":"Firmware update available",
              "severity":"warning","default_on":true}]
            """, json["alert_kinds"]!);
    }

    [Fact]
    public void AManifestWithoutAlertKindsDoesNotMentionThem() =>
        Assert.False(new Capabilities().ToJson().ContainsKey("alert_kinds"));

    // ── publishing and the held set ──────────────────────────────────────

    [Fact]
    public async Task SetAlertsPublishesTheWholeListRetained()
    {
        var (c, log) = Captured();
        await c.SetAlertsAsync("wled_1", new[]
        {
            new PluginAlert { Topic = "firmware_update", Reason = "out", Key = "17.0.0" },
        });
        var p = Assert.Single(log);
        Assert.Equal("homecore/devices/wled_1/alerts", p.Topic);
        Assert.True(p.Retain);
        AssertJson("""[{"topic":"firmware_update","key":"17.0.0","reason":"out"}]""",
            JsonNode.Parse(p.Payload)!);
    }

    [Fact]
    public async Task SetPluginAlertsPublishesToThePluginTopic()
    {
        var (c, log) = Captured();
        await c.SetPluginAlertsAsync(new[] { new PluginAlert { Topic = "login_expired", Reason = "The login expired" } });
        var p = Assert.Single(log);
        Assert.Equal("homecore/plugins/plugin.test/alerts", p.Topic);
        Assert.True(p.Retain);
    }

    [Fact]
    public async Task AnEmptyListClearsAndIsForgotten()
    {
        var (c, log) = Captured();
        await c.SetAlertsAsync("wled_1", new[] { new PluginAlert { Topic = "t", Reason = "r" } });
        await c.SetAlertsAsync("wled_1", Array.Empty<PluginAlert>());
        // The empty list goes out — it is what clears the retained message.
        Assert.Equal(new Published("homecore/devices/wled_1/alerts", "[]", true), log[^1]);

        log.Clear();
        await Republish(c);
        Assert.Empty(log);
    }

    [Fact]
    public async Task HeldAlertsAreSaidAgainOnEveryReconnect()
    {
        // A restarted core's broker has forgotten its retained messages; an
        // alert set an hour ago must not vanish because core did.
        var (c, log) = Captured();
        await c.SetAlertsAsync("a", new[] { new PluginAlert { Topic = "k", Reason = "first" } });
        await c.SetAlertsAsync("a", new[] { new PluginAlert { Topic = "k", Reason = "second" } });
        await c.SetPluginAlertsAsync(new[] { new PluginAlert { Topic = "p", Reason = "mine" } });
        await c.SetAlertsAsync("gone", new[] { new PluginAlert { Topic = "x", Reason = "y" } });
        await c.SetAlertsAsync("gone", Array.Empty<PluginAlert>());

        for (var i = 0; i < 2; i++)
        {
            log.Clear();
            await Republish(c);
            Assert.Equal(
                new[]
                {
                    new Published("homecore/devices/a/alerts", """[{"topic":"k","reason":"second"}]""", true),
                    new Published("homecore/plugins/plugin.test/alerts", """[{"topic":"p","reason":"mine"}]""", true),
                },
                log);
        }
    }

    [Fact]
    public async Task AClearSetWhileDisconnectedIsDeliveredOnConnect()
    {
        // MQTTnet does not queue while disconnected. A clear that was simply
        // dropped would leave the broker holding the stale list it retained.
        var c = new PluginClient(new PluginOptions { PluginId = "plugin.test" });
        await c.SetAlertsAsync("wled_1", Array.Empty<PluginAlert>()); // offline: held, not thrown

        var log = new List<Published>();
        c.PublishSink = (t, p, r) => { log.Add(new Published(t, p, r)); return Task.CompletedTask; };
        await Republish(c);
        Assert.Equal(new[] { new Published("homecore/devices/wled_1/alerts", "[]", true) }, log);

        // Delivered, so forgotten.
        log.Clear();
        await Republish(c);
        Assert.Empty(log);
    }
}
