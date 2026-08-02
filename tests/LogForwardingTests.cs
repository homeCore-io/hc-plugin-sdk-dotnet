using Microsoft.Extensions.Logging;
using HomeCore.PluginSdk;
using Xunit;

namespace HomeCore.PluginSdk.Tests;

public class LogForwardingTests
{
    [Theory]
    // The point of substring matching: these are the shapes real plugins use.
    [InlineData("password")]
    [InlineData("api_key")]
    [InlineData("bot_token")]
    [InlineData("client_secret")]
    [InlineData("auth_header")]
    [InlineData("PSK")]
    [InlineData("passcode")]
    [InlineData("credential_id")]
    [InlineData("AppKey")]
    public void SecretLookingFieldNamesAreRedacted(string name) =>
        Assert.True(LogForwarding.IsSecretField(name), name);

    [Theory]
    [InlineData("host")]
    [InlineData("device_id")]
    [InlineData("brightness")]
    [InlineData("serial")]
    public void OrdinaryFieldNamesAreNot(string name) =>
        Assert.False(LogForwarding.IsSecretField(name), name);

    [Fact]
    public void LevelNamesMatchWhatTheOtherSdksEmit()
    {
        // WARN not WARNING, and Critical folds into ERROR, so a mixed-language
        // estate filters the log stream on one set of names.
        Assert.Equal("TRACE", LogForwarding.LevelName(LogLevel.Trace));
        Assert.Equal("DEBUG", LogForwarding.LevelName(LogLevel.Debug));
        Assert.Equal("INFO", LogForwarding.LevelName(LogLevel.Information));
        Assert.Equal("WARN", LogForwarding.LevelName(LogLevel.Warning));
        Assert.Equal("ERROR", LogForwarding.LevelName(LogLevel.Error));
        Assert.Equal("ERROR", LogForwarding.LevelName(LogLevel.Critical));
    }

    [Fact]
    public void LevelParsingAcceptsBothSpellings()
    {
        Assert.Equal(LogLevel.Warning, LogForwarding.ParseLevel("warn"));
        Assert.Equal(LogLevel.Warning, LogForwarding.ParseLevel("WARNING"));
        Assert.Equal(LogLevel.Information, LogForwarding.ParseLevel("info"));
        Assert.Equal(LogLevel.Trace, LogForwarding.ParseLevel("TRACE"));
        // An unknown level must not silence the plugin.
        Assert.Equal(LogLevel.Information, LogForwarding.ParseLevel("nonsense"));
    }

    [Fact]
    public void ForwardingIsOffUntilEnabled()
    {
        // Linking the SDK must not start shipping a plugin's logs to a topic
        // anything can subscribe to.
        var c = new PluginClient(new PluginOptions { PluginId = "plugin.test" });
        Assert.Equal(LogLevel.Information, c.LogForwardMinLevel);
        c.EnableLogForwarding(LogLevel.Debug);
        Assert.Equal(LogLevel.Debug, c.LogForwardMinLevel);
    }

    [Fact]
    public async Task ForwardingWhenDisconnectedDoesNotThrow()
    {
        // A logging call must never take down the caller, whatever the broker
        // is doing.
        var c = new PluginClient(new PluginOptions { PluginId = "plugin.test" });
        c.EnableLogForwarding();
        await c.ForwardLogAsync("INFO", "no broker here");
    }

    [Fact]
    public void ProviderCreatesALoggerHonouringTheMinimumLevel()
    {
        var c = new PluginClient(new PluginOptions { PluginId = "plugin.test" });
        var logger = new MqttLoggerProvider(c, LogLevel.Warning).CreateLogger("cat");
        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
        Assert.False(logger.IsEnabled(LogLevel.None));
    }

    [Fact]
    public void LoggingThroughTheProviderDoesNotThrowWhenDisconnected()
    {
        var c = new PluginClient(new PluginOptions { PluginId = "plugin.test" });
        c.EnableLogForwarding();
        var logger = new MqttLoggerProvider(c).CreateLogger("cat");
        // Includes a secret-looking field, so the redaction path is exercised.
        logger.LogInformation("connecting with {ApiKey} to {Host}", "s3cret", "10.0.0.1");
    }
}

public class SecretScrubbingTests
{
    [Fact]
    public void SecretValuesAreRemovedFromTheRenderedMessage()
    {
        // .NET renders template arguments into the message, so masking only the
        // field would publish the secret anyway.
        var scrubbed = LogForwarding.ScrubSecrets(
            "connecting with s3cr3t-value to 10.0.0.42",
            new[] { "s3cr3t-value" });
        Assert.DoesNotContain("s3cr3t-value", scrubbed);
        Assert.Contains(LogForwarding.Redacted, scrubbed);
        // Non-secret context survives.
        Assert.Contains("10.0.0.42", scrubbed);
    }

    [Fact]
    public void ShortValuesAreLeftAlone()
    {
        // A field called `key` whose value is "1" would otherwise replace every
        // "1" in the sentence and turn the line into nonsense.
        const string msg = "retry 1 of 3 after 1s";
        Assert.Equal(msg, LogForwarding.ScrubSecrets(msg, new[] { "1" }));
    }

    [Fact]
    public void EveryOccurrenceGoes()
    {
        var scrubbed = LogForwarding.ScrubSecrets(
            "token abcdef1234 rejected; retrying with abcdef1234",
            new[] { "abcdef1234" });
        Assert.DoesNotContain("abcdef1234", scrubbed);
    }

    [Fact]
    public void NothingToScrubIsANoOp()
    {
        const string msg = "all fine";
        Assert.Equal(msg, LogForwarding.ScrubSecrets(msg, null));
        Assert.Equal(msg, LogForwarding.ScrubSecrets(msg, Array.Empty<string>()));
    }
}
