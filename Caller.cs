using System.Text.Json;

namespace HomeCore.PluginSdk;

/// <summary>
/// Who made an action request, as homeCore verified them.
/// </summary>
/// <remarks>
/// homeCore puts this in every action's management payload as <c>caller</c>.
/// The SDK takes it out of the params and hands it to the handler on its own:
/// <see cref="PluginClient.OnActionWithCaller"/> for an immediate action and
/// <see cref="StreamContext.Caller"/> for a streaming one. It is null when the
/// homeCore that sent the request is too old to say.
/// </remarks>
/// <param name="Uid">The account's id.</param>
/// <param name="Username">The account's name.</param>
/// <param name="Role">The account's role (<c>admin</c>, <c>user</c>, <c>read_only</c>, ...).</param>
/// <param name="Scopes">What the caller may do in homeCore's API.</param>
/// <param name="Kind">
/// How the caller signed in: <c>user</c>, <c>api_key</c>, <c>local_admin</c>
/// or <c>whitelist</c>.
/// </param>
public sealed record Caller(
    string Uid,
    string Username,
    string Role,
    IReadOnlyList<string> Scopes,
    string Kind)
{
    /// <summary>
    /// Read the <c>caller</c> object from a management payload. Null when it is
    /// absent or is not an object. Missing fields read as empty.
    /// </summary>
    internal static Caller? FromCommand(JsonElement cmd)
    {
        if (cmd.ValueKind != JsonValueKind.Object
            || !cmd.TryGetProperty("caller", out var c)
            || c.ValueKind != JsonValueKind.Object)
            return null;

        return new Caller(
            Text(c, "uid"),
            Text(c, "username"),
            Text(c, "role"),
            c.TryGetProperty("scopes", out var s) ? Strings(s) : Array.Empty<string>(),
            Text(c, "kind"));
    }

    private static string Text(JsonElement o, string key) =>
        !o.TryGetProperty(key, out var v) ? ""
        : v.ValueKind == JsonValueKind.String ? v.GetString()!
        : v.ValueKind == JsonValueKind.Null ? ""
        : v.GetRawText();

    /// <summary>A JSON array as strings; anything else is empty.</summary>
    internal static IReadOnlyList<string> Strings(JsonElement v) =>
        v.ValueKind != JsonValueKind.Array
            ? Array.Empty<string>()
            : v.EnumerateArray()
                .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : x.GetRawText())
                .ToList();
}
