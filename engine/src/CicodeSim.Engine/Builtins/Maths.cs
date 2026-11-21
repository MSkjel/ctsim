using System;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>
    /// Maths, conversion, bit and quality built-ins. Domain errors raise 274 without halting and
    /// return 0.
    /// </summary>
    public static class Maths
    {
        public const int ERange = 257;
        public const int EInvalidArg = 274;

        /// <summary>.</summary>
        public const double PI = 3.141592653589793;

        private static Value I(long v) => Value.Long(v);

        private static Value R(double v) => Value.Real(v);

        /// <summary>(double)(int)x with cvttsd2si: outside the int range = INT_MIN.</summary>
        private static double Trunc32(double x)
        {
            if (double.IsNaN(x) || !(-2147483649.0 < x && x < 2147483648.0))
                return -2147483648.0;
            return (double)(long)x;
        }

        /// <summary>pow with the special cases of C99 Annex F for a NaN or an infinite argument;
        /// error 1 = a domain or pole error (a NaN result, or 0 to a negative power), 2 = an
        /// overflow.</summary>
        public static double CrtPow(double x, double y, out int error)
        {
            error = 0;
            if (!IsFinite(x) || !IsFinite(y))
            {
                if (double.IsNaN(x))
                    return y == 0.0 ? 1.0 : x;
                if (double.IsNaN(y))
                    return x == 1.0 ? 1.0 : y;
                if (double.IsInfinity(x))
                {
                    bool oddY = IsFinite(y) && Math.Abs(y) % 2.0 == 1.0;
                    if (y > 0.0)
                        return oddY ? x : Math.Abs(x);
                    if (y == 0.0)
                        return 1.0;
                    return oddY ? Ops.CopySign(0.0, x) : 0.0;
                }
                // y infinite
                if (Math.Abs(x) == 1.0)
                    return 1.0;
                if (y > 0.0 && Math.Abs(x) > 1.0)
                    return y;
                if (y < 0.0 && Math.Abs(x) < 1.0)
                    return -y;
                return 0.0;
            }
            double r = CMath.Pow(x, y);
            if (!IsFinite(r))
            {
                if (double.IsNaN(r))
                    error = 1;
                else if (x == 0.0)
                    error = 1;
                else
                    error = 2;
            }
            return r;
        }

        private static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);

        private static BuiltinImpl Trig(Func<double, double> fn) =>
            (t, a) =>
            {
                double x = A.R(a.Get(0));
                if (-1000000000.0 <= x && x < 1000000000.0)
                    return R(fn(x));
                t.RaiseError(EInvalidArg, false);
                return R(0.0);
            };

        private static BuiltinImpl Arc(Func<double, double> fn) =>
            (t, a) =>
            {
                double x = A.R(a.Get(0));
                if (-1.0 <= x && x <= 1.0)
                    return R(fn(x));
                t.RaiseError(EInvalidArg, false);
                return R(0.0);
            };

        internal static void Register(Action<string, string, BuiltinImpl> add)
        {
            add("Abs", "(clears the sign bit)", (t, a) => R(Ops.CopySign(A.R(a.Get(0)), 1.0)));

            add("Max", "(a <= b ? b : a)", (t, a) =>
            {
                double x = A.R(a.Get(0)),
                    y = A.R(a.Get(1));
                return R(x <= y ? y : x);
            });

            add("Min", "(b <= a ? b : a)", (t, a) =>
            {
                double x = A.R(a.Get(0)),
                    y = A.R(a.Get(1));
                return R(y <= x ? y : x);
            });

            add(
                "Round",
                "(truncate, +1 when the rest is >= 0.5: negative numbers truncate toward zero)",
                (t, a) =>
                {
                    double x = A.R(a.Get(0));
                    long p = A.Short(A.I(a.Get(1)));
                    long m = 1;
                    if (p > 0)
                    {
                        for (long k = 0; k < p; k++)
                            m = (int)(m * 10);
                        x = (double)m * x;
                        double d = Trunc32(x);
                        if (0.5 <= x - d)
                            d += 1.0;
                        if (m == 0)
                            return R(double.PositiveInfinity); // a division by zero
                        return R(d / (double)m);
                    }
                    if (p < 0)
                    {
                        long cnt = (-p) & 0xFFFF;
                        for (long k = 0; k < cnt; k++)
                            m = (int)(m * 10);
                    }
                    if (m == 0)
                        x = x != 0.0 ? Ops.CopySign(double.PositiveInfinity, x) : double.NaN;
                    else
                        x = x / (double)m;
                    double d2 = Trunc32(x);
                    if (0.5 <= x - d2)
                        d2 += 1.0;
                    return R((double)m * d2);
                }
            );

            add("Pow", "(CRT pow)", (t, a) =>
            {
                double x = A.R(a.Get(0)),
                    y = A.R(a.Get(1));
                double r = CrtPow(x, y, out int e);
                // an overflow or a pole is +inf whatever its sign (the UCRT's pow gives -inf for a
                // negative base and an odd power: to verify against the runtime)
                if (e == 2)
                    return R(double.PositiveInfinity);
                if (e == 1)
                    return R(x == 0.0 && y < 0 ? double.PositiveInfinity : double.NaN);
                return R(r);
            });

            add("Sqrt", "(x < 0: 274, 0)", (t, a) =>
            {
                double x = A.R(a.Get(0));
                if (x < 0.0)
                {
                    t.RaiseError(EInvalidArg, false);
                    return R(0.0);
                }
                return R(double.IsNaN(x) ? x : Math.Sqrt(x));
            });

            add("Exp", "(x >= 88: 274, 0)", (t, a) =>
            {
                double x = A.R(a.Get(0));
                if (x < 88.0)
                    return R(CMath.Exp(x));
                t.RaiseError(EInvalidArg, false);
                return R(0.0);
            });

            add("Ln", "(x <= 0: 274, 0)", (t, a) =>
            {
                double x = A.R(a.Get(0));
                if (0.0 < x)
                    return R(CMath.Log(x));
                t.RaiseError(EInvalidArg, false);
                return R(0.0);
            });

            add("Log", "(log10; x <= 0: 274, 0)", (t, a) =>
            {
                double x = A.R(a.Get(0));
                if (0.0 < x)
                    return R(CMath.Log10(x));
                t.RaiseError(EInvalidArg, false);
                return R(0.0);
            });

            add("Sin", "(|x| < 1e9 else 274)", Trig(CMath.Sin));
            add("Cos", "(|x| < 1e9 else 274)", Trig(CMath.Cos));
            add("Tan", "(|x| < 1e9 else 274)", Trig(CMath.Tan));
            add("ArcTan", "(|x| < 1e9 else 274)", Trig(CMath.Atan));
            add("ArcSin", "(-1..1 else 274)", Arc(CMath.Asin));
            add("ArcCos", "(-1..1 else 274)", Arc(CMath.Acos));

            add("Fact", "(0..13 else 274; 32-bit product)", (t, a) =>
            {
                long n = A.I(a.Get(0));
                if (n < 0 || n > 13)
                {
                    t.RaiseError(EInvalidArg, false);
                    return I(0);
                }
                long v = 1;
                for (long k = 2; k <= n; k++)
                    v = (int)(v * k);
                return I(v);
            });

            add("Sign", "", (t, a) =>
            {
                double x = A.R(a.Get(0));
                return R(0.0 < x ? 1.0 : (x < 0.0 ? -1.0 : 0.0));
            });

            add("Pi", "", (t, a) => R(PI));

            add("DegToRad", "(x / 180 * pi)", (t, a) => R(A.R(a.Get(0)) / 180.0 * PI));

            add("RadToDeg", "(x * 180 / pi)", (t, a) => R(A.R(a.Get(0)) * 180.0 / PI));

            add("LowByte", "", (t, a) => I(A.I(a.Get(0)) & 0xFF));

            add("HighByte", "", (t, a) => I((A.I(a.Get(0)) >> 8) & 0xFF));

            add("LowWord", "", (t, a) => I(A.I(a.Get(0)) & 0xFFFF));

            add("HighWord", "(unsigned shift)", (t, a) => I((A.I(a.Get(0)) & 0xFFFFFFFFL) >> 16));

            add("IntToReal", "", (t, a) => R(A.I(a.Get(0))));

            add("StrToValue", "(an empty or not fully numeric string HALTS the task)", (t, a) =>
            {
                string s = A.SkipSpace(A.S(a.Get(0)));
                if (s.Length == 0)
                {
                    t.Vm.Halt(t, "StrToValue: not a number");
                    return R(0.0);
                }
                double v = Crt.Strtod(s, out int used);
                if (used < s.Length)
                    t.Vm.Halt(t, "StrToValue: not a number");
                return R(v);
            });

            add("EngToGeneric", "(to 0..32000, 257 outside)", (t, a) =>
            {
                double x = A.R(a.Get(0)),
                    lo = A.R(a.Get(1)),
                    hi = A.R(a.Get(2));
                if (lo == hi)
                    return I(0);
                if (hi <= lo)
                {
                    double tmp = lo;
                    lo = hi;
                    hi = tmp;
                }
                double rng = hi - lo;
                if (rng == 0.0)
                    rng = 1.0;
                double k = 32000.0 / rng;
                if (k != 1.0 || lo != 0.0)
                    x = (0.0 - k * lo) + k * x;
                bool oor = (0.0 - x) > 1.1920928955078125e-07 || (x - 32000.0) > 1.1920928955078125e-07;
                double d = 0.0;
                if (0.0 <= x)
                    d = x <= 32000.0 ? x : 32000.0;
                if (oor)
                    t.RaiseError(ERange, false);
                return I((long)d);
            });

            add("Rand", "(CRT rand % n; n < 1: 274)", (t, a) =>
            {
                long n = A.Short(A.I(a.Get(0)));
                if (n < 1)
                {
                    t.RaiseError(EInvalidArg, false);
                    return I(0);
                }
                var vm = t.Vm;
                long seed = (vm.RandSeed * 214013 + 2531011) & 0xFFFFFFFFL; // MSVC rand()
                vm.RandSeed = seed;
                return I(((seed >> 16) & 0x7FFF) % n);
            });

            // --- quality (..) ---------------------------------------
            add("QualityIsGood", "((q & 0xc0) == 0xc0)", (t, a) =>
                I((A.U64(a.Get(0)) & 0xC0) == 0xC0 ? 1 : 0)
            );

            add("QualityIsBad", "((q & 0xff) < 0x40)", (t, a) => I((A.U64(a.Get(0)) & 0xFF) < 0x40 ? 1 : 0));

            add("QualityIsUncertain", "((q & 0xc0) == 0x40)", (t, a) =>
                I((A.U64(a.Get(0)) & 0xC0) == 0x40 ? 1 : 0)
            );

            add("QualityIsOverride", "(bit 16)", (t, a) => I((long)((A.U64(a.Get(0)) >> 16) & 1)));

            add("QualityIsControlInhibit", "(bit 17)", (t, a) => I((long)((A.U64(a.Get(0)) >> 17) & 1)));

            add("QualityCreate", "", (t, a) =>
            {
                long g = A.I(a.Get(0)) & 0xFF;
                long sub = (a.Count > 1 ? A.I(a.Get(1)) : 0) & 0xFF;
                long lim = (a.Count > 2 ? A.I(a.Get(2)) : 0) & 0xFF;
                long ext = (a.Count > 3 ? A.I(a.Get(3)) : 0) & 0xFF;
                long ovr = (a.Count > 4 ? A.I(a.Get(4)) : 0) & 0xFF;
                long inh = (a.Count > 5 ? A.I(a.Get(5)) : 0) & 0xFF;
                long ds = (a.Count > 6 ? A.I(a.Get(6)) : 0) & 0xFFFFFFFFL;
                long low;
                if (g < 4 && g != 2)
                    low = g << 6;
                else
                {
                    low = 0;
                    t.RaiseError(EInvalidArg, false);
                }
                long major = low >> 6;
                if ((major == 3 && sub < 7) || (major == 1 && sub < 7) || (low == 0 && sub < 9))
                    low ^= (sub & 0xF) << 2;
                else
                    t.RaiseError(EInvalidArg, false);
                if (lim < 4)
                    low ^= lim & 3;
                else
                    t.RaiseError(EInvalidArg, false);
                if (ext >= 0x15)
                {
                    ext = 0;
                    t.RaiseError(EInvalidArg, false);
                }
                long word = (low & 0xFF) | (ext << 8) | ((((inh & 1) << 1) | (ovr & 1)) << 16);
                return I((long)(((ulong)ds << 32) | (ulong)word));
            });

            add("QualityGetPart", "", (t, a) =>
            {
                ulong q = A.U64(a.Get(0));
                long part = A.Short(A.I(a.Get(1)));
                ulong lo = q & 0xFFFFFFFFUL;
                switch (part)
                {
                    case 0:
                        return I((long)((lo & 0xFF) >> 6));
                    case 1:
                        return I((long)(((lo & 0xFF) >> 2) & 0xF));
                    case 2:
                        return I((long)(lo & 3));
                    case 3:
                        return I((long)((lo >> 8) & 0xFF));
                    case 4:
                        return I((long)((lo >> 16) & 1));
                    case 5:
                        return I((long)((lo >> 17) & 1));
                    case 6:
                        return I((int)(q >> 32));
                    case 7:
                        return I((long)(lo & 0xFF));
                }
                return I(-1);
            });

            add("QualitySetPart", "(an invalid part value: 274, unchanged)", (t, a) =>
            {
                ulong q = A.U64(a.Get(0));
                long part = A.Short(A.I(a.Get(1)));
                long val = A.I(a.Get(2));
                ulong lo = q & 0xFFFFFFFFUL,
                    hi = q >> 32;
                ulong b = lo & 0xFF;
                ulong uval = (ulong)val;
                switch (part)
                {
                    case 0:
                        if (val >= 0 && val < 4 && val != 2)
                            return I((long)((hi << 32) | (lo & ~0xFFUL) | (b & 0x3F) | ((uval & 3) << 6)));
                        break;
                    case 1:
                    {
                        ulong g = b >> 6;
                        if (((g == 3 || g == 1) && val >= 0 && val < 7) || (g == 0 && val >= 0 && val < 9))
                        {
                            ulong nb = ((((uval << 2) ^ b) & 0x3C) ^ b) & 0xFF;
                            return I((long)((hi << 32) | (lo & ~0xFFUL) | nb));
                        }
                        break;
                    }
                    case 2:
                        if (val >= 0 && val < 4)
                        {
                            ulong nb = ((b ^ uval) & 3) ^ b;
                            return I((long)((hi << 32) | (lo & ~0xFFUL) | (nb & 0xFF)));
                        }
                        break;
                    case 3:
                        if (val >= 0 && val < 0x15)
                            return I((long)((hi << 32) | (lo & ~0xFF00UL) | ((uval & 0xFF) << 8)));
                        break;
                    case 4:
                        return I((long)((hi << 32) | (lo & ~0x10000UL) | ((uval & 1) << 16)));
                    case 5:
                        return I((long)((hi << 32) | (lo & ~0x20000UL) | ((uval & 1) << 17)));
                    case 6:
                        return I((long)(((uval & 0xFFFFFFFFUL) << 32) | lo));
                    case 7:
                        return I((long)((hi << 32) | (lo & ~0xFFUL) | (uval & 0xFF)));
                    default:
                        return I((long)q);
                }
                t.RaiseError(EInvalidArg, false);
                return I((long)q);
            });
        }
    }
}
