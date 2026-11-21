using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using CicodeSim.Compiler.Syntax;
using CicodeSim.Project;
using CicodeSim.Values;
using Xunit;

namespace CicodeSim.Tests.Compiler
{
    /// <summary>The Cicode lexer and label expansion against CtCmp32's probed behaviour
    /// (the probe names are cited).</summary>
    public class LexerTests
    {
        internal static (List<Token> toks, List<string> diags) Toks(
            string src,
            LabelTable? labels = null,
            string mode = "ci"
        )
        {
            var r = Lexer.Tokenize(src, labels, "t.ci", mode, "P");
            return (
                r.Tokens.Where(t => t.Kind != K.Eof).ToList(),
                r.Diagnostics.Select(d => d.Name).ToList()
            );
        }

        /// <summary>(kind name, value) of each token; numbers as text.</summary>
        internal static List<(string, string)> Kv(
            string src,
            LabelTable? labels = null,
            string mode = "ci"
        ) => Toks(src, labels, mode).toks.Select(t => (K.Text(t.Kind), Val(t))).ToList();

        internal static string Val(Token t)
        {
            switch (t.Kind)
            {
                case K.Int:
                    return t.IValue.ToString(CultureInfo.InvariantCulture);
                case K.Real:
                    return FloatText.Shortest(t.RValue);
                case K.Intr:
                    return "$" + t.Intr!.Code.ToString(CultureInfo.InvariantCulture);
            }
            return t.SValue ?? "";
        }

        private static (string, string) N(string v) => ("name", v);

        private static (string, string) I(long v) => ("int", v.ToString(CultureInfo.InvariantCulture));

        private static (string, string) R(double v) => ("real", FloatText.Shortest(v));

        private static (string, string) S(string v) => ("str", v);

        private static (string, string) Op(string v) => (v, v);

        // ------------------------------------------------------------ input and whitespace
        [Fact]
        public void SourceTextEndsAtNulAndCtrlZAndIsCp1252()
        {
            Assert.Equal("a", Lexer.SourceText(new byte[] { (byte)'a', 0x1a, (byte)'b' })); // 2.3 x01
            Assert.Equal("a", Lexer.SourceText(new byte[] { (byte)'a', 0, (byte)'g' })); // 2.4 x12
            Assert.Equal("nø", Lexer.SourceText(new byte[] { (byte)'n', 0xf8 })); // 2.5 cp1252
        }

        [Fact]
        public void LineBreaksAreWhitespaceAndLinesCountLf()
        {
            Assert.Equal(
                new[] { "a", "b", "c" },
                Toks("a\r\n\tb\u000b\u000cc").toks.Select(t => t.SValue).ToArray()
            );
            var li = new LineIndex("a\nb\nc");
            Assert.Equal((3, 1), li.LineCol(4));
        }

        // ------------------------------------------------------------------------- comments
        [Fact]
        public void Comments()
        {
            Assert.Equal(new[] { N("a"), N("c") }, Kv("a ! b\nc")); // 3.2
            Assert.Equal(new[] { N("a") }, Kv("a != b")); // 3.3 c12
            Assert.Equal(new[] { I(4), Op("/"), I(2) }, Kv("4 / 2 //2")); // 3.4 c13
            Assert.Equal(new[] { I(1), Op("+"), I(2) }, Kv("1/**/+2")); // 3.5
            // no nesting: the first */ closes, the second is '*' '/' (3.6 c05)
            Assert.Equal(new[] { N("z"), Op("*"), Op("/") }, Kv("/* x /* y */ z */"));
            Assert.Equal(new[] { "E2056" }, Toks("/* open").diags); // 3.7 c06
            Assert.Equal(new[] { S("a ! b // c /* d") }, Kv("\"a ! b // c /* d\"")); // 3.10
        }

        // -------------------------------------------------------------------------- strings
        [Theory]
        [InlineData("\"a^nb\"", "a\nb")]
        [InlineData("\"^N\"", "\n")]
        [InlineData("\"^r^t^f\"", "\r\t\f")]
        [InlineData("\"^v\"", "\x11")] // ^v is 0x11, not VT (4.7)
        [InlineData("\"^0\"", "\0")]
        [InlineData("\"^0x41\"", "A")]
        [InlineData("\"^0x4\"", "\0x4")]
        [InlineData("\"^(^)^,^@\"", "^(^)^,^@")] // the caret is kept
        [InlineData("\"^q^'\"", "q'")]
        [InlineData("\"^\"\"", "\"")]
        [InlineData("\"^^\"", "^")]
        [InlineData("\"line1\nline2\"", "line1\nline2")] // strings span lines (4.2 s04)
        public void StringEscapes(string src, string value)
        {
            Assert.Equal(new[] { S(value) }, Kv(src));
        }

        [Fact]
        public void AdjacentStringsAreTwoTokens()
        {
            Assert.Equal(new[] { S("a"), S("b") }, Kv("\"a\"\"b\"")); // 4.5 s07
        }

        [Fact]
        public void UnterminatedStringIsBadAtTheEndOfTheInput()
        {
            var (ts, diags) = Toks("x = \"abc^\";\nRETURN x;\n"); // s02 / dapaudit p04
            Assert.Equal(new[] { "E2054" }, diags);
            Assert.Equal(K.Bad, ts[ts.Count - 1].Kind);
            var r = Lexer.Tokenize("x = \"abc", null, "t", "ci", "P");
            Assert.Equal(1, r.Diagnostics[0].Line);
            Assert.Equal("x = \"abc".Length, r.Diagnostics[0].Pos);
        }

        [Fact]
        public void StringLengthLimit255ThenTheRestIsLexedAgain()
        {
            var (ok, diags) = Toks("\"" + new string('a', 255) + "\""); // s09
            Assert.Empty(diags);
            Assert.Equal(255, ok[0].SValue!.Length);
            var (ts, d2) = Toks("\"" + new string('a', 256) + "\";\nb"); // s10
            Assert.Equal(new[] { "E2055", "E2054" }, d2);
            Assert.Equal(K.Bad, ts[0].Kind);
            Assert.Equal(255, ts[0].SValue!.Length);
            Assert.Equal(K.Name, ts[1].Kind);
            Assert.Equal("a", ts[1].SValue);
            // an escape counts as one character (s12 / s13, s14 / s15)
            Assert.Empty(
                Toks("\"" + string.Concat(Enumerable.Repeat("^n", 200)) + new string('a', 55) + "\"").diags
            );
            Assert.Equal(
                "E2055",
                Toks("\"" + string.Concat(Enumerable.Repeat("^n", 200)) + new string('a', 56) + "\"").diags[0]
            );
            Assert.Empty(
                Toks(
                    "\"" + string.Concat(Enumerable.Repeat("^0x41", 100)) + new string('a', 155) + "\""
                ).diags
            );
            Assert.Equal(
                "E2055",
                Toks(
                    "\"" + string.Concat(Enumerable.Repeat("^0x41", 100)) + new string('a', 156) + "\""
                ).diags[0]
            );
        }

        // -------------------------------------------------------------------------- numbers
        [Theory]
        [InlineData("123", 123L)]
        [InlineData("017", 17L)]
        [InlineData("09", 9L)] // no C octal (7.1)
        [InlineData("0x1F", 31L)]
        [InlineData("0X1f", 31L)]
        [InlineData("0x0000000001", 1L)]
        [InlineData("0x", 0L)]
        [InlineData("0xFFFFFFFF", 0xFFFFFFFFL)]
        [InlineData("0b101", 5L)]
        [InlineData("0B11", 3L)]
        [InlineData("0b", 0L)] // 7.3
        [InlineData("0o17", 15L)]
        [InlineData("0O7", 7L)] // 7.4
        [InlineData("4294967295", 0xFFFFFFFFL)]
        [InlineData("2147483648", 2147483648L)] // 7.5
        public void Integers(string src, long value)
        {
            Assert.Equal(new[] { I(value) }, Kv(src));
        }

        [Theory]
        [InlineData("1.5", 1.5)]
        [InlineData("1.", 1.0)]
        [InlineData("1e3", 1000.0)]
        [InlineData("1E+3", 1000.0)]
        [InlineData("1.5e-3", 0.0015)]
        [InlineData("1e", 1.0)]
        [InlineData("2.5E2", 250.0)]
        public void Reals(string src, double value)
        {
            Assert.Equal(new[] { R(value) }, Kv(src));
        }

        [Fact]
        public void NumberFormsEndWhereTheirFormEnds()
        {
            Assert.Equal(new[] { I(1), I(2), N("b") }, Kv("12b")); // radix rescan (g10)
            Assert.Equal(new[] { I(2), I(2) }, Kv("0b102")); // n46
            Assert.Equal(new[] { I(0), I(8) }, Kv("0o8")); // n48
            Assert.Equal(new[] { I(1), N("abc") }, Kv("1abc")); // 6.3
            Assert.Equal(new[] { Op("."), I(5) }, Kv(".5")); // 7.9 n31
            Assert.Equal(new[] { R(6.0), N("U") }, Kv("6.0EU")); // 8.3 f11
        }

        [Fact]
        public void NumbersPast32BitsStopWhereTheCompilerStops()
        {
            var (ts, diags) = Toks("4294967296"); // n24
            Assert.Equal(new[] { "E2058" }, diags);
            Assert.Equal(K.Bad, ts[0].Kind);
            (ts, diags) = Toks("99999999999"); // g_num_dec_11digits
            Assert.Equal(new[] { "E2058" }, diags);
            Assert.Equal((K.Bad, "9999999999"), (ts[0].Kind, ts[0].Text));
            Assert.Equal((K.Int, 9L), (ts[1].Kind, ts[1].IValue));
            (ts, diags) = Toks("0x100000000"); // n21
            Assert.Equal((K.Bad, "0x10000000"), (ts[0].Kind, ts[0].Text));
            Assert.Equal(0L, ts[1].IValue);
            (ts, diags) = Toks("0o40000000000"); // g_num_oct_33bits
            Assert.Equal("0o4000000000", ts[0].Text);
        }

        // ------------------------------------------------------------------ names, keywords
        [Fact]
        public void Names()
        {
            Assert.Equal(new[] { N("nTempø"), N("æøå") }, Kv("nTempø æøå")); // 6.1 i06
            Assert.Equal(new[] { N("a\\b") }, Kv("a\\b")); // 6.2 i04
            Assert.Equal(new[] { N("_x"), N("__y2_") }, Kv("_x __y2_"));
            Assert.Equal(new[] { N("Tag.Field"), N("a.b.c") }, Kv("Tag.Field a.b.c")); // 6.4
            Assert.Equal(Op("."), Kv("Tag . Field")[1]);
            Assert.Equal("abcø", Lexer.NameKey("ABCø"));
            Assert.Equal("Æbc", Lexer.NameKey("ÆBC")); // _stricmp folds ASCII only
            // significant length 254, longer names are cut silently (6.5 i20 / i21)
            var t = Toks(new string('a', 300)).toks[0];
            Assert.Equal(254, t.SValue!.Length);
            Assert.Equal(300, t.Text.Length);
        }

        [Fact]
        public void KeywordsAreCaseInsensitive()
        {
            Assert.Equal(
                new[] { Op("IF"), Op("THEN"), Op("END"), Op("CIVBA"), Op("NOP") },
                Kv("if Then END cIvBa nop")
            );
            Assert.Equal(34, K.Keywords.Length);
            Assert.Equal(new[] { N("XOR"), N("local") }, Kv("XOR local")); // 11.1: not keywords
        }

        [Fact]
        public void OperatorsAndSpecialTokens()
        {
            Assert.Equal(
                new[] { "<=", "<>", ">=", "<", ">", "=", "+", "-", "*", "/", ":" },
                Toks("<= <> >= < > = + - * / :").toks.Select(t => K.Text(t.Kind)).ToArray()
            );
            Assert.Equal(new[] { "<", ">" }, Toks("< >").toks.Select(t => K.Text(t.Kind)).ToArray()); // y06
            Assert.Equal(new[] { ("pipe", "abc") }, Kv("|abc|")); // 5 x08
            Assert.Equal(new[] { ("sg", "x") }, Kv("?x?")); // 5 i12
            Assert.Equal(("pic", "###.#"), Kv("x:###.#")[2]); // 8.3
            Assert.Equal(("pic", "#0.##EU"), Kv("x:#0.##EU")[2]);
            Assert.Equal(new[] { Op("%"), N("x"), Op("%") }, Kv("%x%")); // .ci: plain tokens
            Assert.Equal(new[] { ("genie", "x") }, Kv("%x%", mode: "page")); // page: a genie token
            Assert.Equal(new[] { Op("'"), N("a"), Op("'") }, Kv("'a'")); // 5 s06
        }

        // --------------------------------------------------------------------------- labels
        internal static LabelTable Labels(params (string name, string body)[] rows)
        {
            var lt = new LabelTable();
            foreach (var (name, body) in rows)
                lt.Add(Label.FromRow(name, body, "P", 1), "labels.dbf");
            return lt;
        }

        [Fact]
        public void LabelsExpandBeforeKeywordsAndInDeclarations()
        {
            var lt = Labels(("NORMAL", "0"));
            // 'INT Normal = 5;' compiles to 'INT 0 = 5;' (0.1)
            Assert.Equal(new[] { Op("INT"), I(0), Op("="), I(5), Op(";") }, Kv("INT Normal = 5;", lt));
            Assert.Equal(new[] { S("Normal") }, Kv("\"Normal\"", lt)); // not in strings (4.8)
        }

        [Fact]
        public void LabelTokensCarryThePositionOfTheUse()
        {
            var lt = Labels(("Twice(x)", "x*2"));
            var ts = Toks("a\nTwice(3)", lt).toks;
            Assert.Equal(
                new[] { I(3), Op("*"), I(2) },
                ts.Skip(1).Select(t => (K.Text(t.Kind), Val(t))).ToArray()
            );
            Assert.All(ts.Skip(1), t => Assert.True(t.Pos == 2 && t.Label == "Twice"));
        }

        [Fact]
        public void FunctionLikeLabelsAndDefaults()
        {
            var lt = Labels(("Twice(x)", "x*2"), ("Greet(a, b=7)", "a+b"));
            Assert.Equal(new[] { I(1), Op("+"), I(7) }, Kv("Greet(1)", lt));
            Assert.Equal(new[] { I(1), Op("+"), I(2) }, Kv("Greet(1, 2)", lt));
            // a function-like label without its arguments: E2057 and a bad token (b25 / x09)
            var (ts, diags) = Toks("Twice", lt);
            Assert.Equal(new[] { "E2057" }, diags);
            Assert.Equal(K.Bad, ts[0].Kind);
        }

        [Fact]
        public void DuplicateLabelIsE2038AndTheFirstStays()
        {
            var lt = Labels(("A", "1"), ("a", "2"));
            Assert.Equal(new[] { "E2038" }, lt.Diagnostics.Select(d => d.Name).ToArray());
            Assert.Equal(new[] { I(1) }, Kv("A", lt));
        }

        [Fact]
        public void IntrinsicLabels()
        {
            var lt = Labels(("__LINE__", "$3"), ("IFDEF(sTag,sTrue,sFalse)", "$9"));
            var ts = Toks("__LINE__ IFDEF(\"a\", 1, 2)", lt).toks;
            Assert.Equal(K.Intr, ts[0].Kind);
            Assert.Equal(3, ts[0].Intr!.Code);
            Assert.Equal(9, ts[1].Intr!.Code);
            Assert.Equal(new[] { "\"a\"", "1", "2" }, ts[1].Intr!.Args.ToArray());
            Assert.True(LabelTable.Intrinsics.ContainsKey(3) && LabelTable.Intrinsics.ContainsKey(9));
            Assert.Equal(new[] { Op("$"), I(3) }, Kv("$3")); // '$' alone is a plain token (5 i14)
        }

        /// <summary>The exact decimal of m * 2^e (m > 0) with <paramref name="pad"/> more zeros and
        /// <paramref name="delta"/> added in the last place.</summary>
        private static string Exact(BigInteger m, int e, int pad, int delta)
        {
            // value * 10^k is an integer for k = max(0, -e)
            int k = e < 0 ? -e : 0;
            var n = e < 0 ? m * BigInteger.Pow(5, k) : m * BigInteger.Pow(2, e);
            n = n * BigInteger.Pow(10, pad) + delta;
            string s = n.ToString(CultureInfo.InvariantCulture);
            int point = k + pad;
            if (s.Length <= point)
                s = new string('0', point - s.Length + 1) + s;
            return s.Substring(0, s.Length - point) + "." + s.Substring(s.Length - point);
        }

        private static double Parse(string s)
        {
            Assert.True(FloatText.TryParseNumber(s, out double d), s.Substring(0, Math.Min(40, s.Length)));
            return d;
        }

        [Fact]
        public void LongRunsOfEscapesAndQuestionMarksLexInLinearTime()
        {
            // where a super genie run is found
            Assert.Equal(new[] { N("a"), Op("?"), ("sg", "b?c?d") }, Kv("a?b?c?d"));
            Assert.Equal(new[] { ("sg", "a?b?c") }, Kv("a?b?c"));
            Assert.Equal(new[] { Op("?"), ("sg", "a?b?") }, Kv("?a?b?"));
            Assert.Equal(new[] { Op("?"), ("sg", "a??") }, Kv("?a??"));
            Assert.Equal(
                new[] { N("x"), N("a"), Op("?"), N("b"), Op("?"), ("sg", "c?d?e") },
                Kv("x a?b?c?d?e")
            );
            Assert.Equal(new[] { ("bad", "\n\n\n\n\n") }, Kv("\"^n^n^n^n^n"));
            // a long 'a?a?a? ...' run and an open string full of escapes lex in linear time
            var sw = Stopwatch.StartNew();
            var (ts, _) = Toks(string.Concat(Enumerable.Repeat("a?", 100_000)));
            Assert.Equal(199_997, ts.Count); // ... a ? then the run 'a?a?'
            Assert.Equal(("sg", "a?a?"), (K.Text(ts[ts.Count - 1].Kind), ts[ts.Count - 1].SValue));
            Assert.Equal(
                new[] { N("a"), Op("?"), N("a"), Op("?"), N("a"), Op("?"), ("sg", "a?a?") },
                Kv(string.Concat(Enumerable.Repeat("a?", 5)))
            );
            var (st, sd) = Toks("s = \"" + string.Concat(Enumerable.Repeat("^n", 200_000)));
            Assert.Equal(new[] { "E2054" }, sd);
            Assert.Equal(new string('\n', 200_000), st[2].SValue);
            var (qt, qd) = Toks("s = \"" + string.Concat(Enumerable.Repeat("^\"", 200_000)) + "\"");
            Assert.Equal((K.Bad, new string('"', 255)), (qt[2].Kind, qt[2].SValue)); // E2055 (4.4)
            Assert.Equal("E2055", qd[0]);
            Assert.True(sw.Elapsed.TotalSeconds < 5, sw.Elapsed.ToString());
        }

        [Fact]
        public void LongRealLiteralsRoundCorrectlyInLinearTime()
        {
            // the midpoint between two doubles and a hair above / below it, written out with more
            // than the parser's 800 significant digits: the truncated digits decide nothing
            // (halfway rounds to even, a hair above up, a hair below down)
            var rnd = new Random(4242);
            var bitsList = new List<long>
            {
                1,
                2,
                3,
                0x000FFFFFFFFFFFFFL,
                0x0010000000000000L,
                0x7FEFFFFFFFFFFFFEL,
                0x3FF0000000000000L,
            };
            for (int i = 0; i < 60; i++)
                bitsList.Add((long)(rnd.NextDouble() * 0x7FEFFFFFFFFFFFFEL));
            foreach (long bits in bitsList)
            {
                double lo = BitConverter.Int64BitsToDouble(bits),
                    hi = BitConverter.Int64BitsToDouble(bits + 1);
                int exp = (int)((bits >> 52) & 0x7FF);
                long frac = bits & 0xFFFFFFFFFFFFFL;
                BigInteger m = exp == 0 ? frac : frac | (1L << 52);
                int e = exp == 0 ? -1074 : exp - 1075;
                var mid = 2 * m + 1; // the midpoint = (2m + 1) * 2^(e - 1)
                double even = (bits & 1) == 0 ? lo : hi;
                Assert.Equal(even, Parse(Exact(mid, e - 1, 900, 0)));
                Assert.Equal(hi, Parse(Exact(mid, e - 1, 900, 1)));
                Assert.Equal(lo, Parse(Exact(mid, e - 1, 900, -1)));
            }
            // a 1,000,000-digit literal lexes in linear time
            var sw = Stopwatch.StartNew();
            var t = Toks("r = 0." + new string('3', 1_000_000) + ";").toks;
            Assert.True(sw.Elapsed.TotalSeconds < 10, sw.Elapsed.ToString());
            Assert.Equal(1.0 / 3.0, t[2].RValue);
            Assert.Equal(
                double.PositiveInfinity,
                Parse("1" + new string('0', 400) + "." + new string('7', 2000))
            );
            Assert.Equal(0.0, Parse("0." + new string('0', 400) + new string('7', 2000)));
        }
    }
}
