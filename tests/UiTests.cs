using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HomeCore.PluginSdk;
using Xunit;

namespace HomeCore.PluginSdk.Tests;

/// <summary>The plugin's own screens: finding, declaring and serving its UI folder.</summary>
[Collection("env")]
public class UiTests : IDisposable
{
    /// <summary>
    /// The build of the shared fixture below. Every homeCore SDK pins this same
    /// value — it is what plugin_sdk_rs::ui::load gives for these two files —
    /// so one UI folder has one address whichever SDK serves it.
    /// </summary>
    private const string FixtureBuild = "6695a38681c3d6e7";
    private const string FixtureManifest =
        """{"host_api":1,"slots":[{"slot":"plugin.page","element":"hc-x-page"}]}""";

    private readonly string _tmp;
    private readonly string? _savedEnv = Environment.GetEnvironmentVariable("HC_PLUGIN_UI_DIR");

    public UiTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), $"hc-ui-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmp);
        Environment.SetEnvironmentVariable("HC_PLUGIN_UI_DIR", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HC_PLUGIN_UI_DIR", _savedEnv);
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private string Folder()
    {
        File.WriteAllBytes(Path.Combine(_tmp, "manifest.json"), Encoding.UTF8.GetBytes(FixtureManifest));
        File.WriteAllBytes(Path.Combine(_tmp, "index.js"), Encoding.UTF8.GetBytes("export {}"));
        return _tmp;
    }

    private static async Task<JsonObject> Manage(PluginClient c, string json)
    {
        var m = typeof(PluginClient).GetMethod(
            "HandleManagementCommandAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return await (Task<JsonObject>)m.Invoke(c, new object[] { JsonDocument.Parse(json).RootElement })!;
    }

    private static PluginClient Managed()
    {
        var c = new PluginClient(new PluginOptions { PluginId = "plugin.test" });
        typeof(PluginClient).GetField("_mgmt", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(c, new ManagementOptions());
        return c;
    }

    [Fact]
    public void TheBuildIsTheOneEverySdkComputes()
    {
        Assert.Equal(FixtureBuild, Ui.Load(Folder(), "1.2.3").Build);
    }

    [Fact]
    public void AUiFolderIsDeclaredWithABuildThatChangesWithItsFiles()
    {
        var d = Folder();
        var a = Ui.Load(d, "1.2.3");
        Assert.Equal(("1.2.3", "index.js", 1u), (a.Version, a.Entry, a.HostApi));
        Assert.Equal(16, a.Build.Length);
        File.WriteAllText(Path.Combine(d, "index.js"), "export const x = 1");
        Assert.NotEqual(a.Build, Ui.Load(d, "1.2.3").Build);
    }

    [Fact]
    public void AFolderWithoutItsEntryOrAManifestOffersNothing()
    {
        Assert.Throws<UiException>(() => Ui.Load(_tmp, "1"));
        File.WriteAllText(Path.Combine(_tmp, "manifest.json"), """{"host_api":1,"slots":[]}""");
        Assert.Contains("isn't there", Assert.Throws<UiException>(() => Ui.Load(_tmp, "1")).Message);
        File.WriteAllText(Path.Combine(_tmp, "index.js"), "export {}");
        Assert.Contains("nowhere", Assert.Throws<UiException>(() => Ui.Load(_tmp, "1")).Message);
    }

    /// <summary>
    /// Which slots exist is homeCore's to say: an SDK that dropped the ones it
    /// doesn't know would keep a plugin from ever offering a newer one.
    /// </summary>
    [Fact]
    public void EverySlotIsPassedOnAsDeclaredUnknownOnesAndFieldsIncluded()
    {
        const string slots = """
            [{"slot":"future.slot","element":"hc-x-later"},
             {"slot":"dashboard.card","element":"hc-x-card","label":"Quick presets",
              "description":"One tap to a preset","forms":["compact","large"],
              "settings":"hc-x-card-settings","someday":{"nested":true}},
             {"slot":"device.panel","element":"hc-x-panel","device_types":["light"]},
             {"slot":"plugin.page","element":"NoHyphen"}]
            """;
        File.WriteAllText(Path.Combine(_tmp, "manifest.json"), $$"""{"host_api":1,"slots":{{slots}}}""");
        File.WriteAllText(Path.Combine(_tmp, "index.js"), "export {}");
        var got = Ui.Load(_tmp, "1.0.0").ToJson()["slots"];
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(slots), got), got!.ToJsonString());
    }

    [Fact]
    public void AnUnusableVersionOrEntryOffersNothing()
    {
        var d = Folder();
        Assert.Contains("version or entry", Assert.Throws<UiException>(() => Ui.Load(d, " ")).Message);
        File.WriteAllText(Path.Combine(d, "manifest.json"),
            """{"host_api":1,"entry":"./index.js","slots":[{"slot":"plugin.page","element":"hc-x-page"}]}""");
        Assert.Equal("./index.js", Ui.Load(d, "1").Entry);
        var sub = Path.Combine(d, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "manifest.json"),
            """{"host_api":1,"entry":"../index.js","slots":[{"slot":"plugin.page","element":"hc-x-page"}]}""");
        Assert.Contains("version or entry", Assert.Throws<UiException>(() => Ui.Load(sub, "1")).Message);
    }

    [Fact]
    public void TheManifestUiIsTheRustWireForm()
    {
        var ui = new PluginUi
        {
            HostApi = 1,
            Version = "1.0.0",
            Build = "abc",
            Slots = new[]
            {
                new UiSlot("plugin.page", "hc-x-page"),
                new UiSlot("device.panel", "hc-x-p", new[] { "light" }),
            },
        };
        Assert.Equal(
            """{"host_api":1,"version":"1.0.0","build":"abc","entry":"index.js","slots":[{"slot":"plugin.page","element":"hc-x-page"},{"slot":"device.panel","element":"hc-x-p","device_types":["light"]}]}""",
            ui.ToJson().ToJsonString());
    }

    [Theory]
    [InlineData("index.js", true)]
    [InlineData("a/b.js", true)]
    [InlineData("./x.js", true)]
    [InlineData("", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("../x", false)]
    [InlineData("a/../../x", false)]
    [InlineData("a//b", false)]
    [InlineData("a\\b", false)]
    [InlineData("a/", false)]
    public void RelativePathsStayInsideTheFolder(string p, bool ok) =>
        Assert.Equal(ok, Ui.RelativePath(p));

    [Fact]
    public void FilesGoInPiecesAndNeverFromOutsideTheFolder()
    {
        File.WriteAllText(Path.Combine(_tmp, "index.js"), "abcdefghij");
        (string, bool) P(ulong o, int m)
        {
            var (data, done) = Ui.Piece(_tmp, "index.js", o, m);
            return (Encoding.UTF8.GetString(data), done);
        }
        Assert.Equal(("abcd", false), P(0, 4));
        Assert.Equal(("ij", true), P(8, 4));
        Assert.Equal(("ghij", true), P(6, 4));
        Assert.Equal(("", true), P(50, 4));
        foreach (var bad in new[] { "../etc/passwd", "missing.js", "/etc/passwd" })
            Assert.Equal("no such file", Assert.Throws<UiException>(() => Ui.Piece(_tmp, bad, 0, 4)).Message);
    }

    [Fact]
    public void FindDirPrefersTheEnvThenBesideTheProgramThenTheSourceTree()
    {
        var project = Path.Combine(_tmp, "proj");
        var bin = Path.Combine(project, "bin", "Debug", "net8.0");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(project, "My.Plugin.csproj"), "<Project />");
        Assert.Null(Ui.FindDir(null, bin));
        // A folder without a manifest doesn't count.
        Directory.CreateDirectory(Path.Combine(project, "ui", "dist"));
        Assert.Null(Ui.FindDir(null, bin));
        File.WriteAllText(Path.Combine(project, "ui", "dist", "manifest.json"), "{}");
        Assert.Equal(Path.Combine(project, "ui", "dist"), Ui.FindDir(null, bin));
        Assert.Null(Ui.FindDir(Path.Combine(_tmp, "elsewhere"), bin));
        Directory.CreateDirectory(Path.Combine(bin, "ui"));
        File.WriteAllText(Path.Combine(bin, "ui", "manifest.json"), "{}");
        Assert.Equal(Path.Combine(bin, "ui"), Ui.FindDir(null, bin));
        Environment.SetEnvironmentVariable("HC_PLUGIN_UI_DIR", "/somewhere");
        Assert.Equal("/somewhere", Ui.FindDir(null, bin));
    }

    [Fact]
    public void WithUiDirPutsUiOnTheManifestEvenWithNoActions()
    {
        var c = Managed();
        Assert.Same(c, c.WithUiDir(Folder(), "2.0.0"));
        var m = c.BuildManifest()!;
        Assert.Equal("plugin.test", m["plugin_id"]!.GetValue<string>());
        Assert.Empty(m["actions"]!.AsArray());
        Assert.Equal(
            $$"""{"host_api":1,"version":"2.0.0","build":"{{FixtureBuild}}","entry":"index.js","slots":[{"slot":"plugin.page","element":"hc-x-page"}]}""",
            m["ui"]!.ToJsonString());
    }

    [Fact]
    public async Task WithUiDirOnAMissingFolderOffersNoUi()
    {
        var c = Managed();
        c.WithUiDir(Path.Combine(_tmp, "nope"), "1.0.0");
        Assert.Null(c.BuildManifest());
        var resp = await Manage(c, """{"action":"ui_file","request_id":"r1","file":"index.js"}""");
        Assert.Equal(
            """{"request_id":"r1","status":"error","error":"no such file: this plugin serves no UI"}""",
            resp.ToJsonString());
    }

    [Fact]
    public async Task UiFileServesPiecesInBase64()
    {
        var c = Managed();
        var d = Folder();
        var big = new byte[256 * 1024];
        for (var i = 0; i < big.Length; i++) big[i] = (byte)(i % 256);
        File.WriteAllBytes(Path.Combine(d, "big.js"), big);
        c.WithUiDir(d, "1.0.0");

        async Task<JsonObject> Ask(string extra) =>
            await Manage(c, $$"""{"action":"ui_file","request_id":"r1",{{extra}}}""");
        byte[] Bytes(JsonObject r) => Convert.FromBase64String(r["data"]!.GetValue<string>());

        var a = await Ask("\"file\":\"index.js\"");
        Assert.Equal(
            $$"""{"request_id":"r1","status":"ok","data":"{{Convert.ToBase64String(Encoding.UTF8.GetBytes("export {}"))}}","done":true}""",
            a.ToJsonString());
        var b = await Ask("\"file\":\"index.js\",\"offset\":8,\"max\":1");
        Assert.Equal("}", Encoding.UTF8.GetString(Bytes(b)));
        Assert.True(b["done"]!.GetValue<bool>());
        // max defaults to 128 KiB, and is clamped to [1, 160 KiB].
        var cDefault = await Ask("\"file\":\"big.js\"");
        Assert.Equal(131072, Bytes(cDefault).Length);
        Assert.False(cDefault["done"]!.GetValue<bool>());
        var dBig = await Ask("\"file\":\"big.js\",\"max\":1000000000");
        Assert.Equal(163840, Bytes(dBig).Length);
        Assert.False(dBig["done"]!.GetValue<bool>());
        Assert.Single(Bytes(await Ask("\"file\":\"big.js\",\"max\":0")));
        foreach (var bad in new[] { "../manifest.json", "nope.js" })
            Assert.Equal(
                """{"request_id":"r1","status":"error","error":"no such file"}""",
                (await Ask($"\"file\":\"{bad}\"")).ToJsonString());
    }
}
