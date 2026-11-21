using System.Text;

namespace CicodeSim.Values
{
    /// <summary>A plain-object face for JsonValue.</summary>
    public static class Json
    {
        /// <summary>A JSON text (NaN / Infinity accepted) as plain objects.</summary>
        public static object? Parse(string text) => JsonValue.Parse(text).ToPlain();

        /// <summary>Append a JSON string literal (non-ASCII as \u escapes); null = null.</summary>
        public static void WriteString(StringBuilder sb, string? s)
        {
            if (s == null)
            {
                sb.Append("null");
                return;
            }
            JsonValue.WriteJsonString(sb, s, ensureAscii: true);
        }

        public static string Quote(string? s)
        {
            var sb = new StringBuilder();
            WriteString(sb, s);
            return sb.ToString();
        }
    }
}
