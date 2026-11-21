using System.Collections.Generic;
using System.Linq;
using CicodeSim.Compiled;
using CicodeSim.Values;
using CicodeSim.Vm;
using Xunit;
using static CicodeSim.Tests.Vm.VmTestUtil;

namespace CicodeSim.Tests.Vm
{
    /// <summary>The VM's debugger seam: frames, locals, breakpoint lines and the Stepper on the
    /// line hook (breakpoints, pause, step into / over / out, the runtime's defaults).</summary>
    public class DebugTests
    {
        private const string File = @"C:\proj\a.ci";

        /// <summary>F(): line 10 a = 1; line 11 G(); line 12 RETURN a + 1. G(): lines 20, 21.</summary>
        private static Program Build()
        {
            var pb = new ProgramBuilder();
            var f = pb.Function("F", "", Types.Long, file: File);
            f.Locals.Add((0, "a"));
            f.StackVar(Types.Long);
            f.Line(10).ConstInt(1).StoreStack(0);
            f.Line(11).Call("G", 0).Op("Pop");
            f.Line(12).LoadStack(0).ConstInt(1).Op("Add").RetValue().End();
            var g = pb.Function("G", "", Types.Long, file: File);
            g.Line(20).ConstInt(5).Op("Pop");
            g.Line(21).ConstInt(7).RetValue().End();
            return pb.Build();
        }

        private sealed class Session
        {
            public readonly CicodeVm Vm;
            public readonly Scheduler Sch;
            public readonly Stepper Dbg;
            public readonly List<(string Task, StopReason Reason, int Line, int Depth)> Stops =
                new List<(string, StopReason, int, int)>();

            public Session(Program prog)
            {
                Vm = MakeVm(prog);
                Sch = new Scheduler(Vm);
                Dbg = new Stepper(Vm);
                Dbg.Stopped += (t, r, code, text, bp) => Stops.Add((t.Name, r, t.Line, t.Depth));
            }
        }

        [Fact]
        public void BreakpointLinesResolveToLineMarkers()
        {
            var s = new Session(Build());
            var r = s.Dbg.SetBreakpoints(
                File,
                new[]
                {
                    new BreakpointRequest { Line = 11 },
                    new BreakpointRequest { Line = 13 },
                    new BreakpointRequest { Line = 15 },
                    new BreakpointRequest { Line = 99 },
                }
            );
            Assert.True(r[0].Verified && r[0].Line == 11);
            Assert.True(r[1].Verified && r[1].Line == 20); // the next marker in the file
            Assert.True(r[2].Verified && r[2].Line == 20);
            Assert.False(r[3].Verified);
            Assert.Equal(new[] { 10, 11, 12 }, DebugInspect.LineMarkers(s.Vm.Library.Function("F")!));
        }

        [Fact]
        public void BreakpointStopsWithFramesAndLocals()
        {
            var s = new Session(Build());
            s.Dbg.SetBreakpoints(File, new[] { new BreakpointRequest { Line = 21 } });
            var t = s.Sch.Spawn("F");
            s.Sch.Run();
            Assert.Equal(("F", StopReason.Breakpoint, 21, 2), s.Stops.Single());
            Assert.Equal(TaskState.Wait, t.State);
            var frames = DebugInspect.Frames(t);
            Assert.Equal(new[] { "G", "F" }, frames.Select(f => f.Name).ToArray());
            Assert.Equal(new[] { 21, 11 }, frames.Select(f => f.Line).ToArray());
            var locals = DebugInspect.Locals(t, frames[1]);
            Assert.Equal("a", locals.Single().Name);
            Assert.Equal(1, locals.Single().Value.I);
            // the simulator can change a variable: a = 41, F returns a + 1
            Assert.True(DebugInspect.SetLocal(t, frames[1], "A", Value.Real(41.0)));
            s.Dbg.Continue(t);
            s.Sch.Run();
            Assert.True(t.State == TaskState.Stop && t.Result!.Value.I == 42);
        }

        [Fact]
        public void StepOverIntoAndOut()
        {
            var s = new Session(Build());
            s.Dbg.SetBreakpoints(File, new[] { new BreakpointRequest { Line = 10 } });
            var t = s.Sch.Spawn("F");
            s.Sch.Run();
            Assert.Equal(10, t.Line);
            s.Dbg.Step(t, StepKind.Over);
            s.Sch.Run();
            Assert.Equal(11, t.Line); // the next line of the same frame
            s.Dbg.Step(t, StepKind.Into);
            s.Sch.Run();
            Assert.Equal((20, 2), (t.Line, t.Depth)); // into G
            s.Dbg.Step(t, StepKind.Out);
            s.Sch.Run();
            Assert.Equal((11, 1), (t.Line, t.Depth)); // back in F at the call's line
            s.Dbg.Step(t, StepKind.Over);
            s.Sch.Run();
            Assert.Equal(12, t.Line);
            s.Dbg.Step(t, StepKind.Over); // steps off the outermost function:
            s.Sch.Run(); // parked, with no event
            Assert.True(t.State == TaskState.Wait && t.StopReason == "parked");
            Assert.Equal(5, s.Stops.Count);
            Assert.All(s.Stops.Skip(1), x => Assert.Equal(StopReason.Step, x.Reason));
            s.Dbg.Continue(t);
            s.Sch.Run();
            Assert.True(t.State == TaskState.Stop && t.Result!.Value.I == 2);
        }

        [Fact]
        public void StepOverSkipsTheCalledFunction()
        {
            var s = new Session(Build());
            s.Dbg.SetBreakpoints(File, new[] { new BreakpointRequest { Line = 11 } });
            var t = s.Sch.Spawn("F");
            s.Sch.Run();
            s.Dbg.Step(t, StepKind.Over);
            s.Sch.Run();
            Assert.Equal((12, 1), (t.Line, t.Depth));
        }

        [Fact]
        public void PauseAndStepReleasesTheOtherTasks()
        {
            var s = new Session(Build());
            s.Dbg.SetBreakpoints(File, new[] { new BreakpointRequest { Line = 10 } });
            var a = s.Sch.Spawn("F", name: "a");
            var b = s.Sch.Spawn("F", name: "b");
            s.Sch.Run();
            Assert.True(a.Pause && b.Pause);
            s.Dbg.Step(a, StepKind.Over);
            s.Sch.Run();
            Assert.Equal(11, a.Line);
            Assert.True(b.Done && b.Result!.Value.I == 2); // released by a's step
            s.Dbg.RequestPause(a);
            s.Dbg.Continue(a);
            s.Sch.Run();
            Assert.Equal(StopReason.Pause, s.Stops.Last().Reason);
        }

        [Fact]
        public void ForegroundCodeCannotBreak()
        {
            var prog = Build();
            var s = new Session(prog);
            s.Dbg.SetBreakpoints(File, new[] { new BreakpointRequest { Line = 20 } });
            var pb = new ProgramBuilder();
            var t = s.Vm.TaskForCall("F", new Value[0], foreground: true);
            s.Vm.Run(t);
            Assert.True(t.State == TaskState.Stop && s.Stops.Count == 0);
        }

        [Fact]
        public void ForegroundCanBreakParksTheTask()
        {
            // with the option a breakpoint in foreground code stops it like background code
            var vm = MakeVm(Build());
            var dbg = new Stepper(vm, new DebugOptions { ForegroundCanBreak = true });
            var stops = new List<int>();
            dbg.Stopped += (t, r, code, text, bp) => stops.Add(t.Line);
            dbg.SetBreakpoints(File, new[] { new BreakpointRequest { Line = 20 } });
            var task = vm.TaskForCall("F", new Value[0], foreground: true);
            vm.RunSlice(task);
            Assert.Equal(new[] { 20 }, stops.ToArray());
            Assert.Equal(TaskState.Wait, task.State);
            Assert.Equal(20, task.Line);
            dbg.Continue(task);
            vm.Run(task);
            Assert.True(task.State == TaskState.Stop && task.Result!.Value.I == 2);
            Assert.Single(stops);
        }

        [Fact]
        public void ContinueAllReleasesAForegroundTaskOutsideTheScheduler()
        {
            // a foreground evaluation stopped with ForegroundCanBreak is not one of the
            // Scheduler's tasks
            var vm = MakeVm(Build());
            var dbg = new Stepper(vm, new DebugOptions { ForegroundCanBreak = true });
            dbg.SetBreakpoints(File, new[] { new BreakpointRequest { Line = 20 } });
            var task = vm.TaskForCall("F", new Value[0], foreground: true);
            vm.RunSlice(task);
            Assert.True(task.State == TaskState.Wait && task.Pause);
            dbg.Continue(null);
            Assert.False(task.Pause);
            vm.Run(task);
            Assert.True(task.State == TaskState.Stop && task.Result!.Value.I == 2);
        }

        [Fact]
        public void HaltingErrorIsReportedAndTheTaskEnds()
        {
            // live (conformance/runtime/debugger/hwerror_259_array_overrun_halts.json): a halting
            // error is announced with its line, code and text, then the task halts at once: no
            // stop, no hold on the line
            var pb = new ProgramBuilder();
            var f = pb.Function("E", "", Types.Long, file: File);
            f.Line(5).ConstInt(1).ConstInt(0).Op("Div").RetValue().End();
            var s = new Session(pb.Build());
            var reports = new List<ErrorReportInfo>();
            s.Dbg.ErrorReported += reports.Add;
            var ended = new List<CicodeTask>();
            s.Sch.TaskEnded += ended.Add;
            var t = s.Sch.Spawn("E");
            s.Sch.Run();
            Assert.Empty(s.Stops);
            var r = reports.Single();
            Assert.True(r.Halting && r.Code == 273 && r.Line == 5 && r.ErrDesc == "E" && r.ErrPage == "");
            Assert.Equal(
                "Thread "
                    + t.Handle
                    + " Hardware error (273) '"
                    + Errors.Text(273)
                    + "' ErrPage: '' ErrDesc: 'E'",
                r.Announce
            );
            Assert.True(t.State == TaskState.Halt && t.HaltCode == 273 && t.Line == 5);
            Assert.Equal(new[] { t }, ended.ToArray());
        }

        /// <summary>F(): line 5 StrMid("abc", 10, 2) (257, no halt); line 6 a = 1; line 7 RETURN a.</summary>
        private static Program NonHalting(int errSet = 0)
        {
            var pb = new ProgramBuilder();
            var f = pb.Function("F", "", Types.Long, file: File);
            f.Locals.Add((0, "a"));
            f.StackVar(Types.Long);
            if (errSet != 0)
                f.Line(4).ConstInt(1).Convert(Types.Long).Func("ErrSet", 1);
            f.Line(5)
                .ConstStr("abc")
                .ConstInt(10)
                .Convert(Types.Long)
                .ConstInt(2)
                .Convert(Types.Long)
                .Func("StrMid", 3)
                .Op("Pop");
            f.Line(6).ConstInt(1).StoreStack(0);
            f.Line(7).LoadStack(0).RetValue().End();
            return pb.Build();
        }

        [Fact]
        public void NonHaltingErrorStopsAtTheNextStatement()
        {
            // live (hwerror_424_in_builtin_continues.json): the report, then the task is
            // suspended alive at the next statement; locals readable, continue resumes
            var s = new Session(NonHalting());
            var reports = new List<ErrorReportInfo>();
            s.Dbg.ErrorReported += reports.Add;
            var stops = new List<(StopReason, int, int)>();
            s.Dbg.Stopped += (t, r, code, text, bp) => stops.Add((r, code, t.Line));
            var task = s.Sch.Spawn("F");
            s.Sch.Run();
            var rep = reports.Single();
            Assert.True(
                !rep.Halting
                    && rep.Code == 257
                    && rep.Line == 5
                    && rep.ErrPage == "StrMid"
                    && rep.ErrDesc == "F"
            );
            Assert.Equal((StopReason.Exception, 257, 6), stops.Single());
            Assert.True(task.State == TaskState.Wait && task.Pause);
            s.Dbg.Continue(task);
            s.Sch.Run();
            Assert.True(task.State == TaskState.Stop && task.Result!.Value.I == 1);
            // a breakpoint on the next statement: the stop is the breakpoint's (0x1003)
            var s2 = new Session(NonHalting());
            var stops2 = new List<(StopReason, int, int)>();
            s2.Dbg.Stopped += (t, r, code, text, bp) => stops2.Add((r, code, t.Line));
            s2.Dbg.SetBreakpoints(File, new[] { new BreakpointRequest { Line = 6 } });
            s2.Sch.Spawn("F");
            s2.Sch.Run();
            Assert.Equal((StopReason.Breakpoint, 257, 6), stops2.Single());
        }

        [Fact]
        public void ErrSetSuppressesTheReport()
        {
            var s = new Session(NonHalting(errSet: 1));
            var reports = new List<ErrorReportInfo>();
            s.Dbg.ErrorReported += reports.Add;
            var task = s.Sch.Spawn("F");
            s.Sch.Run();
            Assert.Empty(reports);
            Assert.Empty(s.Stops);
            Assert.True(task.State == TaskState.Stop);
            // BreakOnError off: no report either
            var s2 = new Session(NonHalting());
            s2.Dbg.Options.BreakOnError = false;
            var rep2 = new List<ErrorReportInfo>();
            s2.Dbg.ErrorReported += rep2.Add;
            s2.Sch.Spawn("F");
            s2.Sch.Run();
            Assert.Empty(rep2);
        }

        [Fact]
        public void ForegroundErrorIsReportedOnceAndABreakpointGivesNotice343()
        {
            var s = new Session(NonHalting());
            var reports = new List<ErrorReportInfo>();
            s.Dbg.ErrorReported += reports.Add;
            for (int i = 0; i < 2; i++)
            {
                var t = s.Vm.TaskForCall("F", new Value[0], foreground: true);
                s.Vm.Run(t);
                Assert.True(t.State == TaskState.Stop);
            }
            Assert.True(reports.Single().Foreground && reports.Single().Code == 257);
            s.Dbg.Continue(null); // re-arms the report
            s.Vm.Run(s.Vm.TaskForCall("F", new Value[0], foreground: true));
            Assert.Equal(2, reports.Count);
            reports.Clear();
            s.Dbg.Options.BreakOnError = false;
            s.Dbg.SetBreakpoints(File, new[] { new BreakpointRequest { Line = 6 } });
            for (int i = 0; i < 2; i++)
                s.Vm.Run(s.Vm.TaskForCall("F", new Value[0], foreground: true));
            var n = reports.Single();
            Assert.True(
                n.Notice && n.Code == 343 && n.Text == "Foreground Cicode cannot break" && n.Line == 6
            );
            Assert.Empty(s.Stops);
        }

        [Fact]
        public void EachDistinctForegroundErrorIsReportedOnceAndNoneStopsThePage()
        {
            // F and G raise the same error in two places: both are reported, each once however
            // many scans repeat it; with ForegroundCanBreak page code still runs on
            var pb = new ProgramBuilder();
            foreach (var (name, line) in new[] { ("F", 5), ("G", 15) })
            {
                var f = pb.Function(name, "", Types.Long, file: File);
                f.Line(line)
                    .ConstStr("abc")
                    .ConstInt(10)
                    .Convert(Types.Long)
                    .ConstInt(2)
                    .Convert(Types.Long)
                    .Func("StrMid", 3)
                    .Op("Pop");
                f.Line(line + 1).ConstInt(1).RetValue().End();
            }
            var s = new Session(pb.Build());
            s.Dbg.Options.ForegroundCanBreak = true;
            var reports = new List<ErrorReportInfo>();
            s.Dbg.ErrorReported += reports.Add;
            for (int i = 0; i < 3; i++)
                foreach (var fn in new[] { "F", "G" })
                {
                    var t = s.Vm.TaskForCall(fn, new Value[0], foreground: true);
                    s.Vm.Run(t);
                    Assert.True(t.State == TaskState.Stop && t.Result!.Value.I == 1);
                }
            Assert.Equal(new[] { ("F", 5), ("G", 15) }, reports.Select(r => (r.Function, r.Line)).ToArray());
            Assert.All(reports, r => Assert.True(r.Foreground && r.Code == 257));
            Assert.Empty(s.Stops);
            // (ForegroundErrorBreak stops it: Host/ForegroundBreakTests)
        }

        [Fact]
        public void BreakpointFilesMatchByProjectFolderAndName()
        {
            // the library keeps the compile-time path of another install: the folder + name match
            var pb = new ProgramBuilder();
            var f = pb.Function(
                "F",
                "",
                Types.Long,
                file: @"C:\ProgramData\AVEVA Plant SCADA 2023\User\Example\Simulatr.ci"
            );
            f.Line(10).ConstInt(1).RetValue().End();
            var s = new Session(pb.Build());
            var r = s.Dbg.SetBreakpoints(
                @"c:\programdata\aveva plant scada 2023 r2\user\EXAMPLE\simulatr.CI",
                new[] { new BreakpointRequest { Line = 10 } }
            );
            Assert.True(r.Single().Verified && r.Single().Line == 10);
            Assert.True(
                s.Dbg.SetBreakpoints("Simulatr.ci", new[] { new BreakpointRequest { Line = 10 } })
                    .Single()
                    .Verified
            );
            var bad = s
                .Dbg.SetBreakpoints(
                    @"C:\x\ExampleSA\Simulatr.ci",
                    new[] { new BreakpointRequest { Line = 10 } }
                )
                .Single();
            Assert.False(bad.Verified);
            Assert.Contains("not", bad.Message);
            s.Sch.Spawn("F");
            s.Sch.Run();
            Assert.Equal(StopReason.Breakpoint, s.Stops.Single().Reason);
        }

        [Fact]
        public void StepReleasesOthersRaisesReleasedAndAStepOffParks()
        {
            var s = new Session(Build());
            var released = new List<string>();
            var parked = new List<string>();
            s.Dbg.Released += t => released.Add(t.Name);
            s.Dbg.Parked += t => parked.Add(t.Name);
            s.Dbg.SetBreakpoints(File, new[] { new BreakpointRequest { Line = 12 } });
            var a = s.Sch.Spawn("F", name: "a");
            var b = s.Sch.Spawn("F", name: "b");
            s.Sch.Run();
            s.Dbg.Step(a, StepKind.Over);
            Assert.Equal(new[] { "b" }, released.ToArray());
            s.Sch.Run();
            Assert.Equal(new[] { "a" }, parked.ToArray());
            Assert.True(a.Pause && a.StopReason == "parked");
            s.Dbg.Detach();
            s.Sch.Run();
            Assert.True(a.Done && b.Done);
        }

        [Fact]
        public void HitConditionsAndLogPoints()
        {
            Assert.True(Stepper.HitMatches("3", 3) && !Stepper.HitMatches("3", 4));
            Assert.True(Stepper.HitMatches(">= 2", 5) && !Stepper.HitMatches(">=2", 1));
            Assert.True(Stepper.HitMatches("%2", 4) && !Stepper.HitMatches("% 2", 3));
            var s = new Session(Build());
            var logs = new List<string>();
            s.Dbg.LogPoint += (t, id, msg) => logs.Add(msg);
            s.Dbg.SetBreakpoints(
                File,
                new[]
                {
                    new BreakpointRequest { Line = 20, LogMessage = "in G" },
                }
            );
            s.Sch.Spawn("F");
            s.Sch.Run();
            Assert.Equal(new[] { "in G" }, logs.ToArray());
            Assert.Empty(s.Stops);
        }

        [Fact]
        public void TheHistoryRingGrowsInOrderThenDropsTheOldest()
        {
            var g = new TaskHistory(1000);
            for (int k = 1; k <= 130; k++)
                g.Add(new HistoryEvent { Seq = k });
            Assert.False(g.Truncated);
            Assert.Equal(130, g.Count);
            for (int i = 0; i < g.Count; i++)
                Assert.Equal(i + 1, g[i].Seq);

            var h = new TaskHistory(100);
            for (int k = 1; k <= 250; k++)
                h.Push().Seq = k;
            Assert.True(h.Truncated);
            Assert.Equal(100, h.Count);
            Assert.Equal(151, h.FirstSeq);
            Assert.Equal(250, h.LastSeq);
            for (int i = 0; i < h.Count; i++)
                Assert.Equal(151 + i, h[i].Seq);
            Assert.Equal(99, h.IndexAtOrBefore(250));
            Assert.Equal(-1, h.IndexAtOrBefore(150));
        }

        [Fact]
        public void WritesAreWatchedOnlyWithDataBreakpointsAndDetachDropsTheHistory()
        {
            var s = new Session(Build());
            s.Dbg.SetDataBreakpoints(
                new[]
                {
                    new DataBreakpointRequest { Name = "a", Function = "F" },
                }
            );
            s.Dbg.Attach(); // (set before the attach: the new recorder watches them)
            Assert.NotNull(s.Vm.Recorder!.Written);
            var t = s.Sch.Spawn("F");
            s.Sch.Run();
            var stop = s.Stops.Single();
            Assert.Equal(StopReason.DataBreakpoint, stop.Reason);
            Assert.Equal(11, stop.Line); // the line after a = 1
            Assert.NotNull(s.Vm.Recorder.Of(t));

            s.Dbg.SetDataBreakpoints(new DataBreakpointRequest[0]);
            Assert.Null(s.Vm.Recorder.Written);
            s.Dbg.Detach();
            Assert.Null(s.Vm.Recorder);
            Assert.Null(t.Hist);
            s.Sch.Run();
            Assert.True(t.State == TaskState.Stop && t.Result!.Value.I == 2);
        }
    }
}
