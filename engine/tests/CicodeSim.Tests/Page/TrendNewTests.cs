using System;
using System.IO;
using System.Linq;
using CicodeSim.Page;
using CicodeSim.Tests.World;
using CicodeSim.Values;
using CicodeSim.World.Display;
using Xunit;
using WorldT = CicodeSim.World.World;

namespace CicodeSim.Tests.Page
{
    /// <summary>TrnNew on an AN that already has a trend, and the default trend format.</summary>
    [Collection("World")]
    public class TrendNewTests : IDisposable
    {
        private readonly TempDir _dir = new TempDir();
        private readonly WorldT _world;

        public TrendNewTests()
        {
            WorldT.ClearCaches();
            string main = WorldFixture.MakeInstall(_dir.Path);
            var cols = new[] { "NAME", "EXPR", "SAMPLEPER", "ENG_UNITS", "FORMAT" };
            WorldFixture.WriteDbf(
                Path.Combine(main, "trend.dbf"),
                WorldFixture.F(40, cols),
                new[]
                {
                    WorldFixture.Row(
                        ("NAME", "TrnTemp"),
                        ("EXPR", "Tank1_Temp"),
                        ("SAMPLEPER", "1"),
                        ("FORMAT", "###.#")
                    ),
                    WorldFixture.Row(("NAME", "TrnLevel"), ("EXPR", "Tank1_Level"), ("SAMPLEPER", "1")),
                    // no FORMAT, and none on Tank1_Temp
                    WorldFixture.Row(
                        ("NAME", "TrnNoFmt"),
                        ("EXPR", "Tank1_Temp"),
                        ("SAMPLEPER", "1"),
                        ("ENG_UNITS", "lt/s")
                    ),
                }
            );
            _world = new WorldT(main, start: WorldFixture.DefaultStart);
            _world.Start();
        }

        public void Dispose() => _dir.Dispose();

        private (Animator a, PageRun r) TrendPage()
        {
            var a = new Animator(_world, new CicodeSim.Compiled.Program());
            var p = new PageFixture();
            p.Row("PGTREND", ("AN", 30L), ("PEN1", "TrnTemp"), ("PEN2", "TrnLevel"), ("PERIODIC", 1L));
            var d = Draw.Obj(30, 0x1003, new Rect(0, 0, 100, 50));
            d.Trend = new TrendInfo { Samples = 10, PixelsPerSample = 10 };
            d.Trend.Pens.Add(new ColourPair(0xFF0000, 0x00FF00));
            Assert.Equal(0, a.Open("TestPage", 0, Draw.Model(d), compiled: p.Build()));
            return (a, a.Run(0)!);
        }

        private static object[] New(string symbol, string pen1 = "") =>
            new object[] { 30, symbol, pen1, "", "", "", "", "", "", "", "" };

        private static string[] Pens(Animator a, PageRun r)
        {
            a.RunScans(1);
            return r.State.Objects[30].Trend!.Pens.Select(p => p.Pen + ":" + p.Tag).ToArray();
        }

        [Fact]
        public void DspTrendInfoAnswersForTheTrendShownAtTheAn()
        {
            var (a, _) = TrendPage();
            long Info(long type) => PageObjectTests.Call(a, "DspTrendInfo", "", type, 30).v.I;
            // line, 10 samples, 50 pixels high, 10 pixels a sample, 2 pens
            Assert.Equal(
                new long[] { 0, 10, 50, 10, 2 },
                new long[] { Info(0), Info(1), Info(2), Info(3), Info(4) }
            );
            // pen 1's colour and its flashing colour; pen 2 has none in the drawing
            Assert.Equal((0xFF0000L, 0x00FF00L, -1L), (Info(11), Info(19), Info(12)));
            // no trend at the AN, or no AN: raised 274, 0
            var (v, t) = PageObjectTests.Call(a, "DspTrendInfo", "Trends.Main_Loop", 1);
            Assert.Equal((0L, CicodeSim.Builtins.Codes.InvalidArg), (v.I, t.Error));
            Assert.Equal(0, PageObjectTests.Call(a, "DspTrendInfo", "", 1, 31).v.I);
        }

        [Fact]
        public void TrnNewKeepsTheTrendOfTheSameDefinition()
        {
            var (a, r) = TrendPage();
            Assert.Equal(new[] { "1:TrnTemp", "2:TrnLevel" }, Pens(a, r));
            // the PGTREND trend has no definition yet: TrnNew keeps it and its pens, the Tags unused
            Assert.Equal(0, PageObjectTests.Call(a, "TrnNew", New("sa_trend.pen4_156_sym", "TrnNoFmt")).v.I);
            Assert.Equal(new[] { "1:TrnTemp", "2:TrnLevel" }, Pens(a, r));
            // TrnNew, TrnSetPen, TrnNew, TrnSetPen ... (SA_Library Trend.ci _SATrend_SetPen)
            Assert.Equal(0, PageObjectTests.Call(a, "TrnSetPen", 30, 3, "TrnNoFmt").v.I);
            Assert.Equal(0, PageObjectTests.Call(a, "TrnNew", New("SA_TREND.pen4_156_sym")).v.I);
            Assert.Equal(new[] { "1:TrnTemp", "2:TrnLevel", "3:TrnNoFmt" }, Pens(a, r));
            // another definition: the trend is made again with the given pens
            Assert.Equal(0, PageObjectTests.Call(a, "TrnNew", New("other.trend", "TrnLevel")).v.I);
            Assert.Equal(new[] { "1:TrnLevel" }, Pens(a, r));
        }

        [Fact]
        public void ATrendWithoutAFormatHasTheCompiledDefault()
        {
            var (a, r) = TrendPage();
            Assert.Equal(0, PageObjectTests.Call(a, "TrnSetPen", 30, 3, "TrnNoFmt").v.I);
            Value w = Value.Variant(new ValueRef(Value.Long(0))),
                d = Value.Variant(new ValueRef(Value.Long(0)));
            Assert.Equal(0, PageObjectTests.Call(a, "TrnGetFormat", 30, 3, w, d).v.I);
            Assert.Equal(6, ((ValueRef)w.O!).Value.I);
            Assert.Equal(2, ((ValueRef)d.O!).Value.I);
            // live (Others, SA_Meters_FB without a FORMAT): '100.00lt/s'
            Assert.Equal(
                "150.00lt/s",
                PageObjectTests.S(PageObjectTests.Call(a, "TrnGetScaleStr", 30, 3, 100, 1).v)
            );
            Assert.Equal(
                "  0.00",
                PageObjectTests.S(PageObjectTests.Call(a, "TrnGetScaleStr", 30, 3, 0, 0).v)
            );
            // a FORMAT of its own: '###.#'
            Assert.Equal(
                "150.0",
                PageObjectTests.S(PageObjectTests.Call(a, "TrnGetScaleStr", 30, 1, 100, 0).v)
            );
        }
    }
}
