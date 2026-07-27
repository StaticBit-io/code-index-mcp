using System.Text.Json;

namespace CodeIndex.Server.Tools;

/// <summary>
/// Helpers for wrapping externally sourced content (here: fragments of indexed source code)
/// in "untrusted-content" markers inside MCP tool responses. Downstream agents that follow
/// the standard <c>untrusted-content</c> rule treat anything inside these markers as data,
/// never instructions — a defence against indirect prompt injection where attacker-controlled
/// text embedded in a third-party file tries to override the agent's behaviour.
/// </summary>
/// <remarks>
/// This is a deliberate copy of the <c>Mcp.Auth.ResourceServer.UntrustedContent</c> helper
/// from the <c>staticbit-mcp-auth</c> repository, duplicated here (rather than referenced via
/// package) so this stdio-only, unauthenticated server does not pick up the OAuth
/// resource-server package's JWT/ASP.NET dependency chain. Behaviour is kept byte-identical
/// to the original.
/// <para/>
/// Marker format (newlines are significant):
/// <code>
/// &lt;untrusted-content origin="{origin}"&gt;
/// {content}
/// &lt;/untrusted-content&gt;
/// </code>
/// The <c>origin</c> attribute is HTML-escaped. Any inner <c>&lt;/untrusted-content&gt;</c>
/// substring inside the <c>content</c> argument is defused by inserting a zero-width space
/// so that an attacker cannot prematurely close the wrapper from inside the payload.
/// </remarks>
public static class UntrustedContent
{
    private const string OpenTagPrefix = "<untrusted-content origin=\"";
    private const string OpenTagSuffix = "\">";
    private const string CloseTag = "</untrusted-content>";

    /// <summary>
    /// Defused form of the closing tag — the literal <c>&lt;/untrusted-content&gt;</c>
    /// substring with a zero-width space (U+200B) inserted before the final <c>&gt;</c>
    /// so the marker no longer matches but the bytes are still human-readable.
    /// </summary>
    private const string DefusedCloseTag = "</untrusted-content​>";

    /// <summary>
    /// Wraps <paramref name="content"/> in <c>&lt;untrusted-content&gt;</c> markers so a
    /// downstream agent can distinguish data from instructions.
    /// </summary>
    /// <param name="content">The untrusted text payload. Any inner closing-tag substrings
    /// are defused with a zero-width space.</param>
    /// <param name="origin">Free-form short label describing where the content came from
    /// (e.g. an upstream tool name, a URL host, or a user-supplied id). HTML-escaped for
    /// the attribute value.</param>
    /// <returns>The wrapped string with leading/trailing markers.</returns>
    /// <exception cref="ArgumentNullException">Either argument is <c>null</c>.</exception>
    public static string Wrap(string content, string origin)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(origin);

        string defused = content.Replace(CloseTag, DefusedCloseTag, StringComparison.Ordinal);
        string escapedOrigin = EscapeAttribute(origin);

        return string.Concat(
            OpenTagPrefix,
            escapedOrigin,
            OpenTagSuffix,
            "\n",
            defused,
            "\n",
            CloseTag);
    }

    /// <summary>
    /// Serializes <paramref name="payload"/> to JSON via <see cref="System.Text.Json"/>
    /// and wraps the result in <c>&lt;untrusted-content&gt;</c> markers. Use this when
    /// the data you return to the agent is an object rather than a raw string.
    /// </summary>
    /// <param name="payload">The object to serialize. May be <c>null</c> — serialized as <c>"null"</c>.</param>
    /// <param name="origin">Origin label, see <see cref="Wrap(string, string)"/>.</param>
    /// <returns>The wrapped JSON string.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="origin"/> is <c>null</c>.</exception>
    public static string WrapJson(object? payload, string origin)
    {
        ArgumentNullException.ThrowIfNull(origin);

        string json = JsonSerializer.Serialize(payload, JsonOptions);
        return Wrap(json, origin);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private static string EscapeAttribute(string value)
    {
        // Manual HTML attribute escape — avoids pulling in System.Web for a tiny helper.
        // Order matters: ampersand first so we do not re-escape escapes.
        if (value.Length == 0)
        {
            return string.Empty;
        }

        System.Text.StringBuilder sb = new(value.Length);
        foreach (char c in value)
        {
            switch (c)
            {
                case '&':
                    sb.Append("&amp;");
                    break;
                case '"':
                    sb.Append("&quot;");
                    break;
                case '<':
                    sb.Append("&lt;");
                    break;
                case '>':
                    sb.Append("&gt;");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
