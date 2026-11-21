using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CicodeSim.Host;
using CicodeSim.Sim;
using CicodeSim.Values;
using Xunit;
using Xunit.Abstractions;
using static CicodeSim.Tests.Host.ProtocolClient;

namespace CicodeSim.Tests.Host
{
    /// <summary>What a viewer's first requests cost the simulation.</summary>
    [Collection("ctsim server")]
    public sealed class ViewerLatencyTests
    {
        private readonly ITestOutputHelper _out;

        public ViewerLatencyTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static string Dump(CicodeSim.Page.CompiledPage cp)
        {
            var parts = new List<string> { Convert.ToBase64String(cp.Code) };
            foreach (var t in cp.Tables.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            foreach (var r in t.Value)
                parts.Add(
                    t.Key
                        + ":"
                        + string.Join(
                            "|",
                            r.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                                .Select(kv =>
                                    kv.Key
                                    + "="
                                    + Convert.ToString(
                                        kv.Value,
                                        System.Globalization.CultureInfo.InvariantCulture
                                    )
                                )
                        )
                );
            return string.Join("\n", parts);
        }

        /// <summary>The rows compiled against the running program's source unit (compile 'all')
        /// are the rows compiled against a unit of their own, record for record and byte for byte.</summary>
        [ProjectFact]
        public void RowsCompileAgainstTheRunningSourceUnitAsAgainstTheirOwn()
        {
            using (
                var sim = new Simulation(
                    new SimulationOptions
                    {
                        ProjectDir = SimCorpus.Example,
                        Compile = "all",
                        StartAlarms = false,
                    }
                )
            )
            {
                sim.Start();
                Assert.NotNull(sim.Live.Source);
                foreach (string page in new[] { "Mixer", "Alarm" })
                {
                    var shared = sim.Pages.CompileRows(page, null, new List<string>());
                    sim.Pages.NoSharedSource = true;
                    var own = sim.Pages.CompileRows(page, null, new List<string>());
                    sim.Pages.NoSharedSource = false;
                    Assert.NotNull(shared);
                    Assert.NotNull(own);
                    Assert.True(shared!.Tables.Values.Sum(t => t.Count) > 50, page);
                    Assert.Equal(Dump(own!), Dump(shared));
                }
            }
        }

        /// <summary>A reload that does not compile leaves the source unit ahead of the code that
        /// runs: page rows then compile against a unit of their own, and against the running
        /// unit again once a reload compiles.</summary>
        [ProjectFact]
        public void RowsTakeTheirOwnUnitWhileAReloadDoesNotCompile()
        {
            using (
                var sim = new Simulation(
                    new SimulationOptions
                    {
                        ProjectDir = SimCorpus.Example,
                        Compile = "all",
                        StartAlarms = false,
                    }
                )
            )
            {
                sim.Start();
                Assert.NotNull(sim.Live.Source);
                string file = sim.Live.Source!.Files.First(f =>
                    f.EndsWith(".ci", StringComparison.OrdinalIgnoreCase)
                    && f.StartsWith(SimCorpus.Example + "\\", StringComparison.OrdinalIgnoreCase)
                );
                Assert.NotNull(sim.Pages.CompileRows("Mixer", null, new List<string>()));
                Assert.True(sim.Pages.LastRowsShared);
                var broken = sim.Invoke(() =>
                    sim.Reload(file, File.ReadAllText(file) + "\r\nFUNCTION Broken_Reload(\r\n")
                );
                _out.WriteLine(broken.ToString());
                Assert.False(sim.Live.SourceInStep);
                Assert.NotNull(sim.Pages.CompileRows("Mixer", null, new List<string>()));
                Assert.False(sim.Pages.LastRowsShared);
                sim.Invoke(() => sim.Reload(file, null));
                Assert.True(sim.Live.SourceInStep);
                Assert.NotNull(sim.Pages.CompileRows("Mixer", null, new List<string>()));
                Assert.True(sim.Pages.LastRowsShared);
            }
        }

        /// <summary>ctsim/tags builds the tag listing off the simulation thread, once.</summary>
        [ProjectFact]
        public void TheTagListingIsBuiltOffTheSimulationThread()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            using (var server = new SimServer(sim, "lat-" + Guid.NewGuid().ToString("N").Substring(0, 8)))
            {
                string disc = Path.Combine(Path.GetTempPath(), "ctsim-lat-" + Guid.NewGuid().ToString("N"));
                try
                {
                    server.Listen(disc);
                    using (var c = Connect(server.Listener!.Address))
                    {
                        c.Ok("ctsim/hello", Args(("client", "latency")));
                        var a = c.Ok("ctsim/tags", Args(("start", 0), ("count", 10)));
                        Assert.True(a["total"].Long > 10);
                        Assert.False(server.ListingBuiltOnSimThread);
                        var b = c.Ok("ctsim/tags", Args(("start", 0), ("count", 10)));
                        Assert.Equal(a["total"].Long, b["total"].Long);
                        Assert.Equal(
                            a["tags"].Items.Select(t => t["name"].Str),
                            b["tags"].Items.Select(t => t["name"].Str)
                        );
                    }
                }
                finally
                {
                    try
                    {
                        Directory.Delete(disc, true);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
    }
}
