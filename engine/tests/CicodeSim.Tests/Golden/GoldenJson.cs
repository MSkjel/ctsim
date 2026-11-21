using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using CicodeSim.Values;

namespace CicodeSim.Tests.Golden
{
    /// <summary>The canonical text of a golden record.</summary>
    public static class GoldenJson
    {
        /// <summary>An object with its keys in the order given (sorted when written).</summary>
        public sealed class Obj : List<KeyValuePair<string, object?>>
        {
            public Obj Add(string key, object? value)
            {
                Add(new KeyValuePair<string, object?>(key, value));
                return this;
            }
        }

        /// <summary>The bits of a double as golden text ("nan" for every NaN).</summary>
        public static string RealBits(double d) =>
            double.IsNaN(d)
                ? "nan"
                : ((ulong)BitConverter.DoubleToInt64Bits(d)).ToString("x16", CultureInfo.InvariantCulture);

        public static string Write(object? v)
        {
            var sb = new StringBuilder(128);
            Write(sb, v);
            return sb.ToString();
        }

        public static void Write(StringBuilder sb, object? v)
        {
            switch (v)
            {
                case null:
                    sb.Append("null");
                    return;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    return;
                case string s:
                    Str(sb, s);
                    return;
                case double d:
                    sb.Append("{\"r\":\"").Append(RealBits(d)).Append("\"}");
                    return;
                case float f:
                    Write(sb, (double)f);
                    return;
                case int i:
                    sb.Append(i.ToString(CultureInfo.InvariantCulture));
                    return;
                case long l:
                    sb.Append(l.ToString(CultureInfo.InvariantCulture));
                    return;
                case uint ui:
                    sb.Append(ui.ToString(CultureInfo.InvariantCulture));
                    return;
                case ulong ul:
                    sb.Append(ul.ToString(CultureInfo.InvariantCulture));
                    return;
                case short sh:
                    sb.Append(sh.ToString(CultureInfo.InvariantCulture));
                    return;
                case byte by:
                    sb.Append(by.ToString(CultureInfo.InvariantCulture));
                    return;
                case BigInteger bi:
                    sb.Append(bi.ToString(CultureInfo.InvariantCulture));
                    return;
                case JsonValue jv:
                    WriteJson(sb, jv);
                    return;
                case Obj o:
                    WriteObject(sb, o);
                    return;
                case IDictionary dict:
                {
                    var o = new Obj();
                    foreach (DictionaryEntry e in dict)
                        o.Add((string)e.Key, e.Value);
                    WriteObject(sb, o);
                    return;
                }
                case IEnumerable seq:
                {
                    sb.Append('[');
                    bool first = true;
                    foreach (object? x in seq)
                    {
                        if (!first)
                            sb.Append(',');
                        first = false;
                        Write(sb, x);
                    }
                    sb.Append(']');
                    return;
                }
            }
            throw new ArgumentException("no golden text for " + v.GetType().Name);
        }

        private static void WriteObject(StringBuilder sb, List<KeyValuePair<string, object?>> pairs)
        {
            var l = new List<KeyValuePair<string, object?>>(pairs);
            l.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            sb.Append('{');
            for (int i = 0; i < l.Count; i++)
            {
                if (i > 0)
                    sb.Append(',');
                Str(sb, l[i].Key);
                sb.Append(':');
                Write(sb, l[i].Value);
            }
            sb.Append('}');
        }

        private static void WriteJson(StringBuilder sb, JsonValue v)
        {
            switch (v.Kind)
            {
                case JsonKind.Null:
                    sb.Append("null");
                    return;
                case JsonKind.Bool:
                    sb.Append(v.Bool ? "true" : "false");
                    return;
                case JsonKind.Int:
                    sb.Append(v.BigValue.ToString(CultureInfo.InvariantCulture));
                    return;
                case JsonKind.Float:
                    Write(sb, v.FloatValue);
                    return;
                case JsonKind.String:
                    Str(sb, v.Str);
                    return;
                case JsonKind.Array:
                {
                    sb.Append('[');
                    var items = v.Items;
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (i > 0)
                            sb.Append(',');
                        WriteJson(sb, items[i]);
                    }
                    sb.Append(']');
                    return;
                }
                default:
                {
                    var o = new Obj();
                    foreach (var kv in v.Pairs)
                        o.Add(kv.Key, kv.Value);
                    WriteObject(sb, o);
                    return;
                }
            }
        }

        public static void Str(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c < 0x20 || c > 0x7E)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ------------------------------------------------------------------------ hashes
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        /// <summary>The first 16 hex digits of the SHA-256 of the text's UTF-8 bytes.</summary>
        public static string Hash(string text)
        {
            using var sha = SHA256.Create();
            byte[] h = sha.ComputeHash(Utf8.GetBytes(text));
            var sb = new StringBuilder(16);
            for (int i = 0; i < 8; i++)
                sb.Append(h[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        /// <summary>The first 16 hex digits of the SHA-256 of the bytes.</summary>
        public static string HashBytes(byte[] bytes)
        {
            using var sha = SHA256.Create();
            byte[] h = sha.ComputeHash(bytes);
            var sb = new StringBuilder(16);
            for (int i = 0; i < 8; i++)
                sb.Append(h[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        /// <summary>A digest of lines (joined by \n, a final \n) built as they come.</summary>
        public sealed class Digest : IDisposable
        {
            private readonly IncrementalHash _h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            public long Lines { get; private set; }

            public void Add(string line)
            {
                byte[] b = Utf8.GetBytes(line);
                _h.AppendData(b);
                _h.AppendData(Newline);
                Lines++;
            }

            public string Hex()
            {
                byte[] h = _h.GetHashAndReset();
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++)
                    sb.Append(h[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }

            public void Dispose() => _h.Dispose();

            private static readonly byte[] Newline = { 10 };
        }

        /// <summary>The first 8 bytes of the SHA-256 of the text, big-endian: a record's 64-bit hash
        /// for the order-free bucket sums of the large dumps.</summary>
        public static ulong Hash64(SHA256 sha, string text)
        {
            byte[] h = sha.ComputeHash(Utf8.GetBytes(text));
            ulong v = 0;
            for (int i = 0; i < 8; i++)
                v = (v << 8) | h[i];
            return v;
        }
    }
}
