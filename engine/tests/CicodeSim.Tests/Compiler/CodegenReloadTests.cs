using System;
using System.IO;
using CicodeSim.Compiled;
using CicodeSim.Compiler.Codegen;
using CicodeSim.Tests.Golden;
using CicodeSim.Values;
using CicodeSim.Vm;
using Xunit;

namespace CicodeSim.Tests.Compiler
{
    /// <summary>LiveProgram's reload rules.</summary>
    public class CodegenReloadTests
    {
        internal const string A =
            "GLOBAL INT gCount = 1;\r\nINT FUNCTION Fa()\r\n\tRETURN gCount;\r\nEND\r\n";

        /// <summary>A syntax error whose recovered parse (RETURN 500 + gCount) would give another value.</summary>
        internal const string ABroken =
            "GLOBAL INT gCount = 1;\r\nINT FUNCTION Fa()\r\n\tRETURN 500 + gCount +;\r\nEND\r\n";
        internal const string B = "INT FUNCTION Fb()\r\n\tRETURN Fa() + 10;\r\nEND\r\n";
        internal const string B2 = "INT FUNCTION Fb()\r\n\tRETURN Fa() + 20;\r\nEND\r\n";

        /// <summary>A project folder (a temporary parent of its own) with the files.</summary>
        internal static string NewProject(params (string name, string text)[] files)
        {
            string dir = Path.Combine(
                Path.GetTempPath(),
                "ctsim-test-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                "RlProj"
            );
            Directory.CreateDirectory(dir);
            foreach (var (n, t) in files)
                File.WriteAllText(Path.Combine(dir, n), t);
            return dir;
        }

        internal static void Drop(string dir)
        {
            try
            {
                Directory.Delete(Path.GetDirectoryName(dir)!, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>The folder's _LIBRARY.RDB compiled from its files, the .ci files older than it.</summary>
        internal static void WriteRdb(string dir)
        {
            var lib = SourceLibrary.Open(dir);
            Assert.Empty(lib.Errors());
            File.WriteAllBytes(
                Path.Combine(dir, "_LIBRARY.RDB"),
                RdbWriter.Write(lib.Image(), RdbWriter.Stamp.Now())
            );
            var old = DateTime.UtcNow.AddHours(-1);
            foreach (string f in Directory.GetFiles(dir, "*.ci"))
                File.SetLastWriteTimeUtc(f, old);
            File.SetLastWriteTimeUtc(Path.Combine(dir, "_LIBRARY.RDB"), old.AddMinutes(1));
        }

        internal static string Run(Program prog, string fn)
        {
            var vm = new CicodeVm(
                prog,
                CicodeSim.Builtins.Registry.Default(),
                null,
                new ManualClock(tickMs: 1.0),
                null,
                new CodeConfig { MaxSliceOps = 20000 }
            );
            var t = vm.TaskForCall(fn, Array.Empty<Value>());
            for (int n = 0; n < 20 && !t.Done; n++)
                vm.RunSlice(t);
            return t.Result != null
                ? (
                    t.Result.Value.S
                    ?? t.Result.Value.I.ToString(System.Globalization.CultureInfo.InvariantCulture)
                )
                : "(none " + t.HaltReason + ")";
        }

        [GoldenFact]
        [Trait("Golden", "light")]
        public void AllModeNeverLinksAFileThatDoesNotCompile()
        {
            string dir = NewProject(("a.ci", A), ("b.ci", B));
            try
            {
                string a = Path.Combine(dir, "a.ci"),
                    b = Path.Combine(dir, "b.ci");
                var lp = LiveProgram.Open(dir, LiveProgram.All);
                Assert.Equal("11", Run(lp.Program, "Fb"));
                var r1 = lp.Reload(a, ABroken);
                Assert.False(r1.Compiled);
                Assert.NotEmpty(r1.Errors);
                Assert.Equal("11", Run(lp.Program, "Fb"));
                // b compiles, but a (from source) does not: nothing changes, the reason names a.ci
                var r2 = lp.Reload(b, B2);
                Assert.False(r2.Compiled);
                Assert.Empty(r2.Errors);
                Assert.Contains("a.ci", r2.Reason);
                Assert.Equal("11", Run(lp.Program, "Fb"));
                // a's fix brings b's change in too
                Assert.True(lp.Reload(a, A).Compiled);
                Assert.Equal("21", Run(lp.Program, "Fb"));
                // a later file's own errors count when an earlier file has errors too (the unit's
                // error list stops at the first file with one): refused, its errors returned
                Assert.False(lp.Reload(a, ABroken).Compiled);
                var r3 = lp.Reload(
                    b,
                    "INT FUNCTION Fb()\r\n\tRETURN Fa() + ;\r\nEND\r\nINT FUNCTION Fc()\r\n\tRETURN 3;\r\nEND\r\n"
                );
                Assert.False(r3.Compiled);
                Assert.NotEmpty(r3.Errors);
                Assert.Equal("21", Run(lp.Program, "Fb"));
            }
            finally
            {
                Drop(dir);
            }
        }

        /// <summary>A folder without _LIBRARY.RDB is a project whose code compiles from source (an
        /// empty one: System and Include alone); a folder that does not exist is none (exec, the
        /// page commands and a simulation refuse it instead of running System and Include).</summary>
        [GoldenFact]
        [Trait("Golden", "light")]
        public void AFolderThatDoesNotExistIsNoProject()
        {
            string dir = NewProject();
            try
            {
                var lp = LiveProgram.Open(dir, LiveProgram.Changed);
                Assert.Contains(lp.Notes, n => n.StartsWith("no _LIBRARY.RDB", StringComparison.Ordinal));
                string missing = Path.Combine(dir, "NoSuchFolder");
                foreach (string m in new[] { LiveProgram.All, LiveProgram.Changed, LiveProgram.None })
                    Assert.Throws<DirectoryNotFoundException>(() => LiveProgram.Open(missing, m));
                Assert.Throws<DirectoryNotFoundException>(() =>
                    LiveProgram.Open("NoSuchProject_ctsim", LiveProgram.All)
                );
                Assert.Throws<DirectoryNotFoundException>(() =>
                    new CicodeSim.Sim.Simulation(new CicodeSim.Sim.SimulationOptions { ProjectDir = missing })
                );
            }
            finally
            {
                Drop(dir);
            }
        }

        /// <summary>A function defined twice stops the compile and says so.</summary>
        [GoldenFact]
        [Trait("Golden", "light")]
        public void AFunctionDefinedTwiceStopsTheCompileAndSaysSo()
        {
            string dir = NewProject(("a.ci", A), ("b.ci", B));
            try
            {
                WriteRdb(dir);
                // a new file defines Fa again; another new file has a function the RDB does not
                File.WriteAllText(Path.Combine(dir, "c.ci"), "INT FUNCTION Fa()\r\n\tRETURN 7;\r\nEND\r\n");
                File.WriteAllText(
                    Path.Combine(dir, "d.ci"),
                    "INT FUNCTION Fd(INT x, INT y)\r\n\tRETURN x + y + StrLength(\"ab\");\r\nEND\r\n"
                );
                foreach (string mode in new[] { LiveProgram.Changed, LiveProgram.All })
                {
                    var lp = LiveProgram.Open(dir, mode);
                    Assert.Contains(lp.Errors, d => d.Code == 2021);
                    var why = lp.Failed.Find(f => f.name == "Fd").why;
                    Assert.True(
                        why != null && why.StartsWith("the compile stops at E2021", StringComparison.Ordinal),
                        mode + ": " + why
                    );
                    // (not the 'unknown function StrLength' of a body the compiler never resolved)
                    Assert.DoesNotContain("unknown function", why);
                    var fd = lp.Program.Function("Fd");
                    Assert.NotNull(fd);
                    Assert.NotNull(fd!.NotCompiled);
                    var vm = new CicodeVm(
                        lp.Program,
                        CicodeSim.Builtins.Registry.Default(),
                        null,
                        new ManualClock(tickMs: 1.0),
                        null,
                        new CodeConfig { MaxSliceOps = 20000 }
                    );
                    var t = vm.TaskForCall("Fd", new[] { Value.Long(1), Value.Long(2) });
                    for (int n = 0; n < 5 && !t.Done; n++)
                        vm.RunSlice(t);
                    Assert.Equal(TaskState.Halt, t.State);
                    Assert.NotEqual(518, t.HaltCode);
                    Assert.Contains("Fd did not compile: the compile stops at E2021", t.HaltReason);
                    if (mode == LiveProgram.Changed)
                        Assert.Equal("11", Run(lp.Program, "Fb")); // the RDB's code runs on
                }
            }
            finally
            {
                Drop(dir);
            }
        }

        [GoldenFact]
        [Trait("Golden", "light")]
        public void ChangedModeKeepsTheCompiledCodeOfAFileThatDoesNotCompile()
        {
            string dir = NewProject(("a.ci", A), ("b.ci", B));
            try
            {
                string a = Path.Combine(dir, "a.ci"),
                    b = Path.Combine(dir, "b.ci");
                WriteRdb(dir);
                // a newer a.ci that does not compile at the start: its RDB code runs, a note says so
                File.WriteAllText(a, ABroken);
                var start = LiveProgram.Open(dir, LiveProgram.Changed);
                Assert.Empty(start.SourceFiles);
                Assert.Contains(start.Notes, n =>
                    n.StartsWith("a.ci does not compile", StringComparison.Ordinal)
                );
                Assert.Equal("11", Run(start.Program, "Fb"));
                File.WriteAllText(a, A);
                File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddHours(-2));
                // a refused edit of a file whose code comes from the RDB does not hold back another
                var lp = LiveProgram.Open(dir, LiveProgram.Changed);
                Assert.Empty(lp.SourceFiles);
                Assert.False(lp.Reload(a, ABroken).Compiled);
                var r = lp.Reload(b, B2);
                Assert.True(r.Compiled, r.Reason);
                Assert.Equal("21", Run(lp.Program, "Fb"));
                // once a comes from source, its errors hold the others back
                Assert.True(lp.Reload(a, A.Replace("RETURN gCount", "RETURN gCount + 1")).Compiled);
                Assert.Equal("22", Run(lp.Program, "Fb"));
                Assert.False(lp.Reload(a, ABroken).Compiled);
                var r2 = lp.Reload(b, B);
                Assert.False(r2.Compiled);
                Assert.Contains("a.ci", r2.Reason);
                Assert.Equal("22", Run(lp.Program, "Fb"));
            }
            finally
            {
                Drop(dir);
            }
        }
    }
}
