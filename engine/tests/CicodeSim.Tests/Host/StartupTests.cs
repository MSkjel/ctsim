using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using CicodeSim.Host;
using CicodeSim.Sim;
using CicodeSim.Values;
using Xunit;
using static CicodeSim.Tests.Host.ProtocolClient;

namespace CicodeSim.Tests.Host
{
    /// <summary>'Start like the runtime' (ctsim/load {startup: "auto"}, RuntimeStartup) and the
    /// display answers it needs: PageInfo(30), the page list of a viewer-opened page, PageFileInfo
    /// from the .ctg head, MenuGetWindowNode (R2 ExampleSA / Example / Test, read only).</summary>
    [Collection("ctsim server")]
    public sealed class StartupTests
    {
        private static CallOutcome Run(Simulation sim, string text)
        {
            CallOutcome? o = null;
            var done = new ManualResetEventSlim(false);
            sim.Invoke(() => sim.StartCall(text, r =>
                {
                    o = r;
                    done.Set();
                }));
            // small steps: a call ends in its first slices, and a busy project (ExampleSA's
            // conveyor loop never sleeps while the line is off) costs real time per simulated ms
            for (int i = 0; i < 3000 && !done.IsSet; i++)
                sim.Invoke(() => sim.StepClock(10));
            Assert.True(done.Wait(30000), text);
            return o!;
        }

        private static List<string> Functions(JsonValue body) =>
            body["startupTasks"].Items.Select(x => x["function"].Str).ToList();

        [ProjectFact]
        public void ExampleSAStartsItsWorkspaceAndPrioritiesOnceWindowZeroShowsAPage()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.ExampleSA))
            {
                // the screen profile the project ships (the active citect.ini may name another)
                sim.Invoke(() => sim.SetParam("MultiMonitors", "ScreenProfile", "OneMonitorHD"));
                // asked before any page: it waits for window 0
                var early = sim.Invoke(() => sim.Boot.Request());
                Assert.True(early["pending"].Bool);
                Assert.Empty(early["startupTasks"].Items);
                Assert.True(sim.Invoke(() => sim.Boot.Pending));
                var notes = new List<string>();
                var open = sim.Invoke(() =>
                    sim.Pages.Open("Master_PageMenu1_HD1080", 0, null, null, null, "rdb", notes)
                );
                Assert.Equal(0, open["error"].Long);
                Assert.True(sim.Invoke(() => sim.Boot.Ran));
                var again = sim.Invoke(() => sim.Boot.Request());
                var fns = Functions(again);
                Assert.Equal("_CitectBegin", fns[0]);
                Assert.Contains("ClientStartup", fns);
                Assert.Contains(again["notes"].Items, n => n.Str.Contains("ran already"));
                sim.Invoke(() => sim.StepClock(5000));
                // _CitectBegin -> _SA_Library_Startup set the priority array
                Assert.Equal("1", Run(sim, "_Alarm_GetPriority(0)").Result);
                Assert.Equal("Engineer", Run(sim, "UserInfo(1)").Result);
                Assert.Equal("Screen1", Run(sim, "PageInfo(30)").Result);
                // the viewer-opened page is in the window's page list
                Assert.Equal("0", Run(sim, "PageListCurrent()").Result);
                // a page no viewer sent: its size from the .ctg head (SA_Include has Info_Alarm_HD1080)
                Assert.Equal("1920", Run(sim, "PageFileInfo(\"Overview_L2\", 0)").Result);
                Assert.Equal("760", Run(sim, "PageFileInfo(\"Overview_L2\", 1)").Result);
                Assert.NotEqual("0", Run(sim, "PageFileInfo(\"Info_Alarm_HD1080\", 0)").Result);
                // the window's menu: one root per page instance, the generic items in it
                string node = Run(sim, "MenuGetWindowNode(0)").Result;
                Assert.True(long.Parse(node) > 0, node);
                Assert.Equal(node, Run(sim, "MenuGetWindowNode(0)").Result);
                Assert.Equal("-1", Run(sim, "MenuGetWindowNode(-2)").Result);
                string gen = Run(sim, "MenuGetGenericNode()").Result;
                if (long.Parse(gen) > 0)
                {
                    string g1 = Run(sim, "MenuGetFirstChild(" + gen + ")").Result,
                        w1 = Run(sim, "MenuGetFirstChild(" + node + ")").Result;
                    Assert.NotEqual(g1, w1); // a copy, not the generic node itself
                    Assert.Equal(
                        Run(sim, "MenuNodeGetProperty(" + g1 + ", 0)").Result,
                        Run(sim, "MenuNodeGetProperty(" + w1 + ", 0)").Result
                    );
                }
            }
        }

        [ProjectFact]
        public void ExampleStartupSetsTheAlarmPageGlobals()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            {
                var notes = new List<string>();
                Assert.Equal(
                    0,
                    sim.Invoke(() => sim.Pages.Open("Mixer", 0, null, null, null, "rdb", notes))["error"].Long
                );
                // without the startup AlarmPage is '' and PageAlarm() opens nothing
                var b = sim.Invoke(() => sim.Boot.Request());
                Assert.False(b.Get("pending")?.Bool ?? false);
                Assert.Equal("_CitectBegin", Functions(b)[0]);
                sim.Invoke(() => sim.StepClock(3000));
                Run(sim, "PageAlarm()");
                Assert.Equal("Alarm", Run(sim, "PageInfo(0)").Result);
                // a project without a screen profile file answers ''
                sim.Invoke(() => sim.SetParam("MultiMonitors", "ScreenProfile", "NoSuchProfile"));
                Assert.Equal("", Run(sim, "PageInfo(30)").Result);
            }
        }

        [ProjectFact]
        public void AWaitingStartupRunsWhenCicodeShowsAPageInWindowZero()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            {
                Assert.True(sim.Invoke(() => sim.Boot.Request())["pending"].Bool);
                // a page change of Cicode (ctsim/call, a scenario's call step), not openPage
                Run(sim, "PageDisplay(\"Mixer\")");
                Assert.Equal("Mixer", Run(sim, "PageInfo(0)").Result);
                Assert.True(sim.Invoke(() => sim.Boot.Ran));
                Assert.Equal("_CitectBegin", Functions(sim.Invoke(() => sim.Boot.Request()))[0]);
            }
        }

        [ProjectFact]
        public void ALoadWithAPageAnswersTheTasksItsPageStarted()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            using (var server = new SimServer(sim, "test-" + Guid.NewGuid().ToString("N").Substring(0, 8)))
            {
                string disc = Path.Combine(
                    Path.GetTempPath(),
                    "ctsim-startup-" + Guid.NewGuid().ToString("N")
                );
                try
                {
                    server.Listen(disc);
                    using (var c = Connect(server.Listener!.Address))
                    {
                        c.Ok("ctsim/hello", Args(("client", "startup")));
                        var b = c.Ok("ctsim/load", Args(("startup", "auto"), ("page", "Mixer")), 60000);
                        Assert.Equal(0, b["page"]["error"].Long);
                        Assert.Null(b.Get("pending"));
                        Assert.Equal("_CitectBegin", Functions(b)[0]);
                    }
                }
                finally
                {
                    server.Dispose();
                    try
                    {
                        Directory.Delete(disc, true);
                    }
                    catch (Exception) { }
                }
            }
        }
    }
}
