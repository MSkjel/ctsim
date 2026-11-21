using System;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CicodeSim.Project;
using CicodeSim.World;
using Xunit;
using static CicodeSim.Tests.World.WorldFixture;
using CMath = CicodeSim.Values.CMath;
using Crt = CicodeSim.Values.Crt;
using FloatOrder = CicodeSim.Values.FloatOrder;
using FloatText = CicodeSim.Values.FloatText;
using JsonKind = CicodeSim.Values.JsonKind;
using JsonValue = CicodeSim.Values.JsonValue;
using WorldT = CicodeSim.World.World;

namespace CicodeSim.Tests.World
{
    /// <summary>The text, number, clock and scenario rules the world follows, one test
    /// each.</summary>
    [Collection("World")]
    public sealed class ReviewWorldTests : IDisposable
    {
        private readonly TempDir _tmp = new TempDir();

        public void Dispose() => _tmp.Dispose();

        private static string DataDir([CallerFilePath] string here = "") =>
            Path.Combine(Path.GetDirectoryName(here)!, "Data");

        private static double FromBits(string hex) =>
            BitConverter.Int64BitsToDouble(unchecked((long)ulong.Parse(hex, NumberStyles.HexNumber)));

        private static string Bits(double d) =>
            unchecked((ulong)BitConverter.DoubleToInt64Bits(d)).ToString("x16");

        // ------------------------------------------------------------------ sin, max, min
        [Fact]
        public void SinIsTheUcrtsOnWindows()
        {
            // the scenario sine generator: net48's Math.Sin is not the Universal CRT's;
            // Data/sin_cases.txt holds the UCRT's answers
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;
            int n = 0;
            foreach (string line in File.ReadAllLines(Path.Combine(DataDir(), "sin_cases.txt")))
            {
                if (line.Length == 0 || line[0] == '#')
                    continue;
                string[] f = line.Split(' ');
                Assert.Equal(f[1], Bits(CMath.Sin(FromBits(f[0]))));
                n++;
            }
            Assert.True(n > 2000);
            Assert.Equal("1.2246467991473532e-16", FloatText.Shortest(CMath.Sin(Math.PI)));
        }

        [Fact]
        public void MaxAndMinKeepTheFirstArgument()
        {
            Assert.Equal(0.0, FloatOrder.Max(0.0, double.NaN));
            Assert.True(double.IsNaN(FloatOrder.Max(double.NaN, 0.0)));
            Assert.Equal(1.0, FloatOrder.Min(1.0, double.NaN));
            Assert.Equal("0.0", FloatText.Shortest(FloatOrder.Max(0.0, -0.0)));
            Assert.Equal("-0.0", FloatText.Shortest(FloatOrder.Max(-0.0, 0.0)));
            Assert.Equal("-0.0", FloatText.Shortest(FloatOrder.Min(-0.0, 0.0)));
            Assert.Equal(3.0, FloatOrder.Max(2.0, 3.0));
            Assert.Equal(2.0, FloatOrder.Min(2.0, 3.0));
        }

        [Fact]
        public void ClockKeepsANaNDelayOutOfItsTime()
        {
            // CallLater(NaN): the delay max(0, NaN) is 0, so the call fires at the next advance
            // and the clock's time stays a number
            var c = new SimClock(0.0);
            int fired = 0;
            c.CallLater(double.NaN, () => fired++);
            c.Advance(0.5);
            Assert.Equal(1, fired);
            Assert.Equal(0.5, c.Now());
            c.CallAt(1.0, () => fired++);
            c.AdvanceTo(2.0);
            Assert.Equal(2, fired);
            Assert.Equal(2.0, c.Now());
        }

        [Fact]
        public void RampWithANaNUntilWritesItsEndValue()
        {
            // the duration max(0, NaN - t0) is 0: the ramp writes 'to' at once
            var w = MakeWorld(_tmp.Path);
            var sc = JsonValue.Parse(
                @"{""format"": ""ctsim-scenario/0"", ""steps"": [
                {""at"": 0.0, ""ramp"": {""tag"": ""Tank1_Temp"", ""from"": 0, ""to"": 10, ""until"": ""nan""}},
                {""at"": 0.5, ""expect"": {""Tank1_Temp"": 10.0}}]}"
            );
            var res = w.RunScenario(sc);
            Assert.Equal(1, res.Passed);
            Assert.Empty(res.Failed);
            Assert.Equal(10.0, w.Tags.Get("Tank1_Temp").D);
        }

        // ------------------------------------------------------------------ numbers in text
        [Fact]
        public void TextNumbersFollowTheirRules()
        {
            // Data/text_number_cases.txt: [text, integer, base-16 integer, the number a string
            // written to a REAL tag stores, quality]. Integers and qualities take ASCII digits
            // with blanks around them (no underscores, no other scripts); the tag conversion keeps
            // its settled rule (digits of any script, Unicode white space, a separator
            // U+001C..U+001F before the number = 433), frozen in the world-operation golden data
            var w = MakeWorld(_tmp.Path);
            var task = Task();
            int n = 0;
            foreach (string line in File.ReadAllLines(Path.Combine(DataDir(), "text_number_cases.txt")))
            {
                if (line.Length == 0 || line[0] == '#')
                    continue;
                var c = JsonValue.Parse(line);
                var text = c.Items[0];
                Assert.Equal(c.Items[4].Str, Q(text));
                if (text.Kind != JsonKind.String)
                    continue;
                string s = text.Str,
                    label = CitectText.Quote(s);
                Assert.True(c.Items[1].Str == Int(s, 10), label);
                Assert.True(c.Items[2].Str == Int(s, 16), label);
                // through a REAL tag: 433, or the number stored as the tag's 4-byte float
                int err = w.Tags.Write(Ref("Motor_Speed"), Str(s), task);
                string want = c.Items[3].Str;
                if (want == "error")
                    Assert.True(err == TagDb.ErrTypeMismatch, label);
                else
                {
                    Assert.True(err == 0, label);
                    double x = want.StartsWith("int:", StringComparison.Ordinal)
                        ? FloatText.FromInteger(
                            System.Numerics.BigInteger.Parse(want.Substring(4), CultureInfo.InvariantCulture)
                        )
                        : double.Parse(want, CultureInfo.InvariantCulture);
                    Assert.True((double)(float)x == w.Tags.Get("Motor_Speed").D, label);
                }
                n++;
            }
            Assert.True(n > 40);
        }

        private static string Int(string s, int b)
        {
            var v = CitectText.ParseInt(s, b);
            return v == null ? "error" : v.Value.ToString(CultureInfo.InvariantCulture);
        }

        private static string Q(JsonValue v)
        {
            try
            {
                return Quality.Parse(v).ToString(CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                return "error";
            }
        }

        [Fact]
        public void BigIntegerQualitiesKeepTheirLow64Bits()
        {
            // a quality keeps the low 64 bits of its number, bit 63 too (a data-source error code
            // >= 0x80000000)
            var w = MakeWorld(_tmp.Path);
            Assert.Equal(0, w.Tags.Set("Pump1_Run", JsonValue.Of(1), JsonValue.Of(13835058055282163904UL)));
            Assert.Equal(13835058055282163904UL, w.Tags.Get("Pump1_Run").Q);
            Assert.Equal(0UL, Quality.Parse(JsonValue.Of(System.Numerics.BigInteger.One << 64)));
        }

        // ------------------------------------------------------------------ text
        [Fact]
        public void LowerAndUpperOfEveryWindows1252Character()
        {
            // Data/cp1252_case_mapping.txt: one to one; net10.0's invariant casing maps U+00B5
            // (micro sign) to U+039C, which Windows-1252 has no byte for
            int n = 0;
            foreach (string line in File.ReadAllLines(Path.Combine(DataDir(), "cp1252_case_mapping.txt")))
            {
                if (line.Length == 0 || line[0] == '#')
                    continue;
                string[] f = line.Split('\t');
                char c = (char)int.Parse(f[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                Assert.Equal(
                    TextCodec.Decode1252[
                        int.Parse(f[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    ],
                    c
                );
                Assert.Equal(f[2], Hex(CitectText.Lower(c.ToString())));
                Assert.Equal(f[3], Hex(CitectText.Upper(c.ToString())));
                n++;
            }
            Assert.Equal(256, n);
        }

        private static string Hex(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char ch in s)
            {
                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ users
        [Fact]
        public void HugeNumbersInRolesAndLoginsDoNotThrow()
        {
            // 99999999999 stays in the set, clamped to int.MaxValue (a global privilege then
            // makes every area viewable)
            var s = Users.ParseList("99999999999, 3, -99999999999");
            Assert.Equal(3, s.Count);
            Assert.Contains(3, s);
            Assert.Contains(int.MaxValue, s);
            Assert.Contains(int.MinValue, s);
            var w = MakeWorld(_tmp.Path);
            var sess = w.Users.LoginAs(
                "U",
                privileges: JsonValue.Parse("{\"99999999999\": 1, \"0\": [99999999999]}"),
                areas: JsonValue.Parse("[99999999999, 7]")
            );
            Assert.Contains(int.MaxValue, sess.GlobalPrivs);
            Assert.True(w.Users.CanView(5)); // any global privilege views every area
            Assert.Equal(0, w.Users.GetPriv(1, 5)); // exclusive: level 1 is not held
            w.Users.Exclusive = false;
            Assert.Equal(1, w.Users.GetPriv(8, 5)); // a higher level implies the lower ones
            Assert.Contains(int.MaxValue, sess.ViewAreas);
        }

        // ------------------------------------------------------------------ scenarios
        [Fact]
        public void BadGeneratorArgumentsAreReportedAsBadSteps()
        {
            // a bad generator is a bad-step error and the run goes on: an 'every' of NaN, a list,
            // a string or a number instead of the generator's object, no 'tag'
            var w = MakeWorld(_tmp.Path);
            var sc = JsonValue.Parse(
                @"{""format"": ""ctsim-scenario/0"", ""steps"": [
                {""at"": 0.0, ""random"": {""tag"": ""Motor_Speed"", ""every"": ""nan"", ""seed"": 3}},
                {""at"": 0.0, ""ramp"": [1, 2]},
                {""at"": 0.0, ""sine"": ""Tank1_Temp""},
                {""at"": 0.0, ""ramp"": 5},
                {""at"": 0.0, ""ramp"": {""from"": 0}},
                {""at"": 1.0, ""expect"": {""Pump1_Run"": 0}}]}"
            );
            var res = w.RunScenario(sc);
            Assert.Equal(5, res.Errors.Count);
            Assert.Equal(
                ("random Motor_Speed", "bad-step", "'every' must be above 0"),
                (res.Errors[0].step, res.Errors[0].code, res.Errors[0].reason)
            );
            Assert.Equal(
                ("ramp [1,2]", "bad-step", "the step's value must be an object, not [1,2]"),
                (res.Errors[1].step, res.Errors[1].code, res.Errors[1].reason)
            );
            Assert.Equal(("sine Tank1_Temp", "bad-step"), (res.Errors[2].step, res.Errors[2].code));
            Assert.Equal(("ramp 5", "bad-step"), (res.Errors[3].step, res.Errors[3].code));
            Assert.Equal(
                ("ramp", "bad-step", "no 'tag'"),
                (res.Errors[4].step, res.Errors[4].code, res.Errors[4].reason)
            );
            Assert.Equal(1, res.Passed);
            Assert.Equal(1.0, res.End);
        }

        [Fact]
        public void WorldOpsWritesOneResultPerOperationAndTheTrace()
        {
            string main = MakeInstall(_tmp.Path);
            string ops = Path.Combine(_tmp.Path, "ops.jsonl"),
                outPath = Path.Combine(_tmp.Path, "out.txt");
            File.WriteAllLines(
                ops,
                new[]
                {
                    @"{""op"": ""start""}",
                    @"{""op"": ""write"", ""name"": ""Tank1_Temp"", ""element"": """", ""item"": """", ""cluster"": """", ""value"": {""t"": 2, ""v"": 125.0}, ""flags"": 15, ""index"": null}",
                    @"{""op"": ""advance"", ""dt"": 0.5}",
                    @"{""op"": ""read"", ""name"": ""ALM_TEMP.On"", ""element"": """", ""item"": """", ""cluster"": """", ""light"": 0, ""index"": null}",
                    @"{""op"": ""set"", ""name"": ""Pump1_Run"", ""element"": """", ""value"": 1, ""quality"": 13835058055282163904, ""index"": null}",
                    @"{""op"": ""nope""}",
                }
            );
            WorldOps.Run(main, ops, outPath);
            string[] lines = File.ReadAllLines(outPath);
            Assert.Equal(@"{""op"":""start"",""res"":null}", lines[0]);
            Assert.Equal(@"{""op"":""write"",""res"":[0,0]}", lines[1]);
            Assert.StartsWith(@"{""op"":""read"",""res"":[[4,1,192,", lines[3]);
            Assert.Equal(@"{""op"":""set"",""res"":0}", lines[4]);
            Assert.Equal(
                @"{""op"":""nope"",""error"":{""code"":""not-found"",""message"":""no operation 'nope'""}}",
                lines[5]
            );
            Assert.Equal("#trace", lines[6]);
            Assert.Contains(lines, l =>
                l.Contains(@"""ev"":""alarm""") && l.Contains(@"""tag"":""ALM_TEMP""")
            );
        }

        [Fact]
        public void CheckRangeWithANaNEndKeepsTheOtherEnd()
        {
            // (min(ez, ef), max(ez, ef)) with ef NaN is (ez, ez), not (NaN, NaN), which would let
            // every value through ScaleCheck
            var d = new TagDef("T", "variable", "p", "REAL", engZero: 5.0, engFull: double.NaN);
            Assert.Equal((5.0, 5.0), d.CheckRange());
            d = new TagDef("T", "variable", "p", "REAL", engZero: double.NaN, engFull: 5.0);
            var (lo, hi) = d.CheckRange();
            Assert.True(double.IsNaN(lo) && double.IsNaN(hi));
        }

        // ------------------------------------------------------------------ clock.start
        [Theory]
        [InlineData("2026-10-01T12:00:00Z", 1790856000.0)]
        [InlineData("2026-10-01t12:00:00z", 1790856000.0)]
        [InlineData("2026-10-01 12:00:00Z", 1790856000.0)]
        [InlineData("2026-10-01T12:00:00,5Z", 1790856000.5)]
        [InlineData("2026-10-01T12:00:00.1234567Z", 1790856000.123456)]
        [InlineData("20261001T120000Z", 1790856000.0)]
        [InlineData("20261001T12Z", 1790856000.0)]
        [InlineData("2026-10-01T12:30Z", 1790857800.0)]
        [InlineData("2026-W40-4T12:00:00Z", 1790856000.0)]
        [InlineData("2026W404T12:00:00Z", 1790856000.0)]
        [InlineData("2026-W40T00:00:00Z", 1790553600.0)]
        [InlineData("2026-10-01T12:00:00+01:00", 1790852400.0)]
        [InlineData("2026-10-01T12:00:00-0130", 1790861400.0)]
        [InlineData("2026-10-01T12:00:00+01", 1790852400.0)]
        [InlineData("2026-10-01T24:00:00Z", 1790899200.0)]
        [InlineData("2026-12-31T24:00Z", 1798761600.0)]
        [InlineData("0001-01-01T00:00:00Z", -62135596800.0)]
        [InlineData(" 2026-10-01T12:00:00.000Z ", 1790856000.0)]
        public void ParseStartReadsIso8601(string text, double want)
        {
            Assert.Equal(want, SimClock.ParseStart(text));
        }

        [Theory]
        [InlineData("2026-10-01T12:00:00+25:00")]
        [InlineData("2026-10-01T12:00:00+01:00:30")]
        [InlineData("2026-10-01T12:00:60Z")]
        [InlineData("2026-02-29T00:00:00Z")]
        [InlineData("2026-02-29T24:30:00Z")]
        [InlineData("2026-10-01T24:00:01Z")]
        [InlineData("2026-10-01X12:00:00Z")]
        [InlineData("2026-10-01T12:00:00+00:00Z")]
        [InlineData("2026-10-01T12:00:00+01:3")]
        [InlineData("2026-10-01T12:00:00.Z")]
        [InlineData("2026-W54-1")]
        [InlineData("2026-W40-8")]
        [InlineData("2026-1001")]
        [InlineData("26-10-01")]
        [InlineData("")]
        public void ParseStartRefusesWhatIsNoDateAndTime(string text)
        {
            // the scenario reports it as a bad-start error and keeps its start
            var e = Assert.Throws<FormatException>(() => SimClock.ParseStart(text));
            Assert.Equal("not an ISO 8601 date and time: " + CitectText.Quote(text), e.Message);
        }

        [Fact]
        public void NaiveStartIsTheCrtsMktime()
        {
            // a start without an offset is local time as the Universal CRT's _mktime64 reads it
            // (one daylight saving rule for every year, a nonexistent hour read as daylight time,
            // nothing before 1970-01-01 UTC), not as .NET's local zone reads it. Values in Pacific
            // Standard Time.
            if (TimeZoneInfo.Local.Id != "Pacific Standard Time" || !Crt.Mktime64IsUcrt)
                return;
            Assert.Equal(1772962200.0, Crt.Mktime64(2026, 3, 8, 2, 30, 0));
            Assert.Equal(1793525400.0, Crt.Mktime64(2026, 11, 1, 1, 30, 0));
            Assert.Equal(1111345200.0, Crt.Mktime64(2005, 3, 20, 12, 0, 0));
            Assert.Equal(0.0, Crt.Mktime64(1969, 12, 31, 16, 0, 0));
            Assert.Equal(32535244800.0, Crt.Mktime64(3001, 1, 1, 0, 0, 0));
            Assert.Equal(1772352000.0, Crt.Mktime64(2026, 2, 28, 24, 0, 0));
            Assert.Throws<OverflowException>(() => Crt.Mktime64(1969, 12, 31, 15, 59, 59));
            Assert.Throws<OverflowException>(() => Crt.Mktime64(9999, 12, 31, 23, 59, 59));
            Assert.Equal(1772962200.0, SimClock.ParseStart("2026-03-08T02:30:00"));
            Assert.Equal(1790838000.0, SimClock.ParseStart("2026-10-01"));
        }

        [Fact]
        public void ANaNStartIsRefusedAndKeepsTheStart()
        {
            var c = new SimClock(0.0);
            var e = Assert.Throws<FormatException>(() => c.SetStart(JsonValue.Parse("NaN")));
            Assert.Equal("not a start time: nan", e.Message);
            Assert.Equal(0.0, c.StartUnix);
            Assert.Throws<FormatException>(() => c.SetStart(double.PositiveInfinity));
            Assert.Equal(0.0, c.StartUnix);
        }

        [Fact]
        public void AStartOutsideTheClocksRangeIsABadStart()
        {
            // the clock runs on FILETIMEs from 1601-01-01 to DateTime's last tick; a local time
            // _mktime64 refuses is a bad start too
            var c = new SimClock(0.0);
            foreach (
                string s in new[]
                {
                    "1e300",
                    "-1e300",
                    "-1e12",
                    "\"0001-01-01T00:00:00Z\"",
                    "\"1600-12-31T23:59:59Z\"",
                }
            )
            {
                Assert.Throws<FormatException>(() => c.SetStart(JsonValue.Parse(s)));
                Assert.Equal(0.0, c.StartUnix);
            }
            c.SetStart(JsonValue.Parse("\"1601-01-01T00:00:00Z\""));
            Assert.Equal(0L, c.StartFiletime);
            var c2 = new SimClock(0.0);
            c2.SetStart(JsonValue.Parse("\"9999-12-31T23:59:59Z\""));
            Assert.InRange(c2.StartFiletime, SimClock.MaxFiletime - 10_000_000, SimClock.MaxFiletime);
            if (!Crt.Mktime64IsUcrt)
                return; // off Windows .NET's local zone takes 1900
            Assert.Throws<FormatException>(() => new SimClock(0.0).SetStart("1900-01-01T00:00:00"));
            var w = MakeWorld(_tmp.Path);
            var res = w.RunScenario(
                JsonValue.Parse(
                    @"{""format"": ""ctsim-scenario/0"", ""clock"": {""start"": ""1900-01-01T00:00:00""},
                ""steps"": [{""at"": 1.0, ""expect"": {""Pump1_Run"": 0}}]}"
                )
            );
            Assert.Equal(("clock.start", "bad-start"), (res.Errors[0].step, res.Errors[0].code));
            Assert.Equal(1, res.Passed);
        }

        // ------------------------------------------------------------------ JSON
        [Fact]
        public void JsonParseCases()
        {
            // scenario / ops files: Data/json_parse_cases.txt, the text and its value as compact
            // JSON (non-ASCII escaped) or 'E' for a FormatException (atoms, escapes, numbers,
            // white space, nesting, duplicate keys). A \u escape takes exactly 4 hex digits.
            string path = Path.Combine(DataDir(), "json_parse_cases.txt");
            int n = 0,
                bad = 0;
            string first = "";
            foreach (string line in File.ReadAllLines(path, System.Text.Encoding.UTF8))
            {
                if (line.Length == 0)
                    continue;
                int tab = line.IndexOf('\t');
                string input = JsonValue.Parse(line.Substring(0, tab)).Str;
                string want = JsonValue.Parse(line.Substring(tab + 1)).Str;
                string got;
                try
                {
                    got = AsciiJson(JsonValue.Parse(input));
                }
                catch (FormatException)
                {
                    got = "E";
                }
                n++;
                if (got != want && bad++ < 6)
                    first += " | " + CitectText.Quote(input) + ": " + got + " != " + want;
            }
            Assert.True(bad == 0, bad + " of " + n + " differ:" + first);
            Assert.True(n > 500);
            Assert.Throws<FormatException>(() => JsonValue.Parse("\"\\u 12a\""));
            Assert.Throws<FormatException>(() => JsonValue.Parse("\"\\u12a \""));
            Assert.Equal("\u00e9", JsonValue.Parse("\"\\u00E9\"").Str);
        }

        /// <summary>The compact JSON text with every character outside ' '..'~' as a \u escape.</summary>
        internal static string AsciiJson(JsonValue v)
        {
            string s = v.Dump();
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c >= ' ' && c <= '~')
                    sb.Append(c);
                else
                    sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        [Fact]
        public void CteditUserTakesBlanksAroundTheKey()
        {
            string ini = Path.Combine(_tmp.Path, "citect.ini");
            File.WriteAllBytes(
                ini,
                TextCodec.Encode(
                    "[General]\r\nUSER=C:\\wrong\r\n[CtEdit]\r\n \tuser \t= \t C:\\Users\\x \t\r\n"
                )
            );
            Assert.Equal(@"C:\Users\x", CitectIni.CteditUser(ini));
        }
    }
}
