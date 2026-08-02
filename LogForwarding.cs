using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HomeCore.PluginSdk;

/// <summary>
/// Forwards this plugin's logs to homeCore, so they appear in the live log
/// stream alongside core's own rather than only in the plugin's stdout.
/// </summary>
/// <remarks>
/// <para>
/// Lines are published to <c>homecore/plugins/{plugin_id}/logs</c> as JSON, at
/// QoS 0 and not retained: logs are a stream, and a plugin that reconnects
/// should not replay its last line as though it were new.
/// </para>
/// <para>
/// <b>Secrets.</b> That topic is one anything can subscribe to, so field values
/// whose <i>names</i> look secret are replaced with <c>&lt;redacted&gt;</c>.
/// The field is still emitted, so the shape of the line is preserved — only the
/// value is masked. Only names go through the filter; the formatted message is
/// published as-is, which is why the convention is to pass secrets as named
/// fields rather than interpolating them into the message.
/// </para>
/// </remarks>
public static class LogForwarding
{
    /// <summary>Replacement emitted for fields whose names match the denylist.</summary>
    public const string Redacted = "<redacted>";

    /// <summary>
    /// Substrings that mark a field as secret, matched case-insensitively.
    /// Substring rather than whole-word on purpose, so <c>api_key</c>,
    /// <c>bot_token</c>, <c>client_secret</c> and <c>auth_header</c> are all
    /// caught. Kept identical to the Rust SDK's list.
    /// </summary>
    private static readonly string[] SecretSubstrings =
    {
        "password", "secret", "token", "key", "psk", "passcode", "credential", "auth",
    };

    public static bool IsSecretField(string name)
    {
        foreach (var s in SecretSubstrings)
            if (name.Contains(s, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// Minimum length of a secret value before it is scrubbed out of the
    /// rendered message.
    /// </summary>
    /// <remarks>
    /// Short values are not worth the risk: a field called <c>key</c> whose
    /// value is <c>"1"</c> would otherwise replace every "1" in the sentence
    /// and turn the line into nonsense. Real credentials are long.
    /// </remarks>
    public const int MinScrubLength = 6;

    /// <summary>
    /// Remove secret values from a rendered message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a .NET-specific necessity. In Rust a field passed as
    /// <c>tracing::info!(api_key = %k, "connecting")</c> never appears in the
    /// message text, so redacting the field is enough. .NET's structured
    /// logging renders every template argument into the message, so
    /// <c>LogInformation("connecting with {ApiKey}", k)</c> puts the secret in
    /// the text as well — and the text is published verbatim.
    /// </para>
    /// <para>
    /// Masking the field but not the message would be worse than not masking at
    /// all, because it looks like protection. So values from secret-named
    /// fields are replaced in the message too.
    /// </para>
    /// <para>
    /// It is still a backstop, not a licence: a secret interpolated into the
    /// string yourself (<c>$"key {k}"</c>) has no field to be recognised by, and
    /// nothing can find it. Do not log secrets.
    /// </para>
    /// </remarks>
    public static string ScrubSecrets(string message, IEnumerable<string>? secretValues)
    {
        if (secretValues is null || string.IsNullOrEmpty(message)) return message;
        foreach (var v in secretValues)
        {
            if (v.Length < MinScrubLength) continue;
            message = message.Replace(v, Redacted, StringComparison.Ordinal);
        }
        return message;
    }

    /// <summary>
    /// The level names homeCore uses. <see cref="LogLevel.Warning"/> is
    /// <c>WARN</c> and <see cref="LogLevel.Critical"/> folds into <c>ERROR</c>,
    /// matching what the other SDKs emit.
    /// </summary>
    public static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "ERROR",
        _ => "INFO",
    };

    /// <summary>Parse a level name as the SDK writes it. Unknown names are Information.</summary>
    public static LogLevel ParseLevel(string name) => name.ToUpperInvariant() switch
    {
        "TRACE" => LogLevel.Trace,
        "DEBUG" => LogLevel.Debug,
        "INFO" or "INFORMATION" => LogLevel.Information,
        "WARN" or "WARNING" => LogLevel.Warning,
        "ERROR" => LogLevel.Error,
        "CRITICAL" or "FATAL" => LogLevel.Critical,
        _ => LogLevel.Information,
    };
}

/// <summary>
/// An <see cref="ILoggerProvider"/> that ships log lines to homeCore.
/// </summary>
/// <remarks>
/// Register it on any logging builder and everything the plugin logs is
/// forwarded, with no separate logging call to remember:
/// <code>
/// using var factory = LoggerFactory.Create(b =>
/// {
///     b.AddConsole();
///     b.AddHomeCore(client, LogLevel.Information);
/// });
/// </code>
/// </remarks>
public sealed class MqttLoggerProvider : ILoggerProvider
{
    private readonly PluginClient _client;
    private readonly LogLevel _minLevel;

    public MqttLoggerProvider(PluginClient client, LogLevel minLevel = LogLevel.Information)
    {
        _client = client;
        _minLevel = minLevel;
    }

    public ILogger CreateLogger(string categoryName) =>
        new MqttLogger(_client, categoryName, _minLevel);

    public void Dispose() { }

    private sealed class MqttLogger : ILogger
    {
        private readonly PluginClient _client;
        private readonly string _category;
        private readonly LogLevel _minLevel;

        internal MqttLogger(PluginClient client, string category, LogLevel minLevel)
        {
            _client = client;
            _category = category;
            _minLevel = minLevel;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= _minLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            JsonObject? fields = null;
            List<string>? secretValues = null;

            // Structured fields, when the state carries them — which it does
            // for the usual `logger.LogInformation("x {Name}", value)` form.
            if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
            {
                foreach (var kv in pairs)
                {
                    // The template itself is not a field; the formatted message
                    // already carries it.
                    if (kv.Key == "{OriginalFormat}") continue;
                    fields ??= new JsonObject();

                    if (LogForwarding.IsSecretField(kv.Key))
                    {
                        fields[kv.Key] = LogForwarding.Redacted;
                        // .NET renders every template argument into the message
                        // text, so masking the field alone would publish the
                        // secret anyway — see ScrubSecrets.
                        var rendered = kv.Value?.ToString();
                        if (!string.IsNullOrEmpty(rendered))
                            (secretValues ??= new List<string>()).Add(rendered);
                    }
                    else
                    {
                        fields[kv.Key] = kv.Value?.ToString();
                    }
                }
            }

            if (exception is not null)
            {
                fields ??= new JsonObject();
                fields["exception"] = exception.ToString();
            }

            var message = LogForwarding.ScrubSecrets(formatter(state, exception), secretValues);

            // Fire and forget: a logging call must never block the caller, and
            // an unreachable broker must never turn a log line into a throw.
            _ = _client.ForwardLogAsync(
                LogForwarding.LevelName(logLevel),
                message,
                target: _category,
                fields: fields);
        }
    }
}

/// <summary>Extension for wiring <see cref="MqttLoggerProvider"/> into a logging builder.</summary>
public static class LoggingBuilderExtensions
{
    /// <summary>Forward this plugin's logs to homeCore's live log stream.</summary>
    public static ILoggingBuilder AddHomeCore(
        this ILoggingBuilder builder,
        PluginClient client,
        LogLevel minLevel = LogLevel.Information)
    {
        // Registered through Services rather than builder.AddProvider(), which
        // lives in Microsoft.Extensions.Logging proper. This SDK depends only
        // on the Abstractions package, and a logging SDK is not a good reason
        // to pull the full implementation into every plugin's dependency tree.
        builder.Services.AddSingleton<ILoggerProvider>(new MqttLoggerProvider(client, minLevel));
        return builder;
    }
}
