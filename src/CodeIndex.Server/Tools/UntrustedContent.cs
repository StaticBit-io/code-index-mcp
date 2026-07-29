using System.Security.Cryptography;
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
/// resource-server package's JWT/ASP.NET dependency chain.
/// <para/>
/// <b>This copy has diverged from the original on purpose.</b> The original scheme wrapped
/// content between a fixed <c>&lt;untrusted-content origin="..."&gt;</c> / <c>&lt;/untrusted-content&gt;</c>
/// pair and "defused" any literal closing-tag substring already inside the content by
/// inserting a zero-width space (U+200B) before the final <c>&gt;</c>. That defusing was
/// proven bypassable: it was a single exact, case-sensitive <see cref="string.Replace(string, string)"/>,
/// so <c>&lt;/untrusted-content &gt;</c> (extra space — still a valid XML end tag), or
/// <c>&lt;/Untrusted-Content&gt;</c> (different case), or the literal defused form itself
/// (<c>&lt;/untrusted-content​&gt;</c>, which does not visually render any differently
/// from the genuine marker) all passed through unmodified and could close the wrapper early.
/// The opening marker was not defused at all either, so attacker-controlled text could also
/// forge an apparent *exit* from untrusted context. Defusing a marker that indexed source can
/// predict is not a sound approach at all — no amount of extra <c>Replace</c> calls fixes that.
/// <para/>
/// Marker format now (newlines are significant):
/// <code>
/// &lt;untrusted-content id="{nonce}" origin="{origin}"&gt;
/// {content}
/// &lt;/untrusted-content id="{nonce}"&gt;
/// </code>
/// Content-independent unguessability replaces defusing: <c>{nonce}</c> is a
/// fresh cryptographically random value generated per call (see <see cref="GenerateNonce"/>) and
/// embedded in both markers. Indexed source cannot know the nonce in advance, so it cannot forge
/// a closing (or opening) marker regardless of case, whitespace, or hidden Unicode — there is
/// nothing for it to copy. The <c>origin</c> attribute is HTML-escaped, including line breaks and
/// tabs as numeric character references (a path on a Linux filesystem may legally contain a
/// newline, which would otherwise split the opening marker itself across lines).
/// </remarks>
public static class UntrustedContent
{
    /// <summary>
    /// Byte length of the random nonce embedded in each pair of markers (see
    /// <see cref="GenerateNonce"/>). 12 bytes (96 bits) is comfortably beyond any
    /// brute-force or coincidental-collision concern while staying short enough that the
    /// hex-encoded marker stays readable.
    /// </summary>
    private const int NonceByteLength = 12;

    /// <summary>
    /// Upper bound on attempts to draw a nonce that does not already appear verbatim inside
    /// the content passed to <see cref="Wrap"/>. Each attempt is an
    /// independent 96-bit draw, so the probability of exhausting this budget against any
    /// finite content is astronomically small; it exists only so a pathological caller
    /// cannot spin forever instead of the practically-impossible case being silently ignored.
    /// </summary>
    private const int MaxNonceAttempts = 10;

    /// <summary>
    /// Wraps <paramref name="content"/> in <c>&lt;untrusted-content&gt;</c> markers carrying a
    /// fresh random nonce so a downstream agent can distinguish data from instructions and
    /// indexed source cannot forge either marker.
    /// </summary>
    /// <param name="content">The untrusted text payload, embedded verbatim — there is nothing
    /// to defuse because the marker it would need to forge is unguessable per call.</param>
    /// <param name="origin">Free-form short label describing where the content came from
    /// (e.g. an upstream tool name, a URL host, or a user-supplied id). HTML-escaped for
    /// the attribute value.</param>
    /// <returns>The wrapped string with leading/trailing markers.</returns>
    /// <exception cref="ArgumentNullException">Either argument is <c>null</c>.</exception>
    public static string Wrap(string content, string origin)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(origin);

        string nonce = GenerateNonceAvoiding(content);
        string escapedOrigin = EscapeAttribute(origin);

        return string.Concat(
            "<untrusted-content id=\"", nonce, "\" origin=\"", escapedOrigin, "\">",
            "\n",
            content,
            "\n",
            "</untrusted-content id=\"", nonce, "\">");
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

    /// <summary>
    /// Draws a random nonce (see <see cref="GenerateNonce"/>), retrying on the
    /// astronomically unlikely chance that <paramref name="content"/> already contains that
    /// exact 96-bit value as a literal substring. Regenerating — rather than trying to
    /// "defuse" that one coincidental occurrence — keeps the invariant simple and total: the
    /// nonce embedded in this call's markers never appears anywhere inside this call's
    /// content, full stop, so there is no forged-marker case left to reason about.
    /// </summary>
    private static string GenerateNonceAvoiding(string content)
    {
        for (int attempt = 0; attempt < MaxNonceAttempts; attempt++)
        {
            string candidate = GenerateNonce();
            if (!content.Contains(candidate, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"Could not draw a {NonceByteLength * 8}-bit nonce absent from the content after " +
            $"{MaxNonceAttempts} attempts. This should be statistically impossible and likely " +
            "indicates a broken random number generator.");
    }

    /// <summary>Cryptographically random, lowercase-hex nonce, <see cref="NonceByteLength"/> bytes.</summary>
    private static string GenerateNonce()
    {
        Span<byte> bytes = stackalloc byte[NonceByteLength];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    private static string EscapeAttribute(string value)
    {
        // Manual HTML attribute escape — avoids pulling in System.Web for a tiny helper.
        // Order matters: ampersand first so we do not re-escape escapes. \n/\r/\t are escaped
        // as numeric character references rather than left raw: a file path on a Linux
        // filesystem may legally contain a newline or tab, and a raw one here would split the
        // opening marker itself across lines instead of merely being unusual attribute content.
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
                case '\n':
                    sb.Append("&#10;");
                    break;
                case '\r':
                    sb.Append("&#13;");
                    break;
                case '\t':
                    sb.Append("&#9;");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
