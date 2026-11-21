using System;
using System.IO;
using CicodeSim.Compiled;
using CicodeSim.Compiler.Codegen;
using CicodeSim.Tests.World;
using CicodeSim.Values;
using CicodeSim.Vm;
using Xunit;

namespace CicodeSim.Tests.Compiler
{
    /// <summary>
    /// Whatever stops the compile, the functions it never reached are marked as not compiled.
    /// </summary>
    public class CompileStopTests
    {
        private static string Halt(Program prog, string fn, params Value[] args)
        {
            var vm = new CicodeVm(
                prog,
                CicodeSim.Builtins.Registry.Default(),
                null,
                new ManualClock(tickMs: 1.0),
                null,
                new CodeConfig { MaxSliceOps = 20000 }
            );
            var t = vm.TaskForCall(fn, args);
            for (int n = 0; n < 5 && !t.Done; n++)
                vm.RunSlice(t);
            Assert.Equal(TaskState.Halt, t.State);
            Assert.NotEqual(518, t.HaltCode);
            return t.HaltReason;
        }

        [Fact]
        public void AnIncludeRowThatNamesNoProjectStopsTheCompileAndEveryFunctionSaysSo()
        {
            using var tmp = new TempDir();
            string user = Path.Combine(tmp.Path, "User");
            string dir = Path.Combine(user, "C");
            Directory.CreateDirectory(dir);
            WorldFixture.WriteDbf(
                Path.Combine(user, "MASTER.DBF"),
                WorldFixture.Master,
                new[] { WorldFixture.Row(("NAME", "C"), ("PATH", "C")) }
            );
            WorldFixture.WriteDbf(
                Path.Combine(dir, "include.dbf"),
                WorldFixture.Names,
                new[] { WorldFixture.Row(("NAME", "Nope")) }
            );
            File.WriteAllText(
                Path.Combine(dir, "c.ci"),
                "INT FUNCTION G(INT x, INT y)\r\n\tRETURN x + y;\r\nEND\r\n"
            );
            // no _LIBRARY.RDB: every function from source
            foreach (string mode in new[] { LiveProgram.Changed, LiveProgram.All })
            {
                var lp = LiveProgram.Open(dir, mode, userDir: user);
                Assert.Contains(lp.Errors, d => d.Code == 4016);
                var why = lp.Failed.Find(f => f.name == "G").why;
                Assert.True(
                    why != null && why.StartsWith("the compile stops at F4016", StringComparison.Ordinal),
                    mode + ": " + why
                );
                var g = lp.Program.Function("G");
                Assert.NotNull(g);
                Assert.StartsWith("the compile stops at F4016", g!.NotCompiled);
                Assert.StartsWith(
                    "G did not compile: the compile stops at F4016",
                    Halt(lp.Program, "G", Value.Long(1), Value.Long(2))
                );
            }
        }

        /// <summary>A reload after a stopped compile (a body-only edit of another file) keeps
        /// every unreached function as not compiled with its reason: the placeholder is not
        /// cached as code.</summary>
        [Fact]
        public void AReloadAfterAStoppedCompileKeepsTheReasons()
        {
            string dir = CodegenReloadTests.NewProject(
                ("a.ci", CodegenReloadTests.A),
                ("b.ci", CodegenReloadTests.B)
            );
            try
            {
                File.WriteAllText(Path.Combine(dir, "c.ci"), "INT FUNCTION Fa()\r\n\tRETURN 7;\r\nEND\r\n");
                File.WriteAllText(
                    Path.Combine(dir, "d.ci"),
                    "INT FUNCTION Fd(INT x, INT y)\r\n\tRETURN x + y;\r\nEND\r\n"
                );
                var lp = LiveProgram.Open(dir, LiveProgram.All);
                Assert.NotNull(lp.Program.Function("Fd")!.NotCompiled);
                var r = lp.Reload(
                    Path.Combine(dir, "a.ci"),
                    CodegenReloadTests.A.Replace("RETURN gCount", "RETURN gCount + 1")
                );
                Assert.True(r.Compiled, r.Reason);
                var fd = lp.Program.Function("Fd");
                Assert.NotNull(fd);
                Assert.StartsWith("the compile stops at E2021", fd!.NotCompiled);
                Assert.Contains(lp.Failed, f => f.name == "Fd");
                Assert.StartsWith(
                    "Fd did not compile: the compile stops at E2021",
                    Halt(lp.Program, "Fd", Value.Long(1), Value.Long(2))
                );
            }
            finally
            {
                CodegenReloadTests.Drop(dir);
            }
        }

        /// <summary>A function the compile did reach (one of the stopping file) calls one it did
        /// not: the call halts with that function's reason too, from compiled code (the
        /// interpreter's own call path), not with 'too many arguments'.</summary>
        [Fact]
        public void CompiledCodeCallingAnUncompiledFunctionSaysWhy()
        {
            string dir = CodegenReloadTests.NewProject(
                ("a.ci", CodegenReloadTests.A),
                ("b.ci", CodegenReloadTests.B)
            );
            try
            {
                // c.ci defines Fa again (E2021: the compile stops at c.ci; its own bodies are compiled) and
                // calls Fd of d.ci, which the compile never reaches
                File.WriteAllText(
                    Path.Combine(dir, "c.ci"),
                    "INT FUNCTION Fa()\r\n\tRETURN 7;\r\nEND\r\nINT FUNCTION Fc()\r\n\tRETURN Fd(1, 2);\r\nEND\r\n"
                );
                File.WriteAllText(
                    Path.Combine(dir, "d.ci"),
                    "INT FUNCTION Fd(INT x, INT y)\r\n\tRETURN x + y;\r\nEND\r\n"
                );
                var lp = LiveProgram.Open(dir, LiveProgram.All);
                Assert.Null(lp.Program.Function("Fc")!.NotCompiled);
                Assert.NotNull(lp.Program.Function("Fd")!.NotCompiled);
                string why = Halt(lp.Program, "Fc");
                Assert.StartsWith("Fd did not compile: the compile stops at E2021", why);
            }
            finally
            {
                CodegenReloadTests.Drop(dir);
            }
        }

        /// <summary>The simulation's log event of the halt (ctsim/log) says it halts
        /// and why, in fields a viewer reads: halts = true, haltReason = the function's reason.</summary>
        [Fact]
        public void TheLogEventOfTheHaltCarriesTheReasonAsFields()
        {
            string dir = CodegenReloadTests.NewProject(
                ("a.ci", CodegenReloadTests.A),
                ("b.ci", CodegenReloadTests.B)
            );
            try
            {
                File.WriteAllText(
                    Path.Combine(dir, "c.ci"),
                    "INT FUNCTION Fa()\r\n\tRETURN 7;\r\nEND\r\nINT FUNCTION Fc()\r\n\tRETURN Fd(1, 2);\r\nEND\r\n"
                );
                File.WriteAllText(
                    Path.Combine(dir, "d.ci"),
                    "INT FUNCTION Fd(INT x, INT y)\r\n\tRETURN x + y;\r\nEND\r\n"
                );
                using var sim = new CicodeSim.Sim.Simulation(
                    new CicodeSim.Sim.SimulationOptions
                    {
                        ProjectDir = dir,
                        Compile = CicodeSim.Sim.CodeSource.All,
                        ClockMode = "paused",
                        Start = "2026-10-01T08:00:00Z",
                        StartAlarms = false,
                        ScanMs = 50,
                        TimeZone = TimeZoneInfo.Utc,
                    }
                );
                var logs = new System.Collections.Generic.List<CicodeSim.Sim.LogEntry>();
                var jsons = new System.Collections.Generic.List<CicodeSim.Values.JsonValue>();
                // (the fields as the server sends them: at delivery, not read later)
                sim.Log += e =>
                {
                    lock (logs)
                    {
                        logs.Add(e);
                        jsons.Add(e.ToJson());
                    }
                };
                sim.Start();
                sim.Invoke(() => sim.StartCall("Fc()", null));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                CicodeSim.Sim.LogEntry? halt = null;
                while (halt == null && sw.ElapsedMilliseconds < 15000)
                {
                    lock (logs)
                        halt = logs.Find(e => e.Kind == "hardware" && e.Halts);
                    System.Threading.Thread.Sleep(10);
                }
                Assert.NotNull(halt);
                Assert.Equal(292, halt!.Code);
                Assert.StartsWith("Fd did not compile: the compile stops at E2021", halt.HaltReason);
                Assert.EndsWith(": the task halts (" + halt.HaltReason + ")", halt.Text);
                CicodeSim.Values.JsonValue j;
                lock (logs)
                    j = jsons[logs.IndexOf(halt)];
                Assert.True(j["halts"].Bool);
                Assert.Equal(halt.HaltReason, j["haltReason"].Str);
            }
            finally
            {
                CodegenReloadTests.Drop(dir);
            }
        }

        [Fact]
        public void WithACompiledLibraryTheUnreachedFunctionsKeepItsCode()
        {
            string dir = CodegenReloadTests.NewProject(
                ("a.ci", CodegenReloadTests.A),
                ("b.ci", CodegenReloadTests.B)
            );
            try
            {
                CodegenReloadTests.WriteRdb(dir);
                // then an include row that names no project, and a new function
                WorldFixture.WriteDbf(
                    Path.Combine(dir, "include.dbf"),
                    WorldFixture.Names,
                    new[] { WorldFixture.Row(("NAME", "Nope")) }
                );
                File.WriteAllText(
                    Path.Combine(dir, "b.ci"),
                    CodegenReloadTests.B + "INT FUNCTION Fn2()\r\n\tRETURN 2;\r\nEND\r\n"
                );
                var lp = LiveProgram.Open(dir, LiveProgram.Changed);
                Assert.Contains(lp.Errors, d => d.Code == 4016);
                Assert.Equal("11", CodegenReloadTests.Run(lp.Program, "Fb")); // the RDB's code runs on
                Assert.StartsWith("Fn2 did not compile: the compile stops at F4016", Halt(lp.Program, "Fn2"));
            }
            finally
            {
                CodegenReloadTests.Drop(dir);
            }
        }
    }
}
