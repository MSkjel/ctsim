using System;
using System.Globalization;
using System.Text;
using CicodeSim.Values;

namespace CicodeSim.Vm
{
    /// <summary>Receives hardware errors raised by conversions and operators.</summary>
    public interface IErrorSink
    {
        void RaiseError(int code, bool halt);
    }

    /// <summary>An error sink that ignores errors (callers that only want the value).</summary>
    public sealed class NoErr : IErrorSink
    {
        public static readonly NoErr Instance = new NoErr();

        public void RaiseError(int code, bool halt) { }
    }

    /// <summary>An error sink that records (code, halt) pairs (tests, built-ins).</summary>
    public sealed class Collect : IErrorSink
    {
        public readonly System.Collections.Generic.List<(int Code, bool Halt)> Errors =
            new System.Collections.Generic.List<(int, bool)>();

        public void RaiseError(int code, bool halt) => Errors.Add((code, halt));

        public int[] Codes
        {
            get
            {
                var c = new int[Errors.Count];
                for (int i = 0; i < c.Length; i++)
                    c[i] = Errors[i].Code;
                return c;
            }
        }
    }

    /// <summary>Value conversions of the Cicode VM.</summary>
    public static class Conv
    {
        public const int ETypeMismatch = 433;
        public const int EConversion = 434;
        public const int ENull = 530;
        public const int EInvalidArg = 274;

        /// <summary>Skip C isspace characters.</summary>
        public static string SkipSpace(string s)
        {
            int i = 0;
            while (i < s.Length && Crt.IsSpace(s[i]))
                i++;
            return i == 0 ? s : s.Substring(i);
        }

        private static bool IsDigit(char c) => c >= '0' && c <= '9';

        /// <summary>The runtime's string to REAL: skip spaces; with the C
        /// locale ('.' decimal point) the FIRST ',' becomes '.'; strtod; characters left over =
        /// 0x112 (274; the value is the parsed prefix); a NULL string = 0x212.</summary>
        public static double ParseReal(string? s, out int code)
        {
            if (s == null)
            {
                code = 0x212;
                return 0.0;
            }
            string t = SkipSpace(s);
            int k = t.IndexOf(',');
            if (k >= 0)
                t = t.Substring(0, k) + "." + t.Substring(k + 1);
            double v = Crt.Strtod(t, out int used);
            code = used < t.Length ? 0x112 : 0;
            return v;
        }

        /// <summary>ParseReal without an error: the mixed string / number rule of the operators.</summary>
        public static bool ParsesAsNumber(string? s)
        {
            ParseReal(s, out int code);
            return code == 0;
        }

        /// <summary>The runtime's string to INT test (case 7): skip spaces;
        /// the first character must be a digit, '-', '+' or the end, else (0, false); then C
        /// atol ("12abc" = 12, "50.25" = 50, "" = 0).</summary>
        public static long StrToInt(string? s, out bool ok)
        {
            if (s == null)
            {
                ok = false;
                return 0;
            }
            string t = SkipSpace(s);
            if (t.Length > 0 && !(IsDigit(t[0]) || t[0] == '+' || t[0] == '-'))
            {
                ok = false;
                return 0;
            }
            ok = true;
            return Crt.Atol(t);
        }

        // ------------------------------------------------------------------ to INT
        /// <summary>REAL to INT (constants 0.5 /
        /// -2147483648.0 / 2147483647.0): false when out of range (x - 0.5 &lt; -2^31 or
        /// x + 0.5 &gt; 2^31 - 1, NaN).</summary>
        public static bool RoundHalfAway(double x, out long r)
        {
            double lo = x - 0.5;
            double hi = x + 0.5;
            if (!(lo >= -2147483648.0) || !(hi <= 2147483647.0))
            {
                r = 0;
                return false;
            }
            r = 0.0 > x ? (long)lo : (long)hi; // cvttsd2si: truncation toward zero
            return true;
        }

        /// <summary>Convert to INT (type 4): Convert 4, a store into an INT, operands of And / Or
        /// / Bit* / Not.</summary>
        public static Value ToInt(in Value v, IErrorSink err)
        {
            int t = v.T;
            if (t == Types.Long)
                return v;
            if (Types.IsIntLike(t))
                return Value.Long((int)v.I, v.Q, v.Ts, v.Vts, v.Qts);
            if (t == Types.Real)
            {
                if (!RoundHalfAway(v.D, out long r))
                {
                    err.RaiseError(EConversion, true);
                    r = 0;
                }
                return Value.Long(r, v.Q, v.Ts, v.Vts, v.Qts);
            }
            if (t == Types.String)
            {
                string? s = v.S;
                if (s == null)
                {
                    err.RaiseError(ENull, true);
                    return Value.Long(0, v.Q, v.Ts, v.Vts, v.Qts);
                }
                long r = StrToInt(s, out bool ok);
                if (!ok)
                    err.RaiseError(ETypeMismatch, true);
                return Value.Long(r, v.Q, v.Ts, v.Vts, v.Qts);
            }
            if (t == Types.Void)
                return Value.Long(0, v.Q, v.Ts, v.Vts, v.Qts);
            if (t == Types.Variant && v.O is Ref)
                return ToIntDepth(Value.Deref(v), err, 1);
            if (t == Types.ULong)
            {
                long x = v.I & 0xFFFFFFFFL;
                if (x >= 0x80000000L)
                {
                    err.RaiseError(EConversion, true);
                    x = 0;
                }
                return Value.Long(x, v.Q, v.Ts, v.Vts, v.Qts);
            }
            // OBJECT, TIMESTAMP, QUALITY, by-reference types ...: quality cleared, 433
            err.RaiseError(ETypeMismatch, true);
            return Value.Long(0, 0);
        }

        private static Value ToIntDepth(in Value v, IErrorSink err, int depth)
        {
            if (v.T == Types.Variant && v.O is Ref)
            {
                // a reference cycle: followed RecursionLimit deep, then the task halts
                if (depth > RecursionLimit)
                    throw new ReferenceCycleException();
                return ToIntDepth(Value.Deref(v), err, depth + 1);
            }
            return ToInt(v, err);
        }

        /// <summary>How deep the conversions follow a reference cycle before the task halts with
        /// 'recursion' (the engine's settled limit).</summary>
        internal const int RecursionLimit = 900;

        // ------------------------------------------------------------------ to REAL
        /// <summary>Convert to REAL. A string that does not parse completely gives its parsed prefix and 433.</summary>
        public static Value ToReal(in Value v, IErrorSink err)
        {
            int t = v.T;
            if (t == Types.Real)
                return v;
            if (Types.IsIntLike(t))
                return Value.Real((int)v.I, v.Q, v.Ts, v.Vts, v.Qts);
            if (t == Types.String)
            {
                double r = ParseReal(v.S, out int code);
                if (code != 0)
                    err.RaiseError(code == 0x112 ? ETypeMismatch : code, true);
                return Value.Real(r, v.Q, v.Ts, v.Vts, v.Qts);
            }
            if (t == Types.Void)
                return Value.Real(0.0, v.Q, v.Ts, v.Vts, v.Qts);
            if (t == Types.Variant && v.O is Ref)
                return ToRealDepth(Value.Deref(v), err, 1);
            if (t == Types.ULong)
                return Value.Real(v.I & 0xFFFFFFFFL, v.Q, v.Ts, v.Vts, v.Qts);
            err.RaiseError(ETypeMismatch, true);
            return Value.Real(0.0, 0);
        }

        private static Value ToRealDepth(in Value v, IErrorSink err, int depth)
        {
            if (v.T == Types.Variant && v.O is Ref)
            {
                if (depth > RecursionLimit)
                    throw new ReferenceCycleException();
                return ToRealDepth(Value.Deref(v), err, depth + 1);
            }
            return ToReal(v, err);
        }

        // ------------------------------------------------------------------ to STRING
        /// <summary>REAL to STRING: "%.15G" in the "English" locale.</summary>
        public static string RealToStr(double x) => Crt.FormatG15(x);

        /// <summary>TIMESTAMP to STRING (TimestampToStr with the global format). The
        /// simulator may replace it (verify).</summary>
        public static Func<ulong, string> TimestampToStr = DefaultTimestampToStr;

        /// <summary>QUALITY to STRING (verify).</summary>
        public static Func<ulong, string> QualityToStr = DefaultQualityToStr;

        public static string DefaultTimestampToStr(ulong ft)
        {
            if (ft == 0)
                return "";
            // 1601-01-01 + whole microseconds: year 9999 at most, a later FILETIME prints ""
            // ((DateTime.MaxValue.Ticks - 1601 ticks) / 10)
            ulong micro = ft / 10;
            const ulong maxMicro = (3155378975999999999UL - 504911232000000000UL) / 10;
            if (micro > maxMicro)
                return "";
            var d = new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Unspecified).AddTicks((long)(micro * 10));
            return d.ToString("yyyy-MM-dd HH:mm:ss.", CultureInfo.InvariantCulture)
                + (d.Ticks % TimeSpan.TicksPerSecond / 10000).ToString("000", CultureInfo.InvariantCulture);
        }

        public static string DefaultQualityToStr(ulong q)
        {
            switch ((q & 0xC0) >> 6)
            {
                case 0:
                    return "Bad";
                case 1:
                    return "Uncertain";
                case 2:
                    return "NA";
                default:
                    return "Good";
            }
        }

        private static string Cap(string s) =>
            s.Length > Types.StringMax ? s.Substring(0, Types.StringMax) : s;

        /// <summary>(text, error code) of an element formatted into a 256-byte buffer.</summary>
        public static string ValueText(in Value v, out int code)
        {
            code = 0;
            int t = v.T;
            if (t == Types.String)
            {
                if (v.S == null)
                {
                    code = ENull;
                    return "";
                }
                return Cap(v.S);
            }
            if (Types.IsIntLike(t))
                return ((int)v.I).ToString(CultureInfo.InvariantCulture);
            if (t == Types.Real)
                return Cap(RealToStr(v.D));
            if (t == Types.Void || t == Types.Object)
                return "";
            if (t == Types.Variant && v.O is Ref)
                return ValueTextDepth(Value.Deref(v), out code, 1);
            if (t == Types.ULong)
                return (v.I & 0xFFFFFFFFL).ToString(CultureInfo.InvariantCulture);
            if (t == Types.Timestamp)
                return Cap(TimestampToStr((ulong)v.I));
            if (t == Types.Quality)
                return Cap(QualityToStr((ulong)v.I));
            code = ETypeMismatch;
            return "";
        }

        private static string ValueTextDepth(in Value v, out int code, int depth)
        {
            if (v.T == Types.Variant && v.O is Ref)
            {
                if (depth > RecursionLimit)
                    throw new ReferenceCycleException();
                return ValueTextDepth(Value.Deref(v), out code, depth + 1);
            }
            return ValueText(v, out code);
        }

        /// <summary>Convert to STRING. A conversion error clears the quality and raises the formatter's code.</summary>
        public static Value ToStr(in Value v, IErrorSink err)
        {
            if (v.T == Types.String)
                return v;
            Value d = v.T == Types.Variant ? Value.Deref(v) : v;
            string s = ValueText(d, out int code);
            if (code != 0)
            {
                err.RaiseError(code, true);
                return Value.Str(s, 0);
            }
            return Value.Str(s, d.Q, d.Ts, d.Vts, d.Qts);
        }

        // ------------------------------------------------------------------ other targets
        /// <summary>Convert to OBJECT: only an OBJECT (through a reference) passes; anything
        /// else is 433 with the quality cleared and the null handle -1.</summary>
        public static Value ToObject(in Value v, IErrorSink err)
        {
            if (v.T == Types.Object)
                return v;
            if (v.T == Types.Variant && v.O is Ref)
                return ToObjectDepth(Value.Deref(v), err, 1);
            err.RaiseError(ETypeMismatch, true);
            return Value.Object(-1, 0);
        }

        private static Value ToObjectDepth(in Value v, IErrorSink err, int depth)
        {
            if (v.T == Types.Variant && v.O is Ref)
            {
                if (depth > RecursionLimit)
                    throw new ReferenceCycleException();
                return ToObjectDepth(Value.Deref(v), err, depth + 1);
            }
            return ToObject(v, err);
        }

        /// <summary>Convert to ULONG (the compiler never emits it). Modelled on the INT rule
        /// with the unsigned range (verify).</summary>
        public static Value ToULong(in Value v, IErrorSink err)
        {
            int t = v.T;
            if (t == Types.ULong)
                return v;
            if (t == Types.Variant && v.O is Ref)
                return ToULongDepth(Value.Deref(v), err, 1);
            if (Types.IsIntLike(t))
                return Value.Int(Types.ULong, v.I & 0xFFFFFFFFL, v.Q, v.Ts, v.Vts, v.Qts);
            if (t == Types.Real)
            {
                double x = v.D;
                if (!(x - 0.5 >= -0.5) || !(x + 0.5 <= 4294967295.0))
                {
                    err.RaiseError(EConversion, true);
                    return Value.Int(Types.ULong, 0, v.Q, v.Ts, v.Vts, v.Qts);
                }
                return Value.Int(Types.ULong, (long)(x + 0.5), v.Q, v.Ts, v.Vts, v.Qts);
            }
            if (t == Types.String)
            {
                long r = StrToInt(v.S, out bool ok);
                if (!ok)
                    err.RaiseError(ETypeMismatch, true);
                return Value.Int(Types.ULong, r & 0xFFFFFFFFL, v.Q, v.Ts, v.Vts, v.Qts);
            }
            err.RaiseError(ETypeMismatch, true);
            return Value.Int(Types.ULong, 0, 0);
        }

        private static Value ToULongDepth(in Value v, IErrorSink err, int depth)
        {
            if (v.T == Types.Variant && v.O is Ref)
            {
                if (depth > RecursionLimit)
                    throw new ReferenceCycleException();
                return ToULongDepth(Value.Deref(v), err, depth + 1);
            }
            return ToULong(v, err);
        }

        /// <summary>The Convert (0x09) opcode.</summary>
        public static Value Convert(in Value v, int target, IErrorSink err)
        {
            switch (target)
            {
                case 0:
                case 1:
                case 3:
                case 4:
                case 5:
                case 8:
                case 0xA:
                    return ToInt(v, err);
                case Types.Real:
                    return ToReal(v, err);
                case Types.String:
                    return ToStr(v, err);
                case Types.Object:
                    return ToObject(v, err);
                case Types.ULong:
                    return ToULong(v, err);
                case Types.Void:
                case Types.RealValue:
                case Types.Timestamp:
                case Types.Quality:
                {
                    Value d = v.T == Types.Variant ? Value.Deref(v) : v;
                    if (d.T == target)
                        return d;
                    if (d.T == Types.Variant && target == Types.Void) // a reference to nothing
                        return Value.Int(Types.Void, 0, d.Q);
                    err.RaiseError(ETypeMismatch, true);
                    return v;
                }
            }
            err.RaiseError(ETypeMismatch, true);
            return v;
        }

        // ------------------------------------------------------------------ formats
        /// <summary>The printf format of a Cicode format word.</summary>
        public static string FormatWordToPrintf(long word, string? suffix, int t)
        {
            long w = word & 0xFF;
            long d = (word >> 8) & 0xFF;
            long fl = (word >> 16) & 0xFFFF;
            string suf = "";
            if ((fl & 4) != 0)
                suf = " " + (suffix ?? "").Replace("%", "%%");
            if (w == 0)
                return suf.Length > 0 ? suf.Substring(1) : "";
            string left = (fl & 2) != 0 ? "-" : "";
            string zero = (fl & 1) != 0 ? "0" : "";
            if (t == Types.String)
                return "%" + left + w + "." + (d != 0 ? d : w) + "s" + suf;
            if (t == Types.Real || t == Types.LongReal)
                return "%" + left + zero + w + "." + d + ((fl & 0x10) != 0 ? "e" : "f") + suf;
            if (t == Types.ULong)
                return "%" + left + zero + w + "lu" + suf;
            return "%" + left + zero + w + "ld" + suf;
        }

        /// <summary>The runtime replaces the C locale's decimal point by '.'; the simulator
        /// formats in the C locale already.</summary>
        public static string FixDecimal(string s) => s;

        private static PrintfArg ArgOf(in Value d)
        {
            if (Types.IsRealType(d.T))
                return PrintfArg.Float(d.D);
            if (d.T == Types.String)
                return d.S == null ? PrintfArg.Other : PrintfArg.Str(d.S);
            // a TIMESTAMP / QUALITY is its unsigned 64-bit payload
            if (d.T == Types.Timestamp || d.T == Types.Quality)
                return PrintfArg.UInt64(unchecked((ulong)d.I));
            // an integer type its payload, a VARIANT holding a reference the integer 0
            return PrintfArg.Int(d.PayloadIsInt ? d.I : 0);
        }

        /// <summary>The :format operator (StringFmt 0x46).</summary>
        public static Value FormatValue(
            in Value v,
            long word,
            string? suffix,
            IErrorSink err,
            Func<string, string>? resolveSuffix = null
        )
        {
            if (v.T == Types.String)
                return v;
            Value d = v.T == Types.Variant ? Value.Deref(v) : v;
            string s;
            if (d.T == Types.Timestamp || d.T == Types.Quality)
                s = ValueText(d, out _);
            else if (d.T == Types.String)
                s = Cap(d.S ?? "");
            else
            {
                string suf = suffix ?? "";
                if (suf.IndexOf('?') >= 0 && resolveSuffix != null)
                    suf = Cap(resolveSuffix(suf));
                string fmt = FormatWordToPrintf(word, suf, d.T);
                if (d.T == Types.Real || d.T == Types.LongReal)
                {
                    double x = d.D;
                    s = Crt.Sprintf(fmt, PrintfArg.Float(x));
                    if (d.T == Types.Real && s.Length >= 21)
                        s = Printf.Format("%.2e", PrintfArg.Float(x));
                }
                else
                    s = Crt.Sprintf(fmt, d.PayloadIsInt ? PrintfArg.Int(d.I) : PrintfArg.Int(0));
            }
            return Value.Str(Cap(s), d.Q, d.Ts, d.Vts, d.Qts);
        }

        /// <summary>The String opcode (0x0a): sprintf(buf, fmt, value) with the format string on
        /// top (AssFmt pushes it); a REAL printing 21+ characters becomes "%.2e"
        /// (case 10).</summary>
        public static Value StringOp(in Value value, string? fmt, IErrorSink err)
        {
            Value d = value.T == Types.Variant ? Value.Deref(value) : value;
            PrintfArg arg;
            if (d.T == Types.String)
                arg = PrintfArg.Str(d.S ?? "");
            else
                arg = ArgOf(d);
            string s = Crt.Sprintf(fmt ?? "", arg);
            if (d.T == Types.Real && s.Length > 20)
                s = Printf.Format("%.2e", PrintfArg.Float(d.D));
            return Value.Str(Cap(s), d.Q, d.Ts, d.Vts, d.Qts);
        }
    }

    /// <summary>A reference cycle followed <see cref="Conv.RecursionLimit"/> deep: the task halts
    /// with the reason 'recursion'.</summary>
    public sealed class ReferenceCycleException : Exception
    {
        public ReferenceCycleException()
            : base("recursion") { }
    }
}
