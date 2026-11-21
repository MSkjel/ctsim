using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Threading;
using CicodeSim.Project;
using CicodeSim.Values;
using Xunit;

namespace CicodeSim.Tests.Vm
{
    /// <summary>The engine's JSON parser reads client messages: a hostile or broken text ends in
    /// a FormatException, never a dead process (a stack overflow cannot be caught) or a reader
    /// thread stuck for minutes.</summary>
    public class JsonReviewTests
    {
        private static string Nest(int depth, bool obj)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < depth; i++)
                sb.Append(obj ? "{\"a\":" : "[");
            sb.Append('1');
            sb.Append(obj ? '}' : ']', depth);
            return sb.ToString();
        }

        /// <summary>Runs <paramref name="body"/> on a thread with a 256 KB stack (what MaxDepth
        /// promises) and rethrows what it threw.</summary>
        private static void OnSmallStack(Action body)
        {
            Exception? err = null;
            var t = new Thread(
                () =>
                {
                    try
                    {
                        body();
                    }
                    catch (Exception e)
                    {
                        err = e;
                    }
                },
                256 * 1024
            );
            t.Start();
            t.Join();
            if (err != null)
                throw err;
        }

        [Fact]
        public void NestingUpToMaxDepthParsesAndWalksOnASmallStack()
        {
            foreach (bool obj in new[] { false, true })
            {
                string text = Nest(JsonValue.MaxDepth, obj);
                OnSmallStack(() =>
                {
                    var v = JsonValue.Parse(text);
                    Assert.Equal(text, v.Dump());
                    Assert.NotNull(v.ToPlain());
                    Assert.True(v.ValueEquals(JsonValue.Parse(text)));
                    Assert.NotEmpty(v.Dump());
                    Assert.NotNull(Json.Parse(text));
                });
            }
        }

        [Fact]
        public void DeeperNestingIsAFormatExceptionNotAStackOverflow()
        {
            foreach (bool obj in new[] { false, true })
            {
                var e = Assert.Throws<FormatException>(() =>
                    JsonValue.Parse(Nest(JsonValue.MaxDepth + 1, obj))
                );
                Assert.Contains("nesting deeper than 512", e.Message);
            }
            string deep = new string('[', 200000) + new string(']', 200000);
            OnSmallStack(() => Assert.Throws<FormatException>(() => JsonValue.Parse(deep)));
            OnSmallStack(() => Assert.Throws<FormatException>(() => Json.Parse(deep)));
            // depth counts open containers, not containers seen: siblings do not add up
            var wide = new StringBuilder("[");
            for (int i = 0; i < 2000; i++)
                wide.Append(i > 0 ? "," : "").Append(Nest(100, i % 2 == 0));
            Assert.Equal(2000, JsonValue.Parse(wide.Append(']').ToString()).Count);
        }

        [Fact]
        public void IntegersHaveADigitLimit()
        {
            string ok = new string('7', JsonValue.MaxIntDigits);
            Assert.Equal(BigInteger.Parse(ok), JsonValue.Parse(ok).BigValue);
            Assert.Equal(-BigInteger.Parse(ok), JsonValue.Parse("-" + ok).BigValue);
            var e = Assert.Throws<FormatException>(() => JsonValue.Parse("[" + new string('1', 5000) + "]"));
            Assert.Equal("JSON: an integer of 5000 digits (at most 4300) at character 1", e.Message);
            Assert.Throws<FormatException>(() => JsonValue.Parse("-" + new string('1', 4301)));
            // the long fast path keeps the boundaries exact
            Assert.Equal(long.MaxValue, JsonValue.Parse("9223372036854775807").Long);
            Assert.Equal(long.MinValue, JsonValue.Parse("-9223372036854775808").Long);
            Assert.False(JsonValue.Parse("9223372036854775808").FitsLong);
            Assert.Equal(999999999999999999L, JsonValue.Parse("999999999999999999").Long);
            Assert.Equal(0L, JsonValue.Parse("-0").Long);
        }

        // ------------------------------------------------------------------ long float text
        /// <summary>d * 2^1075 exactly (d finite, > 0): every double and every midpoint between
        /// two doubles is an integer there.</summary>
        private static BigInteger Scaled(double d)
        {
            long bits = BitConverter.DoubleToInt64Bits(d);
            int e = (int)((bits >> 52) & 0x7FF);
            long f = bits & 0xFFFFFFFFFFFFFL;
            long m = e == 0 ? f : f | (1L << 52);
            int ex = e == 0 ? -1074 : e - 1075;
            return new BigInteger(m) << (ex + 1075);
        }

        private static IEnumerable<double> Doubles()
        {
            yield return double.Epsilon;
            yield return 2 * double.Epsilon;
            yield return BitConverter.Int64BitsToDouble(0x000FFFFFFFFFFFFFL); // largest subnormal
            yield return BitConverter.Int64BitsToDouble(0x0010000000000000L); // smallest normal
            yield return 1.0;
            yield return 0.1;
            yield return 9007199254740992.0;
            yield return 1e300;
            yield return BitConverter.Int64BitsToDouble(0x7FEFFFFFFFFFFFFEL);
            var rnd = new Random(20261001);
            for (int i = 0; i < 300; i++)
            {
                long bits = ((long)rnd.Next(0, 0x7FF) << 52) | ((long)rnd.Next() << 21) ^ rnd.Next();
                double d = BitConverter.Int64BitsToDouble(bits & 0x7FEFFFFFFFFFFFFFL);
                if (d > 0 && !double.IsInfinity(d))
                    yield return d;
            }
        }

        [Fact]
        public void LongDecimalTextRoundsExactlyAtEveryMidpoint()
        {
            // the float parser keeps 800 significant digits plus a sticky one: at a midpoint
            // between two doubles (exact: ties to even), a hair above (up) and a hair below
            // (down) with 50 more digits, so every case is longer than 800 digits
            BigInteger five = BigInteger.Pow(5, 1075);
            int n = 0;
            foreach (double lo in Doubles())
            {
                double hi = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(lo) + 1);
                BigInteger mid = (Scaled(lo) + Scaled(hi)) / 2;
                BigInteger digits = mid * five; // mid = digits * 10^-1075
                double even = (BitConverter.DoubleToInt64Bits(lo) & 1) == 0 ? lo : hi;
                BigInteger pad = BigInteger.Pow(10, 50);
                string tie = digits.ToString(System.Globalization.CultureInfo.InvariantCulture) + "e-1075";
                string above =
                    (digits * pad + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "e-1125";
                string below =
                    (digits * pad - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "e-1125";
                Assert.True(
                    above.Length > 805,
                    above.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                );
                foreach (var (text, want) in new[] { (tie, even), (above, hi), (below, lo) })
                {
                    Assert.True(FloatText.TryParseNumber(text, out double got), text);
                    Assert.True(
                        BitConverter.DoubleToInt64Bits(want) == BitConverter.DoubleToInt64Bits(got),
                        $"{text.Substring(0, 40)}... want {FloatText.Shortest(want)} got {FloatText.Shortest(got)}"
                    );
                    Assert.Equal(want, JsonValue.Parse(text).FloatValue);
                    n++;
                }
            }
            Assert.True(n > 800, n.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        [Fact]
        public void AFiveMillionDigitNumberParsesQuickly()
        {
            // the parser keeps 800 significant digits and a sticky one
            string text = "1." + new string('1', 5_000_000);
            var sw = Stopwatch.StartNew();
            double d = JsonValue.Parse(text).FloatValue;
            Assert.Equal(1.1111111111111112, d);
            Assert.Equal(0.1, JsonValue.Parse("0." + new string('0', 5_000_000) + "1e5000000").FloatValue);
            Assert.Equal(1.0 / 3, JsonValue.Parse("0." + new string('3', 5_000_000)).FloatValue);
            Assert.True(
                sw.ElapsedMilliseconds < 5000,
                sw.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
            );
        }

        [Fact]
        public void TheFacadeRejectsWhatJsonRejects()
        {
            // not JSON
            foreach (
                string bad in new[]
                {
                    "01",
                    "+1",
                    "1.",
                    ".5",
                    "\"a\u0001b\"",
                    "\"\\u 41\"",
                    "[1,]",
                    "{\"a\":1,}",
                    "-",
                    "tru",
                }
            )
                Assert.Throws<FormatException>(() => Json.Parse(bad));
            Assert.Equal(-1L, Json.Parse("-1"));
            Assert.True(double.IsPositiveInfinity((double)Json.Parse("1e400")!));
            Assert.True(double.IsNegativeInfinity((double)Json.Parse("-Infinity")!));
            var big = Json.Parse("123456789012345678901234567890");
            Assert.IsType<double>(big);
            Assert.Equal(1.2345678901234568e29, (double)big!);
        }
    }
}
