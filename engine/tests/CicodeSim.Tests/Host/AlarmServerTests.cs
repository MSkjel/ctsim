using System;
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
    /// <summary>The viewer's alarm and equipment lists: ctsim/alarms,
    /// ctsim/equipment and ctsim/alarmsChanged on the R2 ExampleSA project (read only), in
    /// process over a real pipe.</summary>
    [Collection("ctsim server")]
    public sealed class AlarmServerTests
    {
        [ProjectFact]
        public void AlarmsAndEquipmentOverTheProtocol()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.ExampleSA))
            using (var server = new SimServer(sim, "alarms-" + Guid.NewGuid().ToString("N").Substring(0, 8)))
            {
                string disc = Path.Combine(
                    Path.GetTempPath(),
                    "ctsim-alarms-" + Guid.NewGuid().ToString("N")
                );
                try
                {
                    server.Listen(disc);
                    using (var c = Connect(server.Listener!.Address))
                    {
                        var h = c.Ok("ctsim/hello", Args(("client", "alarms")));
                        Assert.True(h["capabilities"]["alarms"].Bool);
                        Assert.True(h["protocolRevision"].Long >= 10);
                        // every configured alarm, paged
                        var all = c.Ok("ctsim/alarms");
                        long total = all["total"].Long;
                        Assert.True(total > 100);
                        var page = c.Ok("ctsim/alarms", Args(("start", 5), ("count", 10)));
                        Assert.Equal(10, page["alarms"].Count);
                        Assert.Equal(5, page["start"].Long);
                        Assert.Equal(all["alarms"].Items[5]["tag"].Str, page["alarms"].Items[0]["tag"].Str);
                        // a filter on the tag, name or equipment.item
                        var ov = c.Ok("ctsim/alarms", Args(("filter", "OV08_Fault")));
                        var a = ov["alarms"].Items.Single(x => x["tag"].Str == "OV08_Fault");
                        Assert.Equal("off", a["state"].Str);
                        Assert.Equal("digital", a["type"].Str);
                        Assert.False(a["on"].Bool);
                        Assert.Equal("", a["onTime"].Str);
                        Assert.Equal("Company.TopMilk.Mixing.OV08", a["equipment"].Str);
                        // a regular expression, a refused one; the equipment list too
                        var rx = c.Ok("ctsim/alarms", Args(("filter", "^ov0[0-9]_fault$"), ("regex", true)));
                        Assert.Contains(rx["alarms"].Items, x => x["tag"].Str == "OV08_Fault");
                        Assert.False(
                            c.Request("ctsim/alarms", Args(("filter", "[ov"), ("regex", true)))[
                                "success"
                            ].Bool
                        );
                        var eqx = c.Ok(
                            "ctsim/equipment",
                            Args(("filter", @"topmilk\.mixing$"), ("regex", true))
                        );
                        Assert.Contains(eqx["equipment"].Items, x =>
                            x["name"].Str == "Company.TopMilk.Mixing"
                        );
                        Assert.DoesNotContain(eqx["equipment"].Items, x => x["name"].Str.EndsWith(".OV08"));
                        // equipment: that one and its children
                        var mix = c.Ok("ctsim/alarms", Args(("equipment", "Company.TopMilk.Mixing")));
                        Assert.Contains(mix["alarms"].Items, x => x["tag"].Str == "OV08_Fault");
                        Assert.All(mix["alarms"].Items, x =>
                            Assert.StartsWith(
                                "Company.TopMilk.Mixing",
                                x["equipment"].Str,
                                StringComparison.OrdinalIgnoreCase
                            )
                        );
                        // a state change: the list follows, alarmsChanged tells the viewer
                        c.Ok("ctsim/setAlarm", Args(("name", "OV08_Fault"), ("state", "on")));
                        var ch = c.WaitEvent("ctsim/alarmsChanged", e => e["serial"].Long > 0, 5000);
                        Assert.True(ch["serial"].Long > 0);
                        a = c.Ok("ctsim/alarms", Args(("filter", "OV08_Fault")))["alarms"]
                            .Items.Single(x => x["tag"].Str == "OV08_Fault");
                        Assert.Equal("unack_on", a["state"].Str);
                        Assert.True(a["on"].Bool);
                        Assert.False(a["ack"].Bool);
                        Assert.NotEqual("", a["onTime"].Str);
                        // the alarms a compiled page reads, or of the equipment its tags belong to
                        var pg = c.Ok("ctsim/alarms", Args(("page", "Mixing_L3")));
                        Assert.True(pg["total"].Long > 0 && pg["total"].Long < total);
                        // equipment with the runtime fields: scheduled = 0 / DEFSTATE / 0, else Unknown
                        var eq = c.Ok("ctsim/equipment", Args(("filter", "Company.TopMilk.Mixing")));
                        var m = eq["equipment"].Items.Single(x => x["name"].Str == "Company.TopMilk.Mixing");
                        Assert.True(m["scheduled"].Bool);
                        Assert.Equal(("0", "Off", "0"), (m["MODE"].Str, m["STATE"].Str, m["DRMODE"].Str));
                        var d = c.Ok("ctsim/equipment", Args(("filter", "Company.Library.Drive01")))[
                                "equipment"
                            ]
                            .Items.Single(x => x["name"].Str == "Company.Library.Drive01");
                        Assert.False(d["scheduled"].Bool);
                        Assert.Equal(
                            ("Unknown", "Unknown", "Unknown"),
                            (d["MODE"].Str, d["STATE"].Str, d["DRMODE"].Str)
                        );
                        Assert.True(c.Ok("ctsim/equipment")["total"].Long > 50);
                        var bad = c.Request("ctsim/alarms", Args(("start", -1)));
                        Assert.False(bad["success"].Bool);
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
        public void AHaltingCicodeErrorRaisesAHardwareAlarm()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.ExampleSA))
            {
                CallOutcome? Run(string expr)
                {
                    var done = new ManualResetEventSlim(false);
                    CallOutcome? o = null;
                    sim.Invoke(() => sim.StartCall(expr, x =>
                        {
                            o = x;
                            done.Set();
                        }));
                    Assert.True(done.Wait(10000), expr + " was never answered");
                    return o;
                }
                long g0 = sim.Invoke(() => sim.World.Alarms.Generation);
                var bad = Run("_PltLineStyleGet(1000)")!;
                Assert.Equal("halt", bad.State);
                Assert.Equal(259, bad.Error); // an array overrun in Include graph.ci
                var hw = sim.Invoke(() =>
                    sim.World.Alarms.HardwareAlarms.Find(x => x.Code == bad.Error && x.Name == "Cicode")
                );
                Assert.NotNull(hw);
                Assert.Equal("_PltLineStyleGet", hw!.Page); // no built-in failed: the function
                Assert.True(sim.Invoke(() => hw.St.On && !hw.St.Ack && hw.St.InList));
                Assert.True(sim.Invoke(() => sim.World.Alarms.Generation) > g0);
                // the same error again counts up, no second alarm
                Run("_PltLineStyleGet(1000)");
                Assert.Equal(
                    1,
                    sim.Invoke(() =>
                        sim
                            .World.Alarms.HardwareAlarms.FindAll(x =>
                                x.Code == bad.Error && x.Name == "Cicode"
                            )
                            .Count
                    )
                );
                Assert.Equal(2, sim.Invoke(() => hw.Count));
                // a function with no error raises none
                int n = sim.Invoke(() => sim.World.Alarms.HardwareAlarms.Count);
                Assert.Equal("stop", Run("IntToStr(5)")!.State);
                Assert.Equal(n, sim.Invoke(() => sim.World.Alarms.HardwareAlarms.Count));
            }
        }
    }
}
