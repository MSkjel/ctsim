using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CicodeSim.Builtins;
using CicodeSim.Compiled;
using CicodeSim.Page;
using CicodeSim.Tests.World;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World;
using CicodeSim.World.Display;
using Xunit;
using WorldT = CicodeSim.World.World;

namespace CicodeSim.Tests.Page
{
    /// <summary>Trend objects and their functions on the synthetic install.</summary>
    [Collection("World")]
    public class PageObjectTests : IDisposable
    {
        private readonly TempDir _dir = new TempDir();
        private readonly WorldT _world;

        public PageObjectTests()
        {
            WorldT.ClearCaches();
            string main = WorldFixture.MakeInstall(_dir.Path);
            var cols = new[]
            {
                "NAME",
                "EXPR",
                "SAMPLEPER",
                "ENG_ZERO",
                "ENG_FULL",
                "ENG_UNITS",
                "FORMAT",
                "COMMENT",
                "TYPE",
            };
            WorldFixture.WriteDbf(
                Path.Combine(main, "trend.dbf"),
                WorldFixture.F(40, cols),
                new[]
                {
                    WorldFixture.Row(
                        ("NAME", "TrnTemp"),
                        ("EXPR", "Tank1_Temp"),
                        ("SAMPLEPER", "00:00:01"),
                        ("ENG_ZERO", "0"),
                        ("ENG_FULL", "150"),
                        ("ENG_UNITS", "degC"),
                        ("FORMAT", "###.#"),
                        ("COMMENT", "Tank 1 temperature")
                    ),
                    WorldFixture.Row(
                        ("NAME", "TrnLevel"),
                        ("EXPR", "Tank1_Level"),
                        ("SAMPLEPER", "2"),
                        ("FORMAT", "####.##")
                    ),
                    WorldFixture.Row(("NAME", "TrnExpr"), ("EXPR", "Tank1_Temp * 2"), ("SAMPLEPER", "1")),
                    WorldFixture.Row(("NAME", "TrnTank2"), ("EXPR", "Plant.Tank2.Level"), ("SAMPLEPER", "1")),
                }
            );
            var spc = new[]
            {
                "NAME",
                "EXPR",
                "SAMPLEPER",
                "ENG_UNITS",
                "FORMAT",
                "LSL",
                "USL",
                "SUBGRPSIZE",
                "XDOUBLEBAR",
                "RANGE",
                "SDEVIATION",
            };
            WorldFixture.WriteDbf(
                Path.Combine(main, "spc.dbf"),
                WorldFixture.F(40, spc),
                new[]
                {
                    WorldFixture.Row(
                        ("NAME", "Weight_SPC"),
                        ("EXPR", "400 + Tank1_Level"),
                        ("SAMPLEPER", "2"),
                        ("ENG_UNITS", "kg"),
                        ("LSL", "390"),
                        ("USL", "460"),
                        ("SUBGRPSIZE", "4"),
                        ("RANGE", "12.5")
                    ),
                }
            );
            _world = new WorldT(main, start: WorldFixture.DefaultStart);
            _world.Start();
        }

        public void Dispose() => _dir.Dispose();

        private Animator Make(CicodeSim.Compiled.Program? lib = null) =>
            new Animator(_world, lib ?? new CicodeSim.Compiled.Program());

        /// <summary>One built-in call by a task of the animator's VM in window 0 (a waiting call
        /// gives the value it computes when the task wakes).</summary>
        internal static (Value v, CicodeTask t) Call(Animator a, string name, params object[] args) =>
            CallIn(a, false, name, args);

        internal static (Value v, CicodeTask t) CallIn(
            Animator a,
            bool fg,
            string name,
            params object[] args
        ) => CallAt(a, fg, 0, name, args);

        internal static (Value v, CicodeTask t) CallWin(
            Animator a,
            int window,
            string name,
            params object[] args
        ) => CallAt(a, false, window, name, args);

        internal static (Value v, CicodeTask t) CallAt(
            Animator a,
            bool fg,
            int window,
            string name,
            object[] args
        )
        {
            var b = Func0Table.Default.Lookup(name);
            Assert.True(b != null, name);
            var t = new CicodeTask(a.Vm, "test", fg);
            TaskContext.Set(t, window, -1);
            var vals = args.Select(x => x is Value v ? v : CicodeVm.ValueOf(x)).ToArray();
            var r = a.Vm.Builtins!.Call(b!.Index, t, ArgList.Of(vals));
            Assert.False(r.IsNotSimulated, name + " is not simulated");
            if (r.IsBlock)
                r = t.Wait!.Result!() ?? Value.Nothing;
            return (r, t);
        }

        internal static string S(Value v) => v.T == Types.String ? v.S ?? "" : Conv.ValueText(v, out _);

        private static Value Ref(Value initial) => Value.Variant(new ValueRef(initial));

        private static Value Held(Value r) => ((ValueRef)r.O!).Value;

        // ================================================================== trends
        private (Animator a, PageRun r) TrendPage(int samples = 10)
        {
            var a = Make();
            var p = new PageFixture();
            p.Row("PGTREND", ("AN", 30L), ("PEN1", "TrnTemp"), ("PEN2", "TrnLevel"), ("PERIODIC", 1L));
            var d = Draw.Obj(30, 0x1003, new Rect(0, 0, 100, 50));
            d.Trend = new TrendInfo { Samples = samples, PixelsPerSample = 10 };
            d.Trend.Pens.Add(ColourPair.Steady(0x00FF00));
            d.Trend.Pens.Add(new ColourPair(0xFF0000, 0x0000FF));
            Assert.Equal(0, a.Open("TestPage", 0, Draw.Model(d), compiled: p.Build()));
            return (a, a.Run(0)!);
        }

        [Fact]
        public void TrendStateCarriesThePensSamplesAfterARamp()
        {
            var (a, r) = TrendPage();
            // a ramp: Tank1_Temp 0, 10, 20 ... one step a second
            for (int i = 0; i <= 12; i++)
            {
                _world.Tags.Set("Tank1_Temp", 10.0 * i);
                a.RunFor(1.0);
            }
            var tr = r.State.Objects[30].Trend!;
            Assert.Equal(1.0, tr.Period); // the smallest SAMPLEPER of the pens
            Assert.Equal(10.0, tr.Span); // samples x period
            Assert.Equal(10, tr.Samples);
            Assert.False(tr.Historical);
            Assert.Equal(2, tr.Pens.Count);
            var p1 = tr.Pens[0];
            Assert.Equal("TrnTemp", p1.Tag);
            Assert.Equal(0.0, p1.Lo);
            Assert.Equal(150.0, p1.Hi);
            Assert.Equal(0x00FF00L, p1.Colour);
            Assert.Equal(10, p1.Values.Length);
            // oldest first, one sample a second on the period's grid back from the newest: the
            // value set at second i is 10 i
            Assert.Equal(Math.Floor(p1.End), p1.End);
            for (int k = 0; k < 10; k++)
            {
                double at = p1.End - (9 - k);
                Assert.Equal(Math.Min(120.0, 10.0 * Math.Floor(at)), p1.Values[k]);
            }
            Assert.Equal(120.0, p1.Values[9]);
            // the second pen: Tank1_Level never written, 0 all along; its colour flashes
            Assert.All(tr.Pens[1].Values, v => Assert.Equal(0.0, v));
            // the JSON of the state
            var j = r.State.Objects[30].ToJson()["trend"];
            Assert.Equal("realtime", j["mode"].Str);
            Assert.Equal(10, j["pens"].Items[0]["values"].Count);
            Assert.Equal(120.0, j["pens"].Items[0]["values"].Items[9].ToNumber());
            Assert.Contains(tr.Pens[1].Colour, new long?[] { 0xFF0000, 0x0000FF }); // a flashing pen: the phase's half
        }

        [Fact]
        public void TagHistoryKeepsOnlyTrendTags()
        {
            var a = Make();
            var hist = TagHistory.Attach(_world);
            // Tank1_Temp (TrnTemp, TrnExpr) and Tank1_Level (TrnLevel) are trend tags
            Assert.True(hist.IsWatched("Tank1_Temp"));
            Assert.True(hist.IsWatched("tank1_level"));
            Assert.False(hist.IsWatched("Motor_Speed"));
            Assert.True(hist.IsWatched("Tank2_Level")); // read by its Equip.Item (TrnTank2)
            int before = hist.Entries;
            _world.Tags.Set("Motor_Speed", 5.0);
            _world.Tags.Set("Motor_Speed", 6.0);
            Assert.Equal(before, hist.Entries); // not logged
            _world.Tags.Set("Tank1_Temp", 7.0);
            Assert.Equal(before + 1, hist.Entries);
            // a read watches the tag from then on: its value since its last change is known
            a.RunFor(1);
            Assert.True(hist.TryValueAt("Motor_Speed", _world.Clock.Now(), out double v, out _));
            Assert.Equal(6.0, v);
            Assert.True(hist.IsWatched("Motor_Speed"));
            _world.Tags.Set("Motor_Speed", 8.0);
            Assert.Equal(before + 2, hist.Entries);
        }

        [Fact]
        public void TrnFunctionsReadAndChangeTheTrend()
        {
            var (a, r) = TrendPage();
            for (int i = 0; i <= 5; i++)
            {
                _world.Tags.Set("Tank1_Temp", 10.0 * i);
                a.RunFor(1.0);
            }
            Assert.Equal("TrnTemp", S(Call(a, "TrnGetPen", 30, 1, 0).v));
            Assert.Equal("TrnTemp", S(Call(a, "TrnGetPen", 30, 0, 0).v)); // 0 = the focus pen
            var (v, t) = Call(a, "TrnGetPen", 30, 3, 0);
            Assert.Equal("", S(v));
            Assert.Equal(TrendObjects.PenNotFound, t.Error); // an unused pen: 301
            (v, t) = Call(a, "TrnGetPen", 31, 1, 0);
            Assert.Equal(TrendObjects.NotFound, t.Error); // no trend: 300
            (v, t) = Call(a, "TrnGetPen", 0, 1, 0);
            Assert.Equal(Codes.InvalidArg, t.Error); // AN 0: 274
            Assert.Equal(0, t.ErrSetLevel); // the getter's ErrSet(1) undone
            Assert.Equal(1.0, Call(a, "TrnGetPeriod", 30).v.D);
            Assert.Equal(10, Call(a, "TrnGetSpan", 30).v.I);
            // the times: 0 % = the newest sample, 100 % = a span back
            long now = (long)Math.Floor(_world.Clock.Unix());
            Assert.Equal(now, Call(a, "TrnGetTime", 30, 0, 0).v.I);
            Assert.Equal(now - 10, Call(a, "TrnGetTime", 30, 0, 100).v.I);
            Assert.Equal(Codes.InvalidArg, Call(a, "TrnGetTime", 30, 0, 101).t.Error);
            // TrnSetSpan: the period from the span; TrnSetPeriod: the span from the period
            Assert.Equal(0, Call(a, "TrnSetSpan", 30, 20).v.I);
            Assert.Equal(2.0, Call(a, "TrnGetPeriod", 30).v.D);
            Assert.Equal(20, Call(a, "TrnGetSpan", 30).v.I);
            Assert.Equal(0, Call(a, "TrnSetPeriod", 30, 0.5).v.I);
            Assert.Equal(5, Call(a, "TrnGetSpan", 30).v.I);
            Assert.Equal(0, Call(a, "TrnSetPeriod", 30, 0.0).v.I); // back to the pens' period
            Assert.Equal(1.0, Call(a, "TrnGetPeriod", 30).v.D);
            Assert.Equal(Codes.InvalidArg, Call(a, "TrnSetSpan", 30, 0).v.I);
            // historical mode and back
            Assert.Equal(0, Call(a, "TrnGetMode", 30, 1).v.I);
            Assert.Equal(0, Call(a, "TrnSetTime", 30, -1, now - 3).v.I);
            Assert.Equal(1, Call(a, "TrnGetMode", 30, 1).v.I);
            Assert.Equal(now - 3, Call(a, "TrnGetTime", 30, 1, 0).v.I);
            Assert.Equal(0, Call(a, "TrnScroll", 30, 0, -2, 2).v.I); // two samples back
            Assert.Equal(now - 5, Call(a, "TrnGetTime", 30, 1, 0).v.I);
            Assert.Equal(0, Call(a, "TrnSetTime", 30, -1, 0).v.I);
            Assert.Equal(0, Call(a, "TrnGetMode", 30, 1).v.I);
            Assert.Equal(Codes.InvalidArg, Call(a, "TrnScroll", 30, 0, 1, 0).v.I); // a mode without bit 1 or 2
            // scales
            Assert.Equal(75.0, Call(a, "TrnGetScale", 30, 1, 50).v.D);
            Assert.Equal(0, Call(a, "TrnSetScale", 30, 1, 100, 200.0).v.I);
            Assert.Equal(200.0, Call(a, "TrnGetScale", 30, 1, 100).v.D);
            Assert.Equal("200.0", S(Call(a, "TrnGetScaleStr", 30, 1, 100, 0).v));
            Assert.Equal("200.0degC", S(Call(a, "TrnGetScaleStr", 30, 1, 100, 1).v));
            Assert.Equal(0, Call(a, "TrnSetScale", 30, 1, -2, 0.0).v.I); // the default scale
            Assert.Equal(150.0, Call(a, "TrnGetScale", 30, 1, 100).v.D);
            Assert.Equal(Codes.InvalidArg, Call(a, "TrnSetScale", 30, 1, 50, 1.0).v.I);
            // the cursor
            Assert.Equal(-1, Call(a, "TrnGetCursorPos", 30).v.I);
            Assert.Equal(0, Call(a, "TrnSetCursorPos", 30, 0).v.I);
            Assert.Equal(0, Call(a, "TrnGetCursorPos", 30).v.I);
            Assert.Equal(50.0, Call(a, "TrnGetCursorValue", 30, 1).v.D);
            Assert.Equal(" 50.0", S(Call(a, "TrnGetCursorValueStr", 30, 1, 0).v));
            Assert.Equal(now, Call(a, "TrnGetCursorTime", 30, 1).v.I);
            Assert.Equal(0, Call(a, "TrnSetCursorPos", 30, 99).v.I); // past the samples: the middle
            Assert.Equal(5, Call(a, "TrnGetCursorPos", 30).v.I);
            Assert.Equal(0, Call(a, "TrnSetCursor", 30, 2).v.I); // two samples later
            Assert.Equal(3, Call(a, "TrnGetCursorPos", 30).v.I);
            Assert.Equal(30.0, Call(a, "TrnGetCursorValue", 30, 1).v.D);
            Assert.Equal(0, Call(a, "TrnSetCursor", 30, 0).v.I); // off
            Assert.Equal(-1, Call(a, "TrnGetCursorPos", 30).v.I);
            // the cursor off: TrnGetCursorValue answers 0 without an error, the Str form traps 341
            // and answers '' (the runtime skips 0x155, the runtime does not)
            (v, t) = Call(a, "TrnGetCursorValue", 30, 1);
            Assert.Equal(0.0, v.D);
            Assert.Equal(0, t.Error);
            (v, t) = Call(a, "TrnGetCursorValueStr", 30, 1, 0);
            Assert.Equal("", S(v));
            Assert.Equal(TrendObjects.NoCursor, t.Error);
            Assert.Equal(0, t.ErrSetLevel);
            // TrnGetScaleStr: an error answers '<na>' (the 274 too), a call that succeeds clears
            // the task's error first
            (v, t) = Call(a, "TrnGetScaleStr", 30, 1, 101, 0);
            Assert.Equal("<na>", S(v));
            Assert.Equal(Codes.InvalidArg, t.Error);
            (v, t) = Call(a, "TrnGetScaleStr", 31, 1, 0, 0);
            Assert.Equal("<na>", S(v));
            Assert.Equal(TrendObjects.NotFound, t.Error);
            var tc = new CicodeTask(a.Vm, "test", false);
            TaskContext.Set(tc, 0, -1);
            tc.Error = 999;
            var gs = a.Vm.Builtins!.Call(
                Func0Table.Default.Lookup("TrnGetScaleStr")!.Index,
                tc,
                ArgList.Of(Value.Long(30), Value.Long(1), Value.Long(0), Value.Long(0))
            );
            Assert.Equal("  0.0", S(gs));
            Assert.Equal(0, tc.Error);
            Assert.Equal(0, tc.ErrSetLevel);
            // samples by index, newest = 0
            Assert.Equal(50.0, Call(a, "TrnGetBufValue", 30, 1, 0).v.D);
            Assert.Equal(50.0, Call(a, "TrnGetBufValue", 30, 1, 1).v.D); // 50 was set a second ago
            Assert.Equal(40.0, Call(a, "TrnGetBufValue", 30, 1, 2).v.D);
            Assert.Equal(now - 1, Call(a, "TrnGetBufTime", 30, 1, 1).v.I);
            Assert.Equal(Codes.OutOfRange, Call(a, "TrnGetBufValue", 30, 1, 10).t.Error);
            // the focus pen
            Assert.Equal(1, Call(a, "TrnGetPenFocus", 30).v.I);
            Assert.Equal(1, Call(a, "TrnSetPenFocus", 30, 2).v.I); // the old focus
            Assert.Equal(2, Call(a, "TrnGetPenFocus", 30).v.I);
            Assert.Equal("TrnLevel", S(Call(a, "TrnGetPen", 30, 0, 0).v));
            Assert.Equal(2, Call(a, "TrnSetPenFocus", 30, -2).v.I); // the next pen with a tag
            Assert.Equal(1, Call(a, "TrnGetPenFocus", 30).v.I);
            // pen data
            Assert.Equal("degC", S(Call(a, "TrnGetUnits", 30, 1).v));
            Assert.Equal("Tank 1 temperature", S(Call(a, "TrnGetPenComment", 30, 1).v));
            Value w = Ref(Value.Long(0)),
                dd = Ref(Value.Long(0));
            Assert.Equal(0, Call(a, "TrnGetFormat", 30, 2, w, dd).v.I);
            Assert.Equal(7, Held(w).I); // '####.##'
            Assert.Equal(2, Held(dd).I);
            Assert.Equal(2, Call(a, "TrnGetPenNo", 30, "trnlevel").v.I);
            Value err = Ref(Value.Long(-1));
            Assert.Equal("10", S(Call(a, "TrnClientInfo", 30, 0, 1, "", err).v));
            Assert.Equal(0, Held(err).I);
            // TrnSetPen: in background code the request waits one turn; '!' clears
            var (sv, st) = Call(a, "TrnSetPen", 30, 3, "TrnExpr");
            Assert.Equal(0, sv.I);
            Assert.Equal("TrnExpr", S(Call(a, "TrnGetPen", 30, 3, 0).v));
            Assert.Equal(Codes.FgBlock, CallIn(a, true, "TrnSetPen", 30, 3, "TrnTemp").v.I);
            Assert.Equal(0, Call(a, "TrnSetPen", 30, 3, "!").v.I);
            Assert.Equal(PenNotFoundCode, Call(a, "TrnGetPen", 30, 3, 0).t.Error);
            // the state follows the changes at the next scan
            Call(a, "TrnSetPeriod", 30, 2.0);
            a.RunScans(1);
            var tr = r.State.Objects[30].Trend!;
            Assert.Equal(2.0, tr.Period);
            Assert.Equal(20.0, tr.Span);
        }

        private const int PenNotFoundCode = TrendObjects.PenNotFound;

        [Fact]
        public void TrnNewMakesATrendAndTrnDeleteRemovesIt()
        {
            var (a, r) = TrendPage();
            Assert.Equal(
                0,
                Call(a, "TrnNew", 40, "trn001", "TrnLevel", "TrnTemp", "", "", "", "", "", "", "").v.I
            );
            a.RunScans(1);
            var tr = r.State.Objects[40].Trend!;
            Assert.Equal(new[] { "TrnLevel", "TrnTemp" }, tr.Pens.Select(p => p.Tag).ToArray());
            Assert.Equal(PageTrend.DefaultSamples, tr.Samples); // no trend drawn at AN 40
            Assert.Equal(0, Call(a, "TrnDelete", 40).v.I);
            Assert.Equal(TrendObjects.NotFound, Call(a, "TrnDelete", 40).v.I);
            a.RunScans(1);
            Assert.Null(r.State.Objects[40].Trend);
        }

        [Fact]
        public void TrnSelectSetsUpTheTrendOfAPageNotShownYet()
        {
            var a = Make();
            var p0 = new PageFixture("First");
            Assert.Equal(0, a.Open("First", 0, compiled: p0.Build()));
            // select the trend of 'Second' at AN 50 in this window, set its pen, then show the page
            Assert.Equal(0, Call(a, "TrnSelect", -3, "Second", 50, "").v.I);
            Assert.Equal(0, Call(a, "TrnSetPen", -2, 1, "TrnLevel").v.I);
            var p = new PageFixture("Second");
            p.Row("PGTREND", ("AN", 50L), ("PEN1", "TrnTemp"));
            Assert.Equal(0, a.Open("Second", 0, compiled: p.Build()));
            a.RunScans(1);
            // the selected pens replace the page's own (doc 1193865)
            var tr = a.Run(0)!.State.Objects[50].Trend!;
            Assert.Single(tr.Pens);
            Assert.Equal("TrnLevel", tr.Pens[0].Tag);
            Assert.Equal("TrnLevel", S(Call(a, "TrnGetPen", 50, 1, 0).v));
        }

        [Fact]
        public void TrendCallsWithoutATrendOrAWindow()
        {
            var a = Make();
            // window 0 without a page: no trend there (300)
            var (v, t) = Call(a, "TrnGetPen", 30, 1, 0);
            Assert.Equal("", S(v));
            Assert.Equal(TrendObjects.NotFound, t.Error);
            Assert.Equal(TrendObjects.NotFound, Call(a, "TrnSetPeriod", 30, 1.0).v.I);
            // a task of a window that is not open: the window check fails (274)
            (v, t) = CallWin(a, 7, "TrnGetPen", 30, 1, 0);
            Assert.Equal(Codes.InvalidArg, t.Error);
            Assert.Equal(Codes.InvalidArg, CallWin(a, 7, "TrnSetPeriod", 30, 1.0).v.I);
            Assert.Equal(-1, CallWin(a, 7, "TrnGetPenFocus", 30).v.I);
            // AN -2 (TrnSelect's trend) needs the window too (274 first)
            Assert.Equal(0, Call(a, "TrnSelect", -3, "Second", 50, "").v.I);
            Assert.Equal(Codes.InvalidArg, CallWin(a, 7, "TrnSetPen", -2, 1, "TrnLevel").v.I);
            Assert.Equal(Codes.InvalidArg, CallWin(a, 7, "TrnSetPeriod", -2, 1.0).v.I);
            Assert.Equal(Codes.InvalidArg, CallWin(a, 7, "TrnGetCluster", -2).t.Error);
            Assert.Equal(0, Call(a, "TrnSetPen", -2, 1, "TrnLevel").v.I); // with the window: set
        }

        // ================================================================== SPC
        [Fact]
        public void SpcServerSettingsAndChartLimits()
        {
            var a = Make();
            var p = new PageFixture();
            p.Row(
                "PGTREND",
                ("AN", 50L),
                ("PEN1", "Weight_SPC.XUCL"),
                ("PEN2", "Weight_SPC.XLCL"),
                ("PEN3", "Weight_SPC.X")
            );
            Assert.Equal(0, a.Open("TestPage", 0, compiled: p.Build()));
            var r = a.Run(0)!;
            a.RunScans(1);
            var tr = r.State.Objects[50].Trend!;
            Assert.Equal(2.0, tr.Period); // the SPC tag's SAMPLEPER
            Assert.All(tr.Pens, x => Assert.All(x.Values, v => Assert.Null(v))); // the series: not simulated
            // a manual X-bar UCL draws as that constant; automatic again: no samples
            Assert.Equal(0, Call(a, "SPCSetLimit", 50, 1, 455.0, 1).v.I);
            a.RunScans(1);
            tr = r.State.Objects[50].Trend!;
            Assert.All(tr.Pens[0].Values, v => Assert.Equal(455.0, v));
            Assert.All(tr.Pens[1].Values, v => Assert.Null(v));
            Assert.Equal(0, Call(a, "SPCSetLimit", 50, 1, 0.0, 0).v.I);
            a.RunScans(1);
            Assert.All(r.State.Objects[50].Trend!.Pens[0].Values, v => Assert.Null(v));
            Assert.Equal(0, Call(a, "SPCSetLimit", 50, 9, 1.0, 1).v.I); // no pen shows it: nothing
            Assert.Equal(Codes.InvalidArg, Call(a, "SPCSetLimit", 50, 10, 1.0, 1).v.I);
            var (lv, lt) = Call(a, "SPCSetLimit", 51, 1, 1.0, 1);
            Assert.Equal(TrendObjects.NotFound, lv.I); // returned only (trap flag 0)
            Assert.Equal(0, lt.Error);
            // the server's settings: spc.dbf, changed by the Set functions
            Value size = Ref(Value.Long(0));
            Assert.Equal(0, Call(a, "SPCSubgroupSizeGet", "Weight_SPC", size).v.I);
            Assert.Equal(4, Held(size).I);
            Assert.Equal(0, Call(a, "SPCSubgroupSizeSet", "Weight_SPC", 6).v.I);
            Assert.Equal(0, Call(a, "SPCSubgroupSizeGet", "Weight_SPC", size).v.I);
            Assert.Equal(6, Held(size).I);
            Assert.Equal(340, Call(a, "SPCSubgroupSizeSet", "Weight_SPC", 41).v.I);
            var (gv, gt) = CallIn(a, true, "SPCSubgroupSizeGet", "Weight_SPC", size);
            Assert.Equal(Codes.FgBlock, gv.I); // returned, never raised
            Assert.Equal(0, gt.Error);
            Value lsl = Ref(Value.Real(0)),
                usl = Ref(Value.Real(0));
            Assert.Equal(0, Call(a, "SPCSpecLimitGet", "Weight_SPC", lsl, usl).v.I);
            Assert.Equal(390.0, Held(lsl).D);
            Assert.Equal(460.0, Held(usl).D);
            Assert.Equal(Codes.InvalidArg, Call(a, "SPCSpecLimitSet", "Weight_SPC", 10.0, 10.0).v.I);
            Assert.Equal(0, Call(a, "SPCSpecLimitSet", "Weight_SPC", 395.0, 450.0).v.I);
            Assert.Equal(0, Call(a, "SPCSpecLimitGet", "Weight_SPC", lsl, usl).v.I);
            Assert.Equal(395.0, Held(lsl).D);
            Value x = Ref(Value.Real(0)),
                rg = Ref(Value.Real(0)),
                sd = Ref(Value.Real(0));
            Assert.Equal(0, Call(a, "SPCProcessXRSGet", "Weight_SPC", x, rg, sd).v.I);
            Assert.Equal(2, Trends.Validity(Held(x).D)); // not set: the invalid value
            Assert.Equal(12.5, Held(rg).D);
            Assert.Equal(Codes.InvalidArg, Call(a, "SPCProcessXRSSet", "Weight_SPC", 420.0, -1.0, 2.0).v.I);
            Assert.Equal(0, Call(a, "SPCProcessXRSSet", "Weight_SPC", 420.0, 10.0, Trends.Invalid).v.I);
            Assert.Equal(0, Call(a, "SPCProcessXRSGet", "Weight_SPC", x, rg, sd).v.I);
            Assert.Equal(420.0, Held(x).D);
            (gv, gt) = Call(a, "SPCSubgroupSizeGet", "NoSuch_SPC", size);
            Assert.Equal(Codes.NameNotFound, gv.I);
            Assert.Equal(0, gt.Error);
            (gv, gt) = Call(a, "SPCSubgroupSizeGet", "", size);
            Assert.Equal(Codes.NameNotFound, gv.I);
            Assert.Equal(0, gt.Error);
            Assert.Equal(Codes.NameNotFound, Call(a, "SPCSpecLimitGet", "", lsl, usl).t.Error); // the others raise it
            Assert.Equal(Codes.NameNotFound, Call(a, "SPCSpecLimitGet", "", lsl, usl).v.I);
            // the client's cache of a displayed SPC tag
            Assert.Equal(6.0, Call(a, "SPCClientInfo", "Weight_SPC", 1).v.D);
            Assert.Equal(450.0, Call(a, "SPCClientInfo", "Weight_SPC", 7).v.D);
            Assert.Equal(420.0, Call(a, "SPCClientInfo", "Weight_SPC", 3).v.D);
            Assert.Equal(Codes.InvalidArg, Call(a, "SPCClientInfo", "Weight_SPC", 12).t.Error);
            // the SPC series readers: the client's 'no data'
            Assert.Equal(
                Codes.NameNotFound,
                Call(a, "SPCClientTableGet", "Weight_SPC", 1, 0, 10, Value.Long(0), 1).v.I
            );
            Assert.Equal(
                Codes.InvalidArg,
                Call(a, "SPCClientTableGet", "Weight_SPC", 5, 0, 10, Value.Long(0), 1).t.Error
            );
            Assert.Equal(
                Codes.InvalidArg,
                Call(a, "SPCGetHistogramTable", "Weight_SPC", 2, Value.Long(0)).v.I
            );
            Assert.Equal(
                Codes.NameNotFound,
                Call(a, "SPCGetSubgroupTable", "Weight_SPC", 0, Value.Long(0)).v.I
            );
            Assert.Equal(0, Call(a, "SPCAlarms", "Weight_SPC", "XAboveUCL").v.I);
            // not displayed: the client has no data
            Call(a, "TrnDelete", 50);
            Assert.Equal(Codes.NameNotFound, Call(a, "SPCClientInfo", "Weight_SPC", 1).t.Error);
        }

        // ================================================================== alarm lists
        /// <summary>An animator whose library has Cb(INT an): LocInt = an, with a page shown.</summary>
        private Animator AlarmAnimator()
        {
            var lb = new ProgramBuilder();
            lb.Function("Cb", "4", Types.Long)
                .Begin()
                .LoadStack(0)
                .Store("LocInt")
                .ConstInt(0)
                .RetValue()
                .End();
            var a = Make(lb.Build());
            Assert.Equal(0, a.Open("TestPage", 0, compiled: new PageFixture().Build()));
            return a;
        }

        [Fact]
        public void AlarmDspListShowsTheRaisedAlarms()
        {
            var a = AlarmAnimator();
            var r = a.Run(0)!;
            _world.Tags.Set("LocInt", 0);
            Assert.Equal(0, Call(a, "AlarmDsp", 10, 3, 0, "", 0, "Cb").v.I);
            a.RunFor(1.0);
            Assert.Equal(10, (int)_world.Tags.Get("LocInt").I); // the first fill called the callback
            var ls = r.State.Objects[10].AlarmList!;
            Assert.DoesNotContain(ls.Rows, x => x.Tag == "ALM_PUMP");
            int total0 = ls.Total;
            _world.Tags.Set("LocInt", 0);
            _world.Tags.Set("Pump1_Run", 1); // ALM_PUMP (category 1, priority 1) on
            a.RunFor(2.0);
            Assert.Equal(10, (int)_world.Tags.Get("LocInt").I); // the data changed: the callback again
            ls = r.State.Objects[10].AlarmList!;
            Assert.Equal(0, ls.Type);
            Assert.Equal(3, ls.Count);
            Assert.True(ls.Total > total0);
            Assert.Equal(3, ls.Rows.Count);
            int k = ls.Rows.FindIndex(x => x.Tag == "ALM_PUMP");
            Assert.Equal(0, k); // priority first, the newest first
            var row = ls.Rows[k];
            Assert.Equal("unack_on", row.State);
            Assert.True(row.Flash);
            Assert.Equal(1, row.Category);
            // the default format {Time,12} {Tag,10} {Name,20} {Desc,32}
            Assert.Equal(new[] { "Time", "Tag", "Name", "Desc" }, row.Fields.Select(f => f.Key).ToArray());
            Assert.Contains("ALM_PUMP", row.Text);
            Assert.Contains("Pump", row.Text);
            // each row drawn on its AN
            Assert.Equal("ALM_PUMP", r.State.Objects[10].AlarmRow!.Tag);
            Assert.Equal(ls.Rows[1].Tag, r.State.Objects[11].AlarmRow!.Tag);
            Assert.Equal(ls.Rows[2].Tag, r.State.Objects[12].AlarmRow!.Tag);
            var j = r.State.Objects[10].ToJson();
            Assert.Equal(3, j["alarm_list"]["rows"].Count);
            Assert.Equal("ALM_PUMP", j["alarm_row"]["tag"].Str);
            // AlarmGetDsp: the row at an AN; foreground at once, background after a turn
            Assert.Equal("ALM_PUMP", S(CallIn(a, true, "AlarmGetDsp", 10, "Tag").v));
            Assert.Equal("Pump", S(Call(a, "AlarmGetDsp", 10, "Name").v));
            Assert.Equal("1", S(Call(a, "AlarmGetDsp", 10, "Category").v));
            Assert.Equal("1", S(Call(a, "AlarmGetDsp", 10, "SAStateOffset").v));
            Assert.Equal(Codes.InvalidArg, Call(a, "AlarmGetDsp", 50, "Tag").t.Error);
            // AlarmGetInfo / AlarmSetInfo
            Assert.Equal(0, Call(a, "AlarmGetInfo", 10, 3).v.I);
            Assert.Equal(0, Call(a, "AlarmSetInfo", 10, 3, "4").v.I); // every configured alarm
            a.RunScans(1);
            ls = r.State.Objects[10].AlarmList!;
            Assert.Equal(4, ls.Type);
            Assert.Equal(_world.Alarms.ListOf(4).Count, ls.Total);
            Assert.True(ls.Total > 6);
            Assert.Equal(0, Call(a, "AlarmDspNext", 10).v.I);
            Assert.Equal(3, Call(a, "AlarmGetInfo", 10, 1).v.I);
            Assert.Equal(1, Call(a, "AlarmGetInfo", 10, 0).v.I); // the page
            Assert.Equal(1, Call(a, "AlarmGetInfo", 12, 17).v.I); // the page of the row
            Assert.Equal(0, Call(a, "AlarmDspPrev", 10).v.I);
            Assert.Equal(0, Call(a, "AlarmGetInfo", 10, 1).v.I);
            Assert.Equal(569, Call(a, "AlarmSetInfo", 10, 1, "999").v.I); // past the list's alarms
            Assert.Equal(Codes.InvalidArg, Call(a, "AlarmSetInfo", 10, 11, "1").v.I);
            Assert.Equal(Codes.InvalidArg, Call(a, "AlarmDspNext", 99).v.I);
            Assert.Equal(0, Call(a, "AlarmSetInfo", 10, 3, "0").v.I);
            // AlarmAck of the row at its AN
            a.RunScans(1);
            Assert.Equal(0, Call(a, "AlarmAck", 0, 10, "").v.I);
            a.RunScans(1);
            ls = r.State.Objects[10].AlarmList!;
            Assert.Equal("ack_on", ls.Rows.First(x => x.Tag == "ALM_PUMP").State);
            Assert.Equal(Codes.InvalidArg, Call(a, "AlarmAck", 9, 10, "").v.I);
        }

        [Fact]
        public void AlarmDspCalledEveryScanKeepsItsSettings()
        {
            var a = Make();
            var p = new PageFixture();
            // a Cicode object showing the list every scan (Example's hardware page does: AN 21)
            p.Row(
                "PGADV",
                ("AN", 21L),
                (
                    "CMD",
                    p.Code(c =>
                        c.ConstInt(21)
                            .Convert(4)
                            .ConstInt(2)
                            .Convert(4)
                            .ConstInt(4)
                            .Convert(4)
                            .ConstStr("")
                            .ConstInt(0)
                            .Convert(4)
                            .ConstStr("")
                            .Func("AlarmDsp", 6)
                            .Op("Pop")
                    )
                )
            );
            Assert.Equal(0, a.Open("TestPage", 0, compiled: p.Build()));
            var r = a.Run(0)!;
            a.RunScans(1);
            Assert.Equal(4, r.State.Objects[21].AlarmList!.Type);
            Assert.Equal(0, Call(a, "AlarmDspNext", 21).v.I);
            Assert.Equal(0, Call(a, "AlarmSetInfo", 21, 7, "2").v.I); // priority 2 only
            a.RunScans(2);
            var ls = r.State.Objects[21].AlarmList!;
            Assert.Equal(2, Call(a, "AlarmGetInfo", 21, 7).v.I);
            Assert.NotEmpty(ls.Rows);
            Assert.All(ls.Rows, x => Assert.Equal(2, x.Priority));
            Assert.Equal(_world.Alarms.ListOf(4).Count(x => _world.Alarms.Priority(x.a) == 2), ls.Total);
        }

        [Fact]
        public void HardwareAlarmListShowsTheHardwareAlarms()
        {
            var a = AlarmAnimator();
            var r = a.Run(0)!;
            Assert.Equal(0, Call(a, "AlarmDsp", 30, 4, 5, "", 0, "").v.I);
            a.RunScans(1);
            int before = r.State.Objects[30].AlarmList!.Total;
            _world.Alarms.RaiseHardware(275, "Divide by zero", "Cicode task", "TestPage");
            a.RunScans(1);
            var ls = r.State.Objects[30].AlarmList!;
            Assert.Equal(before + 1, ls.Total);
            Assert.Contains(ls.Rows, x => x.Tag == "Cicode task" && x.Category == 255);
            Assert.Equal("Divide by zero", S(Call(a, "AlarmGetDsp", 30, "Desc").v));
        }

        [Fact]
        public void RowsAtTheSameTimeKeepADefinedOrder()
        {
            var a = AlarmAnimator();
            var r = a.Run(0)!;
            Assert.Equal(0, Call(a, "AlarmDsp", 30, 20, 5, "", 0, "").v.I);
            // twenty hardware alarms raised at the same simulated time: newest first means the
            // one raised later first (List.Sort alone leaves equal rows in an order that is not
            // the same on net48 and net10.0)
            for (int i = 0; i < 20; i++)
                _world.Alarms.RaiseHardware(
                    275,
                    "Divide by zero",
                    "Task" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture),
                    "TestPage"
                );
            a.RunScans(1);
            var ls = r.State.Objects[30].AlarmList!;
            var tags = ls
                .Rows.Select(x => x.Tag)
                .Where(x => x.StartsWith("Task", StringComparison.Ordinal))
                .ToArray();
            Assert.Equal(
                Enumerable
                    .Range(0, 20)
                    .Reverse()
                    .Select(i => "Task" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture))
                    .ToArray(),
                tags
            );
            // the trend expression names fold as the engine compares names, non-ASCII letters too
            Assert.Equal(NameHashes.Hash("PUMPE_Ø"), NameHashes.Hash("pumpe_ø"));
            Assert.Equal(NameHashes.Hash("Tank1_Temp"), NameHashes.Hash("TANK1_TEMP"));
        }

        [Fact]
        public void AlarmListCreateFillsTheArrayOfTheAn()
        {
            var a = AlarmAnimator();
            var r = a.Run(0)!;
            _world.Tags.Set("Pump1_Run", 1);
            _world.Tags.Set("Tank1_Temp", 130.0);
            a.RunFor(4.0);
            Assert.Equal(
                0,
                Call(a, "AlarmListCreate", 20, 0, 300, 100, 20, 1, "", "{Tag,10}{Name,20}", -1, -1).v.I
            );
            a.RunScans(1);
            var ls = r.State.Objects[20].AlarmList!;
            Assert.True(ls.Array);
            Assert.Equal(4, ls.Count); // 100 / 20 = 5 rows, one the header
            Assert.Equal(new[] { "Tag", "Name" }, ls.Columns.Select(c => c.Key).ToArray());
            Assert.True(ls.Total >= 2);
            Assert.Null(r.State.Objects[20].AlarmRow); // an array list draws no rows itself
            // the state's JSON: ctgedit's ObjectState names (snake_case), nested ones too
            var lj = r.State.Objects[20].ToJson()["alarm_list"];
            Assert.True(lj["no_draw"].Bool);
            Assert.False(lj.Has("noDraw"));
            Assert.True(lj["rows"].Items[0].Has("font_handle"));
            // the array bound to the AN: the header row 0, the rows from 1
            var tb = PageObjectTables.Of(_world.Display);
            int hd = tb.AlarmList(0, "TestPage", 20, exact: true)!.ArrayHandle;
            var arr = ProcessData.Of(a.Host!).ArrayTable.Get(hd)!;
            Assert.Equal(3, arr.X);
            Assert.Equal(5, arr.Y);
            Assert.Equal(16, arr.Z);
            string Cell(int x, int y, int z) =>
                arr.Items.TryGetValue((y * arr.X + x) * arr.Z + z, out var v) ? v : "";
            Assert.Equal("Tag", Cell(1, 0, 1));
            Assert.Equal("10", Cell(1, 0, 3));
            Assert.Equal("Name", Cell(2, 0, 1));
            Assert.Equal("30", Cell(0, 0, 11));
            Assert.Equal(ls.Rows[0].Tag, Cell(1, 1, 1));
            Assert.Equal(
                ls.Total.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Cell(0, 0, 12)
            );
            // the SA rows: AlarmGetDsp(AN + row - 1), the record id in column 0 z 2
            Assert.Equal(ls.Rows[0].Tag, S(Call(a, "AlarmGetDsp", 20, "Tag").v));
            Assert.Equal(S(Call(a, "AlarmGetDsp", 20, "RecordId").v), Cell(0, 1, 2));
            Assert.Equal(ls.Rows[1].Tag, S(Call(a, "AlarmGetDsp", 21, "Tag").v));
            // a filter edited through the AN
            long fs = Call(a, "AlarmFilterEditOpen", "20", 0).v.I;
            Assert.True(fs >= 0);
            Assert.Equal(0, Call(a, "AlarmFilterEditSet", fs, "Tag=ALM_TEMP").v.I);
            Assert.Equal(0, Call(a, "AlarmFilterEditCommit", fs).v.I);
            Assert.Equal(0, Call(a, "AlarmListDisplay", 20).v.I);
            Assert.Equal("ALM_TEMP", Cell(1, 1, 1));
            Assert.Equal("", Cell(1, 2, 1));
            a.RunScans(1);
            ls = r.State.Objects[20].AlarmList!;
            Assert.Single(ls.Rows);
            Assert.Equal("ALM_TEMP", ls.Rows[0].Tag);
            Assert.Equal(0, Call(a, "AlarmListDestroy", 20).v.I);
            Assert.Equal(Codes.InvalidArg, Call(a, "AlarmListDestroy", 20).v.I);
            Assert.Null(ProcessData.Of(a.Host!).ArrayTable.Get(hd)); // the array freed
            a.RunScans(1);
            Assert.Null(r.State.Objects[20].AlarmList);
        }

        [Fact]
        public void AlarmListCreateArraysGoWithTheirPage()
        {
            var a = AlarmAnimator();
            var arrays = ProcessData.Of(a.Host!).ArrayTable;
            int before = arrays.Count;
            // the page shown again and again (SA pages make their list in the page's entry code):
            // each close frees the list's array, so the arrays do not pile up
            for (int i = 0; i < 5; i++)
            {
                Assert.Equal(
                    0,
                    Call(a, "AlarmListCreate", 20, 0, 300, 100, 20, 1, "", "{Tag,10}{Name,20}", -1, -1).v.I
                );
                Assert.Equal(
                    0,
                    Call(a, "AlarmListCreate", 20, 0, 300, 100, 20, 1, "", "{Tag,10}", -1, -1).v.I
                ); // again: replaced
                Assert.Equal(before + 1, arrays.Count);
                a.Close(0);
                Assert.Equal(before, arrays.Count);
                Assert.Equal(0, a.Open("TestPage", 0, compiled: new PageFixture().Build()));
            }
            // an array the code destroyed itself: its handle, reused by another array, is not freed
            Assert.Equal(0, Call(a, "AlarmListCreate", 20, 0, 300, 100, 20, 1, "", "{Tag,10}", -1, -1).v.I);
            int hd = PageObjectTables
                .Of(_world.Display)
                .AlarmList(0, "TestPage", 20, exact: true)!
                .ArrayHandle;
            Assert.Equal(0, Call(a, "ArrayDestroy", hd).v.I);
            long other = Call(a, "ArrayCreate", "", 2, 2, 1).v.I;
            Assert.Equal(hd, other); // the handle reused
            Assert.Equal(0, Call(a, "AlarmListFill", 20).v.I); // writes nothing into it
            Assert.Empty(arrays.Get(other)!.Items);
            a.Close(0);
            Assert.NotNull(arrays.Get(other)); // and the close leaves it
        }

        [Fact]
        public void AlarmListCreateArrayIsTheArrayOfItsAn()
        {
            // the list's array is bound to (AN, window) as ArrayCreateByAn binds one
            // (DisplayAns.BindArray), so the Array*ByAn functions SA_Controls AlarmList.ci calls
            // on the list's AN find it
            var a = AlarmAnimator();
            var arrays = ProcessData.Of(a.Host!).ArrayTable;
            int before = arrays.Count;
            Assert.Equal(-1, Call(a, "ArrayGetArrayByAn", 20).v.I);
            // an array ArrayCreateByAn made at the AN goes when the list is made there (guess)
            long own = Call(a, "ArrayCreateByAn", 20, 2, 2, 1).v.I;
            Assert.True(own >= 0);
            Assert.Equal(own, Call(a, "ArrayGetArrayByAn", 20).v.I);
            Assert.Equal(
                0,
                Call(a, "AlarmListCreate", 20, 0, 300, 100, 20, 1, "", "{Tag,10}{Name,20}", -1, -1).v.I
            );
            int hd = PageObjectTables
                .Of(_world.Display)
                .AlarmList(0, "TestPage", 20, exact: true)!
                .ArrayHandle;
            Assert.Equal(hd, Call(a, "ArrayGetArrayByAn", 20).v.I);
            Assert.Equal(1, Call(a, "ArrayExistsByAn", 20).v.I);
            Assert.Equal(before + 1, arrays.Count);
            var (map, mt) = Call(a, "ArrayGetMapNameByAn", 20);
            Assert.Equal(0, mt.Error);
            Assert.Equal(S(Call(a, "ArrayGetMapName", hd).v), S(map));
            Assert.Equal("Tag", S(Call(a, "ArrayGetStringByAn", 20, 1, 0, 1).v)); // row 0: the columns
            Assert.Equal(1, Call(a, "ArrayGetIntByAn", 20, 0, 0, 7).v.I); // the header flag
            Assert.Equal(0, Call(a, "ArrayExistsByAn", 21).v.I); // a row AN has none
            // the list again at the AN: one array still, the new one
            Assert.Equal(0, Call(a, "AlarmListCreate", 20, 0, 300, 100, 20, 0, "", "{Tag,10}", -1, -1).v.I);
            int hd2 = PageObjectTables
                .Of(_world.Display)
                .AlarmList(0, "TestPage", 20, exact: true)!
                .ArrayHandle;
            Assert.Equal(hd2, Call(a, "ArrayGetArrayByAn", 20).v.I);
            Assert.Equal(0, Call(a, "ArrayGetIntByAn", 20, 0, 0, 7).v.I);
            Assert.Equal(before + 1, arrays.Count);
            // ArrayDestroyByAn ends it; the list then writes into no array
            Assert.Equal(0, Call(a, "ArrayDestroyByAn", 20).v.I);
            Assert.Equal(-1, Call(a, "ArrayGetArrayByAn", 20).v.I);
            Assert.Equal(before, arrays.Count);
            Assert.Equal(0, Call(a, "AlarmListFill", 20).v.I);
            // a list made again, then the page closed: freed by the page close once
            Assert.Equal(0, Call(a, "AlarmListCreate", 20, 0, 300, 100, 20, 1, "", "{Tag,10}", -1, -1).v.I);
            Assert.Equal(before + 1, arrays.Count);
            a.Close(0);
            Assert.Equal(before, arrays.Count);
            Assert.Equal(0, a.Open("TestPage", 0, compiled: new PageFixture().Build()));
            Assert.Equal(-1, Call(a, "ArrayGetArrayByAn", 20).v.I);
        }

        [Fact]
        public void SequenceOfEventsListsTheTransitions()
        {
            var a = AlarmAnimator();
            var r = a.Run(0)!;
            Assert.Equal(0, Call(a, "AlarmDsp", 40, 20, 16, "", 0, "").v.I);
            _world.Tags.Set("Pump1_Run", 1);
            a.RunFor(2.0);
            _world.Alarms.Acknowledge("ALM_PUMP");
            a.RunFor(1.0);
            var ls = r.State.Objects[40].AlarmList!;
            Assert.Equal(16, ls.Type);
            Assert.True(ls.Rows.Count >= 2);
            Assert.Equal("ALM_PUMP", ls.Rows[0].Tag); // newest first: the ack
            Assert.Equal("ack", ls.Rows[0].State);
            Assert.Contains(ls.Rows, x => x.Tag == "ALM_PUMP" && x.State == "on");
            Assert.Equal("ACK", S(Call(a, "AlarmGetDsp", 40, "LogState").v));
            Assert.Equal("Operator", S(Call(a, "AlarmGetDsp", 40, "Classification").v));
        }

        // ================================================================== alarm list layout, trend history, drawn buttons
        [Fact]
        public void AlarmListWithoutAHeaderFillsFromRowOneAndTellsItsLayout()
        {
            // ArrayView.ci: row 0 holds the columns with a header or without one; the data rows
            // start at row 1
            var a = AlarmAnimator();
            var r = a.Run(0)!;
            _world.Tags.Set("Pump1_Run", 1);
            _world.Tags.Set("Tank1_Temp", 130.0);
            a.RunFor(4.0);
            Assert.Equal(
                0,
                Call(
                    a,
                    "AlarmListCreate",
                    20,
                    0,
                    300,
                    100,
                    20,
                    0,
                    "",
                    "{Tag,10}{Comment,20}{OnDate,8}",
                    -1,
                    -1
                ).v.I
            );
            a.RunScans(1);
            var ls = r.State.Objects[20].AlarmList!;
            Assert.Equal(5, ls.Count); // 100 / 20, no header row
            int hd = PageObjectTables
                .Of(_world.Display)
                .AlarmList(0, "TestPage", 20, exact: true)!
                .ArrayHandle;
            var arr = ProcessData.Of(a.Host!).ArrayTable.Get(hd)!;
            Assert.Equal(6, arr.Y); // one row more without a header
            string Cell(int x, int y, int z) =>
                arr.Items.TryGetValue((y * arr.X + x) * arr.Z + z, out var v) ? v : "";
            Assert.Equal("Tag", Cell(1, 0, 1)); // the columns stay
            Assert.Equal("Comment", Cell(2, 0, 1));
            Assert.Equal(ls.Rows[0].Tag, Cell(1, 1, 1)); // the first row at row 1
            // the column titles (Localization.ci's strings) and the list's layout in its state
            Assert.Equal("@(Tag)", Cell(1, 0, 4));
            Assert.Equal("@(User Comment)", Cell(2, 0, 4));
            Assert.Equal("@(On Date)", Cell(3, 0, 4));
            Assert.False(ls.DrawHeader);
            Assert.Equal((20, 300, 100), (ls.RowHeight, ls.Width, ls.Height));
            Assert.Equal(new[] { "@(Tag)", "@(User Comment)", "@(On Date)" }, ls.Titles.ToArray());
            // a title the code cleared (SA AlarmPage: _ArrayView_ClearColumnTitle) stays cleared
            Assert.Equal(0, Call(a, "ArraySetStringByAn", 20, " ", 1, 0, 4).v.I);
            a.RunScans(1);
            ls = r.State.Objects[20].AlarmList!;
            Assert.Equal(" ", ls.Titles[0]);
            var lj = r.State.Objects[20].ToJson()["alarm_list"];
            Assert.False(lj["drawHeader"].Bool);
            Assert.Equal(20, lj["rowHeight"].Long);
            Assert.Equal(300, lj["width"].Long);
            Assert.Equal(100, lj["height"].Long);
            Assert.Equal("@(User Comment)", lj["titles"].Items[1].Str);
            // a localised state (a viewer's) gives the titles' text
            var vj = r.State.Objects[20].ToJson(new DisplayJson(_world.Display))["alarm_list"];
            Assert.Equal("User Comment", vj["titles"].Items[1].Str);
        }

        [Fact]
        public void SeededTrendHistoryFillsTheSpanBeforeTheStart()
        {
            var (a, r) = TrendPage(samples: 100);
            _world.Tags.Set("Tank1_Temp", 42.0);
            a.RunFor(3.0);
            var p1 = r.State.Objects[30].Trend!.Pens[0];
            Assert.Null(p1.Values[0]); // before the start: no samples
            Assert.NotNull(p1.Values[99]);
            TrendTags.Of(_world).SeedHistory = true;
            a.RunScans(1);
            p1 = r.State.Objects[30].Trend!.Pens[0];
            Assert.All(p1.Values, v => Assert.NotNull(v)); // the whole span
            Assert.Equal(p1.Values[99], 42.0);
            TrendTags.Of(_world).SeedHistory = false;
            a.RunScans(1);
            Assert.Null(r.State.Objects[30].Trend!.Pens[0].Values[0]);
        }

        [Fact]
        public void ButtonsCicodeDrewRunTheirFunctionsWhenPressed()
        {
            // Include page.ci: DspButtonFn(hAn, _PageMenuFn, ...); the FUNCTION arguments are the
            // functions' names; a press runs the down function, the release the up function, each
            // with the command cursor on the button (_PageMenuFn reads KeyGetCursor())
            var lb = new ProgramBuilder();
            lb.Function("BtnDn", "", Types.Long)
                .Begin()
                .ConstInt(1)
                .Store("LocInt")
                .ConstInt(0)
                .RetValue()
                .End();
            lb.Function("BtnUp", "", Types.Long)
                .Begin()
                .ConstInt(7)
                .Store("LocInt")
                .ConstInt(0)
                .RetValue()
                .End();
            var a = Make(lb.Build());
            Assert.Equal(0, a.Open("TestPage", 0, compiled: new PageFixture().Build()));
            var run = a.Run(0)!;
            _world.Tags.Set("LocInt", 0);
            Assert.Equal(0, Call(a, "_DspButtonFn", 40, "BtnUp", "OK", -1, 60, 20, "BtnDn", "", 0).v.I);
            var b = Animator.DrawnButton(run, 40)!;
            Assert.Equal("BtnUp", b.Args.Find(x => x.Key == "upFunction").Value.Str);
            Assert.Equal("BtnDn", b.Args.Find(x => x.Key == "downFunction").Value.Str);
            // the caption the menu's _PageMenuFn reads back (DspAnInfo(KeyGetCursor(), 2))
            Assert.Equal("OK", S(Call(a, "DspAnInfo", 40, 2).v));
            Assert.Equal("", S(Call(a, "DspAnInfo", 40, 1).v));
            Assert.True(a.PressDrawnButton(0, 40, Animator.TouchKind.Down));
            a.RunFor(0.5);
            Assert.Equal(1, (int)_world.Tags.Get("LocInt").I);
            Assert.Equal(40, run.Window.CommandCursor);
            Assert.True(a.PressDrawnButton(0, 40, Animator.TouchKind.Up));
            a.RunFor(0.5);
            Assert.Equal(7, (int)_world.Tags.Get("LocInt").I);
            Assert.False(a.PressDrawnButton(0, 41, Animator.TouchKind.Up)); // no button there
            Assert.Null(Animator.DrawnButton(run, 41));
            // a viewer's double click (down, up, dblclick, up) presses the button twice: the
            // dblclick is a press only, its release comes as an up of its own
            Assert.Equal(Animator.TouchKind.Down, CicodeSim.Sim.SimPages.DrawnButtonKind("dblclick"));
            Assert.Equal(Animator.TouchKind.Down, CicodeSim.Sim.SimPages.DrawnButtonKind("down"));
            Assert.Equal(Animator.TouchKind.Up, CicodeSim.Sim.SimPages.DrawnButtonKind("up"));
            Assert.Equal(Animator.TouchKind.DoubleClick, CicodeSim.Sim.SimPages.DrawnButtonKind("click"));
            _world.Tags.Set("LocInt", 0);
            Assert.True(a.PressDrawnButton(0, 40, CicodeSim.Sim.SimPages.DrawnButtonKind("dblclick")));
            a.RunFor(0.5);
            Assert.Equal(1, (int)_world.Tags.Get("LocInt").I); // the down function only
        }
    }
}
