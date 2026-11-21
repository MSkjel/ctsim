using System;
using System.IO;
using CicodeSim.Sim;
using CicodeSim.Values;
using Xunit;
using static CicodeSim.Tests.Host.ProtocolClient;

namespace CicodeSim.Tests.Host
{
    /// <summary>A stopped compile through the DAP session.</summary>
    [Collection("ctsim server")]
    public sealed class DapCompileStopTests : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(),
            "ctsim-stop-" + Guid.NewGuid().ToString("N")
        );

        public DapCompileStopTests()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, "a.ci"), "INT FUNCTION Fa()\r\n\tRETURN 1;\r\nEND\r\n");
            File.WriteAllText(
                Path.Combine(_dir, "c.ci"),
                "INT FUNCTION Fa()\r\n\tRETURN 7;\r\nEND\r\nINT FUNCTION Fc()\r\n\tRETURN Fd(1, 2);\r\nEND\r\n"
            );
            File.WriteAllText(
                Path.Combine(_dir, "d.ci"),
                "INT FUNCTION Fd(INT x, INT y)\r\n\tRETURN x + y;\r\nEND\r\n"
            );
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, true);
            }
            catch (Exception) { }
        }

        [Fact]
        public void RefusalsAndHaltsSayWhatDidNotCompile()
        {
            using (var rig = new DapRig(CodeSource.All, _dir))
            {
                var c = rig.Client;
                rig.Initialize();
                var fd = c.Request("launch", Args(("function", "Fd"), ("args", ArgsOf(1, 2))));
                Assert.False(fd["success"].Bool);
                Assert.StartsWith("Fd did not compile: the compile stops at E2021", fd["message"].Str);
                Assert.Contains("c.ci does not compile (1 error):", fd["message"].Str);
                var nope = c.Request("launch", Args(("function", "NopeXyz")));
                Assert.False(nope["success"].Bool);
                Assert.Contains(" compiles the .ci files of ", nope["message"].Str);
                Assert.Contains("not compiled from source: ", nope["message"].Str);

                // compiled code calling the function the compile never reached: the halt says why
                c.Ok("setExceptionBreakpoints", Args(("filters", JsonValue.NewArray())));
                c.Ok("launch", Args(("function", "Fc")));
                c.Ok("configurationDone");
                var err = c.WaitEvent("output", o =>
                    o["category"].Str == "stderr"
                    && o["output"].Str.StartsWith("Cicode hardware error 292", StringComparison.Ordinal)
                );
                Assert.Contains(
                    "[the task halts]\n  Fd did not compile: the compile stops at E2021",
                    err["output"].Str
                );
                var halted = c.WaitEvent("output", o =>
                    o["output"].Str.StartsWith("Fc halted", StringComparison.Ordinal)
                );
                Assert.Contains("(Fd did not compile: the compile stops at E2021", halted["output"].Str);
            }
        }

        private static JsonValue ArgsOf(params int[] values)
        {
            var a = JsonValue.NewArray();
            foreach (int v in values)
                a.Add(JsonValue.Of(v));
            return a;
        }
    }
}
