using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CicodeSim.Host;
using CicodeSim.Sim;
using CicodeSim.Values;
using Xunit;
using Xunit.Abstractions;
using static CicodeSim.Tests.Host.ProtocolClient;

namespace CicodeSim.Tests.Host
{
    /// <summary>'Start like the runtime' with the startup page (load {startPage: true}) and a page
    /// shown the way the project's navigation shows it (ctsim/navigate): the SA workspace puts it
    /// in its content pane (R2 ExampleSA / Example, read only).</summary>
    [Collection("ctsim server")]
    public sealed class NavigateTests
    {
        private readonly ITestOutputHelper _out;

        public NavigateTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static (SimServer server, string disc) Serve(Simulation sim)
        {
            var server = new SimServer(sim, "navigate-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string disc = Path.Combine(Path.GetTempPath(), "ctsim-navigate-" + Guid.NewGuid().ToString("N"));
            server.Listen(disc);
            return (server, disc);
        }

        private static void Done(SimServer server, string disc)
        {
            server.Dispose();
            try
            {
                Directory.Delete(disc, true);
            }
            catch (Exception) { }
        }

        /// <summary>Runs <paramref name="request"/> as a viewer does: each drawing the engine asks
        /// for meanwhile gets an empty one (a held page starts at once, not after the 20 s wait).</summary>
        private static JsonValue AnsweringModels(ProtocolClient c, Func<JsonValue> request)
        {
            var task = Task.Run(request);
            while (!task.IsCompleted)
            {
                JsonValue want;
                try
                {
                    want = c.WaitEvent("ctsim/modelWanted", null, 100);
                }
                catch (TimeoutException)
                {
                    continue;
                }
                var model = JsonValue.Parse(
                    @"{""format"": ""ctsim-page/0"", ""size"": [1920, 1080], ""objects"": []}"
                );
                model.Set("page", want["page"].Str);
                c.Ok("ctsim/pageModel", Args(("window", want["window"].Long), ("model", model)));
            }
            return task.Result;
        }

        private static JsonValue Fixture(string name) =>
            JsonValue.Parse(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Page", "Fixtures", name))
            );

        [ProjectFact(
            Needs = "engine/tests/CicodeSim.Tests/Page/Fixtures/ExampleSA_Master_PageMenu1_HD1080.page.json"
        )]
        public void AnSaContentPageIsShownInTheWorkspacePaneOfTheStartupPage()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.ExampleSA, clock: "run"))
            {
                // the computer's citect.ini may name another project's page: the test names its own
                sim.Invoke(() => sim.SetParam("MultiMonitors", "ScreenProfile", "OneMonitorHD"));
                sim.Invoke(() =>
                    sim.SetParam("MultiMonitors", "StartupPageScreen1", "Master_PageMenu1_HD1080")
                );
                var (server, disc) = Serve(sim);
                try
                {
                    using (var c = Connect(server.Listener!.Address))
                    {
                        var hello = c.Ok("ctsim/hello", Args(("client", "navigate"), ("models", true)));
                        Assert.True(hello["capabilities"]["navigate"].Bool);
                        var load = c.Ok("ctsim/load", Args(("startup", "auto"), ("startPage", true)), 60000);
                        var sp = load["startPage"];
                        _out.WriteLine(sp.ToString());
                        Assert.Equal("Master_PageMenu1_HD1080", sp["page"].Str);
                        Assert.Equal(0, sp["window"].Long);
                        Assert.Equal(0, sp["error"].Long);
                        Assert.StartsWith("[MultiMonitors] StartupPageScreen1", sp["source"].Str);
                        // a models viewer: the page waits for its drawing, the startup code for the page
                        Assert.True(sp["held"].Bool);
                        Assert.True(load["pending"].Bool);
                        var want = c.WaitEvent("ctsim/modelWanted", e => e["window"].Long == 0);
                        Assert.Equal("Master_PageMenu1_HD1080", want["page"].Str);
                        Assert.False(sim.Invoke(() => sim.Boot.Ran));
                        Assert.True(
                            c.Ok(
                                "ctsim/pageModel",
                                Args(
                                    ("window", 0),
                                    ("model", Fixture("ExampleSA_Master_PageMenu1_HD1080.page.json"))
                                )
                            )["started"].Bool
                        );
                        // the startup ran once the page started: the workspace opens its panes
                        SimTestUtil.Until(sim, () => sim.Boot.Ran, 10000, "the startup code");
                        var nav = AnsweringModels(c, () =>
                            c.Ok("ctsim/navigate", Args(("page", "DefaultAlarm_HD1080")), 60000)
                        );
                        _out.WriteLine(nav.ToString());
                        Assert.Equal("workspace", nav["method"].Str);
                        int pane = (int)nav["window"].Long;
                        Assert.True(pane > 0, nav.ToString());
                        Assert.Contains(nav["shownIn"].Items, x => x.Long == pane);
                        var wins = c.Ok("ctsim/windows")["windows"].Items.ToList();
                        Assert.Equal(
                            "Master_PageMenu1_HD1080",
                            wins.First(w => w["window"].Long == 0)["page"].Str
                        );
                        var content = wins.First(w => w["window"].Long == pane);
                        Assert.Equal("DefaultAlarm_HD1080", content["page"].Str);
                        Assert.Equal(0, content["parent"].Long);
                        // a page the workspace has no pane for: none shows it, the answer says so
                        // (the workspace waits for a pane that never comes: a fast clock)
                        sim.Invoke(() => sim.SetSpeed(50));
                        var none = AnsweringModels(c, () =>
                            c.Ok(
                                "ctsim/navigate",
                                Args(("page", "Master_PageMenu1_HD1080"), ("how", "workspace")),
                                60000
                            )
                        );
                        _out.WriteLine(none.ToString());
                        Assert.Contains(none["notes"].Items, n =>
                            n.Str.StartsWith("Workspace_ShowContent returned -1", StringComparison.Ordinal)
                        );
                        Assert.Contains(none["notes"].Items, n =>
                            n.Str.StartsWith("Workspace_ShowContent returned -1", StringComparison.Ordinal)
                        );
                    }
                }
                finally
                {
                    Done(server, disc);
                }
            }
        }

        [ProjectFact]
        public void AProjectWithoutAWorkspaceGetsPageDisplayAndItsStartupPage()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example, clock: "run"))
            {
                sim.Invoke(() => sim.SetParam("MultiMonitors", "StartupPageScreen1", "NoSuchPage"));
                sim.Invoke(() => sim.SetParam("Page", "Startup", "Mixer"));
                var (server, disc) = Serve(sim);
                try
                {
                    using (var c = Connect(server.Listener!.Address))
                    {
                        c.Ok("ctsim/hello", Args(("client", "navigate")));
                        var load = c.Ok("ctsim/load", Args(("startup", "auto"), ("startPage", true)), 60000);
                        var sp = load["startPage"];
                        _out.WriteLine(sp.ToString());
                        // StartupPageScreen1 names no page of the project: [Page] Startup
                        Assert.Equal("Mixer", sp["page"].Str);
                        Assert.False(sp["held"].Bool);
                        Assert.Contains(sp["notes"].Items, n => n.Str.Contains("NoSuchPage"));
                        Assert.Null(load.Get("pending"));
                        var nav = c.Ok("ctsim/navigate", Args(("page", "Genies")), 60000);
                        Assert.Equal("pageDisplay", nav["method"].Str);
                        Assert.Equal(0, nav["window"].Long);
                        Assert.Equal(
                            "Genies",
                            c.Ok("ctsim/windows")["windows"]
                                .Items.First(w => w["window"].Long == 0)["page"]
                                .Str
                        );
                        // a second load with a page in window 0: the startup page is not shown again
                        var again = c.Ok("ctsim/load", Args(("startPage", true)), 60000)["startPage"];
                        Assert.True(again["already"].Bool);
                        Assert.Equal("Genies", again["page"].Str);
                        // a page that does not exist: the open's error
                        var bad = c.Request("ctsim/navigate", Args(("page", "NoSuchPageAtAll")), 60000);
                        Assert.False(bad["success"].Bool);
                    }
                }
                finally
                {
                    Done(server, disc);
                }
            }
        }

        // the runtime shows the startup page, then runs the startup code
        // (the startup page first); the user's page comes after that

        [ProjectFact]
        public void APageTheUserGoesToShowsOnceTheStartupCodeHasSettled()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example, clock: "run", speed: 4.0))
            {
                sim.Invoke(() => sim.SetParam("MultiMonitors", "StartupPageScreen1", "NoSuchPage"));
                sim.Invoke(() => sim.SetParam("Page", "Startup", "Mixer"));
                var (server, disc) = Serve(sim);
                try
                {
                    using (var c = Connect(server.Listener!.Address))
                    {
                        c.Ok("ctsim/hello", Args(("client", "navigate")));
                        var load = c.Ok("ctsim/load", Args(("startup", "auto"), ("startPage", true)), 60000);
                        Assert.Equal("Mixer", load["startPage"]["page"].Str);
                        Assert.NotEmpty(load["startupTasks"].Items);
                        var nav = c.Ok(
                            "ctsim/navigate",
                            Args(("page", "Genies"), ("how", "pageDisplay")),
                            60000
                        );
                        _out.WriteLine(nav.ToString());
                        var st = nav["startup"];
                        Assert.Equal("settled", st["state"].Str);
                        Assert.True(st["simulatedMs"].Long >= RuntimeStartup.SettleMinMs, st.ToString());
                        Assert.Equal("pageDisplay", nav["method"].Str);
                        Assert.Equal(
                            "Genies",
                            c.Ok("ctsim/windows")["windows"]
                                .Items.First(w => w["window"].Long == 0)["page"]
                                .Str
                        );
                        // the page history holds the startup page, as the runtime's (a page's
                        // back button is enabled: PageHistoryEmpty() is 0)
                        var hist = sim.Invoke(() => sim.World.Display.Windows[0].History.ToList());
                        Assert.Contains(hist, h =>
                            string.Equals(h, "Mixer", StringComparison.OrdinalIgnoreCase)
                        );
                        // once settled a navigation does not wait
                        var again = c.Ok(
                            "ctsim/navigate",
                            Args(("page", "Mixer"), ("how", "pageDisplay")),
                            60000
                        );
                        Assert.Equal("settled", again["startup"]["state"].Str);
                        Assert.True(again["startup"]["waitedMs"].Long < 2000, again.ToString());
                    }
                }
                finally
                {
                    Done(server, disc);
                }
            }
        }

        [ProjectFact]
        public void TheStartupCodeRunsOnAFastClockUntilItSettles()
        {
            // speed 1: the startup's simulated seconds pass FastFactor times faster, then the
            // clock runs at its own speed again (the Speed a client reads never changes)
            using (var sim = SimTestUtil.Start(SimCorpus.Example, clock: "run", speed: 1.0))
            {
                sim.Invoke(() => sim.SetParam("MultiMonitors", "StartupPageScreen1", "NoSuchPage"));
                sim.Invoke(() => sim.SetParam("Page", "Startup", "Mixer"));
                var (server, disc) = Serve(sim);
                try
                {
                    using (var c = Connect(server.Listener!.Address))
                    {
                        c.Ok("ctsim/hello", Args(("client", "navigate")));
                        c.Ok("ctsim/load", Args(("startup", "auto"), ("startPage", true)), 60000);
                        var nav = c.Ok(
                            "ctsim/navigate",
                            Args(("page", "Genies"), ("how", "pageDisplay")),
                            60000
                        );
                        _out.WriteLine(nav.ToString());
                        var st = nav["startup"];
                        Assert.Equal("settled", st["state"].Str);
                        long sim_ms = st["simulatedMs"].Long,
                            real_ms = st["waitedMs"].Long;
                        Assert.True(sim_ms >= RuntimeStartup.SettleMinMs, st.ToString());
                        Assert.True(real_ms * 4 < sim_ms + 400, st.ToString());
                        // the boost ends with the startup (the loop's next Tick)
                        var end = DateTime.UtcNow.AddSeconds(5);
                        while (sim.Invoke(() => sim.World.Clock.Boost) != 1.0 && DateTime.UtcNow < end)
                            System.Threading.Thread.Sleep(20);
                        Assert.Equal(1.0, sim.Invoke(() => sim.World.Clock.Boost));
                        Assert.Equal(1.0, sim.Invoke(() => sim.World.Clock.Speed));
                    }
                }
                finally
                {
                    Done(server, disc);
                }
            }
        }

        [ProjectFact]
        public void FastStartupFalseRunsTheStartupCodeAtTheClocksSpeed()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example, clock: "run", speed: 1.0))
            {
                sim.Invoke(() => sim.SetParam("Page", "Startup", "Mixer"));
                var (server, disc) = Serve(sim);
                try
                {
                    using (var c = Connect(server.Listener!.Address))
                    {
                        c.Ok("ctsim/hello", Args(("client", "navigate")));
                        c.Ok(
                            "ctsim/load",
                            Args(("startup", "auto"), ("startPage", true), ("fastStartup", false)),
                            60000
                        );
                        Assert.Equal(1.0, sim.Invoke(() => sim.World.Clock.Boost));
                    }
                }
                finally
                {
                    Done(server, disc);
                }
            }
        }

        [ProjectFact]
        public void WithoutTheStartupCodeOrOnAPausedClockANavigationDoesNotWait()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            {
                var (server, disc) = Serve(sim);
                try
                {
                    using (var c = Connect(server.Listener!.Address))
                    {
                        c.Ok("ctsim/hello", Args(("client", "navigate")));
                        var nav = c.Ok(
                            "ctsim/navigate",
                            Args(("page", "Genies"), ("how", "pageDisplay")),
                            60000
                        );
                        Assert.Equal("notAsked", nav["startup"]["state"].Str);
                        // window 0 shows a page: the startup code starts at once
                        c.Ok("ctsim/load", Args(("startup", "auto")), 60000);
                        Assert.True(sim.Invoke(() => sim.Boot.Ran));
                        var off = c.Ok(
                            "ctsim/navigate",
                            Args(("page", "Mixer"), ("how", "pageDisplay"), ("afterStartup", false)),
                            60000
                        );
                        Assert.Equal("notWaited", off["startup"]["state"].Str);
                        // the clock is paused: what can run has run, the rest sleeps
                        var paused = c.Ok(
                            "ctsim/navigate",
                            Args(("page", "Genies"), ("how", "pageDisplay")),
                            60000
                        );
                        _out.WriteLine(paused.ToString());
                        Assert.Contains(paused["startup"]["state"].Str, new[] { "paused", "settled" });
                        Assert.True(paused["startup"]["waitedMs"].Long < 10000, paused.ToString());
                    }
                }
                finally
                {
                    Done(server, disc);
                }
            }
        }
    }
}
