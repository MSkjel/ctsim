using System;
using System.Runtime.InteropServices;
using System.Text;

namespace CicodeSim.Values
{
    /// <summary>The C runtime functions the runtime uses for its conversions and formats.</summary>
    public static class Crt
    {
        /// <summary>C isspace in the "C" locale: space, \t, \n, \v, \f, \r.</summary>
        public static bool IsSpace(char ch) => ch == ' ' || (ch >= '\t' && ch <= '\r');

        private static bool IsDigit(char c) => c >= '0' && c <= '9';

        private static bool IsHex(char c) =>
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

        // ------------------------------------------------------------------ atol
        /// <summary>atol: leading white space, an optional sign, decimal digits; out of range
        /// clamps to LONG_MIN / LONG_MAX (StrToInt("2147483648") = 2147483647); no digits
        /// = 0.</summary>
        public static long Atol(string s)
        {
            int i = 0,
                n = s.Length;
            while (i < n && IsSpace(s[i]))
                i++;
            bool neg = false;
            if (i < n && (s[i] == '+' || s[i] == '-'))
            {
                neg = s[i] == '-';
                i++;
            }
            int j = i;
            long v = 0;
            bool big = false;
            while (j < n && s[j] >= '0' && s[j] <= '9')
            {
                if (!big)
                {
                    v = v * 10 + (s[j] - '0');
                    if (v > 0x80000000L)
                        big = true;
                }
                j++;
            }
            if (j == i)
                return 0;
            if (big)
                return neg ? -0x80000000L : 0x7FFFFFFFL;
            if (neg)
                v = -v;
            if (v > 0x7FFFFFFF)
                return 0x7FFFFFFF;
            if (v < -0x80000000L)
                return -0x80000000L;
            return v;
        }

        // ------------------------------------------------------------------ strtod
        /// <summary>Length of a match of [+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)? at i (0 = none).</summary>
        private static int MatchDec(string s, int i)
        {
            int p = i,
                n = s.Length;
            if (p < n && (s[p] == '+' || s[p] == '-'))
                p++;
            int d0 = p;
            while (p < n && IsDigit(s[p]))
                p++;
            if (p > d0)
            {
                if (p < n && s[p] == '.')
                {
                    p++;
                    while (p < n && IsDigit(s[p]))
                        p++;
                }
            }
            else
            {
                if (p < n && s[p] == '.' && p + 1 < n && IsDigit(s[p + 1]))
                {
                    p += 2;
                    while (p < n && IsDigit(s[p]))
                        p++;
                }
                else
                    return 0;
            }
            if (p < n && (s[p] == 'e' || s[p] == 'E'))
            {
                int q = p + 1;
                if (q < n && (s[q] == '+' || s[q] == '-'))
                    q++;
                int e0 = q;
                while (q < n && IsDigit(s[q]))
                    q++;
                if (q > e0)
                    p = q;
            }
            return p - i;
        }

        /// <summary>Length of a match of [+-]?0[xX]([0-9a-fA-F]+\.?[0-9a-fA-F]*|\.[0-9a-fA-F]+)([pP][+-]?\d+)?.</summary>
        private static int MatchHex(string s, int i)
        {
            int p = i,
                n = s.Length;
            if (p < n && (s[p] == '+' || s[p] == '-'))
                p++;
            if (!(p + 1 < n && s[p] == '0' && (s[p + 1] == 'x' || s[p + 1] == 'X')))
                return 0;
            p += 2;
            int h0 = p;
            while (p < n && IsHex(s[p]))
                p++;
            if (p > h0)
            {
                if (p < n && s[p] == '.')
                {
                    p++;
                    while (p < n && IsHex(s[p]))
                        p++;
                }
            }
            else
            {
                if (p < n && s[p] == '.' && p + 1 < n && IsHex(s[p + 1]))
                {
                    p += 2;
                    while (p < n && IsHex(s[p]))
                        p++;
                }
                else
                    return 0;
            }
            if (p < n && (s[p] == 'p' || s[p] == 'P'))
            {
                int q = p + 1;
                if (q < n && (s[q] == '+' || s[q] == '-'))
                    q++;
                int e0 = q;
                while (q < n && IsDigit(s[q]))
                    q++;
                if (q > e0)
                    p = q;
            }
            return p - i;
        }

        private static bool AtCI(string s, int p, string w)
        {
            if (p + w.Length > s.Length)
                return false;
            for (int k = 0; k < w.Length; k++)
            {
                char c = s[p + k];
                if (c >= 'A' && c <= 'Z')
                    c = (char)(c + 32);
                if (c != w[k])
                    return false;
            }
            return true;
        }

        private static bool IsWordChar(char c) =>
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';

        /// <summary>Length of [+-]?(inf(inity)?|nan(\([0-9A-Za-z_]*\))?) case-insensitive at i.</summary>
        private static int MatchSpecial(string s, int i, out bool isNan)
        {
            isNan = false;
            int p = i;
            if (p < s.Length && (s[p] == '+' || s[p] == '-'))
                p++;
            if (AtCI(s, p, "inf"))
            {
                p += 3;
                if (AtCI(s, p, "inity"))
                    p += 5;
                return p - i;
            }
            if (AtCI(s, p, "nan"))
            {
                isNan = true;
                p += 3;
                if (p < s.Length && s[p] == '(')
                {
                    int q = p + 1;
                    while (q < s.Length && IsWordChar(s[q]))
                        q++;
                    if (q < s.Length && s[q] == ')')
                        p = q + 1;
                }
                return p - i;
            }
            return 0;
        }

        /// <summary>strtod: (value, characters consumed). Decimal, hexadecimal (0x1.8p3) and
        /// INF / INFINITY / NAN[(chars)] forms after leading white space, correctly rounded;
        /// beyond the largest double = an infinity; nothing parsed = (0.0, 0).</summary>
        public static double Strtod(string s, out int used)
        {
            int i = 0,
                n = s.Length;
            while (i < n && IsSpace(s[i]))
                i++;
            int m = MatchHex(s, i);
            if (m > 0)
            {
                string txt = s.Substring(i, m);
                bool neg = txt[0] == '-';
                string body = txt.TrimStart('+', '-');
                if (body.IndexOf('p') < 0 && body.IndexOf('P') < 0)
                    body += "p0";
                double v = FloatText.ParseHex(body, out _);
                used = i + m;
                return neg ? -v : v;
            }
            m = MatchDec(s, i);
            if (m > 0)
            {
                used = i + m;
                return FloatText.ParseDecimal(s, i, i + m);
            }
            m = MatchSpecial(s, i, out bool isNan);
            if (m > 0)
            {
                used = i + m;
                bool neg = s[i] == '-';
                if (!isNan)
                    return neg ? double.NegativeInfinity : double.PositiveInfinity;
                return NanBits(s.Substring(i, m), neg);
            }
            used = 0;
            return 0.0;
        }

        /// <summary>The NaN the runtime's strtod returns.</summary>
        private static double NanBits(string text, bool neg)
        {
            int p = text.IndexOf('(');
            string inner = p < 0 ? "" : text.Substring(p + 1, text.Length - p - 2).ToLowerInvariant();
            if (inner == "ind")
                return BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8000000000000UL));
            ulong bits = inner == "snan" ? 0x7FF8000000000001UL : 0x7FFFFFFFFFFFFFFFUL;
            if (neg)
                bits |= 0x8000000000000000UL;
            return BitConverter.Int64BitsToDouble(unchecked((long)bits));
        }

        // ------------------------------------------------------------------ sprintf
        /// <summary>"%.15G": the runtime's REAL to STRING (C formatting in
        /// the "English" locale).</summary>
        public static string FormatG15(double x)
        {
            if (x == Math.Floor(x) && Math.Abs(x) < 1e15)
            {
                // an integer of at most 15 digits prints as itself
                if (x == 0.0)
                    return BitConverter.DoubleToInt64Bits(x) < 0 ? "-0" : "0";
                return ((long)x).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return FloatText.Format(x, 'G', 15, false);
        }

        /// <summary>The %a conversion.</summary>
        public static string HexFloat(double x)
        {
            if (double.IsNaN(x))
                return FloatText.NanText(x, false);
            if (double.IsInfinity(x))
                return x < 0 ? "-inf" : "inf";
            FloatText.Decompose(x, out bool neg, out ulong mant, out _);
            string sign = neg ? "-" : "";
            if (mant == 0)
                return sign + "0x0.0p+0";
            long bits = BitConverter.DoubleToInt64Bits(x);
            int be = (int)((bits >> 52) & 0x7FF);
            ulong frac = (ulong)bits & 0xFFFFFFFFFFFFFUL;
            string lead = be == 0 ? "0" : "1";
            int e = be == 0 ? -1022 : be - 1023;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            return sign
                + "0x"
                + lead
                + "."
                + frac.ToString("x13", ci)
                + "p"
                + (e < 0 ? "-" : "+")
                + Math.Abs(e).ToString(ci);
        }

        private static readonly string[] LenMods =
        {
            "hh",
            "h",
            "ll",
            "l",
            "L",
            "I64",
            "I32",
            "I",
            "j",
            "z",
            "t",
            "w",
        };
        private const string ConvChars = "diouxXcsSfFeEgGaAp%";

        /// <summary>sprintf with one value.</summary>
        public static string Sprintf(string fmt, PrintfArg value)
        {
            var sb = new StringBuilder(fmt.Length + 16);
            int pos = 0;
            bool used = false;
            int n = fmt.Length;
            int i = 0;
            while (i < n)
            {
                if (fmt[i] != '%')
                {
                    i++;
                    continue;
                }
                if (
                    !MatchSpec(
                        fmt,
                        i,
                        out int end,
                        out string flags,
                        out string width,
                        out string? prec,
                        out char conv
                    )
                )
                {
                    i++;
                    continue;
                }
                sb.Append(fmt, pos, i - pos);
                pos = end;
                i = end;
                if (conv == '%')
                {
                    sb.Append('%');
                    continue;
                }
                if (used)
                    continue;
                used = true;
                if (width == "*" || prec == "*")
                    continue;
                string spec =
                    "%" + flags + width + (prec == null ? "" : "." + (prec.Length > 0 ? prec : "0"));
                sb.Append(One(spec, conv, value));
            }
            sb.Append(fmt, pos, n - pos);
            return sb.ToString();
        }

        /// <summary>A conversion spec starting at i (a '%'): flags, a width or '*', a precision
        /// (tried first) or none, a length modifier (the longest of LenMods that a conversion
        /// letter follows) or none, then the conversion letter.</summary>
        private static bool MatchSpec(
            string f,
            int i,
            out int end,
            out string flags,
            out string width,
            out string? prec,
            out char conv
        )
        {
            int n = f.Length;
            int p = i + 1;
            int f0 = p;
            while (p < n && "-+ #0".IndexOf(f[p]) >= 0)
                p++;
            flags = f.Substring(f0, p - f0);
            if (p < n && f[p] == '*')
            {
                width = "*";
                p++;
            }
            else
            {
                int w0 = p;
                while (p < n && IsDigit(f[p]))
                    p++;
                width = f.Substring(w0, p - w0);
            }
            prec = null;
            int afterWidth = p;
            // with the precision part, then without it
            for (int withPrec = 1; withPrec >= 0; withPrec--)
            {
                p = afterWidth;
                string? pr = null;
                if (withPrec == 1)
                {
                    if (!(p < n && f[p] == '.'))
                        continue;
                    p++;
                    if (p < n && f[p] == '*')
                    {
                        pr = "*";
                        p++;
                    }
                    else
                    {
                        int q0 = p;
                        while (p < n && IsDigit(f[p]))
                            p++;
                        pr = f.Substring(q0, p - q0);
                    }
                }
                foreach (string lm in LenMods)
                {
                    if (
                        string.CompareOrdinal(f, p, lm, 0, lm.Length) == 0
                        && p + lm.Length < n
                        && ConvChars.IndexOf(f[p + lm.Length]) >= 0
                    )
                    {
                        conv = f[p + lm.Length];
                        end = p + lm.Length + 1;
                        prec = pr;
                        return true;
                    }
                }
                if (p < n && ConvChars.IndexOf(f[p]) >= 0)
                {
                    conv = f[p];
                    end = p + 1;
                    prec = pr;
                    return true;
                }
            }
            end = 0;
            conv = '\0';
            return false;
        }

        private static string One(string spec, char conv, PrintfArg value)
        {
            if ("diouxX".IndexOf(conv) >= 0)
            {
                long iv;
                if (value.Kind == PrintfArg.KFloat)
                    iv =
                        double.IsNaN(value.D) || double.IsInfinity(value.D)
                            ? 0
                            : FloatText.TruncLow64(value.D);
                else if (value.Kind == PrintfArg.KInt)
                    iv = value.I;
                else
                    iv = 0;
                iv &= 0xFFFFFFFFL;
                if (conv == 'd' || conv == 'i')
                    return Printf.Format(spec + "d", PrintfArg.Int((int)iv));
                return Printf.Format(spec + conv, PrintfArg.Int(iv));
            }
            if ("fFeEgGaA".IndexOf(conv) >= 0)
            {
                double x =
                    value.Kind == PrintfArg.KFloat ? value.D
                    : value.Kind == PrintfArg.KInt ? value.IntAsDouble
                    : 0.0;
                if (conv == 'a' || conv == 'A')
                    return HexFloat(x);
                return Printf.Format(spec + conv, PrintfArg.Float(x));
            }
            if (conv == 's' || conv == 'S')
                return Printf.Format(
                    spec + "s",
                    PrintfArg.Str(value.Kind == PrintfArg.KStr ? value.S ?? "" : "")
                );
            if (conv == 'c')
            {
                if (value.Kind == PrintfArg.KInt)
                    return Printf.Format(spec + "c", PrintfArg.Str(((char)(value.I & 0xFF)).ToString()));
                return "";
            }
            if (conv == 'p')
                return "00000000";
            return "";
        }

        // ------------------------------------------------------------------ time
        [StructLayout(LayoutKind.Sequential)]
        private struct Tm
        {
            public int Sec,
                Min,
                Hour,
                MDay,
                Mon,
                Year,
                WDay,
                YDay,
                IsDst;
        }

        [DllImport(
            "ucrtbase.dll",
            EntryPoint = "_mktime64",
            CallingConvention = CallingConvention.Cdecl,
            ExactSpelling = true
        )]
        private static extern long UcrtMktime64(ref Tm tm);

        // 1 = the Universal CRT answers, 0 = not tried yet, -1 = not available
        private static int _ucrt;

        /// <summary>_mktime64 of a local date and time.</summary>
        public static double Mktime64(int year, int month, int day, int hour, int minute, int second)
        {
            if (_ucrt >= 0)
            {
                try
                {
                    var tm = new Tm
                    {
                        Sec = second,
                        Min = minute,
                        Hour = hour,
                        MDay = day,
                        Mon = month - 1,
                        Year = year - 1900,
                        WDay = -1,
                        YDay = 0,
                        IsDst = -1,
                    };
                    long t = UcrtMktime64(ref tm);
                    _ucrt = 1;
                    if (t == -1 && tm.WDay == -1)
                        throw new OverflowException(
                            "the C runtime's mktime refuses "
                                + year
                                + "-"
                                + month
                                + "-"
                                + day
                                + " as a local time"
                        );
                    return t;
                }
                catch (Exception e)
                    when (e is DllNotFoundException
                        || e is EntryPointNotFoundException
                        || e is BadImageFormatException
                    )
                {
                    _ucrt = -1;
                }
            }
            var local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
            return (local.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Ticks
                / TimeSpan.TicksPerSecond;
        }

        /// <summary>True when <see cref="Mktime64"/> uses the Universal CRT (tests).</summary>
        public static bool Mktime64IsUcrt
        {
            get
            {
                try
                {
                    Mktime64(2000, 1, 1, 0, 0, 0);
                }
                catch (OverflowException) { }
                return _ucrt == 1;
            }
        }
    }
}
