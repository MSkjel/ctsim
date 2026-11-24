using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using CicodeSim.Host;
using CicodeSim.Sim;
using CicodeSim.Values;
using CicodeSim.Vm;
using Xunit;
using static CicodeSim.Tests.Host.ProtocolClient;

namespace CicodeSim.Tests.Host
{
    /// <summary>The Simulation host and the ctsim/* protocol in-process (a SimServer on its own
    /// pipe): the compile seam, calls, not-simulated built-ins, the clock, scenarios, batching.</summary>
    [Collection("ctsim server")]
    public sealed class SimulationTests
    {
        [ProjectFact]
        public void TheCompileSeam()
        {
            // 'all': every function from source, the same functions as the RDB
            using (
                var all = new Simulation(
                    new SimulationOptions
                    {
                        ProjectDir = SimCorpus.ExampleSA,
                        Compile = "all",
                        StartAlarms = false,
                    }
                )
            )
            {
                Assert.Equal(1869, all.Library.Functions.Count);
                Assert.NotNull(all.Live.Source);
            }
            Assert.Throws<ArgumentException>(() =>
                new Simulation(new SimulationOptions { ProjectDir = SimCorpus.ExampleSA, Compile = "fast" })
            );
            Assert.Throws<FileNotFoundException>(() =>
                new Simulation(
                    new SimulationOptions
                    {
                        ProjectDir = SimCorpus.ExampleSA,
                        Rdb = Path.Combine(SimCorpus.ExampleSA, "NoSuch.RDB"),
                    }
                )
            );
            using (
                var sim = new Simulation(
                    new SimulationOptions
                    {
                        ProjectDir = SimCorpus.ExampleSA,
                        Compile = "none",
                        StartAlarms = false,
                    }
                )
            )
            {
                Assert.Equal("ExampleSA", sim.Root);
                Assert.True(sim.Library.Functions.Count > 1000);
                Assert.Equal(SimCorpus.User, sim.UserDir, ignoreCase: true);
            }
        }

        [ProjectFact]
        public void CallsOutcomesAndNotSimulatedBuiltins()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.ExampleSA))
            {
                var ns = new List<(string, int)>();
                sim.NotSimulated += (n, c) =>
                {
                    lock (ns)
                        ns.Add((n, c));
                };
                CallOutcome Run(string text)
                {
                    CallOutcome? o = null;
                    var done = new ManualResetEventSlim(false);
                    sim.Invoke(() => sim.StartCall(text, r =>
                        {
                            o = r;
                            done.Set();
                        }));
                    Assert.True(done.Wait(10000), text);
                    return o!;
                }
                var r = Run("StrToInt(\"12abc\")");
                Assert.Equal(("stop", "12", "INT"), (r.State, r.Result, r.Type));
                Assert.Equal(12, r.Value.Long);
                // ExampleSA's own PageDisplay (SA_Include overrides the built-in) reads ParameterGet,
                // then calls _PageDisplay (the display client): the page is not in ExampleSA's catalogue, so it ends with the page change's error
                var pd = Run("PageDisplay(\"Mixer\")");
                Assert.Equal("stop", pd.State);
                // a built-in that is not simulated halts the task there: the first one without
                // arguments that the World registry does not implement and no project function hides
                var reg = (CicodeSim.Builtins.Registry)sim.Vm.Builtins!;
                string? nsName = null;
                foreach (var b in reg.Table.Entries)
                    if (
                        b.MinArgs == 0
                        && b.NArgs == 0
                        && !reg.IsImplemented(b.Name)
                        && sim.Library.Function(b.Name) == null
                        && b.Name.Length > 0
                        && char.IsLetter(b.Name[0])
                    )
                    {
                        nsName = b.Name;
                        break;
                    }
                if (nsName == null)
                {
                    // every built-in has a behaviour: one answers 'not
                    // simulated' as the registry does for a missing implementation
                    var b = reg.Table.Lookup("MultiMonitorStart")!;
                    sim.Invoke(() => reg.Register(b.Name, (tk, args) => Value.NotSimulated(b.Name, b.Index)));
                    nsName = b.Name;
                }
                Assert.NotNull(nsName);
                var ps = Run(nsName + "()");
                Assert.Equal("halt", ps.State);
                Assert.Equal(nsName, ps.NotSimulated);
                Assert.Contains("is not simulated", ps.HaltReason);
                SimTestUtil.Until(sim, () =>
                {
                    lock (ns)
                        return ns.Count > 0;
                });
                Assert.Equal((nsName!, 1), ns[0]);
                // A stub returns its documented result and emits a not-simulated event.
                var beep = Run("Beep(0)");
                Assert.Equal(("stop", "1"), (beep.State, beep.Result));
                var set = Run("TimeSet(1600000000)");
                Assert.Equal(("stop", "0"), (set.State, set.Result));
                SimTestUtil.Until(sim, () =>
                {
                    lock (ns)
                        return ns.Exists(x => x == ("Beep", 1));
                });
                // run-time call text: 292 for anything but one call with literal arguments
                foreach (
                    string bad in new[]
                    {
                        "1 + 2",
                        "TRUE",
                        "\"abc\"",
                        "NoSuchFunctionXyz()",
                        "IntToStr(1) + 1",
                    }
                )
                {
                    var e = Assert.Throws<CallTextException>(() =>
                        sim.Invoke(() => sim.StartCall(bad, null))
                    );
                    Assert.Equal(Errors.InvalidFunction, e.Code);
                }
                var mismatch = Assert.Throws<CallTextException>(() =>
                    sim.Invoke(() => sim.StartCall("AddZeroFront(\"abc\")", null))
                );
                Assert.Equal(Errors.TypeMismatch, mismatch.Code);
                Assert.Contains("number type (4) parameter expected", mismatch.Message);
                Assert.Equal("\"x(1)\"", ValueFormat.Quote("x(1)"));
                Simulation.SplitCall(" StrLength( \"a)b\" ) ;", out string name, out string args);
                Assert.Equal(("StrLength", " \"a)b\" "), (name, args));
            }
        }

        [ProjectFact]
        public void ClockModesStepsAndSpeed()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.ExampleSA))
            {
                var changes = new List<JsonValue>();
                sim.ClockChanged += c =>
                {
                    lock (changes)
                        changes.Add(c);
                };
                var s = sim.Invoke(() => sim.ClockState());
                Assert.Equal("paused", s["mode"].Str);
                Assert.Equal(0, s["ms"].Long);
                Assert.Equal("2026-10-01T08:00:00.000Z", s["time"].Str);
                Assert.Equal(1250, sim.Invoke(() => sim.StepClock(1250)));
                Assert.Equal(1.25, sim.Invoke(() => sim.World.Clock.Now()));
                sim.Invoke(() => sim.SetSpeed(50));
                sim.Invoke(() => sim.SetClockMode("run"));
                SimTestUtil.Until(sim, () => sim.World.Clock.NowMs > 3000, 10000, "the clock to run");
                sim.Invoke(() => sim.SetClockMode("pause"));
                long at = sim.Invoke(() => sim.World.Clock.NowMs);
                Thread.Sleep(100);
                Assert.Equal(at, sim.Invoke(() => sim.World.Clock.NowMs)); // paused: time stands still
                Assert.Throws<ArgumentException>(() => sim.Invoke(() => sim.SetClockMode("fast")));
                sim.Events.Flush();
                lock (changes)
                {
                    Assert.Equal(
                        new[] { "paused", "paused", "run", "paused" },
                        changes.Select(c => c["mode"].Str).ToArray()
                    );
                    Assert.Equal(50.0, changes[1]["speed"].Double);
                }
            }
        }

        [ProjectFact]
        public void ScenariosRunOnTheMovingClock()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.ExampleSA))
            {
                JsonValue? result = null;
                var done = new ManualResetEventSlim(false);
                sim.ScenarioFinished += r =>
                {
                    result = r;
                    done.Set();
                };
                var sc = JsonValue.Parse(
                    @"{""format"": ""ctsim-scenario/0"", ""project"": ""ExampleSA"",
                    ""description"": ""moving clock"",
                    ""tags"": {""WTP_T201_LT"": 10},
                    ""steps"": [
                        {""at"": 1.0, ""set"": {""WTP_T201_LT"": 85}},
                        {""at"": 1.0, ""expect"": {""WTP_T201_LT"": 85}},
                        {""at"": 2.0, ""call"": ""TaskNew(\""_PltLineStyleGet\"", \""1\"", 0)""},
                        {""at"": 3.0, ""expect"": {""WTP_T201_LT"": 99}}]}"
                );
                sim.Invoke(() =>
                {
                    sim.LoadScenario(sc, null);
                    sim.RunScenario();
                });
                Assert.True(sim.Invoke(() => sim.ScenarioRunning));
                Assert.Equal(10, sim.Invoke(() => sim.ReadTag("WTP_T201_LT").Value.Double)); // the setup ran
                sim.Invoke(() => sim.StepClock(5000));
                Assert.True(done.Wait(10000));
                Assert.Equal(1, result!["passed"].Long);
                Assert.Equal(1, result["failed"].Long);
                Assert.Equal("WTP_T201_LT", result["failures"].Items.Single()["target"].Str);
                Assert.Equal(3.0, result["end"].Double);
                Assert.Equal("moving clock", result["description"].Str);
                Assert.False(sim.Invoke(() => sim.ScenarioRunning));
                // a stopped one says which too
                sim.Invoke(() =>
                {
                    sim.LoadScenario(sc, null);
                    sim.RunScenario();
                    sim.StopScenario();
                });
                sim.Events.Flush();
                Assert.True(result!["stopped"].Bool);
                Assert.Equal("moving clock", result["description"].Str);
            }
        }

        [ProjectFact]
        public void TheProtocolInProcess()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.ExampleSA))
            using (var server = new SimServer(sim, "test-" + Guid.NewGuid().ToString("N").Substring(0, 8)))
            {
                string disc = Path.Combine(
                    Path.GetTempPath(),
                    "ctsim-inproc-" + Guid.NewGuid().ToString("N")
                );
                try
                {
                    server.Listen(disc);
                    using (var c = Connect(server.Listener!.Address))
                    {
                        // before hello: any ctsim/* request works; a non-ctsim request is refused
                        var bad = c.Request("threads");
                        Assert.False(bad["success"].Bool);
                        Assert.Contains("ctsim/hello", bad["message"].Str);
                        var h = c.Ok(
                            "ctsim/hello",
                            Args(("client", "inproc"), ("events", Names("log", "task")))
                        );
                        Assert.True(h["capabilities"]["pages"].Bool);
                        Assert.False(h["debugger"].Bool);
                        var wrong = c.Request("ctsim/load", Args(("project", "Example")));
                        Assert.False(wrong["success"].Bool);
                        Assert.Contains("this server simulates", wrong["message"].Str);
                        c.Ok("ctsim/load", Args(("project", "ExampleSA")));
                        // subscriptions: add, replace, remove
                        Assert.Equal(
                            2,
                            c.Ok("ctsim/subscribe", Args(("names", Names("WTP_T201_LT", "WTP_T202_LT"))))[
                                "subscribed"
                            ].Long
                        );
                        Assert.Equal(
                            1,
                            c.Ok("ctsim/subscribe", Args(("names", Names("WTP_T201_LT")), ("replace", true)))[
                                "subscribed"
                            ].Long
                        );
                        Assert.Equal(
                            0,
                            c.Ok("ctsim/unsubscribe", Args(("names", Names("WTP_T201_LT"))))[
                                "subscribed"
                            ].Long
                        );
                        // a page's tags (its compiled RDB's TAGREFs)
                        var sp = c.Ok("ctsim/subscribe", Args(("page", "WTP_INTAKE_HD1080")));
                        Assert.True(sp["page"]["tags"].Long > 0);
                        Assert.True(sp["subscribed"].Long > 0);
                        var pt = c.Ok("ctsim/tags", Args(("page", "WTP_INTAKE_HD1080")));
                        Assert.True(pt["total"].Long > 0);
                        Assert.False(
                            c.Request("ctsim/subscribe", Args(("page", "NoSuchPage")))["success"].Bool
                        );
                        // a failing setTag says why
                        var st = c.Request("ctsim/setTag", Args(("name", "NoSuchTagXyz"), ("value", 1)));
                        Assert.False(st["success"].Bool);
                        Assert.Equal(424, st["body"]["error"].Long);
                        // no debugger attached: TagRead's non-halting 424 does not park the call's task
                        var gv = c.Ok("ctsim/call", Args(("expr", "GetTagVal(\"NoSuchTagXyz\")")));
                        Assert.Equal("stop", gv["state"].Str);
                        Assert.Equal("0", gv["result"].Str);
                        // wait: false and timeoutMs on a call that sleeps (paused clock)
                        var w = c.Ok("ctsim/call", Args(("expr", "Sleep(5)"), ("timeoutMs", 200)));
                        Assert.True(w["timedOut"].Bool);
                        Assert.Equal("running", w["state"].Str);
                        var started = c.WaitEvent("ctsim/taskStarted", e => e["task"].Long == w["task"].Long);
                        Assert.Equal("Sleep", started["name"].Str);
                        c.Ok("ctsim/clock", Args(("mode", "step"), ("value", 6)));
                        var exited = c.WaitEvent("ctsim/taskExited", e => e["task"].Long == w["task"].Long);
                        Assert.Equal("Stop", exited["state"].Str);
                        // scenario through the protocol
                        var sc = c.Ok(
                            "ctsim/scenario",
                            Args(
                                ("action", "run"),
                                (
                                    "scenario",
                                    JsonValue.Parse(
                                        @"{""format"": ""ctsim-scenario/0"", ""steps"": [{""at"": 0.5, ""set"": {""WTP_T201_LT"": 7}}]}"
                                    )
                                )
                            )
                        );
                        Assert.True(sc["loaded"].Bool && sc["running"].Bool);
                        Assert.False(c.Request("ctsim/scenario", Args(("action", "jump")))["success"].Bool);
                        c.Ok("ctsim/scenario", Args(("action", "stop")));
                        // no page shown yet: an empty state; input needs a page
                        var pst = c.Ok("ctsim/pageState", Args(("window", 0)));
                        Assert.Equal("", pst["page"].Str);
                        Assert.Equal(0, pst["objects"].Count);
                        var inp = c.Request("ctsim/input", Args(("window", 0), ("an", 12), ("kind", "down")));
                        Assert.False(inp["success"].Bool);
                        Assert.Contains("shows no page", inp["message"].Str);
                        var re = c.Ok(
                            "ctsim/reload",
                            Args(("file", @"C:\x\a.ci"), ("text", "FUNCTION F() END"))
                        );
                        Assert.True(re["accepted"].Bool);
                        Assert.False(c.Request("ctsim/nope")["success"].Bool);
                    }
                    Assert.Single(Directory.GetFiles(disc));
                }
                finally
                {
                    server.Dispose();
                    Assert.Empty(Directory.GetFiles(disc));
                    try
                    {
                        Directory.Delete(disc, true);
                    }
                    catch (Exception) { }
                }
            }
        }

        [ProjectFact]
        public void TagChangesAreBatchedPerScan()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.ExampleSA))
            {
                var batches = new List<TagBatch>();
                sim.TagsChanged += b =>
                {
                    lock (batches)
                        batches.Add(b);
                };
                sim.Invoke(() =>
                {
                    for (int i = 0; i < 100; i++)
                        sim.World.Tags.Set("WTP_T201_LT", JsonValue.Of(i), null, by: "user");
                    sim.World.Tags.Set("WTP_T202_LT", JsonValue.Of(5.5), null, by: "user");
                });
                SimTestUtil.Until(sim, () =>
                {
                    lock (batches)
                        return batches.Count > 0;
                });
                Thread.Sleep(150);
                lock (batches)
                {
                    var names = batches.SelectMany(b => b.Updates).Select(u => u.Name).ToList();
                    Assert.Equal(1, names.Count(n => n == "WTP_T201_LT")); // 100 writes, one update: the current value
                    Assert.Equal(
                        99,
                        batches.SelectMany(b => b.Updates).Single(u => u.Name == "WTP_T201_LT").Value.Double
                    );
                    Assert.Contains("WTP_T202_LT", names);
                    Assert.Equal("paused", batches[0].Clock["mode"].Str);
                }
            }
        }

        [ProjectFact]
        public void TrnPlotPrintsATrendThroughThePlotFunctions()
        {
            // the Include project's TrnPlot (graph.ci) drives PlotOpen .. PlotClose to a printer:
            // an SVG page in the sandbox
            using (
                var sim = new Simulation(
                    new SimulationOptions
                    {
                        ProjectDir = SimCorpus.ExampleSA,
                        Compile = CodeSource.Changed,
                        ClockMode = "paused",
                        Start = "2026-10-01T08:00:00Z",
                        StartAlarms = false,
                        ScanMs = 50,
                        TimeZone = TimeZoneInfo.Utc,
                        Sandbox = new CicodeSim.Builtins.SandboxOptions { InMemory = true },
                    }
                )
            )
            {
                sim.Start();
                // 30 seconds of the trend's tag: TrnGetTable has samples to give
                for (int i = 0; i < 30; i++)
                {
                    int v = i;
                    sim.Invoke(() =>
                        sim.World.Tags.Set("SA_WindCompass_WindSpeed", JsonValue.Of(v), null, by: "user")
                    );
                    sim.Invoke(() => sim.StepClock(1000));
                }
                CallOutcome? o = null;
                var done = new ManualResetEventSlim(false);
                sim.Invoke(() =>
                    sim.StartCall(
                        "TrnPlot(\"LPT1:\", 20, 0, 1, \"Wind\", 0, \"SA_WindCompass_WindSpeed\")",
                        r =>
                        {
                            o = r;
                            done.Set();
                        }
                    )
                );
                Assert.True(done.Wait(30000), "TrnPlot");
                Assert.Equal(("stop", "0"), (o!.State, o.Result));
                byte[]? b = sim.Invoke(() =>
                    sim.Host.Sandbox.ReadAll(sim.Host.Sandbox.Full("Printers\\LPT1-1.svg"))
                );
                Assert.NotNull(b);
                string svg = System.Text.Encoding.UTF8.GetString(b!);
                Assert.Contains("width=\"2480\" height=\"3508\"", svg);
                Assert.Contains("<polyline", svg); // the pen's line
                Assert.Contains(">Wind</text>", svg); // the title
                Assert.Contains(">SA_WindCompass_WindSpeed</text>", svg); // the legend
            }
        }
    }
}
