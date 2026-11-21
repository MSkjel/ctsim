using System;
using System.IO;
using System.Linq;
using CicodeSim.Builtins;
using Xunit;

namespace CicodeSim.Tests
{
    /// <summary>Structure rules: one registration per built-in, known
    /// statuses only, failures passed over are noted, ctsim.exe ships only the host commands.</summary>
    [Collection("Console")] // (it captures Console.Error, as CommandLineTests: never at once)
    public class CodeStructureTests
    {
        [Fact]
        public void NoBuiltinIsRegisteredTwice()
        {
            Assert.Empty(Registry.Duplicates());
        }

        [Fact]
        public void EveryWorldBuiltinHasAKnownStatus()
        {
            var bad = WorldBuiltins
                .Declared()
                .Where(d => !BuiltinStatus.Known(d.Info.Status))
                .Select(d => d.Info.Name + ": " + d.Info.Status)
                .ToList();
            Assert.Empty(bad);
        }

        [Fact]
        public void APassedOverFailureIsCountedAndKept()
        {
            string site = "CodeStructureTests." + Guid.NewGuid().ToString("N");
            for (int i = 0; i < 25; i++)
                EngineNotes.Swallowed(site, new IOException("disk gone"));
            Assert.Equal(25, EngineNotes.CountsBySite()[site]);
            Assert.Equal(20, EngineNotes.Last().Count(l => l.StartsWith(site + ": IOException: disk gone")));
        }

        [Fact]
        public void TheShippedHostAnswersOnlyTheHostCommands()
        {
            var err = new StringWriter();
            var old = Console.Error;
            Console.SetError(err);
            int rc;
            try
            {
                rc = CicodeSim.Host.Program.Main(new[] { "pcode", "x.rdb" });
            }
            finally
            {
                Console.SetError(old);
            }
            Assert.Equal(2, rc);
            Assert.Contains("ctsim-dev", err.ToString());
            Assert.DoesNotContain("world-sweep", err.ToString());
        }
    }
}
