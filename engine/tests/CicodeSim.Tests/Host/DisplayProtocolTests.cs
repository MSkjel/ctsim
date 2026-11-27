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
    /// <summary>The viewer side of the protocol on the Example project.</summary>
    [Collection("ctsim server")]
    public sealed class DisplayProtocolTests
    {
        private readonly ITestOutputHelper _out;

        public DisplayProtocolTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static (SimServer server, string disc) Serve(Simulation sim)
        {
            var server = new SimServer(sim, "display-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string disc = Path.Combine(Path.GetTempPath(), "ctsim-display-" + Guid.NewGuid().ToString("N"));
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

        [ProjectFact]
        public void PageStringsAreLocalisedForAViewer()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            {
                var (server, disc) = Serve(sim);
                try
                {
                    using (var c = Connect(server.Listener!.Address))
                    {
                        var h = c.Ok("ctsim/hello", Args(("client", "display")));
                        Assert.True(h["capabilities"]["displayText"].Bool);
                        Assert.Equal(EngineInfo.ProtocolRevision, (int)h["protocolRevision"].Long);
                        var op = c.Ok("ctsim/openPage", Args(("name", "Mixer")));
                        var texts = op["state"]
                            ["objects"]
                            .Items.Select(o => o["state"].Get("text"))
                            .Where(t => t != null && !t.IsNull)
                            .Select(t => t!.Str)
                            .ToList();
                        _out.WriteLine(string.Join(" | ", texts.Take(40)));
                        Assert.Contains("Menu", texts);
                        Assert.DoesNotContain(texts, t => t.Contains("@("));
                        // the engine's own state keeps the native markup (what Cicode reads)
                        var raw = sim.Invoke(() => sim.Pages.Animator!.State(0)!.ToJson());
                        Assert.Contains(raw["objects"].Items, o =>
                            (o["state"].Get("text")?.Kind == JsonKind.String)
                            && o["state"]["text"].Str.Contains("@(Menu)")
                        );
                    }
                }
                finally
                {
                    Done(server, disc);
                }
            }
        }

        /// <summary>A text Cicode draws on the page (DspText) reaches the viewer localised, with
        /// its font as fontInfo and the full text for a pixel length.</summary>
        [ProjectFact]
        public void DrawnTextsCarryTheirFonts()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            {
                var (server, disc) = Serve(sim);
                try
                {
                    using (var c = Connect(server.Listener!.Address))
                    {
                        c.Ok("ctsim/hello", Args(("client", "drawn")));
                        c.Ok("ctsim/openPage", Args(("name", "Mixer")));
                        var r = c.Ok(
                            "ctsim/call",
                            Args(("expr", "DspText(222, -1, \"@(Menu) and more\", 30, 0, 1)"))
                        );
                        Assert.Equal("0", r["result"].Str);
                        c.Ok("ctsim/clock", Args(("mode", "step"), ("value", 0.5)));
                        var ch = c.WaitEvent("ctsim/pageChanged", e =>
                            e.Get("drawn") != null && e["drawn"].Items.Any(d => d["an"].Long == 222)
                        );
                        var d222 = ch["drawn"].Items.First(d => d["an"].Long == 222);
                        _out.WriteLine(d222.ToString());
                        Assert.Equal("Menu and more", d222["text"].Str);
                        Assert.Equal((30L, 1L), (d222["args"]["length"].Long, d222["args"]["trunc"].Long));
                        var fi = d222["fontInfo"];
                        Assert.Equal(0, fi["handle"].Long);
                        Assert.True(fi["face"].Str.Length > 0 && fi["height"].Long > 0);
                    }
                }
                finally
                {
                    Done(server, disc);
                }
            }
        }

        /// <summary>ctsim/checkExpression: a page field compiled, nothing run; the
        /// parser's errors with code and column, the code generation's as message only.</summary>
        [ProjectFact]
        public void APageFieldIsCheckedWithoutRunning()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example, clock: "paused"))
            {
                var (server, disc) = Serve(sim);
                try
                {
                    using (var c = Connect(server.Listener!.Address))
                    {
                        var hello = c.Ok("ctsim/hello", Args(("client", "check")));
                        Assert.True(hello["capabilities"]["checkExpression"].Bool);
                        Assert.True(hello["capabilities"]["checkFunctions"].Bool);
                        var fns = c.Ok("ctsim/checkFunctions");
                        Assert.Equal(0, fns["errors"].Count);
                        Assert.Equal(0, fns["warnings"].Count);
                        var ok = c.Ok("ctsim/checkExpression", Args(("text", "IntToStr(1 + 2)")));
                        Assert.True(ok["ok"].Bool);
                        Assert.Equal(0, ok["errors"].Count);
                        var bad = c.Ok("ctsim/checkExpression", Args(("text", "IntToStr(1 + )")));
                        Assert.False(bad["ok"].Bool);
                        var e = bad["errors"].Items.First();
                        Assert.True(e["code"].Long >= 2000);
                        Assert.StartsWith("E", e["name"].Str);
                        Assert.True(e["message"].Str.Length > 0);
                        Assert.Equal(1, e["line"].Long);
                        // a name the project does not define compiles as a tag (the runtime's
                        // error 424 comes when the page runs): a warning
                        var unknown = c.Ok("ctsim/checkExpression", Args(("text", "NoSuchTag_x + 1")));
                        Assert.True(unknown["ok"].Bool);
                        Assert.Contains("NoSuchTag_x", unknown["warnings"].Items.First()["message"].Str);
                        Assert.Equal(0, ok["warnings"].Count);
                        // a command: statements
                        var cmd = c.Ok(
                            "ctsim/checkExpression",
                            Args(("text", "PageDisplay(\"Mixer\");"), ("kind", "command"))
                        );
                        Assert.True(cmd["ok"].Bool);
                        Assert.False(
                            c.Request("ctsim/checkExpression", Args(("text", "1"), ("kind", "maybe")))[
                                "success"
                            ].Bool
                        );
                    }
                }
                finally
                {
                    Done(server, disc);
                }
            }
        }

        /// <summary>ctsim/options dll / sql: off by default; ask sends a permission
        /// question to the viewer that shows dialogs; 'always' puts the library on the whitelist
        /// and tells the viewers; off again frees what was loaded.</summary>
        [ProjectFact]
        public void TheSimulationAsksBeforeItLoadsALibrary()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example, clock: "run"))
            {
                var (server, disc) = Serve(sim);
                try
                {
                    using (var c = Connect(server.Listener!.Address))
                    {
                        var hello = c.Ok("ctsim/hello", Args(("client", "outside"), ("dialogs", true)));
                        Assert.True(hello["capabilities"]["outside"].Bool);
                        Assert.Equal(
                            ("off", "off"),
                            (hello["options"]["dll"].Str, hello["options"]["sql"].Str)
                        );
                        Assert.Equal(0, hello["options"]["dllAllowed"].Count);
                        const string open = "DLLOpen(\"kernel32.dll\", \"lstrlenA\", \"JC\")";
                        Assert.Equal("-1", c.Ok("ctsim/call", Args(("expr", open)))["result"].Str);
                        Assert.False(c.Request("ctsim/options", Args(("dll", "maybe")))["success"].Bool);
                        // one bad key changes nothing, the DLL time limit is an option
                        Assert.False(
                            c.Request("ctsim/options", Args(("dll", "on"), ("sql", "maybe")))["success"].Bool
                        );
                        Assert.Equal("off", c.Ok("ctsim/options", Args())["dll"].Str);
                        Assert.False(c.Request("ctsim/options", Args(("dllTimeout", -1)))["success"].Bool);
                        Assert.Equal(5, c.Ok("ctsim/options", Args(("dllTimeout", 5)))["dllTimeout"].Long);
                        Assert.Equal(5, c.WaitEvent("ctsim/optionsChanged")["dllTimeout"].Long);
                        var opt = c.Ok(
                            "ctsim/options",
                            Args(
                                ("dll", "ask"),
                                ("sqlAllowed", JsonValue.Array(new[] { JsonValue.Of("DSN=Plant") }))
                            )
                        );
                        Assert.Equal(("ask", "off"), (opt["dll"].Str, opt["sql"].Str));
                        Assert.Equal("[\"DSN=Plant\"]", opt["sqlAllowed"].ToString());
                        Assert.Equal("ask", c.WaitEvent("ctsim/optionsChanged")["dll"].Str);
                        // denied
                        var call = Task.Run(() =>
                            c.Request("ctsim/call", Args(("expr", open), ("timeoutMs", 30000)), 40000)
                        );
                        var d = c.WaitEvent("ctsim/dialog", e => e["kind"].Str == "permission");
                        _out.WriteLine(d.ToString());
                        Assert.Equal(
                            ("dll", "load", "lstrlenA"),
                            (d["access"].Str, d["action"].Str, d["detail"].Str)
                        );
                        Assert.EndsWith("kernel32.dll", d["target"].Str, StringComparison.OrdinalIgnoreCase);
                        Assert.Equal("[\"once\",\"session\",\"always\",\"deny\"]", d["choices"].ToString());
                        c.Ok("ctsim/answer", Args(("id", d["id"].Long), ("permission", "deny")));
                        Assert.Equal("-1", call.Result["body"]["result"].Str);
                        // always: loaded, and on the whitelist for every viewer
                        call = Task.Run(() =>
                            c.Request("ctsim/call", Args(("expr", open), ("timeoutMs", 30000)), 40000)
                        );
                        long first = d["id"].Long;
                        d = c.WaitEvent("ctsim/dialog", e =>
                            e["kind"].Str == "permission" && e["id"].Long != first
                        );
                        c.Ok("ctsim/answer", Args(("id", d["id"].Long), ("permission", "always")));
                        string handle = call.Result["body"]["result"].Str;
                        Assert.True(int.Parse(handle) >= 0, call.Result.ToString());
                        var ch = c.WaitEvent("ctsim/optionsChanged", e => e["dllAllowed"].Count == 1);
                        Assert.Equal(d["target"].Str, ch["dllAllowed"].Items[0].Str);
                        Assert.Equal(
                            "5",
                            c.Ok("ctsim/call", Args(("expr", "DLLCall(" + handle + ", \"^\"hello^\"\")")))[
                                "result"
                            ].Str
                        );
                        // off again: nothing stays loaded
                        c.Ok("ctsim/options", Args(("dll", "off")));
                        Assert.Equal(
                            "",
                            c.Ok("ctsim/call", Args(("expr", "DLLCall(" + handle + ", \"^\"hello^\"\")")))[
                                "result"
                            ].Str
                        );
                        Assert.Equal(1, c.Ok("ctsim/options", Args())["dllAllowed"].Count);
                    }
                }
                finally
                {
                    Done(server, disc);
                }
            }
        }

        [ProjectFact]
        public void DialogsGoToTheViewerThatShowsThem()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example, clock: "run"))
            {
                var (server, disc) = Serve(sim);
                try
                {
                    using (var c = Connect(server.Listener!.Address))
                    {
                        c.Ok("ctsim/hello", Args(("client", "dialogs"), ("dialogs", true)));
                        c.Ok("ctsim/openPage", Args(("name", "Mixer")));
                        // a message box: answered OK
                        var call = Task.Run(() =>
                            c.Request(
                                "ctsim/call",
                                Args(("expr", "Message(\"Title\", \"Hello\", 1)"), ("timeoutMs", 30000)),
                                40000
                            )
                        );
                        var d = c.WaitEvent("ctsim/dialog", e => e["kind"].Str == "message");
                        Assert.Equal(("Title", "Hello"), (d["title"].Str, d["text"].Str));
                        Assert.Equal("[\"OK\",\"Cancel\"]", d["buttons"].ToString());
                        long id = d["id"].Long;
                        Assert.True(c.Ok("ctsim/answer", Args(("id", id), ("ok", true)))["answered"].Bool);
                        Assert.Equal(
                            id,
                            c.WaitEvent("ctsim/dialogClosed", e => e["id"].Long == id)["id"].Long
                        );
                        var r = call.Result;
                        Assert.True(r["success"].Bool, r.ToString());
                        Assert.Equal("0", r["body"]["result"].Str);
                        Assert.False(
                            c.Request("ctsim/answer", Args(("id", id), ("ok", true)))["success"].Bool
                        ); // answered already
                        // an input box: the text typed
                        call = Task.Run(() =>
                            c.Request(
                                "ctsim/call",
                                Args(("expr", "Input(\"Name\", \"Who\", \"x\")"), ("timeoutMs", 30000)),
                                40000
                            )
                        );
                        d = c.WaitEvent("ctsim/dialog", e => e["kind"].Str == "input");
                        Assert.Equal("x", d["fields"].Items[0]["value"].Str);
                        c.Ok("ctsim/answer", Args(("id", d["id"].Long), ("ok", true), ("text", "Ada")));
                        Assert.Equal("Ada", call.Result["body"]["result"].Str);
                        // Include CTUTIL.CI's DspPopupMenu: the menu, then the chosen item's number
                        call = Task.Run(() =>
                            c.Request(
                                "ctsim/call",
                                Args(
                                    ("expr", "DspPopupMenu(-1, \"Start,!Stop,,Reset\", 100, 50)"),
                                    ("timeoutMs", 30000)
                                ),
                                40000
                            )
                        );
                        d = c.WaitEvent("ctsim/dialog", e => e["kind"].Str == "popupMenu");
                        var items = d["items"].Items;
                        Assert.Equal(4, items.Count);
                        Assert.Equal(("Start", 1L), (items[0]["label"].Str, items[0]["id"].Long));
                        Assert.False(items[1]["enabled"].Bool);
                        Assert.True(items[2]["separator"].Bool);
                        Assert.Equal(("Reset", 3L), (items[3]["label"].Str, items[3]["id"].Long));
                        c.Ok("ctsim/answer", Args(("id", d["id"].Long), ("choice", 3)));
                        var pr = call.Result;
                        Assert.True(pr["success"].Bool, pr.ToString());
                        Assert.Equal("3", pr["body"]["result"].Str);
                        // a form (Include page.ci _PageSelect: two buttons and a combo box of
                        // the pages): the page picked is displayed
                        c.Ok("ctsim/openPage", Args(("name", "Dynamic_1"), ("window", 0)));
                        call = Task.Run(() =>
                            c.Request(
                                "ctsim/call",
                                Args(("expr", "_PageSelect()"), ("timeoutMs", 30000)),
                                40000
                            )
                        );
                        d = c.WaitEvent("ctsim/dialog", e => e["kind"].Str == "form");
                        Assert.Equal("Page Select", d["title"].Str);
                        var fields = d["fields"].Items;
                        Assert.Equal(("ok", "OK"), (fields[0]["kind"].Str, fields[0]["label"].Str)); // "@(OK,20,C)"
                        var combo = fields.First(f => f["kind"].Str == "combo");
                        Assert.Contains(combo["items"].Items, x => x.Str == "Mixer");
                        var values = JsonValue.NewArray();
                        foreach (var f in fields)
                            values.Add(f["kind"].Str == "combo" ? JsonValue.Of("Mixer") : JsonValue.Null);
                        c.Ok("ctsim/answer", Args(("id", d["id"].Long), ("ok", true), ("values", values)));
                        Assert.True(call.Result["success"].Bool, call.Result.ToString());
                        c.WaitEvent("ctsim/windowsChanged", e =>
                            e["windows"].Items.Any(w => w["window"].Long == 0 && w["page"].Str == "Mixer")
                        );
                    }
                    // the viewer that shows dialogs goes away: the dialog is cancelled
                    using (var plain = Connect(server.Listener!.Address))
                    {
                        plain.Ok("ctsim/hello", Args(("client", "caller")));
                        Task<JsonValue> waiting;
                        using (var shows = Connect(server.Listener!.Address))
                        {
                            shows.Ok("ctsim/hello", Args(("client", "shows"), ("dialogs", true)));
                            waiting = Task.Run(() =>
                                plain.Request(
                                    "ctsim/call",
                                    Args(("expr", "Message(\"Gone\", \"x\", 1)"), ("timeoutMs", 30000)),
                                    40000
                                )
                            );
                            shows.WaitEvent("ctsim/dialog", e => e["title"].Str == "Gone");
                        }
                        Assert.Equal("299", waiting.Result["body"]["result"].Str);
                    }
                    // no viewer shows dialogs: the headless answer (cancelled)
                    using (var c = Connect(server.Listener!.Address))
                    {
                        c.Ok("ctsim/hello", Args(("client", "plain")));
                        var r = c.Request(
                            "ctsim/call",
                            Args(("expr", "Message(\"Title\", \"Hello\", 1)"), ("timeoutMs", 30000)),
                            40000
                        );
                        Assert.Equal("299", r["body"]["result"].Str);
                        var m = c.Request(
                            "ctsim/call",
                            Args(("expr", "DspPopupMenu(-1, \"A,B\")"), ("timeoutMs", 30000)),
                            40000
                        );
                        Assert.Equal("0", m["body"]["result"].Str);
                    }
                }
                finally
                {
                    Done(server, disc);
                }
            }
        }

        [ProjectFact]
        public void APageCicodeDisplaysWaitsForItsDrawing()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example, clock: "run"))
            {
                var (server, disc) = Serve(sim);
                try
                {
                    using (var c = Connect(server.Listener!.Address))
                    {
                        c.Ok("ctsim/hello", Args(("client", "models"), ("models", true)));
                        c.Ok("ctsim/openPage", Args(("name", "Mixer")));
                        c.Ok(
                            "ctsim/call",
                            Args(("expr", "WinNewAt(\"!EnergyStartStop\", 40, 50, 1)"), ("timeoutMs", 30000)),
                            40000
                        );
                        var want = c.WaitEvent("ctsim/modelWanted", e =>
                            e["page"].Str.Equals("!EnergyStartStop", StringComparison.OrdinalIgnoreCase)
                        );
                        int window = (int)want["window"].Long;
                        Assert.True(window > 0);
                        Assert.EndsWith(".ctg", want["path"].Str, StringComparison.OrdinalIgnoreCase);
                        // held: no scan yet
                        Assert.Equal(0, c.Ok("ctsim/pageState", Args(("window", window)))["scan"].Long);
                        var pm = JsonValue.Parse(
                            @"{""format"": ""ctsim-page/0"", ""page"": ""!EnergyStartStop"", ""size"": [400, 300], ""objects"": []}"
                        );
                        var am = c.Ok("ctsim/pageModel", Args(("window", window), ("model", pm)));
                        Assert.True(am["started"].Bool);
                        Assert.True(c.Ok("ctsim/pageState", Args(("window", window)))["scan"].Long > 0);
                        c.Ok("ctsim/closePage", Args(("window", window)));
                        // the drawing is known now: no hold
                        c.Ok(
                            "ctsim/call",
                            Args(("expr", "WinNewAt(\"!EnergyStartStop\", 40, 50, 1)"), ("timeoutMs", 30000)),
                            40000
                        );
                        int w2 = (int)
                            c.Ok("ctsim/windows")["windows"]
                                .Items.First(w =>
                                    w["page"]
                                        .Str.Equals("!EnergyStartStop", StringComparison.OrdinalIgnoreCase)
                                )["window"]
                                .Long;
                        Assert.True(c.Ok("ctsim/pageState", Args(("window", w2)))["scan"].Long > 0);
                        c.Ok("ctsim/closePage", Args(("window", w2)));
                        // no drawing comes: the page starts after the wait
                        sim.Pages.ModelWaitMs = 300;
                        c.Ok(
                            "ctsim/call",
                            Args(("expr", "WinNewAt(\"!PUMP_A_ACC\", 40, 50, 1)"), ("timeoutMs", 30000)),
                            40000
                        );
                        var w3 = c.WaitEvent("ctsim/modelWanted", e =>
                            e["page"].Str.Equals("!PUMP_A_ACC", StringComparison.OrdinalIgnoreCase)
                        );
                        var log = c.WaitEvent("ctsim/log", e => e["text"].Str.Contains("no drawing came"));
                        Assert.True(
                            c.Ok("ctsim/pageState", Args(("window", w3["window"].Long)))["scan"].Long > 0
                        );
                        c.Ok("ctsim/closePage", Args(("window", w3["window"].Long)));
                        // a held page closed before its drawing came starts first (its entry
                        // before its exit, as in the runtime)
                        sim.Pages.ModelWaitMs = 60000;
                        c.Ok(
                            "ctsim/call",
                            Args(("expr", "WinNewAt(\"!PUMP_A_ACC\", 40, 50, 1)"), ("timeoutMs", 30000)),
                            40000
                        );
                        int w4 = (int)
                            c.WaitEvent("ctsim/modelWanted", e =>
                                e["page"].Str.Equals("!PUMP_A_ACC", StringComparison.OrdinalIgnoreCase)
                            )["window"].Long;
                        int before = sim.Invoke(() => sim.Pages.Animator!.Events.Count);
                        c.Ok("ctsim/closePage", Args(("window", w4)));
                        var evs = sim.Invoke(() =>
                            sim
                                .Pages.Animator!.Events.Skip(before)
                                .Where(e => e.Window == w4)
                                .Select(e => e.Event)
                                .ToList()
                        );
                        _out.WriteLine(string.Join(", ", evs));
                        int entry = evs.IndexOf("entry"),
                            exit = evs.IndexOf("exit");
                        Assert.True(entry >= 0 && exit > entry);
                        // the viewer has no drawing: 'none' starts it at once
                        c.Ok(
                            "ctsim/call",
                            Args(("expr", "WinNewAt(\"!PUMP_A_ACC\", 40, 50, 1)"), ("timeoutMs", 30000)),
                            40000
                        );
                        int w5 = (int)
                            c.WaitEvent("ctsim/modelWanted", e =>
                                e["page"].Str.Equals("!PUMP_A_ACC", StringComparison.OrdinalIgnoreCase)
                            )["window"].Long;
                        var none = c.Ok("ctsim/pageModel", Args(("window", w5), ("none", true)));
                        Assert.True(none["started"].Bool);
                        Assert.True(c.Ok("ctsim/pageState", Args(("window", w5)))["scan"].Long > 0);
                        c.Ok("ctsim/closePage", Args(("window", w5)));
                    }
                    // a viewer without models: a page Cicode displays starts at once
                    using (var c = Connect(server.Listener!.Address))
                    {
                        c.Ok("ctsim/hello", Args(("client", "plain")));
                        var w = (int)
                            c.Ok(
                                "ctsim/call",
                                Args(("expr", "WinNewAt(\"!PUMP_A_ACC\", 60, 70, 1)"), ("timeoutMs", 30000)),
                                40000
                            )["value"].Long;
                        Assert.True(w > 0);
                        Assert.True(c.Ok("ctsim/pageState", Args(("window", w)))["scan"].Long > 0);
                    }
                }
                finally
                {
                    Done(server, disc);
                }
            }
        }

        [ProjectFact]
        public void ADrawingThatComesLateKeepsWhatCicodeSet()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example, clock: "run"))
            {
                var (server, disc) = Serve(sim);
                try
                {
                    using (var c = Connect(server.Listener!.Address))
                    {
                        c.Ok("ctsim/hello", Args(("client", "late")));
                        c.Ok("ctsim/openPage", Args(("name", "Mixer")));
                        var pm = JsonValue.Parse(
                            @"{""format"": ""ctsim-page/0"", ""page"": ""Mixer"", ""size"": [1366, 746],
                            ""objects"": [{""an"": 556, ""name"": ""Meter"", ""metadata"": [[""State"", ""0""], [""Label"", ""x""]]}]}"
                        );
                        c.Ok("ctsim/pageModel", Args(("window", 0), ("model", pm)));
                        Assert.Equal(
                            "0",
                            sim.Invoke(() =>
                                sim.World.Display.Windows[0].Page!.Get(556)!.MetadataValue("State")
                            )
                        );
                        var set = c.Ok(
                            "ctsim/call",
                            Args(("expr", "DspAnSetMetadata(556, \"State\", \"3\")"), ("timeoutMs", 30000)),
                            40000
                        );
                        Assert.Equal("0", set["result"].Str);
                        c.Ok("ctsim/pageModel", Args(("window", 0), ("model", pm)));
                        var o = sim.Invoke(() => sim.World.Display.Windows[0].Page!.Get(556)!);
                        Assert.Equal("3", o.MetadataValue("State")); // Cicode's value kept
                        Assert.Equal("x", o.MetadataValue("Label"));
                    }
                }
                finally
                {
                    Done(server, disc);
                }
            }
        }

        /// <summary>The SA alarm page's entry finds its search box (an ActiveX control: a stub)
        /// and runs on: AlarmPage_Init does not halt with 360 at ObjectByName("SEARCH").</summary>
        [ProjectFact]
        public void TheSaAlarmPageFindsItsSearchControl()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.ExampleSA))
            {
                var ev = new SimEvents(sim);
                var notes = new System.Collections.Generic.List<string>();
                var open = sim.Invoke(() =>
                    sim.Pages.Open("DefaultAlarm_HD1080", 0, null, null, null, "rdb", notes)
                );
                Assert.Equal(0, open["error"].Long);
                sim.Invoke(() => sim.StepClock(2000));
                System.Collections.Generic.List<LogEntry> logs;
                lock (ev.Logs)
                    logs = new System.Collections.Generic.List<LogEntry>(ev.Logs);
                foreach (
                    var l in logs.Where(x => x.Text.Contains("ObjectByName") || x.Text.Contains("ActiveX"))
                        .Take(10)
                )
                    _out.WriteLine(l.Level + ": " + l.Text);
                Assert.DoesNotContain(logs, l => l.Code == 360);
                Assert.Contains(logs, l => l.Text.Contains("ActiveX control 'SEARCH'"));
            }
        }
    }
}
