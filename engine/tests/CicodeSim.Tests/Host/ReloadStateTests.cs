using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using CicodeSim.Sim;
using CicodeSim.Tests.Compiler;
using CicodeSim.Vm;
using Xunit;

namespace CicodeSim.Tests.Host
{
    /// <summary>ctsim/reload swaps the program but not the state: a module / global variable keeps
    /// its value (one storage for the code still running and the new code), also over a whole-unit
    /// compile; a new variable starts from its initialiser.</summary>
    [Collection("ctsim server")]
    public sealed class ReloadStateTests
    {
        [ProjectFact]
        public void VariablesKeepTheirValuesOverAReload()
        {
            const string a =
                "GLOBAL INT gCount = 1;\r\nMODULE STRING mName = \"start\";\r\nINT FUNCTION Fa()\r\n\tRETURN gCount;\r\nEND\r\n"
                + "FUNCTION SetA(INT v)\r\n\tgCount = v;\r\n\tmName = \"set\";\r\nEND\r\nSTRING FUNCTION NameA()\r\n\tRETURN mName;\r\nEND\r\n";
            string dir = CodegenReloadTests.NewProject(("a.ci", a), ("b.ci", CodegenReloadTests.B));
            try
            {
                using (var sim = SimTestUtil.Start(dir))
                {
                    string Call(string text)
                    {
                        CallOutcome? o = null;
                        var done = new ManualResetEventSlim(false);
                        sim.Invoke(() => sim.StartCall(text, r =>
                            {
                                o = r;
                                done.Set();
                            }));
                        Assert.True(done.Wait(10000), text);
                        return o!.Result;
                    }
                    Call("SetA(42)");
                    Assert.Equal("42", Call("Fa()"));
                    var r = sim.Invoke(() => sim.Reload(Path.Combine(dir, "b.ci"), CodegenReloadTests.B2));
                    Assert.True(r["compiled"].Bool, r.Dump());
                    Assert.Equal("62", Call("Fb()"));
                    Assert.Equal("set", Call("NameA()"));
                    // a declaration change (a new variable and function): the whole unit again, the
                    // values stay, the new variable has its initialiser
                    string a2 =
                        a
                        + "GLOBAL INT gNew = 7;\r\nINT FUNCTION Fz()\r\n\tRETURN gCount * 2 + gNew;\r\nEND\r\n";
                    var r2 = sim.Invoke(() => sim.Reload(Path.Combine(dir, "a.ci"), a2));
                    Assert.True(r2["compiled"].Bool && !r2["incremental"].Bool, r2.Dump());
                    Assert.Equal("91", Call("Fz()"));
                    Assert.Equal("set", Call("NameA()"));
                }
            }
            finally
            {
                CodegenReloadTests.Drop(dir);
            }
        }

        /// <summary>The library a reload replaced is freed once nothing runs it: the VM's tables
        /// (decoded code, variable storage) do not keep it.</summary>
        [ProjectFact]
        public void AReplacedLibraryIsFreed()
        {
            string file = Path.Combine(SimCorpus.ExampleSA, "Mixing.ci");
            string text = CicodeSim.Compiler.Syntax.Lexer.SourceText(File.ReadAllBytes(file));
            using (var sim = SimTestUtil.Start(SimCorpus.ExampleSA))
            {
                // the first reload links Mixing.ci into the RDB's program (which stays: the base)
                Assert.True(sim.Invoke(() => sim.Reload(file, text))["compiled"].Bool);
                Touch(sim);
                var first = Weak(sim);
                for (int i = 0; i < 2; i++)
                {
                    var r = sim.Invoke(() => sim.Reload(file, text + (i == 0 ? "\r\n" : "")));
                    Assert.True(r["compiled"].Bool, r.Dump());
                    Touch(sim);
                }
                for (int k = 0; k < 3 && first.IsAlive; k++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
                Assert.False(first.IsAlive, "the replaced library is still referenced");
                Assert.NotNull(sim.Invoke(() => sim.Vm.Variable("l_nControlMode_Manual")));
            }
        }

        /// <summary>A function the code generator cannot compile (here: too long for its length
        /// word) runs as an empty body; the reload says so in a warning.</summary>
        [ProjectFact]
        public void AReloadWarnsOfAFunctionNotCompiled()
        {
            var sb = new System.Text.StringBuilder("INT FUNCTION Long()\r\n\tINT i = 0;\r\n");
            for (int k = 0; k < 5000; k++)
                sb.Append("\ti = i + 1;\r\n");
            sb.Append("\tRETURN i;\r\nEND\r\n");
            string dir = CodegenReloadTests.NewProject(
                ("a.ci", CodegenReloadTests.A),
                ("b.ci", CodegenReloadTests.B)
            );
            try
            {
                using (var sim = SimTestUtil.Start(dir))
                {
                    var logs = new System.Collections.Generic.List<string>();
                    sim.Log += e =>
                    {
                        lock (logs)
                            logs.Add(e.Level + " " + e.Text);
                    };
                    var r = sim.Invoke(() =>
                        sim.Reload(Path.Combine(dir, "b.ci"), CodegenReloadTests.B + sb)
                    );
                    Assert.True(r["compiled"].Bool, r.Dump());
                    sim.Events.Flush();
                    lock (logs)
                        Assert.Contains(logs, l =>
                            l.StartsWith("warning ", StringComparison.Ordinal)
                            && l.Contains("not compiled from source: Long")
                        );
                }
            }
            finally
            {
                CodegenReloadTests.Drop(dir);
            }
        }

        /// <summary>A page over the simulation's VM (Animator over a shared VM) calls the
        /// reloaded function: a page code block calls library functions by name, and the VM's cached
        /// blocks resolved that name in the replaced library.</summary>
        [ProjectFact]
        public void PageCodeCallsTheReloadedFunction()
        {
            string dir = CodegenReloadTests.NewProject(
                ("a.ci", CodegenReloadTests.A),
                ("b.ci", CodegenReloadTests.B)
            );
            try
            {
                using (var sim = SimTestUtil.Start(dir))
                {
                    var pools = new Pools();
                    var cb = new CodeBuilder(pools);
                    cb.Call("Fb", 0).End();
                    byte[] code = cb.Code();
                    var page = pools.Fill(new CicodeSim.Compiled.Program());
                    var an = sim.Invoke(() => new CicodeSim.Page.Animator(sim.World, sim.Vm, sim.Scheduler));
                    string Run() =>
                        sim.Invoke(() =>
                        {
                            var t = an.RunForeground(code, page, 0, 1, "page expression");
                            return t.Result != null
                                ? t.Result.Value.I.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                : "(" + t.HaltReason + ")";
                        });
                    Assert.Equal("11", Run());
                    var r = sim.Invoke(() => sim.Reload(Path.Combine(dir, "b.ci"), CodegenReloadTests.B2));
                    Assert.True(r["compiled"].Bool, r.Dump());
                    Assert.Equal("21", Run());
                    Assert.Same(sim.Invoke(() => sim.Library), an.Library);
                    sim.Invoke(() => an.Dispose());
                }
            }
            finally
            {
                CodegenReloadTests.Drop(dir);
            }
        }

        /// <summary>An OnEvent handler is a function number of the library: a reload that numbers
        /// the functions otherwise (a function added before it) keeps the event on the same
        /// function.</summary>
        [ProjectFact]
        public void AnEventHandlerFollowsItsFunctionOverAReload()
        {
            const string ev =
                "INT FUNCTION Handler()\r\n\tRETURN 7;\r\nEND\r\nINT FUNCTION Other()\r\n\tRETURN 99;\r\nEND\r\n"
                + "FUNCTION Reg()\r\n\tOnEvent(100, Handler);\r\nEND\r\nINT FUNCTION Fire()\r\n\tRETURN CallEvent(0, 100);\r\nEND\r\n";
            string dir = CodegenReloadTests.NewProject(("a.ci", CodegenReloadTests.A), ("e.ci", ev));
            try
            {
                using (var sim = SimTestUtil.Start(dir))
                {
                    string Call(string text)
                    {
                        CallOutcome? o = null;
                        var done = new ManualResetEventSlim(false);
                        sim.Invoke(() => sim.StartCall(text, r =>
                            {
                                o = r;
                                done.Set();
                            }));
                        Assert.True(done.Wait(10000), text);
                        return o!.Result;
                    }
                    Call("Reg()");
                    Assert.Equal("7", Call("Fire()"));
                    // a new function before Handler: the unit compiles again, Handler's number moves
                    var r = sim.Invoke(() =>
                        sim.Reload(
                            Path.Combine(dir, "e.ci"),
                            "INT FUNCTION First()\r\n\tRETURN 1;\r\nEND\r\n" + ev
                        )
                    );
                    Assert.True(r["compiled"].Bool && !r["incremental"].Bool, r.Dump());
                    Assert.Equal("7", Call("Fire()"));
                    // a handler whose function is gone: no handler (CallEvent gives 0)
                    var r2 = sim.Invoke(() =>
                        sim.Reload(Path.Combine(dir, "e.ci"), ev.Replace("Handler", "Renamed"))
                    );
                    Assert.True(r2["compiled"].Bool, r2.Dump());
                    Assert.Equal("0", Call("Fire()"));
                }
            }
            finally
            {
                CodegenReloadTests.Drop(dir);
            }
        }

        /// <summary>Handlers follow their functions when the RDB was compiled elsewhere.</summary>
        [ProjectFact]
        public void HandlersFollowTheirFunctionsWhenTheRdbWasCompiledElsewhere()
        {
            const string ev =
                "GLOBAL INT gClosed = 0;\r\n"
                + "INT FUNCTION Handler()\r\n\tRETURN 7;\r\nEND\r\nINT FUNCTION Other()\r\n\tRETURN 99;\r\nEND\r\n"
                + "FUNCTION Reg()\r\n\tOnEvent(100, Handler);\r\nEND\r\nINT FUNCTION Fire()\r\n\tRETURN CallEvent(0, 100);\r\nEND\r\n"
                + "FUNCTION OnMapClose(STRING sMap)\r\n\tgClosed = 1;\r\nEND\r\n"
                + "INT FUNCTION OpenMap()\r\n\tMapOpen(\"ctsimReloadMap\", 0, \"OnMapClose\");\r\n\tRETURN 1;\r\nEND\r\n"
                + "INT FUNCTION CloseMap()\r\n\tMapClose(\"ctsimReloadMap\");\r\n\tRETURN gClosed;\r\nEND\r\n";
            string compiledAt = CodegenReloadTests.NewProject(("a.ci", CodegenReloadTests.A), ("e.ci", ev));
            CodegenReloadTests.WriteRdb(compiledAt);
            // the same project in another folder, its files (and their dates) copied
            string dir = Path.Combine(Path.GetDirectoryName(compiledAt)!, "moved", "RlProj");
            Directory.CreateDirectory(dir);
            foreach (string f in Directory.GetFiles(compiledAt))
            {
                string to = Path.Combine(dir, Path.GetFileName(f));
                File.Copy(f, to);
                File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(f));
            }
            try
            {
                using (var sim = SimTestUtil.Start(dir))
                {
                    string Call(string text)
                    {
                        CallOutcome? o = null;
                        var done = new ManualResetEventSlim(false);
                        sim.Invoke(() => sim.StartCall(text, r =>
                            {
                                o = r;
                                done.Set();
                            }));
                        Assert.True(done.Wait(10000), text);
                        return o!.Result;
                    }
                    Assert.Null(sim.Live.Source); // the RDB alone
                    Call("Reg()");
                    Assert.Equal("1", Call("OpenMap()"));
                    Assert.Equal("7", Call("Fire()"));
                    // e.ci's bodies edited (the shape kept): its functions now come from source,
                    // with this folder's FILENAME
                    var r = sim.Invoke(() =>
                        sim.Reload(
                            Path.Combine(dir, "e.ci"),
                            ev.Replace("RETURN 99", "RETURN 98").Replace("gClosed = 1", "gClosed = 2")
                        )
                    );
                    Assert.True(r["compiled"].Bool, r.Dump());
                    Assert.Equal("98", Call("Other()"));
                    Assert.Equal("7", Call("Fire()"));
                    Assert.Equal("2", Call("CloseMap()"));
                }
            }
            finally
            {
                CodegenReloadTests.Drop(compiledAt);
            }
        }

        /// <summary>A reload resolves the breakpoints again (DebugTarget.LibraryChanged): one on a
        /// line without code binds to the next line with code; once the reloaded file has code on
        /// its line it binds there, and the change is reported (DAP 'breakpoint' event).</summary>
        [ProjectFact]
        public void BreakpointsResolveAgainAfterAReload()
        {
            const string x = "INT FUNCTION Fx()\r\n\tINT i = 1;\r\n\r\n\r\n\tRETURN i;\r\nEND\r\n";
            string dir = CodegenReloadTests.NewProject(("a.ci", CodegenReloadTests.A), ("x.ci", x));
            try
            {
                using (var sim = SimTestUtil.Start(dir))
                {
                    var changed = new System.Collections.Generic.List<BreakpointResult>();
                    sim.Debugger.BreakpointChanged += (s, e) =>
                    {
                        lock (changed)
                            changed.Add(e.Breakpoint);
                    };
                    string file = Path.Combine(dir, "x.ci");
                    var set = sim.Debugger.SetBreakpoints(file, new[] { new BreakpointRequest { Line = 3 } });
                    Assert.True(set[0].Verified);
                    Assert.Equal(5, set[0].Line);
                    var r = sim.Invoke(() =>
                        sim.Reload(file, x.Replace("\r\n\r\n\r\n", "\r\n\ti = i + 1;\r\n\r\n"))
                    );
                    Assert.True(r["compiled"].Bool, r.Dump());
                    SimTestUtil.Until(sim, () =>
                    {
                        lock (changed)
                            return changed.Count > 0;
                    });
                    lock (changed)
                    {
                        Assert.Equal(set[0].Id, changed[0].Id);
                        Assert.True(changed[0].Verified);
                        Assert.Equal(3, changed[0].Line);
                    }
                }
            }
            finally
            {
                CodegenReloadTests.Drop(dir);
            }
        }

        /// <summary>What running code leaves in the VM: decoded code, a variable's storage.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Touch(Simulation sim) =>
            sim.Invoke(() =>
            {
                for (int k = 0; k < 300 && k < sim.Library.Functions.Count; k++)
                    sim.Vm.CodeFor(sim.Library.Functions[k]);
                sim.Vm.Variable("l_nControlMode_Manual");
            });

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference Weak(Simulation sim) => new WeakReference(sim.Invoke(() => sim.Library));
    }
}
