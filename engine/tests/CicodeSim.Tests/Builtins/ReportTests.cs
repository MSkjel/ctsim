using System;
using System.IO;
using System.Linq;
using System.Text;
using CicodeSim.Builtins;
using CicodeSim.Tests.Host;
using CicodeSim.Vm;
using Xunit;

namespace CicodeSim.Tests.Builtins
{
    /// <summary>The report server (Builtins.Reports) on the synthetic World: schedules,
    /// triggers, Report(), format files written to their devices in the sandbox.</summary>
    [Collection("World")]
    public sealed class ReportTests : DataTestBase
    {
        private static readonly string[] ReportFields =
        {
            "NAME",
            "TIME",
            "PERIOD",
            "TRIG",
            "REPORT",
            "DEVICE",
            "CLUSTER",
        };

        private void Text(string name, string text) =>
            File.WriteAllBytes(Path.Combine(MainDir, name), Encoding.ASCII.GetBytes(text));

        /// <summary>The synthetic World with a report server, the Print labels, the devices
        /// and <paramref name="reports"/> (rows of <see cref="ReportFields"/>).</summary>
        private Sim MakeReports(params string[][] reports) =>
            MakeData(() =>
            {
                Dbf(Path.Combine(MainDir, "repsrv.dbf"), new[] { "NAME" }, new[] { "ReportServer1" });
                Dbf(
                    Path.Combine(MainDir, "labels.dbf"),
                    new[] { "NAME", "EXPR" },
                    new[] { "Print(sText,nMode=0)", "DevPrint(DevCurr(),sText,nMode)" },
                    new[] { "PrintLn(sText)", "DevPrint(DevCurr(),sText,1)" }
                );
                Dbf(
                    Path.Combine(MainDir, "devices.dbf"),
                    new[] { "NAME", "FORMAT", "HEADER", "FILENAME", "TYPE", "FILES" },
                    new[] { "RepDev", "{TEXT,40}", "", "[RUN]:shift.txt", "ASCII_DEV", "1" },
                    new[] { "RtfDev", "", "", "[RUN]:rtf.txt", "ASCII_DEV", "1" },
                    new[] { "TrigDev", "", "", "[RUN]:trig.txt", "ASCII_DEV", "1" },
                    new[] { "VarDev", "", "", "[RUN]:vars.txt", "ASCII_DEV", "1" }
                );
                Dbf(Path.Combine(MainDir, "reports.dbf"), ReportFields, reports);
                Text(
                    "shift.rpt",
                    "SHIFT REPORT\r\n"
                        + "{Motor_Speed:###.#} {Name_Str      }|\r\n"
                        + "{1+2}|\r\n"
                        + "{! a comment line}\r\n"
                        + "{CICODE}\r\n"
                        + "PrintLn(\"Lines: \" + IntToStr(3));\r\n"
                        + "Print(\"x\")\r\n"
                        + "{END}\r\n"
                        + "End{FF}\r\n"
                );
                Text(
                    "inside.rpt",
                    "dev={CICODE}Print(DevInfo(DevCurr(), 0)){END};state={CICODE}Print(RepGetControl(\"Inside\", 0)){END}"
                        + ";cluster={CICODE}Print(RepGetCluster()){END};font={CICODE}Print(PrintFont(\"Big\")){END}"
                );
                Text(
                    "rep.rtf",
                    "{\\rtf1\\ansi {\\b Title}\\par \\{Motor_Speed:##.#\\} \\{Name_Str\\}\\par \\{CICODE\\}\\par Print(\"z\")\\par \\{END\\}\\par}"
                );
                Text("trig.rpt", "on\r\n");
                Text(
                    "vars.rpt",
                    "{CICODE}\r\n"
                        + "INT nCount = 5;\r\n"
                        + "STRING sName;\r\n"
                        + "nCount = nCount + 1;\r\n"
                        + "{END}\r\n"
                        + "first={nCount:###}\r\n"
                        + "{CICODE}\r\n"
                        + "nCount = nCount * 10;\r\n"
                        + "sName = \"abc\";\r\n"
                        + "{END}\r\n"
                        + "second={nCount:###} {sName   }|\r\n"
                );
            });

        private static string Out(Sim s, string file)
        {
            string path = s.Host.Sandbox.Full(Path.Combine(s.W.Unit.RootDir, file));
            byte[]? b = s.Host.Sandbox.ReadAll(path);
            return b == null ? "<none>" : Encoding.ASCII.GetString(b);
        }

        /// <summary>Advance the clock in half seconds, running the tasks that become ready.</summary>
        private static void RunFor(Sim s, double seconds)
        {
            s.Run.Scheduler.Run();
            for (double t = 0; t < seconds; t += 0.5)
            {
                s.W.Clock.Advance(0.5);
                s.Run.Scheduler.Run();
            }
        }

        private static Reports.Rep Rep(Sim s, string name) =>
            Reports.Reps(s.Host).First(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

        [Fact]
        public void AScheduledReportWritesItsFormatFileWithItsFieldsFilled()
        {
            var s = MakeReports(new[] { "Shift", "", "00:00:30", "", "shift", "RepDev", "" });
            s.W.Tags.Set("Motor_Speed", (object)7.5);
            s.W.Tags.Set("Name_Str", (object)"abc");
            Reports.Start(s.Host, s.Run.Vm);
            // (the world starts at 08:00:00: the first run at 08:00:30)
            Assert.Equal(1790841630.0, Rep(s, "Shift").Next);
            RunFor(s, 29);
            Assert.Equal("<none>", Out(s, "shift.txt"));
            RunFor(s, 2);
            Assert.Equal(1, Rep(s, "Shift").Runs);
            const string first = "SHIFT REPORT\r\n  7.5 abc           |\r\n3  |\r\nLines: 3\r\nxEnd\f\r\n";
            Assert.Equal(first, Out(s, "shift.txt"));
            // the next run 30 s on: the file of the first becomes the history file (TxtRollOver 1)
            s.W.Tags.Set("Motor_Speed", (object)12.5);
            RunFor(s, 30);
            Assert.Equal(2, Rep(s, "Shift").Runs);
            Assert.StartsWith("SHIFT REPORT\r\n 12.5 abc", Out(s, "shift.txt"));
            Assert.Equal(first, Out(s, "shift.001"));
            Assert.Contains(s.Log, l => l == "report Shift runs (schedule)");
        }

        [Fact]
        public void AnRtfReportKeepsItsMarkupAndEscapesItsBraces()
        {
            var s = MakeReports(new[] { "Rtf", "", "", "", "rep.rtf", "RtfDev", "" });
            s.W.Tags.Set("Motor_Speed", (object)7.5);
            s.W.Tags.Set("Name_Str", (object)"a{b}");
            var r = Call(s, "Report", "Rtf");
            Assert.True(r.Blocked);
            Assert.Equal(0, r.I);
            RunFor(s, 1);
            Assert.Equal("{\\rtf1\\ansi {\\b Title}\\par  7.5 a\\{b\\}    \\par z\\par}", Out(s, "rtf.txt"));
            // RtfRollOver 0: a second run adds to the file
            Assert.Equal(0, Call(s, "Report", "Rtf").I);
            RunFor(s, 1);
            Assert.Equal(2, Rep(s, "Rtf").Runs);
            Assert.EndsWith(
                "\\par}{\\rtf1\\ansi {\\b Title}\\par  7.5 a\\{b\\}    \\par z\\par}",
                Out(s, "rtf.txt")
            );
        }

        [Fact]
        public void AnAsciiReportsFirstBlockDeclaresTheReportsVariables()
        {
            var s = MakeReports(new[] { "Vars", "", "", "", "vars", "VarDev", "" });
            const string output = "first=  6\r\nsecond= 60 abc     |\r\n";
            Assert.Equal(0, Call(s, "Report", "Vars").I);
            RunFor(s, 1);
            Assert.Equal(output, Out(s, "vars.txt"));
            // a second run starts with the declared values again
            Assert.Equal(0, Call(s, "Report", "Vars").I);
            RunFor(s, 1);
            Assert.Equal(2, Rep(s, "Vars").Runs);
            Assert.Equal(output, Out(s, "vars.txt"));
            Assert.DoesNotContain(s.Log, l => l.Contains("does not compile") || l.Contains("did not end"));
        }

        [Fact]
        public void ReportCodeLogsToTheReportsDevice()
        {
            var s = MakeReports(new[] { "Inside", "", "", "", "inside.rpt", "TrigDev", "" });
            Assert.Equal(-1, Call(s, "DevCurr").I);
            Assert.Equal("", Call(s, "RepGetCluster").S);
            Assert.Equal(269, Call(s, "PrintFont", "Big").Error);
            Assert.Equal(0, Call(s, "Report", "Inside").I);
            // a second request while it runs: busy
            Assert.Equal(278, Call(s, "Report", "Inside").I);
            RunFor(s, 1);
            string cluster = CicodeSim.Builtins.Servers.Clusters(s.Host).FirstOrDefault() ?? "";
            Assert.Equal("dev=TrigDev;state=3;cluster=" + cluster + ";font=0", Out(s, "trig.txt"));
            Assert.Equal(0, Call(s, "RepGetControl", "Inside", 0).I);
        }

        [Fact]
        public void ReportRefusesWhatItCannotRun()
        {
            var s = MakeReports(new[] { "Shift", "", "", "", "shift", "RepDev", "" });
            Assert.Equal(280, Call(s, "Report", "NoSuchReport").I);
            Assert.Equal(282, CallFg(s, "Report", "Shift").I);
            var g = Call(s, "RepGetControl", "NoSuchReport", 0);
            Assert.Equal(-1, g.I);
            Assert.Equal(280, g.Error);
            Assert.Equal(280, Call(s, "RepSetControl", "NoSuchReport", 5, 1).I);
            Assert.Equal(274, Call(s, "RepSetControl", "Shift", 9, 1).I);
        }

        [Fact]
        public void WithoutAReportServerNoReportRuns()
        {
            var s = MakeData();
            Assert.Equal(281, Call(s, "Report", "Shift").I);
        }

        [Fact]
        public void RepGetControlAndRepSetControlReadAndChangeTheSchedule()
        {
            var s = MakeReports(
                new[] { "Hourly", "06:00:00", "01:00:00", "", "shift", "RepDev", "" },
                new[] { "Weekly", "", "Monday", "", "shift", "RepDev", "" },
                new[] { "Monthly", "07:30:00", "15th", "", "shift", "RepDev", "" },
                new[] { "Yearly", "", "25th June", "", "shift", "RepDev", "" },
                new[] { "OnTrig", "", "", "Valve_A", "shift", "RepDev", "" }
            );
            long Get(string name, int type) => Call(s, "RepGetControl", name, type).I;
            Assert.Equal(0, Get("Hourly", 0));
            Assert.Equal(1, Get("Hourly", 4));
            Assert.Equal(3600, Get("Hourly", 2));
            Assert.Equal(6 * 3600, Get("Hourly", 3));
            Assert.Equal(0, Get("Hourly", 5));
            Assert.Equal(2, Get("Weekly", 4));
            Assert.Equal(1, Get("Weekly", 2));
            Assert.Equal(3, Get("Monthly", 4));
            Assert.Equal(15, Get("Monthly", 2));
            Assert.Equal(4, Get("Yearly", 4));
            Assert.Equal(31 + 28 + 31 + 30 + 31 + 25, Get("Yearly", 2));
            Assert.Equal(0, Get("OnTrig", 4));

            // the next runs once the server runs (2026-10-01, a Thursday, 08:00:00 UTC)
            Reports.Start(s.Host, s.Run.Vm);
            Assert.Equal(1790845200, Get("Hourly", 1)); // 09:00:00
            Assert.Equal(1791158400, Get("Weekly", 1)); // Monday 5 October 00:00:00
            Assert.Equal(1792049400, Get("Monthly", 1)); // 15 October 07:30:00
            Assert.Equal(1813881600, Get("Yearly", 1)); // 25 June 2027 00:00:00
            Assert.Equal(0, Get("OnTrig", 1));

            // every 8 hours from 07:00: the next at 15:00
            Assert.Equal(0, Call(s, "RepSetControl", "Weekly", 5, 1).I);
            Assert.Equal(1, Get("Weekly", 5));
            Assert.Equal(0, Call(s, "RepSetControl", "Weekly", 4, 1).I);
            Assert.Equal(0, Call(s, "RepSetControl", "Weekly", 3, 7 * 3600).I);
            Assert.Equal(0, Call(s, "RepSetControl", "Weekly", 2, 8 * 3600).I);
            Assert.Equal(0, Call(s, "RepSetControl", "Weekly", 5, 0).I);
            Assert.Equal(0, Get("Weekly", 5));
            Assert.Equal(1, Get("Weekly", 4));
            Assert.Equal(1790866800, Get("Weekly", 1)); // 15:00:00

            // a one-time run of the event report in 10 s
            Assert.Equal(0, Call(s, "RepSetControl", "OnTrig", 1, 1790841610).I);
            RunFor(s, 11);
            Assert.Equal(1, Rep(s, "OnTrig").Runs);
            Assert.Equal(0, Get("OnTrig", 1));
        }

        [Fact]
        public void ATriggerReportRunsOnEachRisingEdge()
        {
            var s = MakeReports(new[] { "OnValve", "", "", "Valve_A", "trig", "TrigDev", "" });
            s.W.Tags.Set("Valve_A", (object)1);
            Reports.Start(s.Host, s.Run.Vm);
            // [Report]InhibitEvent 1: a trigger that is on at the first look does not run it
            RunFor(s, 3);
            Assert.Equal(0, Rep(s, "OnValve").Runs);
            s.W.Tags.Set("Valve_A", (object)0);
            RunFor(s, 2);
            s.W.Tags.Set("Valve_A", (object)1);
            RunFor(s, 2);
            Assert.Equal(1, Rep(s, "OnValve").Runs);
            Assert.Equal("on\r\n", Out(s, "trig.txt"));
            // staying on runs it no more; off and on again runs it again
            RunFor(s, 3);
            Assert.Equal(1, Rep(s, "OnValve").Runs);
            s.W.Tags.Set("Valve_A", (object)0);
            RunFor(s, 2);
            s.W.Tags.Set("Valve_A", (object)1);
            RunFor(s, 2);
            Assert.Equal(2, Rep(s, "OnValve").Runs);
        }

        /// <summary>The Example project's two reports, every 30 s: example.rtf to replog
        /// ([Report]:replog.txt) and report.htm to Html_Report ([Report]:Report.htm), [Path]Report
        /// being [DATA].</summary>
        [ProjectFact]
        public void TheExampleProjectsReportsRun()
        {
            CicodeSim.World.World.ClearCaches();
            var run = CicodeSim.World.WorldRun.ForProject(SimCorpus.Example, start: "2026-10-01T08:00:00Z");
            var w = run.World;
            var h = WorldBuiltins.HostOf(run.Vm.Builtins)!;
            h.TimeZone = TimeZoneInfo.Utc;
            var log = new System.Collections.Generic.List<string>();
            h.Log = log.Add;
            Reports.Start(h, run.Vm);
            run.Scheduler.Run();
            for (int i = 0; i < 64; i++)
            {
                w.Clock.Advance(0.5);
                run.Scheduler.Run();
            }
            Assert.Equal(1, Reports.Reps(h).First(r => r.Name == "Example").Runs);
            Assert.Equal(1, Reports.Reps(h).First(r => r.Name == "Html_Report").Runs);
            string data = CicodeSim.Project.CitectIni.IniValue("CtEdit", "Data", "");
            string Read(string f) =>
                Encoding.ASCII.GetString(
                    h.Sandbox.ReadAll(h.Sandbox.Full(Path.Combine(data, f))) ?? new byte[0]
                );
            string rtf = Read("replog.txt");
            Assert.StartsWith("{\\rtf1", rtf);
            Assert.Contains("\\tab\\tab\\tab\\ul Example Report\\cf1\\par", rtf);
            Assert.DoesNotContain("\\{", rtf);
            // a value after a control word: a space ends the word (\cf4 0.0, not \cf40.0)
            Assert.Matches(@"\\cf4 -?[0-9]+\.[0-9]", rtf);
            Assert.DoesNotMatch(@"\\(cf|f|fs)[0-9]+[0-9]\.", rtf);
            Assert.Matches(@"\\fs24 \d+[/.-]\d+[/.-]\d+ +\d+:\d+", rtf);
            string html = Read("Report.htm");
            Assert.StartsWith("<HTML>", html);
            Assert.Contains("<TITLE>Example Report</TITLE>", html);
            Assert.DoesNotContain("{", html);
            // {DATE()  ...  } at its width, then {TIME(1)  ...  }
            Assert.Matches(@"\d+[/.-]\d+[/.-]\d+ {30,}\d+:\d+", html);
            Assert.DoesNotContain("CICODE", html);
            Assert.DoesNotContain(log, l => l.Contains("does not compile") || l.Contains("did not end"));
        }
    }
}
