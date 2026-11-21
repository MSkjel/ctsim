using System;
using System.IO;
using System.Linq;
using CicodeSim.Values;
using Xunit;
using static CicodeSim.Tests.Host.ProtocolClient;

namespace CicodeSim.Tests.Host
{
    /// <summary>Page code the debugger stops, through the DAP session.</summary>
    [Collection("ctsim server")]
    public sealed class DapForegroundTests
    {
        private static readonly string Alarms = Path.Combine(SimCorpus.Example, "Alarms.ci");

        private static int LineOf(string text)
        {
            var lines = File.ReadAllLines(Alarms);
            for (int k = 0; k < lines.Length; k++)
                if (lines[k].Contains(text))
                    return k + 1;
            throw new InvalidOperationException("Alarms.ci has no line with " + text);
        }

        private static JsonValue Bps(params int[] lines)
        {
            var a = JsonValue.NewArray();
            foreach (int l in lines)
                a.Add(Args(("line", l)));
            return Args(("source", Args(("path", Alarms))), ("breakpoints", a));
        }

        /// <summary>Attached, a breakpoint in AlarmCountByEquipment, the Mixer page opened: the
        /// stop's thread id.</summary>
        private static long StopInPageCode(DapRig rig, int line, JsonValue? attach = null)
        {
            var c = rig.Client;
            rig.Initialize();
            Assert.True(c.Ok("setBreakpoints", Bps(line))["breakpoints"].Items.Single()["verified"].Bool);
            c.Ok("attach", attach ?? Args());
            c.Ok("configurationDone");
            rig.Sim.Post(() => rig.Sim.Pages.OpenForScenario(0, "Mixer"));
            var stop = c.WaitEvent("stopped");
            Assert.Equal("breakpoint", stop["reason"].Str);
            return stop["threadId"].Long;
        }

        [ProjectFact]
        public void PageCodeStopsStepsAndItsThreadExits()
        {
            using (var rig = new DapRig(project: SimCorpus.Example))
            {
                var c = rig.Client;
                int line = LineOf("AlmFilter = \"Equipment=\" + EquipName;");
                long tid = StopInPageCode(rig, line);
                Assert.NotNull(rig.Sim.HeldBy);
                // the held task is a thread the client can ask about
                Assert.Contains(c.Ok("threads")["threads"].Items, t => t["id"].Long == tid);
                var f0 = c.Ok("stackTrace", Args(("threadId", tid)))["stackFrames"].Items[0];
                Assert.Equal("AlarmCountByEquipment", f0["name"].Str, ignoreCase: true);
                Assert.Equal(line, f0["line"].Long);
                Assert.Equal(Alarms, f0["source"]["path"].Str, ignoreCase: true);
                var locals = c.Ok("scopes", Args(("frameId", f0["id"].Long)))["scopes"]
                    .Items.First(s => s["name"].Str == "Locals");
                Assert.NotEmpty(
                    c.Ok("variables", Args(("variablesReference", locals["variablesReference"].Long)))[
                        "variables"
                    ].Items
                );

                // a step: stopped again on the next line, in the held scan
                c.Ok("next", Args(("threadId", tid)));
                var step = c.WaitEvent("stopped", s => s["reason"].Str == "step");
                Assert.Equal(tid, step["threadId"].Long);
                Assert.Equal(
                    line + 1,
                    c.Ok("stackTrace", Args(("threadId", tid)))["stackFrames"].Items[0]["line"].Long
                );

                // no breakpoint, a step out: the expression ends, the scan goes on and the thread
                // the client was shown exits (page code sends no TaskExited of its own)
                c.Ok("setBreakpoints", Bps());
                c.Ok("stepOut", Args(("threadId", tid)));
                c.WaitEvent("thread", t => t["reason"].Str == "exited" && t["threadId"].Long == tid);
                SimTestUtil.Until(rig.Sim, () => rig.Sim.HeldBy == null);
                c.Ok("disconnect");
            }
        }

        [ProjectFact]
        public void AReloadWhileHeldDoesNotStopTheSessionReading()
        {
            using (var rig = new DapRig(project: SimCorpus.Example))
            {
                var c = rig.Client;
                long tid = StopInPageCode(rig, LineOf("AlmFilter = \"Equipment=\" + EquipName;"));
                // a save during the stop (reloadOnSave): the reload waits for the code to go on,
                // the requests after it are still read and answered
                var reload = System.Threading.Tasks.Task.Run(() =>
                    c.Request("ctsim/reload", Args(("file", Alarms)), 30000)
                );
                Assert.False(reload.Wait(500));
                c.Ok("setBreakpoints", Bps(), 5000);
                Assert.Contains(c.Ok("threads", null, 5000)["threads"].Items, t => t["id"].Long == tid);
                c.Ok("continue", Args(("threadId", tid)), 5000);
                Assert.True(reload.Wait(20000), "the reload is answered once the code went on");
                Assert.True(reload.Result["success"].Bool, reload.Result.Dump());
                c.Ok("disconnect");
            }
        }

        [ProjectFact]
        public void PageCodeErrorsStopOnlyWithTheirFilterAndNeverBeforeTheFilterArrives()
        {
            // ExampleSA's Mixing_L3 raises hardware errors in its expressions on every scan
            using (var rig = new DapRig(project: SimCorpus.ExampleSA))
            {
                var c = rig.Client;
                var caps = rig.Initialize();
                var page = caps["exceptionBreakpointFilters"]
                    .Items.Single(f => f["filter"].Str == "pageCodeError");
                Assert.False(page["default"].Bool);
                // before setExceptionBreakpoints (VS Code sends it after its breakpoints) nothing
                // stops on an error: the errors are only written
                c.Ok("attach");
                rig.Sim.Post(() => rig.Sim.Pages.OpenForScenario(0, "Mixing_L3"));
                SimTestUtil.Until(
                    rig.Sim,
                    () => rig.Sim.World.Display.OpenWindow(0)?.Page != null,
                    what: "the page"
                );
                c.WaitEvent("output", o =>
                    o["category"].Str == "stderr"
                    && o["output"].Str.StartsWith("Cicode hardware error", StringComparison.Ordinal)
                );
                System.Threading.Thread.Sleep(1500); // ExampleSA's background tasks raise errors too
                Assert.Null(rig.Sim.HeldBy);
                lock (c.Received)
                    Assert.DoesNotContain(c.Received, m =>
                        m.Get("type")?.Str == "event" && m.Get("event")?.Str == "stopped"
                    );

                // the hardwareError filter alone: page code runs on, the error says how to stop it
                c.Ok("setExceptionBreakpoints", Args(("filters", List("hardwareError"))));
                c.Ok("configurationDone");
                rig.Sim.Post(() => rig.Sim.Pages.OpenForScenario(0, "Mixing_L3"));
                c.WaitEvent("output", o =>
                    o["output"]
                        .Str.Contains("[page code runs on; the 'Page code hardware errors' filter stops it]")
                );
                Assert.Null(rig.Sim.HeldBy);

                // with the page code filter the first error holds the page
                c.Ok("setExceptionBreakpoints", Args(("filters", List("hardwareError", "pageCodeError"))));
                c.Ok("continue", Args(("threadId", 1))); // re-arms the error report
                rig.Sim.Post(() => rig.Sim.Pages.OpenForScenario(0, "Mixing_L3"));
                // (ExampleSA's background tasks stop on their own errors too)
                c.WaitEvent("stopped", s => s["reason"].Str == "exception" && s["threadId"].Long == -1);
                Assert.NotNull(rig.Sim.HeldBy);
                c.Ok("disconnect", null, 10000);
                SimTestUtil.Until(rig.Sim, () => rig.Sim.HeldBy == null, 10000, "the hold to end");
            }
        }

        private static JsonValue List(params string[] names)
        {
            var a = JsonValue.NewArray();
            foreach (var n in names)
                a.Add(JsonValue.Of(n));
            return a;
        }

        [ProjectFact]
        public void DisconnectWhileHeldLetsThePageCodeGoOn()
        {
            using (var rig = new DapRig(project: SimCorpus.Example))
            {
                StopInPageCode(rig, LineOf("AlmFilter = \"Equipment=\" + EquipName;"));
                Assert.NotNull(rig.Sim.HeldBy);
                rig.Client.Ok("disconnect", null, 10000);
                SimTestUtil.Until(rig.Sim, () => rig.Sim.HeldBy == null, 10000, "the hold to end");
            }
        }

        [ProjectFact]
        public void ForegroundBreakFalseKeepsTheRuntimesRule()
        {
            using (var rig = new DapRig(project: SimCorpus.Example))
            {
                var c = rig.Client;
                rig.Initialize();
                c.Ok("setBreakpoints", Bps(LineOf("AlmFilter = \"Equipment=\" + EquipName;")));
                c.Ok("attach", Args(("foregroundBreak", false)));
                c.Ok("configurationDone");
                Assert.False(rig.Sim.Invoke(() => rig.Sim.ForegroundBreak));
                rig.Sim.Invoke(() => rig.Sim.Pages.OpenForScenario(0, "Mixer"));
                // notice 343 on the console, no stop, nothing held
                c.WaitEvent("output", o => o["output"].Str.Contains("Foreground Cicode cannot break"));
                Assert.Null(rig.Sim.HeldBy);
                Assert.DoesNotContain(c.Received.ToArray(), m =>
                    m.Get("type")?.Str == "event" && m.Get("event")?.Str == "stopped"
                );
            }
        }
    }
}
