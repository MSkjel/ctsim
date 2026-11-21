using System;
using System.Globalization;
using System.Text;
using CicodeSim.Values;
using CicodeSim.Vm;
using WQuality = CicodeSim.World.Quality;

namespace CicodeSim.Sim
{
    /// <summary>VM values as the protocol and the debugger show them.</summary>
    public static class ValueFormat
    {
        /// <summary>The Cicode type name of a VM type code: INT (any integer class), REAL,
        /// STRING, OBJECT, TIMESTAMP, QUALITY, VOID.</summary>
        public static string TypeName(int t)
        {
            switch (t)
            {
                case Types.String:
                case Types.Byte:
                    return "STRING";
                case Types.Object:
                    return "OBJECT";
                case Types.Timestamp:
                    return "TIMESTAMP";
                case Types.Quality:
                    return "QUALITY";
                case Types.Void:
                    return "VOID";
                case Types.Variant:
                    return "VARIANT";
            }
            return Types.IsRealType(t) ? "REAL" : "INT";
        }

        /// <summary>A value as JSON: an integer, a number, a string; a TIMESTAMP its ISO 8601
        /// UTC text, a QUALITY its name; VOID null.</summary>
        public static JsonValue Json(in Value v)
        {
            if (v.T == Types.Variant)
            {
                // a VARIANT that refers to nothing (a NULL VARIANT: DLLCallEx of a bad handle) is null
                Value d = Value.Deref(v);
                return d.T == Types.Variant ? JsonValue.Null : Json(d);
            }
            switch (v.T)
            {
                case Types.String:
                case Types.Byte:
                    return JsonValue.Of(v.S ?? "");
                case Types.Void:
                    return JsonValue.Null;
                case Types.Timestamp:
                    return JsonValue.Of(IsoFiletime(unchecked((ulong)v.I)));
                case Types.Quality:
                    return JsonValue.Of(WQuality.Name(unchecked((ulong)v.I)));
                case Types.ULong:
                    return JsonValue.Of(unchecked((uint)v.I));
            }
            if (Types.IsRealType(v.T))
                return JsonValue.Of(v.D);
            return JsonValue.Of(v.I);
        }

        /// <summary>A value as the debugger shows it: a number, a quoted string (^" for a
        /// quote, ^n etc. for control characters), a TIMESTAMP as ISO text, a QUALITY as its
        /// name; ' {quality}' after a value whose quality is not good.</summary>
        public static string Display(in Value v)
        {
            if (v.T == Types.Variant)
            {
                Value d = Value.Deref(v);
                return d.T == Types.Variant ? "" : Display(d);
            }
            string s;
            switch (v.T)
            {
                case Types.String:
                case Types.Byte:
                    s = Quote(v.S ?? "");
                    break;
                case Types.Void:
                    s = "";
                    break;
                case Types.Timestamp:
                    s = IsoFiletime(unchecked((ulong)v.I));
                    if (s.Length == 0)
                        s = "0";
                    break;
                case Types.Quality:
                    s = WQuality.Name(unchecked((ulong)v.I));
                    break;
                case Types.ULong:
                    s = unchecked((uint)v.I).ToString(CultureInfo.InvariantCulture);
                    break;
                default:
                    s = Types.IsRealType(v.T) ? Real(v.D) : v.I.ToString(CultureInfo.InvariantCulture);
                    break;
            }
            if (v.T != Types.Void && v.Q != WQuality.QualityGood && v.T != Types.Quality)
                s += " {" + WQuality.Name(v.Q) + "}";
            return s;
        }

        /// <summary>A REAL as Cicode converts it to STRING ('%.15G': 6.0 is "6") when that text
        /// reads back as the same double, else its shortest round-trip form.</summary>
        public static string Real(double d)
        {
            string g = Crt.FormatG15(d);
            if (
                FloatText.TryParseNumber(g, out double back)
                && (back == d || (double.IsNaN(back) && double.IsNaN(d)))
            )
                return g;
            return FloatText.Shortest(d);
        }

        /// <summary>A Cicode string literal.</summary>
        public static string Quote(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"':
                        sb.Append("^\"");
                        break;
                    case '^':
                        sb.Append("^^");
                        break;
                    case '\n':
                        sb.Append("^n");
                        break;
                    case '\r':
                        sb.Append("^r");
                        break;
                    case '\t':
                        sb.Append("^t");
                        break;
                    case '\0':
                        sb.Append("^0");
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        /// <summary>ISO 8601 UTC with milliseconds of a FILETIME ('' for 0 or out of range).</summary>
        public static string IsoFiletime(ulong ft)
        {
            if (ft == 0 || ft > (ulong)DateTime.MaxValue.ToFileTimeUtc())
                return "";
            try
            {
                return DateTime
                    .FromFileTimeUtc((long)ft)
                    .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            }
            catch (ArgumentException)
            {
                return "";
            }
        }

        /// <summary>A value typed by a user (SetVariable, setTag text): a quoted Cicode string,
        /// an integer, a real, TRUE / FALSE; else the text as a STRING. Null for nothing.</summary>
        public static Value Parse(string text, out bool isString)
        {
            string s = (text ?? "").Trim();
            isString = false;
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
            {
                isString = true;
                var sb = new StringBuilder();
                for (int i = 1; i < s.Length - 1; i++)
                {
                    char c = s[i];
                    if (c == '^' && i + 1 < s.Length - 1)
                    {
                        char e = s[++i];
                        switch (char.ToLowerInvariant(e))
                        {
                            case 'n':
                                sb.Append('\n');
                                break;
                            case 'r':
                                sb.Append('\r');
                                break;
                            case 't':
                                sb.Append('\t');
                                break;
                            case '0':
                                sb.Append('\0');
                                break;
                            default:
                                sb.Append(e);
                                break;
                        }
                        continue;
                    }
                    sb.Append(c);
                }
                return Value.Str(sb.ToString());
            }
            if (string.Equals(s, "TRUE", StringComparison.OrdinalIgnoreCase))
                return Value.Long(1);
            if (string.Equals(s, "FALSE", StringComparison.OrdinalIgnoreCase))
                return Value.Long(0);
            if (
                long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l)
                && l >= int.MinValue
                && l <= int.MaxValue
            )
                return Value.Long((int)l);
            if (
                s.Length > 0
                && FloatText.TryParseNumber(s, out double d)
                && (char.IsDigit(s[s.Length - 1]) || s.EndsWith(".", StringComparison.Ordinal))
            )
                return Value.Real(d);
            isString = true;
            return Value.Str(s);
        }
    }
}
