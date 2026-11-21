using System;
using System.IO;
using CicodeSim.Host;
using Xunit;

namespace CicodeSim.Tests.Host
{
    /// <summary>ctsim's command line: a misspelt option is refused (exit 2, the token and the
    /// usage on stderr), never read as no filter.</summary>
    [Collection("Console")]
    public sealed class CommandLineTests : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(),
            "ctsim-cli-" + Guid.NewGuid().ToString("N")
        );

        public CommandLineTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, true);
            }
            catch (Exception) { }
        }

        private static (int code, string err) Run(Func<int> f)
        {
            var err = new StringWriter();
            var old = Console.Error;
            Console.SetError(err);
            try
            {
                return (f(), err.ToString());
            }
            finally
            {
                Console.SetError(old);
            }
        }

        [Fact]
        public void ServersRefusesAMisspeltFilter()
        {
            var (code, err) = Run(() =>
                Program.Main(new[] { "servers", "--discovery", _dir, "--projet", "X" })
            );
            Assert.Equal(2, code);
            Assert.Contains("'--projet'", err);
            Assert.Contains("usage: ctsim servers", err);
        }

        [Fact]
        public void ServersRefusesAnOptionWithoutItsValue()
        {
            var (code, err) = Run(() => Program.Main(new[] { "servers", "--discovery", _dir, "--project" }));
            Assert.Equal(2, code);
            Assert.Contains("--project needs a value", err);
        }

        [Fact]
        public void VersionRefusesAMisspeltFlag()
        {
            var (code, err) = Run(() => Program.Version(new[] { "--version", "--jsno" }));
            Assert.Equal(2, code);
            Assert.Contains("'--jsno'", err);
        }
    }
}
