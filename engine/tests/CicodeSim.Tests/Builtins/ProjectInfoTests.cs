using System.IO;
using CicodeSim.Builtins;
using CicodeSim.Compiled;
using CicodeSim.Vm;
using CicodeSim.World;
using Xunit;

namespace CicodeSim.Tests.Builtins
{
    /// <summary>ProjectInfo: the records of the running project's
    /// _PROJECT.RDB 'project' table at the offsets the runtime reads (R2, read only), '' for an
    /// unknown project or type and without the compiled table.</summary>
    [Collection("World")]
    public sealed partial class ProjectInfoTests : DataTestBase
    {
        private Sim Corpus(string project)
        {
            string dir = Path.Combine(R2, project);
            var w = new CicodeSim.World.World(dir, root: project);
            var run = new WorldRun(w, new ProgramBuilder().Build());
            var h = WorldBuiltins.HostOf(run.Vm.Builtins)!;
            return new Sim
            {
                W = w,
                Run = run,
                Host = h,
            };
        }

        [Fact]
        public void ExampleSaRootRecord()
        {
            if (!HaveR2)
                return;
            var s = Corpus("ExampleSA");
            Assert.Equal("ExampleSA", Call(s, "ProjectInfo", 0, 0).S);
            Assert.Equal(
                "Workspace and Situational Awareness Example Project",
                Call(s, "ProjectInfo", 0, 1).S
            );
            Assert.Equal("8.50", Call(s, "ProjectInfo", 0, 2).S);
            Assert.Equal("0000", Call(s, "ProjectInfo", 0, 3).S);
            Assert.NotEqual("", Call(s, "ProjectInfo", 0, 4).S); // the compile's date and time
            Assert.NotEqual("", Call(s, "ProjectInfo", 0, 5).S);
            // the included projects follow in the reverse of the compile order
            Assert.Equal("IndustrialGraphics_Include", Call(s, "ProjectInfo", 1, 0).S);
            Assert.Equal("Include", Call(s, "ProjectInfo", 5, 0).S);
            Assert.Equal("System", Call(s, "ProjectInfo", 6, 0).S);
            // LOCATION (0x341) is not a type; 6 and negative types are ''
            Assert.Equal("", Call(s, "ProjectInfo", 0, 6).S);
            Assert.Equal("", Call(s, "ProjectInfo", 0, -1).S);
            // past the records, a negative project (0xFFFF as an unsigned short) ''
            Assert.Equal("", Call(s, "ProjectInfo", 1000, 0).S);
            Assert.Equal("", Call(s, "ProjectInfo", -1, 0).S);
        }

        [Theory]
        [InlineData("ExampleSA")]
        public void EveryRecordAsTheTableHoldsIt(string project)
        {
            if (!HaveR2 || !File.Exists(Path.Combine(R2, project, "_PROJECT.RDB")))
                return;
            var f = RdbFile.Open(Path.Combine(R2, project, "_PROJECT.RDB"));
            var recs = f.Records("project");
            Assert.NotEmpty(recs);
            Assert.Equal(project, (string?)recs[0]["NAME"]);
            var s = Corpus(project);
            string[] fields = { "NAME", "DESC", "MAJOR", "MINOR", "DATE", "TIME" };
            for (int i = 0; i < recs.Count; i++)
            for (int t = 0; t < fields.Length; t++)
                Assert.Equal((string?)recs[i][fields[t]] ?? "", Call(s, "ProjectInfo", i, t).S);
            Assert.Equal("", Call(s, "ProjectInfo", recs.Count, 0).S);
        }

        [Fact]
        public void NoCompiledTableGivesEmpty()
        {
            // the synthetic install is not compiled (no _PROJECT.RDB): '' for everything
            var s = MakeData();
            Assert.Equal("", Call(s, "ProjectInfo", 0, 0).S);
            Assert.Equal("", Call(s, "ProjectInfo", 0, 1).S);
        }
    }
}
