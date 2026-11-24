using System;
using System.IO;
using System.Linq;
using CicodeSim.Builtins;
using CicodeSim.Page;
using CicodeSim.Tests.Vm;
using CicodeSim.World;
using Xunit;

namespace CicodeSim.Tests.Page
{
    /// <summary>Trend objects and alarm lists on compiled pages.</summary>
    [Collection("World")]
    public class CorpusPageObjectTests
    {
        private static readonly string ExSA = Path.Combine(Corpus.User, "ExampleSA");
        private static readonly string Ex = Path.Combine(Corpus.User, "Example");

        [CorpusFact]
        public void MetersTrendShowsAPenRamp()
        {
            var a = Animator.ForProject(ExSA);
            var tags = a.World.Tags;
            const string tag = "TopMilk_TS01_PV";
            Assert.NotNull(tags.Find(tag));
            Assert.Equal(0, a.Open("Meters", 0));
            var r = a.Run(0)!;
            // the four PGTREND objects of the page (their SA genies give the pens at run time)
            foreach (int an in new[] { 664, 1092, 1378, 1385 })
                Assert.NotNull(r.State.Objects[an].Trend);
            Assert.Equal(0, PageObjectTests.Call(a, "TrnSetPen", 1385, 1, tag).v.I);
            double t0 = a.World.Clock.Now();
            for (int i = 0; i <= 15; i++)
            {
                tags.Set(tag, 5.0 * i);
                a.RunFor(1.0);
            }
            var tr = r.State.Objects[1385].Trend!;
            Assert.Equal(1.0, tr.Period); // SAMPLEPER 00:00:01
            var p = Assert.Single(tr.Pens);
            Assert.Equal(tag, p.Tag);
            // the samples follow the ramp (the value set at second i after t0: 5 i), one a second
            // back from the newest; none before the ramp started
            int n = p.Values.Length;
            Assert.Equal(100, n);
            for (int k = 0; k < 20; k++)
            {
                double at = p.End - k;
                double? want = at < t0 ? (double?)null : Math.Min(75.0, 5.0 * Math.Floor(at - t0 + 1e-9));
                if (want != null)
                    Assert.Equal(want, p.Values[n - 1 - k]);
            }
            Assert.Equal(75.0, p.Values[n - 1]);
            Assert.Equal(tag, PageObjectTests.S(PageObjectTests.Call(a, "TrnGetPen", 1385, 1, 0).v));
            Assert.Equal(75.0, PageObjectTests.Call(a, "TrnGetBufValue", 1385, 1, 0).v.D);
            Assert.DoesNotContain(a.Logs, l => l.StartsWith("engine exception", StringComparison.Ordinal));
        }

        [CorpusFact]
        public void SpcChartsShowTheirPens()
        {
            if (!File.Exists(Path.Combine(Ex, "_LIBRARY.RDB")))
                return;
            var a = Animator.ForProject(Ex);
            Assert.Equal(0, a.Open("SPCXRSChart", 0));
            a.RunScans(2);
            var s = a.Run(0)!.State.Objects;
            var x = s[50].Trend!;
            Assert.Equal(
                new[] { "Weight_SPC.XUCL", "Weight_SPC.XLCL", "Weight_SPC.XCL", "Weight_SPC.X" },
                x.Pens.Select(p => p.Tag).ToArray()
            );
            Assert.Equal("Weight_SPC.R", s[51].Trend!.Pens[3].Tag);
            Assert.Equal("Weight_SPC.S", s[52].Trend!.Pens[3].Tag);
            // a sample of an SPC chart is a subgroup: the SPC tag's SAMPLEPER x its subgroup size
            var w = TrendTags.Of(a.World).Find("Weight_SPC")!;
            Assert.Equal(w.SamplePeriod * SpcSeries.Size(a.World, w), x.Period, 9);
            // the series over the seeded history: the newest X, the UCL at or above the centre
            // line, the LCL at or below it (a constant history has no range: the three meet)
            double? Newest(int pen) => x.Pens[pen].Values[x.Pens[pen].Values.Length - 1];
            Assert.True(Newest(3) != null && Newest(0) >= Newest(2) && Newest(1) <= Newest(2));
        }

        [CorpusFact]
        public void ExpressionTrendIsSampledFromTheCompiledTrend()
        {
            if (!File.Exists(Path.Combine(Ex, "_LIBRARY.RDB")))
                return;
            var a = Animator.ForProject(Ex);
            Assert.Equal(0, a.Open("Hardware", 0));
            // FIC_100 = CPU_Usage + Rand(5), SAMPLEPER 0.1 s (Example trend.dbf)
            Assert.Equal(
                0,
                PageObjectTests.Call(a, "TrnNew", 600, "trend", "FIC_100", "", "", "", "", "", "", "", "").v.I
            );
            a.RunFor(3.0);
            var tr = a.Run(0)!.State.Objects[600].Trend!;
            Assert.Equal(0.1, tr.Period, 9);
            var vals = tr.Pens[0].Values;
            int known = vals.Count(v => v != null);
            Assert.InRange(known, 25, 32);
            double cpu = a.World.Tags.Get("CPU_Usage").D;
            Assert.All(vals.Where(v => v != null), v => Assert.InRange(v!.Value, cpu, cpu + 5));
            Assert.True(TrendTags.Of(a.World).IsSampled("FIC_100"));
            // the change log keeps only the trend tags, not every tag
            var hist = TagHistory.Attach(a.World);
            Assert.True(
                hist.WatchedCount <= 2 * TrendTags.Of(a.World).Count + 8,
                hist.WatchedCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
            );
        }

        [CorpusFact]
        public void HardwareAlarmPageListsHardwareAlarms()
        {
            if (!File.Exists(Path.Combine(Ex, "_LIBRARY.RDB")))
                return;
            var a = Animator.ForProject(Ex);
            Assert.Equal(0, a.Open("Hardware", 0));
            var r = a.Run(0)!;
            a.RunScans(2);
            // AN 21: AlarmDsp(21, 24, 5, "", 0, "") every scan (the page's Cicode object)
            var ls = r.State.Objects[21].AlarmList!;
            Assert.Equal(5, ls.Type);
            Assert.Equal(24, ls.Count);
            int before = ls.Total;
            a.World.Alarms.RaiseHardware(275, "Divide by zero", "Cicode task", "Hardware");
            a.RunScans(2);
            ls = r.State.Objects[21].AlarmList!;
            Assert.Equal(before + 1, ls.Total);
            var row = ls.Rows.First(x => x.Tag == "Cicode task");
            Assert.Equal("unack_on", row.State);
            Assert.Contains("Divide by zero", row.Text);
            Assert.NotNull(r.State.Objects[21 + ls.Rows.IndexOf(row)].AlarmRow);
        }

        /// <summary>SA alarm page rows come through the list AN's array.</summary>
        [CorpusFact]
        public void SaAlarmPageRowsComeThroughTheArrayOfTheListAn()
        {
            const string page = "DefaultAlarm_HD1080";
            const int an = 55;
            var a = Animator.ForProject(ExSA);
            // the page's drawing as far as the page code needs it: the list genie's name (ctgedit's
            // page model of the page: AN 55 'AlarmList', a top-level group)
            var list = Draw.Obj(an, 0x08, new CicodeSim.World.Display.Rect(411, 69, 524, 182));
            list.Name = "AlarmList";
            // AG01_Fault: a digital alarm on TopMilk_AG01_Fault (ExampleSA digalm.dbf)
            Assert.NotNull(a.World.Alarms.Get("AG01_Fault"));
            a.World.Tags.Set("TopMilk_AG01_Fault", 1);
            int unrelated = (int)PageObjectTests.Call(a, "ArrayCreate", "merge-e unrelated", 1, 1, 1).v.I;
            Assert.True(unrelated >= 0);
            Assert.Equal(0, a.Open(page, 0, Draw.Model(list)));
            a.RunFor(2.0);
            Assert.DoesNotContain(a.Logs, l => l.StartsWith("engine exception", StringComparison.Ordinal));

            // the list and its array, bound to (AN 55, window 0) in the array record
            var tl = CicodeSim
                .World.Display.PageObjectTables.Of(a.Display)
                .AlarmList(0, page, an, exact: true);
            Assert.NotNull(tl);
            Assert.True(tl!.ArrayList);
            int hd = (int)PageObjectTests.Call(a, "ArrayGetArrayByAn", an).v.I;
            Assert.Equal(tl.ArrayHandle, hd);
            Assert.Equal(1, PageObjectTests.Call(a, "ArrayExistsByAn", an).v.I);
            var (mapName, mt) = PageObjectTests.Call(a, "ArrayGetMapNameByAn", an);
            Assert.Equal(0, mt.Error);
            Assert.StartsWith(".Map", PageObjectTests.S(mapName));
            Assert.Single(ProcessData.Of(a.Host!).ArrayTable.Live(), x =>
                x.Item2.An == an && x.Item2.Window == 0
            );

            // the list's rows: the alarm, in the alarm_list state and in the array (row 1 = the
            // first data row under the header; column 0 z 2 the record id, the cells z 1)
            var st = a.Run(0)!.State.Objects;
            var ls = st[an].AlarmList!;
            int k = ls.Rows.FindIndex(x => x.Tag == "AG01_Fault");
            Assert.True(k >= 0, string.Join(",", ls.Rows.Select(x => x.Tag)));
            // the list has no format: the SA default of an active list (no Tag column; the live
            // DefaultAlarm_HD1080 shows PriorityAndState, On Date, On Time, Name ...)
            Assert.Equal("PriorityAndState", ls.Columns[0].Key);
            int nameCol = 1 + ls.Columns.FindIndex(c => c.Key == "Name");
            Assert.True(nameCol >= 1);
            Assert.Equal(
                a.World.Alarms.Get("AG01_Fault")!.Name,
                PageObjectTests.S(PageObjectTests.Call(a, "ArrayGetStringByAn", an, nameCol, k + 1, 1).v)
            );
            Assert.NotEqual(
                "",
                PageObjectTests.S(PageObjectTests.Call(a, "ArrayGetStringByAn", an, 0, k + 1, 2).v)
            );

            // the genie's row objects read the array: the header row (AN 164, row 0) hides, the
            // first data row (AN 166, row 1) shows, sized by _ArrayView_Width / _RowHeight; its
            // COL_ARR _ArrayView_Row_ColorIndex gives an odd row index 1 (the colour itself is
            // the drawing's array entry, not in this drawing)
            Assert.False(st[164].Shown);
            Assert.True(st[166].Shown);
            Assert.NotNull(st[166].Scale);
            Assert.Equal(1, Lib(a, "_ArrayView_Row_ColorIndex", an, 0, 1).Result!.Value.I);

            // the SA row click on the alarm's row selects it; the selection reads it back
            var click = Lib(a, "_AlarmList_Row_LeftClick", an, 0, k + 1);
            Assert.Equal(CicodeSim.Vm.TaskState.Stop, click.State);
            Assert.Equal(
                "AG01_Fault",
                PageObjectTests.S(Lib(a, "AlarmList_Selected_AlarmTagName", an).Result!.Value)
            );

            // the page close: EXIT_FUNC AlarmPage_Exit destroys the list first
            // (AlarmListDestroy(DspGetAnFromName("AlarmList")) frees its array), so when the
            // display's PageClosing runs the list and its array are gone; PageClosing then frees
            // the arrays still bound to the window (AlarmPage_Init's ArrayCreateByAn(2, 5, 1000)
            // of the equipment references, freed by nothing else); nothing else goes
            int refs = (int)PageObjectTests.Call(a, "ArrayGetArrayByAn", 2).v.I;
            Assert.True(refs >= 0 && refs != hd);
            bool? listAtClosing = null,
                arrayAtClosing = null,
                refsAtClosing = null;
            void Closing(CicodeSim.World.Display.Window w, CicodeSim.World.Display.PageInstance p)
            {
                // DisplayAns' handler subscribed first, so the bound arrays are freed by now
                listAtClosing =
                    CicodeSim.World.Display.PageObjectTables.Of(a.Display).AlarmList(0, page, an, exact: true)
                    != null;
                arrayAtClosing = ProcessData.Of(a.Host!).ArrayTable.Get(hd) != null;
                refsAtClosing = ProcessData.Of(a.Host!).ArrayTable.Get(refs) != null;
            }
            a.Display.PageClosing += Closing;
            Assert.Equal(0, a.Close(0));
            a.Display.PageClosing -= Closing;
            Assert.False(listAtClosing);
            Assert.False(arrayAtClosing);
            Assert.False(refsAtClosing);
            Assert.Equal(0, PageObjectTests.Call(a, "ArrayExists", hd).v.I);
            Assert.Equal(0, PageObjectTests.Call(a, "ArrayExists", refs).v.I);
            Assert.Equal(1, PageObjectTests.Call(a, "ArrayExists", unrelated).v.I);
            Assert.Empty(CicodeSim.World.Display.PageObjectTables.Of(a.Display).AlarmListsOf(0, page));
            Assert.DoesNotContain(ProcessData.Of(a.Host!).ArrayTable.Live(), x => x.Item2.An == an);
            // shown again: a new list whose array the AN names
            Assert.Equal(0, a.Open(page, 0, Draw.Model(list)));
            a.RunFor(2.0);
            var tl2 = CicodeSim
                .World.Display.PageObjectTables.Of(a.Display)
                .AlarmList(0, page, an, exact: true);
            Assert.NotNull(tl2);
            Assert.Equal(tl2!.ArrayHandle, (int)PageObjectTests.Call(a, "ArrayGetArrayByAn", an).v.I);
            Assert.Single(ProcessData.Of(a.Host!).ArrayTable.Live(), x =>
                x.Item2.An == an && x.Item2.Window == 0
            );
        }

        /// <summary>The SA trend tag comes from the item filter.</summary>
        [CorpusFact]
        public void SaTrendTagComesFromTheItemFilter()
        {
            var a = Animator.ForProject(ExSA);
            var pv = Lib(a, "_SATrend_GetTrendTag", "Cluster1", "Company.Library.Meter", "PV");
            Assert.Equal(CicodeSim.Vm.TaskState.Stop, pv.State);
            Assert.Equal("SA_Meters_PV", PageObjectTests.S(pv.Result!.Value));
            Assert.Equal(
                "SA_Meters_OP",
                PageObjectTests.S(
                    Lib(a, "_SATrend_GetTrendTag", "Cluster1", "Company.Library.Meter", "OP").Result!.Value
                )
            );
            Assert.Equal(
                "",
                PageObjectTests.S(
                    Lib(
                        a,
                        "_SATrend_GetTrendTag",
                        "Cluster1",
                        "Company.Library.Meter",
                        "NoItem"
                    ).Result!.Value
                )
            );
        }

        /// <summary>A library function run to its end as a task of window 0 (background: the
        /// blocking AlarmGetDsp refuses foreground code).</summary>
        private static CicodeSim.Vm.CicodeTask Lib(Animator a, string fn, params object[] args)
        {
            var t = a.Vm.TaskForCall(fn, args.Select(x => CicodeSim.Vm.CicodeVm.ValueOf(x)).ToArray(), false);
            CicodeSim.World.Display.TaskContext.Set(t, 0, -1);
            a.Vm.Run(t);
            return t;
        }
    }
}
