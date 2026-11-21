using System;
using System.IO;
using System.Linq;
using CicodeSim.Page;
using CicodeSim.Tests.Vm;
using Xunit;

namespace CicodeSim.Tests.Page
{
    /// <summary>The recorded live page cases (conformance/pages, ctgedit; read here) run
    /// from their frames' snapshots: what each capture shows and the animator can tell without a
    /// renderer (texts, quality indication, rotation), checked frame by frame.</summary>
    [Collection("World")]
    public class LiveCaseTests
    {
        private static readonly string ExSA = Path.Combine(Corpus.User, "ExampleSA");

        private static string? CaseDir(string page, string name)
        {
            string? d = AppContext.BaseDirectory;
            while (d != null && !File.Exists(Path.Combine(d, "LICENSE")))
                d = Path.GetDirectoryName(d);
            if (d == null)
                return null;
            string c = Path.Combine(d, "conformance", "pages", "ExampleSA", page, name);
            return File.Exists(Path.Combine(c, "case.json")) ? c : null;
        }

        private static LiveCase.Frame Frame(string page, string name, string frame) =>
            LiveCase.RunAll(ExSA, CaseDir(page, name)!, frame).Single();

        [CorpusFact(Needs = "conformance/pages/ExampleSA")]
        public void MixingTankLevelTextsAndTheBadFrame()
        {
            if (CaseDir("Mixing_L3", "tank_level") == null)
                return;
            // live: f1_level_120.png shows '120.0' over 'L', TS03 '2.5' 'DegC', FLW01 '0.0' 'L/s'
            var s = Frame("Mixing_L3", "tank_level", "f1_level_120").State.Objects;
            Assert.Equal("120.0", s[435].Text);
            Assert.Equal("L", s[434].Text);
            Assert.Equal("DegC", s[555].Text);
            Assert.Equal("0.0", s[905].Text);
            Assert.Equal("L/s", s[904].Text);
            s = Frame("Mixing_L3", "tank_level", "f2_level_1350").State.Objects;
            Assert.Equal("1350.0", s[435].Text);
            // live: f3_level_bad.png shows '#BAD' for the value and for its units
            s = Frame("Mixing_L3", "tank_level", "f3_level_bad").State.Objects;
            Assert.Equal("#BAD", s[435].Text);
            Assert.Equal("#BAD", s[434].Text);
            Assert.Equal("DegC", s[555].Text);
        }

        [CorpusFact(Needs = "conformance/pages/ExampleSA")]
        public void MetersBadFrameShowsThirtyFiveBadTexts()
        {
            if (CaseDir("Meters", "meter") == null)
                return;
            // live: f3_pv_bad.png shows 35 '#BAD' texts (value + units on every meter, one
            // orientation without units); f1_pv_15.png shows the PV '15.0' 18 times
            var bad = Frame("Meters", "meter", "f3_pv_bad").State.Objects.Values.Count(o => o.Text == "#BAD");
            Assert.Equal(35, bad);
            var pv = Frame("Meters", "meter", "f1_pv_15").State.Objects.Values.Count(o => o.Text == "15.0");
            Assert.Equal(18, pv);
        }

        [CorpusFact(Needs = "conformance/pages/ExampleSA")]
        public void OthersCompassNeedleFollowsWindPV()
        {
            if (CaseDir("Others", "wind_compass") == null)
                return;
            double Rot(string frame) =>
                Frame("Others", "wind_compass", frame).State.Objects[106].Rotate!.Value;
            Assert.Equal(30.0, Rot("f0_baseline"), 9);
            Assert.Equal(90.0, Rot("f1_wind_90"), 9);
            Assert.Equal(225.0, Rot("f2_wind_225"), 9);
            // live: f3_windpv_bad.png hatches the needle (bad quality on WindPV)
            var f3 = Frame("Others", "wind_compass", "f3_windpv_bad").State.Objects[106];
            Assert.Equal("bad", f3.Indication);
        }

        [CorpusFact(Needs = "conformance/pages/ExampleSA")]
        public void PumpFaceplateShowsItsEquipmentItemsThroughTheAssociation()
        {
            string? dir = CaseDir("FP_PMP_HD1080", "pump05");
            if (dir == null)
                return;
            // live: f0_baseline.png shows Temp '0.00' and TRK '0.00' (the PV / PVTrack items of
            // the __EquipmentRef equipment through '?__EquipmentRef?.PV'; FORMAT-less tags keep
            // the association's 0x206) and 'DegC' (AssInfo)
            var s = Frame("FP_PMP_HD1080", "pump05", "f0_baseline").State.Objects;
            Assert.Equal("  0.00", s[49].Text);
            Assert.Equal("  0.00", s[96].Text);
            Assert.Equal("DegC", s[230].Text);
            Assert.Equal(" 53.00", Frame("FP_PMP_HD1080", "pump05", "f4_running_bad").State.Objects[49].Text);
            // the meter limits come through the SA PageObject framework (AssResolve = AssInfo 19):
            // live 'HH 85.0', 'H 75.0', 'EL 0.0', 'EH 150.0' once the SA library's startup had
            // read its PLC limit parameters and the page had run a while
            var f = LiveCase
                .RunAll(ExSA, dir, "f0_baseline", settle: 30, startup: new[] { "_SA_Library_Startup" })
                .Single()
                .State.Objects;
            Assert.Equal("  85.0", f[164].Text);
            Assert.Equal("  75.0", f[168].Text);
            Assert.Equal("   0.0", f[184].Text);
            Assert.Equal(" 150.0", f[188].Text);
            Assert.DoesNotContain(f.Values, x => x.Text == "#BAD" || x.Text == "#ASC");
        }

        [CorpusFact(Needs = "conformance/pages/ExampleSA")]
        public void WindFaceplateValuesShowWithTheAssociationsFormat()
        {
            if (CaseDir("FP_Wind_HD1080", "faceplate") == null)
                return;
            // live: f0_baseline.png '50.00', '45.00', '50.00', '20.00', '50.00', WindPV '30.00',
            // WindPVAvg '15.00'; f3_speed_bad.png '#BAD' for WindSpeed, '300.00', '280.00'
            var s = Frame("FP_Wind_HD1080", "faceplate", "f0_baseline").State.Objects;
            var want = new[]
            {
                (11, " 50.00"),
                (31, " 45.00"),
                (47, " 50.00"),
                (62, " 20.00"),
                (77, " 50.00"),
                (118, " 30.00"),
                (134, " 15.00"),
            };
            foreach (var (an, text) in want)
                Assert.Equal(text, s[an].Text);
            var f3 = Frame("FP_Wind_HD1080", "faceplate", "f3_speed_bad").State.Objects;
            Assert.Equal("#BAD", f3[11].Text);
            Assert.Equal("300.00", f3[118].Text);
            Assert.Equal("280.00", f3[134].Text);
        }

        [CorpusFact(Needs = "conformance/pages/ExampleSA")]
        public void OverviewAlarmFlagsAndModeTabsFollowTheRecordedState()
        {
            if (CaseDir("Overview_L2", "sequence") == null)
                return;
            // ctsim-pagecase/1 (2026-10-02): the alarm server kept OFF unacknowledged alarms from
            // earlier runs, the scheduler has Company.TopMilk.Mixing Active (DEFSTATE Off). live:
            // f0_baseline.png shows 'H' flags (priority 2) at Milk Supply, Ingredients, AG01, AG02,
            // the AG05 row and CNV01, 'E' (priority 1) over TS01..TS03, pale UNACKOFF frames
            // (0xFFFFBF, 0xFFBFBF), none at FLW01 / FLW02, AG03, AG04, CNV02; the ACTIVE tab on
            var fr = Frame("Overview_L2", "sequence", "f0_baseline");
            Assert.Equal(134, fr.AlarmsSet);
            Assert.Empty(fr.LiveProblems);
            var s = fr.State.Objects;
            long P(int an) => s[an].Alarm!.Priority;
            // Milk Supply / Ingredients: no alarm of their own, those of the equipment they
            // reference (eqref.dbf; the indicator's INCLUDE = IncludingReference)
            Assert.Equal(2, P(320));
            Assert.Equal(2, P(773));
            foreach (int an in new[] { 821, 917, 991 })
                Assert.Equal(1, P(an));
            foreach (int an in new[] { 1213, 1228, 1232, 1267, 282, 295, 374 })
                Assert.Equal(2, P(an));
            foreach (int an in new[] { 67, 106, 1217, 1221, 1268 })
                Assert.Equal(0, P(an));
            Assert.Equal("unack_off", s[320].Alarm!.State);
            Assert.Equal(0xFFFFBF, s[320].Alarm!.Colour);
            Assert.Equal(0xFFBFBF, s[821].Alarm!.Colour);
            // the frame of an OFF unacknowledged alarm is steady (UNACKOFFFG = UNACKOFFBG in
            // ExampleSA; flashes a pair whose colours differ): f0_baseline.png and
            // its _b capture show the same pale pixels; what comes and goes there is the
            // priority flag above the frame
            Assert.False(s[821].Alarm!.Flash);
            Assert.False(s[320].Alarm!.Flash);
            // the mode tabs: COL_A = EquipGetProperty("Company.TopMilk.Mixing", "State", 1) = ...
            Assert.NotNull(s[1120].FillColour); // Active
            Assert.False(s.ContainsKey(1121) && s[1121].FillColour != null); // OFF
        }

        [CorpusFact(Needs = "conformance/pages/ExampleSA")]
        public void PumpFaceplateIndicatorShowsThePumpsAlarms()
        {
            if (CaseDir("FP_PMP_HD1080", "pump05") == null)
                return;
            // live: the ?__EquipmentRef? indicator (AN 388) frames PMP05 in pale yellow (its
            // OFF unacknowledged priority 2 alarm)
            foreach (var f in new[] { "f0_baseline", "f4_running_bad" })
            {
                var st = Frame("FP_PMP_HD1080", "pump05", f).State.Objects[388].Alarm!;
                Assert.Equal((2L, "unack_off"), (st.Priority, st.State));
            }
        }

        [CorpusFact(Needs = "conformance/pages/ExampleSA")]
        public void MeterIndicatorFlashesOnlyWhileItsAlarmIsOn()
        {
            if (CaseDir("Meters", "meter") == null)
                return;
            // live: AN 99 frames the meter in a steady pale cyan (P3 OFF unacknowledged) at 15 %;
            // at 85 % its alarm is ON and the frame alternates UNACKONFG 0x00FFFF with the white
            // UNACKONBG (f2_pv_85.png cyan, f2_pv_85_b.png none)
            var f1 = Frame("Meters", "meter", "f1_pv_15").State.Objects[99].Alarm!;
            Assert.Equal(
                (3L, "unack_off", 0xBFFFFFL, false),
                (f1.Priority, f1.State, f1.Colour!.Value, f1.Flash)
            );
            var fr2 = Frame("Meters", "meter", "f2_pv_85").State;
            var f2 = fr2.Objects[99].Alarm!;
            Assert.Equal(
                (3L, "unack_on", 0x00FFFFL, 0xFFFFFFL, true),
                (f2.Priority, f2.State, f2.Fg!.Value, f2.Bg!.Value, f2.Flash)
            );
            // the frame colour of the phase (PageRun.Emit): cyan in the on phase, white in the off
            Assert.Equal(fr2.FlashOn ? 0x00FFFFL : 0xFFFFFFL, f2.Colour!.Value);
            Assert.Equal(0xFFFFFFL, f2.InPhase(false).Colour!.Value);
            Assert.Equal(0x00FFFFL, f2.InPhase(true).Colour!.Value);
        }

        /// <summary>The pages over a library compiled from source (compile all) give the
        /// states they give over CtCmp32's _LIBRARY.RDB: page code calls the library by name.</summary>
        [CorpusFact(Needs = "conformance/pages/ExampleSA")]
        public void TheCasesGiveTheSameStatesOverCodeCompiledFromSource()
        {
            foreach (var (page, name) in new[] { ("Mixing_L3", "tank_level"), ("Overview_L2", "sequence") })
            {
                string? dir = CaseDir(page, name);
                if (dir == null)
                    continue;
                string States(string compile)
                {
                    var notes = new System.Collections.Generic.List<string>();
                    var sb = new System.Text.StringBuilder();
                    foreach (var fr in LiveCase.RunAll(ExSA, dir, null, compile, notes))
                    {
                        Assert.Equal(0, fr.OpenError);
                        foreach (var s in fr.State.Objects.Values.OrderBy(o => o.An))
                            sb.Append(fr.Name).Append(' ').Append(s.ToCompactJson().Dump()).Append('\n');
                        fr.Animator.Dispose();
                    }
                    if (compile == CicodeSim.Compiler.Codegen.LiveProgram.All)
                        Assert.Contains(notes, n =>
                            n.StartsWith("every function from source", StringComparison.Ordinal)
                        );
                    return sb.ToString();
                }
                string rdb = States(CicodeSim.Compiler.Codegen.LiveProgram.None);
                Assert.True(rdb.Length > 1000, page + " " + name);
                Assert.Equal(rdb, States(CicodeSim.Compiler.Codegen.LiveProgram.All));
            }
        }
    }
}
