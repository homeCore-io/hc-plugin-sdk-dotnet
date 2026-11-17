using System.Globalization;
using System.Text.Json.Nodes;
using HomeCore.PluginSdk;
using Xunit;

namespace HomeCore.PluginSdk.Tests;

public class StreamingTests
{
    private static (StreamContext, List<JsonObject>) Stream()
    {
        var events = new List<JsonObject>();
        var c = new PluginClient(new PluginOptions { PluginId = "plugin.test" });
        c.PublishSink = (_, p, _) =>
        {
            // The empty payload that clears the retained frame is not an event.
            if (p.Length > 0) lock (events) events.Add(JsonNode.Parse(p)!.AsObject());
            return Task.CompletedTask;
        };
        return (new StreamContext(c, "r1", "chat"), events);
    }

    private static DateTime Ts(JsonObject ev) =>
        DateTime.ParseExact(
            ev["ts"]!.GetValue<string>(),
            "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    [Fact]
    public async Task ProgressAndAwaitingUserCarryDataWhenGiven()
    {
        var (ctx, events) = Stream();
        await ctx.ProgressAsync(message: "writing", data: new JsonObject { ["text"] = "Hel" });
        await ctx.ProgressAsync(percent: 50);
        await ctx.AwaitingUserAsync("Apply this?", data: new JsonObject { ["preview"] = "on" });
        await ctx.AwaitingUserAsync("Press the button");

        Assert.Equal("Hel", events[0]["data"]!["text"]!.GetValue<string>());
        Assert.False(events[1].ContainsKey("data"));
        Assert.Equal("on", events[2]["data"]!["preview"]!.GetValue<string>());
        Assert.False(events[3].ContainsKey("data"));
    }

    [Fact]
    public async Task EveryEventIsLaterThanTheOneBefore()
    {
        // Back to back events fall in the same microsecond; a reader that drops
        // an event whose ts it has seen would lose one.
        var (ctx, events) = Stream();
        for (var i = 0; i < 200; i++)
            await ctx.ProgressAsync(data: new JsonObject { ["i"] = i });
        await ctx.FinalizeAsync(null); // the SDK's own error event, too

        Assert.Equal(201, events.Count);
        Assert.Equal("plugin_dropped_stream", events[^1]["data"]!["reason"]!.GetValue<string>());
        for (var i = 1; i < events.Count; i++)
            Assert.True(Ts(events[i]) > Ts(events[i - 1]), $"event {i} is not after event {i - 1}");
    }

    [Fact]
    public async Task TsIsUtcWithMicroseconds()
    {
        var (ctx, events) = Stream();
        await ctx.ProgressAsync();
        Assert.Matches(
            @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}Z$",
            events[0]["ts"]!.GetValue<string>());
    }
}
