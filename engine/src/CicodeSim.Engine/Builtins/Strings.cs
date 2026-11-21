using System;
using System.Globalization;
using System.Text;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>String built-ins. Strings are at most 255 single-byte (Windows-1252) characters.</summary>
    public static class Strings
    {
        public const int ERange = 257;
        public const int EFormat = 258;
        public const int EInvalidArg = 274;
        public const int ENull = 530;

        private static Value I(long v) => Value.Long(v);

        private static Value S(string v) => Value.Str(v);

        /// <summary>The runtime's length-limited copy: at most (short) n
        /// characters, none when n &lt;= 0.</summary>
        public static string Slc(string src, long n)
        {
            long m = A.Short(n);
            if (m <= 0)
                return "";
            return A.To(src, m);
        }

        /// <summary>Slc(src[offs:], n) without the intermediate copy (offs &gt;= 0).</summary>
        private static string SlcFrom(string src, long offs, long n)
        {
            long m = A.Short(n);
            if (m <= 0 || offs >= src.Length)
                return "";
            long len = Math.Min(m, src.Length - offs);
            return A.Sub(src, (int)offs, (int)len);
        }

        /// <summary>count characters of pad repeated.</summary>
        public static string Fill(string pad, long count)
        {
            if (count <= 0 || pad.Length == 0)
                return "";
            if (count > 100_000_000)
                throw new VmError("a fill too large for memory");
            if (pad.Length == 1)
                return new string(pad[0], (int)count);
            var sb = new StringBuilder((int)count);
            while (sb.Length < count)
                sb.Append(pad);
            sb.Length = (int)count;
            return sb.ToString();
        }

        private static long Byte(string s, int i) => Cp1252.Byte(s[i]);

        public static string Left(CicodeTask task, string x, long n)
        {
            long n16 = n & 0xFFFF,
                ns = A.Short(n);
            if (n16 > 0x7FFF || ns > 254)
            {
                task.RaiseError(ERange, false);
                if (ns > 254)
                    n = 254;
            }
            return Slc(x, n);
        }

        /// <summary>StrMid: 257 (no halt) whenever offset + length runs
        /// past the string or past 254 characters; the part that exists is still returned.</summary>
        public static string Mid(CicodeTask task, string x, long off, long n)
        {
            long ln = x.Length;
            long off16 = off & 0xFFFF;
            long offs = A.Short(off);
            long nOrig = A.Short(n);
            long p3 = n & 0xFFFF;
            if (nOrig + offs > 254)
                p3 = (254 - off16) & 0xFFFF;
            long p3s = A.Short(p3);
            long u5 = (ln - off16) & 0xFFFF;
            if (p3s + offs <= ln)
                u5 = p3;
            if (u5 < 0x8000 && off16 < 0x8000 && offs < ln && offs < 255)
            {
                string res = SlcFrom(x, offs, u5);
                if (p3s + offs <= ln && nOrig + offs < 255)
                    return res;
                task.RaiseError(ERange, false);
                return res;
            }
            task.RaiseError(ERange, false);
            return "";
        }

        private static int Find(string hay, string needle, long start)
        {
            if (start > hay.Length)
                return -1;
            if (start < 0)
                start = 0;
            return hay.IndexOf(needle, (int)start, StringComparison.Ordinal);
        }

        /// <summary>_LibControl_StrArray item count N ('#N').</summary>
        private static long ArrN(string x) => x.Length > 1 ? x[1] - 0x30 : -0x30;

        private static bool ArrOk(string x) => x.Length > 1 && x[0] == '#' && x[1] >= '1' && x[1] <= '9';

        /// <summary>_LibControl_StrArray_GetLen: (len - 2) / N.</summary>
        public static long ArrLen(string x)
        {
            if (ArrOk(x))
                return A.Short((x.Length - 2) / ArrN(x));
            return 0;
        }

        private static string ArrExtend(string x, long items)
        {
            long n = ArrN(x);
            long count = ArrLen(x);
            if (count + 1 <= items)
                x = x + new string('0', (int)Math.Max(0, n * (items - count)));
            return A.Cap(x);
        }

        /// <summary>_LibControl_StrReplace: every occurrence,
        /// case-insensitive by default (both lowered with _strlwr).</summary>
        public static string LcReplace(
            CicodeTask task,
            string src,
            string orig,
            string repl,
            long caseSensitive
        )
        {
            int ln = src.Length,
                ol = orig.Length;
            if (ol == 0)
                return src;
            string hay = caseSensitive != 0 ? src : A.CLower(src);
            string needle = caseSensitive != 0 ? orig : A.CLower(orig);
            var sb = new StringBuilder();
            long pos = 0;
            if (ln > 0)
            {
                while (true)
                {
                    int p = pos < hay.Length ? Find(hay, needle, pos) : -1;
                    if (p < 0)
                    {
                        long rest = ln - pos;
                        sb.Append(rest > 0 ? A.From(src, ln - rest) : "");
                        break;
                    }
                    if (pos < p)
                        sb.Append(Mid(task, src, pos, p - pos));
                    sb.Append(repl);
                    pos = ol + p;
                    if (!(pos < ln))
                        break;
                }
            }
            return A.Cap(sb.ToString());
        }

        internal static void Register(Action<string, string, BuiltinImpl> add)
        {
            add("StrLength", "(strlen)", (t, a) => I(A.S(a.Get(0)).Length));

            add("StrLeft", "", (t, a) => S(Left(t, A.S(a.Get(0)), A.I(a.Get(1)))));

            add("StrRight", "", (t, a) =>
            {
                string x = A.S(a.Get(0));
                long n = A.I(a.Get(1));
                long ln = x.Length;
                long n16 = n & 0xFFFF;
                long start = 0;
                if (n16 < 255)
                {
                    if (ln <= A.Short(n16))
                        n = ln;
                    start = (ln - n) & 0xFFFF;
                }
                else
                {
                    t.RaiseError(ERange, false);
                    if (A.Short(n16) > 254)
                        n = 254;
                }
                return S(Slc(A.From(x, start), n));
            });

            add("StrMid", "", (t, a) => S(Mid(t, A.S(a.Get(0)), A.I(a.Get(1)), A.I(a.Get(2)))));

            add("StrSearch", "(strstr from the offset; -1 = not found)", (t, a) =>
            {
                long start = A.Short(A.I(a.Get(0)));
                string x = A.S(a.Get(1)),
                    sub = A.S(a.Get(2));
                if (start < x.Length)
                {
                    int k = Find(x, sub, Math.Max(start, 0)); // a negative offset reads before the buffer
                    if (k >= 0)
                        return I(k & 0xFFFF);
                }
                return I(0xFFFF);
            });

            add("StrToInt", "; live: StrToInt(\"12abc\") = 12, \"abc\" = 0", (t, a) =>
            {
                Value v = a.Get(0);
                if (v.IsNothing || (v.T == Types.String && v.S == null))
                {
                    t.RaiseError(ENull, false);
                    return I(0);
                }
                string s = A.SkipSpace(A.S(v));
                if (s.Length > 0 && !((s[0] >= '0' && s[0] <= '9') || s[0] == '+' || s[0] == '-'))
                {
                    t.RaiseError(EInvalidArg, false);
                    return I(0);
                }
                return I(Crt.Atol(s));
            });

            add("StrToReal", "(its 274 / 530 raised, no halt)", (t, a) =>
            {
                double x = Conv.ParseReal(A.S(a.Get(0)), out int code);
                if (code != 0)
                    t.RaiseError(code, false);
                return Value.Real(x);
            });

            add("IntToStr", "(_ltoa)", (t, a) => S(A.I(a.Get(0)).ToString(CultureInfo.InvariantCulture)));

            add("StrUpper", "(_strupr, C locale)", (t, a) => S(A.CUpper(A.S(a.Get(0)))));

            add("StrLower", "(_strlwr, C locale)", (t, a) => S(A.CLower(A.S(a.Get(0)))));

            add("StrTrim", "(isspace both ends)", (t, a) =>
                S(A.S(a.Get(0)).Trim(' ', '\t', '\n', '\v', '\f', '\r'))
            );

            add("StrWord", ": the first word; the variable keeps the rest from the delimiter on", (t, a) =>
            {
                Value r = a.Get(0);
                string src = A.SkipSpace(A.S(r));
                int k = 0;
                while (k < src.Length && !A.IsSpace(src[k]))
                    k++;
                string word = src.Substring(0, k),
                    rest = src.Substring(k);
                if (!r.IsNothing && r.T == Types.Variant)
                    A.Assign(t, r, rest);
                return S(word);
            });

            add(
                "StrPad",
                "(Length > 0 pads on the right, < 0 on the left, the result cut to the length)",
                (t, a) =>
                {
                    string x = A.S(a.Get(0)),
                        pad = A.S(a.Get(1));
                    long n = A.I(a.Get(2));
                    long ns = A.Short(n);
                    long ln = x.Length;
                    if (ns >= 0)
                    {
                        long m = ns > 254 ? 255 : ns;
                        long pc0 = m > ln ? m - ln : 0;
                        return S(A.To(x + Fill(pad, pc0), m));
                    }
                    long m2 = (-ns) & 0xFFFF;
                    if (A.Short(m2) > 254)
                        m2 = 255;
                    long pc = m2 > ln ? m2 - ln : 0;
                    if (pc < 1)
                        return S(A.To(x, m2));
                    return S(A.To(Fill(pad, pc) + x, m2));
                }
            );

            add("StrFill", "(Length < 255)", (t, a) =>
            {
                string x = A.S(a.Get(0));
                long n = A.I(a.Get(1));
                if ((n & 0xFFFF) < 255)
                    return S(Fill(x, A.Short(n)));
                return S("");
            });

            add("StrGetChar", "(offset < 256 into the 256-byte buffer)", (t, a) =>
            {
                string x = A.Buffer(a.Get(0));
                long o = A.I(a.Get(1)) & 0xFFFF;
                if (o < 256)
                    return I(o < x.Length ? Byte(x, (int)o) : 0);
                t.RaiseError(ERange, false);
                return I(0);
            });

            add("StrSetChar", "(writes the byte into the variable's buffer)", (t, a) =>
            {
                Value r = a.Get(0);
                long o = A.I(a.Get(1)) & 0xFFFF;
                long ch = A.I(a.Get(2));
                if (o < 256)
                {
                    byte[] buf = Cp1252.Encode(A.Buffer(r));
                    if (o >= buf.Length)
                        Array.Resize(ref buf, (int)o + 1);
                    buf[o] = (byte)(ch & 0xFF);
                    string nw = Cp1252.Decode(buf, 0, buf.Length);
                    if (!r.IsNothing && r.T == Types.Variant)
                        A.Assign(t, r, nw);
                    return I(0);
                }
                t.RaiseError(ERange, false);
                return I(ERange);
            });

            add("StrToChar", "(the first byte)", (t, a) =>
            {
                string x = A.S(a.Get(0));
                return I(x.Length > 0 ? Byte(x, 0) : 0);
            });

            add("CharToStr", "", (t, a) =>
            {
                long b = A.I(a.Get(0)) & 0xFF;
                return S(b != 0 ? Cp1252.Char((byte)b).ToString() : "");
            });

            add("HexToStr", "(\"%lx\" / \"%0<w>lx\", width < 9)", (t, a) =>
            {
                long n = A.I(a.Get(0)),
                    w = A.I(a.Get(1));
                long w16 = w & 0xFFFF;
                if (w16 < 9)
                {
                    string fmt = w16 == 0 ? "%x" : "%0" + w16 + "x";
                    return S(Printf.Format(fmt, PrintfArg.Int(n & 0xFFFFFFFFL)));
                }
                t.RaiseError(ERange, false);
                return S("");
            });

            add("StrClean", "(keeps isprint characters, C locale)", (t, a) =>
            {
                string x = A.S(a.Get(0));
                var sb = new StringBuilder(x.Length);
                foreach (char c in x)
                    if (c >= ' ' && c <= '~')
                        sb.Append(c);
                return S(sb.ToString());
            });

            add("StrToHex", "", (t, a) =>
            {
                string s = A.SkipSpace(A.S(a.Get(0)));
                const string hexd = "0123456789abcdefABCDEF";
                if (s.Length > 0 && hexd.IndexOf(s[0]) < 0 && s[0] != '+' && s[0] != '-')
                {
                    t.RaiseError(EInvalidArg, false);
                    return I(0);
                }
                int k = 0;
                bool neg = false;
                if (s.Length > 0 && (s[0] == '-' || s[0] == '+'))
                {
                    neg = s[0] == '-';
                    k = 1;
                }
                long v = 0;
                while (k < s.Length && hexd.IndexOf(s[k]) >= 0)
                {
                    char c = s[k];
                    int h = c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;
                    v = (v * 16 + h) & 0xFFFFFFFFL;
                    k++;
                }
                v = (int)v;
                return I(neg ? (int)(-v) : v);
            });

            add(
                "RealToStr",
                "(\"%<w>.<p>f\"; w <= 29, |p| <= 19 else 257; separator ',' / '.' replaces the decimal point)",
                (t, a) =>
                {
                    double x = A.R(a.Get(0));
                    long w = A.I(a.Get(1)),
                        p = A.I(a.Get(2));
                    string sep = a.Count > 3 ? A.S(a.Get(3)) : "";
                    long w16 = w & 0xFFFF;
                    long ps = A.Short(p);
                    long pa = (ps < 0 ? -ps : ps) & 0xFFFF;
                    if (w16 > 29 || pa > 19)
                    {
                        t.RaiseError(ERange, false);
                        return S("");
                    }
                    string output = Printf.Format("%" + w16 + "." + pa + "f", PrintfArg.Float(x));
                    if (output.Length > 255)
                    {
                        t.RaiseError(EFormat, false);
                        return S("");
                    }
                    if ((p & 0x8000) != 0)
                        return S(output);
                    if (sep == "," || sep == ".")
                    {
                        int k = output.IndexOf('.');
                        if (sep != "." && k >= 0)
                            output = output.Substring(0, k) + sep + output.Substring(k + 1);
                    }
                    // any other separator goes through the runtime's locale number
                    // formatting; with the default "" it leaves the text as it is (live:
                    // RealToStr(1.5, 10, 4) = "    1.5000"); other separators: (verify)
                    return S(output);
                }
            );

            add(
                "StrFormat",
                "(\"%<w>.<d>f\" or \"%<w>.<d>f %s\" with EU; the 16-bit width clamped to 1..20, the decimals to 0..10; 258 without the halt flag when the text does not fit 256 bytes)",
                (t, a) =>
                {
                    double x = A.R(a.Get(0));
                    long w = A.Short(A.I(a.Get(1))),
                        d = A.Short(A.I(a.Get(2)));
                    string eu = a.Count > 3 ? A.S(a.Get(3)) : "";
                    w = w >= 20 ? 20 : w;
                    w = w <= 1 ? 1 : w;
                    d = d >= 10 ? 10 : d;
                    d = d <= 0 ? 0 : d;
                    string output = Printf.Format(
                        "%"
                            + w.ToString(CultureInfo.InvariantCulture)
                            + "."
                            + d.ToString(CultureInfo.InvariantCulture)
                            + "f",
                        PrintfArg.Float(x)
                    );
                    if (eu.Length > 0)
                        output += " " + eu;
                    if (output.Length > 255)
                    {
                        t.RaiseError(EFormat, false);
                        return S("");
                    }
                    return S(output);
                }
            );

            add("StrReplace", "(sTerminators = whole-word replace)", (t, a) =>
            {
                string x = A.S(a.Get(0)),
                    find = A.S(a.Get(1));
                string repl = a.Count > 2 ? A.S(a.Get(2)) : "";
                string terms = a.Count > 3 ? A.S(a.Get(3)) : "";
                long ln = x.Length,
                    fl = find.Length,
                    rl = repl.Length;
                if (ln == 0 || fl == 0)
                    return S(A.Cap(x));
                var sb = new StringBuilder();
                long pos = 0,
                    total = 0;
                while (true)
                {
                    int p = Find(x, find, pos);
                    if (p < 0)
                    {
                        sb.Append(A.From(x, pos));
                        break;
                    }
                    long before = p - pos;
                    total += rl + before;
                    if ((total - fl - p) + ln > 255)
                    {
                        t.RaiseError(EFormat, false);
                        sb.Append(A.From(x, pos));
                        break;
                    }
                    bool replace = true;
                    if (terms.Length > 0)
                    {
                        if (ln > fl + p && terms.IndexOf(x[(int)(p + fl)]) < 0)
                            replace = false;
                        else if (p > 0 && terms.IndexOf(x[p - 1]) < 0)
                            replace = false;
                    }
                    long step;
                    if (replace)
                    {
                        sb.Append(before >= 1 ? A.Slice(x, pos, pos + before) : "");
                        sb.Append(repl);
                        step = fl;
                    }
                    else
                    {
                        sb.Append(before + 1 >= 1 ? A.Slice(x, pos, pos + before + 1) : "");
                        step = 1;
                    }
                    pos = (step + p) & 0xFFFF;
                    if (!(A.Short(step + p) < ln))
                        break;
                }
                return S(A.Cap(sb.ToString()));
            });

            add("StrToLines", "(breaks at spaces, lines joined by LF)", (t, a) =>
            {
                string x = A.S(a.Get(0));
                long n = A.I(a.Get(1));
                Value r = a.Get(2);
                long ln = x.Length;
                void OutLines(long k)
                {
                    if (!r.IsNothing && r.T == Types.Variant)
                        A.Assign(t, r, k);
                }
                if (n < 1 || n > 254 || ln > 0x7FFF || (255 - ln / n) <= ln)
                {
                    t.RaiseError(ERange, false);
                    OutLines(0);
                    return S("");
                }
                long lines = 0;
                long p = x.Length - A.SkipSpace(x).Length;
                if (p >= ln)
                {
                    OutLines(0);
                    return S("");
                }
                long rem = ln - p;
                long e = p + (rem > n ? n : rem);
                var sb = new StringBuilder();
                while (true)
                {
                    while (p < e && e < ln && !A.IsSpace(x[(int)e]))
                        e--;
                    long seg;
                    if (e == p)
                    {
                        rem = ln - p;
                        seg = n < rem ? n : rem;
                    }
                    else
                        seg = e - p;
                    sb.Append(A.Slice(x, p, p + seg));
                    long q = p + seg;
                    string tail = A.From(x, q);
                    p = q + (tail.Length - A.SkipSpace(tail).Length);
                    lines++;
                    if (p >= ln)
                        break;
                    sb.Append('\n');
                    rem = ln - p;
                    e = p + (n < rem ? n : rem);
                }
                OutLines(lines);
                return S(A.Cap(sb.ToString()));
            });

            // --- the Library_Controls string arrays ('#N' + items of N characters) ---------
            add("_LibControl_StrArray_GetLen", "", (t, a) => I(ArrLen(A.S(a.Get(0)))));

            add("_LibControl_StrArray_GetInt", "(item 1-based; outside: the default)", (t, a) =>
            {
                string x = A.S(a.Get(0));
                long item = A.Short(A.I(a.Get(1)));
                long dflt = a.Count > 2 ? A.I(a.Get(2)) : 0;
                long n = ArrN(x);
                if (0 < item && item <= ArrLen(x))
                {
                    string sub = Mid(t, x, (item - 1) * n + 2, n);
                    string s = A.SkipSpace(sub);
                    if (s.Length > 0 && !((s[0] >= '0' && s[0] <= '9') || s[0] == '+' || s[0] == '-'))
                    {
                        t.RaiseError(EInvalidArg, false);
                        return I(0);
                    }
                    return I(Crt.Atol(s));
                }
                return I(dflt);
            });

            add("_LibControl_StrArray_SetLen", "(new items '0' x N; bTruncate cuts)", (t, a) =>
            {
                string x = A.S(a.Get(0));
                long items = A.I(a.Get(1));
                long trunc = a.Count > 2 ? A.Short(A.I(a.Get(2))) : 0;
                if (!ArrOk(x))
                    return S(x);
                long n = ArrN(x);
                if (trunc == 0 || ArrLen(x) < A.Short(items))
                    return S(ArrExtend(x, A.Short(items)));
                return S(Left(t, x, (n * items + 2) & 0xFFFF));
            });

            add("_LibControl_StrArray_SetInt", "(the value right-aligned in its N characters)", (t, a) =>
            {
                string x = A.S(a.Get(0));
                long item = a.Count > 1 ? A.Short(A.I(a.Get(1))) : 1;
                long value = a.Count > 2 ? A.I(a.Get(2)) : 0;
                long n = ArrN(x);
                if (item < 1 || !ArrOk(x))
                    return S(x);
                string v = value.ToString(CultureInfo.InvariantCulture);
                v = v.Length < n ? new string(' ', (int)(n - v.Length)) + v : A.To(v, n);
                long off = (item - 1) * n + 2;
                long end = A.Short((item - 1) * n) + n + 2;
                if (end >= 0xFF)
                {
                    t.RaiseError(EInvalidArg, false);
                    return S(x);
                }
                string ext = ArrExtend(x, item);
                return S(A.Cap(A.To(ext, off) + v + A.From(ext, off + n)));
            });

            add("_LibControl_StrReplace", "", (t, a) =>
                S(
                    LcReplace(
                        t,
                        A.S(a.Get(0)),
                        A.S(a.Get(1)),
                        a.Count > 2 ? A.S(a.Get(2)) : "",
                        a.Count > 3 ? A.I(a.Get(3)) : 0
                    )
                )
            );

            add("_LibControl_SetCallbackArg", "(replaces '#<sParam>')", (t, a) =>
            {
                string cb = A.S(a.Get(0)),
                    param = A.S(a.Get(1)),
                    value = A.S(a.Get(2));
                long cs = a.Count > 3 ? A.I(a.Get(3)) : 0;
                if (cb.IndexOf('#') < 0)
                    return S(cb);
                return S(LcReplace(t, cb, A.Cap("#" + param), value, cs));
            });
        }
    }
}
