using System;
using System.IO;
using System.Linq;
using CicodeSim.Host;
using CicodeSim.Values;
using Xunit;
using static CicodeSim.Tests.Host.ProtocolClient;

namespace CicodeSim.Tests.Host
{
    /// <summary>The simulator tools: ctsim/profile (where the time goes: scans, page expressions,
    /// task slices) and the scenario generator square.</summary>
    public class SimToolsTests
    {
        private static void WithServer(Action<ProtocolClient> body)
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            using (var server = new SimServer(sim, "tools-" + Guid.NewGuid().ToString("N").Substring(0, 8)))
            {
                string disc = Path.Combine(Path.GetTempPath(), "ctsim-tools-" + Guid.NewGuid().ToString("N"));
                try
                {
                    server.Listen(disc);
                    using (var c = Connect(server.Listener!.Address))
                    {
                        c.Ok("ctsim/hello", Args(("client", "tools")));
                        body(c);
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

        [ProjectFact]
        public void TheProfileTimesScansExpressionsAndTasks()
        {
            WithServer(c =>
            {
                var off = c.Ok("ctsim/profile");
                Assert.False(off["running"].Bool);
                Assert.Equal(0, off["expressions"].Count);
                c.Ok("ctsim/profile", Args(("action", "start")));
                c.Ok("ctsim/openPage", Args(("name", "Mixer")));
                c.Ok("ctsim/clock", Args(("mode", "step"), ("value", 2.0)));
                var r = c.Ok("ctsim/profile", Args(("action", "stop"), ("top", 5)));
                Assert.False(r["running"].Bool);
                var scan = r["scans"].Items.Single(s => s["page"].Str == "Mixer");
                Assert.True(scan["count"].Long > 0 && scan["ms"].ToNumber() > 0);
                Assert.Equal(5, r["expressions"].Count);
                Assert.True(r["expressionsTotal"].Long > 5);
                var top = r["expressions"].Items[0];
                Assert.True(top["an"].Long > 0 && top["class"].Str.Length > 0 && top["page"].Str == "Mixer");
                Assert.True(
                    r["expressions"].Items[0]["ms"].ToNumber() >= r["expressions"].Items[4]["ms"].ToNumber()
                );
                // each function the page's code called, built-ins included
                Assert.True(r["functionsTotal"].Long > 0);
                var fn = r["functions"].Items[0];
                Assert.True(fn["name"].Str.Length > 0 && fn["calls"].Long > 0 && fn["callers"].Count > 0);
                // the call tree only when asked
                Assert.Null(r.Get("tree"));
                var t = c.Ok("ctsim/profile", Args(("tree", true)))["tree"];
                Assert.True(t.Count > 0 && t.Items[0]["kind"].Str == "origin");
                // stopped: nothing more is timed; reset forgets
                long n = scan["count"].Long;
                c.Ok("ctsim/clock", Args(("mode", "step"), ("value", 1.0)));
                Assert.Equal(
                    n,
                    c.Ok("ctsim/profile")["scans"].Items.Single(s => s["page"].Str == "Mixer")["count"].Long
                );
                Assert.Equal(0, c.Ok("ctsim/profile", Args(("action", "reset")))["scans"].Count);
            });
        }

        [ProjectFact]
        public void ASquareGeneratorSwitchesBetweenMinAndMax()
        {
            WithServer(c =>
            {
                var sc = JsonValue.Parse(
                    "{\"format\": \"ctsim-scenario/0\", \"steps\": [{\"at\": 0, \"square\": "
                        + "{\"tag\": \"FILTER_TANK_LEV\", \"min\": 10, \"max\": 90, \"period\": 2, \"duty\": 0.5, \"every\": 0.25}}]}"
                );
                c.Ok("ctsim/scenario", Args(("action", "run"), ("scenario", sc)));
                double Val() =>
                    c.Ok("ctsim/tags", Args(("names", Names("FILTER_TANK_LEV"))))["tags"]
                        .Items[0]["value"]
                        .ToNumber();
                // (the step at 0 runs when the clock moves: the test's clock is paused)
                c.Ok("ctsim/clock", Args(("mode", "step"), ("value", 0.1)));
                Assert.Equal(10.0, Val());
                c.Ok("ctsim/clock", Args(("mode", "step"), ("value", 1.15)));
                Assert.Equal(90.0, Val());
                c.Ok("ctsim/clock", Args(("mode", "step"), ("value", 1.0)));
                Assert.Equal(10.0, Val());
                c.Ok("ctsim/scenario", Args(("action", "stop")));
            });
        }
    }
}
