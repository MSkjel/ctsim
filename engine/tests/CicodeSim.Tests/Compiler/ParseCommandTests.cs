using System.Collections.Generic;
using System.IO;
using CicodeSim.Host;
using CicodeSim.Tests.World;
using Xunit;

namespace CicodeSim.Tests.Compiler
{
    /// <summary>ctsim-dev parse on a synthetic User folder (temporary): the unit it names is the unit
    /// the file was compiled in.</summary>
    public class ParseCommandTests
    {
        [Fact]
        public void ParseNamesTheUnitOnlyWhenTheFileIsInIt()
        {
            using var tmp = new TempDir();
            string user = Path.Combine(tmp.Path, "User");
            foreach (string p in new[] { "A", "B", "C" })
                Directory.CreateDirectory(Path.Combine(user, p));
            WorldFixture.WriteDbf(
                Path.Combine(user, "MASTER.DBF"),
                WorldFixture.Master,
                new[]
                {
                    WorldFixture.Row(("NAME", "A"), ("PATH", "A")),
                    WorldFixture.Row(("NAME", "B"), ("PATH", "B")),
                    WorldFixture.Row(("NAME", "C"), ("PATH", "C")),
                }
            );
            WorldFixture.WriteDbf(
                Path.Combine(user, "C", "include.dbf"),
                WorldFixture.Names,
                new[] { WorldFixture.Row(("NAME", "Nope")) }
            );
            string a = Path.Combine(user, "A", "a.ci"),
                c = Path.Combine(user, "C", "c.ci");
            File.WriteAllText(a, "FUNCTION F()\nEND\n");
            File.WriteAllText(c, "FUNCTION G()\nEND\n");

            string Run(params string[] args)
            {
                var sw = new StringWriter();
                Assert.Equal(0, FrontendCommands.Parse(new List<string>(args), sw));
                return sw.ToString();
            }

            Assert.Contains("\"unit\":\"A\",\"diagnostics\":[]", Run(a, "--user", user));
            // B does not include A: an error, not A's file parsed alone under B's name
            Assert.Equal(
                1,
                FrontendCommands.Parse(
                    new List<string> { a, "--user", user, "--root", "B" },
                    new StringWriter()
                )
            );
            // a root that is not registered: F4016, as the compiler reports it
            Assert.Contains(
                "\"unit\":\"Nope\",\"diagnostics\":[{\"code\":4016",
                Run(a, "--user", user, "--root", "Nope")
            );
            // C's include project is not registered: the unit's F4016 is what the compiler reports
            string outC = Run(c, "--user", user);
            Assert.Contains("\"unit\":\"C\"", outC);
            Assert.Contains("\"code\":4016", outC);
            Assert.Contains("\"unit\":null", Run(a, "--alone"));
        }
    }
}
