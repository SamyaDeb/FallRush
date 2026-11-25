using System.Text;

namespace FallRush.OnChain
{
    /// <summary>harden #13: JSON string escaping for the hand-built join messages (a player name with a quote, a
    /// backslash or a control character broke the JSON, so the join failed and the player sat on a frozen hold).</summary>
    public static class ChainJson
    {
        /// <summary>The contents of a JSON string literal (no surrounding quotes) for <paramref name="s"/>: quotes and
        /// backslashes escaped, control characters as <c>\uXXXX</c>, lone surrogates replaced (they are not valid UTF-8).</summary>
        public static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;
            var sb = new StringBuilder(s.Length + 8);
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '"' || c == '\\')
                    sb.Append('\\').Append(c);
                else if (c < 0x20 || c == 0x7f || c == '\u2028' || c == '\u2029')
                    sb.Append("\\u").Append(((int)c).ToString("x4"));
                else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                    sb.Append(c).Append(s[++i]);
                else if (char.IsSurrogate(c))
                    sb.Append('\uFFFD');
                else
                    sb.Append(c);
            }

            return sb.ToString();
        }

        /// <summary>The join name as both transports send it: escaped, "Rushling" when empty.</summary>
        public static string JoinName(string name)
        {
            var n = Escape(name);
            return n.Length == 0 ? "Rushling" : n;
        }
    }
}
