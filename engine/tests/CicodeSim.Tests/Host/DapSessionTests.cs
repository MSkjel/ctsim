using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using CicodeSim.Host;
using CicodeSim.Host.Dap;
using CicodeSim.Sim;
using CicodeSim.Values;
using CicodeSim.Vm;
using Xunit;
using static CicodeSim.Tests.Host.ProtocolClient;

namespace CicodeSim.Tests.Host
{
    /// <summary>The throwaway runtime project of the conformance recorder (a copy of ExampleSA
    /// with zz_conformance.ci compiled in; its _LIBRARY.RDB also holds Include's functions).</summary>
    public static class CiSimRT
    {
        public static readonly string Dir = Path.Combine(FrontendProbes.Root, "rtproj", "CiSimRT");
        public static readonly string Conformance = Path.Combine(Dir, "zz_conformance.ci");
        public static bool Present =>
            File.Exists(Path.Combine(Dir, "_LIBRARY.RDB")) && File.Exists(Conformance);
    }

    /// <summary>A fact that needs the CiSimRT project.</summary>
    public sealed class CiSimRTFactAttribute : FactAttribute
    {
        public CiSimRTFactAttribute()
        {
            if (!CiSimRT.Present)
                Skip = CiSimRT.Dir + " (with zz_conformance.ci) not present";
        }
    }

    /// <summary>A server of CiSimRT (or <c>project</c>) in this process (paused clock, the given
    /// compile mode) with the extension's DAP session, a debugger client on its pipe.</summary>
    internal sealed class DapRig : IDisposable
    {
        public readonly Simulation Sim;
        public readonly SimServer Server;
        public ProtocolClient Client;
        private readonly string _disc = Path.Combine(
            Path.GetTempPath(),
            "ctsim-dap-" + Guid.NewGuid().ToString("N")
        );

        public DapRig(string compile = CodeSource.Changed, string? project = null)
        {
            Directory.CreateDirectory(_disc);
            Sim = SimTestUtil.Start(project ?? CiSimRT.Dir, compile: compile);
            Server = new SimServer(Sim) { DapFactory = DapSessions.Create };
            Server.Listen(_disc);
            Client = Connect(Server.Listener!.Transport, Server.Listener.Address);
        }

        public JsonValue Initialize()
        {
            var caps = Client.Ok(
                "initialize",
                Args(
                    ("clientID", "test"),
                    ("adapterID", "cicode-sim"),
                    ("linesStartAt1", true),
                    ("columnsStartAt1", true)
                )
            );
            Client.WaitEvent("initialized");
            return caps;
        }

        /// <summary>Disconnect and start the next session on a new connection.</summary>
        public void NextSession()
        {
            Client.Ok("disconnect");
            var sw = Stopwatch.StartNew();
            while (Server.DebuggerAttached)
            {
                if (sw.ElapsedMilliseconds > 10000)
                    throw new TimeoutException("the server still has the old debugger");
                Thread.Sleep(10);
            }
            Client.Dispose();
            Client = Connect(Server.Listener!.Transport, Server.Listener.Address);
            Initialize();
        }

        /// <summary>The .ci file of a function of the library (its compile-time path).</summary>
        public string FileOf(string function) => Sim.Invoke(() => Sim.Library.Function(function)!.Filename);

        public void Dispose()
        {
            Client.Dispose();
            Server.Dispose();
            Sim.Dispose();
            try
            {
                Directory.Delete(_disc, true);
            }
            catch (Exception) { }
        }
    }

    /// <summary>The DAP session end to end over the pipe.</summary>
    [Collection("ctsim server")]
    public sealed class DapSessionTests
    {
        private static string RepoRoot()
        {
            string d = AppContext.BaseDirectory;
            while (d != null && !File.Exists(Path.Combine(d, "LICENSE")))
                d = Path.GetDirectoryName(d)!;
            return d ?? throw new DirectoryNotFoundException("repo root");
        }

        /// <summary>A Conf_ function's live result (conformance/runtime/functions).</summary>
        private static string LiveResult(string function) =>
            JsonValue
                .Parse(
                    File.ReadAllText(
                        Path.Combine(RepoRoot(), "conformance", "runtime", "functions", function + ".json")
                    )
                )["result"]
                .Str;

        private static JsonValue Src(string path) => Args(("path", path));

        private static JsonValue Bp(
            int line,
            string? condition = null,
            string? hit = null,
            string? log = null
        )
        {
            var o = Args(("line", line));
            if (condition != null)
                o.Set("condition", condition);
            if (hit != null)
                o.Set("hitCondition", hit);
            if (log != null)
                o.Set("logMessage", log);
            return o;
        }

        private static JsonValue List(params JsonValue[] items)
        {
            var a = JsonValue.NewArray();
            foreach (var i in items)
                a.Add(i);
            return a;
        }

        private static bool IsOutput(JsonValue o, string start) =>
            o["output"].Str.StartsWith(start, StringComparison.Ordinal);

        /// <summary>The position of the first message that matches (-1: none).</summary>
        private static int IndexOf(ProtocolClient c, Func<JsonValue, bool> match)
        {
            lock (c.Received)
                return c.Received.FindIndex(m => match(m));
        }

        private static bool IsEvent(JsonValue m, string name) =>
            m.Get("type")?.Str == "event" && m.Get("event")?.Str == name;

        private const string Err424 =
            @"^Cicode hardware error 424 'Tag not found' in thread \d+, GetTagVal \(TagRead\) at .*\\ddewrite\.ci:23\n$";
        private const string Err259 =
            @"^Cicode hardware error 259 'Array has been overrun' in thread (\d+), _PltLineStyleGet at .*\\graph\.ci:271 \[the task halts\]\n$";

        [CiSimRTFact]
        public void LaunchRunsAFunctionToItsReturnValue()
        {
            using (var rig = new DapRig())
            {
                var c = rig.Client;
                var caps = rig.Initialize();
                foreach (
                    string cap in new[]
                    {
                        "supportsConfigurationDoneRequest",
                        "supportsConditionalBreakpoints",
                        "supportsHitConditionalBreakpoints",
                        "supportsLogPoints",
                        "supportsEvaluateForHovers",
                        "supportsSetVariable",
                        "supportsExceptionInfoRequest",
                        "supportsTerminateRequest",
                    }
                )
                    Assert.True(caps[cap].Bool, cap);
                var filters = caps["exceptionBreakpointFilters"].Items;
                Assert.Equal(
                    new[] { "hardwareError", "pageCodeError" },
                    filters.Select(f => f["filter"].Str).ToArray()
                );
                var filter = filters[0];
                Assert.Equal("Cicode hardware errors", filter["label"].Str);
                Assert.True(filter["default"].Bool);

                // refused launches: no function, an unknown one, a compile mode that is not one
                // or not the server's, another project
                var none = c.Request("launch", Args(("project", CiSimRT.Dir)));
                Assert.False(none["success"].Bool);
                Assert.Contains("'function'", none["message"].Str);
                var unknown = c.Request("launch", Args(("function", "NoSuchFunctionXyz")));
                Assert.False(unknown["success"].Bool);
                Assert.Contains("no function 'NoSuchFunctionXyz'", unknown["message"].Str);
                var fast = c.Request("launch", Args(("function", "Conf_IntArith"), ("compile", "fast")));
                Assert.False(fast["success"].Bool);
                Assert.Contains("compile must be changed, all or none", fast["message"].Str);
                var all = c.Request("launch", Args(("function", "Conf_IntArith"), ("compile", "all")));
                Assert.False(all["success"].Bool);
                Assert.Contains("loaded the program with compile 'changed'", all["message"].Str);
                Assert.Contains("--compile all", all["message"].Str);
                var other = c.Request(
                    "launch",
                    Args(("function", "Conf_IntArith"), ("project", @"C:\NoSuchProjectXyz"))
                );
                Assert.False(other["success"].Bool);
                Assert.Contains("start a server for", other["message"].Str);

                c.Ok(
                    "launch",
                    Args(
                        ("function", "Conf_IntArith"),
                        ("project", CiSimRT.Dir),
                        ("file", CiSimRT.Conformance),
                        ("compile", "changed")
                    )
                );
                c.Ok("setExceptionBreakpoints", Args(("filters", Names("hardwareError"))));
                c.Ok("configurationDone");
                // the task's thread, its end, then the return value and the session's end
                var started = c.WaitEvent("thread", t => t["reason"].Str == "started");
                Assert.Equal(
                    started["threadId"].Long,
                    c.WaitEvent("thread", t => t["reason"].Str == "exited")["threadId"].Long
                );
                var ret = c.WaitEvent("output", o => IsOutput(o, "Conf_IntArith returned"));
                Assert.Equal(
                    "Conf_IntArith returned \"" + LiveResult("Conf_IntArith") + "\"\n",
                    ret["output"].Str
                );
                Assert.Equal("console", ret["category"].Str);
                Assert.Equal(0, c.WaitEvent("exited")["exitCode"].Long);
                c.WaitEvent("terminated");
                Assert.True(
                    IndexOf(c, m => IsEvent(m, "exited")) < IndexOf(c, m => IsEvent(m, "terminated"))
                );
                c.Ok("disconnect");
            }
        }

        [CiSimRTFact]
        public void CompileAllRunsAndDebugsFromSource()
        {
            using (var rig = new DapRig(CodeSource.All))
            {
                var c = rig.Client;
                Assert.StartsWith("source:", rig.Sim.Invoke(() => rig.Sim.Library.Source));
                rig.Initialize();
                var bp = c.Ok(
                        "setBreakpoints",
                        Args(("source", Src(CiSimRT.Conformance)), ("breakpoints", List(Bp(155))))
                    )["breakpoints"]
                    .Items.Single();
                Assert.True(bp["verified"].Bool);
                c.Ok("launch", Args(("function", "Conf_IntArith"), ("compile", "all")));
                c.Ok("configurationDone");
                var stop = c.WaitEvent("stopped");
                Assert.Equal("breakpoint", stop["reason"].Str);
                long tid = stop["threadId"].Long;
                var f0 = c.Ok("stackTrace", Args(("threadId", tid)))["stackFrames"].Items.Single();
                Assert.Equal(155, f0["line"].Long);
                Assert.Equal(CiSimRT.Conformance, f0["source"]["path"].Str, ignoreCase: true);
                Assert.Equal(
                    "2147483647",
                    c.Ok(
                        "evaluate",
                        Args(("expression", "big"), ("frameId", f0["id"].Long), ("context", "hover"))
                    )["result"].Str
                );
                c.Ok("setBreakpoints", Args(("source", Src(CiSimRT.Conformance)), ("breakpoints", List())));
                c.Ok("continue", Args(("threadId", tid)));
                Assert.Equal(
                    "Conf_IntArith returned \"" + LiveResult("Conf_IntArith") + "\"\n",
                    c.WaitEvent("output", o => IsOutput(o, "Conf_IntArith returned"))["output"].Str
                );
                c.WaitEvent("terminated");

                // every recorded function, compiled from source, returns the live runtime's result
                // (a launch that leaves compile out takes the server's)
                int n = 0;
                foreach (
                    string path in Directory.GetFiles(
                        Path.Combine(RepoRoot(), "conformance", "runtime", "functions"),
                        "*.json"
                    )
                )
                {
                    var rec = JsonValue.Parse(File.ReadAllText(path));
                    if (!rec["ok"].Bool)
                        continue;
                    string fn = rec["function"].Str;
                    rig.NextSession();
                    c = rig.Client;
                    c.Ok("launch", Args(("function", fn)));
                    c.Ok("configurationDone");
                    Assert.Equal(
                        fn + " returned \"" + rec["result"].Str + "\"\n",
                        c.WaitEvent("output", o => IsOutput(o, fn + " returned"))["output"].Str
                    );
                    c.WaitEvent("terminated");
                    n++;
                }
                Assert.True(n >= 14, "functions run: " + n);
            }
        }

        [CiSimRTFact]
        public void ArgsAreCicodeLiterals()
        {
            using (var rig = new DapRig())
            {
                rig.Initialize();
                // a string with a quote and a caret, to a built-in
                string s = "say \"hi\" ^ there";
                rig.Client.Ok("launch", Args(("function", "StrLength"), ("args", List(JsonValue.Of(s)))));
                rig.Client.Ok("configurationDone");
                Assert.Equal(
                    "StrLength returned " + s.Length + "\n",
                    rig.Client.WaitEvent("output", o => IsOutput(o, "StrLength"))["output"].Str
                );
                rig.Client.WaitEvent("terminated");

                rig.NextSession();
                rig.Client.Ok("launch", Args(("function", "Conf_R"), ("args", List(JsonValue.Of(1.5)))));
                rig.Client.Ok("configurationDone");
                Assert.Equal(
                    "Conf_R returned \"1.5\"\n",
                    rig.Client.WaitEvent("output", o => IsOutput(o, "Conf_R"))["output"].Str
                );
                rig.Client.WaitEvent("terminated");

                rig.NextSession();
                // configurationDone first, then the launch (VS Code sends them in either order)
                rig.Client.Ok("configurationDone");
                rig.Client.Ok("launch", Args(("function", "Conf_I"), ("args", List(JsonValue.Of(-42)))));
                Assert.Equal(
                    "Conf_I returned \"-42\"\n",
                    rig.Client.WaitEvent("output", o => IsOutput(o, "Conf_I"))["output"].Str
                );
                rig.Client.WaitEvent("terminated");

                rig.NextSession();
                // an argument the parameter type refuses: the launch fails, nothing runs
                var bad = rig.Client.Request(
                    "launch",
                    Args(("function", "Conf_I"), ("args", List(JsonValue.Of("abc"))))
                );
                Assert.False(bad["success"].Bool);
                Assert.StartsWith("args: ", bad["message"].Str);
                var missing = rig.Client.Request("launch", Args(("function", "Conf_I")));
                Assert.False(missing["success"].Bool);
            }
        }

        [CiSimRTFact]
        public void ArgsBeyondIntGoByTheParameterTypeAndExtraArgsAreRefused()
        {
            using (var rig = new DapRig())
            {
                rig.Initialize();
                // call text reads an integer literal as 32 bits: a REAL parameter gets a real literal
                rig.Client.Ok(
                    "launch",
                    Args(("function", "Conf_R"), ("args", List(JsonValue.Of(3000000000L))))
                );
                rig.Client.Ok("configurationDone");
                Assert.Equal(
                    "Conf_R returned \"3000000000\"\n",
                    rig.Client.WaitEvent("output", o => IsOutput(o, "Conf_R"))["output"].Str
                );
                rig.Client.WaitEvent("terminated");

                rig.NextSession();
                // a STRING parameter gets the number's text
                rig.Client.Ok(
                    "launch",
                    Args(("function", "StrLength"), ("args", List(JsonValue.Of(-3000000000L))))
                );
                rig.Client.Ok("configurationDone");
                Assert.Equal(
                    "StrLength returned 11\n",
                    rig.Client.WaitEvent("output", o => IsOutput(o, "StrLength"))["output"].Str
                );
                rig.Client.WaitEvent("terminated");

                rig.NextSession();
                var c = rig.Client;
                // an INT parameter cannot hold it
                var big = c.Request(
                    "launch",
                    Args(("function", "Conf_I"), ("args", List(JsonValue.Of(3000000000L))))
                );
                Assert.False(big["success"].Bool);
                Assert.Equal(
                    "args[0]: 3000000000 is outside the range of the INT parameter (-2147483648 to 2147483647)",
                    big["message"].Str
                );
                var bigReal = c.Request(
                    "launch",
                    Args(("function", "Conf_I"), ("args", List(JsonValue.Of(3e9))))
                );
                Assert.False(bigReal["success"].Bool);
                Assert.StartsWith("args[0]: ", bigReal["message"].Str);
                // more arguments than parameters (call text drops them, 518): refused
                var many = c.Request(
                    "launch",
                    Args(
                        ("function", "Conf_I"),
                        ("args", List(JsonValue.Of(1), JsonValue.Of(2), JsonValue.Of(3)))
                    )
                );
                Assert.False(many["success"].Bool);
                Assert.Equal("args: Conf_I takes 1 argument(s), 3 given", many["message"].Str);
                var builtin = c.Request(
                    "launch",
                    Args(("function", "StrLength"), ("args", List(JsonValue.Of("a"), JsonValue.Of("b"))))
                );
                Assert.False(builtin["success"].Bool);
                Assert.EndsWith(" takes 1 argument(s), 2 given", builtin["message"].Str);
                // the INT range itself is fine
                c.Ok("launch", Args(("function", "Conf_I"), ("args", List(JsonValue.Of(-2147483648L)))));
                c.Ok("configurationDone");
                Assert.Equal(
                    "Conf_I returned \"-2147483648\"\n",
                    c.WaitEvent("output", o => IsOutput(o, "Conf_I"))["output"].Str
                );
                c.WaitEvent("terminated");
            }
        }

        [CiSimRTFact]
        public void ScenarioThenTagsThenTheCallWithATrace()
        {
            string trace = Path.Combine(
                Path.GetTempPath(),
                "ctsim-dap-" + Guid.NewGuid().ToString("N"),
                "last.trace.jsonl"
            );
            try
            {
                using (var rig = new DapRig())
                {
                    var c = rig.Client;
                    rig.Initialize();
                    var scenario = JsonValue.Parse(
                        @"{""format"": ""ctsim-scenario/0"", ""tags"": {""WTP_T201_LT"": 10, ""WTP_T202_LT"": 20, ""WTP_T201_LSL"": 1}}"
                    );
                    var tags = JsonValue.Parse(
                        @"{""WTP_T201_LT"": 42.5, ""WTP_T202_LT"": {""value"": 7, ""quality"": ""good"", ""timestamp"": ""2026-10-01T00:00:00Z""}}"
                    );
                    // an unknown tag refuses the launch
                    var bad = c.Request(
                        "launch",
                        Args(
                            ("function", "TagRead"),
                            ("args", List(JsonValue.Of("WTP_T201_LT"))),
                            ("tags", JsonValue.Parse(@"{""NoSuchTagXyz"": 1}"))
                        )
                    );
                    Assert.False(bad["success"].Bool);
                    Assert.Equal("tags: NoSuchTagXyz: error 424 Tag not found", bad["message"].Str);
                    c.Ok(
                        "launch",
                        Args(
                            ("function", "TagRead"),
                            ("args", List(JsonValue.Of("WTP_T201_LT"))),
                            ("scenario", scenario),
                            ("tags", tags),
                            ("trace", trace)
                        )
                    );
                    c.Ok("configurationDone");
                    Assert.StartsWith(
                        "tags: the timestamp of WTP_T202_LT is not set",
                        c.WaitEvent("output", o => IsOutput(o, "tags:"))["output"].Str
                    );
                    Assert.Equal(
                        "TagRead returned \"42.5\"\n",
                        c.WaitEvent("output", o => IsOutput(o, "TagRead"))["output"].Str
                    );
                    c.WaitEvent("terminated");
                    // the tags win over the scenario's; the scenario's other tags are set
                    Assert.Equal(42.5, rig.Sim.Invoke(() => rig.Sim.ReadTag("WTP_T201_LT")).Value.Double);
                    Assert.Equal(7, rig.Sim.Invoke(() => rig.Sim.ReadTag("WTP_T202_LT")).Value.Double);
                    Assert.Equal(1, rig.Sim.Invoke(() => rig.Sim.ReadTag("WTP_T201_LSL")).Value.Double);
                    c.Ok("disconnect");
                    SimTestUtil.Until(
                        rig.Sim,
                        () => rig.Sim.World.Trace == null,
                        what: "the session's trace closing"
                    );
                }
                string text = File.ReadAllText(trace);
                Assert.Contains("\"WTP_T201_LT\"", text);
                Assert.Contains("42.5", text);
            }
            finally
            {
                try
                {
                    Directory.Delete(Path.GetDirectoryName(trace)!, true);
                }
                catch (Exception) { }
            }
        }

        [CiSimRTFact]
        public void BreakpointShowsTheFrameItsVariablesAndEvaluates()
        {
            using (var rig = new DapRig())
            {
                var c = rig.Client;
                rig.Initialize();
                var bp = c.Ok(
                        "setBreakpoints",
                        Args(("source", Src(CiSimRT.Conformance)), ("breakpoints", List(Bp(155))))
                    )["breakpoints"]
                    .Items.Single();
                Assert.True(bp["verified"].Bool);
                Assert.Equal(155, bp["line"].Long);
                Assert.Equal(CiSimRT.Conformance, bp["source"]["path"].Str);
                c.Ok("launch", Args(("function", "Conf_IntArith")));
                c.Ok("configurationDone");
                var stop = c.WaitEvent("stopped");
                Assert.Equal("breakpoint", stop["reason"].Str);
                Assert.Equal(bp["id"].Long, stop["hitBreakpointIds"].Items.Single().Long);
                Assert.False(stop["allThreadsStopped"].Bool);
                long tid = stop["threadId"].Long;
                Assert.Contains(c.Ok("threads")["threads"].Items, t =>
                    t["id"].Long == tid && t["name"].Str == "Cicode thread " + tid + " (Conf_IntArith)"
                );

                var st = c.Ok("stackTrace", Args(("threadId", tid), ("startFrame", 0), ("levels", 20)));
                Assert.Equal(1, st["totalFrames"].Long);
                var f0 = st["stackFrames"].Items.Single();
                Assert.Equal("Conf_IntArith", f0["name"].Str);
                Assert.Equal(155, f0["line"].Long);
                Assert.Equal("zz_conformance.ci", f0["source"]["name"].Str);
                Assert.Equal(CiSimRT.Conformance, f0["source"]["path"].Str, ignoreCase: true);
                var past = c.Ok("stackTrace", Args(("threadId", tid), ("startFrame", 1), ("levels", 20)));
                Assert.Empty(past["stackFrames"].Items);
                Assert.Equal(1, past["totalFrames"].Long);
                long fid = f0["id"].Long;

                var scopes = c.Ok("scopes", Args(("frameId", fid)))["scopes"].Items;
                Assert.Equal("Locals", scopes[0]["name"].Str);
                Assert.Equal("locals", scopes[0]["presentationHint"].Str);
                Assert.Equal(10, scopes[0]["namedVariables"].Long);
                Assert.False(scopes[0]["expensive"].Bool);
                Assert.Contains(scopes, s => s["name"].Str == "Module"); // mTick of zz_conformance.ci
                long lref = scopes[0]["variablesReference"].Long;
                var vars = c.Ok("variables", Args(("variablesReference", lref)))["variables"].Items;
                Assert.Equal(10, vars.Count);
                var big = vars.Single(v => v["name"].Str == "big");
                Assert.Equal("2147483647", big["value"].Str);
                Assert.Equal("INT", big["type"].Str);
                Assert.Equal("big", big["evaluateName"].Str);
                Assert.Equal("-2147483648", vars.Single(v => v["name"].Str == "w")["value"].Str);
                var page = c.Ok("variables", Args(("variablesReference", lref), ("start", 1), ("count", 2)))[
                    "variables"
                ].Items;
                Assert.Equal(
                    new[] { vars[1]["name"].Str, vars[2]["name"].Str },
                    page.Select(v => v["name"].Str).ToArray()
                );
                var module = c.Ok(
                    "variables",
                    Args(
                        (
                            "variablesReference",
                            scopes.First(s => s["name"].Str == "Module")["variablesReference"].Long
                        )
                    )
                )["variables"].Items;
                Assert.Contains(module, v => v["name"].Str == "mTick");

                // evaluate: a hover takes names only, the console and a watch also one call
                Assert.Equal(
                    "2147483647",
                    c.Ok("evaluate", Args(("expression", "big"), ("frameId", fid), ("context", "hover")))[
                        "result"
                    ].Str
                );
                Assert.False(
                    c.Request(
                        "evaluate",
                        Args(("expression", "Conf_I(7)"), ("frameId", fid), ("context", "hover"))
                    )["success"].Bool
                );
                var call = c.Ok(
                    "evaluate",
                    Args(("expression", "Conf_I(7)"), ("frameId", fid), ("context", "repl"))
                );
                Assert.Equal("\"7\"", call["result"].Str);
                Assert.Equal("STRING", call["type"].Str);
                Assert.Equal(
                    "-2147483648",
                    c.Ok("evaluate", Args(("expression", "w"), ("frameId", fid), ("context", "watch")))[
                        "result"
                    ].Str
                );
                Assert.Equal(
                    "\"3\"",
                    c.Ok("evaluate", Args(("expression", "Conf_I(3)"), ("context", "repl")))["result"].Str
                ); // no frame
                var bad = c.Request(
                    "evaluate",
                    Args(("expression", "NoSuchNameXyz"), ("frameId", fid), ("context", "watch"))
                );
                Assert.False(bad["success"].Bool);
                Assert.NotEqual("", bad["message"].Str);

                // setVariable: the simulator can change a local; a value of the wrong type is refused
                Assert.Equal(
                    "5",
                    c.Ok("setVariable", Args(("variablesReference", lref), ("name", "big"), ("value", "5")))[
                        "value"
                    ].Str
                );
                Assert.Equal(
                    "5",
                    c.Ok("evaluate", Args(("expression", "big"), ("frameId", fid), ("context", "hover")))[
                        "result"
                    ].Str
                );
                var refused = c.Request(
                    "setVariable",
                    Args(("variablesReference", lref), ("name", "big"), ("value", "abc"))
                );
                Assert.False(refused["success"].Bool);
                Assert.Contains("not a number", refused["message"].Str);

                // continue (every stopped task, the runtime's CONTINUE_ALL): the function returns
                // and the session ends
                Assert.True(c.Ok("continue", Args(("threadId", tid)))["allThreadsContinued"].Bool);
                Assert.Equal(
                    "Conf_IntArith returned \"" + LiveResult("Conf_IntArith") + "\"\n",
                    c.WaitEvent("output", o => IsOutput(o, "Conf_IntArith returned"))["output"].Str
                );
                c.WaitEvent("terminated");
                // a frame of the ended task is gone
                Assert.False(c.Request("scopes", Args(("frameId", fid)))["success"].Bool);
            }
        }

        [CiSimRTFact]
        public void StopOnEntryStopsOnTheFirstLine()
        {
            using (var rig = new DapRig())
            {
                var c = rig.Client;
                rig.Initialize();
                c.Ok("launch", Args(("function", "Conf_ForTo"), ("stopOnEntry", true)));
                c.Ok("configurationDone");
                var stop = c.WaitEvent("stopped");
                Assert.Equal("entry", stop["reason"].Str);
                long tid = stop["threadId"].Long;
                Assert.Equal(
                    188,
                    c.Ok("stackTrace", Args(("threadId", tid)))["stackFrames"].Items[0]["line"].Long
                );
                c.Ok("next", Args(("threadId", tid)));
                var step = c.WaitEvent("stopped");
                Assert.Equal("step", step["reason"].Str);
                Assert.Equal(
                    189,
                    c.Ok("stackTrace", Args(("threadId", tid)))["stackFrames"].Items[0]["line"].Long
                );
                // the step's response went before the stop it caused
                int resp = IndexOf(c, m => m.Get("type")?.Str == "response" && m["command"].Str == "next");
                int ev = IndexOf(c, m => IsEvent(m, "stopped") && m["body"]["reason"].Str == "step");
                Assert.True(resp >= 0 && resp < ev, "next response " + resp + ", stopped event " + ev);
                c.Ok("continue", Args(("threadId", tid)));
                Assert.Equal(
                    "Conf_ForTo returned \"" + LiveResult("Conf_ForTo") + "\"\n",
                    c.WaitEvent("output", o => IsOutput(o, "Conf_ForTo returned"))["output"].Str
                );
                c.WaitEvent("terminated");
            }
        }

        [CiSimRTFact]
        public void HaltingHardwareErrorEndsTheTaskWithNoStop()
        {
            // conformance/runtime/debugger/hwerror_259_array_overrun_halts.json through the session
            using (var rig = new DapRig())
            {
                var c = rig.Client;
                rig.Initialize();
                c.Ok("setExceptionBreakpoints", Args(("filters", Names("hardwareError"))));
                c.Ok("launch", Args(("function", "_PltLineStyleGet"), ("args", List(JsonValue.Of(1000)))));
                c.Ok("configurationDone");
                var err = c.WaitEvent("output", o =>
                    o["category"].Str == "stderr" && IsOutput(o, "Cicode hardware error")
                );
                Assert.Matches(Err259, err["output"].Str);
                Assert.Equal(271, err["line"].Long);
                Assert.Equal("graph.ci", err["source"]["name"].Str, ignoreCase: true);
                var exit = c.WaitEvent("thread", t => t["reason"].Str == "exited");
                Assert.Contains("in thread " + exit["threadId"].Long + ",", err["output"].Str);
                var end = c.WaitEvent("output", o => IsOutput(o, "_PltLineStyleGet"));
                Assert.Equal(
                    "_PltLineStyleGet halted: hardware error 259 'Array has been overrun'\n",
                    end["output"].Str
                );
                Assert.Equal("stderr", end["category"].Str);
                Assert.Equal(1, c.WaitEvent("exited")["exitCode"].Long);
                c.WaitEvent("terminated");
                Assert.Equal(-1, IndexOf(c, m => IsEvent(m, "stopped")));
            }
        }

        [CiSimRTFact]
        public void NonHaltingHardwareErrorStopsWithExceptionInfo()
        {
            // conformance/runtime/debugger/hwerror_424_in_builtin_continues.json through the session
            using (var rig = new DapRig())
            {
                var c = rig.Client;
                rig.Initialize();
                c.Ok("setExceptionBreakpoints", Args(("filters", Names("hardwareError"))));
                c.Ok("launch", Args(("function", "GetTagVal"), ("args", List(JsonValue.Of("NoSuchTagXyz")))));
                c.Ok("configurationDone");
                var err = c.WaitEvent("output", o => o["category"].Str == "stderr");
                Assert.Matches(Err424, err["output"].Str);
                var stop = c.WaitEvent("stopped");
                Assert.Equal("exception", stop["reason"].Str);
                Assert.Equal("Cicode hardware error", stop["description"].Str);
                long tid = stop["threadId"].Long;
                Assert.Equal(
                    "Thread "
                        + tid
                        + " Hardware error (424) 'Tag not found' ErrPage: 'TagRead' ErrDesc: 'GetTagVal'",
                    stop["text"].Str
                );
                // the top frame shows the failing statement (the task is suspended before 24)
                var top = c.Ok("stackTrace", Args(("threadId", tid)))["stackFrames"].Items[0];
                Assert.Equal(23, top["line"].Long);
                Assert.Equal("ddewrite.ci", top["source"]["name"].Str, ignoreCase: true);
                var info = c.Ok("exceptionInfo", Args(("threadId", tid)));
                Assert.Equal("Hardware error 424", info["exceptionId"].Str);
                Assert.Equal("Tag not found (raised by TagRead) in GetTagVal", info["description"].Str);
                Assert.Equal("always", info["breakMode"].Str);
                Assert.Equal("Cicode hardware error", info["details"]["typeName"].Str);
                Assert.Equal(stop["text"].Str, info["details"]["message"].Str);
                Assert.Contains("ddewrite.ci:23", info["details"]["stackTrace"].Str);
                Assert.Contains("suspended before line 24", info["details"]["stackTrace"].Str);
                // stepping goes on from 24: the next stop shows where the task really is
                c.Ok("next", Args(("threadId", tid)));
                Assert.Equal("step", c.WaitEvent("stopped")["reason"].Str);
                Assert.Equal(
                    25,
                    c.Ok("stackTrace", Args(("threadId", tid)))["stackFrames"].Items[0]["line"].Long
                );
                Assert.False(c.Request("exceptionInfo", Args(("threadId", tid)))["success"].Bool);
                c.Ok("continue", Args(("threadId", tid)));
                Assert.Equal(
                    "GetTagVal returned \"0\"\n",
                    c.WaitEvent("output", o => IsOutput(o, "GetTagVal"))["output"].Str
                );
                c.WaitEvent("terminated");
                Assert.False(c.Request("exceptionInfo", Args(("threadId", tid)))["success"].Bool);

                // the filter off: the error is written and the task runs on (no stop)
                rig.NextSession();
                c = rig.Client;
                c.Ok("setExceptionBreakpoints", Args(("filters", JsonValue.NewArray())));
                c.Ok("launch", Args(("function", "GetTagVal"), ("args", List(JsonValue.Of("NoSuchTagXyz")))));
                c.Ok("configurationDone");
                err = c.WaitEvent("output", o => o["category"].Str == "stderr");
                Assert.Matches(Err424, err["output"].Str);
                Assert.Equal(
                    "GetTagVal returned \"0\"\n",
                    c.WaitEvent("output", o => IsOutput(o, "GetTagVal"))["output"].Str
                );
                c.WaitEvent("terminated");
                Assert.Equal(-1, IndexOf(c, m => IsEvent(m, "stopped")));
            }
        }

        [CiSimRTFact]
        public void ErrorStopsKeepTheirReportAcrossContinue()
        {
            // continue clears a task's error stop before the task runs: its next error is
            // reported on the event thread, possibly before the continue returns
            using (var rig = new DapRig())
            {
                var c = rig.Client;
                rig.Initialize();
                c.Ok("attach", Args(("server", "auto"), ("project", CiSimRT.Dir)));
                c.Ok("setExceptionBreakpoints", Args(("filters", Names("hardwareError"))));
                c.Ok(
                    "setBreakpoints",
                    Args(("source", Src(rig.FileOf("GetTagVal"))), ("breakpoints", List(Bp(23))))
                );
                c.Ok("configurationDone");
                // attach reports the tasks stopped before it after the configurationDone response:
                // the next request is handled once that is done
                c.Ok("threads");
                for (int run = 0; run < 12; run++)
                {
                    int task = rig.Sim.Invoke(() =>
                        rig.Sim.StartCall("GetTagVal(\"NoSuchTagXyz\")", null).Handle
                    );
                    var bp = c.WaitEvent("stopped");
                    Assert.Equal("breakpoint", bp["reason"].Str);
                    Assert.Equal(task, bp["threadId"].Long);
                    c.Ok("continue", Args(("threadId", task)));
                    var stop = c.WaitEvent("stopped");
                    Assert.Equal("exception", stop["reason"].Str);
                    Assert.Equal(
                        "Thread "
                            + task
                            + " Hardware error (424) 'Tag not found' ErrPage: 'TagRead' ErrDesc: 'GetTagVal'",
                        stop["text"].Str
                    );
                    var info = c.Ok("exceptionInfo", Args(("threadId", task)));
                    Assert.Equal("Tag not found (raised by TagRead) in GetTagVal", info["description"].Str);
                    Assert.Contains("ddewrite.ci:23", info["details"]["stackTrace"].Str);
                    Assert.Equal(
                        23,
                        c.Ok("stackTrace", Args(("threadId", task)))["stackFrames"].Items[0]["line"].Long
                    );
                    c.Ok("continue", Args(("threadId", task)));
                    c.WaitEvent("thread", t => t["reason"].Str == "exited" && t["threadId"].Long == task);
                }
                c.Ok("disconnect");
            }
        }

        [CiSimRTFact]
        public void NoDebugRunWritesEveryHardwareErrorAndNeverStops()
        {
            // Run Without Debugging: a normal task, no breakpoint, no stop, every error written
            using (var rig = new DapRig())
            {
                var c = rig.Client;
                rig.Initialize();
                string dde = rig.FileOf("GetTagVal");
                c.Ok("setExceptionBreakpoints", Args(("filters", Names("hardwareError"))));
                // a breakpoint set before the launch comes off the target
                var bp = c.Ok("setBreakpoints", Args(("source", Src(dde)), ("breakpoints", List(Bp(23)))))[
                        "breakpoints"
                    ]
                    .Items.Single();
                Assert.True(bp["verified"].Bool);
                c.Ok(
                    "launch",
                    Args(
                        ("function", "GetTagVal"),
                        ("args", List(JsonValue.Of("NoSuchTagXyz"))),
                        ("noDebug", true)
                    )
                );
                var changed = c.WaitEvent("breakpoint")["breakpoint"];
                Assert.Equal(bp["id"].Long, changed["id"].Long);
                Assert.False(changed["verified"].Bool);
                Assert.Equal(CicodeDapSession.NoDebugBreakpoint, changed["message"].Str);
                // and one set after it is not set
                var late = c.Ok("setBreakpoints", Args(("source", Src(dde)), ("breakpoints", List(Bp(24)))))[
                        "breakpoints"
                    ]
                    .Items.Single();
                Assert.False(late["verified"].Bool);
                Assert.Equal(CicodeDapSession.NoDebugBreakpoint, late["message"].Str);
                c.Ok("setExceptionBreakpoints", Args(("filters", Names("hardwareError"))));
                Assert.False(rig.Sim.Debugger.Options.BreakOnError);
                c.Ok("configurationDone");
                var err = c.WaitEvent("output", o => o["category"].Str == "stderr");
                Assert.Matches(Err424, err["output"].Str);
                Assert.Equal(23, err["line"].Long);
                c.WaitEvent("thread", t => t["reason"].Str == "exited");
                Assert.Equal(
                    "GetTagVal returned \"0\"\n",
                    c.WaitEvent("output", o => IsOutput(o, "GetTagVal"))["output"].Str
                );
                Assert.Equal(0, c.WaitEvent("exited")["exitCode"].Long);
                c.WaitEvent("terminated");
                Assert.Equal(-1, IndexOf(c, m => IsEvent(m, "stopped")));
                Assert.False(c.Request("pause", Args(("threadId", 1)))["success"].Bool);

                // a halting error: the error with its place, then the call's end
                rig.NextSession();
                c = rig.Client;
                // a new session stops on no error until its filter arrives
                Assert.False(rig.Sim.Debugger.Options.BreakOnError);
                c.Ok("setExceptionBreakpoints", Args(("filters", Names("hardwareError"))));
                c.Ok(
                    "launch",
                    Args(
                        ("function", "_PltLineStyleGet"),
                        ("args", List(JsonValue.Of(1000))),
                        ("noDebug", true)
                    )
                );
                c.Ok("configurationDone");
                err = c.WaitEvent("output", o =>
                    o["category"].Str == "stderr" && IsOutput(o, "Cicode hardware error")
                );
                Assert.Matches(Err259, err["output"].Str);
                Assert.Equal(271, err["line"].Long);
                Assert.Equal(
                    "_PltLineStyleGet halted: hardware error 259 'Array has been overrun'\n",
                    c.WaitEvent("output", o => IsOutput(o, "_PltLineStyleGet"))["output"].Str
                );
                Assert.Equal(1, c.WaitEvent("exited")["exitCode"].Long);
                c.WaitEvent("terminated");
                Assert.Equal(-1, IndexOf(c, m => IsEvent(m, "stopped")));
            }
        }

        [CiSimRTFact]
        public void LogPointsConditionsAndHitCounts()
        {
            using (var rig = new DapRig())
            {
                var c = rig.Client;
                rig.Initialize();
                var bps = c.Ok(
                    "setBreakpoints",
                    Args(
                        ("source", Src(CiSimRT.Conformance)),
                        (
                            "breakpoints",
                            List(
                                Bp(155, log: "w={w}"),
                                Bp(158, condition: "d2 = 4"),
                                Bp(160, condition: "m1 = -1"),
                                Bp(161, hit: "2")
                            )
                        )
                    )
                )["breakpoints"].Items;
                Assert.All(bps, b => Assert.True(b["verified"].Bool));
                c.Ok("launch", Args(("function", "Conf_IntArith")));
                c.Ok("configurationDone");
                var log = c.WaitEvent("output", o =>
                    o["output"].Str.StartsWith("w=", StringComparison.Ordinal)
                );
                Assert.Equal("w=-2147483648\n", log["output"].Str);
                Assert.Equal(155, log["line"].Long);
                var stop = c.WaitEvent("stopped");
                Assert.Equal(bps[2]["id"].Long, stop["hitBreakpointIds"].Items.Single().Long);
                long tid = stop["threadId"].Long;
                Assert.Equal(
                    160,
                    c.Ok("stackTrace", Args(("threadId", tid)))["stackFrames"].Items[0]["line"].Long
                );
                c.Ok("continue", Args(("threadId", tid)));
                c.WaitEvent("output", o => IsOutput(o, "Conf_IntArith returned"));
                c.WaitEvent("terminated");
                lock (c.Received)
                {
                    Assert.Single(c.Received, m => IsEvent(m, "stopped"));
                    Assert.DoesNotContain(c.Received, m =>
                        IsEvent(m, "output") && IsOutput(m["body"], "Breakpoint condition")
                    );
                }
            }
        }

        [Fact]
        public void ConditionsAreParsedWhenSet()
        {
            Assert.StartsWith(
                "The condition cannot be parsed: ",
                CicodeDapSession.ConditionError("(((", out _)
            );
            Assert.NotNull(CicodeDapSession.ConditionError("a =", out _));
            Assert.NotNull(CicodeDapSession.ConditionError("a = \"x", out _));
            Assert.NotNull(CicodeDapSession.ConditionError("n:###", out _));
            Assert.NotNull(
                CicodeDapSession.ConditionError(new string('(', 200) + "1" + new string(')', 200), out _)
            );
            // names are known only where the task stops; operator errors are run-time ones
            Assert.Null(CicodeDapSession.ConditionError("NoSuchVarXyz = 1", out bool calls));
            Assert.False(calls);
            Assert.Null(CicodeDapSession.ConditionError("a[2] / 0 > 1 AND s = \"x\" OR NOT b", out calls));
            Assert.False(calls);
            Assert.Null(CicodeDapSession.ConditionError("Conf_I(w) = \"1\"", out calls));
            Assert.True(calls);
            Assert.Null(CicodeDapSession.ConditionError(null, out calls));
        }

        [CiSimRTFact]
        public void ConditionThatDoesNotParseIsNotSetAndOneThatFailsSaysSo()
        {
            using (var rig = new DapRig())
            {
                var c = rig.Client;
                rig.Initialize();
                var bps = c.Ok(
                    "setBreakpoints",
                    Args(
                        ("source", Src(CiSimRT.Conformance)),
                        (
                            "breakpoints",
                            List(Bp(155, condition: "NoSuchVarXyz = 1"), Bp(158, condition: "((("), Bp(160))
                        )
                    )
                )["breakpoints"].Items;
                Assert.Equal(3, bps.Count);
                Assert.True(bps[0]["verified"].Bool);
                Assert.False(bps[1]["verified"].Bool);
                Assert.Equal(158, bps[1]["line"].Long);
                Assert.StartsWith("The condition cannot be parsed: ", bps[1]["message"].Str);
                Assert.True(bps[2]["verified"].Bool);
                Assert.NotEqual(bps[0]["id"].Long, bps[2]["id"].Long);
                c.Ok("launch", Args(("function", "Conf_IntArith")));
                c.Ok("configurationDone");
                // a condition that cannot be evaluated stops, and says why
                var why = c.WaitEvent("output", o => IsOutput(o, "Breakpoint condition"));
                Assert.Equal(
                    "Breakpoint condition 'NoSuchVarXyz = 1' cannot be evaluated (unknown name 'NoSuchVarXyz'): the task stops there\n",
                    why["output"].Str
                );
                Assert.Equal(155, why["line"].Long);
                var stop = c.WaitEvent("stopped");
                Assert.Equal(bps[0]["id"].Long, stop["hitBreakpointIds"].Items.Single().Long);
                c.Ok("continue", Args(("threadId", stop["threadId"].Long)));
                // the one that does not parse never stops; the next plain one does
                stop = c.WaitEvent("stopped");
                Assert.Equal(bps[2]["id"].Long, stop["hitBreakpointIds"].Items.Single().Long);
                c.Ok("continue", Args(("threadId", stop["threadId"].Long)));
                c.WaitEvent("output", o => IsOutput(o, "Conf_IntArith returned"));
                c.WaitEvent("terminated");
            }
        }

        [CiSimRTFact]
        public void ContinueResumesEveryStoppedTask()
        {
            using (var rig = new DapRig())
            {
                var c = rig.Client;
                rig.Initialize();
                c.Ok("attach", Args(("server", "auto"), ("project", CiSimRT.Dir)));
                c.Ok(
                    "setBreakpoints",
                    Args(("source", Src(CiSimRT.Conformance)), ("breakpoints", List(Bp(8))))
                );
                c.Ok("configurationDone");
                long t1 = c.Ok("ctsim/call", Args(("expr", "Conf_Tick()"), ("wait", false)))["task"].Long;
                Assert.Equal(t1, c.WaitEvent("stopped")["threadId"].Long);
                long t2 = c.Ok("ctsim/call", Args(("expr", "Conf_Tick()"), ("wait", false)))["task"].Long;
                Assert.Equal(t2, c.WaitEvent("stopped")["threadId"].Long);
                // the runtime's CONTINUE_ALL: both run on
                Assert.True(c.Ok("continue", Args(("threadId", t1)))["allThreadsContinued"].Bool);
                var exited = new HashSet<long>
                {
                    c.WaitEvent("thread", t => t["reason"].Str == "exited")["threadId"].Long,
                    c.WaitEvent("thread", t => t["reason"].Str == "exited")["threadId"].Long,
                };
                Assert.Equal(new HashSet<long> { t1, t2 }, exited);
                Assert.DoesNotContain(rig.Sim.Debugger.Tasks(), t => t.Paused);

                // a continue re-arms the foreground error report (once until the next Continue(null)):
                // the runtime's rule, with the simulator's page code stops off
                rig.Sim.Invoke(() => rig.Sim.ForegroundBreak = false);
                int Foreground() =>
                    rig.Sim.Invoke(() =>
                    {
                        var t = rig.Sim.Vm.TaskForCall(
                            "GetTagVal",
                            new[] { Value.Str("NoSuchTagXyz") },
                            foreground: true
                        );
                        rig.Sim.Scheduler.Add(t);
                        return t.Handle;
                    });
                bool Reported(int task)
                {
                    c.WaitEvent("thread", t => t["reason"].Str == "exited" && t["threadId"].Long == task);
                    return IndexOf(c, m =>
                            IsEvent(m, "output")
                            && m["body"]["output"].Str.Contains("in thread " + task + ",")
                        ) >= 0;
                }
                c.Ok("setExceptionBreakpoints", Args(("filters", Names("hardwareError"))));
                int f1 = Foreground();
                Assert.True(Reported(f1));
                int f2 = Foreground();
                Assert.False(Reported(f2));
                c.Ok("continue", Args(("threadId", 0)));
                int f3 = Foreground();
                Assert.True(Reported(f3));
                lock (c.Received)
                    Assert.Contains(c.Received, m =>
                        IsEvent(m, "output")
                        && m["body"]["output"].Str.Contains("in thread " + f3 + ",")
                        && m["body"]
                            ["output"]
                            .Str.EndsWith(
                                "[foreground Cicode is never suspended]\n",
                                StringComparison.Ordinal
                            )
                    );
                c.Ok("disconnect");
            }
        }

        [CiSimRTFact]
        public void TerminateWritesTheHaltOnce()
        {
            using (var rig = new DapRig())
            {
                var c = rig.Client;
                rig.Initialize();
                c.Ok(
                    "setBreakpoints",
                    Args(("source", Src(CiSimRT.Conformance)), ("breakpoints", List(Bp(155))))
                );
                c.Ok("launch", Args(("function", "Conf_IntArith")));
                c.Ok("configurationDone");
                c.WaitEvent("stopped");
                c.Ok("terminate");
                var end = c.WaitEvent("output", o => o["output"].Str.Contains(" halted: "));
                Assert.Equal("Conf_IntArith halted: terminated by the debugger\n", end["output"].Str);
                Assert.Equal("stderr", end["category"].Str);
                Assert.Equal(155, end["line"].Long);
                Assert.Equal("zz_conformance.ci", end["source"]["name"].Str, ignoreCase: true);
                Assert.Equal(1, c.WaitEvent("exited")["exitCode"].Long);
                c.WaitEvent("terminated");
                lock (c.Received)
                    Assert.Single(c.Received, m =>
                        IsEvent(m, "output") && m["body"]["output"].Str.Contains(" halted: ")
                    );
                c.Ok("disconnect");
            }
        }

        [CiSimRTFact]
        public void KeepRunningKeepsTheSessionAndTheReturnValue()
        {
            using (var rig = new DapRig())
            {
                var c = rig.Client;
                rig.Initialize();
                c.Ok(
                    "setBreakpoints",
                    Args(("source", Src(CiSimRT.Conformance)), ("breakpoints", List(Bp(8))))
                );
                c.Ok(
                    "launch",
                    Args(("function", "Conf_I"), ("args", List(JsonValue.Of(42))), ("keepRunning", true))
                );
                c.Ok("configurationDone");
                Assert.Equal(
                    "Conf_I returned \"42\"\n",
                    c.WaitEvent("output", o => IsOutput(o, "Conf_I returned"))["output"].Str
                );
                var rv = c.Ok("evaluate", Args(("expression", "(return value)"), ("context", "repl")));
                Assert.Equal("\"42\"", rv["result"].Str);
                Assert.Equal("STRING", rv["type"].Str);
                // the session lives on: a task started in it (ctsim/call) stops at a breakpoint,
                // and its frame shows the return value scope
                var call = c.Ok("ctsim/call", Args(("expr", "Conf_Tick()"), ("wait", false)));
                var stop = c.WaitEvent("stopped");
                Assert.Equal(call["task"].Long, stop["threadId"].Long);
                var frame = c.Ok("stackTrace", Args(("threadId", stop["threadId"].Long)))[
                    "stackFrames"
                ].Items[0];
                Assert.Equal(8, frame["line"].Long);
                var scopes = c.Ok("scopes", Args(("frameId", frame["id"].Long)))["scopes"].Items;
                var ret = scopes.Last();
                Assert.Equal("Return value", ret["name"].Str);
                Assert.Equal("returnValue", ret["presentationHint"].Str);
                var v = c.Ok("variables", Args(("variablesReference", ret["variablesReference"].Long)))[
                        "variables"
                    ]
                    .Items.Single();
                Assert.Equal("(return value)", v["name"].Str);
                Assert.Equal("\"42\"", v["value"].Str);
                Assert.False(
                    c.Request(
                        "setVariable",
                        Args(
                            ("variablesReference", ret["variablesReference"].Long),
                            ("name", "(return value)"),
                            ("value", "1")
                        )
                    )["success"].Bool
                );
                c.Ok("continue", Args(("threadId", stop["threadId"].Long)));
                c.WaitEvent("thread", t =>
                    t["reason"].Str == "exited" && t["threadId"].Long == stop["threadId"].Long
                );
                Assert.Equal(-1, IndexOf(c, m => IsEvent(m, "terminated")));
                // the client ends it
                c.Ok("terminate");
                c.WaitEvent("terminated");
                c.Ok("disconnect");
            }
        }

        [CiSimRTFact]
        public void AttachJoinsTheRunningSimulation()
        {
            using (var rig = new DapRig())
            {
                var c = rig.Client;
                rig.Initialize();
                // a project this server does not simulate is refused, as for launch
                var other = c.Request(
                    "attach",
                    Args(("server", "auto"), ("project", @"C:\NoSuchProjectXyz"))
                );
                Assert.False(other["success"].Bool);
                Assert.Contains("start a server for", other["message"].Str);
                c.Ok("attach", Args(("server", "auto"), ("project", CiSimRT.Dir)));
                c.Ok(
                    "setBreakpoints",
                    Args(("source", Src(CiSimRT.Conformance)), ("breakpoints", List(Bp(8))))
                );
                c.Ok("configurationDone");
                Assert.StartsWith(
                    "Attached to the simulation of CiSimRT",
                    c.WaitEvent("output")["output"].Str
                );
                Assert.False(c.Request("launch", Args(("function", "Conf_Tick")))["success"].Bool); // one start per session
                // a task of the simulation (here a viewer-style call) stops at the breakpoint
                rig.Sim.Invoke(() => rig.Sim.StartCall("Conf_Tick()", null));
                var stop = c.WaitEvent("stopped");
                Assert.Equal("breakpoint", stop["reason"].Str);
                c.Ok("pause", Args(("threadId", stop["threadId"].Long))); // already stopped: fine
                c.Ok("stepOut", Args(("threadId", stop["threadId"].Long)));
                // stepping off the outermost function parks the task; the session resumes it and it ends
                c.WaitEvent("thread", t =>
                    t["reason"].Str == "exited" && t["threadId"].Long == stop["threadId"].Long
                );
                c.Ok("disconnect");
                Assert.Equal(-1, IndexOf(c, m => IsEvent(m, "terminated")));
            }
        }

        [CiSimRTFact]
        public void DisconnectDetachesTheDebugger()
        {
            using (var rig = new DapRig())
            {
                var c = rig.Client;
                var ev = new SimEvents(rig.Sim);
                rig.Initialize();
                c.Ok(
                    "setBreakpoints",
                    Args(("source", Src(CiSimRT.Conformance)), ("breakpoints", List(Bp(155))))
                );
                c.Ok("setExceptionBreakpoints", Args(("filters", JsonValue.NewArray())));
                c.Ok("launch", Args(("function", "Conf_IntArith")));
                c.Ok("configurationDone");
                long tid = c.WaitEvent("stopped")["threadId"].Long;
                Assert.True(rig.Sim.Invoke(() => rig.Sim.Debugger.Attached));
                Assert.False(rig.Sim.Debugger.Options.BreakOnError);
                // terminateDebuggee false: the debugger leaves, the stopped task runs on to its end
                c.Ok("disconnect", Args(("terminateDebuggee", false)));
                var exit = ev.WaitExit((int)tid);
                Assert.Equal(TaskState.Stop, exit.State);
                SimTestUtil.Until(
                    rig.Sim,
                    () => !rig.Sim.Debugger.Attached && rig.Sim.Stepper.BreakpointCount == 0,
                    what: "the detach"
                );
                Assert.True(rig.Sim.Debugger.Options.BreakOnError); // the session's option given back
                var sw = Stopwatch.StartNew();
                while (c.Ended == null && sw.ElapsedMilliseconds < 10000)
                    Thread.Sleep(10);
                Assert.NotNull(c.Ended); // the server closed the connection

                // the next session; a disconnect while the launched call is stopped ends the call
                SimTestUtil.Until(
                    rig.Sim,
                    () => !rig.Server.DebuggerAttached,
                    what: "the old debugger leaving"
                );
                rig.Client.Dispose();
                rig.Client = Connect(rig.Server.Listener!.Transport, rig.Server.Listener.Address);
                c = rig.Client;
                rig.Initialize();
                c.Ok(
                    "setBreakpoints",
                    Args(("source", Src(CiSimRT.Conformance)), ("breakpoints", List(Bp(155))))
                );
                c.Ok("launch", Args(("function", "Conf_IntArith")));
                c.Ok("configurationDone");
                tid = c.WaitEvent("stopped")["threadId"].Long;
                c.Ok("disconnect");
                exit = ev.WaitExit((int)tid);
                Assert.Equal(TaskState.Halt, exit.State);
                Assert.Equal("terminated by the debugger", exit.HaltReason);
            }
        }

        [CiSimRTFact]
        public void ALateDetachDoesNotSilenceTheSession()
        {
            // SimServer.OnClosed detaches a closed session's debugger after the next session may
            // have started: the session attaches again when it starts, and with no debugger
            // attached an error is still written
            using (var rig = new DapRig())
            {
                var c = rig.Client;
                rig.Initialize();
                c.Ok("setExceptionBreakpoints", Args(("filters", Names("hardwareError"))));
                rig.Sim.Debugger.Detach();
                c.Ok("launch", Args(("function", "GetTagVal"), ("args", List(JsonValue.Of("NoSuchTagXyz")))));
                c.Ok("configurationDone");
                var stop = c.WaitEvent("stopped");
                Assert.Equal("exception", stop["reason"].Str);
                c.Ok("continue", Args(("threadId", stop["threadId"].Long)));
                c.WaitEvent("terminated");

                rig.NextSession();
                c = rig.Client;
                c.Ok("setExceptionBreakpoints", Args(("filters", Names("hardwareError"))));
                c.Ok(
                    "setBreakpoints",
                    Args(("source", Src(rig.FileOf("GetTagVal"))), ("breakpoints", List(Bp(23))))
                );
                c.Ok("launch", Args(("function", "GetTagVal"), ("args", List(JsonValue.Of("NoSuchTagXyz")))));
                c.Ok("configurationDone");
                Assert.Equal("breakpoint", c.WaitEvent("stopped")["reason"].Str);
                rig.Sim.Debugger.Detach(); // the task runs on, unattached
                Assert.Matches(
                    Err424,
                    c.WaitEvent("output", o => o["category"].Str == "stderr")["output"].Str
                );
                Assert.Equal(
                    "GetTagVal returned \"0\"\n",
                    c.WaitEvent("output", o => IsOutput(o, "GetTagVal"))["output"].Str
                );
                c.WaitEvent("terminated");
                lock (c.Received)
                    Assert.Single(c.Received, m => IsEvent(m, "stopped"));
            }
        }

        [CiSimRTFact]
        public void DapOverStdioEndsWithTheSession()
        {
            string disc = Path.Combine(Path.GetTempPath(), "ctsim-dap-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(disc);
            var psi = new ProcessStartInfo(
                ServerProcess.Exe,
                "dap --project "
                    + ServerProcess.Quote(CiSimRT.Dir)
                    + " --discovery "
                    + ServerProcess.Quote(disc)
                    + " --clock paused"
            )
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            try
            {
                using (var p = Process.Start(psi)!)
                {
                    var err = new System.Text.StringBuilder();
                    p.ErrorDataReceived += (s, e) =>
                    {
                        if (e.Data != null)
                            lock (err)
                                err.AppendLine(e.Data);
                    };
                    p.BeginErrorReadLine();
                    try
                    {
                        var c = new ProtocolClient(p.StandardOutput.BaseStream, p.StandardInput.BaseStream);
                        Assert.True(
                            c.Ok("initialize", Args(("adapterID", "cicode-sim")))[
                                "supportsTerminateRequest"
                            ].Bool
                        );
                        c.WaitEvent("initialized");
                        c.Ok("launch", Args(("function", "Conf_IntArith"), ("project", CiSimRT.Dir)));
                        c.Ok("configurationDone");
                        Assert.Equal(
                            "Conf_IntArith returned \"" + LiveResult("Conf_IntArith") + "\"\n",
                            c.WaitEvent("output", o => IsOutput(o, "Conf_IntArith returned"))["output"].Str
                        );
                        c.WaitEvent("terminated");
                        c.Ok("disconnect");
                        Assert.True(p.WaitForExit(15000), "ctsim dap did not exit: " + err);
                        Assert.Equal(0, p.ExitCode);
                        lock (c.Received)
                            Assert.All(c.Received, m => Assert.True(m.Get("type") != null));
                        Assert.Empty(Directory.GetFiles(disc));
                    }
                    finally
                    {
                        if (!p.HasExited)
                            p.Kill();
                    }
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
