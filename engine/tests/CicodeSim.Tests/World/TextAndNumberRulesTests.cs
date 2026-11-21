using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using CicodeSim.Project;
using CicodeSim.World;
using Xunit;
using Crt = CicodeSim.Values.Crt;
using FloatText = CicodeSim.Values.FloatText;
using JsonValue = CicodeSim.Values.JsonValue;

namespace CicodeSim.Tests.World
{
    /// <summary>Text and number rules of the World and project files.</summary>
    public sealed class TextAndNumberRulesTests
    {
        private static string DataDir([CallerFilePath] string here = "") =>
            Path.Combine(Path.GetDirectoryName(here)!, "Data");

        private static double FromBits(string hex) =>
            BitConverter.Int64BitsToDouble(unchecked((long)ulong.Parse(hex, NumberStyles.HexNumber)));

        private static string Bits(double d) =>
            unchecked((ulong)BitConverter.DoubleToInt64Bits(d)).ToString("x16");

        [Fact]
        public void ShortestFormatG15AndRoundCases()
        {
            int n = 0;
            foreach (string line in File.ReadAllLines(Path.Combine(DataDir(), "float_text_cases.txt")))
            {
                if (line.Length == 0)
                    continue;
                string[] f = line.Split('\t');
                double x = FromBits(f[0]);
                Assert.Equal(f[1], FloatText.Shortest(x));
                Assert.Equal(f[2], Crt.FormatG15(x));
                Assert.Equal(f[3], Bits(FloatText.Round(x, 6)));
                Assert.Equal(f[4], Bits(FloatText.Round(x, 9)));
                n++;
            }
            Assert.True(n > 400);
        }

        [Fact]
        public void NumberTextParses()
        {
            // blanks (C isspace) around an optional sign and a decimal literal or inf / nan;
            // no digit separators, no other scripts' digits, no other white space
            foreach (string line in File.ReadAllLines(Path.Combine(DataDir(), "number_text_cases.txt")))
            {
                if (line.Length == 0)
                    continue;
                var j = JsonValue.Parse(line);
                string text = j.Items[0].Str,
                    want = j.Items[1].Str;
                bool ok = FloatText.TryParseNumber(text, out double d);
                if (want == "error")
                    Assert.False(ok, text);
                else if (want == "nan")
                    Assert.True(ok && double.IsNaN(d), text);
                else
                {
                    Assert.True(ok, text);
                    Assert.Equal(want, Bits(d));
                }
            }
        }

#if NET5_0_OR_GREATER
        [Fact]
        public void ShortestDigitsAreTheShortestRoundTrip()
        {
            // .NET Core 3.0+ "R" is the shortest round-trip string: the same digits
            var rnd = new Random(7);
            var buf = new byte[8];
            for (int i = 0; i < 20000; i++)
            {
                rnd.NextBytes(buf);
                double x = BitConverter.ToDouble(buf, 0);
                if (double.IsNaN(x) || double.IsInfinity(x) || x == 0)
                    continue;
                x = Math.Abs(x);
                FloatText.ShortestDigits(x, out string digits, out int point);
                string net = x.ToString("R", CultureInfo.InvariantCulture);
                // compare digit strings: strip the exponent / point of .NET's form
                string nd = net.Split('E')[0].Replace(".", "").TrimStart('0').TrimEnd('0');
                Assert.Equal(nd, digits);
                Assert.Equal(
                    x,
                    double.Parse(
                        digits.Substring(0, 1)
                            + (digits.Length > 1 ? "." + digits.Substring(1) : "")
                            + "E"
                            + point,
                        CultureInfo.InvariantCulture
                    )
                );
            }
        }
#endif

        [Fact]
        public void Mulberry32ForEverySeed()
        {
            // Data/mulberry32.txt: 1,000 draws a seed, seeds including negative ones and ones
            // beyond 32 bits (the generator takes the low 32 bits, two's complement)
            foreach (string line in File.ReadAllLines(Path.Combine(DataDir(), "mulberry32.txt")))
            {
                if (line.Length == 0)
                    continue;
                string[] f = line.Split(' ');
                var r = Mulberry32.FromSeed(
                    System.Numerics.BigInteger.Parse(f[0], CultureInfo.InvariantCulture)
                );
                for (int i = 1; i < f.Length; i++)
                    Assert.Equal(uint.Parse(f[i], CultureInfo.InvariantCulture), r.Next());
            }
        }

        [Fact]
        public void TextRules()
        {
            Assert.Equal("a", CitectText.Trim(" \t\r\na \v\f"));
            Assert.Equal("a\x1c", CitectText.Trim("  a\x1c\t")); // only the blanks of C isspace
            Assert.Equal(" a ", CitectText.Trim("  a  "));
            Assert.Equal("'a'", CitectText.Quote("a"));
            Assert.Equal("'a'b'", CitectText.Quote("a'b"));
            Assert.Equal(8, (long)CitectText.ParseInt("08")!.Value);
            Assert.Equal(-31, (long)CitectText.ParseInt(" -0x1f ", 16)!.Value);
            Assert.Null(CitectText.ParseInt("1_000"));
            Assert.Null(CitectText.ParseInt("١"));
            Assert.Null(CitectText.ParseInt("+"));
            Assert.Equal("STRASSE", CitectText.Upper("strasse"));
            Assert.Equal("STRAßE", CitectText.Upper("straße"));
            Assert.Equal("µS", CitectText.Upper("µs"));
            Assert.True(CitectText.SameName("Måler", "MÅLER"));
            Assert.True(CitectText.IsDigits("0123"));
            Assert.False(CitectText.IsDigits("²"));
        }

        [Fact]
        public void LosslessWindows1252()
        {
            var all = new byte[256];
            for (int i = 0; i < 256; i++)
                all[i] = (byte)i;
            string s = TextCodec.Decode(all);
            Assert.Equal('€', s[0x80]);
            Assert.Equal('\u0081', s[0x81]);
            Assert.Equal('–', s[0x96]);
            Assert.Equal(all, TextCodec.Encode(s, strict: true));
        }

        [Fact]
        public void DbfFieldsAreTrimmedOfBlanks()
        {
            using var tmp = new TempDir();
            string p = Path.Combine(tmp.Path, "t.dbf");
            WorldFixture.WriteDbf(
                p,
                new[] { ("NAME", 10), ("NAME", 6), ("TYPE", 8), ("DESC", 8) },
                new[]
                {
                    WorldFixture.Row(("NAME", "  a "), ("TYPE", "INT"), ("DESC", " x")),
                    WorldFixture.Row(("NAME", "b"), ("TYPE", "")),
                }
            );
            // a deleted row
            byte[] b = File.ReadAllBytes(p);
            var h = Dbf.Header(p)!;
            b[h.HeaderSize + h.RecordSize] = (byte)'*';
            File.WriteAllBytes(p, b);
            var rows = Dbf.CitectRows(p);
            Assert.Single(rows);
            // a duplicate field name: the LAST field so named wins (the writer filled both)
            Assert.Equal("a", rows[0].Get("NAME"));
            Assert.Equal("INT", rows[0].Get("TYPE"));
            Assert.Equal(" x", rows[0].Get("DESC")); // a non-breaking space is no blank
            Assert.Equal("x", rows[0].Get("MISSING", "x"));
            var cols = Dbf.CitectRows(p, new[] { "TYPE", "NOPE" }, "-");
            Assert.Equal(new[] { "INT", "-" }, cols[0]);
            Assert.Empty(Dbf.CitectRows(Path.Combine(tmp.Path, "none.dbf")));
            File.WriteAllBytes(Path.Combine(tmp.Path, "bad.dbf"), new byte[40]);
            Assert.Empty(Dbf.CitectRows(Path.Combine(tmp.Path, "bad.dbf")));
        }
    }
}
