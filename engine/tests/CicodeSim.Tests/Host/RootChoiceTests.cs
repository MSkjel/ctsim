using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CicodeSim.Host;
using CicodeSim.Project;
using CicodeSim.Sim;
using CicodeSim.Tests.World;
using CicodeSim.Values;
using Xunit;
using static CicodeSim.Tests.Host.ProtocolClient;
using static CicodeSim.Tests.World.WorldFixture;

namespace CicodeSim.Tests.Host
{
    /// <summary>The root a simulation runs as.</summary>
    [Collection("World")]
    public sealed partial class RootChoiceTests : IDisposable
    {
        private readonly TempDir _tmp = new TempDir();
        private readonly string _user;

        public RootChoiceTests()
        {
            TagScope.Clear();
            _user = Path.Combine(_tmp.Path, "inst", "User");
            var dirs = new Dictionary<string, string>();
            foreach (string p in new[] { "RootA", "RootB", "Lib", "Solo", "Include" })
            {
                dirs[p] = Path.Combine(_user, p);
                Directory.CreateDirectory(dirs[p]);
            }
            WriteDbf(
                Path.Combine(_user, "MASTER.DBF"),
                Master,
                dirs.Select(kv => Row(("NAME", kv.Key), ("PATH", kv.Value)))
            );
            WriteDbf(
                Path.Combine(dirs["RootA"], "include.dbf"),
                WorldFixture.Names,
                new[] { Row(("NAME", "Lib")) }
            );
            WriteDbf(
                Path.Combine(dirs["RootB"], "include.dbf"),
                WorldFixture.Names,
                new[] { Row(("NAME", "Lib")) }
            );
            // a tag defined only in a project that compiles Lib
            WriteDbf(
                Path.Combine(dirs["RootA"], "variable.dbf"),
                Variable,
                new[] { Var("A_Only", "DIGITAL") }
            );
            WriteDbf(
                Path.Combine(dirs["RootB"], "variable.dbf"),
                Variable,
                new[] { Var("B_Only", "DIGITAL") }
            );
            WriteDbf(
                Path.Combine(dirs["Lib"], "variable.dbf"),
                Variable,
                new[] { Var("Lib_Tag", "DIGITAL") }
            );
            // not a library: loading it would fail, so the test proves it is not the program
            File.WriteAllText(Path.Combine(dirs["Lib"], "_LIBRARY.RDB"), "not an RDB");
        }

        public void Dispose()
        {
            TagScope.Clear();
            _tmp.Dispose();
        }

        private string Dir(string p) => Path.Combine(_user, p);

        private static Simulation Sim(string dir, string? root = null) =>
            new Simulation(
                new SimulationOptions
                {
                    ProjectDir = dir,
                    Root = root,
                    ClockMode = "paused",
                    StartAlarms = false,
                    TimeZone = TimeZoneInfo.Utc,
                }
            );

        [Fact]
        public void PickRootForListsTheRootsWhoseClosureHoldsTheProject()
        {
            var lib = TagScope.PickRootFor(Dir("Lib"));
            Assert.Equal(new[] { "RootA", "RootB" }, lib.Candidates);
            Assert.Equal("RootA", lib.Root);
            Assert.Contains(lib.Reason, new[] { RootPick.FirstByName, RootPick.LastDatabase });
            var asked = TagScope.PickRootFor(Dir("Lib"), "rootb");
            Assert.Equal("RootB", asked.Root);
            Assert.Equal(RootPick.Asked, asked.Reason);
            var a = TagScope.PickRootFor(Dir("RootA"));
            Assert.Equal("RootA", a.Root);
            Assert.Equal(RootPick.Self, a.Reason);
            Assert.Equal(new[] { "RootA" }, a.Candidates);
            var solo = TagScope.PickRootFor(Dir("Solo"));
            Assert.Equal(RootPick.Self, solo.Reason);
        }

        [Fact]
        public void AnIncludedProjectRunsAsTheRootThatCompilesItNotItsOwnLibrary()
        {
            using var sim = Sim(Dir("Lib"));
            Assert.Equal("RootA", sim.Root);
            Assert.Equal(new[] { "RootA", "RootB" }, sim.RootCandidates);
            Assert.Contains(sim.RootReason, new[] { RootPick.FirstByName, RootPick.LastDatabase });
            // the root's unit: RootA's tags are defined, RootB's are not
            Assert.NotNull(sim.World.Tags.Find("A_Only"));
            Assert.NotNull(sim.World.Tags.Find("Lib_Tag"));
            Assert.Null(sim.World.Tags.Find("B_Only"));
            var info = sim.Info();
            Assert.Equal("RootA", info["root"].Str);
            Assert.Equal(sim.RootReason, info["rootReason"].Str);
            Assert.Equal(new[] { "RootA", "RootB" }, info["roots"].Items.Select(x => x.Str).ToArray());
        }

        [Fact]
        public void AnAskedRootIsTakenAndOneThatDoesNotCompileTheProjectIsAnError()
        {
            using (var sim = Sim(Dir("Lib"), "RootB"))
            {
                Assert.Equal("RootB", sim.Root);
                Assert.Equal(RootPick.Asked, sim.RootReason);
                Assert.NotNull(sim.World.Tags.Find("B_Only"));
                Assert.Null(sim.World.Tags.Find("A_Only"));
            }
            var e = Assert.Throws<ArgumentException>(() => Sim(Dir("Lib"), "Solo").Dispose());
            Assert.Contains("not compiled as part of", e.Message);
            Assert.Contains("RootA, RootB", e.Message);
            Assert.Throws<ArgumentException>(() => Sim(Dir("Lib"), "Nowhere").Dispose());
        }

        [Fact]
        public void ARootRunsAsItself()
        {
            using var sim = Sim(Dir("RootB"));
            Assert.Equal("RootB", sim.Root);
            Assert.Equal(RootPick.Self, sim.RootReason);
        }

        [Fact]
        public void TheDiscoveryEntryCarriesTheRootAndMatchesOnlyThatRoot()
        {
            string disc = Path.Combine(_tmp.Path, "disc");
            Directory.CreateDirectory(disc);
            using var sim = Sim(Dir("Lib"));
            sim.Start();
            using var server = new SimServer(sim);
            server.Listen(disc);
            var entry = Discovery.List(disc).Single();
            Assert.Equal("RootA", entry.Root);
            Assert.Equal(new[] { "RootA", "RootB" }, entry.Roots);
            Assert.Equal(sim.RootReason, entry.RootReason);
            Assert.True(entry.Matches(Dir("Lib")));
            Assert.True(entry.Matches(Dir("Lib"), "roota"));
            Assert.True(entry.Matches("Lib", "RootA"));
            Assert.False(entry.Matches(Dir("Lib"), "RootB"));
            Assert.False(entry.Matches(Dir("RootA"), "RootA"));

            using var c = Connect(server.Listener!.Transport, server.Listener.Address);
            var hello = c.Ok("ctsim/hello", Args(("client", "test"), ("root", "RootA")));
            Assert.Equal("RootA", hello["root"].Str);
            Assert.Equal(new[] { "RootA", "RootB" }, hello["roots"].Items.Select(x => x.Str).ToArray());
            var bad = c.Request("ctsim/load", Args(("project", Dir("Lib")), ("root", "RootB")));
            Assert.False(bad["success"].Bool);
            Assert.Contains("--root RootB", bad["message"].Str);
            Assert.True(
                c.Request("ctsim/load", Args(("project", Dir("Lib")), ("root", "rootA")))["success"].Bool
            );
            Assert.False(c.Request("ctsim/hello", Args(("root", "RootB")))["success"].Bool);
        }
    }
}
