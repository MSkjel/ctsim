using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CicodeSim.Compiler.Codegen;
using CicodeSim.Host;
using CicodeSim.Sim;
using CicodeSim.Values;
using Xunit;
using Xunit.Abstractions;
using static CicodeSim.Tests.Host.ProtocolClient;

namespace CicodeSim.Tests.Host
{
    /// <summary>The page requests and events of the server (engine/PROTOCOL.md, Pages): the
    /// page animator over the simulation's VM on the R2 Example / ExampleSA projects (read
    /// only), in process and over the protocol.</summary>
    [Collection("ctsim server")]
    public sealed class PageServerTests
    {
        private readonly ITestOutputHelper _out;

        public PageServerTests(ITestOutputHelper output)
        {
            _out = output;
        }

        /// <summary>WaitEvent that says what did come when the event does not.</summary>
        private static JsonValue Wait(
            ProtocolClient c,
            string name,
            Func<JsonValue, bool> match,
            int timeoutMs = 20000
        )
        {
            try
            {
                return c.WaitEvent(name, match, timeoutMs);
            }
            catch (TimeoutException e)
            {
                var seen = new List<string>();
                lock (c.Received)
                    foreach (var m in c.Received)
                        if (m.Get("type")?.Str == "event")
                        {
                            string t = m["event"].Str;
                            if (t != "ctsim/pageChanged")
                                t += " " + m["body"];
                            seen.Add(t.Length > 400 ? t.Substring(0, 400) : t);
                        }
                var counts = seen.GroupBy(x => x.Split(' ')[0]).Select(g => g.Key + " x" + g.Count());
                throw new TimeoutException(
                    e.Message
                        + "; received: "
                        + string.Join(", ", counts)
                        + "; windows: "
                        + string.Join(
                            " | ",
                            seen.Where(x => x.StartsWith("ctsim/windowsChanged", StringComparison.Ordinal))
                        )
                        + "; other: "
                        + string.Join(
                            " | ",
                            seen.Where(x => !x.StartsWith("ctsim/log", StringComparison.Ordinal)).Take(60)
                        )
                        + "; logs: "
                        + string.Join(
                            " | ",
                            seen.Where(x => x.StartsWith("ctsim/log", StringComparison.Ordinal)).Take(30)
                        )
                );
            }
        }

        private static JsonValue ObjectsOf(JsonValue page)
        {
            var o = JsonValue.NewObject();
            foreach (var e in page["objects"].Items)
                o.Set(e["an"].Long.ToString(System.Globalization.CultureInfo.InvariantCulture), e["state"]);
            return o;
        }

        private static (JsonValue tables, JsonValue control) RowsJson(string project, string page)
        {
            var (tables, control) = PageRowCompiler.ProjectRows(project, page);
            var t = JsonValue.NewObject();
            foreach (var kv in tables)
            {
                var arr = JsonValue.NewArray();
                foreach (var r in kv.Value)
                {
                    var row = JsonValue.NewObject();
                    foreach (var c in r)
                        row.Set(c.Key, c.Value);
                    arr.Add(row);
                }
                t.Set(kv.Key, arr);
            }
            var ctl = JsonValue.NewObject();
            if (control != null)
                foreach (var c in control)
                    ctl.Set(c.Key, c.Value);
            return (t, ctl);
        }

        /// <summary>A page compiled from its pg*.DBF rows (the rows ctgedit sends for an unsaved
        /// page) animates exactly as its CtCmp32 .RDB: every object state equal after the open and
        /// two seconds of scans with the same tags (pages whose stored rows are current).</summary>
        [ProjectFact]
        public void ARowsCompiledPageAnimatesAsItsRdb()
        {
            var cases = new[]
            {
                (SimCorpus.Example, "Mixer", "Mixer_Sugar", 42.0),
                (SimCorpus.Example, "Dynamic_1", "V_Movement", 37.0),
                (SimCorpus.ExampleSA, "Mixing_L3", "TopMilk_TNK20_PV", 61.0),
            };
            foreach (var (project, page, tag, value) in cases)
            {
                JsonValue Run(bool rows)
                {
                    using (var sim = SimTestUtil.Start(project))
                    {
                        var notes = new List<string>();
                        var cp = rows ? sim.Pages.CompileRows(page, null, notes) : null;
                        if (rows)
                            Assert.NotNull(cp);
                        foreach (string n in notes)
                            _out.WriteLine(page + ": " + n);
                        var open = sim.Invoke(() =>
                            sim.Pages.Open(page, 0, null, cp, null, rows ? "projectRows" : "rdb", notes)
                        );
                        Assert.Equal(0, open["error"].Long);
                        // the entry task first (Dynamic_1's DynamicResetAll writes the tags), then the tag
                        sim.Invoke(() => sim.StepClock(500));
                        sim.Invoke(() => sim.SetTag(tag, JsonValue.Of(value)));
                        sim.Invoke(() => sim.StepClock(2000));
                        return sim.Invoke(() => sim.Pages.StateJson(0));
                    }
                }
                var a = ObjectsOf(Run(false));
                var b = ObjectsOf(Run(true));
                var diffs = new List<string>();
                foreach (var kv in a.Pairs)
                {
                    var other = b.Get(kv.Key);
                    if (other == null)
                        diffs.Add("AN " + kv.Key + " only from the RDB");
                    else if (other.ToString() != kv.Value.ToString())
                        diffs.Add("AN " + kv.Key + ": " + kv.Value + " vs " + other);
                }
                foreach (var kv in b.Pairs)
                    if (a.Get(kv.Key) == null)
                        diffs.Add("AN " + kv.Key + " only from the rows");
                _out.WriteLine(page + ": " + a.Count + " objects, " + diffs.Count + " differ");
                foreach (string d in diffs.Take(10))
                    _out.WriteLine("  " + d);
                Assert.True(a.Count > 10, page + ": too few objects");
                Assert.Empty(diffs);
            }
        }

        /// <summary>Page requests over the protocol.</summary>
        [ProjectFact]
        public void PagesOverTheProtocol()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            using (var server = new SimServer(sim, "pages-" + Guid.NewGuid().ToString("N").Substring(0, 8)))
            {
                string disc = Path.Combine(Path.GetTempPath(), "ctsim-pages-" + Guid.NewGuid().ToString("N"));
                try
                {
                    server.Listen(disc);
                    using (var c = Connect(server.Listener!.Address))
                    {
                        var h = c.Ok("ctsim/hello", Args(("client", "pages")));
                        Assert.True(h["capabilities"]["pages"].Bool);
                        // the build's identity (PROTOCOL.md 'Versions')
                        Assert.Equal(EngineInfo.ProtocolRevision, (int)h["protocolRevision"].Long);
                        Assert.Equal(EngineInfo.Commit, h["commit"].Str);
                        Assert.Equal(EngineInfo.BuildTime, h["buildTime"].Str);
                        var op = c.Ok("ctsim/openPage", Args(("name", "Mixer")));
                        Assert.Equal(0, op["window"].Long);
                        Assert.Equal("Mixer", op["page"].Str);
                        Assert.Equal("rdb", op["source"].Str);
                        var first = ObjectsOf(op["state"]);
                        Assert.True(first.Count > 100);
                        var w0 = Wait(c, "ctsim/windowsChanged", e =>
                            e["windows"].Items.Any(w => w["window"].Long == 0 && w["page"].Str == "Mixer")
                        );
                        Assert.False(w0["windows"].Items.First(w => w["window"].Long == 0)["hasModel"].Bool);
                        // a tag the page shows: the next scan sends that object, not the page
                        c.Ok("ctsim/setTag", Args(("name", "Mixer_Sugar"), ("value", 42.5)));
                        c.Ok("ctsim/clock", Args(("mode", "step"), ("value", 0.5)));
                        var ch = Wait(c, "ctsim/pageChanged", e =>
                            e["window"].Long == 0 && e["objects"].Items.Any(o => o["an"].Long == 542)
                        );
                        Assert.Equal("Mixer", ch["page"].Str);
                        Assert.True(ch["objects"].Count < first.Count);
                        var sugar = ch["objects"].Items.First(o => o["an"].Long == 542)["state"];
                        Assert.Contains("42", sugar["text"].Str);
                        Assert.True(ch.Get("flashOn") != null && ch.Get("serial") != null);
                        // the full state says the same
                        var ps = c.Ok("ctsim/pageState", Args(("window", 0)));
                        Assert.Equal(sugar.ToString(), ObjectsOf(ps)["542"].ToString());
                        // a click on a power meter: its touch command runs (AssMetaDataPopup)
                        var click = c.Ok(
                            "ctsim/input",
                            Args(("window", 0), ("an", 556), ("kind", "click"), ("x", 100), ("y", 100))
                        );
                        Assert.True(click["ran"].Bool);
                        Assert.Equal(556, click["an"].Long);
                        // a popup (what WinNewAt does for AssPopUp): its own window, its own states,
                        // its drawing summary given afterwards, freed by closePage
                        int popup = sim.Invoke(() =>
                            sim.Pages.Animator!.NewWindow("!EnergyStartStop", 40, 50, 1, 0)
                        );
                        Assert.True(popup > 0);
                        var pop = Wait(c, "ctsim/windowsChanged", e =>
                            e["windows"].Items.Any(w => w["window"].Long == popup)
                        );
                        var pw = pop["windows"].Items.First(w => w["window"].Long == popup);
                        Assert.Equal("!EnergyStartStop", pw["page"].Str, ignoreCase: true);
                        Assert.Equal(0, pw["parent"].Long);
                        Assert.Equal(40, pw["x"].Long);
                        Assert.False(pw["hasModel"].Bool);
                        Assert.EndsWith(".ctg", pw["path"].Str, StringComparison.OrdinalIgnoreCase);
                        Assert.True(c.Ok("ctsim/pageState", Args(("window", popup)))["objects"].Count > 0);
                        var pm = JsonValue.Parse(
                            @"{""format"": ""ctsim-page/0"", ""page"": ""!EnergyStartStop"", ""size"": [400, 300], ""objects"": []}"
                        );
                        Assert.Equal(
                            popup,
                            c.Ok("ctsim/pageModel", Args(("window", popup), ("model", pm)))["window"].Long
                        );
                        Wait(c, "ctsim/windowsChanged", e =>
                            e["windows"].Items.Any(w => w["window"].Long == popup && w["hasModel"].Bool)
                        );
                        Assert.False(
                            c.Request(
                                "ctsim/pageModel",
                                Args(("window", popup), ("model", JsonValue.Parse(@"{""page"": ""Other""}")))
                            )["success"].Bool
                        );
                        Assert.True(c.Ok("ctsim/closePage", Args(("window", popup)))["closed"].Bool);
                        Wait(c, "ctsim/windowsChanged", e =>
                            !e["windows"].Items.Any(w => w["window"].Long == popup)
                        );
                        Assert.Contains(c.Ok("ctsim/windows")["windows"].Items, w => w["window"].Long == 0);
                        // a key on an object with a keyboard command (ENTER on AN 391)
                        var key = c.Ok(
                            "ctsim/input",
                            Args(("window", 0), ("an", 391), ("kind", "key"), ("key", "Return"))
                        );
                        Assert.True(key["ran"].Bool);
                        Assert.False(
                            c.Request(
                                "ctsim/input",
                                Args(("window", 0), ("an", 391), ("kind", "key"), ("key", "NoSuchKeyName"))
                            )["success"].Bool
                        );
                        // a click that changes the page of window 0 (PageDisplay: Include Cicode over
                        // _PageDisplay, a display built-in; checked once it is simulated)
                        Assert.True(
                            c.Ok("ctsim/input", Args(("window", 0), ("an", 624), ("kind", "click")))[
                                "ran"
                            ].Bool
                        );
                        c.Ok("ctsim/clock", Args(("mode", "step"), ("value", 0.5)));
                        string? shown = sim.Invoke(() => sim.World.Display.Windows[0].Page?.Name);
                        var missing = sim.Invoke(() =>
                            sim
                                .Vm.NotSimulatedCounts.Keys.Where(k =>
                                    k.IndexOf("PageDisplay", StringComparison.OrdinalIgnoreCase) >= 0
                                )
                                .ToList()
                        );
                        if (shown == "ProcessLine" || missing.Count == 0)
                        {
                            Wait(c, "ctsim/windowsChanged", e =>
                                e["windows"]
                                    .Items.Any(w => w["window"].Long == 0 && w["page"].Str == "ProcessLine")
                            );
                            Wait(c, "ctsim/pageChanged", e =>
                                e["window"].Long == 0 && e["page"].Str == "ProcessLine"
                            );
                        }
                        else
                            _out.WriteLine(
                                "not simulated yet: "
                                    + string.Join(", ", missing)
                                    + ": the page change is not checked"
                            );
                        // a second page goes to the next free window
                        var op2 = c.Ok("ctsim/openPage", Args(("name", "Dynamic_1")));
                        Assert.NotEqual(0, op2["window"].Long);
                        Assert.False(
                            c.Request("ctsim/openPage", Args(("name", "NoSuchPageXyz")))["success"].Bool
                        );
                        Assert.True(
                            c.Ok("ctsim/closePage", Args(("window", op2["window"].Long)))["closed"].Bool
                        );
                        Assert.True(c.Ok("ctsim/closePage", Args(("window", 0)))["closed"].Bool);
                        Assert.Equal("", c.Ok("ctsim/pageState", Args(("window", 0)))["page"].Str);
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

        /// <summary>An unsaved page: its model's rows run, not its .RDB (a touch command added
        /// in the editor writes a tag when clicked); its drawing summary feeds the classes (a
        /// rotation origin from the object's rect). The tags it reads are listed from that code.</summary>
        [ProjectFact]
        public void AnUnsavedPageRunsItsModelsRows()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            using (var server = new SimServer(sim, "pages-" + Guid.NewGuid().ToString("N").Substring(0, 8)))
            {
                server.Listen(
                    Path.Combine(Path.GetTempPath(), "ctsim-pages-" + Guid.NewGuid().ToString("N"))
                );
                using (var c = Connect(server.Listener!.Address))
                {
                    c.Ok("ctsim/hello", Args(("client", "unsaved")));
                    var (tables, control) = RowsJson(SimCorpus.Example, "Mixer");
                    // the edit: a touch command on the sugar text that writes a tag
                    var touch = JsonValue.NewObject();
                    touch.Set("PAGE", "Mixer");
                    touch.Set("AN", "542");
                    touch.Set("ACTION", "KEY_LBUTTON");
                    touch.Set("CMD", "Mixer_Malt = 77");
                    var pgtouch = tables.Get("pgtouch") ?? JsonValue.NewArray();
                    pgtouch.Add(touch);
                    tables.Set("pgtouch", pgtouch);
                    var model = JsonValue.Parse(
                        @"{""format"": ""ctsim-page/0"", ""project"": ""Example"", ""page"": ""Mixer"", ""source"": ""unsaved"",
                        ""compiled"": {""rdb"": true, ""rdbNewer"": true, ""rowsMatch"": false, ""current"": false},
                        ""size"": [1366, 768], ""objects"": [{""an"": 542, ""type"": 5, ""rect"": [100, 200, 160, 220], ""parent"": 0}]}"
                    );
                    model.Set("tables", tables);
                    model.Set("control", control);
                    var op = c.Ok("ctsim/openPage", Args(("name", "Mixer"), ("model", model)));
                    Assert.Equal("rows", op["source"].Str);
                    Assert.Contains(op["notes"].Items, n =>
                        n.Str.StartsWith("compiled from its rows", StringComparison.Ordinal)
                    );
                    var w0 = Wait(c, "ctsim/windowsChanged", e =>
                        e["windows"].Items.Any(w => w["window"].Long == 0 && w["page"].Str == "Mixer")
                    );
                    Assert.True(w0["windows"].Items.First(w => w["window"].Long == 0)["hasModel"].Bool);
                    Assert.True(
                        c.Ok(
                            "ctsim/input",
                            Args(("window", 0), ("an", 542), ("ans", Names()), ("kind", "click"))
                        )["ran"].Bool
                    );
                    c.Ok("ctsim/clock", Args(("mode", "step"), ("value", 0.5)));
                    var t = c.Ok("ctsim/subscribe", Args(("names", Names("Mixer_Malt"))));
                    Assert.Equal(77.0, t["tags"].Items.Single()["value"].Double);
                    // the page's tags: the code it runs (the rows), which reads Mixer_Malt too
                    var pt = c.Ok("ctsim/tags", Args(("page", "Mixer")));
                    Assert.Contains(pt["tags"].Items, x => x["name"].Str == "Mixer_Malt");
                    // the edited page displayed again by Cicode (PageDisplay) still runs its rows
                    var call = c.Ok("ctsim/call", Args(("expr", "PageDisplay(\"Mixer\")")));
                    c.Ok("ctsim/clock", Args(("mode", "step"), ("value", 0.5)));
                    Assert.True(
                        c.Ok("ctsim/input", Args(("window", 0), ("an", 542), ("kind", "click")))["ran"].Bool
                    );
                }
            }
        }

        /// <summary>ctsim/call {window} runs the call's task in that display window, as CtAPI
        /// ctCicode's hWin: a PageSetStr there sets the window's page variable, not window 0's
        /// (the conformance harness replays the recorded calls in the page's window).</summary>
        [ProjectFact]
        public void ACallRunsInTheWindowItNames()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            using (var server = new SimServer(sim, "pages-" + Guid.NewGuid().ToString("N").Substring(0, 8)))
            {
                server.Listen(
                    Path.Combine(Path.GetTempPath(), "ctsim-pages-" + Guid.NewGuid().ToString("N"))
                );
                using (var c = Connect(server.Listener!.Address))
                {
                    c.Ok("ctsim/hello", Args(("client", "callwindow")));
                    Assert.Equal(
                        0,
                        c.Ok("ctsim/openPage", Args(("name", "Mixer"), ("window", 0)))["window"].Long
                    );
                    int w = (int)c.Ok("ctsim/openPage", Args(("name", "Dynamic_1")))["window"].Long;
                    Assert.NotEqual(0, w);
                    c.Ok("ctsim/clock", Args(("mode", "step"), ("value", 0.5)));
                    c.Ok("ctsim/call", Args(("expr", "PageSetStr(\"CallWin\", \"in w\")"), ("window", w)));
                    Assert.Equal(
                        "in w",
                        c.Ok("ctsim/call", Args(("expr", "PageGetStr(\"CallWin\")"), ("window", w)))[
                            "result"
                        ].Str
                    );
                    Assert.Equal(
                        "",
                        c.Ok("ctsim/call", Args(("expr", "PageGetStr(\"CallWin\")")))["result"].Str
                    );
                    Assert.Equal(
                        "",
                        c.Ok("ctsim/call", Args(("expr", "PageGetStr(\"CallWin\")"), ("window", 0)))[
                            "result"
                        ].Str
                    );
                }
            }
        }

        /// <summary>Only a plain left button runs touch commands.</summary>
        [ProjectFact]
        public void OnlyAPlainLeftButtonRunsTouchCommands()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            {
                JsonValue In(int window, string json) =>
                    sim.Invoke(() => sim.Pages.Input(window, JsonValue.Parse(json)));
                Assert.Equal(
                    0,
                    sim.Invoke(() => sim.Pages.Open("Mixer", 0, null, null, null, "rdb", new List<string>()))[
                        "error"
                    ].Long
                );
                sim.Invoke(() => sim.StepClock(500));
                Assert.False(
                    In(
                        0,
                        @"{""an"": 556, ""kind"": ""click"", ""button"": ""right"", ""x"": 100, ""y"": 100}"
                    )["ran"].Bool
                );
                Assert.False(
                    In(0, @"{""an"": 556, ""kind"": ""click"", ""button"": ""middle""}")["ran"].Bool
                );
                Assert.False(In(0, @"{""an"": 556, ""kind"": ""click"", ""modifiers"": 1}")["ran"].Bool);
                Assert.False(In(0, @"{""an"": 556, ""kind"": ""click"", ""modifiers"": 2}")["ran"].Bool);
                Assert.True(
                    In(0, @"{""an"": 556, ""kind"": ""click"", ""button"": ""left"", ""modifiers"": 4}")[
                        "ran"
                    ].Bool
                );
                Assert.Throws<ArgumentException>(() =>
                    In(0, @"{""an"": 556, ""kind"": ""click"", ""button"": ""fourth""}")
                );
                var open = sim.Invoke(() =>
                    sim.Pages.Open("Dynamic_1", null, null, null, null, "rdb", new List<string>())
                );
                Assert.Equal(0, open["error"].Long);
                int w = (int)open["window"].Long;
                sim.Invoke(() => sim.StepClock(500));
                int slider = sim.Invoke(() => sim.Pages.Animator!.Run(w)!.Objects.First(o => o.SliderX).An);
                var right = In(
                    w,
                    "{\"an\": "
                        + slider
                        + ", \"kind\": \"down\", \"button\": \"right\", \"x\": 10, \"y\": 10}"
                );
                Assert.False(right["ran"].Bool);
                var drag = In(
                    w,
                    "{\"an\": "
                        + slider
                        + ", \"kind\": \"drag\", \"button\": \"right\", \"x\": 60, \"y\": 10, \"dx\": 50, \"dy\": 0}"
                );
                Assert.False(drag["ran"].Bool);
                Assert.Equal(0, drag["an"].Long);
                // the left button drags it
                In(w, "{\"an\": " + slider + ", \"kind\": \"down\", \"x\": 10, \"y\": 10}");
                Assert.Equal(
                    slider,
                    In(
                        w,
                        "{\"an\": "
                            + slider
                            + ", \"kind\": \"drag\", \"x\": 40, \"y\": 10, \"dx\": 30, \"dy\": 0}"
                    )["an"].Long
                );
                In(
                    w,
                    "{\"an\": "
                        + slider
                        + ", \"kind\": \"up\", \"modifiers\": 1, \"x\": 40, \"y\": 10, \"dx\": 30, \"dy\": 0}"
                );
            }
        }

        /// <summary>Failing loops don't flood the viewers.</summary>
        [ProjectFact]
        public void FailingLoopsDoNotFloodTheViewers()
        {
            foreach (
                var (project, page, an) in new[]
                {
                    (SimCorpus.ExampleSA, "Meters", 85),
                    (SimCorpus.Example, "Mixer", 556),
                }
            )
            {
                using (var sim = SimTestUtil.Start(project))
                using (
                    var server = new SimServer(sim, "flood-" + Guid.NewGuid().ToString("N").Substring(0, 8))
                )
                {
                    server.Listen(
                        Path.Combine(Path.GetTempPath(), "ctsim-flood-" + Guid.NewGuid().ToString("N"))
                    );
                    using (var c = Connect(server.Listener!.Address))
                    {
                        c.Ok("ctsim/hello", Args(("client", "flood")));
                        c.Ok("ctsim/openPage", Args(("name", page)));
                        c.Ok("ctsim/clock", Args(("mode", "step"), ("value", 1)));
                        c.Ok(
                            "ctsim/input",
                            Args(("window", 0), ("an", an), ("kind", "click"), ("x", 10), ("y", 10))
                        );
                        c.Ok("ctsim/clock", Args(("mode", "step"), ("value", 2)));
                        // the connection still answers
                        Assert.Equal(
                            page,
                            c.Ok("ctsim/pageState", Args(("window", 0)))["page"].Str,
                            ignoreCase: true
                        );
                        int logs;
                        lock (c.Received)
                            logs = c.Received.Count(m => m.Get("event")?.Str == "ctsim/log");
                        _out.WriteLine(page + " AN " + an + ": " + logs + " log events");
                        Assert.True(logs < 2000, page + ": " + logs + " log events");
                    }
                }
            }
        }

        /// <summary>Closing a page runs its close events: EXIT_FUNC in the foreground (Example
        /// !PID: PIDOnPageExit), traced as the page's exit.</summary>
        [ProjectFact]
        public void ClosePageRunsTheExitEvents()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            {
                var open = sim.Invoke(() =>
                    sim.Pages.Open("!PID", null, null, null, null, "rdb", new List<string>())
                );
                Assert.Equal(0, open["error"].Long);
                int w = (int)open["window"].Long;
                sim.Invoke(() => sim.StepClock(500));
                var closed = sim.Invoke(() => sim.Pages.Close(w));
                Assert.True(closed["closed"].Bool);
                var events = sim.Invoke(() =>
                    sim.Pages.Animator!.Events.ConvertAll(e => e.Event + " " + e.Page)
                );
                Assert.Contains("entry !PID", events);
                Assert.Contains("exit !PID", events);
                Assert.True(events.IndexOf("exit !PID") > events.IndexOf("entry !PID"));
            }
        }
    }
}
