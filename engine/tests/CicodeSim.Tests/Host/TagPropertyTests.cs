using System;
using System.IO;
using CicodeSim.Host;
using CicodeSim.Sim;
using CicodeSim.Values;
using CicodeSim.World;
using Xunit;
using static CicodeSim.Tests.Host.ProtocolClient;

namespace CicodeSim.Tests.Host
{
    /// <summary>A simulation overrides a tag's definition fields (ctsim/setTagProperty):
    /// TagGetProperty, the scaling and the tag listing read the override; set back, the
    /// project's values again. Example's FILTER_TANK_LEV: INT, 0 .. 100, format '###'.</summary>
    public class TagPropertyTests
    {
        private static string Eval(Simulation sim, string expr)
        {
            var t = sim.Invoke(() => sim.StartCall(expr, null));
            SimTestUtil.Until(sim, () => t.Done, what: expr);
            var r = t.Result;
            return r == null ? ""
                : r.Value.T == Types.String ? r.Value.S ?? ""
                : r.Value.ToString();
        }

        private static double Num(string s) =>
            double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

        [ProjectFact]
        public void AnOverriddenPropertyIsWhatCicodeReadsUntilItIsSetBack()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            {
                const string tag = "FILTER_TANK_LEV";
                Assert.Equal(100.0, Num(Eval(sim, "TagGetProperty(\"" + tag + "\", \"EngUnitsHigh\")")));
                var d = sim.Invoke(() => sim.World.Tags.SetProperty(tag, "eng_full", "250"));
                Assert.NotNull(d);
                Assert.Equal(250.0, Num(Eval(sim, "TagGetProperty(\"" + tag + "\", \"EngUnitsHigh\")")));
                Assert.Equal("100", d!.Configured("ENG_FULL"));
                sim.Invoke(() => sim.World.Tags.SetProperty(tag, "COMMENT", "Overridden"));
                Assert.Equal("Overridden", Eval(sim, "TagGetProperty(\"" + tag + "\", \"Description\")"));
                Assert.Same(d, sim.Invoke(() => sim.World.Tags.Overridden(tag)));
                // one field back, then every field back
                sim.Invoke(() => sim.World.Tags.SetProperty(tag, "ENG_FULL", null));
                Assert.Equal(100.0, Num(Eval(sim, "TagGetProperty(\"" + tag + "\", \"EngUnitsHigh\")")));
                Assert.Equal("Overridden", Eval(sim, "TagGetProperty(\"" + tag + "\", \"Description\")"));
                sim.Invoke(() => sim.World.Tags.SetProperty(tag, null, null));
                Assert.Equal(
                    "Filter Tank Level",
                    Eval(sim, "TagGetProperty(\"" + tag + "\", \"Description\")")
                );
                Assert.Null(sim.Invoke(() => sim.World.Tags.Overridden(tag)));
                // the tag's identity cannot be overridden; a range field takes numbers only
                Assert.Throws<ArgumentException>(() =>
                    sim.Invoke(() => sim.World.Tags.SetProperty(tag, "TYPE", "REAL"))
                );
                Assert.Throws<ArgumentException>(() =>
                    sim.Invoke(() => sim.World.Tags.SetProperty(tag, "ENG_ZERO", "low"))
                );
                Assert.Null(sim.Invoke(() => sim.World.Tags.SetProperty("NoSuchTagXyz", "COMMENT", "x")));
            }
        }

        [ProjectFact]
        public void SetTagPropertyOverTheProtocol()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            using (
                var server = new SimServer(sim, "tagprops-" + Guid.NewGuid().ToString("N").Substring(0, 8))
            )
            {
                string disc = Path.Combine(
                    Path.GetTempPath(),
                    "ctsim-tagprops-" + Guid.NewGuid().ToString("N")
                );
                try
                {
                    server.Listen(disc);
                    using (var c = Connect(server.Listener!.Address))
                    {
                        var h = c.Ok("ctsim/hello", Args(("client", "tagprops")));
                        Assert.True(h["capabilities"]["tagProperties"].Bool);
                        var e = c.Ok(
                            "ctsim/setTagProperty",
                            Args(("name", "filter_tank_lev"), ("field", "FORMAT"), ("value", "###.##"))
                        );
                        Assert.Equal("FILTER_TANK_LEV", e["name"].Str);
                        Assert.Equal("###.##", e["format"].Str);
                        Assert.Equal("###.##", e["overrides"]["FORMAT"].Str);
                        Assert.Equal("###", e["configured"]["FORMAT"].Str);
                        Assert.Equal(0.0, e["rawZero"].ToNumber());
                        var ev = c.WaitEvent("ctsim/tagDefinitionsChanged");
                        Assert.Equal("FILTER_TANK_LEV", ev["names"].Items[0].Str);
                        // the listing carries it too
                        var t = c.Ok("ctsim/tags", Args(("names", Names("FILTER_TANK_LEV"))));
                        Assert.Equal("###.##", t["tags"].Items[0]["format"].Str);
                        // back to the project's
                        var back = c.Ok("ctsim/setTagProperty", Args(("name", "FILTER_TANK_LEV")));
                        Assert.Equal("###", back["format"].Str);
                        Assert.Null(back.Get("overrides"));
                        Assert.False(
                            c.Request(
                                "ctsim/setTagProperty",
                                Args(("name", "FILTER_TANK_LEV"), ("field", "TYPE"), ("value", "REAL"))
                            )["success"].Bool
                        );
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

        [Fact]
        public void TheRangeOverrideRescalesTheStoredRawValue()
        {
            var d = new TagDef(
                "T",
                TagDef.Variable,
                "P",
                "INT",
                rawZero: 0,
                rawFull: 100,
                engZero: 0,
                engFull: 100
            );
            Assert.Equal(50.0, d.ToEng(50));
            d.SetOverride("ENG_FULL", "200");
            Assert.Equal(100.0, d.ToEng(50)); // the same raw value, the new range
            Assert.Equal("200", d.Overrides["ENG_FULL"]);
            d.ClearOverrides();
            Assert.Equal(50.0, d.ToEng(50));
            Assert.False(d.HasOverrides);
        }
    }
}
