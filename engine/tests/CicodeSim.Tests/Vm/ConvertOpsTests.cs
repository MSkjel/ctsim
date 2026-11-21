using System;
using CicodeSim.Compiled;
using CicodeSim.Values;
using CicodeSim.Vm;
using Xunit;

namespace CicodeSim.Tests.Vm
{
    /// <summary>Conversions and operators of the Cicode VM.</summary>
    public class ConvertOpsTests
    {
        private static Value I(long x) => Value.Long(x);

        private static Value R(double x) => Value.Real(x);

        private static Value S(string x) => Value.Str(x);

        // ------------------------------------------------------------------ 5.1 to INT
        [Theory]
        [InlineData(2.5, 3)]
        [InlineData(-2.5, -3)]
        [InlineData(2.4999, 2)]
        [InlineData(-0.4, 0)]
        [InlineData(0.5, 1)]
        [InlineData(2147483646.4, 2147483646)]
        public void RealRoundsHalfAwayFromZero(double x, long want)
        {
            var e = new Collect();
            Assert.Equal(want, Conv.ToInt(R(x), e).I);
            Assert.Empty(e.Errors);
        }

        [Theory]
        [InlineData(2147483647.0)]
        [InlineData(-2147483649.0)]
        [InlineData(1e300)]
        [InlineData(double.NaN)]
        public void RealOutOfRangeIs434(double x)
        {
            var e = new Collect();
            var r = Conv.ToInt(R(x), e);
            Assert.Equal(0, r.I);
            Assert.Equal(new[] { (434, true) }, e.Errors.ToArray());
        }

        [Theory]
        [InlineData("12abc", 12, new int[0])]
        [InlineData("50.25", 50, new int[0])]
        [InlineData("", 0, new int[0])]
        [InlineData("  -7", -7, new int[0])]
        [InlineData("+3", 3, new int[0])]
        [InlineData("abc", 0, new[] { 433 })]
        [InlineData("2147483648", 2147483647, new int[0])]
        [InlineData("-99999999999", -2147483648, new int[0])]
        public void StringToIntFirstCharacterRule(string s, long want, int[] codes)
        {
            // only the first non-space character is checked, then C atol (clamping)
            var e = new Collect();
            Assert.Equal(want, Conv.ToInt(S(s), e).I);
            Assert.Equal(codes, e.Codes);
        }

        [Fact]
        public void UlongAndObject()
        {
            var e = new Collect();
            Assert.Equal(0, Conv.ToInt(Value.Int(Types.ULong, 0x80000000), e).I);
            Assert.Equal(new[] { 434 }, e.Codes);
            e = new Collect();
            var r = Conv.ToInt(Value.Object(5, 0xC0, 1, 2, 3), e);
            Assert.True(r.I == 0 && r.Q == 0 && r.Ts == 0); // quality cleared
            Assert.Equal(new[] { 433 }, e.Codes);
        }

        // ------------------------------------------------------------------ 5.2 to REAL
        [Theory]
        [InlineData("1,5", 1.5, new int[0])]
        [InlineData("1,234.5", 1.234, new[] { 433 })]
        [InlineData("12 ", 12.0, new[] { 433 })]
        [InlineData("abc", 0.0, new[] { 433 })]
        [InlineData("", 0.0, new int[0])]
        [InlineData("  2e3", 2000.0, new int[0])]
        [InlineData("0x10", 16.0, new int[0])]
        public void StringToRealFirstCommaRule(string s, double want, int[] codes)
        {
            var e = new Collect();
            Assert.Equal(want, Conv.ToReal(S(s), e).D);
            Assert.Equal(codes, e.Codes);
        }

        [Fact]
        public void IntAndUlongToReal()
        {
            Assert.Equal(-3.0, Conv.ToReal(I(-3), NoErr.Instance).D);
            Assert.Equal(4294967295.0, Conv.ToReal(Value.Int(Types.ULong, 0xFFFFFFFF), NoErr.Instance).D);
        }

        // ------------------------------------------------------------------ 5.3 to STRING
        [Theory]
        [InlineData(1.5, "1.5")]
        [InlineData(100.0, "100")]
        [InlineData(1e21, "1E+21")]
        [InlineData(0.1, "0.1")]
        [InlineData(-2.25, "-2.25")]
        [InlineData(1e-5, "1E-05")]
        public void RealToStringIs15G(double x, string want)
        {
            Assert.Equal(want, Conv.ToStr(R(x), NoErr.Instance).S);
        }

        [Fact]
        public void OtherTypesToString()
        {
            Assert.Equal("-5", Conv.ToStr(I(-5), NoErr.Instance).S);
            Assert.Equal("", Conv.ToStr(Value.Object(7), NoErr.Instance).S);
            Assert.Equal("4294967295", Conv.ToStr(Value.Int(Types.ULong, 0xFFFFFFFF), NoErr.Instance).S);
        }

        // ------------------------------------------------------------------ 5.4 :format
        public static readonly object[][] FormatWordCases =
        {
            new object[] { "I", 42.0, 0x1, "", "42" }, // the compiler's '%1ld' for INT + string
            new object[] { "R", 3.14159, 0x103, "", "3.1" }, // w 3 d 1
            new object[] { "R", 3.14159, 0x10206, "", "003.14" }, // zero pad
            new object[] { "R", 3.0, 0x20106, "", "3.0   " }, // left justify
            new object[] { "R", 3.0, 0x40103, "m%", "3.0 m%" }, // suffix, '%' doubled in the format
            new object[] { "I", 5.0, 0x40000, "kW", "kW" }, // width 0: only the suffix
            new object[] { "R", 1234.5, 0x100208, "", "1.23e+03" }, // exponential
            new object[] { "R", 1e30, 0x0006, "", "1.00e+30" }, // 21+ characters: "%.2e"
            new object[] { "S", 0.0, 0x103, "", "text" }, // a STRING is left as it is
        };

        [Theory]
        [MemberData(nameof(FormatWordCases))]
        public void FormatWord(string kind, double x, int word, string suf, string want)
        {
            Value v =
                kind == "I" ? I((long)x)
                : kind == "R" ? R(x)
                : S("text");
            Assert.Equal(want, Conv.FormatValue(v, word, suf, NoErr.Instance).S);
        }

        [Fact]
        public void FormatWordToPrintfTemplates()
        {
            Assert.Equal("%5.3s", Conv.FormatWordToPrintf(0x0305, "", Types.String));
            Assert.Equal("%5.5s", Conv.FormatWordToPrintf(0x0005, "", Types.String));
            Assert.Equal("%-05lu", Conv.FormatWordToPrintf(0x30005, "", Types.ULong));
            Assert.Equal("%3.2f EU", Conv.FormatWordToPrintf(0x40203, "EU", Types.Real));
        }

        // ------------------------------------------------------------------ 6 operators
        private static OpCtx Ctx(bool ic = true) => new OpCtx(1000, ic);

        [Fact]
        public void IntWrapsAndCDivision()
        {
            var c = Ctx();
            Assert.Equal(-1294967293, Ops.Full(Op.Add, I(1000000001), I(2000000002), c).I);
            Assert.Equal(0, Ops.Numeric(Op.Mul, I(65536), I(65536), c).I);
            Assert.Equal(-3, Ops.Div(I(-7), I(2), c).I);
            Assert.Equal(-1, Ops.Mod(I(-7), I(2), c).I);
            Assert.Equal(1, Ops.Mod(I(7), I(-2), c).I);
        }

        [Fact]
        public void DivideByZero()
        {
            var c = Ctx();
            var r = Ops.Div(I(1), R(0.0), c);
            Assert.True(r.T == Types.Long && r.I == 0);
            Assert.Equal(new[] { 273 }, c.Codes);
            Assert.Equal(273, c.ExprError);
            c = Ctx();
            Assert.Equal(0, Ops.Mod(R(1.0), I(0), c).I);
            Assert.Equal(new[] { 273 }, c.Codes);
        }

        [Fact]
        public void MixedStringNumberRule()
        {
            var c = Ctx();
            var r = Ops.Full(Op.Add, S("12"), I(3), c);
            Assert.Equal((Types.Real, 15.0), (r.T, r.D));
            r = Ops.Full(Op.Add, S("abc"), I(3), c);
            Assert.Equal((Types.String, "abc3"), (r.T, r.S));
            Assert.Equal(1, Ops.Full(Op.Gt, S("10"), I(9), c).I); // numeric
            Assert.Equal(1, Ops.Full(Op.Gt, S("a"), I(9), c).I); // "a" vs "9"
            Assert.Equal("3x", Ops.Full(Op.Add, I(3), S("x"), c).S);
            Assert.Empty(c.Errors);
        }

        [Fact]
        public void MulSubWithStringConvertToReal()
        {
            var c = Ctx();
            var r = Ops.Numeric(Op.Mul, S("2.5"), I(2), c);
            Assert.Equal((Types.Real, 5.0), (r.T, r.D));
            Assert.Empty(c.Errors);
            r = Ops.Numeric(Op.Sub, S("abc"), I(1), c);
            Assert.Equal(-1.0, r.D);
            Assert.Equal(new[] { 433 }, c.Codes);
        }

        [Fact]
        public void StringCompareIgnoreCaseFlag()
        {
            Assert.Equal(1, Ops.Full(Op.Eq, S("abc"), S("ABC"), Ctx(true)).I);
            Assert.Equal(0, Ops.Full(Op.Eq, S("abc"), S("ABC"), Ctx(false)).I);
            // _stricmp folds to lower case: '_' (0x5f) sorts before 'a' (0x61) either way
            Assert.Equal(1, Ops.Full(Op.Lt, S("_"), S("a"), Ctx(true)).I);
            Assert.Equal(1, Ops.Full(Op.Lt, S("B"), S("a"), Ctx(false)).I); // strcmp: 'B' < 'a'
            Assert.Equal(0, Ops.Full(Op.Lt, S("B"), S("a"), Ctx(true)).I); // 'b' > 'a'
        }

        [Fact]
        public void ConcatCapsAt255()
        {
            var r = Ops.Full(Op.Add, S(new string('x', 250)), S(new string('y', 10)), Ctx());
            Assert.Equal(255, r.S!.Length);
            Assert.EndsWith("yyyyy", r.S);
        }

        [Fact]
        public void ConcatAndCompareStopAtTheFirstNul()
        {
            // a byte buffer (StrSetChar, ComRead) keeps the bytes after a NUL; strcat and
            // strcmp do not see them
            Assert.Equal("Ax", Ops.Full(Op.Add, S("A\0B"), S("x\0y"), Ctx()).S);
            Assert.Equal(1, Ops.Full(Op.Eq, S("A\0B"), S("A"), Ctx()).I);
            Assert.Equal(0, Ops.Full(Op.Eq, S("A\0B"), S("AB"), Ctx()).I);
        }

        [Fact]
        public void LogicRoundsToInt()
        {
            var c = Ctx();
            Assert.Equal(0, Ops.Logic(Op.And, R(0.4), I(1), c).I);
            Assert.Equal(1, Ops.Logic(Op.Or, R(0.6), I(0), c).I);
            Assert.Equal(1, Ops.Not(R(0.4), c).I);
            Assert.Equal(0xF0, Ops.Logic(Op.BitAnd, I(0xF0F0), R(255.0), c).I);
            Assert.Equal(-2, Ops.Logic(Op.BitXor, I(-1), I(1), c).I);
        }

        [Fact]
        public void NotAndNegOfString433()
        {
            var c = Ctx();
            var r = Ops.Not(S("x"), c);
            Assert.True(r.I == 0 && r.Q == 0);
            Assert.Equal(new[] { 433 }, c.Codes);
            c = Ctx();
            Assert.Equal(2147483647, Ops.Neg(Value.Int(Types.ULong, 0x80000001), c).I);
            Assert.Equal(new[] { 434 }, c.Codes);
        }

        [Fact]
        public void TimestampsTruncateToMsAndQualityOps()
        {
            var c = Ctx();
            var a = Value.Int(Types.Timestamp, 100000005);
            var b = Value.Int(Types.Timestamp, 100000009);
            Assert.Equal(1, Ops.Full(Op.Eq, a, b, c).I); // equal at millisecond resolution
            Assert.Equal(1, Ops.Full(Op.Lt, a, Value.Int(Types.Timestamp, 100010000), c).I);
            var r = Ops.Full(Op.Add, Value.Int(Types.Quality, 1), Value.Int(Types.Quality, 2), c);
            Assert.Equal((Types.Long, 3L), (r.T, r.I));
            c = Ctx();
            Ops.Numeric(Op.Sub, a, b, c); // Sub has no TIMESTAMP path: 433
            Assert.Equal(new[] { 433, 433 }, c.Codes);
        }

        [Fact]
        public void QualityMerge()
        {
            // the worse major wins; equal majors with another substatus keep only
            // the major bits; bits 16 / 17 of either are kept
            Assert.Equal(0x40UL, Quality.Merge(0xC0, 0x40));
            Assert.Equal(0x00UL, Quality.Merge(0x00, 0xC0));
            Assert.Equal(0xC0UL, Quality.Merge(0xC0, 0xC0));
            Assert.Equal(0x40UL, Quality.Merge(0x44, 0x48));
            Assert.Equal(0xC0UL | 0x30000UL, Quality.Merge(0xC0 | 0x10000, 0xC0 | 0x20000));
        }

        [Fact]
        public void TimestampsMergeLatestZeroIsNow()
        {
            Quality.Merged(
                Value.Long(1, 0xC0, 50, 0, 0),
                Value.Long(1, 0xC0, 0, 0, 0),
                1000,
                out _,
                out ulong ts,
                out ulong vts,
                out ulong qts
            );
            Assert.Equal((1000UL, 1000UL, 1000UL), (ts, vts, qts));
            Quality.Merged(
                Value.Long(1, 0xC0, 5000, 7, 0),
                Value.Long(1, 0xC0, 3000, 9, 0),
                1,
                out _,
                out ts,
                out vts,
                out qts
            );
            Assert.Equal((5000UL, 9UL, 1UL), (ts, vts, qts));
        }

        [Fact]
        public void CSprintfOneArgument()
        {
            Assert.Equal("  3.1 kg", Crt.Sprintf("%5.1f kg", PrintfArg.Float(3.14159)));
            Assert.Equal("7", Crt.Sprintf("%ld", PrintfArg.Int(7)));
            Assert.Equal("ffffffff", Crt.Sprintf("%x", PrintfArg.Int(-1)));
            Assert.Equal("a|", Crt.Sprintf("%s|%s", PrintfArg.Str("a")));
            Assert.Equal("100%", Crt.Sprintf("100%%", PrintfArg.Int(0)));
        }

        [Fact]
        public void BothOperandsConvertRightFirst()
        {
            // every operator family converts the top of the stack (the right operand) first, so
            // the last error raised, the one IsError() reports, is the left operand's
            var c = new OpCtx();
            Ops.Logic(Op.And, S("abc"), R(1e20), c); // 433 left, 434 right
            Assert.Equal(new[] { 434, 433 }, c.Codes);
            c = new OpCtx();
            Ops.Numeric(Op.Mul, S("abc"), Value.Str(null), c); // 433 left, 530 right
            Assert.Equal(new[] { 530, 433 }, c.Codes);
        }
    }
}
