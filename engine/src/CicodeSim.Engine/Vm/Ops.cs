using System;
using System.Collections.Generic;
using CicodeSim.Compiled;
using CicodeSim.Values;

namespace CicodeSim.Vm
{
    /// <summary>What an operator needs from the running task: the current FILETIME (a zero
    /// timestamp counts as now), the IgnoreCase flag, error raising and the first expression
    /// error.</summary>
    public interface IOpContext : IErrorSink
    {
        ulong Now { get; }
        bool IgnoreCase { get; }
        void NoteExprError(int code);
    }

    /// <summary>A minimal operator context for tests and pure evaluation.</summary>
    public sealed class OpCtx : IOpContext
    {
        public ulong Now { get; set; }
        public bool IgnoreCase { get; set; }
        public readonly List<(int Code, bool Halt)> Errors = new List<(int, bool)>();
        public int ExprError;

        public OpCtx(ulong now = 0, bool ignoreCase = true)
        {
            Now = now;
            IgnoreCase = ignoreCase;
        }

        public void RaiseError(int code, bool halt) => Errors.Add((code, halt));

        public void NoteExprError(int code)
        {
            if (ExprError == 0)
                ExprError = code;
        }

        public int[] Codes => Errors.ConvertAll(e => e.Code).ToArray();
    }

    /// <summary>Operators of the Cicode VM.</summary>
    public static class Ops
    {
        public const int EDivideByZero = 273;
        public const int ETypeMismatch = 433;
        public const int EConversion = 434;

        private static int Cls(int t) => t >= 0 && t < 0x20 ? Types.TypeClass(t) : 3;

        // ------------------------------------------------------------------ helpers
        /// <summary>C / on 32-bit ints (truncation toward zero). INT_MIN / -1 traps in the
        /// runtime (x86 #DE, guess: unhandled); here it wraps to INT_MIN.</summary>
        public static long CDiv(long a, long b)
        {
            long q = Math.Abs(a) / Math.Abs(b);
            if ((a < 0) != (b < 0))
                q = -q;
            return (int)q;
        }

        /// <summary>C % (sign of the dividend).</summary>
        public static long CMod(long a, long b)
        {
            long r = Math.Abs(a) % Math.Abs(b);
            return a < 0 ? -r : r;
        }

        /// <summary>C fmod: NaN for an infinite x or a zero y, x for an infinite y, like the %
        /// of doubles.</summary>
        public static double CFmod(double x, double y) => x % y;

        private static int CompareBytes(string a, string b, bool ignoreCase)
        {
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                int x = Cp1252.Byte(a[i]),
                    y = Cp1252.Byte(b[i]);
                if (ignoreCase)
                {
                    if (x >= 'A' && x <= 'Z')
                        x += 32;
                    if (y >= 'A' && y <= 'Z')
                        y += 32;
                }
                if (x != y)
                    return x < y ? -1 : 1;
            }
            return a.Length == b.Length ? 0 : (a.Length < b.Length ? -1 : 1);
        }

        /// <summary>strcmp, or _stricmp when the task's IgnoreCase flag is set (C locale, only
        /// ASCII letters fold). Negative / 0 / positive.</summary>
        public static int StrCompare(string a, string b, bool ignoreCase)
        {
            // ASCII strings compare by code point, others by their Windows-1252 bytes
            return CompareBytes(a, b, ignoreCase);
        }

        /// <summary>A string as C sees it: up to its first NUL (a byte buffer StrSetChar or
        /// ComRead wrote keeps the bytes after it, but strcat and strcmp stop there).</summary>
        public static string CString(string s)
        {
            int nul = s.IndexOf('\0');
            return nul < 0 ? s : s.Substring(0, nul);
        }

        /// <summary>String Add: strncat with 256 - (len + 1), so at most 255 characters.</summary>
        public static string Concat(string a, string b)
        {
            a = CString(a);
            b = CString(b);
            int room = 255 - a.Length;
            if (room <= 0)
                return a;
            return b.Length <= room ? a + b : a + b.Substring(0, room);
        }

        private static long IntCmp(int op, long x, long y)
        {
            switch (op)
            {
                case Op.Lt:
                    return x < y ? 1 : 0;
                case Op.Le:
                    return x <= y ? 1 : 0;
                case Op.Gt:
                    return x > y ? 1 : 0;
                case Op.Ge:
                    return x >= y ? 1 : 0;
                case Op.Eq:
                    return x == y ? 1 : 0;
                default:
                    return x != y ? 1 : 0;
            }
        }

        private static long RealCmp(int op, double x, double y)
        {
            switch (op)
            {
                case Op.Lt:
                    return x < y ? 1 : 0;
                case Op.Le:
                    return x <= y ? 1 : 0;
                case Op.Gt:
                    return x > y ? 1 : 0;
                case Op.Ge:
                    return x >= y ? 1 : 0;
                case Op.Eq:
                    return x == y ? 1 : 0;
                default:
                    return x != y ? 1 : 0;
            }
        }

        private static long UCmp(int op, ulong x, ulong y)
        {
            switch (op)
            {
                case Op.Lt:
                    return x < y ? 1 : 0;
                case Op.Le:
                    return x <= y ? 1 : 0;
                case Op.Gt:
                    return x > y ? 1 : 0;
                case Op.Ge:
                    return x >= y ? 1 : 0;
                case Op.Eq:
                    return x == y ? 1 : 0;
                default:
                    return x != y ? 1 : 0;
            }
        }

        public static bool IsCompare(int op) =>
            op == Op.Lt || op == Op.Le || op == Op.Gt || op == Op.Ge || op == Op.Eq || op == Op.Ne;

        /// <summary>Int arithmetic of two 32-bit values (already wrapped).</summary>
        public static long IntArith(int op, long x, long y)
        {
            switch (op)
            {
                case Op.Add:
                    return (int)(x + y);
                case Op.Sub:
                    return (int)(x - y);
                case Op.Mul:
                    return (int)(x * y);
                case Op.Div:
                    return y != 0 ? CDiv(x, y) : 0;
                case Op.And:
                    return x != 0 && y != 0 ? 1 : 0;
                case Op.Or:
                    return x != 0 || y != 0 ? 1 : 0;
                case Op.BitAnd:
                    return (int)(x & y);
                case Op.BitOr:
                    return (int)(x | y);
                case Op.BitXor:
                    return (int)(x ^ y);
            }
            return IntCmp(op, x, y);
        }

        private static Value RealOp(int op, double x, double y, ulong q, ulong ts, ulong vts, ulong qts)
        {
            switch (op)
            {
                case Op.Add:
                    return Value.Real(x + y, q, ts, vts, qts);
                case Op.Sub:
                    return Value.Real(x - y, q, ts, vts, qts);
                case Op.Mul:
                    return Value.Real(x * y, q, ts, vts, qts);
                case Op.Div:
                    if (y == 0.0)
                    {
                        // a division by zero: NaN for 0 / 0 and NaN / 0, else an infinity with the signs
                        double r =
                            (x == 0 || double.IsNaN(x))
                                ? double.NaN
                                : CopySign(double.PositiveInfinity, x) * CopySign(1.0, y);
                        return Value.Real(r, q, ts, vts, qts);
                    }
                    return Value.Real(x / y, q, ts, vts, qts);
            }
            return Value.Long(RealCmp(op, x, y), q, ts, vts, qts);
        }

        public static double CopySign(double mag, double sgn)
        {
            long m = BitConverter.DoubleToInt64Bits(mag) & 0x7FFFFFFFFFFFFFFFL;
            long s = BitConverter.DoubleToInt64Bits(sgn) & unchecked((long)0x8000000000000000UL);
            return BitConverter.Int64BitsToDouble(m | s);
        }

        // ------------------------------------------------------------------ families
        /// <summary>Mul / Sub / Div (after the zero check).</summary>
        public static Value Numeric(int op, Value a, Value b, IOpContext ctx)
        {
            if (a.T == Types.Variant)
                a = Value.Deref(a);
            if (b.T == Types.Variant)
                b = Value.Deref(b);
            Quality.Merged(a, b, ctx.Now, out ulong q, out ulong ts, out ulong vts, out ulong qts);
            if (Cls(a.T) == 0 && Cls(b.T) == 0)
                return Value.Long(IntArith(op, (int)a.I, (int)b.I), q, ts, vts, qts);
            return NumericReal(op, a, b, q, ts, vts, qts, ctx);
        }

        private static Value NumericReal(
            int op,
            in Value a,
            in Value b,
            ulong q,
            ulong ts,
            ulong vts,
            ulong qts,
            IOpContext ctx
        )
        {
            Value b2 = Conv.ToReal(b, ctx); // right first
            Value a2 = Conv.ToReal(a.WithQ(q, ts, vts, qts), ctx);
            return RealOp(op, a2.D, b2.D, a2.Q, a2.Ts, a2.Vts, a2.Qts);
        }

        private static Value StringOp(
            int op,
            in Value a,
            in Value b,
            ulong q,
            ulong ts,
            ulong vts,
            ulong qts,
            IOpContext ctx
        )
        {
            Value b2 = Conv.ToStr(b, ctx); // right first
            Value a2 = Conv.ToStr(a.WithQ(q, ts, vts, qts), ctx);
            string x = CString(a2.S ?? ""),
                y = CString(b2.S ?? "");
            if (op == Op.Add)
                return Value.Str(Concat(x, y), a2.Q, a2.Ts, a2.Vts, a2.Qts);
            int c = StrCompare(x, y, ctx.IgnoreCase);
            bool r;
            switch (op)
            {
                case Op.Lt:
                    r = c < 0;
                    break;
                case Op.Le:
                    r = c <= 0;
                    break;
                case Op.Gt:
                    r = c > 0;
                    break;
                case Op.Ge:
                    r = c >= 0;
                    break;
                case Op.Eq:
                    r = c == 0;
                    break;
                case Op.Ne:
                    r = c != 0;
                    break;
                default:
                    throw new KeyNotFoundException("op " + op);
            }
            return Value.Long(r ? 1 : 0, a2.Q, a2.Ts, a2.Vts, a2.Qts);
        }

        private static long U64Op(int op, ulong x, ulong y)
        {
            ulong r;
            switch (op)
            {
                case Op.Add:
                    r = x + y;
                    break;
                case Op.Sub:
                    r = x - y;
                    break;
                case Op.Mul:
                    r = x * y;
                    break;
                case Op.Div:
                    r = y != 0 ? x / y : 0;
                    break;
                case Op.And:
                    return x != 0 && y != 0 ? 1 : 0;
                case Op.Or:
                    return x != 0 || y != 0 ? 1 : 0;
                case Op.BitAnd:
                    r = x & y;
                    break;
                case Op.BitOr:
                    r = x | y;
                    break;
                case Op.BitXor:
                    r = x ^ y;
                    break;
                default:
                    return UCmp(op, x, y);
            }
            // the result's type byte is INT: only its low 32 bits are ever read
            return (int)r;
        }

        /// <summary>Add and the comparisons (branch order from the runtime's machine code).</summary>
        public static Value Full(int op, Value a, Value b, IOpContext ctx)
        {
            if (a.T == Types.Variant)
                a = Value.Deref(a);
            if (b.T == Types.Variant)
                b = Value.Deref(b);
            Quality.Merged(a, b, ctx.Now, out ulong q, out ulong ts, out ulong vts, out ulong qts);
            int ca = Cls(a.T),
                cb = Cls(b.T);
            if (ca == 0 && cb == 0)
                return Value.Long(IntArith(op, (int)a.I, (int)b.I), q, ts, vts, qts);
            if (cb <= 1) // right is a number
            {
                if (ca <= 1)
                    return NumericReal(op, a, b, q, ts, vts, qts, ctx);
                if (ca == 2)
                {
                    if (Conv.ParsesAsNumber(a.S))
                        return NumericReal(op, a, b, q, ts, vts, qts, ctx);
                    return StringOp(op, a, b, q, ts, vts, qts, ctx);
                }
                return NumericReal(op, a, b, q, ts, vts, qts, ctx);
            }
            if (ca <= 1) // left is a number, right is not
            {
                if (cb == 2 && !Conv.ParsesAsNumber(b.S))
                    return StringOp(op, a, b, q, ts, vts, qts, ctx);
                return NumericReal(op, a, b, q, ts, vts, qts, ctx);
            }
            if (ca == 2 && cb == 2)
                return StringOp(op, a, b, q, ts, vts, qts, ctx);
            if (b.T == Types.Timestamp && a.T == Types.Timestamp)
            {
                ulong x = (ulong)a.I / 10000 * 10000;
                ulong y = (ulong)b.I / 10000 * 10000;
                return Value.Long(U64Op(op, x, y), q, ts, vts, qts);
            }
            if (b.T == Types.Quality && a.T == Types.Quality)
                return Value.Long(U64Op(op, (ulong)a.I, (ulong)b.I), q, ts, vts, qts);
            return StringOp(op, a, b, q, ts, vts, qts, ctx);
        }

        /// <summary>And / Or / BitAnd / BitOr / BitXor: both operands to INT.</summary>
        public static Value Logic(int op, Value a, Value b, IOpContext ctx)
        {
            if (a.T == Types.Variant)
                a = Value.Deref(a);
            if (b.T == Types.Variant)
                b = Value.Deref(b);
            Quality.Merged(a, b, ctx.Now, out ulong q, out ulong ts, out ulong vts, out ulong qts);
            if (Cls(a.T) == 0 && Cls(b.T) == 0)
                return Value.Long(IntArith(op, (int)a.I, (int)b.I), q, ts, vts, qts);
            Value b2 = Conv.ToInt(b, ctx); // right first
            Value a2 = Conv.ToInt(a.WithQ(q, ts, vts, qts), ctx);
            return Value.Long(IntArith(op, a2.I, b2.I), a2.Q, a2.Ts, a2.Vts, a2.Qts);
        }

        /// <summary>0 = not zero, 1 = zero, -1 = neither int nor real.</summary>
        private static int IsZero(in Value v)
        {
            int c = Cls(v.T);
            if (c == 0)
                return (int)v.I == 0 ? 1 : 0;
            if (c == 1)
                return v.D == 0.0 ? 1 : 0;
            return -1;
        }

        /// <summary>Div (case 0x17): an int 0 or REAL 0.0 divisor is 273 (halt flag set), the result INT 0 with the merged quality and the expression error
        /// #DIV/0; otherwise the numeric family.</summary>
        public static Value Div(Value a, Value b, IOpContext ctx)
        {
            if (a.T == Types.Variant)
                a = Value.Deref(a);
            if (b.T == Types.Variant)
                b = Value.Deref(b);
            if (IsZero(b) == 1)
            {
                ctx.RaiseError(EDivideByZero, true);
                Quality.Merged(a, b, ctx.Now, out ulong q, out ulong ts, out ulong vts, out ulong qts);
                ctx.NoteExprError(EDivideByZero);
                return Value.Long(0, q, ts, vts, qts);
            }
            return Numeric(Op.Div, a, b, ctx);
        }

        /// <summary>Mod (case 0x19): int % int (sign of the dividend); any
        /// REAL operand: fmod, REAL result; a zero divisor: 273 like Div; a string or other
        /// operand: 433, INT 0 with the quality cleared.</summary>
        public static Value Mod(Value a, Value b, IOpContext ctx)
        {
            if (a.T == Types.Variant)
                a = Value.Deref(a);
            if (b.T == Types.Variant)
                b = Value.Deref(b);
            int ca = Cls(a.T),
                cb = Cls(b.T);
            int z = IsZero(b);
            if (z == 1)
            {
                ctx.RaiseError(EDivideByZero, true);
                Quality.Merged(a, b, ctx.Now, out ulong q0, out ulong ts0, out ulong vts0, out ulong qts0);
                ctx.NoteExprError(EDivideByZero);
                return Value.Long(0, q0, ts0, vts0, qts0);
            }
            if (z == -1 || ca >= 2)
            {
                ctx.RaiseError(ETypeMismatch, true);
                return Value.Long(0, 0);
            }
            Quality.Merged(a, b, ctx.Now, out ulong q, out ulong ts, out ulong vts, out ulong qts);
            if (ca == 0 && cb == 0)
                return Value.Long(CMod((int)a.I, (int)b.I), q, ts, vts, qts);
            double x = Conv.ToReal(a, ctx).D;
            double y = Conv.ToReal(b, ctx).D;
            return Value.Real(CFmod(x, y), q, ts, vts, qts);
        }

        /// <summary>Neg: negate a value.</summary>
        public static Value Neg(Value a, IOpContext ctx)
        {
            if (a.T == Types.Variant)
                a = Value.Deref(a);
            int c = Cls(a.T);
            if (c == 0)
            {
                long x = a.I;
                if (a.T == Types.ULong && (x & 0xFFFFFFFFL) > 0x7FFFFFFF)
                    ctx.RaiseError(EConversion, true);
                return Value.Long((int)(-x), a.Q, a.Ts, a.Vts, a.Qts);
            }
            if (c == 1)
                return Value.Real(-a.D, a.Q, a.Ts, a.Vts, a.Qts);
            ctx.RaiseError(ETypeMismatch, true);
            return Value.Long(0, 0);
        }

        /// <summary>Not (case 0x14): a number is converted to INT first (REAL rounds),
        /// result INT (x == 0); a string or other: 433 and INT 0 with the quality cleared.</summary>
        public static Value Not(Value a, IOpContext ctx)
        {
            if (a.T == Types.Variant)
                a = Value.Deref(a);
            int c = Cls(a.T);
            if (c <= 1)
            {
                Value x = a.T == Types.Long ? a : Conv.ToInt(a, ctx);
                return Value.Long((int)x.I == 0 ? 1 : 0, x.Q, x.Ts, x.Vts, x.Qts);
            }
            ctx.RaiseError(ETypeMismatch, true);
            return Value.Long(0, 0);
        }

        public static bool IsFull(int op) => op == Op.Add || IsCompare(op);

        public static bool IsLogic(int op) =>
            op == Op.And || op == Op.Or || op == Op.BitAnd || op == Op.BitOr || op == Op.BitXor;

        /// <summary>Apply binary opcode <paramref name="op"/> to (left a, right b).</summary>
        public static Value Binary(int op, Value a, Value b, IOpContext ctx)
        {
            if (IsFull(op))
                return Full(op, a, b, ctx);
            if (op == Op.Div)
                return Div(a, b, ctx);
            if (op == Op.Mod)
                return Mod(a, b, ctx);
            if (IsLogic(op))
                return Logic(op, a, b, ctx);
            if (op == Op.Mul || op == Op.Sub)
                return Numeric(op, a, b, ctx);
            throw new ArgumentException($"not a binary opcode: 0x{op:x2}");
        }

        /// <summary>Brfalse / BrTrue test (cases 0x0d / 0x44): int != 0, REAL != 0.0
        /// (exact, NaN is true); a string or other: 433 and -1.</summary>
        public static int Truth(Value v, IOpContext ctx)
        {
            if (v.T == Types.Variant)
                v = Value.Deref(v);
            int c = Cls(v.T);
            if (c == 0)
                return (int)v.I != 0 ? 1 : 0;
            if (c == 1)
                return v.D != 0.0 ? 1 : 0;
            ctx.RaiseError(ETypeMismatch, true);
            return -1;
        }
    }
}
