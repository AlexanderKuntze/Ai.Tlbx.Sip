using System.Globalization;
using System.Text;

namespace Ai.Tlbx.Sip;

/// <summary>
/// A party of a call as signalled in a SIP name-addr header (RFC 3261 section 20.10).
/// <see cref="Number"/> is the user part of a sip:/sips: URI or the number of a tel: URI, unescaped and without
/// URI parameters; it is null when the party is anonymous or the URI has no user part.
/// </summary>
public sealed record SipPartyIdentity(string Uri, string? Number, string? DisplayName, bool Anonymous)
{
    /// <summary>Parses one name-addr or addr-spec, for example <c>"Max" &lt;sip:0176123@pbx.example&gt;;tag=x</c>.</summary>
    internal static SipPartyIdentity? Parse(string value)
    {
        value = value.Trim();
        if (value.Length == 0) return null;
        string? display = null; string uri;
        int open = IndexOutsideQuotes(value, '<');
        if (open >= 0)
        {
            int close = value.IndexOf('>', open + 1);
            if (close < 0) return null;
            uri = value[(open + 1)..close].Trim();
            string name = value[..open].Trim();
            if (name.Length >= 2 && name[0] == '"' && name[^1] == '"') name = Unquote(name[1..^1]);
            display = name.Length > 0 ? name : null;
        }
        else
        {
            // addr-spec without angle brackets: header parameters start at the first ';' (RFC 3261 section 20.10).
            int parameters = value.IndexOf(';');
            uri = (parameters >= 0 ? value[..parameters] : value).Trim();
        }
        if (uri.Length == 0) return null;
        string? number = null;
        string scheme = uri.Split(':', 2)[0].ToLowerInvariant();
        string rest = uri.Contains(':') ? uri.Split(':', 2)[1] : "";
        if (scheme is "sip" or "sips")
        {
            int at = rest.IndexOf('@');
            if (at > 0) number = rest[..at].Split(';')[0];
        }
        else if (scheme == "tel") number = rest.Split(';')[0];
        if (number is not null)
        {
            try { number = System.Uri.UnescapeDataString(number); } catch (UriFormatException) { }
            if (number.Length == 0) number = null;
        }
        // RFC 3323 section 4.1.1.3: anonymous parties use the display name and host "Anonymous"/"anonymous.invalid".
        bool anonymous = string.Equals(display, "Anonymous", StringComparison.OrdinalIgnoreCase) ||
            uri.Contains("anonymous.invalid", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(number, "anonymous", StringComparison.OrdinalIgnoreCase);
        return new(uri, anonymous ? null : number, display, anonymous);
    }
    /// <summary>Splits a header value that may contain several comma-separated entries, ignoring commas inside quotes and angle brackets.</summary>
    internal static IEnumerable<string> SplitEntries(string value)
    {
        var current = new StringBuilder(); bool quoted = false; int angle = 0;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (quoted && c == '\\' && i + 1 < value.Length) { current.Append(c).Append(value[++i]); continue; }
            if (c == '"') quoted = !quoted;
            else if (!quoted && c == '<') angle++;
            else if (!quoted && c == '>' && angle > 0) angle--;
            if (c == ',' && !quoted && angle == 0) { if (current.ToString().Trim().Length > 0) yield return current.ToString().Trim(); current.Clear(); continue; }
            current.Append(c);
        }
        if (current.ToString().Trim().Length > 0) yield return current.ToString().Trim();
    }
    /// <summary>The value of a header parameter after the closing angle bracket, for example <c>index</c> in History-Info.</summary>
    internal static string HeaderParameter(string entry, string name)
    {
        int close = entry.LastIndexOf('>');
        foreach (string part in (close >= 0 ? entry[(close + 1)..] : entry).Split(';').Skip(close >= 0 ? 0 : 1))
        {
            var pair = part.Split('=', 2);
            if (pair[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) return pair.Length > 1 ? pair[1].Trim().Trim('"') : "";
        }
        return "";
    }
    /// <summary>Orders History-Info index values like 1, 1.1, 1.2, 2 (RFC 7044 section 10.3).</summary>
    internal static int[] IndexKey(string index) =>
        index.Split('.').Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : int.MaxValue).ToArray();
    internal static int CompareIndex(int[] a, int[] b)
    {
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++) if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        return a.Length.CompareTo(b.Length);
    }
    private static int IndexOutsideQuotes(string value, char wanted)
    {
        bool quoted = false;
        for (int i = 0; i < value.Length; i++)
        {
            if (quoted && value[i] == '\\') { i++; continue; }
            if (value[i] == '"') quoted = !quoted;
            else if (!quoted && value[i] == wanted) return i;
        }
        return -1;
    }
    private static string Unquote(string value)
    {
        var text = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++) text.Append(value[i] == '\\' && i + 1 < value.Length ? value[++i] : value[i]);
        return text.ToString();
    }
}
