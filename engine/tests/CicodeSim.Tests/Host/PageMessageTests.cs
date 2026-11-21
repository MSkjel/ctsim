using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CicodeSim.Host;
using CicodeSim.Sim;
using Xunit;
using Xunit.Abstractions;

namespace CicodeSim.Tests.Host
{
    /// <summary>Pages Cicode displays that are not compiled, and the log's plain-word texts for a
    /// failed page change and a hardware error (R2 Example, read only).</summary>
    [Collection("ctsim server")]
    public sealed class PageMessageTests
    {
        private readonly ITestOutputHelper _out;

        public PageMessageTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static CallOutcome Run(Simulation sim, string text)
        {
            CallOutcome? o = null;
            var done = new ManualResetEventSlim(false);
            sim.Invoke(() => sim.StartCall(text, r =>
                {
                    o = r;
                    done.Set();
                }));
            Assert.True(done.Wait(30000), text);
            return o!;
        }

        /// <summary>A page in the project (pages.DBF + its pg rows) with no compiled .RDB runs
        /// from its rows when Cicode displays it, and the log says so; a page that is nowhere is
        /// reported by name and project.</summary>
        [ProjectFact]
        public void AnUncompiledPageRunsFromItsRows()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            {
                var logs = new List<LogEntry>();
                sim.Log += e =>
                {
                    lock (logs)
                        logs.Add(e);
                };
                sim.Invoke(() => sim.Pages.EnsureAnimator());
                sim.Pages.HideRdb = p => string.Equals(p, "Dynamic_1", StringComparison.OrdinalIgnoreCase);
                Assert.Null(sim.Pages.RdbOf("Dynamic_1"));
                var r = Run(sim, "PageDisplay(\"Dynamic_1\")");
                Assert.Equal("stop", r.State);
                Assert.Equal(
                    "Dynamic_1",
                    sim.Invoke(() => sim.World.Display.Windows[0].Page?.Name),
                    StringComparer.OrdinalIgnoreCase
                );
                SimTestUtil.Until(
                    sim,
                    () =>
                    {
                        lock (logs)
                            return logs.Any(e => e.Text.Contains("runs from its project rows"));
                    },
                    what: "the 'runs from its project rows' line"
                );

                Run(sim, "PageDisplay(\"NoSuchPage_xyz\")");
                SimTestUtil.Until(
                    sim,
                    () =>
                    {
                        lock (logs)
                            return logs.Any(e => e.Text.Contains("NoSuchPage_xyz"));
                    },
                    what: "the missing page line"
                );
                string line;
                lock (logs)
                    line = logs.First(e => e.Text.Contains("NoSuchPage_xyz")).Text;
                _out.WriteLine(line);
                Assert.Contains(
                    "there is no page 'NoSuchPage_xyz' in Example or the projects it includes",
                    line
                );
            }
        }

        /// <summary>A hardware error's log line carries the Cicode call stack (innermost first,
        /// with files and lines) and its plain-word message, also as JSON.</summary>
        [ProjectFact]
        public void AnErrorCarriesItsCallStack()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            {
                var logs = new List<LogEntry>();
                sim.Log += e =>
                {
                    lock (logs)
                        logs.Add(e);
                };
                Run(sim, "GetTagVal(\"NoSuchTagXyz\")");
                SimTestUtil.Until(
                    sim,
                    () =>
                    {
                        lock (logs)
                            return logs.Any(e => e.Kind == "hardware" && e.Code == 424);
                    },
                    what: "the hardware error line"
                );
                LogEntry h;
                lock (logs)
                    h = logs.First(e => e.Kind == "hardware" && e.Code == 424);
                _out.WriteLine(h.ToJson().ToString());
                Assert.NotEmpty(h.Stack);
                Assert.Equal("GetTagVal", h.Stack[0].Function);
                Assert.EndsWith("ddewrite.ci", h.Stack[0].File, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(23, h.Stack[0].Line);
                // the failing call with the values it got, the frame's arguments
                Assert.StartsWith("TagRead(\"NoSuchTagXyz\"", h.Call);
                Assert.Contains(("sTag", "\"NoSuchTagXyz\""), h.Stack[0].Args);
                var j = h.ToJson();
                Assert.Null(j.Get("message"));
                Assert.Equal("\"NoSuchTagXyz\"", j["stack"].Items[0]["args"]["sTag"].Str);
                Assert.Equal("GetTagVal", j["stack"].Items[0]["function"].Str);
                Assert.Equal(424, j["code"].Long);
                Assert.Null(j.Get("an"));
            }
        }

        /// <summary>Every log line says what it is (ctsim/log 'kind'): a TraceMsg is the
        /// Cicode's own message, a simulator note the simulator's.</summary>
        [ProjectFact]
        public void LogLinesSayWhatTheyAre()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            {
                var logs = new List<LogEntry>();
                sim.Log += e =>
                {
                    lock (logs)
                        logs.Add(e);
                };
                Run(sim, "TraceMsg(\"kinds probe\")");
                sim.Note("info", "a simulator note");
                SimTestUtil.Until(
                    sim,
                    () =>
                    {
                        lock (logs)
                            return logs.Any(e => e.Text.Contains("kinds probe"))
                                && logs.Any(e => e.Text == "a simulator note");
                    },
                    what: "the two lines"
                );
                LogEntry trace,
                    note;
                lock (logs)
                {
                    trace = logs.First(e => e.Text.Contains("kinds probe"));
                    note = logs.First(e => e.Text == "a simulator note");
                }
                Assert.Equal("message", trace.Kind);
                Assert.Equal("message", trace.ToJson().Get("kind")?.Str);
                Assert.Equal("simulator", note.Kind);
                Assert.Equal("simulator", note.ToJson().Get("kind")?.Str);
            }
        }
    }
}
