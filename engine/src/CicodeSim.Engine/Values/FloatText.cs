using System;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace CicodeSim.Values
{
    /// <summary>Doubles to text and back, correctly rounded on every target.</summary>
    public static class FloatText
    {
        // --------------------------------------------------------------------- decomposition
        /// <summary>x = mant * 2^exp2 for a finite x (mant 0 for zero).</summary>
        public static void Decompose(double x, out bool neg, out ulong mant, out int exp2)
        {
            long bits = BitConverter.DoubleToInt64Bits(x);
            neg = bits < 0;
            int be = (int)((bits >> 52) & 0x7FF);
            ulong frac = (ulong)bits & 0xFFFFFFFFFFFFFUL;
            if (be == 0)
            {
                mant = frac;
                exp2 = -1074;
            }
            else
            {
                mant = frac | (1UL << 52);
                exp2 = be - 1075;
            }
        }

        private static readonly object Pow10Lock = new object();
        private static BigInteger[] _pow10 = BuildPow10(700);

        private static BigInteger[] BuildPow10(int size)
        {
            var a = new BigInteger[size];
            BigInteger v = BigInteger.One;
            for (int i = 0; i < size; i++)
            {
                a[i] = v;
                v *= 10;
            }
            return a;
        }

        /// <summary>10^n as a big integer (n &gt;= 0; cached up to 4000).</summary>
        public static BigInteger Pow10(int n)
        {
            if (n < 0)
                throw new ArgumentOutOfRangeException(nameof(n));
            var p = _pow10;
            if (n < p.Length)
                return p[n];
            if (n > 4000)
                return BigInteger.Pow(10, n);
            lock (Pow10Lock)
            {
                if (n < _pow10.Length)
                    return _pow10[n];
                _pow10 = BuildPow10(Math.Max(n + 1, Math.Min(4001, _pow10.Length * 2 + 32)));
                return _pow10[n];
            }
        }

        private static readonly double[] ExactPow10 =
        {
            1e0,
            1e1,
            1e2,
            1e3,
            1e4,
            1e5,
            1e6,
            1e7,
            1e8,
            1e9,
            1e10,
            1e11,
            1e12,
            1e13,
            1e14,
            1e15,
            1e16,
            1e17,
            1e18,
            1e19,
            1e20,
            1e21,
            1e22,
        };

        /// <summary>The number of bits of |x| (0 for zero).</summary>
        public static int BitLength(BigInteger x)
        {
            if (x.Sign < 0)
                x = -x;
            if (x.IsZero)
                return 0;
            byte[] b = x.ToByteArray(); // little endian, may carry a sign byte 0
            int top = b.Length - 1;
            while (top > 0 && b[top] == 0)
                top--;
            int bits = top * 8;
            int t = b[top];
            while (t != 0)
            {
                bits++;
                t >>= 1;
            }
            return bits;
        }

        private static int BitLength(ulong x)
        {
            int n = 0;
            while (x != 0)
            {
                n++;
                x >>= 1;
            }
            return n;
        }

        /// <summary>V = mant * 2^exp2 as num / den.</summary>
        private static void Rational(ulong mant, int exp2, out BigInteger num, out BigInteger den)
        {
            num = new BigInteger(mant);
            den = BigInteger.One;
            if (exp2 >= 0)
                num <<= exp2;
            else
                den <<= -exp2;
        }

        /// <summary>The estimate of decpt (V in [10^(k-1), 10^k)), possibly off by one.</summary>
        private static int EstimateDecpt(ulong mant, int exp2)
        {
            double l = Math.Log10((double)mant) + exp2 * 0.30102999566398119521;
            return (int)Math.Floor(l) + 1;
        }

        /// <summary>num / den rounded half to even.</summary>
        private static BigInteger DivRoundEven(BigInteger num, BigInteger den, out BigInteger floor)
        {
            floor = BigInteger.DivRem(num, den, out var rem);
            if (rem.IsZero)
                return floor;
            int c = (rem << 1).CompareTo(den);
            if (c > 0 || (c == 0 && !floor.IsEven))
                return floor + 1;
            return floor;
        }

        // --------------------------------------------------------------------- digits
        /// <summary>The value rounded (half to even) to <paramref name="nsig"/> significant digits:
        /// exactly nsig digit characters and the decimal point position (the digit string before
        /// trailing zeros are dropped). The value is positive and finite.</summary>
        public static string SignificantDigits(ulong mant, int exp2, int nsig, out int decpt)
        {
            int k = EstimateDecpt(mant, exp2);
            BigInteger lowLimit = Pow10(nsig - 1);
            BigInteger highLimit = Pow10(nsig);
            for (int guard = 0; guard < 6; guard++)
            {
                Rational(mant, exp2, out var num, out var den);
                int s = nsig - k;
                if (s >= 0)
                    num *= Pow10(s);
                else
                    den *= Pow10(-s);
                BigInteger q = DivRoundEven(num, den, out var floor);
                if (floor >= highLimit)
                {
                    k++;
                    continue;
                }
                if (floor < lowLimit)
                {
                    // the estimate was one too high (the rounded q may still reach lowLimit)
                    k--;
                    continue;
                }
                if (q == highLimit)
                {
                    // rounding carried into a new digit
                    decpt = k + 1;
                    return "1" + new string('0', nsig - 1);
                }
                decpt = k;
                return q.ToString(CultureInfo.InvariantCulture);
            }
            throw new InvalidOperationException("SignificantDigits did not converge");
        }

        /// <summary>V * 10^ndec rounded half to even, for a positive finite V.</summary>
        public static BigInteger FixedDigits(ulong mant, int exp2, int ndec)
        {
            Rational(mant, exp2, out var num, out var den);
            if (ndec >= 0)
                num *= Pow10(ndec);
            else
                den *= Pow10(-ndec);
            return DivRoundEven(num, den, out _);
        }

        /// <summary>The shortest digits that read back as x (finite, &gt; 0), the closest such
        /// string when two of that length read back: (digits without trailing zeros, decimal
        /// exponent of the first digit).</summary>
        public static void ShortestDigits(double x, out string digits, out int point)
        {
            if (FastShortest(x, out digits, out point))
                return;
            // If a string of at most 15 digits reads back, the correctly rounded 15-digit one is it
            // (15-digit decimals are 1e-14 apart relative, the interval that reads back as x is at
            // most 4.4e-16 wide), so trying 15, 16, 17 digits finds the shortest; at 16 digits two
            // may read back and the closest wins. A subnormal has fewer bits: every length is tried.
            for (int p = x < 2.2250738585072014e-308 ? 1 : 15; p <= 17; p++)
            {
                RoundSig(x, p, out BigInteger d, out int k);
                if (ReadsBack(x, d, k))
                {
                    Finish(d, k, out digits, out point);
                    return;
                }
                // at a power of two the interval is lopsided: a neighbour may read back
                BigInteger up = d + 1,
                    down = d - 1;
                bool okUp = up < Pow10(p) && ReadsBack(x, up, k);
                bool okDown = down >= Pow10(p - 1) && ReadsBack(x, down, k);
                if (okUp || okDown)
                {
                    BigInteger pick =
                        okUp && okDown ? Closer(x, up, down, k)
                        : okUp ? up
                        : down;
                    Finish(pick, k, out digits, out point);
                    return;
                }
            }
            RoundSig(x, 17, out BigInteger d17, out int k17);
            Finish(d17, k17, out digits, out point);
        }

        /// <summary>Fast shortest digits for short decimals.</summary>
        private static bool FastShortest(double x, out string digits, out int point)
        {
            digits = "";
            point = 0;
            if (!(x >= 1e-7 && x < 1e15))
                return false;
            for (int k = 0; k <= 15; k++)
            {
                double scale = ExactPow10[k];
                double y = x * scale;
                if (y >= 9007199254740992.0) // 2^53
                    return false;
                double c = Math.Round(y);
                if (c == 0)
                    continue;
                if (c / scale != x)
                    continue;
                if ((c + 1) / scale == x || (c - 1) / scale == x)
                    return false;
                // the digits of c with k decimals: c = digits * 10^t (trailing zeros dropped)
                string s = ((long)c).ToString(CultureInfo.InvariantCulture);
                int tz = 0;
                while (tz < s.Length - 1 && s[s.Length - 1 - tz] == '0')
                    tz++;
                digits = s.Substring(0, s.Length - tz);
                point = s.Length - 1 - k;
                return true;
            }
            return false;
        }

        /// <summary>x (finite, &gt; 0) rounded to p significant digits, ties to even on the exact
        /// value: (d, k) with x ~ d * 10^k, 10^(p-1) &lt;= d &lt; 10^p.</summary>
        private static void RoundSig(double x, int p, out BigInteger d, out int k)
        {
            Decompose(x, out _, out ulong m, out int e);
            int X = (int)Math.Floor(Math.Log10(x)); // estimate of floor(log10 x)
            while (CompareWithPow10(m, e, X) < 0)
                X--;
            while (CompareWithPow10(m, e, X + 1) >= 0)
                X++;
            k = X - p + 1;
            Rational(m, e, out var num, out var den);
            if (k >= 0)
                den *= Pow10(k);
            else
                num *= Pow10(-k);
            d = DivRoundEven(num, den, out _);
            if (d == Pow10(p))
            {
                d /= 10;
                k++;
            }
        }

        /// <summary>sign(m*2^e - 10^n).</summary>
        private static int CompareWithPow10(ulong m, int e, int n)
        {
            BigInteger a = m,
                b = BigInteger.One;
            if (e >= 0)
                a <<= e;
            else
                b <<= -e;
            if (n >= 0)
                b *= Pow10(n);
            else
                a *= Pow10(-n);
            return a.CompareTo(b);
        }

        /// <summary>Does d * 10^k read back as x (finite, &gt; 0)?</summary>
        private static bool ReadsBack(double x, BigInteger d, int k)
        {
            Decompose(x, out _, out ulong m, out int e);
            long bits = BitConverter.DoubleToInt64Bits(x);
            bool pow2Low = (bits & 0xFFFFFFFFFFFFFL) == 0 && ((bits >> 52) & 0x7FF) > 1; // the next lower double is half as far
            // boundaries in units of 2^(e-2): x = 4m, high = 4m + 2, low = 4m - 2 (or - 1)
            BigInteger hi = 4 * (BigInteger)m + 2;
            BigInteger lo = 4 * (BigInteger)m - (pow2Low ? 1 : 2);
            bool inclusive = (m & 1) == 0;
            int s = e - 2;
            BigInteger D = d,
                scaleB = BigInteger.One;
            if (k >= 0)
                D *= Pow10(k);
            else
                scaleB *= Pow10(-k);
            BigInteger hiS = hi * scaleB,
                loS = lo * scaleB;
            if (s >= 0)
            {
                hiS <<= s;
                loS <<= s;
            }
            else
                D <<= -s;
            int ch = D.CompareTo(hiS),
                cl = D.CompareTo(loS);
            if (inclusive)
                return ch <= 0 && cl >= 0;
            return ch < 0 && cl > 0;
        }

        private static BigInteger Closer(double x, BigInteger a, BigInteger b, int k)
        {
            // |a*10^k - x| vs |b*10^k - x|, exactly
            Decompose(x, out _, out ulong m, out int e);
            BigInteger X = m,
                S = BigInteger.One;
            if (e >= 0)
                X <<= e;
            else
                S <<= -e;
            BigInteger A = a * S,
                B = b * S;
            if (k >= 0)
            {
                A *= Pow10(k);
                B *= Pow10(k);
            }
            else
                X *= Pow10(-k);
            int c = BigInteger.Abs(A - X).CompareTo(BigInteger.Abs(B - X));
            if (c < 0)
                return a;
            if (c > 0)
                return b;
            return a.IsEven ? a : b;
        }

        private static void Finish(BigInteger d, int k, out string digits, out int point)
        {
            string s = d.ToString(CultureInfo.InvariantCulture);
            int trailing = 0;
            while (trailing < s.Length - 1 && s[s.Length - 1 - trailing] == '0')
                trailing++;
            digits = s.Substring(0, s.Length - trailing);
            point = k + s.Length - 1; // the decimal exponent of the first digit
        }

        // --------------------------------------------------------------------- text
        /// <summary>The shortest round-trip form of a double.</summary>
        public static string Shortest(double x)
        {
            if (double.IsNaN(x))
                return "nan";
            if (double.IsPositiveInfinity(x))
                return "inf";
            if (double.IsNegativeInfinity(x))
                return "-inf";
            if (x == 0)
                return BitConverter.DoubleToInt64Bits(x) < 0 ? "-0.0" : "0.0";
            // exact integers below 2^53: their own digits are the shortest that read back
            if (x == Math.Floor(x) && Math.Abs(x) < 9007199254740992.0)
                return ((long)x).ToString(CultureInfo.InvariantCulture) + ".0";
            ShortestDigits(Math.Abs(x), out string digits, out int X);
            var sb = new StringBuilder(24);
            if (x < 0)
                sb.Append('-');
            int decpt = X + 1;
            int n = digits.Length;
            if (decpt <= -4 || decpt > 16)
            {
                sb.Append(digits[0]);
                if (n > 1)
                    sb.Append('.').Append(digits, 1, n - 1);
                sb.Append('e').Append(X < 0 ? '-' : '+');
                int ax = Math.Abs(X);
                if (ax < 10)
                    sb.Append('0');
                sb.Append(ax.ToString(CultureInfo.InvariantCulture));
            }
            else if (decpt <= 0)
                sb.Append("0.").Append('0', -decpt).Append(digits);
            else if (decpt >= n)
                sb.Append(digits).Append('0', decpt - n).Append(".0");
            else
                sb.Append(digits, 0, decpt).Append('.').Append(digits, decpt, n - decpt);
            return sb.ToString();
        }

        /// <summary>The NaN C's NAN macro and strtod's "nan" without a sign stand for in the tool
        /// formats (a JSON NaN, a scenario's "nan"): the positive quiet NaN 0x7FF8000000000000.</summary>
        public static readonly double QuietNaN = BitConverter.Int64BitsToDouble(0x7FF8000000000000L);

        /// <summary>-NAN: 0xFFF8000000000000, the indefinite value.</summary>
        public static readonly double NegativeQuietNaN = BitConverter.Int64BitsToDouble(
            unchecked((long)0xFFF8000000000000UL)
        );

        /// <summary>A NaN as the runtime's printf writes it.</summary>
        public static string NanText(double x, bool upper)
        {
            ulong b = (ulong)BitConverter.DoubleToInt64Bits(x) | 0x0008000000000000UL;
            string s =
                b == 0xFFF8000000000000UL ? "-nan(ind)"
                : (b >> 63) != 0 ? "-nan"
                : "nan";
            return upper ? s.ToUpperInvariant() : s;
        }

        /// <summary>printf e / E / f / F / g / G of a double.</summary>
        public static string Format(double val, char conv, int precision, bool alt)
        {
            bool upper = false;
            int mode;
            char code = conv;
            switch (conv)
            {
                case 'E':
                    upper = true;
                    code = 'e';
                    mode = 2;
                    precision++;
                    break;
                case 'e':
                    mode = 2;
                    precision++;
                    break;
                case 'F':
                    upper = true;
                    code = 'f';
                    mode = 3;
                    break;
                case 'f':
                    mode = 3;
                    break;
                case 'G':
                    upper = true;
                    code = 'g';
                    mode = 2;
                    if (precision == 0)
                        precision = 1;
                    break;
                case 'g':
                    mode = 2;
                    if (precision == 0)
                        precision = 1;
                    break;
                default:
                    throw new ArgumentException("printf conversion " + conv);
            }
            if (double.IsNaN(val))
                return NanText(val, upper);
            if (double.IsInfinity(val))
            {
                string inf = upper ? "INF" : "inf";
                return val < 0 ? "-" + inf : inf;
            }
            string digits = Digits(val, mode, precision, out int decpt, out bool sign);
            int digitsLen = digits.Length;
            int vdigitsEnd = digitsLen;
            bool useExp = false;
            int exp = 0;
            switch (code)
            {
                case 'e':
                    useExp = true;
                    vdigitsEnd = precision;
                    break;
                case 'f':
                    vdigitsEnd = decpt + precision;
                    break;
                case 'g':
                    if (decpt <= -4 || decpt > precision)
                        useExp = true;
                    if (alt)
                        vdigitsEnd = precision;
                    break;
            }
            if (useExp)
            {
                exp = decpt - 1;
                decpt = 1;
            }
            int vdigitsStart = decpt <= 0 ? decpt - 1 : 0;
            vdigitsEnd = vdigitsEnd > decpt ? vdigitsEnd : decpt;

            var sb = new StringBuilder(digitsLen + 32);
            if (sign)
                sb.Append('-');
            if (decpt <= 0)
            {
                sb.Append('0', decpt - vdigitsStart);
                sb.Append('.');
                sb.Append('0', -decpt);
            }
            else
                sb.Append('0', -vdigitsStart);
            if (0 < decpt && decpt <= digitsLen)
            {
                sb.Append(digits, 0, decpt);
                sb.Append('.');
                sb.Append(digits, decpt, digitsLen - decpt);
            }
            else
                sb.Append(digits);
            if (digitsLen < decpt)
            {
                sb.Append('0', decpt - digitsLen);
                sb.Append('.');
                sb.Append('0', vdigitsEnd - decpt);
            }
            else
                sb.Append('0', vdigitsEnd - digitsLen);
            if (sb.Length > 0 && sb[sb.Length - 1] == '.' && !alt)
                sb.Length--;
            if (useExp)
            {
                sb.Append(upper ? 'E' : 'e');
                sb.Append(exp < 0 ? '-' : '+');
                int a = Math.Abs(exp);
                if (a < 10)
                    sb.Append('0');
                sb.Append(a.ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>The digits of val for mode 2 (ndigits significant) or 3 (ndigits after the
        /// point): trailing zeros dropped; zero = "0" at decpt 1.</summary>
        private static string Digits(double val, int mode, int ndigits, out int decpt, out bool sign)
        {
            Decompose(val, out sign, out ulong mant, out int exp2);
            if (mant == 0)
            {
                decpt = 1;
                return "0";
            }
            if (mode == 2)
            {
                string s = SignificantDigits(mant, exp2, Math.Max(1, ndigits), out decpt);
                s = s.TrimEnd('0');
                return s.Length == 0 ? "0" : s;
            }
            BigInteger r = FixedDigits(mant, exp2, ndigits);
            if (r.IsZero)
            {
                decpt = -ndigits;
                return "";
            }
            string t = r.ToString(CultureInfo.InvariantCulture);
            decpt = t.Length - ndigits;
            return t.TrimEnd('0');
        }

        /// <summary>x rounded to <paramref name="ndigits"/> decimals (negative: tens, hundreds
        /// ...), ties to even on the exact binary value; the result is the double nearest the
        /// rounded decimal. OverflowException when that is beyond the largest double.</summary>
        public static double Round(double x, int ndigits)
        {
            if (double.IsNaN(x) || double.IsInfinity(x) || x == 0)
                return x;
            if (ndigits > 323)
                return x;
            if (ndigits < -308)
                return x < 0 ? -0.0 : 0.0;
            bool neg = x < 0;
            double ax = Math.Abs(x);
            if (ndigits >= 0 && ndigits <= 22)
            {
                double scale = ExactPow10[ndigits];
                double y = ax * scale;
                if (y < 4503599627370496.0) // 2^52
                {
                    double fl = Math.Floor(y);
                    double frac = y - fl;
                    double err = y * 2.3e-16 + 1e-300;
                    if (Math.Abs(frac - 0.5) > 4 * err)
                    {
                        double q = frac > 0.5 ? fl + 1 : fl;
                        double r = q / scale;
                        return neg ? -r : r;
                    }
                }
            }
            Decompose(ax, out _, out ulong m, out int e);
            BigInteger dq = FixedDigits(m, e, ndigits);
            double res;
            if (ndigits >= 0)
                res = FromRatio(dq, Pow10(ndigits), out _);
            else
                res = FromRatio(dq * Pow10(-ndigits), BigInteger.One, out _);
            if (double.IsInfinity(res))
                throw new OverflowException("rounded value too large to represent");
            return neg ? -res : res;
        }

        // --------------------------------------------------------------------- to double
        /// <summary>Significant digits <see cref="FromDigits"/> works with (more than the 768 any
        /// rounding decision needs).</summary>
        internal const int MaxSignificant = 800;

        /// <summary>The double nearest digits * 10^exp (digits = ASCII decimal digits).</summary>
        public static double FromDigits(string digits, long exp)
        {
            string ds = digits.TrimStart('0');
            if (ds.Length == 0)
                return 0.0;
            // trailing zeros into the exponent
            int tz = 0;
            while (tz < ds.Length - 1 && ds[ds.Length - 1 - tz] == '0')
                tz++;
            if (tz > 0)
            {
                ds = ds.Substring(0, ds.Length - tz);
                exp += tz;
            }
            if (ds.Length > MaxSignificant)
            {
                // The first MaxSignificant digits and a sticky 1 for the rest (nonzero: ds ends in a
                // nonzero digit) round like the whole string: a midpoint between two doubles or the
                // overflow / underflow boundary has at most 768 significant digits, so none lies
                // strictly between the truncated value and the next MaxSignificant-digit number,
                // and both the true value and the stand-in lie strictly inside that interval.
                // Without it BigInteger.Parse below is superlinear in the digit count.
                exp += ds.Length - (MaxSignificant + 1);
                ds = ds.Substring(0, MaxSignificant) + "1";
            }
            if (exp + ds.Length > 310)
                return double.PositiveInfinity;
            if (exp + ds.Length < -330)
                return 0.0;
            if (ds.Length <= 15 && exp >= -22 && exp <= 22)
            {
                double v = double.Parse(ds, NumberStyles.None, CultureInfo.InvariantCulture); // exact
                return exp >= 0 ? v * ExactPow10[exp] : v / ExactPow10[-exp];
            }
            BigInteger N = BigInteger.Parse(ds, CultureInfo.InvariantCulture);
            if (exp >= 0)
                return FromRatio(N * Pow10((int)exp), BigInteger.One, out _);
            return FromRatio(N, Pow10((int)(-exp)), out _);
        }

        /// <summary>The double nearest num / den (both positive), ties to even;
        /// <paramref name="overflow"/> (and an infinity) when it rounds past the largest
        /// double.</summary>
        public static double FromRatio(BigInteger num, BigInteger den, out bool overflow)
        {
            overflow = false;
            if (num.IsZero)
                return 0.0;
            int nb = BitLength(num),
                db = BitLength(den);
            int e = nb - db - 56;
            BigInteger n = num,
                d = den;
            if (e >= 0)
                d <<= e;
            else
                n <<= -e;
            BigInteger qb = BigInteger.DivRem(n, d, out var rem);
            bool sticky = !rem.IsZero;
            ulong q = (ulong)qb; // 56 or 57 bits
            int qbits = BitLength(q);
            int E = qbits - 1 + e; // value in [2^E, 2^(E+1))
            if (E > 1023)
            {
                overflow = true;
                return double.PositiveInfinity;
            }
            int shift = E >= -1022 ? qbits - 53 : -1074 - e;
            ulong m;
            if (shift <= 0)
                m = q << -shift; // cannot happen for normal values (shift >= 3)
            else if (shift >= 64)
                return 0.0; // below half of the smallest subnormal
            else
            {
                m = q >> shift;
                ulong mask = (1UL << shift) - 1;
                ulong dropped = q & mask;
                ulong half = 1UL << (shift - 1);
                if (dropped > half || (dropped == half && (sticky || (m & 1) == 1)))
                    m++;
            }
            int lsbExp = e + shift; // value = m * 2^lsbExp
            if (m >= (1UL << 53))
            {
                m >>= 1;
                lsbExp++;
            }
            if (m >= (1UL << 52))
            {
                int biased = lsbExp + 1075;
                if (biased >= 2047)
                {
                    overflow = true;
                    return double.PositiveInfinity;
                }
                long bits = ((long)biased << 52) | (long)(m - (1UL << 52));
                return BitConverter.Int64BitsToDouble(bits);
            }
            // subnormal (lsbExp == -1074) or zero
            return BitConverter.Int64BitsToDouble((long)m);
        }

        /// <summary>The double nearest an integer, ties to even (.NET Framework's own BigInteger
        /// conversion is not correctly rounded); an infinity beyond the largest double.</summary>
        public static double FromInteger(BigInteger v)
        {
            if (v.Sign == 0)
                return 0.0;
            double r = FromRatio(BigInteger.Abs(v), BigInteger.One, out _);
            return v.Sign < 0 ? -r : r;
        }

        /// <summary>The double nearest an unsigned 64-bit integer, ties to even.</summary>
        public static double FromUInt64(ulong u)
        {
            if (u <= long.MaxValue)
                return (long)u;
            ulong h = (u >> 1) | (u & 1);
            return (long)h * 2.0;
        }

        /// <summary>a / b for two integers as the double nearest the exact quotient (a long
        /// beyond 2^53 converted first would round twice).</summary>
        public static double Quotient(long a, long b)
        {
            if (b == 0)
                throw new DivideByZeroException();
            if (Math.Abs(a) < (1L << 53) && Math.Abs(b) < (1L << 53))
                return (double)a / b;
            bool neg = (a < 0) != (b < 0);
            double r = FromRatio(BigInteger.Abs(a), BigInteger.Abs(b), out _);
            return neg ? -r : r;
        }

        /// <summary>The low 64 bits of x truncated toward zero, for a finite double.</summary>
        public static long TruncLow64(double x)
        {
            if (x > -9.2e18 && x < 9.2e18)
                return (long)x;
            Decompose(x, out bool neg, out ulong mant, out int exp2);
            // |x| >= 2^63: an integer; exp2 >= 11
            BigInteger v = new BigInteger(mant) << exp2;
            if (neg)
                v = -v;
            BigInteger low = v & ulong.MaxValue;
            return unchecked((long)(ulong)low);
        }

        // --------------------------------------------------------------------- parsing
        /// <summary>The double nearest a decimal literal already matched by
        /// [+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)? (s[start, end); the exponent part may also be
        /// absent or incomplete: it is then not read).</summary>
        public static double ParseDecimal(string s, int start, int end)
        {
            int i = start;
            bool neg = false;
            if (i < end && (s[i] == '+' || s[i] == '-'))
            {
                neg = s[i] == '-';
                i++;
            }
            var digits = new StringBuilder(Math.Min(end - i, MaxSignificant + 2));
            int fracDigits = 0;
            bool inFrac = false;
            for (; i < end; i++)
            {
                char ch = s[i];
                if (ch == '.')
                {
                    inFrac = true;
                    continue;
                }
                if (ch < '0' || ch > '9')
                    break;
                if (inFrac)
                    fracDigits++;
                if (digits.Length == 0 && ch == '0')
                    continue; // leading zero
                digits.Append(ch);
            }
            long exp10 = 0;
            if (i < end && (s[i] == 'e' || s[i] == 'E'))
            {
                int save = i;
                i++;
                bool eneg = false;
                if (i < end && (s[i] == '+' || s[i] == '-'))
                {
                    eneg = s[i] == '-';
                    i++;
                }
                long ev = 0;
                int e0 = i;
                for (; i < end; i++)
                {
                    char ch = s[i];
                    if (ch < '0' || ch > '9')
                        break;
                    if (ev < 1000000000L)
                        ev = ev * 10 + (ch - '0');
                }
                if (i == e0)
                    i = save; // 'e' without digits: not part of the number
                else
                    exp10 = eneg ? -ev : ev;
            }
            double r = FromDigits(digits.ToString(), exp10 - fracDigits);
            return neg ? -r : r;
        }

        /// <summary>The double nearest a hexadecimal literal: "0x" (optional) + hex digits
        /// [. hex digits] [p [+-] decimal exponent], no sign; <paramref name="overflow"/> (and an
        /// infinity) beyond the largest double.</summary>
        public static double ParseHex(string s, out bool overflow)
        {
            overflow = false;
            int i = 0;
            if (s.Length >= 2 && s[0] == '0' && (s[1] == 'x' || s[1] == 'X'))
                i = 2;
            BigInteger m = BigInteger.Zero;
            int frac = 0;
            bool inFrac = false;
            for (; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch == '.')
                {
                    inFrac = true;
                    continue;
                }
                int h = HexVal(ch);
                if (h < 0)
                    break;
                m = (m << 4) + h;
                if (inFrac)
                    frac++;
            }
            long pexp = 0;
            if (i < s.Length && (s[i] == 'p' || s[i] == 'P'))
            {
                i++;
                bool eneg = false;
                if (i < s.Length && (s[i] == '+' || s[i] == '-'))
                {
                    eneg = s[i] == '-';
                    i++;
                }
                for (; i < s.Length; i++)
                {
                    char ch = s[i];
                    if (ch < '0' || ch > '9')
                        break;
                    if (pexp < 100000000000L)
                        pexp = pexp * 10 + (ch - '0');
                }
                if (eneg)
                    pexp = -pexp;
            }
            if (m.IsZero)
                return 0.0;
            long e2 = pexp - 4L * frac;
            int mb = BitLength(m);
            if (mb + e2 > 1030)
            {
                overflow = true;
                return double.PositiveInfinity;
            }
            if (mb + e2 < -1100)
                return 0.0;
            BigInteger num = m,
                den = BigInteger.One;
            if (e2 >= 0)
                num <<= (int)e2;
            else
                den <<= (int)-e2;
            return FromRatio(num, den, out overflow);
        }

        private static int HexVal(char ch)
        {
            if (ch >= '0' && ch <= '9')
                return ch - '0';
            if (ch >= 'a' && ch <= 'f')
                return ch - 'a' + 10;
            if (ch >= 'A' && ch <= 'F')
                return ch - 'A' + 10;
            return -1;
        }

        /// <summary>Blanks C isspace skips: space, \t, \n, \v, \f, \r.</summary>
        public static bool IsBlank(char c) => c == ' ' || (c >= '\t' && c <= '\r');

        /// <summary>Parse a number written as text.</summary>
        public static bool TryParseNumber(string? text, out double value)
        {
            value = 0;
            if (text == null)
                return false;
            int a = 0,
                b = text.Length;
            while (a < b && IsBlank(text[a]))
                a++;
            while (b > a && IsBlank(text[b - 1]))
                b--;
            if (a == b)
                return false;
            int i = a;
            bool neg = false;
            if (text[i] == '+' || text[i] == '-')
            {
                neg = text[i] == '-';
                i++;
            }
            int len = b - i;
            if (
                len == 3 && string.Compare(text, i, "inf", 0, 3, StringComparison.OrdinalIgnoreCase) == 0
                || len == 8
                    && string.Compare(text, i, "infinity", 0, 8, StringComparison.OrdinalIgnoreCase) == 0
            )
            {
                value = neg ? double.NegativeInfinity : double.PositiveInfinity;
                return true;
            }
            if (len == 3 && string.Compare(text, i, "nan", 0, 3, StringComparison.OrdinalIgnoreCase) == 0)
            {
                // C's NAN and -NAN (0x7FF8000000000000 / 0xFFF8000000000000)
                value = neg ? NegativeQuietNaN : QuietNaN;
                return true;
            }
            int p = i,
                intDigits = 0,
                fracDigits = 0;
            while (p < b && text[p] >= '0' && text[p] <= '9')
            {
                p++;
                intDigits++;
            }
            if (p < b && text[p] == '.')
            {
                p++;
                while (p < b && text[p] >= '0' && text[p] <= '9')
                {
                    p++;
                    fracDigits++;
                }
            }
            if (intDigits == 0 && fracDigits == 0)
                return false;
            if (p < b && (text[p] == 'e' || text[p] == 'E'))
            {
                p++;
                if (p < b && (text[p] == '+' || text[p] == '-'))
                    p++;
                int e0 = p;
                while (p < b && text[p] >= '0' && text[p] <= '9')
                    p++;
                if (p == e0)
                    return false;
            }
            if (p != b)
                return false;
            value = ParseDecimal(text, a, b);
            return true;
        }
    }
}
