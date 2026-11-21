using System;
using System.IO;
using System.Linq;
using CicodeSim.Host;
using CicodeSim.Values;
using Xunit;
using static CicodeSim.Tests.Host.ProtocolClient;

namespace CicodeSim.Tests.Host
{
    /// <summary>Alarm properties in tagsChanged, --start read in the --tz zone.</summary>
    [Collection("ctsim server")]
    public sealed class ServerFollowupTests
    {
        /// <summary>A subscribed alarm property ('Alarm.On') changes with no tag write (the alarm
        /// scan): it is sent when its value changes, by the name it was subscribed with, and not
        /// again while it stays.</summary>
        [ProjectFact]
        public void AlarmPropertiesAreSentWhenTheyChange()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.ExampleSA, startAlarms: true))
            using (var server = new SimServer(sim, "alarm-" + Guid.NewGuid().ToString("N").Substring(0, 8)))
            {
                server.Listen(
                    Path.Combine(Path.GetTempPath(), "ctsim-alarm-" + Guid.NewGuid().ToString("N"))
                );
                using (var c = Connect(server.Listener!.Address))
                {
                    c.Ok("ctsim/hello", Args(("client", "alarms")));
                    c.Ok("ctsim/setTag", Args(("name", "TopMilk_OV08_Fault"), ("value", 0)));
                    c.Ok("ctsim/clock", Args(("mode", "step"), ("value", 2)));
                    var sub = c.Ok("ctsim/subscribe", Args(("names", Names("OV08_Fault.On"))));
                    Assert.Equal(1, sub["subscribed"].Long);
                    Assert.Equal(0.0, sub["tags"].Items.Single()["value"].Double);
                    c.Ok("ctsim/setTag", Args(("name", "TopMilk_OV08_Fault"), ("value", 1)));
                    c.Ok("ctsim/clock", Args(("mode", "step"), ("value", 2)));
                    var ev = c.WaitEvent("ctsim/tagsChanged", e =>
                        e["tags"].Items.Any(t => t["name"].Str == "OV08_Fault.On")
                    );
                    Assert.Equal(
                        1.0,
                        ev["tags"].Items.First(t => t["name"].Str == "OV08_Fault.On")["value"].Double
                    );
                    c.Ok("ctsim/clock", Args(("mode", "step"), ("value", 2)));
                    c.Ok("ctsim/setTag", Args(("name", "TopMilk_OV08_Fault"), ("value", 0)));
                    c.Ok("ctsim/clock", Args(("mode", "step"), ("value", 2)));
                    var off = c.WaitEvent("ctsim/tagsChanged", e =>
                        e["tags"].Items.Any(t => t["name"].Str == "OV08_Fault.On")
                    );
                    Assert.Equal(
                        0.0,
                        off["tags"].Items.First(t => t["name"].Str == "OV08_Fault.On")["value"].Double
                    );
                    Assert.Equal(0, c.Ok("ctsim/unsubscribe", Args())["subscribed"].Long);
                }
            }
        }

        /// <summary>A long clock step (a page scanned 1,200 times) holds no other client: their
        /// requests are answered while it runs; the step answers when it ends, with what it moved.</summary>
        [ProjectFact]
        public void ALongStepDoesNotHoldOtherRequests()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            using (var server = new SimServer(sim, "step-" + Guid.NewGuid().ToString("N").Substring(0, 8)))
            {
                server.Listen(Path.Combine(Path.GetTempPath(), "ctsim-step-" + Guid.NewGuid().ToString("N")));
                using (var a = Connect(server.Listener!.Address))
                using (var b = Connect(server.Listener!.Address))
                {
                    a.Ok("ctsim/hello", Args(("client", "stepper"), ("events", Names("log"))));
                    b.Ok("ctsim/hello", Args(("client", "other"), ("events", Names("log"))));
                    a.Ok("ctsim/openPage", Args(("name", "Mixer")));
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    JsonValue? stepped = null;
                    var t = new System.Threading.Thread(() =>
                        stepped = a.Ok("ctsim/clock", Args(("mode", "step"), ("value", 300)), 120000)
                    );
                    t.Start();
                    System.Threading.Thread.Sleep(50);
                    var answers = new System.Collections.Generic.List<long>();
                    for (int i = 0; i < 5 && stepped == null; i++)
                    {
                        var q = System.Diagnostics.Stopwatch.StartNew();
                        b.Ok("ctsim/clock");
                        b.Ok("ctsim/pageState", Args(("window", 0)));
                        answers.Add(q.ElapsedMilliseconds);
                    }
                    t.Join(120000);
                    long stepMs = sw.ElapsedMilliseconds;
                    Assert.NotNull(stepped);
                    Assert.Equal(300000, stepped!["moved"].Long);
                    Assert.Equal("paused", stepped["mode"].Str);
                    // the other client's requests took a burst or two each, not the step
                    Assert.NotEmpty(answers);
                    Assert.True(
                        answers.Max() < Math.Max(1000, stepMs / 2),
                        "answers " + string.Join(", ", answers) + " ms, the step " + stepMs + " ms"
                    );
                    // a step asked while one runs follows it; 'run' ends a step where it is
                    var s2 = a.Ok("ctsim/clock", Args(("mode", "step"), ("value", 0.5)));
                    Assert.Equal(500, s2["moved"].Long);
                }
            }
        }

        /// <summary>A step still running when the simulation ends answers with what it moved (its
        /// client does not wait for its request timeout).</summary>
        [ProjectFact]
        public void AStepAnswersWhenTheSimulationEnds()
        {
            var sim = SimTestUtil.Start(SimCorpus.Example);
            var server = new SimServer(sim, "endstep-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                server.Listen(
                    Path.Combine(Path.GetTempPath(), "ctsim-endstep-" + Guid.NewGuid().ToString("N"))
                );
                using (var a = Connect(server.Listener!.Address))
                {
                    a.Ok("ctsim/hello", Args(("client", "stepper"), ("events", Names("clock"))));
                    a.Ok("ctsim/openPage", Args(("name", "Mixer")));
                    JsonValue? stepped = null;
                    Exception? failed = null;
                    var t = new System.Threading.Thread(() =>
                    {
                        try
                        {
                            stepped = a.Ok("ctsim/clock", Args(("mode", "step"), ("value", 100000)), 60000);
                        }
                        catch (Exception e)
                        {
                            failed = e;
                        }
                    });
                    t.Start();
                    System.Threading.Thread.Sleep(300);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    sim.Dispose();
                    Assert.True(t.Join(20000), "the step did not answer");
                    Assert.Null(failed);
                    Assert.NotNull(stepped);
                    Assert.True(stepped!["moved"].Long < 100000000L);
                    Assert.True(sw.ElapsedMilliseconds < 20000);
                }
            }
            finally
            {
                server.Dispose();
                sim.Dispose();
            }
        }

        /// <summary>scenario save writes the state as a scenario.</summary>
        [ProjectFact]
        public void ScenarioSaveWritesTheStateAsAScenario()
        {
            string file = Path.Combine(
                Path.GetTempPath(),
                "ctsim-save-" + Guid.NewGuid().ToString("N") + ".json"
            );
            try
            {
                using (var sim = SimTestUtil.Start(SimCorpus.ExampleSA))
                using (
                    var server = new SimServer(sim, "save-" + Guid.NewGuid().ToString("N").Substring(0, 8))
                )
                {
                    server.Listen(
                        Path.Combine(Path.GetTempPath(), "ctsim-save-" + Guid.NewGuid().ToString("N"))
                    );
                    using (var c = Connect(server.Listener!.Address))
                    {
                        c.Ok("ctsim/hello", Args(("client", "save")));
                        c.Ok("ctsim/setTag", Args(("name", "WTP_T201_LT"), ("value", 42.5)));
                        c.Ok("ctsim/setTag", Args(("name", "WTP_T202_LT"), ("value", 7), ("quality", "bad")));
                        c.Ok("ctsim/clock", Args(("mode", "step"), ("value", 3)));
                        var r = c.Ok("ctsim/scenario", Args(("action", "save"), ("path", file)));
                        Assert.Equal(file, r["saved"].Str, ignoreCase: true);
                        var sc = r["scenario"];
                        Assert.Equal("ctsim-scenario/0", sc["format"].Str);
                        Assert.Equal("2026-10-01T08:00:03.000Z", sc["clock"]["start"].Str);
                        Assert.Equal(42.5, sc["tags"]["WTP_T201_LT"]["value"].Double);
                        Assert.Equal("bad", sc["tags"]["WTP_T202_LT"]["quality"].Str);
                        var inside = c.Request(
                            "ctsim/scenario",
                            Args(("action", "save"), ("path", Path.Combine(SimCorpus.ExampleSA, "x.json")))
                        );
                        Assert.False(inside["success"].Bool);
                        Assert.False(File.Exists(Path.Combine(SimCorpus.ExampleSA, "x.json")));
                    }
                }
                using (var sim = SimTestUtil.Start(SimCorpus.ExampleSA))
                {
                    sim.Invoke(() =>
                    {
                        sim.LoadScenario(null, file);
                        sim.RunScenario();
                    });
                    sim.Invoke(() => sim.StepClock(500));
                    Assert.Equal(42.5, sim.Invoke(() => sim.ReadTag("WTP_T201_LT")).Value.Double);
                    Assert.Equal("bad", sim.Invoke(() => sim.ReadTag("WTP_T202_LT")).Quality);
                }
            }
            finally
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception) { }
            }
        }

        /// <summary>serve --start without a zone is a wall time of --tz (the simulated computer's
        /// zone), not this computer's.</summary>
        [ProjectFact]
        public void StartWithoutAZoneIsReadInTheSimulatedZone()
        {
            string disc = Path.Combine(Path.GetTempPath(), "ctsim-tz-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(disc);
            try
            {
                using (
                    var server = new ServerProcess(
                        "serve --project "
                            + ServerProcess.Quote(SimCorpus.ExampleSA)
                            + " --discovery "
                            + ServerProcess.Quote(disc)
                            + " --start 2026-07-01T08:00:00 --tz \"W. Europe Standard Time\" --clock paused"
                    )
                )
                using (var c = Connect(server.Pipe))
                {
                    var h = c.Ok("ctsim/hello", Args(("client", "tz")));
                    // summer time in Western Europe: UTC+2
                    Assert.Equal("2026-07-01T06:00:00.000Z", h["clock"]["start"].Str);
                    c.Ok("ctsim/shutdown");
                    Assert.True(server.WaitExit(15000));
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(disc, true);
                }
                catch (Exception) { }
            }
        }
    }
}
