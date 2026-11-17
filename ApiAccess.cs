using System.Text;

namespace HomeCore.PluginSdk;

/// <summary>
/// REST access homeCore granted this plugin with the <c>api_access</c>
/// management action: where its API is, the key to use, and what the key may
/// do.
/// </summary>
/// <remarks>
/// Only a plugin whose manifest declares <see cref="Capabilities.ApiScopes"/>
/// is sent one. The key is minted fresh on every plugin start and kept only in
/// memory; it never appears in <see cref="ToString"/>, so it cannot leak into
/// a log line.
/// </remarks>
/// <param name="Url">
/// homeCore's REST base this plugin can reach, e.g.
/// <c>http://127.0.0.1:8080/api/v1</c>. No trailing slash.
/// </param>
/// <param name="Key">Bearer key (<c>hc_sk_...</c>).</param>
/// <param name="Scopes">Scopes the key carries. Can be fewer than the manifest asked for.</param>
public sealed record ApiAccess(string Url, string Key, IReadOnlyList<string> Scopes)
{
    // The record's generated ToString prints every property. This one leaves
    // the key out.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Url = ").Append(Url);
        builder.Append(", Scopes = [").Append(string.Join(", ", Scopes)).Append(']');
        return true;
    }
}
