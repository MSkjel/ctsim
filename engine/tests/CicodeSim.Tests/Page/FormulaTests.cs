using System.Collections.Generic;
using CicodeSim.Page;
using CicodeSim.World.Display;
using Xunit;

namespace CicodeSim.Tests.Page
{
    /// <summary>The animation class arithmetic.</summary>
    public class FormulaTests
    {
        [Fact]
        public void MovementMapsPercentAndTruncatesTowardZero()
        {
            // X: -MVXMINPT at 0 %, MVXMAXPT at 100 %
            Assert.Equal(-10, Formulas.MoveX(0, 10, 20));
            Assert.Equal(20, Formulas.MoveX(100, 10, 20));
            Assert.Equal(5, Formulas.MoveX(50, 10, 20));
            Assert.Equal(33, Formulas.MoveX(33.3, 0, 100));
            Assert.Equal(-33, Formulas.MoveX(66.7, 100, 0)); // -33.3 truncates toward zero
            // clamped: above 100 at maximum, below 0 at minimum
            Assert.Equal(20, Formulas.MoveX(250, 10, 20));
            Assert.Equal(-10, Formulas.MoveX(-5, 10, 20));
            // Y: MVYMINPT (down) at 0 %, -MVYMAXPT (up) at 100 %
            Assert.Equal(5, Formulas.MoveY(0, 5, 10));
            Assert.Equal(-10, Formulas.MoveY(100, 5, 10));
            Assert.Equal(-2, Formulas.MoveY(50, 5, 10)); // 5 + 50 * -15 / 100 = -2.5 -> -2
            // (int)(short)(int): 16 bits
            Assert.Equal(-31072, Formulas.TruncShort(34464.0));
        }

        [Fact]
        public void ScalingRotationAndFill()
        {
            Assert.Equal(0.5, Formulas.ScaleFactor(50, 0, 100));
            Assert.Equal(1.0, Formulas.ScaleFactor(50, 50, 150));
            Assert.Equal(1.5, Formulas.ScaleFactor(120, 50, 150));
            // degrees, clockwise: -ROTMINPC at 0 %, ROTMAXPC at 100 %
            Assert.Equal(180.0, Formulas.RotateDegrees(50, 0, 360), 9);
            Assert.Equal(-90.0, Formulas.RotateDegrees(0, 90, 270), 9);
            Assert.Equal(270.0, Formulas.RotateDegrees(100, 90, 270), 9);
            Assert.Equal(90.0, Formulas.RotateDegrees(50, 90, 270), 9);
            Assert.Equal(0.25, Formulas.FillFraction(25, 0, 100));
            Assert.Equal(0.6, Formulas.FillFraction(50, 20, 100), 12);
        }

        [Fact]
        public void PivotsAreExpressOrCustomWithCDivision()
        {
            // f(-1, n) = 0, f(1, n) = n, else n / 2
            Assert.Equal(0, Formulas.Express(-1, 41));
            Assert.Equal(41, Formulas.Express(1, 41));
            Assert.Equal(20, Formulas.Express(0, 41));
            Assert.Equal(20, Formulas.Express(5, 41)); // any other value: the centre
            Assert.Equal(-20, Formulas.Express(0, -41)); // C division of a negative width
            // Custom: n / 2 + offset
            Assert.Equal(28, Formulas.Pivot(1, 8, 41));
            Assert.Equal(7, Formulas.Pivot(1, -13, 41));
            Assert.Equal(41, Formulas.Pivot(0, 1, 41));
        }

        [Fact]
        public void VertexIsNotClampedAndSlidersInvertTheMap()
        {
            Assert.Equal(-50.0, Formulas.Vertex(50, 0, -100));
            Assert.Equal(-150.0, Formulas.Vertex(150, 0, -100)); // no clamp seen (verify)
            Assert.Equal(10.0, Formulas.Vertex(0, 10, 20));
            Assert.Equal(50.0, Formulas.SliderPercent(50, 0, 100));
            Assert.Equal(25.0, Formulas.SliderPercent(-50, -100, 100));
            Assert.Equal(100.0, Formulas.SliderPercent(400, 0, 100));
            Assert.Equal(0.0, Formulas.SliderPercent(-5, 0, 100));
        }

        [Fact]
        public void TouchRepeatSchedule()
        {
            // T = RPT_RATE + 1, first repeat after (1000 / T + 1) ticks
            Assert.Equal((501, 1002), Formulas.RepeatSchedule(500));
            Assert.Equal((101, 1010), Formulas.RepeatSchedule(100));
            Assert.Equal((200, 1200), Formulas.RepeatSchedule(199)); // 6 ticks: says 1000 (an arithmetic slip)
        }

        [Fact]
        public void ErrorTextsFollowTheTable()
        {
            var want = new Dictionary<int, string>
            {
                [0] = "#OK",
                [32] = "#WAIT",
                [257] = "#RANGE",
                [272] = "#MEM",
                [273] = "#DIV/0",
                [295] = "#STACK",
                [323] = "#ASC",
                [324] = "#ASC",
                [344] = "#OVR",
                [423] = "#PEND",
                [432] = "#PEND",
                [53] = "#COM",
                [281] = "#COM",
                [402] = "#COM",
                [403] = "#COM",
                [404] = "#COM",
                [405] = "#COM",
                [418] = "#COM",
                [424] = "#COM",
                [425] = "#COM",
                [525] = "#COM",
                [532] = "#COM",
                [7] = "#COM",
                [255] = "#COM",
                [256] = "#ERR",
                [274] = "#ERR",
                [433] = "#ERR",
            };
            foreach (var kv in want)
                Assert.Equal(kv.Value, Indication.ErrorText(kv.Key));
        }

        [Fact]
        public void QualityFlagsAndTextIndication()
        {
            var cfg = new DisplayConfig();
            // byte 0 bits 7..6 0 bad, 1 uncertain; byte 2 bit 0 override, bit 1 inhibit
            Assert.Equal(0, Indication.Flags(0xC0, 0, false));
            Assert.Equal(Indication.Bad, Indication.Flags(0x00, 0, false));
            Assert.Equal(Indication.Uncertain, Indication.Flags(0x40, 0, false));
            Assert.Equal(Indication.Override, Indication.Flags(0xC0 | 0x10000, 0, false));
            Assert.Equal(Indication.Inhibit, Indication.Flags(0xC0 | 0x20000, 0, false));
            Assert.Equal(0, Indication.Flags(0xC0, 257, false)); // RangeCheck off drops 257
            Assert.Equal(Indication.Error, Indication.Flags(0xC0, 257, true));
            Assert.Equal(Indication.Error | Indication.Bad, Indication.Flags(0x00, 424, false));
            // mode 0 (#COM)
            Assert.Equal(
                "#BAD",
                Indication.Text("12.3", Indication.Error | Indication.Bad, 424, 0, cfg, out var r1)
            );
            Assert.Equal("#BAD", r1);
            Assert.Equal(
                "#UNC",
                Indication.Text("12.3", Indication.Error | Indication.Uncertain, 424, 0, cfg, out _)
            );
            Assert.Equal("#DIV/0", Indication.Text("0", Indication.Error, 273, 0, cfg, out _));
            // bad quality without an error code shows the value (flags & 5 == 5)
            Assert.Equal("12.3", Indication.Text("12.3", Indication.Bad, 0, 0, cfg, out var r2));
            Assert.Null(r2);
            // mode 1 (No) and 2 (background) show the value
            Assert.Equal(
                "12.3",
                Indication.Text("12.3", Indication.Error | Indication.Bad, 424, 1, cfg, out _)
            );
            Assert.Equal(
                "12.3",
                Indication.Text("12.3", Indication.Error | Indication.Bad, 424, 2, cfg, out _)
            );
            var off = new DisplayConfig { ShowErrorText = false };
            Assert.Equal("0", Indication.Text("0", Indication.Error, 273, 0, off, out _));
            Assert.Equal("bad", Indication.Name(Indication.Bad | Indication.Error, 0));
            Assert.Null(Indication.Name(Indication.Bad, 1));
        }

        [Fact]
        public void KeySequencesTakeHashDataIntoArguments()
        {
            var keys = new KeyTable();
            keys.Add("ENTER", 0x0D);
            keys.Add("F2", 0x0171);
            List<int> Buf(params object[] k)
            {
                var l = new List<int>();
                foreach (var x in k)
                    l.Add(x is string s ? s[0] : (int)x);
                return l;
            }
            var seq = keys.Sequence("### ENTER");
            Assert.Equal(new[] { 0x23, 0x23, 0x23, 0x0D }, seq.ToArray());
            Assert.Equal(KeyMatch.Result.Prefix, KeyMatch.Match(seq, Buf("1", "2"), ',', out _));
            Assert.Equal(
                KeyMatch.Result.Complete,
                KeyMatch.Match(seq, Buf("1", "2", "3", 0x0D), ',', out var a)
            );
            Assert.Equal("123", a[0]);
            Assert.Equal("", a[1]);
            // fewer characters than '#' are fine, more are not
            Assert.Equal(KeyMatch.Result.Complete, KeyMatch.Match(seq, Buf("7", 0x0D), ',', out a));
            Assert.Equal("7", a[0]);
            Assert.Equal(
                KeyMatch.Result.None,
                KeyMatch.Match(seq, Buf("1", "2", "3", "4", 0x0D), ',', out _)
            );
            // two runs split by the separator
            var two = keys.Sequence("##,## ENTER");
            Assert.Equal(
                KeyMatch.Result.Complete,
                KeyMatch.Match(two, Buf("1", "2", ",", "3", "4", 0x0D), ',', out a)
            );
            Assert.Equal("12", a[0]);
            Assert.Equal("34", a[1]);
            // '#*' = any length
            var star = keys.Sequence("#* ENTER");
            Assert.Equal(
                KeyMatch.Result.Complete,
                KeyMatch.Match(star, Buf("a", "b", "c", "d", "e", 0x0D), ',', out a)
            );
            Assert.Equal("abcde", a[0]);
            // plain keys
            var f2 = keys.Sequence("F2 ENTER");
            Assert.Equal(KeyMatch.Result.Prefix, KeyMatch.Match(f2, Buf(0x0171), ',', out _));
            Assert.Equal(KeyMatch.Result.Complete, KeyMatch.Match(f2, Buf(0x0171, 0x0D), ',', out _));
            Assert.Equal(KeyMatch.Result.None, KeyMatch.Match(f2, Buf(0x0D), ',', out _));
        }
    }
}
