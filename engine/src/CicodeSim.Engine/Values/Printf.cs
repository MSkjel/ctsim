using System;
using System.Globalization;
using System.Text;

namespace CicodeSim.Values
{
    /// <summary>The argument of one printf conversion: an integer, a double, a string or
    /// anything else (which <see cref="Crt.Sprintf"/> prints as 0, 0.0 or ''). An unsigned
    /// integer holds all 64 bits of a TIMESTAMP / QUALITY payload.</summary>
    public readonly struct PrintfArg
    {
        public const int KInt = 0,
            KFloat = 1,
            KStr = 2,
            KOther = 3;
        public readonly int Kind;
        public readonly long I;
        public readonly double D;
        public readonly string? S;

        /// <summary>I is an unsigned 64-bit value (a conversion to double reads it unsigned).</summary>
        public readonly bool Unsigned;

        private PrintfArg(int kind, long i, double d, string? s, bool unsigned)
        {
            Kind = kind;
            I = i;
            D = d;
            S = s;
            Unsigned = unsigned;
        }

        public static PrintfArg Int(long v) => new PrintfArg(KInt, v, 0, null, false);

        public static PrintfArg UInt64(ulong v) => new PrintfArg(KInt, unchecked((long)v), 0, null, true);

        public static PrintfArg Float(double v) => new PrintfArg(KFloat, 0, v, null, false);

        public static PrintfArg Str(string v) => new PrintfArg(KStr, 0, 0, v, false);

        public static readonly PrintfArg Other = new PrintfArg(KOther, 0, 0, null, false);

        /// <summary>The integer as a double (the nearest one; unsigned when it is).</summary>
        public double IntAsDouble => Unsigned ? FloatText.FromUInt64(unchecked((ulong)I)) : I;
    }

    /// <summary>Why <see cref="Printf.TryFormat"/> refused a format.</summary>
    public enum PrintfError
    {
        None = 0,

        /// <summary>An incomplete or unknown conversion, or a precision beyond int.MaxValue.</summary>
        BadFormat = 1,

        /// <summary>No conversion, more than one, a '*' width or precision, or an argument the
        /// conversion cannot take (a string for %d, a number for %c beyond U+10FFFF ...).</summary>
        BadArgument = 2,

        /// <summary>An infinite double for an integer conversion, a character code out of range.</summary>
        Overflow = 3,

        /// <summary>A width or precision above <see cref="Printf.MaxWidth"/>.</summary>
        TooWide = 4,
    }

    /// <summary>A format with one printf conversion and its argument.</summary>
    public static class Printf
    {
        private const int FLjust = 1,
            FSign = 2,
            FBlank = 4,
            FAlt = 8,
            FZero = 16;

        /// <summary>The widest width or precision accepted (a larger one is
        /// <see cref="PrintfError.TooWide"/>).</summary>
        public const long MaxWidth = 10_000_000;

        /// <summary>The formatted text; FormatException when the format is refused.</summary>
        public static string Format(string fmt, PrintfArg arg)
        {
            PrintfError e = TryFormat(fmt, arg, out string r);
            if (e != PrintfError.None)
                throw new FormatException("printf format refused (" + e + "): " + fmt);
            return r;
        }

        public static PrintfError TryFormat(string fmt, PrintfArg arg, out string result)
        {
            var sb = new StringBuilder(fmt.Length + 16);
            bool used = false;
            int n = fmt.Length;
            int i = 0;
            result = "";
            while (i < n)
            {
                char ch = fmt[i];
                if (ch != '%')
                {
                    sb.Append(ch);
                    i++;
                    continue;
                }
                i++;
                if (i >= n)
                    return PrintfError.BadFormat;
                if (fmt[i] == '%')
                {
                    sb.Append('%');
                    i++;
                    continue;
                }
                int flags = 0;
                long width = -1,
                    prec = -1;
                char c = '\0';
                while (i < n)
                {
                    c = fmt[i];
                    if (c == '-')
                        flags |= FLjust;
                    else if (c == '+')
                        flags |= FSign;
                    else if (c == ' ')
                        flags |= FBlank;
                    else if (c == '#')
                        flags |= FAlt;
                    else if (c == '0')
                        flags |= FZero;
                    else
                        break;
                    i++;
                }
                if (i >= n)
                    return PrintfError.BadFormat;
                c = fmt[i];
                if (c == '*')
                    return PrintfError.BadArgument;
                if (c >= '0' && c <= '9')
                {
                    width = 0;
                    while (i < n && fmt[i] >= '0' && fmt[i] <= '9')
                    {
                        width = width * 10 + (fmt[i] - '0');
                        if (width > MaxWidth * 100)
                            return PrintfError.TooWide;
                        i++;
                    }
                    if (i >= n)
                        return PrintfError.BadFormat;
                    c = fmt[i];
                }
                if (c == '.')
                {
                    prec = 0;
                    i++;
                    if (i < n)
                    {
                        c = fmt[i];
                        if (c == '*')
                            return PrintfError.BadArgument;
                        while (i < n && fmt[i] >= '0' && fmt[i] <= '9')
                        {
                            prec = prec * 10 + (fmt[i] - '0');
                            if (prec > int.MaxValue)
                                return PrintfError.BadFormat;
                            i++;
                        }
                    }
                    if (i >= n)
                        return PrintfError.BadFormat;
                    c = fmt[i];
                }
                if (c == 'h' || c == 'l' || c == 'L')
                {
                    i++;
                    if (i >= n)
                        return PrintfError.BadFormat;
                    c = fmt[i];
                }
                i++;
                if (width > MaxWidth || prec > MaxWidth)
                    return PrintfError.TooWide;
                if (used)
                    return PrintfError.BadArgument; // a second conversion
                used = true;
                PrintfError err = FormatOne(sb, c, flags, (int)width, (int)prec, arg);
                if (err != PrintfError.None)
                    return err;
            }
            if (!used)
                return PrintfError.BadArgument; // no conversion
            result = sb.ToString();
            return PrintfError.None;
        }

        private static PrintfError FormatOne(
            StringBuilder sb,
            char conv,
            int flags,
            int width,
            int prec,
            PrintfArg arg
        )
        {
            string str;
            bool sign = false;
            switch (conv)
            {
                case 's':
                    if (arg.Kind == PrintfArg.KStr)
                        str = arg.S ?? "";
                    else if (arg.Kind == PrintfArg.KInt)
                        str = arg.I.ToString(CultureInfo.InvariantCulture);
                    else
                        str = FloatText.Shortest(arg.D);
                    if (prec >= 0 && str.Length > prec)
                        str = str.Substring(0, prec);
                    break;
                case 'd':
                case 'i':
                case 'u':
                case 'o':
                case 'x':
                case 'X':
                {
                    long v;
                    if (arg.Kind == PrintfArg.KInt)
                        v = arg.I;
                    else if (arg.Kind == PrintfArg.KFloat && (conv == 'd' || conv == 'i' || conv == 'u'))
                    {
                        if (double.IsNaN(arg.D))
                            return PrintfError.BadArgument;
                        if (double.IsInfinity(arg.D))
                            return PrintfError.Overflow;
                        v = FloatText.TruncLow64(arg.D);
                    }
                    else
                        return PrintfError.BadArgument;
                    str = FormatInteger(v, (flags & FAlt) != 0, prec, conv);
                    sign = true;
                    break;
                }
                case 'e':
                case 'E':
                case 'f':
                case 'F':
                case 'g':
                case 'G':
                {
                    double x;
                    if (arg.Kind == PrintfArg.KFloat)
                        x = arg.D;
                    else if (arg.Kind == PrintfArg.KInt)
                        x = arg.I;
                    else
                        return PrintfError.BadArgument;
                    str = FloatText.Format(x, conv, prec < 0 ? 6 : prec, (flags & FAlt) != 0);
                    if (double.IsNaN(x) || double.IsInfinity(x))
                    {
                        // the UCRT's '#' with no decimals forces its point after the first
                        // character of the text too ("i.nf", "-.inf", "n.an"), and it pads with
                        // zeros only where no letter follows the sign (so "-.inf" does: "-00.inf")
                        if ((flags & FAlt) != 0 && prec == 0 && conv != 'g' && conv != 'G')
                            str = str.Substring(0, 1) + "." + str.Substring(1);
                        int p0 = str[0] == '-' ? 1 : 0;
                        if (char.IsLetter(str[p0]))
                            flags &= ~FZero;
                    }
                    sign = true;
                    break;
                }
                case 'c':
                    if (arg.Kind == PrintfArg.KInt)
                    {
                        if (arg.I < 0 || arg.I > 0x10FFFF)
                            return PrintfError.Overflow;
                        str = arg.I > 0xFFFF ? char.ConvertFromUtf32((int)arg.I) : ((char)arg.I).ToString();
                    }
                    else if (arg.Kind == PrintfArg.KStr && arg.S != null && arg.S.Length == 1)
                        str = arg.S;
                    else
                        return PrintfError.BadArgument;
                    break;
                default:
                    return PrintfError.BadFormat;
            }
            Pad(sb, str, conv, flags, width, prec, sign);
            return PrintfError.None;
        }

        /// <summary>[sign][0x / 0o when '#'][zeros up to the precision][digits].</summary>
        private static string FormatInteger(long v, bool alt, int prec, char type)
        {
            bool neg = v < 0;
            ulong mag = neg ? (ulong)(-(v + 1)) + 1UL : (ulong)v;
            string digits;
            string prefix = "";
            switch (type)
            {
                case 'o':
                    digits = ToBase(mag, 8);
                    prefix = alt ? "0o" : "";
                    break;
                case 'x':
                    digits = ToBase(mag, 16);
                    prefix = alt ? "0x" : "";
                    break;
                case 'X':
                    digits = ToBase(mag, 16).ToUpperInvariant();
                    prefix = alt ? "0X" : "";
                    break;
                default:
                    digits = mag.ToString(CultureInfo.InvariantCulture);
                    break;
            }
            if (prec > digits.Length)
                digits = new string('0', prec - digits.Length) + digits;
            return (neg ? "-" : "") + prefix + digits;
        }

        private static string ToBase(ulong v, int b)
        {
            if (v == 0)
                return "0";
            var c = new char[64];
            int p = 64;
            while (v != 0)
            {
                int d = (int)(v % (ulong)b);
                c[--p] = (char)(d < 10 ? '0' + d : 'a' + d - 10);
                v /= (ulong)b;
            }
            return new string(c, p, 64 - p);
        }

        /// <summary>The sign, the 0x / 0o prefix, the fill and the width around a converted
        /// value.</summary>
        private static void Pad(
            StringBuilder sb,
            string str,
            char ch,
            int flags,
            int width,
            int prec,
            bool sign
        )
        {
            // the fill is decided before the sign is looked at
            char fill = sign && (flags & FZero) != 0 ? '0' : ' ';
            int len = str.Length;
            if (
                (width == -1 || width <= len)
                && (prec == -1 || prec >= len)
                && (flags & (FSign | FBlank)) == 0
            )
            {
                sb.Append(str);
                return;
            }
            int pindex = 0;
            char signchar = '\0';
            if (sign)
            {
                char c0 = len > 0 ? str[0] : '\0';
                if (c0 == '-' || c0 == '+')
                {
                    signchar = c0;
                    len--;
                    pindex++;
                }
                else if ((flags & FSign) != 0)
                    signchar = '+';
                else if ((flags & FBlank) != 0)
                    signchar = ' ';
                else
                    sign = false;
            }
            if (width < len)
                width = len;
            bool altPrefix = (flags & FAlt) != 0 && (ch == 'x' || ch == 'X' || ch == 'o');
            if (sign)
            {
                if (fill != ' ')
                    sb.Append(signchar);
                if (width > len)
                    width--;
            }
            if (altPrefix)
            {
                if (fill != ' ')
                {
                    sb.Append('0');
                    sb.Append(ch);
                    pindex += 2;
                }
                width -= 2;
                if (width < 0)
                    width = 0;
                len -= 2;
            }
            if (width > len && (flags & FLjust) == 0)
            {
                sb.Append(fill, width - len);
                width = len;
            }
            if (fill == ' ')
            {
                if (sign)
                    sb.Append(signchar);
                if (altPrefix)
                {
                    sb.Append('0');
                    sb.Append(ch);
                    pindex += 2;
                }
            }
            if (len > 0)
                sb.Append(str, pindex, len);
            if (width > len)
                sb.Append(' ', width - len);
        }
    }
}
