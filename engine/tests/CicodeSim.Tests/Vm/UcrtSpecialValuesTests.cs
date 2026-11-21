using System;
using CicodeSim.Values;
using CicodeSim.Vm;
using Xunit;

namespace CicodeSim.Tests.Vm
{
    /// <summary>Infinities and NaNs as the 32-bit Universal CRT gives them.</summary>
    public class UcrtSpecialValuesTests
    {
        private static double D(ulong bits) => BitConverter.Int64BitsToDouble(unchecked((long)bits));

        private static ulong Bits(double x) => (ulong)BitConverter.DoubleToInt64Bits(x);

        [Theory]
        [InlineData(0x7FF8000000000000UL, "NAN")]
        [InlineData(0xFFF8000000000000UL, "-NAN(IND)")] // the indefinite value (0 / 0, inf - inf)
        [InlineData(0x7FF0000000000001UL, "NAN")] // signalling: quiet on x86, not "NAN(SNAN)"
        [InlineData(0xFFF0000000000001UL, "-NAN")]
        [InlineData(0xFFF8000000000001UL, "-NAN")]
        [InlineData(0x7FFFFFFFFFFFFFFFUL, "NAN")]
        [InlineData(0xFFFFFFFFFFFFFFFFUL, "-NAN")]
        [InlineData(0xFFFFBBBBFFFFBBBBUL, "-NAN")] // the trend invalid marker
        [InlineData(0x7FF0000000000000UL, "INF")]
        [InlineData(0xFFF0000000000000UL, "-INF")]
        public void RealToStringOfSpecialValues(ulong bits, string want)
        {
            Assert.Equal(want, Crt.FormatG15(D(bits)));
            Assert.Equal(want, Conv.RealToStr(D(bits)));
        }

        [Theory]
        [InlineData("%f", 0xFFF8000000000000UL, "-nan(ind)")]
        [InlineData("%e", 0x7FF8000000000000UL, "nan")]
        [InlineData("%08.2f", 0x7FF0000000000000UL, "     inf")] // the '0' flag pads with blanks
        [InlineData("%08.2f", 0xFFF0000000000000UL, "    -inf")]
        [InlineData("%08.2f", 0xFFF8000000000000UL, "-nan(ind)")]
        [InlineData("%+f", 0x7FF8000000000000UL, "+nan")]
        [InlineData("% e", 0x7FF0000000000000UL, " inf")]
        [InlineData("%-6.1f|", 0x7FF0000000000000UL, "inf   |")]
        [InlineData("%.2e", 0xFFF8000000000000UL, "-nan(ind)")]
        [InlineData("%#.0f", 0x7FF0000000000000UL, "i.nf")] // '#' with no decimals
        [InlineData("%#.0E", 0x7FF8000000000000UL, "N.AN")]
        [InlineData("%#013.f", 0xFFF0000000000000UL, "-00000000.inf")]
        [InlineData("%#.0g", 0x7FF0000000000000UL, "inf")]
        public void PrintfOfSpecialValues(string fmt, ulong bits, string want)
        {
            Assert.Equal(want, Printf.Format(fmt, PrintfArg.Float(D(bits))));
        }

        [Fact]
        public void FormatOperatorPadsAnInfinityWithBlanks()
        {
            // :format word 0x10206 = width 6, 2 decimals, zero pad: "%06.2f"
            Assert.Equal(
                "   inf",
                Conv.FormatValue(Value.Real(double.PositiveInfinity), 0x10206, "", NoErr.Instance).S
            );
            Assert.Equal(
                "  -inf",
                Conv.FormatValue(Value.Real(double.NegativeInfinity), 0x10206, "", NoErr.Instance).S
            );
            Assert.Equal("000.50", Conv.FormatValue(Value.Real(0.5), 0x10206, "", NoErr.Instance).S);
        }

        [Theory]
        [InlineData("nan", 0x7FFFFFFFFFFFFFFFUL, 3)]
        [InlineData("-nan", 0xFFFFFFFFFFFFFFFFUL, 4)]
        [InlineData("NaN(123)", 0x7FFFFFFFFFFFFFFFUL, 8)]
        [InlineData("nan()", 0x7FFFFFFFFFFFFFFFUL, 5)]
        [InlineData("nan(", 0x7FFFFFFFFFFFFFFFUL, 3)]
        [InlineData("nan(ind)", 0xFFF8000000000000UL, 8)]
        [InlineData("-nan(ind)", 0xFFF8000000000000UL, 9)] // the sign does not flip it
        [InlineData("+NAN(IND)", 0xFFF8000000000000UL, 9)]
        [InlineData("nan(indx)", 0x7FFFFFFFFFFFFFFFUL, 9)]
        [InlineData("nan(snan)", 0x7FF8000000000001UL, 9)] // made quiet by the x87 return
        [InlineData("-nan(SNAN)", 0xFFF8000000000001UL, 10)]
        [InlineData("infinity", 0x7FF0000000000000UL, 8)]
        [InlineData("-inf", 0xFFF0000000000000UL, 4)]
        public void StrtodOfSpecialValues(string s, ulong bits, int used)
        {
            double v = Crt.Strtod(s, out int u);
            Assert.Equal((bits, used), (Bits(v), u));
            Assert.Equal(bits, Bits(Conv.ParseReal(s, out int code)));
            Assert.Equal(used == s.Length ? 0 : Conv.EInvalidArg, code); // text left over: 274
        }

        [Fact]
        public void ToolFormatsReadNanAsTheQuietNaN()
        {
            // a JSON NaN and a scenario's "nan" are C's NAN; "-nan" is -NAN
            Assert.Equal(0x7FF8000000000000UL, Bits(JsonValue.Parse("NaN").FloatValue));
            Assert.True(FloatText.TryParseNumber(" NaN ", out double a));
            Assert.Equal(0x7FF8000000000000UL, Bits(a));
            Assert.True(FloatText.TryParseNumber("-nan", out double b));
            Assert.Equal(0xFFF8000000000000UL, Bits(b));
            Assert.Equal("NAN", Crt.FormatG15(a));
        }
    }
}
