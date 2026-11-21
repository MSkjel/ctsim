using System;
using System.Globalization;

namespace CicodeSim.Values
{
    /// <summary>Type codes of the VM. Signature characters are (char)(0x30 + code).</summary>
    public static class Types
    {
        public const int Digital = 0x00;

        /// <summary>FUNC0 "INT": a built-in declared INT returns 16 bits, sign-extended.</summary>
        public const int Int16 = 0x01;
        public const int Real = 0x02;
        public const int Bcd = 0x03;

        /// <summary>Cicode INT (32-bit, wraps) and FUNC0 "LONG".</summary>
        public const int Long = 0x04;

        /// <summary>By-reference LONG argument in built-in signatures.</summary>
        public const int LongBcd = 0x05;

        /// <summary>By-reference REAL argument.</summary>
        public const int LongReal = 0x06;

        /// <summary>At most 255 characters.</summary>
        public const int String = 0x07;

        /// <summary>By-reference STRING argument in built-in signatures.</summary>
        public const int Byte = 0x08;
        public const int Void = 0x09;
        public const int UInt = 0x0A;
        public const int RealValue = 0x0B;

        /// <summary>Null handle = -1.</summary>
        public const int Object = 0x0C;

        /// <summary>In the VM: a reference to another stack element or variable (AddrStack).</summary>
        public const int Variant = 0x0D;
        public const int VarArg = 0x10;
        public const int Tag = 0x11;
        public const int ULong = 0x12;

        /// <summary>FILETIME, 100 ns units.</summary>
        public const int Timestamp = 0x13;

        /// <summary>64-bit.</summary>
        public const int Quality = 0x14;

        public const int StringMax = 255;

        /// <summary>Operand classes. Every operator picks its path by class, not by type.</summary>
        public const int ClassInt = 0;
        public const int ClassReal = 1;
        public const int ClassString = 2;
        public const int ClassOther = 3;

        private static readonly int[] Classes =
        {
            0,
            0,
            1,
            0,
            0,
            0,
            1,
            2,
            0,
            3,
            0,
            3,
            3,
            3,
            3,
            3,
            3,
            3,
            0,
            3,
            3,
            0,
            0,
            0,
            0,
        };

        /// <summary>0 int, 1 float, 2 string, 3 other.</summary>
        public static int TypeClass(int t) => t >= 0 && t < Classes.Length ? Classes[t] : ClassOther;

        /// <summary>Types whose value lives in the integer payload as an int (DIGITAL INT16 BCD
        /// LONG LONGBCD BYTE UINT ULONG).</summary>
        public static bool IsIntType(int t) =>
            t == Digital
            || t == Int16
            || t == Bcd
            || t == Long
            || t == LongBcd
            || t == Byte
            || t == UInt
            || t == ULong;

        /// <summary>The real types: REAL, LONGREAL.</summary>
        public static bool IsRealType(int t) => t == Real || t == LongReal;

        /// <summary>The integer-like types (no ULONG).</summary>
        public static bool IsIntLike(int t) =>
            t == Digital || t == Int16 || t == Bcd || t == Long || t == LongBcd || t == Byte || t == UInt;

        /// <summary>Signature character of a type code: (char)(0x30 + code).</summary>
        public static char SigChar(int typeCode) => (char)(0x30 + typeCode);

        public static int SigType(char ch) => ch - 0x30;
    }

    /// <summary>One VM stack element: a value, its type, a quality and three timestamps.</summary>
    public struct Value : IEquatable<Value>
    {
        public int T;
        public long I;
        public double D;
        public object? O;
        public ulong Q;
        public ulong Ts;
        public ulong Vts;
        public ulong Qts;

        public const ulong QualityGood = 0xC0;

        // marker type codes (built-in results, never on the stack)
        public const int TNothing = -1;
        public const int TBlock = -2;
        public const int TNotSimulated = -3;

        /// <summary>A built-in's "no value".</summary>
        public static readonly Value Nothing = new Value { T = TNothing };

        /// <summary>A built-in returns this (with the task's Wait set) when its result comes later.</summary>
        public static readonly Value Block = new Value { T = TBlock };

        public static Value NotSimulated(string name, int index) =>
            new Value
            {
                T = TNotSimulated,
                O = name,
                I = index,
            };

        public readonly bool IsNothing => T == TNothing;
        public readonly bool IsBlock => T == TBlock;
        public readonly bool IsNotSimulated => T == TNotSimulated;

        // --- constructors -----------------------------------------------------------------
        public static Value Int(
            int t,
            long v,
            ulong q = QualityGood,
            ulong ts = 0,
            ulong vts = 0,
            ulong qts = 0
        ) =>
            new Value
            {
                T = t,
                I = v,
                Q = q,
                Ts = ts,
                Vts = vts,
                Qts = qts,
            };

        public static Value Long(long v, ulong q = QualityGood, ulong ts = 0, ulong vts = 0, ulong qts = 0) =>
            new Value
            {
                T = Types.Long,
                I = v,
                Q = q,
                Ts = ts,
                Vts = vts,
                Qts = qts,
            };

        public static Value Real(
            double v,
            ulong q = QualityGood,
            ulong ts = 0,
            ulong vts = 0,
            ulong qts = 0
        ) =>
            new Value
            {
                T = Types.Real,
                D = v,
                Q = q,
                Ts = ts,
                Vts = vts,
                Qts = qts,
            };

        public static Value RealOf(
            int t,
            double v,
            ulong q = QualityGood,
            ulong ts = 0,
            ulong vts = 0,
            ulong qts = 0
        ) =>
            new Value
            {
                T = t,
                D = v,
                Q = q,
                Ts = ts,
                Vts = vts,
                Qts = qts,
            };

        public static Value Str(
            string? v,
            ulong q = QualityGood,
            ulong ts = 0,
            ulong vts = 0,
            ulong qts = 0
        ) =>
            new Value
            {
                T = Types.String,
                O = v,
                Q = q,
                Ts = ts,
                Vts = vts,
                Qts = qts,
            };

        public static Value Object(
            long v,
            ulong q = QualityGood,
            ulong ts = 0,
            ulong vts = 0,
            ulong qts = 0
        ) =>
            new Value
            {
                T = Types.Object,
                I = v,
                Q = q,
                Ts = ts,
                Vts = vts,
                Qts = qts,
            };

        public static Value Variant(
            Ref? r,
            ulong q = QualityGood,
            ulong ts = 0,
            ulong vts = 0,
            ulong qts = 0
        ) =>
            new Value
            {
                T = Types.Variant,
                O = r,
                Q = q,
                Ts = ts,
                Vts = vts,
                Qts = qts,
            };

        /// <summary>The same payload with another quality and timestamps.</summary>
        public readonly Value WithQ(ulong q, ulong ts, ulong vts, ulong qts)
        {
            Value v = this;
            v.Q = q;
            v.Ts = ts;
            v.Vts = vts;
            v.Qts = qts;
            return v;
        }

        /// <summary>The same payload and type, quality cleared, timestamps zero.</summary>
        public readonly Value Bare(ulong q = 0)
        {
            Value v = this;
            v.Q = q;
            v.Ts = 0;
            v.Vts = 0;
            v.Qts = 0;
            return v;
        }

        /// <summary>This value's payload stored with type <paramref name="t"/>.</summary>
        public readonly Value Retyped(int t)
        {
            Value v = this;
            v.T = t;
            return v;
        }

        public readonly string? S => O as string;
        public readonly Ref? R => O as Ref;

        /// <summary>A VARIANT that holds a reference.</summary>
        public readonly bool IsRef => T == Types.Variant && O is Ref;

        /// <summary>True when the payload is the integer <see cref="I"/> (every type but the
        /// REAL types, STRING and a VARIANT holding a reference).</summary>
        public readonly bool PayloadIsInt =>
            T >= 0 && !Types.IsRealType(T) && T != Types.String && !(T == Types.Variant && O != null);

        // --- references ------------------------------------------------------------------
        /// <summary>Follow VARIANT references to the element they point at (while the type byte is 0xd). A cycle stops after 64 steps.</summary>
        public static Value Deref(Value v)
        {
            int n = 0;
            while (v.T == Types.Variant && v.O is Ref r)
            {
                v = r.Get();
                n++;
                if (n > 64)
                    break;
            }
            return v;
        }

        /// <summary>A long as the VM's 32-bit two's complement INT.</summary>
        public static long Wrap32(long x) => (int)x;

        public static Value Default(int t)
        {
            // StackVar defaults (opcode 0x0f): REAL 0.0, INT 0, STRING '', OBJECT -1,
            // TIMESTAMP / QUALITY 0
            if (Types.IsRealType(t))
                return RealOf(t, 0.0);
            if (t == Types.String || t == Types.Byte)
                return Str("");
            if (t == Types.Object)
                return Object(-1);
            return Int(t, 0);
        }

        // --- equality (debugging, tests) -----------------------------------------------------
        public readonly bool Equals(Value o)
        {
            if (T != o.T || Q != o.Q || Ts != o.Ts || Vts != o.Vts || Qts != o.Qts)
                return false;
            if (Types.IsRealType(T))
                return D.Equals(o.D);
            if (T == Types.String)
                return string.Equals(S, o.S, StringComparison.Ordinal);
            if (T == Types.Variant && (O != null || o.O != null))
                return ReferenceEquals(O, o.O);
            return I == o.I;
        }

        public override readonly bool Equals(object? obj) => obj is Value v && Equals(v);

        public override readonly int GetHashCode() => T ^ I.GetHashCode() ^ D.GetHashCode();

        public override readonly string ToString()
        {
            string payload;
            if (Types.IsRealType(T))
                payload = FloatText.Shortest(D);
            else if (T == Types.String)
                payload = S == null ? "null" : "'" + S + "'";
            else if (T == Types.Variant && O != null)
                payload = "ref";
            else
                payload = I.ToString(CultureInfo.InvariantCulture);
            string extra = Q == QualityGood && Ts == 0 && Vts == 0 && Qts == 0 ? "" : $" q=0x{Q:x} ts={Ts}";
            return $"Value(0x{T:x2}, {payload}{extra})";
        }
    }

    /// <summary>What a VARIANT element points at.</summary>
    public abstract class Ref
    {
        public virtual int BaseType => Types.Variant;
        public abstract Value Get();
        public abstract void Set(Value value);
    }

    /// <summary>A reference to a standalone value (a VARIANT built-in result).</summary>
    public sealed class ValueRef : Ref
    {
        public Value Value;

        public ValueRef(Value value)
        {
            Value = value;
        }

        public override Value Get() => Value;

        public override void Set(Value value)
        {
            Value = value;
        }
    }

    /// <summary>Quality helpers.</summary>
    public static class Quality
    {
        public const ulong Good = 0xC0;

        /// <summary>Merge two 64-bit qualities; the worse one wins.</summary>
        public static ulong Merge(ulong a, ulong b)
        {
            ulong la = a & 0xFF;
            ulong lb = b & 0xFF;
            ulong ga = la >> 6,
                gb = lb >> 6;
            ulong r;
            if (gb < ga)
                r = b;
            else if (ga < gb || ((la ^ lb) & 0x3C) == 0)
                r = a;
            else
                r = la & 0xC0;
            if (((a | b) & 0x10000) != 0)
                r |= 0x10000;
            if (((a | b) & 0x20000) != 0)
                r |= 0x20000;
            return r;
        }

        /// <summary>(quality, timestamp, value timestamp, quality timestamp) of a op b
        /// (each timestamp the latest, 0 = now).</summary>
        public static void Merged(
            in Value a,
            in Value b,
            ulong now,
            out ulong q,
            out ulong ts,
            out ulong vts,
            out ulong qts
        )
        {
            q = a.Q == b.Q ? a.Q : Merge(a.Q, b.Q);
            ulong x = a.Ts != 0 ? a.Ts : now;
            ulong y = b.Ts != 0 ? b.Ts : now;
            ts = x > y ? x : y;
            x = a.Vts != 0 ? a.Vts : now;
            y = b.Vts != 0 ? b.Vts : now;
            vts = x > y ? x : y;
            x = a.Qts != 0 ? a.Qts : now;
            y = b.Qts != 0 ? b.Qts : now;
            qts = x > y ? x : y;
        }
    }
}
