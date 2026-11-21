using System;
using System.Linq;
using CicodeSim.Values;
using Xunit;
using static CicodeSim.Tests.Host.ProtocolClient;

namespace CicodeSim.Tests.Host
{
    /// <summary>Variable history, time travel and data breakpoints through the DAP session.</summary>
    [Collection("ctsim server")]
    public sealed class DapHistoryTests
    {
        private static JsonValue Bps(params int[] lines)
        {
            var a = JsonValue.NewArray();
            foreach (int l in lines)
                a.Add(Args(("line", l)));
            return Args(("source", Args(("path", CiSimRT.Conformance))), ("breakpoints", a));
        }

        private static JsonValue Top(ProtocolClient c, long tid) =>
            c.Ok("stackTrace", Args(("threadId", tid)))["stackFrames"].Items[0];

        private static JsonValue Locals(ProtocolClient c, JsonValue frame)
        {
            var scope = c.Ok("scopes", Args(("frameId", frame["id"].Long)))["scopes"]
                .Items.First(s => s["name"].Str == "Locals");
            return c.Ok("variables", Args(("variablesReference", scope["variablesReference"].Long)));
        }

        [CiSimRTFact]
        public void VariablesCarryTheirHistoryAndTheViewTravelsBackAndForward()
        {
            using (var rig = new DapRig())
            {
                var c = rig.Client;
                var caps = rig.Initialize();
                Assert.True(caps["supportsStepBack"].Bool);
                Assert.True(caps["supportsDataBreakpoints"].Bool);
                c.Ok("setBreakpoints", Bps(195));
                c.Ok("launch", Args(("function", "Conf_ForTo")));
                c.Ok("configurationDone");
                long tid = c.WaitEvent("stopped")["threadId"].Long;
                var f0 = Top(c, tid);
                Assert.Equal(195, f0["line"].Long);

                // count has children: its changes, oldest first, the last one 2 -> 3 on line 193
                var count = Locals(c, f0)["variables"].Items.Single(v => v["name"].Str == "count");
                Assert.Equal("3", count["value"].Str);
                Assert.NotEqual(0, count["variablesReference"].Long);
                var hist = c.Ok("variables", Args(("variablesReference", count["variablesReference"].Long)))[
                    "variables"
                ].Items;
                Assert.True(hist.Count >= 3, "changes: " + hist.Count);
                Assert.StartsWith(hist.Count + ": line 193", hist.Last()["name"].Str);
                Assert.StartsWith("2 -> 3", hist.Last()["value"].Str);
                Assert.EndsWith(" s", hist.Last()["value"].Str);

                // cicode/history: the call's changes for the inline values
                var ch = c.Ok("cicode/history", Args(("frameId", f0["id"].Long)));
                Assert.Equal(-1, ch["cursor"].Long);
                var countWrites = ch["changes"]
                    .Items.Where(x => x["name"].Str == "count" && !x["entry"].Bool && x["line"].Long == 193)
                    .Select(x => x["value"].Str)
                    .ToArray();
                Assert.Equal(new[] { "1", "2", "3" }, countWrites);

                // step back: a stop at an earlier line, the view in the past
                c.Ok("stepBack", Args(("threadId", tid)));
                var back = c.WaitEvent("stopped", s => s["reason"].Str == "step");
                Assert.Equal("Paused (history)", back["description"].Str);
                long lineBack = Top(c, tid)["line"].Long;
                Assert.True(lineBack < 195, "line " + lineBack);
                Assert.NotEqual(
                    -1,
                    c.Ok("cicode/history", Args(("frameId", Top(c, tid)["id"].Long)))["cursor"].Long
                );

                // next in the past: one line forward in the recording
                c.Ok("next", Args(("threadId", tid)));
                c.WaitEvent("stopped", s => s["reason"].Str == "step");
                Assert.NotEqual(lineBack, Top(c, tid)["line"].Long);

                // reverse continue with no earlier breakpoint: the start of the recording
                c.Ok("reverseContinue", Args(("threadId", tid)));
                // (the note comes before the stop)
                c.WaitEvent("output", o => o["output"].Str.Contains("the start of its recorded history"));
                c.WaitEvent("stopped", s => s["reason"].Str == "step");
                // the call's first line marker, where its entry values were recorded
                long entryLine = ch["changes"].Items.First(x => x["entry"].Bool)["line"].Long;
                Assert.Equal(entryLine, Top(c, tid)["line"].Long);

                // step out in the past: back to now, at the breakpoint
                c.Ok("stepOut", Args(("threadId", tid)));
                var live = c.WaitEvent("stopped", s => s["reason"].Str == "step");
                Assert.False(live.Has("description"));
                Assert.Equal(195, Top(c, tid)["line"].Long);
                c.Ok("continue", Args(("threadId", tid)));
                Assert.Equal(
                    "Conf_ForTo returned \"count=3;i_after=4\"\n",
                    c.WaitEvent("output", o => o["output"].Str.StartsWith("Conf_ForTo returned"))[
                        "output"
                    ].Str
                );
                c.WaitEvent("terminated");
            }
        }

        [CiSimRTFact]
        public void ADataBreakpointOnALocalStopsAfterEachWrite()
        {
            using (var rig = new DapRig())
            {
                var c = rig.Client;
                rig.Initialize();
                c.Ok("launch", Args(("function", "Conf_ForTo"), ("stopOnEntry", true)));
                c.Ok("configurationDone");
                long tid = c.WaitEvent("stopped")["threadId"].Long;
                var f0 = Top(c, tid);
                var scope = c.Ok("scopes", Args(("frameId", f0["id"].Long)))["scopes"]
                    .Items.First(s => s["name"].Str == "Locals");
                var info = c.Ok(
                    "dataBreakpointInfo",
                    Args(("variablesReference", scope["variablesReference"].Long), ("name", "count"))
                );
                Assert.Equal("Conf_ForTo.count", info["description"].Str);
                Assert.Equal("write", info["accessTypes"].Items.Single().Str);
                var set = c.Ok(
                    "setDataBreakpoints",
                    Args(("breakpoints", List(Args(("dataId", info["dataId"].Str)))))
                );
                var bp = set["breakpoints"].Items.Single();
                Assert.True(bp["verified"].Bool);

                // each write of count stops the task at its next line
                var lines = new System.Collections.Generic.List<long>();
                for (int k = 0; k < 4; k++)
                {
                    c.Ok("continue", Args(("threadId", tid)));
                    var stop = c.WaitEvent("stopped");
                    Assert.Equal("data breakpoint", stop["reason"].Str);
                    Assert.Equal(bp["id"].Long, stop["hitBreakpointIds"].Items.Single().Long);
                    lines.Add(Top(c, tid)["line"].Long);
                }
                Assert.Equal(new long[] { 191, 194, 194, 194 }, lines.ToArray());

                // a name that is no variable: no dataId
                var none = c.Ok("dataBreakpointInfo", Args(("name", "1 + 2")));
                Assert.True(none["dataId"].IsNull);
                c.Ok("setDataBreakpoints", Args(("breakpoints", JsonValue.NewArray())));
                c.Ok("continue", Args(("threadId", tid)));
                Assert.StartsWith(
                    "Conf_ForTo returned",
                    c.WaitEvent("output", o => o["output"].Str.StartsWith("Conf_ForTo returned"))[
                        "output"
                    ].Str
                );
            }
        }

        private static JsonValue List(params JsonValue[] items)
        {
            var a = JsonValue.NewArray();
            foreach (var i in items)
                a.Add(i);
            return a;
        }
    }
}
